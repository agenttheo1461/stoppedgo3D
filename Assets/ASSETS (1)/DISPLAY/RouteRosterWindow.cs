using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE ROSTER WINDOW
//
//  Browse every managed route and favorite the ones you'd like to be offered
//  more readily. A favorited route always shows in MainMenu's route list
//  (regardless of whether it made the "soonest 8" cut) and, when clicked
//  there, offers an A→Z / Z→A / later chooser instead of jumping straight
//  into bus selection. FavoriteRouteAvailabilityNotifier also periodically
//  nudges about one that's ready to go, if that setting is on.
//
//  Opened from Settings ▸ ROUTES ▸ "Manage favorite routes" -- a second
//  window layered on top of Settings, same pattern as BusRosterWindow.
// ═══════════════════════════════════════════════════════════════════════════════
public class RouteRosterWindow : MonoBehaviour
{
    public static RouteRosterWindow Instance { get; private set; }

    private bool _open;
    public bool IsOpen => _open;

    private Rect _windowRect = new Rect(180, 100, 400, 480);
    private bool _dragging;
    private Vector2 _dragOffset;
    private Vector2 _scroll;

    private bool _stylesReady;
    private GUIStyle _lblTitle, _lblDim, _lblBody, _btnSecond, _starOn, _starOff;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("RouteRosterWindow").AddComponent<RouteRosterWindow>();
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
        _lblTitle  = MDT_UITheme.MakeLabel(16, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblDim    = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _lblBody   = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _btnSecond = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 12);
        _starOn    = MDT_UITheme.MakeButton(new Color(0.35f, 0.28f, 0.03f, 1f), MDT_UITheme.TextAmber, 16, FontStyle.Bold);
        _starOff   = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextDim, 16);
    }

    private void OnGUI()
    {
        if (!_open) return;
        EnsureStyles();

        MDT_UITheme.DrawSoftShadow(_windowRect, 18f);
        MDT_UITheme.DrawRoundedRectBordered(_windowRect, 14f, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        var titleBar = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, 34);
        HandleDrag(titleBar);
        GUI.Label(new Rect(titleBar.x + 14, titleBar.y, titleBar.width - 60, titleBar.height), "MY ROUTES", _lblTitle);
        if (GUI.Button(new Rect(titleBar.xMax - 34, titleBar.y + 5, 24, 24), "✕", _btnSecond))
            _open = false;

        float x = _windowRect.x + 14, y = _windowRect.y + 44, w = _windowRect.width - 28;

        GUI.Label(new Rect(x, y, w, 32), "Favorite a route to always see it in the Main Menu list, with quick A→Z / Z→A picks -- and be a candidate for the occasional \"available\" nudge.", _lblDim);
        y += 44;

        var routes = BusScheduler.Instance != null && BusScheduler.Instance.managedRoutes != null
            ? BusScheduler.Instance.managedRoutes.Where(r => r != null)
                .OrderBy(r => RouteSortKey(r.routeNumber)).ToList()
            : null;
        int count = routes?.Count ?? 0;

        var listArea = new Rect(x, y, w, _windowRect.height - (y - _windowRect.y) - 14);
        float rowH = 42f;
        _scroll = GUI.BeginScrollView(listArea, _scroll, new Rect(0, 0, w - 20, Mathf.Max(listArea.height, count * rowH)));
        for (int i = 0; i < count; i++)
        {
            var route = routes[i];
            var row = new Rect(0, i * rowH, w - 24, rowH - 6);
            MDT_UITheme.DrawRoundedRect(row, 8f, MDT_UITheme.BGRowEven);

            bool fav = FavoriteRouteData.Instance != null && FavoriteRouteData.Instance.IsFavorite(route.routeNumber);
            if (GUI.Button(new Rect(row.x + 6, row.y + 3, 34, row.height - 6), fav ? "★" : "☆", fav ? _starOn : _starOff))
                FavoriteRouteData.Instance?.Toggle(route.routeNumber);

            var col = route.routeColor; col.a = 1f;
            GUI.color = col;
            GUI.Label(new Rect(row.x + 48, row.y + 8, 10, 18), "●", _lblBody);
            GUI.color = Color.white;
            GUI.Label(new Rect(row.x + 62, row.y + 8, row.width - 74, 18), $"Route {route.routeNumber}  ·  {route.routeName}", _lblBody);
        }
        GUI.EndScrollView();
    }

    /// <summary>Numeric routes sort numerically (1 before 10); anything non-numeric falls back to string order after them.</summary>
    private static int RouteSortKey(string routeNumber) =>
        int.TryParse(routeNumber, out int n) ? n : int.MaxValue;

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
