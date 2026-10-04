using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  NETWORK GAME BRIDGE  —  Multiplayer, rebuilt architecture (2026-09-29)
//
//  REPLACES the whole per-bus NetworkObject/BusNetworkSync/dynamic-spawn model.
//  That model was the source of nearly every multiplayer bug this project hit
//  (GlobalObjectIdHash collisions, prefab-registration mismatches, the SYSTEMS
//  GameObject accidentally getting network-ified and poisoning the whole
//  connection sync, ownership/kinematic fights). Per the user's own explicit
//  direction: "a client just reads the information of where and what
//  everything is -- ALL PREFABS are shared not cloned -- they exist for the
//  server itself as they are for host, only the actual data of playerhandoff
//  is different... it's just like the game used to be 1 player in a static
//  world, all we gotta do is multiple players in the same world."
//
//  NEW MODEL:
//  - Every process (host AND every client) spawns its OWN full local fleet at
//    scene Start(), via the SAME DepotManager.BuildAndAssignFleet()/BusSpawner
//    path single-player already uses. Nothing is torn down, nothing is
//    dynamically Instantiate()'d/Destroy()'d over the network. Since
//    FleetRosterData.BuildSlots() is driven by the same static asset data on
//    every machine, BusManager.RegisterBus's simple incrementing busID counter
//    assigns the SAME busID to "the bus for fleet #1234" on every process --
//    that's the shared, stable identity buses are matched up by, no
//    GlobalObjectIdHash/NetworkObject needed at all.
//  - ONE NetworkObject total for the whole session: a PREFAB asset (not a
//    scene-placed instance), registered and dynamically Instantiate()'d +
//    Spawn()'d once by the host right after StartHost() succeeds (see
//    LanConnectHUD.SpawnNetworkGameBridge) -- also NOT on the same GameObject
//    as NetworkManager (NGO's own Editor hard-blocks that combination).
//    Why a prefab and not scene-placed: with EnableSceneManagement disabled
//    (required so a client's independently-loaded world survives connecting
//    at all -- see LanConnectHUD's header comment), NGO has no client-side
//    scene-load event to hang its normal "match an incoming spawn to an
//    already-existing local scene object" reconciliation on, so EVERY spawned
//    object in the initial connection sync -- scene-placed or not -- gets
//    routed through the dynamic prefab-spawn path, which needs a real
//    registered prefab asset to instantiate from. Confirmed the hard way: a
//    scene-placed instance of this hit the exact same "NetworkPrefab could
//    not be found" error the stray SYSTEMS GameObject did earlier.
//  - Host periodically broadcasts every registered bus's position/rotation/
//    door state (keyed by busID) to all clients. A client applies each entry
//    directly onto its OWN already-existing local bus with that busID --
//    except the one bus THIS client itself currently possesses (skip-self,
//    so a driving client doesn't rubber-band its own input against a
//    slightly-stale echo of what it just sent).
//  - A client currently possessing a bus pushes ITS OWN live position/door
//    state to the host at the same interval; the host applies it onto its own
//    local copy of that busID (so host's own view -- and its next broadcast to
//    every OTHER client -- reflects it) and does NOT run that busID's own NPC
//    AI while a remote client is driving it (mirrors exactly what
//    BusSelectMenu's own possess flow already does for a LOCAL possession:
//    disable the NPCBusController + BusAIBrain, pull it out of
//    BusRegistry.ActiveBuses).
//  - Possession itself (which busID belongs to which player) is now tracked
//    HERE (a real registry), not inferred from NetworkObject ownership, since
//    there's no NetworkObject per bus any more. A NetworkList<int> exposes the
//    currently-taken busIDs to every client (for ClientBusPickerWindow's
//    "what's actually free" check) without needing a round trip.
//  - Every player-action RPC BusNetworkSync used to expose per-bus
//    (RequestAdoptSlot/RequestRelief/RequestCompleteSlot/RequestReleaseSlot/
//    RequestRecordActualDeparture, and the replacement-found/search-failed
//    delivery) moves here unchanged in behavior -- they never actually needed
//    a specific bus's NetworkObject, just an RPC channel and the requesting
//    clientId, both of which this ONE singleton provides just as well.
// ═══════════════════════════════════════════════════════════════════════════════
[RequireComponent(typeof(NetworkObject))]
public class NetworkGameBridge : NetworkBehaviour
{
    public static NetworkGameBridge Instance { get; private set; }

    // [FIX] Lowered from 0.15s -- a smoothing/interpolation attempt at fixing the visible
    // "blocky"/stepped motion this interval causes (snap-to-latest-sample, nothing in between)
    // broke movement entirely and got reverted (see NPCBusController.ApplyNetworkTransform's own
    // comment). This is the simpler fix instead: broadcast more often so each individual step is
    // smaller and less noticeable, without adding a new moving part. Costs more bandwidth/CPU per
    // second (roughly 3x the RPC rate) -- fine for small-scale LAN testing; revisit if it ever
    // needs to scale up.
    [Tooltip("How often (seconds) the host broadcasts bus state and a possessing client pushes its own bus's state.")]
    public float tickInterval = 0.05f;

    [Tooltip("How often (seconds) the host broadcasts schedule-slot state (who's assigned to which upcoming departure). Much slower than tickInterval -- this changes far less often than position, and the payload scales with fleet size.")]
    public float scheduleSyncInterval = 2f;

    private float _tickTimer;
    private float _scheduleSyncTimer;

    // ── Host-side possession bookkeeping, FAST lookup copies only ───────────
    // [FIX] These two plain Dictionaries used to be the ONLY place busID<->clientId was tracked --
    // which meant GetPossessingClientId/GetPossessedBusID (below) silently returned nothing on
    // every machine except the host, since a ServerRpc BODY only ever executes server-side: a
    // client's own local NetworkGameBridge instance never had these dictionaries written to at
    // all. BusTrackerService/MDT_LiveMap/PlayerRegistry all call these methods expecting them to
    // work on ANY machine -- on a client they were quietly resolving to "nobody," which is exactly
    // the "why isn't the link synced between the two" gap. Kept here purely as an O(1) lookup
    // cache for the host's OWN hot-path checks inside the ServerRpc handlers below (e.g. "is this
    // busID already taken") -- the REAL, cross-machine-readable source of truth is
    // _possessionMap (a real NetworkList) just below. Every write/remove site updates both
    // together; never read these two on their own from anything that might run on a client.
    private readonly Dictionary<int, ulong> _busToClient = new();
    private readonly Dictionary<ulong, int> _clientToBus = new();

