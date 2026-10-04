using System.Reflection;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  SETTINGS WINDOW
//
//  Draggable window covering every player-facing setting: theme, key
//  rebinding (KeyBindData's fields via reflection, so a future binding is
//  automatically covered), notification preferences, developer/debug mode,
//  UI scale override, units, graphics quality, shape-based status
//  indicators (accessibility), and a self-service "clear saved data" button.
//  Everything here reads/writes straight through SettingsData/KeyBindings --
//  this window holds no state of its own besides which key (if any) is
//  currently being rebound.
// ═══════════════════════════════════════════════════════════════════════════════
public class SettingsWindow : MonoBehaviour
{
    public static SettingsWindow Instance { get; private set; }

    private bool _open;
    public bool IsOpen => _open;

    private Rect _windowRect = new Rect(120, 70, 420, 560);
    private bool _dragging;
    private Vector2 _dragOffset;
    private Vector2 _scroll;

    private string _rebindingField; // non-null while waiting for the next keypress
    private string _clearedMessage;
    private float _clearedMessageAt = -999f;

    private bool _stylesReady;
    private GUIStyle _lblTitle, _lblSection, _lblDim, _lblBody, _btnPrimary, _btnSecond, _btnDanger, _btnToggleOn, _btnToggleOff;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("SettingsWindow").AddComponent<SettingsWindow>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Apply the saved quality preset once at startup (a redeploy every
        // frame would be wasteful and pointless -- nothing else changes it).
        if (SettingsData.QualityLevel >= 0)
            QualitySettings.SetQualityLevel(SettingsData.QualityLevel, true);
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    public void Toggle() => _open = !_open;
    public void Open()  => _open = true;
    public void Close() { _open = false; _rebindingField = null; }

    // [ADD] Settings doesn't touch shift state (unlike Main Menu, which now
    // fully resets on open), so it gets its own direct key -- no need to
    // route through Main Menu just to change a setting mid-drive. Skips
    // firing while actively capturing a key rebind, so pressing F5 while
    // waiting to rebind something doesn't also close the window.
    private void Update()
    {
        if (_rebindingField != null) return;
        if (Input.GetKeyDown(KeyBindings.Current.settingsMenu))
            Toggle();
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle    = MDT_UITheme.MakeLabel(16, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblSection  = MDT_UITheme.MakeLabel(12, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextAmber);
        _lblDim      = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _lblBody     = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _btnPrimary  = MDT_UITheme.MakeButton(new Color(0.05f, 0.30f, 0.14f, 1f), MDT_UITheme.TextGreen, 13, FontStyle.Bold);
        _btnSecond   = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 12);
        _btnDanger   = MDT_UITheme.MakeButton(new Color(0.30f, 0.06f, 0.06f, 1f), MDT_UITheme.TextRed, 12);
        _btnToggleOn = MDT_UITheme.MakeButton(new Color(0.05f, 0.30f, 0.14f, 1f), MDT_UITheme.TextGreen, 12, FontStyle.Bold);
        _btnToggleOff= MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextDim, 12);
    }

