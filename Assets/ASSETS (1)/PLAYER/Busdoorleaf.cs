using UnityEngine;
using System.Collections;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS DOOR LEAF — a single door panel that animates from its CLOSED pose
//  (however you've currently placed it in the scene) to an authored OPEN
//  pose (a plain local position + rotation you set by eye, no hinge/pivot
//  math required).
//
//  WORKFLOW:
//  1. Place/rotate the door mesh in the Scene view wherever "closed" should
//     look right — that's captured automatically the first time this runs.
//  2. In the Inspector, drag/rotate the SAME object in the Scene view to
//     wherever "open" should look right, then right-click this component's
//     header → "Capture Current Pose As Open" — copies the live transform
//     into openLocalPosition/openLocalEulerAngles for you, so you never
//     type Vector3 numbers blind.
//  3. A gizmo (shown whenever this object is selected) draws the authored
//     open target directly in the Scene view — a wire cube + axes — so you
//     can see exactly where it'll end up even outside Play mode.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusDoorLeaf : MonoBehaviour
{
    public enum DoorState { Closed, Opening, Open, Closing }

    [Header("Open Pose (local space, relative to this object's parent)")]
    public Vector3 openLocalPosition;
    public Vector3 openLocalEulerAngles;

    [Header("Motion")]
    [Tooltip("Real pneumatic transit doors take roughly this long to fully cycle.")]
    public float duration = 1.1f;
    public AnimationCurve curve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Header("Gizmo")]
    public bool showGizmo = true;
    public Color gizmoColor = new Color(1f, 0.6f, 0.1f, 0.9f);
    public Vector3 gizmoBoxSize = new Vector3(0.05f, 1.8f, 0.9f); // rough door-panel-sized box, adjust to your actual leaf

    public DoorState State { get; private set; } = DoorState.Closed;
    public bool IsOpenOrOpening => State == DoorState.Open || State == DoorState.Opening;

    private Vector3 _closedLocalPos;
    private Quaternion _closedLocalRot;
    private bool _closedPoseCaptured;
    private Coroutine _routine;

    void Awake()
    {
        CaptureClosedPoseIfNeeded();
    }

    private void CaptureClosedPoseIfNeeded()
    {
        if (_closedPoseCaptured) return;
        _closedLocalPos = transform.localPosition;
        _closedLocalRot = transform.localRotation;
        _closedPoseCaptured = true;
    }

    public void Open()
    {
        CaptureClosedPoseIfNeeded();
        if (State == DoorState.Open || State == DoorState.Opening) return;
        if (_routine != null) StopCoroutine(_routine);
        _routine = StartCoroutine(Animate(opening: true));
    }

    public void Close()
    {
        if (State == DoorState.Closed || State == DoorState.Closing) return;
        if (_routine != null) StopCoroutine(_routine);
        _routine = StartCoroutine(Animate(opening: false));
    }

    // Snaps instantly to closed — useful at spawn/pooling time so a bus
    // doesn't visibly cycle its doors the moment it appears.
    public void SnapClosed()
    {
        CaptureClosedPoseIfNeeded();
        if (_routine != null) StopCoroutine(_routine);
        transform.localPosition = _closedLocalPos;
        transform.localRotation = _closedLocalRot;
        State = DoorState.Closed;
    }

    private IEnumerator Animate(bool opening)
    {
        State = opening ? DoorState.Opening : DoorState.Closing;

        Vector3 fromPos = transform.localPosition;
        Quaternion fromRot = transform.localRotation;
        Vector3 toPos = opening ? openLocalPosition : _closedLocalPos;
        Quaternion toRot = opening ? Quaternion.Euler(openLocalEulerAngles) : _closedLocalRot;

        float t = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / Mathf.Max(0.01f, duration);
            float eased = curve.Evaluate(Mathf.Clamp01(t));
            transform.localPosition = Vector3.LerpUnclamped(fromPos, toPos, eased);
            transform.localRotation = Quaternion.SlerpUnclamped(fromRot, toRot, eased);
            yield return null;
        }

        transform.localPosition = toPos;
        transform.localRotation = toRot;
        State = opening ? DoorState.Open : DoorState.Closed;
        _routine = null;
    }

#if UNITY_EDITOR
    [ContextMenu("Capture Current Pose As Open")]
    private void CaptureCurrentPoseAsOpen()
    {
        openLocalPosition = transform.localPosition;
        openLocalEulerAngles = transform.localEulerAngles;
        Debug.Log($"[BusDoorLeaf] '{name}' open pose captured: pos={openLocalPosition}, euler={openLocalEulerAngles}");
    }

    [ContextMenu("Capture Current Pose As Closed")]
    private void CaptureCurrentPoseAsClosed()
    {
        _closedLocalPos = transform.localPosition;
        _closedLocalRot = transform.localRotation;
        _closedPoseCaptured = true;
        Debug.Log($"[BusDoorLeaf] '{name}' closed pose captured: pos={_closedLocalPos}, euler={_closedLocalRot.eulerAngles}");
    }

    [ContextMenu("Preview: Snap To Open (Editor Only)")]
    private void PreviewSnapToOpen()
    {
        transform.localPosition = openLocalPosition;
        transform.localRotation = Quaternion.Euler(openLocalEulerAngles);
    }

    [ContextMenu("Preview: Snap To Closed (Editor Only)")]
    private void PreviewSnapToClosed()
    {
        if (_closedPoseCaptured)
        {
            transform.localPosition = _closedLocalPos;
            transform.localRotation = _closedLocalRot;
        }
    }

    void OnDrawGizmosSelected()
    {
        if (!showGizmo || transform.parent == null) return;

        Matrix4x4 prevMatrix = Gizmos.matrix;
        Color prevColor = Gizmos.color;

        // Draw the authored OPEN target, in the parent's local space, so it
        // stays correct regardless of where the bus itself is in the world.
        Matrix4x4 openLocalMatrix = Matrix4x4.TRS(openLocalPosition, Quaternion.Euler(openLocalEulerAngles), Vector3.one);
        Gizmos.matrix = transform.parent.localToWorldMatrix * openLocalMatrix;

        Gizmos.color = gizmoColor;
        Gizmos.DrawWireCube(Vector3.zero, gizmoBoxSize);

        // small axis ticks so rotation is visible, not just position
        Gizmos.color = Color.red;   Gizmos.DrawLine(Vector3.zero, Vector3.right * 0.3f);
        Gizmos.color = Color.green; Gizmos.DrawLine(Vector3.zero, Vector3.up * 0.3f);
        Gizmos.color = Color.blue;  Gizmos.DrawLine(Vector3.zero, Vector3.forward * 0.3f);

        Gizmos.matrix = prevMatrix;

        // A line from the door's current live position to the open target,
        // in world space, so the travel path is obvious at a glance.
        Vector3 openWorldPos = transform.parent.TransformPoint(openLocalPosition);
        Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.4f);
        Gizmos.DrawLine(transform.position, openWorldPos);

        Gizmos.color = prevColor;
    }
#endif
}