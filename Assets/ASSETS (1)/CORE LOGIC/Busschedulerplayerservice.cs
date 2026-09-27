using System;
using System.Linq;
using UnityEngine;

/// <summary>
/// v1.0 — THE SCHEDULER'S PLAYER HALF. One of the two components of the
/// rebuilt shift system (its partner is PlayerShiftDirector, the flow
/// brain). This class is the ONLY thing in the game allowed to talk to
/// BusScheduler on the player's behalf — the old system had ShiftRunner,
/// PlayerHandoff, ShiftPickerMenu AND ShiftBoardMenu all poking the
/// scheduler directly, which is where the misfires and dead-slot bugs
/// lived.
///
/// Design principle: the NPC scheduler core (chains, dispatch, constraint
/// gating) is the healthy half of the old code and is kept as the engine.
/// This service composes its EXISTING public primitives into exactly three
/// player operations, each with hard guarantees:
///
///   ReserveBlock(...)      — claim a scheduled slot + its whole remaining
///                            chain for the player. The NPC that would
///                            have run it is detached and never spawns.
///   CompleteLeg()          — routes into the same CompleteSlot path NPCs
///                            use, so lap counting / chain promotion /
///                            downstream systems see a perfectly normal
///                            service run.
///   HandBack(...)          — THE FIX for "quit early leaves a dead
///                            route": ALWAYS produces a valid outcome.
///                            First choice: a real eligible NPC takes over
///                            the remaining chain mid-rotation (same
///                            relief machinery NPC↔NPC handoffs use).
///                            Fallback: the chain is released to the free
///                            pool AND stale past-departure legs are
///                            retired so nothing zombies. Either way, no
///                            dead slots, ever.
/// </summary>
public class BusSchedulerPlayerService : MonoBehaviour
{
    public static BusSchedulerPlayerService Instance { get; private set; }

    /// <summary>Fired when HandBack successfully deploys a replacement NPC
    /// onto the player's abandoned chain. NPC-side systems that need to
    /// physically place/resume the bus (spawn at terminal, set leg state)
    /// should listen here. The slot is the leg the replacement now owns.</summary>
    public event Action<int, TimetableSlot> OnReplacementDeployed;

    /// <summary>Fired when HandBack could not find any eligible replacement
    /// and released the chain to the free pool instead. Informational —
    /// the schedule is still clean (stale legs retired), this just notes
    /// service on that rotation lapses until the dispatcher refills it.</summary>
    public event Action<string> OnChainReleasedUnserved;

    [Tooltip("Verbose logging for every reservation/handback decision.")]
    public bool logDecisions = true;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private BusScheduler Sched => BusScheduler.Instance;

