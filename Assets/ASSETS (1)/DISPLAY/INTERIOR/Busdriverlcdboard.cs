using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusDriverLCDBoard  —  v2, FLAT
//
//  [REDESIGN] Was a 3D world-space board: physical housing cube + screen
//  quad + a per-instance RenderTexture, either baked onto a bus prefab
//  (17/21 series permanently carried one, whether ever possessed or not --
//  see FleetRosterData.CensusBoardTypes) or dynamically attached/detached
//  per possession swap. Both approaches meant one RenderTexture existing
//  somewhere per bus, which is what caused the whole leak investigation.
//
//  Now a flat, draggable, screen-space OnGUI panel -- same class name, same
//  file name, same visual content, so nothing that already referenced
//  BusDriverLCDBoard by name breaks. Same drag pattern DriverConsole already
//  uses (grab the header, drag, release). DriverConsole itself is untouched
//  by this change.
//
//  ONE persistent instance for the whole session (same as DriverConsole),
//  NOT one per bus. "Changes per bus the same way DriverConsole/PlayerHandoff
//  does" comes for free: it reads live off PlayerHandoff.Instance every
//  refresh tick, and PlayerHandoff.Instance IS whichever bus is currently
//  possessed -- there's no per-bus object to create or destroy on a
//  possession swap, so that entire leak class is structurally impossible
//  here, the same way DriverConsole itself was never a leak risk.
//
//  Visual polish over the original (per request -- "some slight changes to
//  make it look a bit better"):
//    - Soft drop shadow behind the panel instead of a flat hard edge.
//    - A visible grab-handle dot row in the header so it reads as
//      draggable at a glance, matching the affordance DriverConsole gives
//      its own header.
//    - Slightly tighter row spacing / larger speed digits for readability
//      now that it's a fixed on-screen size rather than a 3D board viewed
//      at varying distance.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusDriverLCDBoard : MonoBehaviour
{
    public static BusDriverLCDBoard Instance { get; private set; }

    [Header("Data Source")]
    [Tooltip("Assign PlayerHandoff (or anything implementing IBusDisplaySource -- IBusDriverDisplaySource too, for the full driver-only fields). Leave blank to auto-resolve PlayerHandoff.Instance every frame, same as DriverConsole relies on it.")]
    public MonoBehaviour dataSourceBehaviour;
    private IBusDisplaySource _source;

    /// <summary>Human-readable line for the console's "boards" command --
    /// same purpose the original 3D board's SourceDescription had.</summary>
    public string SourceDescription =>
        dataSourceBehaviour != null
            ? $"{dataSourceBehaviour.GetType().Name} on '{dataSourceBehaviour.name}'"
              + (_source == null ? " (does NOT implement IBusDisplaySource!)" : "")
              + (_source is IBusDriverDisplaySource ? "" : " (no IBusDriverDisplaySource -- driver-only fields will show '--')")
            : "auto-resolving PlayerHandoff.Instance";

    [Header("Panel")]
    [Range(280, 700)] public int panelWidth  = 440;
    [Range(160, 420)] public int panelHeight = 240;
    public float dataRefreshIntervalSeconds = 0.25f;
    [Tooltip("Panel hides entirely while the possessed bus's engine is off -- same rule the original 3D board used.")]
    public bool hideWhenEngineOff = true;

    [Header("Panel Colors")]
    public Color panelBg          = new Color(0.035f, 0.035f, 0.04f, 0.94f);
    public Color shadowColor      = new Color(0f, 0f, 0f, 0.35f);
    public Color accentStripColor = new Color(0.85f, 0.55f, 0.15f, 1f);
    public Color dimTextColor     = new Color(0.5f, 0.5f, 0.5f);
    public Color speedColor       = new Color(0.95f, 0.95f, 0.98f);
    public Color adherenceOnTime  = new Color(0.35f, 0.9f, 0.5f);
    public Color adherenceLate    = new Color(0.95f, 0.55f, 0.25f);
    public Color adherenceEarly   = new Color(0.4f, 0.75f, 0.95f);
    public Color activeStatusColor = new Color(0.95f, 0.85f, 0.3f);

    // ── Drag state -- same pattern as DriverConsole.HandleDragging ─────────
    private float _panelX, _panelY;
    private bool  _isDragging;
    private Vector2 _dragOffset;
    private const float HeaderH = 26f;

    private GUIStyle _timeStyle, _fleetStyle, _speedStyle, _speedUnitStyle, _adherenceStyle,
                      _routeStyle, _destStyle, _nextLabelStyle, _nextStopStyle, _distStyle,
                      _statusStyle, _offStyle, _paxStyle;
    private bool  _stylesBuilt;
    private float _dataRefreshTimer;
    private BusDriverDisplayState _state = new BusDriverDisplayState();

    private void Awake()
    {
        Instance = this;
        // Default position: upper-right, out of DriverConsole's bottom-dock
        // footprint -- fully draggable from there afterward.
        _panelX = Screen.width - panelWidth - 16f;
        _panelY = 16f;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void ResolveSource()
    {
        if (dataSourceBehaviour != null) { _source = dataSourceBehaviour as IBusDisplaySource; return; }
        // Auto-follow whichever bus is currently possessed, same singleton
        // PlayerHandoff.Instance DriverConsole itself already leans on --
        // this is what makes the panel "change per bus" with no per-bus
        // object of its own.
        _source = PlayerHandoff.Instance as IBusDisplaySource;
    }

    private void Update()
    {
        ResolveSource();
        if (_source == null) return;
        if (hideWhenEngineOff && !_source.IsEngineRunning) return;

        _dataRefreshTimer -= Time.deltaTime;
        if (_dataRefreshTimer <= 0f)
        {
            _dataRefreshTimer = dataRefreshIntervalSeconds;
            _state.RefreshFrom(_source, CurrentTimeLabel());
        }
    }

    private string CurrentTimeLabel() =>
        BusScheduler.Instance != null
            ? BusScheduler.MinutesToTimeString(BusScheduler.Instance.GameTimeMinutes)
            : "--:--";

    private void OnGUI()
    {
        if (_source == null) return;
        if (hideWhenEngineOff && !_source.IsEngineRunning) return;

        if (!_stylesBuilt) { BuildStyles(); _stylesBuilt = true; }

        _panelX = Mathf.Clamp(_panelX, 0, Screen.width  - panelWidth);
        _panelY = Mathf.Clamp(_panelY, 0, Screen.height - panelHeight);
        var panelRect = new Rect(_panelX, _panelY, panelWidth, panelHeight);

        // Soft drop shadow -- offset duplicate, low alpha, drawn first.
        DrawFilledRect(new Rect(panelRect.x + 4f, panelRect.y + 5f, panelRect.width, panelRect.height), shadowColor);

        DrawFilledRect(panelRect, panelBg);

        float borderT = 3f;
        DrawFilledRect(new Rect(panelRect.x, panelRect.y, panelRect.width, borderT), accentStripColor);
        DrawFilledRect(new Rect(panelRect.x, panelRect.yMax - borderT, panelRect.width, borderT), accentStripColor);

        // Grab-handle affordance -- three small dots centred in the header,
        // purely visual, signals "drag here" at a glance.
        float dotY = panelRect.y + HeaderH * 0.5f;
        float dotCx = panelRect.x + panelRect.width * 0.5f;
        for (int i = -1; i <= 1; i++)
            DrawFilledRect(new Rect(dotCx + i * 8f - 1.5f, dotY - 1.5f, 3f, 3f), new Color(1f, 1f, 1f, 0.25f));

        GUI.BeginGroup(panelRect);
        int w = panelWidth, h = panelHeight;
        float pad = w * 0.05f;

        // Row 1: time (left) / fleet number (right)
        GUI.Label(new Rect(pad, h * 0.04f, w * 0.55f, h * 0.10f), _state.currentTimeLabel ?? "--", _timeStyle);
        GUI.Label(new Rect(w * 0.40f, h * 0.04f, w * 0.55f - pad, h * 0.10f), $"FLEET {_state.fleetNumber}", _fleetStyle);

        // Row 2: speed (big) / adherence
        string speedNum = _state.hasDriverExtras ? Mathf.RoundToInt(_state.speedKph).ToString() : "--";
        GUI.Label(new Rect(pad, h * 0.16f, w * 0.5f, h * 0.22f), speedNum, _speedStyle);
        GUI.Label(new Rect(pad + w * 0.30f, h * 0.30f, w * 0.2f, h * 0.07f), "km/h", _speedUnitStyle);

        if (_state.hasDriverExtras)
        {
            _adherenceStyle.normal.textColor =
                Mathf.Abs(_state.scheduleAdherenceMinutes) < 0.5f ? adherenceOnTime :
                _state.scheduleAdherenceMinutes > 0 ? adherenceLate : adherenceEarly;
            GUI.Label(new Rect(w * 0.5f, h * 0.19f, w * 0.46f, h * 0.12f), _state.AdherenceLabel(), _adherenceStyle);
        }
        else
        {
            _adherenceStyle.normal.textColor = dimTextColor;
            GUI.Label(new Rect(w * 0.5f, h * 0.19f, w * 0.46f, h * 0.12f), "--", _adherenceStyle);
        }

        // Passenger load as a percent of capacity, under the adherence readout (e.g. "35% FULL").
        if (_state.hasDriverExtras)
        {
            int pct = _state.PercentFull;
            _paxStyle.normal.textColor = pct >= 100 ? new Color(0.95f, 0.35f, 0.3f)
                                       : pct >= 85  ? adherenceLate
                                       : adherenceOnTime;
            GUI.Label(new Rect(w * 0.5f, h * 0.31f, w * 0.46f, h * 0.09f), $"{pct}% FULL", _paxStyle);
        }

        DrawFilledRect(new Rect(pad, h * 0.41f, w - pad * 2f, 2f), new Color(1f, 1f, 1f, 0.5f));

        // Row 3: route + destination
        string routeText = string.IsNullOrEmpty(_state.routeNumber) ? "--" : _state.routeNumber;
        string destText  = string.IsNullOrEmpty(_state.destinationHeadsign) ? "" : _state.destinationHeadsign;
        GUI.Label(new Rect(pad, h * 0.45f, w * 0.28f, h * 0.13f), routeText, _routeStyle);
        GUI.Label(new Rect(pad + w * 0.28f, h * 0.45f, w * 0.68f, h * 0.13f), destText, _destStyle);

        // Row 4: next stop + distance
        // [ADD] Wheelchair lift ramp -- no mesh/dedicated readout exists yet
        // (see IBusDriverDisplaySource.RampStateLabel), so while it's active
        // this row takes over from the normal next-stop text entirely (the
        // bus is dwelling at a stop with the ramp out anyway, not moving
        // toward a next stop). An ADA pax waiting/wanting off with the ramp
        // still stowed just tints the existing next-stop line blue instead.
        var adaBlue = new Color(0.40f, 0.62f, 1.0f);
        bool rampActive = _state.hasDriverExtras && _state.rampStateLabel != "STOWED";
        GUI.Label(new Rect(pad, h * 0.61f, w * 0.16f, h * 0.08f), rampActive ? "RAMP" : "NEXT", _nextLabelStyle);
        string nextStop = string.IsNullOrEmpty(_state.nextStopName) ? "--" : _state.nextStopName;
        if (rampActive)
        {
            nextStop = "♿ " + _state.rampStateLabel;
            _nextStopStyle.normal.textColor = adaBlue;
        }
        else if (_state.hasDriverExtras && _state.adaPaxEventPending)
        {
            nextStop = "♿ " + nextStop;
            _nextStopStyle.normal.textColor = adaBlue;
        }
        else
        {
            _nextStopStyle.normal.textColor = _state.stopRequested ? activeStatusColor : new Color(0.9f, 0.9f, 0.9f);
        }
        GUI.Label(new Rect(pad, h * 0.69f, w * 0.68f, h * 0.10f), nextStop, _nextStopStyle);
        GUI.Label(new Rect(w * 0.68f, h * 0.69f, w * 0.30f, h * 0.10f), _state.hasDriverExtras ? _state.DistanceLabel() : "--", _distStyle);

        DrawFilledRect(new Rect(pad, h * 0.81f, w - pad * 2f, 2f), new Color(1f, 1f, 1f, 0.5f));

        // Row 5: status strip
        float statusY = h * 0.85f;
        string runText = _state.hasDriverExtras && !string.IsNullOrEmpty(_state.runOrBlockLabel) ? _state.runOrBlockLabel : "";
        if (!string.IsNullOrEmpty(runText))
            GUI.Label(new Rect(pad, statusY, w * 0.32f, h * 0.09f), runText, _statusStyle);

        _statusStyle.normal.textColor = (_state.hasDriverExtras && _state.doorsOpen) ? activeStatusColor : dimTextColor;
        GUI.Label(new Rect(w * 0.34f, statusY, w * 0.30f, h * 0.09f), "DOORS", _statusStyle);

        _statusStyle.normal.textColor = (_state.hasDriverExtras && _state.parkingBrakeSet) ? activeStatusColor : dimTextColor;
        GUI.Label(new Rect(w * 0.66f, statusY, w * 0.32f, h * 0.09f), "P-BRAKE", _statusStyle);

        if (!_state.isInService)
            GUI.Label(new Rect(0, h * 0.94f, w, h * 0.05f), "NOT IN SERVICE", _offStyle);

        GUI.EndGroup();

        HandleDragging(panelRect);
    }

    private static void DrawFilledRect(Rect r, Color c)
    {
        var prevColor = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prevColor;
    }

    // ── Dragging -- identical pattern to DriverConsole.HandleDragging ──────
    private void HandleDragging(Rect panelRect)
    {
        var headerRect = new Rect(panelRect.x, panelRect.y, panelRect.width, HeaderH);
        var e = Event.current;

        if (e.type == EventType.MouseDown && headerRect.Contains(e.mousePosition) && !_isDragging)
        {
            _isDragging = true;
            _dragOffset = e.mousePosition - new Vector2(_panelX, _panelY);
            e.Use();
        }
        if (_isDragging)
        {
            if (e.type == EventType.MouseDrag)
            {
                _panelX = e.mousePosition.x - _dragOffset.x;
                _panelY = e.mousePosition.y - _dragOffset.y;
                e.Use();
            }
            if (e.type == EventType.MouseUp) { _isDragging = false; e.Use(); }
        }
    }

    private void BuildStyles()
    {
        int h = panelHeight;
        _timeStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.075f), fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
        _timeStyle.normal.textColor = Color.white;

        _fleetStyle = new GUIStyle(_timeStyle) { alignment = TextAnchor.UpperRight, fontSize = Mathf.RoundToInt(h * 0.06f) };
        _fleetStyle.normal.textColor = new Color(0.7f, 0.7f, 0.72f);

        _speedStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.26f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _speedStyle.normal.textColor = speedColor;

        _speedUnitStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.06f), alignment = TextAnchor.LowerLeft };
        _speedUnitStyle.normal.textColor = new Color(0.6f, 0.6f, 0.62f);

        _adherenceStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.07f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };

        _paxStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.065f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };

        _routeStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.11f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _routeStyle.normal.textColor = Color.white;

        _destStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.065f), alignment = TextAnchor.MiddleLeft, wordWrap = true };
        _destStyle.normal.textColor = new Color(0.8f, 0.8f, 0.82f);

        _nextLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.05f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        _nextLabelStyle.normal.textColor = new Color(0.5f, 0.5f, 0.52f);

        _nextStopStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.075f), alignment = TextAnchor.MiddleLeft, wordWrap = true };
        _distStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.075f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
        _distStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);

        _statusStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.055f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };

        _offStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(h * 0.06f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _offStyle.normal.textColor = new Color(0.9f, 0.3f, 0.3f);
    }
}