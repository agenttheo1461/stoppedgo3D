using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  DRIVER PERSONALITY
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public enum DriverArchetype { Normal, Slacker, Stubborn, Express, Cautious, SpeedRacer, EarlyBird, Rookie, Veteran }
public enum BusCondition    { Pristine, Standard, Worn, Neglected }

public class DriverPersonality
{
    public DriverArchetype archetype = DriverArchetype.Normal;
    [Range(0.5f, 2.0f)] public float speedMultiplier = 1.0f;
    [Range(0.5f, 2.0f)] public float dwellMultiplier  = 1.0f;
    [Range(0.0f, 1.0f)] public float aggressiveness   = 0.5f;
    [Range(0f, 1f)]     public float brakingBias       = 0.4f;
    [Range(0f, 0.3f)]   public float speedVariance     = 0.05f;
    [Range(0f, 0.5f)]   public float stopOvershoots    = 0f;
    [Tooltip("How eagerly this specific driver 'kicks down' -- a brief full-throttle punch when climbing a hill with a real speed deficit, like a transmission dropping a gear for passing power. Rolled per-bus for variety; some drivers punch it, some ease in.")]
    [Range(0f, 1f)]     public float kickdownProneness = 0f;

    public float BrakeRate      => Mathf.Lerp(8f,  25f, brakingBias);
    public float BrakeStartDist => Mathf.Lerp(60f, 25f, brakingBias);
    public float AccelGain      => Mathf.Lerp(0.7f, 1.4f, aggressiveness) * (1f + kickdownProneness * 0.3f);
}

// ═══════════════════════════════════════════════════════════════════════════════
//  NPC BUS CONTROLLER  v4.1 — POSSESSION STOP-INDEX FIX
//
//  [FIX vs v4] ResyncToCurrentPosition() previously only re-synced the route
//  SEGMENT/T after a player released possession — it never touched
//  _nextStopIdx. Since NPCBusController is disabled during possession,
//  _nextStopIdx stays frozen at whatever it was pre-possession. If the
//  player physically drove past one or more stops while in control, that
//  frozen index now points at a stop BEHIND the bus. A stop behind the bus
//  can never satisfy the "approaching" distance check in
//  ComputeApproachSpeed/FollowRoute, so the bus never brakes, never enters
//  StopDwell, and _nextStopIdx never advances — every remaining stop on
//  that leg silently gets skipped for the rest of the trip.
//
//  Fix: ResyncToCurrentPosition() now also calls ResyncNextStopIndex(),
//  which walks the stop sequence and finds the first stop whose position
//  resolves to a segment/T at-or-ahead of the bus's newly-resynced
//  segment/T, and sets _nextStopIdx to that. Stops physically behind the
//  bus are correctly treated as already served; stops still ahead are
//  correctly still queued.
// ═══════════════════════════════════════════════════════════════════════════════
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(AudioSource))]
public class NPCBusController : MonoBehaviour, IBusDisplaySource
{
    // ── Articulation ──────────────────────────────────────────────────────────
    [Header("Articulation — Lagged Follow")]
    [Range(1f, 20f)] public float trailerFollowSpeed = 6f;

    [Header("Depot Assignment")]
    public DepotData homeDepot;

    [Header("Depot Return")]
    public Vector3 depotGatePosition = Vector3.zero;
    [Tooltip("Automatically send retired buses back to a depot when their route lap threshold is reached.")]
    public bool autoReturnToDepotOnRetirement = true;
    [Tooltip("If the home depot is full, allow retiring buses to use another depot with available space.")]
    public bool allowAlternativeDepotIfHomeFull = true;
    [Range(0.1f, 30f)] private float depotReturnSpeedFraction = 15.3f;
    [Tooltip("If a bus finishes a leg and its next assigned slot doesn't depart for at least this many game-minutes, send it to wait at the depot instead of idling at the terminal.")]
    public float maxWaitBeforeDepotReturn = 120f;
    [Tooltip("How many game-minutes before scheduled departure a depot-waiting bus starts its egress drive back to the terminal.")]
    public float depotEgressLeadMinutes = 8f;
    [Tooltip("A bus that ends a leg at a non-terminal turnback stop with no follow-up slot yet holds at the terminal-idle bay and waits this many game-minutes for the scheduler to assign one, instead of immediately retiring. If none shows up in that window, it falls through to the same depot-retirement path a real-terminal bus uses. Was previously unbounded -- a bus with no follow-up slot could hold here forever.")]
    public float terminalHoldTimeoutMinutes = 15f;

    // ── Identity ──────────────────────────────────────────────────────────────
    [Header("Identity")]
    public int busID;

    [Header("Debug")]
    [Tooltip("Gates every Debug.Log() in this file (not LogWarning/LogError, " +
             "which only fire on genuine anomalies and stay on regardless). " +
             "Defaults OFF -- a confirmed real bug found via Xcode CPU/Memory " +
             "profiling: an unconditional per-fixed-tick Debug.Log firing for " +
             "every active bus was a major contributor to sustained high CPU " +
             "(66-169%) and a steady, never-plateauing memory climb over a " +
             "play session (every log call allocates a new string, and " +
             "Development Builds additionally retain log history in memory). " +
             "That one call is already removed entirely; this flag covers " +
             "every OTHER Debug.Log in the file (spawn/depot/detour/nudge-" +
             "around events) so none of them can become the next version of " +
             "the same problem. Flip on only when actively debugging.")]
    public bool verboseLogging = false;
    public int fleetNumber;

    // ── Player / AI Mode ─────────────────────────────────────────────────────
    [Header("Player / AI Mode")]
    [Tooltip("TRUE while a human is driving this bus. When true, FollowRoute()/AI " +
             "scheduling is completely bypassed in favor of HandlePlayerDriving() — " +
             "not just disabled, the AI branch never runs. Flip this from PlayerHandoff " +
             "when possessing/releasing a bus; ResyncToCurrentPosition() is still called " +
             "on release so the AI resumes from wherever the player actually left it.")]
    public bool isPlayer = false;

    [Header("Player Steering (used only when isPlayer)")]
    public Transform frontPivot;
    public float wheelbase            = 6.0f;
    public float maxSteerAngle        = 45f;
    public float speedForMaxSteer     = 10f;
    public float steerResponseSpeed   = 5f;
    private float _playerSteerAngle   = 0f;
    /// <summary>Mirrors BusSimulationController.SteerAngleDegrees for the
    /// isPlayer path — BusWheelSteer checks whichever controller is actually
    /// present on this bus.</summary>
    public float PlayerSteerAngleDegrees => _playerSteerAngle;

    public enum GearDirection { Reverse = -1, Neutral = 0, Drive = 1 }
    public GearDirection currentDirection = GearDirection.Drive;
    public bool parkingBrake = false;
    [HideInInspector] public bool accelKey = false;
    [HideInInspector] public bool brakeKey = false;
    [HideInInspector] public bool kickdownKey = false;

    // Player door state — NPC buses use _stopSequence/StopDwell instead.
    public bool doorsOpen = false;
    public bool rearDoorsOpen = false;

    [Header("Door Rigs (visual)")]
    [Tooltip("The actual animated front doorway. If left empty, doors are treated as instantly open/closed (old behavior) — assign this to get real animation + movement gating.")]
    public BusDoorSet frontDoorSet;
    [Tooltip("The actual animated rear doorway. Optional — some buses/stops never use it.")]
    public BusDoorSet rearDoorSet;
    [Tooltip("At a non-terminal stop, chance [0-1] the rear door also opens (real operators crack it if someone's actually back there — we don't simulate that directly, so this stands in for it). Terminals always open the rear door.")]
    [Range(0f, 1f)] public float rearDoorOpenChance = 0.35f;

    // [FIX — #19, door half] Every BusDoorSet found anywhere in the hierarchy
    // (Awake, GetComponentsInChildren) — not just whatever's dragged into
    // frontDoorSet/rearDoorSet. Fixes child/articulated sections whose door
    // leaves never toggled because nothing ever called them "by component."
    private BusDoorSet[] _allDoorSets;

    /// <summary>True only once BOTH doorways have finished their close animation —
    /// not merely been told to close. This is what actually gates movement now,
    /// for both AI and player driving.</summary>
    private bool DoorsFullyClosed
    {
        get
        {
            if (_allDoorSets != null && _allDoorSets.Length > 0)
            {
                for (int i = 0; i < _allDoorSets.Length; i++)
                    if (_allDoorSets[i] != null && !_allDoorSets[i].IsFullyClosed) return false;
                return true;
            }
            return (frontDoorSet == null || frontDoorSet.IsFullyClosed) &&
                   (rearDoorSet  == null || rearDoorSet.IsFullyClosed);
        }
    }

    // ── Driver-mind hooks (written by BusDriverMind, read by dwell/departure logic) ──
    [Tooltip("Minutes added to every scheduled departure this driver waits for. NEGATIVE = leaves early (EarlyBird), positive = habitually late (Slacker/Rookie). Set per-archetype in RandomizePersonality, then nudged live by BusDriverMind.")]
    public float departureOffsetMinutes = 0f;
    [Tooltip("One-shot extra seconds added to the NEXT stop dwell — BusDriverMind uses this for schedule-holding (running ahead → deliberately dwell longer to pad back onto schedule, like real operators do). Consumed and zeroed each stop.")]
    public float mindExtraDwellSeconds = 0f;
    private bool _playerHasProcessedStopArrival = false;

    // ── Engine / TX ───────────────────────────────────────────────────────────
    [Header("Engine")]
    public BusSimulationController.EngineType engineType = BusSimulationController.EngineType.L9N;
    public string tx          = "voith";
    public bool   economyMode = false;
    [Header("Hill Mode / Kickdown")]
    [Tooltip("Runtime state -- true while this bus is treated as climbing a hill (pitched nose-up beyond hillPitchThreshold). Feeds straight into audioEngine.hillMode for the DSP to react.")]
    public bool  hillMode = false;
    [Tooltip("Bus nose-up pitch, in degrees, beyond which it's considered climbing a hill.")]
    [Range(1f, 15f)] public float hillPitchThreshold = 4f;
    public int    acLevel     = 75;

    // ── Bellows ───────────────────────────────────────────────────────────────
    [Header("Bellows")]
    public MeshFilter bellowsMeshFilter;
    public float bellowsLength = 1.8f;
    [Range(0f, 90f)] public float maxBendDegrees = 45f;
    private Mesh      _bellowsMesh;
    private Vector3[] _bellowsBaseVerts;

    [Header("Bellows Position Test")]
    public Transform bellowsFrontMarker;
    public Transform bellowsRearMarker;

    [Header("Rigging Adjustments")]
    public float lateralOffset = 0f;

    // ── Personality ───────────────────────────────────────────────────────────
    [Header("Personality")]
    public DriverPersonality personality = new();

    // ── Audio ─────────────────────────────────────────────────────────────────
    [Header("NPC Audio")]
    [Range(0f, 1f)] public float npcVolumeScale = 0.55f;
    [Range(0f, 1f)] public float spatialBlend   = 1f;
    public float maxAudioDistance = 120f;

    [Header("Bus Audio Personality")]
    public bool acHighEngLow = false;

    // ── Movement ──────────────────────────────────────────────────────────────
    [Header("Movement")]
    public BusLightController lightController;

    // [ADD] Real per-bus light rigs — BusExteriorLightController and
    // BusInteriorLightController. Both are entirely optional per-bus
    // components (resolved via GetComponentInChildren so they can sit on
    // the root OR any child): if a bus doesn't have one, its half of the
    // light AI below is just a no-op, same pattern those controllers
    // already use for resolving NPCBusController/BusSimulationController
    // off themselves.
    [Header("Light AI")]
    [Tooltip("Minute-of-day (0-1439) headlights/night-interior-mode switch ON.")]
    public float nightStartMinuteOfDay = 1200f; // 20:00
    [Tooltip("Minute-of-day (0-1439) headlights/night-interior-mode switch back OFF.")]
    public float nightEndMinuteOfDay   = 360f;  // 06:00
    [Tooltip("Interior light mode used during the DAY. At night this is overridden to AllOn regardless of this setting.")]
    public BusInteriorLightController.InteriorLightMode dayInteriorLightMode = BusInteriorLightController.InteriorLightMode.LeftAndBackRightOnly;
    private BusExteriorLightController _extLights;
    private BusInteriorLightController _intLights;
    // [FIX — #19] GetComponentInChildren<T>() only ever resolves ONE match.
    // On an articulated bus (or any multi-section prefab) with its own
    // separate light controller on a child section (the trailer, an
    // articulated middle section), that second controller was simply never
    // found -- _extLights/_intLights above still only point at the first
    // one, so its tail/interior lights never reacted to anything. This is
    // the same code path for NPC-driven AND player-possessed buses (see
    // ApplyBusLights' own comment: "Player buses drive their own lights via
    // manual input elsewhere -- this is the AI half only" -- the exterior
    // rig writes below still run for both), so it affected "Artics AND
    // Players" exactly as reported. These two arrays hold EVERY light
    // controller found under the root, and every WRITE below now fans out
    // to all of them; only the read-only state checks (HazardsOn, etc.)
    // still use the single _extLights/_intLights reference above, which is
    // fine since every controller here is being driven to the same state.
    private BusExteriorLightController[] _extLightsAll;
    private BusInteriorLightController[] _intLightsAll;
    private BusDestinationBoard _destinationBoard;
    private bool  _lightRigsResolved = false;
    private float _lastFacingYaw;
    private float _turnSignalHoldTimer = 0f;
    private const float TURN_SIGNAL_YAW_RATE_THRESHOLD = 8f;   // deg/sec sustained to count as "turning"
    private const float TURN_SIGNAL_HOLD_SECONDS       = 0.6f; // keep signal on this long after yaw rate drops, avoids flicker

    // [ADD] Idle-zone engine/battery shutdown — see UpdateIdleEngineShutdown.
    [Header("Idle Zone Engine/Battery Shutdown")]
    [Tooltip("Seconds a bus must sit parked (terminal idle-zone bay, or waiting at depot) before its engine shuts off. Battery follows ~1.5s after that. Avoids the DSP's engine/battery-off transition audio (\"gurgling\") firing the instant the bus parks.")]
    public float idleShutdownDelaySeconds = 5f;
    private float _idleParkedTimer = 0f;

    public float designBaseSpeed      = 60f;
    public float baseTargetSpeed      = 60f;
    public float stopApproachRadius   = 40f;
    public float stopDwellBase        = 8f;
    public float terminalDwellBase    = 20f;
    [NonSerialized] public float moodSpeedMult = 1f;

    [Header("Lateness Recovery")]
    public float expressDeadRunSpeedMultiplier = 1.5f;
    public float maxLatenessSpeedBoost         = 1.5f;

    // ── Route Variant ─────────────────────────────────────────────────────────
    public string variantLetter { get; set; }

    // ── Bus awareness ─────────────────────────────────────────────────────────
    [Header("Bus Awareness")]
    public float    awareRadius            = 30f;
    public float    minFollowGap           = 8f;
    public float    raycastDistance        = 25f;
    public LayerMask busDetectionMask      = ~0;
    public float    busDetectionHalfWidth  = 1f;
    public float    busDetectionStartOffset= 6f;
    public float    busDetectionHeightOffset= 1.2f;
    public float    busDetectionHalfHeight = 1.6f;

    [Header("Spawn Placement")]
    [Tooltip("Any time a bus is placed directly onto the route (depot egress, terminal spawn, offline-progression resync), it's dropped this many units ABOVE the route's actual height there, instead of exactly on it. Gravity + the bus's own collider settle it onto the road's real surface within a frame or two — avoids spawning slightly embedded/underground from any tiny mismatch between the spline and the real mesh collider.")]
    public float spawnHeightBuffer = 0.5f;
    [Tooltip("Safety-net only — does not run every frame or compete with gravity/colliders for height. If a bus's actual Y ever ends up this far below the route spline's Y at its current position (collider gap, tunneling through a thin mesh, or a bad spawn), it gets snapped back onto the route once and logs a warning, instead of falling forever.")]
    public float fallRecoveryThreshold = 3f;

    // ── Obstacle bypass ───────────────────────────────────────────────────────
    [Header("Obstacle Bypass")]
    public float bypassWaitThreshold   = 4f;
    public float bypassLateralOffset   = 2f;
    public float bypassForwardDist     = 12f;
    public float bypassClearSweepWidth = 3f;

    // ── Articulation ──────────────────────────────────────────────────────────
public enum ArticulationMode { None, HistoryWheelTrack, HingeConstraint, KinematicRig, DelayedRotation, TractrixJoint }
// TractrixJoint appended at the END on purpose -- same rule as
// BusSimulationController's copy of this enum: keeps every existing
// serialized int (per-prefab, per-scene) pointing at the mode it always
// pointed at. This is the geometric rod/tractrix solver -- rear axle stays
// exactly trailerLength behind the hitch and its heading is DERIVED from
// that geometry, not sampled from a delay buffer or lerped toward a target.
// See UpdateTrailerTractrix, ported straight over from BusSimulationController.
    [Header("Articulated Bus")]
    public ArticulationMode articulationMode   = ArticulationMode.None;
    public Transform trailerPivot;
    public float     tractorRearOffset         = -5.0f;
    public float     trailerFrontOffset        = 3.5f;
    public float     trailerLength             = 9.0f;
    public bool      trailerMeshFacesBackward  = false;
    public int       historyBufferSize         = 512;
    [Range(1f, 15f)]  public float hingeFilterSpeed = 5f;
    [Range(10f, 80f)] public float maxHingeAngle    = 55f;
[Tooltip("Full 3-axis hitch sim used when articulationMode = KinematicRig. Setup: just place trailerPivot at the hinge.")]
public ArticulationRig rig = new ArticulationRig();

    [Header("Hitch Point (shared -- used by HistoryWheelTrack + DelayedRotation)")]
    [Tooltip("Optional. Drag any Transform here (even a plain empty GameObject) to wherever you want the trailer to hinge. Overrides tractorRearOffset AND the auto-measured center. Leave empty to auto-measure instead (recommended).")]
    public Transform pivotPoint;
    [Tooltip("Auto-measures the hitch point from the bus's own collider/renderer bounds -- true rear edge (Z) AND true lateral center (X), so an off-center mesh or a wrong tractorRearOffset guess can't throw the trailer off to one side or metres away. Turn off to use the raw tractorRearOffset number instead (old behavior).")]
    public bool autoDetectHitch = true;

    [Header("Delayed Rotation (DelayedRotation mode only)")]
    [Tooltip("How far back in time the trailer's rotation is sampled from, in seconds. Position has ZERO delay (always rigidly at the hitch) -- only rotation lags by this amount.")]
    [Range(0.02f, 1.5f)] public float rotationDelaySeconds = 1.5f;
    [Tooltip("Baked-in rest orientation for the trailer mesh, applied on top of the delayed rotation every frame.")]
    public Vector3 trailerMeshRestOffset = new Vector3(-90f, 0f, 0f);
    [Tooltip("Manual lateral trim, applied along the TRAILER's own right axis (not the tractor's) on top of the auto-detected hitch, so it stays symmetric through turns.")]
    public float trailerLateralTrim = -35f;
    [Tooltip("Fine-tune the pivot point relative to wherever it currently sits (auto-detected hitch + trailerLateralTrim). X = right/left, Y = up/down, Z = forward/back. NPC rig uses Z = 0 (no forward/back nudge needed for this bus).")]
    public Vector3 pivotRelativeOffset = new Vector3(0f, 2.5f, 0f);
    [Tooltip("How much of the delayed rotation reaches the trailer AT OR ABOVE catchUpFullSpeedKph. Below that speed this is scaled down further by the speed-based catch-up gate.")]
    [Range(0f, 1f)] public float rotationInfluence = 1f;
    [Header("Speed-Gated Catch-Up")]
    [Tooltip("Below this speed (km/h) the trailer's rotation FREEZES exactly where it is. At/above this speed it catches up at full rate. Linear in between.")]
    [Range(1f, 30f)] public float catchUpFullSpeedKph = 10f;
    [Tooltip("How long the catch-up takes at full speed, in seconds. Below full speed it takes proportionally longer.")]
    [Range(0.1f, 10f)] public float catchUpDurationSeconds = 3f;

    [Header("Self-Collision (multi-section prefabs)")]
    [Tooltip("Drag in any OTHER articulated section colliders here -- e.g. a middle section between front and rear -- so this rig can ignore collisions between all of the bus's own parts.")]
    public Transform[] additionalArticulatedSegments;

    [Header("Trailer Ground Pitch (dynamic X — TractrixJoint only)")]
    [Tooltip("Raycasts down at the hitch point and the rear axle to find real ground height under each, and pitches the trailer section (rotation X) to match the slope between them -- ported straight from BusSimulationController. Layers ON TOP of trailerMeshRestOffset, doesn't replace it.")]
    public bool  trailerGroundPitchEnabled = true;
    [Tooltip("Layers the ground raycasts hit. Set this to your road/terrain layer only -- if it also hits the bus's own colliders you'll get garbage pitch.")]
    public LayerMask groundPitchLayer = ~0;
    [Tooltip("Raycast starts this far above the hitch/axle point and casts straight down.")]
    public float groundPitchRayHeight = 2.5f;
    [Tooltip("How fast the pitch can change, degrees/second. Lower = softer suspension feel.")]
    public float groundPitchSmoothDegPerSec = 40f;
    [Tooltip("Hard clamp on the dynamic pitch, degrees, so a raycast miss or a weird collider spike can't fold the mesh.")]
    public float groundPitchMaxDeg = 20f;
    [Tooltip("Flip this if the pitch comes out upside-down for your mesh's axis convention -- same per-rig flag pattern as trailerMeshFacesBackward.")]
    public bool  groundPitchInverted = false;
    private float _groundPitchDegSmoothed = 0f;

    private readonly List<float>      _rotHistoryTimes  = new List<float>();
    private readonly List<Quaternion> _rotHistoryValues = new List<Quaternion>();
    private Quaternion _lastAppliedTrailerRot;
    private bool       _hasAppliedTrailerRot = false;
    private bool       _hitchOffsetCached = false;
    private Vector3    _cachedHitchLocal;

    // ── Articulation runtime ──────────────────────────────────────────────────
    private Vector3[] _historyBuf;
    private float[]   _historyDist;
    private int       _historyHead      = 0;
    private int       _historyCount     = 0;
    private float     _historyTotalLen  = 0f;
    private Vector3   _historyLastPos;
    private bool      _historyInit      = false;
    private float     currentHingeYaw   = 0f;
    private float     _hingeAngle       = 0f;
    // [ADD] Exposed for the articulation creak/groan audio layer -- see
    // BusSimulationController.HingeAngleDegrees for the mirrored accessor.
    public float HingeAngleDegrees => _hingeAngle;
    private Vector3   _lastTrailerRearPos;
    private bool      _trailerInitialized = false;
    // ── TractrixJoint runtime (rod/tractrix solver -- ported from BusSimulationController) ──
    private Vector3   _rearAxleWorld;
    private bool      _tractrixInit = false;
    /// <summary>Force the rod solver to re-seat the rear axle straight behind
    /// the hitch on its next tick -- call after a teleport/respawn/depot spawn
    /// so the section doesn't drag from a stale world position.</summary>
    public void ResetTractrixJoint() => _tractrixInit = false;
    // ── HingeConstraint runtime (lerp-follow -- has no init flag of its own,
    // so a pending-snap flag plays the same role as _trailerInitialized/_tractrixInit) ──
    private bool  _hingeNeedsSnap = false;
    // ── Hill mode / kickdown runtime state ─────────────────────────────────
    private bool  _isKickingDown = false;
    private float _kickdownTimer = 0f;
    private bool  _checkedTrailerRigidbody = false;

    // ── Public route state ────────────────────────────────────────────────────
    public int           NextStopIndex  => _nextStopIdx;

    // ═════════════════════════════════════════════════════════════════════════
    //  IBusDisplaySource -- for BusInteriorScrollBoard / BusInteriorLCDBoard.
    //  Reuses _stopSequence/_nextStopIdx/_paxPlan as-is -- "stop requested"
    //  IS the existing GetAlightingCount roll for the upcoming stop, not a
    //  new pull-cord system.
    // ═════════════════════════════════════════════════════════════════════════
    int IBusDisplaySource.BusID => busID;

    string IBusDisplaySource.RouteNumber => _route != null ? _route.routeNumber : "";

    // LCD boards show the plain route number and the destination of whatever this bus is running (a short turn
    // shows its own turnback destination). The '~' short-turn symbol is for consoles / the 2D driver board only.
    string IBusDisplaySource.DestinationHeadsign =>
        _route == null ? "" : _route.GetDestinationName(_isOutbound, CurrentVariant);

    string IBusDisplaySource.RouteQualifier =>
        _route == null ? "" : (_isOutbound ? _route.routeQualifierOutbound : _route.routeQualifierInbound);

    string IBusDisplaySource.CurrentStopName
    {
        get
        {
            int idx = _nextStopIdx - 1; // the stop just served, if any
            if (_stopSequence == null || idx < 0 || idx >= _stopSequence.Count) return "—";
            return _stopSequence[idx].stopName;
        }
    }

    string IBusDisplaySource.NextStopName
    {
        get
        {
            if (_stopSequence == null || _nextStopIdx < 0 || _nextStopIdx >= _stopSequence.Count) return "—";
            return _stopSequence[_nextStopIdx].stopName;
        }
    }

    bool IBusDisplaySource.IsNextStopRequested =>
        _paxPlan != null && _stopSequence != null && _nextStopIdx < _stopSequence.Count
            && _paxPlan.GetAlightingCount(_nextStopIdx, onboardPax) > 0;

    List<string> IBusDisplaySource.GetUpcomingStops(int count)
    {
        var result = new List<string>(count);
        if (_stopSequence == null || _nextStopIdx < 0) return result;

        for (int i = _nextStopIdx; i < _stopSequence.Count && result.Count < count; i++)
            result.Add(_stopSequence[i].stopName);

        return result;
    }

    // ── [07-30] added for the redesigned LCD board ──────────────────────────
    int  IBusDisplaySource.FleetNumber     => fleetNumber;
    bool IBusDisplaySource.IsEngineRunning => audioEngine != null && audioEngine.engineState == BusAudioEngine.EngineRunState.Running;

    bool IBusDisplaySource.IsInService => State == BusState.InService;

    List<(string stopName, string etaLabel, bool isTerminal)> IBusDisplaySource.GetUpcomingStopsWithEta(int count)
    {
        var result = new List<(string, string, bool)>(count);
        if (_stopSequence == null || _nextStopIdx < 0 || _route == null) return result;

        // [FIX 07-31b] Mirrors PlayerHandoff's fix: a Completed slot in
        // _slotByBus (the scheduler's own comments flag that map as capable
        // of going stale) is NOT a schedule anchor -- its departure is in
        // the past, and anchoring to it collapsed every row toward zero
        // ("<1" at the origin, raw travel offsets after). No usable slot ->
        // "--" rows, never midnight-anchored math.
        TimetableSlot dispSlot = null;
        if (BusScheduler.Instance != null && BusScheduler.Instance.TryGetAssignedSlot(busID, out var slot)
            && slot != null && slot.state != SlotState.Completed)
            dispSlot = slot;

        if (dispSlot == null)
        {
            for (int i = _nextStopIdx; i < _stopSequence.Count && result.Count < count; i++)
                result.Add((_stopSequence[i].stopName, "--", i == _stopSequence.Count - 1));
            return result;
        }
        float scheduledDeparture = dispSlot.scheduledDeparture;

        // [FIX 07-31] Corrected model, replacing the kinematic distance/
        // speed calc entirely -- see PlayerHandoff's version for the full
        // explanation. Pure schedule as the base for every row; once
        // departed, ONE shared "how are we running" measurement (bounded
        // nearest-stop search, 1-minute deadband) gets applied uniformly
        // across all rows, so they can't show inconsistent numbers relative
        // to each other. Pre-departure, no adjustment at all.
        // [FIX 07-31b] Same clock + physics guard as PlayerHandoff: before
        // scheduledDeparture, while still physically at the origin stop, the
        // trip has not begun regardless of what the state machine reads
        // mid-transition -- a bus cannot be "running early" before pull-out,
        // so no negative offset may ever cancel the wait to departure.
        float nowMinutes = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : scheduledDeparture;
        bool physicallyAtOrigin = _stopSequence.Count > 0
            && Vector3.Distance(transform.position, _stopSequence[0].GetWorldPosition()) <= stopDetectRadius;
        bool busHasNotDepartedYet = State == BusState.AtTerminal
            || (physicallyAtOrigin && nowMinutes < scheduledDeparture);

        float aheadBehindMinutes = 0f;
        if (busHasNotDepartedYet)
        {
            // Late-only pre-departure offset, clamped at zero, same 1-minute
            // deadband as the running case: sitting past departure pushes
            // every row back; sitting early holds pure schedule.
            float lateBy = nowMinutes - scheduledDeparture;
            aheadBehindMinutes = lateBy <= 1f ? 0f : lateBy;
        }
        else if (BusTrackerService.Instance != null)
        {
            float nowForOffset = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : scheduledDeparture;
            int nearestIdx = 0;
            float best = float.MaxValue;
            for (int j = 0; j <= _nextStopIdx && j < _stopSequence.Count; j++)
            {
                float d = Vector3.Distance(transform.position, _stopSequence[j].GetWorldPosition());
                if (d < best) { best = d; nearestIdx = j; }
            }
            if (BusTrackerService.Instance.TryGetScheduledOffsetMinutes(_route, _isOutbound, variantLetter, nearestIdx, out float nearestOffsetMins))
            {
                float expectedNow = scheduledDeparture + nearestOffsetMins;
                float rawAheadBehind = nowForOffset - expectedNow;
                aheadBehindMinutes = Mathf.Abs(rawAheadBehind) <= 1f ? 0f : rawAheadBehind;
            }
        }

        for (int i = _nextStopIdx; i < _stopSequence.Count && result.Count < count; i++)
        {
            bool isTerminal = i == _stopSequence.Count - 1;
            string eta;
            float now = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : scheduledDeparture;
            float threshold = BusTrackerService.Instance != null ? BusTrackerService.Instance.arrivingThresholdMinutes : 1f;

            float scheduledArrival;
            if (i == _nextStopIdx && busHasNotDepartedYet)
            {
                scheduledArrival = scheduledDeparture;
            }
            else if (BusTrackerService.Instance != null
                     && BusTrackerService.Instance.TryGetScheduledOffsetMinutes(_route, _isOutbound, variantLetter, i, out float offsetMins))
            {
                scheduledArrival = scheduledDeparture + offsetMins;
            }
            else
            {
                result.Add((_stopSequence[i].stopName, "--", isTerminal));
                continue;
            }

            float minsAway = Mathf.Max(0f, (scheduledArrival + aheadBehindMinutes) - now);
            eta = minsAway < threshold ? "<1 min" : $"{Mathf.CeilToInt(minsAway)} min";
            result.Add((_stopSequence[i].stopName, eta, isTerminal));
        }
        return result;
    }

    public bool          IsOutbound     => _isOutbound;
    public BusRouteData  CurrentRoute   => _route;
    /// <summary>The variant this bus is running (a short turn is one, letter '~'), or null on the mainline.</summary>
    public RouteVariantData CurrentVariant => _route != null && !string.IsNullOrEmpty(_currentVariantLetter) ? _route.GetVariant(_currentVariantLetter) : null;
public bool IsBlockedByBus   => _blockedByBus;
public bool IsBlockedByLight => _blockedByLight;

    /// <summary>[ADD] Clears this bus's route assignment (CurrentRoute /
    /// IsOutbound / variant) so it stops being reported (dispatch roster,
    /// "N active" counts, etc.) as still on its old route.
    ///
    /// Deliberately NOT wired into the *start* of a depot/maintenance trip
    /// (RetireToDepot / StartDrivingToMaintenanceBay / BusManager's
    /// PullBusToIdle) -- a bus that's merely driving there can still be
    /// pulled back onto a route via AbortDepotIngress before it arrives,
    /// and should keep reporting its real assignment during that window.
    /// Only call this once the bus has physically come to rest: depot
    /// arrival (BusManager.NotifyBusParkedAtDepot) or maintenance bay
    /// arrival (DriveToMaintenanceBay's State -> AtMaintenanceBay).</summary>
    public void ClearRouteAssignment()
    {
        _route = null;
        _isOutbound = false;
        _currentVariantLetter = "";
        variantLetter = "";
    }
    // ── State machine ─────────────────────────────────────────────────────────
    public enum BusState
    {
        Idle, DeadRunning, InService, Decelerating, AtStop, AtTerminal,
        Rotating, Relieved, ExpressDeadRun, Braking, Detour, DetourToStart, Bypassing,
        TerminalIngress, TerminalEgress, DepotEgress, DepotIngress, WaitingAtDepot,
        AIFreeRoam,
        DrivingToFuelStation, AtFuelStation, DrivingToMaintenanceBay, AtMaintenanceBay,
    }
    public BusState State { get; private set; } = BusState.Idle;

    // ── Route data ────────────────────────────────────────────────────────────
    private BusRouteData        _route;
    private bool                _isOutbound;
    private string              _currentVariantLetter = "";
    private List<IRouteSegment> _segments      = new();
    private List<BusStopData>   _stopSequence  = new();
    private PaxRollPlan         _paxPlan;

    /// <summary>Builds this bus's driving segments preferring the real
    /// road-graph-backed path (route nodes act as directional waypoint
    /// hints, the actual driven path snaps to real roads/lanes) — falls
    /// back to the old hand-authored BuildSegments if CityManager's graph
    /// isn't available yet or a leg between two hints has a genuine gap.</summary>
    private List<IRouteSegment> BuildRouteSegments(List<RouteNode> nodes)
    {
        // Reverted to the plain Vector3-node path — no RoadGraphPathfinder
        // involved. Route nodes are the literal driven geometry again
        // (curve-flagged runs use CatmullRomSegment, same math as roads;
        // everything else is straight lines between consecutive nodes).
        return _route.BuildSegments(nodes);
    }


    private int   _currentSegmentIdx = 0;
    private float _segmentT          = 0f;
    private int   _nextStopIdx       = 0;

    // ── Unreachable-stop watchdog ──────────────────────────────────────────
    // Only ever watches the CURRENT _nextStopIdx — never anything before it.
    // A bus that boarded mid-route has already passed earlier stops (via
    // ResyncNextStopIndex / OfflineProgression), so those are none of this
    // check's business; this exists purely so a bad stop placement ahead of
    // the bus can't freeze every stop after it for the rest of the trip.
    //
    // [REVERTED] The segment-index version of this ("has _currentSegmentIdx
    // moved past this stop's nearest segment") was too trigger-happy on
    // real routes — loops, overlapping segments, and FindNearestSegmentPoint
    // picking an ambiguous nearest segment near road crossings all produced
    // false positives on stops the bus was actually going to reach fine.
    // Back to a plain, forgiving timeout: if the bus has been driving
    // toward the same next stop for a long time and never gotten close,
    // it's genuinely unreachable — no segment-position math involved.
    private int   _watchedStopIdx     = -1;
    private float _watchedStopTimer   = 0f;
    private float _watchedStopMinDist = float.MaxValue;
    private const float UNREACHABLE_STOP_TIMEOUT   = 90f;  // seconds stuck on the same next stop
    private const float DOOR_CLOSE_TIMEOUT         = 15f;  // [ADD Bug 4] seconds to wait for DoorsFullyClosed before forcing on
    private const float UNREACHABLE_STOP_DIST_THRESHOLD = 30f;
    private bool  _flaggedForRotation = false;
    public  bool  IsFlaggedForRotation => _flaggedForRotation;
    private float _segmentSpeedJitter  = 0f;
    private float _segmentSpeedJitterSmooth = 0f;

    // ── Express dead-run ──────────────────────────────────────────────────────
    private TimetableSlot _expressTargetSlot;
    private bool          _expressGoToTerminalA;
    private Vector3       _expressTerminalPosition;
    private bool          _expressTargetPositionSet = false;
    // [FIX] Express dead-runs used to beeline straight-line from wherever the
    // bus currently was, ignoring the road graph entirely — at
    // expressDeadRunSpeedMultiplier that reads as the bus teleporting off its
    // route (cutting across blocks/terrain instead of driving there). Now it
    // builds a real road-graph path once, the same way depot ingress/egress
    // already does, and follows it waypoint-by-waypoint.
    private List<Vector3> _expressPath;
    private int           _expressPathIdx = 0;

    // ── Dead-run ──────────────────────────────────────────────────────────────
    private Vector3 _deadRunTarget;

    // ── Detour ────────────────────────────────────────────────────────────────

    // ── Obstacle bypass ───────────────────────────────────────────────────────
    private float   _blockedTimer          = 0f;
    private bool    _bypassInProgress      = false;
    private bool    _blockedByLight        = false;
    private bool    _loggedMissingJunctionState = false;

