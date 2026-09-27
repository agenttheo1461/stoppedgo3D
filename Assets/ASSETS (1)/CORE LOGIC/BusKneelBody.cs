using UnityEngine;

/// <summary>Implemented by kneel-capable bus suspension components so
/// anything that just wants to trigger a kneel — like PlayerHandoff's K
/// hotkey — doesn't need to know or care which implementation is on a given
/// bus.</summary>
public interface IKneelable
{
    bool IsKneeling { get; }
    void SetKneeling(bool kneel);
    void ToggleKneeling();
}

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS KNEEL BODY  —  no suspension at all. Wheels get ZERO special handling:
//  they're just ordinary collider geometry, contributing to the bus's single
//  compound Rigidbody like any other part of it. Gravity + normal Unity
//  collision response is what holds the bus up and lets it settle on real
//  terrain — the same thing that's been true since the earlier fix where
//  NPCBusController stopped forcing Y and left it to gravity/colliders.
//  Wheels don't move relative to the body AT ALL; there's nothing to lock,
//  because nothing ever moves them.
//
//  Kneeling is now purely cosmetic and has nothing to do with wheels or
//  physics: it lowers the BODY GROUP (everything that isn't a wheel — mesh,
//  doors, interior) relative to the bus root, using a plain local-position
//  Lerp — PLUS a small local-Z roll so the RIGHT (curb) side dips lower than
//  the left, matching how a real ADA kneel visibly leans the bus toward the
//  curb rather than just sinking evenly. The wheels stay exactly where they
//  are; the body sinks and leans toward them. Still no physics involved —
//  same plain transform Lerp/rotation as before, just two axes instead of
//  one.
//
//  HIERARCHY THIS EXPECTS
//  ───────────────────────
//  Bus root (Rigidbody, still MovePosition-driven for X/Z, Y left to gravity)
//   ├─ bodyGroup — everything except wheels (mesh, doors, interior). This is
//   │   what gets moved down/up on kneel. Auto-detected by name if not
//   │   assigned (see bodyGroupNameContains).
//   └─ wheels — plain children with their own (real, ENABLED) colliders.
//       No script touches them. No Rigidbody of their own. They're just
//       part of the bus's one compound collider like any other part of it.
//
//  CLEANUP FROM THE OLD SUSPENSION ATTEMPTS
//  ──────────────────────────────────────────
//  If BusWheelSuspension or BusWheelJointSuspension were ever added to this
//  bus, they (a) disabled the wheels' own colliders and (b) may have left
//  behind auto-generated "_SuspensionMount" / "_PhysicsWheel" objects. Right-
//  click this component and use "Clean Up Old Suspension Objects" to
//  re-enable wheel colliders and remove that leftover scaffolding.
// ═══════════════════════════════════════════════════════════════════════════════
[RequireComponent(typeof(Rigidbody))]
public class BusKneelBody : MonoBehaviour, IKneelable
{
    [Header("Body Group")]
    [Tooltip("Everything that ISN'T a wheel — body mesh, doors, interior. This is what physically moves down/up on kneel. Leave empty to auto-detect by name.")]
    public Transform bodyGroup;
    [Tooltip("If bodyGroup is empty, auto-detect a direct child whose name contains this substring (case-insensitive).")]
    public string bodyGroupNameContains = "Body";

    [Tooltip("Articulated buses: OTHER body groups (e.g. the rear section's body) that should kneel together with this one. Each gets the same drop and lean, applied to its own local position/rotation. Leave empty for a single-section bus.")]
    public Transform[] extraBodyGroups;

