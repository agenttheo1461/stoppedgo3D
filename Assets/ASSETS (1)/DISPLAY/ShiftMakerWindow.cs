using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  SHIFT MAKER WINDOW
//
//  A small draggable planner (PC mouse-drag or mobile touch-drag, same OnGUI
//  event handling either way) where the player builds their own multi-day
//  plan -- route, direction, which day (0 = the day it's consulted on, +1,
//  +2, ... beyond), and a flexible time-of-day window rather than one exact
//  time -- saved locally via ShiftMakerData so it's available on future
//  playthroughs. MainMenu surfaces resolved entries alongside its normal
//  auto-picked routes (see MainMenu.AppendResolvedCustomEntries); this
//  window only edits the plan, it doesn't claim anything itself.
// ═══════════════════════════════════════════════════════════════════════════════
public class ShiftMakerWindow : MonoBehaviour
{
    public static ShiftMakerWindow Instance { get; private set; }

    private bool _open;
    public bool IsOpen => _open;

    private Rect _windowRect = new Rect(140, 100, 380, 520);
    private bool _dragging;
    private Vector2 _dragOffset;
    private Vector2 _scroll;

    // ── New-entry builder state ─────────────────────────────────────────────
    private int _routeIndex = 0;
    private bool _outbound = true;
    private int _dayOffset = 0;
    private string _timeFromInput = "07:00";
    private string _timeToInput = "09:00";