    private static readonly Dictionary<Type, System.Reflection.MemberInfo> _junctionStateMemberCache = new();
    private static readonly string[] _junctionStateMemberNames =
    {
        "CurrentState","LightState","State","currentState","lightState","state",
        "CurrentColor","currentColor","CurrentPhase","currentPhase"
    };

    // Player and NPC buses are the same component now (differentiated by
    // isPlayer), so bus-ahead detection only needs one cache instead of two.
    private static readonly Dictionary<int, NPCBusController> _busColliderCache = new();

    private static NPCBusController GetCachedBus(Collider col)
    {
        int id = col.GetInstanceID();
        if (!_busColliderCache.TryGetValue(id, out var bus))
            _busColliderCache[id] = bus = ResolveBusForCollider(col);
        return bus;
    }

    /// <summary>Which bus does this collider belong to? Normally the NPCBusController in its parents. But on an
    /// ARTICULATED bus the rear section is a SIBLING of the object the controller sits on (root -> front + rear), so a
    /// parent lookup finds nothing and the rear section was invisible to every other bus: NPCs saw the front half,
    /// stopped for it, and drove straight into the back half. If the parent search fails, walk up and accept the
    /// nearest ancestor that contains exactly one controller (that ancestor is this bus's own rig, not a shared
    /// container holding many buses).</summary>
    private static NPCBusController ResolveBusForCollider(Collider col)
    {
        var direct = col.GetComponentInParent<NPCBusController>();
        if (direct != null) return direct;
        Transform t = col.transform.parent;
        for (int guard = 0; t != null && guard < 4; guard++, t = t.parent)
        {
            var found = t.GetComponentsInChildren<NPCBusController>(true);
            if (found.Length == 1) return found[0];
            if (found.Length > 1) return null;   // a container with several buses -- can't tell which
        }
        return null;
    }
public int oldBusVariant = 0;
    // Real, built-in DIWA voice options — set from FleetRosterData.DiwaVoiceOptions
    // via EngineConfig at roster spawn time. See BusAudioEngine header comment.
    public bool diwaOpt1_1 = false;
    public bool diwaOpt1_2 = false;
    public bool diwaOpt1_3 = false;
    public bool diwaOpt1_4 = false; // second D864.6 character: suppressed whine til late G1, hiss window, extended G1, audible piston firing
    public bool diwaOpt1_5 = false; // "the shaker": no whine at all, deeper+louder core, rapid-fire click vibration-sim at idle
    public bool diwaOpt1_6 = false; // "fourth voice": whine hidden like opt1_4 but quieter, extended two-tone G1, groan-only retarder
    public bool diwaOpt1_7 = false; // "fifth voice": as opt1_6 but whine never hidden -- Wandler wind-up on move-off instead
    public bool diwaOpt1_8 = false; // \"strained\": harder, surging whine (D864.6)
    private Vector3 _bypassTravelDir;
    private Vector3 _bypassLeftDir;
    private Vector3 _bypassStartPos;
    private float   _bypassLateralProgress = 0f;
    private float   _bypassForwardTravelled= 0f;
    private float   _bypassBaseYaw         = 0f;
    private enum BypassPhase { SwingLeft, SprintForward, MergeBack }
    private BypassPhase _bypassPhase;

    // ── Breakdown visual ──────────────────────────────────────────────────────
    private LineRenderer      _breakdownHalo;
    private GameObject        _breakdownBeacon;
    private Renderer          _breakdownBeaconRenderer;   // cached — avoids GetComponent every frame
    private MaterialPropertyBlock _breakdownMPB;          // cached — avoids alloc every frame
    private float             _beaconBlinkTimer = 0f;
    private bool              _breakdownEngineForceOff = false;
    private bool              _breakdownBatteryKilled  = false;
    private bool              _breakdownAcKilled        = false;
    // [ADD] Ref needed for BusBreakdownSystem's condition-weighted,
    // drivetrain-aware roll. Merged fuel+maintenance into one component --
    // was two separate GetComponent lookups (_fuelSystem/_maintSystem) for
    // what's conceptually one bus's status.
    private BusVehicleSystem _vehicleSystem;
    // [ADD] Front doors auto-open once a full-shutdown breakdown has actually
    // brought the bus to a complete stop -- not immediately on trigger. Rear
    // doors deliberately never open for a breakdown.
    private bool               _breakdownDoorsHandled = false;
    // [ADD] Smoke VFX for EngineFailure/Overheating -- lazily built like the
    // halo/beacon above.
    private ParticleSystem     _breakdownSmoke;

    // ── Simulation state ──────────────────────────────────────────────────────
    private float IDLE, GOV;
    private int   CYLINDERS = 6;
    private float FHz(float r) => (r / 60f) * (CYLINDERS / 2f);

    public float rpm = 0f, spd = 0f, accel = 0f, bkPd = 0f, shiftCD = 0f;
    [Tooltip("Max change in accel per second (0-1 scale). Both gap-to-throttle sites (FollowRoute, DriveTowardSmooth) used to hard-assign accel straight from the current frame's target-speed gap with zero smoothing -- if the gap flickered (e.g. bus-ahead hysteresis boundary noise), accel could snap fully open then fully shut in consecutive physics frames, reading as a rapid rev-tap. This caps how fast accel is allowed to move at all, so a flickering target can't produce an instant full-throttle blip.")]
    public float maxAccelRatePerSec = 2.5f;
    public int   gear    = 0;
    public bool  running = false;

    private float regenHz             = 50f;
    private bool  regenActive         = false;
    private float baeRegenGainSmooth  = 0f;

    // ── H40EP state ───────────────────────────────────────────────────────────
    private int   h40Mode      = 1;
    private float h40ModeTimer = 0f, h40DipTimer = 0f, h40DipAmount = 0f;
    private int   h40PrevMode  = 1;
    private bool  h40StopStart = false;
    private float h40SSTimer   = 0f;
    private float alShiftTransient = 0f, alShiftTransientDur = 0f, kickdownActive_f = 0f;

    // ── DSP audio ─────────────────────────────────────────────────────────────
    private double SR = 48000.0;
    private double ph_e1, ph_e2, ph_e3, ph_esub, ph_eex, ph_etb;
    private double noise_lp, noise_hp_prev;
    private double noise_lo = 0, noise_hi = 0;

    [Header("Bus Condition Presets")]
    public BusCondition conditionPreset = BusCondition.Standard;

    [Header("Old Bus — Worn Out Sound Toggles")]
    public bool ob_deepMoan, ob_worn_whine, ob_revHang, ob_delayedShifts;
    public bool ob_airRush, ob_roar, ob_rattle, ob_exhaustChuff, ob_beltSqueal, ob_doorWheeze;
    public bool oldBus = false;

    [Header("Pax Boarding")]
    public Transform doorTransform;
    public float doorSideOffset    = 1.3f;
    public float doorForwardOffset = 4.0f;

    public Vector3 GetDoorWorldPosition()
    {
        if (doorTransform != null) return doorTransform.position;
        return transform.position
             - transform.forward * doorForwardOffset
             + transform.right   * doorSideOffset;
    }

    private readonly BoardingHandle _boardingHandle = new BoardingHandle();

    private double acNoise_lp;
    private uint   _noiseSeed = 0xABCD1234u;

    public BusAudioEngine audioEngine;

    private double NextNoiseSample()
    {
        _noiseSeed ^= _noiseSeed << 13;
        _noiseSeed ^= _noiseSeed >> 17;
        _noiseSeed ^= _noiseSeed << 5;
        return (_noiseSeed / (double)uint.MaxValue) * 2.0 - 1.0;
    }

    private AudioSource _audioSource;

    // [ADD] Same possession check BusDisplaySourceResolver/BusExteriorLight
    // Controller/BusInteriorLightController already use -- cached here so
    // OnAudioFilterRead (audio thread) only ever does a reference comparison,
    // never a live GetComponent/PlayerHandoff lookup.
    private BusSimulationController _sim;
    private bool IsPlayerDrivingThisBus =>
        _sim != null
        && PlayerHandoff.Instance != null
        && PlayerHandoff.Instance.IsOnDuty
        && PlayerHandoff.Instance.playerBus == _sim;
    private Rigidbody   _rb;
    private Vector3     _busPosition;
    private Vector3     _listenerPosition;
    private Transform   _listener;

    private bool _lightHoldActive = false;
    private Vector3    _lightHoldPoint;
    private GameObject _heldLightSlab;
    // [ADD — turn-through-a-red-box fix] Travel direction at the moment
    // this light was latched. The held-light memory below was purely
    // proximity-based — it never checked whether the bus was still headed
    // the same way. Mid-turn at an intersection, the detection box can
    // clip the OLD approach's signal collider for a single frame (the box
    // sweeps across it as the bus rotates), latching a hold. Once the turn
    // completes and the bus is now facing an entirely different road, that
    // held slab is still within lookDist of the same physical intersection
    // — so without a heading check, the bus kept obeying a light for a road
    // it's no longer on until THAT light happened to cycle back to green.
    private Vector3     _lightHoldHeading;

    private static readonly float     TCIRC   = BusSimulationController.TCIRC;
    private static readonly float[]   AL_R    = BusSimulationController.AL_R;
    private static readonly bool[]    AL_LOCK = BusSimulationController.AL_LOCK;
    private static readonly float[][] V_R     = BusSimulationController.V_R;
    private static readonly float[]   V_DOWN  = BusSimulationController.V_DOWN;

    // ── Depot state ───────────────────────────────────────────────────────────
    private List<Vector3> _depotPath         = new();
    private int           _depotPathIdx      = 0;
    private float         _depotSpeedFraction= 0.25f;
    private int           _depotTargetBay    = -1;
    private bool          _depotPathDone     = false;
    private TimetableSlot _depotTargetSlot   = null;
    private bool          _pendingDepotReturn= false;
    // One-shot per low-fuel episode -- set once relief is requested, cleared
    // once fuel recovers above the threshold, so MaybeRequestLowFuelRelief
    // doesn't re-request every tick while still below 30%.
    private bool          _lowFuelReliefRequested = false;
    // Gap-wait: true when the current DepotIngress is a "go wait for my
    // already-committed next leg" trip rather than a full retirement — on
    // arrival the bus parks WITHOUT joining the general idle pool (it's
    // still reserved to _depotWaitSlot) and polls for its scheduled egress.
    private bool          _ingressIsForWait  = false;
    private TimetableSlot _depotWaitSlot     = null;
    // Retirement fuel/maintenance detour: the bus's ultimate destination is
    // captured here before any DrivingToFuelStation/DrivingToMaintenanceBay
    // detour starts, so ContinueRetirementAfterFuelOrMaintenanceStop() can
    // resume the real parking leg (or chain into the other detour) once the
    // zone-side fueling/repair finishes -- see RetireToDepot().
    private DepotData        _retirementDepot;
    private int              _retirementBayIdx = -1;
    private DepotParkingSpot _retirementSpot;
    private TerminalIdleZone _activeIdleZone;
    private int              _claimedBayIdx   = -1;
    private List<Vector3>    _idlePath;
    private int              _idlePathIdx;
    private bool             _idleIngressDone;
    private bool             _idleEgressDone;
    // [ADD] True for the entire time StopDwell()/TerminalDwell() is actually
    // running -- set at the top of each, cleared right before every exit
    // (every yield break and the natural end). Lets ResumeAfterPossession
    // tell "coroutine got killed mid-flight" apart from "coroutine finished
    // on its own and this bus is legitimately sitting here." See
    // ResumeAfterPossession's own comment for why that distinction matters.
    private bool             _dwellCoroutineActive;

    // ── Audio culling ─────────────────────────────────────────────────────────
    private const float AUDIO_CULL_DIST        = 80f;
    private const float AUDIO_CULL_HYSTERESIS  = 10f;
    private bool        _audioCulled           = false;

    private void UpdateAudioCulling()
    {
        if (_listener == null) return;
        float dist = Vector3.Distance(_busPosition, _listenerPosition);

        // Distance-based priority so Unity's own real-voice-limiting system
        // (Edit > Project Settings > Audio > Real Voice Count) can make good
        // decisions when many buses are audible at once -- lower number =
        // higher priority, so closer buses get a lower number here. Kept --
        // unrelated to the fade/tick-throttle revert.
        _audioSource.priority = (int)Mathf.Clamp(dist * 1.5f, 1f, 255f);

        // [REVERT] Fade multiplier removed per report -- was causing real
        // audio issues. Back to the original hard AudioSource.enabled
        // toggle.
        if (!_audioCulled && dist > AUDIO_CULL_DIST + AUDIO_CULL_HYSTERESIS)
        {
            _audioSource.enabled = false;
            _audioCulled = true;
        }
        else if (_audioCulled && dist < AUDIO_CULL_DIST)
        {
            _audioSource.enabled = true;
            _audioCulled = false;
        }
    }

    // ── Manager registration ──────────────────────────────────────────────────
    private bool _registeredWithManager = false;

    private void Start()
    {
        RandomizePersonality();
        RandomizeACEnginePersonality();
        audioEngine.acHighEngLow = acHighEngLow;
        audioEngine.acLevel      = acLevel;
        audioEngine.engineType   = (BusAudioEngine.EngineType)(int)engineType;

        // Moved here from Awake() — BusSpawner.SpawnBus() sets fleetNumber
        // AFTER Instantiate() already ran Awake(), so a FleetMetadata
        // lookup there always saw fleetNumber still at its default (0).
        // Start() runs on the object's first frame, well after BusSpawner's
        // synchronous setup, so fleetNumber is guaranteed real here.
        //
        // Auto-upgrades a real 35ft mini's tx to "voith35" instead of a
        // separate boolean flag — same precedent as d8646art, selected
        // purely by tx string. Only fires if it was actually assigned the
        // base "voith" transmission; a mini running something else (ZF,
        // Allison, etc.) is left alone.
        var meta = FleetMetadata.Get(fleetNumber);
        if (meta != null && tx == "voith" && (meta.busType == "XD35" || meta.busType == "XN35"))
            tx = "voith35";
        audioEngine.tx = tx;

        audioEngine.ApplyEngineConstants();
        IDLE = audioEngine.IDLE;
        GOV  = audioEngine.GOV;
        audioEngine.rpm = IDLE;

        // NPC buses have no driver pressing an ENG button — nothing else ever
        // calls RequestEngineToggle() for AI-driven buses, so engineState sits
        // at its default Off forever and every NPC bus spawns permanently
        // silent. Bring AI buses straight to Running here, skipping the crank
        // sequence (the player path still goes through the ENG button/toggle).
        if (!isPlayer)
        {
            audioEngine.batteryOn   = true;
            audioEngine.engineState = BusAudioEngine.EngineRunState.Running;
        }

        UpdateTrailerHistory();
        IgnoreSelfSegmentCollisions();
        if (!running) audioEngine.running = false;

        // Starting pax load — same random seed range the old player-only
        // BusSimulationController used, now shared by both modes.
        onboardPax = 0;

        if (frontPivot == null) frontPivot = transform;

        // Register once; ManagedTick no longer needs to write this every frame.
        BusRegistry.ActiveBuses[busID] = this;

        if (BusUpdateManager.Instance != null)
        {
            BusUpdateManager.Instance.Register(this);
            _registeredWithManager = true;
        }
    }

    private void OnDestroy()
    {
        if (_registeredWithManager && BusUpdateManager.Instance != null)
            BusUpdateManager.Instance.Unregister(this);
        BusBreakdownSystem.OnReplaceTierExpired -= HandleReplaceTierExpired;

        // [FIX M5] _busColliderCache is a static dictionary keyed by transient
        // collider instance ID that entries were never removed from — every
        // bus ever destroyed/despawned left its cache entry (and its whole
        // object graph) alive indefinitely, and Unity can reuse instance IDs
        // after destruction, so a stale entry could even resolve a new
        // collider to the wrong (old) bus. Remove every entry this bus owns.
        if (_ownColliders != null)
        {
            for (int i = 0; i < _ownColliders.Length; i++)
            {
                if (_ownColliders[i] == null) continue;
                _busColliderCache.Remove(_ownColliders[i].GetInstanceID());
            }
        }

        // [FIX — memory leak] BusSpawner._adsConfigured (a HashSet<NPCBusController>)
        // only ever had entries added, never removed -- every bus ever
        // destroyed stayed permanently referenced there, unable to be
        // garbage collected. Same shape as the M5 fix directly above, just
        // in a different file's collection. Guarded null check since a
        // bus that was never spawned through BusSpawner (edge cases,
        // tests) shouldn't require it to exist.
        if (BusSpawner.Instance != null)
            BusSpawner.Instance.NotifyBusDestroyed(this);
    }

    // [ADD] Confirmed real crash: FMOD's real-time audio thread caught mid-
    // callback inside OnAudioFilterRead (Data Abort / Translation fault)
    // while the main thread simultaneously destroyed that same GameObject
    // (AudioSource::Cleanup -> GameObject::Deactivate -> DestroyWorldObjects,
    // captured on the SAME crash). Disabling the AudioSource inside
    // OnDestroy() itself is too late -- OnDestroy fires AS PART OF the same
    // teardown operation Unity's already committed to, not before it, so it
    // can't prevent a callback that's already in flight.
    //
    // The only real fix is at the CALL SITE that decides to despawn this
    // bus (wherever that is -- BusManager/FleetDispatcher/whatever handles
    // route completion, which I don't have in this file) -- it needs to
    // call THIS method first, then destroy the GameObject a frame later,
    // instead of destroying it directly. Same two-step pattern already
    // fixed for the custom-bus case in BusSelectMenu.ReleaseCurrentBus().
    //
    // Usage at the call site:
    //   npcController.PrepareForSafeDespawn();
    //   StartCoroutine(DestroyNextFrame(npcController.gameObject)); // wait 1 frame, then Destroy()
    public void PrepareForSafeDespawn()
    {
        if (_audioSource != null) _audioSource.enabled = false;
        foreach (var src in GetComponentsInChildren<AudioSource>(true))
            src.enabled = false;
    }

    // ── Own-collider exclusion ────────────────────────────────────────────────
    // Populated in Awake. No lazy-init overhead; never touches GetComponentsInChildren again.
    private Collider[] _ownColliders;

    private bool IsOwnCollider(Collider col)
    {
        for (int i = 0; i < _ownColliders.Length; i++)
            if (_ownColliders[i] == col) return true;
        if (trailerPivot != null)
        {
            Transform t = col.transform;
            while (t != null) { if (t == trailerPivot) return true; t = t.parent; }
        }
        return false;
    }

    private void ApplyConditionPreset()
    {
        ob_deepMoan = ob_worn_whine = ob_revHang = ob_delayedShifts =
        ob_airRush = ob_roar = ob_rattle = ob_exhaustChuff =
        ob_beltSqueal = ob_doorWheeze = false;

        switch (conditionPreset)
        {
            case BusCondition.Pristine:   break;
            case BusCondition.Standard:   ob_rattle = true; break;
            case BusCondition.Worn:       ob_worn_whine = ob_delayedShifts = ob_exhaustChuff = true; break;
            case BusCondition.Neglected:
                ob_deepMoan = ob_worn_whine = ob_revHang = ob_delayedShifts =
                ob_airRush = ob_roar = ob_rattle = ob_exhaustChuff =
                ob_beltSqueal = ob_doorWheeze = true; break;
        }
    }

    private List<BusStopData> ResolveStopSequence(bool outbound, string variant)
    {
        // Pax plan is rolled ONCE per trip, right alongside the stop sequence
        // it applies to — see RerollPaxPlan(), called at every call site that
        // reassigns _stopSequence. Never re-rolled per-frame or per-stop.
        return ResolveStopSequenceInternal(outbound, variant);
    }

    /// <summary>Rolls a fresh PaxRollPlan sized to the current _stopSequence.
    /// Call this immediately after every `_stopSequence = ResolveStopSequence(...)`
    /// assignment — never inside a per-frame update.</summary>
    private void RerollPaxPlan()
    {
        int count = _stopSequence?.Count ?? 0;
        var positions = new Vector3[count];
        var terminals = new bool[count];
        for (int i = 0; i < count; i++)
        {
            positions[i] = _stopSequence[i].GetWorldPosition();
            // Don't trust BusStopData.isTerminal alone — a separate
            // segment-count-based mechanism actually drives real
            // end-of-route detection, and this flag may not reliably mark
            // the true last stop of THIS leg's sequence. The literal last
            // index always counts as terminal here too, so
            // GetAlightingCount's "everyone off" rule can't silently miss it.
            terminals[i] = _stopSequence[i].isTerminal || i == count - 1;
        }
        _paxPlan = PaxRollPlanner.RollTrip(positions, terminals, SimClock.Instance.GameTimeMinutes);
    }

    private List<BusStopData> ResolveStopSequenceInternal(bool outbound, string variant)
    {
        if (_route == null) return new List<BusStopData>();

        if (BusTrackerService.Instance != null)
        {
            var stops = BusTrackerService.Instance.GetResolvedStopsForVariant(_route, outbound, variant);
            if (stops != null && stops.Count > 0) return stops;
        }

        RouteVariantData variantData = null;
        if (!string.IsNullOrEmpty(variant) && _route.variants != null)
            variantData = _route.variants.Find(v => v != null &&
                v.variantLetter.Equals(variant, StringComparison.OrdinalIgnoreCase));

        if (variantData != null)
        {
            var bindings = outbound ? variantData.outboundStopsOverride : variantData.inboundStopsOverride;
            if (bindings != null && bindings.Count > 0)
            {
                var preResolved = outbound ? variantData.resolvedOutboundStops : variantData.resolvedInboundStops;
                if (preResolved != null && preResolved.Count > 0) return preResolved;

                var resolved = ResolveStopsFromBindings(bindings);
                if (resolved.Count > 0)
                {
                    if (outbound) variantData.resolvedOutboundStops = resolved;
                    else          variantData.resolvedInboundStops  = resolved;
                    return resolved;
                }
            }
        }

        var mainlineStops = outbound ? _route.resolvedOutboundStops : _route.resolvedInboundStops;
        if (mainlineStops != null && mainlineStops.Count > 0) return mainlineStops;

        var mainlineBindings = outbound ? _route.outboundStops : _route.inboundStops;
        if (mainlineBindings != null && mainlineBindings.Count > 0)
        {
            var resolvedMainline = ResolveStopsFromBindings(mainlineBindings);
            if (resolvedMainline.Count > 0)
            {
                if (outbound) _route.resolvedOutboundStops = resolvedMainline;
                else          _route.resolvedInboundStops  = resolvedMainline;
                return resolvedMainline;
            }
        }

        return new List<BusStopData>();
    }

    private List<BusStopData> ResolveStopsFromBindings(List<RouteStopBinding> bindings)
    {
        var result = new List<BusStopData>(bindings.Count);
        foreach (var binding in bindings)
        {
            if (string.IsNullOrEmpty(binding.stopCode)) continue;
            BusStopData found = null;
            if (CityManager.Instance != null) found = CityManager.Instance.GetStop(binding.stopCode);
            if (found == null && CityManager.Instance != null)
            {
                // [PERF FIX] Was its own FindObjectsByType<BusStopMarker>() scan --
                // same shared cache PlayerHandoff's terminal lookups use now.
                var marker = CityManager.Instance.GetStopMarker(binding.stopCode);
                if (marker != null) found = marker.Data;
            }
            if (found != null) result.Add(found);
            else Debug.LogWarning($"[Bus#{busID}] Stop '{binding.stopCode}' not found.");
        }
        return result;
    }

    private void Awake()
    {
        _vehicleSystem = GetComponent<BusVehicleSystem>();

        // [ADD] Root-or-child, same resolution pattern the light rigs
        // themselves already use for finding NPCBusController/
        // BusSimulationController. Null on a bus that simply doesn't have
        // the component — every call site below checks for that and no-ops.
        _extLights = GetComponentInChildren<BusExteriorLightController>(true);
        _intLights = GetComponentInChildren<BusInteriorLightController>(true);
        // [FIX — #19] Plural find alongside the singular one above, so a
        // second light controller on a child/trailer section actually gets
        // driven too, not just discovered-and-ignored.
        _extLightsAll = GetComponentsInChildren<BusExteriorLightController>(true);
        _intLightsAll = GetComponentsInChildren<BusInteriorLightController>(true);
        _lightRigsResolved = true;

        // [FIX — #19, door half] frontDoorSet/rearDoorSet are Inspector-assigned
        // to whichever single BusDoorSet the prefab author dragged in, same as
        // _extLights/_intLights used to be singular-only. An articulated/child
        // section can carry its OWN BusDoorSet that never gets wired to either
        // field, so it silently never opens/closes. Auto-discover every
        // BusDoorSet in the hierarchy (by component, not by Inspector slot) and
        // fan every open/close call out to all of them; frontDoorSet/rearDoorSet
        // stay as-is for anything that reads them directly (e.g. designer tools),
        // but they're no longer the only things actually driven.
        _allDoorSets = GetComponentsInChildren<BusDoorSet>(true);

        // [FIX] Was GetComponentInChildren<BusDestinationBoard>() called
        // fresh at 7 separate call sites (leg start, depot ingress, spawn,
        // idle toggle) instead of cached once like the light rigs above --
        // every one of those transitions did a full hierarchy re-walk to
        // re-find a component that never moves. Same one-time-resolve
        // pattern as _extLights/_intLights.
        _destinationBoard = GetComponentInChildren<BusDestinationBoard>(true);

        var meta = FleetMetadata.Get(fleetNumber);
        if (meta != null)
        {
            conditionPreset = meta.condition;
            ApplyConditionPreset();
            // [ADD] This is the actual missing connection between
            // FleetRosterData and the breakdown system. BusCondition already
            // existed and was series-driven, but only ever touched cosmetic
            // audio flags -- it never reached BusVehicleSystem's real
            // numeric PartWear, which is what BusBreakdownSystem's
            // condition-weighted roll actually reads. Without this, a
            // 17-year-old "Neglected" 1000 Series bus and a fresh
            // "Pristine" 2300 Series bus rolled IDENTICAL breakdown odds.
            if (_vehicleSystem != null)
                _vehicleSystem.ApplySeriesDefaults(meta.fuelType, meta.isHybridAssist, FleetMetadata.ConditionToNumeric(meta.condition));
            // Mini-35 tx upgrade moved to Start() — fleetNumber isn't set by
            // BusSpawner yet at this point in the object's lifecycle, so a
            // lookup here always silently failed to match real mini buses.
        }

        _allRenderers = GetComponentsInChildren<Renderer>(true);

        // [ADD] baseTargetSpeed was a flat 60 (km/h) for every NPC bus
        // regardless of its actual physical spec. Now derived from the
        // REAL BusSimulationController physics data sitting on this same
        // GameObject/root (massKg/maxPowerKW/dragCdA) -- a genuine
        // steady-state power-vs-drag balance, not a guess:
        //   at top speed, power-limited tractive force == aero drag force
        //   maxPowerKW*1000 = 0.5 * airDensity(1.18) * dragCdA * v^3
        //   => v = cbrt( power / (0.59 * dragCdA) )
        // Real buses are electronically GOVERNED well below their
        // theoretical aero/power ceiling though (a bus is aerodynamically
        // and power-capable of more than transit operators ever let it
        // do) -- so the physics result is clamped to a 105 km/h real-world
        // governor cap. Lighter/more powerful configs hit that governor
        // ceiling; heavier/less powerful ones naturally cruise slower,
        // giving genuine variation between bus types instead of one flat
        // number for every single NPC bus in the fleet.
        var simCtrl = GetComponent<BusSimulationController>();
        if (simCtrl != null && simCtrl.maxPowerKW > 0f && simCtrl.dragCdA > 0f)
        {
            const float GOVERNOR_CAP_KPH = 105f;
            float powerLimitedVms = Mathf.Pow((simCtrl.maxPowerKW * 1000f) / (0.59f * simCtrl.dragCdA), 1f / 3f);
            float powerLimitedKph = powerLimitedVms * 3.6f;

            // [ADD] Pure flat-ground power/drag balance is mass-independent
            // (mass affects ACCELERATION, not steady-state top speed, on
            // level ground) -- but real routes aren't perfectly flat, and a
            // heavier bus genuinely sustains a lower real-world cruise
            // speed on grades/rolling resistance than a lighter one with
            // the same power. Modest effect, not the dominant term (that's
            // still power/drag) -- centered on the 14500kg reference spec,
            // +/-15% max at double/half that mass.
            const float MASS_REFERENCE_KG = 14500f;
            float massRatio   = simCtrl.massKg / MASS_REFERENCE_KG;
            float massPenalty = Mathf.Clamp((massRatio - 1f) * -0.15f, -0.15f, 0.15f);
            powerLimitedKph *= (1f + massPenalty);

            baseTargetSpeed = Mathf.Min(powerLimitedKph, GOVERNOR_CAP_KPH);
        }
        // If no BusSimulationController is found on this object, or its
        // physics fields aren't populated, baseTargetSpeed just keeps
        // whatever the Inspector default already had it set to -- no
        // change in that fallback case.

        BusBreakdownSystem.OnReplaceTierExpired += HandleReplaceTierExpired;

        _rb = GetComponent<Rigidbody>();
        // [FIX] Was also FreezePositionY — that hard-locks the Rigidbody's Y
        // at the physics level regardless of what MovePosition asks for, so
        // even with the move.y flatten removed below, a bus physically could
        // not follow a bridge/incline's rise or fall.
        // [FIX] Was also FreezeRotationX. That was wrong: Rigidbody axis
        // freezes block ANY change to that axis, including ones coming from
        // MoveRotation — not just physics/collision-induced ones. With X
        // frozen, every Quaternion.LookRotation(tangent, Vector3.up) call
        // that computes a pitched rotation for a slope/incline had its pitch
        // silently stripped back to level before it ever reached the
        // transform. Only RotationZ (roll) stays frozen, so collisions
        // can't tip a bus onto its side, but manual MoveRotation can freely
        // pitch it up/down to match the road's tangent.
        //
        // [CHANGED] RotationZ is now UNFROZEN too, on request, so the body
        // can roll slightly under physics (bumps, uneven terrain, the
        // BusKneelBody curb lean) instead of feeling rigidly clamped flat —
        // that roll is what reads as "suspension." Tradeoff this reverses:
        // Z was the one axis explicitly kept frozen so a hard side impact
        // couldn't tip the bus onto its side. With it open, a big enough
        // collision now CAN roll the bus past recovery. If that shows up,
        // the fix isn't re-freezing Z (back to square one) — it's a small
        // corrective torque/anti-roll term that pulls RotationZ back toward
        // level over time without hard-locking it.
        _rb.constraints = RigidbodyConstraints.None;
        // [CHANGE] Was Interpolate. Switched to None per request — tested
        // and confirmed still smooth in practice, so the render-smoothing
        // Interpolate provides isn't needed here. Purely a rendering
        // concern either way; physics still resolves collisions against the
        // Rigidbody's real simulated position regardless of this setting,
        // so this shouldn't change collision/detection behavior at all —
        // flagging that in case anything unexpected shows up during testing
        // and this setting gets blamed for it.
        _rb.interpolation = RigidbodyInterpolation.None;

        _audioSource = GetComponent<AudioSource>();
        // [FIX] This bus's own AudioSource kept writing NPCBusController's
        // own independently-ticking audioEngine output forever, even after
        // the player possessed this exact bus -- OnAudioFilterRead only
        // ever checked _audioCulled/!running, nothing about possession.
        // NPCBusController is documented elsewhere (BusDisplaySourceResolver)
        // as "goes inert on possession", but that was only ever true
        // logically (AI/driving stops) -- the audio side was never actually
        // gated, so it kept racing whatever BusSimulationController produces
        // once the player takes over, reading as duplicate/doubled audio.
        // Cached here (not resolved live in OnAudioFilterRead, which runs on
        // the audio thread and shouldn't do GetComponent work) so the check
        // below is just a cheap reference comparison.
        _sim = GetComponent<BusSimulationController>();
        _audioSource.spatialBlend  = spatialBlend;
        _audioSource.rolloffMode   = AudioRolloffMode.Linear;
        _audioSource.maxDistance   = maxAudioDistance;
        _audioSource.minDistance   = 5f;
        _audioSource.loop          = true;
        _audioSource.clip          = AudioClip.Create("NPCSynth", 44100, 1, 44100, false);
        _audioSource.Play();

        SR = AudioSettings.outputSampleRate;
        audioEngine = new BusAudioEngine((uint)(0xABCD1234u ^ (uint)busID))
        {
            oldBusVariant    = oldBusVariant,
            diwaOpt1_1       = diwaOpt1_1,
            diwaOpt1_2       = diwaOpt1_2,
            diwaOpt1_3       = diwaOpt1_3,
            diwaOpt1_4       = diwaOpt1_4,
            diwaOpt1_5       = diwaOpt1_5,
            diwaOpt1_6       = diwaOpt1_6,
            diwaOpt1_7       = diwaOpt1_7,
            diwaOpt1_8       = diwaOpt1_8,
            tx               = tx,
            economyMode      = economyMode,
            hillMode         = hillMode,
            npcVolumeScale   = npcVolumeScale,
            spatialBlend     = spatialBlend,
            maxAudioDistance = maxAudioDistance,
            acHighEngLow     = acHighEngLow,
            acLevel          = acLevel,
            oldBus           = ob_deepMoan || ob_worn_whine || ob_revHang || ob_delayedShifts
                             || ob_airRush || ob_roar || ob_rattle || ob_exhaustChuff
                             || ob_beltSqueal || ob_doorWheeze,
            ob_deepMoan      = ob_deepMoan,
            ob_worn_whine    = ob_worn_whine,
            ob_revHang       = ob_revHang,
            ob_delayedShifts = ob_delayedShifts,
            ob_airRush       = ob_airRush,
            ob_roar          = ob_roar,
            ob_rattle        = ob_rattle,
            ob_exhaustChuff  = ob_exhaustChuff,
            ob_beltSqueal    = ob_beltSqueal,
            ob_doorWheeze    = ob_doorWheeze,
        };
        audioEngine.Configure(_audioSource);

        _listener = Camera.main != null ? Camera.main.transform : transform;

        _ownColliders = GetComponentsInChildren<Collider>(true);
        if (verboseLogging) Debug.Log($"[Bus#{busID}] Awake — {_ownColliders.Length} own colliders");

        if (bellowsMeshFilter != null)
        {
            _bellowsMesh = Instantiate(bellowsMeshFilter.sharedMesh);
            bellowsMeshFilter.mesh = _bellowsMesh;
            _bellowsBaseVerts = _bellowsMesh.vertices;
        }
    }

    // ── LOD / renderer culling ────────────────────────────────────────────────
    private Renderer[] _allRenderers;
    private bool       _renderersEnabled = true;
    private float      _visTimer         = 0f;
    private float      _lodTimer         = 0f;

    // ── Physics LOD ───────────────────────────────────────────────────────────
    [System.NonSerialized] public float BusFixedDt  = 0.02f;
    private float _accumFixedDt   = 0f;
    private int   _fixedInterval  = 1;
    private int   _fixedCounter   = 0;

    // ── Raycast LOD ───────────────────────────────────────────────────────────
    private int _checkInterval = 1;
    private int _checkCounter  = 0;

    // Distance thresholds — FIXED from 10000 / 10000 which forced near-tier on all buses.
    private const float NEAR_DIST   =  30f;
    private const float MID_DIST    =  70f;
    private const float RENDER_HIDE = 150f;
    private const float RENDER_SHOW = 130f;
    private const int   MID_FIXED   =   2;
    private const int   FAR_FIXED   =   3;

    // [ADD] Depot-return tuning — see DriveDepotEgress/DriveDepotIngress/
    // DriveDepotPathTight. Buses run the open-road leg of a depot return
    // 1.5x faster than a normal in-service cruise, but never past the same
    // 105 km/h real-world governor cap baseTargetSpeed itself is already
    // clamped to (see Awake) — this const just guards buses whose
    // baseTargetSpeed is already near that ceiling from being pushed over it.
    private const float DEPOT_RETURN_SPEED_MULTIPLIER = 1.5f;
    /// <summary>Open-road speed of depot egress/ingress as a fraction of a normal cruise (baseTargetSpeed): 95%.
    /// (Used to be 1.5x with no traffic rules on the way in/out, and read as ~50% in practice.)</summary>
    private const float DEPOT_ROAD_SPEED_FRACTION = 0.95f;

