using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS WHEEL STEER  —  turns the FRONT wheels (visual only) to follow the
//  driver's steering input. Companion to BusWheelBob: that component owns
//  each wheel's local POSITION (Y bob), this one owns local ROTATION (Y
//  yaw) — same wheel transforms, two independent components, so there's no
//  shared state and no path for one to clobber the other's axis.
//
//  This does NOT compute steering itself. It only reads a number someone
//  else already calculated — BusSimulationController.SteerAngleDegrees for
//  the player bus (per PlayerHandoff, which drives that class directly), or
//  NPCBusController.PlayerSteerAngleDegrees on rigs using that path instead.
//  Whichever controller is actually on this GameObject is auto-detected once
//  at Awake and re-checked live every frame (not cached as "the" answer), so
//  a mid-game relief/bus-swap can't leave wheels stuck turned from whatever
//  the last driven state was.
//
//  SETUP
//  ─────
//  Same auto-detect-by-name approach as BusWheelBob: add this to the bus
//  root alongside it, leave wheels[] empty, it finds the FRONT wheels
//  itself (name must contain both "Front" and "Wheel"). No per-wheel
//  dragging.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusWheelSteer : MonoBehaviour
{
    [System.Serializable]
    public class SteerWheel
    {
        public Transform wheel;
        [System.NonSerialized] public Quaternion restLocalRot;
        [System.NonSerialized] public bool hasRest;
        [System.NonSerialized] public float currentYaw;
    }

    [Header("Auto Setup (zero manual per-wheel assignment)")]
    public bool autoDetectFrontWheels = true;
    [Tooltip("Case-insensitive substring — a child transform must contain THIS and wheelNameContains to be treated as a steered front wheel.")]
    public string frontNameContains = "Front";
    [Tooltip("Case-insensitive substring — see above.")]
    public string wheelNameContains = "Wheel";
    [Tooltip("Case-insensitive substring — excludes false positives (e.g. a \"SteeringWheel\" mesh).")]
    public string excludeNameContains = "Steering";

    [Header("Feel")]
    [Tooltip("How quickly the wheel mesh's visual yaw catches up to the actual steer angle. Higher = snappier/more direct.")]
    public float smoothSpeed = 10f;

    private List<SteerWheel> _wheels = new List<SteerWheel>();
    private BusSimulationController _playerController;
    private NPCBusController _npcController;

    /// <summary>Current target steer angle in degrees (positive = right),
    /// read fresh every frame from whichever controller is present.</summary>
    private float SteerAngleDegrees
    {
        get
        {
            if (_playerController != null) return _playerController.SteerAngleDegrees;
            if (_npcController != null && _npcController.isPlayer) return _npcController.PlayerSteerAngleDegrees;
            return 0f;
        }
    }

    private void Awake()
    {
        _playerController = transform.root.GetComponentInChildren<BusSimulationController>(true);
        _npcController     = GetComponent<NPCBusController>();

        if (autoDetectFrontWheels)
            BuildWheelList();

        foreach (var w in _wheels)
        {
            if (w.wheel == null) continue;
            w.restLocalRot = w.wheel.localRotation;
            w.hasRest = true;
        }
    }

    private void BuildWheelList()
    {
        _wheels.Clear();
        string frontKey   = frontNameContains.ToLowerInvariant();
        string wheelKey   = wheelNameContains.ToLowerInvariant();
        string excludeKey = excludeNameContains.ToLowerInvariant();

        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t == transform) continue;
            string lowerName = t.name.ToLowerInvariant();
            if (!lowerName.Contains(wheelKey)) continue;
            if (!lowerName.Contains(frontKey)) continue;
            if (!string.IsNullOrEmpty(excludeKey) && lowerName.Contains(excludeKey)) continue;

            _wheels.Add(new SteerWheel { wheel = t });
        }

        if (_wheels.Count == 0)
            Debug.LogWarning($"[BusWheelSteer] '{name}': found no front wheels matching \"{frontNameContains}\"+\"{wheelNameContains}\" (excluding \"{excludeNameContains}\").", this);
        else
        {
            var sb = new System.Text.StringBuilder();
            foreach (var w in _wheels) sb.Append(w.wheel.name).Append(", ");
            Debug.Log($"[BusWheelSteer] '{name}': found {_wheels.Count} front wheel(s): {sb}", this);
        }
    }

    private void Update()
    {
        float target = SteerAngleDegrees;

        foreach (var w in _wheels)
        {
            if (w.wheel == null || !w.hasRest) continue;

            w.currentYaw = Mathf.Lerp(w.currentYaw, target, Time.deltaTime * smoothSpeed);

            // Rest rotation is only ever read from the one-time captured
            // value, never from the wheel's current rotation — same
            // "re-assert from rest, don't accumulate" pattern BusWheelBob
            // uses for position. That's what keeps this component's yaw
            // and BusWheelBob's Y-bob from ever fighting or drifting into
            // each other even though they touch the same transform.
            w.wheel.localRotation = w.restLocalRot * Quaternion.Euler(0f, w.currentYaw, 0f);
        }
    }

    [ContextMenu("Force Re-Detect Front Wheels")]
    private void ForceRedetect()
    {
        BuildWheelList();
        foreach (var w in _wheels)
        {
            if (w.wheel == null) continue;
            w.restLocalRot = w.wheel.localRotation;
            w.hasRest = true;
        }
    }
}