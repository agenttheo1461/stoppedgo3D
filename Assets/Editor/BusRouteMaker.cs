using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

// ⚠ REQUIRES PLACEMENT IN A FOLDER NAMED "Editor" ANYWHERE UNDER Assets/ ⚠
// Same reasoning as BusStopMaker.cs — EditorWindow doesn't exist in a
// built player, this file must live where Unity excludes it from builds.

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS ROUTE MAKER
//
//  In-game route authoring tool. Click stops on a live map (same visual
//  language as MDT_LiveMap, built on MDT_UITheme) instead of typing raw
//  Vector3 node coordinates by hand.
//
//  WORKFLOW
//  ────────
//   1. Press toggleKey (default U) to open the tool. Must be in Play Mode
//      with CityManager already resolved (its Start() must have run).
//   2. Click a stop on the map  → Terminal A.
//   3. Click a different stop   → Terminal Z.
//   4. "Outbound A → Z" stage: click stops in travel order. Every click
//      traces the road network from the previous stop to the new one and
//      appends RouteNodes automatically — see NODE GENERATION below.
//   5. "Finish Outbound" → automatically starts the inbound stage, seeded at
//      Terminal Z. Click stops Z → A the same way, "Finish Inbound" when done.
//   6. Details stage: route number/name/color, schedule, articulated policy,
//      per-stop minutesFromStart (auto-estimated, hand-editable), and
//      variants — each variant can optionally get its own outbound/inbound
//      override path built the same click-driven way.
//   7. "Create Route Asset" writes a fully-populated BusRouteData .asset into
//      Assets/Routes/ and selects it — everything is still hand-editable in
//      the Inspector afterward if it didn't come out quite right.
//
//  NODE GENERATION RULES
//  ──────────────────────
//   • Same road, straight stretch → ONE new node at the next stop. We don't
//     "register the whole road" — no dense sampling unless the road actually
//     curves.
//   • Same road, but it curves between the two stops (checked empirically by
//     comparing the spline to the straight chord between the two stops'
//     resolved t-values) → a handful of extra sample nodes are dropped along
//     the curve so the path still hugs the road.
//   • Different road → CityManager's auto-detected intersection list is
//     walked (BFS over an implicit road-adjacency graph built by checking
//     which intersection points sit on which road splines) to find the
//     shortest chain of roads connecting the two stops' roads. ONE corner
//     node is dropped per hop. On a normal orthogonal grid this means each
//     new node changes only X or only Z relative to the last node — never
//     both — exactly like:
//         (0,0,0) → (0,0,30) → (20,0,30) → (20,0,50)
//     A diagonal/curved connector road is the only case where a corner can  
//     shift both axes at once. If no connecting chain can be found at all,
//     a single straight dogleg corner is inserted as a fallback and a
//     warning is logged so you know to eyeball that stretch afterward.
//   • Every node gets a perpendicular lane offset:
//         offset = max(0, (roadWidth - laneOffsetBaseWidth) / laneOffsetDivisor)
//     Defaults (base 4, divisor 2) give width 7 → 1.5, width 9 → 2.5, width
//     3 → 0, matching spec exactly. The offset is applied to the RIGHT of
//     the direction of travel: right = Cross(Vector3.up, dir).normalized.
//     On an axis-aligned grid this reduces exactly to: travelling +X or −Z
//     → negative offset on the perpendicular axis; travelling −X or +Z →
//     positive offset on the perpendicular axis — which is the rule you
//     described. Building the INBOUND path is just the same engine run in
//     reverse (Z → A), so the offsets automatically flip to the correct
//     opposite-lane side without any special-casing.
//   • Nodes generated here always have isCurve = false. BusRouteData's own
//     bezier-grouping in BuildSegments couples curve runs to their flanking
//     straight nodes in a way that looked fragile to chain through
//     incrementally, so curved stretches are approximated with extra plain
//     straight nodes instead of true bezier control points. Functionally
//     equivalent for driving; if you want true bezier smoothing somewhere,
//     flip isCurve = true by hand afterward in the Inspector.
// ═══════════════════════════════════════════════════════════════════════════════
public class BusRouteMaker : EditorWindow
{
    [MenuItem("Tools/City Building/Bus Route Maker")]
    public static void ShowWindow()
    {
        var window = GetWindow<BusRouteMaker>("Bus Route Maker");
        window.minSize = new Vector2(880, 620);
        window.Show();
    }

    [Header("Panel")]
    public int panelWidth  = 880;
    public int panelHeight = 620;

    /// <summary>Same reasoning as BusStopMaker's version — CityManager.Instance
    /// is a runtime-only singleton, this falls back to a direct scene lookup
    /// so it works in Edit Mode with no Play button needed.</summary>
    private static CityManager ResolveCityManager()
    {
        var cm = CityManager.Instance;
        if (cm == null) cm = UnityEngine.Object.FindObjectOfType<CityManager>();
        return cm;
    }

    [Header("Map View")]
    public float viewRadius = 700f;
    public float zoomMin = 0.3f;
    public float zoomMax = 5f;
    public float zoomStep = 0.15f;

    [Header("Lane Offset Formula")]
    [Tooltip("offset = max(0, (roadWidth - laneOffsetBaseWidth) / laneOffsetDivisor). Defaults match: width 7 -> 1.5, width 9 -> 2.5, width 3 -> 0.")]
    public float laneOffsetBaseWidth = 4f;
    public float laneOffsetDivisor   = 2f;

    // Cross-road hops are found via CityManager.Graph (the real geometric
    // road graph) + RoadGraphPathfinder — see BuildRawPath. Each hop now
    // emits one point per real intersection/junction node, not a
    // fixed-distance resample (this used to be a tunable
    // graphHopSampleSpacing field, now removed since nothing reads it).

    [Header("Geometry Simplification")]
    [Tooltip("A point is dropped from the final node list if the path direction changes by less than this many degrees across it — e.g. several stops in a row on the same straight stretch of road collapse to just the straight-run's start and end, with corners kept exactly where the road actually turns.")]
    public float simplifyAngleThresholdDeg = 2.5f;

    [Header("Grid / Axis Alignment")]
    [Tooltip("Every hand-built route in this city moves along exactly one axis (X or Z) per segment, never diagonally, and every coordinate lands on a clean .0 or .5 — that's the actual rule the road grid is built on, and the tool now enforces it structurally instead of trusting raw pathfinder/click output. " +
             "If a straight hop between two points would move more than this many meters on BOTH axes at once, a corner point is inserted so it doesn't cut a diagonal through what should be a square turn. Curved points (isCurve) are exempt — they're tracing an actual curved road, not a grid corner.")]
    public float axisPurityToleranceMeters = 0.6f;

    [Header("Same-Road Curve Sampling")]
    [Tooltip("[FIX] A same-road run between two waypoints used to always collapse to nothing in between (see BuildRawPath) on the assumption that a 'RoadSpanSegment' downstream would reproduce the road's real curve automatically — but RouteNode was later stripped down to a bare baked Vector3 with no live road reference, so that mechanism no longer exists anywhere. Nothing replaced it, so a same-road run across a curving stretch of road was silently drawing a straight chord across the curve instead of following it -- the actual cause of \"nodes don't align with the road.\" " +
             "If arcLength/chordDistance between two same-road waypoints exceeds this ratio, the road curves enough to matter, and intermediate points are now sampled directly off the road's own spline (RoadSegment.EvaluatePosition) instead of being skipped. 1.0 = perfectly straight; 1.02 means the real road path is at least 2% longer than a straight line between the same two points, which a dead-straight road can never be (only measurement noise could produce a ratio fractionally above 1.0 on a truly straight road, so this stays comfortably above that noise floor without also firing on roads that are ALMOST straight but not worth extra nodes for).")]
    public float curveDetectionArcChordRatio = 1.02f;
    [Tooltip("How many extra points to sample along a detected curve between two same-road waypoints. More points = smoother curve-following, more nodes in the final route.")]
    public int curveSampleCount = 4;

    [Header("Auto Dwell/Segment Time (whole minutes, auto-estimated per stop)")]
    [Tooltip("REBUILT — the old tier-bucket system (<=90m -> 1 min, <=350m -> 2 min, <=500m -> 3 min, +1 min/200m beyond) was landing on numbers like '300m = 2 minutes,' which implies an average speed of about 9 km/h between stops — walking pace, not a bus. " +
             "Replaced with a continuous formula: minutes = ceil((distanceMeters / assumedSegmentSpeedKmh-in-m/s + assumedDwellSeconds) / 60), with a 1-minute floor. At the defaults, 300m lands on 1 minute instead of 2 — still generous (includes a flat dwell/deceleration allowance on top of pure travel time), but no longer wildly inflated for short hops. All hand-editable afterward in the Details stage or the asset itself.")]
    public float assumedSegmentSpeedKmh = 28f;
    [Tooltip("Flat seconds added per segment on top of pure travel time, covering deceleration into the stop + a brief dwell for boarding — not a full schedule-recovery pad, just enough that a very short hop still reads as at least a real stop, not an instant teleport.")]
    public float assumedDwellSeconds    = 15f;

    [Header("Road-Point Clicking")]
    [Tooltip("You can click a plain point/corner/dead-end anywhere on a road, not just a bus stop, while building a route — it becomes a pass-through waypoint (path geometry only, no schedule/dwell entry of its own). Stops always take priority if one is closer to the click than this.")]
    public int   roadClickSampleCount  = 60;
    [Tooltip("Click must land within this many screen pixels of a sampled road point to register as a road waypoint. Only checked when no stop is closer.")]
    public float roadClickPixelRadius  = 10f;

    // ═════════════════════════════════════════════════════════════════════
    //  WORKFLOW STATE
    // ═════════════════════════════════════════════════════════════════════
    private enum Stage { PickTerminalA, PickTerminalZ, BuildOutbound, BuildInbound, EditDetails, Done }

    /// <summary>Kept for CityLineMaker's shell to call — brings this window
    /// into focus and re-centers the map, rather than an actual visibility
    /// toggle (EditorWindows are opened/closed by the window system itself).</summary>
    public void SetOpen(bool open)
    {
        if (open) { OnOpen(); Focus(); }
    }
    public bool IsOpen => true;
    private Stage _stage   = Stage.PickTerminalA;

    private BusStopData _terminalA;
    private BusStopData _terminalZ;

    private List<RouteWaypoint>  _currentSeq  = new List<RouteWaypoint>();
    private List<BusStopData>    _outboundSeq = new List<BusStopData>();
    private List<BusStopData>    _inboundSeq  = new List<BusStopData>();
    private bool _buildingOutboundDirection = true;
    private int  _activeVariantIndex = -1; // -1 = building the mainline route

    // ── Extend-route mode ────────────────────────────────────────────────
    // Lets you move ONE terminal outward and keep clicking new stops from
    // wherever the route currently ends, instead of rebuilding the whole
    // outbound/inbound path from scratch. Reuses the normal BuildOutbound/
    // BuildInbound stage + map click handling; FinishCurrentSequence()
    // branches to FinishExtension() instead of the normal finish logic
    // whenever _extending is true.
    private bool _extending    = false;
    private bool _extendMovingZ = true; // true = moving Terminal Z outward, false = Terminal A

    private List<RouteNode> _previewNodes  = new List<RouteNode>();
    private List<RouteNode> _outboundNodes = new List<RouteNode>();
    private List<RouteNode> _inboundNodes  = new List<RouteNode>();
    private List<RouteStopBinding> _outboundBindings = new List<RouteStopBinding>();
    private List<RouteStopBinding> _inboundBindings  = new List<RouteStopBinding>();
    private readonly List<RouteVariantData> _variants = new List<RouteVariantData>();

    // ── Editing an existing route asset (vs. creating a brand new one) ──────
    private BusRouteData _loadedAsset = null;
#if UNITY_EDITOR
    private BusRouteData _pickerAsset = null;
#endif

    // Route detail fields (filled in during EditDetails, written to the asset)
    private string _routeNumber = "";
    private string _routeName   = "";
    private Color  _routeColor  = new Color(0.25f, 0.55f, 0.95f);
    private int    _maxBusesAllowed = 5;
    private ArticulatedRequirement _articulatedPolicy = ArticulatedRequirement.Allowed;
    private string _destOutbound = "";
    private string _destInbound  = "";
    private float _operatingStart = 255f;
    private float _operatingEnd   = 1215f;
    private float _headwayA = 15f;
    private float _headwayZ = 15f;
    private float _oneWayMinutes = 30f;

    private readonly Color[] _colorPalette =
    {
        new Color(0.85f,0.20f,0.20f), new Color(0.95f,0.55f,0.15f), new Color(0.95f,0.85f,0.15f),
        new Color(0.25f,0.75f,0.35f), new Color(0.20f,0.60f,0.95f), new Color(0.55f,0.35f,0.85f),
        new Color(0.95f,0.40f,0.70f), new Color(0.80f,0.80f,0.80f),
    };

    private string _statusMessage = "Click a stop on the map to set Terminal A.";

    // ── Fleet series checklist source ─────────────────────────────────────
    // Auto-found in OnEnable via AssetDatabase; drag a different one in via
    // the ObjectField in the Details stage if the wrong one gets picked (or
    // more than one FleetRosterData asset exists in the project).
    private FleetRosterData _fleetRoster;
    private readonly List<int> _allowedFleetSeries = new List<int>();

    private readonly List<ScheduleWindow> _scheduleWindows = new List<ScheduleWindow>();
    private Vector2 _scheduleWindowsScroll;
    private bool _showNodeEditor = false;

    // ── Variant terminal override picking (map-click driven, same UX as
    // the mainline terminal picker) ──────────────────────────────────────
    private int  _pickingVariantTerminalFor = -1; // -1 = not currently picking
    private bool _pickingVariantTerminalIsA = true;

    // ── RELOCATE terminal (completely new stop, other terminal untouched,
    // full rebuild from scratch — distinct from Extend, which keeps the
    // existing path and just continues past it) ──────────────────────────
    private bool? _relocatingTerminalIsZ = null; // null = not currently relocating

    // ── Map view runtime state ───────────────────────────────────────────
    private Vector3 _worldCentre = Vector3.zero;
    private Vector2 _panOffset   = Vector2.zero;
    private float   _zoom        = 1f;
    private bool    _dragging    = false;
    private Vector2 _dragStart, _panAtDrag;

    // ── Live node editing directly on the map (no "Done" gate needed) ──────
    // Click-drag an existing baked node dot (outbound = amber, inbound =
    // green) to reposition it — snaps to the same .0/.5 grid as everything
    // else on mouse-up. Alt+Click a node deletes it. Ctrl+Click a node
    // toggles isCurve. Right-click (context click) on empty road inserts a
    // new node into whichever list is nearer, between its two closest
    // existing points. This works at ANY stage where node lists exist
    // (BuildOutbound/BuildInbound/EditDetails) — you never have to press
    // Finish/Done first to start editing baked geometry.
    private List<RouteNode> _draggingNodeList  = null;
    private int             _draggingNodeIndex = -1;
    private const float     NodeHandlePixelRadius = 7f;

    private Vector2 _stopListScroll;
    private Vector2 _detailsScroll;

    // Cross-road hops are now resolved via CityManager.Graph + RoadGraphPathfinder
    // (real geometric road graph) instead of a private junction-BFS — see
    // BuildRawPath's "different road" branch. Nothing to cache here anymore.

    // ═════════════════════════════════════════════════════════════════════
    //  UNITY LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════
    private void OnEnable()
    {
        OnOpen();
        if (_fleetRoster == null) AutoFindFleetRoster();
    }

    private void AutoFindFleetRoster()
    {
        var guids = AssetDatabase.FindAssets("t:FleetRosterData");
        if (guids.Length == 0) return;
        var path = AssetDatabase.GUIDToAssetPath(guids[0]);
        _fleetRoster = AssetDatabase.LoadAssetAtPath<FleetRosterData>(path);
        if (guids.Length > 1)
            Debug.LogWarning($"[BusRouteMaker] Found {guids.Length} FleetRosterData assets — auto-picked '{path}'. Use the object field in the Details stage to pick a different one.");
    }