    private void OnGUI()
    {
        if (!_open) return;
        EnsureStyles();
        CaptureRebindKey();

        MDT_UITheme.DrawSoftShadow(_windowRect, 18f);
        MDT_UITheme.DrawRoundedRectBordered(_windowRect, 14f, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        var titleBar = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, 34);
        HandleDrag(titleBar);
        GUI.Label(new Rect(titleBar.x + 14, titleBar.y, titleBar.width - 60, titleBar.height), "SETTINGS", _lblTitle);
        if (GUI.Button(new Rect(titleBar.xMax - 34, titleBar.y + 5, 24, 24), "✕", _btnSecond))
            Close();

        float x = _windowRect.x + 14, y = 0, w = _windowRect.width - 28;
        var scrollArea = new Rect(x, _windowRect.y + 44, w, _windowRect.height - 58);
        _scroll = GUI.BeginScrollView(scrollArea, _scroll, new Rect(0, 0, w - 20, 1560));
        y = 0;

        y = DrawSection(w, y, "DISPLAY");
        y = DrawToggleRow(w, y, "Light theme", SettingsData.LightTheme, v => SettingsData.LightTheme = v);
        y = DrawToggleRow(w, y, "Metric units (km/h)", SettingsData.UseMetricUnits, v => SettingsData.UseMetricUnits = v);
        y = DrawToggleRow(w, y, "Shape status indicators (colorblind aid)", SettingsData.ShapeIndicators, v => SettingsData.ShapeIndicators = v);
        y = DrawUiScaleRow(w, y);
        y = DrawQualityRow(w, y);

        y += 10;
        y = DrawSection(w, y, "NOTIFICATIONS");
        y = DrawToggleRow(w, y, "Shift countdown reminders", SettingsData.NotifyShiftCountdown, v => SettingsData.NotifyShiftCountdown = v);
        y = DrawToggleRow(w, y, "Starred bus availability nudges", SettingsData.NotifyStarredBuses, v => SettingsData.NotifyStarredBuses = v);
        y = DrawToggleRow(w, y, "Favorite route availability nudges", SettingsData.NotifyFavoriteRoutes, v => SettingsData.NotifyFavoriteRoutes = v);
        y = DrawToggleRow(w, y, "Real OS push (Android/iOS/macOS)", SettingsData.UseOsPush, v => SettingsData.UseOsPush = v);

        // [ADD] Second layer -- a route picker gets a whole second window
        // (RouteRosterWindow), same as MY BUSES on the main menu footer does
        // for starred buses, rather than trying to cram a scrollable route
        // list into this already-tall single-page settings sheet.
        y += 10;
        y = DrawSection(w, y, "ROUTES");
        var routesRow = new Rect(0, y, w, 34);
        if (GUI.Button(routesRow, "Manage favorite routes ▸", _btnSecond))
            RouteRosterWindow.Instance?.Open();
        y += 38;

        y += 10;
        y = DrawSection(w, y, "DEVELOPER");
        y = DrawToggleRow(w, y, "Developer mode (debug hotkeys)", SettingsData.DeveloperMode, v => SettingsData.DeveloperMode = v);

        y += 10;
        y = DrawSection(w, y, "DATA");
        var clearRow = new Rect(0, y, w, 34);
        if (GUI.Button(clearRow, "Clear diagnostic data (trip history + old day saves)", _btnDanger))
        {
            SettingsData.ClearDiagnosticData();
            _clearedMessage = "Cleared.";
            _clearedMessageAt = Time.realtimeSinceStartup;
        }
        y += 38;
        if (!string.IsNullOrEmpty(_clearedMessage) && Time.realtimeSinceStartup - _clearedMessageAt < 3f)
        {
            GUI.Label(new Rect(0, y, w, 18), _clearedMessage, _lblDim);
            y += 20;
        }

        y += 10;
        y = DrawSection(w, y, "CONTROLS — click, then press a key");
        y = DrawKeyBindingRows(w, y);

        GUI.EndScrollView();
    }

    private float DrawSection(float w, float y, string title)
    {
        GUI.Label(new Rect(0, y, w, 20), title, _lblSection);
        y += 22;
        MDT_UITheme.DrawDivider(0, y, w);
        return y + 10;
    }

    private float DrawToggleRow(float w, float y, string label, bool value, System.Action<bool> setter)
    {
        var row = new Rect(0, y, w, 30);
        GUI.Label(new Rect(row.x, row.y, w - 90, row.height), label, _lblBody);
        if (GUI.Button(new Rect(row.xMax - 80, row.y, 80, row.height), value ? "ON" : "OFF", value ? _btnToggleOn : _btnToggleOff))
            setter(!value);
        return y + 34;
    }

    private float DrawUiScaleRow(float w, float y)
    {
        var row = new Rect(0, y, w, 30);
        GUI.Label(new Rect(row.x, row.y, 130, row.height), "UI scale", _lblBody);
        string label = SettingsData.UiScaleOverride <= 0f ? "Auto" : SettingsData.UiScaleOverride.ToString("0.00") + "x";
        if (GUI.Button(new Rect(row.x + 134, row.y, 30, row.height), "◀", _btnSecond))
            SettingsData.UiScaleOverride = Mathf.Clamp(SettingsData.UiScaleOverride <= 0f ? 1.2f : SettingsData.UiScaleOverride - 0.1f, 0f, MDT_UITheme.MaxUIScale);
        GUI.Label(new Rect(row.x + 168, row.y, 70, row.height), label, _lblBody);
        if (GUI.Button(new Rect(row.x + 240, row.y, 30, row.height), "▶", _btnSecond))
            SettingsData.UiScaleOverride = Mathf.Clamp((SettingsData.UiScaleOverride <= 0f ? 0.9f : SettingsData.UiScaleOverride) + 0.1f, MDT_UITheme.MinUIScale, MDT_UITheme.MaxUIScale);
        if (GUI.Button(new Rect(row.xMax - 70, row.y, 70, row.height), "Auto", _btnSecond))
            SettingsData.UiScaleOverride = 0f;
        return y + 34;
    }