    /// <summary>Depot legs on the open road now obey the same traffic rules as in-service driving: the junction/red-light
    /// limit and the bus-ahead follow limit computed by CheckForwardObstacles(). Inside the depot itself (yard, bays)
    /// they're not applied -- parked buses in adjacent bays would otherwise hard-stop the approach.</summary>
    private float ApplyDepotTrafficRules(float targetSpeed)
    {
        targetSpeed = Mathf.Min(targetSpeed, GOVERNOR_CAP_KPH_CLASS);
        targetSpeed = Mathf.Min(targetSpeed, _awarenessSpeedLimit);
        targetSpeed = Mathf.Min(targetSpeed, _busAheadSpeedLimit);
        return targetSpeed;
    }
    private const float GOVERNOR_CAP_KPH_CLASS         = 105f;
    private const int   MID_CHECK   =   3;
    private const int   FAR_CHECK   =   6;

    // ── Fallback Unity callbacks (only active when manager is absent) ──────────
    private void Update()      { if (!_registeredWithManager) ManagedTick(Time.deltaTime, Time.time); }
    private void FixedUpdate() { if (!_registeredWithManager) ManagedFixedTick(Time.fixedDeltaTime); }

    // ── ManagedTick — called every frame by BusUpdateManager ─────────────────
    /// <summary>
    /// Safety net for a bus that's fallen through the road/world (missing
    /// collider, gap in mesh, physics glitch, whatever). If it ever drops
    /// below Y = -1, teleport it up to Y = 5 directly above its current X/Z
    /// and let gravity bring it back down onto the road normally -- this
    /// reads as a clean "drop back into place" rather than an obvious pop,
    /// since it still falls and settles under real physics instead of being
    /// snapped exactly onto the road surface.
    /// </summary>
    private void CheckFallThroughRecovery()
    {
        if (transform.position.y >= -1f) return;

        Vector3 pos = transform.position;
        pos.y = 5f;

        if (_rb != null && !_rb.isKinematic)
        {
            _rb.position = pos;
            // Zero velocity so it drops straight down cleanly instead of
            // carrying whatever downward speed it had while falling through
            // the world -- a fresh, gentle drop onto the road.
            _rb.linearVelocity  = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
        else
        {
            transform.position = pos;
        }

        Debug.LogWarning($"[{name}] Bus fell below Y = -1 (fell through the world) -- recovered to Y = 5 above its current X/Z, letting it drop back onto the road.");
    }

    public void ManagedTick(float dt, float ts)
    {
        CheckFallThroughRecovery();

        if (State == BusState.Idle) return;

        _busPosition = transform.position;
        if (_listener != null) _listenerPosition = _listener.position;

        // LOD tier — re-evaluated every 0.5 s
        _lodTimer += dt;
        if (_lodTimer >= 0.5f)
        {
            _lodTimer = 0f;
            float dist = Vector3.Distance(_busPosition, _listenerPosition);
            if      (dist < NEAR_DIST) { _fixedInterval = 1;          _checkInterval = 1;          }
            else if (dist < MID_DIST)  { _fixedInterval = MID_FIXED;  _checkInterval = MID_CHECK;  }
            else                       { _fixedInterval = FAR_FIXED;   _checkInterval = FAR_CHECK;  }
        }

        // Renderer cull — re-evaluated every 0.4 s
        _visTimer += dt;
        if (_visTimer >= 0.4f)
        {
            _visTimer = 0f;
            float d   = Vector3.Distance(_busPosition, _listenerPosition);
            bool wantOn = d < (_renderersEnabled ? RENDER_HIDE : RENDER_SHOW);
            SetRenderersEnabled(wantOn);
        }

        if (isPlayer) PollPlayerInput();

        // [REVERT] Tick-rate throttle removed per report -- was causing real
        // audio issues (audible pitch/RPM stepping as far buses only got
        // real DSP updates every 3rd/6th frame instead of every frame).
        // Back to full-rate every frame, unconditionally, same as before
        // that optimization existed. The CPU-cost tradeoff this was meant
        // to address is still real at fleet scale, but a broken-sounding
        // fix is worse than the perf problem it was solving -- revisit with
        // a different approach later if needed.
        UpdateSimulationTick(ts);
        UpdateAudioCulling();
        if (!isPlayer) UpdateIdleEngineShutdown(dt);

        // Movement-only work (obstacle avoidance, unstuck recovery, breakdown
        // FX) is meaningless for a bus that's dwelling/parked and not going
        // anywhere -- AtStop/AtTerminal/WaitingAtDepot pile up over a long
        // session (terminal holds, overnight depot parking) and were paying
        // this cost every frame forever. LOD/audio-cull/idle-shutdown above
        // still run unconditionally for these states -- they're what settle
        // presentation state over time and are load-bearing (see ManagedTick
        // header comment / TerminalDwell's "Deliberately NOT SetIdle" note).
        bool stationaryParked = !isPlayer && (State == BusState.AtStop || State == BusState.AtTerminal || State == BusState.WaitingAtDepot
                                              || State == BusState.AtFuelStation || State == BusState.AtMaintenanceBay);
        if (!stationaryParked)
        {
            // Raycasts — throttled
            _checkCounter++;
            if (_checkCounter % _checkInterval == 0)
            {
                CheckForwardObstacles();
            }
            UpdateUnstuckManeuver();

            // Breakdown visuals — only near buses
            if (_fixedInterval == 1)
            {
                UpdateBreakdownVisuals();
                UpdateBreakdownSmoke();
            }
        }

        // Re-register only if stale (e.g. after a domain reload in editor)
        if (!BusRegistry.ActiveBuses.TryGetValue(busID, out var cached) || cached != this)
            BusRegistry.ActiveBuses[busID] = this;
    }

    // ── ManagedFixedTick — called every FixedUpdate by BusUpdateManager ───────
    public void ManagedFixedTick(float fdt)
    {
        if (!isPlayer && (State == BusState.Idle || State == BusState.AtStop || State == BusState.AtTerminal))
        {
            accel = 0f; bkPd = 1f; spd = 0f;
            _rb.linearVelocity = Vector3.zero;
            return;
        }
        if (!isPlayer && State == BusState.WaitingAtDepot)
        {
            accel = 0f; bkPd = 1f; spd = 0f;
            _rb.linearVelocity = Vector3.zero;
            DepotWaitCheck();
            return;
        }
        if (!isPlayer && State == BusState.AtFuelStation)
        {
            accel = 0f; bkPd = 1f; spd = 0f;
            _rb.linearVelocity = Vector3.zero;
            FuelStopCheck();
            return;
        }
        if (!isPlayer && State == BusState.AtMaintenanceBay)
        {
            accel = 0f; bkPd = 1f; spd = 0f;
            _rb.linearVelocity = Vector3.zero;
            MaintenanceStopCheck();
            return;
        }

        _accumFixedDt += fdt;
        _fixedCounter++;
        if (_fixedCounter % _fixedInterval != 0) return;

        BusFixedDt    = _accumFixedDt;
        _accumFixedDt = 0f;
        if (verboseLogging) Debug.Log($"[Bus#{busID}] State={State} isPlayer={isPlayer} segs={_segments?.Count ?? -1} locked={(BusBreakdownSystem.Instance != null && BusBreakdownSystem.Instance.IsBusLocked(busID))}");
        FollowRoute();
        UpdateHillModeAndKickdown(BusFixedDt);
        UpdateTrailer();
    }

    /// <summary>
    /// Hill detection + kickdown "punch." Runs after FollowRoute has already
    /// decided its normal accel/bkPd for this tick -- this layer only
    /// detects a hill climb (via the bus's own pitch) and, for kickdown-prone
    /// drivers with a real speed deficit, occasionally overrides accel to a
    /// brief full-throttle punch, like a transmission dropping a gear for
    /// climbing power. Also feeds hillMode (and audioEngine.hillMode, set in
    /// UpdateSimulationTick) so the DSP can react with a lower/held gear.
    /// </summary>
    private void UpdateHillModeAndKickdown(float fdt)
    {
        if (fdt <= 0f) return;

        // Hill detection via the bus's own pitch. eulerAngles.x is 0..360;
        // normalize to -180..180 so nose-up reads as a clean signed value.
        // This project's rigs pitch nose-up as a NEGATIVE local X rotation
        // (matches the convention already used by ArticulationRig's pitch
        // sampling) -- flip the sign below if a specific rig reads opposite.
        float rawPitch = transform.eulerAngles.x;
        float pitch    = rawPitch > 180f ? rawPitch - 360f : rawPitch;
        bool climbingHill = -pitch > hillPitchThreshold;

        hillMode = climbingHill;

        if (!climbingHill)
        {
            _isKickingDown = false;
            return;
        }

        // Only worth punching through when there's an actual speed deficit --
        // no reason to kick down while already comfortably at cruise speed
        // on the climb.
        float deficit = baseTargetSpeed > 0.5f
            ? Mathf.Clamp01((baseTargetSpeed - spd) / baseTargetSpeed)
            : 0f;
        bool wantsKickdown = deficit > 0.2f && personality.kickdownProneness > 0.25f;

        if (wantsKickdown && !_isKickingDown)
        {
            // Rolled once per opportunity, scaled by both proneness and dt
            // so it doesn't re-trigger multiple times a second for the same
            // climb -- a real driver kicks down once per hill, not
            // continuously.
            if (UnityEngine.Random.value < personality.kickdownProneness * fdt * 2f)
            {
                _isKickingDown = true;
                _kickdownTimer = Mathf.Lerp(0.6f, 2.2f, personality.kickdownProneness);
            }
        }

        if (_isKickingDown)
        {
            accel = 1f; // full-throttle punch
            bkPd  = 0f;
            _kickdownTimer -= fdt;
            if (_kickdownTimer <= 0f) _isKickingDown = false;
        }
    }

    /// <summary>Finds the (segment index, T) on _segments closest to worldPos.
    /// Shared by ResyncToCurrentPosition (bus itself) and ResyncNextStopIndex
    /// (each stop), so both are measured in the same coordinate space and can
    /// be compared directly. earlyExitDist lets a caller stop scanning once
    /// "close enough" is found (ResyncToCurrentPosition uses this — the bus
    /// itself only needs an approximate fix, not the exhaustive scan a stop
    /// lookup needs); pass a negative value (default) to always scan every
    /// segment.</summary>
    private (int seg, float t) FindNearestSegmentPoint(Vector3 worldPos, float earlyExitDist = -1f)
    {
        float bestDist = float.MaxValue;
        int   bestSeg  = 0;
        float bestT    = 0f;
        if (_segments == null) return (0, 0f);

        for (int s = 0; s < _segments.Count; s++)
        {
            var seg = _segments[s];
            for (int step = 0; step <= 24; step++)
            {
                float t = step / 24f;
                float d = Vector3.Distance(worldPos, seg.Evaluate(t));
                if (d < bestDist) { bestDist = d; bestSeg = s; bestT = t; }
            }
            if (earlyExitDist >= 0f && bestDist < earlyExitDist) break;
        }
        return (bestSeg, bestT);
    }

    /// <summary>Re-derives _nextStopIdx from the bus's CURRENT resynced
    /// segment/T position, instead of trusting whatever _nextStopIdx was
    /// frozen at during possession. Walks the stop sequence in order and
    /// picks the first stop whose own nearest segment/T is at-or-ahead of
    /// the bus's — i.e. the first stop the bus hasn't physically passed yet.
    /// Must be called AFTER _currentSegmentIdx/_segmentT have been resynced.</summary>
    private void ResyncNextStopIndex()
    {
        if (_stopSequence == null || _stopSequence.Count == 0) return;

        for (int i = 0; i < _stopSequence.Count; i++)
        {
            var stop = _stopSequence[i];
            if (stop == null) continue;

            var (stopSeg, stopT) = FindNearestSegmentPoint(stop.GetWorldPosition());
            bool stopIsAheadOrHere =
                (stopSeg > _currentSegmentIdx) ||
                (stopSeg == _currentSegmentIdx && stopT >= _segmentT - 0.02f);

            if (stopIsAheadOrHere)
            {
                if (_nextStopIdx != i)
                    if (verboseLogging) Debug.Log($"[Bus#{busID}] Resynced next stop index {_nextStopIdx} → {i} after possession release.");
                _nextStopIdx = i;
                return;
            }
        }

        // Every remaining stop resolves behind the bus's current position —
        // the whole rest of the leg was driven past under possession.
        // Send it straight to the terminal rather than hunting for a stop
        // it will never reach.
        if (_nextStopIdx != _stopSequence.Count)
            if (verboseLogging) Debug.Log($"[Bus#{busID}] Resynced — all stops on this leg already passed; heading to terminal.");
        _nextStopIdx = _stopSequence.Count;
    }

/// <summary>Re-syncs segment tracking to wherever the transform currently is —
/// call this before handing control back after a player possession, so
/// FollowRoute resumes from the real position instead of a stale pre-possession one.</summary>
public void ResyncToCurrentPosition()
{
    if (_segments == null || _segments.Count == 0) return;

    float bestDist = float.MaxValue;
    int   bestSeg  = _currentSegmentIdx;
    float bestT    = _segmentT;

    for (int s = 0; s < _segments.Count; s++)
    {
        var seg = _segments[s];
        for (int step = 0; step <= 24; step++)
        {
            float t = step / 24f;
            float d = Vector3.Distance(transform.position, seg.Evaluate(t));
            if (d < bestDist) { bestDist = d; bestSeg = s; bestT = t; }
        }
        if (bestDist < 5f) break;
    }

    _currentSegmentIdx = bestSeg;
    _segmentT          = bestT;

    if (_rb != null) _rb.position = transform.position; // sync rigidbody cache too
    // Trailer articulation was either driven by a different system (player
    // possession) or simply not ticking (component disabled) while this bus
    // was out of AI control -- its follow memory is stale relative to
    // wherever the player left the bus, same class of bug as a teleport.
    ResetArticulationState();

    // [FIX] Stop index must be resynced too, or a stop the player drove past
    // while possessing this bus stays queued as "next" forever, and the bus
    // never advances past it — silently skipping every stop after it.
    ResyncNextStopIndex();

    if (verboseLogging) Debug.Log($"[Bus#{busID}] Resynced to segment {bestSeg}, T={bestT:F2} after possession release.");
}

/// <summary>[ADD] Root-cause fix for buses that freeze forever after being
/// released back to AI. Disabling this component (which possession does —
/// see BusSelectMenu.ApplyFleetPossession) silently kills any coroutine it
/// had running; that's fine for most states since ManagedFixedTick drives
/// them by polling every tick regardless (DepotIngress/DepotEgress/
/// WaitingAtDepot all work this way), but AtStop, AtTerminal,
/// TerminalIngress and TerminalEgress are the exceptions — they're only
/// ever advanced by StopDwell()/TerminalDwell() itself (ManagedFixedTick
/// explicitly skips FollowRoute for AtStop/AtTerminal; TerminalIngress/
/// TerminalEgress physically finish driving via DriveTerminalIngress/
/// Egress each tick, but the line that actually transitions State back out
/// only lives inside the coroutine, right after that drive completes). If
/// the player possessed the bus while it was in any of these, that
/// coroutine is gone and .enabled = true alone leaves nothing to advance
/// the state machine — the bus sits there forever, active and visible.
///
/// [FIX vs the first version of this method] That version unconditionally
/// restarted TerminalDwell()/StopDwell() whenever it saw AtStop/AtTerminal —
/// but a bus commonly reaches AtTerminal via TerminalDwell's own "hold at
/// terminal-idle" branch, which sets State = AtTerminal and then exits
/// (yield break) ON PURPOSE, no interruption involved at all. Restarting
/// TerminalDwell() on a bus like that re-runs its side effects a second
/// time -- CompleteSlot() (scheduler thinks this leg completed twice) and
/// ProcessStopArrival-style pax accounting -- on a bus that already did
/// them once. _dwellCoroutineActive (set true at the top of both
/// coroutines, cleared right before every exit) is what actually tells the
/// two cases apart: true means the coroutine was genuinely still in flight
/// when it got killed; false means it already finished normally and this
/// bus is just legitimately sitting where it's supposed to be.
///
/// For a genuine interruption, this deliberately does NOT try to resume the
/// coroutine or its dwell/ingress/egress logic -- same double-side-effect
/// risk as restarting it outright, since we can't know how far it got.
/// Instead it drops the bus to Idle and hands it back to BusManager's idle
/// pool -- FleetDispatcher's own regular sweep (TryDispatchPreAssignedSlot)
/// already finds a bus that still owns a real scheduler slot and resumes it
/// correctly (mid-trip via AssignRouteWithProgress, or re-parked at the
/// terminal), using the exact same resync-safe entry points Key 7 / the
/// main-menu resnap use. A few seconds idle until the next dispatch tick
/// picks it back up beats being permanently stuck.
///
/// Call this right after re-enabling the component (and after
/// ResyncToCurrentPosition, though this no longer depends on its result
/// directly -- kept for the states where it still matters).</summary>
public void ResumeAfterPossession()
{
    bool inDwellOwnedState = State == BusState.AtStop || State == BusState.AtTerminal
                           || State == BusState.TerminalIngress || State == BusState.TerminalEgress;
    if (!inDwellOwnedState) return;

    // Coroutine already finished on its own -- this bus is legitimately
    // parked/holding exactly where TerminalDwell()/StopDwell() left it.
    // Nothing to recover; touching it here would be the double-side-effect
    // bug described above.
    if (!_dwellCoroutineActive) return;

    StopAllCoroutines();
    _dwellCoroutineActive = false;

    if (_activeIdleZone != null) { _activeIdleZone.Release(busID); _claimedBayIdx = -1; _activeIdleZone = null; }

    if (verboseLogging) Debug.Log($"[Bus#{busID}] Possession genuinely interrupted it mid-{State} — dropping to Idle for redispatch instead of guessing how to resume.");

    SetIdle(true);
    BusManager.Instance?.NotifyBusParkedAtDepot(busID); // the one place a bus gets added back to BusManager's idle pool -- see that method's own comment
}

/// <summary>[ADD] Companion fix to ResumeAfterPossession, for the OTHER
/// half of the same leak: that method cleans up on RELEASE, but nothing
/// ever cleaned up on POSSESSION itself. An in-service bus sitting parked
/// in a TerminalIdleZone bay between legs (State AtTerminal/TerminalIngress/
/// WaitingAtDepot, holding a real _activeIdleZone claim) still has that
/// claim marked occupied the instant BusSelectMenu.ApplyFleetPossession
/// disables this component -- the coroutine that would eventually release
/// it (TerminalEgress, or the retirement/wait branches) is dead the moment
/// .enabled = false runs, same as every other coroutine-owned state. Without
/// this, that bay stays permanently "occupied" by a bus that's now off
/// being player-driven -- orphaned forever, since nothing else will ever
/// release a claim whose owning coroutine no longer exists. Call this
/// BEFORE disabling the component, from the possession side.</summary>
public void ReleaseHeldIdleZoneBayForPossession()
{
    if (_activeIdleZone != null) { _activeIdleZone.Release(busID); _claimedBayIdx = -1; _activeIdleZone = null; }
}

    public void SetRenderersEnabled(bool on)
    {
        if (_renderersEnabled == on) return;
        _renderersEnabled = on;
        for (int i = 0; i < _allRenderers.Length; i++)
            if (_allRenderers[i] != null) _allRenderers[i].enabled = on;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  TRAFFIC LIGHT & BUS-AHEAD CHECKS
    // ═════════════════════════════════════════════════════════════════════════
    // [REMOVED] CheckTrafficLight() and CheckBusAhead() as two separate
    // methods -- merged below into CheckForwardObstacles(), one shared
    // physics query classified into both obstacle types. See its header
    // comment for the full rationale.

    // ── Light phase classification ──────────────────────────────────────────
    // [UPGRADED] Was strictly red/not-red via TryGetJunctionIsRed. Same
    // reflection-based lookup (can't touch ProceduralJunctionController
    // itself, so this is the most that can be improved from this side) but
    // now classifies red/yellow/green/unknown instead of a boolean, which
    // lets the caller treat yellow as "commit through if already close"
    // instead of an instant hard stop -- more realistic AND smoother, since
    // it removes the panic-brake case of a light flipping right as a bus
    // arrives close to it.
    private enum TrafficPhase { Unknown, Green, Yellow, Red }

    private TrafficPhase GetJunctionPhase(ProceduralJunctionController junction, Collider hitCollider)
    {
        var type = junction.GetType();
        if (!_junctionStateMemberCache.TryGetValue(type, out var member))
        {
            member = null;
            foreach (var name in _junctionStateMemberNames)
            {
                member = (System.Reflection.MemberInfo)
                    type.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    ?? type.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (member != null) break;
            }
            _junctionStateMemberCache[type] = member;
        }

        string valueStr = null;
        if (member != null)
        {
            object value = member is System.Reflection.FieldInfo fi    ? fi.GetValue(junction)
                         : member is System.Reflection.PropertyInfo pi ? pi.GetValue(junction)
                         : null;
            valueStr = value?.ToString();
        }

        if (valueStr == null && hitCollider != null)
        {
            var mr = hitCollider.GetComponent<Renderer>();
            valueStr = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.name : null;
            if (valueStr != null && !_loggedMissingJunctionState)
            {
                _loggedMissingJunctionState = true;
                Debug.LogWarning(
                    $"[Bus#{busID}] No light-state field on {junction.GetType().Name} via reflection. " +
                    $"Falling back to material-name matching.");
            }
        }

        if (valueStr == null) return TrafficPhase.Unknown;
        if (valueStr.IndexOf("Red", StringComparison.OrdinalIgnoreCase) >= 0) return TrafficPhase.Red;
        if (valueStr.IndexOf("Yellow", StringComparison.OrdinalIgnoreCase) >= 0
         || valueStr.IndexOf("Amber",  StringComparison.OrdinalIgnoreCase) >= 0) return TrafficPhase.Yellow;
        if (valueStr.IndexOf("Green", StringComparison.OrdinalIgnoreCase) >= 0) return TrafficPhase.Green;
        return TrafficPhase.Unknown;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  UNIFIED FORWARD OBSTACLE CHECK
    //
    //  [MERGED] Previously two separate physics passes every throttled tick:
    //  a SphereCast for junctions (CheckTrafficLight) and an OverlapBox for
    //  other buses (CheckBusAhead) -- covering almost the same forward
    //  volume, run back to back. Both questions are really the same
    //  question: "what's ahead, how far, and what kind of thing is it" --
    //  so this is now ONE OverlapBox query, classified in a single pass into
    //  junction-ahead vs bus-ahead, each producing its own speed-limit field
    //  exactly as before (nothing downstream changes: _blockedByLight,
    //  _blockedByBus, _awarenessSpeedLimit, _busAheadSpeedLimit,
    //  _busAheadHardStopped, IsBlockedByLight/IsBlockedByBus all mean
    //  exactly what they meant before). This also removes the old double-
    //  scan case in CheckBusAhead (a full second OverlapBox re-scan just to
    //  exclude the bus being nudged around) -- the exclusion is now just a
    //  skip inline in the one loop.
    //
    //  Side effect that's a genuine improvement, not just a simplification:
    //  the old SphereCast returned only the FIRST thing hit along a thin
    //  ray, so a junction slab slightly off-axis (wide intersections,
    //  multiple signal heads) could be missed entirely depending on angle.
    //  The shared box volume (same one bus-ahead detection already used,
    //  sized to the longer of the two original lookahead distances) catches
    //  it regardless of exact angle, which is the "better recognition of
    //  lights" fix from the detection side, not just the phase-reading side.
    // ═════════════════════════════════════════════════════════════════════════
    private void CheckForwardObstacles()
    {
        bool  isArticulated    = articulationMode != ArticulationMode.None || trailerPivot != null;
        float rangeBonus       = isArticulated ? 1.35f : 1f;
        float stopAt           = isArticulated ? 9f    : 7f;
        float dynamicLookahead = Mathf.Min(150f, Mathf.Max(raycastDistance, (ComputeRequiredStopDistance() + 8f) * rangeBonus));
        float busLookDist      = Mathf.Max(raycastDistance, minFollowGap + 5f);
        float lookDist         = Mathf.Max(dynamicLookahead, busLookDist);

        Vector3 travelDir   = -transform.forward;
        Vector3 boxNear     = transform.position + travelDir * busDetectionStartOffset;
        Vector3 boxCenter   = boxNear + Vector3.up * busDetectionHeightOffset + travelDir * (lookDist * 0.5f);
        Vector3 halfExtents = new Vector3(busDetectionHalfWidth, busDetectionHalfHeight, lookDist * 0.5f);
        Quaternion boxRot   = Quaternion.LookRotation(travelDir, Vector3.up);

        int hitCount = Physics.OverlapBoxNonAlloc(boxCenter, halfExtents, _busOverlapBuf, boxRot,
                                                   busDetectionMask, QueryTriggerInteraction.Collide);

        float    nearestBusDist   = float.MaxValue;
        float    nearestLightDist = float.MaxValue;
        ProceduralJunctionController nearestJunction = null;
        Collider nearestJunctionCollider = null;

        for (int i = 0; i < hitCount; i++)
        {
            Collider col = _busOverlapBuf[i];
            if (col == null || IsOwnCollider(col)) continue;

            var junction = col.GetComponentInParent<ProceduralJunctionController>();
            if (junction != null)
            {
                float jDist = Vector3.Distance(transform.position, col.ClosestPoint(transform.position));
                if (jDist < nearestLightDist) { nearestLightDist = jDist; nearestJunction = junction; nearestJunctionCollider = col; }
                continue; // a junction slab is never also a bus -- no need to also check GetCachedBus
            }

            var otherBus = GetCachedBus(col);
            // Articulated rear sections have solid colliders but no Rigidbody of their own; only triggers are skipped.
            if (otherBus == null || (col.attachedRigidbody == null && col.isTrigger)) continue;
            if (otherBus == this || otherBus.busID == busID) continue;
            // While actively nudging around a specific bus, ignore it for the
            // hard-stop/follow-gap limiter -- otherwise this would immediately
            // re-clamp speed to 0 against the very bus the maneuver exists to
            // get around. (Previously required a full second OverlapBox scan
            // to achieve; now it's just this one extra skip.)
            if (_unstuckActive && otherBus == _unstuckTargetBus) continue;

            float bDist = Vector3.Distance(transform.position, col.ClosestPoint(transform.position));
            if (bDist < nearestBusDist) nearestBusDist = bDist;
        }

        // ── Light branch ─────────────────────────────────────────────────
        // [ADD] Captured BEFORE nearestJunction's block below reassigns
        // _lightHoldActive/_blockedByLight -- so this reflects last tick's
        // state, i.e. "was this bus genuinely clear of any light right up
        // until now." Used by the any-turn exception a few lines down.
        bool wasClearOfAnyLight = !_lightHoldActive && !_blockedByLight;

        if (nearestJunction != null)
        {
            TrafficPhase phase = GetJunctionPhase(nearestJunction, nearestJunctionCollider);
            // [ADD] Right-turn-on-red -- always allowed, full stop, no
            // dependency on the cross-street's own phase (confirmed choice,
            // not the more-realistic "only if cross traffic is also
            // stopped" variant). Only applies to Red, not Yellow -- a bus
            // approaching on yellow still follows the existing
            // commit-through-if-close logic below regardless of turn
            // direction; the exception is specifically for a bus already
            // stopped/stopping at a red light that's about to turn right.
            bool aboutToTurnRight = phase == TrafficPhase.Red && IsStoppedForRightTurn(nearestLightDist + 15f);

            // [ADD] Any-turn exception: a bus that had nothing blocking it
            // on its OWN approach (wasClearOfAnyLight) and is now executing
            // ANY turn (left, right, or a sharp merge -- not just right)
            // shouldn't suddenly start stopping because the forward
            // detection box now overlaps a DIFFERENT light near the same
            // junction -- e.g. the post for the road it's turning onto, or
            // a cross-street post it swings past mid-turn. It already had a
            // clear path through this junction and committed to the turn;
            // a light that has nothing to do with the approach it actually
            // used shouldn't retroactively stop it. Doesn't apply if the
            // bus WAS already stopping for something (a real red it was
            // already obeying stays obeyed even mid-turn).
            bool alreadyClearedThroughTurn = wasClearOfAnyLight && IsApproachingAnyTurn(nearestLightDist + 15f);

            // Yellow: commit through rather than hard-brake if already close
            // enough that stopping would be a hard brake anyway -- real
            // driver behavior, and reads as "more varied" since not every
            // bus panics the instant a light turns yellow.
            bool shouldStop = !alreadyClearedThroughTurn
                            && ((phase == TrafficPhase.Red && !aboutToTurnRight)
                            || (phase == TrafficPhase.Yellow && nearestLightDist > stopAt * 2.2f));

            _lightHoldActive = shouldStop;
            if (shouldStop)
            {
                _lightHoldPoint      = nearestJunctionCollider.ClosestPoint(transform.position);
                _heldLightSlab       = nearestJunctionCollider.gameObject;
                _lightHoldHeading    = travelDir; // [ADD] see field comment
                _blockedByLight      = true;
                _awarenessSpeedLimit = nearestLightDist <= stopAt ? 0f : ComputeBrakingSpeedForDistance(nearestLightDist - stopAt);
            }
            else
            {
                _blockedByLight      = false;
                _awarenessSpeedLimit = float.MaxValue;
                _heldLightSlab       = null;
            }
        }
        else if (_lightHoldActive)
        {
            // Held-light memory -- same safety net the old version had: keep
            // braking for a light detected on a recent pass even if this
            // particular query didn't re-hit its collider.
            //
            // [FIX — turn-through-a-red-box] Added a heading check. This was
            // purely proximity-based before: a light latched mid-turn (the
            // detection box briefly clipping the OLD approach's signal as
            // the bus rotates through it) stayed "held" as long as the bus
            // remained physically close to that same intersection — true
            // for the ENTIRE turn and well onto the new road, regardless of
            // whether that light has anything to do with the road the bus
            // is now actually on. If the bus's current travel direction has
            // diverged more than ~45° from the direction it was heading
            // when this hold was set, the old approach's light is no longer
            // relevant — drop the hold immediately instead of continuing to
            // enforce a red light for a road the bus already turned off of.
            float headingDot = Vector3.Dot(travelDir, _lightHoldHeading);
            if (_heldLightSlab == null || !_heldLightSlab.activeInHierarchy || headingDot < 0.7f)
            {
                _lightHoldActive = false; _heldLightSlab = null;
                _awarenessSpeedLimit = float.MaxValue; _blockedByLight = false;
            }
            else
            {
                float distToHold = Vector3.Distance(transform.position, _lightHoldPoint) - busDetectionStartOffset;
                if (distToHold < lookDist)
                {
                    _blockedByLight      = true;
                    _awarenessSpeedLimit = distToHold <= stopAt ? 0f : ComputeBrakingSpeedForDistance(distToHold - stopAt);
                }
                else
                {
                    _lightHoldActive = false; _blockedByLight = false; _awarenessSpeedLimit = float.MaxValue;
                }
            }
        }
        else
        {
            _blockedByLight      = false;
            _awarenessSpeedLimit = float.MaxValue;
        }

        // ── Bus branch ──────────────────────────────────────────────────
        if (nearestBusDist >= float.MaxValue)
        {
            _busAheadHardStopped = false;
            _blockedByBus        = false;
            _busAheadSpeedLimit  = float.MaxValue;
        }
        else
        {
            _blockedByBus = true;
            const float hardStopGap    = 10f;
            const float hardReleaseGap = 12f;
            if (nearestBusDist <= (_busAheadHardStopped ? hardReleaseGap : hardStopGap))
            {
                _busAheadHardStopped = true;
                _busAheadSpeedLimit  = 0f;
            }
            else
            {
                // [FIX] Was a fixed-distance linear lerp squeezed into a
                // ~1-11m band (hardStopGap..max(minFollowGap, hardStopGap+1)),
                // completely independent of how fast this bus is actually
                // going. At real cruise speeds this bus's OWN deceleration
                // model needs far more room than that to actually stop --
                // e.g. ComputeRequiredStopDistance says ~38m at 60kph and
                // 13 kph/s decel -- so the old ramp barely started slowing
                // the bus before it was already almost touching whatever
                // was ahead, at any speed above a crawl. Detection RANGE
                // (lookDist/busLookDist, the OverlapBox size above) is
                // completely unchanged here -- this only changes how the
                // bus reacts to a target it's ALREADY detecting, using the
                // same physics-based braking curve already used for
                // lights/stops (ComputeBrakingSpeedForDistance) instead of
                // a distance-blind lerp. Same effect as extending detection
                // without actually extending it: the bus now starts
                // meaningfully braking as early as its own physics require,
                // anywhere within the range it was already scanning.
                _busAheadHardStopped = false;
                _busAheadSpeedLimit  = ComputeBrakingSpeedForDistance(nearestBusDist - hardStopGap);
            }
        }
    }

    private readonly Collider[] _busOverlapBuf = new Collider[16];
    private float _busAheadSpeedLimit  = float.MaxValue;
    private bool  _blockedByBus        = false;
    // Hysteresis for the hard-stop distance below -- prevents nearestDist
    // jitter (sub-cm kinematic MovePosition noise, frame to frame) right
    // around the threshold from flickering _busAheadSpeedLimit between 0
    // and non-zero every frame, which is what produced the "rev-then-brake
    // within 0.01s" chatter: with no memory of the previous frame, a bus
    // sitting almost exactly at hardStopGap could cross that boundary from
    // pure floating-point noise, and accel (see FollowRoute/DriveTowardSmooth)
    // had nothing stopping it from snapping fully open then fully shut in
    // consecutive frames in response.
    private bool  _busAheadHardStopped = false;

    // ── Unstuck nudge-around ────────────────────────────────────────────────
    // Nothing here touches CheckTrafficLight/CheckBusAhead's own detection —
    // it only reads their public outputs (_blockedByBus/_blockedByLight/
    // IsBlockedByLight) and, when armed, injects a lateral offset into the
    // position FollowRoute already computes each frame from the spline.
    [Header("Unstuck Nudge-Around")]
    [Tooltip("Seconds trailing another bus (no light ahead) before this bus tries to nudge around it.")]
    public float unstuckTriggerSeconds = 30f;
    [Tooltip("Lateral displacement (m) of the nudge-out and nudge-back-in legs.")]
    public float unstuckLateralOffset = 5f;
    [Tooltip("Forward distance (m) held at full lateral offset before easing back onto the segment.")]
    public float unstuckForwardDistance = 20f;
    [Tooltip("Speed cap (km/h) applied to the bus being passed while the maneuver is active, so the pass reads as a yield rather than a clip-through.")]
    public float unstuckYieldSpeedCap = 8f;
    [Tooltip("Hard ceiling (seconds) on how long a nudge-around maneuver is allowed to run before it's force-aborted and the lead bus's yield cap released, regardless of distance travelled. Safety net for the passing bus itself getting stuck mid-maneuver.")]
    public float unstuckMaxManeuverSeconds = 45f;

    // Anything below this stays float.MaxValue every frame unless something
    // is actively nudging around this bus right now — consumed alongside
    // _awarenessSpeedLimit/_busAheadSpeedLimit in FollowRoute's speed chain.
    private float _externalYieldSpeedCap = float.MaxValue;
    // [ADD] Which bus currently "owns" the right to yield-cap this bus.
    // Without this, FindNearestBusAhead() has no exclusivity at all — in a
    // queue of multiple buses stuck behind the same lead bus, every one of
    // them independently starts its own nudge-around maneuver once ITS OWN
    // 30s timer fires, and all of them target the same lead bus (it's the
    // nearest bus ahead for everyone in the queue). Each re-applies the 8
    // km/h cap every frame for its own maneuver's duration, and since the
    // maneuvers are staggered, the cap barely ever actually clears — the
    // lead bus reads as permanently crawling at ~8-10 km/h during any real
    // backup instead of being briefly slowed once per pass. This field lets
    // only ONE trailing bus hold the yield-cap on a given bus at a time.
    private int _yieldHeldByBusID = -1;

    private float _stuckBehindTimer   = 0f;
    private bool  _unstuckActive      = false;
    private float _unstuckDistTravelled = 0f;
    private float _unstuckManeuverTimer = 0f;
    private float _unstuckLegDistance = 0f; // total distance of the current maneuver, clamped to the next turn node
    private NPCBusController _unstuckTargetBus = null;

    /// <summary>Call once per tick, after CheckForwardObstacles()
    /// have both run for this frame. Arms/advances/clears the nudge-around
    /// state; does not move the bus itself — FollowRoute reads
    /// CurrentUnstuckLateralOffset() and adds it on top of the segment
    /// position it already computes.</summary>
    private void UpdateUnstuckManeuver()
    {
        if (_blockedByBus && !_blockedByLight)
        {
            _stuckBehindTimer += BusFixedDt;
        }
        else if (!_unstuckActive)
        {
            _stuckBehindTimer = 0f;
        }

        if (!_unstuckActive)
        {
            if (_stuckBehindTimer < unstuckTriggerSeconds) return;
            if (_blockedByLight) return; // never nudge around a red light — nothing to go around

            // Find the exact bus we're trailing so we can signal it to yield
            // and re-check it hasn't already moved off (jam cleared itself).
            _unstuckTargetBus = FindNearestBusAhead();
            if (_unstuckTargetBus == null) { _stuckBehindTimer = 0f; return; }

            // [FIX] Only THIS bus's own _blockedByLight was checked above.
            // In any queue longer than one bus at a red light, the 2nd/3rd+
            // bus back is too far from the junction collider to detect the
            // light directly — it only ever sees "bus ahead" — so it had no
            // way to tell "queued normally behind a bus that's stopped at a
            // red" apart from "stuck behind a genuinely stalled bus," and
            // would start trying to nudge around a perfectly ordinary queue
            // once its own timer fired. One-hop check: if the bus directly
            // ahead is itself light-blocked, treat this bus as effectively
            // light-blocked too and don't attempt to pass. (Doesn't chase the
            // chain further back than one bus — a 4th+ bus in a very long
            // queue could still misread it as stuck, but this covers the
            // common case.)
            if (_unstuckTargetBus.IsBlockedByLight)
            {
                _stuckBehindTimer = 0f;
                return;
            }

            // [FIX] If another bus already holds this lead bus's yield slot,
            // don't pile a second maneuver on top of it — that's exactly
            // what kept lead buses permanently capped in a multi-bus queue.
            // Reset our own timer instead of starting immediately; we'll
            // naturally re-check and pick up the slot once it's free.
            if (_unstuckTargetBus._yieldHeldByBusID != -1 && _unstuckTargetBus._yieldHeldByBusID != busID)
            {
                _stuckBehindTimer = 0f;
                return;
            }

            _unstuckActive = true;
            _unstuckDistTravelled = 0f;
            _unstuckManeuverTimer = 0f;
            _unstuckLegDistance = ComputeUnstuckLegDistance();
            _unstuckTargetBus._yieldHeldByBusID = busID; // claim the slot
            if (verboseLogging) Debug.Log($"[Bus#{busID}] Trailed lead bus #{_unstuckTargetBus.busID} for {unstuckTriggerSeconds:F0}s with no light ahead — nudging around.");
            return;
        }

        // Maneuver is active — drive it off distance travelled this frame,
        // NOT elapsed time, so it naturally plays out faster/tighter at full
        // road speed and slower/looser when crawling.
        float distThisFrame = (spd / 3.6f) * BusFixedDt;
        _unstuckDistTravelled += distThisFrame;
        _unstuckManeuverTimer += BusFixedDt;

        if (_unstuckTargetBus != null)
            _unstuckTargetBus._externalYieldSpeedCap = unstuckYieldSpeedCap;

        // [ADD] Stall safety net — completion is purely distance-gated, so
        // if THIS bus (the one doing the passing) gets stuck mid-maneuver
        // (blocked by something else, a light, another jam), distance never
        // accumulates and the yield slot — and the lead bus's speed cap —
        // would be held indefinitely. Same underlying symptom as the multi-
        // bus queue bug above, just single-holder instead of multi-holder.
        // Hard-abort and release after a generous ceiling.
        bool stalled = _unstuckManeuverTimer >= unstuckMaxManeuverSeconds;

        if (_unstuckDistTravelled >= _unstuckLegDistance || stalled)
        {
            _unstuckActive = false;
            _stuckBehindTimer = 0f;
            if (stalled)
                if (verboseLogging) Debug.Log($"[Bus#{busID}] Nudge-around STALLED after {unstuckMaxManeuverSeconds:F0}s — releasing lead bus #{(_unstuckTargetBus != null ? _unstuckTargetBus.busID : -1)}'s yield cap.");
            if (_unstuckTargetBus != null)
            {
                _unstuckTargetBus._externalYieldSpeedCap = float.MaxValue;
                // [FIX] Only release the slot if we're still the one holding
                // it — defensive against any edge-case reassignment.
                if (_unstuckTargetBus._yieldHeldByBusID == busID)
                    _unstuckTargetBus._yieldHeldByBusID = -1;
            }
            _unstuckTargetBus = null;
            if (!stalled)
                if (verboseLogging) Debug.Log($"[Bus#{busID}] Nudge-around complete — back on segment {_currentSegmentIdx}, T={_segmentT:F2}.");
        }
    }

    /// <summary>Clamps the maneuver's total forward distance to whichever is
    /// shorter: the configured full profile (out + hold + back), or the
    /// distance to the next node the route actually turns at. Turning at a
    /// node mid-nudge is just the same profile ending early — no separate
    /// "turn" code path, the ease-back leg becomes the turn approach.</summary>
    private float ComputeUnstuckLegDistance()
    {
        float fullProfile = unstuckLateralOffset + unstuckForwardDistance + unstuckLateralOffset;
        float distToTurn  = DistanceToNextTurnNode();
        if (distToTurn > 0f && distToTurn < fullProfile)
            return Mathf.Max(unstuckLateralOffset * 2f, distToTurn); // never shorter than out+back
        return fullProfile;
    }

    /// <summary>Walks forward from the current segment looking for a route
    /// node with a meaningfully different heading than the current tangent
    /// (i.e. an actual turn, not just spline noise) and returns the along-
    /// route distance to it. Returns -1 if nothing found within the lookahead.</summary>
    private float DistanceToNextTurnNode() => DistanceToNextTurnNode(out _);

    /// <summary>Same as above, but also reports the tangent dot product at
    /// the turn (1 = straight, 0 = ~90°, negative = sharper/reversing) so
    /// callers can scale behavior (e.g. cornering speed) by how sharp the
    /// turn actually is, not just whether one exists.</summary>
    private float DistanceToNextTurnNode(out float turnDot)
    {
        turnDot = 1f;
        if (_segments == null || _currentSegmentIdx >= _segments.Count) return -1f;

        const float turnDotThreshold = 0.85f; // ~32°+ heading change counts as "a turn"
        Vector3 refTangent = _segments[_currentSegmentIdx].Tangent(_segmentT);
        float   dist        = _segments[_currentSegmentIdx].Length * (1f - _segmentT);
        const float lookaheadCap = 60f;

        for (int i = _currentSegmentIdx + 1; i < _segments.Count && dist < lookaheadCap; i++)
        {
            Vector3 segTangent = _segments[i].Tangent(0f);
            float   dot        = Vector3.Dot(refTangent.normalized, segTangent.normalized);
            if (dot < turnDotThreshold)
            {
                turnDot = dot;
                return dist;
            }
            dist += _segments[i].Length;
        }
        return -1f;
    }

    /// <summary>Right-turn-on-red support (item C). Same lookahead as
    /// DistanceToNextTurnNode -- "the upcoming turn" means the same thing
    /// here as it does for cornering-speed reduction -- but also reports
    /// the turn's SIGN via the cross product's Y component: in Unity's
    /// left-handed, Y-up convention, cross(refTangent, segTangent).y is
    /// positive for a right turn and negative for a left turn (verified
    /// against forward=(0,0,1): a 90° right turn to (1,0,0) gives cross.y =
    /// +1, a 90° left turn to (-1,0,0) gives cross.y = -1). maxLookahead
    /// should be scoped to roughly "distance to the junction currently
    /// being checked" by the caller, not the full 60m cornering-speed
    /// lookahead, so this reports the turn AT that junction specifically.</summary>
    private bool IsStoppedForRightTurn(float maxLookahead)
    {
        if (_segments == null || _currentSegmentIdx >= _segments.Count) return false;

        const float turnDotThreshold = 0.85f;
        Vector3 refTangent = _segments[_currentSegmentIdx].Tangent(_segmentT);
        float   dist        = _segments[_currentSegmentIdx].Length * (1f - _segmentT);

        for (int i = _currentSegmentIdx + 1; i < _segments.Count && dist < maxLookahead; i++)
        {
            Vector3 segTangent = _segments[i].Tangent(0f);
            float   dot        = Vector3.Dot(refTangent.normalized, segTangent.normalized);
            if (dot < turnDotThreshold)
                return Vector3.Cross(refTangent.normalized, segTangent.normalized).y > 0f;
            dist += _segments[i].Length;
        }
        return false;
    }

    /// <summary>Any-turn light-suppression support. True if the bus is
    /// either about to execute a turn within maxLookahead (forward-looking,
    /// same pattern as IsStoppedForRightTurn but not filtered by
    /// direction), OR has just finished one and is still within its exit
    /// zone (same distIntoSegment/turnExitHoldDistance check
    /// ComputeTurnSpeedCap's own "exit cap" already uses below). Covers
    /// both "about to turn" and "already did the turn" -- the caller needs
    /// both, not just the forward-looking half.</summary>
    private bool IsApproachingAnyTurn(float maxLookahead)
    {
        if (_segments == null || _currentSegmentIdx >= _segments.Count) return false;

        const float turnDotThreshold = 0.85f;

        Vector3 refTangent = _segments[_currentSegmentIdx].Tangent(_segmentT);
        float   dist        = _segments[_currentSegmentIdx].Length * (1f - _segmentT);
        for (int i = _currentSegmentIdx + 1; i < _segments.Count && dist < maxLookahead; i++)
        {
            Vector3 segTangent = _segments[i].Tangent(0f);
            if (Vector3.Dot(refTangent.normalized, segTangent.normalized) < turnDotThreshold)
                return true;
            dist += _segments[i].Length;
        }

        if (_currentSegmentIdx > 0)
        {
            float distIntoSegment = _segments[_currentSegmentIdx].Length * _segmentT;
            if (distIntoSegment < turnExitHoldDistance)
            {
                Vector3 prevTangent = _segments[_currentSegmentIdx - 1].Tangent(1f);
                Vector3 curTangent  = _segments[_currentSegmentIdx].Tangent(0f);
                if (Vector3.Dot(prevTangent.normalized, curTangent.normalized) < turnDotThreshold)
                    return true;
            }
        }

        return false;
    }

    [Header("Turn Speed Reduction")]
    [Tooltip("Target speed (kph) while actually executing a sharp (~90°+) turn. Milder turns get proportionally less reduction, down to no cap at all for the ~32° detection threshold.")]
    [Range(5f, 40f)] public float turnCorneringSpeedKph = 20f;
    [Tooltip("How far (metres) past a turn's segment boundary the cornering-speed cap keeps being enforced, so the bus doesn't snap back to cruise speed the instant it crosses into the new segment while still visually mid-turn.")]
    [Range(0f, 30f)] public float turnExitHoldDistance = 12f;

    /// <summary>[ADD — turn-speed reduction] Nothing in the speed pipeline
    /// previously accounted for upcoming turn sharpness at all — a bus could
    /// (and did) take a 90° intersection turn at full cruise speed. Two
    /// consequences: it looked wrong, and per report, taking a sharp corner
    /// too fast let the bus's physical path overshoot laterally onto the
    /// wrong side of the road it just turned onto for a moment — which is
    /// almost certainly the actual mechanism behind buses getting stuck at
    /// a light right after turning (briefly occupying/clipping a collider
    /// area that belongs to the wrong lane/approach), not just a stale-
    /// memory read. Slowing genuinely for the turn should prevent the
    /// overshoot in the first place rather than just papering over its
    /// symptom. Reuses the same braking-distance math already used for
    /// lights/stops (GetEffectiveDecelKmhPerSec/ComputeBrakingSpeedForDistance)
    /// so cornering deceleration feels consistent with the rest of the
    /// bus's braking behavior, just solved for a nonzero target speed
    /// instead of a full stop.</summary>
    private float ComputeTurnSpeedCap()
    {
        float decel = GetEffectiveDecelKmhPerSec();

        // ── Approaching an upcoming turn ─────────────────────────────────
        float distToTurn = DistanceToNextTurnNode(out float approachDot);
        float approachCap = float.MaxValue;
        if (distToTurn >= 0f)
        {
            // 1 at the ~32° detection threshold (barely a turn, no real cap
            // needed) down to 0 at a 90°+ turn (full cornering-speed cap).
            float severity = Mathf.Clamp01(Mathf.InverseLerp(0.85f, 0f, approachDot));
            float cornerTarget = Mathf.Lerp(baseTargetSpeed, turnCorneringSpeedKph, severity);
            // Max current speed such that braking at `decel` reaches
            // cornerTarget exactly at the turn, not before or after.
            approachCap = Mathf.Sqrt(cornerTarget * cornerTarget + 7.2f * decel * Mathf.Max(0f, distToTurn));
        }

        // ── Still inside/just exiting a turn already taken ──────────────
        // DistanceToNextTurnNode only looks FORWARD from the current
        // segment, so the cap above releases the instant _currentSegmentIdx
        // advances onto the new (turned) segment -- exactly when the bus is
        // still physically swinging through the corner. Compare the new
        // segment's start tangent against the previous segment's tangent
        // and keep enforcing the same cornering speed for a short distance
        // into the new segment.
        float exitCap = float.MaxValue;
        if (_segments != null && _currentSegmentIdx > 0 && _currentSegmentIdx < _segments.Count)
        {
            float distIntoSegment = _segments[_currentSegmentIdx].Length * _segmentT;
            if (distIntoSegment < turnExitHoldDistance)
            {
                Vector3 prevTangent = _segments[_currentSegmentIdx - 1].Tangent(1f);
                Vector3 curTangent  = _segments[_currentSegmentIdx].Tangent(0f);
                float   dot         = Vector3.Dot(prevTangent.normalized, curTangent.normalized);
                if (dot < 0.85f)
                {
                    float severity     = Mathf.Clamp01(Mathf.InverseLerp(0.85f, 0f, dot));
                    exitCap = Mathf.Lerp(baseTargetSpeed, turnCorneringSpeedKph, severity);
                }
            }
        }

        return Mathf.Min(approachCap, exitCap);
    }

    /// <summary>Returns the lateral (right-vector relative) offset to add to
    /// this frame's route-spline position while a nudge-around is in
    /// progress. Profile: ease left over the first out-leg, hold across the
    /// forward leg, ease right back to zero over the final leg — all keyed
    /// off distance travelled so far this maneuver, not time.</summary>
    private Vector3 CurrentUnstuckLateralOffset(Vector3 right)
    {
        if (!_unstuckActive) return Vector3.zero;

        float outLeg  = unstuckLateralOffset;
        float backLeg = unstuckLateralOffset;
        float holdLeg = Mathf.Max(0f, _unstuckLegDistance - outLeg - backLeg);
        float d = _unstuckDistTravelled;

        float lateral;
        if (d < outLeg)
            lateral = Mathf.Lerp(0f, -unstuckLateralOffset, d / Mathf.Max(0.01f, outLeg));
        else if (d < outLeg + holdLeg)
            lateral = -unstuckLateralOffset;
        else
            lateral = Mathf.Lerp(-unstuckLateralOffset, 0f,
                (d - outLeg - holdLeg) / Mathf.Max(0.01f, backLeg));

        // Negative = left of travel direction, matching "5 left" in the spec
        // (travelDir = -transform.forward elsewhere in this file, so "right"
        // passed in should be the route tangent's right-hand vector).
        return right * lateral;
    }

    /// <summary>Nearest other bus inside the same detection box CheckBusAhead
    /// already uses — reused here just to get a reference to signal, not to
    /// duplicate the distance/speed-limit math.</summary>
    private NPCBusController FindNearestBusAhead()
    {
        Vector3 travelDir  = -transform.forward;
        float   lookDist   = Mathf.Max(raycastDistance, minFollowGap + 5f);
        Vector3 boxNear    = transform.position + travelDir * busDetectionStartOffset;
        Vector3 boxCenter  = boxNear + Vector3.up * busDetectionHeightOffset + travelDir * (lookDist * 0.5f);
        Vector3 halfExtents= new Vector3(busDetectionHalfWidth, busDetectionHalfHeight, lookDist * 0.5f);
        Quaternion boxRot  = Quaternion.LookRotation(travelDir, Vector3.up);

        int hitCount = Physics.OverlapBoxNonAlloc(boxCenter, halfExtents, _busOverlapBuf, boxRot,
                                                   busDetectionMask, QueryTriggerInteraction.Collide);
        NPCBusController nearest = null;
        float nearestDist = float.MaxValue;
        for (int i = 0; i < hitCount; i++)
        {
            Collider col = _busOverlapBuf[i];
            if (col == null || IsOwnCollider(col)) continue;
            var otherBus = GetCachedBus(col);
            // Articulated rear sections have solid colliders but no Rigidbody of their own; only triggers are skipped.
            if (otherBus == null || (col.attachedRigidbody == null && col.isTrigger)) continue;
            if (otherBus == this || otherBus.busID == busID) continue;
            float dist = Vector3.Distance(transform.position, col.ClosestPoint(transform.position));
            if (dist < nearestDist) { nearestDist = dist; nearest = otherBus; }
        }
        return nearest;
    }

    // [REMOVED] CheckBusAhead() — merged into CheckForwardObstacles() above.

    // ═════════════════════════════════════════════════════════════════════════
    //  FOLLOW ROUTE
    // ═════════════════════════════════════════════════════════════════════════
    private float _awarenessSpeedLimit = float.MaxValue;
    private float gearHoldTimer        = 0f;

    // ── Smooth accel/brake blend ─────────────────────────────────────────────
    // [FIX — smoother movement] Both FollowRoute and DriveTowardSmooth used
    // to pick desiredAccel/bkPd from three hard branches on `gap` (targetSpeed
    // - spd): gap>+2, gap<-2, else. That "else" branch had a fixed
    // desiredAccel/bkPd regardless of how close gap actually was to either
    // boundary, so crossing gap==-2 snapped both values instantly instead of
    // easing -- a real discontinuity, not just a coarse approximation. This
    // is now one continuous function shared by both call sites: accelWeight
    // ramps smoothly from 0 to 1 across gap in [-2, +2] instead of switching,
    // so the accel/brake handoff is always a glide.
    private float ComputeSmoothAccelBrake(float gap, float gain, float brakeRate, out float desiredBrake)
    {
        float accelWeight = Mathf.Clamp01(gap / 4f + 0.5f); // 0 at gap<=-2, 1 at gap>=+2, smooth ramp between
        float holdAccel    = 0.15f * gain;                   // gentle cruise-hold accel (old "else" branch value)
        float pushAccel    = Mathf.Clamp01(gap / 20f * gain);
        float desiredAccel = Mathf.Lerp(0f, Mathf.Max(holdAccel, pushAccel), accelWeight);
        desiredBrake        = Mathf.Lerp(Mathf.Clamp01(-gap / brakeRate), 0f, accelWeight);
        return desiredAccel;
    }

    private void FollowRoute()
    {
        // Player buses never enter any AI branch below — not disabled, not
        // skipped-per-state, just a hard fork at the top of the tick.
        if (isPlayer) { HandlePlayerDriving(BusFixedDt); return; }

        if (State == BusState.ExpressDeadRun) { DoExpressDeadRun(); return; }
        if (State == BusState.AIFreeRoam)     { DoAIFreeRoam();     return; }
        if (State == BusState.DeadRunning)    { DeadRun();          return; }
        if (State == BusState.DepotEgress)    { DriveDepotEgress(); return; }
        if (State == BusState.DepotIngress)   { DriveDepotIngress(); return; }
        if (State == BusState.WaitingAtDepot) { spd = 0f; accel = 0f; bkPd = 1f; DepotWaitCheck(); return; }
        if (State == BusState.DrivingToFuelStation)    { DriveToFuelStation();    return; }
        if (State == BusState.AtFuelStation)           { spd = 0f; accel = 0f; bkPd = 1f; FuelStopCheck();       return; }
        if (State == BusState.DrivingToMaintenanceBay) { DriveToMaintenanceBay(); return; }
        if (State == BusState.AtMaintenanceBay)        { spd = 0f; accel = 0f; bkPd = 1f; MaintenanceStopCheck(); return; }

        if (State == BusState.AtStop || State == BusState.Idle)
        {
            spd = 0f; accel = 0f; bkPd = 1f;
            _rb.linearVelocity = Vector3.zero;
            ApplyBusLights();
            return;
        }

        if (_segments == null || _segments.Count == 0)
        {
            Debug.LogWarning($"[Bus#{busID}] FollowRoute: no segments");
            return;
        }

        if (BusBreakdownSystem.Instance != null && BusBreakdownSystem.Instance.IsBusLocked(busID))
        {
            spd = 0f; accel = 0f; bkPd = 1f;
            _rb.linearVelocity = Vector3.zero;
            return;
        }

        if (State == BusState.TerminalIngress) { DriveTerminalIngress(); return; }
        if (State == BusState.TerminalEgress)  { DriveTerminalEgress();  return; }

        if (_currentSegmentIdx >= _segments.Count)
        {
            StartCoroutine(TerminalDwell());
            return;
        }

        // Fast-forward past any upcoming non-terminal stop that has zero
        // planned boarding AND nothing in the pre-rolled alighting intent —
        // so the approach-speed calc below never even sees it as a target.
        // GetAlightingCount is a pure read (alighting was pre-rolled once
        // per trip in RollTrip, not live RNG here), so checking it per-frame
        // costs nothing and never flickers the decision.
        if (State == BusState.InService && _stopSequence != null)
        {
            while (_nextStopIdx < _stopSequence.Count)
            {
                var upcoming = _stopSequence[_nextStopIdx];
                // Don't rely on isTerminal alone — if it doesn't reliably
                // mark the true last stop of THIS leg's sequence, a run of
                // zero-demand stops near the end would walk _nextStopIdx
                // straight past the array, silently killing all braking for
                // the rest of the trip (ComputeApproachSpeed bounds-checks
                // and just returns "not approaching" forever after that —
                // no crash, no more log lines, bus just never stops again).
                // The literal last index is always unskippable regardless.
                bool isLastStop = _nextStopIdx == _stopSequence.Count - 1;
                if (upcoming.isTerminal || isLastStop) break;

                int upcomingBoarding  = _paxPlan != null ? _paxPlan.PaxAt(_nextStopIdx) : 0;
                int upcomingAlighting = _paxPlan != null ? _paxPlan.GetAlightingCount(_nextStopIdx, onboardPax) : 0;
                if (upcomingBoarding > 0 || upcomingAlighting > 0) break; // real activity predicted — approach normally

                if (verboseLogging) Debug.Log($"[{upcoming.stopCode}] Flag-stop skip — no boarding or alighting, staying at cruise speed.");
                _nextStopIdx++;
            }
        }

        // [FIX — more varied/smoother movement] _segmentSpeedJitter is
        // rerolled once per segment (SampleSpeedJitter, called at every
        // _currentSegmentIdx++ elsewhere in this file), which made cruise
        // speed a staircase -- a fixed multiplier for the whole segment,
        // then an instant step to a new one at the next boundary. That reroll
        // point is unchanged (still one roll per segment, still the same
        // variety source), but what's actually consumed here now eases
        // toward it over a couple of seconds instead of jumping, so the
        // speed variation reads as a continuous organic drift rather than a
        // per-segment snap.
        _segmentSpeedJitterSmooth += (_segmentSpeedJitter - _segmentSpeedJitterSmooth) * Mathf.Clamp01(BusFixedDt * 0.35f);

        float lateness     = BusScheduler.Instance?.GetLatenessMinutes(busID) ?? 0f;
        // [Count II-b] Schedule-window speed scaling -- an overnight window's
        // faster empty-road pace (or a peak window's slightly slower one, per
        // Count II-a's tripTimeMultiplierPercent) is reflected in how fast the
        // bus actually drives, not just in the scheduling math. Clamped inside
        // GetScheduleSpeedMultiplier itself (BusScheduler.minScheduleSpeedMultiplier/
        // maxScheduleSpeedMultiplier) so an aggressively-scaled window can't push
        // this past what's been playtested against junction timing/collision
        // avoidance.
        float scheduleSpeedMult = BusScheduler.Instance?.GetScheduleSpeedMultiplier(busID) ?? 1f;
        float cruiseSpeed  = baseTargetSpeed
            * personality.speedMultiplier
            * Mathf.Clamp(1f + lateness * 0.05f, 0.8f, maxLatenessSpeedBoost)
            * (1f + _segmentSpeedJitterSmooth)
            * moodSpeedMult
            * scheduleSpeedMult;
        cruiseSpeed = Mathf.Max(5f, cruiseSpeed);

        float targetSpeed = ComputeApproachSpeed(cruiseSpeed, out bool isApproaching, out float distToStop);
        targetSpeed = Mathf.Min(targetSpeed, _awarenessSpeedLimit);
        targetSpeed = Mathf.Min(targetSpeed, _busAheadSpeedLimit);
        targetSpeed = Mathf.Min(targetSpeed, _externalYieldSpeedCap);
        targetSpeed = Mathf.Min(targetSpeed, ComputeTurnSpeedCap()); // [ADD] turn-speed reduction
        _externalYieldSpeedCap = float.MaxValue; // consumed this frame; re-set by whoever's passing us, if anyone
        if (State == BusState.InService && isApproaching)
        {
            Vector3 stopTargetPos = _stopSequence[_nextStopIdx].GetWorldPosition();
            float   headingDot    = Vector3.Dot(stopTargetPos - transform.position, -transform.forward);
            if (distToStop < 12f || (headingDot < 0f && distToStop < 20f))
                State = BusState.Braking;
        }

        if (State == BusState.InService) MaybeBreakDown();
        if (State == BusState.InService) MaybeRequestLowFuelRelief();

        if (State == BusState.Braking)
        {
            accel = 0f;
            bkPd  = Mathf.Lerp(bkPd, 1f, BusFixedDt * 8f);
            if (spd < 0.5f)
            {
                accel = 0f; bkPd = 1f;
                State = BusState.AtStop;
                StartCoroutine(StopDwell());
                return;
            }
        }
        else if (_awarenessSpeedLimit <= 0f || _busAheadSpeedLimit <= 0f)
        {
            accel = 0f;
            bkPd  = Mathf.Lerp(bkPd, 1f, BusFixedDt * 6f);
        }
        else
        {
            float gap  = targetSpeed - spd;
            float gain = personality.AccelGain;
            float desiredAccel = ComputeSmoothAccelBrake(gap, gain, personality.BrakeRate, out float desiredBrake);
            bkPd = desiredBrake;
            // Rate-limited, not hard-assigned -- see maxAccelRatePerSec tooltip.
            // A single-frame flicker in targetSpeed/gap can no longer snap
            // accel from 0 straight to full open in the same tick.
            accel = Mathf.MoveTowards(accel, desiredAccel, maxAccelRatePerSec * BusFixedDt);
        }

        // [REDESIGN] This used to be a hand-rolled final override, duplicated
        // (with drift) in PlayerHandoff for the player's own bus. Now it's
        // one call into the single shared method that ALSO handles doors,
        // sound flags, and engine/battery/AC shutdown -- BusSimulationController
        // calls this exact same method for the player, so there is only one
        // place this logic can ever go wrong instead of two.
        if (BusBreakdownSystem.Instance != null)
        {
            BusBreakdownSystem.Instance.ApplyBreakdownToVehicle(
                busID, audioEngine, frontDoorSet, ref doorsOpen,
                ref spd, ref accel, ref bkPd, ref _breakdownDoorsHandled, BusFixedDt,
                out _breakdownEngineForceOff, out _breakdownBatteryKilled, out _breakdownAcKilled);
        }

        if (spd < 0.1f) spd = 0f;

        // ── Speed-stall watchdog ─────────────────────────────────────────
        // [FIX] Route progress (_segmentT below) is driven entirely by `spd`,
        // and X/Z position re-locks onto the spline every tick regardless of
        // any physical shove -- so a collision can't actually knock this bus
        // off its path. What it CAN do is leave _busAheadSpeedLimit/
        // _awarenessSpeedLimit pinned near 0 (e.g. the impact spins this
        // bus's forward detection box onto whatever hit it), and nothing
        // ever timed that out -- a bus that landed there simply never moved
        // again. Mid-turn is the easiest place to land in that dead zone,
        // since turnCorneringSpeedKph already has spd low, so a hit right
        // then has an easy time pinning it the rest of the way to 0. Mirrors
        // the rollover watchdog just below: only steps in after a genuinely
        // sustained (5s+) stall with nothing legitimate holding it there
        // (not a red light, not a scheduled stop) -- never a normal brief
        // slow-down or an actual traffic queue.
        bool wantsToMove = targetSpeed > 1f && State != BusState.AtStop && !_blockedByLight;
        if (wantsToMove && spd < 0.5f)
        {
            _speedStallTimer += BusFixedDt;
            if (_speedStallTimer >= 5f)
            {
                _busAheadSpeedLimit  = float.MaxValue;
                _awarenessSpeedLimit = float.MaxValue;
                _blockedByBus        = false;
                accel = 0.3f; bkPd = 0f; spd = 3f; // direct nudge -- MoveTowards ramping alone
                                                    // would just re-stall next tick off whatever
                                                    // pinned it, since we don't know the exact cause.
                _speedStallTimer = 0f;
                if (verboseLogging) Debug.LogWarning($"[Bus#{busID}] Stalled (speed pinned near 0, nothing legitimate blocking it) for 5+s — forcing recovery.");
            }
        }
        else
        {
            _speedStallTimer = 0f;
        }

        var seg = _segments[_currentSegmentIdx];
        float distThisFrame = (spd / 3.6f) * BusFixedDt;
        float segLen        = Mathf.Max(0.1f, seg.Length);
        _segmentT += distThisFrame / segLen;

        if (_segmentT >= 1f)
        {
            _segmentT = 0f;
            _currentSegmentIdx++;
            _segmentSpeedJitter = SampleSpeedJitter();
        }
        else
        {
            Vector3 pos = seg.Evaluate(_segmentT);
            // [FIX] This used to also pull pos.y straight from the route
            // spline, then re-clamp it against a fresh downward raycast every
            // frame, and MovePosition the rigidbody straight to that Y. That's
            // two different systems fighting over height on top of whatever
            // gravity/the wheel colliders were doing to the rigidbody in the
            // same physics step — a losing battle no matter which one "wins"
            // on a given frame. Route data now only drives X/Z. Y is left
            // completely alone here — gravity pulls the bus down, its own
            // colliders (wheels included) rest on whatever's actually under
            // them, same as it would for a real vehicle. No floor detection,
            // no raycast, no clamping: if the road climbs a hill, the bus
            // rides up it because it's physically sitting on it, not because
            // any code told it to.
            if (_unstuckActive)
            {
                Vector3 tangentNow = seg.Tangent(_segmentT);
                Vector3 right = Vector3.Cross(Vector3.up, tangentNow).normalized;
                pos += CurrentUnstuckLateralOffset(right);

                // If the offset carries us past the segment our next stop
                // sits on without a normal approach/brake having fired for
                // it, it's being nudged past rather than served — mark it
                // skipped the same way AdvanceNextStopPastSkipped already
                // does elsewhere, instead of leaving it to silently dangle.
                if (_stopSequence != null && _nextStopIdx < _stopSequence.Count && State != BusState.AtStop)
                {
                    var (stopSeg, stopT) = FindNearestSegmentPoint(_stopSequence[_nextStopIdx].GetWorldPosition());
                    bool carriedPast = stopSeg < _currentSegmentIdx ||
                                        (stopSeg == _currentSegmentIdx && stopT < _segmentT - 0.02f);
                    if (carriedPast)
                    {
                        if (verboseLogging) Debug.Log($"[Bus#{busID}] Nudge-around carried past stop idx {_nextStopIdx} — marking skipped.");
                        _nextStopIdx++;
                    }
                }
            }

            Vector3 move = new Vector3(pos.x - transform.position.x, 0f, pos.z - transform.position.z);
            _rb.MovePosition(transform.position + move);

            // ── Fall-through safety net ──────────────────────────────────
            // Y is still 100% physics-driven above (unchanged) — this does
            // NOT compete with gravity/colliders frame-to-frame. It only
            // fires as a one-shot recovery if the bus has ended up
            // implausibly far below where the route says it should be
            // (collider gap, tunneling through a thin mesh at speed, or a
            // spawn that landed before the road collider existed). A real
            // bridge/incline never triggers this — pos.y tracks the route
            // height there too, so the gap stays near zero on legitimate
            // slopes.
            if (transform.position.y < pos.y - fallRecoveryThreshold)
            {
                Vector3 recoverPos = transform.position;
                recoverPos.y = pos.y + spawnHeightBuffer;
                _rb.position = recoverPos;
                transform.position = recoverPos;
                _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
                Debug.LogWarning($"[Bus#{busID}] Fell through road surface — recovered onto route at Y={recoverPos.y:F2}.");
            }

            Vector3 tangent = seg.Tangent(_segmentT);
            if (tangent != Vector3.zero)
            {
                Quaternion targetRot = Quaternion.LookRotation(tangent, Vector3.up)
                                     * Quaternion.Euler(0, 180, 0);
                _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, targetRot, BusFixedDt * 5f));

                // ── Rollover watchdog ─────────────────────────────────────
                // RotationZ (roll) was deliberately unfrozen so bumps/uneven
                // terrain read as suspension travel instead of feeling
                // rigidly clamped flat (see the constraints comment in
                // Awake). Tradeoff: a hard enough side impact can now roll a
                // bus past recovery, with nothing physically stopping it.
                // Rather than re-freezing Z (back to feeling rigid again),
                // this watches for the roll sitting outside a normal range
                // and only steps in if it's been stuck there — a bump that
                // rocks past 30° and settles back on its own never triggers
                // this; only a genuine flip/stuck-on-its-side case does.
                float roll = NormalizeAngleSigned(transform.eulerAngles.z);
                bool rolledOver = roll > 30f || roll < -60f;
                if (rolledOver)
                {
                    _rolloverTimer += BusFixedDt;
                    if (_rolloverTimer >= 5f)
                    {
                        _rb.MoveRotation(targetRot);
                        _rb.angularVelocity = Vector3.zero;
                        _rb.linearVelocity  = Vector3.zero;
                        _rolloverTimer = 0f;
                        Debug.LogWarning($"[Bus#{busID}] Stuck rolled over (roll={roll:F1}°) for 5+s — righted back onto the route.");
                    }
                }
                else
                {
                    _rolloverTimer = 0f;
                }
            }
        }
        ApplyBusLights();
    }

    private float _rolloverTimer = 0f;
    // [ADD] See the speed-stall watchdog below.
    private float _speedStallTimer = 0f;

    /// <summary>Unity's transform.eulerAngles is always 0-360; this folds it
    /// to -180..180 so "past 30°" and "past -60°" mean what they look like
    /// instead of e.g. 350° actually being -10°.</summary>
    private static float NormalizeAngleSigned(float angle)
    {
        angle %= 360f;
        if (angle > 180f) angle -= 360f;
        return angle;
    }

    private float _currentDwellTimer    = 0f;
    private bool  _hasProcessedBoarding = false;

    // [FIX — #19] Same fan-out as the ext/int light writes in UpdateAutoLights
    // -- SetDoorOpen only ever reached the first BusInteriorLightController
    // GetComponentInChildren found, so a trailer/articulated section's own
    // interior lights never learned the doors had opened.
    private void SetIntDoorOpenAll(bool open)
    {
        for (int i = 0; i < (_intLightsAll?.Length ?? 0); i++)
            _intLightsAll[i]?.SetDoorOpen(open);
    }

    // [FIX — #19, door half] frontDoorSet/rearDoorSet only ever drove whatever
    // ONE BusDoorSet each was Inspector-dragged onto. A child/articulated
    // section (mid-body door on an artic, say) can carry its own BusDoorSet
    // that isn't either of those two and was never toggled at all. We can't
    // tell "front" from "rear" for a section neither field points at, so any
    // such extra set rides along with whichever side toggles first, and only
    // closes once BOTH sides have called for close (so it doesn't slam shut
    // while the other side is still boarding).
    private BusDoorSet[] _extraDoorSets;
    private BusDoorSet[] ExtraDoorSets()
    {
        if (_extraDoorSets != null) return _extraDoorSets;
        if (_allDoorSets == null || _allDoorSets.Length == 0) return _extraDoorSets = new BusDoorSet[0];
        var list = new System.Collections.Generic.List<BusDoorSet>();
        for (int i = 0; i < _allDoorSets.Length; i++)
        {
            var d = _allDoorSets[i];
            if (d != null && d != frontDoorSet && d != rearDoorSet) list.Add(d);
        }
        return _extraDoorSets = list.ToArray();
    }

    private void OpenFrontDoors()
    {
        frontDoorSet?.Open();
        var extras = ExtraDoorSets();
        for (int i = 0; i < extras.Length; i++) extras[i]?.Open();
    }
    private void CloseFrontDoors()
    {
        frontDoorSet?.Close();
        if (!rearDoorsOpen)
        {
            var extras = ExtraDoorSets();
            for (int i = 0; i < extras.Length; i++) extras[i]?.Close();
        }
    }
    private void OpenRearDoors()
    {
        rearDoorSet?.Open();
        var extras = ExtraDoorSets();
        for (int i = 0; i < extras.Length; i++) extras[i]?.Open();
    }
    private void CloseRearDoors()
    {
        rearDoorSet?.Close();
        if (!doorsOpen)
        {
            var extras = ExtraDoorSets();
            for (int i = 0; i < extras.Length; i++) extras[i]?.Close();
        }
    }

    private void ApplyBusLights()
    {
        if (lightController != null)
        {
            lightController.headlightsOn = State == BusState.InService
                                        || State == BusState.Braking
                                        || State == BusState.DeadRunning
                                        || State == BusState.ExpressDeadRun;
            lightController.isBraking    = State == BusState.Braking || bkPd > 0.3f || spd < 0.5f;
        }

        // Player buses drive their own lights via manual input elsewhere —
        // this is the AI half only.
        if (!isPlayer) UpdateAutoLights();
    }

    /// <summary>True between nightStartMinuteOfDay and nightEndMinuteOfDay
    /// (wraps past midnight). No SimClock yet (very first frames) reads as
    /// day — headlights/all-interior stay off until real game time exists.</summary>
    private bool IsNightNow()
    {
        if (SimClock.Instance == null) return false;
        float m = SimClock.Instance.AbsoluteGameMinutes % 1440f;
        if (m < 0f) m += 1440f;
        return nightStartMinuteOfDay <= nightEndMinuteOfDay
            ? (m >= nightStartMinuteOfDay && m < nightEndMinuteOfDay)
            : (m >= nightStartMinuteOfDay || m < nightEndMinuteOfDay);
    }

    /// <summary>Drives BusExteriorLightController/BusInteriorLightController
    /// off this bus's actual driving state — headlights at night while
    /// moving, turn signals inferred from real yaw rate (NPC buses have no
    /// steering-wheel angle to read, only how fast they're actually
    /// rotating), hazards while approaching a stop (State.Braking), and
    /// interior lights forced AllOn at night / dayInteriorLightMode by day.
    /// Entirely optional per bus — every branch below no-ops if that bus
    /// doesn't have the matching component.</summary>
    private void UpdateAutoLights()
    {
        bool night = IsNightNow();

        if (_extLights != null)
        {
            bool moving = State == BusState.InService || State == BusState.Braking
                       || State == BusState.DeadRunning || State == BusState.ExpressDeadRun
                       || State == BusState.DepotEgress || State == BusState.DepotIngress
                       || State == BusState.TerminalEgress;

            // Turn signals — yaw rate is signed by Unity's standard
            // left-handed Y-axis convention (positive = turning right given
            // how this bus's forward/rotation is set up elsewhere in this
            // file). If a given rig turns out mirrored, swap the two
            // SetLeftSignal/SetRightSignal calls below rather than the sign.
            float dt = Mathf.Max(BusFixedDt, 0.0001f);
            float yawNow = transform.eulerAngles.y;
            float yawRate = Mathf.DeltaAngle(_lastFacingYaw, yawNow) / dt;
            _lastFacingYaw = yawNow;

            bool turningRight = yawRate >  TURN_SIGNAL_YAW_RATE_THRESHOLD;
            bool turningLeft  = yawRate < -TURN_SIGNAL_YAW_RATE_THRESHOLD;
            _turnSignalHoldTimer = (turningLeft || turningRight)
                ? TURN_SIGNAL_HOLD_SECONDS
                : Mathf.Max(0f, _turnSignalHoldTimer - dt);

            // Hazards — approaching a stop.
            bool approachingStop = State == BusState.Braking;

            // [FIX — #19] Fan out to every exterior light controller found
            // under this bus (root section + any child/trailer section),
            // not just the first one GetComponentInChildren happened to
            // resolve. State checks (HazardsOn) still read off the primary
            // _extLights -- every controller here is driven to the same
            // target state, so they never disagree with each other.
            for (int i = 0; i < (_extLightsAll?.Length ?? 0); i++)
            {
                var lc = _extLightsAll[i];
                if (lc == null) continue;
                lc.SetHeadlights(night && moving);
                lc.SetBrake(State == BusState.Braking || bkPd > 0.3f || spd < 0.5f);
                if (!_extLights.HazardsOn)
                {
                    if (_turnSignalHoldTimer > 0f && turningLeft)       lc.SetLeftSignal(true);
                    else if (_turnSignalHoldTimer > 0f && turningRight) lc.SetRightSignal(true);
                    else if (_turnSignalHoldTimer <= 0f)
                    {
                        lc.SetLeftSignal(false);
                        lc.SetRightSignal(false);
                    }
                }
                if (approachingStop != _extLights.HazardsOn) lc.SetHazards(approachingStop);
            }
        }

        for (int i = 0; i < (_intLightsAll?.Length ?? 0); i++)
            _intLightsAll[i]?.SetMode(night ? BusInteriorLightController.InteriorLightMode.AllOn : dayInteriorLightMode);
    }

    /// <summary>Polled every tick (see ManagedTick). Shuts the engine off,
    /// then the battery ~1.5s later, once a bus has sat parked
    /// (idle-zone bay or waiting at depot) for idleShutdownDelaySeconds —
    /// stops the DSP's idle/battery-cutoff transition sound from firing
    /// the instant the bus parks. Restores BOTH running and battery power
    /// the moment the bus starts its departure drive again — running can't
    /// be assumed already-restored by the departure path itself; only
    /// DepotEgress/some InService transitions set it explicitly, and
    /// TerminalEgress (the idle-zone departure path) never did.</summary>
    private void UpdateIdleEngineShutdown(float dt)
    {
        bool parkedIdle = (State == BusState.AtTerminal && _activeIdleZone != null && _claimedBayIdx >= 0)
                        || State == BusState.WaitingAtDepot;

        if (!parkedIdle)
        {
            _idleParkedTimer = 0f;
            bool departing = State == BusState.TerminalEgress || State == BusState.DepotEgress || State == BusState.InService;
            if (departing)
            {
                // [FIX] Only DepotEgress/some InService transitions explicitly
                // set running = true on their own — TerminalEgress (the
                // idle-zone departure path) never did. Without this, a bus
                // shut down by this same system would start driving out of
                // the terminal with its engine flag still stuck false.
                if (!running) running = true;
                if (audioEngine != null && !audioEngine.batteryOn && !_breakdownBatteryKilled)
                    audioEngine.batteryOn = true;
            }
            return;
        }

        _idleParkedTimer += dt;

        if (_idleParkedTimer >= idleShutdownDelaySeconds && running)
            running = false;

        if (_idleParkedTimer >= idleShutdownDelaySeconds + 1.5f
            && audioEngine != null && audioEngine.batteryOn && !_breakdownBatteryKilled)
            audioEngine.batteryOn = false;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PLAYER DRIVING  (isPlayer == true)
    //
    //  Everything below is the merged BusSimulationController movement logic.
    //  It runs INSTEAD of FollowRoute()/state-machine AI — not layered on top
    //  of it — so there is exactly one movement code path per mode, sharing
    //  the same engine/audio tick, articulation, and pax/skip-stop logic that
    //  NPC buses use.
    // ═════════════════════════════════════════════════════════════════════════
    private void PollPlayerInput()
    {
        UpdatePlayerStopTracking();

        if (Input.GetKeyDown(KeyBindings.Current.gearReverse)) { currentDirection = GearDirection.Reverse; gear = 0; spd = 0; }
        if (Input.GetKeyDown(KeyBindings.Current.gearNeutral)) { currentDirection = GearDirection.Neutral; gear = 0; spd = 0; }
        if (Input.GetKeyDown(KeyBindings.Current.gearDrive)) { currentDirection = GearDirection.Drive;   gear = 0; spd = 0; }

        // [RESTORED — the previous "duplicate toggle" theory was wrong] The
        // real bug wasn't two scripts toggling the same key and cancelling
        // out -- it's that BusController's Update() ran EVERY frame
        // regardless of who's actually driving, racing this script's own
        // per-frame writes to the shared audioEngine (see the fix now in
        // BusController.cs's Update(), which steps aside entirely whenever
        // this script's isPlayer is true). With that root-caused, THIS
        // script is the real, sole driving authority during player control
        // ("scheduling is completely bypassed in favor of
        // HandlePlayerDriving()"), so its own P-key toggle belongs here.
        if (Input.GetKeyDown(KeyBindings.Current.parkingBrake))
        {
            parkingBrake = !parkingBrake;
            Debug.Log($"<color=cyan>[Parking Brake]</color> {(parkingBrake ? "<color=red>APPLIED</color>" : "<color=green>RELEASED</color>")}");
        }

        // [FIX] Used to unlock the instant doorsOpen/rearDoorsOpen flipped false —
        // i.e. the moment Close() was CALLED, not when the door leaf actually
        // finished swinging shut. A bus could pull away mid-animation with the
        // door still visibly open. Now gated on DoorsFullyClosed, which only
        // goes true once every leaf reports DoorState.Closed.
        bool drivingLocked = !DoorsFullyClosed || parkingBrake;
        kickdownKey = !drivingLocked && Input.GetKey(KeyBindings.Current.kickdown);

        if (!drivingLocked)
        {
            if (currentDirection == GearDirection.Drive)
            {
                accelKey = KeyBindings.ThrottleHeld;
                brakeKey = KeyBindings.BrakeHeld;
            }
            else if (currentDirection == GearDirection.Reverse)
            {
                accelKey = KeyBindings.BrakeHeld;
                brakeKey = KeyBindings.ThrottleHeld;
            }
            else
            {
                accelKey = false;
                brakeKey = KeyBindings.ThrottleHeld || KeyBindings.BrakeHeld;
            }
        }
        else
        {
            accelKey = false;
            brakeKey = false;
            spd      = 0f;
        }
    }

    /// <summary>Player-mode equivalent of FollowRoute() — reads the cached
    /// input state from PollPlayerInput(), drives the accel/brake ramp via
    /// the SAME UpdateSimulationTick used by NPC buses, then moves the
    /// tractor transform directly (no Rigidbody route-following, no state
    /// machine). Articulation/bellows still run every tick via UpdateTrailer,
    /// called by ManagedFixedTick right after this.</summary>
    private void HandlePlayerDriving(float dt)
    {
        // [REVERTED] Originally added a breakdown roll/apply here, on the
        // assumption this method was the live player-driving authority.
        // It isn't for this project's actual setup — BusSimulationController
        // (BusController.cs) is the real driver: its own Update()/FixedUpdate()
        // already call TryRollBreakdown/ApplyBreakdownToVehicle (search
        // "[ADD] Same unified breakdown effect" there). Adding the same
        // calls here too would run BOTH every physics tick for the same
        // physical bus — the exact double-authority fight Update() already
        // had to be fixed for once (see BusController.Update()'s own
        // isPlayer early-return comment) — so this stays untouched and the
        // real fix goes in BusController.cs instead.
        bool drivingLocked = !DoorsFullyClosed || parkingBrake;

        if (!drivingLocked)
        {
            float speedFactor     = Mathf.Clamp01(speedForMaxSteer / Mathf.Max(spd, 0.1f));
            float dynamicMaxAngle = maxSteerAngle * speedFactor;
            float rawInput        = Input.GetAxis("Horizontal");
            float targetAngle     = rawInput * dynamicMaxAngle;
            _playerSteerAngle     = Mathf.Lerp(_playerSteerAngle, targetAngle, dt * steerResponseSpeed);

            Transform tractor = frontPivot != null ? frontPivot : transform;

            if (spd > 0.01f)
            {
                float steeringRad = _playerSteerAngle * Mathf.Deg2Rad;
                if (Mathf.Abs(_playerSteerAngle) > 0.1f)
                {
                    float turnRadius      = wheelbase / Mathf.Sin(steeringRad);
                    float angularVelocity = (spd / 3.6f) / turnRadius;
                    tractor.Rotate(Vector3.up, angularVelocity * Mathf.Rad2Deg * dt, Space.World);
                }

                float mps = spd / 3.6f;
                tractor.Translate(Vector3.forward * mps * (currentDirection == GearDirection.Reverse ? 1 : -1) * dt, Space.Self);
            }

            if (_rb != null) _rb.position = transform.position; // keep cached rigidbody position in sync
        }

        ApplyBusLights();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PLAYER STOP DETECTION  (CityManager-driven proximity list)
    //
    //  This is a DIFFERENT mechanism than the NPC's _stopSequence/BusStopData
    //  route system. CityManager scans nearby spawned stop markers by radius
    //  and pushes them here as plain Transforms — the player bus doesn't
    //  necessarily have an assigned BusScheduler route/slot, so it can't rely
    //  on _stopSequence being populated. Ported over unchanged from the old
    //  BusSimulationController; only the door-open hook now calls the shared
    //  ProcessStopArrival() instead of its own separate pax roll.
    // ═════════════════════════════════════════════════════════════════════════
    [Header("Player Stop Zone (used only when isPlayer)")]
    public float stopZoneX = 10f;
    public float stopZoneZ = 10f;
    public float stopDetectRadius = 50f;

    private Transform[]  busStops       = Array.Empty<Transform>();
    private int          nextStopIndex  = -1;
    private float        distToNextStop = float.MaxValue;
    private HashSet<int> announcedStops = new HashSet<int>();

    /// <summary>Called by CityManager.RefreshNearbyStops() whenever the set of
    /// stop markers within range of the player bus changes. NPC buses never
    /// call this — they get their stops from BusScheduler/_stopSequence.</summary>
    public void UpdateStopList(List<Transform> newStopList)
    {
        if (!isPlayer) return;
        busStops = newStopList != null ? newStopList.ToArray() : Array.Empty<Transform>();
        announcedStops.Clear();
    }

    private bool PlayerAtStop
    {
        get
        {
            if (nextStopIndex < 0 || nextStopIndex >= busStops.Length || busStops[nextStopIndex] == null)
                return false;
            // [FIX] Was also checking Mathf.Abs(delta.y) <= stopZoneY. Height
            // is entirely physics-driven now (gravity + wheel colliders), so
            // it can legitimately be off from a stop's baked/flattened height
            // even while the bus is correctly parked there horizontally.
            // X/Z only — Y is treated as infinitely tall/deep and never
            // checked.
            Vector3 delta = transform.position - busStops[nextStopIndex].position;
            return Mathf.Abs(delta.x) <= stopZoneX
                && Mathf.Abs(delta.z) <= stopZoneZ;
        }
    }

    /// <summary>Re-evaluates which stop is "next" from the current busStops
    /// list, purely by nearest-within-radius — call every frame while
    /// isPlayer, same cadence the old BusSimulationController.Update() used.</summary>
    private void UpdatePlayerStopTracking()
    {
        if (busStops == null || busStops.Length == 0) { nextStopIndex = -1; return; }

        float best    = float.MaxValue;
        int   bestIdx = -1;
        for (int i = 0; i < busStops.Length; i++)
        {
            if (busStops[i] == null) continue;
            // [FIX] Was Vector3.Distance (3D, including Y) — same height
            // mismatch problem as PlayerAtStop/RefreshNearbyStops. X/Z only.
            Vector3 delta = transform.position - busStops[i].position;
            delta.y = 0f;
            float dist = delta.magnitude;
            if (dist < stopDetectRadius && dist < best) { best = dist; bestIdx = i; }
        }

        nextStopIndex  = bestIdx;
        distToNextStop = best;

        if (nextStopIndex >= 0 && !announcedStops.Contains(nextStopIndex))
        {
            announcedStops.Add(nextStopIndex);
            _playerHasProcessedStopArrival = false; // fresh stop — allow ProcessStopArrival to fire again
        }

        if (nextStopIndex >= 0 && distToNextStop > stopDetectRadius + 10f)
            announcedStops.Remove(nextStopIndex);
    }

    /// <summary>Door key handler for the player bus. Mirrors NPC dwell
    /// behavior: opening doors at a stop processes arrival (alight + board,
    /// with skip-stop support); opening doors away from a stop is a no-op
    /// pax-wise. Closing doors just starts the close animation — actual
    /// driving stays locked until DoorsFullyClosed reports true.</summary>
    private void HandlePlayerDoorToggle(bool front)
    {
        if (front)
        {
            doorsOpen = !doorsOpen;
            if (doorsOpen) OpenFrontDoors(); else CloseFrontDoors();
        }
        else
        {
            rearDoorsOpen = !rearDoorsOpen;
            if (rearDoorsOpen) OpenRearDoors(); else CloseRearDoors();
        }

        if (front && doorsOpen && PlayerAtStop)
        {
            string stopLabel = busStops[nextStopIndex].name;
ProcessStopArrival(
    stopLabel,
    ref _playerHasProcessedStopArrival,
    0);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SHARED STOP ARRIVAL — pax roll + skip-stop
    //
    //  Used by BOTH modes:
    //    · NPC:    called from StopDwell() below, in place of its old
    //              PaxSimManager-only block. Passes stop.stopCode.
    //    · Player: called from HandlePlayerDoorToggle() above. Passes the
    //              stop marker's Transform.name (player stops aren't
    //              BusStopData — see PLAYER STOP DETECTION above).
    //
    //  Behavior:
    //    1. If PaxSimManager is available, use the time-of-day-aware
    //       boarding sequence (existing NPC path) keyed by stopCode.
    //    2. Otherwise fall back to the flat "roll 0-5 boarding, ~65% chance
    //       of 1-4 alighting" logic that used to live only on the player's
    //       BusSimulationController — so a stop is never silently a no-op
    //       just because the richer systems aren't present in a given scene.
    //    3. Skip-stop: if nothing boards and nothing alights, returns false
    ///      so the caller (StopDwell) can bypass the door-open/dwell cycle
    ///      entirely instead of just logging a no-op after already
    ///      committing to it.
    /// </summary>
private bool ProcessStopArrival(
    string stopCode,
    ref bool hasProcessedFlag,
    int plannedBoarding,
    int stopIndex = -1)
    {
        if (hasProcessedFlag || string.IsNullOrEmpty(stopCode)) return false;
        hasProcessedFlag = true;
int alighting;
int boarding  = Mathf.Max(0, plannedBoarding);

if (stopIndex >= 0 && _paxPlan != null)
{
    // Real pax plan available (NPC path, real BusStopData index) — use its
    // pre-rolled alighting intent, terminal-aware (forces full clearance
    // at the last stop, no chance involved).
    alighting = _paxPlan.GetAlightingCount(stopIndex, onboardPax);
}
else
{
    // No plan/index to work with (player path — player stops aren't
    // BusStopData, see class comment above) — original flat fallback.
    alighting = UnityEngine.Random.value < 0.4f
        ? UnityEngine.Random.Range(1, Mathf.Min(onboardPax, 4) + 1)
        : 0;
}

if (PaxSimManager.Instance != null)
    boarding = Mathf.Min(
        boarding,
        PaxSimManager.Instance.CountWaiting(stopCode));

// onboardPax was previously never actually updated by boarding/alighting
// anywhere in this class — it sat frozen at whatever Random.Range(0,8)
// gave it at spawn, for the bus's entire life. That silently broke both
// the alighting roll (checked against a number with no relation to
// reality) and the upstream skip-stop pre-check (onboardPax>0 stayed true
// forever for ~7/8 of buses, so they kept approaching/braking for stops
// they had no one left to serve). This is the one place both final
// (PaxSimManager-clamped) numbers are known — update it here.
onboardPax = Mathf.Max(0, onboardPax + boarding - alighting);

        if (alighting == 0 && boarding == 0)
        {
if (verboseLogging) Debug.Log($"[{stopCode}] No passengers boarded or alighted.");
            return false;
        }
        return true;
    }

    /// <summary>Onboard pax count — tracked here now for both modes instead
    /// of only on the old player-only BusSimulationController.</summary>
    public int onboardPax = 0;

    // ═════════════════════════════════════════════════════════════════════════
    //  DEAD-RUN
    // ═════════════════════════════════════════════════════════════════════════
    private void DeadRun()
    {
        Vector3 dir  = _deadRunTarget - transform.position; dir.y = 0f;
        float dist = dir.magnitude;
        if (dist < 3f) { spd = 0f; accel = 0f; bkPd = 1f; State = BusState.Idle; return; }
        float targetSpeed = baseTargetSpeed * 0.8f;
        if (spd < targetSpeed) { accel = 1f; bkPd = 0f; }
        _rb.MovePosition(transform.position + dir.normalized * (spd / 3.6f) * Time.fixedDeltaTime);
        if (dir != Vector3.zero)
            _rb.MoveRotation(Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0f, 180f, 0f));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  APPROACH SPEED / BRAKING PHYSICS
    // ═════════════════════════════════════════════════════════════════════════
    private float GetEffectiveDecelKmhPerSec()
    {
        bool isArticulated = articulationMode != ArticulationMode.None || trailerPivot != null;
        return isArticulated ? 10f : 13f;
    }

    private float ComputeRequiredStopDistance()
    {
        float decel = GetEffectiveDecelKmhPerSec();
        return (spd * spd) / (7.2f * decel);
    }

    private float ComputeBrakingSpeedForDistance(float dist)
    {
        float decel = GetEffectiveDecelKmhPerSec();
        return Mathf.Sqrt(Mathf.Max(0f, 7.2f * decel * Mathf.Max(0f, dist)));
    }

private float ComputeApproachSpeed(float cruiseSpeed, out bool isApproaching, out float distToStop)
{
    isApproaching = false;
    distToStop    = float.MaxValue;

    if (_stopSequence == null || _nextStopIdx >= _stopSequence.Count)
        return cruiseSpeed;

    var stop = _stopSequence[_nextStopIdx];
    if (stop == null)
        return cruiseSpeed;

    // [FIX] Was Vector3.Distance(transform.position, stop.GetWorldPosition())
    // — full 3D distance, including Y. The bus's actual height is now
    // entirely physics-driven (gravity + wheel colliders), while a stop's
    // height comes from the road's baked/flattened surface height — those
    // two Y values can legitimately differ by a bit even when the bus is
    // sitting exactly at the stop horizontally, and on any real elevation
    // change the gap only grows. Comparing full 3D distance meant the bus
    // could be sitting right next to a stop and still register as "far
    // away" purely because of a Y mismatch, so it never braked or dwelled —
    // no buses stopping anywhere. Distance is now X/Z only; Y is treated as
    // infinitely tall/deep and never counted against the bus.
    Vector3 toStopFlat = stop.GetWorldPosition() - transform.position;
    toStopFlat.y = 0f;
    distToStop = toStopFlat.magnitude;

    TrackUnreachableStopWatch(stop, distToStop);

    float brakeStart = Mathf.Max(
        personality.BrakeStartDist,
        ComputeRequiredStopDistance() + 10f);

    if (distToStop > brakeStart)
        return cruiseSpeed;

    isApproaching = true;

    float physSpeed = ComputeBrakingSpeedForDistance(distToStop);

    return Mathf.Clamp(Mathf.Min(cruiseSpeed, physSpeed), 5f, cruiseSpeed);
}

/// <summary>
/// ROUTE DATA ERROR CATCHER: watches whether the bus is actually making
/// progress toward the current _nextStopIdx's stop. If a stop was
/// accidentally given a position/tValue that puts it off the real route
/// geometry (wrong road, wrong side of the road, a bad snap, etc.), the
/// bus's distance to it never drops far enough to trigger Braking, so it
/// never reaches BusState.AtStop, _nextStopIdx never advances past it, and
/// every remaining stop on this trip silently stops being served — forever,
/// with no error anywhere telling you why. This only ever inspects the
/// CURRENT next stop; anything before _nextStopIdx is behind the bus
/// already (it boarded mid-route, or already served it) and is correctly
/// left alone.
///
/// [REVERTED to a plain timeout check] The previous version compared
/// _currentSegmentIdx against the stop's nearest segment (via
/// FindNearestSegmentPoint) to decide "has the bus already driven past
/// this stop's closest point." That tripped false positives on ordinary
/// reachable stops — loops, overlapping segments near junctions, and
/// ambiguous nearest-segment picks near crossings could all satisfy
/// "passed it" immediately even though the bus was still on track to
/// actually reach the stop. This is simpler and far less prone to that:
/// just a plain wall-clock timer on how long the bus has been trying to
/// reach the SAME next stop without ever getting close. No segment
/// position/ordering assumptions at all.
/// </summary>
private void TrackUnreachableStopWatch(BusStopData stop, float distToStop)
{
    if (_watchedStopIdx != _nextStopIdx)
    {
        _watchedStopIdx     = _nextStopIdx;
        _watchedStopMinDist = float.MaxValue;
        _watchedStopTimer   = 0f;
    }

    if (distToStop < _watchedStopMinDist) _watchedStopMinDist = distToStop;

    // Bus is already closing in on it normally — reset the timer so a
    // slow-but-genuine approach (traffic, heavy braking, red lights) never
    // gets mistaken for being stuck.
    if (_watchedStopMinDist <= UNREACHABLE_STOP_DIST_THRESHOLD)
    {
        _watchedStopTimer = 0f;
        return;
    }

    _watchedStopTimer += Time.deltaTime;

    if (_watchedStopTimer > UNREACHABLE_STOP_TIMEOUT)
    {
        Debug.LogWarning($"[Bus#{busID}] ROUTE ERROR — stop '{stop.stopCode}' ({stop.stopName}) on Route " +
                        $"{_route?.routeNumber} is UNREACHABLE from this route's actual path. The bus never " +
                        $"got closer than {_watchedStopMinDist:F0}m to it after {UNREACHABLE_STOP_TIMEOUT:F0}s " +
                        $"of trying — it's almost certainly placed off the real road/route geometry (wrong " +
                        $"road, wrong side, or a bad tValue). Without this catch, this bus would freeze on " +
                        $"this stop forever and never serve ANY stop after it for the rest of the trip. " +
                        $"Skipping the bad stop so the trip continues.");
        _nextStopIdx++;
        _watchedStopIdx = -1; // re-check fresh against whatever is next, in case it's also bad
    }
}
    // ═════════════════════════════════════════════════════════════════════════
    //  STOP / TERMINAL DWELL
    // ═════════════════════════════════════════════════════════════════════════
    private static readonly WaitForSeconds _wait2s = new WaitForSeconds(2f);

    private bool _npcHasProcessedStopArrival = false;
private IEnumerator StopDwell()
{
    State = BusState.AtStop;
    _dwellCoroutineActive = true;
    var   stop      = _stopSequence[_nextStopIdx];
    BusStopBerthRegistry.Register(stop.stopCode, busID);

    // Check pax activity BEFORE committing to the door/dwell cycle — this
    // is the actual skip-stop the ProcessStopArrival design comment always
    // intended (previously it only logged the no-op after doors had
    // already opened and the full dwell had already been paid for).
    _npcHasProcessedStopArrival = false;
    int  plannedBoarding = _paxPlan != null ? _paxPlan.PaxAt(_nextStopIdx) : 0;
    bool hadActivity = ProcessStopArrival(stop.stopCode, ref _npcHasProcessedStopArrival, plannedBoarding, _nextStopIdx);
    bool isLastStop  = _nextStopIdx == _stopSequence.Count - 1;
    bool anyActivity = hadActivity || stop.isTerminal || isLastStop;

    if (!anyActivity)
    {
        // Flag-stop skip: nobody boarding, nobody alighting — real transit
        // doesn't crack the doors open for an empty curb at 3am. Doors stay
        // shut, no dwell paid, stop is simply marked complete.
        if (verboseLogging) Debug.Log($"[{stop.stopCode}] Skip-stop — no boarding/alighting, doors stay closed.");
        BusStopBerthRegistry.Unregister(stop.stopCode, busID);
        _nextStopIdx++;
        _blockedTimer = 0f;
        State = BusState.InService;
        bkPd  = 0f;
        _dwellCoroutineActive = false;
        yield break;
    }

    float baseDwell = (stop.isTerminal ? terminalDwellBase : stopDwellBase) * personality.dwellMultiplier;

    if (mindExtraDwellSeconds > 0f)
    {
        baseDwell += mindExtraDwellSeconds;
        mindExtraDwellSeconds = 0f;
    }
    float floorDwell = BusScheduler.Instance?.GetLatenessAdjustedDwell(busID, baseDwell) ?? baseDwell;

    // Open doors for dwell — front always opens. Rear always opens at
    // terminals; elsewhere it's a per-stop chance (real operators only
    // crack the rear door if someone's actually back there to use it —
    // we don't model passenger position, so a chance stands in for it).
    doorsOpen = true;
    OpenFrontDoors();

    bool openRear = stop.isTerminal || UnityEngine.Random.value < rearDoorOpenChance;
    rearDoorsOpen = openRear;
    if (openRear) OpenRearDoors();

    // [FIX Bug 1] NPC buses never told their interior/exterior light
    // controller that doors had opened — only the possessed player bus
    // did, via PlayerHandoff. Mirror PlayerHandoff's doorsOpen||rearDoorsOpen
    // pattern so NPC buses switch to the ALL-zones lighting config too.
    SetIntDoorOpenAll(doorsOpen || rearDoorsOpen);

    if (floorDwell > 0f) yield return new WaitForSeconds(floorDwell);

    // Signal intent to close...
    doorsOpen = false;
    rearDoorsOpen = false;
    CloseFrontDoors();
    if (openRear) CloseRearDoors();
    SetIntDoorOpenAll(doorsOpen || rearDoorsOpen);

    // ...but don't pull away until the doors are PHYSICALLY closed, not
    // just told to close. Closing is a ~1s animation (BusDoorLeaf.duration);
    // departing the instant Close() is called used to let a bus lurch
    // forward with its doors still visibly swinging shut.
    //
    // [FIX Bug 4] This wait previously had no timeout. If DoorsFullyClosed
    // never reports true (stuck door anim, destroyed door object mid-close,
    // etc.) the coroutine looped forever, never reached Unregister() below,
    // and permanently blocked this stop's berth for every other bus.
    // Mirrors the UNREACHABLE_STOP_TIMEOUT pattern used above in this file:
    // on timeout, force doors closed, force-unregister the berth, log a
    // warning, and proceed rather than freezing the bus (and the berth)
    // forever.
    float doorCloseTimer = 0f;
    while (!DoorsFullyClosed)
    {
        doorCloseTimer += Time.deltaTime;
        if (doorCloseTimer > DOOR_CLOSE_TIMEOUT)
        {
            Debug.LogWarning($"[Bus#{busID}] Doors at stop '{stop.stopCode}' never reported fully " +
                              $"closed after {DOOR_CLOSE_TIMEOUT:F0}s — forcing closed and releasing " +
                              $"the berth so this stop doesn't get permanently blocked for other buses.");
            // NOTE: BusDoorSet isn't among the files I have, so I don't know
            // whether it exposes a hard "force closed" API distinct from
            // Close(). Close() was already called above; here we just stop
            // waiting on it and proceed so the berth isn't held forever.
            // If BusDoorSet does have a force/snap-shut method, call it here
            // instead of relying on the animation to eventually catch up.
            doorsOpen = false;
            rearDoorsOpen = false;
            SetIntDoorOpenAll(false);
            break;
        }
        yield return null;
    }

    BusStopBerthRegistry.Unregister(stop.stopCode, busID);
    _nextStopIdx++;
    _blockedTimer = 0f;
    State = BusState.InService;
    bkPd  = 0f;
    _dwellCoroutineActive = false;
}

    // ═════════════════════════════════════════════════════════════════════════
    //  DEPOT EGRESS / INGRESS
    // ═════════════════════════════════════════════════════════════════════════
    public void StartDepotEgress(List<Vector3> path, TimetableSlot slot, float speedFraction)
    {
        _depotPath          = path ?? new List<Vector3>();
        _depotPathIdx       = 0;
        _depotSpeedFraction = Mathf.Clamp01(speedFraction);
        _depotTargetSlot    = slot;
        _depotPathDone      = false;
        _pendingDepotReturn = false;

        if (slot != null && BusScheduler.Instance != null)
        {
            var route = BusScheduler.Instance.GetRouteData(slot.routeNumber);
            if (route != null) { _route = route; _isOutbound = slot.isOutbound; _currentVariantLetter = slot.variantLetter ?? ""; }
        }

        State = BusState.DepotEgress; running = true; bkPd = 0f;
        if (BusScheduler.Instance != null && slot != null)
            BusScheduler.Instance.RecordActualDeparture(busID, BusScheduler.Instance.GameTimeMinutes);

        if (verboseLogging) Debug.Log($"[Bus#{busID}] Depot egress started. Path waypoints: {_depotPath.Count}");
    }

    private void DriveDepotEgress()
    {
        if (_depotPath == null || _depotPath.Count == 0 || _depotPathIdx >= _depotPath.Count)
        { CompleteDepotEgress(); return; }

        Vector3 target  = _depotPath[_depotPathIdx]; target.y = transform.position.y;
        float   dist    = Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z),
                                            new Vector3(target.x, 0, target.z));
        // [FIX] Was Mathf.Max(3f, spd * 0.1f) -- grew with speed, so the
        // open-road leg (now driven faster, see below) advanced to the NEXT
        // waypoint up to several meters before actually reaching it. These
        // waypoints are a dense, real lane-centered polyline from
        // RoadGraphPathfinder.FindWaypoints -- cutting that early on a curve
        // reads as the bus drifting to the side of the road instead of
        // following it. Fixed, small radius instead; depot paths are short
        // so this costs nothing.
        float   arrivalR= 1.6f;

        if (dist < arrivalR) { _depotPathIdx++; return; }

        bool  insideDepot = _depotPathIdx < 2;
        // [FIX] Open-road depot-return leg now runs DEPOT_RETURN_SPEED_MULTIPLIER
        // (1.5x) a normal in-service cruise, still hard-capped at the same
        // 105 km/h real-world governor baseTargetSpeed itself never exceeds.
        float targetSpeed = insideDepot
            ? baseTargetSpeed * Mathf.Min(_depotSpeedFraction, DEPOT_ROAD_SPEED_FRACTION) * personality.speedMultiplier
            : ApplyDepotTrafficRules(baseTargetSpeed * DEPOT_ROAD_SPEED_FRACTION * personality.speedMultiplier);
        DriveDepotPathTight(target, targetSpeed);
    }

    /// <summary>Depot-path variant of DriveTowardSmooth -- same accel/brake
    /// ramp, but a tighter rotation catch-up rate (matches the smaller,
    /// fixed arrivalR now used by DriveDepotEgress/DriveDepotIngress) so the
    /// bus actually tracks the lane-centered waypoint polyline through turns
    /// instead of cutting toward the inside/outside of the curve. Kept
    /// separate from DriveTowardSmooth rather than changing that shared
    /// method, since it's also used by terminal ingress/egress, detours, and
    /// AI free-roam -- none of which reported this issue.</summary>
    private void DriveDepotPathTight(Vector3 worldTarget, float targetSpeed)
    {
        worldTarget.y = transform.position.y;
        Vector3 dir = worldTarget - transform.position; dir.y = 0f;
        if (dir.sqrMagnitude < 0.001f) return;

        float gap = targetSpeed - spd;
        float gain = personality.AccelGain;
        float desiredAccel = ComputeSmoothAccelBrake(gap, gain, personality.BrakeRate, out float desiredBrake);
        bkPd = desiredBrake;
        accel = Mathf.MoveTowards(accel, desiredAccel, maxAccelRatePerSec * Time.fixedDeltaTime);

        _rb.MovePosition(transform.position + dir.normalized * (spd / 3.6f) * Time.fixedDeltaTime);
        Quaternion targetRot = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0, 180, 0);
        _rb.MoveRotation(Quaternion.RotateTowards(_rb.rotation, targetRot, 140f * Time.fixedDeltaTime));
    }

    private void CompleteDepotEgress()
    {
        if (_depotPath != null && _depotPath.Count > 0)
        {
            Vector3 final = _depotPath[_depotPath.Count - 1]; final.y = transform.position.y;
            _rb.MovePosition(final);
        }
        if (_depotTargetSlot != null && _route != null)
        {
            var variantData = _route.GetVariant(_currentVariantLetter);
            var nodes       = _route.GetNodes(_isOutbound, variantData);
            _segments       = BuildRouteSegments(nodes);
            _stopSequence   = ResolveStopSequence(_isOutbound, _currentVariantLetter);
            _currentSegmentIdx = 0; _segmentT = 0f; _nextStopIdx = 0;
            _flaggedForRotation = false;
            _segmentSpeedJitter = SampleSpeedJitter();
            _destinationBoard
                ?.SetRoute(_route.routeNumber, _isOutbound ? _route.destinationNameOutbound : _route.destinationNameInbound);
        }
        State = BusState.InService; bkPd = 0f;
        if (verboseLogging) Debug.Log($"[Bus#{busID}] Depot egress complete — entering service on Route {_route?.routeNumber}.");
    }

    public void StartDepotIngress(List<Vector3> path, int bayIndex, float speedFraction)
    {
        _depotPath          = path ?? new List<Vector3>();
        _depotPathIdx       = 0;
        _depotSpeedFraction = Mathf.Clamp01(speedFraction);
        _depotTargetBay     = bayIndex;
        _depotPathDone      = false;
        State = BusState.DepotIngress; running = true; bkPd = 0f;
        _destinationBoard?.SetBlank();
        if (verboseLogging) Debug.Log($"[Bus#{busID}] Depot ingress started → bay {bayIndex}. Path waypoints: {_depotPath.Count}");
    }

    private void DriveDepotIngress()
    {
        if (_depotPath == null || _depotPath.Count == 0 || _depotPathIdx >= _depotPath.Count)
        { CompleteDepotIngress(); return; }

        Vector3 target   = _depotPath[_depotPathIdx]; target.y = transform.position.y;
        float   dist     = Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z),
                                             new Vector3(target.x, 0, target.z));
        bool  nearEnd    = _depotPathIdx >= _depotPath.Count - 2;
        // [FIX] Same fixed-radius fix as DriveDepotEgress above -- was
        // speed-scaled and let the bus cut corners on the open-road leg.
        float arrivalR   = nearEnd ? 1.8f : 1.6f;

        if (dist < arrivalR) { _depotPathIdx++; return; }

        bool  insideDepot = _depotPathIdx >= _depotPath.Count - 2;
        // [FIX] Was baseTargetSpeed * 0.8f on the open-road leg -- SLOWER
        // than a normal cruise. Now DEPOT_RETURN_SPEED_MULTIPLIER (1.5x)
        // faster, still hard-capped at the 105 km/h governor.
        float targetSpeed = insideDepot
            ? baseTargetSpeed * Mathf.Min(_depotSpeedFraction, DEPOT_ROAD_SPEED_FRACTION) * personality.speedMultiplier
            : ApplyDepotTrafficRules(baseTargetSpeed * DEPOT_ROAD_SPEED_FRACTION * personality.speedMultiplier);
        if (nearEnd)
        {
            float stopFrac = Mathf.Clamp01(dist / Mathf.Max(1f, targetSpeed * 0.5f));
            targetSpeed    = Mathf.Lerp(targetSpeed * 0.2f, targetSpeed, Mathf.SmoothStep(0f, 1f, stopFrac));
        }
        DriveDepotPathTight(target, targetSpeed);
    }

