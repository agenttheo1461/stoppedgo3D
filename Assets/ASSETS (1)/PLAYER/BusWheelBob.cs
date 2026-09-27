using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS WHEEL BOB  —  each wheel can move up/down independently, but ONLY on
//  Y, and ONLY via a smooth visual Lerp — no physics, no forces, no
//  rigidbody on the wheel at all. Same core idea as BusDoorLeaf: capture a
//  "natural" rest pose once, then only ever animate toward a target from
//  there. BusDoorLeaf does it for open/closed; this does it for a
//  ground-following Y offset, continuously instead of a one-shot animation.
//
//  WHY THIS CAN'T REPEAT THE EARLIER FAILURES
//  ────────────────────────────────────────────
//  - No physics involved at all — no Rigidbody, no AddForce, no
//    ConfigurableJoint. Nothing here can fight gravity, launch the bus, or
//    fight another physics system, because it's not a physics system. It's
//    the same kind of plain transform Lerp BusDoorLeaf already uses safely.
//  - X and Z are RE-ASSERTED from the captured rest values every single
//    frame, unconditionally — not just "not moved," but actively forced back
//    to rest every frame regardless of what happened elsewhere. There is no
//    code path here that can accumulate horizontal drift, because X/Z are
//    never read from "last frame's position" — only ever from the one-time
//    captured rest value.
//  - Only Y is ever Lerped, and only within maxBobDistance of rest — it
//    can't run away to infinity like an unclamped spring could.
//
//  SETUP
//  ─────
//  Same auto-detect-by-name approach as before: add this to the bus root,
//  leave wheels[] empty, it finds them itself. No per-wheel dragging.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusWheelBob : MonoBehaviour
{
    [System.Serializable]
    public class BobWheel
    {
        public Transform wheel;
        [System.NonSerialized] public Vector3 restLocalPos;
        [System.NonSerialized] public bool hasRest;
        [System.NonSerialized] public float currentYOffset; // smoothed, local space
    }

    [Header("Auto Setup (zero manual per-wheel assignment)")]
    public bool autoDetectWheels = true;
    [Tooltip("Case-insensitive substring — any child transform whose name contains this is treated as a wheel.")]
    public string wheelNameContains = "Wheel";
    [Tooltip("Case-insensitive substring — excludes false positives (e.g. a \"SteeringWheel\" mesh).")]
    public string excludeNameContains = "Steering";

    [Header("Ground Probe")]
    [Tooltip("How far above the wheel's rest position the probe ray starts.")]
    public float probeStartHeight = 0.5f;
    [Tooltip("Total probe ray length (down from probeStartHeight above rest).")]
    public float probeLength = 1.2f;
    public LayerMask groundMask = ~0;

    [Header("Bob Feel")]
    [Tooltip("Max distance (meters, local Y) a wheel is allowed to bob away from its rest position, either direction. Keeps this cosmetic and small — real suspension travel is only a few centimeters.")]
    public float maxBobDistance = 0.08f;
    [Tooltip("How quickly the visual Y offset catches up to the ground probe's result. Higher = snappier/stiffer, lower = softer/floatier.")]
    public float smoothSpeed = 8f;

    private List<BobWheel> _wheels = new List<BobWheel>();

    private void Awake()
    {
        if (autoDetectWheels)
            BuildWheelList();

        foreach (var w in _wheels)
        {
            if (w.wheel == null) continue;
            w.restLocalPos = w.wheel.localPosition;
            w.hasRest = true;
        }
    }

    private void BuildWheelList()
    {
        _wheels.Clear();
        string wheelKey   = wheelNameContains.ToLowerInvariant();
        string excludeKey = excludeNameContains.ToLowerInvariant();

        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t == transform) continue;
            string lowerName = t.name.ToLowerInvariant();
            if (!lowerName.Contains(wheelKey)) continue;
            if (!string.IsNullOrEmpty(excludeKey) && lowerName.Contains(excludeKey)) continue;

            _wheels.Add(new BobWheel { wheel = t });
        }

        if (_wheels.Count == 0)
            Debug.LogWarning($"[BusWheelBob] '{name}': found no wheels matching \"{wheelNameContains}\" (excluding \"{excludeNameContains}\").", this);
        else
        {
            var sb = new System.Text.StringBuilder();
            foreach (var w in _wheels) sb.Append(w.wheel.name).Append(", ");
            Debug.Log($"[BusWheelBob] '{name}': found {_wheels.Count} wheel(s): {sb}", this);
        }
    }

    private void Update()
    {
        foreach (var w in _wheels)
        {
            if (w.wheel == null || !w.hasRest) continue;

            // Ground probe from the wheel's REST world position (not its
            // current, possibly-already-bobbed position) — otherwise the
            // probe origin would chase its own previous result and the whole
            // thing could slowly walk away from rest over time.
            Vector3 restWorldPos = w.wheel.parent != null
                ? w.wheel.parent.TransformPoint(w.restLocalPos)
                : w.restLocalPos;

            Vector3 rayOrigin = restWorldPos + Vector3.up * probeStartHeight;
            float targetOffset = 0f;

            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, probeLength, groundMask))
            {
                float groundLocalY = w.wheel.parent != null
                    ? w.wheel.parent.InverseTransformPoint(hit.point).y
                    : hit.point.y;

                float rawOffset = groundLocalY - w.restLocalPos.y;
                targetOffset = Mathf.Clamp(rawOffset, -maxBobDistance, maxBobDistance);
            }

            w.currentYOffset = Mathf.Lerp(w.currentYOffset, targetOffset, Time.deltaTime * smoothSpeed);

            // X and Z are RE-ASSERTED from restLocalPos every frame,
            // unconditionally — this is what makes horizontal drift
            // structurally impossible here, not just "unlikely."
            Vector3 pos = w.restLocalPos;
            pos.y += w.currentYOffset;
            w.wheel.localPosition = pos;
        }
    }

    [ContextMenu("Force Re-Detect Wheels")]
    private void ForceRedetect()
    {
        BuildWheelList();
        foreach (var w in _wheels)
        {
            if (w.wheel == null) continue;
            w.restLocalPos = w.wheel.localPosition;
            w.hasRest = true;
        }
    }
}