using UnityEngine;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusInteriorRouteNumberSign
//
//  The cheapest possible board: shows the route number and NOTHING else.
//  No destination, no via, no ETAs, no gray strip. Meant for the small
//  side-window / stanchion-mounted number-only displays real buses run
//  alongside the big windshield destination sign -- one glance, one number.
//
//    ┌──────────┐
//    │          │
//    │   109    │
//    │          │
//    └──────────┘
//
//  Same housing/RenderTexture-on-a-quad approach as BusInteriorDestinationSign
//  and BusInteriorLCDBoard, just square-ish and with almost nothing to draw --
//  intentionally NOT sharing a base class with those two since this one's
//  entire draw call is a single autofit label.
//
//  POWER: fully black whenever IsEngineRunning is false, same convention as
//  the other boards.
//
//  [PERF] Was refreshing its cached route number and redrawing its
//  RenderTexture every frame regardless of visibility -- see
//  BusBoardVisibilityGate.cs. This was arguably the cheapest board to begin
//  with, but it's still one more full RT bind+clear+label per off-screen
//  bus per frame, and it adds up the same as the others at fleet scale.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusInteriorRouteNumberSign : MonoBehaviour
{
    [Header("Data Source")]
    [Tooltip("Assign the BusDisplaySourceResolver on this same bus.")]
    public MonoBehaviour dataSourceBehaviour;
    private IBusDisplaySource _source;

    [Header("Board (world space)")]
    public Vector2 boardSizeMetres = new Vector2(0.20f, 0.20f);
    public float boardDepthMetres = 0.018f;
    [Range(0.01f, 0.15f)] public float bezelLipFraction = 0.05f;
    public Color housingColor = new Color(0.045f, 0.048f, 0.055f, 1f);

    [Header("Render Texture")]
    public Vector2Int textureResolution = new Vector2Int(256, 256);
    public float dataRefreshIntervalSeconds = 1f;

    [Header("Panel Colors")]
    public Color backgroundColor = Color.black;
    public Color routeColor      = Color.white;

    [Header("Performance")]
    [Tooltip("Board stops refreshing data and redrawing entirely once its quad isn't visible/is beyond this distance from Camera.main. Set to 0 to disable the distance backstop and rely on renderer visibility alone.")]
    public float cullDistanceMetres = 40f;

    private RenderTexture _rt;
    private Material      _quadMaterial, _housingMaterial;
    // [FIX] Same fix as every other board this session (LCDBoard,
    // DriverLCDBoard, DestinationSign, ScrollBoard) -- confirmed via
    // Instruments: 10,579 Material objects / 38.9MB, absurd for a
    // texture-free toon-shader-only material setup. Every board instance
    // was creating its own unique housing + quad material, every bus,
    // forever.
    private static readonly Dictionary<Color, Material> _sharedHousingMaterial = new Dictionary<Color, Material>();
    private static Material _sharedQuadMaterial;
    private MaterialPropertyBlock _quadPropBlock;
    private GUIStyle _routeStyle;
    private bool  _stylesBuilt;
    private float _dataRefreshTimer;
    private string _routeNumberCached = "--";
    private BusBoardVisibilityGate _visGate;

    private int    _fontSizeCache;
    private string _textSizedFor;

    public string SourceDescription =>
        dataSourceBehaviour != null
            ? $"{dataSourceBehaviour.GetType().Name} on '{dataSourceBehaviour.name}'" + (_source == null ? " (does NOT implement IBusDisplaySource!)" : "")
            : "no dataSourceBehaviour assigned";

    private void Awake()
    {
        // [ADD] Isolation-test kill switch -- see BusBoardTestSwitch.cs.
        if (BusBoardTestSwitch.DisableAllBoards) return;

        _source = dataSourceBehaviour as IBusDisplaySource;
        if (_source == null)
            Debug.LogWarning($"[{name}] dataSourceBehaviour does not implement IBusDisplaySource -- sign will show placeholder text only.");

        BuildRenderTexture();
        BuildHousing();
    }

    private void OnDestroy()
    {
        if (_rt != null) { _rt.Release(); Destroy(_rt); }
    }

    private void Update()
    {
        // [PERF] Skip the data pull entirely when nobody could see this
        // board anyway.
        if (_visGate != null && !_visGate.ShouldRender) return;

        _dataRefreshTimer -= Time.deltaTime;
        if (_dataRefreshTimer <= 0f && _source != null)
        {
            _dataRefreshTimer = dataRefreshIntervalSeconds;
            _routeNumberCached = string.IsNullOrEmpty(_source.RouteNumber) ? "--" : _source.RouteNumber;
        }
    }

    private void OnGUI()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        // [PERF] Skip the actual RenderTexture redraw when off-screen/far.
        if (_visGate != null && !_visGate.ShouldRender) return;

        if (_rt == null) return;
        if (!_stylesBuilt) { BuildStyles(); _stylesBuilt = true; }

        RenderTexture prevActive = RenderTexture.active;
        RenderTexture.active = _rt;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, textureResolution.x, textureResolution.y, 0);

        bool runningNow = _source != null && _source.IsEngineRunning;

        if (!runningNow)
        {
            GL.Clear(true, true, Color.black);
        }
        else
        {
            GL.Clear(true, true, backgroundColor);
            float w = textureResolution.x, h = textureResolution.y;
            EnsureFontFits(_routeNumberCached, w * 0.85f);
            GUI.Label(new Rect(0, 0, w, h), _routeNumberCached, _routeStyle);
        }

        GL.PopMatrix();
        RenderTexture.active = prevActive;
    }

    private void EnsureFontFits(string text, float maxWidth)
    {
        if (text == _textSizedFor && _fontSizeCache > 0)
        {
            _routeStyle.fontSize = _fontSizeCache;
            return;
        }

        int size = Mathf.RoundToInt(textureResolution.y * 0.65f);
        _routeStyle.fontSize = size;
        var content = new GUIContent(text);

        while (size > 16 && _routeStyle.CalcSize(content).x > maxWidth)
        {
            size -= 2;
            _routeStyle.fontSize = size;
        }

        _fontSizeCache = size;
        _textSizedFor  = text;
    }

    private void BuildStyles()
    {
        _routeStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(textureResolution.y * 0.65f),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
        _routeStyle.normal.textColor = routeColor;
    }

    private void BuildRenderTexture()
    {
        // [ADD] Destroy-before-create guard -- see BusInteriorLCDBoard's
        // identical fix for the full reasoning.
        if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }

        _rt = new RenderTexture(textureResolution.x, textureResolution.y, 0, RenderTextureFormat.ARGB32)
        {
            name       = $"RouteNumSignRT_{name}",
            filterMode = FilterMode.Bilinear,
            useMipMap  = false,
            wrapMode   = TextureWrapMode.Clamp,
        };
        _rt.Create();
    }

    private void BuildHousing()
    {
        float depth = Mathf.Max(0.003f, boardDepthMetres);
        float lip   = Mathf.Min(boardSizeMetres.x, boardSizeMetres.y) * bezelLipFraction;

        var housing = GameObject.CreatePrimitive(PrimitiveType.Cube);
        housing.name = "RouteNumSignHousing";
        Destroy(housing.GetComponent<Collider>());
        housing.transform.SetParent(transform, false);
        housing.transform.localRotation = Quaternion.identity;
        housing.transform.localPosition = new Vector3(0f, 0f, depth * 0.5f);
        housing.transform.localScale    = new Vector3(boardSizeMetres.x + lip * 2f, boardSizeMetres.y + lip * 2f, depth);

        var hr = housing.GetComponent<MeshRenderer>();
        if (!_sharedHousingMaterial.TryGetValue(housingColor, out _housingMaterial) || _housingMaterial == null)
        {
            _housingMaterial = new Material(GetBestOpaqueShader());
            SetMatColor(_housingMaterial, housingColor);
            _sharedHousingMaterial[housingColor] = _housingMaterial;
        }
        hr.sharedMaterial    = _housingMaterial;
        hr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        hr.receiveShadows    = true;

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "RouteNumSignScreen";
        Destroy(quad.GetComponent<Collider>());
        quad.transform.SetParent(transform, false);
        quad.transform.localPosition = new Vector3(0f, 0f, -0.0015f);
        quad.transform.localRotation = Quaternion.identity;
        quad.transform.localScale    = new Vector3(boardSizeMetres.x, boardSizeMetres.y, 1f);

        var mr = quad.GetComponent<MeshRenderer>();
        if (_sharedQuadMaterial == null)
            _sharedQuadMaterial = new Material(Shader.Find("Unlit/Texture"));
        mr.sharedMaterial = _sharedQuadMaterial;
        _quadPropBlock = new MaterialPropertyBlock();
        _quadPropBlock.SetTexture("_MainTex", _rt);
        mr.SetPropertyBlock(_quadPropBlock);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;

        // [PERF] Gate lives on the quad, since that's the actual renderer.
        _visGate = quad.AddComponent<BusBoardVisibilityGate>();
        _visGate.maxDistance = cullDistanceMetres;
    }

    private static Shader GetBestOpaqueShader()
    {
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        if (s == null) s = Shader.Find("Standard");
        if (s == null) s = Shader.Find("Diffuse");
        if (s == null) s = Shader.Find("Unlit/Color");
        return s;
    }

    private static void SetMatColor(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        else if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        else m.color = c;
    }
}