    /// <summary>The busID<->clientId link itself, unmanaged and IEquatable so NetworkList can
    /// replicate it directly (NGO's own constraint: NetworkList&lt;T&gt; where T : unmanaged,
    /// IEquatable&lt;T&gt; -- confirmed in the installed package's NetworkList.cs). This is what
    /// actually answers "who possesses busID X" / "what does clientId Y possess" correctly on
    /// EVERY machine, not just the host -- GetPossessingClientId/GetPossessedBusID below read
    /// this, not the host-only Dictionaries above.
    /// [FIX] `unmanaged, IEquatable<T>` alone compiles but throws a REAL runtime
    /// ArgumentException the moment this NetworkList first tries to actually send a delta --
    /// "Serialization has not been generated for type... implement INetworkSerializable or mark it
    /// as serializable by memcpy by adding INetworkSerializeByMemcpy." Confirmed from the exact
    /// error text + installed package source (a pure marker interface, no methods -- exactly right
    /// for a plain-old-data struct like this one, int + ulong, no pointers). NGO's ILPP needs this
    /// explicit tag to generate the actual serialization code for a CUSTOM struct in a NetworkList
    /// -- unlike NetworkList&lt;int&gt; elsewhere in this file, which is a primitive NGO already
    /// has built-in serialization for. Another case (like the earlier string[] RPC bug) where
    /// `dotnet build` passing said nothing about NGO-specific runtime requirements.</summary>
    public struct PossessionEntry : IEquatable<PossessionEntry>, INetworkSerializeByMemcpy
    {
        public int BusID;
        public ulong ClientId;
        public bool Equals(PossessionEntry other) => BusID == other.BusID && ClientId == other.ClientId;
        public override bool Equals(object obj) => obj is PossessionEntry other && Equals(other);
        public override int GetHashCode() => System.HashCode.Combine(BusID, ClientId);
    }
    private readonly NetworkList<PossessionEntry> _possessionMap = new NetworkList<PossessionEntry>();

    /// <summary>Busids currently possessed by ANY player (host included, sentinel clientId =
    /// NetworkManager.ServerClientId = 0 for the host's own local possession). Read-everyone,
    /// written only by the server-side possess/release handlers below. Clients read this directly
    /// (no round trip) to know which buses are actually free to take.</summary>
    private readonly NetworkList<int> _possessedBusIDs = new NetworkList<int>();

    /// <summary>Records busID<->clientId in ALL FOUR tracking structures at once (the two host-only
    /// fast-lookup Dictionaries plus the real replicated _possessionMap) -- server-side only, call
    /// from inside a ServerRpc. Having one place that touches all of them is what keeps them from
    /// drifting apart the way _busToClient/_clientToBus alone already had (silently client-unreadable).</summary>
    private void RecordPossession(int busID, ulong clientId)
    {
        _busToClient[busID] = clientId;
        _clientToBus[clientId] = busID;
        for (int i = 0; i < _possessionMap.Count; i++)
            if (_possessionMap[i].BusID == busID) { _possessionMap.RemoveAt(i); break; }
        _possessionMap.Add(new PossessionEntry { BusID = busID, ClientId = clientId });
    }