    private float DrawQualityRow(float w, float y)
    {
        var row = new Rect(0, y, w, 30);
        GUI.Label(new Rect(row.x, row.y, 130, row.height), "Graphics quality", _lblBody);
        string[] names = QualitySettings.names;
        string current = SettingsData.QualityLevel >= 0 && SettingsData.QualityLevel < names.Length
            ? names[SettingsData.QualityLevel] : "Default";
        if (GUI.Button(new Rect(row.x + 134, row.y, 30, row.height), "◀", _btnSecond) && names.Length > 0)
            ApplyQuality(Mathf.Max(0, (SettingsData.QualityLevel < 0 ? QualitySettings.GetQualityLevel() : SettingsData.QualityLevel) - 1));
        GUI.Label(new Rect(row.x + 168, row.y, 130, row.height), current, _lblBody);
        if (GUI.Button(new Rect(row.x + 300, row.y, 30, row.height), "▶", _btnSecond) && names.Length > 0)
            ApplyQuality(Mathf.Min(names.Length - 1, (SettingsData.QualityLevel < 0 ? QualitySettings.GetQualityLevel() : SettingsData.QualityLevel) + 1));
        return y + 34;
    }

    private static void ApplyQuality(int level)
    {
        SettingsData.QualityLevel = level;
        QualitySettings.SetQualityLevel(level, true);
    }

    private float DrawKeyBindingRows(float w, float y)
    {
        if (KeyBindings.Instance == null)
        {
            GUI.Label(new Rect(0, y, w, 18), "No KeyBindings component in this scene.", _lblDim);
            return y + 20;
        }

        foreach (var field in typeof(KeyBindData).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType != typeof(KeyCode)) continue;
            var row = new Rect(0, y, w, 26);
            GUI.Label(new Rect(row.x, row.y, w - 110, row.height), field.Name, _lblBody);

            bool waiting = _rebindingField == field.Name;
            var current = (KeyCode)field.GetValue(KeyBindings.Instance.binds);
            string keyLabel = waiting ? "Press a key…" : current.ToString();
            if (GUI.Button(new Rect(row.xMax - 105, row.y, 80, row.height), keyLabel, waiting ? _btnToggleOn : _btnSecond))
                _rebindingField = waiting ? null : field.Name;
            if (GUI.Button(new Rect(row.xMax - 22, row.y, 22, row.height), "↺", _btnSecond))
                KeyBindings.Instance.ResetBindingToDefault(field.Name);

            y += 28;
        }
        return y;
    }

    /// <summary>While a binding is armed for rebind, the next real keypress
    /// (any Event.current.isKey, not Input.GetKeyDown -- OnGUI sees the raw
    /// event stream a frame ahead of Input's polled state) claims it. Escape
    /// cancels without changing anything.</summary>
    private void CaptureRebindKey()
    {
        if (_rebindingField == null) return;
        var e = Event.current;
        if (e == null || e.type != EventType.KeyDown) return;

        if (e.keyCode == KeyCode.Escape) { _rebindingField = null; e.Use(); return; }
        if (e.keyCode == KeyCode.None) return;

        KeyBindings.Instance?.SetBinding(_rebindingField, e.keyCode);
        _rebindingField = null;
        e.Use();
    }

    private void HandleDrag(Rect titleBar)
    {
        var e = Event.current;
        if (e.type == EventType.MouseDown && titleBar.Contains(e.mousePosition))
        {
            _dragging = true;
            _dragOffset = e.mousePosition - new Vector2(_windowRect.x, _windowRect.y);
        }
        else if (e.type == EventType.MouseUp)
        {
            _dragging = false;
        }
        else if (e.type == EventType.MouseDrag && _dragging)
        {
            _windowRect.x = e.mousePosition.x - _dragOffset.x;
            _windowRect.y = e.mousePosition.y - _dragOffset.y;
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0, Screen.width - _windowRect.width);
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0, Screen.height - _windowRect.height);
        }
    }
}