    private bool _stylesReady;
    private GUIStyle _lblTitle, _lblDim, _lblBody, _btnPrimary, _btnSecond, _btnDanger, _textField;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("ShiftMakerWindow").AddComponent<ShiftMakerWindow>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    public void Toggle() => _open = !_open;
    public void Open()  => _open = true;
    public void Close() => _open = false;

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle   = MDT_UITheme.MakeLabel(16, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblDim     = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _lblBody    = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _btnPrimary = MDT_UITheme.MakeButton(new Color(0.05f, 0.30f, 0.14f, 1f), MDT_UITheme.TextGreen, 13, FontStyle.Bold);
        _btnSecond  = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 12);
        _btnDanger  = MDT_UITheme.MakeButton(new Color(0.30f, 0.06f, 0.06f, 1f), MDT_UITheme.TextRed, 12);
        _textField  = new GUIStyle(GUI.skin.textField) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
    }

    private void OnGUI()
    {
        if (!_open) return;
        EnsureStyles();

        MDT_UITheme.DrawSoftShadow(_windowRect, 18f);
        MDT_UITheme.DrawRoundedRectBordered(_windowRect, 14f, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        var titleBar = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, 34);
        HandleDrag(titleBar);

        GUI.Label(new Rect(titleBar.x + 14, titleBar.y, titleBar.width - 60, titleBar.height), "PLAN MY DAY", _lblTitle);
        if (GUI.Button(new Rect(titleBar.xMax - 34, titleBar.y + 5, 24, 24), "✕", _btnSecond))
            _open = false;

        float x = _windowRect.x + 14, y = _windowRect.y + 44, w = _windowRect.width - 28;

        GUI.Label(new Rect(x, y, w, 18), "Your saved entries — resolved against whatever's actually running when you open the Main Menu.", _lblDim);
        y += 24;

        var listArea = new Rect(x, y, w, 150);
        var entries = ShiftMakerData.Instance != null ? ShiftMakerData.Instance.Entries : null;
        int count = entries?.Count ?? 0;
        float rowH = 30f;
        _scroll = GUI.BeginScrollView(listArea, _scroll, new Rect(0, 0, w - 20, Mathf.Max(listArea.height, count * rowH)));
        for (int i = 0; i < count; i++)
        {
            var e = entries[i];
            var row = new Rect(0, i * rowH, w - 24, rowH - 4);
            MDT_UITheme.DrawRoundedRect(row, 8f, MDT_UITheme.BGRowEven);
            GUI.Label(new Rect(row.x + 8, row.y, row.width - 90, row.height), $"{e.RouteLabel}  {e.DirLabel}  ·  {e.DayLabel}  ·  {e.TimeLabel}", _lblBody);
            if (GUI.Button(new Rect(row.xMax - 76, row.y + 3, 70, row.height - 6), "Remove", _btnDanger))
            {
                ShiftMakerData.Instance?.RemoveEntry(i);
                break; // list mutated -- redraw next frame
            }
        }
        GUI.EndScrollView();
        y += listArea.height + 12;

        MDT_UITheme.DrawDivider(x, y, w);
        y += 14;

        GUI.Label(new Rect(x, y, w, 18), "ADD A TRIP", _lblDim);
        y += 22;

        var routes = BusScheduler.Instance != null ? BusScheduler.Instance.managedRoutes : null;
        int routeCount = routes?.Length ?? 0;
        string routeLabel = routeCount > 0 ? $"Route {routes[Mathf.Clamp(_routeIndex, 0, routeCount - 1)].routeNumber}" : "No routes loaded";

        var pickerRow = new Rect(x, y, w, 30);
        if (GUI.Button(new Rect(pickerRow.x, pickerRow.y, 30, pickerRow.height), "◀", _btnSecond) && routeCount > 0)
            _routeIndex = (_routeIndex - 1 + routeCount) % routeCount;
        GUI.Label(new Rect(pickerRow.x + 34, pickerRow.y, pickerRow.width - 98, pickerRow.height), routeLabel, _lblBody);
        if (GUI.Button(new Rect(pickerRow.xMax - 30, pickerRow.y, 30, pickerRow.height), "▶", _btnSecond) && routeCount > 0)
            _routeIndex = (_routeIndex + 1) % routeCount;
        y += 36;

        var dirRow = new Rect(x, y, w, 28);
        if (GUI.Button(new Rect(dirRow.x, dirRow.y, dirRow.width * 0.5f - 4, dirRow.height), _outbound ? "● A → Z" : "A → Z", _outbound ? _btnPrimary : _btnSecond))
            _outbound = true;
        if (GUI.Button(new Rect(dirRow.x + dirRow.width * 0.5f + 4, dirRow.y, dirRow.width * 0.5f - 4, dirRow.height), !_outbound ? "● Z → A" : "Z → A", !_outbound ? _btnPrimary : _btnSecond))
            _outbound = false;
        y += 34;

        // [ADD] Day picker -- 0 = the day this gets consulted on, +1/+2/... beyond.
        var dayRow = new Rect(x, y, w, 30);
        GUI.Label(new Rect(dayRow.x, dayRow.y, 40, dayRow.height), "Day", _lblDim);
        if (GUI.Button(new Rect(dayRow.x + 44, dayRow.y, 30, dayRow.height), "◀", _btnSecond))
            _dayOffset = Mathf.Max(0, _dayOffset - 1);
        string dayLabel = _dayOffset <= 0 ? "Today" : _dayOffset == 1 ? "+1 day" : $"+{_dayOffset} days";
        GUI.Label(new Rect(dayRow.x + 78, dayRow.y, dayRow.width - 154, dayRow.height), dayLabel, _lblBody);
        if (GUI.Button(new Rect(dayRow.xMax - 30, dayRow.y, 30, dayRow.height), "▶", _btnSecond))
            _dayOffset++;
        y += 36;

        // [ADD] Flexible time WINDOW instead of one exact time -- the nearest
        // real departure inside this range gets matched (see MainMenu.WithinWindow).
        var timeRow = new Rect(x, y, w, 28);
        float halfW = (timeRow.width - 100f) * 0.5f;
        GUI.Label(new Rect(timeRow.x, timeRow.y, 40, timeRow.height), "From", _lblDim);
        _timeFromInput = GUI.TextField(new Rect(timeRow.x + 44, timeRow.y, halfW, timeRow.height), _timeFromInput, 5, _textField);
        GUI.Label(new Rect(timeRow.x + 48 + halfW, timeRow.y, 24, timeRow.height), "to", _lblDim);
        _timeToInput = GUI.TextField(new Rect(timeRow.x + 76 + halfW, timeRow.y, halfW, timeRow.height), _timeToInput, 5, _textField);
        y += 34;

        if (GUI.Button(new Rect(x, y, w, 36), "ADD TO PLAN", _btnPrimary) && routeCount > 0)
        {
            if (TryParseTime(_timeFromInput, out float from) && TryParseTime(_timeToInput, out float to))
            {
                var route = routes[Mathf.Clamp(_routeIndex, 0, routeCount - 1)];
                ShiftMakerData.Instance?.AddEntry(route.routeNumber, "", _outbound, _dayOffset, from, to);
            }
        }
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

    private static bool TryParseTime(string text, out float minutes)
    {
        minutes = 0f;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out int h) || !int.TryParse(parts[1], out int m)) return false;
        if (h < 0 || h > 23 || m < 0 || m > 59) return false;
        minutes = h * 60f + m;
        return true;
    }
}
