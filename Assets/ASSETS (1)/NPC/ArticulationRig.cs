using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ArticulationRig
//
//  Third-generation articulation for Headway's artics (XD60/XDE60/XN60/XE60).
//  A full 3-axis kinematic hitch simulation: YAW (steering swing), PITCH
//  (crest/dip follow), and ROLL (body-lean twist), with hydraulic-style
//  damping, self-centering, jackknife protection, reverse handling, and a
//  single authoritative hinge-state output that drives the bellows and can
//  drive anything else (audio creak, interior camera sway, whatever).
//
//  DESIGN: "SEPARATE BUT CONNECTED"
//  ────────────────────────────────
//  This is NOT a MonoBehaviour. It's a plain [Serializable] class that lives
//  as ONE field inside NPCBusController. That gives you:
//    • Its own collapsible Inspector foldout on the bus, zero extra
//      components, zero extra GameObjects, prefab-friendly.
//    • All state self-contained here — NPCBusController only grows by
//      ~6 lines (enum case + field + one Tick call). See the doc.
//    • Reusable: the player bus, a tow truck, anything can own one.
//
//  SETUP = ONE THING 
//  ─────────────────
//  Place `trailerPivot` where the physical hinge is (the turntable at the
//  rear of the front section). That's it. On first tick the rig:
//    • Captures the hinge's local offset from the tractor automatically
//      (no tractorRearOffset / trailerFrontOffset numbers to eyeball).
//    • Auto-measures trailer length from the trailer's renderer bounds
//      if trailerLength is left at 0.
//    • Snaps the trailer straight behind the tractor so there's no
//      first-frame lurch.
//  Every other field below is a feel knob with a sane default — you never
//  HAVE to touch any of them.
//
//  HOW THE MATH WORKS (per FixedUpdate tick)
//  ─────────────────────────────────────────
//  1. YAW — pursuit kinematics. The hinge point H is rigidly welded to the
//     tractor. The trailer's virtual rear axle A trails behind; each tick
//     the trailer's forward becomes normalize(H − A), then A is re-seated
//     at H − forward·L. This is the real geometry of a towed vehicle: the
//     rear axle always chases the hinge along the trailer's own axis, which
//     is why the trailer cuts the corner exactly like a real artic does.
//  2. DAMPER — the raw pursuit yaw is then filtered through a
//     spring-damper toward the tractor's heading (hydraulic articulation
//     damper sim). Stiffness scales with speed: at 60 km/h the joint
//     self-centers firmly (stability), at walking pace it's loose
//     (manoeuvrability) — same behaviour as a real artic's damping ECU.
//  3. CLAMP — |yaw| is hard-limited to maxYawDegrees. Past
//     jackknifeWarnRatio of that limit a restoring push-out torque ramps
//     in, so the joint resists the last few degrees instead of slamming
//     into a wall. IsJackknifing exposes this for AI/audio.
//  4. PITCH — two ground rays (one under the hinge, one under the trailer
//     axle) give the trailer its own grade angle, smoothed. The trailer
//     genuinely bends over crests and dips instead of impaling the road.
//  5. ROLL — two sources, summed then smoothed:
//       a) chassis-follow: the trailer lags the tractor's roll (twist
//          transfer through the joint, torsionally soft like the real
//          turntable), and
//       b) dynamic lean: yawRate × speed centrifugal lean, so the rear
//          section visibly heels outward through a fast corner.
//  6. REVERSE — pursuit geometry is unstable in reverse (real artics
//     jackknife when shunted). Below reverse threshold the rig blends the
//     yaw toward frozen/straight instead, which reads correctly for
//     depot shunts without simulating a banksman.
//  7. OUTPUT — HingeYawDeg / HingePitchDeg / HingeRollDeg and the composed
//     trailer pose. NPCBusController feeds HingeYawDeg straight into its
//     existing DeformBellows() — the bellows finally deforms live.
//
//  Everything is deterministic kinematics driven off the tractor transform:
//  no Rigidbody on the trailer, no ConfigurableJoint, no physics blow-ups,
//  and it costs two raycasts + a handful of quaternion ops per bus per tick
//  (raycasts skippable via simulatePitch=false for far-LOD buses).
// ═══════════════════════════════════════════════════════════════════════════════