    private void CompleteDepotIngress()
    {
        var depot = BusDepot.GetDepotForBus(this);
        if (depot != null && _depotTargetBay >= 0)
        {
            Vector3    bayPos = depot.GetBayPosition(_depotTargetBay); bayPos.y = transform.position.y;
            Quaternion bayRot = depot.GetBayRotation(_depotTargetBay);
            _rb.MovePosition(bayPos); _rb.MoveRotation(bayRot);
            ResetArticulationState();
        }
        spd = 0f; accel = 0f; bkPd = 1f;
        // [FIX Bug 2] BusUpdateManager never ticks an Idle bus, and this is
        // the only place a bus transitions into being parked/idle at a
        // depot -- so whatever light state existed at the exact moment it
        // pulled in (a turn signal, hazards, headlights) was never touched
        // again until the bus was redispatched. Reset explicitly here.
        for (int i = 0; i < (_extLightsAll?.Length ?? 0); i++)
        {
            var lc = _extLightsAll[i]; if (lc == null) continue;
            lc.SetHeadlights(false);
            lc.SetLeftSignal(false);
            lc.SetRightSignal(false);
            lc.SetHazards(false);
        }
        for (int i = 0; i < (_intLightsAll?.Length ?? 0); i++)
            _intLightsAll[i]?.SetMode(BusInteriorLightController.InteriorLightMode.Off);

        if (_ingressIsForWait && _depotWaitSlot != null)
        {
            // Still committed to _depotWaitSlot in the scheduler — don't
            // SetIdle/NotifyBusParkedAtDepot, that would drop it into the
            // general idle pool where HandleDispatch's fallback could hand
            // it a second, conflicting slot. WaitingAtDepot just polls for
            // its own scheduled departure and drives itself back out.
            State = BusState.WaitingAtDepot;
            if (verboseLogging) Debug.Log($"[Bus#{busID}] Waiting at depot bay {_depotTargetBay} for Route {_depotWaitSlot.routeNumber} @ {BusScheduler.MinutesToTimeString(_depotWaitSlot.scheduledDeparture)}.");
        }
        else
        {
            SetIdle(true);
            BusManager.Instance?.NotifyBusParkedAtDepot(busID);
            if (verboseLogging) Debug.Log($"[Bus#{busID}] Depot ingress complete — parked in bay {_depotTargetBay}.");
        }
    }

