using System;
using System.Collections.Generic;
using UnityEngine; 

public enum ArticulationMode { None, HistoryWheelTrack, HingeConstraint, SimpleTail, PhysicsTether, DelayedRotation, TractrixJoint }
// TractrixJoint appended at the END on purpose -- keeps every existing
// serialized int value (per-scene, per-prefab) exactly where it was, same
// rule as the EngineType enum note below. This is the ONE mode that does NOT
// route to the legacy history-follow placement; it runs the new geometric
// rod/tractrix solver (UpdateTrailerTractrix) instead. See that method.

[RequireComponent(typeof(AudioSource))]
public class BusSimulationController : MonoBehaviour
{
    [SerializeField] private float maxSteerAngle = 45f;
    [SerializeField] private float speedForMaxSteer = 10f;
    [SerializeField] private float steerResponseSpeed = 5f;

    [Header("Physics Steering")]
    public float wheelbase = 6.0f;
    private float currentSteerAngle = 0f;
    /// <summary>Current visual steer angle in degrees (positive = right).
    /// Read-only outside this class — BusWheelSteer polls this every frame
    /// to turn the front wheel meshes; nothing else should ever write it.</summary>
    public float SteerAngleDegrees => currentSteerAngle;

    [Header("DIWA Voice Options (real — copied from the adopted NPC slot's FleetMetadata/EngineConfig, same as ob_* fields above)")]
    public bool diwaOpt1_1; // delayed whine, ~0.5s behind rpm
    public bool diwaOpt1_2; // deep whine overlay, G1 only
    public bool diwaOpt1_3; // smoother/quieter overall
    public bool diwaOpt1_4; // second D864.6 character: suppressed whine til late G1, hiss window, extended G1, audible piston firing
    public bool diwaOpt1_5; // "the shaker": no whine at all, deeper+louder core, rapid-fire click vibration-sim at idle
    public bool diwaOpt1_6; // "fourth voice": whine hidden like opt1_4 but quieter, extended two-tone G1, groan-only retarder
    public bool diwaOpt1_7; // "fifth voice": as opt1_6 but whine never hidden -- Wandler wind-up on move-off instead
    public bool diwaOpt1_8; // \"strained\": harder, surging whine (D864.6)

    [Tooltip("Copied from the adopted NPC slot's FleetMetadata (FleetSeriesDefinition.isArticulated), " +
             "same handoff path as the diwaOpt fields above. Used only as the AMBIGUOUS-tx fallback in " +
             "ApplyEngineConstants() -- when tx is Voith/ZF/B400R this is what decides articulated strain " +
             "character; it's ignored when tx alone already confirms it (B500R/H50EP/HDS300/etc).")]
    public bool isArticulated = false;

    [Tooltip("Per-series customizable rated power tier. Auto-defaults via FleetSeriesDefinition.ResolveRatedPowerTier() " +
             "(280 if non-articulated; 320 for L9N/ISLG artics, 330 for ISL/ISL9/L9/X10 artics) but can be freely " +
             "overridden here. Gets clamped back to a LEGAL tier for this engine+articulation combo in " +
             "ApplyEngineConstants() via ClampToLegalTier(), so picking an illegal combo silently falls back " +
             "instead of breaking anything.")]
    public FleetSeriesDefinition.RatedPowerTier ratedTierOverride = FleetSeriesDefinition.RatedPowerTier.HP280;

    [Tooltip("Check this on the Gillig BRT prefab (and its children, if the body/chassis mesh is split " +
             "across child objects -- this single bool on the root BusController is what the audio " +
             "engine reads, so it just needs to be true once here regardless of how the mesh hierarchy " +
             "is structured underneath). Gillig has a genuinely more hollow-bodied character than the " +
             "Xcelsior -- engine/whine read louder through the chassis -- plus a post-shift stall/rattle " +
             "artifact the Xcelsior doesn't have. Both driven from this one flag.")]
    public bool isGillig = false;

    // ════════ ENGINE TYPE ════════
    // NOTE: this enum is cast by raw int index to/from BusAudioEngine.EngineType
    // (see Start()/ApplyEngineConstants() below) — it MUST stay in identical
    // order to that enum, not just have matching names. Appended ISL/ISB67/ISLG
    // to match the BusAudioEngine.Combustion pass — appended at the END so
    // existing fleet-roster int values (serialized per-series) don't shift.
    // XHE40/XHE60 (hydrogen fuel cell-electric) appended after ISLG for the
    // same reason — BusAudioEngine.EngineType grew those two at its end too,
    // so the index cast still lines up.
    public enum EngineType { L9N, L9, B67, X10, XE40, ISL9, XE60, ISL, ISB67, ISLG, XHE40, XHE60, B72 }
    [Header("Bus / Engine Identity")]
    [Tooltip("L9N = XN40 CNG (2017+) | L9 = XD40 Diesel (2017+) | B67 = XDE40 Hybrid | XE40 = XE40 Electric | " +
             "XE60 = XE60 Electric (ZF AVE 130) | ISL9 = XD40/XD60 Diesel (2010-16) | ISL = older 8.9L Diesel (2007-09) | " +
             "ISB67 = older 6.7L Diesel (2007-16, non-hybrid) | ISLG = Cummins Westport ISL G CNG (2007-16, XN40) | " +
             "XHE40 = Hydrogen Fuel Cell-Electric (XHE40) | XHE60 = Hydrogen Fuel Cell Portal Axle (XHE60)")]
    public EngineType engineType = EngineType.L9N;

    [Header("H40/50 EP — Launch Rev Sequence")]
    [Tooltip("Only used when tx is h40ep/h50ep. Place your own rev boosts on takeoff: " +
             "each entry is one rev (instant peak, exponential decay over 'duration'), " +
             "followed by 'gapAfter' seconds of silence before the next entry fires. " +
             "BusAudioEngine itself isn't a MonoBehaviour, so this list lives here and " +
             "gets handed to it — edit it on THIS component, not on the audio engine.")]
    public List<BusAudioEngine.EPRevStage> epRevStages = new List<BusAudioEngine.EPRevStage>
    {
        new BusAudioEngine.EPRevStage(0.55f, 1.0f, 4.0f),
        new BusAudioEngine.EPRevStage(0.78f, 1.6f, 7.0f),
        new BusAudioEngine.EPRevStage(1.00f, 2.2f, 0.0f),
    };

    [Header("Player Handoff Integration")]
    [Tooltip("TRUE on the player bus: disables the built-in stop/pax/door system. " +
             "PlayerHandoff takes ownership instead. Leave FALSE on all NPC buses.")]
    public bool stopPlayerPaxSystem = false;

    public static readonly float   TCIRC  = 2f * (float)Math.PI * 0.48f;
    public static readonly float[] AL_R   = { 0f, 4.39f, 3.75f, 2.24f, 1.60f, 1.25f, 0.65f };
    public static readonly bool[]  AL_LOCK= { false, true, true, true, true, true, true };
    public static readonly float[][] V_R  = new float[][]
    {
        new float[] { 0f,  38f, 0f,    1900f },
        new float[] { 36f, 58f, 1150f, 1600f },
        new float[] { 55f, 76f, 1050f, 1450f },
        new float[] { 73f, MAX_SPD, 900f, 1280f }
    };
    public static readonly float[] V_DOWN = { 0f, 0f, 34f, 52f, 70f };

    // ════════ BUS GAMEPLAY ════════
    [Header("Bus Gameplay")]
    public bool  doorsOpen    = false;
    [Tooltip("Rear door, independent of the front — toggled via Shift+1 in PlayerHandoff. Also locks movement, same as the front doors.")]
    public bool  rearDoorsOpen = false;
    public bool  parkingBrake = false;

    [Header("Door Rigs (visual)")]
    [Tooltip("Real animated doorways. If unassigned, doors are treated as instantly open/closed.")]
    public BusDoorSet frontDoorSet;
    public BusDoorSet rearDoorSet;

