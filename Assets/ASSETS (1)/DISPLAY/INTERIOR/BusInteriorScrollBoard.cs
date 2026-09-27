using UnityEngine;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusInteriorScrollBoard
//
//  Classic single-line dot-matrix headsign board, mounted INSIDE the bus.
//  Same OnGUI-into-RenderTexture trick as WorldSpaceStopTrackerBoard: draw
//  normally in screen-space via GL.LoadPixelMatrix while _rt is the active
//  render target, then a quad in front of the driver/above the windshield
//  samples that texture. No shader work, no custom camera.
//
//  Content rotates through BusPassengerDisplayState.BuildRotation(): time,
//  bus ID, next stop, route/destination, with STOP REQUESTED (and any
//  console-pushed BusBoardDebugChannel message) jumping the queue when
//  active. Unlike the stop-side board this ISN'T page/flash -- it's a true
//  continuous horizontal marquee, matching real LED headsigns.
//
//  [07-30] Gets the same thin physical housing treatment as the LCD board --
//  a real box with a couple millimetres of depth behind the glass instead of
//  a bare Quad -- plus a soft glow pass so the dot-matrix characters bloom
//  instead of sitting perfectly flat. SourceDescription feeds the console's
//  "boards" diagnostic command.
//
//  [PERF] Was doing its full Update() data refresh AND full OnGUI() redraw
//  every frame regardless of whether anyone was anywhere near this bus --
//  see BusBoardVisibilityGate.cs. Now gated behind that, using the quad's
//  actual renderer visibility (near-zero extra cost, rides Unity's existing
//  culling) plus a distance backstop. Everything else about this board's
//  behavior is unchanged -- a board that goes offscreen just freezes its
//  RenderTexture content and its scroll position until it's visible again,
//  which is invisible to the player since nobody could see it moving anyway.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusInteriorScrollBoard : MonoBehaviour
{
    [Header("Data Source")]
    [Tooltip("Assign the BusSimulationController or NPCBusController on this same bus. Must implement IBusDisplaySource.")]
    public MonoBehaviour dataSourceBehaviour; // dragged in inspector, cast to IBusDisplaySource at runtime
    private IBusDisplaySource _source;

    [Header("Board (world space)")]
    public Vector2 boardSizeMetres = new Vector2(0.5f, 0.08f);
    [Tooltip("Physical depth of the housing behind the glass. Real headsign units are shallow but not paper-thin.")]
    public float boardDepthMetres = 0.018f;
    [Range(0.01f, 0.15f)] public float bezelLipFraction = 0.06f;
    public Color housingColor = new Color(0.03f, 0.03f, 0.035f, 1f);

    [Header("Render Texture")]
    public Vector2Int textureResolution = new Vector2Int(512, 64);
    [Tooltip("How often BusPassengerDisplayState re-pulls from the data source. The board still redraws every OnGUI regardless -- this only gates the underlying data refresh.")]
    public float dataRefreshIntervalSeconds = 1f;

    [Header("Marquee")]
    [Tooltip("Pixels per second the text scrolls right-to-left.")]
    public float scrollSpeedPxPerSec = 80f;
    [Tooltip("Blank gap between the end of one message and the start of the next, in pixels.")]
    public float gapBetweenMessagesPx = 120f;
    [Tooltip("Amber/LED look. Set alpha 0 on the background color for a transparent board if you're compositing over a physical bezel mesh instead.")]
    public Color ledColor = new Color(1f, 0.65f, 0.1f, 1f);
    public Color backgroundColor = Color.black;
    [Range(0f, 1f)] public float glowStrength = 0.35f;

    [Header("Performance")]
    [Tooltip("Board stops refreshing data and redrawing entirely once its quad isn't visible/is beyond this distance from Camera.main. Set to 0 to disable the distance backstop and rely on renderer visibility alone.")]
    public float cullDistanceMetres = 40f;

    // ── Runtime ───────────────────────────────────────────────────────────────
    private RenderTexture _rt;
    private Material      _quadMaterial, _housingMaterial;
    // [FIX] Same fix as Businteriordestinationsign.cs -- confirmed via
    // Instruments: 10,579 Material objects / 38.9MB across the project,
    // absurd for a texture-free toon-shader-only material setup. Every
    // board instance was creating its own unique housing + quad material.
    private static readonly Dictionary<Color, Material> _sharedHousingMaterial = new Dictionary<Color, Material>();
    private static Material _sharedQuadMaterial;
    private MaterialPropertyBlock _quadPropBlock;
    private GUIStyle      _ledStyle, _glowStyle;
    private bool          _styleBuilt;
    private float         _dataRefreshTimer;
    private BusPassengerDisplayState _state = new BusPassengerDisplayState();
    private BusBoardVisibilityGate   _visGate;

    private List<string> _rotation = new List<string> { "--" };
    private int   _msgIndex = 0;
    private float _scrollX; // current scroll offset, in px, counting down from board width

    [Header("Stop Requested")]
    [Tooltip("STOP REQUESTED no longer scrolls with the rest of the rotation -- it flashes centered on its own cycle, same as a real Clever/Luminator board interrupting for it.")]
    public float stopRequestedFlashPeriodSeconds = 3f;
    [Range(0f, 1f)] public float stopRequestedFlashOnFraction = 0.6f; // portion of the period spent lit vs blank
    private float _flashTimer;
    private bool  _stopRequestedNow;

    /// <summary>Human-readable line for the console's "boards" command.</summary>
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
            Debug.LogWarning($"[{name}] dataSourceBehaviour does not implement IBusDisplaySource -- board will show placeholder text only.");

        BuildRenderTexture();
        BuildHousing();
    }

    private void OnDestroy()
    {
        if (_rt != null) { _rt.Release(); Destroy(_rt); }
    }

    private void Update()
    {
        // [PERF] Skip the data pull entirely when nobody could see this board
        // anyway -- GetUpcomingStops/GetUpcomingStopsWithEta aren't free at
        // fleet scale, no reason to pay for them off-screen.
        if (_visGate != null && !_visGate.ShouldRender) return;

        _dataRefreshTimer -= Time.deltaTime;
        if (_dataRefreshTimer <= 0f)
        {
            _dataRefreshTimer = dataRefreshIntervalSeconds;
            if (_source != null)
            {
                _state.RefreshFrom(_source, SimClock.Instance != null ? SimClock.Instance.GameTimeString : "--:--");
                _stopRequestedNow = _state.stopRequested;

                // STOP REQUESTED gets its own flash path below and no longer
                // scrolls with the rest -- strip it back out of whatever
                // BuildRotation() returns. Everything else (including a
                // console-pushed debug message) still scrolls normally.
                _rotation = _state.BuildRotation();
                _rotation.RemoveAll(s => s == "STOP REQUESTED");
            }
        }

        _flashTimer += Time.deltaTime;
        if (_flashTimer >= stopRequestedFlashPeriodSeconds) _flashTimer -= stopRequestedFlashPeriodSeconds;

        // Marquee only advances while it's actually the thing on screen --
        // freezing it during the STOP REQUESTED flash keeps the scroll
        // position from jumping the moment the flash ends.
        if (!(_stopRequestedNow && FlashIsLit))
            _scrollX += scrollSpeedPxPerSec * Time.deltaTime;
    }

    private bool FlashIsLit => (_flashTimer / stopRequestedFlashPeriodSeconds) < stopRequestedFlashOnFraction;

    private void OnGUI()
    {
        // [07-30] Same fix as BusInteriorLCDBoard -- see its OnGUI for the
        // full explanation. Only draw on an actual Repaint pass; input events
        // (KeyDown/MouseDown/etc) each trigger their own OnGUI call and were
        // causing spurious partial draws that read as flashing/blacking out
        // while a key or mouse button was held.
        if (Event.current == null || Event.current.type != EventType.Repaint) return;

        // [PERF] Skip the actual RenderTexture redraw when off-screen/far --
        // this is the expensive part (RT bind + GL matrix + label draws).
        // The texture just keeps whatever it last drew, which is correct
        // since nobody could see it change anyway.
        if (_visGate != null && !_visGate.ShouldRender) return;

        if (_rt == null) return;
        if (!_styleBuilt) { BuildStyles(); _styleBuilt = true; }

        RenderTexture prevActive = RenderTexture.active;
        RenderTexture.active = _rt;
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, textureResolution.x, textureResolution.y, 0);
        GL.Clear(true, true, backgroundColor);

        // STOP REQUESTED preempts the marquee entirely and flashes centered
        // on its own fixed cycle -- it does NOT scroll past like every other
        // message in the rotation, and it keeps flashing on this cadence
        // regardless of what else is going on (debug push, terminal, etc).
        if (_stopRequestedNow)
        {
            if (FlashIsLit) DrawCentered("STOP REQUESTED", _ledStyle);
        }
        else
        {
            DrawMarquee();
        }

        GL.PopMatrix();
        RenderTexture.active = prevActive;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  MARQUEE -- continuous horizontal scroll across concatenated messages
    // ═════════════════════════════════════════════════════════════════════════
    private void DrawMarquee()
    {
        float w = textureResolution.x;
        float h = textureResolution.y;

        if (_rotation == null || _rotation.Count == 0) return;

        // Measure each message's pixel width via the style, build a running
        // total strip length so we know when to wrap the scroll offset.
        float totalStripWidth = 0f;
        var widths = new List<float>(_rotation.Count);
        foreach (var msg in _rotation)
        {
            float mw = _ledStyle.CalcSize(new GUIContent(msg)).x;
            widths.Add(mw);
            totalStripWidth += mw + gapBetweenMessagesPx;
        }
        if (totalStripWidth <= 0f) return;

        float offset = _scrollX % totalStripWidth;

        // Draw the strip starting off-screen right, walking left, wrapping
        // by drawing a second pass shifted back by one full strip length so
        // there's no visible seam when it loops. Glow pass first (bigger,
        // dim, same positions) so the crisp LED pass sits on top of a soft
        // bloom instead of looking printed flat onto the glass.
        float x = w - offset;
        for (int glowPass = 0; glowPass < (glowStrength > 0f ? 2 : 1); glowPass++)
        {
            bool isGlow = glowPass == 0 && glowStrength > 0f;
            var style = isGlow ? _glowStyle : _ledStyle;
            for (int pass = 0; pass < 2; pass++)
            {
                float cursorX = x - pass * totalStripWidth;
                for (int i = 0; i < _rotation.Count; i++)
                {
                    float mw = widths[i];
                    if (cursorX + mw > 0 && cursorX < w)
                        GUI.Label(new Rect(cursorX, 0, mw, h), _rotation[i], style);
                    cursorX += mw + gapBetweenMessagesPx;
                }
            }
        }
    }

    private void DrawCentered(string text, GUIStyle style)
    {
        float w = textureResolution.x, h = textureResolution.y;
        var prevAlign = style.alignment;
        style.alignment = TextAnchor.MiddleCenter;

        if (glowStrength > 0f)
        {
            var prevGlowAlign = _glowStyle.alignment;
            _glowStyle.alignment = TextAnchor.MiddleCenter;
            GUI.Label(new Rect(0, 0, w, h), text, _glowStyle);
            _glowStyle.alignment = prevGlowAlign;
        }

        GUI.Label(new Rect(0, 0, w, h), text, style);
        style.alignment = prevAlign;
    }

    private void BuildStyles()
    {
        _ledStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = Mathf.RoundToInt(textureResolution.y * 0.7f),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
        };
        _ledStyle.normal.textColor = ledColor;

        // Slightly larger + translucent, same font otherwise -- a cheap bloom
        // that reads as LED glow rather than a printed sticker.
        _glowStyle = new GUIStyle(_ledStyle)
        {
            fontSize = Mathf.RoundToInt(textureResolution.y * 0.82f),
        };
        _glowStyle.normal.textColor = new Color(ledColor.r, ledColor.g, ledColor.b, glowStrength);
    }

    private void BuildRenderTexture()
    {
        // [ADD] Destroy-before-create guard -- see BusInteriorLCDBoard's
        // identical fix for the full reasoning.
        if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }

        _rt = new RenderTexture(textureResolution.x, textureResolution.y, 0, RenderTextureFormat.ARGB32)
        {
            name       = $"ScrollBoardRT_{name}",
            filterMode = FilterMode.Point, // crisp LED pixels rather than blurred bilinear
            useMipMap  = false,
            wrapMode   = TextureWrapMode.Clamp,
        };
        _rt.Create();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PHYSICAL HOUSING — thin but real. Same front-cap-at-z=0 trick as the
    //  LCD board: the housing extends backward from local z=0 so it reads as
    //  a genuine box from a side angle, and the glass sits proud of it by a
    //  hair with a small lip of housing visible around the edge.
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildHousing()
    {
        float depth = Mathf.Max(0.002f, boardDepthMetres);
        float lip   = Mathf.Min(boardSizeMetres.x, boardSizeMetres.y) * bezelLipFraction;

        var housing = GameObject.CreatePrimitive(PrimitiveType.Cube);
        housing.name = "ScrollBoardHousing";
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
        quad.name = "ScrollBoardSurface";
        Destroy(quad.GetComponent<Collider>());
        quad.transform.SetParent(transform, false);
        quad.transform.localPosition = new Vector3(0f, 0f, -0.001f);
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

        // [PERF] Gate lives on the quad, since that's the actual renderer --
        // OnBecameVisible/Invisible only fire on the GameObject holding the
        // Renderer component itself.
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