    private void OnOpen()
    {
        var city = ResolveCityManager();
        if (city != null && city.AllStops != null && city.AllStops.Length > 0)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var s in city.AllStops)
            {
                if (s == null) continue;
                sum += s.GetWorldPosition();
                n++;
            }
            if (n > 0) _worldCentre = sum / n;
        }

        _zoom = 1f;
        _panOffset = Vector2.zero;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  OnGUI — PANEL CHROME
    // ═════════════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        BuildStyles();

        float px = (position.width  - panelWidth)  * 0.5f;
        float py = (position.height - panelHeight) * 0.5f;
        var panelRect = new Rect(px, py, panelWidth, panelHeight);

        MDT_UITheme.DrawPanel(panelRect);
        GUI.BeginGroup(panelRect);

        const int headerH = 42;
        const int footerH = 24;

        DrawHeader(panelWidth, headerH);

        float mapW  = panelWidth * 0.58f;
        float bodyH = panelHeight - headerH - footerH;
        var mapRect  = new Rect(0,    headerH, mapW,               bodyH);
        var sideRect = new Rect(mapW, headerH, panelWidth - mapW,  bodyH);

        HandleMapInput(mapRect);
        DrawMapArea(mapRect);
        DrawSidePanel(sideRect);
        DrawFooter(panelWidth, headerH + bodyH, footerH);

        GUI.EndGroup();
    }

    private void DrawHeader(float w, float h)
    {
        var hr = new Rect(0, 0, w, h);
        MDT_UITheme.DrawHeader(hr);

        GUI.Label(new Rect(10, 4, 200, 16), "BUS ROUTE MAKER", _styleTitle);
        GUI.Label(new Rect(10, 20, w - 60, 18), StageLabel(), _styleStageLabel);
        float stepX = 190f;
        DrawStageStepper(new Rect(stepX, 4, Mathf.Max(200f, w - stepX - 190f), 16));

        // [FIX] This tool calls city.GetRoad() in several places (map
        // rendering, click detection, stop resolution) that just silently
        // `continue`/return a fallback when it's null, rather than showing
        // anything like BusStopMaker's "Road not built yet" message. Since
        // GetRoad() returns null until CityManager.BuildCity() has actually
        // run — which only happened automatically in Play Mode's Start()
        // before — a blank/broken-looking map with no explanation was the
        // likely result here. This button is a persistent, always-visible
        // way to trigger that build regardless of which stage you're on.
        if (GUI.Button(new Rect(w - 170, 6, 130, 24), "Build City Now", _styleStageLabel))
        {
            var cm = ResolveCityManager();
            if (cm != null) cm.BuildCity();
        }

        if (GUI.Button(new Rect(w - 30, 8, 22, 22), "\u00d7", _styleCloseButton))
            Close();
    }

    private string StageLabel()
    {
        switch (_stage)
        {
            case Stage.PickTerminalA: return "Step 1 — click a stop to set TERMINAL A";
            case Stage.PickTerminalZ: return $"Step 2 — Terminal A = {_terminalA?.stopName}. Click a stop to set TERMINAL Z";
            case Stage.BuildOutbound:
                return _extending
                    ? $"Extending past Terminal Z — click stops in order ({_currentSeq.Count} selected)"
                    : _activeVariantIndex < 0
                        ? $"Outbound A \u2192 Z — click stops in order ({_currentSeq.Count} selected)"
                        : $"Variant {_variants[_activeVariantIndex].variantLetter} outbound — click stops ({_currentSeq.Count} selected)";
            case Stage.BuildInbound:
                return _extending
                    ? $"Extending past Terminal A — click stops in order ({_currentSeq.Count} selected)"
                    : _activeVariantIndex < 0
                        ? $"Inbound Z \u2192 A — click stops in order ({_currentSeq.Count} selected)"
                        : $"Variant {_variants[_activeVariantIndex].variantLetter} inbound — click stops ({_currentSeq.Count} selected)";
            case Stage.EditDetails: return "Route details, schedule & variants";
            case Stage.Done:        return "Route asset created";
            default: return "";
        }
    }

    private void DrawFooter(float w, float y, float h)
    {
        var r = new Rect(0, y, w, h);
        MDT_UITheme.DrawRect(r, MDT_UITheme.BGFooter);
        MDT_UITheme.DrawDivider(r.x, r.y, r.width);
        bool hasMsg = !string.IsNullOrEmpty(_statusMessage);
        MDT_UITheme.DrawLED(new Vector2(14, y + h * 0.5f), 3.5f, hasMsg ? MDT_UITheme.LEDAmber : MDT_UITheme.LEDGreen);
        GUI.Label(new Rect(26, y + 3, w - 36, h - 4), hasMsg ? _statusMessage : "Ready", _styleStatus);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  MAP — INPUT
    // ═════════════════════════════════════════════════════════════════════
    private bool IsSelectableStage() =>
        _stage == Stage.PickTerminalA || _stage == Stage.PickTerminalZ ||
        _stage == Stage.BuildOutbound || _stage == Stage.BuildInbound ||
        _pickingVariantTerminalFor >= 0 || _relocatingTerminalIsZ.HasValue; // EditDetails stage otherwise has no click handling at all

    private void HandleMapInput(Rect mapRect)
    {
        var ev = Event.current;

        if (ev.type == EventType.ScrollWheel && mapRect.Contains(ev.mousePosition))
        {
            _zoom = Mathf.Clamp(_zoom - ev.delta.y * zoomStep, zoomMin, zoomMax);
            ev.Use();
        }

        // Right-click / context-click anywhere on the map with node lists
        // present inserts a new node into whichever list has the nearer
        // segment — works without leaving the map, at any stage.
        if (ev.type == EventType.ContextClick && mapRect.Contains(ev.mousePosition) && HasEditableNodes())
        {
            Vector2 localMouse = ev.mousePosition - new Vector2(mapRect.x, mapRect.y);
            TryInsertNodeAtScreenPoint(localMouse, mapRect);
            ev.Use();
            return;
        }

        if (ev.type == EventType.MouseDown && ev.button == 0 && mapRect.Contains(ev.mousePosition))
        {
            Vector2 localMouse = ev.mousePosition - new Vector2(mapRect.x, mapRect.y);
            var city = ResolveCityManager();
            bool hit = false;

            // Existing baked node handles take priority over everything
            // else so you can grab/delete/re-curve geometry without
            // accidentally re-triggering terminal picking or a new
            // waypoint click underneath it.
            if (!hit && HasEditableNodes() &&
                TryHitNodeHandle(localMouse, mapRect, out var hitList, out var hitIdx))
            {
                if (ev.alt)
                {
                    hitList.RemoveAt(hitIdx);
                    _statusMessage = $"Deleted node #{hitIdx} directly on the map.";
                }
                else if (ev.control || ev.command)
                {
                    var n = hitList[hitIdx];
                    n.isCurve = !n.isCurve;
                    hitList[hitIdx] = n;
                    _statusMessage = $"Node #{hitIdx} isCurve = {n.isCurve}.";
                }
                else
                {
                    _draggingNodeList  = hitList;
                    _draggingNodeIndex = hitIdx;
                    _statusMessage = $"Dragging node #{hitIdx} — release to drop (snaps to grid).";
                }
                hit = true;
            }

            if (city?.AllStops != null && IsSelectableStage())
            {
                BusStopData best = null;
                float bestDist = 11f;
                foreach (var s in city.AllStops)
                {
                    if (s == null) continue;
                    Vector2 sp = WorldToScreen(s.GetWorldPosition(), mapRect.width, mapRect.height);
                    float d = Vector2.Distance(sp, localMouse);
                    if (d < bestDist) { bestDist = d; best = s; }
                }
                if (best != null)
                {
                    OnWaypointClicked(RouteWaypoint.FromStop(best, city));
                    hit = true;
                }
            }

            // Road-point click (corners / dead-ends / plain waypoints on the way) —
            // only during sequence-building stages, and only when no stop was
            // closer. Terminals must always be real stops (a layover has to be
            // somewhere a bus can actually dwell), so this never applies at the
            // PickTerminalA/PickTerminalZ stages.
            if (!hit && city != null && AllowsRoadPointClick() &&
                TryFindNearestRoadPoint(localMouse, mapRect, city, out var roadCode, out var road, out var t, out var pos, out var isOneWay))
            {
                OnWaypointClicked(RouteWaypoint.FromRoadPoint(roadCode, road, t, pos, isOneWay));
                hit = true;
            }

            if (!hit)
            {
                _dragging  = true;
                _dragStart = ev.mousePosition;
                _panAtDrag = _panOffset;
            }
            ev.Use();
        }

        if (ev.type == EventType.MouseDrag && _draggingNodeIndex >= 0 && _draggingNodeList != null)
        {
            Vector2 localMouse = ev.mousePosition - new Vector2(mapRect.x, mapRect.y);
            var city = ResolveCityManager();
            Vector3 world = ScreenToWorldMap(localMouse, mapRect.width, mapRect.height, city);
            if (_draggingNodeIndex < _draggingNodeList.Count)
            {
                var n = _draggingNodeList[_draggingNodeIndex];
                n.position = world; // live, unsnapped while dragging so it feels smooth
                _draggingNodeList[_draggingNodeIndex] = n;
            }
            ev.Use();
        }
        else if (ev.type == EventType.MouseDrag && _dragging)
        {
            _panOffset = _panAtDrag + (ev.mousePosition - _dragStart);
            ev.Use();
        }

        if (ev.type == EventType.MouseUp && (_draggingNodeIndex >= 0 || _dragging))
        {
            if (_draggingNodeIndex >= 0 && _draggingNodeList != null && _draggingNodeIndex < _draggingNodeList.Count)
            {
                var n = _draggingNodeList[_draggingNodeIndex];
                n.position = SnapToHalfV3(n.position); // snap only on release, matches every other node in the project
                _draggingNodeList[_draggingNodeIndex] = n;
                _statusMessage = $"Node #{_draggingNodeIndex} moved to {n.position}.";
            }
            _draggingNodeList  = null;
            _draggingNodeIndex = -1;
            _dragging = false;
            ev.Use();
        }
    }

    private bool HasEditableNodes() =>
        (_outboundNodes != null && _outboundNodes.Count > 0) || (_inboundNodes != null && _inboundNodes.Count > 0);

    /// <summary>Inverse of WorldToScreen for this window's map — same
    /// convention as MDT_LiveMap's ScreenToWorld (Y=0 plane), used to turn a
    /// drag/insert click back into a world point.</summary>
    private Vector3 ScreenToWorldMap(Vector2 localScreen, float w, float h, CityManager city)
    {
        float baseScale = Mathf.Min(w, h) * 0.45f / Mathf.Max(viewRadius, 1f);
        float scale = baseScale * _zoom;
        if (scale <= 0.0001f) scale = 0.0001f;

        float dx = localScreen.x - w * 0.5f - _panOffset.x;
        float dz = localScreen.y - h * 0.5f - _panOffset.y;

        return new Vector3(_worldCentre.x + dx / scale, _worldCentre.y, _worldCentre.z - dz / scale);
    }

    /// <summary>Hit-tests both node lists' screen positions against a click,
    /// same pixel-radius pattern as stop hit-testing. Checks outbound then
    /// inbound; whichever handle is actually closest to the click wins.</summary>
    private bool TryHitNodeHandle(Vector2 localMouse, Rect mapRect, out List<RouteNode> list, out int index)
    {
        List<RouteNode> bestList = null;
        int bestIndex = -1;
        float bestDist = NodeHandlePixelRadius;

        void Check(List<RouteNode> nodes)
        {
            if (nodes == null) return;
            for (int i = 0; i < nodes.Count; i++)
            {
                Vector2 sp = WorldToScreen(nodes[i].position, mapRect.width, mapRect.height);
                float d = Vector2.Distance(sp, localMouse);
                if (d < bestDist) { bestDist = d; bestList = nodes; bestIndex = i; }
            }
        }
        Check(_outboundNodes);
        Check(_inboundNodes);

        list = bestList;
        index = bestIndex;
        return list != null;
    }

    /// <summary>Right-click insert: finds the nearer of the two node lists'
    /// closest SEGMENT (not just closest point) to the click, and inserts a
    /// new node at the click's world position between that segment's two
    /// endpoints. Falls back to appending at the end if only one list has
    /// any nodes.</summary>
    private void TryInsertNodeAtScreenPoint(Vector2 localMouse, Rect mapRect)
    {
        var city = ResolveCityManager();
        Vector3 world = SnapToHalfV3(ScreenToWorldMap(localMouse, mapRect.width, mapRect.height, city));

        List<RouteNode> bestList = null;
        int bestSegStart = -1;
        float bestDist = float.MaxValue;

        void CheckList(List<RouteNode> nodes)
        {
            if (nodes == null || nodes.Count < 1) return;
            for (int i = 0; i < nodes.Count - 1; i++)
            {
                Vector2 a = WorldToScreen(nodes[i].position, mapRect.width, mapRect.height);
                Vector2 b = WorldToScreen(nodes[i + 1].position, mapRect.width, mapRect.height);
                float d = DistancePointToSegment(localMouse, a, b);
                if (d < bestDist) { bestDist = d; bestList = nodes; bestSegStart = i; }
            }
            if (nodes.Count == 1)
            {
                Vector2 a = WorldToScreen(nodes[0].position, mapRect.width, mapRect.height);
                float d = Vector2.Distance(a, localMouse);
                if (d < bestDist) { bestDist = d; bestList = nodes; bestSegStart = 0; }
            }
        }
        CheckList(_outboundNodes);
        CheckList(_inboundNodes);

        if (bestList == null) { _statusMessage = "No node geometry to insert into yet — build outbound/inbound first."; return; }

        var newNode = new RouteNode { position = world, isCurve = false };
        bestList.Insert(bestSegStart + 1, newNode);
        _statusMessage = $"Inserted new node after #{bestSegStart} ({(bestList == _outboundNodes ? "outbound" : "inbound")}). Drag it into place if needed.";
    }

    private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 0.0001f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
        Vector2 proj = a + ab * t;
        return Vector2.Distance(p, proj);
    }

    private bool AllowsRoadPointClick() => _stage == Stage.BuildOutbound || _stage == Stage.BuildInbound;

    /// <summary>Hit-tests a click against every road's spline (same set DrawMapArea
    /// already renders as the reference grid) by sampling each one in screen space —
    /// consistent, zoom-correct click feel identical to how stops are hit-tested.
    /// Covers corners, dead-ends (a road's own t=0/t=1 samples are just points in
    /// this list, nothing special-cased), and any plain mid-road point.</summary>
    private bool TryFindNearestRoadPoint(Vector2 localMouse, Rect mapRect, CityManager city,
                                          out string roadCode, out RoadSegment road, out float t, out Vector3 pos,
                                          out bool isOneWay)
    {
        roadCode = null; road = null; t = 0f; pos = Vector3.zero; isOneWay = false;
        if (city?.roadDefinitions == null) return false;

        float bestDist = roadClickPixelRadius;
        bool found = false;
        int samples = Mathf.Max(2, roadClickSampleCount);

        foreach (var def in city.roadDefinitions)
        {
            if (def == null || string.IsNullOrEmpty(def.roadCode)) continue;
            var seg = city.GetRoad(def.roadCode);
            if (seg == null) continue;

            for (int i = 0; i <= samples; i++)
            {
                float sampleT = i / (float)samples;
                Vector3 samplePos = seg.EvaluatePosition(sampleT);
                Vector2 sp = WorldToScreen(samplePos, mapRect.width, mapRect.height);
                float d = Vector2.Distance(sp, localMouse);
                if (d < bestDist)
                {
                    bestDist = d; found = true;
                    roadCode = def.roadCode; road = seg; t = sampleT; pos = samplePos; isOneWay = def.isOneWay;
                }
            }
        }
        return found;
    }

    private void OnWaypointClicked(RouteWaypoint wp)
    {
        // Relocating a terminal completely takes top priority — this can
        // also happen while sitting in EditDetails, same as variant
        // terminal picking below.
        if (_relocatingTerminalIsZ.HasValue)
        {
            if (!wp.IsStop) { _statusMessage = "The new terminal must be an actual stop — click a stop, not a road point."; return; }
            bool movingZ = _relocatingTerminalIsZ.Value;
            if ((movingZ && wp.stop == _terminalA) || (!movingZ && wp.stop == _terminalZ))
            {
                _statusMessage = "The new terminal can't be the same stop as the OTHER terminal.";
                return;
            }

            if (movingZ) _terminalZ = wp.stop; else _terminalA = wp.stop;
            _relocatingTerminalIsZ = null;

            // Full rebuild from scratch, both directions — deliberately NOT
            // an extend. Old outbound/inbound nodes for this route get
            // completely replaced once FinishCurrentSequence runs for each
            // direction below, exactly like "Redo Outbound"/"Redo Inbound"
            // already do, just kicked off automatically back-to-back here
            // instead of two separate manual button presses.
            _activeVariantIndex = -1;
            _buildingOutboundDirection = true;
            _currentSeq = new List<RouteWaypoint> { RouteWaypoint.FromStop(_terminalA, ResolveCityManager()) };
            _stage = Stage.BuildOutbound;
            RefreshPreview();
            _statusMessage = $"Terminal {(movingZ ? "Z" : "A")} relocated to {wp.stop.stopName}. " +
                              "Now click stops (or road points) from A to Z to rebuild the outbound path.";
            return;
        }

        // Variant terminal override picking takes priority over whatever
        // _stage says — this can happen while sitting in EditDetails (which
        // the normal switch below has no case for at all), same map-click
        // interaction pattern as the mainline terminal picker.
        if (_pickingVariantTerminalFor >= 0)
        {
            if (!wp.IsStop) { _statusMessage = "Terminal overrides must be an actual stop — click a stop, not a road point."; return; }
            var variant = _variants[_pickingVariantTerminalFor];
            if (_pickingVariantTerminalIsA) variant.terminalACodeOverride = wp.stop.stopCode;
            else variant.terminalZCodeOverride = wp.stop.stopCode;
            _statusMessage = $"Variant {variant.variantLetter} terminal {(_pickingVariantTerminalIsA ? "A" : "Z")} override = {wp.stop.stopName}.";
            _pickingVariantTerminalFor = -1;
            return;
        }

        switch (_stage)
        {
            case Stage.PickTerminalA:
                if (!wp.IsStop) { _statusMessage = "Terminals must be an actual stop — click a stop, not a road point."; break; }
                _terminalA = wp.stop;
                _stage = Stage.PickTerminalZ;
                _statusMessage = $"Terminal A = {wp.stop.stopName}. Now click a different stop for Terminal Z.";
                break;

            case Stage.PickTerminalZ:
                if (!wp.IsStop) { _statusMessage = "Terminals must be an actual stop — click a stop, not a road point."; break; }
                if (wp.stop == _terminalA)
                {
                    _statusMessage = "Terminal Z must be a different stop than Terminal A.";
                    break;
                }
                _terminalZ = wp.stop;
                _stage = Stage.BuildOutbound;
                _currentSeq = new List<RouteWaypoint> { RouteWaypoint.FromStop(_terminalA, ResolveCityManager()) };
                _buildingOutboundDirection = true;
                _activeVariantIndex = -1;
                RefreshPreview();
                _statusMessage = $"Terminal Z = {wp.stop.stopName}. Now click stops OR any point/corner on the road, in travel order.";
                break;

            case Stage.BuildOutbound:
            case Stage.BuildInbound:
                if (_currentSeq.Count > 0 && IsSameWaypoint(_currentSeq[_currentSeq.Count - 1], wp)) break;
                _currentSeq.Add(wp);
                RefreshPreview();
                _statusMessage = wp.IsStop
                    ? $"Added stop '{wp.stop.stopName}' ({_currentSeq.Count} waypoints so far)."
                    : $"Added road point on '{wp.roadCode}' ({_currentSeq.Count} waypoints so far).";
                break;
        }
    }

    private bool IsSameWaypoint(RouteWaypoint a, RouteWaypoint b)
    {
        if (a == null || b == null) return false;
        if (a.IsStop || b.IsStop) return a.stop == b.stop;
        return Vector3.Distance(a.pos, b.pos) < 0.5f;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  MAP — DRAWING
    // ═════════════════════════════════════════════════════════════════════
    private Vector2 WorldToScreen(Vector3 world, float w, float h)
    {
        float baseScale = Mathf.Min(w, h) * 0.45f / Mathf.Max(viewRadius, 1f);
        float scale = baseScale * _zoom;

        float dx =  (world.x - _worldCentre.x) * scale;
        float dz = -(world.z - _worldCentre.z) * scale;

        return new Vector2(w * 0.5f + dx + _panOffset.x, h * 0.5f + dz + _panOffset.y);
    }

    private void DrawMapArea(Rect mapRect)
    {
        GUI.BeginGroup(mapRect);
        MDT_UITheme.DrawInset(new Rect(0, 0, mapRect.width, mapRect.height), MDT_UITheme.BGMid);

        var city = ResolveCityManager();
        if (city == null)
        {
            GUI.Label(new Rect(10, 10, mapRect.width - 20, 40),
                "No CityManager found in scene. Enter Play Mode with the city loaded.", _styleStatus);
            GUI.EndGroup();
            return;
        }
        if (city.AllStops == null || city.AllStops.Length == 0)
        {
            GUI.Label(new Rect(10, 10, mapRect.width - 20, 40),
                "Waiting for CityManager to finish resolving stops...", _styleStatus);
            GUI.EndGroup();
            return;
        }

        // City road grid (reference lines)
        if (city.roadDefinitions != null)
        {
            foreach (var def in city.roadDefinitions)
            {
                if (def == null || string.IsNullOrEmpty(def.roadCode)) continue;
                var road = city.GetRoad(def.roadCode);
                if (road == null) continue;

                const int n = 32;
                Vector2 prev = Vector2.zero;
                for (int i = 0; i <= n; i++)
                {
                    Vector2 sp = WorldToScreen(road.EvaluatePosition(i / (float)n), mapRect.width, mapRect.height);
                    if (i > 0) MDT_UITheme.DrawLine(prev, sp, new Color(0.24f, 0.28f, 0.34f, 0.85f), 2f);
                    prev = sp;
                }
            }
        }

        // Route previews
        DrawPreviewPolyline(mapRect, _outboundNodes, new Color(0.95f, 0.75f, 0.20f, 0.85f));
        DrawPreviewPolyline(mapRect, _inboundNodes,  new Color(0.55f, 0.85f, 0.45f, 0.85f));
        DrawPreviewPolyline(mapRect, _previewNodes,  new Color(0.30f, 0.90f, 1.00f, 0.95f));

        // [FIX] Variant override geometry (path AND terminals) was never
        // drawn on the map at all — only the mainline outbound/inbound
        // nodes and mainline _terminalA/_terminalZ dots were. So setting a
        // variant terminal override, or building a variant's own path,
        // visibly changed nothing on the map — the only feedback was the
        // status-bar text. Every variant's override path now draws in a
        // dim magenta (the currently-expanded/active variant draws bright),
        // and its overridden terminal stop(s) get their own marker below.
        for (int vi = 0; vi < _variants.Count; vi++)
        {
            var v = _variants[vi];
            if (v == null) continue;
            bool isActive = vi == _activeVariantIndex || _terminalOverrideExpanded.Contains(v);
            Color vCol = isActive ? new Color(0.95f, 0.30f, 0.85f, 0.9f) : new Color(0.65f, 0.25f, 0.55f, 0.45f);
            if (v.overrideRoute)
            {
                DrawPreviewPolyline(mapRect, v.outboundNodesOverride, vCol);
                DrawPreviewPolyline(mapRect, v.inboundNodesOverride,  vCol);
            }
        }

        // Draggable node handles — every baked node is a live handle on the
        // map itself, at any stage, not just from the text-field list.
        // Drag = move, Alt+Click = delete, Ctrl/Cmd+Click = toggle isCurve,
        // right-click empty space = insert. See HandleMapInput.
        DrawNodeHandles(mapRect, _outboundNodes, new Color(1f, 0.85f, 0.25f, 1f));
        DrawNodeHandles(mapRect, _inboundNodes,  new Color(0.55f, 1f, 0.55f, 1f));

        // Stops
        Vector2 mouseLocal = Event.current.mousePosition; // already group-local after BeginGroup
        string hoveredName = null;

        foreach (var s in city.AllStops)
        {
            if (s == null) continue;
            Vector2 sp = WorldToScreen(s.GetWorldPosition(), mapRect.width, mapRect.height);
            if (sp.x < -10 || sp.x > mapRect.width + 10 || sp.y < -10 || sp.y > mapRect.height + 10) continue;

            Color col = MDT_UITheme.StopDot;
            float r = 3f;
            int order = SequenceOrderOf(s);

            if (s == _terminalA)      { col = new Color(1f, 0.85f, 0.20f, 1f); r = 5.5f; }
            else if (s == _terminalZ) { col = new Color(1f, 0.55f, 0.20f, 1f); r = 5.5f; }
            else if (order >= 0)      { col = new Color(0.40f, 0.95f, 0.55f, 1f); r = 4.5f; }

            MDT_UITheme.DrawRect(new Rect(sp.x - r, sp.y - r, r * 2, r * 2), col);

            if (order >= 0)
                GUI.Label(new Rect(sp.x + 5, sp.y - 14, 24, 14), order.ToString(), _styleOrderLabel);

            // [FIX] Variant-override terminals get their own ring + letter
            // label so an override is actually visible on the map, not just
            // implied by a status-bar string after you click "Pick on map".
            for (int vi = 0; vi < _variants.Count; vi++)
            {
                var v = _variants[vi];
                if (v == null) continue;
                bool isA = !string.IsNullOrEmpty(v.terminalACodeOverride) && v.terminalACodeOverride == s.stopCode;
                bool isZ = !string.IsNullOrEmpty(v.terminalZCodeOverride) && v.terminalZCodeOverride == s.stopCode;
                if (!isA && !isZ) continue;

                float ringR = r + 4f;
                Color ringCol = new Color(0.95f, 0.30f, 0.85f, 0.95f);
                MDT_UITheme.DrawRect(new Rect(sp.x - ringR, sp.y - 1.5f, ringR * 2, 1.5f), ringCol);
                MDT_UITheme.DrawRect(new Rect(sp.x - 1.5f, sp.y - ringR, 1.5f, ringR * 2), ringCol);
                GUI.Label(new Rect(sp.x - 8, sp.y + r + 2, 40, 12),
                    $"{v.variantLetter}{(isA ? "A" : "Z")}", _styleOrderLabel);
            }

            if (Vector2.Distance(sp, mouseLocal) < 9f) hoveredName = s.stopName;
        }

        if (!string.IsNullOrEmpty(hoveredName))
            MDT_UITheme.DrawTooltip(Event.current.mousePosition, hoveredName, _styleTooltip);

        // Road-point waypoints (corners/dead-ends/plain clicks) get their own
        // small marker + order number too — stops draw above, this only covers
        // the non-stop entries in the in-progress sequence.
        if (_currentSeq != null)
        {
            for (int i = 0; i < _currentSeq.Count; i++)
            {
                var wp = _currentSeq[i];
                if (wp.IsStop) continue; // already drawn as a stop dot above

                Vector2 sp = WorldToScreen(wp.pos, mapRect.width, mapRect.height);
                if (sp.x < -10 || sp.x > mapRect.width + 10 || sp.y < -10 || sp.y > mapRect.height + 10) continue;

                float r = 3.5f;
                MDT_UITheme.DrawRect(new Rect(sp.x - r, sp.y - r, r * 2, r * 2), new Color(0.30f, 0.90f, 1.00f, 0.95f));
                GUI.Label(new Rect(sp.x + 5, sp.y - 14, 24, 14), (i + 1).ToString(), _styleOrderLabel);
            }
        }

        GUI.EndGroup();
    }

    /// <summary>Draws every node in the list as a small draggable diamond
    /// handle — square outline so it reads distinctly from round stop dots.
    /// Curve nodes get a hollow center so isCurve state is visible without
    /// opening the text list. The node currently being dragged is drawn
    /// larger and brighter so there's no ambiguity about what's grabbed.</summary>
    private void DrawNodeHandles(Rect mapRect, List<RouteNode> nodes, Color baseColor)
    {
        if (nodes == null || nodes.Count == 0) return;
        Vector2 mouseLocal = Event.current.mousePosition;
        string hoverLabel = null;

        for (int i = 0; i < nodes.Count; i++)
        {
            Vector2 sp = WorldToScreen(nodes[i].position, mapRect.width, mapRect.height);
            if (sp.x < -10 || sp.x > mapRect.width + 10 || sp.y < -10 || sp.y > mapRect.height + 10) continue;

            bool isDragging = _draggingNodeList == nodes && _draggingNodeIndex == i;
            float r = isDragging ? 6f : 4f;
            Color col = isDragging ? Color.white : baseColor;

            if (nodes[i].isCurve)
                MDT_UITheme.DrawRect(new Rect(sp.x - r + 1.5f, sp.y - r + 1.5f, (r - 1.5f) * 2, (r - 1.5f) * 2), col * 0.4f + new Color(0,0,0,0.6f));
            MDT_UITheme.DrawRect(new Rect(sp.x - r, sp.y - 1f, r * 2, 2f), col);
            MDT_UITheme.DrawRect(new Rect(sp.x - 1f, sp.y - r, 2f, r * 2), col);

            if (Vector2.Distance(sp, mouseLocal) < NodeHandlePixelRadius)
                hoverLabel = $"Node #{i}{(nodes[i].isCurve ? " (curve)" : "")} — drag to move, Alt+Click to delete, Ctrl+Click to toggle curve";
        }

        if (!string.IsNullOrEmpty(hoverLabel))
            MDT_UITheme.DrawTooltip(mouseLocal, hoverLabel, _styleTooltip);
    }

    private int SequenceOrderOf(BusStopData s)
    {
        if (_currentSeq == null) return -1;
        for (int i = 0; i < _currentSeq.Count; i++)
            if (_currentSeq[i].IsStop && _currentSeq[i].stop == s) return i + 1;
        return -1;
    }

    private void DrawPreviewPolyline(Rect mapRect, List<RouteNode> nodes, Color col)
    {
        if (nodes == null || nodes.Count < 2) return;
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            Vector2 a = WorldToScreen(nodes[i].position,     mapRect.width, mapRect.height);
            Vector2 b = WorldToScreen(nodes[i + 1].position, mapRect.width, mapRect.height);
            MDT_UITheme.DrawLine(a, b, new Color(0, 0, 0, 0.35f), 4f);
            MDT_UITheme.DrawLine(a, b, col, 2.5f);
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  SIDE PANEL
    // ═════════════════════════════════════════════════════════════════════
    private void DrawSidePanel(Rect r)
    {
        MDT_UITheme.DrawInset(r, MDT_UITheme.BGMid);
        GUILayout.BeginArea(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 12));

        switch (_stage)
        {
            case Stage.PickTerminalA:
                GUILayout.Label("Click any stop on the map to set it as Terminal A.", _styleBody);
                DrawLoadExistingRouteUI();
                break;

            case Stage.PickTerminalZ:
                GUILayout.Label($"Terminal A: {_terminalA?.stopName}", _styleBody);
                GUILayout.Label("Now click a different stop to set Terminal Z.", _styleBody);
                break;

            case Stage.BuildOutbound:
            case Stage.BuildInbound:
                DrawSequenceBuilderUI();
                break;

            case Stage.EditDetails:
                DrawDetailsUI(r.height - 16);
                break;

            case Stage.Done:
                GUILayout.Label("Route asset created.", _styleSectionHeader);
                GUILayout.Label(_statusMessage, _styleBodyDim);
                GUILayout.Space(10);
                if (GUILayout.Button("Start a new route", _styleButtonPrimary, GUILayout.Height(30)))
                    ResetTool();
                break;
        }

        GUILayout.EndArea();
    }

    private void DrawLoadExistingRouteUI()
    {
        GUILayout.Space(14);
        GUILayout.Label("— OR —", _styleBodyDim);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Edit an existing route (e.g. to add a variant)", _styleSectionHeader);
        GUILayout.FlexibleSpace();
#if UNITY_EDITOR
        if (GUILayout.Button("Refresh", _styleButton, GUILayout.Width(60), GUILayout.Height(18)))
            RefreshRouteAssetListIfNeeded(force: true);
#endif
        GUILayout.EndHorizontal();

#if UNITY_EDITOR
        // Auto-refreshes every draw (cheap AssetDatabase scan, only runs
        // while sitting on PickTerminalA) so a route saved elsewhere while
        // this window stays open always shows up — no more stale list.
        RefreshRouteAssetListIfNeeded(force: true);

        if (_routeAssetChoices.Count == 0)
        {
            GUILayout.Label("No route assets found in Assets/Routes.", _styleBodyDim);
            return;
        }

        _loadListScroll = GUILayout.BeginScrollView(_loadListScroll, GUILayout.Height(120));
        foreach (var candidate in _routeAssetChoices)
        {
            if (candidate == null) continue;
            bool isPicked = candidate == _pickerAsset;
            var style = isPicked ? _styleButtonPrimary : _styleButton;
            if (GUILayout.Button($"Route {candidate.routeNumber} \u2014 {candidate.routeName}", style, GUILayout.Height(22)))
                _pickerAsset = candidate;
        }
        GUILayout.EndScrollView();

        GUILayout.Space(4);
        GUI.enabled = _pickerAsset != null;
        if (GUILayout.Button("Load Route", _styleButtonPrimary, GUILayout.Height(28)))
            LoadExistingRoute(_pickerAsset);
        GUI.enabled = true;
#else
        GUILayout.Label("Loading existing routes is editor-only.", _styleBodyDim);
#endif
    }

#if UNITY_EDITOR
    private readonly List<BusRouteData> _routeAssetChoices = new List<BusRouteData>();
    private Vector2 _loadListScroll;
    private bool _routeAssetListLoaded = false;

    private void RefreshRouteAssetListIfNeeded(bool force = false)
    {
        if (_routeAssetListLoaded && !force) return;
        _routeAssetListLoaded = true;

        _routeAssetChoices.Clear();
        string[] guids = AssetDatabase.FindAssets("t:BusRouteData");
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var route = AssetDatabase.LoadAssetAtPath<BusRouteData>(path);
            if (route != null) _routeAssetChoices.Add(route);
        }
    }