[System.Serializable]
public class ArticulationRig
{
    // ── Feel knobs (all optional — defaults are tuned for a 60-footer) ────────
    [Header("Geometry (auto-measured if 0)")]
    [Tooltip("Hinge → trailer rear axle distance in metres. 0 = auto-measure from the trailer renderer bounds on init.")]
    public float trailerLength = 0f;
    [Tooltip("Tick if the trailer mesh was modelled facing backward (adds a 180° yaw to the visual only).")]
    public bool  trailerMeshFacesBackward = false;

    [Header("Yaw — Steering Swing")]
    [Range(20f, 75f)]
    [Tooltip("Hard mechanical articulation limit. New Flyer artics are ~50-55°.")]
    public float maxYawDegrees = 54f;
    [Range(0f, 1f)]
    [Tooltip("Fraction of maxYaw where jackknife push-out begins resisting. 0.85 = last 15% of travel fights back.")]
    public float jackknifeWarnRatio = 0.85f;
    [Range(0f, 10f)]
    [Tooltip("Self-centering strength at speed (hydraulic damper sim). 0 = free hinge, higher = joint pulls straight sooner.")]
    public float damperStiffness = 2.5f;
    [Range(1f, 30f)]
    [Tooltip("How fast yaw changes are smoothed. High = crisp mechanical joint, low = soft/lazy swing.")]
    public float yawResponse = 14f;
    [Tooltip("Speed (m/s) at which damper reaches full stiffness.")]
    public float damperFullSpeed = 12f;

    [Header("Pitch — Crest / Dip Follow")]
    [Tooltip("Raycast the ground under hinge + axle so the trailer bends over crests. Turn off for far-LOD buses to skip 2 raycasts.")]
    public bool  simulatePitch = true;
    [Range(1f, 20f)] public float pitchResponse = 8f;
    [Range(0f, 20f)]
    [Tooltip("Hard pitch limit at the joint (real turntables allow ~±10°).")]
    public float maxPitchDegrees = 10f;
    [Tooltip("Layers considered 'ground' for the pitch rays.")]
    public LayerMask groundMask = ~0;

    [Header("Roll — Twist / Body Lean")]
    [Range(0f, 1f)]
    [Tooltip("How much of the tractor's roll transfers through the joint. 1 = rigid torsionally, 0 = fully decoupled twist.")]
    public float rollCoupling = 0.55f;
    [Range(0f, 5f)]
    [Tooltip("Centrifugal lean: degrees of outward roll per (rad/s of yaw rate × m/s of speed). ~1.2 looks right on an XD60.")]
    public float leanGain = 1.2f;
    [Range(0f, 12f)] public float maxLeanDegrees = 6f;
    [Range(1f, 20f)] public float rollResponse = 6f;

    [Header("Reverse Handling")]
    [Tooltip("Below this signed forward speed (m/s), pursuit geometry is blended out and the joint straightens (depot shunts).")]
    public float reverseThreshold = -0.15f;
    [Range(0.5f, 10f)] public float reverseStraightenSpeed = 2.5f;

    // ── Live outputs (read-only from outside) ─────────────────────────────────
    /// <summary>Signed hinge yaw, degrees. + = trailer swung to tractor's right. Feed this to DeformBellows.</summary>
    public float HingeYawDeg   { get; private set; }
    /// <summary>Signed hinge pitch, degrees. + = trailer nose-up relative to tractor.</summary>
    public float HingePitchDeg { get; private set; }
    /// <summary>Signed hinge roll (twist), degrees.</summary>
    public float HingeRollDeg  { get; private set; }
    /// <summary>True while yaw is inside the jackknife push-out band.</summary>
    public bool  IsJackknifing { get; private set; }
    /// <summary>0..1 how much of the mechanical yaw limit is used. Handy for audio (creak volume) or AI caution.</summary>
    public float ArticulationUsage => Mathf.Abs(HingeYawDeg) / Mathf.Max(1f, maxYawDegrees);

