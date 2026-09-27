using UnityEngine;
using System.Collections.Generic;

// ═════════════════════════════════════════════════════════════════════════════
//  MDT_LiveMap  —  v6.2
//
//  CHANGES vs v6.1
//  ─────────────
//  · FIX: five call sites depended on PlayerShiftDirector.Phase /
//    BusSchedulerPlayerService.CurrentLeg — classes from a from-scratch
//    shift-system rebuild that was reverted. Rewired onto the real, current
//    owner of shift state (PlayerHandoff) + BusScheduler for the actual
//    TimetableSlot, via one shared helper (GetPlayerCurrentLeg) instead of
//    duplicating the lookup five times.
//
//  CHANGES vs v5
//  ─────────────
//  [MAP-7]  NEW — Road junction visualization + inbound/outbound direction filter.
//           Shows junction points where road segments meet (pulsing indicators).
//           Direction arrows drawn along active route lines showing flow direction.
//           New [D] key cycles direction filter: Both → Outbound Only → Inbound Only.
//           Filters both routes AND bus chips by direction.
//           Enhances visibility of complete inbound/outbound service network.
// ─────────────────────────────────────────────────────────────────────────────
//  CHANGES vs v4
//  ─────────────
//  [MAP-1]..[MAP-5]  Unchanged — see prior version history below.
//
//  [MAP-6]  Stop-click popup.
//           Clicking a stop dot opens a floating panel showing next arrivals
//           for EVERY route serving that stop, grouped by REAL compass
//           direction (NORTH/SOUTH/EAST/WEST) rather than each route's own
//           outbound/inbound labeling, which isn't consistent direction-to-
//           direction across routes. A direction with zero service is
//           omitted entirely rather than shown empty. Click the same stop
//           again (or click elsewhere) to close it — same toggle behavior
//           as the bus chip ETA popup, and clicking either popup type
//           closes the other.
//           Backed by BusTrackerService.GetArrivalsForStopByCompass().
// ─────────────────────────────────────────────────────────────────────────────
//  CHANGES vs v3 (carried forward)
//  ─────────────
//  [MAP-1]  Route lines now follow node paths from BusRouteData.
//           Each route visualizes its outboundNodes and inboundNodes directly
//           as Bezier curves and straight segments. No longer traces stops.
//           SnapshotWorld samples each segment into a polyline for rendering.
//
//  [MAP-2]  Follow-player lock toggle (button or [F] key).
//           When locked, WorldToScreen always centres on the player bus.
//           Pan is disabled while locked; unlock by dragging or pressing [F].
//
//  [MAP-3]  Route filter.
//           Button strip or [R] key cycles: All Routes → Player Route Only →
//           each individual managed route. Filtered routes draw full opacity;
//           others are hidden or drawn at 10% opacity.
//
//  [MAP-4]  Bus chip click → ETA popup.
//           Clicking a chip opens a small floating panel showing the next 3
//           arrivals from BusTrackerService for the bus's current next stop.
//           Panel closes on click-away or another chip click.
//
//  [MAP-5]  Search bar (top-right of map area).
//           Type a fleet number → map jumps to that bus and locks follow.
//           Type a route number → opens a list of active buses on that route;
//           clicking one locks follow on it.
//           Clear search to return to normal mode.
// ═════════════════════════════════════════════════════════════════════════════

// ═════════════════════════════════════════════════════════════════════════════
//  MAP AREA LABEL — a named neighborhood/district/landmark shown as a soft
//  background label on the live map. Purely cosmetic/orientation; no
//  gameplay effect. See areaLabelPickerMode on MDT_LiveMap for a quick way
//  to grab world coordinates for a new one.
// ═════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class MapAreaLabel
{
    public string  areaName = "New Area";
    public Vector3 worldPosition;

    [Tooltip("Base font size at zoom = 1 — scales with zoom so labels stay roughly the same apparent size when you zoom in/out.")]
    public int baseFontSize = 13;

    [Tooltip("Label only shows when zoomed OUT to at least this level (0 = always). Useful for big-picture names (e.g. a whole district) you don't want cluttering a zoomed-in view.")]
    public float minZoomToShow = 0f;

    [Tooltip("Label only shows when zoomed IN to at least this level (0 = always). Useful for small/local names that only make sense zoomed in.")]
    public float maxZoomToShow = 999f;

    [Tooltip("Base tint for this area's chip — the chip border/body are derived from this colour (lightened for the border, darkened for the body), same visual language as a bus chip's routeColor. Chip text itself is always plain white for readability.")]
    public Color labelColor = new Color(0.85f, 0.90f, 0.95f, 0.95f);
}

public class MDT_LiveMap : MonoBehaviour
{
    // ── QoL: persistent view settings ────────────────────────────────────────
private const string PrefZoom      = "MDTMap_Zoom";
private const string PrefRouteIdx  = "MDTMap_RouteFilter";
private const string PrefDirFilter = "MDTMap_DirFilter";
private const string PrefFollow    = "MDTMap_Follow";
private const string PrefFavStops  = "MDTMap_FavStops";

// ── QoL: bus breadcrumb trails ───────────────────────────────────────────
private const float TRAIL_SAMPLE_INTERVAL = 0.5f;
private const int   TRAIL_MAX_POINTS      = 10;
private float _trailSampleTimer = 0f;
private readonly Dictionary<int, List<Vector3>> _busTrails = new Dictionary<int, List<Vector3>>();

// ── QoL: terminal congestion ─────────────────────────────────────────────
private const float TERMINAL_CONGESTION_RADIUS = 70f;

// ── QoL: favorite stops ──────────────────────────────────────────────────
private readonly HashSet<string> _favoriteStops = new HashSet<string>();

// ── QoL: recent buses ─────────────────────────────────────────────────────
private readonly List<int> _recentFleetNumbers = new List<int>(6);
private const int RECENT_BUS_CAP = 5;
    public static MDT_LiveMap Instance;

    private enum ViewMode { Map, List, Both }
    private ViewMode _viewMode = ViewMode.Map;

    [Header("Docked List (Map/List/Both merge)")]
    [Tooltip("Auto-found via GetComponent/FindObjectOfType if left empty.")]
    public MDT_UI_Controller listController;
    [Range(220, 420)] public int listDockWidth = 300;

    private readonly List<RoadEvent> _activeEvents = new List<RoadEvent>();

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Display")]
    public KeyCode toggleKey => KeyBindings.Current.liveMap;
    public int     mapWidth          = 560;
    public int     mapHeight         = 500;
    public int     screenMarginRight = 20;
    public int     screenMarginTop   = 20;

    [Tooltip("Base world-unit radius visible when zoom = 1.")]
    public float viewRadius = 700f;

    [Tooltip("Highlight this route; null = auto from player's active route.")]
    public BusRouteData targetRoute;

    [Header("Refresh")]
    public float refreshInterval = 6f;

    [Header("Zoom")]
    public float zoomMin  = 0.3f;
    public float zoomMax  = 5f;
    public float zoomStep = 0.15f;

    [Header("Route Spline Sampling")]
    [Tooltip("Samples per road segment when building route polylines.")]
    public int splineSamplesPerSegment = 20;

    [Header("Area / Neighborhood Names")]
    [Tooltip("Named areas drawn as soft background labels on the map (neighborhoods, districts, retirement communities, etc.) — purely cosmetic/orientation, no gameplay effect.")]
    public List<MapAreaLabel> areaLabels = new List<MapAreaLabel>();

    [Tooltip("While on, clicking anywhere on the map logs a ready-to-paste MapAreaLabel entry (with the clicked world position) to the Console instead of doing the normal click behavior — a quick way to grab coordinates for a new area label without doing the math by hand. Turn off when done.")]
    public bool areaLabelPickerMode = false;

    // ── Runtime state ─────────────────────────────────────────────────────────
    private bool  _visible      = false;

    /// <summary>[07-30] Public read-only view of _visible, added so other
    /// panels bound to the same raw Escape key (ShiftBoardMenu) can check
    /// "is the live map currently the thing Escape should act on" before
    /// acting on their own Escape press this frame. Without this, closing
    /// the live map and opening/toggling the shift board could both fire
    /// on the exact same keypress — see ShiftBoardMenu.Update().</summary>
    public bool IsVisible => _visible;

    /// <summary>[ADD Bug 38 fix] So MainMenu's full-reset-on-open can force this closed.</summary>
    public void Close() => _visible = false;

    /// <summary>[MOBILE] Same body the keyboard toggleKey handler used to
    /// run inline — pulled out so a touch "MAP" button can drive the exact
    /// open/cycle-view behavior instead of re-implementing it.</summary>
    public void HandleTogglePressed()
    {
        if (!_visible)
        {
            _visible  = true;
            _viewMode = ViewMode.Map;
            _dataDirty = true;
            LoadMapPrefs();
        }
        else if (_viewMode == ViewMode.Both)
        {
            // 4th press: close (there's no X button, so the view loop ends by toggling off).
            _visible  = false;
            _viewMode = ViewMode.Map;
            SaveMapPrefs();
        }
        else
        {
            _viewMode = _viewMode switch
            {
                ViewMode.Map  => ViewMode.List,
                ViewMode.List => ViewMode.Both,
                _             => ViewMode.Map,
            };
            if (_viewMode == ViewMode.Map || _viewMode == ViewMode.Both) _dataDirty = true;
        }
    }

    private float _refreshTimer = 0f;
    private bool  _dataDirty    = true;

    private Vector2 _panOffset  = Vector2.zero;
    private float   _zoom       = 1f;

    // Drag
    private bool    _dragging      = false;
    private Vector2 _dragStart     = Vector2.zero;
    private Vector2 _panAtDrag     = Vector2.zero;
    private float   _lastClickTime = -1f;

    // [MAP-2] Follow-player lock
    private bool _followPlayer = true;
    private int  _followBusID  = -1;   // -1 = follow player, >=0 = follow specific NPC fleet#

    // [MAP-3] Route filter
    // -2 = all routes, -1 = player route only, 0+ = index into managedRoutes
    private int _routeFilterIndex = -2;

    // [MAP-7] Direction filter: -1 = both, 0 = outbound only, 1 = inbound only
    private int _directionFilter = -1;

    // [MAP-7] Road junction visualization
    private readonly List<Vector3> _roadJunctions = new List<Vector3>(256);

    // [MAP-4] Chip click ETA popup
    private int     _etaPopupFleet   = -1;
    private Vector2 _etaPopupScreenPos;
    private List<string> _etaPopupArrivals = new List<string>();
    private string  _etaPopupNextStop = "";

    // [MAP-6] Stop click popup — all routes, both directions
    // [REWRITE] Arrivals for a clicked stop no longer render as a floating
    // popup on the map — they're forwarded to the docked tracker panel
    // (listController.ShowStopBoard) instead. Only the selected stop's code
    // is kept here, purely to drive the highlight ring on the map itself.
    private string _selectedStopCode = "";

    // [MAP-5] Search
    private string _searchText          = "";
    private bool   _searchFocused       = false;
    private List<BusChip> _searchResults = new List<BusChip>();
    private bool   _showSearchResults   = false;
    private const string SearchControl  = "MapSearch";

    // ── Snapshot data ─────────────────────────────────────────────────────────
    private struct BusChip
    {
        public Vector3 worldPos;
        public int     fleetNumber;
        public bool    isPlayer;
        public bool    isOutbound;
        public Color   routeColor;
        public string  routeNumber;
        public string  variantLetter;
        public int     busID;

        // [PERF FIX] DrawBusChip was rebuilding these three strings from
        // scratch via concatenation/interpolation on EVERY draw call for
        // EVERY visible chip, every single repaint -- pure garbage
        // generation for values that essentially never change frame to
        // frame (a bus's route number, variant letter, and fleet number
        // don't change mid-trip). Computed once here, at construction, and
        // just read by DrawBusChip instead. See BuildDisplayStrings below.
        public string dirLabelCached;
        public string routeDisplayCached;
        public string fleetNumberCached;

        // [PERF FIX] Same idea as the cached strings above -- DrawBreakdownRings
        // used to re-query BusBreakdownSystem.IsBusLocked(busID) for every
        // active bus, every single Repaint frame, via its own separate full
        // scan of BusRegistry.ActiveBuses (bypassing SnapshotWorld's refresh
        // throttle entirely). Computed once per refresh here instead, so
        // DrawBreakdownRings just reads a bool off the chip it's already
        // iterating.
        public bool isLocked;

        public void BuildDisplayStrings()
        {
            dirLabelCached     = (isOutbound ? "↗ OUB" : "↙ INB");
            routeDisplayCached = string.IsNullOrEmpty(variantLetter) ? routeNumber : routeNumber + variantLetter;
            fleetNumberCached  = fleetNumber.ToString();
        }
    }

    // [MAP-6] Stop dot now carries stopCode (needed for the click lookup — it
    // previously only stored worldPos/name/isTerminal, which was enough for a
    // tooltip but not enough to query BusTrackerService for arrivals).
    private struct StopDot
    {
        public Vector3 worldPos;
        public string  stopCode;
        public string  stopName;
        public bool    isTerminal;
    }

    // Route polyline: list of world-space points traced along road splines between stops
    private struct RoutePolyline
    {
        public Vector3[] pts;
        public Color      col;
        public int        layer;   // 0=city grid, 1=non-HL route, 2=HL route
        // [ADD] World-space XZ bounding box, computed once when this
        // polyline is built (see ComputeBounds2D). Lets DrawMapArea reject
        // an entire off-screen road/route in O(1) BEFORE ever entering its
        // per-point loop, instead of transforming every point just to find
        // out none of them were visible.
        public Vector2 minB;
        public Vector2 maxB;
    }

