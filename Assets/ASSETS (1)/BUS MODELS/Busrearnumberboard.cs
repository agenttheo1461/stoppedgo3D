using UnityEngine;
using TMPro;

public class BusRearNumberBoard : MonoBehaviour
{
    [Header("Board Size (metres)")]
    public float boardWidth  = 0.55f;
    public float boardHeight = 0.30f;

    [Header("Colours")]
    public Color boardColor = new Color(1.00f, 0.55f, 0.00f, 1f);
    public Color textColor  = new Color(0.10f, 0.04f, 0.00f, 1f);

    [Header("Typography")]
    [Range(1f, 20f)] public float numberTextSize = 8f;
    public TMP_FontAsset font;

    [Header("Auto-Read")]
    public float updateIntervalSeconds = 1f;

    private TextMeshPro          _tmp;
    private MeshRenderer         _bg;
    private NPCBusController     _controller;
    private float                _pollTimer;
    private string               _lastRoute = null;

    private void Awake()
    {
        BuildBoard();
        _controller = GetComponentInParent<NPCBusController>();
    }

    private void Update()
    {
        if (_controller == null) return;
        _pollTimer -= Time.deltaTime;
        if (_pollTimer > 0f) return;
        _pollTimer = updateIntervalSeconds;
        PollController();
    }

    public void SetNumber(string routeNumber)
    {
        if (routeNumber == _lastRoute) return;
        _lastRoute = routeNumber;
        _bg.material.color = boardColor;
        _tmp.color = textColor;
        _tmp.text  = string.IsNullOrEmpty(routeNumber) ? "" : routeNumber.ToUpper();
    }

    public void SetBlank()
    {
        if (_lastRoute == "") return;
        _lastRoute = "";
        _bg.material.color = new Color(0.05f, 0.02f, 0.00f, 1f);
        _tmp.text = "";
    }

    private void PollController()
    {
        if (!_controller.running ||
            _controller.State == NPCBusController.BusState.Idle)
        { SetBlank(); return; }

        BusRouteData route = _controller.CurrentRoute;
        if (route == null) { SetBlank(); return; }

        SetNumber(route.routeNumber);
    }

    private void BuildBoard()
    {
        // ── Background quad — collider removed immediately ─────────────────────
        var bgGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bgGO.name = "RearBoardBG";
        DestroyImmediate(bgGO.GetComponent<MeshCollider>());   // ← immediate, not deferred
        bgGO.transform.SetParent(transform, false);
        bgGO.transform.localPosition = Vector3.zero;
        bgGO.transform.localRotation = Quaternion.identity;
        bgGO.transform.localScale    = new Vector3(boardWidth, boardHeight, 1f);

        _bg = bgGO.GetComponent<MeshRenderer>();
        _bg.material           = new Material(Shader.Find("Unlit/Color"));
        _bg.material.color     = boardColor;
        _bg.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
        _bg.receiveShadows     = false;

        // ── 3D TextMeshPro — no Canvas needed ─────────────────────────────────
        var textGO = new GameObject("RearBoardText");
        textGO.transform.SetParent(transform, false);
        textGO.transform.localPosition = new Vector3(0f, 0f, -0.002f);
        textGO.transform.localRotation = Quaternion.identity;
        textGO.transform.localScale    = Vector3.one;

        _tmp                  = textGO.AddComponent<TextMeshPro>();
        _tmp.alignment        = TextAlignmentOptions.Center;
        _tmp.fontStyle        = FontStyles.Bold;
        _tmp.fontSize         = numberTextSize;
        _tmp.enableAutoSizing = true;
        _tmp.fontSizeMin      = 1f;
        _tmp.fontSizeMax      = numberTextSize;
        _tmp.color            = textColor;
        _tmp.overflowMode     = TextOverflowModes.Ellipsis;

        var rt              = textGO.GetComponent<RectTransform>();
        rt.sizeDelta        = new Vector2(boardWidth, boardHeight);
        rt.anchoredPosition = Vector2.zero;

        if (font != null) _tmp.font = font;

        SetBlank();
    }
}