    // ── Internal state ────────────────────────────────────────────────────────
    private Transform _tractor;
    private Transform _trailer;
    private Vector3   _hingeLocal;       // hinge position in tractor-local space, auto-captured
    private Vector3   _axleWorld;        // virtual trailer rear axle, world space
    private float     _yawFiltered;      // smoothed signed yaw
    private float     _pitchFiltered;
    private float     _rollFiltered;
    private float     _prevTractorYaw;
    private bool      _initialized;

    // ═════════════════════════════════════════════════════════════════════════
    //  INIT — called lazily on first Tick, or explicitly if you prefer
    // ═════════════════════════════════════════════════════════════════════════
    public void Init(Transform tractor, Transform trailerPivot)
    {
        _tractor = tractor;
        _trailer = trailerPivot;
        if (_tractor == null || _trailer == null) return;

        // THE one-thing setup: wherever you placed trailerPivot IS the hinge.
        // Capture its offset in tractor space so it stays welded there forever.
        _hingeLocal = _tractor.InverseTransformPoint(_trailer.position);

        // Auto-measure trailer length from its own renderers if not given.
        if (trailerLength <= 0.01f)
        {
            var rend = _trailer.GetComponentInChildren<Renderer>();
            trailerLength = rend != null
                ? Mathf.Max(3f, rend.bounds.size.z)   // never shorter than 3m
                : 9.0f;                                // sensible artic default
        }

        // Seat the trailer dead straight behind the tractor — no spawn lurch.
        Vector3 hinge = HingeWorld();
        _axleWorld     = hinge - FlatForward(_tractor) * trailerLength;
        _yawFiltered   = 0f;
        _pitchFiltered = 0f;
        _rollFiltered  = 0f;
        _prevTractorYaw = _tractor.eulerAngles.y;
        _initialized = true;

        ApplyPose(hinge, FlatForward(_tractor));
    }