    // [FIX — #19, door half] Every BusDoorSet found anywhere in the hierarchy,
    // not just whatever's dragged into frontDoorSet/rearDoorSet — mirrors the
    // NPCBusController fix so a child/articulated section's door rig actually
    // gets toggled for the player bus too.
    private BusDoorSet[] _allDoorSets;
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
    /// <summary>"wedge" command / stuck-door fallback for when the battery
    /// (and so the normal door button) has no power to work with. Front
    /// door only, per spec -- toggles between wedged-halfway and closed.
    /// Never touches doorsOpen: a wedged door was never a real "open for
    /// boarding" state, just a mechanical half-position.</summary>
    public bool ToggleWedgeFrontDoor()
    {
        if (frontDoorSet != null && frontDoorSet.IsWedged)
        {
            CloseFrontDoors();
            return false;
        }
        frontDoorSet?.Wedge();
        var extras = ExtraDoorSets();
        for (int i = 0; i < extras.Length; i++) extras[i]?.Wedge();
        return true;
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

    /// <summary>True only once both doorways have finished CLOSING — mirrors
    /// NPCBusController.DoorsFullyClosed. Movement stays locked until this is true.</summary>
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

    public enum GearDirection { Reverse = -1, Neutral = 0, Drive = 1 }
    [Header("Transmission Selector")]
    public GearDirection currentDirection = GearDirection.Drive;

    [Header("Transmission Config")]
    [Tooltip("voith | allison | bae | h40ep")]
    public string tx = "voith";

    [Header("Live Metrics")]
    public float rpm     = 0f;
    public float spd     = 0f;
    public int   gear    = 0;
    public float shiftCD = 0f;
    public float accel   = 0f;
    // MobileTouchHUD writes the current gas-pedal intensity here. Values are
    // normalized to 0..1 and are consumed by UpdateSimulation alongside the
    // keyboard throttle input.
    [HideInInspector] public float gasPd = 0f;
    public float bkPd    = 0f;

    // [ADD] Player-side breakdown state -- lets BusSimulationController call
    // the same BusBreakdownSystem.ApplyBreakdownToVehicle NPCBusController
    // uses, instead of duplicating the effect logic a second time.
    private NPCBusController _npcForBusID;
    private BusVehicleSystem _vehicleSystem;
    private bool _breakdownDoorsHandled = false;

    [HideInInspector] public bool accelKey = false;
    [HideInInspector] public bool brakeKey = false;
    [HideInInspector] public float mobileSteerInput = 0f;

    [Header("─── Articulation Link ───────────────────────────")]
    [Tooltip("The front section pivot Transform. If null, falls back to this.transform (rigid bus).")]
    public Transform frontPivot;
    private float steeringInputAccumulator = 0f;
public bool kickdownKey; 
    [Range(0f, 1f)] public float kickdownThrottleThreshold = 0.95f;
    
    // This allows the HUD or external manager to "inject" the input status
    public void SetKickdownState(bool state) => kickdownKey = state;

    // ── Converter Stall Hold ── Shift + throttle, once eligible (see
    // BusAudioEngine — proven G1->G2 upshift, or 7+ km/h for gearless/
    // direct-drive powertrains). This isn't an artificial diagnostic mode;
    // it's just what naturally happens if a driver holds the accelerator
    // against something that won't let the bus actually move faster —
    // same idea as flooring it in Drive with the brake held.
    public bool stallHoldKey;
    public void SetStallHoldState(bool state) => stallHoldKey = state;
    // ════════ ARTICULATED BUS — REAL PHYSICS ════════
    // [REDESIGN v2] The ODE hinge-angle model below (kept for bellows-angle
    // computation only) had a real bug: it integrated ROTATION over time
    // correctly, but PLACED the rear section by projecting backward from
    // the CURRENT hitch position using that integrated heading -- not from
    // where the tractor's hitch actually WAS. Any drift between the ODE's
    // estimated heading and the true path (which accumulates on tight
    // turns) showed up as the rear section visibly sliding sideways off
    // the road, even though its rotation looked fine in isolation.
    // Fixed by recording the hitch point's exact position+rotation history
    // as the bus drives (sampled by distance, not time), and placing the
    // rear section at an EXACT recorded past pose, offset by
    // rearSectionOffset. This cannot slide -- it's occupying a real point
    // the front section actually was, the same way a real trailer's rear
    // axle tracks the exact path the tractor already drove.
    //
    // Only 2 real behaviors exist now: this history-follow model (what
    // EVERY non-None ArticulationMode value routes to, including all the
    // old legacy names -- kept only so scenes with them already assigned
    // don't break) and useBoneRig (below, for a real skinned accordion
    // mesh -- not implemented yet, later work).
    [Header("Articulated Bus")]
    [Tooltip("None = rigid bus. TractrixJoint = RECOMMENDED -- the new geometric rod solver: rear axle rides exactly trailerLength behind the hitch and its heading is DERIVED from that geometry (axle->hitch line), so it can neither slide off the path nor mis-rotate. ANY other non-None value = the older history-follow model, which tracks POSITION fine but takes its heading from the tractor's stale past pose (that's the 'rotation is weird' bug). Old mode names kept only so existing scenes don't break -- switch them to TractrixJoint.")]
    public ArticulationMode articulationMode = ArticulationMode.None;

    [Tooltip("The rear-section root Transform (e.g. TrailerPivot GameObject).")]
    public Transform trailerPivot;

    [Tooltip("Local Z offset from tractor pivot to rear hitch pin -- ONLY used as fallback when autoDetectHitch is off and no pivotPoint is set. Sign is auto-corrected for tractorMeshFacesBackward.")]
    public float tractorRearOffset   = -5.0f;

    [Tooltip("Distance (m) from the hitch/coupling point to the trailer's rear axle -- the trailer's effective wheelbase L_t. XD60 rear section ≈ 6.9 m. NOTE: the old 9.0 default is precisely why the hinge pinned against the 54° stop at full lock (steady-state γ = atan(9/6) ≈ 56°) -- measure yours, don't guess high.")]
    public float trailerLength       = 6.9f;

    [Tooltip("Distance (m) from front coupling to the trailer pivot transform origin, measured along the trailer body. 0 if the pivot IS the coupling.")]
    public float trailerFrontOffset  = 0f;

    public bool trailerMeshFacesBackward = true;

    [Tooltip("Mechanical hinge stop, degrees. Real artics ≈ 54°. The hinge angle is hard-clamped here, same as the physical stops on the Hübner joint.")]
    [Range(10f, 80f)] public float maxHingeAngle    = 54f;

    [Header("Hitch Point")]
    [Tooltip("Optional. Drag any Transform here (even a plain empty GameObject) to wherever you want the trailer to hinge. Overrides everything else. Leave empty to auto-measure instead (recommended).")]
    public Transform pivotPoint;
    [Tooltip("Auto-measures the hitch point from the bus's own renderer bounds -- true rear edge and true lateral center, DIRECTION-AWARE (respects tractorMeshFacesBackward, see the bug note on GetHitchLocalOffset).")]
    public bool autoDetectHitch = true;

    [Header("Trailer Mesh Rigging")]
    [Tooltip("Baked-in rest orientation for the trailer mesh, applied on top of the hinge rotation. X = -90 is the norm for this trailer mesh -- adjust here if the mesh's own import axes are off, instead of touching the mesh or the rotation math.")]
    public Vector3 trailerMeshRestOffset = new Vector3(-90f, 0f, 0f);
    [Tooltip("Fine mesh trim along the TRAILER's own axes, applied after the physics placement. With the hitch-direction bug fixed this should be AT OR NEAR ZERO -- if you still have -35 serialized from before, reset it (right-click component ► Reset Rig Trims).")]
    public float trailerLateralTrim = 0f;
    [Tooltip("Fine mesh trim relative to the physics placement. X = right/left, Y = up/down, Z = forward/back along the trailer's own axes. Should be near zero now -- reset via context menu if old hack values are serialized.")]
    public Vector3 pivotRelativeOffset = Vector3.zero;

    // Articulated High-Speed Bob (cosmetic) -- kept private for now so every
    // bus uses these exact values instead of a stale per-prefab Inspector
    // override from before the latest tuning pass.
    private float bobStartKph = 45f;
    private float bobFullKph = 55f;
    private float frontBobAmplitude = 0.7f;
    private float backBobAmplitude = 0.2f;
    private float bobFrequencyHz = 3.2f;
    private float bobBackPhaseOffsetDeg = 55f;
    private float bobDownhillPitchForExtreme = 8f;
    private float bobDownhillExtremeMultiplier = 1.5f;

    private Transform _frontVisualTransform;
    private Quaternion _frontVisualRestLocalRot;
    private bool      _frontVisualCached = false;
    private float     _bobPhaseAccum = 0f;

    [Header("Bellows")]
    [Tooltip("MeshFilter on the bellows section mesh (must be unique/instanced).")]
    public MeshFilter bellowsMeshFilter;
    [Tooltip("Length of the bellows mesh along its deformation axis (set from mesh.bounds.size).")]
    public float bellowsLength = 1.8f;
    [Range(0f, 90f)] public float maxBendDegrees = 45f;

    [Header("Bellows Position Markers")]
    public Transform bellowsFrontMarker;
    public Transform bellowsRearMarker;

    // ── Articulation runtime state ────────────────────────────────────────────
    private float _trailerYawDeg;          // trailer world heading ψ (deg, about +Y) -- LEGACY, unused by the new history-follow placement, kept only so old-mode code paths that still read it don't break
    private bool  _articInitialized = false;
    private float currentHingeYaw   = 0f;  // published for bellows/other readers
    private float _hingeAngle       = 0f;  // γ = θ − ψ (deg), signed -- now derived from the history sample, not integrated

    // [REDESIGN] Replaces the hinge-angle ODE integration. The rear section
    // now occupies an EXACT recorded past pose of the hitch point -- not a
    // re-derived approximation projected off the current position. This is
    // what real trailers actually do (the rear axle tracks the path the
    // front already drove), and it structurally cannot slide sideways off
    // the path the way integrating a differential equation can when error
    // accumulates on tight turns. Sampled by DISTANCE traveled, not time, so
    // behavior is identical regardless of framerate or speed.
    [System.Serializable]
    private struct HistorySample { public Vector3 pos; public Quaternion rot; public float dist; }
    private readonly List<HistorySample> _hitchHistory = new List<HistorySample>(256);
    private float _totalDistTraveled = 0f;
    private const float HISTORY_SAMPLE_SPACING = 0.08f; // metres between recorded samples -- fine enough for smooth interpolation, coarse enough not to eat memory

    [Header("─── Simple Offset Follow (NEW — replaces the old ODE hinge model) ───")]
    [Tooltip("How far BEHIND the hitch point, along the path already driven, the rear section sits. This is the ONE knob that matters for how far back the rear section trails -- was previously an indirect side-effect of trailerLength/wheelbase/hinge-angle math. For older buses not using bone logic: set this per-bus and you're basically done.")]
    public float rearSectionOffset = 6.9f;
    [Tooltip("How far AHEAD of the tractor's own pivot the 'front' reference point sits, for buses where the visual front section needs its own small offset (rare -- usually 0). Kept separate from rearSectionOffset so front and rear can be tuned independently, per-bus, without touching each other.")]
    public float frontSectionOffset = 0f;
    [Tooltip("Future: bone-chain articulation (FrontBone→HingeBone→AccordionBones→RearBone) for buses with a real skinned accordion mesh. Not implemented yet -- leave OFF. When ON, none of the offset-follow logic below applies.")]
    public bool useBoneRig = false;

    private Mesh      _bellowsMesh;
    private Vector3[] _bellowsBaseVerts;

    /// <summary>Signed hinge (bellows) angle in degrees — tractor heading minus
    /// trailer heading. Positive = tractor yawed right relative to trailer.</summary>
    public float HingeAngleDegrees => _hingeAngle;

    // Movement always drives the root transform now -- frontPivot (if assigned)
    // is purely a cosmetic reference for other systems (e.g. a separate
    // front-wheel-steer visual mesh) and is never the thing that actually
    // carries position/rotation.
    private Transform TractorTransform => transform;

    // ════════ REAL PHYSICS — TRACTOR MODEL ════════
    [Header("Real Physics — Tractor")]
    [Tooltip("TRUE if the bus mesh's visual front points along local -Z (this rig does -- the old code compensated with a hidden *-1 inside Translate). Every system now reads this ONE flag: movement, yaw pivot, hitch auto-detection, gizmos. This was the root cause of 'rotates wrongly in one direction' AND the trailer hanging off the NOSE of the bus.")]
    public bool tractorMeshFacesBackward = true;

    [Tooltip("How far the REAR AXLE sits ahead of the bus's rear edge, metres. New Flyer XD40 ≈ 2.9 m rear overhang; XD60 tractor rear axle sits ~1.0 m ahead of the hinge. The bus yaws about this axle -- front swings wide, rear tracks -- exactly like the real thing.")]
    public float rearAxleFromRearEdge = 2.9f;

    /// <summary>The direction the bus visually/logically drives in, world space.</summary>
    public Vector3 BusForward => tractorMeshFacesBackward ? -transform.forward : transform.forward;
    private float LocalForwardSign => tractorMeshFacesBackward ? -1f : 1f;

    // Runtime physics state
    private float _yawRateRad = 0f;        // θ̇, rad/s, published to the trailer ODE
    private float _signedVel  = 0f;        // v, m/s, + = forward travel
    private bool  _rearAxleCached = false;
    private Vector3 _rearAxleLocal;        // rear axle point in tractor-local space

    // ════════ REAL PHYSICS — DRIVE SYSTEM ════════
    [Header("Real Physics — Drive System")]
    [Tooltip("TRUE = chassis speed comes from real longitudinal dynamics (F = ma with tractive force, aero drag, rolling resistance, brake force) and is FED INTO the audio DSP so RPM/gears/sound follow real motion. FALSE = legacy mode, speed comes back out of BusAudioEngine's internal model like before.")]
    public bool physicalDrive = true;

    [Tooltip("Gross vehicle mass, kg. XD40 curb ≈ 12,700 kg; seated load ≈ 14,500; artic XD60 loaded ≈ 22,000. THE single biggest 'feels heavy' knob -- everything below divides by this.")]
    public float massKg = 14500f;

    [Tooltip("Net engine power at the wheels, kW. Cummins L9 = 209 kW (280 hp), L9N ≈ 209, B6.7 hybrid system ≈ 210 combined, XE40 ≈ 160 continuous. Tractive force is power-limited above ~10 km/h: F = P/v.")]
    public float maxPowerKW = 209f;

    [Tooltip("Launch tractive force cap, N (torque-converter / low-gear limit before power takes over). ~19 kN gives the real ~1.3 m/s² loaded launch of a Voith-equipped 40-footer.")]
    public float maxTractiveForceN = 19000f;

    [Tooltip("Full-pedal service brake deceleration, m/s². Transit spec ≈ 3.0 (dry). Blends with retarder feel already handled in the DSP.")]
    public float brakeMaxDecel = 3.0f;

    [Tooltip("Aero drag area Cd·A, m². Bus Cd ≈ 0.65 × frontal area ≈ 8.5 m² ⇒ ~5.5. This is what makes the top end taper instead of hitting a wall.")]
    public float dragCdA = 5.5f;

    [Tooltip("Rolling resistance coefficient. Bus tires on asphalt ≈ 0.008. Also what slowly bleeds speed when you lift off -- real coast-down, no artificial decay curve.")]
    public float rollingCrr = 0.008f;

    [Tooltip("Reverse speed cap, km/h. Real transit buses are governed to walking pace in reverse.")]
    public float reverseMaxKph = 12f;

    [Header("Real Physics — Steering Feel")]
    [Tooltip("How fast the road wheels can physically swing, deg/s. A bus steering box at ~5 turns lock-to-lock cranked hard ≈ 30-40 deg/s at the road wheel. This is what kills the twitchy video-game snap.")]
    public float steerRateDegPerSec = 35f;

    [Tooltip("Comfort/grip lateral acceleration cap, m/s². Standing passengers tolerate ~1.5; tires push back hard past ~3.5. Steering authority is automatically reduced at speed so v²/R never exceeds this -- the bus understeers wide like the real thing instead of carving.")]
    public float maxLatAccel = 3.5f;

    [Tooltip("Safety margin on the artic geometric steering cap (fraction of maxHingeAngle the steady-state hinge angle is allowed to reach at full lock). 0.9 = full lock can use 90% of the mechanical stop -- the hinge physically CANNOT pin against the stop in forward driving, which is what caused the rear section to rotate rigidly with the bus.")]
    [Range(0.5f, 0.98f)] public float articHingeSafetyMargin = 0.9f;

    // Drive runtime state
    private float _velMS = 0f;        // signed chassis velocity, m/s, + = forward
    private float _steerAxisInput = 0f;
    [Tooltip("In Reverse, flip left/right steering input so A/left still turns the bus left (nose swings left) instead of swinging the tail left. Off = raw wheel-turn behaviour.")]
    public bool invertSteeringInReverse = true;


    // ════════ BUS SETUP ════════
    [Header("Bus Setup")]
    public bool economyMode    = false;
    public int  acLevel        = 75;
    public bool hillMode       = false;
    public bool kickdownActive = false;
    private bool hasResetSteering = false;

    [Header("Engine Mute / Shutdown")]
    // Mirrors of audioEngine.engineState/batteryOn for anything that was
    // reading these directly — the real ignition state now lives on
    // BusAudioEngine (engineState/batteryOn) — controlled via PlayerHandoff's
    // ignition key and the HUD ENG/BAT buttons.
    public bool engineMuted      = false;
    public bool electronicsMuted = false;

    public bool running  = false;
    public bool cranking = false;
    private float? lastTs  = null;
    private float  logTimer = 0f;

    // ════════ STEERING ════════
    [Header("Steering Tuning")]
    [Tooltip("Base turn speed (in degrees per second)")]
    public float baseTurnSpeed = 60f;
    [Tooltip("Heaviness: 0.1 is smooth/heavy, 1.0 is snappy")]
    public float steerResponsiveness = 0.15f;
    private float smoothSteer = 0f;

    // ════════ BUS STOP SYSTEM ════════
    [Header("Bus Stop Zone (box, metres half-extents)")]
    public float stopZoneX = 10f;
    public float stopZoneY = 10f;
    public float stopZoneZ = 10f;

    [Header("Bus Stop Detection")]
    [Tooltip("How far away a stop is announced / becomes the next target (metres)")]
    public float stopDetectRadius = 50f;

    // Runtime stop state — only used when stopPlayerPaxSystem = false
    private Transform[]  busStops        = Array.Empty<Transform>();
    private int          nextStopIndex   = -1;
    private float        distToNextStop  = float.MaxValue;
    private bool         alightRequested = false;
    private int          alightCount     = 0;
    private int          onboardPax      = 0;
    private HashSet<int> announcedStops  = new HashSet<int>();

    // ════════ HYBRID STATE ════════
    private float regenHz            = 50f;
    private bool  regenActive        = false;
    private float baeRegenGainSmooth = 0f;

    // ── Simulation state ──────────────────────────────────────────────────────
    private float IDLE, GOV;
    private int   CYLINDERS = 6;

    // [FIX] Was 90f -- BusAudioEngine's MAX_SPD (the audio-driven physics
    // path) has always been 105f (~65 mph). Real Physics mode was quietly
    // governing 15 km/h under the audio engine's own top speed, so the two
    // physics modes disagreed on the bus's actual top end. Matched to 105f
    // here so Real Physics and the audio-driven mode agree.
    public const float MAX_SPD = 105f;
    public const float FINAL   = 5.13f;

    private float gearHoldTimer = 0f;



    public float npcVolumeScale = 1.0f;
    public float spatialBlend   = 1.0f;
    public float maxAudioDistance = 100.0f;
    public bool acHighEngLow = false;

    [Header("Old Bus — Worn Out Sound Toggles")]
    public bool ob_deepMoan, ob_worn_whine, ob_revHang, ob_delayedShifts;
    public bool ob_airRush, ob_roar, ob_rattle, ob_exhaustChuff, ob_beltSqueal, ob_doorWheeze;

    // [FIX] Moved the "start disabled by default" logic here from the end
    // of Start(). Awake() always runs SYNCHRONOUSLY the moment this
    // GameObject activates (fresh instantiation, or SetActive(true) on a
    // previously-inactive object) -- but Start() is deferred until just
    // before the NEXT frame's Update. ApplyCustomSelection's flow is
    // exactly SetActive(true) immediately followed by bus.enabled = true in
    // the same synchronous call -- with the disable living in Start(), that
    // deferred Start() fired AFTER possession had already set enabled=true,
    // silently disabling the bus again a moment later. Every legitimate
    // possession path (ApplyFleetPossession, ApplyCustomSelection) already
    // re-enables this explicitly, and both run well after Awake() has had
    // its one guaranteed synchronous pass, so this ordering is safe.
    private void Awake()
    {
        enabled = false;
    }

    // ════════ START ════════
    private void Start()
    {
        _npcForBusID = GetComponent<NPCBusController>();
        _vehicleSystem = GetComponent<BusVehicleSystem>();
        // [FIX — #19, door half] By component, not by Inspector slot.
        _allDoorSets = GetComponentsInChildren<BusDoorSet>(true);

        // ── Hierarchy sanity check ─────────────────────────────────────────
        // This script should live on a dedicated FRONT/tractor-only object
        // for articulated buses, with trailerPivot as a SIBLING, not a
        // descendant. If trailerPivot is nested under this same transform,
        // GetHitchLocalOffset's renderer-bounds scan now explicitly filters
        // it out (see that method), so physics stays correct either way —
        // but the wrong placement still means the WHOLE combined mesh
        // (front + back) gets rigidly yawed/moved together by FixedUpdate's
        // rear-axle model, on top of the independent trailer hinge placement,
        // which is not what an articulated rig should do. Flag it loudly
        // once so it gets fixed at the source rather than papered over.
        if (articulationMode != ArticulationMode.None && trailerPivot != null && trailerPivot.IsChildOf(transform))
        {
            Debug.LogWarning($"[{name}] trailerPivot ('{trailerPivot.name}') is a CHILD of this BusController's own " +
                              "transform. For an articulated bus, this script should sit on a dedicated front/tractor-" +
                              "only object with trailerPivot as a SIBLING under a shared parent instead -- otherwise " +
                              "the whole vehicle (front+back together) gets moved by the tractor's own rear-axle motion " +
                              "on top of the independent trailer hinge placement. The hitch-detection bounds scan now " +
                              "excludes trailerPivot's renderers either way, so this is a correctness warning, not a " +
                              "hard failure.");
        }

        // ── Bellows mesh init (must run before any UpdateTrailer call) ──────────
        if (bellowsMeshFilter != null)
        {
            _bellowsMesh      = Instantiate(bellowsMeshFilter.sharedMesh);
            bellowsMeshFilter.mesh = _bellowsMesh;
            _bellowsBaseVerts = _bellowsMesh.vertices;
        }
    _rb = GetComponent<Rigidbody>();
        if (_rb != null)
        {
            // Per explicit request: non-kinematic + no interpolation, forced
            // every frame in Update (see the block added there). This is the
            // OPPOSITE of the kinematic+interpolate setup this file used to
            // have -- that setup existed specifically because forcing
            // isKinematic=false/interpolation=None every frame while also
            // teleporting the transform via MovePosition was the old bug
            // (collisions didn't register). Reverted here on request; if
            // collisions stop registering again, that's why.
            _rb.isKinematic   = false;
            _rb.interpolation = RigidbodyInterpolation.None;
        }
        audioEngine = new BusAudioEngine(0x12345678u)
        {
            engineType       = (BusAudioEngine.EngineType)(int)engineType,
            tx               = tx,
            economyMode      = economyMode,
            hillMode         = hillMode,
            npcVolumeScale   = npcVolumeScale,
            spatialBlend     = spatialBlend,
            maxAudioDistance = maxAudioDistance,
            acHighEngLow     = acHighEngLow,
            acLevel          = acLevel,
            running          = true,
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
            epRevStages      = epRevStages,
        };
        audioEngine.diwaOpt1_1 = diwaOpt1_1;
        audioEngine.diwaOpt1_2 = diwaOpt1_2;
        audioEngine.diwaOpt1_3 = diwaOpt1_3;
        audioEngine.diwaOpt1_4 = diwaOpt1_4;
        audioEngine.diwaOpt1_5 = diwaOpt1_5;
        audioEngine.diwaOpt1_6 = diwaOpt1_6;
        audioEngine.diwaOpt1_7 = diwaOpt1_7;
        audioEngine.diwaOpt1_8 = diwaOpt1_8;
        audioEngine.ApplyEngineConstants();
        audioEngine.Configure(GetComponent<AudioSource>());

        IDLE = audioEngine.IDLE; GOV = audioEngine.GOV;
        rpm = audioEngine.rpm = IDLE;
        running = true;

        ApplyEngineConstants();
        V_R[0][2] = IDLE;

        if (!stopPlayerPaxSystem)
        {
            GameObject[] stopObjects = GameObject.FindGameObjectsWithTag("BusStop");
            busStops = new Transform[stopObjects.Length];
            for (int i = 0; i < stopObjects.Length; i++)
                busStops[i] = stopObjects[i].transform;
            Array.Sort(busStops, (a, b) => a.position.z.CompareTo(b.position.z));
            onboardPax = UnityEngine.Random.Range(0, 8);
        }

        Debug.Log($"<color=green><b>[Bus]</b></color> Engine: <b>{engineType}</b> | TX: <b>{tx.ToUpper()}</b> " +
                  $"| IDLE: {IDLE} | GOV: {GOV}" +
                  (stopPlayerPaxSystem ? " | <color=cyan>PLAYER BUS — pax/stops owned by PlayerHandoff</color>"
                                       : $" | Stops: {busStops.Length} | PAX: {onboardPax}") +
                  (articulationMode != ArticulationMode.None
                      ? $" | <color=yellow>ARTIC: {articulationMode}</color>"
                      : ""));
    }

    // Called by road generator when stop list changes (NPC buses only)
    public void UpdateStopList(List<Transform> newStopList)
    {
        if (stopPlayerPaxSystem) return;
        busStops = newStopList.ToArray();
        Array.Sort(busStops, (a, b) => a.position.z.CompareTo(b.position.z));
        announcedStops.Clear();
    }

    public void ApplyEngineConstants()
    {
        if (audioEngine == null) return;
        audioEngine.engineType = (BusAudioEngine.EngineType)(int)engineType;
        audioEngine.tx = tx;
        audioEngine.epRevStages = epRevStages;
        // [FIX] diwaOpt1_1-5 were previously only ever pushed ONCE, inside the
        // Start()-time audioEngine construction block -- this wrapper (the one
        // actually called again on engine-start/battery-on/re-possession, per
        // whatever ignition sequence lives in PlayerHandoff) never re-synced
        // them. Any per-bus DIWA character customization that happened after
        // Start() (or on a re-adopted NPC slot whose fields changed) silently
        // never reached the audio engine. Copying them here too, every call,
        // makes this method the single complete "push everything to audio"
        // entry point regardless of which code path calls it.
        audioEngine.diwaOpt1_1 = diwaOpt1_1;
        audioEngine.diwaOpt1_2 = diwaOpt1_2;
        audioEngine.diwaOpt1_3 = diwaOpt1_3;
        audioEngine.diwaOpt1_4 = diwaOpt1_4;
        audioEngine.diwaOpt1_5 = diwaOpt1_5;
        audioEngine.diwaOpt1_6 = diwaOpt1_6;
        audioEngine.diwaOpt1_7 = diwaOpt1_7;
        audioEngine.diwaOpt1_8 = diwaOpt1_8;
        // [ADD] tx alone confirms articulated for B500R/H50EP(any gen)/HDS300/etc
        // (60ft-only in the real fleet chart); for ambiguous tx (Voith/ZF/B400R,
        // shared across 35/40/60ft) this falls back to the isArticulated flag
        // handed down from FleetMetadata.
        audioEngine.isArticulatedEngine =
            FleetSeriesDefinition.ResolveIsArticulatedEngine(tx, isArticulated);
        audioEngine.ratedTier = FleetSeriesDefinition.ClampToLegalTier(
            (BusSimulationController.EngineType)(int)engineType,
            audioEngine.isArticulatedEngine,
            ratedTierOverride);
        audioEngine.centerAxleType = FleetSeriesDefinition.ResolveCenterAxleType(
            (BusSimulationController.EngineType)(int)engineType,
            audioEngine.ratedTier);
        audioEngine.isGillig = isGillig;
        audioEngine.ApplyEngineConstants();
        tx        = audioEngine.tx;
        IDLE      = audioEngine.IDLE;
        GOV       = audioEngine.GOV;
        CYLINDERS = audioEngine.CYLINDERS;
        audioEngine.kickdownKey = this.kickdownKey;
        audioEngine.hillMode    = this.hillMode;
    }
private Rigidbody _rb;

// [ADD] Fall-explosion state -- see FixedUpdate/OnCollisionEnter below.
private bool  _isFalling = false;
private float _fallStartY = 0f;
private const float FALL_EXPLOSION_THRESHOLD_M = 30f;
private bool  _explosionParticlesBuilt = false;
private ParticleSystem _explosionBurst;
private ParticleSystem _explosionSmoke;

    // ════════ UPDATE ════════

// ── Signal auto-cancel (player bus): a turn signal switches itself off once the bus has turned 30+ degrees
// from the heading it had when the signal came on (any direction). Hazards are left alone.
private const float SignalAutoCancelDegrees = 30f;
private bool  _sigTracking;
private float _sigStartYaw;
private bool  _sigStartedLeft;

private void UpdateSignalAutoCancel()
{
    if (PlayerHandoff.Instance == null || PlayerHandoff.Instance.playerBus != this) { _sigTracking = false; return; }
    var ext = BusExteriorLightController.Find(this);
    if (ext == null) { _sigTracking = false; return; }

    bool signal = ext.LeftSignalOn || ext.RightSignalOn;
    if (!signal || ext.HazardsOn) { _sigTracking = false; return; }

    float yaw = transform.eulerAngles.y;
    // (Re)start tracking when a signal comes on, or when the player switches sides.
    if (!_sigTracking || _sigStartedLeft != ext.LeftSignalOn)
    {
        _sigTracking = true;
        _sigStartYaw = yaw;
        _sigStartedLeft = ext.LeftSignalOn;
        return;
    }

    if (Mathf.Abs(Mathf.DeltaAngle(_sigStartYaw, yaw)) >= SignalAutoCancelDegrees)
    {
        ext.SetLeftSignal(false);
        ext.SetRightSignal(false);
        _sigTracking = false;
    }
}

private void Update()

    {
        UpdateSignalAutoCancel();
        // [FIXED, PER REQUEST — "buscontroller doesn't tell audio engine
        // this either, I don't hear anything"] Root cause of the parking
        // brake (and very likely the RPM-cut/doors issues patched
        // separately): this script and NPCBusController BOTH run their own
        // full Update() every frame on the same GameObject whenever a
        // player is driving via the hop-in mechanic (NPCBusController's own
        // header comment: "scheduling is completely bypassed in favor of
        // HandlePlayerDriving()" -- it IS the live driving path once
        // isPlayer is true). Nothing ever disabled this script's Update(),
        // so BOTH independently read input, hold their OWN separate
        // parkingBrake/accel/bkPd fields, and both write to and Tick() the
        // SAME shared audioEngine every frame -- whichever one runs last in
        // Unity's script order wins for that frame, and since it's not
        // consistently the one the player actually pressed P on, the value
        // audioEngine sees flickers back to stale/false almost immediately.
        // NPCBusController is the one with "isPlayer" and the merged
        // driving logic, so it's the real authority once a hop-in has
        // happened -- this script steps aside entirely rather than racing
        // it, exactly like NPCBusController's PollPlayerInput/
        // HandlePlayerDriving already say they're meant to fully replace.
        if (_npcForBusID != null && _npcForBusID.isPlayer) return;

        float dt = Time.deltaTime;

        // Forced every frame per explicit request -- isKinematic=false,
        // interpolation=None. Not just set-and-forget in Start() anymore;
        // this re-applies it every Update in case anything else (another
        // script, a prefab override, physics re-enabling it) flips these
        // back. Flagged again here since it's the same setup the comment in
        // Start() explicitly warns caused collisions to stop registering
        // when combined with MovePosition-driven motion.
        if (_rb != null)
        {
            _rb.isKinematic   = false;
            _rb.interpolation = RigidbodyInterpolation.None;
        }

        // TX selector
        if (Input.GetKeyDown(KeyBindings.Current.gearReverse)) { currentDirection = GearDirection.Reverse; gear = 0; spd = 0; Debug.Log("<color=orange>[TX]</color> REVERSE"); }
        if (Input.GetKeyDown(KeyBindings.Current.gearNeutral)) { currentDirection = GearDirection.Neutral; gear = 0; spd = 0; Debug.Log("<color=orange>[TX]</color> NEUTRAL"); }
        if (Input.GetKeyDown(KeyBindings.Current.gearDrive)) { currentDirection = GearDirection.Drive;   gear = 0; spd = 0; Debug.Log("<color=orange>[TX]</color> DRIVE"); }

        // Door hotkeys (1 / Shift+1) are handled exclusively by
        // PlayerHandoff.Update() now — it was ALSO checking this same key
        // combo and calling its own separate HandleDoorToggle/
        // HandleRearDoorToggle, so both scripts fired once per press,
        // toggling the same physical door rig twice in the same frame and
        // cancelling out visually (looked exactly like "the hotkey does
        // nothing," while the console's DOORS button / door / rdoor
        // commands — which only trigger one handler once — worked fine).
        // This script's own HandleDoorToggle/HandleRearDoorToggle methods
        // are left in place in case anything else still calls them
        // directly; only the keyboard trigger is removed.

        if (Input.GetKeyDown(KeyBindings.Current.parkingBrake))
        {
            parkingBrake = !parkingBrake;
            Debug.Log($"<color=cyan>[Parking Brake]</color> {(parkingBrake ? "<color=red>APPLIED</color>" : "<color=green>RELEASED</color>")}");
        }

        // [FIX] Gated on real door-animation state now, same as
        // NPCBusController — see DoorsFullyClosed.
        //
        // [ADD] Now also locked out by a Full-shutdown breakdown. Without
        // this, a breakdown only ever got enforced in FixedUpdate (see the
        // "[ADD] Same unified breakdown effect" block below), which forces
        // accel/bkPd/spd back to a stop — but Update() runs AFTER
        // FixedUpdate in the same frame and immediately recomputes accelKey/
        // brakeKey from raw input here, unaware anything's wrong, and
        // UpdateSimulation() below ramps `accel` back up from that input
        // before the frame is out. Next FixedUpdate forces it back down,
        // next Update ramps it back up again — a genuine fight between the
        // two methods every frame, unlike an NPC (whose AI computes 0 accel
        // and the breakdown override happens in the exact same tick/method,
        // nothing downstream ever re-inflates it). Folding the breakdown
        // check into drivingLocked here stops accelKey/brakeKey from ever
        // being set in the first place while shut down, so there's nothing
        // left for UpdateSimulation to ramp up.
        bool drivingLocked = !DoorsFullyClosed || parkingBrake
            || (BusBreakdownSystem.Instance != null && _npcForBusID != null
                && BusBreakdownSystem.Instance.RequiresFullShutdown(_npcForBusID.busID));
        audioEngine.kickdownKey = !drivingLocked && Input.GetKey(KeyBindings.Current.kickdown);
        // hillMode was only ever pushed to audioEngine once, in Start()'s
        // object initializer -- toggling it later (inspector, script,
        // route-triggered) never reached the audio engine at all. Syncing
        // it every frame here, same as kickdownKey above, fixes that.
        audioEngine.hillMode = this.hillMode;

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
            // Legacy mode zeroed speed instantly here. Physical mode instead
            // applies full service brake in FixedUpdate — the bus SETTLES to
            // a stop with real deceleration, never teleports to 0 km/h.
            if (!physicalDrive) spd = 0f;
        }
        // [FIX] This previously ran unconditionally every frame, silently
        // overwriting the safer `!drivingLocked && ...` kickdown assignment
        // just above it — meaning holding Shift always triggered kickdown
        // even with doors open or the parking brake set. Found this while
        // wiring Shift+1 for the new rear-door toggle: without this gate,
        // every Shift+1 press would ALSO fire a kickdown rev, since this
        // line only checked LeftShift with no regard for drivingLocked at all.
        kickdownKey = !drivingLocked && Input.GetKey(KeyBindings.Current.kickdown);
    if (audioEngine != null) audioEngine.kickdownKey = kickdownKey;

    // Converter Stall Hold — Shift held while driving. Same drivingLocked
    // gate as kickdown so it can't be triggered with doors open / parking
    // brake set / the bus otherwise locked out of player control.
    stallHoldKey = !drivingLocked && KeyBindings.ShiftModifierHeld;
    if (audioEngine != null) audioEngine.stallHoldKey = stallHoldKey;

        if (!stopPlayerPaxSystem)
            UpdateBusStopSystem();

        // [FIX -- root cause of "RPM stuck at a fixed value with parking
        // brake set, no matter what tx/engine"] This used to skip
        // UpdateSimulation() ENTIRELY whenever drivingLocked was true
        // (doors open OR parking brake set) -- and UpdateSimulation is
        // what calls audioEngine.Tick(dt), the thing that actually runs
        // the RPM/gear simulation every frame. So the instant the parking
        // brake went on, the whole engine sim just stopped being ticked at
        // all -- not "settled to a flat idle," genuinely frozen at
        // whatever RPM/gear it happened to be at that exact frame, for
        // every tx/engine uniformly, since none of them ever got another
        // Tick() call. drivingLocked is meant to gate PLAYER INPUT
        // (steering/accel/brake capture, already zeroed above when
        // locked) -- it was never supposed to gate whether the engine
        // itself keeps running. accel/bkPd are already safely driven to 0
        // above when locked, so simulating through unconditionally just
        // lets the engine correctly idle (including the parking-brake
        // idle-hunt wobble) instead of standing still.
        UpdateSimulation(Time.time);

        // Mobile full throttle automatically requests kickdown. On PC the
        // throttle value is held at 1 by the keyboard, so use manual keys.
        kickdownKey = !drivingLocked
            && (Input.GetKey(KeyBindings.Current.kickdown)
            || (Application.isMobilePlatform && accel >= kickdownThrottleThreshold));
        if (audioEngine != null) audioEngine.kickdownKey = kickdownKey;

        // ── Steering INPUT capture only — everything physical (rate-limited
        //    wheel swing, authority caps, motion, trailer) runs in FixedUpdate.
        _steerAxisInput      = drivingLocked
            ? 0f
            : Mathf.Clamp(Input.GetAxis("Horizontal") + mobileSteerInput, -1f, 1f);
        if (invertSteeringInReverse && currentDirection == GearDirection.Reverse) _steerAxisInput = -_steerAxisInput;
        _drivingLockedCached = drivingLocked;

        // ── Articulated high-speed front bob (cosmetic only) ────────────────
        // Applied to a dedicated "front" child mesh transform, NOT this
        // script's own transform — the root here is what FixedUpdate's
        // rear-axle model drives via Rigidbody.MovePosition, and bobbing
        // that directly would fight the kinematic physics (and would also
        // corrupt GetHitchLocalOffset's renderer-bounds scan if it ever
        // re-measures). A separate cosmetic child sidesteps both.
        UpdateArticulatedBob(dt);

        // Periodic log
        logTimer += dt;
        if (running && logTimer >= 0.5f)
        {
            string pbStr = parkingBrake ? " | <color=red>PB</color>" : "";
            string drStr = doorsOpen    ? " | <color=yellow>DOORS OPEN</color>" : "";
            // Debug.Log($"[Bus] {spd:F1} km/h | {rpm:F0} RPM | G{gear}{pbStr}{drStr}");
            logTimer = 0f;
        }
    }