    /// <summary>Reverse of RecordPossession -- removes busID from all four structures. Safe to call
    /// even if it was never recorded (no-op).</summary>
    private void ForgetPossession(int busID)
    {
        if (_busToClient.TryGetValue(busID, out var clientId))
        {
            _busToClient.Remove(busID);
            _clientToBus.Remove(clientId);
        }
        for (int i = 0; i < _possessionMap.Count; i++)
            if (_possessionMap[i].BusID == busID) { _possessionMap.RemoveAt(i); break; }
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    private void Update()
    {
        if (!IsSpawned) return;

        _tickTimer += Time.deltaTime;
        if (_tickTimer < tickInterval) return;
        _tickTimer = 0f;

        if (IsServer) { BroadcastWorldState(); CheckGraceExpirations(); }
        if (NetworkAuthority.IsPureClient) PushMyPossessedBusState();

        if (IsServer)
        {
            _scheduleSyncTimer += Time.deltaTime;
            if (_scheduleSyncTimer >= scheduleSyncInterval)
            {
                _scheduleSyncTimer = 0f;
                BroadcastScheduleState();
                BroadcastPossessedBusRoutes();
            }
        }
    }

    // ── World-state broadcast (host -> everyone) ────────────────────────────
    // [FIX] Extended with trailer transform + real light state. Both used to be computed/derived
    // LOCALLY on a receiving machine (ArticulationRig.Tick for the trailer, State/night-check
    // guessing for lights) -- but the trailer rig needs live physics data (forward speed, hitch
    // dynamics) that simply doesn't exist on a machine not locally simulating this bus, so it just
    // stayed frozen wherever it was; the light approximation was inaccurate enough the user could
    // tell it wasn't right. Same root cause as the destination-sign fix (BroadcastPossessedBus-
    // Routes): a piece of state only ever written by the AI movement-decision path that's skipped
    // on a machine not simulating this bus. Sent for EVERY bus (not just possessed ones) on the
    // SAME tick as position, since this is the exact same "every client needs this to render the
    // bus correctly" category of data -- an ordinary NPC's trailer/lights were just as broken on
    // any pure client as a possessed bus's were, this isn't possession-specific.
    private void BroadcastWorldState()
    {
        if (BusManager.Instance == null) return;

        var records = BusManager.Instance.GetAllRecords();
        int n = records.Count;
        if (n == 0) return;

        var ids = new int[n];
        var positions = new Vector3[n];
        var rotations = new Quaternion[n];
        var doors = new bool[n];
        var rearDoors = new bool[n];
        var states = new NPCBusController.BusState[n];
        var trailerPositions = new Vector3[n];
        var trailerRotations = new Quaternion[n];
        var hasTrailers = new bool[n];
        var headlightsOn = new bool[n];
        var brakeOn = new bool[n];
        var leftSignalOn = new bool[n];
        var rightSignalOn = new bool[n];
        var hazardsOn = new bool[n];
        var interiorModes = new BusInteriorLightController.InteriorLightMode[n];

        for (int i = 0; i < n; i++)
        {
            var rec = records[i];
            ids[i] = rec.busID;
            if (rec.controller != null)
            {
                positions[i] = rec.controller.transform.position;
                rotations[i] = rec.controller.transform.rotation;
                states[i] = rec.controller.State;
                var sim = rec.controller.GetComponentInChildren<BusSimulationController>(true);
                if (sim != null) { doors[i] = sim.doorsOpen; rearDoors[i] = sim.rearDoorsOpen; }

                if (rec.controller.trailerPivot != null)
                {
                    hasTrailers[i] = true;
                    trailerPositions[i] = rec.controller.trailerPivot.position;
                    trailerRotations[i] = rec.controller.trailerPivot.rotation;
                }

                headlightsOn[i]  = rec.controller.ExtHeadlightsOn;
                brakeOn[i]       = rec.controller.ExtBrakeOn;
                leftSignalOn[i]  = rec.controller.ExtLeftSignalOn;
                rightSignalOn[i] = rec.controller.ExtRightSignalOn;
                hazardsOn[i]     = rec.controller.ExtHazardsOn;
                interiorModes[i] = rec.controller.InteriorMode;
            }
        }

        WorldStateClientRpc(ids, positions, rotations, doors, rearDoors, states,
            trailerPositions, trailerRotations, hasTrailers,
            headlightsOn, brakeOn, leftSignalOn, rightSignalOn, hazardsOn, interiorModes);
    }

    [ClientRpc]
    private void WorldStateClientRpc(int[] busIDs, Vector3[] positions, Quaternion[] rotations, bool[] doors, bool[] rearDoors, NPCBusController.BusState[] states,
        Vector3[] trailerPositions, Quaternion[] trailerRotations, bool[] hasTrailers,
        bool[] headlightsOn, bool[] brakeOn, bool[] leftSignalOn, bool[] rightSignalOn, bool[] hazardsOn, BusInteriorLightController.InteriorLightMode[] interiorModes)
    {
        if (IsServer) return; // host already has authoritative local state, nothing to apply onto itself
        if (BusManager.Instance == null) return;

        int myPhysicalBusID = PlayerHandoff.Instance != null ? PlayerHandoff.Instance.PossessedPhysicalBusID : -1;

        for (int i = 0; i < busIDs.Length; i++)
        {
            if (busIDs[i] == myPhysicalBusID) continue; // skip-self -- see class header comment

            var rec = BusManager.Instance.GetRecord(busIDs[i]);
            if (rec?.controller == null) continue;

            rec.controller.ApplyNetworkTransform(positions[i], rotations[i]);
            // [ADD] A client never runs this bus's own AI/state machine locally (BusUpdateManager
            // is fully gated off for a pure client), so State was frozen at whatever it was when
            // the client connected -- this is what fixed "a depot-ingress bus still shows its old
            // route color forever" on a client's live map. See ApplyNetworkState's own comment.
            rec.controller.ApplyNetworkState(states[i]);
            var sim = rec.controller.GetComponentInChildren<BusSimulationController>(true);
            if (sim != null) { sim.doorsOpen = doors[i]; sim.rearDoorsOpen = rearDoors[i]; }
            // [FIX] Interior lights forcing full-bright while a door is open (BusInteriorLight-
            // Controller.SetDoorOpen) was only ever triggered by this bus's own LOCAL door-toggle
            // code -- skipped here for the exact same reason everything else in this method is.
            // Door open/closed state is already right here in the broadcast; just derive it.
            rec.controller.SetIntDoorOpenAll(doors[i] || rearDoors[i]);

            if (hasTrailers[i] && rec.controller.trailerPivot != null)
                rec.controller.trailerPivot.SetPositionAndRotation(trailerPositions[i], trailerRotations[i]);

            rec.controller.ApplyNetworkLights(headlightsOn[i], brakeOn[i], leftSignalOn[i], rightSignalOn[i], hazardsOn[i], interiorModes[i]);
        }
    }

    // ── Schedule-slot state (host -> everyone) ──────────────────────────────
    //  Fixes: a client's own local BusScheduler._allSlots is generated once at its own Start()
    //  and NEVER updated again afterward -- SchedulerTick, which makes all the real dispatch/
    //  assignment/relief/retirement decisions, is host-only (NetworkAuthority.ShouldSimulate). So
    //  BusTrackerService's non-live ("Section 3") arrival predictions -- and anything else reading
    //  a slot's state/assignedBusID on a client -- were reading a permanently stale snapshot,
    //  completely disconnected from what's actually happening on the host. Confirmed by repro:
    //  the SAME route/stop's upcoming arrivals showed completely different (and wrong) fleet
    //  numbers on host vs. client.
    //
    //  Only slots that are actually INTERESTING (someone's been assigned to them, one way or
    //  another) are sent -- Unassigned/Completed slots need no correction, since
    //  PreAssignBusesForRouteDay's deterministic StableHash shuffle already guarantees identical
    //  initial content on every process (confirmed via that method's own comment: "same input,
    //  same output, every run"); it's only the LIVE assignment/state changes that ever diverge.
    //  This keeps the payload bounded by roughly fleet size, not total slot count (which can be
    //  orders of magnitude larger across the whole multi-day scheduling horizon).
    // [FIX] NGO's RPC codegen (NetworkBehaviourILPP) rejects string[] outright -- "Don't know how
    // to serialize System.String[]" -- confirmed the hard way, this only surfaces during Unity's
    // own IL post-processing build step, which `dotnet build` never runs, so it compiled clean
    // there while failing Unity's actual build every time. A single `string` IS supported
    // (FastBufferWriter.WriteValueSafe has a real string overload, confirmed earlier this
    // session), so route/variant strings are packed into ONE joined "legend" string instead of an
    // array -- each unique (routeNumber, variantLetter) pair appears once, and every slot entry
    // just carries an int INDEX into it. Route numbers repeat heavily across a day's slots for the
    // same route, so this is also smaller on the wire than sending the string per-slot would be.
    private const char LegendEntrySeparator = '\u0002';
    private const char LegendFieldSeparator = '\u0001';

    private void BroadcastScheduleState()
    {
        if (BusScheduler.Instance == null) return;
        var allSlots = BusScheduler.Instance.AllSlots;

        var legend = new List<string>();
        var legendLookup = new Dictionary<(string route, string variant), int>();
        var routeVariantIndex = new List<int>();
        var isOutbounds = new List<bool>();
        var scheduledDepartures = new List<float>();
        var states = new List<SlotState>();
        var assignedBusIDs = new List<int>();

        foreach (var slot in allSlots)
        {
            if (slot.state == SlotState.Unassigned || slot.state == SlotState.Completed) continue;

            var routeKey = (slot.routeNumber, slot.variantLetter ?? "");
            if (!legendLookup.TryGetValue(routeKey, out int idx))
            {
                idx = legend.Count;
                legendLookup[routeKey] = idx;
                legend.Add(routeKey.Item1 + LegendFieldSeparator + routeKey.Item2);
            }

            routeVariantIndex.Add(idx);
            isOutbounds.Add(slot.isOutbound);
            scheduledDepartures.Add(slot.scheduledDeparture);
            states.Add(slot.state);
            assignedBusIDs.Add(slot.assignedBusID);
        }

        if (routeVariantIndex.Count == 0) return;
        string legendPacked = string.Join(LegendEntrySeparator.ToString(), legend);
        ScheduleStateClientRpc(legendPacked, routeVariantIndex.ToArray(), isOutbounds.ToArray(),
                               scheduledDepartures.ToArray(), states.ToArray(), assignedBusIDs.ToArray());
    }

    [ClientRpc]
    private void ScheduleStateClientRpc(string legendPacked, int[] routeVariantIndex, bool[] isOutbounds,
                                         float[] scheduledDepartures, SlotState[] states, int[] assignedBusIDs)
    {
        if (IsServer) return; // host already has its own real, authoritative data
        if (BusScheduler.Instance == null) return;
        var allSlots = BusScheduler.Instance.AllSlots;

        var legend = legendPacked.Split(LegendEntrySeparator);

        // Keyed by the same fields the deterministic generation guarantees are identical across
        // processes, so a match should exist for every entry the host sent.
        var byKey = new Dictionary<(string route, string variant, bool outbound, float dep), TimetableSlot>();
        foreach (var slot in allSlots)
        {
            var key = (slot.routeNumber, slot.variantLetter ?? "", slot.isOutbound, slot.scheduledDeparture);
            byKey[key] = slot; // these keys should already be unique -- last-write-wins is just a safety net
        }

        for (int i = 0; i < routeVariantIndex.Length; i++)
        {
            var fields = legend[routeVariantIndex[i]].Split(LegendFieldSeparator);
            string routeNumber = fields[0];
            string variantLetter = fields.Length > 1 ? fields[1] : "";

            var key = (routeNumber, variantLetter, isOutbounds[i], scheduledDepartures[i]);
            if (byKey.TryGetValue(key, out var slot))
            {
                slot.state = states[i];
                slot.assignedBusID = assignedBusIDs[i];
            }
        }
    }

    // ── Possessed-bus route info (host -> everyone), for destination signs ──
    //  A remote-possessed bus's CurrentRoute/variantLetter/IsOutbound (what BusDestinationBoard
    //  reads via NPCBusController.CurrentRoute) is a display-only field only ever WRITTEN by AI
    //  dispatch logic (host-only, gated -- see NetworkAuthority) or the player's own local
    //  route-pick flow (which never runs on a machine that isn't the driver). So a possessed
    //  bus's destination sign, on every machine except the one actually driving it, stayed frozen
    //  at whatever route it was running AS AN NPC right before someone took it over -- never
    //  updated to the player's real pick. Only the handful of currently-possessed buses are sent
    //  (bounded, not the whole ~400-bus fleet), on the same slower cadence as schedule-state sync
    //  since a route doesn't change every tick the way position does. Same packed-string "legend"
    //  trick as BroadcastScheduleState -- avoids the string[] NGO ILPP RPC-serialization bug (see
    //  that method's own header comment) by packing "routeNumber\u0001variantLetter" once per
    //  possessed bus into ONE joined string instead of a string array.
    private const char RouteEntrySeparator = '\u0002';
    private const char RouteFieldSeparator = '\u0001';

    private void BroadcastPossessedBusRoutes()
    {
        if (BusManager.Instance == null) return;
        var busIDs = new List<int>();
        var entries = new List<string>();
        var isOutbounds = new List<bool>();

        foreach (var busID in _possessedBusIDs)
        {
            var rec = BusManager.Instance.GetRecord(busID);
            if (rec?.controller == null) continue;
            busIDs.Add(busID);
            string routeNumber = rec.controller.CurrentRoute?.routeNumber ?? "";
            string variantLetter = rec.controller.variantLetter ?? "";
            entries.Add(routeNumber + RouteFieldSeparator + variantLetter);
            isOutbounds.Add(rec.controller.IsOutbound);
        }

        if (busIDs.Count == 0) return;
        string packed = string.Join(RouteEntrySeparator.ToString(), entries);
        PossessedRoutesClientRpc(busIDs.ToArray(), packed, isOutbounds.ToArray());
    }

    [ClientRpc]
    private void PossessedRoutesClientRpc(int[] busIDs, string packed, bool[] isOutbounds)
    {
        if (IsServer) return;
        if (BusManager.Instance == null) return;

        var entries = packed.Split(RouteEntrySeparator);
        for (int i = 0; i < busIDs.Length; i++)
        {
            var rec = BusManager.Instance.GetRecord(busIDs[i]);
            if (rec?.controller == null) continue;

            var fields = entries[i].Split(RouteFieldSeparator);
            string routeNumber = fields.Length > 0 ? fields[0] : "";
            string variantLetter = fields.Length > 1 ? fields[1] : "";
            rec.controller.ApplyNetworkRouteInfo(routeNumber, variantLetter, isOutbounds[i]);
        }
    }

    // ── A possessing client's own live state -> host ────────────────────────
    // [FIX] THE actual reason lights only ever worked in one direction: this push carried
    // position/rotation/doors but never the driver's REAL light state, so the HOST's own local
    // copy of a CLIENT-possessed bus never learned about it -- its _extLights/_intLightsAll just
    // sat at their untouched defaults forever. BroadcastWorldState then read THAT (wrong, always-
    // off) data straight off the host's copy and broadcast it out to everyone, INCLUDING back to
    // the driving client's own bus -- except the driving client's own screen never applies that
    // broadcast onto its own bus (skip-self, see WorldStateClientRpc), so the driver never noticed
    // anything was wrong; every OTHER machine (the host itself, and any third client) saw the
    // wrong data. Exact match for "only the client can see it, not the host." Now sends the
    // driver's real values (read via the same accessors BroadcastWorldState itself uses -- these
    // work fine even though this bus's own NPCBusController is disabled on the DRIVING machine
    // for local possession, since _extLights/_intLightsAll are separate, still-enabled
    // MonoBehaviours read through by those accessors regardless).
    private void PushMyPossessedBusState()
    {
        var ph = PlayerHandoff.Instance;
        if (ph == null || ph.playerBus == null) return;
        int busID = ph.PossessedPhysicalBusID;
        if (busID < 0) return;

        var t = ph.playerBus.transform;
        var ctrl = BusManager.Instance?.GetRecord(busID)?.controller;
        bool headlightsOn  = ctrl != null && ctrl.ExtHeadlightsOn;
        bool brakeOn       = ctrl != null && ctrl.ExtBrakeOn;
        bool leftSignalOn  = ctrl != null && ctrl.ExtLeftSignalOn;
        bool rightSignalOn = ctrl != null && ctrl.ExtRightSignalOn;
        bool hazardsOn     = ctrl != null && ctrl.ExtHazardsOn;
        var interiorMode   = ctrl != null ? ctrl.InteriorMode : BusInteriorLightController.InteriorLightMode.Off;

        // [FIX] Same one-directional gap as lights, just missed the first time -- the driving
        // client's REAL articulated trailer position is simulated by BusSimulationController's own
        // hitch physics (PLAYER/BusController.cs), completely separate from NPCBusController's
        // ArticulationRig (which never runs for a possessed bus on ANY machine, including the
        // driver's own -- NPCBusController is disabled the moment local possession starts). The
        // earlier trailer fix only ever taught BroadcastWorldState (host -> clients) to read/send
        // it -- for a CLIENT-driven bus, the host's own local copy never learns the real trailer
        // pose at all unless the driving client pushes it here too, same as lights.
        bool hasTrailer = ph.playerBus.trailerPivot != null;
        Vector3 trailerPos = hasTrailer ? ph.playerBus.trailerPivot.position : Vector3.zero;
        Quaternion trailerRot = hasTrailer ? ph.playerBus.trailerPivot.rotation : Quaternion.identity;

        PushBusStateServerRpc(busID, t.position, t.rotation, ph.playerBus.doorsOpen, ph.playerBus.rearDoorsOpen,
            headlightsOn, brakeOn, leftSignalOn, rightSignalOn, hazardsOn, interiorMode,
            hasTrailer, trailerPos, trailerRot);
    }

    [ServerRpc(RequireOwnership = false)]
    private void PushBusStateServerRpc(int busID, Vector3 position, Quaternion rotation, bool doorsOpen, bool rearDoorsOpen,
        bool headlightsOn, bool brakeOn, bool leftSignalOn, bool rightSignalOn, bool hazardsOn, BusInteriorLightController.InteriorLightMode interiorMode,
        bool hasTrailer, Vector3 trailerPos, Quaternion trailerRot)
    {
        var rec = BusManager.Instance?.GetRecord(busID);
        if (rec?.controller == null) return;
        // [FIX] Same smoothing fix as WorldStateClientRpc above -- the host's own view of a
        // remote client's possessed bus was snapping too.
        rec.controller.ApplyNetworkTransform(position, rotation);
        var sim = rec.controller.GetComponentInChildren<BusSimulationController>(true);
        if (sim != null) { sim.doorsOpen = doorsOpen; sim.rearDoorsOpen = rearDoorsOpen; }
        rec.controller.SetIntDoorOpenAll(doorsOpen || rearDoorsOpen);
        rec.controller.ApplyNetworkLights(headlightsOn, brakeOn, leftSignalOn, rightSignalOn, hazardsOn, interiorMode);
        if (hasTrailer && rec.controller.trailerPivot != null)
            rec.controller.trailerPivot.SetPositionAndRotation(trailerPos, trailerRot);
    }

    // ── Possession (which busID belongs to which player) ────────────────────
    /// <summary>Call whenever this client takes a bus (ClientBusPickerWindow's Free Drive grab,
    /// or the normal route-join/adopt-slot flows once those confirm success). onResult(true) means
    /// the host accepted -- go ahead and possess it locally; onResult(false) means someone else
    /// already had it (a race with another client), don't.</summary>
    public void RequestPossessBus(int busID, Action<bool> onResult)
    {
        if (!IsSpawned) { onResult?.Invoke(true); return; } // single-player/no session -- nothing to arbitrate
        _pendingPossessCallback = onResult;
        RequestPossessBusServerRpc(NetworkManager.Singleton.LocalClientId, busID);
    }
    private Action<bool> _pendingPossessCallback;

    [ServerRpc(RequireOwnership = false)]
    private void RequestPossessBusServerRpc(ulong requestingClientId, int busID)
    {
        // A grace-held bus is reserved for whoever disconnected, not up for grabs via the normal
        // path -- RequestReclaimBus is how it comes back.
        bool ok = !_busToClient.ContainsKey(busID) && !_graceHeldBusIDs.Contains(busID);
        if (ok)
        {
            // A client might already hold a different bus (shouldn't normally happen -- the UI
            // only offers a take when playerBus is null -- but guard against a stale double-call).
            ReleaseInternal(requestingClientId);

            RecordPossession(busID, requestingClientId);
            if (!_possessedBusIDs.Contains(busID)) _possessedBusIDs.Add(busID);

            // [FIX] This used to also set rec.controller.enabled = false (fully disabling the
            // HOST's own local NPCBusController for this bus) to stop the host's NPC AI from
            // fighting the remote client's pushed position every tick. That worked for movement,
            // but disabling the component unregisters it from BusUpdateManager entirely (OnDisable
            // -> Unregister) -- so ManagedTick (LOD/renderer-cull/audio-cull) never ran for it
            // again either, freezing its cull state forever and making it invisible/silent on the
            // host no matter what IsNetworkPossessed exemption logic existed, since that logic
            // never got a chance to run. AI movement is now suppressed narrowly instead --
            // BusUpdateManager.FixedUpdate skips ManagedFixedTick for any IsNetworkPossessed bus
            // directly -- so the component can stay enabled and keep ticking its visual/audio
            // upkeep on every machine, including this one, even though this machine isn't driving
            // it. BusAIBrain is different: it runs its own independent Update() with no gating
            // anywhere else (mood/personality/micro-events), so it still needs an explicit
            // disable regardless of local vs. remote possession. Re-enabled by ReleaseInternal
            // below on release/disconnect.
            var rec = BusManager.Instance?.GetRecord(busID);
            if (rec?.controller != null)
            {
                var brain = rec.controller.GetComponent<BusAIBrain>();
                if (brain != null) brain.enabled = false;
                BusRegistry.ActiveBuses.Remove(busID);
            }
        }

        var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { requestingClientId } } };
        PossessResultClientRpc(ok, rpcParams);
    }

    [ClientRpc]
    private void PossessResultClientRpc(bool success, ClientRpcParams rpcParams = default)
    {
        var cb = _pendingPossessCallback;
        _pendingPossessCallback = null;
        cb?.Invoke(success);
    }

    /// <summary>Call on shift-end/disconnect/handoff-away so the bus goes back to normal NPC AI
    /// for everyone (host included) and other clients see it as available again.</summary>
    public void RequestReleaseBus(int busID)
    {
        if (!IsSpawned) return;
        RequestReleaseBusServerRpc(NetworkManager.Singleton.LocalClientId, busID);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestReleaseBusServerRpc(ulong requestingClientId, int busID)
    {
        if (_clientToBus.TryGetValue(requestingClientId, out var owned) && owned == busID)
            ReleaseInternal(requestingClientId);
    }

    private void ReleaseInternal(ulong clientId)
    {
        if (!_clientToBus.TryGetValue(clientId, out var busID)) return;
        ForgetPossession(busID);
        _possessedBusIDs.Remove(busID);
        ReenableAI(busID);
    }

    /// <summary>Shared by ReleaseInternal (voluntary release) and grace-period expiry -- hands a
    /// bus back to normal NPC AI, mirroring BusSelectMenu's own local-possession recipe in
    /// reverse.</summary>
    private void ReenableAI(int busID)
    {
        var rec = BusManager.Instance?.GetRecord(busID);
        if (rec?.controller == null) return;
        rec.controller.enabled = true;
        var brain = rec.controller.GetComponent<BusAIBrain>();
        if (brain != null) brain.enabled = true;
        BusRegistry.ActiveBuses[busID] = rec.controller;
    }

    [Tooltip("How long (seconds) a disconnected client's bus stays reserved for them before it goes back to normal NPC AI. A brief network drop or app restart within this window lets them reclaim the SAME bus instead of losing it.")]
    public float disconnectGraceSeconds = 90f;

    /// <summary>Busids currently held in a post-disconnect grace window -- still excluded from
    /// ClientBusPickerWindow's normal "available" list (they're not free to grab), but offered
    /// there separately as "reclaim your bus" until the grace period expires. Real NetworkList,
    /// same reasoning as _possessedBusIDs above.</summary>
    private readonly NetworkList<int> _graceHeldBusIDs = new NetworkList<int>();

    // Host-side only -- real-time deadline (Time.time) per grace-held busID, checked in Update().
    private readonly Dictionary<int, float> _graceDeadline = new();

    /// <summary>Host-side disconnect handler -- a client that drops (crash, network loss, app
    /// closed) doesn't immediately lose its bus to NPC AI any more. Instead the bus moves into a
    /// grace window: still excluded from AI and from the normal "available" pool, but reclaimable
    /// by whoever reconnects and picks it via RequestReclaimBus. Only actually released back to AI
    /// if nobody reclaims it before disconnectGraceSeconds elapses (see Update's grace-expiry
    /// check). Hook this from NetworkManager.OnClientDisconnectCallback (see LanConnectHUD).</summary>
    public void ReleaseOnDisconnect(ulong clientId)
    {
        if (!IsServer) return;
        if (!_clientToBus.TryGetValue(clientId, out var busID)) return;
        // The busID<->clientId LINK is forgotten (that clientId is gone, its id will be reused by
        // NGO for a future connection) -- but _possessedBusIDs is deliberately left untouched, the
        // bus still reads as "taken" everywhere except the new grace-reclaim list below, and its
        // NPC AI stays disabled.
        ForgetPossession(busID);
        if (!_graceHeldBusIDs.Contains(busID)) _graceHeldBusIDs.Add(busID);
        _graceDeadline[busID] = Time.time + disconnectGraceSeconds;
    }

    private void CheckGraceExpirations()
    {
        if (_graceDeadline.Count == 0) return;
        float now = Time.time;
        List<int> expired = null;
        foreach (var kv in _graceDeadline)
            if (now >= kv.Value) (expired ??= new List<int>()).Add(kv.Key);
        if (expired == null) return;

        foreach (var busID in expired)
        {
            _graceDeadline.Remove(busID);
            _graceHeldBusIDs.Remove(busID);
            _possessedBusIDs.Remove(busID);
            ReenableAI(busID);
        }
    }

    /// <summary>Call from a reconnected client's ClientBusPickerWindow to reclaim a bus still
    /// sitting in its post-disconnect grace window. onResult(false) means the grace period already
    /// expired (or someone else claimed it) -- fall back to picking a normal available bus.</summary>
    public void RequestReclaimBus(int busID, Action<bool> onResult)
    {
        if (!IsSpawned) { onResult?.Invoke(false); return; }
        _pendingReclaimCallback = onResult;
        RequestReclaimBusServerRpc(NetworkManager.Singleton.LocalClientId, busID);
    }
    private Action<bool> _pendingReclaimCallback;

    [ServerRpc(RequireOwnership = false)]
    private void RequestReclaimBusServerRpc(ulong requestingClientId, int busID)
    {
        bool ok = _graceHeldBusIDs.Contains(busID) && !_busToClient.ContainsKey(busID);
        if (ok)
        {
            _graceDeadline.Remove(busID);
            _graceHeldBusIDs.Remove(busID);
            RecordPossession(busID, requestingClientId);
            // Bus's AI was never re-enabled during grace -- nothing more to do here.
        }

        var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { requestingClientId } } };
        ReclaimResultClientRpc(ok, rpcParams);
    }

    [ClientRpc]
    private void ReclaimResultClientRpc(bool success, ClientRpcParams rpcParams = default)
    {
        var cb = _pendingReclaimCallback;
        _pendingReclaimCallback = null;
        cb?.Invoke(success);
    }

    /// <summary>Which physical busID (if any) does this clientId currently possess? -1 if none.
    /// Used for display (e.g. resolving a per-client slot's fleet number). [FIX] Used to read the
    /// host-only _clientToBus Dictionary -- silently always "-1, nobody" on any machine except the
    /// host, since that Dictionary is only ever written inside a ServerRpc BODY (server-only
    /// execution). Now reads _possessionMap, a real NetworkList, so this actually works on every
    /// machine -- the fix for "the link gotta be synced between the two."</summary>
    public int GetPossessedBusID(ulong clientId)
    {
        for (int i = 0; i < _possessionMap.Count; i++)
            if (_possessionMap[i].ClientId == clientId) return _possessionMap[i].BusID;
        return -1;
    }

    /// <summary>Reverse of GetPossessedBusID -- which clientId currently possesses this physical
    /// busID? Null if nobody does. Used to resolve a possessed bus's actual scheduler-sentinel
    /// identity (BusScheduler.PLAYER_BUS_ID-equivalent) for whoever holds it, e.g. so
    /// BusTrackerService's arrival predictions can look up a remote player's real live slot
    /// instead of treating it as either an NPC or missing entirely. Same _possessionMap fix as
    /// GetPossessedBusID above -- this must work on every machine, not just the host.</summary>
    public ulong? GetPossessingClientId(int busID)
    {
        for (int i = 0; i < _possessionMap.Count; i++)
            if (_possessionMap[i].BusID == busID) return _possessionMap[i].ClientId;
        return null;
    }

    /// <summary>Converts a clientId (as tracked by _busToClient/_clientToBus above) into the
    /// scheduler-sentinel identity BusScheduler.PLAYER_BUS_ID would resolve to for that same
    /// client -- -2 for the host's own local play (NetworkManager.ServerClientId), or the
    /// -1000-based per-client encoding otherwise (matching BeginPlayerContext's own scheme
    /// exactly). Lets a caller that only has a physical busID + GetPossessingClientId's result
    /// look up that player's real live TimetableSlot via BusScheduler.TryGetAssignedSlot without
    /// needing Unity.Netcode's NetworkManager type at all.</summary>
    public static int ClientIdToSentinel(ulong clientId)
        => clientId == NetworkManager.ServerClientId ? -2 : (int)(-1000L - (long)clientId);

    /// <summary>Reverse of ClientIdToSentinel above -- given a sentinel (-2 for the host's own
    /// local play, or a per-client one), returns the clientId it actually belongs to. -2 decodes
    /// to NetworkManager.ServerClientId (0) rather than DecodeClientId's -1000-based formula,
    /// since -2 is BusScheduler.PLAYER_BUS_ID's plain default value, not part of that encoding.</summary>
    public static ulong SentinelToClientId(int sentinel)
        => sentinel == -2 ? NetworkManager.ServerClientId : BusScheduler.DecodeClientId(sentinel);

    /// <summary>Client-side read: is this busID in a post-disconnect grace window, reclaimable by
    /// whoever picks it? Backs ClientBusPickerWindow's "reclaim your bus" section.</summary>
    public bool IsInGrace(int busID) => _graceHeldBusIDs.Contains(busID);

    /// <summary>Client-side read: every busID currently reclaimable. Snapshotted into a plain list
    /// since NetworkList isn't safe to hold a live enumerator over across frames.</summary>
    public List<int> GetGraceHeldBusIDs()
    {
        var list = new List<int>(_graceHeldBusIDs.Count);
        foreach (var id in _graceHeldBusIDs) list.Add(id);
        return list;
    }

    /// <summary>Client-side read: is this busID already taken by someone (possessed OR still in
    /// its post-disconnect grace window)? Backs ClientBusPickerWindow's "what's actually free"
    /// filter with no round trip needed -- _possessedBusIDs is a real NetworkList, kept in sync
    /// automatically.</summary>
    public bool IsPossessed(int busID) => _possessedBusIDs.Contains(busID);

    /// <summary>Client-side read: every busID currently possessed by ANY player, host included.
    /// Backs MDT_LiveMap's "draw a chip for every other player's bus" -- a possessed bus is
    /// removed from BusRegistry.ActiveBuses (see RequestPossessBusServerRpc's own comment on why),
    /// so it drops out of the map's normal NPC-chip loop entirely; this is how the map finds it
    /// again. Snapshotted into a plain list for the same reason as GetGraceHeldBusIDs above.</summary>
    public List<int> GetPossessedBusIDs()
    {
        var list = new List<int>(_possessedBusIDs.Count);
        foreach (var id in _possessedBusIDs) list.Add(id);
        return list;
    }

    // ── Player-action RPCs (moved here from the old per-bus BusNetworkSync --
    // none of these ever actually needed a specific bus's NetworkObject, just
    // an RPC channel and the requesting clientId) ───────────────────────────

    private Action<bool, string, string, bool, float> _pendingAdoptCallback;

    /// <summary>See BusNetworkSync's original doc comment (removed) -- unchanged behavior, just
    /// relocated. npcBusID is the physical bus whose in-service slot is being adopted.</summary>
    public void RequestAdoptSlot(int npcBusID, Action<bool, string, string, bool, float> onResult)
    {
        if (!IsSpawned) { onResult?.Invoke(false, "", "", true, 0f); return; }
        _pendingAdoptCallback = onResult;
        RequestAdoptSlotServerRpc(NetworkManager.Singleton.LocalClientId, npcBusID);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestAdoptSlotServerRpc(ulong requestingClientId, int npcBusID)
    {
        bool success = false;
        string routeNumber = "";
        string variantLetter = "";
        bool isOutbound = true;
        float scheduledDeparture = 0f;

        if (BusScheduler.Instance != null)
        {
            using (BusScheduler.BeginPlayerContext(requestingClientId))
            {
                var slot = BusScheduler.Instance.TransferSlotToPlayer(npcBusID);
                if (slot != null)
                {
                    success = true;
                    routeNumber = slot.routeNumber;
                    variantLetter = slot.variantLetter ?? "";
                    isOutbound = slot.isOutbound;
                    scheduledDeparture = slot.scheduledDeparture;
                }
            }
        }

        if (success) RequestPossessBusServerRpcInternal(requestingClientId, npcBusID);

        var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { requestingClientId } } };
        AdoptSlotResultClientRpc(success, routeNumber, variantLetter, isOutbound, scheduledDeparture, rpcParams);
    }

    /// <summary>Shared possession bookkeeping for the adopt-slot path -- same effect as
    /// RequestPossessBusServerRpc but without the accept/reject round trip (the slot transfer
    /// above already IS the authority check; this bus was already known-idle/known-in-service-
    /// under-an-NPC, never contested the way a Free Drive pick could be).</summary>
    private void RequestPossessBusServerRpcInternal(ulong requestingClientId, int busID)
    {
        ReleaseInternal(requestingClientId);
        RecordPossession(busID, requestingClientId);
        if (!_possessedBusIDs.Contains(busID)) _possessedBusIDs.Add(busID);
        // [FIX] Same change as RequestPossessBusServerRpc's identical block -- see its comment.
        // Don't disable rec.controller here either; it would unregister from BusUpdateManager and
        // freeze this bus's visual/audio cull state forever. AI movement is now suppressed via
        // BusUpdateManager.FixedUpdate's IsNetworkPossessed check instead.
        var rec = BusManager.Instance?.GetRecord(busID);
        if (rec?.controller != null)
        {
            var brain = rec.controller.GetComponent<BusAIBrain>();
            if (brain != null) brain.enabled = false;
            BusRegistry.ActiveBuses.Remove(busID);
        }
    }

    [ClientRpc]
    private void AdoptSlotResultClientRpc(bool success, string routeNumber, string variantLetter, bool isOutbound, float scheduledDeparture, ClientRpcParams rpcParams = default)
    {
        var callback = _pendingAdoptCallback;
        _pendingAdoptCallback = null;
        callback?.Invoke(success, routeNumber, variantLetter, isOutbound, scheduledDeparture);
    }

    // ── Route-board "join a route" reservation ──────────────────────────────
    //  PlayerHandoff.JoinRoute/JoinRouteWithSlot (the console "join" command AND
    //  BusSelectMenu's board picker) used to call BusScheduler.Instance.
    //  ReservePlayerSlot/ReservePlayerSlotSpecific DIRECTLY -- on a network client
    //  that hits the client's own local, never-ticking BusScheduler.Instance, so
    //  the reservation was completely fake: the host never learned about it, and
    //  two players could "both" claim the same departure with neither ever
    //  finding out. This bridges it the same way RequestAdoptSlot already does.
    //
    //  Deliberately ignores whatever specific TimetableSlot the client's own
    //  (potentially stale/desynced) local schedule displayed -- that object
    //  means nothing to the host. Only routeNumber/isOutbound cross the wire;
    //  the host resolves the actual slot itself via ReservePlayerSlot, which
    //  already only ever returns a genuinely Unassigned slot (PeekUpcomingSlots).
    //  If the exact departure the client saw got taken by someone else in the
    //  meantime, this naturally returns the NEXT free one on the same
    //  route/direction instead of failing outright -- exactly the "go to the
    //  next slot" behavior wanted, for free, with no extra retry logic needed.
    private Action<bool, string, string, bool, float> _pendingReserveCallback;

    public void RequestReserveSlot(string routeNumber, bool isOutbound, Action<bool, string, string, bool, float> onResult)
    {
        if (!IsSpawned) { onResult?.Invoke(false, "", "", true, 0f); return; }
        _pendingReserveCallback = onResult;
        RequestReserveSlotServerRpc(NetworkManager.Singleton.LocalClientId, routeNumber, isOutbound);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestReserveSlotServerRpc(ulong requestingClientId, string routeNumber, bool isOutbound)
    {
        bool success = false;
        string variantLetter = "";
        float scheduledDeparture = 0f;

        if (BusScheduler.Instance != null)
        {
            using (BusScheduler.BeginPlayerContext(requestingClientId))
            {
                var slot = BusScheduler.Instance.ReservePlayerSlot(routeNumber, isOutbound);
                if (slot != null)
                {
                    success = true;
                    variantLetter = slot.variantLetter ?? "";
                    scheduledDeparture = slot.scheduledDeparture;
                }
            }
        }

        var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { requestingClientId } } };
        ReserveSlotResultClientRpc(success, routeNumber, variantLetter, isOutbound, scheduledDeparture, rpcParams);
    }

    [ClientRpc]
    private void ReserveSlotResultClientRpc(bool success, string routeNumber, string variantLetter, bool isOutbound, float scheduledDeparture, ClientRpcParams rpcParams = default)
    {
        var callback = _pendingReserveCallback;
        _pendingReserveCallback = null;
        callback?.Invoke(success, routeNumber, variantLetter, isOutbound, scheduledDeparture);
    }

    // ── "Continue" the player's pre-filled next chain leg ───────────────────
    //  PlayerHandoff.ContinueAssignedChain used to call BusScheduler.Instance.
    //  TryGetAssignedSlot(PlayerBusID, ...) DIRECTLY -- on a network client that
    //  reads the client's own local _slotByBus dictionary, which is NEVER the
    //  one CompleteSlot/RequestRelief promote a next leg into (that happens on
    //  the HOST's scheduler, under this client's real -1000-based sentinel via
    //  BeginPlayerContext -- see RequestCompleteSlotServerRpc above). The
    //  periodic ScheduleStateClientRpc broadcast keeps a client's AllSlots
    //  fields in sync, but _slotByBus is a separate busID->slot lookup that
    //  broadcast never touches, so a client's local TryGetAssignedSlot call
    //  could only ever see stale/wrong data (or nothing at all). Bridges it
    //  the same way RequestReserveSlot/RequestAdoptSlot already do: ask the
    //  host, which has the one _slotByBus that's ever actually authoritative.
    private Action<bool, string, string, bool, float> _pendingContinueChainCallback;

    public void RequestContinueChain(Action<bool, string, string, bool, float> onResult)
    {
        if (!IsSpawned) { onResult?.Invoke(false, "", "", true, 0f); return; }
        _pendingContinueChainCallback = onResult;
        RequestContinueChainServerRpc(NetworkManager.Singleton.LocalClientId);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestContinueChainServerRpc(ulong requestingClientId)
    {
        bool success = false;
        string routeNumber = "";
        string variantLetter = "";
        bool isOutbound = true;
        float scheduledDeparture = 0f;

        if (BusScheduler.Instance != null)
        {
            using (BusScheduler.BeginPlayerContext(requestingClientId))
            {
                if (BusScheduler.Instance.TryGetAssignedSlot(BusScheduler.PLAYER_BUS_ID, out var slot) && slot != null)
                {
                    success = true;
                    routeNumber = slot.routeNumber;
                    variantLetter = slot.variantLetter ?? "";
                    isOutbound = slot.isOutbound;
                    scheduledDeparture = slot.scheduledDeparture;
                }
            }
        }

        var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { requestingClientId } } };
        ContinueChainResultClientRpc(success, routeNumber, variantLetter, isOutbound, scheduledDeparture, rpcParams);
    }

    [ClientRpc]
    private void ContinueChainResultClientRpc(bool success, string routeNumber, string variantLetter, bool isOutbound, float scheduledDeparture, ClientRpcParams rpcParams = default)
    {
        var callback = _pendingContinueChainCallback;
        _pendingContinueChainCallback = null;
        callback?.Invoke(success, routeNumber, variantLetter, isOutbound, scheduledDeparture);
    }

    public void RequestRelief(string routeNumber, bool isOutbound)
    {
        if (!IsSpawned) return;
        RequestReliefServerRpc(NetworkManager.Singleton.LocalClientId, routeNumber, isOutbound);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestReliefServerRpc(ulong requestingClientId, string routeNumber, bool isOutbound)
    {
        if (BusScheduler.Instance == null) return;
        using (BusScheduler.BeginPlayerContext(requestingClientId))
        {
            BusScheduler.Instance.RequestRelief(BusScheduler.PLAYER_BUS_ID, routeNumber, isOutbound);
        }
    }

    public void RequestCompleteSlot(bool retire)
    {
        if (!IsSpawned) return;
        RequestCompleteSlotServerRpc(NetworkManager.Singleton.LocalClientId, retire);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestCompleteSlotServerRpc(ulong requestingClientId, bool retire)
    {
        if (BusScheduler.Instance == null) return;
        using (BusScheduler.BeginPlayerContext(requestingClientId))
        {
            if (retire) BusScheduler.Instance.CompleteSlotAndRetire(BusScheduler.PLAYER_BUS_ID);
            else        BusScheduler.Instance.CompleteSlot(BusScheduler.PLAYER_BUS_ID);
        }
    }

    public void RequestReleaseSlot()
    {
        if (!IsSpawned) return;
        RequestReleaseSlotServerRpc(NetworkManager.Singleton.LocalClientId);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestReleaseSlotServerRpc(ulong requestingClientId)
    {
        if (BusScheduler.Instance == null) return;
        using (BusScheduler.BeginPlayerContext(requestingClientId))
        {
            BusScheduler.Instance.ReleasePlayerSlot(BusScheduler.PLAYER_BUS_ID);
        }
    }

    public void RequestRecordActualDeparture(float gameTimeMinutes)
    {
        if (!IsSpawned) return;
        RequestRecordActualDepartureServerRpc(NetworkManager.Singleton.LocalClientId, gameTimeMinutes);
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestRecordActualDepartureServerRpc(ulong requestingClientId, float gameTimeMinutes)
    {
        if (BusScheduler.Instance == null) return;
        using (BusScheduler.BeginPlayerContext(requestingClientId))
        {
            BusScheduler.Instance.RecordActualDeparture(BusScheduler.PLAYER_BUS_ID, gameTimeMinutes);
        }
    }

    // ── Relief-search RESULT delivery (host -> the specific client that asked) ──
    // BusScheduler.NotifyReplacementFound/NotifyReplacementSearchFailed call these directly
    // (static access via Instance) instead of the old BusNetworkSync.FindByOwner-by-NetworkObject
    // lookup -- _clientToBus/_busToClient above are the new source of truth for "who owns what."

    public void SendReplacementFoundToClient(ulong clientId, int replacementBusID)
    {
        var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
        ReplacementFoundClientRpc(replacementBusID, rpcParams);
    }

    public void SendReplacementSearchFailedToClient(ulong clientId)
    {
        var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
        ReplacementSearchFailedClientRpc(rpcParams);
    }

    [ClientRpc]
    private void ReplacementFoundClientRpc(int replacementBusID, ClientRpcParams rpcParams = default)
    {
        PlayerHandoff.Instance?.OnReplacementFound(replacementBusID);
    }

    [ClientRpc]
    private void ReplacementSearchFailedClientRpc(ClientRpcParams rpcParams = default)
    {
        PlayerHandoff.Instance?.OnReplacementSearchFailed();
    }
}