#endif

    private BusStopData FindStopByCode(string stopCode)
    {
        if (string.IsNullOrEmpty(stopCode)) return null;
        var city = ResolveCityManager();
        if (city == null || city.AllStops == null) return null;
        foreach (var s in city.AllStops)
            if (s != null && s.stopCode == stopCode) return s;
        return null;
    }

    private List<BusStopData> ResolveStopSequence(List<RouteStopBinding> bindings)
    {
        var seq = new List<BusStopData>();
        if (bindings == null) return seq;
        foreach (var b in bindings)
        {
            var stop = FindStopByCode(b.stopCode);
            if (stop != null) seq.Add(stop);
        }
        return seq;
    }

    private void LoadExistingRoute(BusRouteData asset)
    {
        if (asset == null) return;

        var city = ResolveCityManager();
        if (city == null || city.AllStops == null || city.AllStops.Length == 0)
        {
            // [FIX] This used to proceed anyway, silently leaving _terminalA/
            // _terminalZ null when CityManager hadn't resolved its stops yet
            // (e.g. loading in Edit Mode before BuildCity ran). Everything
            // downstream — Redo Outbound/Inbound, node dragging, map
            // highlighting — would then break with zero indication why. Now
            // it refuses to load and says exactly what's missing instead.
            _statusMessage = "Can't load — CityManager has no stops resolved yet. Enter Play Mode (or press \"Build City Now\") first, then try loading again.";
            return;
        }

        _loadedAsset = asset;

        _terminalA = FindStopByCode(asset.terminalACode);
        _terminalZ = FindStopByCode(asset.terminalZCode);

        // [FIX] Same silent-failure problem, one level down — a stop code on
        // the asset that doesn't resolve (renamed/deleted stop, or a route
        // saved against a different city build) used to leave a null
        // terminal with the tool reporting success regardless.
        var missing = new List<string>();
        if (_terminalA == null && !string.IsNullOrEmpty(asset.terminalACode)) missing.Add($"Terminal A ({asset.terminalACode})");
        if (_terminalZ == null && !string.IsNullOrEmpty(asset.terminalZCode)) missing.Add($"Terminal Z ({asset.terminalZCode})");

        // Any drag in flight on the previous route's node lists is now
        // pointing at detached data once we reassign below — drop it.
        _draggingNodeList  = null;
        _draggingNodeIndex = -1;

        _routeNumber        = asset.routeNumber;
        _routeName          = asset.routeName;
        _routeColor         = asset.routeColor;
        _articulatedPolicy  = asset.articulatedPolicy;
        _maxBusesAllowed    = asset.maxBusesAllowed;
        _destOutbound       = asset.destinationNameOutbound;
        _destInbound        = asset.destinationNameInbound;
        _operatingStart     = asset.operatingStartMinutes;
        _operatingEnd       = asset.operatingEndMinutes;
        _headwayA           = asset.headwayFromAMinutes;
        _headwayZ           = asset.headwayFromZMinutes;
        _oneWayMinutes      = asset.oneWayTripMinutes;

        _allowedFleetSeries.Clear();
        if (asset.allowedFleetSeries != null) _allowedFleetSeries.AddRange(asset.allowedFleetSeries);

        _scheduleWindows.Clear();
        if (asset.scheduleWindows != null) _scheduleWindows.AddRange(asset.scheduleWindows);

        _outboundNodes    = new List<RouteNode>(asset.outboundNodes ?? new List<RouteNode>());
        _inboundNodes     = new List<RouteNode>(asset.inboundNodes ?? new List<RouteNode>());
        _outboundBindings = new List<RouteStopBinding>(asset.outboundStops ?? new List<RouteStopBinding>());
        _inboundBindings  = new List<RouteStopBinding>(asset.inboundStops ?? new List<RouteStopBinding>());
        _outboundSeq      = ResolveStopSequence(_outboundBindings);
        _inboundSeq       = ResolveStopSequence(_inboundBindings);

        _variants.Clear();
        if (asset.variants != null) _variants.AddRange(asset.variants);
        _terminalOverrideExpanded.Clear();
        foreach (var v in _variants)
            if (v != null && (!string.IsNullOrEmpty(v.terminalACodeOverride) || !string.IsNullOrEmpty(v.terminalZCodeOverride)))
                _terminalOverrideExpanded.Add(v);

        _activeVariantIndex = -1;
        _previewNodes = new List<RouteNode>();
        _currentSeq = new List<RouteWaypoint>();
        _stage = Stage.EditDetails;
        _statusMessage = missing.Count > 0
            ? $"Loaded Route {asset.routeNumber}, but couldn't resolve: {string.Join(", ", missing)}. Fix via Relocate Terminal before saving, or the asset will save with a blank terminal code."
            : $"Loaded Route {asset.routeNumber} \u2014 edit details/variants below, then Save Changes.";
    }


    private void DrawSequenceBuilderUI()
    {
        bool isVariant = _activeVariantIndex >= 0;
        string dirLabel = _buildingOutboundDirection ? "A \u2192 Z" : "Z \u2192 A";
        string ctx = isVariant ? $"Variant {_variants[_activeVariantIndex].variantLetter}"
                   : _extending ? $"Extending Terminal {(_extendMovingZ ? "Z" : "A")}"
                   : "Mainline";

        GUILayout.Label($"{ctx} \u2014 {dirLabel}", _styleSectionHeader);
        GUILayout.Label("Click stops on the map in travel order — or click any plain point/corner/dead-end on a road to drop a pass-through waypoint without a schedule stop. Roads, corners and lane offsets are computed automatically.", _styleBodyDim);
        GUILayout.Space(6);

        _stopListScroll = GUILayout.BeginScrollView(_stopListScroll, GUILayout.Height(280));
        for (int i = 0; i < _currentSeq.Count; i++)
        {
            var wp = _currentSeq[i];
            string label = wp.IsStop
                ? $"{i + 1}.  {wp.stop.stopName}  [{wp.stop.parentRoadCode}]"
                : $"{i + 1}.  (road point)  [{wp.roadCode}]";
            GUILayout.Label(label, _styleBody);
        }
        GUILayout.EndScrollView();

        GUILayout.Space(6);
        GUI.enabled = _currentSeq.Count > 1;
        if (GUILayout.Button("Undo last waypoint", _styleButton, GUILayout.Height(24)))
        {
            _currentSeq.RemoveAt(_currentSeq.Count - 1);
            RefreshPreview();
        }
        GUI.enabled = true;

        GUILayout.Space(8);
        string finishLabel = _extending
            ? $"Finish Extension \u2014 set new Terminal {(_extendMovingZ ? "Z" : "A")}"
            : (_buildingOutboundDirection ? "Finish Outbound" : "Finish Inbound");
        if (GUILayout.Button(finishLabel, _styleButtonPrimary, GUILayout.Height(32)))
            FinishCurrentSequence();

        if (isVariant)
        {
            GUILayout.Space(4);
            if (GUILayout.Button("Cancel variant route", _styleButton, GUILayout.Height(22)))
            {
                _activeVariantIndex = -1;
                _previewNodes = new List<RouteNode>();
                _stage = Stage.EditDetails;
            }
        }

        if (_extending)
        {
            GUILayout.Space(4);
            if (GUILayout.Button("Cancel extension", _styleButton, GUILayout.Height(22)))
                CancelExtend();
        }

        // Baked node geometry is editable right here, mid-build — no need
        // to Finish/Done first. Same DrawNodeList used from EditDetails,
        // plus the drag/alt/ctrl handles are always live on the map itself.
        if (_outboundNodes.Count > 0 || _inboundNodes.Count > 0)
        {
            GUILayout.Space(8);
            _showNodeEditor = GUILayout.Toggle(_showNodeEditor,
                $" ROUTE NODES ({_outboundNodes.Count} outbound / {_inboundNodes.Count} inbound) — edit now, before finishing",
                _styleSectionHeader);
            if (_showNodeEditor)
            {
                GUILayout.Label("Drag node dots on the map to move them, Alt+Click to delete, Ctrl+Click to toggle curve, right-click empty road to insert. Or edit x/z directly below.", _styleBodyDim);
                GUILayout.Label($"Outbound ({_outboundNodes.Count}):", _styleBody);
                DrawNodeList(_outboundNodes);
                GUILayout.Space(4);
                GUILayout.Label($"Inbound ({_inboundNodes.Count}):", _styleBody);
                DrawNodeList(_inboundNodes);
            }
        }
    }

    private void DrawDetailsUI(float availableHeight)
    {
        _detailsScroll = GUILayout.BeginScrollView(_detailsScroll, GUILayout.Height(availableHeight));

        // At-a-glance route summary chip so you don't have to open every card.
        Rect chipRect = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
        MDT_UITheme.DrawRoundedRectBordered(chipRect, 6f, MDT_UITheme.BGPill, MDT_UITheme.Divider, 1);
        MDT_UITheme.DrawRoundedRect(new Rect(chipRect.x + 5, chipRect.y + 5, 12, 12), 3f, _routeColor);
        GUI.Label(new Rect(chipRect.x + 22, chipRect.y, chipRect.width - 26, chipRect.height),
            $"Route {(string.IsNullOrEmpty(_routeNumber) ? "?" : _routeNumber)}   •   {_outboundNodes.Count}/{_inboundNodes.Count} nodes   •   {_outboundBindings.Count}/{_inboundBindings.Count} stops   •   {_maxBusesAllowed} buses",
            MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary));
        GUILayout.Space(6);

        if (Fold("identity", "ROUTE IDENTITY", $"{_routeNumber} {_routeName}", true))
        {
            _routeNumber = LabeledTextField("Route number", _routeNumber);
            _routeName   = LabeledTextField("Route name", _routeName);

            GUILayout.Space(4);
            GUILayout.Label("Route color", _styleBodyDim);
            GUILayout.BeginHorizontal();
            foreach (var c in _colorPalette)
            {
                var old = GUI.backgroundColor;
                GUI.backgroundColor = c;
                if (GUILayout.Button("", GUILayout.Width(22), GUILayout.Height(22)))
                    _routeColor = c;
                GUI.backgroundColor = old;
            }
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(4);
        if (Fold("signs", "DESTINATION SIGNS", $"{_destOutbound} / {_destInbound}"))
        {
        _destOutbound = LabeledTextField("Outbound (A\u2192Z) sign", _destOutbound);
        _destInbound  = LabeledTextField("Inbound (Z\u2192A) sign", _destInbound);

        }

        GUILayout.Space(4);
        if (Fold("fleet", "FLEET & VEHICLE POLICY", $"{_maxBusesAllowed} buses, {_articulatedPolicy}"))
        {
            _maxBusesAllowed = LabeledIntField("Max buses on route", _maxBusesAllowed);
            if (GUILayout.Button($"Articulated policy: {_articulatedPolicy}", _styleButton, GUILayout.Height(24)))
                _articulatedPolicy = NextEnumValue(_articulatedPolicy);

            GUILayout.Space(4);
            _fleetRoster = (FleetRosterData)EditorGUILayout.ObjectField("Fleet roster source", _fleetRoster, typeof(FleetRosterData), false);
            GUILayout.Label("Allowed fleet series (blank = any series allowed):", _styleBodyDim);
            DrawFleetSeriesChecklist(_allowedFleetSeries);
        }

        GUILayout.Space(4);
        if (Fold("schedule", "SCHEDULE", $"{_headwayA}/{_headwayZ} min, {_scheduleWindows.Count} windows"))
        {
        _operatingStart = LabeledFloatField("Operating start (min of day)", _operatingStart);
        _operatingEnd   = LabeledFloatField("Operating end (min of day)", _operatingEnd);
        _headwayA       = LabeledFloatField("Headway from A (min)", _headwayA);
        _headwayZ       = LabeledFloatField("Headway from Z (min)", _headwayZ);
        _oneWayMinutes  = LabeledFloatField("One-way trip time (min, auto-estimated)", _oneWayMinutes);

        GUILayout.Space(4);
        GUILayout.Label("Time-of-day windows (optional — overrides the flat headway above when non-empty, matches TrainRouteData/ScheduleWindow):", _styleBodyDim);
        DrawScheduleWindowsUI(_scheduleWindows);
        }

        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Redo Outbound", _styleButton, GUILayout.Height(22)))
        {
            _activeVariantIndex = -1;
            _buildingOutboundDirection = true;
            _currentSeq = new List<RouteWaypoint> { RouteWaypoint.FromStop(_terminalA, ResolveCityManager()) };
            _stage = Stage.BuildOutbound;
            RefreshPreview();
        }
        if (GUILayout.Button("Redo Inbound", _styleButton, GUILayout.Height(22)))
        {
            _activeVariantIndex = -1;
            _buildingOutboundDirection = false;
            _currentSeq = new List<RouteWaypoint> { RouteWaypoint.FromStop(_terminalZ, ResolveCityManager()) };
            _stage = Stage.BuildInbound;
            RefreshPreview();
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(8);
        GUILayout.Label("EXTEND ROUTE (keeps existing path, adds on)", _styleSectionHeader);
        GUILayout.Label("Push a terminal further out and pick up new stops from where the route currently ends — the existing path stays, this just continues past it.", _styleBodyDim);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button($"Extend past Terminal Z ({_terminalZ?.stopName})", _styleButton, GUILayout.Height(24)))
            StartExtend(true);
        if (GUILayout.Button($"Extend past Terminal A ({_terminalA?.stopName})", _styleButton, GUILayout.Height(24)))
            StartExtend(false);
        GUILayout.EndHorizontal();

        GUILayout.Space(8);
        GUILayout.Label("RELOCATE TERMINAL (completely different stop, keeps the OTHER terminal, rebuilds from scratch)", _styleSectionHeader);
        GUILayout.Label("For a genuinely new, unrelated terminal — not an outward extension. The other terminal and its path stay untouched; the whole path on the moved side gets rebuilt fresh (old geometry for that side is discarded, not built on top of).", _styleBodyDim);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button($"Relocate Terminal Z (currently {_terminalZ?.stopName})", _styleButton, GUILayout.Height(24)))
            StartRelocateTerminal(true);
        if (GUILayout.Button($"Relocate Terminal A (currently {_terminalA?.stopName})", _styleButton, GUILayout.Height(24)))
            StartRelocateTerminal(false);
        GUILayout.EndHorizontal();
        if (_relocatingTerminalIsZ.HasValue)
            GUILayout.Label($"Click a stop on the map to be the NEW Terminal {(_relocatingTerminalIsZ.Value ? "Z" : "A")}...", _styleBodyDim);

        GUILayout.Space(8);
        GUILayout.Label($"OUTBOUND STOPS ({_outboundBindings.Count})  \u2014  edit minutesFromStart", _styleSectionHeader);
        for (int i = 0; i < _outboundBindings.Count; i++)
        {
            var b = _outboundBindings[i];
            GUILayout.BeginHorizontal();
            GUILayout.Label(b.stopCode, _styleBody, GUILayout.Width(74));
            // [FIX] Used to be "F1"/float.TryParse — one-decimal display and
            // fractional parsing, directly contradicting BuildBindings'
            // documented "whole minutes only, no more .2/.7 fractional
            // minutesFromStart values" model just below in this same file.
            // Hand-editing a stop time here could silently reintroduce the
            // exact fractional-minute mess the rebuild was meant to
            // eliminate. Now whole-minute in, whole-minute out.
            string mins = GUILayout.TextField(Mathf.RoundToInt(b.minutesFromStart).ToString(), _styleTextField, GUILayout.Width(50));
            if (int.TryParse(mins, out int mv)) { b.minutesFromStart = mv; _outboundBindings[i] = b; }
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(6);
        GUILayout.Label($"INBOUND STOPS ({_inboundBindings.Count})", _styleSectionHeader);
        for (int i = 0; i < _inboundBindings.Count; i++)
        {
            var b = _inboundBindings[i];
            GUILayout.BeginHorizontal();
            GUILayout.Label(b.stopCode, _styleBody, GUILayout.Width(74));
            string mins = GUILayout.TextField(Mathf.RoundToInt(b.minutesFromStart).ToString(), _styleTextField, GUILayout.Width(50));
            if (int.TryParse(mins, out int mv)) { b.minutesFromStart = mv; _inboundBindings[i] = b; }
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8);
        _showNodeEditor = GUILayout.Toggle(_showNodeEditor, $" ROUTE NODES ({_outboundNodes.Count} outbound / {_inboundNodes.Count} inbound) \u2014 view/edit raw geometry", _styleSectionHeader);
        if (_showNodeEditor)
        {
            GUILayout.Label("These are the actual baked Vector3 points the route follows (already snapped to the .0/.5 grid — see SnapToHalfV3). Edit x/z directly, toggle isCurve, or delete a bad point. Removing a node here does NOT re-run pathfinding — it just deletes that point from the straight sequence, so only remove points you're sure are wrong (e.g. a stray dogleg-fallback point), not ones you want re-routed (use Redo Outbound/Inbound or Relocate Terminal for that instead).", _styleBodyDim);

            GUILayout.Space(4);
            GUILayout.Label($"Outbound ({_outboundNodes.Count} nodes):", _styleBody);
            DrawNodeList(_outboundNodes);

            GUILayout.Space(6);
            GUILayout.Label($"Inbound ({_inboundNodes.Count} nodes):", _styleBody);
            DrawNodeList(_inboundNodes);
        }

        GUILayout.Space(8);
        GUILayout.Label("VARIANTS", _styleSectionHeader);
        DrawVariantsUI();

        GUILayout.Space(12);
        string saveLabel = _loadedAsset != null ? "SAVE CHANGES TO ROUTE" : "CREATE ROUTE ASSET";
        if (GUILayout.Button(saveLabel, _styleButtonPrimary, GUILayout.Height(36)))
            CreateRouteAsset();

        GUILayout.Space(4);
        if (GUILayout.Button("Reset entire tool", _styleButton, GUILayout.Height(22)))
            ResetTool();

        GUILayout.Space(8);
        GUILayout.EndScrollView();
    }

    /// <summary>Real checklist of fleet series, per spec ("show a list to
    /// select from for series") — replaces free comma-separated text entry.
    /// Pulls from _fleetRoster.series (each with its own seriesName/
    /// startFleetNumber), computing each one's "block" the same way
    /// BusRouteData.IsFleetSeriesAllowed does: (fleetNumber/100)*100 — e.g.
    /// startFleetNumber 1901 -> block 1900, matching "1900 Series" naming.
    /// Shared between the mainline list and every variant's override list.</summary>
    private void DrawFleetSeriesChecklist(List<int> target)
    {
        if (_fleetRoster == null || _fleetRoster.series == null || _fleetRoster.series.Count == 0)
        {
            GUILayout.Label("No FleetRosterData assigned (or it has no series defined) — assign one above to get a real checklist here instead of typing series numbers by hand.", _styleBodyDim);
            return;
        }

        // Distinct blocks only — a roster can have multiple sub-entries
        // sharing one block (e.g. "2200 std" and "2200 Artic" both block 2200).
        var seen = new HashSet<int>();
        foreach (var def in _fleetRoster.series)
        {
            int block = (def.startFleetNumber / 100) * 100;
            if (!seen.Add(block)) continue;

            bool isChecked = target.Contains(block);
            bool newChecked = GUILayout.Toggle(isChecked, $" {def.seriesName}  ({def.busType}, #{def.startFleetNumber}+)", _styleBody);
            if (newChecked && !isChecked) target.Add(block);
            else if (!newChecked && isChecked) target.Remove(block);
        }
    }

    /// <summary>Add/remove/edit ScheduleWindow entries directly — per spec
    /// ("include time windows"). Shared shape with TrainRouteData's own
    /// scheduleWindows list, same field names, so this and the train tools
    /// stay consistent.</summary>
    private void DrawScheduleWindowsUI(List<ScheduleWindow> windows)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            w.label = GUILayout.TextField(w.label, _styleTextField, GUILayout.Width(120));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Remove", _styleButton, GUILayout.Width(64)))
            {
                windows.RemoveAt(i);
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                break;
            }
            GUILayout.EndHorizontal();

            w.windowStartMinutes  = LabeledFloatField("Window start (min)", w.windowStartMinutes);
            w.windowEndMinutes    = LabeledFloatField("Window end (min)", w.windowEndMinutes);
            w.headwayFromAMinutes = LabeledFloatField("Headway A", w.headwayFromAMinutes);
            w.headwayFromZMinutes = LabeledFloatField("Headway Z", w.headwayFromZMinutes);

            GUILayout.EndVertical();
            GUILayout.Space(4);
        }

        if (GUILayout.Button("+ Add Time Window", _styleButton, GUILayout.Height(24)))
            windows.Add(new ScheduleWindow());
    }

    /// <summary>Per spec ("let me see the outbound and inbound nodes of
    /// route and edit em if needed") — direct view/edit of the baked
    /// RouteNode list. x/z are editable (re-snapped through SnapToHalfV3, the
    /// same half-unit grid every other node in this project lands on, so
    /// hand-edits don't reintroduce float noise OR drift off the grid); y is
    /// shown but not editable here since road-derived height comes from
    /// RoadSegment.EvaluateSurfacePosition's auto-flatten, not something this
    /// tool should let you drift out of sync with the actual road mesh. Each
    /// row also updates the live map preview immediately so a bad edit is
    /// obvious right away.</summary>
    private void DrawNodeList(List<RouteNode> nodes)
    {
        if (nodes == null || nodes.Count == 0)
        {
            GUILayout.Label("(none)", _styleBodyDim);
            return;
        }

        int removeIdx = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            GUILayout.BeginHorizontal();
            GUILayout.Label($"#{i}", _styleBodyDim, GUILayout.Width(32));

            GUILayout.Label("x:", _styleBodyDim, GUILayout.Width(14));
            string xs = GUILayout.TextField(n.position.x.ToString("0.00"), _styleTextField, GUILayout.Width(64));
            GUILayout.Label("z:", _styleBodyDim, GUILayout.Width(14));
            string zs = GUILayout.TextField(n.position.z.ToString("0.00"), _styleTextField, GUILayout.Width(64));
            GUILayout.Label($"y: {n.position.y:0.00}", _styleBodyDim, GUILayout.Width(70));

            if (float.TryParse(xs, out float nx) && float.TryParse(zs, out float nz))
            {
                var edited = new Vector3(nx, n.position.y, nz);
                if (edited != n.position)
                {
                    n.position = SnapToHalfV3(edited);
                    nodes[i] = n;
                    _previewNodes = new List<RouteNode>(_outboundNodes);
                    _previewNodes.AddRange(_inboundNodes);
                }
            }

            n.isCurve = GUILayout.Toggle(n.isCurve, " curve", _styleBody, GUILayout.Width(60));
            nodes[i] = n;

            if (GUILayout.Button("\u00d7", _styleButton, GUILayout.Width(24)))
                removeIdx = i;

            GUILayout.EndHorizontal();
        }

        if (removeIdx >= 0)
        {
            nodes.RemoveAt(removeIdx);
            _statusMessage = $"Removed node #{removeIdx}. Remember: this doesn't re-run pathfinding, it just deletes that point.";
        }
    }

    // ── Terminal-override UI expansion state, per variant ──────────────────
    // [FIX] The "Override terminals" checkbox used to be derived fresh every
    // frame from whether terminalACodeOverride/terminalZCodeOverride were
    // already set. That meant clicking it ON did nothing durable — since you
    // haven't picked a stop yet, both codes are still null, so the very next
    // repaint recomputed the checkbox back to unchecked and the rows
    // (including the "Pick on map" button you needed) vanished again before
    // you could use them. Tracked here instead, by variant reference, so
    // turning it on actually stays on until you turn it off yourself.
    private readonly HashSet<RouteVariantData> _terminalOverrideExpanded = new HashSet<RouteVariantData>();

    private void DrawVariantsUI()
    {
        for (int i = 0; i < _variants.Count; i++)
        {
            var v = _variants[i];
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Letter:", _styleBodyDim, GUILayout.Width(44));
            v.variantLetter = GUILayout.TextField(v.variantLetter, _styleTextField, GUILayout.Width(30));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Remove", _styleButton, GUILayout.Width(64)))
            {
                _terminalOverrideExpanded.Remove(v);
                _variants.RemoveAt(i);
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                break;
            }
            GUILayout.EndHorizontal();

            bool hasRoute = v.overrideRoute && v.outboundNodesOverride != null && v.outboundNodesOverride.Count > 0;
            GUILayout.Label(hasRoute ? "Custom route: set" : "Custom route: none (falls back to mainline path)", _styleBodyDim);
            if (v.overrideVehicleRestrictions)
                GUILayout.Label($"Vehicle restriction: {v.articulatedPolicyOverride}" +
                                (v.allowedFleetSeriesOverride.Count > 0 ? $", series {string.Join(",", v.allowedFleetSeriesOverride)}" : ""),
                                _styleBodyDim);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Build Outbound", _styleButton, GUILayout.Height(22)))
                StartVariantBuild(i, true);
            if (GUILayout.Button("Build Inbound", _styleButton, GUILayout.Height(22)))
                StartVariantBuild(i, false);
            GUILayout.EndHorizontal();

            // Per spec ("variants can have different terminals also") —
            // terminalACodeOverride/terminalZCodeOverride already existed on
            // RouteVariantData, this tool just never exposed any UI for
            // them. Reuses the same stop-picker pattern as the mainline
            // terminal selection (click on the map), just targeting the
            // variant's own override fields instead of _terminalA/_terminalZ.
            bool overrideTerminals = _terminalOverrideExpanded.Contains(v)
                || !string.IsNullOrEmpty(v.terminalACodeOverride) || !string.IsNullOrEmpty(v.terminalZCodeOverride);
            bool newOverrideTerminals = GUILayout.Toggle(overrideTerminals, " Override terminals (this variant starts/ends somewhere different than the mainline)", _styleBody);
            if (newOverrideTerminals) _terminalOverrideExpanded.Add(v);
            else _terminalOverrideExpanded.Remove(v);
            overrideTerminals = newOverrideTerminals;

            if (overrideTerminals)
            {
                var cityForVariant = ResolveCityManager();
                string aLabel = string.IsNullOrEmpty(v.terminalACodeOverride) ? "(same as mainline A)" : (FindStopByCode(v.terminalACodeOverride)?.stopName ?? v.terminalACodeOverride);
                string zLabel = string.IsNullOrEmpty(v.terminalZCodeOverride) ? "(same as mainline Z)" : (FindStopByCode(v.terminalZCodeOverride)?.stopName ?? v.terminalZCodeOverride);

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Terminal A override: {aLabel}", _styleBodyDim);
                if (GUILayout.Button("Pick on map", _styleButton, GUILayout.Width(90)))
                {
                    _pickingVariantTerminalFor = i;
                    _pickingVariantTerminalIsA = true;
                }
                if (!string.IsNullOrEmpty(v.terminalACodeOverride) && GUILayout.Button("Clear", _styleButton, GUILayout.Width(50)))
                    v.terminalACodeOverride = null;
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Terminal Z override: {zLabel}", _styleBodyDim);
                if (GUILayout.Button("Pick on map", _styleButton, GUILayout.Width(90)))
                {
                    _pickingVariantTerminalFor = i;
                    _pickingVariantTerminalIsA = false;
                }
                if (!string.IsNullOrEmpty(v.terminalZCodeOverride) && GUILayout.Button("Clear", _styleButton, GUILayout.Width(50)))
                    v.terminalZCodeOverride = null;
                GUILayout.EndHorizontal();

                if (_pickingVariantTerminalFor == i)
                    GUILayout.Label("Click a stop on the map to set this terminal override...", _styleBodyDim);
            }
            else
            {
                v.terminalACodeOverride = null;
                v.terminalZCodeOverride = null;
            }

            v.overrideSchedule = GUILayout.Toggle(v.overrideSchedule, " Override schedule", _styleBody);
            if (v.overrideSchedule)
            {
                v.operatingStartMinutes = LabeledFloatField("Start", v.operatingStartMinutes);
                v.operatingEndMinutes   = LabeledFloatField("End", v.operatingEndMinutes);
                v.headwayFromAMinutes   = LabeledFloatField("Headway A", v.headwayFromAMinutes);
                v.headwayFromZMinutes   = LabeledFloatField("Headway Z", v.headwayFromZMinutes);
                v.oneWayTripMinutes     = LabeledFloatField("Trip min", v.oneWayTripMinutes);

                GUILayout.Label("Time-of-day windows (optional, overrides the flat headway above):", _styleBodyDim);
                DrawScheduleWindowsUI(v.scheduleWindows ?? (v.scheduleWindows = new List<ScheduleWindow>()));
            }

            v.overrideVehicleRestrictions = GUILayout.Toggle(v.overrideVehicleRestrictions, " Override vehicle restrictions", _styleBody);
            if (v.overrideVehicleRestrictions)
            {
                if (GUILayout.Button($"Articulated policy: {v.articulatedPolicyOverride}", _styleButton, GUILayout.Height(22)))
                    v.articulatedPolicyOverride = NextEnumValue(v.articulatedPolicyOverride);

                // [FIX] Was free comma-separated text entry — per spec
                // ("show a list to select from for series"), reuses the
                // same real checklist the mainline section uses.
                GUILayout.Label("Allowed fleet series (blank = any):", _styleBodyDim);
                DrawFleetSeriesChecklist(v.allowedFleetSeriesOverride);
            }

            GUILayout.EndVertical();
            GUILayout.Space(4);
        }

        if (GUILayout.Button("+ Add Variant", _styleButton, GUILayout.Height(26)))
            AddVariant();
    }

    // ── Small labeled-field helpers (GUILayout) ──────────────────────────
    private string LabeledTextField(string label, string value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _styleBodyDim, GUILayout.Width(150));
        string result = GUILayout.TextField(value ?? "", _styleTextField);
        GUILayout.EndHorizontal();
        return result;
    }

    private float LabeledFloatField(string label, float value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _styleBodyDim, GUILayout.Width(150));
        string s = GUILayout.TextField(value.ToString("F1"), _styleTextField);
        GUILayout.EndHorizontal();
        return float.TryParse(s, out float r) ? r : value;
    }

    private int LabeledIntField(string label, int value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, _styleBodyDim, GUILayout.Width(150));
        string s = GUILayout.TextField(value.ToString(), _styleTextField);
        GUILayout.EndHorizontal();
        return int.TryParse(s, out int r) ? r : value;
    }

    private ArticulatedRequirement NextEnumValue(ArticulatedRequirement v)
    {
        var values = (ArticulatedRequirement[])System.Enum.GetValues(typeof(ArticulatedRequirement));
        int idx = System.Array.IndexOf(values, v);
        return values[(idx + 1) % values.Length];
    }

    // ═════════════════════════════════════════════════════════════════════
    //  SEQUENCE LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════
    private void RefreshPreview()
    {
        var city = ResolveCityManager();
        if (city == null || _currentSeq == null || _currentSeq.Count == 0)
        {
            _previewNodes = new List<RouteNode>();
            return;
        }
        var raw = BuildRawPath(_currentSeq, city);
        _previewNodes = ApplyLaneOffsets(SimplifyToCorners(raw.points));
    }

    private void FinishCurrentSequence()
    {
        var city = ResolveCityManager();
        if (city == null) { _statusMessage = "CityManager not found in scene."; return; }

        if (_extending) { FinishExtension(city); return; }

        // [FIX] This used to silently force-append the terminal stop as a
        // real, SCHEDULED RouteStopBinding the instant the last click wasn't
        // literally that stop — which fights directly against the deliberate
        // "drop-off point short of the real terminal" pattern used
        // throughout this project specifically to avoid an outbound stop
        // binding overlapping the inbound direction at the same terminal.
        // The last waypoint YOU clicked is now always the real endpoint,
        // whatever it is — a plain road/drop-off point (geometry only, no
        // schedule entry, exactly like any other road waypoint) or the
        // terminal stop itself if that's genuinely what you clicked. No
        // more auto-inject, no more manually deleting it afterward.
        // [FIX] Was always comparing against the MAINLINE _terminalA/
        // _terminalZ, even while building a variant with its own terminal
        // override — so finishing a variant that correctly ended at ITS
        // terminal would incorrectly warn "ends at a drop-off point" just
        // because that stop isn't the mainline terminal. Resolves the
        // active variant's own override (if any) first.
        BusStopData expectedEnd;
        if (_activeVariantIndex >= 0)
        {
            var activeVariant = _variants[_activeVariantIndex];
            string overrideCode = _buildingOutboundDirection ? activeVariant.terminalZCodeOverride : activeVariant.terminalACodeOverride;
            expectedEnd = !string.IsNullOrEmpty(overrideCode) ? FindStopByCode(overrideCode) : (_buildingOutboundDirection ? _terminalZ : _terminalA);
        }
        else
        {
            expectedEnd = _buildingOutboundDirection ? _terminalZ : _terminalA;
        }
        bool endsAtTerminal = _currentSeq.Count > 0
            && _currentSeq[_currentSeq.Count - 1].IsStop
            && _currentSeq[_currentSeq.Count - 1].stop == expectedEnd;

        if (_currentSeq.Count < 2)
        {
            _statusMessage = "Click at least one more stop or road point before finishing.";
            return;
        }

        string dropOffNote = (expectedEnd != null && !endsAtTerminal)
            // Informational only — does NOT block finishing and does NOT
            // add anything. If this route genuinely should end exactly at
            // the terminal stop, click the terminal stop itself before
            // finishing; if it's a deliberate drop-off short of it (to
            // avoid overlapping the other direction), this is expected.
            ? $"Ends at a drop-off point, not Terminal {(_buildingOutboundDirection ? "Z" : "A")} itself ({expectedEnd.stopName}) — fine if intentional. "
            : "";

        var pathResult = BuildRawPath(_currentSeq, city);
        var nodes      = ApplyLaneOffsets(SimplifyToCorners(pathResult.points));
        var bindings   = BuildBindings(_currentSeq, pathResult);

        if (_activeVariantIndex < 0)
        {
            if (_buildingOutboundDirection)
            {
                _outboundSeq      = ExtractStops(_currentSeq);
                _outboundNodes    = nodes;
                _outboundBindings = bindings;
                if (bindings.Count > 0) _oneWayMinutes = bindings[bindings.Count - 1].minutesFromStart;

                _stage = Stage.BuildInbound;
                _currentSeq = new List<RouteWaypoint> { RouteWaypoint.FromStop(_terminalZ, city) };
                _buildingOutboundDirection = false;
                RefreshPreview();
                _statusMessage = dropOffNote + "Outbound saved. Now click stops (or road points) from Z back to A for the inbound route.";
            }
            else
            {
                _inboundSeq      = ExtractStops(_currentSeq);
                _inboundNodes    = nodes;
                _inboundBindings = bindings;

                _previewNodes = new List<RouteNode>();
                _stage = Stage.EditDetails;
                _statusMessage = dropOffNote + "Inbound saved. Fill in the route details below, then create the asset.";
            }
        }
        else
        {
            var variant = _variants[_activeVariantIndex];
            variant.overrideRoute = true;
            if (_buildingOutboundDirection)
            {
                variant.outboundNodesOverride = nodes;
                variant.outboundStopsOverride = bindings;
            }
            else
            {
                variant.inboundNodesOverride = nodes;
                variant.inboundStopsOverride = bindings;
            }

            _activeVariantIndex = -1;
            _previewNodes = new List<RouteNode>();
            _stage = Stage.EditDetails;
            _statusMessage = $"Variant {variant.variantLetter} route saved.";
        }
    }

    private void AddVariant()
    {
        char nextLetter = (char)('A' + _variants.Count);
        _variants.Add(new RouteVariantData { variantLetter = nextLetter.ToString() });
    }

    private void StartVariantBuild(int index, bool outbound)
    {
        if (_terminalA == null || _terminalZ == null)
        {
            _statusMessage = "Set terminals before building a variant route.";
            return;
        }
        _activeVariantIndex = index;
        _buildingOutboundDirection = outbound;
        var city = ResolveCityManager();

        // [FIX] Was always seeding the sequence with the MAINLINE _terminalA/
        // _terminalZ regardless of this variant's own terminalACodeOverride/
        // terminalZCodeOverride — so a variant with a different terminal
        // still showed the mainline stop as its start in the stop list.
        // Resolve the variant's own override first, falling back to
        // mainline only when it has none set for that side.
        var variant = _variants[index];
        BusStopData startStop;
        if (outbound)
            startStop = !string.IsNullOrEmpty(variant.terminalACodeOverride) ? FindStopByCode(variant.terminalACodeOverride) : _terminalA;
        else
            startStop = !string.IsNullOrEmpty(variant.terminalZCodeOverride) ? FindStopByCode(variant.terminalZCodeOverride) : _terminalZ;

        if (startStop == null)
        {
            _statusMessage = $"Variant {variant.variantLetter}'s terminal override didn't resolve to a real stop — check it under \"Override terminals\" before building.";
            startStop = outbound ? _terminalA : _terminalZ; // fall back so the tool doesn't hard-stop
        }

        _currentSeq = new List<RouteWaypoint> { RouteWaypoint.FromStop(startStop, city) };
        _stage = outbound ? Stage.BuildOutbound : Stage.BuildInbound;
        RefreshPreview();
        _statusMessage = $"Building variant {variant.variantLetter} {(outbound ? "outbound" : "inbound")} from {startStop?.stopName} \u2014 click stops or road points in order.";
    }

    // ═════════════════════════════════════════════════════════════════════
    //  EXTEND ROUTE — move Terminal A or Terminal Z outward, editing bus
    //  stops from wherever the route currently ends, without redoing the
    //  whole outbound/inbound path.
    // ═════════════════════════════════════════════════════════════════════
    private void StartRelocateTerminal(bool movingZ)
    {
        if (_terminalA == null || _terminalZ == null)
        {
            _statusMessage = "Set both terminals before relocating one.";
            return;
        }
        _relocatingTerminalIsZ = movingZ;
        _statusMessage = $"Click a stop on the map to be the new Terminal {(movingZ ? "Z" : "A")}. Terminal {(movingZ ? "A" : "Z")} ({(movingZ ? _terminalA.stopName : _terminalZ.stopName)}) stays unchanged.";
    }

    private void StartExtend(bool movingZ)
    {
        if (_terminalA == null || _terminalZ == null)
        {
            _statusMessage = "Set both terminals before extending the route.";
            return;
        }
        var city = ResolveCityManager();
        _extending      = true;
        _extendMovingZ  = movingZ;
        _activeVariantIndex = -1;

        if (movingZ)
        {
            // New stops continue the OUTBOUND path forward, past the current
            // Terminal Z, in the direction of travel.
            _buildingOutboundDirection = true;
            _currentSeq = new List<RouteWaypoint> { RouteWaypoint.FromStop(_terminalZ, city) };
            _stage = Stage.BuildOutbound;
            _statusMessage = $"Extending past Terminal Z ({_terminalZ.stopName}). Click stops/road points to continue outbound, ending on the new Terminal Z.";
        }
        else
        {
            // New stops continue the INBOUND path forward, past the current
            // Terminal A, in the direction of travel.
            _buildingOutboundDirection = false;
            _currentSeq = new List<RouteWaypoint> { RouteWaypoint.FromStop(_terminalA, city) };
            _stage = Stage.BuildInbound;
            _statusMessage = $"Extending past Terminal A ({_terminalA.stopName}). Click stops/road points to continue inbound, ending on the new Terminal A.";
        }
        RefreshPreview();
    }

    private void CancelExtend()
    {
        _extending = false;
        _previewNodes = new List<RouteNode>();
        _stage = Stage.EditDetails;
    }

    /// <summary>Reverses a waypoint sequence — used to build the "other side"
    /// geometry (the new stretch as seen from the opposite direction of
    /// travel) so it can be prepended to the route that wasn't being
    /// actively extended, keeping both outbound and inbound continuous
    /// through the new stops.</summary>
    private List<RouteWaypoint> ReversedSeq(List<RouteWaypoint> seq)
    {
        var rev = new List<RouteWaypoint>(seq);
        rev.Reverse();
        return rev;
    }

    private void FinishExtension(CityManager city)
    {
        if (_currentSeq.Count < 2)
        {
            _statusMessage = "Click at least one more stop before finishing the extension.";
            return;
        }
        var lastWp = _currentSeq[_currentSeq.Count - 1];
        if (!lastWp.IsStop)
        {
            _statusMessage = "The new terminal must be an actual stop — click a stop to finish, not a road point.";
            return;
        }

        // Forward geometry: old terminal -> ... -> new terminal.
        var fwdPath     = BuildRawPath(_currentSeq, city);
        var fwdNodes    = ApplyLaneOffsets(SimplifyToCorners(fwdPath.points));
        var fwdBindings = BuildBindings(_currentSeq, fwdPath);

        // Reverse geometry: new terminal -> ... -> old terminal, used to
        // prepend a matching stretch onto the OTHER direction's path so
        // both outbound and inbound stay continuous loops through the
        // new stops.
        var revSeq      = ReversedSeq(_currentSeq);
        var revPath     = BuildRawPath(revSeq, city);
        var revNodes    = ApplyLaneOffsets(SimplifyToCorners(revPath.points));
        var revBindings = BuildBindings(revSeq, revPath);

        BusStopData newTerminal = lastWp.stop;

        if (_extendMovingZ)
        {
            // Append the new stretch to the end of OUTBOUND.
            if (fwdNodes.Count > 1) _outboundNodes.AddRange(fwdNodes.GetRange(1, fwdNodes.Count - 1));
            // [FIX] baseMin/shiftAmt below used to be `float` — same whole-
            // minutes inconsistency as the details-UI fix above. Using
            // Mathf.RoundToInt here works whether minutesFromStart is
            // itself int or float, and guarantees the extended stretch's
            // times land on the same whole-minute grid as everything
            // BuildBindings already produces.
            int baseMin = _outboundBindings.Count > 0 ? Mathf.RoundToInt(_outboundBindings[_outboundBindings.Count - 1].minutesFromStart) : 0;
            for (int i = 1; i < fwdBindings.Count; i++)
            {
                var b = fwdBindings[i];
                b.minutesFromStart += baseMin;
                _outboundBindings.Add(b);
            }
            _oneWayMinutes = _outboundBindings.Count > 0 ? _outboundBindings[_outboundBindings.Count - 1].minutesFromStart : _oneWayMinutes;

            // Prepend the reversed stretch to the front of INBOUND (new
            // terminal -> old terminal, then the existing inbound path).
            var newInboundNodes = revNodes.Count > 1 ? revNodes.GetRange(0, revNodes.Count - 1) : new List<RouteNode>();
            newInboundNodes.AddRange(_inboundNodes);
            _inboundNodes = newInboundNodes;

            int shiftAmt = revBindings.Count > 0 ? Mathf.RoundToInt(revBindings[revBindings.Count - 1].minutesFromStart) : 0;
            var newInboundBindings = revBindings.Count > 1 ? revBindings.GetRange(0, revBindings.Count - 1) : new List<RouteStopBinding>();
            foreach (var b in _inboundBindings)
            {
                var bb = b;
                bb.minutesFromStart += shiftAmt;
                newInboundBindings.Add(bb);
            }
            _inboundBindings = newInboundBindings;

            _terminalZ = newTerminal;
            _statusMessage = $"Terminal Z moved to {newTerminal.stopName}. Outbound extended and inbound reconnected.";
        }
        else
        {
            // Append the new stretch to the end of INBOUND.
            if (fwdNodes.Count > 1) _inboundNodes.AddRange(fwdNodes.GetRange(1, fwdNodes.Count - 1));
            int baseMin = _inboundBindings.Count > 0 ? Mathf.RoundToInt(_inboundBindings[_inboundBindings.Count - 1].minutesFromStart) : 0;
            for (int i = 1; i < fwdBindings.Count; i++)
            {
                var b = fwdBindings[i];
                b.minutesFromStart += baseMin;
                _inboundBindings.Add(b);
            }

            // Prepend the reversed stretch to the front of OUTBOUND (new
            // terminal -> old terminal, then the existing outbound path).
            var newOutboundNodes = revNodes.Count > 1 ? revNodes.GetRange(0, revNodes.Count - 1) : new List<RouteNode>();
            newOutboundNodes.AddRange(_outboundNodes);
            _outboundNodes = newOutboundNodes;

            int shiftAmt = revBindings.Count > 0 ? Mathf.RoundToInt(revBindings[revBindings.Count - 1].minutesFromStart) : 0;
            var newOutboundBindings = revBindings.Count > 1 ? revBindings.GetRange(0, revBindings.Count - 1) : new List<RouteStopBinding>();
            foreach (var b in _outboundBindings)
            {
                var bb = b;
                bb.minutesFromStart += shiftAmt;
                newOutboundBindings.Add(bb);
            }
            _outboundBindings = newOutboundBindings;
            if (_outboundBindings.Count > 0) _oneWayMinutes = _outboundBindings[_outboundBindings.Count - 1].minutesFromStart;

            _terminalA = newTerminal;
            _statusMessage = $"Terminal A moved to {newTerminal.stopName}. Inbound extended and outbound reconnected.";
        }

        _outboundSeq = ResolveStopSequence(_outboundBindings);
        _inboundSeq  = ResolveStopSequence(_inboundBindings);

        _extending = false;
        _activeVariantIndex = -1;
        _previewNodes = new List<RouteNode>();
        _stage = Stage.EditDetails;
    }

    private void ResetTool()
    {
        _extending = false;
        _stage = Stage.PickTerminalA;
        _terminalA = null;
        _terminalZ = null;
        _currentSeq = new List<RouteWaypoint>();
        _outboundSeq.Clear();
        _inboundSeq.Clear();
        _outboundNodes = new List<RouteNode>();
        _inboundNodes  = new List<RouteNode>();
        _outboundBindings = new List<RouteStopBinding>();
        _inboundBindings  = new List<RouteStopBinding>();
        _previewNodes = new List<RouteNode>();
        _variants.Clear();
        _terminalOverrideExpanded.Clear();
        _activeVariantIndex = -1;
        _allowedFleetSeries.Clear();
        _scheduleWindows.Clear();
        _pickingVariantTerminalFor = -1;
        _relocatingTerminalIsZ = null;
        _loadedAsset = null;
#if UNITY_EDITOR
        _pickerAsset = null;
        _routeAssetListLoaded = false;
#endif
        _routeNumber = "";
        _routeName   = "";
        _statusMessage = "Click a stop on the map to set Terminal A.";
    }

    // ═════════════════════════════════════════════════════════════════════
    //  ROUTE WAYPOINT — either a real stop, or a plain clicked point on the
    //  road network (corner/dead-end/mid-road spot). Path geometry treats
    //  both identically; only stops get a RouteStopBinding (schedule/dwell
    //  entry) out of BuildBindings.
    // ═════════════════════════════════════════════════════════════════════
    private class RouteWaypoint
    {
        public BusStopData stop;      // non-null if this waypoint IS a stop
        public string       roadCode; // road this point resolves onto (stop's parentRoadCode, or the clicked road's code)
        public RoadSegment  road;
        public float        t;        // t-value along road
        public Vector3      pos;      // resolved world position
        /// <summary>[ADD] One-way flag for this waypoint's road — a one-way
        /// road gets NO lane offset (dead-center, no opposing traffic to stay
        /// clear of), see RoadSegment.GetBusOffsetPosition. Looked up from
        /// CityManager.roadDefinitions since RoadSegment itself doesn't carry
        /// isOneWay (that lives on the definition, not the built segment).</summary>
        public bool         isOneWay;

        public bool IsStop => stop != null;

        public static RouteWaypoint FromStop(BusStopData s, CityManager city)
        {
            var road = s.resolvedRoad ?? city.GetRoad(s.parentRoadCode);
            var def  = city?.roadDefinitions?.Find(d => d.roadCode == s.parentRoadCode);
            return new RouteWaypoint
            {
                stop     = s,
                roadCode = s.parentRoadCode,
                road     = road,
                t        = s.tValue,
                pos      = road != null ? road.EvaluatePosition(s.tValue) : s.GetWorldPosition(),
                isOneWay = def != null && def.isOneWay
            };
        }

        public static RouteWaypoint FromRoadPoint(string roadCode, RoadSegment road, float t, Vector3 pos, bool isOneWay) =>
            new RouteWaypoint { stop = null, roadCode = roadCode, road = road, t = t, pos = pos, isOneWay = isOneWay };
    }

    /// <summary>Pulls just the stop waypoints back out of a mixed sequence —
    /// used only to keep _outboundSeq/_inboundSeq (display-only, reload-time
    /// reconstruction) as plain stop lists like they always were.</summary>
    private List<BusStopData> ExtractStops(List<RouteWaypoint> seq)
    {
        var result = new List<BusStopData>();
        if (seq == null) return result;
        foreach (var wp in seq)
            if (wp.IsStop) result.Add(wp.stop);
        return result;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  NODE-GENERATION ENGINE
    // ═════════════════════════════════════════════════════════════════════
    private struct RawPoint
    {
        public Vector3 pos;
        public float   roadWidth;
        public bool    isCurved;
        /// <summary>[FIX] True for points sampled directly off a RoadGraph edge —
        /// their position already includes the correct bus-offset (baked in via
        /// RoadSegment.GetBusOffsetPosition, same convention ApplyLaneOffsets
        /// below applies to everything else), so ApplyLaneOffsets must pass them
        /// through unchanged instead of offsetting a second time. Used to say
        /// "lane" (RoadEdge.laneIndex/laneWidth via GetLanePosition) — that was
        /// the per-physical-traffic-lane offset, not the bus-route convention;
        /// cross-road hops now use the same GetBusOffsetPosition everything
        /// else does, see BuildRawPath.</summary>
        public bool    preOffset;
        /// <summary>[ADD] One-way flag, carried through from the RouteWaypoint(s)
        /// this point was derived from — a one-way road gets zero lane offset
        /// in ApplyLaneOffsets (dead-center, no opposing traffic to stay clear
        /// of). Meaningless/ignored when preOffset is true (already baked in).</summary>
        public bool    isOneWay;
        /// <summary>Road this point resolves onto, and its t-value there — set
        /// only for points genuinely sampled off a single road's own spline,
        /// left empty/-1 for cross-road hop/corner points where there's no
        /// single road to reference. [FIX] This USED to claim ApplyLaneOffsets
        /// carries these onto RouteNode for live spline evaluation at runtime —
        /// that's false and was never true of the actual RouteNode class
        /// (BusRouteData.cs: just {Vector3 position; bool isCurve;}, no road
        /// reference, confirmed directly). roadCode/t here are consumed ONLY at
        /// generation time (AddCurveSamplesIfNeeded below), then baked into a
        /// plain Vector3 like everything else — there is no live-road
        /// evaluation at runtime, anywhere, for route nodes. See
        /// curveDetectionArcChordRatio's tooltip for why that distinction
        /// actually matters (it's the whole reason curved same-road stretches
        /// need real sampled points now instead of being skipped).</summary>
        public string  roadCode;
        public float   t;
    }

    /// <summary>points: real geometry connections — a hop to a different
    /// road, the route's very first/last waypoint, or (as of the
    /// AddCurveSamplesIfNeeded fix) sampled points along a same-road stretch
    /// that genuinely curves. NOT one per stop, and NOT dense sampling on a
    /// straight run — a run only gets extra points when it actually needs
    /// them to hug the road's real shape.
    /// waypointCumulativeDistance: one entry per waypoint in the ORIGINAL
    /// seq, giving its running distance from route start — tracked
    /// completely independent of `points`, so BuildBindings can time every
    /// stop correctly even though most stops never become a geometry node.</summary>
    private struct RawPathResult { public List<RawPoint> points; public List<float> waypointCumulativeDistance; }

    /// <summary>Arc length along a road's real spline between t0 and t1 —
    /// used to measure accurate stop-to-stop distance for scheduling even on
    /// a curved stretch, without needing to store any of these samples as
    /// actual path nodes.</summary>
    private static float RoadArcLength(RoadSegment road, float t0, float t1, int samples)
    {
        if (road == null) return 0f;
        float dist = 0f;
        Vector3 prevPos = road.EvaluatePosition(t0);
        for (int s = 1; s <= samples; s++)
        {
            float t = Mathf.Lerp(t0, t1, s / (float)samples);
            Vector3 pos = road.EvaluatePosition(t);
            dist += Vector3.Distance(prevPos, pos);
            prevPos = pos;
        }
        return dist;
    }

    /// <summary>[FIX] Restores real curve-following for same-road runs — see
    /// curveDetectionArcChordRatio's tooltip and the RawPoint.roadCode/t
    /// comment for the full story of why this was missing. arcLength is
    /// passed in rather than recomputed since BuildRawPath's caller already
    /// has it as `stepDist` for the sameRoad case. No-op (adds nothing) when
    /// prev/wp aren't actually the same road, or the stretch between them is
    /// straight enough that arcLength/chordDistance doesn't clear
    /// curveDetectionArcChordRatio.</summary>
    private void AddCurveSamplesIfNeeded(List<RawPoint> points, RouteWaypoint prev, RouteWaypoint wp, float arcLength)
    {
        if (prev.road == null || wp.road == null || prev.road != wp.road) return;

        float chordDist = Vector3.Distance(prev.pos, wp.pos);
        if (chordDist < 0.01f) return; // same point, nothing to sample

        if (arcLength / chordDist < curveDetectionArcChordRatio) return; // straight enough as-is

        int samples = Mathf.Max(1, curveSampleCount);
        for (int s = 1; s <= samples; s++)
        {
            float t = Mathf.Lerp(prev.t, wp.t, s / (float)(samples + 1));
            points.Add(new RawPoint
            {
                pos = prev.road.EvaluatePosition(t),
                roadWidth = prev.road.roadWidth,
                roadCode = prev.roadCode,
                t = t,
                isOneWay = prev.isOneWay // prev.road == wp.road here, same one-way status either way
            });
        }
    }

    /// <summary>
    /// Walks an ordered waypoint sequence (stops and/or plain road points)
    /// and produces the raw (un-offset) path geometry. A geometry point is
    /// added where the road genuinely changes (a real connection), at the
    /// route's start/end, or — via AddCurveSamplesIfNeeded — partway through
    /// a same-road run that curves enough to need it. A whole run of stops
    /// sitting on the same STRAIGHT stretch of road, however many there are,
    /// still collapses to nothing in between; there is no live road-spline
    /// evaluation left downstream of this (RouteNode is a bare baked Vector3,
    /// see the RawPoint.roadCode/t comment above), so a curved run has to get
    /// its real shape baked in right here or it never will.
    /// </summary>
    private RawPathResult BuildRawPath(List<RouteWaypoint> seq, CityManager city)
    {
        var points   = new List<RawPoint>();
        var wpDist   = new List<float>();
        var result   = new RawPathResult { points = points, waypointCumulativeDistance = wpDist };

        if (seq == null || seq.Count == 0) return result;

        RouteWaypoint first = seq[0];
        points.Add(new RawPoint
        {
            pos = first.pos,
            roadWidth = first.road != null ? first.road.roadWidth : 0f,
            roadCode = first.road != null ? first.roadCode : "",
            t = first.road != null ? first.t : -1f,
            isOneWay = first.isOneWay
        });
        wpDist.Add(0f);

        float cumulative = 0f;
        RouteWaypoint prev = first;

        for (int i = 1; i < seq.Count; i++)
        {
            RouteWaypoint wp = seq[i];
            bool isLastWaypoint = (i == seq.Count - 1);

            bool sameRoad = prev.road != null && wp.road != null &&
                            !string.IsNullOrEmpty(prev.roadCode) && prev.roadCode == wp.roadCode;

            // Distance for THIS step, tracked regardless of whether wp ends
            // up becoming a geometry node — this is what lets a stop in the
            // middle of a same-road run still get correct schedule timing.
            float stepDist = sameRoad
                ? RoadArcLength(prev.road, prev.t, wp.t, 12)
                : Vector3.Distance(prev.pos, wp.pos);
            cumulative += stepDist;
            wpDist.Add(cumulative);

            if (!sameRoad)
            {
                // Cross-road hop — a genuine connection between roads, so
                // this legitimately becomes real geometry. Resolved via
                // CityManager.Graph + RoadGraphPathfinder exactly as before;
                // operates on plain world positions, so a raw clicked
                // corner/dead-end/road-point works identically to a stop
                // here — no special-casing needed. Every point produced is
                // sampled off an actual RoadEdge, so the path is
                // structurally incapable of leaving the road network; the
                // only remaining failure mode is "no path exists at all"
                // (a genuinely disconnected road graph), which logs a
                // warning and falls back to a dogleg as an explicit last
                // resort.
                Vector3 hopStart = points[points.Count - 1].pos;
                Vector3 hopEnd   = wp.pos;

                var graph = city.Graph;
                List<RoadEdge> edgePath = graph != null ? RoadGraphPathfinder.FindPath(graph, hopStart, hopEnd) : null;

                if (edgePath != null)
                {
                    // [FIX] This used to resample every edge at
                    // graphHopSampleSpacing (8m) intervals, so a single
                    // ~400m stretch of road produced ~50 raw points before
                    // any simplification even ran — and since outbound and
                    // inbound routinely take genuinely different real
                    // streets (a couplet, like a one-way pair), one
                    // direction could easily cross far more/fewer edges
                    // than the other, which is exactly the "5 nodes vs 50
                    // nodes" asymmetry. RoadGraph.Build() already creates a
                    // RoadNode at every REAL geometric intersection/
                    // T-junction/dead-end (see RoadGraph.cs) — those
                    // positions already ARE "just the intersections or
                    // changes," so this now emits exactly one point per
                    // edge endpoint instead of resampling within it. A
                    // single edge with genuine curvature (a curved road
                    // between two intersections) still only gets its own
                    // two endpoints here — if that curve's actual shape
                    // needs to render smoothly, that's a mesh/spline-follow
                    // concern for whatever consumes this node list at
                    // runtime, not something the ROUTE DEFINITION itself
                    // needs extra points for.
                    foreach (var edge in edgePath)
                    {
                        // [FIX] Was GetLanePosition(edge.tEnd, edge.laneIndex,
                        // edge.laneWidth) — the per-physical-traffic-lane
                        // offset. Now uses the same width-tiered bus-offset
                        // convention as everything else (RoadSegment.
                        // GetBusOffsetPosition — 0 offset on a one-way edge),
                        // so a cross-road hop lands on the same side/distance
                        // a hand-clicked point on the same road would.
                        Vector3 pos = edge.segment.GetBusOffsetPosition(edge.tEnd, edge.isOneWay, !edge.Forward);
                        points.Add(new RawPoint { pos = pos, roadWidth = edge.segment.roadWidth, isOneWay = edge.isOneWay, preOffset = true });
                    }
                }
                else
                {
                    string prevLabel = prev.IsStop ? prev.stop.stopCode : $"road point on {prev.roadCode}";
                    string wpLabel   = wp.IsStop   ? wp.stop.stopCode   : $"road point on {wp.roadCode}";
                    Debug.LogWarning($"[BusRouteMaker] No road-graph path found between '{prevLabel}' and " +
                                      $"'{wpLabel}' \u2014 the road network appears disconnected here. " +
                                      "Inserting a straight dogleg as a last resort; please check this section of the " +
                                      "route in the Inspector.");
                    Vector3 corner = GuessDoglegCorner(hopStart, hopEnd);
                    points.Add(new RawPoint { pos = corner, roadWidth = wp.road != null ? wp.road.roadWidth : 0f, isOneWay = wp.isOneWay });
                }

                points.Add(new RawPoint { pos = wp.pos, roadWidth = wp.road != null ? wp.road.roadWidth : 0f, roadCode = wp.roadCode, t = wp.t, isOneWay = wp.isOneWay });
            }
            else if (isLastWaypoint)
            {
                // Same road as prev, but it's the final waypoint of the
                // whole route. [FIX] Sample the curve first (see
                // AddCurveSamplesIfNeeded/curveDetectionArcChordRatio) — this
                // used to skip straight to the endpoint on the assumption a
                // downstream 'RoadSpanSegment' would reproduce the road's
                // real curve automatically, but that mechanism doesn't exist
                // (RouteNode is a bare baked Vector3, always was by the time
                // this runs — see the RawPoint.roadCode/t comment above).
                // Then always emit the endpoint so the path actually ends
                // where it's supposed to.
                AddCurveSamplesIfNeeded(points, prev, wp, stepDist);
                points.Add(new RawPoint { pos = wp.pos, roadWidth = wp.road != null ? wp.road.roadWidth : 0f, roadCode = wp.roadCode, t = wp.t, isOneWay = wp.isOneWay });
            }
            else
            {
                // Same road, not the last waypoint. [FIX] Used to add
                // nothing here at all — same stale assumption as above. If
                // this stretch of road genuinely curves, sample it now
                // instead of silently cutting a straight chord across the
                // curve; if it's straight (or close enough), this is a no-op
                // and behavior is unchanged from before.
                AddCurveSamplesIfNeeded(points, prev, wp, stepDist);
            }
            // `prev` for the NEXT iteration's sameRoad check still needs to
            // be `wp` (so distance/road-change detection keeps working)
            // regardless of which branch above ran.

            prev = wp;
        }

        // [NEW] Enforce the actual rule this city's road grid runs on: one
        // axis changes per segment, never both at once. Every hand-built
        // route in this project already follows this exactly; the raw
        // pathfinder/click output never did (individual RoadGraph edges,
        // or a straight click-to-click hop, can easily move both X and Z
        // in the same step). This is what was actually producing corners
        // that "skip" or cut a diagonal instead of following the road.
        result.points = EnforceSingleAxisSegments(points);
        return result;
    }

    /// <summary>
    /// [NEW] Walks a raw point list and inserts a corner point wherever two
    /// consecutive points would move on BOTH X and Z at once by more than
    /// axisPurityToleranceMeters — the single-axis-per-segment rule every
    /// hand-built route in this project already follows, which the raw
    /// pathfinder-edge / straight-click-to-click output never enforced on
    /// its own. Mirrors GuessDoglegCorner's convention (move whichever axis
    /// has the smaller delta first) so the inserted corner reads the same
    /// way a manually-placed one would. Curved points (isCurve) are exempt
    /// on either side of a step — they're tracing an actual curved road
    /// spline, not a grid corner, and are allowed to move diagonally.
    /// </summary>
    private List<RawPoint> EnforceSingleAxisSegments(List<RawPoint> raw)
    {
        if (raw == null || raw.Count < 2) return raw ?? new List<RawPoint>();

        var out_ = new List<RawPoint> { raw[0] };
        for (int i = 1; i < raw.Count; i++)
        {
            RawPoint prevOut = out_[out_.Count - 1];
            RawPoint cur     = raw[i];

            if (!prevOut.isCurved && !cur.isCurved)
            {
                float dx = Mathf.Abs(cur.pos.x - prevOut.pos.x);
                float dz = Mathf.Abs(cur.pos.z - prevOut.pos.z);

                if (dx > axisPurityToleranceMeters && dz > axisPurityToleranceMeters)
                {
                    Vector3 cornerPos = dx <= dz
                        ? new Vector3(cur.pos.x, prevOut.pos.y, prevOut.pos.z)
                        : new Vector3(prevOut.pos.x, prevOut.pos.y, cur.pos.z);
                    out_.Add(new RawPoint { pos = cornerPos, roadWidth = cur.roadWidth });
                }
            }

            out_.Add(cur);
        }

        return out_;
    }

    /// <summary>Fallback when no intersection chain is found: a single corner that
    /// changes whichever axis has the smaller delta first, so at least one axis
    /// still changes alone, matching the "one axis at a time" rule as best we can
    /// without real road data for that connection.</summary>
    private Vector3 GuessDoglegCorner(Vector3 from, Vector3 to)
    {
        float dx = Mathf.Abs(to.x - from.x);
        float dz = Mathf.Abs(to.z - from.z);
        return dx >= dz ? new Vector3(to.x, from.y, from.z) : new Vector3(from.x, from.y, to.z);
    }

    /// <summary>
    /// Applies the perpendicular lane offset to every raw point based on local
    /// direction of travel (look-ahead to the next point, or look-behind for the
    /// final point) and that point's associated road width. right = Cross(up, dir)
    /// is exactly the axis rule described in the spec for grid-aligned roads, and
    /// generalises correctly for diagonal/curved ones.
    /// </summary>
    private List<RouteNode> ApplyLaneOffsets(List<RawPoint> raw)
    {
        var nodes = new List<RouteNode>();
        if (raw == null || raw.Count == 0) return nodes;
        if (raw.Count == 1)
        {
            nodes.Add(new RouteNode { position = SnapToHalfV3(raw[0].pos), isCurve = false });
            return nodes;
        }

        for (int i = 0; i < raw.Count; i++)
        {
            if (raw[i].preOffset)
            {
                // Already the correct lane position, sampled straight off a
                // RoadGraph edge — offsetting again would push it off the road.
                // Cross-road hop points never carry a single roadCode anyway.
                // [FIX] Still snapped to the half-grid now (was CleanPosition,
                // 2-decimal rounding only) — see SnapToHalfV3's comment.
                nodes.Add(new RouteNode { position = SnapToHalfV3(raw[i].pos), isCurve = raw[i].isCurved });
                continue;
            }

            Vector3 dir = (i < raw.Count - 1) ? raw[i + 1].pos - raw[i].pos : raw[i].pos - raw[i - 1].pos;
            dir.y = 0f;

            if (dir.sqrMagnitude < 0.0001f)
            {
                dir = (i > 0) ? (raw[i].pos - raw[i - 1].pos) : Vector3.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            }
            dir.Normalize();

            Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;

            // SPEC: locked scale — width 5 -> 0.5, width 7 -> 1.5, width 14 ->
            // 5 offset each side, and the result must land EXACTLY on a .5 or
            // .0, never an arbitrary fraction. [ADD] A one-way road gets ZERO
            // offset — dead-center, no opposing traffic to stay clear of. Same
            // convention RoadSegment.GetBusOffsetPosition applies for
            // RoadGraph-derived points (cross-road hops above), so a
            // one-way road never gets offset regardless of which code path
            // placed the point.
            float laneOffset = raw[i].isOneWay ? 0f : SnapToHalf(GetLaneOffsetForWidth(raw[i].roadWidth));

            // [FIX] RouteNode no longer carries roadCode/roadT/roadLaneOffset —
            // routes are pure baked Vector3 positions now, no live-road
            // snapping at runtime. The lane offset is already applied directly
            // into position above, so there's nothing left to carry through.
            // [FIX] SnapToHalfV3 instead of CleanPosition — see its comment;
            // this is the actual drift fix (100.5 -> 100.25 -> 99.8 is gone,
            // every node now lands exactly on the .0/.5 grid, matching how
            // every hand-built route in this project already looks).
            nodes.Add(new RouteNode
            {
                position = SnapToHalfV3(raw[i].pos + right * laneOffset),
                isCurve  = raw[i].isCurved
            });
        }

        return nodes;
    }

    /// <summary>
    /// Collapses a dense point list down to only the points that actually
    /// matter for the route's SHAPE: the start, the end, real corners
    /// (direction changes past simplifyAngleThresholdDeg), and any point
    /// flagged isCurved (rare now — BuildRawPath itself already only emits
    /// geometry at genuine road connections, not per-stop, so this is
    /// mostly a safety net for hand-edited data rather than something the
    /// normal pipeline leans on).
    ///
    /// This does NOT touch stop timing: BuildBindings computes each stop's
    /// scheduled minutesFromStart from RawPathResult.waypointCumulativeDistance,
    /// tracked independently of the geometry points here — a RouteStopBinding
    /// only ever stores {stopCode, minutesFromStart}, never a node index, so
    /// the bus still dwells at the stop's real position
    /// (BusStopData.GetWorldPosition()) regardless of what this function drops.
    /// </summary>
    private List<RawPoint> SimplifyToCorners(List<RawPoint> raw)
    {
        if (raw == null || raw.Count <= 2) return raw ?? new List<RawPoint>();

        var kept = new List<RawPoint> { raw[0] };

        for (int i = 1; i < raw.Count - 1; i++)
        {
            if (raw[i].isCurved) { kept.Add(raw[i]); continue; } // curve samples always survive

            Vector3 prev = kept[kept.Count - 1].pos;
            Vector3 cur  = raw[i].pos;
            Vector3 next = raw[i + 1].pos;

            Vector3 dirIn  = cur - prev;  dirIn.y  = 0f;
            Vector3 dirOut = next - cur;  dirOut.y = 0f;

            if (dirIn.sqrMagnitude < 0.0001f || dirOut.sqrMagnitude < 0.0001f)
                continue; // zero-length stretch (duplicate point) — always droppable

            float angle = Vector3.Angle(dirIn.normalized, dirOut.normalized);
            if (angle > simplifyAngleThresholdDeg)
                kept.Add(raw[i]); // real corner — the road (or the route) actually turns here
            // else: collinear with both neighbours — redundant mid-straight point, drop it
        }

        kept.Add(raw[raw.Count - 1]);
        return kept;
    }

    /// <summary>Width-tiered offset only — does NOT know about one-way roads.
    /// Callers (ApplyLaneOffsets) are responsible for zeroing this out when
    /// the point's road is one-way; kept separate here since this same
    /// formula is also what RoadSegment.GetBusOffsetPosition mirrors for
    /// RoadGraph-derived points, and that method takes isOneWay directly
    /// rather than through this width-only helper.</summary>
    private float GetLaneOffsetForWidth(float roadWidth)
    {
        return Mathf.Max(0f, (roadWidth - laneOffsetBaseWidth) / Mathf.Max(0.01f, laneOffsetDivisor));
    }

    /// <summary>Snaps a value to the nearest 0.5 — the only two allowed
    /// fractional states per the locked scale spec (.0 or .5, nothing else,
    /// no floating-point noise like .432579).</summary>
    private static float SnapToHalf(float value) => Mathf.Round(value * 2f) * 0.5f;

    /// <summary>Snaps X and Z to the half-unit grid (Y left alone — it's
    /// elevation, not part of the grid convention). This is the actual fix
    /// for the lane-offset drift bug: `right = Cross(up, dir).normalized`
    /// is almost never EXACTLY (1,0,0) or (0,0,1) in floating point even
    /// for a perfectly axis-aligned `dir` — it comes out to something like
    /// 0.999999998 — so `pos + right * laneOffset` (even with a perfectly
    /// clean laneOffset) lands a hair off the grid, and CleanPosition's
    /// 2-decimal rounding (100.23) doesn't fix that, it just preserves the
    /// near-miss instead of forcing it onto .0/.5. Each hop then compounds
    /// the previous hop's tiny error, which is exactly the "100.5 -> 100.25
    /// -> 99.8" drift — every node in this project's hand-built routes
    /// lands clean on the half grid, so the tool's output now does too.</summary>
    private static Vector3 SnapToHalfV3(Vector3 p) =>
        new Vector3(SnapToHalf(p.x), Mathf.Round(p.y * 100f) / 100f, SnapToHalf(p.z));

    /// <summary>
    /// REBUILT — continuous speed-based model, replacing the old tier-bucket
    /// table (see the tooltip on assumedSegmentSpeedKmh above for why: the
    /// old table's "300m = 2 minutes" implied a ~9 km/h average, which is
    /// walking pace, not driving). minutes = ceil((distance / speed +
    /// dwell) / 60), floored at 1 minute so even a very short hop still
    /// reads as a real stop rather than an instant teleport.
    /// </summary>
    private int SegmentMinutesForDistance(float distanceMeters)
    {
        float speedMps = Mathf.Max(0.1f, assumedSegmentSpeedKmh) * 1000f / 3600f;
        float seconds   = distanceMeters / speedMps + Mathf.Max(0f, assumedDwellSeconds);
        return Mathf.Max(1, Mathf.CeilToInt(seconds / 60f));
    }

    private List<RouteStopBinding> BuildBindings(List<RouteWaypoint> seq, RawPathResult path)
    {
        var bindings = new List<RouteStopBinding>();
        if (seq == null || seq.Count == 0) return bindings;

        int   cumulativeMinutes = 0;
        float lastStopDistance  = 0f;
        bool  haveLastStop      = false;

        for (int i = 0; i < seq.Count; i++)
        {
            if (!seq[i].IsStop) continue; // road-only waypoint — geometry only, no schedule entry
            if (i >= path.waypointCumulativeDistance.Count) break;

            float thisDistance = path.waypointCumulativeDistance[i];

            if (haveLastStop) // first stop always starts at 0
                cumulativeMinutes += SegmentMinutesForDistance(thisDistance - lastStopDistance);

            bindings.Add(new RouteStopBinding { stopCode = seq[i].stop.stopCode, minutesFromStart = cumulativeMinutes });
            lastStopDistance = thisDistance;
            haveLastStop = true;
        }

        return bindings;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  ASSET CREATION
    // ═════════════════════════════════════════════════════════════════════
#if UNITY_EDITOR
    private void CreateRouteAsset()
    {
        if (_terminalA == null || _terminalZ == null) { _statusMessage = "Terminals are not set."; return; }
        if (string.IsNullOrEmpty(_routeNumber)) { _statusMessage = "Enter a route number first."; return; }
        if (_outboundNodes.Count == 0 || _inboundNodes.Count == 0)
        {
            _statusMessage = "Build both the outbound and inbound routes first.";
            return;
        }

        var asset = _loadedAsset != null ? _loadedAsset : ScriptableObject.CreateInstance<BusRouteData>();
        asset.routeNumber = _routeNumber;
        asset.routeName   = _routeName;
        asset.routeColor  = _routeColor;
        asset.articulatedPolicy = _articulatedPolicy;
        asset.maxBusesAllowed   = _maxBusesAllowed;
        asset.terminalACode = _terminalA.stopCode;
        asset.terminalZCode = _terminalZ.stopCode;
        asset.destinationNameOutbound = _destOutbound;
        asset.destinationNameInbound  = _destInbound;
        asset.operatingStartMinutes = _operatingStart;
        asset.operatingEndMinutes   = _operatingEnd;
        asset.headwayFromAMinutes   = _headwayA;
        asset.headwayFromZMinutes   = _headwayZ;
        asset.oneWayTripMinutes     = _oneWayMinutes;
        asset.allowedFleetSeries    = new List<int>(_allowedFleetSeries);
        asset.scheduleWindows       = new List<ScheduleWindow>(_scheduleWindows);
        asset.outboundNodes = new List<RouteNode>(_outboundNodes);
        asset.inboundNodes  = new List<RouteNode>(_inboundNodes);
        asset.outboundStops = new List<RouteStopBinding>(_outboundBindings);
        asset.inboundStops  = new List<RouteStopBinding>(_inboundBindings);
        asset.variants       = new List<RouteVariantData>(_variants);

        if (_loadedAsset != null)
        {
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;

            _statusMessage = $"Saved changes to Route {_routeNumber}.";
            _stage = Stage.Done;
            return;
        }

        const string folder = "Assets/Routes";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder("Assets", "Routes");

        string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/Route_{_routeNumber}.asset");
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        EditorGUIUtility.PingObject(asset);
        Selection.activeObject = asset;

        _statusMessage = $"Created {path} \u2014 selected in the Project window. Still fully editable there.";
        _stage = Stage.Done;
    }
#else
    private void CreateRouteAsset()
    {
        _statusMessage = "Route asset creation only works inside the Unity Editor.";
    }
#endif

    // ═════════════════════════════════════════════════════════════════════
    //  STYLES
    // ═════════════════════════════════════════════════════════════════════
    private bool _stylesBuilt = false;
    private GUIStyle _styleTitle, _styleStageLabel, _styleCloseButton, _styleStatus;
    private GUIStyle _styleSectionHeader, _styleBody, _styleBodyDim;
    private GUIStyle _styleButton, _styleButtonPrimary, _styleTextField;
    private GUIStyle _styleOrderLabel, _styleTooltip;

    private void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        _styleTitle      = MDT_UITheme.MakeLabel(13, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _styleStageLabel = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _styleStatus     = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextAmber);
        _styleOrderLabel = MDT_UITheme.MakeLabel(9,  FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextWhite);

        _styleCloseButton = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextRed, 12);

        _styleSectionHeader = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _styleBody           = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _styleBodyDim        = MDT_UITheme.MakeLabel(9,  FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);

        _styleButton        = MDT_UITheme.MakeButton(MDT_UITheme.BGButton,  MDT_UITheme.TextSecond, 10);
        _styleButtonPrimary = MDT_UITheme.MakeButton(MDT_UITheme.BGPillSel, MDT_UITheme.TextWhite,   11);

        _styleTextField = new GUIStyle(GUI.skin.textField) { fontSize = 10 };
        _styleTextField.normal.textColor = MDT_UITheme.TextPrimary;

        _styleTooltip = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 10,
            alignment = TextAnchor.MiddleLeft,
            padding   = new RectOffset(8, 8, 4, 4),
        };
        _styleTooltip.normal.textColor = MDT_UITheme.TextAmber;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  UI WIDGETS — collapsible cards + stage stepper
    // ═════════════════════════════════════════════════════════════════════
    private readonly Dictionary<string, bool> _foldState = new Dictionary<string, bool>();

    /// <summary>Card-style collapsible section header (rounded, bordered, with
    /// an arrow and a dim one-line summary shown while collapsed). Returns
    /// true when the section is open — callers wrap their body in
    /// `if (Fold(...)) { ... }`. State persists per key for the window's
    /// lifetime, so re-drawing every OnGUI keeps sections where you left them.</summary>
    private bool Fold(string key, string title, string summary = null, bool defaultOpen = false)
    {
        if (!_foldState.TryGetValue(key, out bool open)) { open = defaultOpen; _foldState[key] = open; }

        Rect r = GUILayoutUtility.GetRect(0, 24, GUILayout.ExpandWidth(true));
        bool hover = r.Contains(Event.current.mousePosition);
        MDT_UITheme.DrawRoundedRectBordered(r, 5f,
            hover ? MDT_UITheme.BGRowHover : MDT_UITheme.BGPill,
            open ? MDT_UITheme.BorderAccent : MDT_UITheme.Divider, 1);

        GUI.Label(new Rect(r.x + 8, r.y, 16, r.height), open ? "▾" : "▸", _styleSectionHeader);
        GUI.Label(new Rect(r.x + 22, r.y, r.width - 30, r.height), title, _styleSectionHeader);
        if (!open && !string.IsNullOrEmpty(summary))
        {
            var sumRect = new Rect(r.x + 22, r.y, r.width - 30, r.height);
            var right = new GUIStyle(_styleBodyDim) { alignment = TextAnchor.MiddleRight };
            GUI.Label(sumRect, summary, right);
        }

        if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
        {
            open = !open;
            _foldState[key] = open;
            Event.current.Use();
            Repaint();
        }
        if (open) GUILayout.Space(4);
        return open;
    }

    /// <summary>Horizontal 4-step progress stepper drawn in the header:
    /// Terminals → Outbound → Inbound → Details. Done steps are green,
    /// the current step is highlighted, upcoming steps are dim.</summary>
    private void DrawStageStepper(Rect area)
    {
        string[] names = { "Terminals", "Outbound", "Inbound", "Details" };
        int current;
        switch (_stage)
        {
            case Stage.PickTerminalA:
            case Stage.PickTerminalZ: current = 0; break;
            case Stage.BuildOutbound: current = 1; break;
            case Stage.BuildInbound:  current = 2; break;
            default:                  current = 3; break;
        }
        bool allDone = _stage == Stage.Done;

        float chipW = Mathf.Min(96f, (area.width - 3 * 14f) / 4f);
        float x = area.x;
        for (int i = 0; i < names.Length; i++)
        {
            bool done = allDone || i < current;
            bool cur  = !allDone && i == current;
            Color fill = cur ? MDT_UITheme.BGPillSel : MDT_UITheme.BGPill;
            Color edge = done ? MDT_UITheme.LEDGreen : (cur ? MDT_UITheme.BorderAccent : MDT_UITheme.Divider);
            var chip = new Rect(x, area.y, chipW, area.height);
            MDT_UITheme.DrawRoundedRectBordered(chip, chip.height * 0.5f, fill, edge, 1);

            MDT_UITheme.DrawLED(new Vector2(chip.x + 11, chip.center.y), 3.5f,
                done ? MDT_UITheme.LEDGreen : (cur ? MDT_UITheme.LEDAmber : MDT_UITheme.LEDOff));
            var lbl = MDT_UITheme.MakeLabel(9, cur ? FontStyle.Bold : FontStyle.Normal, TextAnchor.MiddleLeft,
                cur ? MDT_UITheme.TextWhite : (done ? MDT_UITheme.TextPrimary : MDT_UITheme.TextDim));
            GUI.Label(new Rect(chip.x + 20, chip.y, chip.width - 22, chip.height), names[i], lbl);

            x += chipW;
            if (i < names.Length - 1)
            {
                MDT_UITheme.DrawRect(new Rect(x + 2, area.center.y - 0.5f, 10, 1f),
                    done ? MDT_UITheme.LEDGreen : MDT_UITheme.Divider);
                x += 14f;
            }
        }
    }
}