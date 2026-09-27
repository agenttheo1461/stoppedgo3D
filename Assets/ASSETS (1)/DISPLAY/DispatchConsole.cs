using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  DISPATCH CONSOLE  —  top-down fleet operations control
//
//  Live roster of every active bus (BusRegistry), with real commands wired
//  straight into BusScheduler / NPCBusController / BusSelectMenu's existing
//  possession pipeline. Doesn't touch the dwell systems at all — read-only
//  on bus state except for the three explicit commands below.
//
//    TAB — open/close
// ═══════════════════════════════════════════════════════════════════════════════
public class DispatchConsole : MonoBehaviour
{
    public static DispatchConsole Instance { get; private set; }

    [Header("Toggle")]
    public KeyCode toggleKey => KeyBindings.Current.dispatchConsole;
    public bool canPossess = true;
    [Header("Panel")]
    [Range(600, 1100)] public int panelW = 860;
    [Range(400, 800)]  public int panelH = 600;

    [Header("Refresh")]
    public float refreshInterval = 1f;


    private bool  _open;
    private float _panelX, _panelY;
    private float _refreshTimer;

    /// <summary>[ADD Bug 38 fix] So MainMenu's full-reset-on-open can force this closed.</summary>
    public void Close() => _open = false;

    private enum SortMode { Route, Lateness, FleetNumber }
    private SortMode _sortMode = SortMode.Route;

    private string  _routeFilter = "";
    private Vector2 _scroll;

    private readonly List<int> _rosterBusIDs = new();
    private int _selectedBusID = -1;

    private bool      _stylesReady = false;
    private GUIStyle  _lblTitle, _lblSub, _lblDim, _lblCyan, _lblBody, _lblAmber, _lblGreen;
    private GUIStyle  _btnPrimary, _btnSecond;