    /// <summary>Polled each tick while WaitingAtDepot — drives the bus back
    /// out for its already-committed slot once the lead window is reached.
    /// The slot was never released, so no scheduler re-assignment is
    /// needed here, just the physical egress.</summary>
    private void DepotWaitCheck()
    {
        if (_depotWaitSlot == null || SimClock.Instance == null) { State = BusState.Idle; return; }

        if (SlotTakenByOther(_depotWaitSlot))
        {
            // Slot was taken over while this bus sat in its bay -- stay parked and go back to the idle pool.
            _depotWaitSlot = null; _ingressIsForWait = false;
            State = BusState.Idle;
            BusManager.Instance?.NotifyBusParkedAtDepot(busID);
            return;
        }

        float gapMinutes = _depotWaitSlot.scheduledDeparture - SimClock.Instance.AbsoluteGameMinutes;
        if (gapMinutes > depotEgressLeadMinutes) return;

        var depot = BusDepot.GetDepotForBus(this);
        Vector3 startPos = transform.position;
        Vector3 gatePos  = depot != null && depot.depotCode == "DEPOT_01" ? depotGatePosition : transform.position;
        var roadPath     = BusPathfinder.BuildPath(startPos, gatePos);
        var fullPath     = new List<Vector3>(roadPath.Count > 0 ? roadPath : new List<Vector3> { startPos });
        fullPath.Add(gatePos);

        var slot = _depotWaitSlot;
        _depotWaitSlot    = null;
        _ingressIsForWait = false;
        _depotTargetBay   = -1;
        StartDepotEgress(fullPath, slot, depotReturnSpeedFraction);
    }