    private readonly List<RoutePolyline> _roadLines = new List<RoutePolyline>(512);
    private readonly List<StopDot>       _stopDots  = new List<StopDot>(512);

    // [ADD] Memoized sample-point cache -- BuildSegments+SampleSegments (and
    // the per-point spline EvaluatePosition calls for the city grid) were
    // being redone from scratch every single SnapshotWorld call even though
    // roads and route paths are static geometry that never moves. Keyed by
    // a stable string identity per (route, direction, variant) so repeat
    // SnapshotWorld calls reuse the already-sampled Vector3[] instead of
    // resampling -- everything ELSE in the loop (filter visibility,
    // highlight color/layer) still recomputes fresh every call, since that
    // part is cheap and needs to react to live state.
    private readonly Dictionary<string, Vector3[]> _sampleCache = new Dictionary<string, Vector3[]>(256);

    private Vector3[] GetOrSampleCached(string cacheKey, System.Func<List<Vector3>> sampleFunc)
    {
        if (_sampleCache.TryGetValue(cacheKey, out var cached)) return cached;
        var pts = sampleFunc().ToArray();
        _sampleCache[cacheKey] = pts;
        return pts;
    }
    private readonly List<BusChip>       _chips     = new List<BusChip>(512);

    private Vector3 _worldCentre;
    private Vector3 _followTarget;

    // Hover state
    private string   _hoveredStopName = "";
    private BusChip? _hoveredBus      = null;

    // ── GUI styles ────────────────────────────────────────────────────────────
    private GUIStyle _styleHeader;
    private GUIStyle _styleClock;
    private GUIStyle _styleLegend;
    private GUIStyle _styleTooltip;
    private GUIStyle _chipDir;
    private GUIStyle _chipRoute;
    private GUIStyle _chipFleet;
    private GUIStyle _searchStyle;
    private GUIStyle _searchResultStyle;
    private bool     _stylesBuilt = false;

    // ── CityManager ───────────────────────────────────────────────────────────
    private CityManager _city;
    private CityManager City
    {
        get { if (_city == null) _city = FindObjectOfType<CityManager>(); return _city; }
    }

    // ── Constants ─────────────────────────────────────────────────────────────
    private const int   HEADER_H    = 34;
    private const int   LEGEND_H    = 24;
    // [ADD] Items 15/16 -- bus chips are the map's tap target (click opens
    // the ETA popup), scaled + floored via MDT_UITheme same as the other
    // three HUDs. HEADER_H/LEGEND_H are chrome, not tap targets, left as-is.
    private static float CHIP_W     => 48f * MDT_UITheme.UIScale;
    private static float CHIP_H     => 36f * MDT_UITheme.UIScale;

