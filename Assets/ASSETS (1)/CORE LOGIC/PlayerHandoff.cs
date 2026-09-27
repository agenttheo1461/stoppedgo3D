using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerHandoff : MonoBehaviour, IBusDisplaySource, IBusDriverDisplaySource
{
    public static PlayerHandoff Instance { get; private set; }

    public event Action<PlayerShiftState> OnShiftStateChanged;
    public event Action<TimetableSlot> OnSlotAssigned;
    public event Action<int> OnReliefRequested;
    public event Action<int> OnReplacementAssigned;
    // [REDO] Split in two. OnShiftEnded now fires ONLY for a genuine full
    // stop (relief handoff, or the player explicitly ending their shift) --
    // never for a between-blocks pause. ShiftRunner's summary card and
    // session-stat reset are keyed off this alone, so they can no longer
    // fire mid-shift just because one block finished. OnBlockAdvanced is
    // the between-blocks signal: laps on the current block are done, more
    // may be available, shift keeps running.
    public event Action OnShiftEnded;
    public event Action OnBlockAdvanced;
    public event Action<int> OnReliefBusFound;
    /// <summary>Fires exactly once per real terminal arrival (inside
    /// OnPlayerArrivedAtTerminal, right after _segmentsCompleted++) — the
    /// precise, single-fire hook for "a lap just finished." Added so
    /// ShiftRunner's lap-complete overlay can subscribe to a real event
    /// instead of inferring it from OnSlotAssigned, which also fires in
    /// bulk whenever BusScheduler's TopUpChain pre-assigns a bus's entire
    /// remaining day at once — that mismatch (one arrival, N bulk-assigned
    /// future legs all firing the same event) was the root cause of an
    /// earlier lag/crash bug. int = segmentsCompleted count.</summary>
    public event Action<int> OnLegArrived;

    private int _pendingReliefFleetNumber = -1;
    public int PendingReliefFleetNumber => _pendingReliefFleetNumber;
    public bool IsReliefSwapPending => _replacementBusID >= 0 && _pendingReliefFleetNumber >= 0;

    // [FIX] Free Drive reads as NOT on duty too, same as OffDuty -- you're
    // driving, but not serving a scheduled route, so boards should still
    // show NOT IN SERVICE and lateness/schedule tracking shouldn't apply.
    public bool IsOnDuty => _shiftState != PlayerShiftState.OffDuty && _shiftState != PlayerShiftState.FreeDrive;
    public bool HasActiveSlot => _activeSlot != null;
    public bool IsAwaitingRelief => _reliefRequested;
    public bool IsBreakdownActive => _isBreakdown;
    public int ReplacementBusID => _replacementBusID;
    public bool IsPlayerSlotActive => _activeSlot != null && _activeSlot.assignedBusID == PlayerBusID;

    public float SecondsFromSchedule
    {
        get
        {
            if (_activeSlot == null || BusScheduler.Instance == null) return 0f;
            float minutesDelta = BusScheduler.Instance.GameTimeMinutes - _activeSlot.scheduledDeparture;
            return minutesDelta * 60f;
        }
    }

    public string OnTimeStatusLabel
    {
        get
        {
            if (_activeSlot == null) return "--";
            float secs = SecondsFromSchedule;
            if (Mathf.Abs(secs) < 5f) return "on time";
            return secs > 0f ? $"+{secs:F0}s late" : $"-{Mathf.Abs(secs):F0}s early";
        }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        RoadEvent.Cleared += OnRoadEventCleared;

        // [FIX — the "cheat into driving" bug] This used to auto-grab
        // whatever BusSimulationController happened to be parented under
        // this object at scene start, the moment playerBus was still null.
        // If any bus is ever parented under PlayerHandoff in the scene/
        // prefab (leftover from testing, or just convenient hierarchy
        // setup), that bus's BusSimulationController is already enabled by
        // default on a real player-bus prefab -- so this line alone made
        // the player fully drivable from frame one, no MainMenu, no
        // DriverProfile, no route pick, no BusSelectMenu possession flow,
        // none of it. Every other method in this file already null-checks
        // playerBus defensively, so leaving it unset until SetPlayerBus()
        // is actually called through the real flow is safe -- the ONLY way
        // to end up with a drivable bus now is through that flow.
    }

    public void SetPlayerBus(BusSimulationController bus)
    {
        if (bus == null) return;
        playerBus = bus;
        var npc = bus.GetComponent<NPCBusController>();
        if (npc != null) _cachedFleetNumber = npc.fleetNumber;
        // New bus means any cached suspension reference is stale — force a
        // fresh GetComponent lookup next time the kneel key is pressed.
        _cachedSuspension = null;
        _cachedInteriorLights = null;

        // [ADD] This is the actual fix for gray/unconfigured ad boards on
        // the player's bus. SetPlayerBus is the one method both the initial
        // player-bus assignment AND every handoff swap route through -- but
        // nothing here ever registered the bus with BusManager, so a
        // player-owned bus never got its ad boards (or bike rack) set up at
        // all unless it happened to already be an NPC that BusSpawner had
        // previously configured. RegisterBus is idempotent (see BusManager/
        // BusSpawner), so calling it here every time is always safe -- it
        // only actually configures anything the first time this physical
        // bus is ever registered.
        if (npc != null && BusManager.Instance != null)
        {
            int physicalBusID = BusManager.Instance.RegisterBus(npc);

            // [ADD] The actual fix for "relief found MY OWN bus" — RegisterBus
            // only (idempotently) makes sure BusManager knows about this
            // physical bus; it never pulls it out of BusManager's idle pool.
            // If this physical bus was previously idle (sitting at a depot,
            // or you're being handed a fresh relief bus that was JUST claimed
            // via ClaimBusForHandoff already — that path is a safe no-op
            // duplicate removal), BusManager still listed its busID in
            // _idlePool/isIdle=true. GetIdleBusForRoute (used by
            // HandleReplacementNeeded to find YOUR relief) has no concept of
            // "currently player-possessed" — it just scans _idlePool — so it
            // could hand back this exact busID as a "replacement" for
            // itself. The self-match guard in HandleReplacementNeeded only
            // ever compared against the abstract PLAYER_BUS_ID (-2) constant,
            // never against which REAL physical busID you're actually
            // sitting in, so it could never catch this. Explicitly claiming
            // the bus here (same call NPC↔NPC handoffs use to remove a bus
            // from the idle pool) is the actual fix; the busID-space check
            // added to HandleReplacementNeeded is the defense-in-depth
            // backstop in case a bus somehow re-enters the idle pool while
            // still possessed.
            if (physicalBusID >= 0)
                BusManager.Instance.ClaimBusForHandoff(physicalBusID);
        }
    }

    private int GetBusID(BusSimulationController bus)
    {
        if (bus == null) return -1;
        var npc = bus.GetComponent<NPCBusController>();
        return npc != null ? npc.busID : -1;
    }

    public void CancelReliefRequest()
    {
        if (!_reliefRequested) return;
        _reliefRequested = false;
        _replacementBusID = -1;
        PrintTagged("Relief request cancelled.", "warn");
    }

    private void SetShiftState(PlayerShiftState newState)
    {
        if (_shiftState == newState) return;
        _shiftState = newState;
        OnShiftStateChanged?.Invoke(newState);
    }

    private void SetActiveSlot(TimetableSlot slot)
    {
        if (slot == null) return;

        _activeSlot = slot;
        _activeRoute = slot.routeNumber;
        _activeVariantLetter = slot.variantLetter ?? "";
        _activeVariant = string.IsNullOrEmpty(_activeVariantLetter)
            ? null
            : BusScheduler.Instance?.GetRouteData(_activeRoute)?.GetVariant(_activeVariantLetter);
        _isOutbound = slot.isOutbound;
        OnSlotAssigned?.Invoke(slot);
    }

    public void ResetForFreshPossession(BusSimulationController bus, int displayFleetNumber = -1, NPCBusController formerNpc = null)
    {
        if (bus != null) playerBus = bus;

        // Same reason as AdoptInServiceSlot: if this bus was sitting idle
        // (e.g. parked at a depot bay) right before being possessed,
        // SetDepotComponentsActive(false) disabled its own colliders/
        // renderers/audio, and this is the path that was missing the
        // re-enable when grabbing straight from idle rather than via
        // in-service handoff.
        formerNpc?.SetIdle(false);

        // [FIX] Was PlayerShiftState.OffDuty -- this is the actual Free
        // Drive entry point (grabbing a bus with no active route via BSM's
        // Fleet tab or the Custom tab both route through here).
        //
        // [D2 note] Left as a direct assignment rather than SetShiftState()
        // — this method does a full hard reset of every field and manually
        // fires OnShiftStateChanged itself a few lines down (see below),
        // after all the reset fields are in their new values. Routing it
        // through SetShiftState() here would fire the event mid-reset,
        // before _activeRoute/_activeSlot/etc. reflect the new state.
        _shiftState = PlayerShiftState.FreeDrive;
        _freeDriveOrigin = true;
        _activeRoute = "";
        _activeVariantLetter = "";
        _activeVariant = null;
        _activeSlot = null;
        _isOutbound = true;
        _segmentsCompleted = 0;
        _lastCompletedLegOutbound = null;
        _onboardPax = 0;
        _stopRequested = false;
        _alightCount = 0;
        _missedAlightCarryover = 0;
        _passengerDestinations.Clear();
        _reliefRequested = false;
        _replacementBusID = -1;
        _cachedFleetNumber = displayFleetNumber >= 0 ? displayFleetNumber : _cachedFleetNumber;
        // [ADD] A fresh possession is a different physical bus -- any
        // wheelchair pax/lift state belonged to the old one.
        _onboardAdaPax        = 0;
        _adaBoardingWaiting   = false;
        _adaAlightRequested   = false;
        _deferredBoardingPax  = 0;
        _rampState            = RampState.Stowed;
        _rampProgress01       = 0f;

        if (displayFleetNumber >= 0)
            PrintTagged($"Player bus now set to fleet #{displayFleetNumber}.", "system");
        OnShiftStateChanged?.Invoke(_shiftState);
        DriverConsole.Instance?.RefreshStopHUD();
    }

    public void RequestRelief()
    {
        if (!IsOnDuty)
        {
            PrintTagged("Not on duty.", "warn");
            return;
        }

        if (_reliefRequested)
        {
            // [FIX] CancelReliefRequest() existed fully implemented but was
            // never actually reachable from anywhere -- once you'd
            // requested relief there was no way back short of it resolving
            // on its own. Wiring reliefKey to double as cancel (pressed a
            // second time) costs no new keybind. Only allowed while still
            // SEARCHING, not once a real replacement bus has already been
            // found/committed (IsReliefSwapPending) -- that bus would need
            // its own "stand down" handling to cancel safely, which is a
            // separate, bigger fix than this one.
            if (IsReliefSwapPending)
                PrintTagged("Relief already requested. Stand by for the replacement bus.", "info");
            else
                CancelReliefRequest();
            return;
        }

        if (_activeSlot == null)
        {
            PrintTagged("No active slot available to relieve.", "warn");
            return;
        }

        if (BusScheduler.Instance == null || BusManager.Instance == null)
        {
            PrintTagged("Relief unavailable: scheduler or bus manager offline.", "error");
            return;
        }

        _reliefRequested = true;
        _replacementBusID = -1;
        SetShiftState(PlayerShiftState.ReliefPending);

        PrintTagged($"Relief requested for Route {ActiveRouteLabel} {(_isOutbound ? "A→Z" : "Z→A")}.", "info");
        OnReliefRequested?.Invoke(_activeSlot.assignedBusID);

        if (BusScheduler.Instance != null)
            BusScheduler.Instance.RequestRelief(PlayerBusID, _activeRoute, _isOutbound);
        else
            PrintTagged("⚠ Scheduler unavailable — relief request could not be sent.", "warn");
        DriverConsole.Instance?.RefreshStopHUD();
    }

    /// <summary>[ADD] Counterpart to OnReplacementFound for the case where
    /// BusScheduler.InitiateReliefSearch genuinely finds nobody eligible.
    /// Without this, a failed search just logged a warning server-side and
    /// left the player sitting in ReliefPending/breakdown state forever --
    /// the exact same "stuck on the route with no way out" bug ConfirmReliefSwap's
    /// failure branch had (see the fix there). Resets relief state and hands
    /// the player back a real path forward instead of a dead end.</summary>
    public void OnReplacementSearchFailed()
    {
        if (!_reliefRequested) return;

        _reliefRequested          = false;
        _replacementBusID         = -1;
        _pendingReliefFleetNumber = -1;

        PrintTagged("No eligible replacement bus available right now. Try <b>relief</b> again shortly, " +
                    "or type <b>end shift</b> to end your leg here.", "warn");
        DriverConsole.Instance?.RefreshStopHUD();
    }

    public void OnReplacementFound(int replacementBusID)
    {
        if (!_reliefRequested)
        {
            PrintTagged("Replacement bus found, but no relief request is active.", "warn");
            return;
        }

        if (_activeSlot == null)
        {
            PrintTagged("Replacement bus cannot be assigned: active slot missing.", "error");
            return;
        }

        if (replacementBusID < 0)
        {
            PrintTagged("Relief request failed: no replacement bus available.", "error");
            return;
        }

        if (BusManager.Instance == null)
        {
            PrintTagged("Replacement bus found, but BusManager is unavailable.", "error");
            return;
        }

        _replacementBusID = replacementBusID;

        int replacementFleet = BusManager.Instance?.GetRecord(replacementBusID)?.controller?.fleetNumber ?? replacementBusID;
        _pendingReliefFleetNumber = replacementFleet;

        PrintTagged($"🔁 Relief bus found: Fleet #{replacementFleet}, Route <b>{ActiveRouteLabel}</b>. " +
                    "Swap now? Type <b>relief yes</b> or <b>relief no</b>.", "info");
        OnReliefBusFound?.Invoke(replacementFleet);
        DriverConsole.Instance?.RefreshStopHUD();
    }

    public void ConfirmReliefSwap(bool accept)
    {
        if (_replacementBusID < 0)
        {
            PrintTagged("No relief swap is pending.", "warn");
            return;
        }

        if (!accept)
        {
            int declinedFleet = _pendingReliefFleetNumber;
            _replacementBusID = -1;
            _pendingReliefFleetNumber = -1;
            PrintTagged($"Relief swap declined (Fleet #{declinedFleet}). " +
                        "No other slot available — ending shift.", "warn");
            _reliefRequested = false;
            EndCurrentLegForShiftAdvance();
            return;
        }

        int replacementBusID = _replacementBusID;
        bool assigned = false;

        if (BusManager.Instance != null)
            assigned = BusManager.Instance.AssignReplacementForPlayer(replacementBusID, _activeSlot);

        if (!assigned)
            PrintTagged($"[Handoff] Primary assign failed. busID={replacementBusID} activeSlotAssigned={_activeSlot?.assignedBusID ?? -1}", "debug");

        if (!assigned && BusScheduler.Instance != null)
        {
            int fromBus = (_activeSlot != null && _activeSlot.assignedBusID >= 0) ? _activeSlot.assignedBusID : PlayerBusID;
            assigned = BusScheduler.Instance.TryClaimNextReliefSlotForReplacement(fromBus, replacementBusID);

            if (assigned)
                PrintTagged($"[Handoff] Scheduler fallback succeeded for replacement #{replacementBusID} (fromBus={fromBus}).", "debug");
        }

        if (!assigned)
        {
            // [FIX] Was just resetting _replacementBusID and returning --
            // _reliefRequested and _shiftState (still ReliefPending) never
            // got cleared, unlike the decline branch above which does this
            // properly. That left the player permanently stuck: every future
            // "relief" attempt just hit RequestRelief()'s own "Relief already
            // requested" guard forever, with no code path that ever got them
            // out. Now mirrors the decline branch's cleanup exactly.
            PrintTagged($"Unable to assign replacement (busID={replacementBusID}) to the next slot. " +
                        "Type <b>relief</b> to try again, or <b>end shift</b> to end your leg here.", "error");
            _replacementBusID         = -1;
            _pendingReliefFleetNumber = -1;
            _reliefRequested          = false;
            SetShiftState(PlayerShiftState.InService);
            DriverConsole.Instance?.RefreshStopHUD();
            return;
        }

        PrintTagged($"Replacement bus #{replacementBusID} assigned to your next slot.", "success");
        OnReplacementAssigned?.Invoke(replacementBusID);

        // [ADD] Remember which slot the replacement now owns so
        // HandleReliefHandoffDeparture can tell you the moment it actually
        // pulls out on it, instead of "assigned" being the last word you
        // hear on whether it's really covering you.
        if (BusScheduler.Instance != null && BusScheduler.Instance.TryGetAssignedSlot(replacementBusID, out var handoffSlot))
        {
            _pendingReliefHandoffSlot = handoffSlot;
            _pendingReliefHandoffBusID = replacementBusID;
        }

        DriverConsole.Instance?.RefreshStopHUD();
    }

    private void CompleteRelief()
    {
        if (!_reliefRequested)
            return;

        if (_replacementBusID < 0)
        {
            PrintTagged("Relief is still pending. Hold on for the replacement bus.", "info");
            return;
        }

        _reliefRequested = false;
        _replacementBusID = -1;

        int myOldPhysicalBusID = GetBusID(playerBus);
        if (myOldPhysicalBusID >= 0)
        {
            if (BusManager.Instance != null)
                BusManager.Instance.RetireBusFromRoute(myOldPhysicalBusID);

            PrintTagged($"Successfully retired physical bus #{myOldPhysicalBusID} from the route.", "debug");
        }
        else
        {
            PrintTagged("Could not find the physical Bus ID to retire. (Is the NPC controller missing?)", "error");
        }

        SetShiftState(PlayerShiftState.Relieved);
        PrintTagged("Relief complete. Your service has been transferred to the replacement bus.", "success");
        OnShiftEnded?.Invoke();
        EndShift();
    }

    public void EndCurrentLegForShiftAdvance()
    {
        if (!IsOnDuty)
        {
            PrintTagged("Not on duty — nothing to advance.", "warn");
            return;
        }

        if (_activeSlot != null)
            BusScheduler.Instance?.ReleasePlayerSlot(PlayerBusID);

        _isBreakdown = false;
        _reliefRequested = false;
        _replacementBusID = -1;
        _departOverridePending = false;

        // [FIX] Was PlayerShiftState.OffDuty -- same state used for a
        // genuine full stop, even though the player is clearly still on
        // shift here, just between legs waiting to type "continue".
        // IsOnDuty was reading FALSE during that entire window, which is
        // wrong. ArrivedAtTerminal already exists and fits this "reached
        // the end, about to do the next thing" moment correctly.
        //
        // [FIX D2] Was a direct _shiftState assignment with no event fire —
        // OnShiftStateChanged never told anyone this transition happened.
        // D4/D5 need to hook reliably into every transition, so this now
        // goes through SetShiftState() like the rest of the file should.
        SetShiftState(PlayerShiftState.ArrivedAtTerminal);
        _activeSlot = null;
        _activeRoute = "";
        _activeVariantLetter = "";
        _activeVariant = null;
        _targetTerminal = null;

        PrintTagged("Leg ended — advancing to next planned block.", "info");
        // [FIX] Used to fire OnShiftEnded here -- this is a PAUSE, not an
        // end, and ShiftRunner's OnShiftEnded handler shows the blocking
        // "SHIFT COMPLETE" card and wipes session totals. Firing it here
        // meant that card (and the session-stat wipe) fired after every
        // single block, not just at a genuine end -- see OnBlockAdvanced.
        OnBlockAdvanced?.Invoke();

        DriverConsole.Instance?.RefreshStopHUD();
    }

    /// <summary>[ADD] The genuine full-stop path that was missing. Nothing
    /// previously called EndShift() (the actual OffDuty/depot-return
    /// transition) except CompleteRelief -- the board's "END SHIFT — RETURN
    /// TO DEPOT" button and the auto-signoff-when-window-closed case both
    /// went through EndCurrentLegForShiftAdvance instead, which only ever
    /// reaches ArrivedAtTerminal (still on duty) and never actually frees
    /// the bus. This is the real counterpart: releases the slot, fires the
    /// genuine OnShiftEnded (summary card + session reset are correct
    /// here), then hands off to EndShift() for the depot return.</summary>
    public void EndShiftFully()
    {
        if (!IsOnDuty)
        {
            PrintTagged("Not on duty.", "warn");
            return;
        }

        // [ADD] Per the breakdown-3.0 design: a player bus limping home from
        // a replace-tier breakdown must actually reach a MaintenanceBay zone
        // and get fully repaired before the shift can end -- unlike an NPC,
        // which gets repaired for free just by being registered at/heading
        // to depot. Without this gate, EndShiftFully() would happily let the
        // player walk away from a bus that's still mechanically broken.
        //
        // [FIX] Was IsLimpingHome(PlayerBusID) -- same -2-sentinel-vs-real-
        // busID bug as UpdatePlayerBreakdowns/CmdBreakdown. _limpingHome is
        // populated under the real busID by BusBreakdownSystem's own
        // OnReplaceTierExpired, so checking -2 here meant this gate could
        // NEVER actually trigger -- EndShiftFully() has been letting players
        // walk away from a mechanically broken bus this entire time, the
        // exact thing this comment says it exists to prevent.
        if (BusBreakdownSystem.Instance != null && BusBreakdownSystem.Instance.IsLimpingHome(GetBusID(playerBus)))
        {
            PrintTagged("Can't end shift yet — this bus is still broken down. " +
                        "Get it to a Maintenance Bay and let it fully repair first.", "warn");
            return;
        }

        if (_activeSlot != null)
            BusScheduler.Instance?.ReleasePlayerSlot(PlayerBusID);

        _isBreakdown = false;
        _reliefRequested = false;
        _replacementBusID = -1;
        _departOverridePending = false;

        PrintTagged("Shift complete — returning to depot.", "success");
        OnShiftEnded?.Invoke();
        EndShift();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  TERMINAL BAY CLAIM (player)
    //  NPC buses reserve a TerminalIdleZone bay before pulling in. The player used to claim nothing, so an NPC
    //  could drive straight into the bay the player was parked in. The player now claims the nearest free bay when
    //  arriving at a terminal, releases it when departing / ending the shift, and can move to another bay with the
    //  console command  <stopCode>/B<index>   (e.g.  s0093/B2  = bay index 2, i.e. the third bay).
    // ═════════════════════════════════════════════════════════════════════════
    private TerminalIdleZone _playerBayZone;
    private int _playerBayIdx = -1;

    /// <summary>"S0093/B2" while holding a bay, else "".</summary>
    public string ClaimedBayLabel => _playerBayZone != null && _playerBayIdx >= 0 ? $"{_playerBayZone.stopCode}/B{_playerBayIdx}" : "";

    public void ReleasePlayerBay()
    {
        if (_playerBayZone != null) _playerBayZone.Release(PlayerBusID);
        _playerBayZone = null;
        _playerBayIdx = -1;
    }

    /// <summary>Claims the free bay nearest to the player's bus at the current terminal (silent if there's no idle zone).</summary>
    private void TryAutoClaimBay()
    {
        if (playerBus == null) return;
        string code = _targetTerminal != null ? _targetTerminal.StopCode : "";
        var zone = TerminalIdleZone.GetForStopIgnoreCase(code);
        if (zone == null || zone.bays.Count == 0) return;

        Vector3 p = playerBus.transform.position;
        int best = -1; float bestD = 80f; // only bays within 80 m
        for (int i = 0; i < zone.bays.Count; i++)
        {
            if (zone.GetBayOwner(i) >= 0 && zone.GetBayOwner(i) != PlayerBusID) continue;
            if (zone.IsBayPhysicallyOccupied(i, PlayerBusID) && !(_playerBayZone == zone && _playerBayIdx == i)) continue;
            float d = Vector3.Distance(p, zone.GetParkingPosition(i));
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return;
        ReleasePlayerBay();
        if (zone.TryClaimSpecific(PlayerBusID, best))
        {
            _playerBayZone = zone; _playerBayIdx = best;
            PrintTagged($"🅿 Bay <b>{best + 1}</b> at {zone.stopCode} reserved for you. Change it with <b>{zone.stopCode}/B&lt;n&gt;</b> (e.g. {zone.stopCode}/B0).", "info");
        }
    }

    /// <summary>Console: "<stopCode>/B<index>" -- move your reservation to that bay of that terminal.</summary>
    private void CmdBay(string stopCode, int bayIdx)
    {
        var zone = TerminalIdleZone.GetForStopIgnoreCase(stopCode);
        if (zone == null) { PrintTagged($"No terminal bays registered for <b>{stopCode.ToUpper()}</b>.", "warn"); return; }
        if (bayIdx < 0 || bayIdx >= zone.bays.Count)
        {
            PrintTagged($"{zone.stopCode} has {zone.bays.Count} bay(s): B0 - B{zone.bays.Count - 1}.", "warn");
            return;
        }
        int owner = zone.GetBayOwner(bayIdx);
        if (owner >= 0 && owner != PlayerBusID) { PrintTagged($"Bay {bayIdx + 1} (B{bayIdx}) at {zone.stopCode} is already taken by Bus #{owner}.", "warn"); return; }
        if (zone.IsBayPhysicallyOccupied(bayIdx, PlayerBusID)) { PrintTagged($"Bay {bayIdx + 1} (B{bayIdx}) at {zone.stopCode} has a bus parked in it.", "warn"); return; }

        ReleasePlayerBay();
        if (!zone.TryClaimSpecific(PlayerBusID, bayIdx)) { PrintTagged("Couldn't claim that bay.", "warn"); return; }
        _playerBayZone = zone; _playerBayIdx = bayIdx;
        Vector3 pos = zone.GetParkingPosition(bayIdx);
        PrintTagged($"🅿 Bay <b>{bayIdx + 1}</b> (B{bayIdx}) at <b>{zone.stopCode}</b> reserved for you -- parking spot at ({pos.x:F0}, {pos.z:F0}).", "success");
    }

    private void OnPlayerArrivedAtTerminal()
    {
        if (_shiftState != PlayerShiftState.InService && _shiftState != PlayerShiftState.ReliefPending)
            return;

        _segmentsCompleted++;
        // [ADDED] Snapshot BEFORE anything downstream (relief completion,
        // TryAutoContinueOrSignOff, a later ContinueAssignedChain/BeginLeg)
        // gets a chance to change _isOutbound for the next leg.
        _lastCompletedLegOutbound = _isOutbound;
        OnLegArrived?.Invoke(_segmentsCompleted);
        TryAutoClaimBay();
        var    scheduler = BusScheduler.Instance;
        string timeStr   = scheduler?.GameTimeString ?? "--:--";

        int earned = PointsManager.Instance.EndLegAndCalculate();
        Debug.Log($"Leg complete! Earned {earned} points");

        PrintTagged($"🏁 [{timeStr}] Arrived at <b>{_targetTerminal?.StopName ?? "Terminal"}</b> " +
                    $"(segment {_segmentsCompleted})", "success");

        if (_onboardPax > 0 || _onboardAdaPax > 0)
        {
            PrintTagged($"← All {_onboardPax} remaining passenger(s) alighted at terminus.", "info");
            _onboardPax    = 0;
            _stopRequested = false;
            _alightCount   = 0;
            _missedAlightCarryover = 0;
            _passengerDestinations.Clear();
            // [ADD] Terminal arrival is a full clear-out for everyone,
            // wheelchair pax included.
            _onboardAdaPax      = 0;
            _adaBoardingWaiting = false;
            _adaAlightRequested = false;
            _deferredBoardingPax = 0;
            DriverConsole.Instance?.RefreshStopHUD();
        }
        if (_reliefRequested)
        {
            if (_replacementBusID < 0)
            {
                if (_shiftState != PlayerShiftState.ArrivedAtTerminal)
                {
                    // [FIX D2] direct assignment -> SetShiftState() so this fires.
                    SetShiftState(PlayerShiftState.ArrivedAtTerminal);
                    PrintTagged("🔄 Relief pending: replacement bus has not been assigned yet.", "warn");
                }
                return;
            }

            scheduler?.CompleteSlotAndRetire(PlayerBusID);
            CompleteRelief();
            return;
        }

        scheduler?.CompleteSlot(PlayerBusID);
        // [FIX D2] direct assignment -> SetShiftState() so this fires.
        SetShiftState(PlayerShiftState.ArrivedAtTerminal);

        TryAutoContinueOrSignOff();
    }

    private string BuildLapLabel()
    {
        int lapsSoFar = BusScheduler.Instance?.GetLapCount(PlayerBusID) ?? _segmentsCompleted;

        var runner = ShiftRunner.Instance;
        if (runner != null && runner.todaysSchedule != null && runner.activeBlockIndex >= 0 && runner.activeBlockIndex < runner.todaysSchedule.Count
            && BusScheduler.Instance != null)
        {
            var block = runner.todaysSchedule[runner.activeBlockIndex];
            int totalInBlock = 0;
            foreach (var s in BusScheduler.Instance.AllSlots)
            {
                if (s.assignedBusID != PlayerBusID) continue;
                if (s.routeNumber != block.routeNumber) continue;
                if (s.scheduledDeparture < block.windowStartMinutes || s.scheduledDeparture > block.windowEndMinutes) continue;
                if (s.state == SlotState.Unassigned) continue;
                totalInBlock++;
            }
            if (totalInBlock > 0)
                return $"Lap {lapsSoFar} of {totalInBlock}";
        }

        return $"Lap {lapsSoFar}";
    }

    // NO AUTO-CONTINUE, PER DESIGN: this used to silently transition
    // straight into the next chained leg (_shiftState = InService, no
    // prompt) whenever BusScheduler still had a pre-assigned slot waiting.
    // That's gone — EVERY arrival now surfaces the board's terminal
    // prompt, whether the chain has another leg queued or the whole
    // rotation is exhausted. The player is ALWAYS the one who presses
    // continue. ContinueAssignedChain() (below) is what the board's
    // "CONTINUE" button calls when a pre-assigned next leg exists.
    private void TryAutoContinueOrSignOff()
    {
        var scheduler = BusScheduler.Instance;
        bool hasChainedLeg = scheduler != null && scheduler.TryGetAssignedSlot(PlayerBusID, out _);

        // [FIX] This used to call ContinueAssignedChain() immediately right
        // here whenever hasChainedLeg was true — meaning arrival NEVER
        // actually stopped for a bus with a pre-filled next leg; it just
        // silently kept driving with the shift state flipping straight back
        // to InService, so the player never got a summary/completion beat
        // and never got a choice. That directly contradicts the class
        // comment above ("NO AUTO-CONTINUE, PER DESIGN") which describes
        // ContinueAssignedChain() as something the player's button press
        // should trigger — this call site was the leftover of the old
        // behavior the comment says was removed. Now EVERY arrival stops
        // here and waits for an explicit "continue" (DriverConsole command)
        // or a board pick, regardless of whether a chain leg is waiting.
        PrintTagged(hasChainedLeg
            ? "Segment complete — type <b>continue</b> for your next assigned leg, or pick a different route from the board."
            : "This bus's scheduled run is finished — signing off.", "board");

        // [FIX Bug 9] Was: chain-exhausted case checked whether the block's
        // time window was still open and, if so, opened the shift board for
        // the player to pick a brand-new route instead of ending -- so
        // "end of shift" never actually happened as long as something was
        // still running that day. A takeover only ever inherits the NPC's
        // own remaining pre-assigned chain (now capped, see TopUpChain's
        // MaxPrechainedLegsFor); once that's exhausted the shift is
        // genuinely done -- the player isn't meant to be offered a fresh
        // booking of their own from here (that's the separate shift-maker
        // feature, not this auto-continue path).
        if (!hasChainedLeg)
            EndShiftFully();
        // hasChainedLeg case: deliberately does nothing else here. Shift
        // state is already ArrivedAtTerminal (set by OnPlayerArrivedAtTerminal
        // before this was called) — the player now explicitly types
        // "continue" (routes to ContinueAssignedChain()) or opens the board
        // themselves. No board auto-open for this case since the CONTINUE
        // console command already covers it without needing the menu.
    }

    /// <summary>Board's "CONTINUE" card calls this when BusScheduler still
    /// has the next chain leg pre-assigned and waiting — the exact
    /// transition the old auto-continue branch used to do silently. Now
    /// it only ever runs from an explicit player button press.</summary>
    public void ContinueAssignedChain()
    {
        var scheduler = BusScheduler.Instance;
        if (scheduler == null || !scheduler.TryGetAssignedSlot(PlayerBusID, out var nextSlot))
        {
            PrintTagged("No pending chain leg to continue — pick a route from the board instead.", "warn");
            return;
        }

        // [FIX 07-30] This used to set _targetTerminal to the NEW leg's
        // destination and jump straight to InService — skipping the
        // WaitingToDepart gate entirely. That's why continuing a chain
        // departed you INSTANTLY even when the next leg's scheduledDeparture
        // was 45 minutes away: nothing here ever checked the time, because
        // this path never went through the one place that does (BeginLeg,
        // normally reached via CmdDepart). It also never called
        // scheduler.RecordActualDeparture, so slot.latenessMinutes for every
        // continued leg was simply never freshly computed — whatever stale
        // value the TimetableSlot object already held is what got reported,
        // which is almost certainly the "1000+ mins late" reports too.
        //
        // Fix: land in WaitingToDepart, same as the very first leg does
        // after CmdArrived — leave _targetTerminal alone (you're already
        // standing at it, having just arrived), and let the existing
        // CmdDepart -> BeginLeg path do the real work when the player
        // actually types depart: real lateness check against
        // nextSlot.scheduledDeparture, RecordActualDeparture, the new
        // leg's _targetTerminal, and the stop-index reset. One canonical
        // "start driving a leg" path instead of two divergent ones.
        SetActiveSlot(nextSlot); // sets _activeSlot/_activeRoute/_activeVariantLetter/_activeVariant/_isOutbound together
        SetShiftState(PlayerShiftState.WaitingToDepart); // [FIX D2] was a direct assignment, never fired OnShiftStateChanged
        _departOverridePending = false;

        string dir     = nextSlot.isOutbound ? "A→Z" : "Z→A";
        string depTime = BusScheduler.MinutesToTimeString(nextSlot.scheduledDeparture);

        PrintTagged($"🔁 Continuing on Fleet #{_cachedFleetNumber} — {BuildLapLabel()}, next departure <b>{depTime}</b> ({dir}).", "board");

        float lateBy = scheduler != null ? scheduler.GameTimeMinutes - nextSlot.scheduledDeparture : 0f;
        if (lateBy > 1f)
            PrintTagged($"⚠ You're <b>{Mathf.CeilToInt(lateBy)} min late</b> for the {depTime} departure. Still your leg, departing now.", "warn");
        else
            PrintTagged("  Type <b>depart</b> when time comes.", "info");

        if (scheduler != null)
            PrintTagged($"  Game time now: {scheduler.GameTimeString}", "info");

        DriverConsole.Instance?.RefreshStopHUD();
    }

    /// <summary>True if the scheduler still has a pre-assigned next leg
    /// waiting for this bus — the board uses this to show a prominent
    /// "CONTINUE" card ahead of the normal route list.</summary>
    /// <summary>Called by ShiftRunner right after possessing a bus via
    /// BusSelectMenu.PossessFleetNumber. That call can ALSO silently adopt
    /// whatever scheduler slot the target bus happened to be actively
    /// running, via AdoptInServiceSlot — it has no idle-check, it just
    /// calls TransferSlotToPlayer(npcBusID) unconditionally and sets
    /// _shiftState = InService the moment that succeeds. If the "idle"
    /// depot bus the player picked wasn't actually idle (stale isIdle flag,
    /// or a normal dispatch tick grabbed it in the gap between the board
    /// refreshing and the player clicking SELECT), this collides directly
    /// with ShiftRunner's own intended TransferSlotToPlayer+JoinRouteWithSlot
    /// call for the slot the player ACTUALLY picked — JoinRouteWithSlot's
    /// "already on duty" guard then correctly refuses the real claim,
    /// because AdoptInServiceSlot's silent adoption already put the player
    /// on duty for a completely different, unrelated leg first.
    ///
    /// This releases whatever got auto-adopted and resets cleanly to
    /// OffDuty so the board's real claim can proceed. No-op if nothing was
    /// actually adopted (already OffDuty — the common/correct case when
    /// the depot bus genuinely was idle).</summary>
    public void ReleaseAutoAdoptedSlotForBoardClaim()
    {
        if (_shiftState == PlayerShiftState.OffDuty) return;

        Debug.LogWarning($"[PlayerHandoff] Possession auto-adopted an active slot (Route {ActiveRouteLabel}) " +
                          "that the shift board didn't ask for — releasing it so the board's actual pick can proceed. " +
                          "This means the depot bus you selected wasn't genuinely idle.");

        if (BusScheduler.Instance != null)
            BusScheduler.Instance.ReleasePlayerSlot(PlayerBusID);

        _activeSlot          = null;
        _activeRoute         = null;
        _activeVariantLetter = "";
        _activeVariant       = null;
        SetShiftState(PlayerShiftState.OffDuty);
    }

    public bool HasPendingChainLeg =>
        BusScheduler.Instance != null && BusScheduler.Instance.TryGetAssignedSlot(PlayerBusID, out _);

    public void AdoptInServiceSlot(int npcBusID, BusSimulationController bus, NPCBusController formerNpc)
    {
        var scheduler = BusScheduler.Instance;
        if (scheduler == null) return;

        var slot = scheduler.TransferSlotToPlayer(npcBusID);

        if (slot == null && bus != null && GetBusID(bus) != npcBusID)
            slot = scheduler.TransferSlotToPlayer(GetBusID(bus));

        if (slot == null && formerNpc != null && formerNpc.busID != npcBusID)
            slot = scheduler.TransferSlotToPlayer(formerNpc.busID);

        if (slot == null)
        {
            int actualBusID = GetBusID(bus);
            int actualFleet = formerNpc != null ? formerNpc.fleetNumber : -1;
            PrintTagged($"Unable to adopt in-service slot: scheduler transfer failed. requested={npcBusID}, busID={actualBusID}, fleet={actualFleet}", "error");
            ResetForFreshPossession(bus, actualFleet, formerNpc);
            return;
        }

        SetPlayerBus(bus);
        SetActiveSlot(slot);
        SetShiftState(PlayerShiftState.InService);
        // [ADD] Adopting an already-running slot mid-route is NOT a Free
        // Drive origin -- ending this shift should genuinely go OffDuty.
        _freeDriveOrigin = false;
        _isBreakdown = false;
        _reliefRequested = false;
        _replacementBusID = -1;

        // Make sure the bus's own colliders/renderers/audio are enabled --
        // if it was sitting idle (e.g. at a depot bay) right before being
        // possessed, SetDepotComponentsActive(false) would have disabled
        // them, and nothing else on this path flips it back.
        formerNpc?.SetIdle(false);

        var routeData = scheduler.GetRouteData(_activeRoute);
        if (routeData != null)
        {
            // [FIX] Used to read routeData.terminalZCode/terminalACode directly,
            // completely ignoring _activeVariant (already resolved above by
            // SetActiveSlot). GetActiveTerminalCode() checks _activeVariant.overrideRoute
            // and returns terminalZCodeOverride/terminalACodeOverride when set.
            SetTargetTerminal(_isOutbound, forStart: false);
        }

        string dir  = _isOutbound ? "A→Z" : "Z→A";
        string dest = _targetTerminal != null ? _targetTerminal.StopName : "far terminal";
        PrintTagged($"🚌 Possessed Fleet #{_cachedFleetNumber} — Route <b>{ActiveRouteLabel}</b> {dir}, " +
                    $"{BuildLapLabel()}, heading to <b>{dest}</b>.", "system");

        DriverConsole.Instance?.RefreshStopHUD();
    }

    public int CurrentStopIndex =>
        _shiftState == PlayerShiftState.InService ? _nextStopIndex : -1;

    public int PlayerBusID => BusScheduler.PLAYER_BUS_ID;

    // ═════════════════════════════════════════════════════════════════════════
    //  IBusDisplaySource -- for BusInteriorScrollBoard / BusInteriorLCDBoard.
    //  Every member here just re-exposes state that already exists elsewhere
    //  in this class (StopRequested, NextStopName, ActiveRouteLabel, etc.) --
    //  no new fields, no new alighting logic. StopRequested already IS the
    //  alighting-driven flag the boards want for "stop requested."
    // ═════════════════════════════════════════════════════════════════════════
    int IBusDisplaySource.BusID => FleetNumber;

    // LCD boards show the plain route number; the '~' short-turn symbol only appears on the console and the
    // 2D driver board (IBusDriverDisplaySource.RouteLabel).
    string IBusDisplaySource.RouteNumber => BusRouteData.BoardRouteNumber(ActiveRouteLabel);

    string IBusDisplaySource.DestinationHeadsign
    {
        get
        {
            // [FIX] Was reading _targetTerminal.StopName -- the literal name
            // of the physical terminal stop/bay -- instead of the route's
            // rider-facing headsign text. Matches NPCBusController's
            // IBusDisplaySource.DestinationHeadsign exactly now: pull
            // destinationNameOutbound/Inbound off BusRouteData for the
            // active direction, same as RouteQualifier already does above.
            BusRouteData route = BusScheduler.Instance != null ? BusScheduler.Instance.GetRouteData(_activeRoute) : null;
            if (route == null) return TargetTerminalName; // fallback if route data isn't resolvable
            return route.GetDestinationName(_isOutbound, _activeVariant);
        }
    }

    string IBusDisplaySource.RouteQualifier
    {
        get
        {
            BusRouteData route = BusScheduler.Instance != null ? BusScheduler.Instance.GetRouteData(_activeRoute) : null;
            if (route == null) return "";
            return _isOutbound ? route.routeQualifierOutbound : route.routeQualifierInbound;
        }
    }

    string IBusDisplaySource.CurrentStopName
    {
        get
        {
            var stops = GetActiveStops(_isOutbound);
            int idx = CurrentStopIndex - 1; // the stop just served, if any
            if (stops == null || idx < 0 || idx >= stops.Count) return "—";
            return stops[idx].stopName;
        }
    }

    string IBusDisplaySource.NextStopName => NextStopName;

    bool IBusDisplaySource.IsNextStopRequested => StopRequested;

    List<string> IBusDisplaySource.GetUpcomingStops(int count)
    {
        var result = new List<string>(count);
        var stops = GetActiveStops(_isOutbound);
        if (stops == null || _nextStopIndex < 0) return result;

        for (int i = _nextStopIndex; i < stops.Count && result.Count < count; i++)
            result.Add(stops[i].stopName);

        return result;
    }

    // ── [07-30] added for the redesigned LCD board ──────────────────────────
    int  IBusDisplaySource.FleetNumber     => FleetNumber;
    bool IBusDisplaySource.IsEngineRunning =>
        playerBus != null && playerBus.audioEngine != null
        && playerBus.audioEngine.engineState == BusAudioEngine.EngineRunState.Running;

    bool IBusDisplaySource.IsInService => _shiftState == PlayerShiftState.InService;

    List<(string stopName, string etaLabel, bool isTerminal)> IBusDisplaySource.GetUpcomingStopsWithEta(int count)
    {
        var result = new List<(string, string, bool)>(count);
        var stops  = GetActiveStops(_isOutbound);
        if (stops == null || _nextStopIndex < 0 || playerBus == null) return result;

        // [FIX 07-31] _nextStopIndex is the REAL, boarding/announcement-
        // driving index -- left untouched here, still only advances via
        // UpdateStopProximity when the bus gets close to a NEW stop, exactly
        // as before, so passenger boarding logic is unaffected by this fix.
        //
        // But that's also what the board's display was starting its list
        // from, which is why an already-served stop stuck around: it only
        // fell off once you got close to the NEXT one, not once you'd
        // actually left the old one's radius. Those are different moments
        // whenever there's any gap between stops at all. displayStartIndex
        // is a separate, cosmetic-only index: if the stop at _nextStopIndex
        // has already been announced (served) AND the bus is now outside
        // its radius, the display moves on immediately -- independent of
        // whether the NEXT stop is anywhere close yet.
        int displayStartIndex = _nextStopIndex;
        if (displayStartIndex >= 0 && displayStartIndex < stops.Count
            && _announcedStops.Contains(displayStartIndex))
        {
            float distToServedStop = Vector3.Distance(playerBus.transform.position, stops[displayStartIndex].GetWorldPosition());
            if (distToServedStop > stopDetectRadius) displayStartIndex++;
        }
        if (displayStartIndex >= stops.Count) return result;

        BusRouteData route = BusScheduler.Instance != null ? BusScheduler.Instance.GetRouteData(_activeRoute) : null;
        if (route == null) return result;

        // [FIX 07-31b] Slot selection. This used to read ONLY the
        // scheduler's _slotByBus map via TryGetAssignedSlot -- the exact map
        // BusScheduler's own comments flag as capable of drifting stale (it
        // can still point at the PREVIOUS leg around a chain advance or
        // handoff). A stale slot's scheduledDeparture is in the PAST, so
        // every row's "dep + offset - now" collapsed toward zero: the origin
        // pinned at "<1 min" and the rest compressed down to raw travel
        // offsets -- the "board says <1 while I'm still minutes from my
        // pull-out" bug. _activeSlot is the slot the REAL departure gate
        // runs off (Update's "GameTimeMinutes < _activeSlot.scheduledDeparture"
        // check), so when it exists and isn't Completed it wins outright;
        // the scheduler map is only a fallback, and a Completed slot from
        // EITHER source counts as no schedule at all.
        TimetableSlot dispSlot = null;
        if (BusScheduler.Instance != null
            && BusScheduler.Instance.TryGetAssignedSlot(PlayerBusID, out var mappedSlot)
            && mappedSlot != null && mappedSlot.state != SlotState.Completed)
            dispSlot = mappedSlot;
        if (_activeSlot != null && _activeSlot.state != SlotState.Completed)
            dispSlot = _activeSlot;

        if (dispSlot == null)
        {
            // No live schedule to anchor to. "--" beats confidently wrong:
            // the old code fell back to scheduledDeparture = 0 (midnight),
            // clamped the negative result to zero, and rendered THAT as a
            // perfectly confident "<1 min".
            for (int i = displayStartIndex; i < stops.Count && result.Count < count; i++)
                result.Add((stops[i].stopName, "--", i == stops.Count - 1));
            return result;
        }
        float scheduledDeparture = dispSlot.scheduledDeparture;

        // [FIX 07-31] Corrected model, replacing last round's kinematic
        // distance/speed calc entirely:
        //
        //   - Pre-departure: PURE schedule, no adjustment at all. Nothing
        //     about "late" or "early" applies until the bus has actually
        //     left -- this branch is untouched from before.
        //
        //   - Post-departure: still schedule as the BASE for every row
        //     (each stop's real per-stop timetable offset), but adjusted by
        //     ONE shared "how are we running" measurement computed ONCE per
        //     refresh -- not per row, which is what caused the old
        //     cross-row inconsistency bugs. That measurement compares now
        //     against where the schedule expected the bus to be, using a
        //     bounded (can't overshoot) nearest-stop search. A 1-minute
        //     deadband means small drift (noise, normal variation) reads as
        //     exactly on schedule with zero adjustment; only once the bus
        //     is genuinely more than a minute off does the offset apply --
        //     added on top of every row's schedule figure if late, pulled
        //     down if early. Same offset, every row, so they can never
        //     show inconsistent numbers relative to each other.
        // [FIX 07-31b] "Hasn't departed" is now decided by clock + physics,
        // not the shift-state enum alone. If the game clock hasn't reached
        // scheduledDeparture AND the bus is still physically sitting at the
        // origin stop, the trip has NOT begun -- whatever the enum
        // momentarily reads during a handoff/chain transition. You cannot be
        // "running early" before pull-out: the departure time is fixed. The
        // "leaving right now" collapse happened exactly when the enum read
        // as departed while the clock hadn't reached departure -- the offset
        // pass below measured now vs the origin's expected time, decided the
        // bus was several minutes EARLY, and subtracted that from every row,
        // which cancelled the wait entirely and turned the board into raw
        // travel times from this instant.
        float aheadBehindMinutes = ComputeAheadBehindMinutes(
            stops, route, dispSlot, scheduledDeparture, displayStartIndex, out bool busHasNotDepartedYet);

        for (int i = displayStartIndex; i < stops.Count && result.Count < count; i++)
        {
            bool isTerminal = i == stops.Count - 1;
            string eta;
            float now = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : scheduledDeparture;
            float threshold = BusTrackerService.Instance != null ? BusTrackerService.Instance.arrivingThresholdMinutes : 1f;

            float scheduledArrival;
            if (i == displayStartIndex && busHasNotDepartedYet)
            {
                // The stop we're sitting at right now, pre-departure: pure
                // departure countdown, no per-stop offset needed at all.
                scheduledArrival = scheduledDeparture;
            }
            else if (BusTrackerService.Instance != null
                     && BusTrackerService.Instance.TryGetScheduledOffsetMinutes(route, _isOutbound, _activeVariantLetter, i, out float offsetMins))
            {
                scheduledArrival = scheduledDeparture + offsetMins;
            }
            else
            {
                result.Add((stops[i].stopName, "--", isTerminal));
                continue;
            }

            float minsAway = Mathf.Max(0f, (scheduledArrival + aheadBehindMinutes) - now);
            eta = minsAway < threshold ? "<1 min" : $"{Mathf.CeilToInt(minsAway)} min";
            result.Add((stops[i].stopName, eta, isTerminal));
        }
        return result;
    }

    // [ADD] Extracted verbatim out of GetUpcomingStopsWithEta above -- this
    // was previously computed ONLY inline for the board's ETA rows, which
    // meant it was the one genuinely correct dynamic-lateness calculation in
    // the whole class (per-stop offset math, bounded nearest-stop search,
    // 1-minute deadband) while the standalone LatenessMinutes property
    // below just called out to BusScheduler.GetLatenessMinutes -- a
    // terminal/departure-only figure with none of this per-stop nuance.
    // Factored out so BOTH the board AND the new driver-dash
    // ScheduleAdherenceMinutes can share the one correct implementation
    // instead of the dashboard silently using the cruder number.
    private float ComputeAheadBehindMinutes(
        List<BusStopData> stops, BusRouteData route, TimetableSlot dispSlot,
        float scheduledDeparture, int displayStartIndex, out bool busHasNotDepartedYet)
    {
        busHasNotDepartedYet = false;
        if (dispSlot == null || route == null || playerBus == null) return 0f;

        float nowMinutes = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : scheduledDeparture;
        bool physicallyAtOrigin = stops.Count > 0
            && Vector3.Distance(playerBus.transform.position, stops[0].GetWorldPosition()) <= stopDetectRadius;
        busHasNotDepartedYet =
            _shiftState == PlayerShiftState.WaitingToDepart || _shiftState == PlayerShiftState.ArrivedAtTerminal
            || (physicallyAtOrigin && nowMinutes < scheduledDeparture);

        if (busHasNotDepartedYet)
        {
            float lateBy = nowMinutes - scheduledDeparture;
            return lateBy <= 1f ? 0f : lateBy;
        }
        if (BusTrackerService.Instance == null) return 0f;

        float nowForOffset = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : scheduledDeparture;
        int nearestIdx = 0;
        float best = float.MaxValue;
        for (int j = 0; j <= displayStartIndex && j < stops.Count; j++)
        {
            float d = Vector3.Distance(playerBus.transform.position, stops[j].GetWorldPosition());
            if (d < best) { best = d; nearestIdx = j; }
        }
        if (!BusTrackerService.Instance.TryGetScheduledOffsetMinutes(route, _isOutbound, _activeVariantLetter, nearestIdx, out float nearestOffsetMins))
            return 0f;

        float expectedNow = scheduledDeparture + nearestOffsetMins;
        float rawAheadBehind = nowForOffset - expectedNow;
        return Mathf.Abs(rawAheadBehind) <= 1f ? 0f : rawAheadBehind;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  IBusDriverDisplaySource -- backs BusDriverLCDBoard. Speed and doors/
    //  park-brake are read straight off the same fields BusDashboardHUD and
    //  the light controllers already use (bus.spd, playerBus.parkingBrake,
    //  PlayerInteriorLights.IsDoorOpen) -- no new state introduced, just
    //  exposed through the interface.
    // ═════════════════════════════════════════════════════════════════════
    string IBusDriverDisplaySource.RouteLabel => ActiveRouteLabel;

    float IBusDriverDisplaySource.SpeedKph => playerBus != null ? Mathf.Abs(playerBus.spd) : 0f;

    // [FIX] Reuses ComputeAheadBehindMinutes -- the real per-stop dynamic
    // calc -- instead of the terminal-only BusScheduler.GetLatenessMinutes
    // the standalone LatenessMinutes property below still uses. Needs the
    // same route/slot/stops setup GetUpcomingStopsWithEta already does, so
    // this re-derives that context rather than trying to cache it (it's
    // already cheap -- a handful of Vector3.Distance calls, same cost the
    // board pays every refresh anyway).
    float IBusDriverDisplaySource.ScheduleAdherenceMinutes
    {
        get
        {
            var stops = GetActiveStops(_isOutbound);
            if (stops == null || _nextStopIndex < 0 || playerBus == null) return 0f;

            int displayStartIndex = _nextStopIndex;
            if (displayStartIndex >= 0 && displayStartIndex < stops.Count
                && _announcedStops.Contains(displayStartIndex))
            {
                float distToServedStop = Vector3.Distance(playerBus.transform.position, stops[displayStartIndex].GetWorldPosition());
                if (distToServedStop > stopDetectRadius) displayStartIndex++;
            }
            if (displayStartIndex >= stops.Count) return 0f;

            BusRouteData route = BusScheduler.Instance != null ? BusScheduler.Instance.GetRouteData(_activeRoute) : null;
            if (route == null) return 0f;

            TimetableSlot dispSlot = null;
            if (BusScheduler.Instance != null
                && BusScheduler.Instance.TryGetAssignedSlot(PlayerBusID, out var mappedSlot)
                && mappedSlot != null && mappedSlot.state != SlotState.Completed)
                dispSlot = mappedSlot;
            if (_activeSlot != null && _activeSlot.state != SlotState.Completed)
                dispSlot = _activeSlot;
            if (dispSlot == null) return 0f;

            return ComputeAheadBehindMinutes(stops, route, dispSlot, dispSlot.scheduledDeparture, displayStartIndex, out _);
        }
    }

    float IBusDriverDisplaySource.DistanceToNextStopMetres
    {
        get
        {
            var stops = GetActiveStops(_isOutbound);
            if (stops == null || _nextStopIndex < 0 || _nextStopIndex >= stops.Count || playerBus == null) return -1f;
            return Vector3.Distance(playerBus.transform.position, stops[_nextStopIndex].GetWorldPosition());
        }
    }

    // No run/block tracking exists in this class today -- returns empty so
    // the driver board just omits the line rather than showing a fake value.
    // Wire this to a real run/block field if/when the scheduler tracks one.
    string IBusDriverDisplaySource.RunOrBlockLabel => "";

    bool IBusDriverDisplaySource.DoorsOpen => PlayerInteriorLights != null && PlayerInteriorLights.IsDoorOpen;

    bool IBusDriverDisplaySource.ParkingBrakeSet => playerBus != null && playerBus.parkingBrake;

    // [ADD] Wheelchair lift ramp -- no mesh yet, so the 2D driver LCD board
    // and driver console are the only places this is visible at all.
    string IBusDriverDisplaySource.RampStateLabel => _rampState switch
    {
        RampState.Deploying  => $"DEPLOYING {_rampProgress01 * 100f:F0}%",
        RampState.Loading    => "DEPLOYED — LOADING",
        RampState.Deployed   => "DEPLOYED",
        RampState.Retracting => $"RETRACTING {_rampProgress01 * 100f:F0}%",
        _                    => "STOWED",
    };
    bool IBusDriverDisplaySource.AdaPaxEventPending => AdaEventPending;

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Player Bus Reference")]
    public BusSimulationController playerBus;

    [Header("Terminal Detection")]
    public float terminalArrivalRadius = 40f;

    [Header("Stop System")]
    public float   stopDetectRadius    = 50f;
    // [ADD] Used only for selecting which stop becomes _nextStopIndex
    // (UpdateStopProximity). Deliberately much tighter than
    // stopDetectRadius/stopZoneHalfExtents (which govern the "has the bus
    // left this stop's zone yet" / AtStop door-open checks) so that on
    // routes with loops or closely-spaced stops, a stop further down the
    // route that happens to be geometrically nearby can't get selected
    // ahead of the stop the bus actually needs to reach next. Matches the
    // real door/curb pickup range rather than the wide detection radius.
    public float   pickupDetectRadius  = 50f;   // wide enough that the next stop and its distance show early enough at speed
    [Tooltip("A stop counts as reachable only if the bus is within this height of it, so a bus crossing a bridge doesn't 'reach' a stop on the road far below (or above) it.")]
    public float   stopSameLevelTolerance = 8f;
    public Vector3 stopZoneHalfExtents = new Vector3(10f, 10f, 10f);

    [Header("Hotkeys")]
    public bool    enableHotkeys = true;

    [Header("Pax Boarding")]
    public Transform doorTransform;
    public float doorSideOffset = 1.3f;
    public float doorForwardOffset = 4.0f;

    public Vector3 GetDoorWorldPosition()
    {
        if (doorTransform != null) return doorTransform.position;
        if (playerBus == null) return transform.position;
        Transform t = playerBus.transform;
        return t.position - t.forward * doorForwardOffset + t.right * doorSideOffset;
    }

    private readonly BoardingHandle _boardingHandle = new BoardingHandle();
    private bool _boardingInProgress = false;

    // ── Shift state ───────────────────────────────────────────────────────────
    public enum PlayerShiftState
    {
        OffDuty,
        FreeDrive,
        DeadrunToStart,
        WaitingToDepart,
        InService,
        ArrivedAtTerminal,
        ReliefPending,
        Relieved,
        Breakdown,
    }

    // [ADD] "Remembers" whether the CURRENT playerBus session originated
    // from Free Drive (grabbed via BSM/Custom with no route) rather than
    // being adopted mid-route via the shift menu. Set true in
    // ResetForFreshPossession, false in AdoptInServiceSlot -- consulted by
    // EndShift()/ReturnToOffDuty() to decide whether finishing a shift
    // should return the player to Free Drive on the SAME bus (no teleport,
    // no release) or genuinely go OffDuty (teleport bus home, release it).
    private bool _freeDriveOrigin = false;

    private PlayerShiftState _shiftState = PlayerShiftState.OffDuty;
    public  PlayerShiftState ShiftState  => _shiftState;

    // ── Route / variant ───────────────────────────────────────────────────────
    private string           _activeRoute        = "";
    private string           _activeVariantLetter= "";
    private RouteVariantData _activeVariant      = null;

    public string ActiveRouteLabel => _activeRoute + _activeVariantLetter;
    public string ActiveRoute      => _activeRoute;
    public string ActiveVariant    => _activeVariantLetter;

    private bool          _isOutbound        = true;
    private TimetableSlot _activeSlot;
    private int           _segmentsCompleted = 0;
    public  bool           IsOutbound        => _isOutbound;
    public  int            SegmentsCompleted => _segmentsCompleted;
    public  int            OnTimeDepartureCount => _onTimeDepartures;
    public  int            LateDepartureCount   => _lateDepartures;
    public  int            EarlyDepartureCount   => _earlyDepartures;
    public  TimetableSlot  ActiveSlot        => _activeSlot;

    // [ADDED] Snapshot of the direction the leg that JUST finished was
    // running, taken the instant it ends (OnPlayerArrivedAtTerminal) —
    // before anything (BeginLeg/ContinueAssignedChain/ApplyJoinedSlot)
    // gets a chance to overwrite _isOutbound for whatever comes next.
    // ShiftBoardMenu reads this to prefer offering the OPPOSITE direction
    // on "continue" (you're physically sitting at the terminal the
    // opposite leg departs from), instead of just taking the next
    // chronological slot regardless of direction. Nullable so "no leg has
    // completed yet this session" (fresh possession, mid-game load) is
    // distinguishable from "last leg was outbound" — cleared back to null
    // whenever a new leg actually begins, so it can't go stale and get
    // applied against some much-later, unrelated arrival.
    private bool? _lastCompletedLegOutbound = null;
    public  bool? LastCompletedLegWasOutbound => _lastCompletedLegOutbound;

    private BusStopMarker _currentTerminalA;
    private BusStopMarker _currentTerminalZ;
    private BusStopMarker _targetTerminal;
    public  string TargetTerminalName => _targetTerminal != null ? _targetTerminal.StopName : "—";
    /// <summary>World position of the current target terminal, or null if
    /// none is set — added for ShiftHUD's bearing-arrow display, which
    /// otherwise has no way to compute a direction from just the distance
    /// float DistToTargetTerminal already exposes.</summary>
    public  Vector3? TargetTerminalPosition => _targetTerminal != null ? _targetTerminal.transform.position : (Vector3?)null;

    private int  _replacementBusID        = -1;
    private bool _reliefRequested         = false;
    private bool _departOverridePending   = false;

    // ── Scoring ───────────────────────────────────────────────────────────────
    private float _shiftScore        = 0f;
    private int   _onTimeDepartures  = 0;
    private int   _lateDepartures    = 0;
    private int   _earlyDepartures   = 0;
    private float _totalLatenessAccum= 0f;
    public  float ShiftScore => _shiftScore;

    // ── Breakdown ─────────────────────────────────────────────────────────────
    private bool  _isBreakdown        = false;
    private float _breakdownStartTime = 0f;
    public  bool  IsBreakdown         => _isBreakdown;

    // ── Passenger state ───────────────────────────────────────────────────────
    private int  _onboardPax           = 0;
    private bool _stopRequested        = false;
    private int  _alightCount          = 0;
    private bool _hasProcessedStop     = false;
    private int  _lastDoorOpenOff      = 0;
    private int  _lastDoorOpenOn       = 0;
    private int  _predictedBoardingPax = 0;
    private readonly List<int> _passengerDestinations = new List<int>();
    // [ADD] Stop requests now stack instead of vanishing: if the bus rolls
    // past a stop with an unmet alight request (doors never opened there),
    // that count folds in here and gets merged into the NEXT stop's own
    // request in UpdateStopProximity, rather than being silently overwritten
    // and lost. See UpdateStopProximity's own comment for the merge logic.
    private int  _missedAlightCarryover = 0;

    // Real pax plan for the CURRENT leg, rolled once in BeginLeg — same
    // time-band/zone-aware system NPCBusController uses, replacing the old
    // flat Random.Range(0,7)/(0,8) rolls that had no idea what time of day
    // it even was (hence "5 pax at 3am" being possible before this).
    private PaxRollPlan _playerPaxPlan;

    public int  OnboardPax           => _onboardPax;
    /// <summary>Seated + standing capacity of the bus being driven, from its fleet series.</summary>
    public int  PassengerCapacity    => FleetMetadata.CapacityFor(FleetNumber);
    public bool StopRequested        => _stopRequested;
    public int  AlightCount          => _alightCount;
    public int  PredictedBoardingPax => _predictedBoardingPax;

    // ═════════════════════════════════════════════════════════════════════════
    //  ADA / WHEELCHAIR-LIFT PAX
    //  No per-passenger object/type system exists anywhere in this project yet
    //  (PaxAgent is generic, no color/type field) -- modeled here the same way
    //  every other pax number already is, as plain counters/flags on
    //  PlayerHandoff, rather than a bigger refactor of PaxSimManager/PaxAgent.
    //  No mesh yet either, so the ramp itself is represented purely as state
    //  the driver console / 2D LCD board can display (see HandleRampToggle/
    //  UpdateRampState below and their DriverConsole/BusDriverLCDBoard hooks).
    // ═════════════════════════════════════════════════════════════════════════
    private const int ADA_CAPACITY = 2;
    private int  _onboardAdaPax      = 0;     // 0..ADA_CAPACITY
    private bool _adaBoardingWaiting = false; // an ADA pax is waiting to board at the CURRENT stop
    private bool _adaAlightRequested = false; // an onboard ADA pax wants off at the CURRENT stop
    // Regular boarding rolled at a stop with an ADA event pending is held
    // here instead of applied immediately -- the wheelchair pax boards/
    // alights first, everyone else waits for the ramp to fully stow again
    // (see ProcessStopDoorOpen / ReleaseDeferredBoarding).
    private int  _deferredBoardingPax = 0;

    public int  OnboardAdaPax   => _onboardAdaPax;
    /// <summary>True while there's a wheelchair pax either waiting to board
    /// or wanting off at the CURRENT stop -- drives the "ADA Pax" override
    /// on the stop-requested display (see DriverConsole/BusDriverLCDBoard).
    /// Regular alighting/boarding for everyone else still happens
    /// independently of this -- it only overrides what gets SHOWN.</summary>
    public bool AdaEventPending => _adaBoardingWaiting || _adaAlightRequested;
    public bool AdaAlightRequested => _adaAlightRequested;

    public enum RampState { Stowed, Deploying, Loading, Deployed, Retracting }
    private RampState _rampState      = RampState.Stowed;
    private float     _rampProgress01 = 0f; // 0-1, meaningful only during Deploying/Retracting
    private float     _rampTimer      = 0f;
    private bool      _rampHiccupDone = false;

    public RampState CurrentRampState => _rampState;
    public float     RampProgress01   => _rampProgress01;

    private const float RAMP_DEPLOY_SECONDS   = 10f;
    private const float RAMP_HICCUP_AT        = 0.55f; // fraction through deploy where it pauses
    private const float RAMP_HICCUP_PAUSE_SEC = 1.0f;
    private const float RAMP_LOAD_SECONDS     = 5f;
    private const float RAMP_RETRACT_SECONDS  = 10f;

    /// <summary>True exactly when the upcoming stop has zero predicted
    /// boarding AND zero requested alighting — i.e. the "skip" case. Exposed
    /// so UI (Driver.cs) can just check one property instead of manually
    /// checking both counts every time.</summary>
    public bool WillSkipStop => _predictedBoardingPax == 0 && !_stopRequested;

    // ── Stop proximity ────────────────────────────────────────────────────────
    private Transform[]  _busStops              = Array.Empty<Transform>();
    private int          _nextStopIndex         = -1;
    private float        _distToNextStop        = float.MaxValue;
    private HashSet<int> _announcedStops        = new HashSet<int>();
    private readonly HashSet<int> _levelGateReported = new HashSet<int>();
    private readonly HashSet<string> _closedStopReported = new HashSet<string>();

    /// <summary>True if an active road event closes this stop for the player's current route + direction.</summary>
    private bool IsStopClosedForPlayer(BusStopData s)
    {
        var reg = RoadEventRegistry.Instance;
        return reg != null && s != null && !string.IsNullOrEmpty(_activeRoute)
               && reg.IsStopClosed(s.stopCode, _activeRoute, _isOutbound);
    }

    private void OnRoadEventCleared(RoadEvent ev) => _closedStopReported.Clear();

    /// <summary>True while the stop the HUD is pointing at is closed by a road event.</summary>
    public bool IsNextStopClosed
    {
        get
        {
            var stops = GetActiveStops(_isOutbound);
            return stops != null && _nextStopIndex >= 0 && _nextStopIndex < stops.Count
                   && IsStopClosedForPlayer(stops[_nextStopIndex]);
        }
    }
    private bool         _stopsInitialized      = false;
    private int          _lastProcessedStopIndex = -1;

    private float _cachedDist        = float.MaxValue;
    private int   _cachedNextIdx     = -1;
    private bool  _cachedStopReq     = false;
    private int   _cachedAlightCount = 0;
    private int   _cachedPax         = 0;
    private int   _cachedBoardingPax = 0;

    public float DistToNextStop => _distToNextStop;

    /// <summary>Current schedule lateness in minutes — positive = running
    /// late, negative = running early. Pulled from the same scheduler call
    /// CmdStatus already uses, just exposed for HUD/console display.
    ///
    /// [FLAG] This is BusScheduler.GetLatenessMinutes -- a terminal/
    /// departure-anchored figure, NOT the per-stop dynamic calc used
    /// elsewhere in this class (see ComputeAheadBehindMinutes, which backs
    /// both the board's ETA rows and IBusDriverDisplaySource.
    /// ScheduleAdherenceMinutes on the driver dash). If you're adding a new
    /// display that wants "how late am I RIGHT NOW relative to the nearest
    /// stop," use the driver-source property instead of this one -- this
    /// one only reflects how the CURRENT LEG started out, not ongoing
    /// per-stop drift. Left in place for whatever existing callers
    /// (console/HUD) already depend on this specific behavior.</summary>
    public float LatenessMinutes =>
        BusScheduler.Instance != null ? BusScheduler.Instance.GetLatenessMinutes(PlayerBusID) : 0f;

    public string NextStopName
    {
        get
        {
            var stops = GetActiveStops(_isOutbound);
            if (stops == null || _nextStopIndex < 0 || _nextStopIndex >= stops.Count) return "—";
            return stops[_nextStopIndex].stopName;
        }
    }

    public float DistToTargetTerminal
    {
        get
        {
            if (_targetTerminal == null || playerBus == null) return float.MaxValue;
            return Vector3.Distance(playerBus.transform.position, _targetTerminal.transform.position);
        }
    }

    // ── Fleet display identity (separate from scheduler identity) ─────────────
    private int _cachedFleetNumber = -1;
    public  int FleetNumber => _cachedFleetNumber >= 0 ? _cachedFleetNumber : PlayerBusID;
    public void SetFleetNumber(int fleetNumber) => _cachedFleetNumber = fleetNumber;

    private void Start()
    {
        if (BusScheduler.Instance != null)
        {
            BusScheduler.Instance.OnPlayerSlotReady += HandleSlotReady;
            // [ADD] Reports when the relief bus you accepted actually pulls
            // out and starts covering your route — see ConfirmReliefSwap and
            // _pendingReliefHandoffSlot for the rest of this mechanism.
            BusScheduler.Instance.OnAssignedDeparture += HandleReliefHandoffDeparture;
        }

        // [ADD] Full-shutdown breakdowns previously never touched relief at
        // all -- break down full-stop mid-route and nothing ever suggested
        // requesting relief, you had to already know to type it yourself.
        // Suggests, doesn't auto-trigger -- the player might want to just
        // wait it out (a self-resolving breakdown clears on its own; only
        // a requiresReplacement one actually needs a real relief swap).
        BusBreakdownSystem.OnBreakdownTriggered += HandleBreakdownTriggeredForRelief;

        InitStopList();
        _onboardPax = UnityEngine.Random.Range(0, 8);
        DriverConsole.Instance?.RefreshStopHUD();
    }

    private void OnDestroy()
    {
        if (BusScheduler.Instance != null)
        {
            BusScheduler.Instance.OnPlayerSlotReady -= HandleSlotReady;
            BusScheduler.Instance.OnAssignedDeparture -= HandleReliefHandoffDeparture;
        }

        BusBreakdownSystem.OnBreakdownTriggered -= HandleBreakdownTriggeredForRelief;
        RoadEvent.Cleared -= OnRoadEventCleared;
    }

    /// <summary>[ADD] Breakdown → relief connective tissue. Only acts on a
    /// FULL-severity breakdown (RequiresFullShutdown) on the player's own
    /// physical bus -- Partial/None severities don't stop you from driving
    /// at all, so suggesting relief for those would be premature noise.
    /// Compares against the real resolved busID, not PlayerBusID -- see
    /// CmdBreakdown's own fix a few methods up for why that distinction
    /// matters here specifically.</summary>
    private void HandleBreakdownTriggeredForRelief(int busID, BreakdownType type, bool requiresReplacement)
    {
        if (!IsOnDuty || _reliefRequested) return;
        if (busID != GetBusID(playerBus)) return;
        if (BusBreakdownSystem.Instance == null || !BusBreakdownSystem.Instance.RequiresFullShutdown(busID)) return;

        PrintTagged($"⚠ {type} has disabled this bus. Type <b>relief</b> (or press {KeyBindings.Current.relief}) to request a replacement.", "warn");
    }

    /// <summary>[ADD] The slot ConfirmReliefSwap handed to the relief bus —
    /// tracked so we can tell you the moment that bus actually pulls out
    /// and starts running it, instead of leaving "relief bus found, swap
    /// confirmed" as the last thing you hear about it. Cleared once the
    /// matching departure fires (or if a new relief request supersedes it).</summary>
    private TimetableSlot _pendingReliefHandoffSlot;
    private int _pendingReliefHandoffBusID = -1;

    private void HandleReliefHandoffDeparture(TimetableSlot slot)
    {
        if (_pendingReliefHandoffSlot == null || slot != _pendingReliefHandoffSlot) return;
        if (slot.assignedBusID != _pendingReliefHandoffBusID) return; // reassigned again before it ever pulled out

        int fleet = BusManager.Instance?.GetRecord(_pendingReliefHandoffBusID)?.controller?.fleetNumber ?? _pendingReliefHandoffBusID;
        PrintTagged($"✅ Relief bus (Fleet #{fleet}) has pulled out and taken over Route {slot.FullRouteLabel} {slot.DirectionLabel} — you're covered.", "success");

        _pendingReliefHandoffSlot = null;
        _pendingReliefHandoffBusID = -1;
    }

    private void Update()
    {
        if (enableHotkeys)
        {
            if (Input.GetKeyDown(KeyBindings.Current.relief))      RequestRelief();
            if (Input.GetKeyDown(KeyBindings.Current.status))      PrintStatus();
            if (Input.GetKeyDown(KeyBindings.Current.kneel))       HandleKneelToggle();
            if (Input.GetKeyDown(KeyBindings.Current.unstuck))     HandleUnstuck();
            if (Input.GetKeyDown(KeyBindings.Current.ignition))    HandleIgnitionToggle();
            if (Input.GetKeyDown(KeyBindings.Current.leftSignal))  HandleLeftSignalToggle();
            if (Input.GetKeyDown(KeyBindings.Current.rightSignal)) HandleRightSignalToggle();
            if (Input.GetKeyDown(KeyBindings.Current.hazards))      HandleHazardToggle();
            if (Input.GetKeyDown(KeyBindings.Current.door))
            {
                if (KeyBindings.ShiftModifierHeld)
                    HandleRearDoorToggle();
                else
                    HandleDoorToggle();
            }
            if (Input.GetKeyDown(KeyBindings.Current.rampDeploy)) HandleRampToggle();
        }
        UpdateBrakeHoldDoorToggle();
        UpdateStopProximity();
        UpdateRampState();
        CheckAutoArrival();
        CheckAutoDepart();
        UpdatePlayerBreakdowns();
    }

    // ── Brake-hold auto door (slam brakes at a dead stop → doors pop) ─────────
    [Header("Auto Door on Brake-Hold")]
    [Tooltip("How long the brake must be held at ~0 kph before the front door auto-toggles.")]
    public float brakeHoldDoorSeconds = 3f;
    [Tooltip("Speed (kph) below which the bus counts as \"stopped\" for this check.")]
    public float brakeHoldSpeedThreshold = 0.5f;

    private float _brakeHoldTimer    = 0f;
    private bool  _brakeHoldConsumed = false; // fired once per hold; must release brake to re-arm

    private void UpdateBrakeHoldDoorToggle()
    {
        if (playerBus == null) { _brakeHoldTimer = 0f; _brakeHoldConsumed = false; return; }

        // NOTE: can't use playerBus.brakeKey here — BusController force-zeros
        // it whenever drivingLocked is true (doors open / parking brake set),
        // which is exactly the state we need to detect the "slam to close"
        // in. Poll the raw pedal keys directly instead; at a dead stop,
        // direction (Drive/Reverse) doesn't matter for "is the brake held".
        bool rawBrakeHeld = KeyBindings.ThrottleHeld || KeyBindings.BrakeHeld;

        bool stoppedAndBraking = rawBrakeHeld && Mathf.Abs(playerBus.spd) < brakeHoldSpeedThreshold;

        if (stoppedAndBraking)
        {
            _brakeHoldTimer += Time.deltaTime;
            if (_brakeHoldTimer >= brakeHoldDoorSeconds && !_brakeHoldConsumed)
            {
                HandleDoorToggle();
                _brakeHoldConsumed = true; // release the brake and slam again to fire the next toggle
            }
        }
        else
        {
            _brakeHoldTimer    = 0f;
            _brakeHoldConsumed = false;
        }
    }

    // ROOT CAUSE FIX: this used to be gated only by
    // "_shiftState == ArrivedAtTerminal", which is NOT sufficient — when
    // TryAutoContinueOrSignOff auto-continues to the next chained leg, it
    // sets _shiftState back to InService for that new leg BEFORE the next
    // Update() call. If the player is still physically sitting inside
    // terminalArrivalRadius (which they always are — no time has passed),
    // this method would fire AGAIN on the very next frame, chain to yet
    // another leg, and repeat every single Update() call — exhausting the
    // whole day's pre-assigned rotation in a fraction of a second. That's
    // the "auto arrived executes 10 million times" bug.
    //
    // Fix: a rearm latch. Once arrival fires, the player must physically
    // move OUTSIDE the radius (by a small margin, so idling right at the
    // edge can't flicker it) before arrival is allowed to fire again.
    [Header("Auto-Arrival Rearm")]
    [Tooltip("If true (recommended for routes whose loop geometry swings back near the terminal mid-leg), arrival never fires automatically off proximity alone — the player must type 'arrive'/'arrived' in the console. If false, restores the old behavior where simply driving within terminalArrivalRadius completes the leg.")]
    public bool requireManualArrival = true;
    [Tooltip("After an arrival fires, the player must move this many multiples of terminalArrivalRadius away before arrival can trigger again. Prevents the same-frame re-trigger loop when a chain auto-continues to a new leg while the player hasn't moved yet. Only relevant when requireManualArrival is false.")]
    [Range(1.2f, 4f)] public float arrivalRearmDistanceMultiplier = 2f;
    private bool _autoArrivalRearmed = true;

    private void CheckAutoArrival()
    {
        if (requireManualArrival) return; // proximity alone never completes the leg — CmdArrived() is the only path in

        if (_shiftState != PlayerShiftState.InService && _shiftState != PlayerShiftState.ReliefPending)
        {
            _autoArrivalRearmed = true; // not mid-leg — always rearmed for whenever service resumes
            return;
        }
        if (_shiftState == PlayerShiftState.ArrivedAtTerminal) return;

        bool near = IsNearTargetTerminal(out float dist);

        if (!_autoArrivalRearmed)
        {
            // Still needs to move away before arrival can fire again.
            if (dist > terminalArrivalRadius * arrivalRearmDistanceMultiplier)
                _autoArrivalRearmed = true;
            return;
        }

        if (!near) return;

        _autoArrivalRearmed = false; // consumed — won't fire again until the player leaves the radius
        OnPlayerArrivedAtTerminal();
    }

    private bool _missedDepartureWarned = false;

    private void CheckAutoDepart()
    {
        // Missed-departure ping: only meaningful in WaitingToDepart. Once you're
        // ArrivedAtTerminal, _activeSlot still points at the leg you just
        // FINISHED (it isn't reassigned until you pick the next leg off the
        // shift board / Continue) — so checking it here was comparing 'now'
        // against the completed trip's old departure time and quoting a
        // nonsense "due X min ago" for a slot that's already done. Use
        // nextdep or the shift board for what's actually coming up next.
        if (_shiftState == PlayerShiftState.WaitingToDepart
            && _activeSlot != null && BusScheduler.Instance != null)
        {
            float overrun = BusScheduler.Instance.GameTimeMinutes - _activeSlot.scheduledDeparture;
            if (overrun > 2f)
            {
                if (!_missedDepartureWarned)
                {
                    _missedDepartureWarned = true;
                    string reason = _isBreakdown ? " — bus is broken down, type <b>recovered</b> first" : "";
                    PrintTagged($"⚠ Departure was due {overrun:F0} min ago{reason}.", "warn");
                }
            }
            else
            {
                _missedDepartureWarned = false; // reset so a future slot can warn again
            }
        }

        if (_isBreakdown) return;
        // [FIX 07-31] ArrivedAtTerminal used to be a valid trigger state
        // here too — but _activeSlot deliberately still points at the leg
        // you just FINISHED while in that state (see the comment a few
        // lines up, which already knows this for the missed-departure
        // warning above but this check didn't apply the same knowledge to
        // itself). A finished leg's scheduledDeparture is always already
        // in the past by the time you've arrived, so the "GameTimeMinutes
        // < scheduledDeparture" guard below could never block it — this
        // fired BeginLeg() on the STALE, already-completed slot the very
        // next frame after arriving, with zero player input. That's the
        // "auto-departs me again on the leg I just finished, reports
        // wrong lateness, replays the same lap number" bug: the auto-fire
        // consumed the arrival before the player ever got to type
        // continue or pick from the board. WaitingToDepart is the only
        // state where _activeSlot is guaranteed to already be the NEW
        // leg (ContinueAssignedChain/the board sets it before landing
        // here), so it's the only state this auto-convenience is safe in.
        if (_shiftState != PlayerShiftState.WaitingToDepart) return;
        if (_activeSlot == null) return;

        var scheduler = BusScheduler.Instance;
        if (scheduler == null) return;

        if (scheduler.GameTimeMinutes < _activeSlot.scheduledDeparture) return;

        // [FIX 07-31] This ArrivedAtTerminal->WaitingToDepart transition is
        // now unreachable — the guard above already requires _shiftState ==
        // WaitingToDepart to get this far. Removed rather than left as dead
        // code implying this method still does something for a state it no
        // longer touches.
        _departOverridePending = false;
        _missedDepartureWarned = false;
        BeginLeg();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  VARIANT HELPERS
    // ═════════════════════════════════════════════════════════════════════════
    private List<BusStopData> GetActiveStops(bool outbound)
    {
        var routeData = BusScheduler.Instance?.GetRouteData(_activeRoute);
        if (routeData == null) return null;
        return routeData.GetStops(outbound, _activeVariant);
    }

    private string GetActiveTerminalCode(bool getTerminalZ)
    {
        var routeData = BusScheduler.Instance?.GetRouteData(_activeRoute);
        if (routeData == null) return "";

        Debug.Log($"[TERMDEBUG] route={routeData.name} (instanceID={routeData.GetInstanceID()}) " +
                  $"activeVariant={(_activeVariant == null ? "NULL" : _activeVariant.variantLetter)} " +
                  $"overrideRoute={(_activeVariant?.overrideRoute.ToString() ?? "n/a")} " +
                  $"terminalZOverride='{_activeVariant?.terminalZCodeOverride}' " +
                  $"terminalAOverride='{_activeVariant?.terminalACodeOverride}' " +
                  $"mainlineZ='{routeData.terminalZCode}' mainlineA='{routeData.terminalACode}'");

        if (_activeVariant != null && _activeVariant.overrideRoute)
        {
            if (getTerminalZ && !string.IsNullOrEmpty(_activeVariant.terminalZCodeOverride))
                return _activeVariant.terminalZCodeOverride;
            if (!getTerminalZ && !string.IsNullOrEmpty(_activeVariant.terminalACodeOverride))
                return _activeVariant.terminalACodeOverride;
        }
        return getTerminalZ ? routeData.terminalZCode : routeData.terminalACode;
    }

    public static void ParseRouteAndVariant(string input, out string route, out string variant)
    {
        input = input.Trim().ToUpper();

        int spaceIdx = input.IndexOf(' ');
        if (spaceIdx > 0)
        {
            route   = input.Substring(0, spaceIdx).Trim();
            variant = input.Substring(spaceIdx + 1).Trim();
            return;
        }

        if (input.Length > 1)
        {
            char last   = input[input.Length - 1];
            string stem = input.Substring(0, input.Length - 1);
            bool stemOk = true;
            foreach (char c in stem) if (!char.IsDigit(c)) { stemOk = false; break; }

            // A trailing letter is a variant ("34A"); a trailing '~' is the short turn ("116~").
            if ((char.IsLetter(last) || last.ToString() == BusRouteData.ShortTurnSymbol) && stemOk)
            {
                route   = stem;
                variant = last.ToString();
                return;
            }
        }

        route   = input;
        variant = "";
    }

    // ── Kneel toggle (K) ───────────────────────────────────────────────────────
    // ADA-style front-axle kneel via BusKneelBody — a purely cosmetic body
    // drop, no physics involved at all (see BusKneelBody for why the earlier
    // spring/joint suspension attempts were scrapped). Gated to
    // parked-with-brake-on, same as a real driver only ever kneels at a
    // stop, never while rolling.
    private BusKneelBody _cachedSuspension;
    private BusKneelBody PlayerSuspension
    {
        get
        {
            if (_cachedSuspension == null && playerBus != null)
                _cachedSuspension = playerBus.GetComponent<BusKneelBody>();
            return _cachedSuspension;
        }
    }

    // ── Interior lights (door-open override) ───────────────────────────────────
    private BusInteriorLightController _cachedInteriorLights;
    private BusInteriorLightController PlayerInteriorLights
    {
        get
        {
            if (_cachedInteriorLights == null && playerBus != null)
                _cachedInteriorLights = playerBus.GetComponent<BusInteriorLightController>();
            return _cachedInteriorLights;
        }
    }

    // [REDESIGN] This used to duplicate the ENTIRE roll + audio-flag +
    // door-open logic that NPCBusController also had its own copy of --
    // exactly the duplication Ted flagged as the root cause of every
    // breakdown bug so far. That's all gone now: BusSimulationController
    // itself calls BusBreakdownSystem.TryRollBreakdown/ApplyBreakdownToVehicle
    // directly in its own FixedUpdate (see BusController.cs), the same way
    // NPCBusController does for AI-driven buses. PlayerHandoff's only job
    // left here is the console UX -- telling the driver what's wrong, how
    // long, and what to do about it.
    private float _lastBreakdownStatusPrintMinute = -999f;

    private void UpdatePlayerBreakdowns()
    {
        if (playerBus == null || BusBreakdownSystem.Instance == null) return;
        if (_shiftState != PlayerShiftState.InService) return;

        // [FIX] Was `int busID = PlayerBusID;` -- PlayerBusID is the
        // abstract -2 sentinel BusScheduler uses for slot bookkeeping, but
        // BusBreakdownSystem's tables (IsBusLocked/GetBreakdownInfo/
        // IsLimpingHome) are always keyed by the real physical busID, the
        // same one the organic roll system in BusSimulationController uses
        // to register breakdowns. Checking -2 here meant this entire
        // "ping the console with active breakdown status" block silently
        // never fired for a real breakdown -- anyActive was always false.
        int busID = GetBusID(playerBus);
        bool anyActive = BusBreakdownSystem.Instance.IsBusLocked(busID);

        // Status ping to the driver console — once when a breakdown starts,
        // then again roughly once a game-minute while it's still active, so
        // the player always has current type/time-left/what-to-do info
        // without spamming the console every frame.
        if (!anyActive) { _lastBreakdownStatusPrintMinute = -999f; return; }

        float nowMinute = BusScheduler.Instance?.GameTimeMinutes ?? 0f;
        if (nowMinute - _lastBreakdownStatusPrintMinute < 1f) return;
        _lastBreakdownStatusPrintMinute = nowMinute;

        foreach (var b in BusBreakdownSystem.Instance.GetBreakdownInfo(busID))
        {
            var meta = BusBreakdownSystem.GetMeta(b.type);
            PrintTagged($"🔧 {b.type} — {b.remainingMinutes:F1} min left. {meta.suggestedAction}", "warn");
        }

        bool limping = BusBreakdownSystem.Instance.IsLimpingHome(busID);
        if (limping)
            PrintTagged("⚠ Head to a Maintenance Bay to fully reset condition — depot alone won't clear this.", "warn");
    }

    // ── Ignition (I) ────────────────────────────────────────────────────────────
    private void HandleIgnitionToggle()
    {
        if (playerBus == null) return;
        var audio = playerBus.audioEngine;
        if (audio == null) return;

        if (!audio.batteryOn)
        {
            PrintTagged("⚡ No power — battery is off.", "error");
            return;
        }

        var before = audio.engineState;
        audio.RequestEngineToggle();

        switch (before)
        {
            case BusAudioEngine.EngineRunState.Off:
                if (audio.IsHybridOrElectric())
                    PrintTagged("🔋 Traction system <b>ON</b>.", "system");
                else
                    PrintTagged("🔑 Cranking... (~8s)", "system");
                break;
            case BusAudioEngine.EngineRunState.ReadyToStart:
                PrintTagged("🚌 Engine <b>RUNNING</b>.", "system");
                break;
            case BusAudioEngine.EngineRunState.Running:
                PrintTagged("🚌 Engine <b>OFF</b>.", "system");
                break;
            case BusAudioEngine.EngineRunState.Cranking:
                PrintTagged("🔑 Already cranking...", "warn");
                break;
        }
    }

    /// <summary>Explicit "wedge" command -- same underlying toggle as the
    /// DOORS button's own no-power fallback (see HandleDoorToggle), just
    /// reachable by name for players who'd rather type it. Refuses while the
    /// battery has power: wedge is a stuck-door fallback, not a shortcut
    /// around the normal door cycle.</summary>
    private void HandleWedgeDoor()
    {
        if (playerBus == null) return;
        var audio = playerBus.audioEngine;
        if (audio != null && audio.batteryOn)
        {
            PrintTagged("Wedge is a stuck-door fallback for when the battery is off -- use <b>door</b> normally while power's on.", "warn");
            return;
        }
        bool nowWedged = playerBus.ToggleWedgeFrontDoor();
        PrintTagged(nowWedged
            ? "🚪 Front door <b>wedged halfway</b> — manual fallback for a stuck door. Type <b>wedge</b> again to release."
            : "🚪 Front door wedge released.",
            "system");
    }

    private bool _quickStartRunning;

    /// <summary>"quickstart" command entry point -- validates then hands off
    /// to the coroutine, same split as every other multi-step command here.</summary>
    private void HandleQuickStart()
    {
        if (playerBus == null) { PrintTagged("No active bus.", "warn"); return; }
        var audio = playerBus.audioEngine;
        if (audio == null) { PrintTagged("No audio engine on this bus.", "error"); return; }
        if (_quickStartRunning) { PrintTagged("Quick Start already in progress.", "warn"); return; }

        if (_isBreakdown)
        {
            PrintTagged("⚠ Bus is broken down — type <b>recovered</b> first.", "warn");
            return;
        }
        if (audio.engineState == BusAudioEngine.EngineRunState.Running)
        {
            PrintTagged("🚌 Engine's already running.", "info");
            return;
        }
        if (audio.engineState == BusAudioEngine.EngineRunState.Cranking)
        {
            PrintTagged("🔑 Already cranking — let it finish, or type <b>ignition</b> once it catches.", "warn");
            return;
        }

        StartCoroutine(QuickStartSequence(audio));
    }

    /// <summary>Battery on, close any open doors (front + rear -- never
    /// reopened, that's a manual call once you're ready to board), then the
    /// same two-press ignition sequence as the ENG button/"ignition"
    /// command, just timed automatically instead of needing a second manual
    /// press once the starter catches. Hybrid/electric buses skip straight
    /// to Running, same as a manual ignition press does for them.</summary>
    private IEnumerator QuickStartSequence(BusAudioEngine audio)
    {
        _quickStartRunning = true;
        PrintTagged("⚡ Quick Start initiated...", "system");

        if (!audio.batteryOn)
        {
            audio.batteryOn = true;
            PrintTagged("🔋 Battery <b>ON</b>.", "system");
        }

        if (playerBus.doorsOpen)     HandleDoorToggle();
        if (playerBus.rearDoorsOpen) HandleRearDoorToggle();

        yield return null; // let the door-close state settle a frame before cranking

        if (audio.IsHybridOrElectric())
        {
            audio.RequestEngineToggle(); // Off -> Running directly
            PrintTagged("🔋 Traction system <b>ON</b>. Quick Start complete.", "success");
            _quickStartRunning = false;
            yield break;
        }

        audio.RequestEngineToggle(); // Off -> Cranking
        PrintTagged("🔑 Cranking... (~8s)", "system");

        while (audio.engineState == BusAudioEngine.EngineRunState.Cranking)
            yield return null;

        if (audio.engineState != BusAudioEngine.EngineRunState.ReadyToStart)
        {
            // Something changed state out from under the sequence -- e.g. a
            // breakdown hit mid-crank. Bail without forcing anything further.
            PrintTagged("Quick Start interrupted.", "warn");
            _quickStartRunning = false;
            yield break;
        }

        audio.RequestEngineToggle(); // ReadyToStart -> Running
        PrintTagged("🚌 Engine <b>RUNNING</b>. Quick Start complete.", "success");
        _quickStartRunning = false;
    }

    /// <summary>Unstuck / right the bus (button, key or the "unstuck" command).</summary>
    public void HandleUnstuck()
    {
        if (playerBus == null) { PrintTagged("No bus to unstick.", "warn"); return; }
        playerBus.RightBus();
        PrintTagged("🔧 Bus righted -- levelled and lifted 1.5 m, same position and heading.", "system");
    }

    private void HandleKneelToggle()
    {
        if (playerBus == null) return;

        var suspension = PlayerSuspension;
        if (suspension == null)
        {
            PrintTagged("No wheel suspension component found on this bus — can't kneel.", "error");
            return;
        }

        if (!suspension.IsKneeling && !playerBus.parkingBrake)
        {
            PrintTagged("Apply the parking brake before kneeling (open doors or set brake first).", "error");
            return;
        }

        suspension.ToggleKneeling();
        PrintTagged(suspension.IsKneeling
            ? "🚌 Kneeling <b>ENGAGED</b> — front axle lowering"
            : "🚌 Kneeling <b>RELEASED</b> — front axle returning to ride height",
            "system");
    }

    // ── Turn signals / hazards ────────────────────────────────────────────────
    private void HandleLeftSignalToggle()
    {
        if (playerBus == null) return;
        var ext = BusExteriorLightController.Find(playerBus);
        if (ext == null) { PrintTagged("No BusExteriorLightController found on this bus.", "error"); return; }
        ext.ToggleLeftSignal();
        PrintTagged(ext.LeftSignalOn ? "◄ Left signal <b>ON</b>." : "Left signal off.", "system");
    }

    private void HandleRightSignalToggle()
    {
        if (playerBus == null) return;
        var ext = BusExteriorLightController.Find(playerBus);
        if (ext == null) { PrintTagged("No BusExteriorLightController found on this bus.", "error"); return; }
        ext.ToggleRightSignal();
        PrintTagged(ext.RightSignalOn ? "Right signal <b>ON</b> ►." : "Right signal off.", "system");
    }

    private void HandleHazardToggle()
    {
        if (playerBus == null) return;
        var ext = BusExteriorLightController.Find(playerBus);
        if (ext == null) { PrintTagged("No BusExteriorLightController found on this bus.", "error"); return; }
        ext.ToggleHazards();
        PrintTagged(ext.HazardsOn ? "⚠ Hazards <b>ON</b>." : "Hazards off.", "system");
    }

    // ── Door toggle ───────────────────────────────────────────────────────────
    private void HandleDoorToggle()
    {
        if (playerBus == null) return;

        var audio = playerBus.audioEngine;
        if (audio != null && !audio.batteryOn)
        {
            // [ADD] With no power the real door motor/valve can't cycle at
            // all, but a driver can still force a wedged half-open position
            // by hand -- same DOORS button/command, no separate one to
            // remember. See HandleWedgeDoor for the explicit "wedge" command.
            bool nowWedged = playerBus.ToggleWedgeFrontDoor();
            PrintTagged(nowWedged
                ? "🚪 Front door <b>wedged halfway</b> — no power to cycle it normally. Press DOORS again to release."
                : "🚪 Front door wedge released.",
                "system");
            return;
        }
        if (audio != null && audio.engineState == BusAudioEngine.EngineRunState.Cranking)
        {
            PrintTagged("🔑 Wait for the starter to finish cranking.", "warn");
            return;
        }

        playerBus.doorsOpen    = !playerBus.doorsOpen;
        playerBus.parkingBrake = playerBus.doorsOpen || playerBus.rearDoorsOpen;

        // [FIX] This only ever wrote BusSimulationController's own
        // doorsOpen/parkingBrake fields. Those fields exist, but nothing
        // reads them into audioEngine anymore while the player is actively
        // driving -- BusController.Update() (and its UpdateSimulation call,
        // which is the only place that pushes audioEngine.parkingBrake from
        // THIS script) bails out early whenever _npcForBusID.isPlayer is
        // true, since NPCBusController is the authoritative driving path
        // once a hop-in has happened (see BusController.Update()'s own
        // comment on this). So opening/closing doors set parkingBrake=true
        // right here, correctly, but that value physically never reached
        // the audio engine -- NPCBusController's OWN separate
        // parkingBrake field (only ever touched by ITS OWN P-key handler)
        // is what actually gets synced to audioEngine.parkingBrake each
        // tick, and doors never touched that copy. Mirroring the same
        // door-driven brake state onto it here so whichever controller is
        // actually authoritative this frame sees the same thing.
        var doorNpc = playerBus.GetComponent<NPCBusController>();
        if (doorNpc != null)
        {
            doorNpc.doorsOpen    = playerBus.doorsOpen;
            doorNpc.parkingBrake = playerBus.parkingBrake;
        }

        PlayerInteriorLights?.SetDoorOpen(playerBus.doorsOpen || playerBus.rearDoorsOpen);

        if (playerBus.doorsOpen)
        {
            _hasProcessedStop = false;
            PrintTagged($"🚪 Front doors <b>OPEN</b>  |  🅿 Brake: <b>ON</b>", "system");
            // [ADD] Covers the door-closed-when-the-ramp-finished edge case
            // -- UpdateRampState already tries this the instant the ramp
            // hits Stowed, but if the door wasn't open at that exact moment
            // it no-ops; this catches it as soon as the door opens again.
            ReleaseDeferredBoarding();
            if (AtStop) ProcessStopDoorOpen();
            else        PrintTagged("  (Not at a designated stop)", "info");
        }
        else
        {
            PrintTagged($"🚪 Front doors <b>CLOSED</b>  |  🅿 Brake: {(playerBus.parkingBrake ? "<b>ON</b> (rear still open)" : "<b>OFF</b>")}", "system");
        }
    }

    // ── Rear door toggle (Shift+1) ─────────────────────────────────────────────
    private void HandleRearDoorToggle()
    {
        if (playerBus == null) return;

        var audio = playerBus.audioEngine;
        if (audio != null && !audio.batteryOn)
        {
            PrintTagged("⚡ No power — doors won't move with the battery off.", "error");
            return;
        }
        if (audio != null && audio.engineState == BusAudioEngine.EngineRunState.Cranking)
        {
            PrintTagged("🔑 Wait for the starter to finish cranking.", "warn");
            return;
        }

        playerBus.rearDoorsOpen = !playerBus.rearDoorsOpen;
        playerBus.parkingBrake  = playerBus.doorsOpen || playerBus.rearDoorsOpen;

        // [FIX] Same reason as HandleDoorToggle above -- mirror onto the
        // sibling NPCBusController, the only script actually syncing
        // parkingBrake into audioEngine while the player is driving.
        var rearDoorNpc = playerBus.GetComponent<NPCBusController>();
        if (rearDoorNpc != null)
        {
            rearDoorNpc.rearDoorsOpen = playerBus.rearDoorsOpen;
            rearDoorNpc.parkingBrake  = playerBus.parkingBrake;
        }

        PlayerInteriorLights?.SetDoorOpen(playerBus.doorsOpen || playerBus.rearDoorsOpen);

        if (playerBus.rearDoorsOpen)
        {
            PrintTagged($"🚪 Rear doors <b>OPEN</b>  |  🅿 Brake: <b>ON</b>", "system");
            // [ADD] Back door can now relieve a pending stop-request/
            // alighting flag on its own -- previously only the front door's
            // ProcessStopDoorOpen touched _stopRequested/_alightCount at
            // all, so opening JUST the back door (a real thing a driver
            // does to let people off without also opening front for
            // boarding) never actually cleared the "stop requested" state.
            ProcessRearDoorAlighting();
        }
        else
        {
            PrintTagged($"🚪 Rear doors <b>CLOSED</b>  |  🅿 Brake: {(playerBus.parkingBrake ? "<b>ON</b> (front still open)" : "<b>OFF</b>")}", "system");
        }
    }

    /// <summary>Alighting-only counterpart to ProcessStopDoorOpen -- the
    /// back door lets people OFF, it doesn't board anyone, so this only
    /// touches _stopRequested/_alightCount/_onboardPax, never
    /// _predictedBoardingPax. Deliberately does NOT share
    /// ProcessStopDoorOpen's _hasProcessedStop/_boardingInProgress gates --
    /// a driver can open just the back door on its own, independent of
    /// whatever state the front door's boarding flow is in. Safe to call
    /// even if the front door later processes the stop too: the alighting
    /// block there is already guarded by `_stopRequested && _alightCount > 0`,
    /// which this clears, so it naturally no-ops the second time instead of
    /// double-counting.</summary>
    private void ProcessRearDoorAlighting()
    {
        if (!AtStop) return;
        if (!_stopRequested || _alightCount <= 0) return;

        string stopName = NextStopName;
        int off = Mathf.Min(_alightCount, _onboardPax);
        _onboardPax    -= off;
        _stopRequested  = false;
        PointsManager.Instance.RegisterAlighting(off);
        for (int i = 0; i < off && _passengerDestinations.Count > 0; i++)
            _passengerDestinations.RemoveAt(0);
        _alightCount = 0;

        if (off > 0)
        {
            _shiftScore += off;
            PrintTagged($"🧍 {stopName}: {off} off via rear door (+{off} pts)", "info");
        }
    }

    // ── Console command router ────────────────────────────────────────────────
    public void HandleConsoleCommand(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;
        string[] parts = input.Trim().Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
        string   cmd   = parts[0].ToLower();

        switch (cmd)
        {
            case "join":
                if (parts.Length < 2) { PrintTagged("Usage: join &lt;route&gt; [&lt;variant&gt;]", "warn"); return; }
                string joinArg = parts.Length >= 3 ? $"{parts[1]} {parts[2]}" : parts[1];
                ParseRouteAndVariant(joinArg, out string jr, out string jv);
                JoinRoute(jr, jv);
                break;

            case "arrived":   CmdArrived(); break;
            case "depart": case "departing": case "go": CmdDepart(); break;
            case "relief":
                if (parts.Length >= 2 && IsReliefSwapPending)
                {
                    string ans = parts[1].ToLower();
                    if (ans == "yes" || ans == "y") ConfirmReliefSwap(true);
                    else if (ans == "no" || ans == "n") ConfirmReliefSwap(false);
                    else PrintTagged("Usage: relief yes | relief no", "warn");
                }
                else
                {
                    RequestRelief();
                }
                break;
            case "status":    PrintStatus(); break;
            case "pax":       CmdPax(); break;
            case "nextdep":   CmdNextDepartures(); break;
            case "breakdown": CmdBreakdown(); break;
            case "recovered": CmdRecovered(); break;
            case "door":      HandleDoorToggle(); break;
            case "rdoor":     HandleRearDoorToggle(); break;
            case "kneel":     HandleKneelToggle(); break;
            case "unstuck": case "fix": case "flip": HandleUnstuck(); break;
            case "ignition":  HandleIgnitionToggle(); break;
            case "quickstart": case "qs": HandleQuickStart(); break;
            case "wedge":     HandleWedgeDoor(); break;
            case "ramp":      HandleRampToggle(); break;
            case "continue":  ContinueAssignedChain(); break;
            case "board":
                ShiftBoardMenu.Instance?.OpenBoard();
                break;

            case "log": case "schedule": case "route":
                if (parts.Length < 2) { PrintTagged("Usage: log &lt;route&gt;", "warn"); return; }
                if (BusScheduler.Instance != null)
                {
                    BusScheduler.Instance.PrintDetailedRouteLog(parts[1]);
                    PrintTagged($"Route {parts[1].ToUpper()} manifest dumped to Unity console.", "info");
                }
                else PrintTagged("System Error: BusScheduler unavailable.", "error");
                break;

            default:
                {
                    var bm = System.Text.RegularExpressions.Regex.Match(cmd, @"^([a-z0-9_\-]+)/b(\d+)$");
                    if (bm.Success) { CmdBay(bm.Groups[1].Value, int.Parse(bm.Groups[2].Value)); break; }
                }
                PrintTagged($"Unknown command: <b>{cmd}</b>. Type <b>help</b> or <b>?help</b> for the full command list.", "warn");
                break;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CMD: arrived
    // ═════════════════════════════════════════════════════════════════════════
    private void CmdArrived()
    {
        switch (_shiftState)
        {
            case PlayerShiftState.DeadrunToStart:
            {
                if (!IsNearTargetTerminal(out float dist))
                {
                    PrintTagged($"⚠ Not close enough to <b>{_targetTerminal?.StopName ?? "terminal"}</b> " +
                                $"({dist:F0} m away, need ≤{terminalArrivalRadius:F0} m).", "warn");
                    return;
                }
                SetShiftState(PlayerShiftState.WaitingToDepart); // [FIX D2]
                string depTime = _activeSlot != null
                    ? BusScheduler.MinutesToTimeString(_activeSlot.scheduledDeparture) : "--:--";
                PrintTagged($"✔ Reported at <b>{_targetTerminal.StopName}</b>.", "success");
                TryAutoClaimBay();
                PrintTagged($"  Departure: <b>{depTime}</b> ({(_isOutbound ? "A→Z" : "Z→A")}) — type <b>depart</b> when time comes.", "info");
                if (BusScheduler.Instance != null)
                    PrintTagged($"  Game time now: {BusScheduler.Instance.GameTimeString}", "info");
                break;
            }

            case PlayerShiftState.InService:
            case PlayerShiftState.ReliefPending:
            {
                if (!IsNearTargetTerminal(out float dist))
                {
                    PrintTagged($"⚠ Not close enough to <b>{_targetTerminal?.StopName ?? "terminal"}</b> " +
                                $"({dist:F0} m away, need ≤{terminalArrivalRadius:F0} m).", "warn");
                    return;
                }
                OnPlayerArrivedAtTerminal();
                break;
            }

            case PlayerShiftState.Breakdown:
                PrintTagged("⚠ Bus is broken down. Type <b>recovered</b> to resume.", "warn");
                break;

            case PlayerShiftState.WaitingToDepart:
                PrintTagged("Already confirmed at terminal — type <b>depart</b> when ready.", "info");
                break;

            case PlayerShiftState.ArrivedAtTerminal:
                PrintTagged("Arrival already logged — waiting for slot / departure time.", "info");
                break;

            case PlayerShiftState.OffDuty:
                PrintTagged("Not on duty. Type <b>join &lt;route&gt;</b> first.", "warn");
                break;

            default:
                PrintTagged($"'arrived' not valid in state ({_shiftState}).", "warn");
                break;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CMD: depart
    // ═════════════════════════════════════════════════════════════════════════
    private void CmdDepart()
    {
        if (_isBreakdown)
        {
            PrintTagged("⚠ Bus is broken down. Type <b>recovered</b> first.", "warn");
            return;
        }

        switch (_shiftState)
        {
            case PlayerShiftState.WaitingToDepart:
            {
                var scheduler = BusScheduler.Instance;
                if (scheduler != null && _activeSlot != null)
                {
                    float lateness = scheduler.GameTimeMinutes - _activeSlot.scheduledDeparture;
                    if (lateness < -1f)
                    {
                        string depTime = BusScheduler.MinutesToTimeString(_activeSlot.scheduledDeparture);
                        PrintTagged($"⚠ Departure not yet due — scheduled {depTime}, now {scheduler.GameTimeString}.", "warn");
                        PrintTagged("  Type <b>depart</b> again to override.", "info");
                        if (!_departOverridePending) { _departOverridePending = true; return; }
                    }
                }
                _departOverridePending = false;
                BeginLeg();
                break;
            }

            case PlayerShiftState.ArrivedAtTerminal:
            {
                if (_activeSlot == null)
                {
                    PrintTagged("⏳ No slot assigned yet — stand by.", "warn");
                    return;
                }

                var scheduler = BusScheduler.Instance;
                float now     = scheduler != null ? scheduler.GameTimeMinutes : 0f;
                float lateness = now - _activeSlot.scheduledDeparture;

                if (lateness >= -1f)
                {
                    _shiftState            = PlayerShiftState.WaitingToDepart;
                    _departOverridePending = false;
                    BeginLeg();
                }
                else
                {
                    SetShiftState(PlayerShiftState.WaitingToDepart); // [FIX D2]
                    string depTime = BusScheduler.MinutesToTimeString(_activeSlot.scheduledDeparture);
                    string dir     = _isOutbound ? "A→Z" : "Z→A";
                    PrintTagged($"🟡 Slot confirmed: <b>{depTime}</b> ({dir}).", "info");
                    PrintTagged($"  Departure due in {Mathf.CeilToInt(-lateness)} min — type <b>depart</b> again when ready, or now to override.", "info");
                    _departOverridePending = true;
                }
                break;
            }

            case PlayerShiftState.InService:
                PrintTagged("Already in service — type <b>arrived</b> when you reach the terminal.", "info");
                break;

            case PlayerShiftState.OffDuty:
                PrintTagged("Not on duty.", "warn");
                break;

            default:
                PrintTagged($"'depart' not valid in state ({_shiftState}).", "warn");
                break;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CMD: breakdown / recovered
    // ═════════════════════════════════════════════════════════════════════════
    private void CmdBreakdown()
    {
        if (!IsOnDuty)
        {
            PrintTagged("Not on duty.", "warn");
            return;
        }
        if (_isBreakdown)
        {
            PrintTagged("Breakdown already reported. Type <b>recovered</b> when fixed.", "warn");
            return;
        }

        // [CHANGE] Was a local-only flag with no BreakdownType at all -- now
        // forces a real random FULL-severity breakdown through
        // BusBreakdownSystem, so this stays useful as a "force one right
        // now" test command while actually being consistent with the real
        // system (shows in the ongoing-breakdowns panel, drives door/audio
        // effects the same way an organic roll would).
        //
        // [UPDATE] Final breakdown list -- HVILFault/InverterFault folded
        // into BatteryTrip (which now branches by drivetrain instead of
        // being its own separate type), TransmissionFailure added.
        // BatteryTrip is only a genuine full-stop on PURE electric buses --
        // on a hybrid it's just the electric-assist whine cutting in and
        // out, no shutdown at all -- so it's only offered here when the
        // possessed bus is actually pure electric, otherwise "force a full
        // breakdown" would silently do nothing on a hybrid.
        var vehicle = playerBus != null ? playerBus.GetComponent<BusVehicleSystem>() : null;
        bool isPureElectric = vehicle != null && vehicle.fuelType == BusVehicleSystem.FuelSystemType.Electric;

        var fullTypes = new List<BreakdownType> { BreakdownType.AirBrakeFailure };
        if (isPureElectric)
            fullTypes.Add(BreakdownType.BatteryTrip);
        else
        {
            fullTypes.Add(BreakdownType.EngineFailure);
        }
        var type = fullTypes[UnityEngine.Random.Range(0, fullTypes.Count)];
        // [FIX] Was TriggerBreakdown(PlayerBusID, ...) -- PlayerBusID is the
        // abstract BusScheduler.PLAYER_BUS_ID sentinel (-2), not this bus's
        // real physical busID. BusBreakdownSystem's whole vehicle-effect
        // side (RequiresFullShutdown, ApplyBreakdownToVehicle, the ongoing-
        // breakdowns panel) all key off the real busID -- registering under
        // -2 meant this command's own comment ("consistent with the real
        // system... shows in the ongoing-breakdowns panel, drives door/audio
        // effects the same way an organic roll would") was never actually
        // true. The console messages printed and _isBreakdown flipped, but
        // the vehicle itself never actually locked up and the panel never
        // showed it -- this test command silently didn't do what it claimed.
        BusBreakdownSystem.Instance?.TriggerBreakdown(GetBusID(playerBus), type);

        _isBreakdown        = true;
        _breakdownStartTime = BusScheduler.Instance?.GameTimeMinutes ?? 0f;

        if (playerBus != null)
        {
            playerBus.doorsOpen    = true;
            playerBus.parkingBrake = true;
            PlayerInteriorLights?.SetDoorOpen(true);
        }

        SetShiftState(PlayerShiftState.Breakdown); // [FIX D2]

        PrintTagged($"🔴 BREAKDOWN reported at {BusScheduler.Instance?.GameTimeString ?? "--:--"} — <b>{type}</b>.", "error");
        PrintTagged($"  Route {ActiveRouteLabel} {(_isOutbound ? "A→Z" : "Z→A")} | Segment {_segmentsCompleted + 1}", "error");
        PrintTagged("  Doors open, brake applied. Type <b>recovered</b> when service can resume.", "error");

        _shiftScore -= 20f;

        BusScheduler.Instance?.InitiateReliefSearch(GetNearestTerminalCode(), _activeRoute);

        DriverConsole.Instance?.RefreshStopHUD();
    }

    private void CmdRecovered()
    {
        if (!_isBreakdown)
        {
            PrintTagged("No breakdown active.", "warn");
            return;
        }

        float downTime = (BusScheduler.Instance?.GameTimeMinutes ?? 0f) - _breakdownStartTime;
        _isBreakdown   = false;
        _shiftState    = PlayerShiftState.InService;

        // [ADD] Manual recovery clears the real system too now, not just the
        // local flag -- otherwise this command would desync from
        // BusBreakdownSystem and leave a phantom entry in the ongoing-
        // breakdowns panel.
        // [FIX] Same PlayerBusID-sentinel-vs-real-busID bug as CmdBreakdown
        // above -- must match whatever busID it was actually registered
        // under.
        BusBreakdownSystem.Instance?.ClearAllBreakdowns(GetBusID(playerBus));

        if (playerBus != null)
        {
            playerBus.doorsOpen    = false;
            playerBus.parkingBrake = false;
            PlayerInteriorLights?.SetDoorOpen(playerBus.rearDoorsOpen);
        }

        PrintTagged($"✔ Breakdown cleared after {downTime:F0} min down.", "success");
        PrintTagged($"  Resuming service to <b>{_targetTerminal?.StopName ?? "terminal"}</b>.", "success");

        DriverConsole.Instance?.RefreshStopHUD();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CMD: pax
    // ═════════════════════════════════════════════════════════════════════════
    private void CmdPax()
    {
        PrintTagged($"🧍 Passenger Report — Route <b>{ActiveRouteLabel}</b>", "info");
        PrintTagged($"  Onboard:       <b>{_onboardPax}</b>", "info");

        if (_stopRequested)
            PrintTagged($"  Stop requested: <b>{_alightCount}</b> want to alight at <b>{NextStopName}</b>", "info");
        else
            PrintTagged("  No stop requested at next stop.", "info");

        if (_predictedBoardingPax > 0)
            PrintTagged($"  Predicted boarding at <b>{NextStopName}</b>: ~{_predictedBoardingPax}", "info");

        if (_passengerDestinations.Count > 0)
        {
            int shortTrip = 0, midTrip = 0, longTrip = 0;
            foreach (int d in _passengerDestinations)
            {
                if      (d <= 2) shortTrip++;
                else if (d <= 5) midTrip++;
                else             longTrip++;
            }
            PrintTagged($"  Journey length: short {shortTrip} · mid {midTrip} · long {longTrip}", "info");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CMD: nextdep
    // ═════════════════════════════════════════════════════════════════════════
    private void CmdNextDepartures()
    {
        if (string.IsNullOrEmpty(_activeRoute))
        {
            PrintTagged("Not on a route.", "warn");
            return;
        }
        var scheduler = BusScheduler.Instance;
        if (scheduler == null) { PrintTagged("Scheduler unavailable.", "error"); return; }

        PrintTagged($"🕐 Next departures — Route <b>{ActiveRouteLabel}</b>", "info");

        int shown = 0;
        foreach (var slot in scheduler.AllSlots)
        {
            if (slot.routeNumber         != _activeRoute)              continue;
            if (slot.scheduledDeparture  <= scheduler.GameTimeMinutes) continue;
            if (shown                    >= 6)                         break;

            string dir   = slot.isOutbound ? "A→Z" : "Z→A";
            string t     = BusScheduler.MinutesToTimeString(slot.scheduledDeparture);
            string who   = slot.state == SlotState.Unassigned     ? "<color=#aaffaa>free</color>"
                         : slot.state == SlotState.AssignedPlayer ? "<b>YOU</b>"
                         : $"Bus#{slot.assignedBusID}";
            string v     = string.IsNullOrEmpty(slot.variantLetter) ? "" : $" [{slot.variantLetter}]";
            PrintTagged($"  {t}  {dir}{v}  [{who}]", "info");
            shown++;
        }
        if (shown == 0)
            PrintTagged("  No further departures in schedule.", "info");
    }

    // ── Begin a leg ───────────────────────────────────────────────────────────
    private void BeginLeg()
    {
        ReleasePlayerBay(); // leaving the terminal -- free the bay
        SetShiftState(PlayerShiftState.InService); // [FIX D2]
        _isBreakdown = false;

        var scheduler = BusScheduler.Instance;
        if (scheduler != null)
        {
            float now      = scheduler.GameTimeMinutes;
            float lateness = _activeSlot != null ? now - _activeSlot.scheduledDeparture : 0f;

            // [FIX 07-30] Pass _activeSlot directly — this method already
            // has the correct current slot right here; letting
            // RecordActualDeparture re-look it up via _slotByBus[busID]
            // was the gap that let the two drift apart. See
            // RecordActualDeparture's own comment for the full mechanism.
            scheduler.RecordActualDeparture(PlayerBusID, now, _activeSlot);

            if      (Mathf.Abs(lateness) <= 1f)         { _shiftScore += 10f; _onTimeDepartures++;  }
            else if (lateness >  0f && lateness <= 5f)  { _shiftScore +=  5f; _lateDepartures++;    }
            else if (lateness >  5f)                    { _shiftScore -=  5f; _lateDepartures++; _totalLatenessAccum += lateness; }
            else if (lateness < -1f)                    { _shiftScore +=  3f; _earlyDepartures++;   }
        }

        _shiftScore += 3f;

        SetTargetTerminal(_isOutbound, forStart: false);
        _announcedStops.Clear();
        _levelGateReported.Clear();
        _nextStopIndex = -1;

        var legStops = GetActiveStops(_isOutbound);
        if (legStops != null && legStops.Count > 0)
        {
            var positions = new Vector3[legStops.Count];
            var terminals = new bool[legStops.Count];
            for (int i = 0; i < legStops.Count; i++)
            {
                positions[i] = legStops[i].GetWorldPosition();
                // [FIX] Missing the "|| i == legStops.Count - 1" fallback
                // NPCBusController's own pax-plan roll already has (see its
                // BuildPaxPlan-equivalent) -- without it, the player's LAST
                // stop on a leg only forced a full clear-out/no-boarding if
                // that exact stop happened to also be separately flagged
                // isTerminal in the stop data. Every leg's last stop is a
                // terminal for pax-planning purposes regardless of that flag.
                terminals[i] = legStops[i].isTerminal || i == legStops.Count - 1;
            }
            float nowMinutes = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : 0f;
            _playerPaxPlan = PaxRollPlanner.RollTrip(positions, terminals, nowMinutes);
        }
        else
        {
            _playerPaxPlan = null;
        }

        string dir  = _isOutbound ? "A→Z" : "Z→A";
        string dest = _targetTerminal != null ? _targetTerminal.StopName : "far terminal";
        PrintTagged($"🟢 Departed — Route <b>{ActiveRouteLabel}</b> {dir}. Head to <b>{dest}</b>.", "success");
        PrintTagged("  Type <b>arrived</b> when you reach the terminal.", "info");

        DriverConsole.Instance?.RefreshStopHUD();
    }

    // ── Slot ready callback ───────────────────────────────────────────────────
    private void HandleSlotReady(TimetableSlot slot)
    {
        if (slot.state         != SlotState.AssignedPlayer) return;
        if (slot.assignedBusID != PlayerBusID)               return;

        var scheduler = BusScheduler.Instance;

        if (_shiftState == PlayerShiftState.WaitingToDepart)
        {
            PrintTagged($"🟡 Departure time reached ({scheduler?.GameTimeString ?? "--:--"}). " +
                        "Type <b>depart</b> to go.", "info");
        }
        else if (_shiftState == PlayerShiftState.ArrivedAtTerminal)
        {
            SetShiftState(PlayerShiftState.WaitingToDepart); // [FIX D2]
            string depTime = BusScheduler.MinutesToTimeString(slot.scheduledDeparture);
            string dir     = _isOutbound ? "A→Z" : "Z→A";
            float  now     = scheduler?.GameTimeMinutes ?? 0f;

            if (now > slot.scheduledDeparture + 1f)
                PrintTagged($"🟡 Slot overdue ({depTime} {dir}) — type <b>depart</b> to go (running late).", "warn");
            else
                PrintTagged($"🟡 Departure time: {depTime} ({dir}) — type <b>depart</b> to begin.", "info");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Join / Relief / End shift
    // ═════════════════════════════════════════════════════════════════════════
    public void JoinRoute(string routeNumber, string variantLetter = "")
    {
        if (_shiftState != PlayerShiftState.OffDuty)
        {
            PrintTagged($"Already on duty (Route {ActiveRouteLabel}). Request relief first.", "warn");
            return;
        }

        var scheduler = BusScheduler.Instance;
        if (scheduler == null) { PrintTagged("BusScheduler not found.", "error"); return; }

        var route = scheduler.GetRouteData(routeNumber);
        if (route == null) { PrintTagged($"Route <b>{routeNumber}</b> not found.", "warn"); return; }

        RouteVariantData resolvedVariant = null;
        string resolvedVariantLetter    = "";

        if (!string.IsNullOrEmpty(variantLetter))
        {
            resolvedVariant = route.GetVariant(variantLetter);
            if (resolvedVariant == null)
            {
                PrintTagged($"Route <b>{routeNumber}</b> has no variant '<b>{variantLetter}</b>'.", "warn");
                if (route.variants != null && route.variants.Count > 0)
                {
                    var letters = new System.Text.StringBuilder("  Available variants: ");
                    foreach (var v in route.variants) letters.Append(v.variantLetter + " ");
                    PrintTagged(letters.ToString(), "info");
                }
                else
                    PrintTagged("  No variants configured on this route.", "info");
                return;
            }
            resolvedVariantLetter = variantLetter.ToUpper();
        }

        var slot = scheduler.ReservePlayerSlot(routeNumber, true);
        if (slot == null) { PrintTagged($"No available outbound slots on Route {routeNumber}.", "warn"); return; }

        _activeSlot          = slot;
        _activeRoute         = routeNumber;
        _activeVariantLetter = resolvedVariantLetter;
        _activeVariant       = resolvedVariant;
        _isOutbound          = true;
        _segmentsCompleted   = 0;
        _lastCompletedLegOutbound = null;
        _reliefRequested     = false;
        _departOverridePending = false;
        _isBreakdown         = false;

        _shiftScore         = 0f;
        _onTimeDepartures   = 0;
        _lateDepartures     = 0;
        _earlyDepartures    = 0;
        _totalLatenessAccum = 0f;

        SetShiftState(PlayerShiftState.DeadrunToStart); // [FIX D2]

        ResolveTerminals(route);
        SetTargetTerminal(true, forStart: true);

        string depTime  = BusScheduler.MinutesToTimeString(slot.scheduledDeparture);
        string termName = _targetTerminal != null
            ? _targetTerminal.StopName
            : $"Terminal A ({route.terminalACode})";
        string label    = string.IsNullOrEmpty(resolvedVariantLetter)
            ? routeNumber
            : $"{routeNumber} (Variant {resolvedVariantLetter})";

        PrintTagged($"✔ Route <b>{label}</b> confirmed.", "success");
        if (!string.IsNullOrEmpty(resolvedVariantLetter) && resolvedVariant != null)
        {
            string stops = resolvedVariant.resolvedOutboundStops?.Count.ToString() ?? "?";
            PrintTagged($"  Variant <b>{resolvedVariantLetter}</b>: {stops} outbound stops, different terminal path.", "info");
        }
        PrintTagged($"  Dead-run to: <b>{termName}</b>", "info");
        PrintTagged($"  First departure: <b>{depTime}</b> (A→Z)", "info");
        PrintTagged($"  Game time now: {scheduler.GameTimeString}", "info");
        PrintTagged("  Type <b>arrived</b> once you reach the terminal.", "info");

        DriverConsole.Instance?.RefreshStopHUD();
    }

    /// <summary>Join a route using a SPECIFIC slot picked via a UI slot-picker.</summary>
    public void JoinRouteWithSlot(string routeNumber, string variantLetter, TimetableSlot chosenSlot, bool fromBoard = false)
    {
        if (_shiftState != PlayerShiftState.OffDuty)
        {
            PrintTagged($"Already on duty (Route {ActiveRouteLabel}). Request relief first.", "warn");
            return;
        }

        var scheduler = BusScheduler.Instance;
        if (scheduler == null) { PrintTagged("BusScheduler not found.", "error"); return; }

        var route = scheduler.GetRouteData(routeNumber);
        if (route == null) { PrintTagged($"Route <b>{routeNumber}</b> not found.", "warn"); return; }

        RouteVariantData resolvedVariant = null;
        string resolvedVariantLetter = "";
        if (!string.IsNullOrEmpty(variantLetter))
        {
            resolvedVariant = route.GetVariant(variantLetter);
            if (resolvedVariant == null)
            {
                PrintTagged($"Route <b>{routeNumber}</b> has no variant '<b>{variantLetter}</b>'.", "warn");
                return;
            }
            resolvedVariantLetter = variantLetter.ToUpper();
        }

        // ROOT-CAUSE FIX: ReservePlayerSlotSpecific ONLY works on a slot
        // whose state is still Unassigned (confirmed directly in
        // BusScheduler.cs: "if (target.state != SlotState.Unassigned)
        // return null;"). It does NOT take over an NPC-owned slot itself —
        // that's TransferSlotToPlayer's job, and TransferSlotToPlayer
        // already sets the slot's state to AssignedPlayer as part of doing
        // so. Calling both in sequence on the same slot object (transfer,
        // then try to also reserve) is therefore self-defeating: the
        // reserve step sees a state that's no longer Unassigned and always
        // rejects it. This method now ONLY handles the genuinely-free case.
        // ClaimRouteAndBus/ContinueOnRoute branch to JoinTransferredSlot
        // below instead when the slot is currently NPC-owned.
        var slot = scheduler.ReservePlayerSlotSpecific(chosenSlot);
        if (slot == null) { PrintTagged($"That slot is no longer available — pick another.", "warn"); return; }

        ApplyJoinedSlot(routeNumber, resolvedVariantLetter, resolvedVariant, route, slot, fromBoard);
    }

    /// <summary>Sibling of JoinRouteWithSlot for a slot that's ALREADY been
    /// transferred from an NPC via BusScheduler.TransferSlotToPlayer — the
    /// caller (ShiftRunner.ClaimRouteAndBus/ContinueOnRoute) does that
    /// transfer itself and passes the resulting slot here. Skips
    /// ReservePlayerSlotSpecific entirely (it would always reject this slot
    /// — see the fix note on JoinRouteWithSlot above) since the transfer
    /// already did the reservation; everything else is identical.</summary>
    public void JoinTransferredSlot(string routeNumber, string variantLetter, TimetableSlot transferredSlot, bool fromBoard = false)
    {
        if (_shiftState != PlayerShiftState.OffDuty)
        {
            PrintTagged($"Already on duty (Route {ActiveRouteLabel}). Request relief first.", "warn");
            return;
        }
        if (transferredSlot == null) { PrintTagged("That slot is no longer available — pick another.", "warn"); return; }

        var scheduler = BusScheduler.Instance;
        if (scheduler == null) { PrintTagged("BusScheduler not found.", "error"); return; }

        var route = scheduler.GetRouteData(routeNumber);
        if (route == null) { PrintTagged($"Route <b>{routeNumber}</b> not found.", "warn"); return; }

        RouteVariantData resolvedVariant = null;
        string resolvedVariantLetter = "";
        if (!string.IsNullOrEmpty(variantLetter))
        {
            resolvedVariant = route.GetVariant(variantLetter);
            if (resolvedVariant == null)
            {
                PrintTagged($"Route <b>{routeNumber}</b> has no variant '<b>{variantLetter}</b>'.", "warn");
                return;
            }
            resolvedVariantLetter = variantLetter.ToUpper();
        }

        ApplyJoinedSlot(routeNumber, resolvedVariantLetter, resolvedVariant, route, transferredSlot, fromBoard);
    }

    /// <summary>Shared tail end of both join paths above — all the actual
    /// player-state setup, identical regardless of whether the slot came
    /// from ReservePlayerSlotSpecific (free) or TransferSlotToPlayer
    /// (NPC-owned).</summary>
    private void ApplyJoinedSlot(string routeNumber, string resolvedVariantLetter, RouteVariantData resolvedVariant,
                                  BusRouteData route, TimetableSlot slot, bool fromBoard)
    {
        _activeSlot          = slot;
        _activeRoute         = routeNumber;
        _activeVariantLetter = resolvedVariantLetter;
        _activeVariant       = resolvedVariant;
        _isOutbound          = slot.isOutbound;
        _segmentsCompleted   = 0;
        _lastCompletedLegOutbound = null;
        _reliefRequested     = false;
        _departOverridePending = false;
        _isBreakdown         = false;

        _shiftScore         = 0f;
        _onTimeDepartures   = 0;
        _lateDepartures     = 0;
        _earlyDepartures    = 0;
        _totalLatenessAccum = 0f;

        SetShiftState(PlayerShiftState.DeadrunToStart); // [FIX D2]

        ResolveTerminals(route);
        SetTargetTerminal(_isOutbound, forStart: true);

        string depTime  = BusScheduler.MinutesToTimeString(slot.scheduledDeparture);
        string termName = _targetTerminal != null ? _targetTerminal.StopName : "Terminal";
        string label    = string.IsNullOrEmpty(resolvedVariantLetter) ? routeNumber : $"{routeNumber}{resolvedVariantLetter}";

        PrintTagged($"✔ Route <b>{label}</b> confirmed — {slot.DirectionLabel} @ {depTime}.", fromBoard ? "board" : "success");
        PrintTagged($"  Dead-run to: <b>{termName}</b>. Type <b>arrived</b> once there.", fromBoard ? "board" : "info");

        StopHUD.Instance?.Refresh();
        DriverConsole.Instance?.RefreshStopHUD();
    }

    private IEnumerator ReturnToOffDuty()
    {
        yield return new WaitForSeconds(3f);
        // [FIX] If this bus was originally grabbed via Free Drive (no route,
        // BSM/Custom) rather than adopted mid-route, finishing this shift
        // should return the player to Free Drive on the SAME bus -- no
        // teleport, no release, they never gave the bus up in the first
        // place, they just finished a route on it.
        if (_freeDriveOrigin && playerBus != null)
        {
            SetShiftState(PlayerShiftState.FreeDrive); // [FIX D2]
            PrintTagged("Shift complete — back to Free Drive.", "system");
        }
        else
        {
            TeleportBusHomeToDepot(playerBus);
            SetShiftState(PlayerShiftState.OffDuty); // [FIX D2]
            playerBus = null;
            PrintTagged("Ready. Type <b>join &lt;route&gt;</b> to start a new shift.", "system");
            // [ADD] Genuine full release -- no bus, no focus target, camera
            // has nothing to follow. Bring the Main Menu up automatically
            // rather than leaving the player staring at an empty scene.
            // Deliberately NOT in the Free Drive branch above -- that one
            // keeps the bus, so there's nothing broken-looking to cover for.
            MainMenu.Instance?.Open();
        }
        DriverConsole.Instance?.RefreshStopHUD();
    }

    private void EndShift()
    {
        ReleasePlayerBay();
        if (BusScheduler.Instance != null)
            BusScheduler.Instance.ReleasePlayerSlot(PlayerBusID);

        // [FIX] Same Free Drive check as ReturnToOffDuty above.
        if (_freeDriveOrigin && playerBus != null)
        {
            _shiftState          = PlayerShiftState.FreeDrive;
            _activeRoute         = "";
            _activeVariant       = null;
            _activeVariantLetter = "";
            _activeSlot          = null;
            _targetTerminal      = null;
            // playerBus stays set, no teleport -- still their bus.
        }
        else
        {
            // [FIX] playerBus was never cleared here before -- only the
            // shift STATE reset. Also teleports the abandoned bus straight
            // back to an open depot spot (same convention buses already
            // use spawning in) instead of leaving it wherever the shift
            // happened to end.
            TeleportBusHomeToDepot(playerBus);
            _shiftState          = PlayerShiftState.OffDuty;
            _activeRoute         = "";
            _activeVariant       = null;
            _activeVariantLetter = "";
            _activeSlot          = null;
            _targetTerminal      = null;
            playerBus            = null;
            // [ADD] Same trigger as ReturnToOffDuty above -- bus is gone,
            // no camera target, bring up the Main Menu automatically.
            MainMenu.Instance?.Open();
        }
        DriverConsole.Instance?.RefreshStopHUD();
    }

    // [ADD] Shared with EndShift()/ReturnToOffDuty() -- both are genuine
    // "player no longer has this bus" endpoints. This is the actual fix for
    // the "NPC never comes back to life" bug: releasing a bus is NOT just a
    // position teleport. BusSelectMenu.ApplyFleetPossession is what actually
    // disabled this bus's NPCBusController/BusAIBrain/colliders and set its
    // tag/CityManager refs at possession time, and only BusSelectMenu knows
    // how to correctly undo every one of those (that state is private to
    // it) -- so this routes through BusSelectMenu.ReleasePossessedBusToNPC()
    // first, which does the FULL release (resync position, re-enable
    // NPCBusController/BusAIBrain/colliders, restore tag, clear CityManager,
    // AND teleport home). The raw teleport-only fallback below only runs if
    // BusSelectMenu.Instance is somehow unavailable, so a bus never gets
    // silently left disabled just because this path used to skip that step.
    private void TeleportBusHomeToDepot(BusSimulationController bus)
    {
        if (bus == null) return;

        if (BusSelectMenu.Instance != null)
        {
            BusSelectMenu.Instance.ReleasePossessedBusToNPC();
            return;
        }

        // Fallback -- position-only, same as before this fix. Every real
        // possession goes through BusSelectMenu first (see its own header
        // comment), so this should only ever fire if that instance is
        // missing entirely.
        var npc = bus.GetComponentInParent<NPCBusController>() ?? bus.transform.root.GetComponentInChildren<NPCBusController>();
        if (npc == null || npc.homeDepot == null) return;

        // [FIX] Same DepotManager-bypass + wrong-ID-space bug as the
        // TerminalDwell/HandleReplaceTierExpired fixes elsewhere -- was
        // GetFreeSpot() + hand-writing spot.occupied/occupiedByBusID with
        // npc.busID instead of the fleetNumber DepotManager's own
        // convention expects. This path only runs if BusSelectMenu.Instance
        // is ever null (see this method's header comment), so low odds of
        // firing, but routing it through ClaimSpotFor costs nothing and
        // keeps DepotManager the single source of truth regardless.
        DepotParkingSpot spot = null;
        if (DepotManager.Instance != null)
            DepotManager.Instance.ClaimSpotFor(npc.homeDepot, npc.fleetNumber, out spot);
        if (spot == null) return; // no free spot -- leave it where it was released

        bus.transform.root.SetPositionAndRotation(spot.position, Quaternion.Euler(spot.rotationEuler));
        var rb = npc.GetComponent<Rigidbody>();
        if (rb != null) rb.linearVelocity = Vector3.zero;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Shift summary
    // ═════════════════════════════════════════════════════════════════════════
    private void PrintShiftSummary()
    {
        string grade = _shiftScore >= 80f  ? "A"
                     : _shiftScore >= 60f  ? "B"
                     : _shiftScore >= 40f  ? "C"
                     : _shiftScore >= 20f  ? "D"
                     : "F";

        PrintTagged("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━", "system");
        PrintTagged($"📋 SHIFT SUMMARY — Route <b>{ActiveRouteLabel}</b>", "system");
        PrintTagged($"   Segments completed: <b>{_segmentsCompleted}</b>", "info");
        PrintTagged($"   Departures — on time: {_onTimeDepartures} · late: {_lateDepartures} · early: {_earlyDepartures}", "info");
        if (_lateDepartures > 0)
            PrintTagged($"   Average lateness: {(_totalLatenessAccum / Mathf.Max(1, _lateDepartures)):F1} min", "warn");
        PrintTagged($"   Score: <b>{_shiftScore:F0} pts</b>  —  Grade: <b>{grade}</b>", _shiftScore >= 60f ? "success" : "warn");
        PrintTagged("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━", "system");
    }

    // ── Status ────────────────────────────────────────────────────────────────
    public void PrintStatus()
    {
        var    scheduler = BusScheduler.Instance;
        string time      = scheduler?.GameTimeString ?? "--:--";

        if (_shiftState == PlayerShiftState.OffDuty)
        {
            PrintTagged($"[{time}] OFF DUTY — type <b>join &lt;route&gt;</b> to begin.", "system");
            return;
        }
        // [ADD] Free Drive needs its own status line -- falling through to
        // the in-service status print below would expect route/slot data
        // that doesn't exist while just freely driving with no shift.
        if (_shiftState == PlayerShiftState.FreeDrive)
        {
            PrintTagged($"[{time}] FREE DRIVE — Fleet #{_cachedFleetNumber}, no active route. Type <b>join &lt;route&gt;</b> to start a shift.", "system");
            return;
        }

        float  lateness = scheduler != null ? scheduler.GetLatenessMinutes(PlayerBusID) : 0f;
        string lateStr  = Mathf.Abs(lateness) < 0.5f
            ? "<color=#22ee80>on time</color>"
            : lateness > 0f
                ? $"<color=red>{lateness:F1} min late</color>"
                : $"<color=#22ee80>{Mathf.Abs(lateness):F1} min early</color>";

        string nextTerm = _targetTerminal != null ? _targetTerminal.StopName : "unknown";
        string nextDep  = _activeSlot != null
            ? BusScheduler.MinutesToTimeString(_activeSlot.scheduledDeparture) : "--:--";
        string dir      = _isOutbound ? "A→Z" : "Z→A";
        float  termDist = DistToTargetTerminal;
        string distStr  = termDist < float.MaxValue * 0.5f ? $"  ({termDist:F0} m)" : "";

        string variantTag = string.IsNullOrEmpty(_activeVariantLetter) ? "" : $" [Variant {_activeVariantLetter}]";

        PrintTagged($"[{time}] Route <b>{ActiveRouteLabel}</b>{variantTag} | {dir} | Seg {_segmentsCompleted} | {lateStr}", "system");
        PrintTagged($"  Target: {nextTerm}{distStr}  |  Slot: {nextDep}", "info");
        PrintTagged($"  Onboard: {_onboardPax} pax" + (_stopRequested ? $" | 🔔 {_alightCount} want off" : ""), "info");
        PrintTagged($"  Score: {_shiftScore:F0} pts  |  State: <b>{_shiftState}</b>" + (_isBreakdown ? " | 🔴 BREAKDOWN" : "") + (_reliefRequested ? " | RELIEF PENDING" : ""), "info");
        PrintTagged("  Commands: " + AvailableCommands(), "info");
    }

    private string AvailableCommands() =>
        _shiftState switch
        {
            PlayerShiftState.DeadrunToStart    => "arrived · relief · status",
            PlayerShiftState.WaitingToDepart   => "depart · pax · nextdep · relief · status",
            PlayerShiftState.InService         => "arrived · door · pax · breakdown · relief · status",
            PlayerShiftState.ArrivedAtTerminal => "depart · pax · nextdep · relief · status",
            PlayerShiftState.ReliefPending     => "arrived · status",
            PlayerShiftState.Breakdown         => "recovered · status",
            _                                  => "join &lt;route&gt; · status",
        };

    private bool IsNearTargetTerminal(out float dist)
    {
        dist = float.MaxValue;

        if (_targetTerminal == null || playerBus == null) return false;

        // Any stop of the target terminal counts; the nearest one is what gets reported.
        BusStopMarker nearest = _targetTerminal;
        float best = TerminalDistance(_targetTerminal);
        foreach (var m in _targetTerminalAlts)
        {
            float d = TerminalDistance(m);
            if (d < best) { best = d; nearest = m; }
        }
        if (best == float.MaxValue) return false;

        dist = best;
        if (best > terminalArrivalRadius) return false;

        if (nearest != _targetTerminal)
        {
            // Reporting at another stop of the same terminal: make it the target so messages name the right stop.
            _targetTerminalAlts.Remove(nearest);
            if (_targetTerminal != null) _targetTerminalAlts.Add(_targetTerminal);
            _targetTerminal = nearest;
        }
        return true;
    }

    private float TerminalDistance(BusStopMarker marker)
    {
        if (marker == null || marker.transform == null) return float.MaxValue;
        Vector3 pos = marker.transform.position;
        if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)) return float.MaxValue;
        float d = Vector3.Distance(playerBus.transform.position, pos);
        if (float.IsNaN(d) || float.IsInfinity(d))
        {
            PrintTagged($"⚠ Distance calc failed: player={playerBus.transform.position}, terminal={pos}", "error");
            return float.MaxValue;
        }
        return d;
    }

    // ── Stop list ─────────────────────────────────────────────────────────────
    private void InitStopList()
    {
        if (_stopsInitialized) return;
        _stopsInitialized = true;
        GameObject[] stopObjs = GameObject.FindGameObjectsWithTag("BusStop");
        _busStops = new Transform[stopObjs.Length];
        for (int i = 0; i < stopObjs.Length; i++)
            _busStops[i] = stopObjs[i].transform;
        Array.Sort(_busStops, (a, b) => a.position.z.CompareTo(b.position.z));
    }

    private void UpdateStopProximity()
    {
        var routeStops = GetActiveStops(_isOutbound);
        if (routeStops == null || routeStops.Count == 0 || playerBus == null) return;

        int searchStart = Mathf.Max(0, _nextStopIndex);

        // [FIX] Loop/close-stop skip-ahead bug: this used to scan ALL
        // remaining stops and pick whichever was globally closest within
        // stopDetectRadius (50m). On routes with a loop or stops close
        // together, a stop several positions ahead could sit within 50m of
        // the bus (e.g. across the street, or the inbound leg swinging back
        // near an earlier outbound stop) and get selected as _nextStopIndex
        // even though the bus hadn't actually reached the stops in between —
        // and since searchStart tracks _nextStopIndex forward only, those
        // skipped stops could never be revisited.
        //
        // Fix: walk stops strictly in route order and take the FIRST one
        // within pickupDetectRadius (door-range, not the old wide detect
        // radius) — never jump ahead to a closer-but-later stop.
        float best    = float.MaxValue;
        int   bestIdx = -1;

        for (int i = searchStart; i < routeStops.Count; i++)
        {
            Vector3 stopPos = routeStops[i].GetWorldPosition();
            // A stop more than stopSameLevelTolerance above/below the bus can't be picked up: a bus crossing a
            // bridge is not "at" the stop on the road under it (the Route 45 s0565 skip).
            // A stop closed by an active road event can't be served: the bus is on the detour. Skip it (and
            // say so once when the bus is close) instead of waiting for an arrival that will never happen.
            if (IsStopClosedForPlayer(routeStops[i]))
            {
                Vector3 cflat = playerBus.transform.position - stopPos; cflat.y = 0f;
                if (cflat.magnitude < 120f && _closedStopReported.Add(routeStops[i].stopCode))
                {
                    var closing = RoadEventRegistry.Instance?.GetClosingEvent(routeStops[i].stopCode, _activeRoute, _isOutbound);
                    PrintTagged($"⛔ Stop <b>{routeStops[i].stopName}</b> is closed{(closing != null ? " — " + closing.eventLabel : "")}. Not serving it.", "warn");
                }
                continue;
            }

            float dy = Mathf.Abs(playerBus.transform.position.y - stopPos.y);
            if (dy > stopSameLevelTolerance)
            {
                // Say so once per stop if a stop that is right beside the bus is being ignored for height, so a bad
                // height on some road shows up in the console instead of the stop silently never registering.
                Vector3 flat = playerBus.transform.position - stopPos; flat.y = 0f;
                if (flat.magnitude < pickupDetectRadius && _levelGateReported.Add(i))
                    PrintTagged($"⚠ Stop <b>{routeStops[i].stopName}</b> ({routeStops[i].stopCode}) ignored: it is {dy:F1} m above/below the bus (limit {stopSameLevelTolerance:F0} m).", "warn");
                continue;
            }
            float   d       = Vector3.Distance(playerBus.transform.position, stopPos);
            if (d < pickupDetectRadius) { best = d; bestIdx = i; break; }
        }

        if (bestIdx < 0)
        {
            // Nothing in range: if the stop we're pointing at is closed by a road event, move the pointer to the
            // next open stop so the HUD, announcements and distance readout follow the detour.
            while (_nextStopIndex >= 0 && _nextStopIndex < routeStops.Count - 1 && IsStopClosedForPlayer(routeStops[_nextStopIndex]))
                _nextStopIndex++;

            _distToNextStop = _nextStopIndex >= 0 && _nextStopIndex < routeStops.Count
                ? Vector3.Distance(playerBus.transform.position, routeStops[_nextStopIndex].GetWorldPosition())
                : float.MaxValue;
            return;
        }

        _nextStopIndex  = bestIdx;
        _distToNextStop = best;

        if (bestIdx >= 0 && !_announcedStops.Contains(bestIdx))
        {
            _announcedStops.Add(bestIdx);

            if (_lastProcessedStopIndex != bestIdx)
                _hasProcessedStop = false;

            // [ADD] Stacking: if the request for whatever stop was current
            // a moment ago never got served (doors never opened -- the only
            // way _stopRequested survives to this point), fold its count in
            // rather than let the overwrite below erase it.
            if (_stopRequested && _alightCount > 0)
                _missedAlightCarryover += _alightCount;

            int planAlighting  = _playerPaxPlan?.GetAlightingCount(bestIdx, _onboardPax) ?? 0;
            int totalAlighting = Mathf.Min(_onboardPax, planAlighting + _missedAlightCarryover);
            if (totalAlighting > 0)
            {
                _stopRequested = true;
                _alightCount   = totalAlighting;
            }
            else
            {
                _stopRequested = false;
                _alightCount   = 0;
            }
            // Folded into _alightCount above -- if THIS stop also gets
            // missed, the merged total (not the original per-stop pieces)
            // carries forward again next time, avoiding any double count.
            _missedAlightCarryover = 0;

            _predictedBoardingPax = _playerPaxPlan?.PaxAt(bestIdx) ?? 0;

            // [ADD] ADA/wheelchair pax -- independent lightweight roll (see
            // this class's ADA fields' own header comment). Re-rolled fresh
            // per newly-announced stop, same cadence the regular alighting/
            // boarding rolls above use. Never at the last stop of the leg --
            // matches "no new pax board at all there".
            bool isLastStopOnLeg = bestIdx == routeStops.Count - 1;
            _adaAlightRequested = _onboardAdaPax > 0 && (isLastStopOnLeg || UnityEngine.Random.value < 0.35f);
            _adaBoardingWaiting = !isLastStopOnLeg && _onboardAdaPax < ADA_CAPACITY && UnityEngine.Random.value < 0.08f;
        }

        if (bestIdx >= 0 && _distToNextStop > stopDetectRadius + 10f)
            _announcedStops.Remove(bestIdx);

        bool changed = (_distToNextStop       != _cachedDist)
                    || (_nextStopIndex        != _cachedNextIdx)
                    || (_stopRequested        != _cachedStopReq)
                    || (_alightCount          != _cachedAlightCount)
                    || (_onboardPax           != _cachedPax)
                    || (_predictedBoardingPax != _cachedBoardingPax);

        if (changed)
        {
            _cachedDist        = _distToNextStop;
            _cachedNextIdx     = _nextStopIndex;
            _cachedStopReq     = _stopRequested;
            _cachedAlightCount = _alightCount;
            _cachedPax         = _onboardPax;
            _cachedBoardingPax = _predictedBoardingPax;
            StopHUD.Instance?.Refresh();
            DriverConsole.Instance?.RefreshStopHUD();
        }
    }

    // ── At-stop box test ──────────────────────────────────────────────────────
    private bool AtStop
    {
        get
        {
            var routeStops = GetActiveStops(_isOutbound);
            if (routeStops == null || _nextStopIndex < 0 || _nextStopIndex >= routeStops.Count) return false;
            Vector3 stopPos = routeStops[_nextStopIndex].GetWorldPosition();
            Vector3 d       = playerBus.transform.position - stopPos;
            return Mathf.Abs(d.x) <= stopZoneHalfExtents.x
                && Mathf.Abs(d.y) <= stopZoneHalfExtents.y
                && Mathf.Abs(d.z) <= stopZoneHalfExtents.z;
        }
    }

    /// <summary>Public entry point for BusSimulationController.HandleDoorToggle
    /// — the door-open pax processing is real (see ProcessStopDoorOpen below),
    /// but that method is private/void and BusSimulationController expected a
    /// public (int off, int on) return. Resets the cache first so an early-out
    /// (not at a stop, already processed, boarding still in progress) correctly
    /// reports (0, 0) rather than stale counts from a previous stop.</summary>
    public (int off, int on) ProcessDoorOpenAtCurrentStop()
    {
        _lastDoorOpenOff = 0;
        _lastDoorOpenOn  = 0;
        ProcessStopDoorOpen();
        return (_lastDoorOpenOff, _lastDoorOpenOn);
    }

    private void ProcessStopDoorOpen()
    {
        if (_hasProcessedStop) return;
        if (!AtStop) { PrintTagged("Not at a designated stop.", "warn"); return; }
        if (_boardingInProgress) return;

        _hasProcessedStop       = true;
        _lastProcessedStopIndex = _nextStopIndex;
        string stopName         = NextStopName;
        string stopCode         = ResolveCurrentStopCode();

        int off = 0;
        if (_stopRequested && _alightCount > 0)
        {
            off = Mathf.Min(_alightCount, _onboardPax);
            _onboardPax   -= off;
            _stopRequested = false;
            PointsManager.Instance.RegisterAlighting(off);
            for (int i = 0; i < off && _passengerDestinations.Count > 0; i++)
                _passengerDestinations.RemoveAt(0);

            _alightCount = 0;
        }

        int boarding = _predictedBoardingPax;
        _predictedBoardingPax = 0;

        // [ADD] A wheelchair pax always boards/alights first -- regular
        // boarding through this same door waits until the full ramp cycle
        // (deploy -> load -> retract) finishes; see ReleaseDeferredBoarding,
        // called once the ramp is back to Stowed with the door still open.
        // Regular alighting above is untouched -- only NEW boarding waits.
        bool holdForRamp = AdaEventPending;
        if (holdForRamp)
        {
            _deferredBoardingPax = boarding;
            boarding = 0;
            PrintTagged(_adaAlightRequested
                ? "♿ Wheelchair passenger requesting the lift to get off — press R once parked, kneeling, in neutral, with the front door open."
                : "♿ Wheelchair passenger waiting to board — press R once parked, kneeling, in neutral, with the front door open.",
                "warn");
        }
        else
        {
            _onboardPax += boarding;
        }
        PointsManager.Instance.RegisterBoarding(boarding);

        var routeStops = GetActiveStops(_isOutbound);
        int stopsRemaining = routeStops != null ? routeStops.Count - _nextStopIndex - 1 : 5;
        for (int i = 0; i < boarding; i++)
            _passengerDestinations.Add(UnityEngine.Random.Range(1, Mathf.Max(2, stopsRemaining + 1)));

        // Cache the counts for ProcessDoorOpenAtCurrentStop's return value —
        // this method itself stays void (its original contract; its own
        // early-returns above don't have meaningful off/on counts to report).
        _lastDoorOpenOff = off;
        _lastDoorOpenOn  = boarding;

        int paxHandled = off + boarding;
        if (paxHandled > 0)
        {
            _shiftScore += paxHandled;
            PrintTagged($"🧍 {stopName}: {off} off, {boarding} on (+{paxHandled} pts)", "info");
        }
        else if (!holdForRamp)
        {
            PrintTagged($"{stopName}: no passenger activity.", "info");
        }

        StopHUD.Instance?.Refresh();
        DriverConsole.Instance?.RefreshStopHUD();

        if (PaxSimManager.Instance != null && !string.IsNullOrEmpty(stopCode) && (off > 0 || boarding > 0))
            StartCoroutine(RunDoorBoardingSequence(stopCode, off, boarding));
    }

    private IEnumerator RunDoorBoardingSequence(string stopCode, int off, int boarding)
    {
        _boardingInProgress = true;
        Vector3 doorPos = GetDoorWorldPosition();
        Vector3 curbPos = doorPos;
        Vector3 fwd     = playerBus != null ? playerBus.transform.forward : transform.forward;

        if (off > 0)
            yield return BusBoardingSequencer.RunAlighting(this, stopCode, doorPos, curbPos, off, _boardingHandle);

        if (boarding > 0)
            yield return BusBoardingSequencer.RunBoarding(this, stopCode, doorPos, fwd, boarding, PlayerBusID, true, _boardingHandle);

        _boardingInProgress = false;
    }

    /// <summary>Applies boarding that was held back by ProcessStopDoorOpen
    /// while a wheelchair pax was being served. Called once the ramp
    /// finishes retracting (see UpdateRampState) and again from
    /// HandleDoorToggle whenever the front door opens, in case the door was
    /// closed at the moment the ramp finished. If the door isn't open by
    /// either point, the deferred pax simply never board this stop --
    /// same as any other pax who'd have needed an open door that never came.</summary>
    private void ReleaseDeferredBoarding()
    {
        if (_deferredBoardingPax <= 0) return;
        if (playerBus == null || !playerBus.doorsOpen) return;

        int boarding = _deferredBoardingPax;
        _deferredBoardingPax = 0;

        _onboardPax += boarding;
        PointsManager.Instance.RegisterBoarding(boarding);

        var routeStops = GetActiveStops(_isOutbound);
        int stopsRemaining = routeStops != null ? routeStops.Count - _nextStopIndex - 1 : 5;
        for (int i = 0; i < boarding; i++)
            _passengerDestinations.Add(UnityEngine.Random.Range(1, Mathf.Max(2, stopsRemaining + 1)));

        _shiftScore += boarding;
        PrintTagged($"🧍 {boarding} boarding now that the lift is stowed.", "info");

        StopHUD.Instance?.Refresh();
        DriverConsole.Instance?.RefreshStopHUD();

        string stopCode = ResolveCurrentStopCode();
        if (PaxSimManager.Instance != null && !string.IsNullOrEmpty(stopCode))
            StartCoroutine(RunDoorBoardingSequence(stopCode, 0, boarding));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  WHEELCHAIR LIFT RAMP  ("ramp" console command / KeyBindings.rampDeploy)
    //  No mesh yet -- see this class's ADA fields' header comment. Progress
    //  is exposed via CurrentRampState/RampProgress01 for DriverConsole and
    //  BusDriverLCDBoard to render as a plain readout in the meantime.
    // ═════════════════════════════════════════════════════════════════════════
    public void HandleRampToggle()
    {
        if (playerBus == null) { PrintTagged("No active bus.", "warn"); return; }

        switch (_rampState)
        {
            case RampState.Stowed:
                // [FIX per request] Freely deployable any time, same as
                // kneel -- no longer gated on an actual wheelchair pax
                // being present. The physical prerequisites below still
                // apply (those are about the lift mechanism itself, not
                // about who's using it). ProcessStopDoorOpen only holds
                // regular boarding back when AdaEventPending is genuinely
                // true, so a manual/test deploy with nobody waiting doesn't
                // affect anyone else's boarding.
                if (!playerBus.doorsOpen)
                {
                    PrintTagged("Ramp needs the front door open first.", "warn");
                    return;
                }
                if (PlayerSuspension == null || !PlayerSuspension.IsKneeling)
                {
                    PrintTagged("Ramp needs the bus kneeling first.", "warn");
                    return;
                }
                if (playerBus.currentDirection != BusSimulationController.GearDirection.Neutral)
                {
                    PrintTagged("Ramp needs the bus in neutral first.", "warn");
                    return;
                }
                if (!playerBus.parkingBrake)
                {
                    PrintTagged("Ramp needs the parking brake set first.", "warn");
                    return;
                }

                _rampState      = RampState.Deploying;
                _rampProgress01 = 0f;
                _rampTimer      = 0f;
                _rampHiccupDone = false;
                PrintTagged("♿ Deploying wheelchair lift...", "system");
                break;

            case RampState.Deployed:
                _rampState      = RampState.Retracting;
                _rampProgress01 = 1f;
                _rampTimer      = 0f;
                PrintTagged("♿ Retracting wheelchair lift...", "system");
                break;

            case RampState.Deploying:
            case RampState.Loading:
            case RampState.Retracting:
                PrintTagged("Lift is already in motion.", "warn");
                break;
        }
    }

    /// <summary>Advances the ramp's own timers. Called once per frame from
    /// Update() regardless of hotkey state, same as UpdateStopProximity.</summary>
    private void UpdateRampState()
    {
        switch (_rampState)
        {
            case RampState.Deploying:
            {
                _rampTimer += Time.deltaTime;
                // Hiccup: pause dead at 55% for one real second before
                // continuing on to 100%, instead of a clean linear ramp.
                float preHiccupSeconds  = RAMP_DEPLOY_SECONDS * RAMP_HICCUP_AT;
                float postHiccupSeconds = RAMP_DEPLOY_SECONDS * (1f - RAMP_HICCUP_AT);

                if (!_rampHiccupDone)
                {
                    if (_rampTimer >= preHiccupSeconds)
                    {
                        _rampProgress01 = RAMP_HICCUP_AT;
                        if (_rampTimer >= preHiccupSeconds + RAMP_HICCUP_PAUSE_SEC)
                        {
                            _rampHiccupDone = true;
                            _rampTimer      = 0f; // restart the timer for the post-hiccup leg
                        }
                    }
                    else
                    {
                        _rampProgress01 = _rampTimer / preHiccupSeconds * RAMP_HICCUP_AT;
                    }
                }
                else
                {
                    float t = Mathf.Clamp01(_rampTimer / postHiccupSeconds);
                    _rampProgress01 = Mathf.Lerp(RAMP_HICCUP_AT, 1f, t);
                    if (t >= 1f)
                    {
                        _rampState      = RampState.Loading;
                        _rampProgress01 = 1f;
                        _rampTimer      = 0f;
                        PrintTagged("♿ Ramp deployed — loading...", "system");
                    }
                }
                break;
            }

            case RampState.Loading:
            {
                _rampTimer += Time.deltaTime;
                if (_rampTimer >= RAMP_LOAD_SECONDS)
                {
                    // [FIX] Message used to unconditionally claim a
                    // wheelchair pax boarded/got off, even on a manual/test
                    // deploy (ramp is now freely usable, see HandleRampToggle)
                    // where neither flag was ever set. Snapshot which actually
                    // happened BEFORE clearing them so the message matches
                    // reality instead of assuming both every time.
                    bool didAlight  = _adaAlightRequested;
                    bool didBoard   = _adaBoardingWaiting;

                    // Alighting first (make room), then boarding -- matches
                    // "wheelchair pax always boards first" relative to
                    // regular pax without needing to touch this order for
                    // the rare stop where both an alight and a board are
                    // pending in the same ramp cycle.
                    if (didAlight)
                    {
                        _onboardAdaPax = Mathf.Max(0, _onboardAdaPax - 1);
                        _adaAlightRequested = false;
                    }
                    if (didBoard)
                    {
                        _onboardAdaPax = Mathf.Min(ADA_CAPACITY, _onboardAdaPax + 1);
                        _adaBoardingWaiting = false;
                    }
                    _rampState = RampState.Deployed;

                    string loadMsg = (didAlight, didBoard) switch
                    {
                        (true, true)   => "♿ Wheelchair off, another aboard",
                        (true, false)  => "♿ Wheelchair off",
                        (false, true)  => "♿ Wheelchair aboard",
                        (false, false) => "Lift cycle complete — no wheelchair passenger this time",
                    };
                    PrintTagged($"{loadMsg} — press R to raise the ramp when ready.", "system");
                }
                break;
            }

            case RampState.Retracting:
            {
                _rampTimer += Time.deltaTime;
                float t = Mathf.Clamp01(_rampTimer / RAMP_RETRACT_SECONDS);
                _rampProgress01 = Mathf.Lerp(1f, 0f, t);
                if (t >= 1f)
                {
                    _rampState      = RampState.Stowed;
                    _rampProgress01 = 0f;
                    PrintTagged("♿ Ramp stowed.", "system");
                    ReleaseDeferredBoarding();
                }
                break;
            }
        }
    }

    private string ResolveCurrentStopCode()
    {
        var routeStops = GetActiveStops(_isOutbound);
        if (routeStops == null || _nextStopIndex < 0 || _nextStopIndex >= routeStops.Count) return null;
        return routeStops[_nextStopIndex].stopCode;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    /// <summary>Every stop that counts as the target terminal besides the primary one (see SetTargetTerminal).</summary>
    private readonly List<BusStopMarker> _targetTerminalAlts = new List<BusStopMarker>();

    /// <summary>Sets where the bus must report in. Terminal A is the first stop of A-&gt;Z AND the last stop of
    /// Z-&gt;A; terminal Z is the first stop of Z-&gt;A AND the last stop of A-&gt;Z (a terminal can have a pickup and
    /// a dropoff stop, e.g. Parkview, Meridian Square). Any of a terminal's stops counts. The primary target (the
    /// one shown as "heading to") is this direction's own first stop when starting, or last stop when ending; the
    /// route's named terminal for that end is accepted as well.</summary>
    private void SetTargetTerminal(bool outbound, bool forStart)
    {
        bool wantZ = forStart ? !outbound : outbound;   // start: A-&gt;Z begins at A, Z-&gt;A at Z; end: A-&gt;Z at Z, Z-&gt;A at A

        var outStops = GetActiveStops(true);
        var inStops  = GetActiveStops(false);
        BusStopMarker Marker(List<BusStopData> l, bool last) =>
            (l == null || l.Count == 0) ? null : FindTerminalMarker(l[last ? l.Count - 1 : 0].stopCode);

        // The stops of this terminal: A = first of A->Z + last of Z->A; Z = first of Z->A + last of A->Z.
        BusStopMarker fromOutbound = wantZ ? Marker(outStops, true)  : Marker(outStops, false);
        BusStopMarker fromInbound  = wantZ ? Marker(inStops,  false) : Marker(inStops,  true);
        BusStopMarker named        = FindTerminalMarker(GetActiveTerminalCode(getTerminalZ: wantZ));

        // Primary: the stop this direction itself starts / ends at.
        BusStopMarker primary = outbound ? fromOutbound : fromInbound;
        if (primary == null) primary = outbound ? fromInbound : fromOutbound;
        if (primary == null) primary = named;

        _targetTerminal = primary;
        _targetTerminalAlts.Clear();
        foreach (var m in new[] { fromOutbound, fromInbound, named })
            if (m != null && m != primary && !_targetTerminalAlts.Contains(m)) _targetTerminalAlts.Add(m);
    }

    private void ResolveTerminals(BusRouteData route)
    {
        // [FIX] Used to infer terminals from stops[0]/stops[last] of the
        // OUTBOUND stop-binding list (FindTerminalFromRouteStops below) —
        // fragile, since it silently breaks if a variant's outbound override
        // list doesn't happen to end exactly on terminalZCodeOverride (e.g.
        // MIDWAY62 sitting as the last authored outbound stop while the real
        // Z terminal, s0029, only appears at the head of the inbound list).
        // GetActiveTerminalCode reads terminalACodeOverride/terminalZCodeOverride
        // directly, so it's correct regardless of stop-list authoring order.
        _currentTerminalA = FindTerminalMarker(GetActiveTerminalCode(getTerminalZ: false));
        _currentTerminalZ = FindTerminalMarker(GetActiveTerminalCode(getTerminalZ: true));
    }

    private BusStopMarker FindTerminalFromRouteStops(BusRouteData route, bool wantZTerminal)
    {
        var stops = route.GetStops(true, _activeVariant);
        if (stops == null || stops.Count == 0)
        {
            PrintTagged($"⚠ Route {route.routeNumber} has no resolved outbound stops — can't find terminal.", "error");
            return null;
        }

        BusStopData targetStopData = wantZTerminal ? stops[stops.Count - 1] : stops[0];

        // [PERF FIX] Was its own FindObjectsOfType<BusStopMarker>() scan --
        // shared cache on CityManager now, same one FindTerminalMarker/
        // GetNearestTerminalCode/NPCBusController's own fallback all use.
        var found = CityManager.Instance != null ? CityManager.Instance.GetStopMarker(targetStopData.stopCode) : null;
        if (found != null) return found;

        PrintTagged($"⚠ Route {route.routeNumber} resolved stop '{targetStopData.stopCode}' has no matching BusStopMarker in scene.", "error");
        return null;
    }

    private BusStopMarker FindTerminalMarker(string stopCode)
    {
        if (string.IsNullOrEmpty(stopCode)) return null;
        return CityManager.Instance != null ? CityManager.Instance.GetStopMarker(stopCode) : null;
    }

    /// <summary>
    /// Where the driver should be heading right now when NOT yet on the route line, for the guide overlay:
    /// dead-running to the start terminal (its idle-zone aisle entry if it has one), arrived at a terminal with an
    /// idle zone (a free bay), or waiting to depart (the first stop of the coming leg).
    /// </summary>
    public bool TryGetGuideTarget(out Vector3 pos, out string label)
    {
        pos = default; label = "";
        if (playerBus == null) return false;

        switch (_shiftState)
        {
            case PlayerShiftState.DeadrunToStart:
            {
                if (_targetTerminal == null) return false;
                var zone = TerminalIdleZone.GetForStop(_targetTerminal.StopCode);
                if (zone != null && zone.bays != null && zone.bays.Count > 0 && zone.bays[0].ingressPath != null && zone.bays[0].ingressPath.Count > 0)
                    pos = zone.bays[0].ingressPath[0];
                else
                    pos = _targetTerminal.transform.position;
                label = "Start terminal";
                return true;
            }
            case PlayerShiftState.ArrivedAtTerminal:
            {
                var zone = TerminalIdleZone.GetForStop(GetNearestTerminalCode());
                if (zone == null || zone.bays == null) return false;
                for (int i = 0; i < zone.bays.Count; i++)
                {
                    int owner = zone.GetBayOwner(i);
                    if ((owner < 0 || owner == PlayerBusID) && !zone.IsBayPhysicallyOccupied(i, PlayerBusID))
                    {
                        pos = zone.GetParkingPosition(i);
                        label = "Idle bay";
                        return true;
                    }
                }
                return false;
            }
            case PlayerShiftState.WaitingToDepart:
            {
                var stops = GetActiveStops(_isOutbound);
                if (stops == null || stops.Count == 0) return false;
                pos = stops[0].GetWorldPosition();
                label = "First stop";
                return true;
            }
        }
        return false;
    }

    private string GetNearestTerminalCode()
    {
        if (playerBus == null || CityManager.Instance == null) return "";
        BusStopMarker nearest = null;
        float         minDist = float.MaxValue;
        foreach (var marker in CityManager.Instance.AllStopMarkers())
        {
            if (!marker.IsTerminal) continue;
            float d = Vector3.Distance(playerBus.transform.position, marker.transform.position);
            if (d < minDist) { minDist = d; nearest = marker; }
        }
        return nearest?.StopCode ?? "";
    }

    // ── Print helpers ─────────────────────────────────────────────────────────
    private void PrintTagged(string msg, string tag = "info")
    {
        if (DriverConsole.Instance != null)
            DriverConsole.Instance.PrintTagged(msg, tag);
        else
            Debug.Log($"[PlayerHandoff|{tag}] {msg}");
    }

    private void Print(string msg) => PrintTagged(msg, "info");
}