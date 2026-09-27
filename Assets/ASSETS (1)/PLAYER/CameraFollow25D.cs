using UnityEngine;

public class CameraFollow25D : MonoBehaviour
{
    [Header("Tracking")]
    public Transform targetBus;
    public float     smoothing    = 8f;
    public Vector3   followOffset = new Vector3(0f, 15f, -10f);

    [Header("Orbit (F mode)")]
    public float orbitDistance    = 12f;
    public float orbitHeight      = 6f;
    public float orbitSensitivity = 3f;

    // Public so BusSelectMenu can reset these on bus swap without reflection
    [HideInInspector] public float orbitYaw   = 0f;
    [HideInInspector] public float orbitPitch = 20f;

    [Header("First Person")]
    public Vector3 firstPersonOffset = new Vector3(0f, 2f, 0.5f);
    public bool    apply180Fix       = true;
    public float   fpLookSensitivity = 3f;

    [Header("Camera Lock")]
    [Tooltip("When true, mouse drag in Orbit mode (F) is ignored -- the camera stops responding to manual look input entirely, but keeps rotating to track the bus's own yaw same as always (orbitYaw still follows busYawDelta below, this only blocks the ADDED user-drag term). Useful for a fixed chase-cam feel without disabling orbit mode itself.")]
    public bool lockOrbitCamera = false;

    [Tooltip("When true, mouse look in First Person mode (X) is ignored -- _fpYaw/_fpPitch stop updating from Input.GetAxis, freezing the look offset wherever it currently sits. The camera still rotates with the bus normally (target.rotation/fpv.rotation is unaffected by this lock, only the manual look offset on top of it is), so panning around still isn't possible but the view still turns naturally through corners same as riding along.")]
    public bool lockFirstPersonCamera = false;

    /// <summary>Convenience for binding to a UI toggle/keybind without the
    /// caller needing to know which of the two fields above applies --
    /// locks (or unlocks) both modes' manual input at once. The two public
    /// bools above stay available individually if you want them
    /// independent (e.g. a settings menu with separate checkboxes).</summary>
    public void SetCameraLocked(bool locked)
    {
        lockOrbitCamera       = locked;
        lockFirstPersonCamera = locked;
    }
    public void ToggleCameraLocked() => SetCameraLocked(!(lockOrbitCamera && lockFirstPersonCamera));
// Required for SmoothDamp to track the camera's momentum between frames
private Vector3 _camVelocity = Vector3.zero;
    // ═════════════════════════════════════════════════════════════════════
    //  [ADD] MOBILE SUPPORT
    //
    //  Two separate gaps, both real:
    //   1. Mode switching only ever listened for KeyCode.F/X -- there was no
    //      way to trigger Orbit/FirstPerson at all on a device with no
    //      keyboard. Public methods below let MobileTouchHUD's on-screen
    //      buttons drive the exact same mode switches the keys already do
    //      (including the same _prevBusYaw/_fpYaw reset logic each mode
    //      switch needs -- these aren't just "set the enum", they mirror
    //      the real KeyDown blocks in Update() exactly).
    //   2. Input.GetAxis("Mouse X")/("Mouse Y") are desktop mouse-delta
    //      values -- under Unity's touch-to-mouse simulation they're either
    //      near-zero or erratic, not a usable drag delta. FeedLookDelta lets
    //      a touch controller push a real Vector2 finger-delta directly
    //      instead, bypassing GetAxis entirely on mobile.
    // ═════════════════════════════════════════════════════════════════════
    public bool IsOrbitMode       => mode == Mode.Orbit;
    public bool IsFirstPersonMode => mode == Mode.FirstPerson;
    public bool IsFollowMode      => mode == Mode.Follow;
    public void SetOrbitMode()
    {
        mode = Mode.Orbit;
        if (FollowTarget != null)
        {
            _prevBusYaw = FollowTarget.eulerAngles.y;
            orbitYaw    = _prevBusYaw;
        }
    }

    public void SetFollowMode() => mode = Mode.Follow;

    public void SetFirstPersonMode()
    {
        mode     = Mode.FirstPerson;
        _fpYaw   = 0f;
        _fpPitch = 0f;
    }

    /// <summary>Same toggle behavior as the F key (Orbit <-> Follow).</summary>
    public void ToggleOrbitMode()
    {
        if (mode == Mode.Orbit) SetFollowMode();
        else SetOrbitMode();
    }