    // ═════════════════════════════════════════════════════════════════════════
    //  PLAYER LEG LOOKUP — single shared helper (FIX)
    //
    //  Every call site that used to read BusSchedulerPlayerService.Instance
    //  ?.CurrentLeg (a class from a from-scratch shift-system rebuild that
    //  was reverted) now goes through this instead: PlayerHandoff is the
    //  real, current owner of the player's identity, and the actual
    //  TimetableSlot is fetched via BusScheduler.TryGetAssignedSlot, the
    //  same lookup pattern used elsewhere in the project (e.g.
    //  PlayerHandoff.HasPendingChainLeg, PlayerDestinationBoard).
    // ═════════════════════════════════════════════════════════════════════════
    private static TimetableSlot GetPlayerCurrentLeg()
    {
        var ph = PlayerHandoff.Instance;
        if (ph == null || BusScheduler.Instance == null) return null;
        BusScheduler.Instance.TryGetAssignedSlot(ph.PlayerBusID, out var leg);
        return leg;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════════

private void Awake()
{
    Instance = this;
    LoadFavoriteStops();
    MDT_FocusBus.OnRequestFollowBus += HandleExternalFollowRequest; // cross-cutting focus sync

    if (listController == null) listController = GetComponent<MDT_UI_Controller>();
    if (listController == null) listController = FindObjectOfType<MDT_UI_Controller>();
    if (listController != null) listController.dockedMode = true;
}

private void OnDestroy()
{
    MDT_FocusBus.OnRequestFollowBus -= HandleExternalFollowRequest;
}

private void HandleExternalFollowRequest(int fleetOrBusID, bool isPlayer)
{
    _followPlayer = true;
    _followBusID  = isPlayer ? -1 : fleetOrBusID;
    foreach (var chip in _chips)
    {
        if ((isPlayer && chip.isPlayer) || (!isPlayer && chip.fleetNumber == fleetOrBusID))
        { _worldCentre = chip.worldPos; break; }
    }
}
[Header("3D Mode")]
public MDT_Live3DCamera live3DCamera;
public RoadMeshRenderer3D roadMeshRenderer3D;
private bool _mode3D = false;
private Vector3 _lastMousePos;
private Vector3? _pickedWorldPoint;
private string _pickedLabel;

private void Toggle3DMode()
{
    _mode3D = !_mode3D;
    if (live3DCamera != null) live3DCamera.SetActive(_mode3D);
}

private void Tick3DInput()
{
    if (live3DCamera == null || !_mode3D) return;

    Vector3 mousePos = Input.mousePosition;
    Vector2 delta = mousePos - _lastMousePos;
    _lastMousePos = mousePos;

    Vector2 leftDrag  = Input.GetMouseButton(0) ? delta : Vector2.zero;
    Vector2 rightDrag = Input.GetMouseButton(1) ? delta : Vector2.zero;
    float scroll = Input.mouseScrollDelta.y;

    live3DCamera.ApplyInput(leftDrag, rightDrag, scroll);
}

private void Update()
{
    _lastMousePos = Input.mousePosition;
    if (_mode3D) Tick3DInput();

    if (!MainMenu.BlocksInput && Input.GetKeyDown(toggleKey)) // [FIX Bug 43]
        HandleTogglePressed();

    if (_visible && Input.GetKeyDown(KeyCode.Escape))
    {
        _visible = false;
        SaveMapPrefs();
    }

    if (!_visible) return;

    if (_viewMode != ViewMode.Map && listController != null)
        listController.TickDocked();

    if (_viewMode == ViewMode.List) return;
/*
    // [MAP-2] Follow lock toggle
    if (Input.GetKeyDown(KeyCode.F))
    {
        _followPlayer = !_followPlayer;
        if (_followPlayer) _followBusID = -1;
    }

    // [MAP-3] Route filter cycle
    if (Input.GetKeyDown(KeyCode.R))
        CycleRouteFilter();

    // [MAP-7] Direction filter cycle (D key)
    if (Input.GetKeyDown(KeyCode.D))
        CycleDirectionFilter();
*/
    // C key re-centres
    if (Input.GetKeyDown(KeyBindings.Current.mapRecenter)) { _panOffset = Vector2.zero; _followPlayer = true; }

    // QoL: Z key zooms/pans to fit every currently-visible chip on screen
    if (Input.GetKeyDown(KeyBindings.Current.mapZoomFit))
        ZoomToFitActiveBuses();

    // QoL: arrow-key panning, disabled while locked to a follow target
    /*if (!_followPlayer)
    {
        float panSpeed = 240f * Time.unscaledDeltaTime;
        Vector2 kb = Vector2.zero;
        if (Input.GetKey(KeyCode.LeftArrow))  kb.x += panSpeed;
        if (Input.GetKey(KeyCode.RightArrow)) kb.x -= panSpeed;
        if (Input.GetKey(KeyCode.UpArrow))    kb.y += panSpeed;
        if (Input.GetKey(KeyCode.DownArrow))  kb.y -= panSpeed;
        if (kb != Vector2.zero) _panOffset += kb;
    }
*/
    _refreshTimer -= Time.unscaledDeltaTime;
    if (_refreshTimer <= 0f || _dataDirty)
    {
        _refreshTimer = refreshInterval;
        _dataDirty    = false;
        SnapshotWorld();
    }

    UpdateFollowTarget();
    UpdateBusTrails();
}

    // ═════════════════════════════════════════════════════════════════════════
    //  FOLLOW TARGET
    // ═════════════════════════════════════════════════════════════════════════
    private void UpdateFollowTarget()
    {
        if (!_followPlayer) return;

        if (_followBusID >= 0)
        {
            // Following a specific NPC fleet
            foreach (var kv in BusRegistry.ActiveBuses)
                if (kv.Value != null && kv.Value.fleetNumber == _followBusID)
                { _worldCentre = kv.Value.transform.position; return; }
            // Lost the bus — fall back to player
            _followBusID = -1;
        }

        var player = PlayerHandoff.Instance;
        if (player != null && player.playerBus != null)
            _worldCentre = player.playerBus.transform.position;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ROUTE FILTER
    // ═════════════════════════════════════════════════════════════════════════
    private void CycleRouteFilter()
    {
        var scheduler = BusScheduler.Instance;
        if (scheduler == null || scheduler.managedRoutes == null) { _routeFilterIndex = -2; return; }

        int routeCount = scheduler.managedRoutes.Length;
        if (_routeFilterIndex == -2)        _routeFilterIndex = -1;
        else if (_routeFilterIndex == -1)   _routeFilterIndex = 0;
        else if (_routeFilterIndex < routeCount - 1) _routeFilterIndex++;
        else                                _routeFilterIndex = -2;
    }

    private bool IsRouteVisible(string routeNumber)
    {
        if (_routeFilterIndex == -2) return true; // all

        var scheduler = BusScheduler.Instance;
        var currentLeg = GetPlayerCurrentLeg();

        if (_routeFilterIndex == -1) // player route only
            return currentLeg != null && currentLeg.routeNumber == routeNumber;

        if (scheduler != null && _routeFilterIndex < scheduler.managedRoutes.Length)
            return scheduler.managedRoutes[_routeFilterIndex]?.routeNumber == routeNumber;

        return true;
    }

    private string FilterLabel()
    {
        if (_routeFilterIndex == -2) return "ALL";
        if (_routeFilterIndex == -1) return "PLAYER";
        var scheduler = BusScheduler.Instance;
        if (scheduler != null && _routeFilterIndex < scheduler.managedRoutes.Length)
            return $"RT {scheduler.managedRoutes[_routeFilterIndex]?.routeNumber ?? "?"}";
        return "?";
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DIRECTION FILTER [MAP-7]
    // ═════════════════════════════════════════════════════════════════════════
    private void CycleDirectionFilter()
    {
        _directionFilter = (_directionFilter + 1) % 3; // -1 → 0 → 1 → -1
    }

    private string DirectionFilterLabel()
    {
        return _directionFilter switch
        {
            -1 => "BOTH",
            0  => "OUTB",
            1  => "INBO",
            _  => "?"
        };
    }

    private bool IsDirectionVisible(bool isOutbound)
    {
        return _directionFilter switch
        {
            -1 => true,  // show both
            0  => isOutbound,   // show only outbound
            1  => !isOutbound,  // show only inbound
            _  => true
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SNAPSHOT
    // ═════════════════════════════════════════════════════════════════════════
    private void SnapshotWorld()
    {
        _roadLines.Clear();
        _stopDots.Clear();
        _chips.Clear();
        _activeEvents.Clear();
        _roadJunctions.Clear();

        if (RoadEventRegistry.Instance != null)
            _activeEvents.AddRange(RoadEventRegistry.Instance.GetAllActiveEvents());

        var city   = City;
        var player = PlayerHandoff.Instance;
        var currentLeg = GetPlayerCurrentLeg();

        BusRouteData highlight = targetRoute;
        if (highlight == null && currentLeg != null && city != null && city.routes != null)
            foreach (var r in city.routes)
                if (r != null && r.routeNumber == currentLeg.routeNumber) { highlight = r; break; }

        if (!(_followPlayer && _followBusID < 0))
            _worldCentre = (_worldCentre == Vector3.zero && player?.playerBus != null)
                ? player.playerBus.transform.position
                : _worldCentre;
        else if (player?.playerBus != null)
            _worldCentre = player.playerBus.transform.position;

// [MAP-7] Junctions are static — built once, cached, never recomputed here.
if (!_junctionsBuilt) BuildRoadJunctionsOnce();

        // ── Layer 0: City grid ────────────────────────────────────────────────
        if (city?.roadDefinitions != null)
        {
            foreach (var roadDef in city.roadDefinitions)
            {
                if (roadDef == null || string.IsNullOrEmpty(roadDef.roadCode)) continue;
                var road = city.GetRoad(roadDef.roadCode);
                if (road == null) continue;

                int n = splineSamplesPerSegment * 2;
                var pts = GetOrSampleCached("grid:" + roadDef.roadCode, () =>
                {
                    var list = new List<Vector3>(n);
                    for (int i = 0; i < n; i++)
                        list.Add(road.EvaluatePosition(i / (float)(n - 1)));
                    return list;
                });

                // [ADD] Grid roads are raw-sampled, never simplified, so
                // they don't have the zoom-chunkiness problem routes did --
                // just needs its bounding box for the new AABB pre-cull.
                var (gMin, gMax) = ComputeBounds2D(pts);
                _roadLines.Add(new RoutePolyline
                {
                    pts   = pts,
                    col   = new Color(0.22f, 0.25f, 0.28f, 0.70f),
                    layer = 0,
                    minB  = gMin,
                    maxB  = gMax
                });
            }
        }

        // ── Layers 1 & 2: Transit routes via nodes [MAP-1] updated ─────────────
        // [ADD] Computed once per SnapshotWorld call (itself already
        // throttled behind _refreshTimer, not per-frame) -- every route
        // polyline built below gets freshly re-simplified against whatever
        // zoom the player is CURRENTLY at, instead of a tolerance baked in
        // once and cached forever. See GetZoomAdaptiveTolerance's comment.
        float simplifyTol = GetZoomAdaptiveTolerance();

        if (city?.routes != null)
        {
            foreach (var route in city.routes)
            {
                if (route == null) continue;

                bool isHL      = (route == highlight);
                int  layer     = isHL ? 2 : 1;
                bool visible   = IsRouteVisible(route.routeNumber);
                if (!visible && _routeFilterIndex != -2) continue;

                Color routeCol = new Color(route.routeColor.r, route.routeColor.g, route.routeColor.b, 1f);

                // [MAP-7] Apply direction filter
                bool tryOutbound = IsDirectionVisible(true);
                bool tryInbound = IsDirectionVisible(false);

                // Draw BOTH outbound and inbound nodes
                if (tryOutbound && route.outboundNodes != null && route.outboundNodes.Count >= 2)
                {
                    // [CHANGE] GetOrSampleCached now returns the RAW dense
                    // samples (SampleSegments no longer simplifies them
                    // itself) -- simplify fresh here, every SnapshotWorld
                    // call, at the current zoom's tolerance.
                    var rawPts = GetOrSampleCached(route.routeNumber + ":out", () =>
                        SampleSegments(route.BuildSegments(route.outboundNodes), splineSamplesPerSegment));
                    if (rawPts.Length >= 2)
                    {
                        var simPts = SimplifyPolyline(new List<Vector3>(rawPts), simplifyTol).ToArray();
                        var (minB, maxB) = ComputeBounds2D(simPts);
                        _roadLines.Add(new RoutePolyline
                        {
                            pts   = simPts,
                            col   = routeCol,
                            layer = layer,
                            minB  = minB,
                            maxB  = maxB
                        });
                    }
                }

                if (tryInbound && route.inboundNodes != null && route.inboundNodes.Count >= 2)
                {
                    var rawPts = GetOrSampleCached(route.routeNumber + ":in", () =>
                        SampleSegments(route.BuildSegments(route.inboundNodes), splineSamplesPerSegment));
                    if (rawPts.Length >= 2)
                    {
                        var simPts = SimplifyPolyline(new List<Vector3>(rawPts), simplifyTol).ToArray();
                        var (minB, maxB) = ComputeBounds2D(simPts);
                        _roadLines.Add(new RoutePolyline
                        {
                            pts   = simPts,
                            col   = routeCol,
                            layer = layer,
                            minB  = minB,
                            maxB  = maxB
                        });
                    }
                }

                // [FIX] Lettered variants with a custom path (overrideRoute + a
                // non-empty outbound/inbound node override) were never drawn at
                // all — only the mainline nodes above ever made it into
                // _roadLines. A bus running a variant's detour had no
                // route-colored line for that stretch, so it visually rode the
                // plain gray road-network line (layer 0) instead of the route's
                // actual color. Draw each variant's override path, same route
                // color, same layer rule as the mainline above.
                if (route.variants != null)
                {
                    foreach (var v in route.variants)
                    {
                        if (v == null || !v.overrideRoute) continue;

                        if (tryOutbound && v.outboundNodesOverride != null && v.outboundNodesOverride.Count >= 2)
                        {
                            var rawVPts = GetOrSampleCached(route.routeNumber + ":var:" + v.variantLetter + ":out", () =>
                                SampleSegments(route.BuildSegments(v.outboundNodesOverride), splineSamplesPerSegment));
                            if (rawVPts.Length >= 2)
                            {
                                var simVPts = SimplifyPolyline(new List<Vector3>(rawVPts), simplifyTol).ToArray();
                                var (minB, maxB) = ComputeBounds2D(simVPts);
                                _roadLines.Add(new RoutePolyline
                                {
                                    pts   = simVPts,
                                    col   = routeCol,
                                    layer = layer,
                                    minB  = minB,
                                    maxB  = maxB
                                });
                            }
                        }

                        if (tryInbound && v.inboundNodesOverride != null && v.inboundNodesOverride.Count >= 2)
                        {
                            var rawVPts = GetOrSampleCached(route.routeNumber + ":var:" + v.variantLetter + ":in", () =>
                                SampleSegments(route.BuildSegments(v.inboundNodesOverride), splineSamplesPerSegment));
                            if (rawVPts.Length >= 2)
                            {
                                var simVPts = SimplifyPolyline(new List<Vector3>(rawVPts), simplifyTol).ToArray();
                                var (minB, maxB) = ComputeBounds2D(simVPts);
                                _roadLines.Add(new RoutePolyline
                                {
                                    pts   = simVPts,
                                    col   = routeCol,
                                    layer = layer,
                                    minB  = minB,
                                    maxB  = maxB
                                });
                            }
                        }
                    }
                }
            }
        }

        // ── Stops [MAP-6] now carries stopCode for click lookups ────────────────
        if (city?.AllStops != null)
            foreach (var s in city.AllStops)
                if (s != null) _stopDots.Add(new StopDot
                {
                    worldPos   = s.GetWorldPosition(),
                    stopCode   = s.stopCode,
                    stopName   = s.stopName,
                    isTerminal = s.isTerminal
                });
// In MDT_LiveMap.SnapshotWorld(), player chip block:
if (player?.playerBus != null)
{
    Color pc = highlight != null
        ? Color.Lerp(highlight.routeColor, Color.white, 0.5f)
        : new Color(0.55f, 0.75f, 1f);

    var playerChip = new BusChip
    {
        worldPos      = player.playerBus.transform.position,
        fleetNumber   = player.FleetNumber,   // was hardcoded 1799
        isPlayer      = true,
        isOutbound    = currentLeg?.isOutbound ?? false,
        routeColor    = pc,
        routeNumber   = currentLeg?.routeNumber ?? "?",
        variantLetter = currentLeg?.variantLetter ?? "",
        busID         = player.PlayerBusID,
        isLocked      = BusBreakdownSystem.Instance != null && BusBreakdownSystem.Instance.IsBusLocked(player.PlayerBusID),
    };
    playerChip.BuildDisplayStrings(); // [PERF FIX] see BusChip struct comment
    _chips.Add(playerChip);
}


        // ── NPC chips ──────────────────────────────────────────────────────────
        foreach (var kv in BusRegistry.ActiveBuses)
        {
            var ctrl = kv.Value;
            if (ctrl == null) continue;

            // [MAP-7] Apply direction filter to chips
            if (!IsDirectionVisible(ctrl.IsOutbound)) continue;

            // [FIX] A bus heading to depot to retire, or already sitting
            // there waiting out a big gap before its next departure, isn't
            // actually serving its route right now — showing it in the full
            // route color reads as "still in service", which is misleading.
            // DepotEgress (pulling OUT of the depot to start a specific
            // route) is deliberately excluded here — that bus IS about to
            // serve that route, so it keeps its route color.
            // [FIX] Was only checking the depot-bound states -- a retired bus
            // now detours through the maintenance bay first (via
            // NPCBusController.StartDrivingToMaintenanceBay), and while it's
            // DrivingToMaintenanceBay/AtMaintenanceBay it kept showing its old
            // route color on the live map for that whole leg -- exactly the
            // "scary 299 on 0th Ave" case, since ClearRouteAssignment() only
            // fires once AtMaintenanceBay is actually reached (see
            // NPCBusController.DriveToMaintenanceBay), not before.
            bool offService = ctrl.State == NPCBusController.BusState.DepotIngress
                            || ctrl.State == NPCBusController.BusState.WaitingAtDepot
                            || ctrl.State == NPCBusController.BusState.DrivingToMaintenanceBay
                            || ctrl.State == NPCBusController.BusState.AtMaintenanceBay;

            Color bc = offService
                ? new Color(0.45f, 0.48f, 0.52f) // neutral gray — off service
                : (ctrl.CurrentRoute != null ? ctrl.CurrentRoute.routeColor : new Color(0.35f, 0.60f, 1f));

            var npcChip = new BusChip
            {
                worldPos      = ctrl.transform.position,
                fleetNumber   = ctrl.fleetNumber,
                isPlayer      = false,
                isOutbound    = ctrl.IsOutbound,
                routeColor    = bc,
                routeNumber   = ctrl.CurrentRoute?.routeNumber ?? "?",
                variantLetter = ctrl.variantLetter,
                busID         = kv.Key,
                isLocked      = BusBreakdownSystem.Instance != null && BusBreakdownSystem.Instance.IsBusLocked(kv.Key),
            };
            npcChip.BuildDisplayStrings(); // [PERF FIX] see BusChip struct comment
            _chips.Add(npcChip);
        }} 

    // ── [MAP-1] Segment-based sampling (node polylines) ──────────────────────
    /// <summary>Sample all segments and return a polyline of world points.</summary>
private List<Vector3> SampleSegments(List<IRouteSegment> segments, int samplesPerSegment)
{
    var result = new List<Vector3>();
    if (segments == null || segments.Count == 0) return result;

    for (int segIdx = 0; segIdx < segments.Count; segIdx++)
    {
        var seg = segments[segIdx];
        if (seg == null) continue;

        // A single IRouteSegment can now span a long, multi-bend curve run
        // (BusRouteData.BuildSegments collapses a whole densified curve run
        // into one CatmullRomSegment). Sample it densely here so the SHAPE
        // is accurate — the density gets trimmed back down right after via
        // SimplifyPolyline, which runs once at build time, not per-frame.
        int samples = SampleCountForSegment(seg.Length, samplesPerSegment);
        for (int i = 0; i < samples; i++)
        {
            float t = i / (float)(samples - 1);
            result.Add(seg.Evaluate(t));
        }
        // Remove duplicate at segment boundary (index-based, no IndexOf scan)
        if (result.Count > 1 && segIdx < segments.Count - 1)
            result.RemoveAt(result.Count - 1);
    }

    // [CHANGE] Used to call SimplifyPolyline with a FIXED world-space
    // tolerance right here, baked permanently into the cached array by
    // GetOrSampleCached -- meaning the simplification could never adapt as
    // zoom changed, which is exactly why routes went chunky/faceted at high
    // zoom (see GetZoomAdaptiveTolerance's comment). Now returns the raw
    // dense samples; simplification happens fresh at _roadLines build time
    // in SnapshotWorld instead, using the current zoom's tolerance. Raw
    // sampling itself is still cached (this is the expensive part --
    // spline evaluation), so this doesn't reintroduce the per-frame cost
    // the old comment above was protecting against; only the cheap
    // Douglas-Peucker pass re-runs, and only once per throttled
    // SnapshotWorld refresh, not per-frame.
    return result;
}

// [ADD] Cheap O(n) bounding-box pass, run once per polyline at build
// time (SnapshotWorld, throttled), not per-frame.
private static (Vector2 min, Vector2 max) ComputeBounds2D(Vector3[] pts)
{
    if (pts == null || pts.Length == 0) return (Vector2.zero, Vector2.zero);
    Vector2 min = new Vector2(pts[0].x, pts[0].z);
    Vector2 max = min;
    for (int i = 1; i < pts.Length; i++)
    {
        Vector2 p = new Vector2(pts[i].x, pts[i].z);
        if (p.x < min.x) min.x = p.x; else if (p.x > max.x) max.x = p.x;
        if (p.y < min.y) min.y = p.y; else if (p.y > max.y) max.y = p.y;
    }
    return (min, max);
}

// [ADD] Screen-space-constant simplification tolerance. World-space
// tolerance alone (the old MapPolylineSimplifyTolerance) looks fine at
// whatever zoom it was tuned for, but the SAME absolute world deviation
// covers more and more screen pixels the further in you zoom -- that's
// why routes went visibly chunky/faceted at high zoom despite looking
// perfectly smooth zoomed out. Converting the desired on-screen error
// budget (in pixels) into a world-space tolerance via the current
// zoom/scale keeps the visual error roughly constant on screen at any
// zoom level, while still simplifying aggressively (fewer points, less
// per-frame draw cost) whenever zoomed out.
private const float MapPolylineSimplifyPixelBudget = 1.25f;
private float GetZoomAdaptiveTolerance()
{
    float baseScale = Mathf.Min(mapWidth, mapHeight) * 0.45f / Mathf.Max(viewRadius, 1f);
    float scale     = Mathf.Max(baseScale * _zoom, 0.0001f);
    return Mathf.Max(0.05f, MapPolylineSimplifyPixelBudget / scale);
}

/// <summary>Ramer-Douglas-Peucker polyline simplification (iterative, no
/// recursion) — keeps the endpoints and any point that deviates from the
/// straight line between its run's endpoints by more than tolerance.</summary>
private static List<Vector3> SimplifyPolyline(List<Vector3> pts, float tolerance)
{
    if (pts == null || pts.Count < 3) return pts;

    int n = pts.Count;
    var keep = new bool[n];
    keep[0] = keep[n - 1] = true;

    var stack = new Stack<(int lo, int hi)>();
    stack.Push((0, n - 1));

    while (stack.Count > 0)
    {
        var (lo, hi) = stack.Pop();
        if (hi <= lo + 1) continue;

        Vector3 a = pts[lo];
        Vector3 b = pts[hi];
        Vector3 ab = b - a;
        float abLenSq = ab.sqrMagnitude;

        float maxDist  = -1f;
        int   maxIndex = -1;

        for (int i = lo + 1; i < hi; i++)
        {
            Vector3 ap = pts[i] - a;
            float dist;
            if (abLenSq < 0.0001f)
            {
                dist = ap.magnitude; // a==b degenerate case
            }
            else
            {
                float t = Mathf.Clamp01(Vector3.Dot(ap, ab) / abLenSq);
                Vector3 proj = a + ab * t;
                dist = Vector3.Distance(pts[i], proj);
            }

            if (dist > maxDist)
            {
                maxDist  = dist;
                maxIndex = i;
            }
        }

        if (maxIndex >= 0 && maxDist > tolerance)
        {
            keep[maxIndex] = true;
            stack.Push((lo, maxIndex));
            stack.Push((maxIndex, hi));
        }
    }

    var outPts = new List<Vector3>(n);
    for (int i = 0; i < n; i++)
        if (keep[i]) outPts.Add(pts[i]);

    return outPts;
}

// Target spacing (world units) between drawn polyline points. Short
// segments still get at least `baseSamples` so tiny curves don't get
// starved down to 2 points; long segments scale up so the drawn line
// keeps pace with however dense the underlying curve actually is.
private const float MapPolylineTargetSpacing = 2.0f;
private const int   MapPolylineMaxSamples    = 400;

private static int SampleCountForSegment(float segLength, int baseSamples)
{
    int byLength = Mathf.CeilToInt(segLength / MapPolylineTargetSpacing);
    int samples  = Mathf.Max(baseSamples, byLength);
    return Mathf.Clamp(samples, 2, MapPolylineMaxSamples);
}
private void BuildRoadJunctionsOnce()
{
    _junctionsBuilt = true;
    _roadJunctions.Clear();

    var city = City;
    if (city?.roadDefinitions == null || city.roadDefinitions.Count == 0) return;

    var junctionSet = new HashSet<string>();
    for (int i = 0; i < city.roadDefinitions.Count; i++)
    {
        var road1 = city.GetRoad(city.roadDefinitions[i]?.roadCode);
        if (road1 == null) continue;
        Vector3[] ep1 = { road1.EvaluatePosition(0f), road1.EvaluatePosition(1f) };

        for (int j = i + 1; j < city.roadDefinitions.Count; j++)
        {
            var road2 = city.GetRoad(city.roadDefinitions[j]?.roadCode);
            if (road2 == null) continue;
            Vector3[] ep2 = { road2.EvaluatePosition(0f), road2.EvaluatePosition(1f) };

            foreach (var p1 in ep1)
            foreach (var p2 in ep2)
            {
                if (Vector3.Distance(p1, p2) < 12f)
                {
                    string key = $"{p1.x:F0}_{p1.y:F0}_{p1.z:F0}";
                    if (junctionSet.Add(key)) _roadJunctions.Add(p1);
                }
            }
        }
    }
}
private bool _junctionsBuilt = false;
    // ── [MAP-1] Spline-between-stops builder (DEPRECATED — kept for reference) ──────────────────────
    /// <summary>Build route polyline between two consecutive stops using route nodes for accurate pathing.</summary>
    private List<Vector3> BuildSplineSegmentBetweenStops(
        CityManager city, BusStopData stopA, BusStopData stopB, List<RouteNode> routeNodes = null)
    {
        var result = new List<Vector3>();

        string roadA = stopA?.parentRoadCode;
        string roadB = stopB?.parentRoadCode;

        Vector3 posA = stopA.GetWorldPosition();
        Vector3 posB = stopB.GetWorldPosition();

        // Same road — sample the spline between the two t-values
        if (!string.IsNullOrEmpty(roadA) && roadA == roadB)
        {
            var road = city.GetRoad(roadA);
            if (road != null)
            {
                float tA = FindNearestT(road, posA);
                float tB = FindNearestT(road, posB);
                float lo = Mathf.Min(tA, tB);
                float hi = Mathf.Max(tA, tB);

                int n = splineSamplesPerSegment;
                for (int i = 0; i <= n; i++)
                    result.Add(road.EvaluatePosition(Mathf.Lerp(lo, hi, i / (float)n)));

                // Ensure actual stop world positions are the endpoints
                if (result.Count > 0) result[0] = posA;
                if (result.Count > 1) result[result.Count - 1] = posB;
                return result;
            }
        }

        // Different roads — use route nodes to determine the actual path direction
        if (!string.IsNullOrEmpty(roadA))
        {
            var road = city.GetRoad(roadA);
            if (road != null)
            {
                float tA = FindNearestT(road, posA);
                // Determine direction using route nodes if available
                float tEnd = DetermineRoadEndT(road, posA, posB, routeNodes, city);
                float lo = Mathf.Min(tA, tEnd);
                float hi = Mathf.Max(tA, tEnd);
                int n = splineSamplesPerSegment / 2;
                for (int i = 0; i <= n; i++)
                    result.Add(road.EvaluatePosition(Mathf.Lerp(lo, hi, i / (float)n)));
            }
        }

        if (!string.IsNullOrEmpty(roadB))
        {
            var road = city.GetRoad(roadB);
            if (road != null)
            {
                float tB = FindNearestT(road, posB);
                // Determine start based on where we exited the previous road
                float tStart = result.Count > 0
                    ? FindNearestT(road, result[result.Count - 1]) : 0f;
                float lo = Mathf.Min(tStart, tB);
                float hi = Mathf.Max(tStart, tB);
                int n = splineSamplesPerSegment / 2;
                for (int i = 0; i <= n; i++)
                    result.Add(road.EvaluatePosition(Mathf.Lerp(lo, hi, i / (float)n)));
            }
        }

        // Absolute fallback: straight line
        if (result.Count < 2)
        {
            result.Clear();
            result.Add(posA);
            result.Add(posB);
        }
        else
        {
            result[0] = posA;
            result[result.Count - 1] = posB;
        }

        return result;
    }

    /// <summary>Use route nodes to determine which direction (t=0 or t=1) to traverse a road.</summary>
    private float DetermineRoadEndT(RoadSegment road, Vector3 stopOnRoad, Vector3 nextStop, 
        List<RouteNode> routeNodes, CityManager city)
    {
        if (routeNodes == null || routeNodes.Count < 2) 
            return 0.5f; // fallback to middle

        // Find where we are in the route nodes
        float distThreshold = 5f;
        int currentNodeIdx = -1;
        for (int i = 0; i < routeNodes.Count; i++)
        {
            if (Vector3.Distance(routeNodes[i].position, stopOnRoad) < distThreshold)
            {
                currentNodeIdx = i;
                break;
            }
        }

        if (currentNodeIdx < 0 || currentNodeIdx >= routeNodes.Count - 1)
            return 0.5f; // fallback

        // Look ahead a few nodes to determine which end of the road we're heading towards
        Vector3 nextNode = routeNodes[currentNodeIdx + 1].position;
        Vector3 roadStart = road.EvaluatePosition(0f);
        Vector3 roadEnd = road.EvaluatePosition(1f);

        float distToStart = Vector3.Distance(nextNode, roadStart);
        float distToEnd = Vector3.Distance(nextNode, roadEnd);

        return distToStart < distToEnd ? 0f : 1f;
    }

    /// <summary>Binary-search for the t on a road spline closest to worldPos.</summary>
    private float FindNearestT(RoadSegment road, Vector3 worldPos)
    {
        const int coarseSteps = 32;
        float bestT    = 0f;
        float bestDist = float.MaxValue;

        for (int i = 0; i <= coarseSteps; i++)
        {
            float t = i / (float)coarseSteps;
            float d = Vector3.Distance(road.EvaluatePosition(t), worldPos);
            if (d < bestDist) { bestDist = d; bestT = t; }
        }

        // Refine with bisection
        float lo = Mathf.Max(0f, bestT - 1f / coarseSteps);
        float hi = Mathf.Min(1f, bestT + 1f / coarseSteps);
        for (int i = 0; i < 8; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float dLo = Vector3.Distance(road.EvaluatePosition((lo + mid) * 0.5f), worldPos);
            float dHi = Vector3.Distance(road.EvaluatePosition((mid + hi) * 0.5f), worldPos);
            if (dLo < dHi) hi = mid; else lo = mid;
        }
        return (lo + hi) * 0.5f;
    }
private void OnGUI()
{
    if (!_visible) return;
    BuildStyles();

    bool showMap  = _viewMode == ViewMode.Map  || _viewMode == ViewMode.Both;
    bool showList = _viewMode == ViewMode.List || _viewMode == ViewMode.Both;

    int mapAreaW  = showMap ? mapWidth : 0;
    int listAreaW = showList ? (showMap ? listDockWidth : mapWidth) : 0;
    int totalW    = mapAreaW + listAreaW + (showMap && showList ? 4 : 0);
    int totalH    = mapHeight;

    int px = Screen.width  - totalW - screenMarginRight;
    int py = screenMarginTop;
    var panelRect = new Rect(px, py, totalW, totalH);

    MDT_UITheme.DrawPanel(panelRect);
    GUI.BeginGroup(panelRect);

    if (showMap)
    {
        var mapGroupRect = new Rect(0, 0, mapAreaW, totalH);
        GUI.BeginGroup(mapGroupRect);

        int mapAreaH = mapHeight - HEADER_H - LEGEND_H;

        DrawHeader(mapWidth);

        if (_mode3D)
        {
            // 3D viewport: the camera renders privately into a RenderTexture
            // sized to this exact panel, then we composite it in like any other
            // UI element — no viewport-rect hackery punching through the screen.
            if (live3DCamera != null)
            {
                var viewportRect = new Rect(0, HEADER_H, mapWidth, mapAreaH);

                // [PERF FIX] GetOutputTexture (and whatever render work backs
                // it in MDT_Live3DCamera) used to run on EVERY OnGUI
                // invocation -- Layout, Repaint, and every raw input event --
                // meaning the 3D map camera was potentially re-rendering its
                // whole view several times per rendered frame. Only the
                // fetch+draw is gated here; the click-to-inspect pick below
                // still runs on the actual MouseDown event regardless, so
                // clicking still feels instant.
                if (Event.current.type == EventType.Repaint)
                {
                    var rt = live3DCamera.GetOutputTexture(mapWidth, mapAreaH);
                    GUI.DrawTexture(viewportRect, rt, ScaleMode.StretchToFill, false);
                }

                // Click-to-inspect: only fire on mouse-down so dragging to orbit
                // doesn't spam picks every frame.
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                    viewportRect.Contains(Event.current.mousePosition))
                {
                    Vector2 local = Event.current.mousePosition - new Vector2(viewportRect.x, viewportRect.y);
                    if (live3DCamera.TryPickWorldPoint(local, out Vector3 hit))
                    {
                        _pickedWorldPoint = hit;
                        _pickedLabel = $"({hit.x:0.0}, {hit.y:0.0}, {hit.z:0.0})";
                    }
                }

                if (_pickedWorldPoint.HasValue)
                {
                    GUI.Label(new Rect(8, viewportRect.yMax - 22, 220, 18), _pickedLabel,
                        MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan));
                }
            }

            // Overlays that use world-to-screen still project correctly on top
            // of the 3D view.
            DrawBusTrails(mapWidth, mapAreaH);
            DrawBreakdownRings(mapWidth, mapAreaH);
            DrawTooltip(mapWidth, mapAreaH);
        }
        else
        {
            HandleInput(mapWidth, mapAreaH);
            DrawMapArea(mapWidth, mapAreaH);
            DrawAreaLabels(mapWidth, mapAreaH);
            DrawBusTrails(mapWidth, mapAreaH);
            DrawFavoriteStopMarkers(mapWidth, mapAreaH);
            DrawTerminalCongestion(mapWidth, mapAreaH);
            DrawBreakdownRings(mapWidth, mapAreaH);
            DrawRouteOnTimeBadge(mapWidth);
            DrawFavoriteStopsBar(mapWidth);
            DrawRecentBusesPanel(mapWidth, mapAreaH);
            DrawLegend(mapWidth, mapAreaH);
            DrawTooltip(mapWidth, mapAreaH);
            DrawEtaPopup(mapWidth, mapAreaH);
            DrawSearchResults(mapWidth);
        }

        GUI.EndGroup();
    }

    if (showList)
    {
        float listX = showMap ? mapAreaW + 4 : 0;
        var listRect = new Rect(listX, 0, listAreaW, totalH);
        if (showMap) MDT_UITheme.DrawDivider(listX - 2, 0, 1);

        if (listController != null)
            listController.DrawDocked(listRect);
        else
            GUI.Label(listRect, "List view: no MDT_UI_Controller found in scene.",
                MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleCenter, MDT_UITheme.TextDim));
    }

    string modeLabel = _viewMode switch { ViewMode.Map => "MAP", ViewMode.List => "LIST", _ => "MAP + LIST" };
    GUI.Label(new Rect(totalW - 90, 4, 86, 14), $"[L] {modeLabel}",
        MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleRight, MDT_UITheme.TextDim));

    GUI.EndGroup();
}
// ═════════════════════════════════════════════════════════════════════════
//  QoL — PERSISTENT VIEW SETTINGS
// ═════════════════════════════════════════════════════════════════════════
private void LoadMapPrefs()
{
    _zoom             = Mathf.Clamp(PlayerPrefs.GetFloat(PrefZoom, 1f), zoomMin, zoomMax);
    _routeFilterIndex = PlayerPrefs.GetInt(PrefRouteIdx, -2);
    _directionFilter  = PlayerPrefs.GetInt(PrefDirFilter, -1);
    _followPlayer     = PlayerPrefs.GetInt(PrefFollow, 1) == 1;
    _followBusID      = -1;
    _panOffset        = Vector2.zero;
}

private void SaveMapPrefs()
{
    PlayerPrefs.SetFloat(PrefZoom, _zoom);
    PlayerPrefs.SetInt(PrefRouteIdx, _routeFilterIndex);
    PlayerPrefs.SetInt(PrefDirFilter, _directionFilter);
    PlayerPrefs.SetInt(PrefFollow, _followPlayer ? 1 : 0);
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — ZOOM TO FIT
// ═════════════════════════════════════════════════════════════════════════
private void ZoomToFitActiveBuses()
{
    if (_chips.Count == 0) return;

    Vector3 min = _chips[0].worldPos;
    Vector3 max = _chips[0].worldPos;
    foreach (var chip in _chips)
    {
        min = Vector3.Min(min, chip.worldPos);
        max = Vector3.Max(max, chip.worldPos);
    }

    Vector3 centre = (min + max) * 0.5f;
    float   spanX  = Mathf.Max(50f, max.x - min.x);
    float   spanZ  = Mathf.Max(50f, max.z - min.z);
    float   span   = Mathf.Max(spanX, spanZ) * 0.5f;

    _followPlayer = false;
    _followBusID  = -1;
    _worldCentre  = centre;
    _panOffset    = Vector2.zero;
    _zoom         = Mathf.Clamp(viewRadius / Mathf.Max(span, 1f), zoomMin, zoomMax);
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — BUS BREADCRUMB TRAILS
// ═════════════════════════════════════════════════════════════════════════
private void UpdateBusTrails()
{
    _trailSampleTimer -= Time.unscaledDeltaTime;
    if (_trailSampleTimer > 0f) return;
    _trailSampleTimer = TRAIL_SAMPLE_INTERVAL;

    void Sample(int id, Vector3 pos)
    {
        if (!_busTrails.TryGetValue(id, out var pts))
        {
            pts = new List<Vector3>(TRAIL_MAX_POINTS);
            _busTrails[id] = pts;
        }
        pts.Add(pos);
        if (pts.Count > TRAIL_MAX_POINTS) pts.RemoveAt(0);
    }

    foreach (var kv in BusRegistry.ActiveBuses)
        if (kv.Value != null) Sample(kv.Key, kv.Value.transform.position);

    // FIX: was gated on PlayerShiftDirector.Instance.Phase == OnLeg (class
    // from a reverted rebuild). InService is the equivalent real state —
    // the player is physically progressing along a leg, not just
    // dead-running to a terminal or waiting at one.
    var player = PlayerHandoff.Instance;
    if (player != null && player.playerBus != null
        && player.ShiftState == PlayerHandoff.PlayerShiftState.InService)
        Sample(player.PlayerBusID, player.playerBus.transform.position);

    var liveIDs = new HashSet<int>(BusRegistry.ActiveBuses.Keys);
    if (player != null) liveIDs.Add(player.PlayerBusID);
    var stale = new List<int>();
    foreach (var kv in _busTrails) if (!liveIDs.Contains(kv.Key)) stale.Add(kv.Key);
    foreach (var id in stale) _busTrails.Remove(id);
}

private void DrawBusTrails(int w, int mh)
{
    if (Event.current.type != EventType.Repaint) return; // [PERF FIX] see DrawMapArea
    foreach (var kv in _busTrails)
    {
        var pts = kv.Value;
        if (pts.Count < 2) continue;

        Color trailCol = new Color(0.6f, 0.75f, 1f);
        foreach (var chip in _chips)
            if (chip.busID == kv.Key) { trailCol = chip.routeColor; break; }

        for (int i = 0; i < pts.Count - 1; i++)
        {
            float t     = (float)i / Mathf.Max(1, pts.Count - 1);
            float alpha = Mathf.Lerp(0.05f, 0.35f, t);
            Vector2 a = WorldToScreen(pts[i],     w, mh) + new Vector2(0, HEADER_H);
            Vector2 b = WorldToScreen(pts[i + 1], w, mh) + new Vector2(0, HEADER_H);
            MDT_UITheme.DrawLine(a, b, new Color(trailCol.r, trailCol.g, trailCol.b, alpha), 2f);
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — BREAKDOWN PULSE RINGS
// ═════════════════════════════════════════════════════════════════════════
private void DrawBreakdownRings(int w, int mh)
{
    if (Event.current.type != EventType.Repaint) return; // [PERF FIX] see DrawMapArea

    // [PERF FIX] Was its own full BusRegistry.ActiveBuses scan + a live
    // BusBreakdownSystem.IsBusLocked() call PER BUS, every single Repaint
    // frame -- completely bypassing SnapshotWorld's refresh throttle that
    // everything else respects. isLocked is now precomputed once per chip
    // refresh (see BusChip.isLocked / SnapshotWorld), so this just iterates
    // the chip list it's already drawing everything else from and reads a
    // bool. Also naturally covers the player chip in the same pass instead
    // of a separate branch after.
    if (_chips.Count == 0) return;

    float pulse = 0.5f + Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 4f)) * 0.5f;
    float ringR = 16f + pulse * 6f;

    foreach (var chip in _chips)
    {
        if (!chip.isLocked) continue;

        Vector2 sp = WorldToScreen(chip.worldPos, w, mh);
        if (sp.x < -20 || sp.x > w + 20 || sp.y < -20 || sp.y > mh + 20) continue;
        Vector2 screenSp = sp + new Vector2(0, HEADER_H);
        MDT_UITheme.DrawRect(new Rect(screenSp.x - ringR, screenSp.y - ringR, ringR * 2, ringR * 2),
            new Color(1f, 0.15f, 0.1f, 0.30f * pulse));
    }
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — ROUTE ON-TIME BADGE
// ═════════════════════════════════════════════════════════════════════════
private float GetRouteOnTimePercent(string routeNumber)
{
    var scheduler = BusScheduler.Instance;
    if (scheduler == null || string.IsNullOrEmpty(routeNumber)) return -1f;

    int total = 0, onTime = 0;
    foreach (var slot in scheduler.AllSlots)
    {
        if (slot.routeNumber != routeNumber) continue;
        if (slot.state != SlotState.InService && slot.state != SlotState.Completed) continue;
        if (slot.actualDeparture < 0f) continue;
        total++;
        if (Mathf.Abs(slot.latenessMinutes) <= 2f) onTime++;
    }
    return total == 0 ? -1f : (onTime / (float)total) * 100f;
}

private void DrawRouteOnTimeBadge(int w)
{
    if (Event.current.type != EventType.Repaint) return; // [PERF FIX] see DrawMapArea
    var currentLeg = GetPlayerCurrentLeg();
    if (currentLeg == null || string.IsNullOrEmpty(currentLeg.routeNumber)) return;

    float pct = GetRouteOnTimePercent(currentLeg.routeNumber);
    if (pct < 0f) return;

    Color col = pct >= 85f ? MDT_UITheme.TextGreen
              : pct >= 60f ? MDT_UITheme.TextAmber
              : new Color(1f, 0.4f, 0.35f);

    GUI.Label(new Rect(8, 27, 60, 7), $"{pct:F0}% ON-TIME",
        MDT_UITheme.MakeLabel(6, FontStyle.Bold, TextAnchor.MiddleLeft, col));
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — TERMINAL CONGESTION
// ═════════════════════════════════════════════════════════════════════════
private void DrawTerminalCongestion(int w, int mh)
{
    if (Event.current.type != EventType.Repaint) return; // [PERF FIX] see DrawMapArea
    foreach (var sd in _stopDots)
    {
        if (!sd.isTerminal) continue;

        int waiting = 0;
        foreach (var kv in BusRegistry.ActiveBuses)
        {
            var ctrl = kv.Value;
            if (ctrl == null || ctrl.State != NPCBusController.BusState.AtTerminal) continue;
            if (Vector3.Distance(ctrl.transform.position, sd.worldPos) <= TERMINAL_CONGESTION_RADIUS)
                waiting++;
        }
        if (waiting < 2) continue;

        Vector2 sp = WorldToScreen(sd.worldPos, w, mh);
        if (sp.x < -20 || sp.x > w + 20 || sp.y < -20 || sp.y > mh + 20) continue;
        Vector2 screenSp = sp + new Vector2(0, HEADER_H);

        var badgeR = new Rect(screenSp.x + 6, screenSp.y - 16, 26, 14);
        MDT_UITheme.DrawRect(badgeR, new Color(1f, 0.55f, 0.1f, 0.92f));
        GUI.Label(badgeR, waiting.ToString(),
            MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleCenter, Color.black));
    }
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — FAVORITE STOPS
// ═════════════════════════════════════════════════════════════════════════
private void LoadFavoriteStops()
{
    _favoriteStops.Clear();
    string raw = PlayerPrefs.GetString(PrefFavStops, "");
    if (string.IsNullOrEmpty(raw)) return;
    foreach (var code in raw.Split(','))
        if (!string.IsNullOrEmpty(code)) _favoriteStops.Add(code);
}

private void SaveFavoriteStops() => PlayerPrefs.SetString(PrefFavStops, string.Join(",", _favoriteStops));

private void ToggleFavoriteStop(string stopCode)
{
    if (string.IsNullOrEmpty(stopCode)) return;
    if (!_favoriteStops.Remove(stopCode)) _favoriteStops.Add(stopCode);
    SaveFavoriteStops();
}

private void DrawFavoriteStopMarkers(int w, int mh)
{
    if (Event.current.type != EventType.Repaint) return; // [PERF FIX] see DrawMapArea
    if (_favoriteStops.Count == 0) return;

    foreach (var sd in _stopDots)
    {
        if (!_favoriteStops.Contains(sd.stopCode)) continue;
        Vector2 sp = WorldToScreen(sd.worldPos, w, mh);
        if (sp.x < -14 || sp.x > w + 14 || sp.y < -14 || sp.y > mh + 14) continue;
        Vector2 screenSp = sp + new Vector2(0, HEADER_H);

        float pulse = 0.6f + Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 1.4f)) * 0.4f;
        MDT_UITheme.DrawRect(new Rect(screenSp.x - 9, screenSp.y - 9, 18, 18),
            new Color(1f, 0.85f, 0.2f, 0.35f * pulse));
        GUI.Label(new Rect(screenSp.x - 6, screenSp.y - 22, 12, 12), "★",
            MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.2f)));
    }
}

private void DrawFavoriteStopsBar(int w)
{
    if (_favoriteStops.Count == 0) return;

    float bx = 8f;
    float by = HEADER_H + 2f;
    float bh = 16f;

    foreach (var code in _favoriteStops)
    {
        StopDot? match = null;
        foreach (var sd in _stopDots) if (sd.stopCode == code) { match = sd; break; }
        if (match == null) continue;

        string label = match.Value.stopName;
        if (label.Length > 14) label = label.Substring(0, 13) + "…";
        float bw = 10f + label.Length * 5.2f;

        var r = new Rect(bx, by, bw, bh);
        if (r.xMax > w - 8) break;

        MDT_UITheme.DrawRect(r, new Color(0.14f, 0.12f, 0.03f, 0.92f));
        MDT_UITheme.DrawRect(new Rect(r.x, r.y, 2, r.height), new Color(1f, 0.85f, 0.2f, 0.9f));
        GUI.Label(r, "★ " + label,
            MDT_UITheme.MakeLabel(7, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(1f, 0.85f, 0.2f)));

        if (GUI.Button(r, GUIContent.none, GUIStyle.none))
        {
            _followPlayer = false;
            _followBusID  = -1;
            _worldCentre  = match.Value.worldPos;
            _panOffset    = Vector2.zero;
        }

        bx += bw + 4f;
    }
}

// ═════════════════════════════════════════════════════════════════════════
//  QoL — RECENT BUSES
// ═════════════════════════════════════════════════════════════════════════
private void RecordRecentBus(int fleetNumber)
{
    _recentFleetNumbers.Remove(fleetNumber);
    _recentFleetNumbers.Insert(0, fleetNumber);
    if (_recentFleetNumbers.Count > RECENT_BUS_CAP)
        _recentFleetNumbers.RemoveAt(_recentFleetNumbers.Count - 1);
}

private void DrawRecentBusesPanel(int w, int mh)
{
    if (_recentFleetNumbers.Count == 0 || _showSearchResults) return;

    float rw = 118f;
    float rh = 16f + _recentFleetNumbers.Count * 16f;
    float rx = w - rw - 6f;
    float ry = HEADER_H + mh - rh - 6f;

    MDT_UITheme.DrawRect(new Rect(rx, ry, rw, rh), MDT_UITheme.BGDeep);
    MDT_UITheme.DrawRect(new Rect(rx, ry, rw, 1), MDT_UITheme.TextCyan * new Color(1, 1, 1, 0.3f));
    GUI.Label(new Rect(rx + 4, ry + 1, rw - 8, 14), "RECENT",
        MDT_UITheme.MakeLabel(7, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));

    for (int i = 0; i < _recentFleetNumbers.Count; i++)
    {
        int fn = _recentFleetNumbers[i];
        var rowR = new Rect(rx + 4, ry + 16 + i * 16, rw - 8, 15);
        bool hovered = rowR.Contains(Event.current.mousePosition);
        if (hovered) MDT_UITheme.DrawRect(rowR, MDT_UITheme.BGPillSel);

        GUI.Label(rowR, $"Fleet #{fn}",
            MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft,
                hovered ? MDT_UITheme.TextWhite : MDT_UITheme.TextSecond));

        if (GUI.Button(rowR, GUIContent.none, GUIStyle.none))
        {
            foreach (var chip in _chips)
            {
                if (chip.fleetNumber != fn) continue;
                _followPlayer = true;
                _followBusID  = chip.isPlayer ? -1 : fn;
                _worldCentre  = chip.worldPos;
                break;
            }
        }
    }
}
    // ── Header ────────────────────────────────────────────────────────────────
    private void DrawHeader(int w)
    {
        var hr = new Rect(0, 0, w, HEADER_H);
        MDT_UITheme.DrawHeader(hr);

        var player = PlayerHandoff.Instance;
        var currentLeg = GetPlayerCurrentLeg();
        string routeStr = currentLeg != null && !string.IsNullOrEmpty(currentLeg.routeNumber)
            ? currentLeg.routeNumber : "—";

        Color badgeColor = MDT_UITheme.BGPillSel;
        if (currentLeg != null && City?.routes != null)
            foreach (var r in City.routes)
                if (r != null && r.routeNumber == currentLeg.routeNumber)
                { badgeColor = Color.Lerp(r.routeColor, Color.black, 0.45f); badgeColor.a = 1f; break; }

        MDT_UITheme.DrawRouteBadge(new Rect(8, 7, 38, 20), routeStr, badgeColor, _styleClock);
        GUI.Label(new Rect(54, 6, 160, 22), "LIVE TRANSIT MAP", _styleHeader);

        if (currentLeg != null && !string.IsNullOrEmpty(currentLeg.routeNumber))
        {
            string dir = currentLeg.isOutbound ? "A → Z" : "Z → A";
            GUI.Label(new Rect(54, 18, 160, 14), dir,
                MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));
        }

        // [3D] Toggle between flat 2D map and orbiting 3D view
        if (GUI.Button(new Rect(w - 294, 7, 58, 20),
            _mode3D ? "🧊 3D" : "▦ 2D",
            MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleCenter,
                _mode3D ? MDT_UITheme.TextCyan : MDT_UITheme.TextDim)))
        {
            Toggle3DMode();
        }