    // ═════════════════════════════════════════════════════════════════════════
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            _open = !_open;
            if (_open)
            {
                _panelX = (Screen.width  - panelW) * 0.5f;
                _panelY = (Screen.height - panelH) * 0.5f;
                RefreshRoster();
            }
        }

        if (!_open) return;

        _refreshTimer -= Time.deltaTime;
        if (_refreshTimer <= 0f) { _refreshTimer = refreshInterval; RefreshRoster(); }
    }

    // ── Roster ────────────────────────────────────────────────────────────────
    private void RefreshRoster()
    {
        _rosterBusIDs.Clear();
        foreach (var kv in BusRegistry.ActiveBuses)
        {
            if (kv.Value == null) continue;
            if (!string.IsNullOrEmpty(_routeFilter)
                && (kv.Value.CurrentRoute == null
                    || !kv.Value.CurrentRoute.routeNumber.Contains(_routeFilter)))
                continue;
            _rosterBusIDs.Add(kv.Key);
        }

        switch (_sortMode)
        {
            case SortMode.Route:
                _rosterBusIDs.Sort((a, b) => string.Compare(
                    BusRegistry.ActiveBuses[a].CurrentRoute?.routeNumber ?? "",
                    BusRegistry.ActiveBuses[b].CurrentRoute?.routeNumber ?? "",
                    System.StringComparison.Ordinal));
                break;
            case SortMode.Lateness:
                _rosterBusIDs.Sort((a, b) =>
                    (BusScheduler.Instance?.GetLatenessMinutes(b) ?? 0f)
                    .CompareTo(BusScheduler.Instance?.GetLatenessMinutes(a) ?? 0f));
                break;
            case SortMode.FleetNumber:
                _rosterBusIDs.Sort((a, b) =>
                    BusRegistry.ActiveBuses[a].fleetNumber.CompareTo(BusRegistry.ActiveBuses[b].fleetNumber));
                break;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GUI
    // ═════════════════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        if (!_open) return;
        EnsureStyles();

        GUI.color = new Color(0, 0, 0, 0.7f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        var pr = new Rect(_panelX, _panelY, panelW, panelH);
        MDT_UITheme.DrawPanel(pr);
        GUI.BeginGroup(pr);

        MDT_UITheme.DrawHeader(new Rect(0, 0, panelW, 40f));
        GUI.Label(new Rect(14, 0, panelW - 80, 40), "DISPATCH CONSOLE", _lblTitle);
        if (GUI.Button(new Rect(panelW - 46, 8, 34, 24), "✕", _btnSecond)) _open = false;

        DrawFilterBar(new Rect(8, 46, panelW - 16, 28));

        float listW = panelW * 0.42f - 12f;
        float listH = panelH - 86f;
        GUI.BeginGroup(new Rect(8, 80, listW, listH));
        DrawRoster(listW, listH);
        GUI.EndGroup();

        float detailX = 16f + listW;
        float detailW = panelW - detailX - 8f;
        GUI.BeginGroup(new Rect(detailX, 80, detailW, listH));
        DrawDetailPanel(detailW, listH);
        GUI.EndGroup();

        GUI.EndGroup();
    }

    private void DrawFilterBar(Rect r)
    {
        GUI.Label(new Rect(r.x, r.y, 50, r.height), "Route:", _lblDim);
        _routeFilter = GUI.TextField(new Rect(r.x + 54, r.y + 2, 100, 22), _routeFilter);

        float bx = r.x + 168;
        foreach (SortMode mode in System.Enum.GetValues(typeof(SortMode)))
        {
            bool active = _sortMode == mode;
            if (GUI.Button(new Rect(bx, r.y, 90, 24), mode.ToString(), active ? _btnPrimary : _btnSecond))
            { _sortMode = mode; RefreshRoster(); }
            bx += 94;
        }

        GUI.Label(new Rect(r.width - 90, r.y, 90, r.height), $"{_rosterBusIDs.Count} active", _lblDim);
    }

    private void DrawRoster(float w, float h)
    {
        float rowH = 30f;
        _scroll = GUI.BeginScrollView(new Rect(0, 0, w, h), _scroll,
            new Rect(0, 0, w - 16f, _rosterBusIDs.Count * rowH));

        for (int i = 0; i < _rosterBusIDs.Count; i++)
        {
            int busID = _rosterBusIDs[i];
            if (!BusRegistry.ActiveBuses.TryGetValue(busID, out var bus) || bus == null) continue;

            bool sel = _selectedBusID == busID;
            var  r   = new Rect(0, i * rowH, w - 16f, rowH - 2f);

            float lateness = BusScheduler.Instance?.GetLatenessMinutes(busID) ?? 0f;
            Color rowTint  = lateness > 3f ? new Color(0.4f, 0.12f, 0.12f, 1f)
                            : sel          ? MDT_UITheme.BGDirSel
                                           : MDT_UITheme.BGPill;
            MDT_UITheme.DrawRoundedRect(r, MDT_UITheme.RadiusRow, rowTint);

            if (GUI.Button(r, "", GUIStyle.none)) _selectedBusID = busID;

            string routeLabel = bus.CurrentRoute != null
                ? $"{bus.CurrentRoute.routeNumber}{bus.variantLetter}"
                : "—";
            string lateStr = lateness > 0.5f ? $"+{lateness:0.0}m" : "on time";

            GUI.Label(new Rect(r.x + 6, r.y, 70, r.height), $"#{bus.fleetNumber}", sel ? _lblCyan : _lblBody);
            GUI.Label(new Rect(r.x + 80, r.y, 60, r.height), routeLabel, _lblDim);
            GUI.Label(new Rect(r.x + 150, r.y, 80, r.height), bus.State.ToString(), _lblDim);
            GUI.Label(new Rect(r.width - 70, r.y, 70, r.height), lateStr, lateness > 3f ? _lblAmber : _lblDim);
        }
        GUI.EndScrollView();
    }

    private void DrawDetailPanel(float w, float h)
    {
        if (_selectedBusID < 0
            || !BusRegistry.ActiveBuses.TryGetValue(_selectedBusID, out var bus)
            || bus == null)
        {
            GUI.Label(new Rect(0, 0, w, 24), "Select a bus from the roster.", _lblDim);
            return;
        }

        float y = 0f;
        GUI.Label(new Rect(0, y, w, 22), $"Fleet #{bus.fleetNumber}", _lblTitle); y += 26f;
        GUI.Label(new Rect(0, y, w, 18),
            $"Route {bus.CurrentRoute?.routeNumber}{bus.variantLetter} · {(bus.IsOutbound ? "A→Z" : "Z→A")}",
            _lblDim); y += 20f;
        GUI.Label(new Rect(0, y, w, 18), $"State: {bus.State}", _lblDim); y += 20f;

        float lateness = BusScheduler.Instance?.GetLatenessMinutes(_selectedBusID) ?? 0f;
        GUI.Label(new Rect(0, y, w, 18),
            lateness > 0.5f ? $"Running {lateness:0.0} min late" : "On schedule",
            lateness > 3f ? _lblAmber : _lblGreen); y += 20f;

        GUI.Label(new Rect(0, y, w, 18), $"Next stop index: {bus.NextStopIndex}", _lblDim); y += 20f;
        GUI.Label(new Rect(0, y, w, 18),
            $"Pending depot return: {(bus.IsPendingDepotReturn ? "yes" : "no")}", _lblDim); y += 28f;

        GUI.Label(new Rect(0, y, w, 18), "COMMANDS", _lblSub); y += 22f;

        if (GUI.Button(new Rect(0, y, w, 30), "🅿 Recall to Depot", _btnSecond))
        {
            bus.FlagForDepotReturn();
            Debug.Log($"[Dispatch] Recall flagged — Bus#{bus.busID} returns to depot after this leg.");
        }
        y += 34f;

if (GUI.Button(new Rect(0, y, w, 30), "↩ Cancel Recall", _btnSecond))
{
    bus.ClearDepotReturnFlag();
    Debug.Log($"[Dispatch] Recall cancelled — Bus#{bus.busID} stays in service.");
}
        y += 38f;

        GUI.enabled = canPossess;
        if (GUI.Button(new Rect(0, y, w, 34), "🚌 Take the Wheel", _btnPrimary))
        {
            BusSelectMenu.Instance.PossessFleetNumber(bus.fleetNumber);
            _open = false;
        }
        GUI.enabled = true;
        y += 36f;

    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STYLES
    // ═════════════════════════════════════════════════════════════════════════
    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle   = MDT_UITheme.MakeLabel(15, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblSub     = MDT_UITheme.MakeLabel(10, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextAmber);
        _lblDim     = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _lblCyan    = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblBody    = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _lblAmber   = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextAmber);
        _lblGreen   = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextGreen);
        _btnPrimary = MDT_UITheme.MakeButton(new Color(0.05f, 0.30f, 0.14f, 1f), MDT_UITheme.TextGreen, 11, FontStyle.Bold);
        _btnSecond  = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 10);
        MDT_UITheme.SetBgRounded(_btnPrimary, new Color(0.05f, 0.30f, 0.14f, 1f),
            new Color(0.05f, 0.30f, 0.14f, 1f) * 1.3f, new Color(0.05f, 0.30f, 0.14f, 1f) * 0.7f, 160, 34, MDT_UITheme.RadiusRow);
        MDT_UITheme.SetBgRounded(_btnSecond, MDT_UITheme.BGButton,
            MDT_UITheme.BGButton * 1.3f, MDT_UITheme.BGButton * 0.7f, 90, 24, MDT_UITheme.RadiusRow);
    }
}