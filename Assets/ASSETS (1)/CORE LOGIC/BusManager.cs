using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
// ═══════════════════════════════════════════════════════════════════════════════
//  IDLE ZONE
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class IdleZone
{
    public string  zoneName;
    public Vector3 position;
    [Tooltip("How many buses can idle here simultaneously (0 = unlimited)")]
    public int     capacity;
    [HideInInspector] public int currentCount;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS RECORD
// ═══════════════════════════════════════════════════════════════════════════════
public class BusRecord
{
    public int              busID;
    public NPCBusController controller;
    public int              lapCount;
    public int              lapThreshold;
    public IdleZone         idleZone;

    // [FIX] isActive/isIdle used to be manually written bool fields, set from
    // 9 separate call sites across BusManager.cs/Fleetbootstraper.cs with no
    // single owner -- exactly the bug shape that caused the "orphaned hold-
    // branch" freeze fixed earlier this session (a bus's State changed
    // without one of those 9 sites also running, so BusManager never found
    // out and the bus was invisible to redispatch forever). Derived from the
    // controller's own State instead, so nothing needs remembering to stay
    // in sync -- there's nothing left TO desync.
    //
    // Three real states, not two: isIdle means the controller is genuinely
    // parked (State == Idle). isActive means actively serving a route --
    // State != Idle is necessary but not sufficient, because a retired bus
    // that's still physically driving to its depot is ALSO State != Idle
    // (DepotIngress) without being "active" in any meaningful sense.
    // BusManager.IsDepotBound (its own _depotBoundBuses set, which it already
    // owned and maintained independently) is what tells that third state
    // apart from genuine active service.
    public bool isIdle =>
        controller != null && controller.State == NPCBusController.BusState.Idle;

    public bool isActive =>
        controller != null
        && controller.State != NPCBusController.BusState.Idle
        && (BusManager.Instance == null || !BusManager.Instance.IsDepotBound(busID));

    // [FIX] assignedRoute/activeSlot removed -- grepped every reference
    // across the whole codebase: 6 write sites (BusManager.cs/
    // Fleetbootstraper.cs), zero reads anywhere. Dead bookkeeping, not a
    // sync risk -- BusScheduler._slotByBus is the actual, already-consulted
    // source of truth for what a bus is currently assigned to
    // (BusScheduler.TryGetAssignedSlot(busID, ...)).

    // [ADD Bug 6] When (SimClock.AbsoluteGameMinutes) this bus most recently
    // became idle, and where it was standing at that moment. Lets
    // GetIdleBusForRoute() give an explicit preference to a bus that just
    // became idle/available at or near the requesting route's terminal,
    // instead of relying purely on distance-weighted scoring — which
    // penalizes a retiring bus via _depotBoundBuses' driving-away state and
    // can pass it over for a freshly-summoned idle bus elsewhere.
    public float            idleSinceMinutes = -1f;
    public Vector3          idlePosition;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS MANAGER  v4  (Overflow-dispatch QoL fix)
//
//  BUGS FIXED vs v3
//  ────────────────
//  [FIX-F]  TryReassignToOverflowRoute did a blind first-fit scan over
//           managedRoutes in Inspector array order: it grabbed the FIRST
//           route under cap with ANY unassigned slot, completely ignoring
//           (a) whether that slot was anywhere near due, and (b) whether
//           other eligible routes had a better (sooner) candidate. Two
//           symptoms this caused:
//             - Overflow buses all piled onto whichever route happened to
//               sit first in the managedRoutes array (e.g. Route 1),
//               regardless of which route actually triggered the overflow.
//             - Buses got launched (AssignRoute + SetIdle(false) fire
//               immediately inside AssignBusToSlot) for slots scheduled
//               many minutes in the future, breaking headway (e.g. a bus
//               meant for a 5:30 departure rolling out at 5:21).
//           Now: scan ALL eligible routes, only consider a candidate slot
//           if it departs within `overflowDispatchWindowMinutes` of the
//           current game time, and pick the soonest-due candidate across
//           the whole set instead of the first one found. If nothing
//           qualifies, the bus is parked idle instead of launched early.
//
//  All v3 fixes (A–E) retained unchanged, see history in project notes.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusManager : MonoBehaviour
{
    public static BusManager Instance { get; private set; }

    [Header("NPC Bus Pool")]
    public List<NPCBusController> busPrefabPool = new();

    [Header("Idle Zones")]
    public List<IdleZone> idleZones = new();

    [Header("Overflow Dispatch")]
    [Tooltip("An overflow bus will only be sent to fill another route's slot if that slot departs within this many game-minutes of now. Prevents early-launching buses toward far-future slots and breaking headway.")]
    public float overflowDispatchWindowMinutes = 15f;

    [Header("Debug")]
    public bool logAssignments = true;

    private Dictionary<int, BusRecord> _busRecords = new();
    private List<int>                  _idlePool   = new();
    private int                        _nextBusID  = 0;

    // [REMOVED 2026-09-29] ClearAllRegistrations -- multiplayer, rebuilt architecture: a network
    // client keeps its own full local fleet/registrations unconditionally now, same as
    // single-player/host. Nothing clears this before connecting any more.

    // FIX: buses that have retired (RetireBusFromRoute) but haven't finished
    // physically driving to the depot yet (NotifyBusParkedAtDepot) live in
    // neither _busRecords.isActive nor _idlePool — completely invisible to
    // GetIdleBus/GetIdleBusForRoute. AbortDepotIngress() exists on
    // NPCBusController specifically to pull a bus out of that drive, but
    // nothing ever called it. This set is what makes an in-transit-to-depot
    // bus discoverable as a candidate again.
    private readonly HashSet<int> _depotBoundBuses = new();

    /// <summary>[ADD] Exposed for BusRecord.isActive's derivation -- see that
    /// property's own comment. A depot-bound bus is neither actively serving
    /// a route nor genuinely idle yet (still physically driving home), a
    /// third state State alone can't distinguish from "still in service"
    /// without also consulting this set.</summary>
    public bool IsDepotBound(int busID) => _depotBoundBuses.Contains(busID);

    // ═════════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════════
    private void Awake()
    {
        // [FIX-D] Set singleton BEFORE RegisterAllBuses so any controller Awake
        // that calls BusManager.Instance during registration finds it.
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        RegisterAllBuses();
    }

    private void Start()
    {
        SubscribeToScheduler();

        // Self-heal: if BusScheduler.Start() built/pre-assigned the day
        // before this object's own Awake() finished registering the fleet
        // (possible whenever BusScheduler is a persistent DontDestroyOnLoad
        // object that already ran in an earlier scene, or any other path
        // where these two don't share the same Awake/Start pass), the whole
        // day is left sitting Unassigned — "TBD" forever, and past slots
        // get force-completed with assignedBusID = -1. This retroactively
        // fills those in now that the fleet is definitely registered.
        BusScheduler.Instance?.RebuildPendingPreAssignments();
            var asset = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
    asset.maxAdditionalLightsCount = 16;
    Debug.Log($"maxAdditionalLightsCount after set: {asset.maxAdditionalLightsCount}");
    }

    private void RegisterAllBuses()
    {
        foreach (var ctrl in busPrefabPool)
        {
            if (ctrl == null) continue;
            RegisterBusInternal(ctrl);
        }

        if (logAssignments)
            Debug.Log($"[BusManager] Registered {_busRecords.Count} buses. Idle pool: {_idlePool.Count}");
    }

    private int RegisterBusInternal(NPCBusController ctrl)
    {
        int id   = _nextBusID++;
        ctrl.busID = id;

        var record = new BusRecord
        {
            busID        = id,
            controller   = ctrl,
            lapCount     = 0,
            lapThreshold = 0, // filled per-route by NotifySegmentComplete / assignment
            // isActive/isIdle are derived from State now -- ctrl.SetIdle(true)
            // below is what actually makes isIdle read true (and isActive false).
        };
        _busRecords[id] = record;
        _idlePool.Add(id);
        ctrl.SetIdle(true);
        return id;
    }

    // ── Late registration (depot spawn) ──────────────────────────────────────
    // [CHANGE] This is now the ACTUAL central chokepoint Ted's instinct was
    // pointing at -- previously this method existed but nothing ever called
    // it (BusSpawner bypassed it entirely with a direct busPrefabPool.Add,
    // and PlayerHandoff never touched it at all). That's exactly why the
    // player's own bus, and any handoff/replacement bus, never got its ad
    // boards configured -- there was no shared "a bus just became active"
    // hook, only BusSpawner's own inline NPC-dispatch-only setup.
    // Now: BusSpawner's normal spawn flow calls this too, and
    // PlayerHandoff.SetPlayerBus calls this on every player bus assignment
    // AND every handoff swap. SetupAdBoards itself is idempotent (see
    // BusSpawner), so calling this more than once for the same physical bus
    // is always safe -- it just won't re-roll or reconfigure anything past
    // the first call.
    public int RegisterBus(NPCBusController ctrl)
    {
        if (ctrl == null) return -1;

        // [FIX] Idempotency guard. This method legitimately gets called more
        // than once for the SAME physical bus -- spawned as NPC by
        // BusSpawner, then later handed to the player by PlayerHandoff.
        // RegisterBusInternal used to run unconditionally on every call,
        // which minted a BRAND NEW busID via _nextBusID++, overwrote
        // ctrl.busID with it, and left the OLD BusRecord/id orphaned in
        // _busRecords/_idlePool forever -- still pointing at this same
        // controller, under an ID the controller no longer reports as its
        // own. Anything that had already cached that old busID (a
        // scheduler slot's assignedBusID, _slotByBus, a saved
        // pre-assignment, the live map's fleet->busID lookup) kept
        // comparing against an ID this bus had silently moved off of, so
        // the bus stopped being recognized as "this route's assigned
        // vehicle" and never got pulled onto it -- and any UI reading off
        // the stale record's busID instead of resolving through to the
        // controller's actual fleetNumber would show that raw leftover ID.
        // Now: if this controller already has a record, reuse its existing
        // busID instead of minting a duplicate.
        foreach (var existing in _busRecords.Values)
        {
            if (existing.controller == ctrl)
            {
                if (logAssignments)
                    Debug.Log($"[BusManager] RegisterBus: '{ctrl.name}' (fleet#{ctrl.fleetNumber}) already registered as busID={existing.busID} -- reusing it instead of minting a duplicate.");
                BusSpawner.Instance?.SetupAdBoards(ctrl); // still safe/idempotent to call again
                return existing.busID;
            }
        }

        int id = RegisterBusInternal(ctrl);

        // [FIX] Was an unconditional Add -- a bus registered twice (e.g.
        // spawned as NPC, later handed to the player) would've ended up
        // with duplicate entries in busPrefabPool.
        if (!busPrefabPool.Contains(ctrl))
            busPrefabPool.Add(ctrl);

        // [ADD] The actual fix for gray/unconfigured ad quads on player and
        // handoff buses -- this is the one call that was missing entirely
        // outside BusSpawner's own NPC path.
        BusSpawner.Instance?.SetupAdBoards(ctrl);

        if (logAssignments)
            Debug.Log($"[BusManager] Late-registered bus. busID={id}, fleet#{ctrl.fleetNumber}");
        return id;
    }

    private void SubscribeToScheduler()
    {
        if (BusScheduler.Instance == null) { Debug.LogError("[BusManager] BusScheduler not found."); return; }
BusScheduler.Instance.OnDispatchBus       += HandleDispatch;
BusScheduler.Instance.OnRotateBus         += HandleRotation;
BusScheduler.Instance.OnIdleBus           += HandleIdle;
BusScheduler.Instance.OnReplacementNeeded += HandleReplacementNeeded;
// [FIX M4] Was an inline lambda, which can never be unsubscribed (you
// can't -= an anonymous delegate you never kept a reference to). Extracted
// into a named handler field so OnDestroy() below can actually detach it.
_onBusRetiredFromRouteHandler = (busID, route) => RetireBusFromRoute(busID);
BusScheduler.Instance.OnBusRetiredFromRoute += _onBusRetiredFromRouteHandler;
        _subscribedToScheduler = true;
    }

    // [ADD M4] BusScheduler is DontDestroyOnLoad and persists forever; BusManager
    // is NOT — it's destroyed and recreated on every scene reload (fresh shift,
    // return to main menu, session restart). Without unsubscribing here, every
    // reload left the previous BusManager instance permanently referenced by the
    // persistent scheduler's delegate lists — its entire object graph
    // (_busRecords, _idlePool, _depotBoundBuses, every cached NPCBusController
    // reference) became uncollectable, and every future event fired on all dead
    // instances as well as the live one. Likely the single largest contributor
    // to session-spanning memory growth found in the review.
    private bool _subscribedToScheduler = false;

    private void OnDestroy()
    {
        if (!_subscribedToScheduler || BusScheduler.Instance == null) return;

        BusScheduler.Instance.OnDispatchBus       -= HandleDispatch;
        BusScheduler.Instance.OnRotateBus         -= HandleRotation;
        BusScheduler.Instance.OnIdleBus           -= HandleIdle;
        BusScheduler.Instance.OnReplacementNeeded -= HandleReplacementNeeded;
        if (_onBusRetiredFromRouteHandler != null)
            BusScheduler.Instance.OnBusRetiredFromRoute -= _onBusRetiredFromRouteHandler;
    }

    private System.Action<int, string> _onBusRetiredFromRouteHandler;

    // ═════════════════════════════════════════════════════════════════════════
    //  DISPATCH
    // ═════════════════════════════════════════════════════════════════════════
    private void HandleDispatch(TimetableSlot slot)
    {
        // The timetable now names its bus at generation time (see
        // BusScheduler.PreAssignBusesForRouteDay) — this is the normal path.
        // Spawn exactly that bus, not whichever one happens to be idle.
        if (slot.assignedBusID >= 0)
        {
            if (!_busRecords.ContainsKey(slot.assignedBusID))
            {
                Debug.LogWarning($"[BusManager] Slot on Route {slot.FullRouteLabel} pre-assigned to " +
                                 $"Bus#{slot.assignedBusID}, but that bus isn't registered. Falling back to idle pool.");
            }
            else
            {
                AssignBusToSlot(slot.assignedBusID, slot);
                return;
            }
        }

        // Fallback only: happens if the pool was empty at generation time
        // (e.g. fleet registers after the scheduler builds the day) or the
        // pre-assigned bus is missing. Grabs whatever's idle so service
        // doesn't just stop.
        int liveCount = BusScheduler.Instance.CountActiveBusesOnRoute(slot.routeNumber);
        int cap       = BusScheduler.Instance.GetRouteCap(slot.routeNumber);

        if (liveCount >= cap)
        {
            int overflow = GetIdleBus();
            if (overflow >= 0) SendBusToIdleZone(overflow);
            return;
        }

        int dispatchID = GetIdleBusForRoute(slot.routeNumber, slot.scheduledDeparture % 1440f, slot.variantLetter);
        if (dispatchID < 0)
        {
            Debug.LogWarning($"[BusManager] No idle bus for Route {slot.FullRouteLabel} " +
                             $"{slot.DirectionLabel} @ {BusScheduler.MinutesToTimeString(slot.scheduledDeparture)}");
            return;
        }

        AssignBusToSlot(dispatchID, slot);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ASSIGNMENT
    // ═════════════════════════════════════════════════════════════════════════
    private void AssignBusToSlot(int busID, TimetableSlot slot)
    {
        if (!_busRecords.TryGetValue(busID, out var record)) return;

        var route = BusScheduler.Instance.GetRouteData(slot.routeNumber);
        if (route == null) { Debug.LogError($"[BusManager] Route {slot.routeNumber} not found."); return; }

        // Ask the scheduler FIRST. If it refuses this bus (vehicle policy, route full), the bus stays where it is.
        // Going ahead anyway sent a physical bus onto the route with no trip behind it, the slot stayed empty,
        // and the next retry sent another one, again and again.
        // [FIX-C] Only tell the scheduler if this slot isn't already committed.
        // If assignedBusID is already set to this bus, CommitAssignment already
        // ran (e.g. from ClaimNextAvailableSlot) — calling it again would double-
        // increment _busesPerRoute.
        if (slot.assignedBusID != busID && !BusScheduler.Instance.AssignBusToSlot(slot, busID)) return;

        // isActive/isIdle are now derived from record.controller.State -- the
        // AssignRouteWithProgress/SpawnParkedAtTerminal call below is what
        // actually moves State off Idle, so nothing needs setting here.
        _idlePool.Remove(busID);

        if (record.idleZone != null)
        {
            record.idleZone.currentCount--;
            record.idleZone = null;
        }

        record.controller.variantLetter = slot.variantLetter;

        // A pre-assigned slot can be handed to us before its departure time
        // (parked, waiting — 0% progress) or after it (already mid-trip, up
        // to and including 100%). Route each to the right spawn behavior
        // instead of always fast-forwarding through offline progression.
        if (BusScheduler.Instance.IsSlotLive(slot))
            record.controller.AssignRouteWithProgress(route, slot.isOutbound, slot);
        else
            record.controller.SpawnParkedAtTerminal(route, slot.isOutbound, slot);

        record.controller.SetIdle(false);

        if (logAssignments)
            Debug.Log($"[BusManager] Bus#{busID} → Route {slot.FullRouteLabel} {slot.DirectionLabel} " +
                      $"(lap threshold: {record.lapThreshold})");
    }

    // ── Segment complete (called by NPCBusController at terminal) ─────────────
    /// <summary>[FIX] Used to keep its own randomized lapCount/lapThreshold
    /// (Random.Range(lapThresholdMin, lapThresholdMax)) completely disconnected
    /// from BusScheduler.lapRetirementThreshold, which is what ACTUALLY drives
    /// retirement in BusScheduler.CompleteSlot. Any UI reading record.lapCount
    /// was showing a number that never matched real retirement timing — this
    /// is the "doesn't count laps" symptom. Now just mirrors the scheduler's
    /// real counter so display and behavior always agree.</summary>
    public void NotifySegmentComplete(int busID)
    {
        if (!_busRecords.TryGetValue(busID, out var record)) return;
        if (BusScheduler.Instance != null)
        {
            record.lapCount     = BusScheduler.Instance.GetLapCount(busID);
            // [FIX] Was the flat lapRetirementThreshold fallback field, not
            // this bus's actual trip-scaled threshold — reintroduced exactly
            // the "display never matches real retirement timing" bug this
            // method was originally written to fix, now that the real value
            // varies per route instead of being one constant everywhere.
            record.lapThreshold = BusScheduler.Instance.GetEffectiveLapThresholdPublic(busID);
        }
    }


    // ═════════════════════════════════════════════════════════════════════════
    //  ROTATION / IDLE / REPLACEMENT
    // ═════════════════════════════════════════════════════════════════════════
    private void HandleRotation(TimetableSlot slot)
    {
        if (slot.assignedBusID < 0) return;
        if (!_busRecords.TryGetValue(slot.assignedBusID, out var record)) return;
        record.controller.FlagForRotation();
    }

    private void HandleIdle(TimetableSlot slot)
    {
        // Still a no-op: RetireBusFromRoute (OnBusRetiredFromRoute) handles
        // bookkeeping, and NPCBusController reports the real idle/parked
        // outcome itself via NotifyBusParkedAtDepot once it's physically done.
    }

// ...existing code...
private void HandleReplacementNeeded(int playerBusID)
{
    // [FIX] Was calling GetIdleBus()/GetRotatingBus() -- neither checks
    // route eligibility or depot assignment at all, and playerBusID (the
    // only context this method receives) was never even used to look up
    // which route actually needs covering. That's exactly why this could
    // hand back a bus from a completely different depot/route policy --
    // TryClaimNextReliefSlotForReplacement (the real assignment step, via
    // AssignReplacementForPlayer) then correctly rejected it, producing
    // "found a replacement, then couldn't assign it." GetIdleBusForRoute
    // already does the real depot+policy+distance-scored selection (see its
    // own comments) -- this just needed to actually be called with the
    // player's real route instead of skipped entirely.
    string routeNumber = null;
    if (BusScheduler.Instance != null && BusScheduler.Instance.TryGetAssignedSlot(playerBusID, out var slot) && slot != null)
        routeNumber = slot.routeNumber;

    int replacementID = -1;
    if (!string.IsNullOrEmpty(routeNumber))
        replacementID = GetIdleBusForRoute(routeNumber);

    // [FIX Bug 3] No check anywhere in this method actually compared the
    // candidate against playerBusID -- if the player's own busID ever ended
    // up in _idlePool/_depotBoundBuses (plausible today since busID is a
    // disposable per-scene-instance counter, not a stable identity -- see
    // D1), or GetRotatingBus() below picked it up, this handed the player
    // back their own bus as its own "replacement." Explicit self-match
    // guard here since nothing else in the candidate-selection chain checks
    // identity at all.
    //
    // [FIX] playerBusID here is ALWAYS BusScheduler.PLAYER_BUS_ID (-2) --
    // it comes straight from BusScheduler.RequestRelief's own hardcoded
    // PlayerBusID argument, passed through OnReplacementNeeded unchanged.
    // -2 never equals a real scene-instance busID, so this comparison could
    // NEVER actually catch the player's own PHYSICAL bus (e.g. fleet 1151's
    // real busID) turning up as its own relief candidate -- which is exactly
    // what happened: SetPlayerBus's RegisterBus call never pulled the
    // physical bus out of _idlePool (that's fixed now, in PlayerHandoff.
    // SetPlayerBus, via ClaimBusForHandoff), but this guard was silently
    // useless as a backstop the whole time regardless. Now also resolves and
    // compares against the player's REAL physical busID.
    int playerPhysicalBusID = -1;
    var playerBusController = PlayerHandoff.Instance != null ? PlayerHandoff.Instance.playerBus : null;
    var playerNpc = playerBusController != null ? playerBusController.GetComponent<NPCBusController>() : null;
    if (playerNpc != null) playerPhysicalBusID = playerNpc.busID;

    if (replacementID == playerBusID || (playerPhysicalBusID >= 0 && replacementID == playerPhysicalBusID))
    {
        Debug.LogWarning($"[BusManager] GetIdleBusForRoute returned the player's own Bus#{replacementID} " +
                          $"(fleet {(playerNpc != null ? playerNpc.fleetNumber : -1)}) as a replacement candidate " +
                          $"for its own route — rejecting. This bus should never have been in the idle pool " +
                          $"while still possessed by the player.");
        _idlePool.Remove(replacementID);
        replacementID = -1;
    }

    if (replacementID >= 0 && BusScheduler.Instance.TryGetAssignedSlot(replacementID, out _))
    {
        Debug.LogWarning($"[BusManager] Bus#{replacementID} is already assigned! Skipping.");
        _idlePool.Remove(replacementID);
        ReleaseBusReservation(replacementID); // was leaking — never released
        replacementID = -1;
    }

    // [FIX] GetRotatingBus() is ALSO route-blind (first flagged-for-rotation
    // active bus, no policy check) -- it used to be trusted unconditionally
    // as the fallback. Now only offered as a fallback if it actually passes
    // the same route/depot policy check GetIdleBusForRoute enforces.
    if (replacementID < 0 && !string.IsNullOrEmpty(routeNumber))
    {
        var routeData = BusScheduler.Instance.GetRouteData(routeNumber);
        int rotating = GetRotatingBus();
        // [FIX Bug 3] Same self-match guard for the fallback path — and the
        // same playerBusID-is-always-(-2) gap fixed above applies here too.
        if (rotating == playerBusID || (playerPhysicalBusID >= 0 && rotating == playerPhysicalBusID))
        {
            Debug.LogWarning($"[BusManager] GetRotatingBus returned the player's own Bus#{rotating} — rejecting fallback candidate.");
            rotating = -1;
        }
        if (rotating >= 0 && routeData != null && _busRecords.TryGetValue(rotating, out var rotRec)
            && routeData.IsBusAllowed(rotRec.controller.fleetNumber)
            && (DepotManager.Instance == null || DepotManager.Instance.CanServeRoute(rotRec.controller.fleetNumber, routeNumber)))
        {
            replacementID = rotating;
        }
    }

    if (replacementID < 0)
    {
        // [FIX] Was just a Debug.LogWarning with no player-facing follow-up
        // -- the player's ReliefPending state never got cleared, which is
        // half of the "stuck on the route forever" bug (PlayerHandoff's own
        // failure-branch fix covers the other half). OnReplacementSearchFailed
        // resets relief state and gives the player a real way out.
        Debug.LogWarning($"[BusManager] No eligible replacement available for Route {routeNumber ?? "(unknown, no active slot for player)"}.");
        // [FIX] Was a bare PlayerHandoff.Instance?.OnReplacementSearchFailed() -- on the HOST that
        // always means the HOST's OWN local player, never the remote client that actually called
        // RequestRelief (playerBusID here is that client's real per-connection sentinel whenever
        // this fired via the network path). NotifyReplacementSearchFailed routes to the right one.
        BusScheduler.NotifyReplacementSearchFailed(playerBusID);
        return;
    }

    // Same reservation contract GetIdleBus() used to guarantee on its own --
    // GetIdleBusForRoute doesn't reserve, so this locks the pick explicitly
    // now instead of leaving it unlocked (which could let a second,
    // concurrent relief request offer the same bus twice).
    _reservedBuses.Add(replacementID);

    // Don't claim here — just hand the candidate to PlayerHandoff.
    // OnReplacementFound → AssignReplacementForPlayer is the ONE place
    // that actually calls TryClaimNextReliefSlotForReplacement. Claiming
    // here too meant every relief request double-claimed the same slot,
    // the second call always failing because the first already succeeded.
    // [FIX] Was a bare PlayerHandoff.Instance?.OnReplacementFound(...) -- same
    // wrong-target bug as the search-failed case above. NotifyReplacementFound
    // routes to whichever player (host-local or a specific remote client) this
    // playerBusID actually belongs to.
    BusScheduler.NotifyReplacementFound(playerBusID, replacementID);
}


public bool AssignReplacementForPlayer(int replacementID, TimetableSlot playerSlot)
{
    if (BusScheduler.Instance == null) return false;
    int fromBusID = playerSlot != null ? playerSlot.assignedBusID : (PlayerHandoff.Instance?.PlayerBusID ?? -1);

    // CHANGE THIS: Allow -2 (Player), only block -1 (Unassigned)
    if (fromBusID == -1) return false;

    if (!BusScheduler.Instance.TryClaimNextReliefSlotForReplacement(fromBusID, replacementID)) return false;

    // The claim only reserves the slot -- nothing physically moves the
    // replacement. Wake it: depot-parked buses wait for their egress window,
    // anything else goes through the normal dispatch path.
    if (_busRecords.TryGetValue(replacementID, out var rec) && rec.controller != null
        && BusScheduler.Instance.TryGetAssignedSlot(replacementID, out var repSlot) && repSlot != null)
    {
        if (!rec.controller.BeginReliefDepotWait(repSlot))
            AssignBusToSlot(replacementID, repSlot);
    }
    return true;
}

/// <summary>NPC-side counterpart to HandleReplacementNeeded/AssignReplacementForPlayer
/// above -- used when an in-service NPC bus needs a replacement queued for
/// its NEXT terminal without interrupting the leg it's currently driving
/// (see NPCBusController's low-fuel check, which calls this). Reuses the
/// same GetIdleBusForRoute candidate search and TryClaimNextReliefSlotForReplacement
/// claim step the player relief flow uses, but deliberately skips
/// PlayerHandoff and the _reservedBuses "pending player decision" bookkeeping
/// entirely -- there's no player confirmation step here, so candidate-select
/// and claim happen back-to-back in one call instead of across the
/// OnReplacementFound round-trip.</summary>
public bool TryRequestNpcRelief(int busID, string routeNumber)
{
    if (BusScheduler.Instance == null || string.IsNullOrEmpty(routeNumber)) return false;
    if (!BusScheduler.Instance.TryGetAssignedSlot(busID, out var mySlot) || mySlot == null) return false;

    int replacementID = GetIdleBusForRoute(routeNumber);
    if (replacementID < 0 || replacementID == busID) return false;
    if (BusScheduler.Instance.TryGetAssignedSlot(replacementID, out _)) return false; // already spoken for

    bool claimed = AssignReplacementForPlayer(replacementID, mySlot);
    if (logAssignments)
        Debug.Log(claimed
            ? $"[BusManager] Low-fuel relief: Bus#{replacementID} will take over Bus#{busID}'s Route {routeNumber} chain from the next terminal."
            : $"[BusManager] Low-fuel relief requested for Bus#{busID} but Bus#{replacementID} couldn't be assigned.");
    return claimed;
}
    // ── Idle zone management ──────────────────────────────────────────────────
private void SendBusToIdleZone(int busID)
{
    if (!_busRecords.TryGetValue(busID, out var record) || !record.isIdle) return;
    var zone = GetAvailableIdleZone(record.controller.transform.position);
    if (zone != null)
    {
        record.idleZone = zone;
        zone.currentCount++;
        record.controller.SetIdleTarget(zone.position);
        if (logAssignments)
            Debug.Log($"[BusManager] Bus#{busID} → idle zone '{zone.zoneName}'.");
    }
    else
    {
        // [CHANGED] Overflow logic now lives in FleetDispatcher, shared
        // with startup/tick dispatch instead of a separate hand-rolled
        // first-fit scan.
        if (!FleetDispatcher.Instance.TryDispatchIdleBus(record))
        {
            PullBusToIdle(busID);
            if (logAssignments)
                Debug.Log($"[BusManager] Bus#{busID} parked idle — no dispatchable route right now.");
        }
    }
}

private void PullBusToIdle(int busID)
{
    if (!_busRecords.TryGetValue(busID, out var record)) return;

    // [FIX] isActive/isIdle are now derived from State -- this used to mark
    // isIdle=true immediately, before the bus had actually arrived anywhere.
    // The SetIdleTarget branch below sends the bus on a DeadRunning drive to
    // an idle zone spot; it only really becomes idle once DeadRun() sees it
    // arrive and sets State = Idle itself. The derived property now reflects
    // that correctly instead of optimistically marking it idle at dispatch
    // time -- a real accuracy improvement, not just a refactor.

    // [FIX] Root cause of the "ghost route assignment" bug -- a bus routed
    // through here via GetIdleBus()'s overflow mechanism kept whatever real
    // BusScheduler slot it held (route number, on-schedule status, next stop
    // index all kept reporting as if it were still actively serving it) for
    // as long as that slot data lived, completely disconnected from
    // whatever this bus is now actually doing (idle zone / depot / wherever
    // depot.AcceptReturn sends it). Safe to call unconditionally --
    // ReleaseSlotWithoutComplete already no-ops if this bus doesn't hold one.
    BusScheduler.Instance?.ReleaseSlotWithoutComplete(busID);

    var depot = BusDepot.GetDepotForBus(busID);
    if (depot != null)
    {
        depot.AcceptReturn(record.controller as NPCBusController);
        if (record.idleZone != null) { record.idleZone.currentCount--; record.idleZone = null; }
    }
    else
    {
        var zone = GetAvailableIdleZone(record.controller.transform.position);
        if (zone != null)
        {
            if (record.idleZone != null) record.idleZone.currentCount--;
            record.idleZone = zone;
            zone.currentCount++;
            record.controller.SetIdleTarget(zone.position);
        }
        else
        {
            record.controller.SetIdle(true);
        }
    }

    if (!_idlePool.Contains(busID)) _idlePool.Add(busID);

    // [ADD Bug 6] Stamp when/where this bus became idle, so GetIdleBusForRoute
    // can prefer it over a farther-away bus if it just became available here.
    record.idleSinceMinutes = SimClock.Instance != null ? SimClock.Instance.AbsoluteGameMinutes : -1f;
    record.idlePosition     = record.controller.transform.position;

    if (logAssignments)
        Debug.Log($"[BusManager] Bus#{busID} pulled to idle. Depot: {depot?.depotCode ?? "none"}");
}

    /// <summary>[FIX] This used to run its own first-fit relief search —
    /// no route/depot eligibility check at all (just the first AtTerminal-
    /// or-Idle NPC in iteration order), and it called
    /// PlayerHandoff.OnReplacementFound TWICE: once with candidate.fleetNumber
    /// (the wrong ID space — OnReplacementFound expects a busID, and every
    /// downstream consumer, AssignReplacementForPlayer/
    /// TryClaimNextReliefSlotForReplacement/_busRecords/_slotByBus, is keyed
    /// on busID, not fleet number) and once with candidate.busID. Since
    /// PlayerHandoff has no guard against a second call, that first
    /// fleetNumber call could leave _replacementBusID set to a fleet number
    /// depending on call order — "found a replacement, then couldn't assign
    /// it" every time, exactly matching the fleet-number/busID mixup this
    /// class already fixes carefully everywhere else (see GetBusIDByFleetNumber's
    /// own doc comment).
    ///
    /// BusScheduler.InitiateReliefSearch(terminalCode, routeNumber) is the
    /// real implementation — it actually runs every candidate through
    /// CanAssign (route/depot/policy eligibility, the same gate the real
    /// assignment step uses) and calls OnReplacementFound exactly once with
    /// a genuine busID. This method now just forwards to it so any existing
    /// call site keeps compiling without needing to be touched, but no
    /// longer runs its own broken duplicate search.</summary>
    public void InitiateReliefSearch(string terminalCode)
    {
        int playerBusID = PlayerHandoff.Instance != null ? PlayerHandoff.Instance.PlayerBusID : BusScheduler.PLAYER_BUS_ID;
        string routeNumber = null;
        if (BusScheduler.Instance != null
            && BusScheduler.Instance.TryGetAssignedSlot(playerBusID, out var slot)
            && slot != null)
            routeNumber = slot.routeNumber;

        if (string.IsNullOrEmpty(routeNumber))
        {
            Debug.LogWarning("[BusManager] InitiateReliefSearch: no active player route to search relief for.");
            BusScheduler.NotifyReplacementSearchFailed(playerBusID);
            return;
        }

        BusScheduler.Instance?.InitiateReliefSearch(terminalCode, routeNumber, playerBusID);
    }
    // [FIX] Was private -- FleetDispatcher now also needs this (to find a
    // spare when a bus's own due slot can't be served because it's disabled/
    // possessed, see TryReassignSlotAwayFromUnavailableBus), same policy/
    // depot/distance-scored search everything else here already uses.
    /// <param name="minuteOfDay">Minute of the day (0-1439) the trip departs. Pass it so night-only bus pools are honoured;
    /// -1 (default) checks the daytime rules only.</param>
    /// <param name="variantLetter">The trip's route version, if it has one.</param>
    public int GetIdleBusForRoute(string routeNumber, float minuteOfDay = -1f, string variantLetter = "")
    {
        if (_idlePool.Count == 0 && _depotBoundBuses.Count == 0) return -1;

        var routeData = BusScheduler.Instance.GetRouteData(routeNumber);
        if (routeData == null) return -1;

        Vector3 routeStart = GetRouteStartPosition(routeData);
        int bestBus = -1;
        float bestPriority = float.MinValue;

        // FIX: depot-bound buses (retired, still physically driving to the
        // depot) are now considered alongside the true idle pool. A small
        // fixed penalty keeps genuinely-idle/already-parked buses preferred
        // when both are viable, since pulling a depot-bound bus means
        // interrupting its drive via AbortDepotIngress below.
        foreach (int busID in _idlePool.Concat(_depotBoundBuses))
        {
            if (BusScheduler.FreeAgentBusIDs.Contains(busID)) continue; // CHIP-type free agents aren't fleet spares
            if (ManagerLocks.IsBusLocked(busID)) continue; // Route Manager: a bus the manager fixed by hand isn't a spare
            if (!_busRecords.TryGetValue(busID, out var rec)) continue;

            if (DepotManager.Instance != null &&
                !DepotManager.Instance.CanServeRoute(rec.controller.fleetNumber, routeNumber))
                continue;

            if (routeData != null)
            {
                var variantData = !string.IsNullOrEmpty(variantLetter) ? routeData.GetVariant(variantLetter) : null;
                bool allowedNow = variantData != null
                    ? routeData.IsBusAllowedForVariant(rec.controller.fleetNumber, variantData, minuteOfDay)
                    : routeData.IsBusAllowed(rec.controller.fleetNumber, minuteOfDay);
                if (!allowedNow) continue;
            }

            float score = routeData.GetAssignmentScore(rec.controller.fleetNumber);
            float distance = routeStart == Vector3.zero
                ? 0f
                : Vector3.Distance(rec.controller.transform.position, routeStart);

            float priority = score * 1000f - distance * 0.1f;
            if (_depotBoundBuses.Contains(busID)) priority -= 500f; // prefer already-idle buses when available

            // [FIX Bug 6] Explicit "just became idle here" preference. Root
            // cause of the symptom (a bus already at the terminal losing out
            // to a freshly-summoned idle bus) is fixed at the source in
            // BusScheduler (Bug 5's notBefore fix lets an on-time bus
            // self-claim before ever reaching this fallback) — but once a
            // bus DOES land here, distance-weighted scoring alone still
            // doesn't reliably favor one that just parked essentially on top
            // of routeStart over one merely nearby. Bonus decays over a
            // short (5 game-minute) window so it only rewards genuinely
            // fresh idles, not any bus that happened to idle here earlier.
            if (rec.idleSinceMinutes >= 0f && SimClock.Instance != null
                && routeStart != Vector3.zero)
            {
                float idleAgeMinutes  = SimClock.Instance.AbsoluteGameMinutes - rec.idleSinceMinutes;
                float idleAtTerminalM = Vector3.Distance(rec.idlePosition, routeStart);
                const float JUST_IDLE_WINDOW_MINUTES = 5f;
                const float NEAR_TERMINAL_METERS      = 50f;
                if (idleAgeMinutes >= 0f && idleAgeMinutes <= JUST_IDLE_WINDOW_MINUTES
                    && idleAtTerminalM <= NEAR_TERMINAL_METERS)
                {
                    priority += 750f * (1f - idleAgeMinutes / JUST_IDLE_WINDOW_MINUTES);
                }
            }

            if (priority > bestPriority)
            {
                bestPriority = priority;
                bestBus = busID;
            }
        }

        if (bestBus >= 0 && _depotBoundBuses.Remove(bestBus))
        {
            _busRecords[bestBus].controller.AbortDepotIngress();
            if (logAssignments) Debug.Log($"[BusManager] Bus#{bestBus} pulled off its depot-bound drive for Route {routeNumber}.");
        }

        return bestBus;
    }

    private Vector3 GetRouteStartPosition(BusRouteData route)
    {
        if (route == null) return Vector3.zero;
        if (string.IsNullOrEmpty(route.terminalACode)) return Vector3.zero;
        var stop = CityManager.Instance?.GetStop(route.terminalACode);
        return stop != null ? stop.GetWorldPosition() : Vector3.zero;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  IDLE POOL HELPERS
    // ═════════════════════════════════════════════════════════════════════════
// Inside BusManager.cs
private HashSet<int> _reservedBuses = new HashSet<int>();

public int GetIdleBus()
{
    // Filter out buses that are already reserved for a pending assignment
    foreach (int busID in _idlePool)
    {
        if (BusScheduler.FreeAgentBusIDs.Contains(busID)) continue; // CHIP-type free agents aren't fleet spares
        if (!_reservedBuses.Contains(busID))
        {
            _reservedBuses.Add(busID); // LOCK IT
            return busID;
        }
    }
    // FIX: fall back to a depot-bound bus (retired, still driving to depot)
    // before giving up — see GetIdleBusForRoute for why this pool exists.
    foreach (int busID in _depotBoundBuses)
    {
        if (BusScheduler.FreeAgentBusIDs.Contains(busID)) continue;
        if (_reservedBuses.Contains(busID)) continue;
        _reservedBuses.Add(busID);
        _depotBoundBuses.Remove(busID);
        if (_busRecords.TryGetValue(busID, out var rec)) rec.controller.AbortDepotIngress();
        if (logAssignments) Debug.Log($"[BusManager] Bus#{busID} pulled off its depot-bound drive.");
        return busID;
    }
    return -1;
}

// Ensure you call this when the assignment is finalized or cancelled!
public void ReleaseBusReservation(int busID) => _reservedBuses.Remove(busID);

    private int GetRotatingBus()
    {
        foreach (var kv in _busRecords)
            if (kv.Value.controller.IsFlaggedForRotation && kv.Value.isActive)
                return kv.Key;
        return -1;
    }

    private IdleZone GetAvailableIdleZone(Vector3 from)
    {
        IdleZone best     = null;
        float    bestDist = float.MaxValue;
        foreach (var zone in idleZones)
        {
            if (zone.capacity > 0 && zone.currentCount >= zone.capacity) continue;
            float dist = Vector3.Distance(from, zone.position);
            if (dist < bestDist) { bestDist = dist; best = zone; }
        }
        return best;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═════════════════════════════════════════════════════════════════════════
    public BusRecord GetRecord(int busID) =>
        _busRecords.TryGetValue(busID, out var r) ? r : null;

    /// <summary>Fleet numbers (e.g. 1901) are the roster identity BusScheduler's
    /// AssignRosterOwners pre-bakes into TimetableSlot.rosterFleetNumber; busIDs
    /// are scene-instance handles. This is the bridge between the two.</summary>
public int GetBusIDByFleetNumber(int fleetNumber)
{
    foreach (var record in _busRecords.Values)
    {
        if (record.controller != null &&
            record.controller.fleetNumber == fleetNumber)
            return record.busID;
    }

    return -1;
}
    /// <summary>Called by BusScheduler when a handoff claims an idle bus as fresh relief.</summary>
    public void ClaimBusForHandoff(int busID)
    {
        if (!_busRecords.TryGetValue(busID, out var record)) return;
        _idlePool.Remove(busID);
        // isActive/isIdle are derived from State now -- the caller (a relief
        // handoff or player possession) follows this synchronously with
        // whatever actually moves State off Idle (a route assignment, or
        // disabling the component entirely for possession), all within the
        // same call chain before any other script gets a frame to observe
        // a stale read.
        if (record.idleZone != null)
        {
            record.idleZone.currentCount--;
            record.idleZone = null;
        }
        if (logAssignments)
            Debug.Log($"[BusManager] Bus#{busID} claimed for handoff — removed from idle pool.");
    }
public void RetireBusAfterHandoff(int oldBusID)
    {
        // If your player ID (-2) doesn't have a physical record, skip the dictionary check
        // Or if the physical bus DOES have a real ID, pass that real ID in.
        if (oldBusID < 0) return; 

        if (!_busRecords.TryGetValue(oldBusID, out var record)) return;

        // [NOTE] Grepped this method's callers while converting isActive/
        // isIdle to derived properties -- RetireBusAfterHandoff has none,
        // anywhere in the codebase. Left in place (out of scope for this
        // fix) but flagging: this method never actually runs. It also never
        // set record.controller.State = Idle even when it did (only the
        // bookkeeping bools), so if it's ever wired up later, whatever calls
        // it will need to also call record.controller.SetIdle(true) for the
        // now-derived isIdle to actually read true.

        // Put it back in the idle pool so it can be used later
        if (!_idlePool.Contains(oldBusID))
        {
            _idlePool.Add(oldBusID);
        }

        if (logAssignments)
            Debug.Log($"[BusManager] Old Bus#{oldBusID} retired to idle pool.");
    }

    /// <summary>Called by BusScheduler when a bus retires to depot after lap handoff.
    /// Previously this only flipped bookkeeping flags (isActive/isIdle/assignedRoute)
    /// and relied on the separately-fired OnIdleBus → HandleIdle → PullBusToIdle event
    /// to actually stop the bus and send it to a depot/idle zone. Both events fire
    /// from the same CompleteSlot() retirement branch today, so it happened to work,
    /// but retirement had no guarantee of ever physically parking the bus if that
    /// second event were ever changed, reordered, or unsubscribed. Retirement now
    /// owns its own physical outcome directly instead of depending on a second event
    /// landing correctly.</summary>
    /// <summary>[FIX] Used to call PullBusToIdle() synchronously here — but this
    /// fires from OnBusRetiredFromRoute, which OnBusScheduler.CompleteSlot invokes
    /// BEFORE NPCBusController.TerminalDwell() has even checked whether the bus
    /// has a real depot to drive to. PullBusToIdle's SetIdle(true) fallback
    /// disables renderers/collider/rigidbody right at the terminal, and
    /// TerminalDwell's subsequent StartDepotIngress() never re-enabled them —
    /// so retiring buses looked like they vanished/froze instead of driving to
    /// a depot bay. Bookkeeping only now; NPCBusController owns the physical
    /// outcome (depot ingress or fallback idle) and reports back via
    /// NotifyBusParkedAtDepot when it's actually done.</summary>
    public void RetireBusFromRoute(int busID)
    {
        if (!_busRecords.TryGetValue(busID, out var record)) return;
        // isActive is derived now -- adding to _depotBoundBuses is what
        // actually makes it read false (see BusRecord.isActive / IsDepotBound).
        _depotBoundBuses.Add(busID);
        if (logAssignments)
            Debug.Log($"[BusManager] Bus#{busID} retired from route — awaiting physical depot return.");
    }

    /// <summary>Called by NPCBusController once it has ACTUALLY come to rest —
    /// either parked in a depot bay (CompleteDepotIngress) or gone idle in
    /// place with no depot available (TerminalDwell's no-depot fallback).
    /// This is the only place a retired bus gets added back to the idle pool.</summary>
    public void NotifyBusParkedAtDepot(int busID)
    {
        if (!_busRecords.TryGetValue(busID, out var record)) return;
        // isIdle is derived now -- NPCBusController.SetIdle(true) (already
        // called right before this by every caller) is what actually makes
        // it read true.
        _depotBoundBuses.Remove(busID);
        if (!_idlePool.Contains(busID)) _idlePool.Add(busID);
        record.controller?.ClearRouteAssignment(); // [ADD] physically parked now -- stop reporting the old route
        if (logAssignments)
            Debug.Log($"[BusManager] Bus#{busID} parked and returned to idle pool.");
    }

    public int IdleBusCount => _idlePool.Count;

    public List<BusRecord> GetActiveBuses()
    {
        var result = new List<BusRecord>();
        foreach (var r in _busRecords.Values)
            if (r.isActive) result.Add(r);
        return result;
    }

    public List<BusRecord> GetAllRecords() => new List<BusRecord>(_busRecords.Values);
}