    /// <summary>Speed-dependent vertical bob for articulated buses. Front
    /// (a cosmetic "front" child mesh, same one CameraFollow25D targets)
    /// bobs harder than the back (trailerPivot) -- ramps in between
    /// bobStartKph and bobFullKph, silent below and capped above. Back bob
    /// is folded directly into UpdateTrailerRealPhysics's own placement
    /// each frame since that method already fully recomputes trailerPivot's
    /// world position from scratch every call.</summary>
    private void UpdateArticulatedBob(float dt)
    {
        if (articulationMode == ArticulationMode.None) { return; }

        if (!_frontVisualCached)
        {
            // [FIX] Was transform.Find("front") only -- an EXACT, case-sensitive,
            // direct-children-only match. Any bus whose front visual isn't spelled
            // exactly "front" (different case, "Front", a naming variant) silently
            // found nothing here -- and since the whole method used to bail out
            // right after ("no front child -- nothing to bob"), that ALSO killed the
            // BACK/hinge bob for that bus, even though the back bob doesn't actually
            // need this transform at all. Falls back to a case-insensitive substring
            // search among direct children (same pattern BusKneelBody's own
            // AutoDetectBodyGroup already uses) before giving up.
            _frontVisualTransform = transform.Find("front");
            if (_frontVisualTransform == null)
            {
                foreach (Transform child in transform)
                {
                    if (child.name.ToLowerInvariant().Contains("front")) { _frontVisualTransform = child; break; }
                }
            }
            if (_frontVisualTransform != null)
                _frontVisualRestLocalRot = _frontVisualTransform.localRotation;
            else
                Debug.LogWarning($"[{name}] UpdateArticulatedBob: no child transform named (or containing) \"front\" found -- the front bob will do nothing on this bus. The back/hinge bob below is unaffected.", this);
            _frontVisualCached = true;
        }

        // [FIX] Used to `return` here if no front child was found, which also skipped
        // computing _currentBackBobPitchDeg below -- the back/hinge bob doesn't need
        // _frontVisualTransform at all, so a missing front child shouldn't silence it
        // too. Front-specific application is now its own guarded block further down.

        float speedFrac = Mathf.InverseLerp(bobStartKph, Mathf.Max(bobStartKph + 0.01f, bobFullKph), spd);
        speedFrac = Mathf.Clamp01(speedFrac);

        // Frequency scales with the same fraction -- faster road speed,
        // faster harmonic, same as real tire/chassis vibration picking up.
        float freqNow = Mathf.Lerp(bobFrequencyHz * 0.4f, bobFrequencyHz, speedFrac);
        _bobPhaseAccum += freqNow * 360f * dt; // degrees
        if (_bobPhaseAccum > 360f) _bobPhaseAccum -= 360f;

        // ROTATION, not translation: nose pitches up/down about local X,
        // layered on top of whatever rest-pose rotation the "front" child
        // already had (so this doesn't fight its baked-in mesh orientation).
        // [ADD] Same downhill-extreme boost the back bob already gets, reusing
        // _groundPitchDegSmoothed -- last FixedUpdate's real terrain pitch, already
        // sitting on this instance -- so the front reacts to a bridge/steep grade
        // too instead of only ever bobbing at its flat-road amplitude.
        float frontTerrainFactor = Mathf.Clamp01(Mathf.Abs(_groundPitchDegSmoothed) / Mathf.Max(0.01f, bobDownhillPitchForExtreme));
        float frontAmplitude = frontBobAmplitude * (1f + frontTerrainFactor * bobDownhillExtremeMultiplier);
        float frontPitchDeg = Mathf.Sin(_bobPhaseAccum * Mathf.Deg2Rad) * frontAmplitude * speedFrac;

        // "Strained" engine (diwaOpt1_8): at high revs the whole body picks up a fast, rough tremor, the
        // physical side of the BRRRR in BusAudioEngine. Small random jitter in pitch and roll, growing with revs.
        float strainPitch = 0f, strainRoll = 0f;
        if (diwaOpt1_8 && audioEngine != null)
        {
            float hi = Mathf.Clamp01(((audioEngine.rpm - audioEngine.IDLE) / Mathf.Max(1f, audioEngine.GOV - audioEngine.IDLE) - 0.62f) / 0.30f);
            float growlK = Mathf.Clamp01((audioEngine.rpm - 1000f) / 600f);
            if (hi > 0.001f || growlK > 0.001f)
            {
                float amp = 0.30f * hi * hi + 0.06f * growlK;
                strainPitch = (UnityEngine.Random.value * 2f - 1f) * amp;
                strainRoll  = (UnityEngine.Random.value * 2f - 1f) * amp * 0.8f;
            }
        }
        if (_frontVisualTransform != null)
            _frontVisualTransform.localRotation = _frontVisualRestLocalRot * Quaternion.Euler(frontPitchDeg + strainPitch, 0f, strainRoll);

        // Cache this frame's back bob pitch for UpdateTrailerRealPhysics to
        // fold into its own already-from-scratch world rotation each frame.
        float backPhase = (_bobPhaseAccum + bobBackPhaseOffsetDeg) % 360f;
        _currentBackBobPitchDeg = Mathf.Sin(backPhase * Mathf.Deg2Rad) * backBobAmplitude * speedFrac;
        _currentBackBobPitchDeg += strainPitch * 0.7f;
    }
    private float _currentBackBobPitchDeg = 0f;