    /// <summary>Feeds a real per-frame drag delta (e.g. a touch's
    /// deltaPosition, or any pre-scaled Vector2) into whichever mode is
    /// currently active, in place of Input.GetAxis("Mouse X"/"Mouse Y").
    /// Respects the same lockOrbitCamera/lockFirstPersonCamera gates as the
    /// mouse path -- a locked mode ignores this too, not just the mouse.
    /// delta.x/y should already be in "degrees per call" units roughly
    /// matching what EffOrbitSensitivity/EffFpSensitivity expect -- scale
    /// on the caller's side (e.g. touch.deltaPosition * a small constant)
    /// since raw pixel deltas are usually far too large otherwise.</summary>
    public void FeedLookDelta(Vector2 delta)
    {
        if (mode == Mode.Orbit && !lockOrbitCamera)
        {
            orbitYaw   += delta.x * EffOrbitSensitivity;
            orbitPitch -= delta.y * EffOrbitSensitivity;
            orbitPitch  = Mathf.Clamp(orbitPitch, 5f, 80f);
        }
        else if (mode == Mode.FirstPerson && !lockFirstPersonCamera)
        {
            _fpYaw   += delta.x * EffFpSensitivity;
            _fpPitch -= delta.y * EffFpSensitivity;
            _fpPitch  = Mathf.Clamp(_fpPitch, -89f, 89f);
            _fpYaw   %= 360f;
        }
    }

    private float _fpYaw   = 0f;
    private float _fpPitch = 0f;

    private enum Mode { Follow, Orbit, FirstPerson }
    private Mode mode = Mode.Follow;

    private float _prevBusYaw = 0f;

    // ── Front-section resolution (articulated buses) + per-bus overrides ────
    // Cached per targetBus so the hierarchy walk / GetComponent below only
    // happens once per possession swap, not every LateUpdate call.
    private Transform         _cachedTargetBus;
    private Transform         _cachedFollowTarget;
    private BusCameraProfile  _cachedProfile;

    /// <summary>Resolves the actual transform to track. Priority order:
    /// 1) an explicit BusCameraProfile.frontSection on targetBus, if present
    ///    and assigned -- the per-bus adjustable path, no name-matching.
    /// 2) a child literally named "Front"/"front", direct or nested.
    /// 3) targetBus itself (old default -- correct for rigid buses).</summary>
    private Transform FollowTarget
    {
        get
        {
            EnsureResolvedForCurrentTarget();
            return _cachedFollowTarget;
        }
    }

    private void EnsureResolvedForCurrentTarget()
    {
        if (targetBus == null) { _cachedTargetBus = null; _cachedFollowTarget = null; _cachedProfile = null; return; }
        if (_cachedTargetBus == targetBus) return;

        _cachedTargetBus = targetBus;
        _cachedProfile   = targetBus.GetComponent<BusCameraProfile>();

        Transform anchorTarget = _cachedProfile != null ? _cachedProfile.frontSection : null;
        _cachedFollowTarget = anchorTarget != null ? anchorTarget : (FindFrontSection(targetBus) ?? targetBus);
    }

    // ── Effective per-bus values -- pull from BusCameraProfile when its
    // matching override flag is on, otherwise fall back to this component's
    // own inspector defaults. Each mode's values are independent so a bus
    // can override just orbit distance and leave follow/FPV untouched.
    private Vector3 EffFollowOffset    => (_cachedProfile != null && _cachedProfile.overrideFollow)      ? _cachedProfile.followOffset      : followOffset;
    private float   EffFollowSmoothing => (_cachedProfile != null && _cachedProfile.overrideFollow)      ? _cachedProfile.followSmoothing   : smoothing;
    private float   EffOrbitDistance   => (_cachedProfile != null && _cachedProfile.overrideOrbit)       ? _cachedProfile.orbitDistance     : orbitDistance;
    private float   EffOrbitHeight     => (_cachedProfile != null && _cachedProfile.overrideOrbit)       ? _cachedProfile.orbitHeight       : orbitHeight;
    private float   EffOrbitSensitivity=> (_cachedProfile != null && _cachedProfile.overrideOrbit)       ? _cachedProfile.orbitSensitivity  : orbitSensitivity;
    private float   EffOrbitSmoothing  => (_cachedProfile != null && _cachedProfile.overrideOrbit)       ? _cachedProfile.orbitSmoothing    : smoothing;
    private Vector3 EffFpOffset        => (_cachedProfile != null && _cachedProfile.overrideFirstPerson) ? _cachedProfile.firstPersonOffset : firstPersonOffset;
    private bool    EffApply180Fix     => (_cachedProfile != null && _cachedProfile.overrideFirstPerson) ? _cachedProfile.apply180Fix        : apply180Fix;
    private float   EffFpSensitivity   => (_cachedProfile != null && _cachedProfile.overrideFirstPerson) ? _cachedProfile.fpLookSensitivity  : fpLookSensitivity;