    [Header("Kneel (ADA-style — lowers the BODY, not the wheels)")]
    [Tooltip("How far the body group drops (local Y, meters) when fully knelt.")]
    public float kneelDropDistance = 0.08f;
    [Tooltip("Extra roll (degrees, local Z) applied when fully knelt so the RIGHT (curb) side dips lower than the left, on top of the uniform drop above. 0 = old even-drop behavior. Positive rolls right-down for a standard rig; flip the sign if your bus's right side comes out leaning the wrong way.")]
    public float kneelRightLeanDegrees = 2.5f;
    [Tooltip("How fast the kneel transition blends, in kneel-cycles/second.")]
    public float kneelSpeed = 3f;

    private Vector3[] _extraRestPos;
    private Quaternion[] _extraRestRot;
    private Vector3 _bodyGroupRestLocalPos;
    private Quaternion _bodyGroupRestLocalRot;
    private bool    _hasRestPos;
    private bool  _kneeling;
    private float _kneelBlend; // 0 = normal, 1 = fully knelt

    public bool IsKneeling   => _kneeling;
    public bool IsFullyKnelt => _kneelBlend > 0.95f;

    private void Awake()
    {
        // ── Nested-Rigidbody safety net ─────────────────────────────────────
        // [RequireComponent(Rigidbody)] above is correct ONLY if this
        // component sits on the actual physics-driven bus root/tractor (the
        // same GameObject as NPCBusController/BusSimulationController and
        // their own Rigidbody). If it ends up ANYWHERE else in the
        // hierarchy -- an empty wrapper parent, a trailerPivot, a stray
        // child -- RequireComponent silently adds a SECOND Rigidbody there.
        // Unity does not handle nested Rigidbodies well: a Rigidbody on a
        // PARENT and a Rigidbody on a CHILD fighting in the same hierarchy
        // produces exactly the "the driven object's MovePosition calls stop
        // visibly doing anything" symptom -- no error, no warning by
        // default, the position numbers on the real driver just never
        // change. This isn't specific to one controller type or one known
        // bad object (trailerPivot) -- ANY nested Rigidbody pair does this,
        // so check both directions generically: is there ALSO a Rigidbody
        // somewhere above us, or somewhere below us?
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            var ancestorRb = transform.parent != null ? transform.parent.GetComponentInParent<Rigidbody>() : null;
            Rigidbody descendantRb = null;
            foreach (var child in GetComponentsInChildren<Rigidbody>(true))
            {
                if (child != rb) { descendantRb = child; break; }
            }

            if (ancestorRb != null || descendantRb != null)
            {
                string conflict = ancestorRb != null
                    ? $"a PARENT Rigidbody on '{ancestorRb.name}'"
                    : $"a CHILD Rigidbody on '{descendantRb.name}'";
                Debug.LogWarning($"[BusKneelBody] '{name}': this GameObject has its own Rigidbody (added by " +
                                  $"[RequireComponent] if one wasn't already here) AND there's {conflict} in the " +
                                  "same hierarchy -- that's a nested-Rigidbody conflict, and Unity's physics handles " +
                                  "those badly (typically: whichever object is actually being driven via MovePosition " +
                                  "stops visibly moving, with no error). Forcing THIS Rigidbody kinematic so it can't " +
                                  "fight the other one, but the real fix is moving BusKneelBody onto the SAME " +
                                  "GameObject as your bus controller + its real driven Rigidbody -- not onto a " +
                                  "wrapper/parent or a trailer/segment object.", this);
                rb.isKinematic = true;
            }
        }

        if (bodyGroup == null)
            AutoDetectBodyGroup();

        if (bodyGroup != null)
        {
            _bodyGroupRestLocalPos = bodyGroup.localPosition;
            _bodyGroupRestLocalRot = bodyGroup.localRotation;
            _hasRestPos = true;
        }
        if (extraBodyGroups != null && extraBodyGroups.Length > 0)
        {
            _extraRestPos = new Vector3[extraBodyGroups.Length];
            _extraRestRot = new Quaternion[extraBodyGroups.Length];
            for (int i = 0; i < extraBodyGroups.Length; i++)
            {
                if (extraBodyGroups[i] == null) continue;
                _extraRestPos[i] = extraBodyGroups[i].localPosition;
                _extraRestRot[i] = extraBodyGroups[i].localRotation;
            }
        }