    // ════════════════════════════════════════════════════════════════════════
    //  REAL PHYSICS — MOVEMENT + ARTICULATION (full rewrite)
    // ════════════════════════════════════════════════════════════════════════

    private bool _drivingLockedCached = true;

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        // [ADD] Fall-distance tracking for the explosion effect below --
        // PLAYER buses specifically, per instruction. Simple state machine:
        // once vertical velocity goes meaningfully negative (actually
        // falling, not just normal suspension jitter), remember the Y
        // position that fall started from. OnCollisionEnter below measures
        // the drop the instant we land and decides whether it crosses the
        // 30m threshold.
        if (_rb != null)
        {
            bool fallingNow = _rb.linearVelocity.y < -1f;
            if (fallingNow && !_isFalling)
            {
                _isFalling = true;
                _fallStartY = transform.position.y;
            }
            else if (!fallingNow && _isFalling && _rb.linearVelocity.y >= -0.2f)
            {
                // Velocity settled back near zero without a collision firing
                // (e.g. gently came to rest against something, no real
                // impact) -- not a real fall-and-land event, just reset.
                _isFalling = false;
            }
        }

        // [ADD] Same unified breakdown effect NPCBusController calls -- one
        // method, one place this logic can go wrong, instead of a second
        // hand-copied version (which is exactly how the engine-state and
        // door-timing bugs happened before). Applied here, BEFORE accel/
        // bkPd feed into this frame's physics integration below, so a
        // full-shutdown breakdown actually decelerates the real physics-
        // driven bus rather than just overwriting the displayed spd value
        // after the fact.
        if (BusBreakdownSystem.Instance != null && _npcForBusID != null)
        {
            // [ADD] Roll for a new breakdown here too, same as
            // NPCBusController.MaybeBreakDown -- full parity, one bus type
            // doesn't get breakdown odds the other doesn't.
            BusBreakdownSystem.Instance.TryRollBreakdown(_npcForBusID.busID, _vehicleSystem);
            BusBreakdownSystem.Instance.TryRollCosmeticSubBreakdown(_npcForBusID.busID, _vehicleSystem);

            BusBreakdownSystem.Instance.ApplyBreakdownToVehicle(
                _npcForBusID.busID, audioEngine, frontDoorSet, ref doorsOpen,
                ref spd, ref accel, ref bkPd, ref _breakdownDoorsHandled, dt,
                out _, out _, out _); // engine/battery/AC forced-off flags -- audioEngine already updated directly inside the call, nothing else here needs to cache them separately

        }