    private static Transform FindFrontSection(Transform root)
    {
        // Fast path: exact direct-child names, both casings, no allocation.
        Transform direct = root.Find("Front") ?? root.Find("front");
        if (direct != null) return direct;

        // Fallback: recursive, case-insensitive search through the whole
        // rig -- covers artics where the front section is nested under an
        // intermediate "Sections"/"Rig" parent rather than a direct child.
        var all = root.GetComponentsInChildren<Transform>(true);
        foreach (var t in all)
        {
            if (t == root) continue;
            if (string.Equals(t.name, "Front", System.StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return null;
    }

    void Start()
    {
        if (targetBus == null)
        {
            GameObject playerObj = GameObject.FindWithTag("Player");
            if (playerObj != null) targetBus = playerObj.transform;
        }

        EnsureResolvedForCurrentTarget();

        Vector3 angles = transform.eulerAngles;
        orbitYaw   = angles.y;
        orbitPitch = 20f;

        if (FollowTarget != null)
            _prevBusYaw = FollowTarget.eulerAngles.y;
    }

    void Update()
    {
        EnsureResolvedForCurrentTarget();

        if (Input.GetKeyDown(KeyBindings.Current.cameraOrbitFollow))
        {
            mode = (mode == Mode.Orbit) ? Mode.Follow : Mode.Orbit;

            if (mode == Mode.Orbit && FollowTarget != null)
            {
                _prevBusYaw = FollowTarget.eulerAngles.y;
                orbitYaw    = _prevBusYaw;
            }
        }

        if (Input.GetKeyDown(KeyBindings.Current.cameraFirstPerson))
        {
            mode     = Mode.FirstPerson;
            _fpYaw   = 0f;
            _fpPitch = 0f;
        }

        if (mode == Mode.FirstPerson && !lockFirstPersonCamera && Input.touchCount == 0 && Input.GetMouseButton(0))
        {
            _fpYaw   += Input.GetAxis("Mouse X") * EffFpSensitivity;
            _fpPitch -= Input.GetAxis("Mouse Y") * EffFpSensitivity;
            _fpPitch  = Mathf.Clamp(_fpPitch, -89f, 89f);
            _fpYaw   %= 360f;
        }

    }
    void LateUpdate()
    {
        Transform target = FollowTarget;
        if (target == null) return;

        // 1. Get the base position
        Vector3 predictedTargetPos = target.position;

        // 2. Manually extrapolate if a Rigidbody exists to smooth out 
        // the space between FixedUpdate physics ticks.
        Rigidbody rb = target.GetComponentInParent<Rigidbody>();
        if (rb != null && rb.interpolation == RigidbodyInterpolation.None)
        {
            predictedTargetPos += rb.linearVelocity * (Time.time - Time.fixedTime);
        }

        if (mode == Mode.Follow)
        {
            Vector3 rotatedOffset = target.rotation * EffFollowOffset;
            Vector3 targetPos     = predictedTargetPos + rotatedOffset; // Use predicted target
            
            float smoothTime = 1f / Mathf.Max(EffFollowSmoothing, 0.1f);
            transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref _camVelocity, smoothTime);
            transform.LookAt(predictedTargetPos);
        }
        else if (mode == Mode.Orbit)
        {
            // ── Bus-rotation tracking ──────────────────────────────────────
            float busYaw      = target.eulerAngles.y;
            float busYawDelta = Mathf.DeltaAngle(_prevBusYaw, busYaw);
            orbitYaw   += busYawDelta;
            _prevBusYaw = busYaw;

            if (!lockOrbitCamera && Input.touchCount == 0 && Input.GetMouseButton(0))
            {
                orbitYaw   += Input.GetAxis("Mouse X") * EffOrbitSensitivity;
                orbitPitch -= Input.GetAxis("Mouse Y") * EffOrbitSensitivity;
                orbitPitch  = Mathf.Clamp(orbitPitch, 5f, 80f);
            }

            Quaternion rotation  = Quaternion.Euler(orbitPitch, orbitYaw, 0);
            Vector3    offset    = rotation * new Vector3(0, 0, -EffOrbitDistance);
            Vector3    targetPos = predictedTargetPos + Vector3.up * EffOrbitHeight + offset; // Use predicted target
            
            float smoothTime = 1f / Mathf.Max(EffOrbitSmoothing, 0.1f);
            transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref _camVelocity, smoothTime);
            transform.LookAt(predictedTargetPos + Vector3.up * EffOrbitHeight);
        }
        else if (mode == Mode.FirstPerson)
        {
            Quaternion lookOffset = Quaternion.Euler(_fpPitch, _fpYaw, 0f);

            Transform fpv = target.Find("FPV");
            if (fpv != null)
            {
                // First person usually tracks the exact transform to prevent clipping, 
                // but if this also jitters, swap fpv.position for predictedTargetPos + localized offset
                transform.position = fpv.position; 
                transform.rotation = fpv.rotation * lookOffset;
            }
            else
            {
                transform.position = predictedTargetPos + target.TransformVector(EffFpOffset); // Use predicted target
                Quaternion rot = target.rotation;
                if (EffApply180Fix) rot *= Quaternion.Euler(0f, 180f, 0f);
                transform.rotation = rot * lookOffset;
            }
        }
    }
}