    /// <summary>Relief replacement claimed a slot while this bus was parked.
    /// Returns true if it's parked in a depot bay and now waits there for the
    /// slot's egress lead window (DepotWaitCheck drives it out); false if the
    /// caller should dispatch it normally.</summary>
    public bool BeginReliefDepotWait(TimetableSlot slot)
    {
        if (isPlayer || slot == null) return false;
        if (State == BusState.WaitingAtDepot) { _depotWaitSlot = slot; _ingressIsForWait = true; return true; }
        if (State != BusState.Idle || _depotTargetBay < 0 || BusDepot.GetDepotForBus(this) == null) return false;
        _depotWaitSlot    = slot;
        _ingressIsForWait = true;
        State = BusState.WaitingAtDepot;
        return true;
    }

    public void AbortDepotIngress()
    {
        if (State != BusState.DepotIngress) return;
        _depotPath.Clear(); _depotPathIdx = 0; State = BusState.Idle;
        if (verboseLogging) Debug.Log($"[Bus#{busID}] Depot ingress aborted — re-dispatching.");
    }

    private DepotData ResolveRetirementDepot()
    {
        DepotData depot = homeDepot;
        if (depot == null && DepotManager.Instance != null)
        {
            depot = DepotManager.Instance.GetHomeDepot(fleetNumber);
            if (depot != null)
            {
                homeDepot = depot;
                if (verboseLogging) Debug.Log($"[Bus#{busID}] Assigned home depot from DepotManager: {depot.depotCode}");
            }
        }

        if (depot != null)
        {
            if (allowAlternativeDepotIfHomeFull && DepotManager.Instance != null && depot.GetFreeSpot() == null)
            {
                var alternate = DepotManager.Instance.depots?.Find(d => d != null && d != depot && d.GetFreeSpot() != null);
                if (alternate != null)
                {
                    depot = alternate;
                    homeDepot = alternate;
                    // [FIX] DepotManager._homeDepotByFleet is the actual source
                    // of truth (see SetHomeDepot's own comment) -- this used to
                    // only update the controller's own cached field, leaving
                    // DepotManager still pointing at the old depot.
                    DepotManager.Instance.SetHomeDepot(fleetNumber, alternate);
                    Debug.LogWarning($"[Bus#{busID}] Home depot full — retiring to alternate depot '{alternate.depotCode}'.");
                }
            }
            return depot;
        }

        if (DepotManager.Instance != null)
        {
            depot = DepotManager.Instance.depots?.Find(d => d != null && d.GetFreeSpot() != null);
            if (depot != null)
            {
                homeDepot = depot;
                DepotManager.Instance.SetHomeDepot(fleetNumber, depot); // [FIX] same reasoning as the alternate-depot branch above
                if (verboseLogging) Debug.Log($"[Bus#{busID}] No home depot set; retiring to available depot '{depot.depotCode}'.");
            }
        }
        return depot;
    }

    public void FlagForDepotReturn()  { _pendingDepotReturn = true;  if (verboseLogging) Debug.Log($"[Bus#{busID}] Flagged for depot return."); }
    public void ClearDepotReturnFlag() => _pendingDepotReturn = false;
    public bool IsPendingDepotReturn  => _pendingDepotReturn;

    // ═════════════════════════════════════════════════════════════════════════
    //  TERMINAL DWELL
    // ═════════════════════════════════════════════════════════════════════════
    private IEnumerator TerminalDwell()
    {
        State = BusState.AtTerminal;
        _dwellCoroutineActive = true;
        accel = 0f; bkPd = 1f; spd = 0f;

        RouteVariantData _arrVariant = null;
        if (!string.IsNullOrEmpty(_currentVariantLetter) && _route?.variants != null)
            _arrVariant = _route.variants.Find(v => v != null && v.variantLetter == _currentVariantLetter);

        // FIX: arrivedCode used to be blindly read from route.terminalZCode/
        // terminalACode regardless of where the bus actually stopped. If a
        // trip's resolved _stopSequence ends short of the route's official
        // terminal (an intentional turnback stop, e.g. sequence ends at 14
        // while the route's real terminal is 15), the bus physically ends
        // its leg at 14 but arrivedCode still said "15" — pointing terminal-
        // idle-zone lookups and the depot-retirement decision at the wrong
        // location. arrivedCode now comes from the stop actually last
        // served, falling back to the route-level terminal code only if
        // that isn't available. atRealTerminal tells us whether that stop is
        // actually flagged as a terminal — depot-return gates on that, not
        // merely "ran out of segments."
        BusStopData actualLastStop = (_stopSequence != null && _stopSequence.Count > 0)
            ? _stopSequence[_stopSequence.Count - 1] : null;
        bool atRealTerminal = actualLastStop != null && actualLastStop.isTerminal;

        string arrivedCode = !string.IsNullOrEmpty(actualLastStop?.stopCode)
            ? actualLastStop.stopCode
            : (_isOutbound
                ? (!string.IsNullOrEmpty(_arrVariant?.terminalZCodeOverride)
                    ? _arrVariant.terminalZCodeOverride : _route?.terminalZCode ?? "")
                : (!string.IsNullOrEmpty(_arrVariant?.terminalACodeOverride)
                    ? _arrVariant.terminalACodeOverride : _route?.terminalACode ?? ""));

        // [FIX] Capture the lap BEFORE CompleteSlot runs — CompleteSlot's
        // first action is _slotByBus.Remove(busID), and GetLapCount reads
        // _slotByBus[busID].chainLegIndex. Calling GetLapCount AFTER
        // CompleteSlot (as this used to) reads a slot that's already been
        // removed or replaced, so it printed 0 (or the NEXT leg's lap)
        // almost every time regardless of what the bus actually just
        // completed — that's a diagnostics bug, not a real "lap 0" event.
        int lapJustCompleted = BusScheduler.Instance?.GetLapCount(busID) ?? 0;
        BusManager.Instance?.NotifySegmentComplete(busID);
        BusScheduler.Instance?.CompleteSlot(busID);

        _isOutbound = !_isOutbound;

        // [FIX] Previously, "no follow-up slot assigned" hard-idled the bus
        // right here (State = Idle; yield break) — which skips the entire
        // depot-return block below. That's the ONLY place ResolveRetirementDepot/
        // StartDepotIngress get called, so any bus that finished its chain (by
        // far the most common way a bus ends up with no follow-up slot) just
        // froze in place at the terminal forever instead of deadrunning home.
        // "No follow-up slot" now falls through to the same retirement/depot-
        // return handling as an explicit null slot, instead of short-circuiting.
        TimetableSlot mySlot = null;
        bool hasFollowUpSlot = false;
        
        if (BusScheduler.Instance != null)
        {
            hasFollowUpSlot = BusScheduler.Instance.TryGetAssignedSlot(busID, out mySlot);
            if (hasFollowUpSlot) _isOutbound = mySlot.isOutbound;
            else if (verboseLogging) Debug.Log($"[Bus#{busID}][DEPOT-CAUSE] TryGetAssignedSlot returned FALSE right after CompleteSlot — no follow-up slot exists in the scheduler for this bus. " +
                            $"Route just finished={_route?.routeNumber} lapJustCompleted={lapJustCompleted} fleet#{fleetNumber} at {arrivedCode}. " +
                            $"Treating as end of chain, returning to depot. If the tracker shows an apparently-open trip for this bus/route right now, " +
                            $"check the [BusScheduler][DEPOT-CAUSE] and [BusScheduler][LAP]/[TOPUP] logs emitted the same frame from CompleteSlot() — " +
                            $"that's where the actual denial reason (cap/policy/no-slot-found) is logged.");
        }

        // FIX: "no follow-up slot" used to fall straight into depot-return
        // no matter where the bus actually stopped. A turnback stop that
        // isn't the route's real terminal (isTerminal == false) can still
        // legitimately have no follow-up slot queued yet without meaning
        // "route finished, retire me" — and even if it did mean that, we
        // have no guaranteed depot path/gate from a non-terminal stop.
        // _pendingDepotReturn is an explicit flag (FlagForDepotReturn) and
        // is honored regardless — that's a deliberate retirement request,
        // not an inferred one.
        if (!atRealTerminal && (!hasFollowUpSlot || mySlot == null) && !_pendingDepotReturn)
        {
            Debug.LogWarning($"[Bus#{busID}] Ended leg at non-terminal stop '{arrivedCode}' with no follow-up slot. " +
                              $"Holding at terminal-idle, waiting up to {terminalHoldTimeoutMinutes:0} game-min " +
                              $"for a real follow-up slot before retiring to depot.");

            var holdZone = TerminalIdleZone.GetForStop(arrivedCode);
            int holdBay  = holdZone != null ? holdZone.TryClaim(busID) : -1;
            if (holdZone != null && holdBay >= 0)
            {
                _activeIdleZone = holdZone; _claimedBayIdx = holdBay;
                _idlePath    = holdZone.GetIngressPath(holdBay, transform.position.y);
                _idlePathIdx = 0; _idleIngressDone = false;
                if (_idlePath != null && _idlePath.Count > 0)
                {
                    State = BusState.TerminalIngress; bkPd = 0f;
                    while (!_idleIngressDone) yield return null;
                }
            }
            spd = 0f; accel = 0f; bkPd = 1f; State = BusState.AtTerminal;

            // Available for redispatch while we wait -- BusManager needs to
            // know this bus exists and isn't committed to anything, same as
            // before. Deliberately NOT SetIdle(true), since that also hides
            // renderers/disables audio and this bus is supposed to stay
            // visibly parked in its bay, not vanish.
            BusManager.Instance?.NotifyBusParkedAtDepot(busID);

            // [FIX] This used to exit the coroutine here permanently -- no
            // timeout, no re-check, no fallback. A bus with no follow-up
            // slot at a turnback stop could sit in this bay forever even if
            // the scheduler genuinely never had one coming (route actually
            // finished). Now: actively wait, re-checking for a real slot,
            // until either one appears or the timeout runs out.
            float holdStartMinutes = SimClock.Instance != null ? SimClock.Instance.AbsoluteGameMinutes : 0f;
            bool gotFollowUpSlot = false;
            while (SimClock.Instance == null ||
                   SimClock.Instance.AbsoluteGameMinutes - holdStartMinutes < terminalHoldTimeoutMinutes)
            {
                // [FIX] An explicit recall (FlagForDepotReturn, e.g. the
                // player clicking "Recall to Depot") used to only be checked
                // once the hold's own timeout expired -- up to
                // terminalHoldTimeoutMinutes late. Checked every wait
                // iteration now, so a mid-hold recall takes effect within
                // one _wait2s tick instead.
                if (_pendingDepotReturn) break;
                if (BusScheduler.Instance != null && BusScheduler.Instance.TryGetAssignedSlot(busID, out mySlot))
                {
                    hasFollowUpSlot = true;
                    gotFollowUpSlot = true;
                    break;
                }
                yield return _wait2s;
                if (SimClock.Instance == null) break; // can't time a wait with no clock -- bail to depot rather than loop forever
            }

            if (gotFollowUpSlot)
            {
                // A real slot showed up -- release this hold-claim (the
                // continuation below re-claims its own bay fresh, same as
                // the always-had-a-slot path already does) and fall through
                // to normal service instead of yield-breaking.
                //
                // [FIX] NotifyBusParkedAtDepot above added this bus to
                // _idlePool while we waited. Resuming service here without
                // undoing that would leave it listed as available forever
                // afterward -- GetIdleBus()'s overflow mechanism could grab
                // an actively-serving bus later, the exact "ghost pool
                // membership" bug this whole session has been fixing.
                // ClaimBusForHandoff already does exactly the cleanup
                // needed (_idlePool.Remove + idleZone bookkeeping).
                BusManager.Instance?.ClaimBusForHandoff(busID);
                if (_activeIdleZone != null) { _activeIdleZone.Release(busID); _claimedBayIdx = -1; _activeIdleZone = null; }
                _isOutbound = mySlot.isOutbound;
                if (verboseLogging) Debug.Log($"[Bus#{busID}] Follow-up slot appeared during terminal hold — resuming normal service on Route {mySlot.routeNumber}.");
                // Falls through to the shared "has a real assignment" tail below.
            }
            else
            {
                if (verboseLogging) Debug.Log(_pendingDepotReturn
                    ? $"[Bus#{busID}] Recalled to depot during terminal hold — retiring."
                    : $"[Bus#{busID}] No follow-up slot appeared within {terminalHoldTimeoutMinutes:0} game-min — retiring to depot.");
                _pendingDepotReturn = false;
                if (_activeIdleZone != null) { _activeIdleZone.Release(busID); _claimedBayIdx = -1; _activeIdleZone = null; }
                RetireToDepot();
                yield break;
            }
        }
        else if (!hasFollowUpSlot || mySlot == null || _pendingDepotReturn)
        {
            _pendingDepotReturn = false;
            if (mySlot != null) BusScheduler.Instance?.ReleaseSlotWithoutComplete(busID);
            RetireToDepot();
            yield break;
        }

        _activeIdleZone = TerminalIdleZone.GetForStop(arrivedCode);
        _claimedBayIdx  = _activeIdleZone != null ? _activeIdleZone.TryClaim(busID) : -1;
        bool usingBay   = (_activeIdleZone != null && _claimedBayIdx >= 0);

        if (usingBay)
        {
            _idlePath    = _activeIdleZone.GetIngressPath(_claimedBayIdx, transform.position.y);
            _idlePathIdx = 0; _idleIngressDone = false;
            if (_idlePath != null && _idlePath.Count > 0)
            {
                State = BusState.TerminalIngress; bkPd = 0f;
                while (!_idleIngressDone) yield return null;
            }
            spd = 0f; accel = 0f; bkPd = 1f; State = BusState.AtTerminal;
        }

        while (BusScheduler.Instance.GameTimeMinutes < mySlot.scheduledDeparture + departureOffsetMinutes)
        {
            if (SlotTakenByOther(mySlot)) { SendHomeAfterSlotTaken(); yield break; }
            yield return _wait2s;
        }
        if (SlotTakenByOther(mySlot)) { SendHomeAfterSlotTaken(); yield break; }

        BusScheduler.Instance.RecordActualDeparture(busID, BusScheduler.Instance.GameTimeMinutes);

        _currentVariantLetter = mySlot.variantLetter;
        RouteVariantData variantData = null;
        if (!string.IsNullOrEmpty(_currentVariantLetter) && _route.variants != null)
            variantData = _route.variants.Find(v => v != null && v.variantLetter == _currentVariantLetter);

        var nodes = _route.GetNodes(_isOutbound, variantData);
        _segments = BuildRouteSegments(nodes);
        _stopSequence = ResolveStopSequence(_isOutbound, _currentVariantLetter);
        RerollPaxPlan();
        _currentSegmentIdx = 0; _segmentT = 0f; _nextStopIdx = 0;
        _flaggedForRotation = false;
        _segmentSpeedJitter = SampleSpeedJitter();

        if (usingBay)
        {
            _idlePath    = _activeIdleZone.GetEgressPath(_claimedBayIdx, transform.position.y);
            _idlePathIdx = 0; _idleEgressDone = false;
            _activeIdleZone.Release(busID); _claimedBayIdx = -1; _activeIdleZone = null;

            if (_idlePath != null && _idlePath.Count > 0)
            {
                State = BusState.TerminalEgress; bkPd = 0f;
                while (!_idleEgressDone) yield return null;
            }
            if (_segments.Count > 0)
            {
                Vector3 tangent = _segments[0].Tangent(0f);
                if (tangent != Vector3.zero)
                    _rb.rotation = Quaternion.LookRotation(tangent, Vector3.up) * Quaternion.Euler(0, 180, 0);
            }
        }
        else
        {
            if (_segments.Count > 0)
            {
                Vector3 startPos = _segments[0].Evaluate(0f);
                startPos.y += spawnHeightBuffer; // drop onto the real surface via gravity, not a guessed/stale Y
                _rb.position = startPos;
                Vector3 tangent = _segments[0].Tangent(0f);
                if (tangent != Vector3.zero)
                    _rb.rotation = Quaternion.LookRotation(tangent, Vector3.up) * Quaternion.Euler(0, 180, 0);
                ResetArticulationState();
            }
        }

        _destinationBoard
            ?.SetRoute(_route.routeNumber, _isOutbound ? _route.destinationNameOutbound : _route.destinationNameInbound);
        State = BusState.InService; bkPd = 0f;
        _dwellCoroutineActive = false;
    }

    /// <summary>[ADD] Extracted from TerminalDwell's retirement branch so the
    /// SAME depot-routing logic can be shared by both the original
    /// real-terminal-with-no-follow-up-slot case AND the terminal-hold
    /// timeout's fallback (see TerminalDwell's hold branch) -- there's
    /// nothing about finding/driving to a depot that actually depends on
    /// which of those two triggered it, so duplicating it a second time
    /// would just be two copies to keep in sync. Not a coroutine itself --
    /// everything here is synchronous setup plus a StartDepotIngress() call,
    /// which is what actually begins the (separately ticked) drive.</summary>
    private void RetireToDepot()
    {
        if (autoReturnToDepotOnRetirement)
        {
            var retirementDepot = ResolveRetirementDepot();
            if (retirementDepot != null)
            {
                // [FIX] Was retirementDepot.GetFreeSpot() + hand-writing
                // spot.occupied/occupiedByBusID directly -- a second,
                // unsynced writer alongside DepotManager (the documented
                // sole owner, see DepotManager's _spotByFleet header) AND
                // keyed by busID instead of fleetNumber, the exact key-
                // space mix-up that comment warns against. Worse, the bay
                // index actually used a few lines down was hardcoded 0,
                // completely unrelated to whichever spot this claimed --
                // BusDepot.GetBayPosition(bayIdx) indexes the SAME
                // depot.parkingSpots list ClaimSpotFor claims from, so
                // every retiring bus was snapping to bay 0 regardless of
                // which spot it "held," clumping multiple buses on top of
                // each other while the real claimed spot sat visually
                // empty. ClaimSpotFor returns the real index to fix both.
                int bayIdx = -1;
                DepotParkingSpot spot = null;
                if (DepotManager.Instance != null)
                    bayIdx = DepotManager.Instance.ClaimSpotFor(retirementDepot, fleetNumber, out spot);
                if (spot == null)
                {
                    Debug.LogWarning($"[Bus#{busID}] No free bays in {retirementDepot.depotCode}!");
                }

                if (verboseLogging) Debug.Log($"[Bus#{busID}] RETIRING → {retirementDepot.depotCode}");
                _dwellCoroutineActive = false;
                _retirementDepot  = retirementDepot;
                _retirementBayIdx = bayIdx;
                _retirementSpot   = spot;

                // Fuel/condition check happens before the final parking leg,
                // not instead of it -- a bus that's fine on both just drives
                // straight to its spot exactly as before this existed.
                if (RouteToFuelOrMaintenanceIfNeeded(retirementDepot)) return;

                BeginFinalDepotParkingLeg();
                return;
            }
        }

        Debug.LogWarning($"[Bus#{busID}] No retirement depot available! Going idle.");
        SetIdle(true);
        BusManager.Instance?.NotifyBusParkedAtDepot(busID);
        _dwellCoroutineActive = false;
    }

    /// <summary>Checks fuel &lt;30% / worst-part condition &lt;70% against
    /// the given depot's assignable FuelStation/MaintenanceBay zones and, if
    /// either trips, starts driving there instead of straight to the parking
    /// spot. Fuel is checked first -- if both are needed, the fuel stop
    /// chains into the maintenance stop afterward (see
    /// ContinueRetirementAfterFuelOrMaintenanceStop) rather than picking
    /// just one. Returns true if a detour was started, in which case the
    /// caller should NOT also start the final parking leg itself.</summary>
    private bool RouteToFuelOrMaintenanceIfNeeded(DepotData depot)
    {
        if (_vehicleSystem != null && _vehicleSystem.currentFraction < 0.30f)
        {
            var station = FuelStation.FindForDepot(depot, _vehicleSystem.fuelType);
            if (station != null) { StartDrivingToFuelStation(station); return true; }
        }
        if (_vehicleSystem != null && _vehicleSystem.maxCondition > 0f
            && (_vehicleSystem.WorstCondition / _vehicleSystem.maxCondition) < 0.70f)
        {
            var bay = MaintenanceBay.FindForDepot(depot);
            if (bay != null) { StartDrivingToMaintenanceBay(bay); return true; }
        }
        return false;
    }

    /// <summary>The actual final leg to the claimed parking spot -- what
    /// RetireToDepot used to do unconditionally before the fuel/maintenance
    /// detour existed. Reached either directly (nothing needed) or after a
    /// fuel/maintenance stop completes.</summary>
    private void BeginFinalDepotParkingLeg()
    {
        Vector3 startPos  = transform.position;
        Vector3 targetPos = _retirementSpot != null ? _retirementSpot.position : transform.position;
        var graph = CityManager.Instance != null ? CityManager.Instance.Graph : null;
        var fullPath = graph != null
            ? RoadGraphPathfinder.FindWaypoints(graph, startPos, targetPos)
            : new List<Vector3> { startPos, targetPos };
        StartDepotIngress(fullPath, _retirementBayIdx, depotReturnSpeedFraction);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  RETIREMENT FUEL / MAINTENANCE DETOUR
    //  Reuses the same _depotPath/_depotPathIdx/_depotSpeedFraction scratch
    //  fields DepotEgress/DepotIngress use for their own waypoint legs --
    //  never active at the same time as those, since this is a leg BEFORE
    //  the DepotIngress call in BeginFinalDepotParkingLeg above overwrites
    //  them fresh. DriveDepotPathTight is the same accel/brake/rotation
    //  helper DepotEgress/DepotIngress already use for this exact kind of
    //  short, lane-centered waypoint approach.
    // ═════════════════════════════════════════════════════════════════════════
    private void StartDrivingToFuelStation(FuelStation station)
    {
        Vector3 startPos = transform.position;
        var graph = CityManager.Instance != null ? CityManager.Instance.Graph : null;
        _depotPath = graph != null
            ? RoadGraphPathfinder.FindWaypoints(graph, startPos, station.WorldCenter)
            : new List<Vector3> { startPos, station.WorldCenter };
        _depotPathIdx       = 0;
        _depotSpeedFraction = depotReturnSpeedFraction;
        State = BusState.DrivingToFuelStation; running = true; bkPd = 0f;
        if (verboseLogging) Debug.Log($"[Bus#{busID}] Low fuel ({_vehicleSystem?.currentFraction:P0}) — detouring to {station.stationCode} before parking.");
    }

    private void DriveToFuelStation()
    {
        if (_depotPath == null || _depotPath.Count == 0 || _depotPathIdx >= _depotPath.Count)
        { spd = 0f; accel = 0f; bkPd = 1f; State = BusState.AtFuelStation; return; }

        Vector3 target = _depotPath[_depotPathIdx]; target.y = transform.position.y;
        float   dist   = Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z),
                                           new Vector3(target.x, 0, target.z));
        if (dist < 1.6f) { _depotPathIdx++; return; }
        DriveDepotPathTight(target, baseTargetSpeed * _depotSpeedFraction * personality.speedMultiplier);
    }

    /// <summary>Polled every tick while AtFuelStation -- fueling itself
    /// happens passively via FuelStation's own zone poll (BusZone.Update),
    /// same as any other bus sitting stopped inside its box. This just
    /// watches for "full enough" and hands off to the next leg.</summary>
    private void FuelStopCheck()
    {
        if (_vehicleSystem == null || _vehicleSystem.currentFraction >= 0.99f)
            ContinueRetirementAfterFuelOrMaintenanceStop();
    }

    private void StartDrivingToMaintenanceBay(MaintenanceBay bay)
    {
        Vector3 startPos = transform.position;
        var graph = CityManager.Instance != null ? CityManager.Instance.Graph : null;
        _depotPath = graph != null
            ? RoadGraphPathfinder.FindWaypoints(graph, startPos, bay.WorldCenter)
            : new List<Vector3> { startPos, bay.WorldCenter };
        _depotPathIdx       = 0;
        _depotSpeedFraction = depotReturnSpeedFraction;
        State = BusState.DrivingToMaintenanceBay; running = true; bkPd = 0f;
        if (verboseLogging) Debug.Log($"[Bus#{busID}] Worn condition ({_vehicleSystem?.WorstCondition:0}/{_vehicleSystem?.maxCondition:0}) — detouring to {bay.bayCode} before parking.");
    }

    private void DriveToMaintenanceBay()
    {
        if (_depotPath == null || _depotPath.Count == 0 || _depotPathIdx >= _depotPath.Count)
        {
            spd = 0f; accel = 0f; bkPd = 1f; State = BusState.AtMaintenanceBay;
            ClearRouteAssignment(); // [ADD] physically inside the bay now -- stop reporting the old route
            return;
        }

        Vector3 target = _depotPath[_depotPathIdx]; target.y = transform.position.y;
        float   dist   = Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z),
                                           new Vector3(target.x, 0, target.z));
        if (dist < 1.6f) { _depotPathIdx++; return; }
        DriveDepotPathTight(target, baseTargetSpeed * _depotSpeedFraction * personality.speedMultiplier);
    }

    /// <summary>Polled every tick while AtMaintenanceBay -- MaintenanceBay's
    /// own zone poll handles the actual dwell-timer + RepairToMax() call.
    /// This just watches the resulting condition and hands off once it
    /// clears the same 70% threshold MaintenanceBay itself uses.</summary>
    private void MaintenanceStopCheck()
    {
        if (_vehicleSystem == null || _vehicleSystem.maxCondition <= 0f
            || (_vehicleSystem.WorstCondition / _vehicleSystem.maxCondition) >= 0.70f)
            ContinueRetirementAfterFuelOrMaintenanceStop();
    }

    /// <summary>Fires once a fuel or maintenance stop finishes. If we just
    /// finished fueling and condition is ALSO still below threshold, chains
    /// straight into the maintenance bay instead of parking first and
    /// needing a second dispatch later. Otherwise proceeds to the real
    /// parking spot.</summary>
    private void ContinueRetirementAfterFuelOrMaintenanceStop()
    {
        if (State == BusState.AtFuelStation && _retirementDepot != null
            && _vehicleSystem != null && _vehicleSystem.maxCondition > 0f
            && (_vehicleSystem.WorstCondition / _vehicleSystem.maxCondition) < 0.70f)
        {
            var bay = MaintenanceBay.FindForDepot(_retirementDepot);
            if (bay != null) { StartDrivingToMaintenanceBay(bay); return; }
        }
        BeginFinalDepotParkingLeg();
    }

    private void DriveTerminalIngress()
    {
        if (_idlePath == null || _idlePathIdx >= _idlePath.Count) { _idleIngressDone = true; return; }
        Vector3 target = new Vector3(_idlePath[_idlePathIdx].x, transform.position.y, _idlePath[_idlePathIdx].z);
        float dist = Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z),
                                       new Vector3(target.x, 0, target.z));
        float arrivalRadius = Mathf.Max(1.2f, spd * 0.06f);
        if (dist < arrivalRadius)
        {
            _idlePathIdx++;
            if (_idlePathIdx >= _idlePath.Count)
            {
                Vector3 finalPos = _idlePath[_idlePath.Count - 1]; finalPos.y = transform.position.y;
                _rb.MovePosition(finalPos); spd = 0f; accel = 0f; bkPd = 1f;
                _idleIngressDone = true;
                _rb.MoveRotation(_activeIdleZone.GetParkingRotation(_claimedBayIdx));
            }
            return;
        }
        float ingressSpeed = baseTargetSpeed * 0.35f;
        if (_idlePathIdx == _idlePath.Count - 1)
        {
            float stopFrac = Mathf.Clamp01(dist / Mathf.Max(1f, ingressSpeed * 0.4f));
            ingressSpeed   = Mathf.Lerp(ingressSpeed * 0.3f, ingressSpeed, Mathf.SmoothStep(0f, 1f, stopFrac));
        }
        DriveTowardSmooth(target, ingressSpeed);
    }

    private void DriveTerminalEgress()
    {
        if (_idlePath == null || _idlePathIdx >= _idlePath.Count) { _idleEgressDone = true; return; }
        Vector3 target = new Vector3(_idlePath[_idlePathIdx].x, transform.position.y, _idlePath[_idlePathIdx].z);
        float dist = Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z),
                                       new Vector3(target.x, 0, target.z));
        float arrivalRadius = Mathf.Max(2f, spd * 0.08f);
        if (dist < arrivalRadius)
        {
            _idlePathIdx++;
            if (_idlePathIdx >= _idlePath.Count) _idleEgressDone = true;
            return;
        }
        float egressFrac  = _idlePath.Count > 1 ? Mathf.Clamp01((float)_idlePathIdx / (_idlePath.Count - 1)) : 1f;
        float egressSpeed = Mathf.Lerp(baseTargetSpeed * 0.35f, baseTargetSpeed * personality.speedMultiplier, egressFrac);
        DriveTowardSmooth(target, egressSpeed);
    }

    private void DriveTowardSmooth(Vector3 worldTarget, float targetSpeed)
    {
        worldTarget.y = transform.position.y;
        Vector3 dir   = worldTarget - transform.position; dir.y = 0f;
        if (dir.sqrMagnitude < 0.001f) return;

        float gap = targetSpeed - spd;
        float gain = personality.AccelGain;
        float desiredAccel = ComputeSmoothAccelBrake(gap, gain, personality.BrakeRate, out float desiredBrake);
        bkPd = desiredBrake;
        accel = Mathf.MoveTowards(accel, desiredAccel, maxAccelRatePerSec * Time.fixedDeltaTime);

        _rb.MovePosition(transform.position + dir.normalized * (spd / 3.6f) * Time.fixedDeltaTime);
        Quaternion targetRot = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0, 180, 0);
        _rb.MoveRotation(Quaternion.RotateTowards(_rb.rotation, targetRot, 90f * Time.fixedDeltaTime));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  AI FREE-ROAM  —  hook for AIBusController (CHIP). A free agent hands
    //  this bus an arbitrary waypoint path (built via RoadGraphPathfinder,
    //  same as the express dead-run above) and this bus just drives it,
    //  fully bypassing its own route/schedule/personality-dwell logic.
    //  Traffic lights and bus-ahead checks still run every tick regardless
    //  of State (see ManagedTick), so a free-roaming bus still obeys them —
    //  it's only the DESTINATION that's externally driven, not the physics.
    // ═════════════════════════════════════════════════════════════════════════
    private List<Vector3> _aiPath;
    private int           _aiPathIdx;

    /// <summary>Fired when the current AI path is fully driven (reached the
    /// final waypoint) or handed an empty/null path. The brain is expected
    /// to either hand over a new path or leave the bus idle.</summary>
    public event Action OnAIPathComplete;

    /// <summary>Hands this bus a path to drive under external (AIBusController)
    /// control. Overwrites whatever it was doing — the brain owns this bus
    /// for as long as it stays in AIFreeRoam.</summary>
    public void BeginAIControl(List<Vector3> waypoints)
    {
        _aiPath    = waypoints;
        _aiPathIdx = 0;
        State      = BusState.AIFreeRoam;
        running    = true;
        bkPd       = 0f;
    }

    /// <summary>Releases the bus back to idle — call this before handing the
    /// bus back to BusScheduler/normal NPC AI (e.g. CHIP bus-hopping off it).</summary>
    public void ReleaseAIControl()
    {
        if (State == BusState.AIFreeRoam)
        {
            State = BusState.Idle;
            accel = 0f; bkPd = 1f; spd = Mathf.Min(spd, 5f);
        }
        _aiPath = null;
        _aiPathIdx = 0;
    }

    private void DoAIFreeRoam()
    {
        if (_aiPath == null || _aiPath.Count == 0)
        {
            spd = 0f; accel = 0f; bkPd = 1f;
            return;
        }

        _aiPathIdx = Mathf.Clamp(_aiPathIdx, 0, _aiPath.Count - 1);
        Vector3 target = _aiPath[_aiPathIdx]; target.y = transform.position.y;
        bool onFinalLeg = _aiPathIdx >= _aiPath.Count - 1;

        float dist = Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z),
                                       new Vector3(target.x, 0, target.z));

        if (onFinalLeg && dist < 6f)
        {
            spd = 0f; accel = 0f; bkPd = 1f;
            _aiPath = null; _aiPathIdx = 0;
            OnAIPathComplete?.Invoke();
            return;
        }

        float arrivalR = Mathf.Max(3f, spd * 0.1f);
        if (!onFinalLeg && dist < arrivalR) { _aiPathIdx++; return; }

        DriveTowardSmooth(target, baseTargetSpeed * personality.speedMultiplier);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  EXPRESS DEAD-RUN
    // ═════════════════════════════════════════════════════════════════════════
    private void DoExpressDeadRun()
    {
        if (!_expressTargetPositionSet)
        {
            if (_route != null) ResolveExpressTerminalPosition();
            if (!_expressTargetPositionSet) { TryFindTerminalByStopMarker(); if (!_expressTargetPositionSet) return; }
            BuildExpressPath();
        }

        // [FIX] Follow the resolved road-graph path waypoint-by-waypoint,
        // exactly like DriveDepotEgress/DriveDepotIngress do, instead of
        // MovePosition-ing straight at _expressTerminalPosition. A missing/
        // empty path (no CityManager graph available) falls back to a
        // direct approach on the FINAL waypoint only, which is still a
        // straight line but at least over a short last stretch rather than
        // the whole dead-run.
        Vector3 target;
        bool onFinalLeg;
        if (_expressPath != null && _expressPath.Count > 0)
        {
            _expressPathIdx = Mathf.Clamp(_expressPathIdx, 0, _expressPath.Count - 1);
            target = _expressPath[_expressPathIdx]; target.y = transform.position.y;
            onFinalLeg = _expressPathIdx >= _expressPath.Count - 1;
        }
        else
        {
            target = _expressTerminalPosition;
            onFinalLeg = true;
        }

        float dist = Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z),
                                       new Vector3(target.x, 0, target.z));

        if (onFinalLeg && dist < 10f) { spd = 0f; accel = 0f; bkPd = 1f; StartCoroutine(ExpressTerminalArrival()); return; }

        float arrivalR = Mathf.Max(3f, spd * 0.1f);
        if (!onFinalLeg && dist < arrivalR) { _expressPathIdx++; return; }

        float targetSpeed = baseTargetSpeed * expressDeadRunSpeedMultiplier;
        DriveTowardSmooth(target, targetSpeed);
    }

    /// <summary>Builds the actual road path for the current express dead-run
    /// via the same pathfinder depot ingress/egress use, so the bus drives
    /// there instead of cutting straight across the map.</summary>
    private void BuildExpressPath()
    {
        _expressPath    = null;
        _expressPathIdx = 0;

        var graph = CityManager.Instance != null ? CityManager.Instance.Graph : null;
        if (graph == null)
        {
            Debug.LogWarning($"[Bus#{busID}] Express dead-run: no road graph available — falling back to a direct approach.");
            return;
        }

        _expressPath = RoadGraphPathfinder.FindWaypoints(graph, transform.position, _expressTerminalPosition);
    }

    private void ResolveExpressTerminalPosition()
    {
        if (_route == null) return;
        RouteVariantData variantData = null;
        if (_expressTargetSlot != null && !string.IsNullOrEmpty(_expressTargetSlot.variantLetter) && _route.variants != null)
            variantData = _route.variants.Find(v => v != null && v.variantLetter == _expressTargetSlot.variantLetter);

        bool useVariantOverride = (variantData != null && variantData.overrideRoute);
        List<RouteNode> nodes = _expressGoToTerminalA
            ? (useVariantOverride ? variantData.outboundNodes : _route.outboundNodes)
            : (useVariantOverride ? variantData.inboundNodes  : _route.inboundNodes);

        if (nodes == null || nodes.Count == 0) return;
        _expressTerminalPosition   = _expressGoToTerminalA ? nodes[0].position : nodes[nodes.Count - 1].position;
        _expressTerminalPosition.y = transform.position.y;
        _expressTargetPositionSet  = true;
    }

