using UnityEngine;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusInteriorDestinationSign
//
//  A real destination sign, not a status board -- ONLY three things ever show:
//
//    ┌────────────────────────────────────────┐
//    │                                        │
//    │   109         DOWNTOWN TERMINAL        │  <- route number + destination,
//    │               VIA EVERGREEN            │     via/qualifier line beneath
//    │                                        │     (omitted entirely when the
//    └────────────────────────────────────────┘      route has none this direction)
//
//  No ETAs, no stop list, no gray strip, no time/rotation. This is the
//  "what bus is this and where's it going" board -- BusInteriorLCDBoard
//  already owns the detailed live-tracking layout; this one is deliberately
//  dumber and reads instantly from across the aisle, same as a real Nova/
//  Clever destination sign over the windshield.
//
//  Shares the same physical housing + RenderTexture-on-a-quad approach as
//  BusInteriorLCDBoard (kept as a separate, self-contained script rather than
//  refactored into a shared base -- the two boards' layouts don't overlap
//  enough to make that worth doing right now).
//
//  POWER: fully black whenever IsEngineRunning is false, same convention as
//  the LCD board -- no boot flourish here, it just cuts in/out since a real
//  destination sign doesn't run its own boot animation.
//
//  [PERF] Was refreshing state and redrawing its RenderTexture every frame
//  regardless of whether the board was anywhere near the player -- see
//  BusBoardVisibilityGate.cs. Now gated behind that.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusInteriorDestinationSign : MonoBehaviour
{
    [Header("Data Source")]
    [Tooltip("Assign the BusDisplaySourceResolver on this same bus (NOT NPCBusController or PlayerHandoff directly -- resolver is the one that's correct across a possession swap).")]
    public MonoBehaviour dataSourceBehaviour;
    private IBusDisplaySource _source;

    [Header("Board (world space)")]
    public Vector2 boardSizeMetres = new Vector2(0.55f, 0.22f);
    public float boardDepthMetres = 0.025f;
    [Range(0.01f, 0.12f)] public float bezelLipFraction = 0.04f;
    public Color housingColor = new Color(0.045f, 0.048f, 0.055f, 1f);

    [Header("Render Texture")]
    [Tooltip("Match boardSizeMetres' aspect ratio -- this board is wide/short like a real destination sign, so keep the texture wide too.")]
    public Vector2Int textureResolution = new Vector2Int(640, 256);
    public float dataRefreshIntervalSeconds = 1f;

    [Header("Panel Colors")]
    public Color backgroundColor = Color.black;
    public Color routeColor      = Color.white;
    public Color destColor       = new Color(0.95f, 0.75f, 0.15f); // classic amber-on-black destination sign look
    public Color qualifierColor  = new Color(0.75f, 0.60f, 0.15f);

    [Header("Screen Look")]
    [Range(0f, 0.5f)] public float scanlineStrength = 0.10f;

    [Header("Performance")]
    [Tooltip("Board stops refreshing data and redrawing entirely once its quad isn't visible/is beyond this distance from Camera.main. Set to 0 to disable the distance backstop and rely on renderer visibility alone.")]
    public float cullDistanceMetres = 40f;

    private RenderTexture _rt;
    private Material      _quadMaterial, _housingMaterial;
    private Texture2D     _scanlineTex;
    // [FIX] Confirmed via Instruments memory capture: 10,579 Material
    // objects / 38.9MB, an absurd count for a project using no bus livery
    // textures at all (materials-only toon shader). Root cause: EVERY board
    // instance (this class, plus its siblings -- ScrollBoard, LCD boards,
    // route number sign) was creating its OWN unique _housingMaterial AND
    // _quadMaterial, every single bus, forever. At fleet scale that's
    // thousands of duplicate materials for what should be a small handful
    // shared across everything.
    //
    // _housingMaterial: housingColor IS a genuinely per-instance-
    // configurable Inspector field (unlike the overlay textures), so a
    // blind single shared instance would be wrong if anyone ever
    // customizes it per-board. Cached by color instead -- every board left
    // at the class default (the common case, by far) shares one material;
    // any board that genuinely customizes its color still correctly gets
    // its own.
    private static readonly Dictionary<Color, Material> _sharedHousingMaterial = new Dictionary<Color, Material>();

    // _quadMaterial: the ONLY thing that ever varies per-instance is which
    // RenderTexture it displays -- and that's exactly what
    // MaterialPropertyBlock exists for. ONE shared material now, texture
    // assigned per-renderer via a property block instead of a unique
    // Material instance per board.
    private static Material _sharedQuadMaterial;
    private MaterialPropertyBlock _quadPropBlock;
    private BusBoardVisibilityGate _visGate;

    private GUIStyle _routeStyle, _destStyle, _qualStyle;
    private bool  _stylesBuilt;
    private float _dataRefreshTimer;
    private BusPassengerDisplayState _state = new BusPassengerDisplayState();

    private int    _routeFontSizeCache;
    private string _routeTextSizedFor;

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
        BuildScanlineTexture();
        BuildHousing();
    }

    private void OnDestroy()
    {
        if (_rt != null) { _rt.Release(); Destroy(_rt); }
        // [FIX] _scanlineTex/_housingMaterial/_quadMaterial are all shared
        // now -- may still be in use by other board instances, so no
        // longer destroyed here. Only the genuinely per-instance
        // RenderTexture gets cleaned up.
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
            _state.RefreshFrom(_source, "--"); // time label unused by this board
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
            DrawPanel();
            DrawScanlines();
        }

        GL.PopMatrix();
        RenderTexture.active = prevActive;
    }

    private void DrawPanel()
    {
        float w = textureResolution.x, h = textureResolution.y;

        bool hasQual = !string.IsNullOrEmpty(_state.routeQualifier);

        // Vertical split: route number occupies the left ~32% column, full
        // height. Destination (+ optional qualifier beneath it) fills the
        // rest, vertically centered as a pair so a missing qualifier doesn't
        // leave the destination text looking off-center.
        float routeColW = w * 0.32f;

        string rt = string.IsNullOrEmpty(_state.routeNumber) ? "--" : _state.routeNumber;
        EnsureRouteFontFits(rt, routeColW * 0.85f);
        var routeRect = new Rect(0, 0, routeColW, h);
        GUI.Label(routeRect, rt, _routeStyle);

        string dest = (_state.destinationHeadsign ?? "").ToUpperInvariant();
        string qual = hasQual ? _state.routeQualifier.ToUpperInvariant() : "";

        float destColX = routeColW + w * 0.02f;
        float destColW = w - destColX;

        if (hasQual)
        {
            float destH = h * 0.52f, qualH = h * 0.30f;
            float blockH = destH + qualH;
            float top = (h - blockH) * 0.5f;
            GUI.Label(new Rect(destColX, top, destColW, destH), dest, _destStyle);
            GUI.Label(new Rect(destColX, top + destH, destColW, qualH), qual, _qualStyle);
        }
        else
        {
            GUI.Label(new Rect(destColX, 0, destColW, h), dest, _destStyle);
        }
    }

    private void DrawScanlines()
    {
        if (scanlineStrength <= 0f || _scanlineTex == null) return;
        var full = new Rect(0, 0, textureResolution.x, textureResolution.y);
        GUI.color = new Color(1f, 1f, 1f, scanlineStrength);
        GUI.DrawTexture(full, _scanlineTex);
        GUI.color = Color.white;
    }

    private void EnsureRouteFontFits(string text, float maxWidth)
    {
        if (text == _routeTextSizedFor && _routeFontSizeCache > 0)
        {
            _routeStyle.fontSize = _routeFontSizeCache;
            return;
        }

        int baseSize = Mathf.RoundToInt(textureResolution.y * 0.34f);
        int size = baseSize;
        _routeStyle.fontSize = size;
        var content = new GUIContent(text);

        while (size > 16 && _routeStyle.CalcSize(content).x > maxWidth)
        {
            size -= 2;
            _routeStyle.fontSize = size;
        }

        _routeFontSizeCache = size;
        _routeTextSizedFor  = text;
    }

    private void BuildStyles()
    {
        int h = textureResolution.y;

        _routeStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(h * 0.34f), // overridden per-frame by EnsureRouteFontFits
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
        _routeStyle.normal.textColor = routeColor;

        _destStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(h * 0.34f),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            wordWrap  = true,
        };
        _destStyle.normal.textColor = destColor;

        _qualStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(h * 0.18f),
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.UpperLeft,
        };
        _qualStyle.normal.textColor = qualifierColor;
    }

    private void BuildRenderTexture()
    {
        // [ADD] Destroy-before-create guard -- see BusInteriorLCDBoard's
        // identical fix for the full reasoning.
        if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }

        _rt = new RenderTexture(textureResolution.x, textureResolution.y, 0, RenderTextureFormat.ARGB32)
        {
            name       = $"DestSignRT_{name}",
            filterMode = FilterMode.Bilinear,
            useMipMap  = false,
            wrapMode   = TextureWrapMode.Clamp,
        };
        _rt.Create();
    }

    private void BuildScanlineTexture()
    {
        int h = textureResolution.y;
        var key = new Vector2Int(1, h);
        if (_sharedScanlineTexCache.TryGetValue(key, out var cached) && cached != null)
        {
            _scanlineTex = cached;
            return;
        }
        _scanlineTex = new Texture2D(1, h, TextureFormat.Alpha8, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
        var cols = new Color[h];
        for (int y = 0; y < h; y++) cols[y] = new Color(0, 0, 0, (y % 2 == 0) ? 1f : 0f);
        _scanlineTex.SetPixels(cols);
        _scanlineTex.Apply(false, true);
        _sharedScanlineTexCache[key] = _scanlineTex;
    }
    private static readonly Dictionary<Vector2Int, Texture2D> _sharedScanlineTexCache = new Dictionary<Vector2Int, Texture2D>();

    private void BuildHousing()
    {
        float depth = Mathf.Max(0.003f, boardDepthMetres);
        float lip   = Mathf.Min(boardSizeMetres.x, boardSizeMetres.y) * bezelLipFraction;

        var housing = GameObject.CreatePrimitive(PrimitiveType.Cube);
        housing.name = "DestSignHousing";
        Destroy(housing.GetComponent<Collider>());
        housing.transform.SetParent(transform, false);
        housing.transform.localRotation = Quaternion.identity;
        housing.transform.localPosition = new Vector3(0f, 0f, depth * 0.5f);
        housing.transform.localScale    = new Vector3(boardSizeMetres.x + lip * 2f, boardSizeMetres.y + lip * 2f, depth);

        var hr = housing.GetComponent<MeshRenderer>();
        // [FIX] Was `new Material(...)` per instance -- now a color-keyed
        // shared cache. Every board left at the default housingColor (the
        // overwhelming majority) shares one material; a genuinely
        // customized board still gets its own, correctly.
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
        quad.name = "DestSignScreen";
        Destroy(quad.GetComponent<Collider>());
        quad.transform.SetParent(transform, false);
        quad.transform.localPosition = new Vector3(0f, 0f, -0.0015f);
        quad.transform.localRotation = Quaternion.identity;
        quad.transform.localScale    = new Vector3(boardSizeMetres.x, boardSizeMetres.y, 1f);

        var mr = quad.GetComponent<MeshRenderer>();
        // [FIX] Was `new Material(...) { mainTexture = _rt }` per instance
        // -- the ONLY thing that ever varies board-to-board is which
        // RenderTexture gets displayed, and that's exactly what
        // MaterialPropertyBlock exists for. One shared material now,
        // texture assigned per-RENDERER via the property block instead of
        // cloning a whole new Material per board.
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