    // ─────────────────────────────────────────────────────────────────────
    //  RESERVE
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Reserves the scheduled block matching (route, departure,
    /// direction) for the player. Uses TransferSlotToPlayer, which moves
    /// the ENTIRE remaining chain — the reservation mechanic: from this
    /// instant, whatever bus was going to run it is fully uninvolved (a
    /// depot-spawn bus simply never spawns; a live bus gets detached).
    /// Returns the claimed slot, or null with a logged reason.</summary>
    public TimetableSlot ReserveBlock(string routeNumber, float departureAbsMin, bool isOutbound)
    {
        if (Sched == null) { Debug.LogError("[PlayerService] BusScheduler.Instance is null."); return null; }

        // Resolve the concrete busID currently scheduled for this block.
        var entries = Sched.GetTodaysBusesForRoute(routeNumber);
        int idx = entries.FindIndex(e =>
            Mathf.Abs(e.scheduledDeparture - departureAbsMin) < 0.01f && e.isOutbound == isOutbound);
        if (idx < 0)
        {
            if (logDecisions) Debug.LogWarning($"[PlayerService] ReserveBlock: no scheduled entry for Route {routeNumber} dep={departureAbsMin:0.0} {(isOutbound ? "A→Z" : "Z→A")}.");
            return null;
        }

        // Pass the exact departure so only it (and the bus's later legs) move, not an earlier leg the bus is running.
        int ownerID = entries[idx].busID;
        TimetableSlot chosenSlot = null;
        foreach (var s in Sched.AllSlots)
            if (s.routeNumber == routeNumber && s.assignedBusID == ownerID && s.isOutbound == isOutbound
                && Mathf.Abs(s.scheduledDeparture - departureAbsMin) < 0.01f
                && (s.variantLetter ?? "") == (entries[idx].variantLetter ?? ""))
            { chosenSlot = s; break; }

        var slot = Sched.TransferSlotToPlayer(ownerID, chosenSlot);
        if (slot == null)
        {
            if (logDecisions) Debug.LogWarning($"[PlayerService] ReserveBlock: TransferSlotToPlayer failed for busID {entries[idx].busID}.");
            return null;
        }

        if (logDecisions)
            Debug.Log($"[PlayerService] Reserved Route {slot.FullRouteLabel} {slot.DirectionLabel} dep {BusScheduler.MinutesToTimeString(slot.scheduledDeparture % 1440f)} " +
                      $"(chain detached from busID {entries[idx].busID}).");
        return slot;
    }

    /// <summary>Transfers whatever slot an NPC bus currently owns to the
    /// player mid-route (Bus Select's relief-takeover flow) — same
    /// TransferSlotToPlayer primitive ReserveBlock uses, just entered by
    /// busID directly instead of a (route, departure, direction) lookup,
    /// since the bus is already running its leg rather than sitting on a
    /// pre-service scheduled entry.</summary>
    public TimetableSlot TakeOverInServiceBus(int npcBusID)
    {
        if (Sched == null) { Debug.LogError("[PlayerService] BusScheduler.Instance is null."); return null; }

        var slot = Sched.TransferSlotToPlayer(npcBusID);
        if (slot == null)
        {
            if (logDecisions) Debug.LogWarning($"[PlayerService] TakeOverInServiceBus: TransferSlotToPlayer failed for busID {npcBusID}.");
            return null;
        }

        if (logDecisions)
            Debug.Log($"[PlayerService] Took over in-service Route {slot.FullRouteLabel} {slot.DirectionLabel} from busID {npcBusID}.");
        return slot;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  LEG LIFECYCLE — thin passthroughs so lap/chain bookkeeping is
    //  byte-identical to an NPC's. No player-special state anywhere.
    // ─────────────────────────────────────────────────────────────────────

    public TimetableSlot CurrentLeg => Sched?.GetCurrentSlotForBus(BusScheduler.PLAYER_BUS_ID);
    public int LapCount => Sched != null ? Sched.GetLapCount(BusScheduler.PLAYER_BUS_ID) : 0;

    /// <summary>Marks the player as physically departed on the current leg
    /// (feeds the same lateness bookkeeping NPCs get).</summary>
    public void RecordDeparture()
    {
        if (Sched == null || SimClock.Instance == null) return;
        Sched.RecordActualDeparture(BusScheduler.PLAYER_BUS_ID, SimClock.Instance.AbsoluteGameMinutes);
    }

    /// <summary>Completes the current leg. CompleteSlot promotes the next
    /// pre-filled chain leg into "current" exactly as it does for NPCs.
    /// Returns the NEW current leg (null = chain exhausted).</summary>
    public TimetableSlot CompleteLeg()
    {
        if (Sched == null) return null;
        Sched.CompleteSlot(BusScheduler.PLAYER_BUS_ID);
        return CurrentLeg;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  HAND BACK — always a valid outcome.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Returns the player's remaining chain to the world. Call at
    /// a terminal (the flow brain enforces that — mid-road bailing isn't a
    /// thing in the new system).
    ///
    /// Guarantee: this NEVER leaves dead slots. Outcome A (preferred): an
    /// eligible NPC bus takes over the whole remaining chain via the same
    /// TryClaimNextReliefSlotForReplacement path NPC↔NPC handoffs use —
    /// constraint-gated, chain-preserving, mid-rotation. Outcome B: no
    /// candidate exists → chain released to the free pool AND any legs
    /// whose departure already passed are retired as Completed so the
    /// dispatcher can't trip on them and nothing zombies.</summary>
    public void HandBack()
    {
        if (Sched == null) return;
        var leg = CurrentLeg;
        if (leg == null)
        {
            if (logDecisions) Debug.Log("[PlayerService] HandBack: no active player leg — nothing to return.");
            return;
        }
        string routeLabel = leg.FullRouteLabel;

        // ── Outcome A: real replacement resumes the chain ────────────────
        var candidate = FindReliefCandidate();
        if (candidate != null &&
            Sched.TryClaimNextReliefSlotForReplacement(BusScheduler.PLAYER_BUS_ID, candidate.busID))
        {
            // The chain now belongs to the NPC. Give NPC-side systems the
            // explicit signal to physically resume it (place at terminal,
            // adopt leg state). SendMessage keeps this compiling without a
            // hard dependency on NPCBusController's exact resume API —
            // wire a ResumeFromHandoff(TimetableSlot) method there and
            // delete the note in that class when done.
            var newLeg = Sched.GetCurrentSlotForBus(candidate.busID);
            candidate.SendMessage("ResumeFromHandoff", newLeg, SendMessageOptions.DontRequireReceiver);
            OnReplacementDeployed?.Invoke(candidate.busID, newLeg);
            if (logDecisions)
                Debug.Log($"[PlayerService] HandBack: Bus#{candidate.busID} (fleet {candidate.fleetNumber}) " +
                          $"took over Route {routeLabel} mid-chain at leg {newLeg?.chainLegIndex ?? -1}.");
            return;
        }

        // ── Outcome B: clean release + stale-leg retirement ──────────────
        Sched.ReleasePlayerSlot(BusScheduler.PLAYER_BUS_ID);

        float now = SimClock.Instance != null ? SimClock.Instance.AbsoluteGameMinutes : 0f;
        int retired = 0;
        foreach (var s in Sched.AllSlots)
        {
            if (s.routeNumber != leg.routeNumber) continue;
            if (s.state != SlotState.Unassigned) continue;
            if (s.scheduledDeparture >= now) continue; // future legs are legitimately re-dispatchable
            s.state = SlotState.Completed;             // past-departure orphans: retire, don't zombie
            retired++;
        }

        OnChainReleasedUnserved?.Invoke(routeLabel);
        if (logDecisions)
            Debug.Log($"[PlayerService] HandBack: no replacement available — chain released to free pool, " +
                      $"{retired} stale past-departure leg(s) retired on Route {routeLabel}.");
    }

    /// <summary>Same candidacy rules the scheduler's own relief search uses:
    /// AtTerminal, Idle, or DepotIngress (pulled out of its depot drive).</summary>
    private NPCBusController FindReliefCandidate()
    {
        if (BusManager.Instance == null) return null;
        foreach (var record in BusManager.Instance.GetAllRecords())
        {
            var ctrl = record.controller as NPCBusController;
            if (ctrl == null) continue;
            if (ctrl.State != NPCBusController.BusState.AtTerminal
                && ctrl.State != NPCBusController.BusState.Idle
                && ctrl.State != NPCBusController.BusState.DepotIngress) continue;
            if (ctrl.State == NPCBusController.BusState.DepotIngress) ctrl.AbortDepotIngress();
            return ctrl;
        }
        return null;
    }
}