        float vAbs = Mathf.Abs(_velMS);
        float steerCap = maxSteerAngle;

        if (vAbs > 1.5f)
        {
            float latCapRad = Mathf.Atan(maxLatAccel * Mathf.Max(0.5f, wheelbase) / (vAbs * vAbs));
            steerCap = Mathf.Min(steerCap, latCapRad * Mathf.Rad2Deg);
        }

        if (articulationMode != ArticulationMode.None && trailerPivot != null)
        {
            float gammaAllowedRad = articHingeSafetyMargin * maxHingeAngle * Mathf.Deg2Rad;
            float rMin = trailerLength / Mathf.Max(0.05f, Mathf.Tan(gammaAllowedRad));
            float articCapRad = Mathf.Atan(Mathf.Max(0.5f, wheelbase) / rMin);
            steerCap = Mathf.Min(steerCap, articCapRad * Mathf.Rad2Deg);
        }

        float steerTarget = _drivingLockedCached ? currentSteerAngle
                          : Mathf.Clamp(_steerAxisInput * steerCap, -steerCap, steerCap);
        currentSteerAngle = Mathf.MoveTowards(currentSteerAngle, steerTarget, steerRateDegPerSec * dt);

        if (physicalDrive)
        {
            float m = Mathf.Max(1000f, massKg);
            float driveDir = currentDirection == GearDirection.Drive   ?  1f
                           : currentDirection == GearDirection.Reverse ? -1f : 0f;

            float fTraction = 0f;
            if (!_drivingLockedCached && driveDir != 0f && accel > 0.001f)
            {
                float powerLimited = (maxPowerKW * 1000f) / Mathf.Max(2.0f, vAbs);
                // [NEW] Forceful gear-1 stretch for Voith, per the XN40 D864.6
                // reference clip -- that bus's real launch runs noticeably
                // slower/longer than our old gear-1 timing assumed. Retiming
                // the audio envelope alone (BusAudioEngine's V6_G1_BUILD_SEC)
                // isn't enough on its own: if the PHYSICAL gear-1 window is
                // unchanged, the stretched whine curve just gets guillotined
                // early again. Cutting launch tractive force only while in
                // gear 1 on Voith buses makes the real climb through
                // VOITH_GEAR1_UPSHIFT_SPD take longer, so the two line up.
                // Scoped to tx=="voith" and gear<=1 only -- doesn't touch any
                // other transmission's launch or Voith's own higher gears.
                float launchForceCap = maxTractiveForceN;
                if (tx == "voith" && gear <= 1) launchForceCap *= 0.80f;
                fTraction = accel * Mathf.Min(launchForceCap, powerLimited) * driveDir;
            }

            float brakeCmd = _drivingLockedCached ? 1f : bkPd;
            float fBrake = brakeCmd * brakeMaxDecel * m;

            float fAero = 0.5f * 1.18f * dragCdA * _velMS * Mathf.Abs(_velMS);

            float fRoll = (vAbs > 0.05f) ? rollingCrr * m * 9.81f * Mathf.Sign(_velMS) : 0f;

            float fBrakeSigned = (vAbs > 0.02f) ? fBrake * Mathf.Sign(_velMS) : 0f;

            float a = (fTraction - fBrakeSigned - fAero - fRoll) / m;
            float newVel = _velMS + a * dt;

            if (Mathf.Abs(_velMS) > 0.001f
                && Mathf.Sign(newVel) != Mathf.Sign(_velMS)
                && Mathf.Sign(fTraction) != Mathf.Sign(newVel))
            {
                newVel = 0f;
            }
            _velMS = newVel;

            float fwdCap = MAX_SPD / 3.6f;
            float revCap = reverseMaxKph / 3.6f;
            _velMS = Mathf.Clamp(_velMS, -revCap, fwdCap);

            if (_drivingLockedCached && vAbs < 0.5f) _velMS = 0f;

            spd = Mathf.Abs(_velMS) * 3.6f;
        }
        else
        {
            _velMS = (spd / 3.6f) * (currentDirection == GearDirection.Reverse ? -1f : 1f);
        }

        _signedVel = _velMS;

        if (Mathf.Abs(_signedVel) >= 0.003f)
        {
            float steerRad = currentSteerAngle * Mathf.Deg2Rad;
            float yawRate  = _signedVel * Mathf.Tan(steerRad) / Mathf.Max(0.5f, wheelbase);
            _yawRateRad = yawRate;

            Vector3 rearAxleLocal = GetRearAxleLocal();
            Vector3 rearAxleWorld = transform.TransformPoint(rearAxleLocal);
            Vector3 newRearAxle   = rearAxleWorld + BusForward * (_signedVel * dt);

            float yawDeg = yawRate * Mathf.Rad2Deg * dt;
            Quaternion newRot = Quaternion.AngleAxis(yawDeg, Vector3.up) * transform.rotation;
            Vector3    newPos = newRearAxle - (newRot * rearAxleLocal);

            if (_rb != null) { _rb.MovePosition(newPos); _rb.MoveRotation(newRot); }
            else             { transform.SetPositionAndRotation(newPos, newRot); }
        }
        else
        {
            _yawRateRad = 0f;
        }