        // [MAP-2] Follow lock button
        Color followCol = _followPlayer ? MDT_UITheme.TextGreen : MDT_UITheme.TextDim;
        string followLabel = _followPlayer
            ? (_followBusID >= 0 ? $"🔒#{_followBusID}" : "🔒 PLAYER")
            : "🔓 FREE";

        if (GUI.Button(new Rect(w - 230, 7, 78, 20),
            followLabel,
            MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleCenter, followCol)))
        {
            _followPlayer = !_followPlayer;
            if (_followPlayer) _followBusID = -1;
        }

        // [MAP-3] Route filter button
        if (GUI.Button(new Rect(w - 148, 7, 60, 20),
            FilterLabel(),
            MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextAmber)))
            CycleRouteFilter();

        // [MAP-7] Direction filter button
        if (GUI.Button(new Rect(w - 84, 7, 60, 20),
            DirectionFilterLabel(),
            MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextCyan)))
            CycleDirectionFilter();

        // Clock
        string time = BusScheduler.Instance != null
            ? $"{Mathf.FloorToInt(BusScheduler.Instance.GameTimeMinutes / 60f) % 24:D2}:" +
              $"{Mathf.FloorToInt(BusScheduler.Instance.GameTimeMinutes) % 60:D2}"
            : "—";
        GUI.Label(new Rect(w - 82, 5, 64, 24), time, _styleClock);

        bool fresh = _refreshTimer > refreshInterval - 0.5f;
        MDT_UITheme.DrawLED(new Vector2(w - 10, HEADER_H * 0.5f), 4f,
            fresh ? MDT_UITheme.LEDGreen : MDT_UITheme.LEDOff);

        // [MAP-5] Search bar
        DrawSearchBar(w);
    }

    // ── [MAP-5] Search bar ────────────────────────────────────────────────────
    private void DrawSearchBar(int w)
    {
        var searchRect = new Rect(54, 18, 110, 14);
        // Draw over the direction text
        MDT_UITheme.DrawRect(searchRect, MDT_UITheme.BGDeep);
        MDT_UITheme.DrawRect(new Rect(searchRect.x, searchRect.yMax - 1, searchRect.width, 1),
            MDT_UITheme.TextCyan * new Color(1, 1, 1, 0.3f));

        GUI.SetNextControlName(SearchControl);
        string newSearch = GUI.TextField(searchRect, _searchText, _searchStyle);

        if (string.IsNullOrEmpty(_searchText) && Event.current.type == EventType.Repaint)
            GUI.Label(searchRect, " search fleet# / route…",
                MDT_UITheme.MakeLabel(7, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));

        if (newSearch != _searchText)
        {
            _searchText = newSearch;
            ProcessSearch();
        }

        // X clear button
        if (!string.IsNullOrEmpty(_searchText))
        {
            if (GUI.Button(new Rect(searchRect.xMax + 2, searchRect.y, 12, searchRect.height),
                "×", MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextDim)))
            {
                _searchText        = "";
                _showSearchResults = false;
                _searchResults.Clear();
            }
        }
    }

    private void ProcessSearch()
    {
        _searchResults.Clear();
        _showSearchResults = false;

        if (string.IsNullOrEmpty(_searchText)) return;

        string s = _searchText.Trim();

        // Try fleet number first
        if (int.TryParse(s, out int fleetNum))
        {
            foreach (var chip in _chips)
            {
                if (chip.fleetNumber == fleetNum)
                {
                    // Lock follow on this bus
                    _followPlayer = true;
                    _followBusID  = fleetNum;
                    _worldCentre  = chip.worldPos;
                    return;
                }
            }
        }

        // Otherwise treat as route number — list all buses on that route
        foreach (var chip in _chips)
        {
            if (chip.routeNumber.Equals(s, System.StringComparison.OrdinalIgnoreCase))
                _searchResults.Add(chip);
        }

        if (_searchResults.Count > 0)
            _showSearchResults = true;
    }

    private void DrawSearchResults(int w)
    {
        if (!_showSearchResults || _searchResults.Count == 0) return;

        float rx = 54f;
        float ry = HEADER_H + 2f;
        float rw = 130f;
        float rh = _searchResults.Count * 20f + 6f;

        MDT_UITheme.DrawRect(new Rect(rx, ry, rw, rh), MDT_UITheme.BGDeep);
        MDT_UITheme.DrawRect(new Rect(rx, ry, rw, 1), MDT_UITheme.TextCyan * new Color(1, 1, 1, 0.4f));

        for (int i = 0; i < _searchResults.Count; i++)
        {
            var chip = _searchResults[i];
            var rowR = new Rect(rx + 2, ry + 3 + i * 20, rw - 4, 18);

            bool hovered = rowR.Contains(Event.current.mousePosition);
            if (hovered) MDT_UITheme.DrawRect(rowR, MDT_UITheme.BGPillSel);

            string label = chip.isPlayer
                ? $"Fleet #{chip.fleetNumber} (YOU)"
                : $"Fleet #{chip.fleetNumber}  {(chip.isOutbound ? "A→Z" : "Z→A")}";

            GUI.Label(rowR, label,
                MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft,
                    hovered ? MDT_UITheme.TextWhite : MDT_UITheme.TextSecond));

            if (GUI.Button(rowR, GUIContent.none, GUIStyle.none))
            {
                _followPlayer      = true;
                _followBusID       = chip.fleetNumber;
                _worldCentre       = chip.worldPos;
                _showSearchResults = false;
            }
        }
    }