    /// <summary>Re-seat straight (teleports, depot spawn, route reset). Cheap, call freely.</summary>
    public void SnapStraight()
    {
        if (!_initialized) return;
        Vector3 hinge = HingeWorld();
        _axleWorld = hinge - FlatForward(_tractor) * trailerLength;
        _yawFiltered = _pitchFiltered = _rollFiltered = 0f;
        ApplyPose(hinge, FlatForward(_tractor));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  TICK — call once per FixedUpdate-equivalent with your dt
    // ═════════════════════════════════════════════════════════════════════════
    public void Tick(Transform tractor, Transform trailerPivot, float dt, float signedForwardSpeed)
    {
        if (trailerPivot == null || tractor == null) return;
        if (!_initialized || _tractor != tractor || _trailer != trailerPivot)
            Init(tractor, trailerPivot);
        if (dt <= 0f) return;

        Vector3 hinge      = HingeWorld();
        Vector3 tractorFwd = FlatForward(_tractor);

        // ── 1. Raw pursuit yaw ────────────────────────────────────────────────
        Vector3 pull = hinge - _axleWorld; pull.y = 0f;
        Vector3 pursuitFwd = pull.sqrMagnitude > 0.0001f ? pull.normalized : tractorFwd;
        float rawYaw = Vector3.SignedAngle(pursuitFwd, tractorFwd, Vector3.up);
        // Sign convention: + = trailer swung to the tractor's RIGHT.

        // ── 2. Hydraulic damper — speed-scaled self-centering ─────────────────
        float speedAbs = Mathf.Abs(signedForwardSpeed);
        float damper   = damperStiffness * Mathf.Clamp01(speedAbs / Mathf.Max(0.1f, damperFullSpeed));
        rawYaw = Mathf.MoveTowards(rawYaw, 0f, damper * dt * Mathf.Abs(rawYaw) * 0.25f);

        // ── 3. Jackknife band + hard clamp ────────────────────────────────────
        float warnAt = maxYawDegrees * jackknifeWarnRatio;
        IsJackknifing = Mathf.Abs(rawYaw) > warnAt;
        if (IsJackknifing)
        {
            // Push-out torque ramps 0→strong across the warning band.
            float over = (Mathf.Abs(rawYaw) - warnAt) / Mathf.Max(0.01f, maxYawDegrees - warnAt);
            rawYaw = Mathf.MoveTowards(rawYaw, Mathf.Sign(rawYaw) * warnAt, over * over * 30f * dt);
        }
        rawYaw = Mathf.Clamp(rawYaw, -maxYawDegrees, maxYawDegrees);

        // ── 4. Reverse handling — blend toward straight when shunting ─────────
        if (signedForwardSpeed < reverseThreshold)
            rawYaw = Mathf.MoveTowards(rawYaw, 0f, reverseStraightenSpeed * 10f * dt);

        // ── 5. Smooth yaw (mechanical response of the joint) ──────────────────
        _yawFiltered = Mathf.Lerp(_yawFiltered, rawYaw, 1f - Mathf.Exp(-yawResponse * dt));
        Vector3 trailerFwd = Quaternion.AngleAxis(-_yawFiltered, Vector3.up) * tractorFwd;

        // Re-seat the virtual axle on the (clamped, smoothed) axis — this is
        // what keeps the pursuit stable even after clamping fought it.
        _axleWorld = hinge - trailerFwd * trailerLength;

        // ── 6. Pitch — ground-sample under hinge and axle ─────────────────────
        float rawPitch = 0f;
        if (simulatePitch)
        {
            if (SampleGroundY(hinge, out float yH) && SampleGroundY(_axleWorld, out float yA))
                rawPitch = Mathf.Atan2(yH - yA, trailerLength) * Mathf.Rad2Deg;
            rawPitch = Mathf.Clamp(rawPitch, -maxPitchDegrees, maxPitchDegrees);
        }
        _pitchFiltered = Mathf.Lerp(_pitchFiltered, rawPitch, 1f - Mathf.Exp(-pitchResponse * dt));

        // ── 7. Roll — coupled chassis twist + centrifugal lean ────────────────
        float tractorRoll = NormalizeAngle(_tractor.eulerAngles.z);
        float yawNow  = _tractor.eulerAngles.y;
        float yawRate = Mathf.DeltaAngle(_prevTractorYaw, yawNow) / dt * Mathf.Deg2Rad; // rad/s
        _prevTractorYaw = yawNow;
        float lean = Mathf.Clamp(-yawRate * signedForwardSpeed * leanGain, -maxLeanDegrees, maxLeanDegrees);
        float rawRoll = tractorRoll * rollCoupling + lean;
        _rollFiltered = Mathf.Lerp(_rollFiltered, rawRoll, 1f - Mathf.Exp(-rollResponse * dt));

        // ── 8. Publish + compose the pose ─────────────────────────────────────
        HingeYawDeg   = _yawFiltered;
        HingePitchDeg = _pitchFiltered;
        HingeRollDeg  = _rollFiltered - tractorRoll; // twist RELATIVE to tractor
        ApplyPose(hinge, trailerFwd);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  INTERNALS
    // ═════════════════════════════════════════════════════════════════════════
    private void ApplyPose(Vector3 hinge, Vector3 trailerFwd)
    {
        // Trailer pivot sits exactly ON the hinge (it IS the hinge — that's
        // the whole setup contract), oriented along the composed axes.
        Quaternion yawRot   = Quaternion.LookRotation(trailerFwd, Vector3.up);
        Quaternion pitchRot = Quaternion.AngleAxis(_pitchFiltered, yawRot * Vector3.right);
        Quaternion rollRot  = Quaternion.AngleAxis(_rollFiltered,  yawRot * Vector3.forward);
        Quaternion pose     = rollRot * pitchRot * yawRot;

        if (trailerMeshFacesBackward)
            pose *= Quaternion.Euler(0f, 180f, 0f);

        _trailer.SetPositionAndRotation(hinge, pose);
    }

    private Vector3 HingeWorld() => _tractor.TransformPoint(_hingeLocal);

    private static Vector3 FlatForward(Transform t)
    {
        Vector3 f = t.forward; f.y = 0f;
        return f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
    }

    private bool SampleGroundY(Vector3 at, out float y)
    {
        if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out RaycastHit hit, 12f, groundMask,
                            QueryTriggerInteraction.Ignore))
        { y = hit.point.y; return true; }
        y = at.y; return false;
    }

    private static float NormalizeAngle(float a)
    {
        a %= 360f;
        if (a > 180f) a -= 360f;
        return a;
    }
}