        if (articulationMode != ArticulationMode.None && trailerPivot != null)
        {
            // TractrixJoint is the ONLY mode on the new geometric solver.
            // Everything else stays on the legacy history-follow path so
            // scenes that still have the old mode names assigned keep working
            // unchanged (position was always fine there -- only its heading
            // source was wrong, which is exactly what TractrixJoint fixes).
            if (articulationMode == ArticulationMode.TractrixJoint)
            {
                UpdateTrailerTractrix(dt);
            }
            else
            {
                RecordHitchHistory();
                UpdateTrailerRealPhysics(dt);
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  FALL EXPLOSION — PLAYER buses only. A fall exceeding
    //  FALL_EXPLOSION_THRESHOLD_M (30m) triggers a black/orange explosion
    //  burst on landing impact, transitioning into lingering smoke
    //  afterward. Reuses the exact same material-safety pattern
    //  NPCBusController.BuildBreakdownSmoke() already established (an
    //  explicit Sprites/Default material assignment -- a
    //  ParticleSystemRenderer with no material assigned renders as
    //  Unity's magenta "missing material" fallback, not the desired
    //  color, so every renderer here gets one explicitly).
    // ═════════════════════════════════════════════════════════════════════
    private void OnCollisionEnter(Collision collision)
    {
        if (!_isFalling) return;
        _isFalling = false;

        float fallDistance = _fallStartY - transform.position.y;
        if (fallDistance >= FALL_EXPLOSION_THRESHOLD_M)
            TriggerFallExplosion();
    }

    private void TriggerFallExplosion()
    {
        if (!_explosionParticlesBuilt)
        {
            _explosionBurst = BuildExplosionBurst();
            _explosionSmoke = BuildExplosionSmoke();
            _explosionParticlesBuilt = true;
        }

        _explosionBurst.transform.position = transform.position;
        _explosionBurst.Play();

        // Smoke starts right as the burst plays and lingers well after it
        // finishes -- the burst itself is short (see startLifetime below),
        // the smoke is what's left behind once the fireball fades.
        _explosionSmoke.transform.position = transform.position;
        _explosionSmoke.Play();

        Debug.Log($"[BusController] Fall of {_fallStartY - transform.position.y:0.0}m exceeded " +
                  $"the {FALL_EXPLOSION_THRESHOLD_M}m threshold — explosion triggered.");
    }

    /// <summary>The actual explosion -- a one-shot burst, black/orange,
    /// particles growing rapidly over their short lifetime so it reads as
    /// an expanding fireball rather than a lingering effect.</summary>
    private ParticleSystem BuildExplosionBurst()
    {
        var go = new GameObject("FallExplosionBurst");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;
        var ps = go.AddComponent<ParticleSystem>();

        var main = ps.main;
        main.loop = false;
        main.startLifetime = 0.6f; // short and punchy -- the burst itself shouldn't linger, that's the smoke's job
        main.startSpeed = 4f;
        main.startSize = 0.4f; // starts small -- sizeOverLifetime below is what makes it "expand rapidly"
        // Black/orange per instruction -- MinMaxGradient randomly picks
        // one of the two per particle so the burst reads as a mixed
        // fireball, not a flat single color.
        var grad = new ParticleSystem.MinMaxGradient(
            new Color(0.08f, 0.08f, 0.08f, 1f),   // near-black
            new Color(1.0f, 0.45f, 0.05f, 1f));    // orange
        main.startColor = grad;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = ps.emission;
        emission.rateOverTime = 0f; // no continuous emission -- burst-only
        emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, 40, 55) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.5f;

        // [ADD] Rapid expansion -- this is THE thing that makes it read as
        // an explosion instead of a puff. Size ramps from small to large
        // very quickly (front-loaded curve), then holds, over the short
        // 0.6s lifetime.
        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        var sizeCurve = new AnimationCurve();
        sizeCurve.AddKey(0f, 1f);
        sizeCurve.AddKey(0.15f, 6f);  // most of the growth happens in the first 15% of its life
        sizeCurve.AddKey(1f, 7f);
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var alphaCurve = new AnimationCurve();
        alphaCurve.AddKey(0f, 1f);
        alphaCurve.AddKey(0.7f, 0.8f);
        alphaCurve.AddKey(1f, 0f); // fades out as the smoke takes over
        var colGrad = new Gradient();
        colGrad.SetKeys(
            new GradientColorKey[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new GradientAlphaKey[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.7f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = new ParticleSystem.MinMaxGradient(colGrad);

        AssignSafeMaterial(ps, "FallExplosionBurst");
        return ps;
    }

    /// <summary>Lingering smoke left behind after the burst fades --
    /// darker/heavier than NPCBusController's engine-idle smoke, since
    /// this represents "just exploded" rather than routine engine wear.</summary>
    private ParticleSystem BuildExplosionSmoke()
    {
        var go = new GameObject("FallExplosionSmoke");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;
        var ps = go.AddComponent<ParticleSystem>();

        var main = ps.main;
        main.loop = false;
        main.duration = 4f;
        main.startLifetime = 3.5f; // lingers well after the burst's 0.6s has faded
        main.startSpeed = 1.2f;
        main.startSize = 1.8f;
        main.startColor = new Color(0.15f, 0.15f, 0.15f, 0.6f); // dark, heavy smoke -- not the gray engine-wear puff
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var emission = ps.emission;
        emission.rateOverTime = 10f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 25f;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 2.5f));

        AssignSafeMaterial(ps, "FallExplosionSmoke");
        return ps;
    }

    /// <summary>Same fix NPCBusController.BuildBreakdownSmoke() already
    /// established -- a ParticleSystemRenderer with no material assigned
    /// renders as Unity's magenta fallback, not the particle's actual
    /// intended color. Every renderer built here gets one explicitly.</summary>
    private void AssignSafeMaterial(ParticleSystem ps, string debugName)
    {
        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        var shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            var mat = new Material(shader);
            mat.color = Color.white; // actual color comes from main.startColor/colorOverLifetime per-particle
            renderer.material = mat;
        }
        else
        {
            Debug.LogWarning($"[BusController] Sprites/Default shader not found -- {debugName} will render with whatever Unity's fallback material is (likely magenta).");
        }
    }


    private Vector3 GetRearAxleLocal()
    {
        if (_rearAxleCached) return _rearAxleLocal;

        Vector3 hitchLocal = GetHitchLocalOffset(transform);
        _rearAxleLocal = hitchLocal + new Vector3(0f, 0f, LocalForwardSign * rearAxleFromRearEdge);
        _rearAxleCached = true;
        return _rearAxleLocal;
    }

    private bool    _hitchOffsetCached = false;
    private Vector3 _cachedHitchLocal;

    private Vector3 GetHitchLocalOffset(Transform tractor)
    {
        if (pivotPoint != null)
            return tractor.InverseTransformPoint(pivotPoint.position);

        if (!autoDetectHitch)
            return new Vector3(0f, 0f, -LocalForwardSign * Mathf.Abs(tractorRearOffset));

        if (_hitchOffsetCached) return _cachedHitchLocal;

        Renderer[] allRends = tractor.GetComponentsInChildren<Renderer>();
        Renderer[] rends = trailerPivot != null
            ? Array.FindAll(allRends, r => !r.transform.IsChildOf(trailerPivot))
            : allRends;

        if (trailerPivot != null && rends.Length != allRends.Length)
            Debug.LogWarning($"[{name}] GetHitchLocalOffset excluded {allRends.Length - rends.Length} renderer(s) " +
                              $"belonging to trailerPivot ('{trailerPivot.name}') from the hitch bounds scan -- " +
                              "this script is sitting on a transform that's a common ancestor of BOTH the tractor " +
                              "AND trailer meshes. For correct physics long-term, move this script onto a dedicated " +
                              "front-section-only child object with trailerPivot as a sibling, not a descendant.");

        if (rends.Length == 0)
        {
            Debug.LogWarning($"[{name}] autoDetectHitch is on but no Renderers were found under {tractor.name} " +
                              "to measure -- falling back to tractorRearOffset. Assign pivotPoint manually instead.");
            _cachedHitchLocal = new Vector3(0f, 0f, -LocalForwardSign * Mathf.Abs(tractorRearOffset));
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
        float rearZ   = tractorMeshFacesBackward ? max.z : min.z;

        _cachedHitchLocal = new Vector3(centerX, 0f, rearZ);
        _hitchOffsetCached = true;
        Debug.Log($"[{name}] Auto-detected hitch offset: {_cachedHitchLocal} " +
                  $"(from {rends.Length} renderer(s), meshFacesBackward={tractorMeshFacesBackward}).");
        return _cachedHitchLocal;
    }

    // ── History recording ────────────────────────────────────────────────────
    // Records the hitch point's exact pose, spaced by distance traveled so
    // behavior is identical at any framerate/speed. This is the entire
    // trick: the rear section later reads an EXACT past sample instead of
    // re-deriving an approximate position from the current one.
    private void RecordHitchHistory()
    {
        Transform tractor = TractorTransform;
        Vector3 hitchLocal = GetHitchLocalOffset(tractor);
        Vector3 hitchWorld = tractor.TransformPoint(hitchLocal);
        hitchWorld.y = tractor.position.y;

        if (_hitchHistory.Count == 0)
        {
            _hitchHistory.Add(new HistorySample { pos = hitchWorld, rot = tractor.rotation, dist = 0f });
            return;
        }

        var last = _hitchHistory[_hitchHistory.Count - 1];
        float stepDist = Vector3.Distance(last.pos, hitchWorld);
        if (stepDist < HISTORY_SAMPLE_SPACING) return; // not moved far enough yet -- don't spam samples while idling

        _totalDistTraveled += stepDist;
        _hitchHistory.Add(new HistorySample { pos = hitchWorld, rot = tractor.rotation, dist = _totalDistTraveled });

        // Prune anything further back than we could ever need -- keeps the
        // buffer bounded regardless of session length. A little slack past
        // the largest plausible rearSectionOffset (say 30m) is plenty.
        float minNeeded = _totalDistTraveled - 30f;
        int trimTo = 0;
        while (trimTo < _hitchHistory.Count - 1 && _hitchHistory[trimTo].dist < minNeeded) trimTo++;
        if (trimTo > 0) _hitchHistory.RemoveRange(0, trimTo);
    }

    /// <summary>Returns the recorded pose from `offsetDist` metres behind the
    /// current hitch position, linearly interpolated between the two
    /// nearest samples. Falls back to the current tractor pose if there's
    /// not enough history yet (e.g. right at spawn, or reversing past the
    /// start of the buffer) -- reads as "rigid" for a moment rather than
    /// snapping to a wrong pose.</summary>
    private bool TrySampleHistory(float offsetDist, out Vector3 pos, out Quaternion rot)
    {
        Transform tractor = TractorTransform;
        pos = tractor.position;
        rot = tractor.rotation;

        if (_hitchHistory.Count < 2) return false;

        float targetDist = _totalDistTraveled - offsetDist;
        if (targetDist <= _hitchHistory[0].dist)
        {
            pos = _hitchHistory[0].pos; rot = _hitchHistory[0].rot;
            return _hitchHistory[0].dist <= targetDist + HISTORY_SAMPLE_SPACING * 2f; // only "found" if we're not just clamping to the oldest sample far short of what was asked for
        }

        for (int i = _hitchHistory.Count - 1; i > 0; i--)
        {
            if (_hitchHistory[i].dist <= targetDist)
            {
                var a = _hitchHistory[i];
                var b = _hitchHistory[i + 1 < _hitchHistory.Count ? i + 1 : i];
                float span = Mathf.Max(0.0001f, b.dist - a.dist);
                float t = Mathf.Clamp01((targetDist - a.dist) / span);
                pos = Vector3.Lerp(a.pos, b.pos, t);
                rot = Quaternion.Slerp(a.rot, b.rot, t);
                return true;
            }
        }
        return false;
    }

    private void UpdateTrailerRealPhysics(float dt)
    {
        if (trailerPivot == null) return;
        Transform tractor = TractorTransform;

        Vector3 fwd = BusForward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) return;
        fwd.Normalize();
        float tractorYawDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;

        // [REDESIGN] Exact past pose instead of a re-derived approximation.
        // If there's not enough history yet, hold rigid (same heading as
        // the tractor) rather than guessing -- matches the old
        // _articInitialized first-frame behavior.
        bool haveSample = TrySampleHistory(rearSectionOffset, out Vector3 histPos, out Quaternion histRot);
        if (!haveSample)
        {
            histPos = tractor.TransformPoint(GetHitchLocalOffset(tractor));
            histPos.y = tractor.position.y;
            histRot = tractor.rotation;
        }

        Vector3 histFwd = histRot * Vector3.forward;
        histFwd.y = 0f;
        if (histFwd.sqrMagnitude < 0.0001f) histFwd = fwd;
        histFwd.Normalize();
        float trailerYawDeg = Mathf.Atan2(histFwd.x, histFwd.z) * Mathf.Rad2Deg;

        // Bellows angle is just the live difference between the tractor's
        // current heading and the rear section's (history-derived) heading
        // -- same meaning as before, cheaper to compute, and clamped to the
        // same mechanical stop.
        _hingeAngle     = Mathf.Clamp(Mathf.DeltaAngle(trailerYawDeg, tractorYawDeg), -maxHingeAngle, maxHingeAngle);
        currentHingeYaw = trailerYawDeg;
        _trailerYawDeg  = trailerYawDeg; // kept in sync for any legacy reader

        Vector3 trailerFwdWorld = new Vector3(
            Mathf.Sin(trailerYawDeg * Mathf.Deg2Rad), 0f,
            Mathf.Cos(trailerYawDeg * Mathf.Deg2Rad));

        Quaternion physRot = Quaternion.LookRotation(trailerFwdWorld, Vector3.up);
        Quaternion restOffset = Quaternion.Euler(trailerMeshRestOffset);
        Quaternion bobRot = Quaternion.Euler(_currentBackBobPitchDeg, 0f, 0f);
        Quaternion finalRot = trailerMeshFacesBackward
            ? physRot * Quaternion.Euler(0f, 180f, 0f) * restOffset * bobRot
            : physRot * restOffset * bobRot;

        trailerPivot.rotation = finalRot;

        // Position comes straight from the recorded sample -- NOT projected
        // backward from the current hitch point. trailerFrontOffset still
        // trims where the pivot origin sits relative to that sampled point
        // (mesh rigging, unchanged), same as before.
        Vector3 basePos = histPos - trailerFwdWorld * trailerFrontOffset;
        Vector3 trimmed = basePos
            + trailerPivot.right   * (trailerLateralTrim + pivotRelativeOffset.x)
            + trailerPivot.up      * pivotRelativeOffset.y
            + trailerPivot.forward * pivotRelativeOffset.z;
        trailerPivot.position = trimmed;

        DeformBellows(_hingeAngle);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  NEW MODE — TRACTRIX / RIGID-ROD JOINT  (ArticulationMode.TractrixJoint)
    // ────────────────────────────────────────────────────────────────────────
    //  Why this exists: the history-follow model tracked POSITION correctly
    //  (rear section sits on a real past hitch point) but took the section's
    //  HEADING from the tractor's recorded past orientation (histRot). That is
    //  the wrong quantity -- every tractor-trailer kinematic model defines the
    //  rear section's heading as the direction of the AXLE->HITCH line, not the
    //  tractor's facing N metres ago. On curves those differ by the whole hinge
    //  angle, so the section pointed wrong through every transition, and the
    //  double meshFacesBackward handling (histRot*forward is already backward,
    //  then finalRot adds another 180) flipped it inconsistently per turn dir.
    //
    //  This solver instead keeps ONE piece of state -- the rear axle's world
    //  position A -- and enforces the real constraint directly:
    //
    //     A is rigidly L = trailerLength behind the hitch H, and can only
    //     roll toward it:   A_new = H - L * normalize(H - A_prev)
    //
    //  In the discrete limit that traces the tractrix of the hitch's path --
    //  the exact curve the ODE models integrate -- but with ZERO drift and NO
    //  history buffer, because the axle position IS the state. The heading then
    //  falls straight out of the geometry as normalize(H - A), so it can
    //  neither slide off the path (position is constraint-pinned) nor
    //  mis-rotate (heading is derived, never replayed).
    //
    //  Rod length knob: uses `trailerLength` (documented as hitch->rear-axle,
    //  XD60 ≈ 6.9 m) -- NOT rearSectionOffset, which is the history model's own
    //  knob and is ignored here. Set trailerLength and you're done.
    // ════════════════════════════════════════════════════════════════════════
    [Header("Trailer Ground Pitch (dynamic X — TractrixJoint only)")]
    [Tooltip("Raycasts down at the hitch point and the rear axle to find real ground height under each, and pitches the trailer section (rotation X) to match the slope between them -- same idea as a real trailer's suspension riding a dip or crest independently of the tractor. This layers ON TOP of trailerMeshRestOffset (the baked-in rest pitch), not instead of it.")]
    public bool  trailerGroundPitchEnabled = true;
    [Tooltip("Layers the ground raycasts hit. Set this to your road/terrain layer only -- if it also hits the bus's own colliders you'll get garbage pitch.")]
    public LayerMask groundPitchLayer = ~0;
    [Tooltip("Raycast starts this far above the hitch/axle point and casts straight down. Needs to clear the tallest bump you drive over.")]
    public float groundPitchRayHeight = 2.5f;
    [Tooltip("How fast the pitch can change, degrees/second. Lower = softer suspension feel, higher = snaps to the slope instantly.")]
    public float groundPitchSmoothDegPerSec = 40f;
    [Tooltip("Hard clamp on the dynamic pitch, degrees, so a raycast miss or a weird collider spike can't fold the mesh.")]
    public float groundPitchMaxDeg = 20f;
    [Tooltip("Flip this if the pitch comes out upside-down for your mesh's axis convention (nose-up on a downhill instead of downhill) -- same per-rig flag pattern as tractorMeshFacesBackward/trailerMeshFacesBackward.")]
    public bool  groundPitchInverted = false;
    private float _groundPitchDegSmoothed = 0f;

    /// <summary>Raycasts straight down at a world XZ point and returns the hit
    /// Y. Returns false (and the point's own Y) if nothing was hit, so callers
    /// can fall back to flat instead of pitching off a bad sample.
    /// [FIX] Was a plain single Physics.Raycast -- on a road with traffic, the
    /// rear-axle sample point can end up directly over another bus (overtaking,
    /// merging, depot queue) and hit ITS roof instead of the road, reporting a
    /// huge fake rise/fall and pitching this trailer's rear up toward the 20°
    /// clamp ("rear leaves the ground and angles upward" at speed -- more
    /// likely at high speed since this bus and traffic both cover more ground
    /// between samples). NPCBusController.SampleGroundY already hit this exact
    /// bug and fixed it by filtering out bus colliders via the shared
    /// IsOwnCollider/GetCachedBus helpers on the paired NPCBusController
    /// component (_npcForBusID, same GameObject) -- reusing that here instead
    /// of duplicating a second collider list.</summary>
    private readonly RaycastHit[] _groundHits = new RaycastHit[12];
    private bool SampleGroundY(Vector3 worldPt, out float groundY)
    {
        Vector3 origin = new Vector3(worldPt.x, worldPt.y + groundPitchRayHeight, worldPt.z);
        int n = Physics.RaycastNonAlloc(origin, Vector3.down, _groundHits,
                                         groundPitchRayHeight * 2f, groundPitchLayer, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue; bool found = false; groundY = worldPt.y;
        for (int i = 0; i < n; i++)
        {
            var col = _groundHits[i].collider;
            if (col == null) continue;
            if (_npcForBusID != null && _npcForBusID.IsOwnCollider(col)) continue;
            if (NPCBusController.GetCachedBus(col) != null) continue;
            if (_groundHits[i].distance < bestDist) { bestDist = _groundHits[i].distance; groundY = _groundHits[i].point.y; found = true; }
        }
        return found;
    }

    /// <summary>Target pitch (deg, unsmoothed) from the slope between the
    /// hitch and rear-axle ground samples. Returns 0 (flat) if either
    /// raycast misses -- never pitch off a guess.</summary>
    private float ComputeGroundPitchTargetDeg(Vector3 hitchPt, Vector3 axlePt)
    {
        if (!trailerGroundPitchEnabled) return 0f;

        bool hHit = SampleGroundY(hitchPt, out float hitchGroundY);
        bool aHit = SampleGroundY(axlePt,  out float axleGroundY);
        if (!hHit || !aHit) return 0f;

        float horiz = Vector3.Distance(
            new Vector3(hitchPt.x, 0f, hitchPt.z),
            new Vector3(axlePt.x,  0f, axlePt.z));
        if (horiz < 0.05f) return 0f;

        // rise > 0 means the hitch end sits HIGHER than the axle end (nose of
        // the section pointed uphill toward the tractor) -- pitch the section
        // nose-up to match, same sense a real trailer's own suspension would.
        float rise = hitchGroundY - axleGroundY;
        float pitchDeg = Mathf.Atan2(rise, horiz) * Mathf.Rad2Deg;
        if (groundPitchInverted) pitchDeg = -pitchDeg;
        return Mathf.Clamp(pitchDeg, -groundPitchMaxDeg, groundPitchMaxDeg);
    }

    private Vector3 _rearAxleWorld;
    private bool    _tractrixInit = false;

    /// <summary>Force the rod solver to re-seat the rear axle straight behind
    /// the hitch on its next tick -- call after a teleport/respawn so the
    /// section doesn't drag from a stale world position.</summary>
    public void ResetTractrixJoint() => _tractrixInit = false;

    /// <summary>UNSTUCK: rights this bus after it has been flipped or tilted (NPC collisions, curbs, ramps). Works on
    /// whatever object this controller is attached to (the root, or the front section of an articulated rig): it keeps
    /// the bus's position and the direction it was travelling, levels pitch and roll, lifts it 1.5 m so it can settle
    /// on the ground, and clears every velocity so it isn't thrown again. The articulated rear section is re-seated
    /// straight behind the hitch on the next tick.</summary>
    public void RightBus()
    {
        Vector3 fwd = Vector3.ProjectOnPlane(BusForward, Vector3.up);
        if (fwd.sqrMagnitude < 0.05f)   // nose pointing straight up/down: fall back to something sane
        {
            fwd = Vector3.ProjectOnPlane(transform.up, Vector3.up);
            if (fwd.sqrMagnitude < 0.05f) fwd = Quaternion.Euler(0f, transform.eulerAngles.y, 0f) * Vector3.forward;
        }
        fwd.Normalize();
        Quaternion rot = Quaternion.LookRotation(tractorMeshFacesBackward ? -fwd : fwd, Vector3.up);
        Vector3 pos = transform.position + Vector3.up * 1.5f;

        if (_rb != null)
        {
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.position = pos;
            _rb.rotation = rot;
        }
        transform.SetPositionAndRotation(pos, rot);

        _velMS = 0f; _signedVel = 0f; _yawRateRad = 0f; spd = 0f;
        ResetTractrixJoint();
    }

    private void UpdateTrailerTractrix(float dt)
    {
        if (trailerPivot == null) return;
        Transform tractor = TractorTransform;

        // Hitch (joint) world point, driven by the tractor this frame.
        Vector3 hitch = tractor.TransformPoint(GetHitchLocalOffset(tractor));
        hitch.y = tractor.position.y;

        // Tractor REAL-travel heading (respects tractorMeshFacesBackward via
        // BusForward -- the property already resolves the -Z rig).
        Vector3 tFwd = BusForward; tFwd.y = 0f;
        if (tFwd.sqrMagnitude < 1e-4f) tFwd = transform.forward;
        tFwd.Normalize();
        float tractorYawDeg = Mathf.Atan2(tFwd.x, tFwd.z) * Mathf.Rad2Deg;

        float L = Mathf.Max(0.5f, trailerLength); // hitch -> rear axle, metres

        // First tick (or after ResetTractrixJoint): seat the axle straight
        // behind the hitch so it doesn't drag in from wherever it was.
        if (!_tractrixInit)
        {
            _rearAxleWorld    = hitch - tFwd * L;
            _rearAxleWorld.y  = tractor.position.y;
            _tractrixInit     = true;
        }

        // ── THE CONSTRAINT ──────────────────────────────────────────────
        // Pull the axle toward the new hitch, keep it exactly L away. Heading
        // is the axle->hitch line = the section's real forward travel dir.
        Vector3 axleToHitch = hitch - _rearAxleWorld; axleToHitch.y = 0f;
        if (axleToHitch.sqrMagnitude < 1e-6f) axleToHitch = tFwd; // degenerate (stacked): reuse tractor fwd
        Vector3 headingDir  = axleToHitch.normalized;
        float   trailerYawDeg = Mathf.Atan2(headingDir.x, headingDir.z) * Mathf.Rad2Deg;

        // ── Mechanical hinge stop (Hübner joint) ────────────────────────
        // Clamp the GEOMETRY, not just the reported number: if the raw
        // heading would fold past the stop, snap it onto the stop and push
        // the axle around with it. The mesh then physically cannot exceed the
        // joint's travel -- same as pinning the real stops at full lock, and
        // it's what keeps reverse (structurally a jack-knife) from folding
        // through itself.
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

        // ── Publish shared state (bellows / gizmos / legacy readers) ─────
        // These are the CORRECT real-travel-heading values (0° hinge on a
        // straight line, growing into the turn) -- the history model fed a
        // mesh-facing-tainted heading in here, which is partly why the
        // bellows also read oddly.
        _hingeAngle     = Mathf.Clamp(Mathf.DeltaAngle(trailerYawDeg, tractorYawDeg), -maxHingeAngle, maxHingeAngle);
        currentHingeYaw = trailerYawDeg;
        _trailerYawDeg  = trailerYawDeg;

        // ── Mesh placement ──────────────────────────────────────────────
        // Reproduce the history model's EXACT orientation convention so every
        // already-tuned rig field (trailerMeshRestOffset, trailerFrontOffset,
        // trailerLateralTrim, pivotRelativeOffset) keeps its meaning -- the
        // ONLY thing that changed is that the heading now comes from correct
        // geometry instead of a stale pose. sectionMeshFwd carries the mesh's
        // own +Z facing; finalRot then applies the same 180 the history path
        // did, so the net mesh orientation is identical to what you had.
        Vector3 sectionMeshFwd = trailerMeshFacesBackward ? -headingDir : headingDir;
        float   meshYawDeg     = Mathf.Atan2(sectionMeshFwd.x, sectionMeshFwd.z) * Mathf.Rad2Deg;

        Vector3 trailerFwdWorld = new Vector3(
            Mathf.Sin(meshYawDeg * Mathf.Deg2Rad), 0f,
            Mathf.Cos(meshYawDeg * Mathf.Deg2Rad));

        Quaternion physRot   = Quaternion.LookRotation(trailerFwdWorld, Vector3.up);
        Quaternion restOff   = Quaternion.Euler(trailerMeshRestOffset);

        // ── Dynamic ground pitch (X) — added ONTO the rest offset ──────────
        // hitch/_rearAxleWorld are the two real contact-ish points of THIS
        // section, so the slope between them is the section's own slope, not
        // the tractor's. Smoothed by rate, not by Lerp-toward-target, so it
        // reads as suspension travel instead of an exponential snap.
        float groundPitchTarget = ComputeGroundPitchTargetDeg(hitch, _rearAxleWorld);
        _groundPitchDegSmoothed = Mathf.MoveTowards(
            _groundPitchDegSmoothed, groundPitchTarget, groundPitchSmoothDegPerSec * dt);
        Quaternion groundPitchRot = Quaternion.Euler(_groundPitchDegSmoothed, 0f, 0f);

        // [ADD] QoL: downhill/bridge-descent EXTREME boost on the back bob -- reuses
        // groundPitchTarget (this tick's real, unsmoothed terrain pitch) as the "how
        // steep/extreme is the road right now" signal instead of a separate slope
        // check, same reasoning as the ground-pitch system right above it.
        // Reads the SMOOTHED pitch (same one actually applied to the mesh above),
        // not the raw per-tick groundPitchTarget -- raw was spiking on a single
        // noisy raycast (a bump, a mesh seam, one missed frame) and instantly
        // multiplying the bob, which read as a sudden "kick" especially at high
        // speed where the hitch/axle sample points move fastest.
        float backBobTerrainFactor = Mathf.Clamp01(Mathf.Abs(_groundPitchDegSmoothed) / Mathf.Max(0.01f, bobDownhillPitchForExtreme));
        Quaternion bobRot = Quaternion.Euler(_currentBackBobPitchDeg * (1f + backBobTerrainFactor * bobDownhillExtremeMultiplier), 0f, 0f);
        // restOff * groundPitchRot: the dynamic X pitch is layered ON TOP of
        // the mesh's baked rest offset, in the mesh's own local space -- it
        // does not replace or fight trailerMeshRestOffset, it rides on it.
        Quaternion restPlusPitch = restOff * groundPitchRot;

        Quaternion finalRot  = trailerMeshFacesBackward
            ? physRot * Quaternion.Euler(0f, 180f, 0f) * restPlusPitch * bobRot
            : physRot * restPlusPitch * bobRot;
        trailerPivot.rotation = finalRot;

        Vector3 basePos = hitch - trailerFwdWorld * trailerFrontOffset;
        Vector3 trimmed = basePos
            + trailerPivot.right   * (trailerLateralTrim + pivotRelativeOffset.x)
            + trailerPivot.up      * pivotRelativeOffset.y
            + trailerPivot.forward * pivotRelativeOffset.z;
        trailerPivot.position = trimmed;

        DeformBellows(_hingeAngle);
    }

    [ContextMenu("Reset Rig Trims (zero the old hack offsets)")]
    private void ResetRigTrims()
    {
        trailerLateralTrim  = 0f;
        pivotRelativeOffset = Vector3.zero;
        Debug.Log($"[{name}] Rig trims reset to zero — the direction-aware hitch fix makes the old -35/2.5 offsets unnecessary.");
    }

    public void DeformBellows(float hingeAngleDeg)
    {
        if (_bellowsMesh == null || _bellowsBaseVerts == null) return;

        float angleRad = Mathf.Clamp(hingeAngleDeg, -maxBendDegrees, maxBendDegrees)
                       * Mathf.Deg2Rad;

        if (Mathf.Abs(angleRad) < 0.0001f)
        {
            _bellowsMesh.vertices = _bellowsBaseVerts;
            _bellowsMesh.RecalculateNormals();
            _bellowsMesh.RecalculateBounds();
            return;
        }

        float radius      = bellowsLength / angleRad;
        Vector3[] verts   = new Vector3[_bellowsBaseVerts.Length];

        for (int i = 0; i < _bellowsBaseVerts.Length; i++)
        {
            Vector3 v = _bellowsBaseVerts[i];

            float lengthCoord  = v.y;
            float lateralCoord = v.x;

            float t             = Mathf.Clamp01(lengthCoord / bellowsLength);
            float sweepAngle    = t * angleRad;
            float effRadius     = radius - lateralCoord;

            float arcAlong   = effRadius * (1f - Mathf.Cos(sweepAngle));
            float arcForward = effRadius * Mathf.Sin(sweepAngle);

            verts[i] = new Vector3(arcAlong, arcForward, v.z);
        }

        _bellowsMesh.vertices = verts;
        _bellowsMesh.RecalculateNormals();
        _bellowsMesh.RecalculateBounds();
    }

    public void AutoCenterTrailerMesh()
    {
        if (trailerPivot == null) return;
        Renderer meshRenderer = trailerPivot.GetComponentInChildren<Renderer>();
        if (meshRenderer == null) return;

        Vector3 worldCenter = meshRenderer.bounds.center;
        Vector3 localCenter = trailerPivot.InverseTransformPoint(worldCenter);
        meshRenderer.transform.localPosition -= new Vector3(localCenter.x, 0f, 0f);

        Debug.Log($"[BusSim] AutoCenter: corrected X offset by {-localCenter.x:0.0000}");
    }

    private bool atStop
    {
        get
        {
            if (nextStopIndex < 0 || nextStopIndex >= busStops.Length || busStops[nextStopIndex] == null)
                return false;
            Vector3 delta = transform.position - busStops[nextStopIndex].position;
            return Mathf.Abs(delta.x) <= stopZoneX
                && Mathf.Abs(delta.y) <= stopZoneY
                && Mathf.Abs(delta.z) <= stopZoneZ;
        }
    }

    private void UpdateBusStopSystem()
    {
        if (busStops == null || busStops.Length == 0) { nextStopIndex = -1; return; }

        float best    = float.MaxValue;
        int   bestIdx = -1;
        for (int i = 0; i < busStops.Length; i++)
        {
            if (busStops[i] == null) continue;
            float dist = Vector3.Distance(transform.position, busStops[i].position);
            if (dist < stopDetectRadius && dist < best) { best = dist; bestIdx = i; }
        }

        nextStopIndex  = bestIdx;
        distToNextStop = best;

        if (nextStopIndex >= 0 && !announcedStops.Contains(nextStopIndex))
        {
            announcedStops.Add(nextStopIndex);

            if (onboardPax > 0 && UnityEngine.Random.value < 0.65f)
            {
                alightRequested = true;
                alightCount     = UnityEngine.Random.Range(1, Mathf.Min(onboardPax, 4) + 1);
                Debug.LogWarning($"[NEXT STOP: {busStops[nextStopIndex].name}] " +
                                 $"{alightCount} passenger(s) want to alight.");
            }
            else
            {
                alightRequested = false;
                alightCount     = 0;
            }
        }

        if (nextStopIndex >= 0 && distToNextStop > stopDetectRadius + 10f)
            announcedStops.Remove(nextStopIndex);
    }

    private bool _hasProcessedStop = false;

    // [FIX] Was private -- PlayerHandoff couldn't call this at all, which is
    // exactly why it ended up with its own separate, incomplete duplicate
    // (raw doorsOpen flip, no frontDoorSet.Open()/.Close() call, so the
    // actual door animation never played -- "1 and Shift+1 don't work").
    // Public now so PlayerHandoff calls THIS real implementation directly
    // instead of maintaining a second copy that can drift out of sync.
    public void HandleDoorToggle()
    {
        doorsOpen = !doorsOpen;
        if (doorsOpen) OpenFrontDoors(); else CloseFrontDoors();

        if (doorsOpen)
        {
            _hasProcessedStop = false;
            if (atStop)
            {
                string sName = busStops[nextStopIndex].name;
                var (off, on) = PlayerHandoff.Instance != null
                    ? PlayerHandoff.Instance.ProcessDoorOpenAtCurrentStop()
                    : (0, 0);
                Debug.Log($"<color=green>[Doors]</color> Opened at stop: <b>{sName}</b> — {off} off, {on} on " +
                          $"(onboard now {(PlayerHandoff.Instance != null ? PlayerHandoff.Instance.OnboardPax : 0)}).");
            }
            else
            {
                Debug.Log("<color=yellow>[Doors]</color> Opened (not at a stop).");
            }
        }
        else
        {
            Debug.Log("<color=yellow>[Doors]</color> Closed.");
        }
    }

    // [FIX] Same as HandleDoorToggle above -- was private, PlayerHandoff
    // had its own incomplete duplicate instead.
    public void HandleRearDoorToggle()
    {
        rearDoorsOpen = !rearDoorsOpen;
        if (rearDoorsOpen) OpenRearDoors(); else CloseRearDoors();

        Debug.Log(rearDoorsOpen
            ? "<color=green>[Doors]</color> Rear opened."
            : "<color=yellow>[Doors]</color> Rear closed.");
    }

    private void ProcessStopDoorOpen()
    {
        if (_hasProcessedStop || !atStop) return;
        _hasProcessedStop = true;

        string sName = (nextStopIndex >= 0 && nextStopIndex < busStops.Length)
                     ? busStops[nextStopIndex].name : "Unknown";

        if (alightRequested && alightCount > 0)
        {
            int off = Mathf.Min(alightCount, onboardPax);
            onboardPax -= off;
            Debug.LogWarning($"<color=yellow>[ALIGHTED: {sName}]</color> {off} off. Remaining: {onboardPax}");
            alightRequested = false;
            alightCount     = 0;
        }
        else
        {
            Debug.Log($"<color=cyan>[{sName}]</color> No passengers requested to alight.");
        }

        int newPax = UnityEngine.Random.Range(0, 6);
        onboardPax += newPax;
        Debug.Log($"<color=green>[BOARDED: {sName}]</color> {newPax} joined. Total onboard: {onboardPax}");
    }

    private void OnDrawGizmos()
    {
        if (!stopPlayerPaxSystem && busStops != null)
        {
            for (int i = 0; i < busStops.Length; i++)
            {
                if (busStops[i] == null) continue;
                bool isNext = (i == nextStopIndex);
                Gizmos.color = isNext ? (atStop ? Color.green : Color.red) : new Color(1f, 1f, 0f, 0.25f);
                Gizmos.DrawWireCube(busStops[i].position,
                    new Vector3(stopZoneX * 2f, stopZoneY * 2f, stopZoneZ * 2f));
                if (isNext && alightRequested)
                {
                    Gizmos.color = Color.magenta;
                    Gizmos.DrawSphere(busStops[i].position + Vector3.up * 3f, 1.5f);
                }
            }
            Gizmos.color = new Color(0f, 1f, 1f, 0.15f);
            Gizmos.DrawWireSphere(transform.position, stopDetectRadius);
        }

        {
            float steerRad = currentSteerAngle * Mathf.Deg2Rad;
            if (Mathf.Abs(steerRad) > 0.01f)
            {
                float   radius = wheelbase / Mathf.Tan(steerRad);
                Vector3 rearAxleW = transform.TransformPoint(GetRearAxleLocal());
                Vector3 busRight  = Vector3.Cross(Vector3.up, BusForward);
                Vector3 icr       = rearAxleW + busRight * radius;
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(icr, Mathf.Abs(radius));
                Gizmos.DrawSphere(icr, 0.5f);
            }
        }

        if (articulationMode != ArticulationMode.None && trailerPivot != null)
        {
            Transform tractor = TractorTransform;

            Vector3 hitch = tractor.TransformPoint(GetHitchLocalOffset(tractor));
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(hitch, 0.3f);

            Vector3 trailerFwdW = new Vector3(
                Mathf.Sin(_trailerYawDeg * Mathf.Deg2Rad), 0f,
                Mathf.Cos(_trailerYawDeg * Mathf.Deg2Rad));
            Vector3 rear = hitch - trailerFwdW * trailerLength;
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.7f);
            Gizmos.DrawWireSphere(rear, 0.25f);

            Gizmos.color = new Color(1f, 1f, 0f, 0.4f);
            Gizmos.DrawLine(hitch, rear);

            if (bellowsFrontMarker != null)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(bellowsFrontMarker.position, 0.15f);
            }
            if (bellowsRearMarker != null)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawWireSphere(bellowsRearMarker.position, 0.15f);
            }
        }
    }