private void TryFindTerminalByStopMarker()
{
    // FIX: was matching route.terminalACode/terminalZCode against
    // BusStopMarker.StopCode — same stale-field bug that stuck
    // PlayerHandoff. Here the consequence is worse: if this fallback also
    // misses, _expressTargetPositionSet never becomes true and
    // DoExpressDeadRun returns early EVERY FRAME FOREVER — a permanently
    // stuck bus with no recovery path.
    //
    // Fix: derive the terminal position from the route's own resolved
    // stop sequence instead — the same data FollowRoute/ResolveStopSequence
    // already trust, so it can never disagree with itself.
    if (_route == null) return;

    var stops = ResolveStopSequence(_expressGoToTerminalA, _currentVariantLetter);
    if (stops == null || stops.Count == 0)
    {
        Debug.LogWarning($"[Bus#{busID}] Express dead-run: no resolved stops for fallback terminal lookup.");
        return;
    }

    BusStopData target = _expressGoToTerminalA ? stops[0] : stops[stops.Count - 1];
    if (target == null) return;

    _expressTerminalPosition   = target.GetWorldPosition();
    _expressTerminalPosition.y = transform.position.y;
    _expressTargetPositionSet  = true;
}
/*
 New method for NPCBusController.cs — call this INSTEAD OF the tail end of
 AssignRoute() whenever the slot being assigned is already due/in-progress
 (i.e. reload/reconnect dispatch), so the bus visually resumes where the
 schedule says it should be instead of restarting from the route's start.
*/
public void AssignRouteWithProgress(BusRouteData route, bool outbound, TimetableSlot slot)
{
    _route = route; _isOutbound = outbound;
    RouteVariantData variantData = route.GetVariant(variantLetter);
    _currentVariantLetter = variantLetter;
    var nodes     = _route.GetNodes(_isOutbound, variantData);
    _segments     = BuildRouteSegments(nodes);
    _stopSequence = ResolveStopSequence(_isOutbound, _currentVariantLetter);
    RerollPaxPlan();
    _flaggedForRotation = false;
    _segmentSpeedJitter = SampleSpeedJitter();

// Same window-scaled duration the scheduler used to call this slot "live",
// not the flat peak-calibrated oneWayTripMinutes -- otherwise a mid-trip
// spawn lands ahead of / behind where the timetable says it should be.
float tripMinutes = BusScheduler.Instance != null ? BusScheduler.Instance.GetSlotTripMinutes(slot) : route.oneWayTripMinutes;
float now = SimClock.Instance != null ? SimClock.Instance.AbsoluteGameMinutes : slot.scheduledDeparture;
float elapsed = Mathf.Max(0f, now - slot.scheduledDeparture);
    if (elapsed <= 0.5f)
    {
        // Slot isn't actually overdue — normal fresh start, same as before.
        _currentSegmentIdx = 0; _segmentT = 0f; _nextStopIdx = 0;
    }
    else
    {
        var (stopIdx, _) = OfflineProgression.ResolveStopProgress(_stopSequence, tripMinutes, elapsed);
        var (segIdx, segT) = OfflineProgression.ResolveSegmentProgress(_segments, elapsed / Mathf.Max(0.01f, tripMinutes));

        _nextStopIdx = Mathf.Clamp(stopIdx, 0, Mathf.Max(0, _stopSequence.Count - 1));
        _currentSegmentIdx = segIdx;
        _segmentT = segT;

        if (_segments.Count > 0)
        {
            Vector3 pos = _segments[Mathf.Min(segIdx, _segments.Count - 1)].Evaluate(segT);
            pos.y += spawnHeightBuffer; // drop onto the real surface via gravity, not a guessed/stale Y
            _rb.position = pos;
            transform.position = pos;
            ResetArticulationState();
        }
        else
        {
            // [FIX] This used to fall through silently, leaving the bus
            // wherever it already physically was (depot/idle position) with
            // no error — looks exactly like "the bus didn't spawn mid-route"
            // with nothing in the console to explain why. _segments comes
            // from _route.BuildSegments(nodes); an empty result means
            // GetNodes/BuildSegments couldn't resolve stops/geometry for
            // this route+variant+direction — check that variant's stop
            // codes resolve (ResolveVariantStops / FindStopByCode warnings).
            Debug.LogError($"[Bus#{busID}] AssignRouteWithProgress: 0 segments built for " +
                            $"Route {route?.routeNumber} variant '{variantLetter}' outbound={outbound} — " +
                            "bus left at its current position instead of on-route. Check route/variant stop resolution.");
        }

        if (verboseLogging) Debug.Log($"[Bus#{busID}] Reload dispatch: fast-forwarded {elapsed:0.0} min into trip — " +
                  $"segment {_currentSegmentIdx}, T={_segmentT:F2}, next stop idx {_nextStopIdx}.");
    }

    running = true; State = BusState.InService;
    BusScheduler.Instance?.RecordActualDeparture(busID, slot.scheduledDeparture);
    _destinationBoard
        ?.SetRoute(route.routeNumber, outbound ? route.destinationNameOutbound : route.destinationNameInbound);
}
    private IEnumerator ExpressTerminalArrival()
    {
        State = BusState.AtTerminal; accel = 0f; bkPd = 1f; spd = 0f;
        yield return new WaitForSeconds(5f);
        BusScheduler.Instance?.NotifyExpressDeadRunComplete(busID);

        if (_expressTargetSlot != null && _route != null)
        {
            _isOutbound           = _expressTargetSlot.isOutbound;
            _currentVariantLetter = _expressTargetSlot.variantLetter;
            RouteVariantData variantData = null;
            if (!string.IsNullOrEmpty(_currentVariantLetter) && _route.variants != null)
                variantData = _route.variants.Find(v => v != null && v.variantLetter == _currentVariantLetter);

            var nodes = _route.GetNodes(_isOutbound, variantData);
            _segments = BuildRouteSegments(nodes);
            _stopSequence = ResolveStopSequence(_isOutbound, _currentVariantLetter);
            RerollPaxPlan();
            _currentSegmentIdx = 0; _segmentT = 0f; _nextStopIdx = 0;
            _flaggedForRotation = false; _segmentSpeedJitter = SampleSpeedJitter();
            BusScheduler.Instance?.RecordActualDeparture(busID, BusScheduler.Instance.GameTimeMinutes);
        }
        State = BusState.InService; bkPd = 0f;
        if (verboseLogging) Debug.Log($"[Bus#{busID}] Express dead-run complete.");
    }

/// <summary>Spawns a freshly-idle bus already sitting at the terminal for
/// an upcoming (not-yet-due) departure, instead of leaving it invisible at
/// depot until the moment it departs. Mirrors the tail half of
/// TerminalDwell() — position at the terminal stop, mark AtTerminal, wait
/// for scheduledDeparture, then enter service exactly like a normal
/// terminal-to-terminal cycle would.</summary>
public void SpawnParkedAtTerminal(BusRouteData route, bool outbound, TimetableSlot slot)
{
    _route = route;
    _isOutbound = outbound;
    _currentVariantLetter = slot.variantLetter ?? "";
    variantLetter = _currentVariantLetter;

    RouteVariantData variantData = route.GetVariant(_currentVariantLetter);
    var nodes = _route.GetNodes(_isOutbound, variantData);
    _segments = BuildRouteSegments(nodes);
    _stopSequence = ResolveStopSequence(_isOutbound, _currentVariantLetter);
    RerollPaxPlan();
    _currentSegmentIdx = 0;
    _segmentT = 0f;
    _nextStopIdx = 0;
    _flaggedForRotation = false;
    _segmentSpeedJitter = SampleSpeedJitter();

    // [FIX] This used to always teleport straight to the raw terminal stop
    // point (_segments[0].Evaluate(0f)), completely bypassing TerminalIdleZone
    // — no bay claim, no capacity check. Every bus FleetDispatcher parked
    // early via this path just stacked on top of the stop marker, ignoring
    // however many other buses were already sitting in real bays. Now it
    // claims a bay exactly like TerminalDwell()'s organic-arrival path does,
    // and only falls back to the raw stop point if no zone/bay is available.
    string departureCode = _isOutbound
        ? (!string.IsNullOrEmpty(variantData?.terminalACodeOverride) ? variantData.terminalACodeOverride : route.terminalACode)
        : (!string.IsNullOrEmpty(variantData?.terminalZCodeOverride) ? variantData.terminalZCodeOverride : route.terminalZCode);

    _activeIdleZone = TerminalIdleZone.GetForStop(departureCode);
    _claimedBayIdx  = _activeIdleZone != null ? _activeIdleZone.TryClaim(busID) : -1;
    bool usingBay   = _activeIdleZone != null && _claimedBayIdx >= 0;

    if (!usingBay)
        Debug.LogWarning($"[Bus#{busID}] SpawnParkedAtTerminal: no idle-zone bay claimed for stop '{departureCode}' " +
                          $"({(_activeIdleZone == null ? "TerminalIdleZone.GetForStop returned null" : "TryClaim returned -1, zone likely full")}) " +
                          "— falling back to raw route-start point instead of a proper parking bay.");

    Vector3 startPos;
    Quaternion? bayRot = null;
    if (usingBay)
    {
        startPos = _activeIdleZone.GetParkingPosition(_claimedBayIdx);
        bayRot   = _activeIdleZone.GetParkingRotation(_claimedBayIdx);
        // Authored bay position — trust its own Y as-is, no override needed.
    }
    else if (_segments.Count > 0)
    {
        startPos = _segments[0].Evaluate(0f);
        startPos.y += spawnHeightBuffer; // drop onto the real surface via gravity, not a guessed/stale Y
    }
    else
    {
        // [FIX] Silent fallback — no idle-zone bay AND no route segments
        // means this bus just stays wherever it already was (depot), with
        // no error, looking exactly like "didn't spawn at the terminal."
        Debug.LogError($"[Bus#{busID}] SpawnParkedAtTerminal: no idle-zone bay for stop '{departureCode}' " +
                        $"AND 0 segments for Route {route?.routeNumber} variant '{_currentVariantLetter}' " +
                        $"outbound={outbound} — bus left at its current position instead of at the terminal. " +
                        "Check TerminalIdleZone.GetForStop(departureCode) and route/variant stop resolution.");
        startPos = transform.position; // already wherever it is — nothing better to use
    }
    _rb.position = startPos;
    transform.position = startPos;

    if (bayRot.HasValue)
    {
        _rb.rotation = bayRot.Value;
        transform.rotation = bayRot.Value;
    }
    else if (_segments.Count > 0)
    {
        Vector3 tangent = _segments[0].Tangent(0f);
        if (tangent != Vector3.zero)
        {
            Quaternion rot = Quaternion.LookRotation(tangent, Vector3.up) * Quaternion.Euler(0, 180, 0);
            _rb.rotation = rot;
            transform.rotation = rot;
        }
    }
    ResetArticulationState();

    SetIdle(false); // re-enable renderers/colliders/rigidbody — this bus is now live, just waiting
    spd = 0f; accel = 0f; bkPd = 1f;
    running = true;
    State = BusState.AtTerminal;

    _destinationBoard
        ?.SetRoute(route.routeNumber, outbound ? route.destinationNameOutbound : route.destinationNameInbound);

    if (verboseLogging) Debug.Log($"[Bus#{busID}] Spawned parked at terminal for Route {route.routeNumber} " +
              $"{(outbound ? "A→Z" : "Z→A")}{(usingBay ? $" (bay {_claimedBayIdx})" : " (no idle zone — raw stop point)")} " +
              $"— waiting for departure @ {BusScheduler.MinutesToTimeString(slot.scheduledDeparture)}.");

    StartCoroutine(WaitForScheduledDeparture(slot));
}

/// <summary>True once the slot this bus is waiting to run has been handed to someone else (e.g. the player
/// took it over: TransferSlotToPlayer re-owns the whole chain). The bus must not depart on it.</summary>
private bool SlotTakenByOther(TimetableSlot slot) => slot == null || slot.assignedBusID != busID;

/// <summary>The slot this bus was parked for was taken over. Give up the bay and head to the depot; do NOT
/// depart -- the scheduler no longer holds a slot for this bus.</summary>
private void SendHomeAfterSlotTaken()
{
    if (verboseLogging) Debug.Log($"[Bus#{busID}] Its departure was taken over -- releasing bay and returning to depot.");
    if (_activeIdleZone != null) { _activeIdleZone.Release(busID); _activeIdleZone = null; }
    _claimedBayIdx = -1;
    _pendingDepotReturn = false;
    _dwellCoroutineActive = false;
    RetireToDepot();
}