private void HandleInput(int w, int mh)
{
    var mapRect = new Rect(0, HEADER_H, w, mh);
    var ev      = Event.current;

    if (ev.type == EventType.ScrollWheel && mapRect.Contains(ev.mousePosition))
    {
        float delta = -ev.delta.y * zoomStep;
        _zoom = Mathf.Clamp(_zoom + delta, zoomMin, zoomMax);
        ev.Use();
    }

    // Area-label coordinate picker: while on, a left-click anywhere on the map
    // just logs a ready-to-paste MapAreaLabel entry instead of doing anything
    // else (no favoriting, no chip/stop selection, no pan-drag) — a quick way
    // to grab world coordinates for a new area name without doing the math by
    // hand. Turn areaLabelPickerMode off when you're done placing labels.
    if (areaLabelPickerMode && ev.type == EventType.MouseDown && ev.button == 0 && mapRect.Contains(ev.mousePosition))
    {
        Vector2 localMouse = ev.mousePosition - new Vector2(0, HEADER_H);
        Vector3 worldPos   = ScreenToWorld(localMouse, w, mh);
        Debug.Log($"[MDT_LiveMap] Area label coords — paste into areaLabels:\n" +
                  $"new MapAreaLabel {{ areaName = \"NAME_HERE\", worldPosition = new Vector3({worldPos.x:F1}f, {worldPos.y:F1}f, {worldPos.z:F1}f) }}");
        ev.Use();
        return;
    }

    // QoL: right-click a stop to toggle favorite, independent of the normal
    // left-click popup flow so it never interferes with it.
    if (ev.type == EventType.MouseDown && ev.button == 1 && mapRect.Contains(ev.mousePosition))
    {
        Vector2 localMouse = ev.mousePosition - new Vector2(0, HEADER_H);
        foreach (var sd in _stopDots)
        {
            Vector2 sp = WorldToScreen(sd.worldPos, w, mh);
            float   r  = sd.isTerminal ? 8f : 6f;
            if (Vector2.Distance(localMouse, sp) < r)
            {
                ToggleFavoriteStop(sd.stopCode);
                break;
            }
        }
        ev.Use();
    }

    if (ev.type == EventType.MouseDown && ev.button == 0 && mapRect.Contains(ev.mousePosition))
    {
        if (Time.realtimeSinceStartup - _lastClickTime < 0.3f)
        {
            _panOffset    = Vector2.zero;
            _followPlayer = true;
            _followBusID  = -1;
        }
        _lastClickTime = Time.realtimeSinceStartup;

        Vector2 localMouse = ev.mousePosition - new Vector2(0, HEADER_H);

        bool hitChip = false;
        foreach (var chip in _chips)
        {
            Vector2 sp = WorldToScreen(chip.worldPos, w, mh);
            if (Vector2.Distance(localMouse, sp) < CHIP_W * 0.6f)
            {
                OpenEtaPopup(chip, sp + new Vector2(0, HEADER_H));
                hitChip = true;
                _followPlayer = true;
                _followBusID  = chip.isPlayer ? -1 : chip.fleetNumber;
                RecordRecentBus(chip.fleetNumber);
                break;
            }
        }

        bool hitStop = false;
        if (!hitChip)
        {
            foreach (var sd in _stopDots)
            {
                Vector2 sp = WorldToScreen(sd.worldPos, w, mh);
                float   r  = sd.isTerminal ? 8f : 6f;
                if (Vector2.Distance(localMouse, sp) < r)
                {
                    _etaPopupFleet = -1;
                    SelectStopForTracker(sd.stopCode, sd.stopName);
                    hitStop = true;
                    break;
                }
            }
        }

        if (!hitChip && !hitStop)
        {
            _etaPopupFleet = -1;
            _dragging  = true;
            _dragStart = ev.mousePosition;
            _panAtDrag = _panOffset;
            if (_followPlayer) _followPlayer = false;
        }

        ev.Use();
    }

    if (ev.type == EventType.MouseDrag && _dragging)
    {
        _panOffset = _panAtDrag + (ev.mousePosition - _dragStart);
        ev.Use();
    }

    if (ev.type == EventType.MouseUp && _dragging)
    {
        _dragging = false;
        ev.Use();
    }
}
    // ── Map area ──────────────────────────────────────────────────────────────
    private void DrawMapArea(int w, int mh)
    {
        // [PERF FIX] OnGUI fires once per Layout event, once per Repaint
        // event, AND once per raw input event (MouseMove/MouseDrag/
        // ScrollWheel/KeyDown all trigger a full extra OnGUI call) -- so
        // every method this touches used to run several times per RENDERED
        // frame, not once, worst-case (and worst-case is exactly "dragging
        // to pan the map," which is when a player is actually looking at
        // it). This method (and the other pure-drawing Draw* methods below)
        // only need to actually run once per rendered frame -- there's only
        // ever one Repaint event per frame. _hoveredBus/_hoveredStopName
        // (this method's only state writes) only feed the tooltip, so
        // updating them once per rendered frame instead of on every mouse-
        // move event is imperceptible.
        if (Event.current.type != EventType.Repaint) return;

        var mapRect = new Rect(0, HEADER_H, w, mh);
        MDT_UITheme.DrawInset(mapRect, MDT_UITheme.BGMid);
        DrawGrid(w, mh);
        DrawCompass(w, mh);

        // [ADD] World-space view bounds, computed ONCE per Repaint (cheap,
        // O(1) -- just the two rect corners through ScreenToWorld) instead
        // of per-point. Every road/route polyline's precomputed AABB
        // (RoutePolyline.minB/maxB) gets tested against this before its
        // per-point loop ever runs in DrawPolyline -- an entirely
        // off-screen road now costs one bounds comparison instead of a
        // WorldToScreen call per point. A small pad keeps a polyline that
        // just clips the edge from popping in/out.
        const float viewPad = 40f;
        Vector3 viewA = ScreenToWorld(new Vector2(-viewPad, -viewPad), w, mh);
        Vector3 viewB = ScreenToWorld(new Vector2(w + viewPad, mh + viewPad), w, mh);
        Vector2 viewMin = new Vector2(Mathf.Min(viewA.x, viewB.x), Mathf.Min(viewA.z, viewB.z));
        Vector2 viewMax = new Vector2(Mathf.Max(viewA.x, viewB.x), Mathf.Max(viewA.z, viewB.z));

        bool InView(in RoutePolyline rl) =>
            rl.maxB.x >= viewMin.x && rl.minB.x <= viewMax.x &&
            rl.maxB.y >= viewMin.y && rl.minB.y <= viewMax.y;

        // Pass 0: city grid
        foreach (var rl in _roadLines)
            if (rl.layer == 0 && InView(rl)) DrawPolyline(rl.pts, rl.col, 1.8f, w, mh);

        // Pass 1: non-active transit routes (filtered/faded)
        float nonHLAlpha = _routeFilterIndex == -2
            ? Mathf.Lerp(0.50f, 0.20f, (_zoom - 1f) / (zoomMax - 1f))
            : 0.08f; // nearly invisible when filter is active

        foreach (var rl in _roadLines)
        {
            if (rl.layer == 1 && InView(rl))
            {
                Color faded = new Color(rl.col.r, rl.col.g, rl.col.b, nonHLAlpha);
                DrawPolyline(rl.pts, faded, 2.5f, w, mh);
            }
        }

        // Pass 2: highlighted / filtered route
        foreach (var rl in _roadLines)
            if (rl.layer == 2 && InView(rl)) DrawPolyline(rl.pts, rl.col, 4.0f, w, mh);

        // [MAP-7] Draw road junctions (where roads meet)
        foreach (var junctionPos in _roadJunctions)
        {
            Vector2 sp = WorldToScreen(junctionPos, w, mh);
            if (sp.x >= -8 && sp.x <= w + 8 && sp.y >= -8 && sp.y <= mh + 8)
            {
                Vector2 screenSp = sp + new Vector2(0, HEADER_H);
                // Small pulsing indicator
                float pulse = 0.5f + Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 2f)) * 0.5f;
                MDT_UITheme.DrawRect(new Rect(screenSp.x - 2, screenSp.y - 2, 4, 4),
                    new Color(0.6f, 0.8f, 1f, 0.6f * pulse));
            }
        }

        // Stops
        _hoveredStopName = "";
        _hoveredBus      = null;
        Vector2 localMouse = Event.current.mousePosition - new Vector2(0, HEADER_H);

        foreach (var sd in _stopDots)
        {
            Vector2 sp = WorldToScreen(sd.worldPos, w, mh);
            if (sp.x < -12 || sp.x > w + 12 || sp.y < -12 || sp.y > mh + 12) continue;

            Vector2 screenSp = sp + new Vector2(0, HEADER_H);
            float r = sd.isTerminal ? 5.5f : 3f;

            if (sd.isTerminal)
            {
                MDT_UITheme.DrawRect(new Rect(screenSp.x - r - 4, screenSp.y - r - 4, (r + 4) * 2, (r + 4) * 2), MDT_UITheme.TermGlow);
                MDT_UITheme.DrawRect(new Rect(screenSp.x - r - 1, screenSp.y - r - 1, (r + 1) * 2, (r + 1) * 2), new Color(1f, 0.9f, 0.2f, 0.7f));
            }

            // [MAP-6] Subtle highlight ring if this is the stop currently popped open
            if (!string.IsNullOrEmpty(_selectedStopCode) && sd.stopCode == _selectedStopCode)
                MDT_UITheme.DrawRect(new Rect(screenSp.x - r - 3, screenSp.y - r - 3, (r + 3) * 2, (r + 3) * 2),
                    new Color(0.25f, 0.82f, 1f, 0.35f));

            MDT_UITheme.DrawRect(new Rect(screenSp.x - r, screenSp.y - r, r * 2, r * 2),
                sd.isTerminal ? MDT_UITheme.TermDot : MDT_UITheme.StopDot);

            if (Vector2.Distance(localMouse, sp) < Mathf.Max(r + 4, 10f))
                _hoveredStopName = sd.stopName;
        }

        // Bus chips
        foreach (var chip in _chips)
        {
            // [MAP-3] Filter dim
            if (!IsRouteVisible(chip.routeNumber) && _routeFilterIndex != -2) continue;

            Vector2 sp = WorldToScreen(chip.worldPos, w, mh);
            if (sp.x < -CHIP_W || sp.x > w + CHIP_W || sp.y < -CHIP_H || sp.y > mh + CHIP_H) continue;

            // Highlight followed bus
            bool isFollowed = _followPlayer && (
                (chip.isPlayer && _followBusID < 0) ||
                (!chip.isPlayer && chip.fleetNumber == _followBusID));

            DrawBusChip(sp + new Vector2(0, HEADER_H), chip, isFollowed);

            if (Vector2.Distance(localMouse, sp) < CHIP_W * 0.6f)
                _hoveredBus = chip;
        }

        DrawRoadEventChips(w, mh);

        float worldVis = (viewRadius * 2f) / _zoom;
        MDT_UITheme.DrawScaleBar(new Rect(mapRect.x + 8, mapRect.y, w - 16, mh), worldVis, _styleLegend);
    }

    // ── [MAP-4] ETA popup (single bus) ────────────────────────────────────────
    private void OpenEtaPopup(BusChip chip, Vector2 screenPos)
    {
        if (_etaPopupFleet == chip.fleetNumber) { _etaPopupFleet = -1; return; } // toggle off

        _etaPopupFleet    = chip.fleetNumber;
        _etaPopupScreenPos = screenPos;
        _etaPopupArrivals.Clear();
        _etaPopupNextStop = "";

        // Find the bus's current next stop
        NPCBusController ctrl = null;
        foreach (var kv in BusRegistry.ActiveBuses)
            if (kv.Value != null && kv.Value.fleetNumber == chip.fleetNumber)
            { ctrl = kv.Value; break; }

        if (ctrl?.CurrentRoute == null || BusTrackerService.Instance == null) return;

        var route = ctrl.CurrentRoute;
        var stops = ctrl.IsOutbound ? route.resolvedOutboundStops : route.resolvedInboundStops;
        int nextIdx = Mathf.Clamp(ctrl.NextStopIndex, 0, (stops?.Count ?? 1) - 1);

        if (stops != null && nextIdx < stops.Count)
        {
            _etaPopupNextStop = stops[nextIdx].stopName ?? stops[nextIdx].stopCode;
            _etaPopupArrivals = BusTrackerService.Instance.GetNextArrivals(
                route, ctrl.IsOutbound, stops[nextIdx].stopCode);
        }
    }

    private void DrawEtaPopup(int w, int mh)
    {
        if (Event.current.type != EventType.Repaint) return; // [PERF FIX] see DrawMapArea
        if (_etaPopupFleet < 0) return;

        float pw = 160f;
        float ph = 20f + _etaPopupArrivals.Count * 18f + 10f;
        float px = Mathf.Clamp(_etaPopupScreenPos.x + 6, 2, w - pw - 2);
        float py = Mathf.Clamp(_etaPopupScreenPos.y - ph * 0.5f, HEADER_H + 2, mh + HEADER_H - ph - 2);

        MDT_UITheme.DrawRect(new Rect(px - 1, py - 1, pw + 2, ph + 2),
            MDT_UITheme.TextCyan * new Color(1, 1, 1, 0.4f));
        MDT_UITheme.DrawRect(new Rect(px, py, pw, ph), MDT_UITheme.BGDeep);

        GUI.Label(new Rect(px + 4, py + 3, pw - 8, 14),
            $"Fleet #{_etaPopupFleet} — next stop:",
            MDT_UITheme.MakeLabel(7, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan));

        GUI.Label(new Rect(px + 4, py + 14, pw - 8, 12),
            _etaPopupNextStop,
            MDT_UITheme.MakeLabel(7, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextAmber));

        for (int i = 0; i < _etaPopupArrivals.Count; i++)
        {
            GUI.Label(new Rect(px + 4, py + 26 + i * 18, pw - 8, 16),
                _etaPopupArrivals[i],
                MDT_UITheme.MakeLabel(9, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextSecond));
        }

        if (_etaPopupArrivals.Count == 0)
        {
            GUI.Label(new Rect(px + 4, py + 26, pw - 8, 16), "No arrivals found.",
                MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));
        }
    }

    // ── [REWRITE] Stop click now forwards to the docked tracker instead of
    //    drawing its own popup on the map. If the map is currently shown
    //    alone (ViewMode.Map), auto-switch to Both so the tracker becomes
    //    visible — otherwise a click would silently do nothing the user
    //    could see. ──────────────────────────────────────────────────────────
    private void SelectStopForTracker(string stopCode, string stopName)
    {
        _selectedStopCode = stopCode;

        if (listController == null) listController = GetComponent<MDT_UI_Controller>();
        if (listController == null) listController = FindObjectOfType<MDT_UI_Controller>();

        if (listController == null)
        {
            Debug.LogWarning($"[MDT_LiveMap] Stop '{stopCode}' ({stopName}) clicked, but no MDT_UI_Controller " +
                              "was found to forward it to — arrivals have nowhere to render. " +
                              "Assign 'listController' or ensure one exists in the scene.");
            return;
        }

        if (_viewMode == ViewMode.Map)
        {
            _viewMode  = ViewMode.Both;
            _dataDirty = true;
            listController.dockedMode = true;
        }

        Debug.Log($"[MDT_LiveMap] Stop '{stopCode}' ({stopName}) clicked — forwarding to tracker panel.");
        listController.ShowStopBoard(stopCode, stopName);
    }

    // ── Road event chips ──────────────────────────────────────────────────────
    public void OnRoadEventActivated(RoadEvent ev) => _dataDirty = true;
    public void OnRoadEventCleared(RoadEvent ev)   => _dataDirty = true;

    private void DrawRoadEventChips(int w, int mh)
    {
        foreach (var ev in _activeEvents)
        {
            if (ev == null || !ev.isActive) continue;
            Vector2 sp = WorldToScreen(ev.eventWorldPosition, w, mh);
            if (sp.x < -40 || sp.x > w + 40 || sp.y < -20 || sp.y > mh + 20) continue;

            Vector2 screenSp = sp + new Vector2(0, HEADER_H);
            Color chipCol = ev.severity switch
            {
                RoadEventSeverity.Minor    => new Color(1f, 1f, 0f, 0.90f),
                RoadEventSeverity.Moderate => new Color(1f, 0.55f, 0f, 0.90f),
                RoadEventSeverity.Major    => new Color(1f, 0.15f, 0.15f, 0.95f),
                _                          => Color.white
            };

            float pulse = Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 2.5f));
            float ringR = 10f + pulse * 4f;
            MDT_UITheme.DrawRect(
                new Rect(screenSp.x - ringR, screenSp.y - ringR, ringR * 2, ringR * 2),
                new Color(chipCol.r, chipCol.g, chipCol.b, 0.25f * pulse));
            MDT_UITheme.DrawRect(new Rect(screenSp.x - 6, screenSp.y - 6, 12, 12), chipCol);

            float rem    = ev.RemainingMinutes();
            string label = ev.GetMapLabel() + (rem > 0 ? $" {rem:F0}m" : "");
            GUI.Label(new Rect(screenSp.x - 50, screenSp.y + 8, 100, 14), label,
                MDT_UITheme.MakeLabel(7, FontStyle.Bold, TextAnchor.MiddleCenter, chipCol));
        }
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────
    private void DrawTooltip(int w, int mh)
    {
        if (Event.current.type != EventType.Repaint) return; // [PERF FIX] see DrawMapArea
        if (_etaPopupFleet >= 0) return; // suppress tooltip while the bus ETA popup is open

        if (_hoveredBus.HasValue)
        {
            var chip    = _hoveredBus.Value;
            string variant = string.IsNullOrEmpty(chip.variantLetter) ? "" : $" [{chip.variantLetter}]";
            string text = $"Fleet #{chip.fleetNumber}\nRte {chip.routeNumber}{variant}\n" +
                          (chip.isOutbound ? "A → Z (Outbound)" : "Z → A (Inbound)") +
                          "\n[click] lock & show ETA";
            MDT_UITheme.DrawTooltip(Event.current.mousePosition, text, _styleTooltip);
        }
        else if (!string.IsNullOrEmpty(_hoveredStopName))
        {
            MDT_UITheme.DrawTooltip(Event.current.mousePosition, _hoveredStopName + "\n[click] all routes here", _styleTooltip);
        }
    }

    // ── Legend / footer ───────────────────────────────────────────────────────
    private void DrawLegend(int w, int mh)
    {
        if (Event.current.type != EventType.Repaint) return; // [PERF FIX] see DrawMapArea
        var r = new Rect(0, HEADER_H + mh, w, LEGEND_H);
        MDT_UITheme.DrawRect(r, MDT_UITheme.BGFooter);
        MDT_UITheme.DrawDivider(r.x, r.y, r.width);

        GUI.Label(new Rect(r.x + 8, r.y + 4, 80, 16), $"⊕ {_zoom:F1}×", _styleLegend);
        GUI.Label(new Rect(r.x + 86, r.y + 4, 260, 16),
            "scroll=zoom  drag=pan  dbl=centre  [F]=follow  [R]=filter  [D]=dir  [C]=centre", _styleLegend);

        // Follow indicator
        string followStr = _followPlayer
            ? (_followBusID >= 0 ? $"↻ #{_followBusID}" : "↻ player")
            : "free";
        Color followCol = _followPlayer ? MDT_UITheme.TextGreen : MDT_UITheme.TextDim;
        GUI.Label(new Rect(r.xMax - 80, r.y + 4, 76, 16), followStr,
            MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleRight, followCol));

        float remaining = Mathf.Max(0, _refreshTimer);
        GUI.Label(new Rect(r.xMax - 56, r.y + 4, 52, 16), $"↻ {remaining:F0}s", _styleLegend);
    }

    // ── Compass ───────────────────────────────────────────────────────────────
    private void DrawCompass(int w, int mh)
    {
        float cx     = w - 22f;
        float cy     = HEADER_H + 22f;
        float radius = 14f;

        MDT_UITheme.DrawRect(new Rect(cx - radius - 4, cy - radius - 4,
            (radius + 4) * 2, (radius + 4) * 2), new Color(0, 0, 0, 0.30f));

        MDT_UITheme.DrawLine(new Vector2(cx, cy), new Vector2(cx, cy - radius),
            new Color(0.25f, 0.82f, 1f, 0.85f), 2f);
        MDT_UITheme.DrawLine(new Vector2(cx, cy), new Vector2(cx, cy + radius * 0.6f),
            new Color(0.6f, 0.6f, 0.6f, 0.5f), 1.5f);

        GUI.Label(new Rect(cx - 10, cy - radius - 14, 20, 14), "N",
            MDT_UITheme.MakeLabel(8, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextCyan));
    }

    // ── Bus chip ──────────────────────────────────────────────────────────────
    private void DrawBusChip(Vector2 centre, BusChip chip, bool isFollowed)
    {
        float x = centre.x - CHIP_W * 0.5f;
        float y = centre.y - CHIP_H * 0.5f;

        MDT_UITheme.DrawRect(new Rect(x + 2, y + 3, CHIP_W, CHIP_H), MDT_UITheme.ChipShadow);

        Color border = chip.isPlayer
            ? MDT_UITheme.ChipPlayer
            : isFollowed
                ? Color.white
                : Color.Lerp(chip.routeColor, Color.white, 0.35f);
        MDT_UITheme.DrawRect(new Rect(x - 1, y - 1, CHIP_W + 2, CHIP_H + 2), border);

        // Extra glow ring for followed bus
        if (isFollowed)
            MDT_UITheme.DrawRect(new Rect(x - 3, y - 3, CHIP_W + 6, CHIP_H + 6),
                new Color(1f, 1f, 1f, 0.15f));

        Color body = Color.Lerp(chip.routeColor, Color.black, 0.58f);
        body.a = 1f;
        MDT_UITheme.DrawRect(new Rect(x, y, CHIP_W, CHIP_H), body);
        MDT_UITheme.DrawRect(new Rect(x, y, CHIP_W, 2), new Color(1f, 1f, 1f, 0.12f));

        // [PERF FIX] Use pre-built cached strings instead of concatenating/
        // interpolating fresh ones on every single draw call -- see the
        // BusChip struct's BuildDisplayStrings() comment.
        GUI.Label(new Rect(x + 2, y + 1, CHIP_W - 4, 13), chip.dirLabelCached, _chipDir);
        GUI.Label(new Rect(x + 2, y + 1, CHIP_W - 4, 13), chip.routeDisplayCached, _chipRoute);
        GUI.Label(new Rect(x, y + 14, CHIP_W, 20), chip.fleetNumberCached, _chipFleet);
    }

    // ── Polyline ──────────────────────────────────────────────────────────────
    private void DrawPolyline(Vector3[] worldPts, Color col, float thickness, int w, int mh)
    {
        if (worldPts == null || worldPts.Length < 2) return;

        // [PERF FIX] This drew every segment of every road line in the
        // entire city on every repaint, with zero screen-bounds culling --
        // unlike the stop dots and bus chips in DrawMapArea just above,
        // which already skip anything outside the visible map rect before
        // drawing. Stops/buses are cheap point-sprites, but roads are line
        // segments spanning the whole city, and each segment cost TWO
        // DrawLine calls (a shadow pass plus the color pass). At full city
        // scale this is very likely the dominant reason the 2D map runs so
        // much slower than everything else -- most segments are nowhere
        // near the current pan/zoom window, especially once zoomed in, yet
        // all of them were still being drawn regardless.
        //
        // Cull per-segment using a padded screen rect rather than a tight
        // one, so a segment with only one endpoint just off-screen still
        // draws correctly (this is a bounding check, not true line
        // clipping -- an unusually long single segment whose endpoints
        // both sit outside the view on opposite sides, while the middle of
        // the segment passes through it, would be incorrectly skipped; for
        // typical multi-point city road polylines this doesn't come up in
        // practice, but flagging it in case any road is represented as one
        // very long two-point segment).
        const float pad = 24f;
        var screenRect = new Rect(-pad, HEADER_H - pad, w + pad * 2f, mh + pad * 2f);

        // [ADD] Soft edge fade -- roads used to get a harsh, visible cut the
        // instant a point crossed the actual panel boundary (0..w, HEADER_H
        // ..HEADER_H+mh), which reads as "broken"/unfinished right at the
        // map's edge. Fades alpha to 0 over the last fadeZonePx pixels
        // approaching the edge instead, so roads/routes trail off smoothly.
        // Deliberately measured against the REAL visible rect, not the
        // padded cull rect above -- the cull rect exists so an on-screen
        // segment with an off-screen endpoint still gets included in the
        // loop at all; the fade is a separate, tighter measurement of how
        // close to the actual visible edge a point is.
        const float fadeZonePx = 28f;
        float EdgeFade(Vector2 p)
        {
            float distX = Mathf.Min(p.x, w - p.x);
            float distY = Mathf.Min(p.y - HEADER_H, (HEADER_H + mh) - p.y);
            float edgeDist = Mathf.Min(distX, distY);
            return Mathf.Clamp01(edgeDist / fadeZonePx);
        }

        for (int i = 0; i < worldPts.Length - 1; i++)
        {
            Vector2 a = WorldToScreen(worldPts[i],     w, mh) + new Vector2(0, HEADER_H);
            Vector2 b = WorldToScreen(worldPts[i + 1], w, mh) + new Vector2(0, HEADER_H);

            if (!screenRect.Contains(a) && !screenRect.Contains(b)) continue;

            // Weakest point of the segment sets the fade -- a segment with
            // one endpoint right at the edge fades accordingly even if its
            // other endpoint is well inside.
            float fade = Mathf.Min(EdgeFade(a), EdgeFade(b));
            if (fade <= 0.001f) continue;

            MDT_UITheme.DrawLine(a, b, new Color(0, 0, 0, 0.35f * fade), thickness + 1.5f);
            MDT_UITheme.DrawLine(a, b, new Color(col.r, col.g, col.b, col.a * fade), thickness);
        }

        // [MAP-7] Draw direction arrows along the polyline
        if (thickness > 3.2f && worldPts.Length > 4) // only for prominent routes
        {
            for (int i = 1; i < worldPts.Length - 1; i += Mathf.Max(2, (worldPts.Length - 1) / 5))
            {
                Vector2 p0 = WorldToScreen(worldPts[i - 1], w, mh) + new Vector2(0, HEADER_H);
                Vector2 p1 = WorldToScreen(worldPts[i],     w, mh) + new Vector2(0, HEADER_H);
                Vector2 p2 = WorldToScreen(worldPts[i + 1], w, mh) + new Vector2(0, HEADER_H);

                // [PERF FIX] Same culling as above -- skip arrows whose
                // center point isn't anywhere near the visible area.
                if (!screenRect.Contains(p1)) continue;

                // [ADD] Same edge fade as the line segments -- an arrow
                // popping fully-opaque right at the fading edge of its own
                // route line looked inconsistent otherwise.
                float arrowFade = EdgeFade(p1);
                if (arrowFade <= 0.001f) continue;

                // Direction at p1
                Vector2 dir = (p2 - p0).normalized;
                if (dir.magnitude < 0.01f) continue;

                // Draw arrow
                Vector2 perp = new Vector2(-dir.y, dir.x);
                Vector2 arrowBase = p1 - dir * 4f;
                Vector2 arrowLeft = arrowBase + (dir + perp).normalized * 3f;
                Vector2 arrowRight = arrowBase + (dir - perp).normalized * 3f;

                Color arrowCol = new Color(col.r, col.g, col.b, col.a * arrowFade);
                MDT_UITheme.DrawLine(p1, arrowLeft, arrowCol, thickness * 0.6f);
                MDT_UITheme.DrawLine(p1, arrowRight, arrowCol, thickness * 0.6f);
            }
        }
    }

    // ── Grid ──────────────────────────────────────────────────────────────────
    private void DrawGrid(int w, int mh)
    {
        float ox = _panOffset.x % 40f;
        float oy = _panOffset.y % 40f;

        for (float gx = ox; gx < w;  gx += 40)
            MDT_UITheme.DrawLine(new Vector2(gx, HEADER_H),
                new Vector2(gx, HEADER_H + mh), MDT_UITheme.Grid, 1f);
        for (float gy = oy; gy < mh; gy += 40)
            MDT_UITheme.DrawLine(new Vector2(0, HEADER_H + gy),
                new Vector2(w, HEADER_H + gy), MDT_UITheme.Grid, 1f);
    }

    // ── Coordinate transform ──────────────────────────────────────────────────
    private Vector2 WorldToScreen(Vector3 world, int w, int mh)
    {
        float baseScale = Mathf.Min(w, mh) * 0.45f / Mathf.Max(viewRadius, 1f);
        float scale     = baseScale * _zoom;

        float dx =  (world.x - _worldCentre.x) * scale;
        float dz = -(world.z - _worldCentre.z) * scale;

        return new Vector2(w  * 0.5f + dx + _panOffset.x,
                           mh * 0.5f + dz + _panOffset.y);
    }

    /// <summary>Inverse of WorldToScreen — takes a map-group-local screen point
    /// (i.e. mouse position minus the HEADER_H offset, same convention HandleInput
    /// already uses) and returns the world position under it, on the Y=0 plane.
    /// Only used by areaLabelPickerMode below; nothing else needs it yet.</summary>
    private Vector3 ScreenToWorld(Vector2 localScreen, int w, int mh)
    {
        float baseScale = Mathf.Min(w, mh) * 0.45f / Mathf.Max(viewRadius, 1f);
        float scale     = baseScale * _zoom;
        if (scale <= 0.0001f) scale = 0.0001f;

        float dx = localScreen.x - w  * 0.5f - _panOffset.x;
        float dz = localScreen.y - mh * 0.5f - _panOffset.y;

        return new Vector3(_worldCentre.x + dx / scale, _worldCentre.y, _worldCentre.z - dz / scale);
    }

    /// <summary>Soft background labels for named areas/neighborhoods — drawn
    /// early (right after the base map/road grid, below bus chips and trails)
    /// so they read as orientation context, not foreground UI.</summary>
    private void DrawAreaLabels(int w, int mh)
    {
        if (Event.current.type != EventType.Repaint) return; // [PERF FIX] see DrawMapArea
        if (areaLabels == null || areaLabels.Count == 0) return;

        foreach (var area in areaLabels)
        {
            if (area == null || string.IsNullOrEmpty(area.areaName)) continue;
            if (_zoom < area.minZoomToShow || _zoom > area.maxZoomToShow) continue;

            Vector2 sp = WorldToScreen(area.worldPosition, w, mh);
            if (sp.x < -100 || sp.x > w + 100 || sp.y < -20 || sp.y > mh + 20) continue;
            Vector2 screenSp = sp + new Vector2(0, HEADER_H);

            DrawAreaChip(screenSp, area);
        }
    }

    /// <summary>Same visual language as DrawBusChip — shadow, coloured border,
    /// darkened solid body, thin top highlight — so a named area reads as
    /// clearly as a bus chip even sitting in the middle of a dense cluster of
    /// them, instead of getting lost as plain floating text.</summary>
    private void DrawAreaChip(Vector2 centre, MapAreaLabel area)
    {
        int fontSize = Mathf.Clamp(area.baseFontSize, 8, 16);
        float chipW = Mathf.Clamp(area.areaName.Length * (fontSize * 0.62f) + 20f, 70f, 240f);
        float chipH = fontSize + 12f;

        float x = centre.x - chipW * 0.5f;
        float y = centre.y - chipH * 0.5f;

        MDT_UITheme.DrawRect(new Rect(x + 2, y + 3, chipW, chipH), MDT_UITheme.ChipShadow);

        Color border = Color.Lerp(area.labelColor, Color.white, 0.3f);
        border.a = 0.9f;
        MDT_UITheme.DrawRect(new Rect(x - 1, y - 1, chipW + 2, chipH + 2), border);

        Color body = Color.Lerp(area.labelColor, Color.black, 0.72f);
        body.a = 0.94f;
        MDT_UITheme.DrawRect(new Rect(x, y, chipW, chipH), body);
        MDT_UITheme.DrawRect(new Rect(x, y, chipW, 2), new Color(1f, 1f, 1f, 0.14f));

        GUI.Label(new Rect(x, y, chipW, chipH),
            area.areaName.ToUpperInvariant(),
            MDT_UITheme.MakeLabel(fontSize, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STYLE BUILDER
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        _styleHeader  = MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _styleClock   = MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextWhite);
        _styleLegend  = MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);

        _styleTooltip = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 10,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.MiddleLeft,
            padding   = new RectOffset(8, 8, 4, 4),
        };
        _styleTooltip.normal.textColor = MDT_UITheme.TextAmber;

        _chipDir = new GUIStyle(GUI.skin.label)
        { fontSize = 7, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
        _chipDir.normal.textColor = new Color(1f, 1f, 1f, 0.75f);

        _chipRoute = new GUIStyle(GUI.skin.label)
        { fontSize = 7, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight };
        _chipRoute.normal.textColor = Color.white;

        _chipFleet = new GUIStyle(GUI.skin.label)
        { fontSize = 11, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _chipFleet.normal.textColor = Color.white;

        _searchStyle = new GUIStyle(GUI.skin.textField)
        {
            fontSize  = 7,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.MiddleLeft,
        };
        _searchStyle.normal.textColor  = MDT_UITheme.TextPrimary;
        _searchStyle.focused.textColor = MDT_UITheme.TextWhite;

        _searchResultStyle = MDT_UITheme.MakeLabel(8, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextSecond);
    }
}