    // ════════ SIMULATION ════════
    public BusAudioEngine audioEngine;

    public void UpdateSimulation(float ts)
    {
        gearHoldTimer += Time.deltaTime;
        if (!running) return;
        float dt = lastTs.HasValue ? Mathf.Min(ts - lastTs.Value, 0.05f) : 0.016f;
        lastTs = ts;

        if (audioEngine != null)
        {
            cranking         = audioEngine.engineState != BusAudioEngine.EngineRunState.Running;
            engineMuted      = cranking;
            electronicsMuted = !audioEngine.batteryOn;
        }

        if (cranking) { accel = 0f; bkPd = 0f; }
        else
        {
            const float AR = 3.5f, AD = 5.0f, BR = 4.0f, BD = 6.0f;
            if (gasPd > 0.001f)
                accel = Mathf.Clamp01(gasPd);
            else
                accel = accelKey ? Mathf.Min(1f, accel + AR * dt) : Mathf.Max(0f, accel - AD * dt);
            bkPd  = brakeKey ? Mathf.Min(1f, bkPd  + BR * dt) : Mathf.Max(0f, bkPd  - BD * dt);
        }

        audioEngine.accel         = accel;
        audioEngine.bkPd          = bkPd;
        audioEngine.spd           = spd;
        audioEngine.rpm           = rpm;
        audioEngine.gear          = gear;
        audioEngine.gearHoldTimer = gearHoldTimer;
        audioEngine.doorsOpen        = doorsOpen;
        audioEngine.doorsFullyClosed = DoorsFullyClosed;
        audioEngine.parkingBrake     = parkingBrake;
        // [ADD] Articulation hinge angle -- drives the AVE130/AVN132 joint
        // creak/groan layer (see DoArticulationCreakDSP).
        audioEngine.hingeAngleDeg = HingeAngleDegrees;
        // [FIX] Gives the audio engine the real selector position instead
        // of it having to infer "Neutral" from gear == 0, which the EVT/
        // hybrid transmissions also reported just from sitting stopped in
        // Drive. See BusAudioEngine.isNeutral for the full story.
        audioEngine.isNeutral      = currentDirection == GearDirection.Neutral;

        if (!cranking) audioEngine.Tick(dt);
        else audioEngine.running = false;

        if (cranking) audioEngine.running = running;

        rpm         = audioEngine.rpm;
        if (!physicalDrive) spd = audioEngine.spd;
        gear        = audioEngine.gear;
        regenActive = audioEngine.RegenActive;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (audioEngine == null) return;

        audioEngine.running = running;
        audioEngine.rpm   = rpm;
        audioEngine.spd   = spd;
        audioEngine.gear  = gear;
        audioEngine.accel = accel;
        audioEngine.bkPd  = bkPd;
        audioEngine.isNeutral = currentDirection == GearDirection.Neutral;
    audioEngine.ProcessAudio(data, channels, 0f);
    }
}