private IEnumerator WaitForScheduledDeparture(TimetableSlot slot)
{
    while (BusScheduler.Instance.GameTimeMinutes < slot.scheduledDeparture + departureOffsetMinutes)
    {
        if (SlotTakenByOther(slot)) { SendHomeAfterSlotTaken(); yield break; }
        yield return _wait2s;
    }
    if (SlotTakenByOther(slot)) { SendHomeAfterSlotTaken(); yield break; }

    // [FIX] Release the claimed bay before entering service — this used to
    // never release, permanently "occupying" a bay for a bus that had
    // already left, silently shrinking the terminal's usable capacity every
    // time FleetDispatcher parked a bus this way.
    if (_activeIdleZone != null)
    {
        _activeIdleZone.Release(busID);
        _activeIdleZone = null;
        _claimedBayIdx = -1;
    }

    BusScheduler.Instance.RecordActualDeparture(busID, BusScheduler.Instance.GameTimeMinutes);
    State = BusState.InService;
    bkPd = 0f;
    if (verboseLogging) Debug.Log($"[Bus#{busID}] Departing terminal — Route {_route?.routeNumber} {(_isOutbound ? "A→Z" : "Z→A")}.");
}
    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═════════════════════════════════════════════════════════════════════════
    public void AssignRoute(BusRouteData route, bool outbound)
    {
        _route = route; _isOutbound = outbound;
        RouteVariantData variantData = route.GetVariant(variantLetter);
        _currentVariantLetter = variantLetter;
        var nodes     = _route.GetNodes(_isOutbound, variantData);
        _segments     = BuildRouteSegments(nodes);
        _stopSequence = ResolveStopSequence(_isOutbound, _currentVariantLetter);
        RerollPaxPlan();
        _currentSegmentIdx = 0; _segmentT = 0f; _nextStopIdx = 0;
        _flaggedForRotation = false; _segmentSpeedJitter = SampleSpeedJitter();
        running = true; State = BusState.InService;
        BusScheduler.Instance?.RecordActualDeparture(busID, BusScheduler.Instance.GameTimeMinutes);
        _destinationBoard
            ?.SetRoute(route.routeNumber, outbound ? route.destinationNameOutbound : route.destinationNameInbound);
    }

    public void SetIdle(bool idle)
    {
        if (idle)
        {
            State = BusState.Idle; running = false; spd = 0f; accel = 0f; bkPd = 1f;
            _destinationBoard?.SetBlank();
            SetDepotComponentsActive(false);
        }
        else { SetDepotComponentsActive(true); running = true; }
    }

    private void SetDepotComponentsActive(bool active)
    {
        if (_rb != null)
        {
            // [FIX] This was backwards: idle-at-depot (active=false) was
            // getting isKinematic=false (physics-driven, free to be pushed/
            // slide around) and in-service (active=true) was getting
            // isKinematic=true (frozen — fights any velocity/AddForce-based
            // movement code). Kinematic should track the INVERSE of "active
            // in the world."
            _rb.isKinematic = !active;
            if (!_rb.isKinematic)
            {
                _rb.linearVelocity  = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
        }
        if (_audioSource != null) _audioSource.enabled = active;
        if (_allRenderers != null)
            for (int i = 0; i < _allRenderers.Length; i++)
            {
                if (_allRenderers[i] == null) continue;
                // [FIX] This used to blanket-set every renderer to `active`,
                // including BusAdBoard-owned ones. Going idle (active=false)
                // hiding everything is correct -- a parked/hidden bus should
                // have its ad plane hidden too. But coming OFF idle
                // (active=true) blanket-re-enabling ad renderers silently
                // overrode any board that SetAd(null) had deliberately
                // turned off (no campaign for that board type, or the 50/50
                // roll came up "no ad") -- exactly the "I set it to null but
                // the quad stayed visible" bug. An ad board's own
                // HasAdAssigned is now the source of truth for whether it
                // comes back on; everything else still follows `active`
                // exactly as before.
                var adBoard = _allRenderers[i].GetComponent<BusAdBoard>();
                if (adBoard != null && active)
                    _allRenderers[i].enabled = adBoard.HasAdAssigned;
                else
                    _allRenderers[i].enabled = active;
            }
        if (_ownColliders != null)
            for (int i = 0; i < _ownColliders.Length; i++)
                if (_ownColliders[i] != null) _ownColliders[i].enabled = active;
        if (trailerPivot != null) trailerPivot.gameObject.SetActive(active);
    }

    public void SetIdleTarget(Vector3 target)
    {
        _deadRunTarget = target; State = BusState.DeadRunning; running = true; bkPd = 0f;
    }

    public void FlagForRotation() => _flaggedForRotation = true;

    public void StartExpressDeadRun(TimetableSlot targetSlot, bool goToTerminalA, BusRouteData route = null)
    {
        _expressTargetSlot        = targetSlot;
        _expressGoToTerminalA     = goToTerminalA;
        _expressTargetPositionSet = false;
        _expressPath              = null;
        _expressPathIdx           = 0;
        if (route != null) _route = route;
        if (_route != null) { ResolveExpressTerminalPosition(); if (_expressTargetPositionSet) BuildExpressPath(); }
        State = BusState.ExpressDeadRun; running = true; bkPd = 0f;
        if (verboseLogging) Debug.Log($"[Bus#{busID}] Express dead-run → {(goToTerminalA ? "Terminal A" : "Terminal Z")} " +
                  $"@ {BusScheduler.MinutesToTimeString(targetSlot.scheduledDeparture)}");
    }

    private float SampleSpeedJitter()
    {
        float v = personality.speedVariance;
        if (v <= 0f) return 0f;
        float u = (UnityEngine.Random.value * 2f - 1f) + (UnityEngine.Random.value * 2f - 1f);
        return u * v * 0.5f;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ARTICULATION
    // ═════════════════════════════════════════════════════════════════════════
    private void UpdateTrailer()
    {
        if (trailerPivot == null || articulationMode == ArticulationMode.None) return;
        switch (articulationMode)
        {
            case ArticulationMode.HistoryWheelTrack: UpdateTrailerHistory(); break;
            case ArticulationMode.HingeConstraint:   UpdateTrailerHinge();   break;
            case ArticulationMode.DelayedRotation:   UpdateTrailerDelayedRotation(); break;
            case ArticulationMode.TractrixJoint:     UpdateTrailerTractrix(); break;
            case ArticulationMode.KinematicRig:
    rig.Tick(transform, trailerPivot, BusFixedDt, CurrentSignedForwardSpeed());
    DeformBellows(rig.HingeYawDeg);   // bellows finally deforms live
    break;
        }
    }
private Vector3 _prevRigPos;
private float CurrentSignedForwardSpeed()
{
    float dt = Mathf.Max(BusFixedDt, 0.0001f);
    Vector3 delta = transform.position - _prevRigPos;
    _prevRigPos = transform.position;
    return Vector3.Dot(delta, transform.forward) / dt;
}

    /// <summary>Re-seats every articulation mode's trailer-follow memory after
    /// a hard position/rotation snap (teleport, depot ingress, route
    /// reassignment/fast-forward, terminal spawn, possession handoff). Every
    /// mode keeps some "last frame" memory to compute this frame's follow
    /// delta -- HistoryWheelTrack/TractrixJoint/HingeConstraint remember a
    /// world position, DelayedRotation remembers a time-stamped rotation
    /// buffer, KinematicRig remembers its own internal pose -- and none of
    /// that survives a jump: left un-reset, the next tick computes a delta
    /// from the OLD (pre-teleport) memory to the new position/orientation,
    /// which is exactly what "the trailer forgot its back" looks like (it
    /// either drags in from across the map, or briefly points the wrong way
    /// while DelayedRotation's history buffer ages out). Call this right
    /// after any hard transform/_rb.position set, before UpdateTrailer()'s
    /// next tick runs. Cheap and safe to call even when trailerPivot/
    /// articulation isn't in use -- every branch here is just clearing a
    /// flag or list.</summary>
    private void ResetArticulationState()
    {
        _trailerInitialized  = false; // HistoryWheelTrack -- re-seats from current transform next tick
        _tractrixInit         = false; // TractrixJoint -- re-seats rear axle straight behind the hitch
        _hasAppliedTrailerRot = false; // DelayedRotation -- next tick snaps instantly instead of slerping
        _rotHistoryTimes.Clear();
        _rotHistoryValues.Clear();     // drop stale pre-teleport rotation history
        _hingeNeedsSnap = true;        // HingeConstraint -- skip the lerp-catch-up next tick
        _prevRigPos     = transform.position; // KinematicRig -- avoid a one-frame speed-delta spike
        if (articulationMode == ArticulationMode.KinematicRig)
            rig.SnapStraight();
    }
    // ── Hitch auto-detection (shared) ──────────────────────────────────────────
    private Vector3 GetHitchLocalOffset(Transform tractor)
    {
        if (pivotPoint != null)
            return tractor.InverseTransformPoint(pivotPoint.position);

        if (!autoDetectHitch)
            return new Vector3(0f, 0f, tractorRearOffset);

        if (_hitchOffsetCached) return _cachedHitchLocal;

        Renderer[] rends = tractor.GetComponentsInChildren<Renderer>();
        if (rends.Length == 0)
        {
            Debug.LogWarning($"[{name}] autoDetectHitch is on but no Renderers were found under {tractor.name} " +
                              "to measure -- falling back to tractorRearOffset. Assign pivotPoint manually instead.");
            _cachedHitchLocal = new Vector3(0f, 0f, tractorRearOffset);
            _hitchOffsetCached = true;
            return _cachedHitchLocal;
        }

        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        foreach (var r in rends)
        {
            Bounds b = r.bounds;
            Vector3 c = b.center, e = b.extents;
            for (int xi = -1; xi <= 1; xi += 2)
            for (int yi = -1; yi <= 1; yi += 2)
            for (int zi = -1; zi <= 1; zi += 2)
            {
                Vector3 worldCorner = c + Vector3.Scale(e, new Vector3(xi, yi, zi));
                Vector3 local = tractor.InverseTransformPoint(worldCorner);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }
        }

        float centerX = (min.x + max.x) * 0.5f;
        // [FIX] Was hardcoded to min.z, which is the FRONT bumper on this
        // rig, not the rear hitch. Every other place in this file defines
        // real travel direction as -transform.forward (see travelDir at
        // lines ~1510/1844/2044/4649), meaning the mesh's local +Z faces
        // backward -- the same convention the player rig documents via
        // tractorMeshFacesBackward=true (where GetHitchLocalOffset picks
        // max.z, not min.z, for exactly this reason). UpdateTrailerTractrix
        // and this function were the only two places in the whole file that
        // used the un-inverted forward/min.z, which put the "hitch" near
        // the front axle instead of the true rear coupling point -- that's
        // what collapsed the rod length and made the trailer section track
        // the tractor's rotation almost immediately instead of lagging
        // through the real geometric hinge.
        float rearZ   = max.z;

        _cachedHitchLocal = new Vector3(centerX, 0f, rearZ);
        _hitchOffsetCached = true;
        return _cachedHitchLocal;
    }

    // ── DelayedRotation ─────────────────────────────────────────────────────
    // Position is ALWAYS rigidly at the hitch, every frame -- no separation
    // between "front" and "back" as far as position goes. Only rotation is
    // different, sampled from a real time-delay buffer of the tractor's own
    // past rotation, then further gated by current speed (frozen at 0 km/h,
    // full catch-up rate at/above catchUpFullSpeedKph).
    private void UpdateTrailerDelayedRotation()
    {
        if (trailerPivot == null) return;
        Transform tractor = transform;

        // Safety net: this mode drives trailerPivot's transform directly
        // every tick -- it never needs a Rigidbody. If one is still sitting
        // on trailerPivot (e.g. left over from an earlier PhysicsTether
        // setup) and it's non-kinematic, gravity acts on it independently
        // of our transform override, and if self-collision wasn't fully
        // ignored for some reason, that falling/wobbling trailer can
        // physically shove the tractor's own Rigidbody around -- which is
        // exactly what tipping the whole bus over looks like. Force it
        // kinematic once, here, so this mode can never fight physics.
        if (!_checkedTrailerRigidbody)
        {
            var trailerRb = trailerPivot.GetComponent<Rigidbody>();
            if (trailerRb != null && !trailerRb.isKinematic)
            {
                trailerRb.isKinematic = true;
                Debug.LogWarning($"[{name}] DelayedRotation: trailerPivot had a non-kinematic Rigidbody " +
                                  "(likely left over from PhysicsTether mode) -- forced it kinematic since " +
                                  "this mode drives the transform directly and doesn't use physics at all.");
            }
            _checkedTrailerRigidbody = true;
        }

        _rotHistoryTimes.Add(Time.time);
        _rotHistoryValues.Add(tractor.rotation);
        float cutoff = Time.time - rotationDelaySeconds - 1f;
        while (_rotHistoryTimes.Count > 2 && _rotHistoryTimes[0] < cutoff)
        {
            _rotHistoryTimes.RemoveAt(0);
            _rotHistoryValues.RemoveAt(0);
        }

        Quaternion delayedRot = SampleDelayedRotation(rotationDelaySeconds, tractor.rotation);
        Quaternion dampedRot  = Quaternion.Slerp(tractor.rotation, delayedRot, rotationInfluence);
        Quaternion restOffset = Quaternion.Euler(trailerMeshRestOffset);
        Quaternion targetRot = trailerMeshFacesBackward
            ? dampedRot * Quaternion.Euler(0f, 180f, 0f) * restOffset
            : dampedRot * restOffset;

        float speedKph = Mathf.Abs(CurrentSignedForwardSpeed()) * 3.6f;
        float speedFraction = Mathf.Clamp01(speedKph / Mathf.Max(0.01f, catchUpFullSpeedKph));
        if (!_hasAppliedTrailerRot)
        {
            _lastAppliedTrailerRot = targetRot;
            _hasAppliedTrailerRot = true;
        }
        else if (speedFraction > 0f)
        {
            float catchUpRate = Mathf.Clamp01((speedFraction * Time.deltaTime) / Mathf.Max(0.01f, catchUpDurationSeconds));
            _lastAppliedTrailerRot = Quaternion.Slerp(_lastAppliedTrailerRot, targetRot, catchUpRate);
        }
        // else: frozen exactly where it was.

        trailerPivot.rotation = _lastAppliedTrailerRot;

        Vector3 hitchLocal = GetHitchLocalOffset(tractor);
        Vector3 basePos    = tractor.TransformPoint(hitchLocal);
        Vector3 trailerRight = trailerPivot.right;
        Vector3 trailerUp    = trailerPivot.up;
        Vector3 trailerFwd   = trailerPivot.forward;
        trailerPivot.position = basePos
            + trailerRight * (trailerLateralTrim + pivotRelativeOffset.x)
            + trailerUp    * pivotRelativeOffset.y
            + trailerFwd   * pivotRelativeOffset.z;

        Vector3 fwdFlat = trailerPivot.forward; fwdFlat.y = 0f;
        if (fwdFlat.sqrMagnitude > 0.0001f)
            currentHingeYaw = Mathf.Atan2(fwdFlat.x, fwdFlat.z) * Mathf.Rad2Deg;
    }

    private Quaternion SampleDelayedRotation(float delay, Quaternion fallbackIfEmpty)
    {
        if (_rotHistoryTimes.Count == 0) return fallbackIfEmpty;
        float targetTime = Time.time - delay;
        if (targetTime <= _rotHistoryTimes[0]) return _rotHistoryValues[0];

        for (int i = 1; i < _rotHistoryTimes.Count; i++)
        {
            if (_rotHistoryTimes[i] >= targetTime)
            {
                float t0 = _rotHistoryTimes[i - 1];
                float t1 = _rotHistoryTimes[i];
                float frac = (t1 - t0) > 0.0001f ? (targetTime - t0) / (t1 - t0) : 0f;
                return Quaternion.Slerp(_rotHistoryValues[i - 1], _rotHistoryValues[i], frac);
            }
        }
        return _rotHistoryValues[_rotHistoryValues.Count - 1];
    }

    // ════════════════════════════════════════════════════════════════════════
    //  TRACTRIX / RIGID-ROD JOINT  (ArticulationMode.TractrixJoint)
    //  Ported straight from BusSimulationController's copy -- same reasoning:
    //  the OTHER modes here (HistoryWheelTrack, DelayedRotation) all take the
    //  rear section's HEADING from a sampled/delayed copy of the TRACTOR's own
    //  past orientation. That's the wrong quantity -- every real tractor-
    //  trailer kinematic model defines the rear section's heading as the
    //  direction of the AXLE->HITCH line, which differs from the tractor's
    //  past facing by the whole hinge angle on any curve. This solver keeps
    //  ONE piece of state -- the rear axle's world position -- and enforces
    //  that real constraint directly every tick:
    //
    //     A_new = H - L * normalize(H - A_prev)
    //
    //  which traces the tractrix of the hitch's path with zero drift and no
    //  history/delay buffer, because the axle position IS the state. Heading
    //  then falls straight out of the geometry as normalize(H - A).
    //
    //  Uses `trailerLength` as the hitch->rear-axle rod length (same field
    //  UpdateTrailerHistory already uses that way) -- NOT tractorRearOffset,
    //  which only feeds GetHitchLocalOffset's fallback when autoDetectHitch
    //  is off.
    // ════════════════════════════════════════════════════════════════════════
    private void UpdateTrailerTractrix()
    {
        if (trailerPivot == null) return;
        Transform tractor = transform;
        float dt = Mathf.Max(BusFixedDt, 0.0001f);

        // Hitch (joint) world point, driven by the tractor this frame.
        Vector3 hitch = tractor.TransformPoint(GetHitchLocalOffset(tractor));
        hitch.y = tractor.position.y;

        // [FIX] The comment this replaced claimed "every other mode in this
        // file treats transform.forward as the real travel direction" --
        // that's actually wrong. Every other travel-direction usage in this
        // file (see travelDir at ~1510/1844/2044/4649) is -transform.forward,
        // i.e. this rig's mesh faces backward, same convention the player
        // rig makes explicit via tractorMeshFacesBackward=true/BusForward.
        // UpdateTrailerTractrix was the one place using the raw, un-inverted
        // forward, which fed a 180°-wrong tractorYawDeg into the hinge-angle
        // math on top of GetHitchLocalOffset's matching min.z/max.z bug --
        // together those are what produced "straight" landing at the wrong
        // absolute angles and the front/back appearing to rotate in lockstep.
        Vector3 tFwd = -tractor.forward; tFwd.y = 0f;
        if (tFwd.sqrMagnitude < 1e-4f) tFwd = -transform.forward;
        tFwd.Normalize();
        float tractorYawDeg = Mathf.Atan2(tFwd.x, tFwd.z) * Mathf.Rad2Deg;

        float L = Mathf.Max(0.5f, trailerLength); // hitch -> rear axle, metres

        // First tick (or after ResetTractrixJoint): seat the axle straight
        // behind the hitch so it doesn't drag in from wherever it was.
        if (!_tractrixInit)
        {
            _rearAxleWorld   = hitch - tFwd * L;
            _rearAxleWorld.y = tractor.position.y;
            _tractrixInit    = true;
        }

        // ── THE CONSTRAINT ──────────────────────────────────────────────
        Vector3 axleToHitch = hitch - _rearAxleWorld; axleToHitch.y = 0f;
        if (axleToHitch.sqrMagnitude < 1e-6f) axleToHitch = tFwd; // degenerate (stacked): reuse tractor fwd
        Vector3 headingDir    = axleToHitch.normalized;
        float   trailerYawDeg = Mathf.Atan2(headingDir.x, headingDir.z) * Mathf.Rad2Deg;

        // ── Mechanical hinge stop ────────────────────────────────────────
        // Clamp the GEOMETRY, not just the reported number, same as the
        // tractor-side copy -- keeps reverse (structurally a jack-knife) from
        // folding the mesh through itself instead of stopping at the pin.
        float rawHinge = Mathf.DeltaAngle(trailerYawDeg, tractorYawDeg);
        if (Mathf.Abs(rawHinge) > maxHingeAngle)
        {
            float clampedYaw = tractorYawDeg - Mathf.Sign(rawHinge) * maxHingeAngle;
            headingDir = new Vector3(
                Mathf.Sin(clampedYaw * Mathf.Deg2Rad), 0f,
                Mathf.Cos(clampedYaw * Mathf.Deg2Rad));
            trailerYawDeg = clampedYaw;
        }

        // Re-pin the axle to exactly L behind the hitch along the final dir.
        _rearAxleWorld   = hitch - headingDir * L;
        _rearAxleWorld.y = tractor.position.y;

        // ── Publish shared state (bellows / gizmos / IBusDisplaySource readers) ──
        _hingeAngle     = Mathf.Clamp(Mathf.DeltaAngle(trailerYawDeg, tractorYawDeg), -maxHingeAngle, maxHingeAngle);
        currentHingeYaw = trailerYawDeg;

        // ── Mesh placement ──────────────────────────────────────────────
        // Same convention as DelayedRotation's own placement block, so every
        // already-tuned rig field (trailerMeshRestOffset, trailerFrontOffset,
        // trailerLateralTrim, pivotRelativeOffset) keeps its meaning.
        Vector3 sectionMeshFwd = trailerMeshFacesBackward ? -headingDir : headingDir;
        float   meshYawDeg     = Mathf.Atan2(sectionMeshFwd.x, sectionMeshFwd.z) * Mathf.Rad2Deg;

        Vector3 trailerFwdWorld = new Vector3(
            Mathf.Sin(meshYawDeg * Mathf.Deg2Rad), 0f,
            Mathf.Cos(meshYawDeg * Mathf.Deg2Rad));

        Quaternion physRot = Quaternion.LookRotation(trailerFwdWorld, Vector3.up);
        Quaternion restOff = Quaternion.Euler(trailerMeshRestOffset);

        // ── Dynamic ground pitch (X) — added ONTO the rest offset ──────────
        float groundPitchTarget = ComputeGroundPitchTargetDeg(hitch, _rearAxleWorld);
        _groundPitchDegSmoothed = Mathf.MoveTowards(
            _groundPitchDegSmoothed, groundPitchTarget, groundPitchSmoothDegPerSec * dt);
        Quaternion groundPitchRot = Quaternion.Euler(_groundPitchDegSmoothed, 0f, 0f);
        Quaternion restPlusPitch  = restOff * groundPitchRot; // dynamic pitch rides ON the baked rest offset

        Quaternion finalRot = trailerMeshFacesBackward
            ? physRot * Quaternion.Euler(0f, 180f, 0f) * restPlusPitch
            : physRot * restPlusPitch;
        trailerPivot.rotation = finalRot;

        Vector3 basePos = hitch - trailerFwdWorld * trailerFrontOffset;
        Vector3 trimmed = basePos
            + trailerPivot.right   * (trailerLateralTrim + pivotRelativeOffset.x)
            + trailerPivot.up      * pivotRelativeOffset.y
            + trailerPivot.forward * pivotRelativeOffset.z;
        trailerPivot.position = trimmed;

        DeformBellows(_hingeAngle);
    }

    /// <summary>Raycasts straight down at a world XZ point and returns the hit
    /// Y. Returns false (and the point's own Y) if nothing was hit, so callers
    /// can fall back to flat instead of pitching off a bad sample.</summary>
    private readonly RaycastHit[] _groundHits = new RaycastHit[12];

    private bool SampleGroundY(Vector3 worldPt, out float groundY)
    {
        Vector3 origin = new Vector3(worldPt.x, worldPt.y + groundPitchRayHeight, worldPt.z);
        // Take the nearest hit that is real GROUND -- not this bus's own colliders and not any other bus. In a depot
        // the rear axle sits next to / over parked buses, and a plain Raycast hit their roofs and bodies, tilting the
        // artic's rear section up to the 20 degree clamp ("tail kicked up").
        int n = Physics.RaycastNonAlloc(origin, Vector3.down, _groundHits, groundPitchRayHeight * 2f,
                                        groundPitchLayer, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue; bool found = false; groundY = worldPt.y;
        for (int i = 0; i < n; i++)
        {
            var col = _groundHits[i].collider;
            if (col == null || IsOwnCollider(col)) continue;
            if (GetCachedBus(col) != null) continue;
            if (_groundHits[i].distance < bestDist) { bestDist = _groundHits[i].distance; groundY = _groundHits[i].point.y; found = true; }
        }
        if (!found) groundY = worldPt.y;
        return found;
    }

    /// <summary>Target pitch (deg, unsmoothed) from the slope between the
    /// hitch and rear-axle ground samples. Returns 0 (flat) if either
    /// raycast misses -- never pitch off a guess.</summary>
    private float ComputeGroundPitchTargetDeg(Vector3 hitchPt, Vector3 axlePt)
    {
        if (!trailerGroundPitchEnabled) return 0f;
        // Depot floors are flat: keep the rear section level while driving in/out of, or parked in, a depot.
        if (State == BusState.DepotIngress || State == BusState.DepotEgress || State == BusState.WaitingAtDepot || State == BusState.Idle)
            return 0f;

        bool hHit = SampleGroundY(hitchPt, out float hitchGroundY);
        bool aHit = SampleGroundY(axlePt,  out float axleGroundY);
        if (!hHit || !aHit) return 0f;

        float horiz = Vector3.Distance(
            new Vector3(hitchPt.x, 0f, hitchPt.z),
            new Vector3(axlePt.x,  0f, axlePt.z));
        if (horiz < 0.05f) return 0f;

        float rise = hitchGroundY - axleGroundY;
        float pitchDeg = Mathf.Atan2(rise, horiz) * Mathf.Rad2Deg;
        if (groundPitchInverted) pitchDeg = -pitchDeg;
        return Mathf.Clamp(pitchDeg, -groundPitchMaxDeg, groundPitchMaxDeg);
    }

    /// <summary>Ignores collisions between this bus's own segments (root/front,
    /// trailerPivot, and anything in additionalArticulatedSegments) so the main
    /// Rigidbody never leans/tips against a kinematically-driven middle or rear
    /// section. Call once, e.g. from Start().</summary>
    private void IgnoreSelfSegmentCollisions()
    {
        var segments = new List<Transform> { transform };
        if (trailerPivot != null) segments.Add(trailerPivot);
        if (additionalArticulatedSegments != null)
            foreach (var seg in additionalArticulatedSegments)
                if (seg != null) segments.Add(seg);

        if (segments.Count < 2) return;

        var allColliders = new List<Collider[]>();
        foreach (var seg in segments)
            allColliders.Add(seg.GetComponentsInChildren<Collider>());

        for (int i = 0; i < allColliders.Count; i++)
        for (int j = i + 1; j < allColliders.Count; j++)
        foreach (var colA in allColliders[i])
        foreach (var colB in allColliders[j])
            if (colA != null && colB != null)
                Physics.IgnoreCollision(colA, colB, true);
    }

    private void UpdateTrailerHinge()
    {
        if (trailerPivot == null) return;
        Vector3 targetPos = transform.position + (transform.forward * tractorRearOffset)
                                                - (transform.forward * trailerFrontOffset);
        targetPos.y = 1.627f;
        Quaternion targetRot = transform.rotation;
        if (_hingeNeedsSnap)
        {
            // Re-seat instantly instead of lerping from wherever trailerPivot
            // was left before a hard position snap -- otherwise this mode has
            // no init flag of its own and would visibly drag/slide in from
            // the pre-teleport spot over several frames.
            trailerPivot.position = targetPos;
            trailerPivot.rotation = targetRot;
            _hingeNeedsSnap = false;
        }
        else
        {
            trailerPivot.position = Vector3.Lerp(trailerPivot.position, targetPos, Time.fixedDeltaTime * trailerFollowSpeed);
            trailerPivot.rotation = Quaternion.Slerp(trailerPivot.rotation, targetRot, Time.fixedDeltaTime * trailerFollowSpeed);
        }
        Vector3 fwdFlat = trailerPivot.forward; fwdFlat.y = 0f;
        if (fwdFlat.sqrMagnitude > 0.0001f)
            currentHingeYaw = Mathf.Atan2(fwdFlat.x, fwdFlat.z) * Mathf.Rad2Deg;
    }

    private void UpdateTrailerHistory()
    {
        if (trailerPivot == null) return;

        Vector3 fixedHitchPos = transform.TransformPoint(new Vector3(0f, 0f, tractorRearOffset));
        fixedHitchPos.y = transform.position.y;

        if (!_trailerInitialized)
        {
            _lastTrailerRearPos = transform.TransformPoint(new Vector3(0f, 0f, tractorRearOffset - trailerLength));
            _lastTrailerRearPos.y = transform.position.y;
            _trailerInitialized = true;
        }

        Vector3 diff = fixedHitchPos - _lastTrailerRearPos; diff.y = 0f;
        Vector3 newTrailerForward = diff.sqrMagnitude > 0.0001f ? diff.normalized : transform.forward;
        _lastTrailerRearPos = fixedHitchPos - newTrailerForward * trailerLength;

        Quaternion finalRotation = Quaternion.LookRotation(newTrailerForward, Vector3.up) * Quaternion.Euler(0f, 180f, 0f);
        trailerPivot.position = fixedHitchPos + (transform.right * lateralOffset);
        trailerPivot.rotation = finalRotation;
    }

    private void DeformBellows(float hingeAngleDeg)
    {
        if (_bellowsMesh == null || _bellowsBaseVerts == null) return;
        float angleRad = Mathf.Clamp(hingeAngleDeg, -maxBendDegrees, maxBendDegrees) * Mathf.Deg2Rad;
        if (Mathf.Abs(angleRad) < 0.0001f)
        {
            _bellowsMesh.vertices = _bellowsBaseVerts;
            _bellowsMesh.RecalculateNormals(); _bellowsMesh.RecalculateBounds();
            return;
        }
        float radius      = bellowsLength / angleRad;
        Vector3[] verts   = new Vector3[_bellowsBaseVerts.Length];
        for (int i = 0; i < _bellowsBaseVerts.Length; i++)
        {
            Vector3 v          = _bellowsBaseVerts[i];
            float lengthCoord  = v.y;
            float lateralCoord = v.x;
            float t            = Mathf.Clamp01(lengthCoord / bellowsLength);
            float sweepAngle   = t * angleRad;
            float effRadius    = radius - lateralCoord;
            verts[i] = new Vector3(effRadius * (1f - Mathf.Cos(sweepAngle)), effRadius * Mathf.Sin(sweepAngle), v.z);
        }
        _bellowsMesh.vertices = verts;
        _bellowsMesh.RecalculateNormals(); _bellowsMesh.RecalculateBounds();
    }

    private void AutoCenterTrailerMesh()
    {
        Renderer meshRenderer = trailerPivot.GetComponentInChildren<Renderer>();
        if (meshRenderer == null) return;
        Vector3 localCenter = trailerPivot.InverseTransformPoint(meshRenderer.bounds.center);
        meshRenderer.transform.localPosition -= new Vector3(localCenter.x, 0f, 0f);
        if (verboseLogging) Debug.Log($"[Bus#{busID}] AutoCenter: corrected X offset by {-localCenter.x:0.0000}");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  BREAKDOWN
    // ═════════════════════════════════════════════════════════════════════════
    private void RandomizeACEnginePersonality()
    {
        acHighEngLow = UnityEngine.Random.value < 0.30f;
        acLevel      = acHighEngLow ? UnityEngine.Random.Range(80, 95) : UnityEngine.Random.Range(50, 75);
    }

    private void MaybeBreakDown()
    {
        if (BusBreakdownSystem.Instance == null || BusBreakdownSystem.Instance.IsBusLocked(busID)) return;
        // [FIX] Was `Random.value < 0.0000000000000001f` -- ten orders of
        // magnitude below what was actually intended (0.000001), and small
        // enough to sit below float32's meaningful precision floor entirely.
        // The real roll -- cadence, condition-weighting, drivetrain-aware
        // type filtering -- now all lives in BusBreakdownSystem itself, this
        // is just the call site.
        BusBreakdownSystem.Instance.TryRollBreakdown(busID, _vehicleSystem);
        BusBreakdownSystem.Instance.TryRollCosmeticSubBreakdown(busID, _vehicleSystem);
    }

    /// <summary>Step 5 of the fuel/maintenance system: a bus that drops
    /// below 30% fuel while actively driving a leg (InService only -- never
    /// while sitting at a terminal/stop) queues a relief bus via
    /// BusManager.TryRequestNpcRelief, the same next-terminal handoff
    /// mechanism player-initiated relief uses. This bus is NOT interrupted
    /// -- it finishes the current leg normally; if a replacement was found,
    /// the scheduler chain now hands off at the next terminal instead of
    /// continuing with this bus.</summary>
    private void MaybeRequestLowFuelRelief()
    {
        if (_vehicleSystem == null || _route == null) return;

        if (_vehicleSystem.currentFraction >= 0.30f) { _lowFuelReliefRequested = false; return; }
        if (_lowFuelReliefRequested) return;

        _lowFuelReliefRequested = true;
        bool requested = BusManager.Instance != null && BusManager.Instance.TryRequestNpcRelief(busID, _route.routeNumber);
        if (verboseLogging)
            Debug.Log(requested
                ? $"[Bus#{busID}] Low fuel ({_vehicleSystem.currentFraction:P0}) mid-route — relief requested for next terminal."
                : $"[Bus#{busID}] Low fuel ({_vehicleSystem.currentFraction:P0}) mid-route — no relief bus available right now.");
    }

    private void UpdateBreakdownVisuals()
    {
        bool broken = BusBreakdownSystem.Instance != null && BusBreakdownSystem.Instance.IsBusLocked(busID);
        if (broken)
        {
            if (_breakdownMPB    == null) _breakdownMPB    = new MaterialPropertyBlock();
            if (_breakdownHalo   == null) _breakdownHalo   = BuildBreakdownHalo();
            if (_breakdownBeacon == null)
            {
                _breakdownBeacon         = BuildBreakdownBeacon();
                _breakdownBeaconRenderer = _breakdownBeacon.GetComponent<Renderer>(); // cache once
            }

            Color haloCol = GetBreakdownColor();
            _breakdownHalo.startColor = haloCol;
            _breakdownHalo.endColor   = haloCol;

            _beaconBlinkTimer += Time.deltaTime;
            float blink = Mathf.Abs(Mathf.Sin(_beaconBlinkTimer * Mathf.PI * 2.5f));

            if (_breakdownBeaconRenderer != null)
            {
                // Reuse cached MPB — no allocation each frame
                _breakdownMPB.SetColor("_EmissionColor", haloCol * (blink * 3f));
                _breakdownBeaconRenderer.SetPropertyBlock(_breakdownMPB);
            }

            _breakdownHalo.gameObject.SetActive(true);
            _breakdownBeacon.SetActive(true);
        }
        else
        {
            if (_breakdownHalo   != null) _breakdownHalo.gameObject.SetActive(false);
            if (_breakdownBeacon != null) _breakdownBeacon.SetActive(false);
            _beaconBlinkTimer = 0f;
        }
    }

    private LineRenderer BuildBreakdownHalo()
    {
        var go = new GameObject("BreakdownHalo"); go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, 3.8f, 0f);
        var lr = go.AddComponent<LineRenderer>(); lr.useWorldSpace = false; lr.loop = true;
        lr.widthMultiplier = 0.18f; lr.positionCount = 32;
        lr.material = new Material(Shader.Find("Sprites/Default"));
        for (int i = 0; i < 32; i++) { float a = i / 32f * Mathf.PI * 2f; lr.SetPosition(i, new Vector3(Mathf.Cos(a) * 1.6f, 0f, Mathf.Sin(a) * 1.6f)); }
        return lr;
    }

    private GameObject BuildBreakdownBeacon()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.name = "BreakdownBeacon";
        Destroy(go.GetComponent<Collider>()); go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, 4.6f, 0f); go.transform.localScale = Vector3.one * 0.45f;
        var mat = new Material(Shader.Find("Standard")); mat.EnableKeyword("_EMISSION");
        go.GetComponent<Renderer>().material = mat;
        return go;
    }

    private Color GetBreakdownColor()
    {
        if (BusBreakdownSystem.Instance == null) return Color.magenta;
        foreach (var b in BusBreakdownSystem.Instance.GetBreakdownInfo(busID))
        {
            var meta = BusBreakdownSystem.GetMeta(b.type);
            // [CHANGE] Color now derives from category/severity instead of a
            // hardcoded per-type switch that only covered the original 6 --
            // automatically correct for all 16 types without needing a case
            // added every time a new BreakdownType shows up.
            if (meta.severity == ShutdownSeverity.Full) return Color.red;
            if (meta.category == BreakdownCategory.Pneumatic) return new Color(1f, 0.4f, 0f);
            if (meta.category == BreakdownCategory.Cosmetic) return new Color(0.6f, 0.6f, 0.6f);
            if (b.type == BreakdownType.FlatTire) return Color.yellow;
            if (b.type == BreakdownType.Leak) return new Color(0f, 0.8f, 1f);
            if (b.type == BreakdownType.ACFailure) return Color.white;
        }
        return Color.magenta;
    }

    // [ADD] Fires when a replace-tier breakdown's timer expires (see
    // BusBreakdownSystem.Update). The bus doesn't just resume normal service
    // -- it needs to limp back to depot, and only a MaintenanceBay visit
    // later actually resets its condition. FlagForDepotReturn already does
    // exactly the "head back to depot at reduced speed" behavior this needs,
    // so no new pathing logic required here.
    private void HandleReplaceTierExpired(int expiredBusID, BreakdownType type)
    {
        if (expiredBusID != busID) return;

        // [FIX] Was just FlagForDepotReturn() -- a PASSIVE flag only ever
        // consumed at the normal end-of-route-leg checkpoint (see
        // FollowRoute's _pendingDepotReturn check). A bus disabled by a
        // full-severity breakdown is stopped dead mid-route and can never
        // reach that checkpoint on its own -- it just sat there forever,
        // correctly cleared of the breakdown itself (BusBreakdownSystem's
        // own timer expired fine), but never actually released from its
        // service slot or sent anywhere. "Never limp home or get cleared
        // of their service tag," confirmed.
        //
        // Per instruction: resume full normal driving toward the nearest
        // depot immediately, ignoring the route entirely -- not a passive
        // flag, an active state transition right now. Reuses the exact
        // same depot-resolution/pathfinding/StartDepotIngress sequence the
        // normal end-of-route retirement path already uses (see
        // FollowRoute), just triggered immediately instead of waiting for
        // a route checkpoint that will never come.
        if (verboseLogging)
            Debug.Log($"[Bus#{busID}] {type} cleared but was replace-tier — releasing service slot and limping to depot now.");

        BusScheduler.Instance?.ReleaseSlotWithoutComplete(busID);

        var retirementDepot = ResolveRetirementDepot();
        if (retirementDepot == null)
        {
            Debug.LogWarning($"[Bus#{busID}] Replace-tier breakdown cleared but no retirement depot available! Going idle in place.");
            SetIdle(true);
            BusManager.Instance?.NotifyBusParkedAtDepot(busID);
            return;
        }

        // [FIX] Same DepotManager-bypass + hardcoded-bay-0 bug as
        // TerminalDwell's two depot branches (see those comments for the
        // full explanation) -- was retirementDepot.GetFreeSpot() + hand-
        // writing spot.occupied/occupiedByBusID (wrong ID space: busID
        // instead of the fleetNumber convention DepotManager actually
        // uses), then snapping to a hardcoded bay 0 in CompleteDepotIngress
        // regardless of which spot got "claimed." Every replace-tier
        // limp-home bus was landing on top of whichever bus already held
        // bay 0 at that depot.
        int bayIdx = -1;
        DepotParkingSpot spot = null;
        if (DepotManager.Instance != null)
            bayIdx = DepotManager.Instance.ClaimSpotFor(retirementDepot, fleetNumber, out spot);
        if (spot == null)
        {
            Debug.LogWarning($"[Bus#{busID}] No free bays in {retirementDepot.depotCode} for replace-tier limp-home!");
            SetIdle(true);
            return;
        }

        Vector3 startPos  = transform.position;
        Vector3 targetPos = spot.position;
        var graph = CityManager.Instance != null ? CityManager.Instance.Graph : null;
        var fullPath = graph != null
            ? RoadGraphPathfinder.FindWaypoints(graph, startPos, targetPos)
            : new List<Vector3> { startPos, targetPos };

        StartDepotIngress(fullPath, bayIdx, depotReturnSpeedFraction);
    }

    // [REDESIGN] ApplyBreakdownAudioOverrides and the door-handling half of
    // UpdateBreakdownPhysicalEffects are GONE -- that logic now lives in
    // exactly one place, BusBreakdownSystem.ApplyBreakdownToVehicle, called
    // from FollowRoute above. Only the smoke VFX (NPC-visual-only, not
    // something the shared method needs to know about) stays here.
    private void UpdateBreakdownSmoke()
    {
        bool smokeType = false;
        if (BusBreakdownSystem.Instance != null)
            foreach (var b in BusBreakdownSystem.Instance.GetBreakdownInfo(busID))
                if (b.type == BreakdownType.EngineFailure || b.type == BreakdownType.Overheating) smokeType = true;

        if (smokeType)
        {
            if (_breakdownSmoke == null) _breakdownSmoke = BuildBreakdownSmoke();
            if (!_breakdownSmoke.isPlaying) _breakdownSmoke.Play();
        }
        else if (_breakdownSmoke != null && _breakdownSmoke.isPlaying)
        {
            _breakdownSmoke.Stop();
        }
    }

    private ParticleSystem BuildBreakdownSmoke()
    {
        var go = new GameObject("BreakdownSmoke");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, 2.2f, -3.5f); // roughly engine-bay height, rear of a standard bus
        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.loop = true;
        main.startLifetime = 2.5f;
        main.startSpeed = 0.8f;
        main.startSize = 1.2f;
        main.startColor = new Color(0.6f, 0.6f, 0.6f, 0.5f);
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        var emission = ps.emission;
        emission.rateOverTime = 6f;
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 15f;

        // [FIX] That bright magenta blob wasn't smoke-colored at all -- it
        // was Unity's built-in "missing material" fallback shader, because
        // this ParticleSystemRenderer never had a material assigned. Every
        // renderer needs one explicitly; there's no sane default. Using the
        // universally-available Sprites/Default shader with a soft round
        // gradient-free blob is good enough for a basic smoke puff without
        // depending on a specific project texture/material asset existing.
        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        var shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            var mat = new Material(shader);
            mat.color = Color.white; // actual color comes from main.startColor per-particle
            renderer.material = mat;
        }
        else
        {
            Debug.LogWarning("[Bus#" + busID + "] Sprites/Default shader not found -- breakdown smoke will render with whatever Unity's fallback material is (likely magenta). Assign a real smoke material to BuildBreakdownSmoke() manually.");
        }

        return ps;
    }

    public void RandomizePersonality()
    {
        float roll = UnityEngine.Random.value;
        if      (roll < 0.12f) personality.archetype = DriverArchetype.Stubborn;
        else if (roll < 0.24f) personality.archetype = DriverArchetype.Slacker;
        else if (roll < 0.36f) personality.archetype = DriverArchetype.Cautious;
        else if (roll < 0.48f) personality.archetype = DriverArchetype.SpeedRacer;
        else if (roll < 0.60f) personality.archetype = DriverArchetype.Express;
        else if (roll < 0.72f) personality.archetype = DriverArchetype.EarlyBird;
        else if (roll < 0.82f) personality.archetype = DriverArchetype.Rookie;
        else if (roll < 0.92f) personality.archetype = DriverArchetype.Veteran;
        else                   personality.archetype = DriverArchetype.Normal;

        // ── Per-archetype departure habits ──────────────────────────────────
        // EarlyBird is the headline: pulls out of the terminal up to 5 game-
        // minutes BEFORE the scheduled departure. Rookies run a touch late
        // (still learning the pre-trip routine), Slackers reliably late,
        // Veterans dead-on, everyone else has tiny human jitter.
        departureOffsetMinutes = personality.archetype switch
        {
            DriverArchetype.EarlyBird => -UnityEngine.Random.Range(3f, 5f),
            DriverArchetype.Slacker   =>  UnityEngine.Random.Range(1.5f, 3.5f),
            DriverArchetype.Rookie    =>  UnityEngine.Random.Range(0.5f, 2f),
            DriverArchetype.Veteran   =>  0f,
            _                         =>  UnityEngine.Random.Range(-0.5f, 0.5f),
        };

        // ── New archetype base stats (existing archetypes keep defaults) ────
        switch (personality.archetype)
        {
            case DriverArchetype.EarlyBird:
                personality.dwellMultiplier = UnityEngine.Random.Range(0.8f, 0.95f); // brisk boarding, wants to stay ahead
                personality.brakingBias     = UnityEngine.Random.Range(0.45f, 0.6f);
                break;
            case DriverArchetype.Rookie:
                personality.speedMultiplier = UnityEngine.Random.Range(0.85f, 0.95f); // hesitant
                personality.dwellMultiplier = UnityEngine.Random.Range(1.15f, 1.4f);  // slow, careful boarding
                personality.aggressiveness  = UnityEngine.Random.Range(0.15f, 0.35f);
                personality.brakingBias     = UnityEngine.Random.Range(0.15f, 0.3f);  // brakes way early
                personality.speedVariance   = UnityEngine.Random.Range(0.12f, 0.25f); // inconsistent
                personality.stopOvershoots  = UnityEngine.Random.Range(0.15f, 0.35f); // misjudges stop positions
                break;
            case DriverArchetype.Veteran:
                personality.speedMultiplier = 1.0f;
                personality.dwellMultiplier = 1.0f;
                personality.aggressiveness  = UnityEngine.Random.Range(0.45f, 0.6f);
                personality.brakingBias     = UnityEngine.Random.Range(0.55f, 0.7f);  // confident, late, smooth braking
                personality.speedVariance   = UnityEngine.Random.Range(0.01f, 0.04f); // machine-consistent
                personality.stopOvershoots  = 0f;                                     // nails every stop
                break;
        }

        // ── Kickdown proneness -- who punches it on a hill ───────────────────
        // A baseline random spread first (so every archetype has SOME chance
        // of being a kicker, not just the obvious ones), then nudged per
        // archetype: SpeedRacer and Express drivers push it because they're
        // in a hurry, Rookies because they're heavy-footed and inexperienced
        // with the pedal, Cautious/EarlyBird/Veteran ease off because they
        // have no reason to rush a climb.
        personality.kickdownProneness = UnityEngine.Random.Range(0.1f, 0.5f);
        personality.kickdownProneness = personality.archetype switch
        {
            DriverArchetype.SpeedRacer => UnityEngine.Random.Range(0.7f, 1.0f),
            DriverArchetype.Express    => UnityEngine.Random.Range(0.55f, 0.85f),
            DriverArchetype.Rookie     => UnityEngine.Random.Range(0.5f, 0.8f),   // heavy-footed, not smooth about it
            DriverArchetype.Cautious   => UnityEngine.Random.Range(0f, 0.15f),
            DriverArchetype.EarlyBird  => UnityEngine.Random.Range(0f, 0.2f),    // no need to rush, already ahead
            DriverArchetype.Veteran    => UnityEngine.Random.Range(0.1f, 0.3f),  // smooth, rarely needs to punch it
            _                          => personality.kickdownProneness,        // Normal/Slacker/Stubborn keep the baseline spread
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GIZMOS
    // ═════════════════════════════════════════════════════════════════════════
    private void OnDrawGizmos()
    {
        Vector3 origin = transform.position + new Vector3(0, 0.3f, 0);
        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(origin, -transform.forward * raycastDistance);
        Gizmos.color = new Color(0, 1, 0, 0.3f);
        Gizmos.DrawLine(origin + transform.right,  origin + transform.right  - transform.forward * raycastDistance);
        Gizmos.DrawLine(origin - transform.right,  origin - transform.right  - transform.forward * raycastDistance);
        Gizmos.color = new Color(0, 0, 1, 0.1f);
        Gizmos.DrawWireSphere(transform.position, awareRadius);

        {
            Vector3 travelDir = -transform.forward;
            float   lookDist  = Mathf.Max(raycastDistance, minFollowGap + 5f);
            Vector3 boxNear   = transform.position + travelDir * busDetectionStartOffset;
            Vector3 boxCenter = boxNear + Vector3.up * busDetectionHeightOffset + travelDir * (lookDist * 0.5f);
            Vector3 boxSize   = new Vector3(busDetectionHalfWidth * 2f, busDetectionHalfHeight * 2f, lookDist);
            Gizmos.color      = _blockedByBus ? new Color(1f, 0f, 0f, 0.35f) : new Color(0f, 1f, 0f, 0.15f);
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(boxCenter, Quaternion.LookRotation(travelDir, Vector3.up), Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, boxSize);
            Gizmos.matrix = oldMatrix;
            Gizmos.color  = Color.magenta;
            Gizmos.DrawWireSphere(boxNear, 0.4f);
        }

        if (State == BusState.TerminalIngress || State == BusState.TerminalEgress)
        {
            if (_idlePath != null && _idlePath.Count > 0)
            {
                Gizmos.color = State == BusState.TerminalIngress ? new Color(0f, 0.9f, 1f, 0.8f) : new Color(1f, 0.9f, 0f, 0.8f);
                for (int i = 0; i < _idlePath.Count - 1; i++) Gizmos.DrawLine(_idlePath[i], _idlePath[i + 1]);
                if (_idlePathIdx < _idlePath.Count) Gizmos.DrawWireSphere(_idlePath[_idlePathIdx], 1.0f);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ENGINE CONSTANTS
    // ═════════════════════════════════════════════════════════════════════════
    public void ApplyEngineConstants()
    {
        if (audioEngine == null) return;
        audioEngine.engineType = (BusAudioEngine.EngineType)(int)engineType;
        audioEngine.tx = tx;
        audioEngine.ApplyEngineConstants();
        tx = audioEngine.tx; IDLE = audioEngine.IDLE; GOV = audioEngine.GOV; CYLINDERS = audioEngine.CYLINDERS;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SIMULATION TICK
    // ═════════════════════════════════════════════════════════════════════════
    private float? _lastTs = null;

    private void UpdateSimulationTick(float ts)
    {
        if (!running) return;
        float dt = _lastTs.HasValue ? Mathf.Min(ts - _lastTs.Value, 0.05f) : 0.016f;
        _lastTs = ts;

        audioEngine.running             = true;
        audioEngine.accel               = accel;
        audioEngine.bkPd                = bkPd;
        audioEngine.spd                 = spd;
        audioEngine.rpm                 = rpm;
        audioEngine.gear                = gear;
        audioEngine.gearHoldTimer       = shiftCD;
        audioEngine.doorsOpen           = doorsOpen;
        audioEngine.doorsFullyClosed    = DoorsFullyClosed;
        audioEngine.parkingBrake        = parkingBrake;
        // [ADD] Articulation hinge angle -- drives the AVE130/AVN132 joint
        // creak/groan layer (see DoArticulationCreakDSP). Only meaningful
        // when this bus is actually articulated; harmless 0 otherwise.
        audioEngine.hingeAngleDeg       = HingeAngleDegrees;
        // [FIX] Same isNeutral sync as BusController.cs -- NPC buses have
        // their own independent audio-engine tick, so without this they'd
        // still hit the G0/fast-idle bug on EVT/hybrid drivetrains even
        // after the player-bus fix. See BusAudioEngine.isNeutral.
        audioEngine.isNeutral           = currentDirection == GearDirection.Neutral;
        audioEngine.alShiftTransientDur = alShiftTransientDur;
        audioEngine.h40Mode             = h40Mode;
        audioEngine.h40ModeTimer        = h40ModeTimer;
        audioEngine.h40StopStart        = h40StopStart;
        audioEngine.h40SSTimer          = h40SSTimer;
        audioEngine.hillMode            = hillMode;

        gearHoldTimer += dt;
        audioEngine.gearHoldTimer = gearHoldTimer;
        audioEngine.Tick(dt);

        rpm  = audioEngine.rpm;
        spd  = audioEngine.spd;
        gear = audioEngine.gear;

        shiftCD             = audioEngine.shiftCD;
        alShiftTransient    = audioEngine.alShiftTransient;
        alShiftTransientDur = audioEngine.alShiftTransientDur;
        h40Mode             = audioEngine.h40Mode;
        h40ModeTimer        = audioEngine.h40ModeTimer;
        h40StopStart        = audioEngine.h40StopStart;
        h40SSTimer           = audioEngine.h40SSTimer;
        regenActive         = audioEngine.RegenActive;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        // [REVERT] Fade envelope removed per report -- was causing real
        // audio issues. Back to the original hard clear-and-return for
        // possession/culled/engine-off, no fade multiplier at all.
        if (IsPlayerDrivingThisBus) { Array.Clear(data, 0, data.Length); return; }

        if (_audioCulled || !running) { Array.Clear(data, 0, data.Length); return; }
        // Only force the "broken" direction — a breakdown can knock power/the
        // engine out, but clearing shouldn't silently flip batteryOn/engine
        // state back to whatever the player/HUD had set before the breakdown.
        if (_breakdownBatteryKilled) audioEngine.batteryOn = false;
        if (_breakdownEngineForceOff) audioEngine.engineState = BusAudioEngine.EngineRunState.Off;
        if (_breakdownAcKilled) audioEngine.acComfortOn = false;
        audioEngine.running      = running;
        audioEngine.rpm          = rpm;
        audioEngine.spd          = spd;
        audioEngine.gear         = gear;
        audioEngine.accel        = accel;
        audioEngine.bkPd         = bkPd;
        audioEngine.isNeutral    = currentDirection == GearDirection.Neutral;
        float dist = Vector3.Distance(_busPosition, _listenerPosition);
        audioEngine.ProcessAudio(data, channels, dist);
    }

    /// <summary>
    /// Returns the bus's progress along its current route leg as a fraction [0, 1].
    /// 0 = at start, 1 = at end. Used by BusScheduler to compute dynamic lateness
    /// from actual route progress vs. expected progress-at-this-time.
    /// Returns 0 if the bus is not actively in service or has no route assigned.
    /// </summary>
    /// <summary>True only at the moments where yanking this bus onto an
    /// express dead-run won't look like it vanished mid-block: actually
    /// stopped at a stop, at a terminal, or not yet in service. Used by
    /// BusScheduler.CheckForExpressDeadRunCandidates to defer the pull
    /// until a sensible moment instead of pulling off a segment mid-route.</summary>
    public bool CanSafelyRedirectFromRoute => State != BusState.InService;

    internal float GetRouteProgressFraction()
    {        // Only compute progress for buses actively in service with a route
        if (State != BusState.InService || _route == null || _segments == null || _segments.Count == 0)
            return 0f;

        // If no stops are defined, use segment progress as fallback
        if (_stopSequence == null || _stopSequence.Count == 0)
        {
            // Progress through segments: current / total
            if (_segments.Count == 0) return 0f;
            float totalDistance = 0f;
            for (int i = 0; i < _segments.Count; i++)
                totalDistance += _segments[i].Length;
            
            if (totalDistance <= 0f) return 0f;

            // Distance traveled through completed segments
            float distanceTraveled = 0f;
            for (int i = 0; i < _currentSegmentIdx; i++)
                distanceTraveled += _segments[i].Length;
            
            // Add current segment progress
            if (_currentSegmentIdx < _segments.Count)
                distanceTraveled += _segments[_currentSegmentIdx].Length * _segmentT;

            return Mathf.Clamp01(distanceTraveled / totalDistance);
        }

        // With stops defined: blend stop progress with segment precision
        // Progress from stops: (nextStopIdx - 1) / totalStops gives stop-level progress
        float stopProgress = Mathf.Clamp01((float)(_nextStopIdx - 1) / _stopSequence.Count);

        // Segment progress between two stops for finer granularity
        float segmentProgress = Mathf.Clamp01(_segmentT);

        // Blend: 80% stop-level, 20% intra-segment refinement
        return Mathf.Clamp01(stopProgress * 0.8f + segmentProgress * 0.2f);
    }

}