        if (bodyGroup == null)
        {
            Debug.LogWarning($"[BusKneelBody] '{name}': no bodyGroup assigned and none found containing \"{bodyGroupNameContains}\" — kneeling will do nothing. Group everything except wheels under one child transform and either assign it here or name it to match.", this);
        }
    }

    private void AutoDetectBodyGroup()
    {
        string key = bodyGroupNameContains.ToLowerInvariant();
        foreach (Transform child in transform)
        {
            if (child.name.ToLowerInvariant().Contains(key))
            {
                bodyGroup = child;
                return;
            }
        }
    }

    private void Update()
    {
        if (!_hasRestPos) return;

        float target = _kneeling ? 1f : 0f;
        _kneelBlend = Mathf.MoveTowards(_kneelBlend, target, Time.deltaTime * kneelSpeed);

        Vector3 pos = _bodyGroupRestLocalPos;
        pos.y -= kneelDropDistance * _kneelBlend;
        bodyGroup.localPosition = pos;

        // Right-side curb lean — same blend value as the drop, so both
        // settle together instead of one lagging the other.
        Quaternion lean = Quaternion.Euler(0f, 0f, kneelRightLeanDegrees * _kneelBlend);
        bodyGroup.localRotation = _bodyGroupRestLocalRot * lean;

        if (extraBodyGroups != null && _extraRestPos != null)
        {
            for (int i = 0; i < extraBodyGroups.Length; i++)
            {
                var g = extraBodyGroups[i];
                if (g == null) continue;
                Vector3 ep = _extraRestPos[i];
                ep.y -= kneelDropDistance * _kneelBlend;
                g.localPosition = ep;
                g.localRotation = _extraRestRot[i] * lean;
            }
        }
    }

    public void SetKneeling(bool kneel) => _kneeling = kneel;
    public void ToggleKneeling() => _kneeling = !_kneeling;

    /// <summary>Undoes what the old suspension components did to this bus:
    /// re-enables every wheel mesh's own collider (so it actually holds the
    /// bus up again via ordinary physics), and removes any leftover
    /// auto-generated "_SuspensionMount" / "_PhysicsWheel" scaffolding
    /// objects those components created.</summary>
    [ContextMenu("Clean Up Old Suspension Objects")]
    private void CleanUpOldSuspensionObjects()
    {
        int collidersReenabled = 0;
        int objectsRemoved = 0;

        // Re-enable colliders that NeutralizeWheelColliders (from the old
        // raycast/joint suspension) disabled or turned into triggers.
        foreach (var col in GetComponentsInChildren<Collider>(true))
        {
            if (!col.enabled)
            {
                col.enabled = true;
                collidersReenabled++;
            }
            if (col.isTrigger && (col.name.EndsWith("_SuspensionMount") == false))
            {
                // Only flip back non-scaffolding colliders — scaffolding
                // objects themselves get destroyed below regardless.
                col.isTrigger = false;
            }
        }

        // Remove leftover scaffolding objects from the old components.
        var toRemove = new System.Collections.Generic.List<GameObject>();
        foreach (var t in GetComponentsInChildren<Transform>(true))
        {
            if (t == transform) continue;
            if (t.name.EndsWith("_SuspensionMount") || t.name.EndsWith("_PhysicsWheel"))
                toRemove.Add(t.gameObject);
        }
        foreach (var go in toRemove)
        {
            objectsRemoved++;
            if (Application.isPlaying) Destroy(go);
            else                       DestroyImmediate(go);
        }

        Debug.Log($"[BusKneelBody] '{name}': re-enabled {collidersReenabled} collider(s), removed {objectsRemoved} leftover suspension object(s). " +
                  "If you also had BusWheelSuspension or BusWheelJointSuspension components on this GameObject, remove those manually too.", this);
    }
}