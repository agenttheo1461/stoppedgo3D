using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  MDT LIVE 3D CAMERA
//
//  The "freely rotating joint" for LiveMap 3D mode: a pivot Transform at a focus
//  point with yaw/pitch driven by drag input and a boom-arm distance for zoom.
//  Distance and pivot are clamped against the road network's bounds (from
//  RoadMeshRenderer3D.ComputeCityBounds) so the map can't be dragged/zoomed
//  into a state where roads render off-frustum — this is the fix for the old
//  clipping complaint.
//
//  Usage: MDT_LiveMap calls SetActive(true/false) when the [3D] toggle is hit,
//  and feeds drag deltas from the same input it already reads for panning the
//  2D map, so both modes share one input path.
// ═══════════════════════════════════════════════════════════════════════════════
public class MDT_Live3DCamera : MonoBehaviour
{
    [Header("Refs")]
    public Camera targetCamera;
    public RoadMeshRenderer3D roadRenderer;
    public DioramaGridBox dioramaBox;

    [Header("Style")]
    [Tooltip("Solid background so the mini city doesn't render whatever skybox/scene is behind it — keeps it reading as a self-contained diorama.")]
    public Color backgroundColor = new Color(0.06f, 0.07f, 0.1f, 1f);

    [Header("Render Target")]
    [Tooltip("The camera renders into this off-screen texture instead of the screen — MDT_LiveMap draws it inside the map panel with GUI.DrawTexture, so it composites like any other UI element rather than punching a viewport hole in the screen.")]
    public RenderTexture outputTexture;
    private int _rtWidth = -1, _rtHeight = -1;

    /// Call once you know the pixel size of the panel it'll be drawn into (or on
    /// resize). Recreates the RenderTexture only if the size actually changed.
    public RenderTexture GetOutputTexture(int width, int height)
    {
        width = Mathf.Max(4, width);
        height = Mathf.Max(4, height);

        if (outputTexture == null || _rtWidth != width || _rtHeight != height)
        {
            if (outputTexture != null) { outputTexture.Release(); Destroy(outputTexture); }
            outputTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                name = "LiveMap3D_RT",
                antiAliasing = 2,
            };
            _rtWidth = width; _rtHeight = height;

            if (targetCamera != null) targetCamera.targetTexture = outputTexture;
        }
        return outputTexture;
    }

    [Header("Pivot")]
    public Vector3 pivot = Vector3.zero;

    [Header("Orbit")]
    public float yaw = 45f;
    public float pitch = 35f;
    public float minPitch = 12f;
    public float maxPitch = 80f;

    [Header("Distance / Zoom")]
    public float distance = 250f;
    public float minDistanceFactor = 0.15f; // fraction of city radius
    public float maxDistanceFactor = 1.6f;
    public float zoomSpeed = 40f;
    public float orbitSpeed = 0.25f;
    public float panSpeed = 0.6f;

    private bool _active;
    private Bounds _cityBounds;
    private float _cityRadius = 300f;

    public bool IsActive => _active;

    public void SetActive(bool active)
    {
        _active = active;
        if (targetCamera != null) targetCamera.gameObject.SetActive(active);
        if (active) RefreshBoundsAndClamp();
    }

    private void RefreshBoundsAndClamp()
    {
        if (roadRenderer == null) return;
        _cityBounds = roadRenderer.ComputeCityBounds();
        _cityRadius = Mathf.Max(50f, _cityBounds.extents.magnitude);

        if (pivot == Vector3.zero) pivot = _cityBounds.center;
        distance = Mathf.Clamp(distance, _cityRadius * minDistanceFactor, _cityRadius * maxDistanceFactor);

        if (targetCamera != null)
        {
            targetCamera.clearFlags = CameraClearFlags.SolidColor;
            targetCamera.backgroundColor = backgroundColor;
        }

        if (dioramaBox != null) dioramaBox.EnsureBuilt(_cityBounds);
    }

    /// Call from LiveMap's existing drag-handling code while 3D mode is active.
    /// leftDrag orbits, rightDrag (or a modifier) pans the pivot, scrollDelta zooms.
    public void ApplyInput(Vector2 leftDragDelta, Vector2 rightDragDelta, float scrollDelta)
    {
        if (!_active) return;

        if (leftDragDelta.sqrMagnitude > 0f)
        {
            yaw   += leftDragDelta.x * orbitSpeed;
            pitch -= leftDragDelta.y * orbitSpeed;
            pitch  = Mathf.Clamp(pitch, minPitch, maxPitch);
        }

        if (rightDragDelta.sqrMagnitude > 0f)
        {
            Quaternion yawRot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 right = yawRot * Vector3.right;
            Vector3 fwdFlat = yawRot * Vector3.forward;
            Vector3 delta = (-right * rightDragDelta.x - fwdFlat * rightDragDelta.y) * panSpeed * (distance / 200f);
            pivot += delta;
            ClampPivotToCity();
        }

        if (Mathf.Abs(scrollDelta) > 0f)
        {
            distance -= scrollDelta * zoomSpeed;
            distance = Mathf.Clamp(distance, _cityRadius * minDistanceFactor, _cityRadius * maxDistanceFactor);
        }

        ApplyTransform();
    }

    private void ClampPivotToCity()
    {
        // Keep pivot from wandering more than one city-radius past the bounds —
        // this is what stops the map from being dragged fully off frustum.
        Vector3 offset = pivot - _cityBounds.center;
        float maxOffset = _cityRadius * 1.2f;
        if (offset.magnitude > maxOffset)
            pivot = _cityBounds.center + offset.normalized * maxOffset;
    }

    private void ApplyTransform()
    {
        if (targetCamera == null) return;
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 pos = pivot + rot * (Vector3.back * distance);
        targetCamera.transform.SetPositionAndRotation(pos, rot);
    }

    /// Converts a click position (in the RenderTexture's own pixel space, i.e.
    /// local to the map panel, NOT screen space) into a world-space hit point.
    /// Raycasts against city-bounds floor plane by default; if the road/city
    /// geometry has colliders on a specific layer, pass it in for a precise hit.
    public bool TryPickWorldPoint(Vector2 localPixelPos, out Vector3 worldPoint, int layerMask = ~0)
    {
        worldPoint = Vector3.zero;
        if (targetCamera == null) return false;

        // Unity's screen-space Y is bottom-up; OnGUI/panel coords are top-down,
        // so flip before feeding into ScreenPointToRay.
        Vector3 screenPos = new Vector3(localPixelPos.x, targetCamera.pixelHeight - localPixelPos.y, 0f);
        Ray ray = targetCamera.ScreenPointToRay(screenPos);

        if (Physics.Raycast(ray, out RaycastHit hit, 5000f, layerMask))
        {
            worldPoint = hit.point;
            return true;
        }

        // Fallback: intersect with the city's floor plane so you still get a
        // coordinate even where nothing has a collider yet.
        float floorY = _cityBounds.size == Vector3.zero ? 0f : _cityBounds.min.y;
        Plane floor = new Plane(Vector3.up, new Vector3(0f, floorY, 0f));
        if (floor.Raycast(ray, out float enter))
        {
            worldPoint = ray.GetPoint(enter);
            return true;
        }
        return false;
    }

    private void LateUpdate()
    {
        if (_active) ApplyTransform();
    }
}