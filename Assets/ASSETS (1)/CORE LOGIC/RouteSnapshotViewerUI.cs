using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE SNAPSHOT VIEWER  —  static per-route "service update sheet"
//
//  Loads a full ORDERED TIMELINE of NetworkSnapshot JSON files (see
//  RouteSnapshot.cs): primarySnapshotJson is the OLDEST anchor, and
//  comparisonSnapshotJsons is every snapshot after it, ALSO ordered oldest
//  to newest.
//
//  HISTORY STEPPING: [ and ] (or the on-screen [ ] buttons) step through
//  the timeline starting with OLD vs OLD — the oldest snapshot compared
//  against itself, so it renders through the same diff/badge path as
//  everything else but trivially shows no changes — then OLD vs NEW1
//  (0 vs 1), then NEW1 vs NEW2 (1 vs 2), then NEW2 vs NEW3 (2 vs 3), and so
//  on. Whichever step is active is what everything on the sheet is
//  computed against:
//
//   · A route in the NEW side of the pair but not the OLD side ->
//     NEW AS OF <new label>.
//   · A route in the OLD side but not the NEW side -> DISCONTINUED,
//     rendered with its last-known (old-side) data, badge reads
//     DISCONTINUED AS OF <new label> (i.e. gone by the time of that
//     snapshot).
//   · A route in both, with differing fields -> CHANGED SINCE <old label>.
//   · A route in both, same routeId but different routeNumber ->
//     RENAMED: X -> Y.
//
//  < / > page through every route relevant to the CURRENT pair (routes in
//  the new side first, in its own order, then old-side-only/discontinued
//  ones appended after) — the route list itself changes as you step
//  through history, since a different pair of snapshots can have a
//  different set of routes worth showing.
//
//  This is intentionally NOT built on MDT_LiveMap's runtime drawing path --
//  that component assumes a live scene (CityManager, live buses, refresh
//  timers). This viewer works purely off static JSON with zero scene
//  dependency, which is the whole point of a snapshot. It reuses
//  MDT_UITheme's low-level primitives (DrawRect/DrawLine/MakeLabel) for a
//  consistent look, not MDT_LiveMap's higher-level drawing methods.
// ═══════════════════════════════════════════════════════════════════════════════
public class RouteSnapshotViewerUI : MonoBehaviour
{
    public static RouteSnapshotViewerUI Instance { get; private set; }

    [Header("Snapshot Files")]
    [Tooltip("The OLDEST snapshot — the anchor everything else is measured against.")]
    public TextAsset primarySnapshotJson;
    [Tooltip("Every snapshot after the oldest, ORDERED OLDEST TO NEWEST — index 0 is the one right after primary, the last one is the newest/current state.")]
    public List<TextAsset> comparisonSnapshotJsons = new();

    [Header("Display")]
    public KeyCode toggleKey => KeyBindings.Current.snapshotViewer;
    public int sheetWidth  = 900;
    public int sheetHeight = 640;

    private static readonly Color DIFF_RED    = new Color(1.00f, 0.32f, 0.32f, 1.00f);
    private static readonly Color NEW_GREEN   = new Color(0.22f, 0.92f, 0.50f, 1.00f);
    private static readonly Color RENAME_BLUE = new Color(0.45f, 0.65f, 1.00f, 1.00f);

    private bool _visible = false;

    /// <summary>[ADD Bug 38 fix] So MainMenu's full-reset-on-open can force this closed.</summary>
    public void Close() => _visible = false;

    // Full ordered timeline: [0] = oldest (primary) ... [last] = newest.
    private readonly List<NetworkSnapshot> _timeline = new();
    private bool _timelineBuilt = false;

    // Which step of the timeline is active. Step 0 is a special first stop
    // meaning "OLDEST vs OLDEST" — the oldest snapshot compared against
    // itself, so it goes through the exact same diff/badge rendering path
    // as every other step (it just trivially shows zero changes, since both
    // sides are identical). The full cycle is:
    //   0 (OLD vs OLD) -> (OLD vs NEW1, i.e. 0 vs 1) -> (NEW1 vs NEW2, i.e.
    //   1 vs 2) -> (NEW2 vs NEW3, i.e. 2 vs 3) -> ...
    // Defaults to the LAST real pair once the timeline loads, so the
    // initial view is "current vs previous," same as before.
    private int _pairIndex = 0;

    private List<string> _displayKeys = new();
    private int _routeIndex = 0;
    private string _currentKey = null; // key of the currently-selected route, tracked so StepHistory can re-find it instead of resetting to 0

    private GUIStyle _giantRoute, _header, _subHeader, _footnote, _diffBanner, _diffLine, _timeBlock;
    private GUIStyle _btnNav, _btnHist, _btnClose;
    private bool _stylesBuilt = false;

    private void Awake()
    {
        // [ADD] Singleton, same pattern as the other MDT managers -- so
        // MainMenu (and anything else) can reach the loaded snapshot data
        // for its own embedded previews without needing a scene reference
        // wired up in the Inspector for every consumer.
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    /// <summary>
    /// Looks up this route (optionally a specific lettered variant, though
    /// the returned RouteDataSnapshot is always the mainline entry -- variant
    /// overrides live on its .variants list) in whatever snapshot is
    /// currently the "new side" of the loaded timeline. Returns null if no
    /// snapshot is loaded yet or the route isn't in it -- callers (e.g.
    /// MainMenu's route cards) should treat that as "no preview available"
    /// rather than an error, since not every live route is guaranteed to
    /// have been captured in an exported snapshot.
    /// </summary>
    public RouteDataSnapshot FindRoute(string routeNumber)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(routeNumber) || NewSide?.routes == null) return null;
        return NewSide.routes.FirstOrDefault(r => r != null && r.routeNumber == routeNumber);
    }

    /// <summary>
    /// Opens the full sheet already focused on the given route -- lets
    /// another screen (MainMenu's "VIEW SERVICE SHEET" button) jump straight
    /// in instead of the player having to press F9 and then page through
    /// _displayKeys by hand to find the route they were just looking at.
    /// No-op (silently does nothing) if the route isn't in the currently
    /// loaded snapshot -- same "not every route has been captured" case
    /// FindRoute already tolerates.
    /// </summary>
    public void ShowRoute(string routeNumber)
    {
        EnsureLoaded();
        int idx = _displayKeys.FindIndex(k => FindInSnapshot(NewSide, k)?.routeNumber == routeNumber);
        if (idx < 0) return;
        _routeIndex = idx;
        _currentKey = _displayKeys[idx];
        _visible = true;
    }

    private void Update()
    {
        if (!MainMenu.BlocksInput && Input.GetKeyDown(toggleKey)) _visible = !_visible; // [FIX Bug 43]
        if (!_visible) return;

        if (Input.GetKeyDown(KeyBindings.Current.snapshotPrevRoute))  StepRoute(-1);
        if (Input.GetKeyDown(KeyBindings.Current.snapshotNextRoute)) StepRoute(1);

        if (Input.GetKeyDown(KeyBindings.Current.snapshotPrevHistory))  StepHistory(-1);
        if (Input.GetKeyDown(KeyBindings.Current.snapshotNextHistory)) StepHistory(1);
    }

    private void EnsureLoaded()
    {
        if (_timelineBuilt) return;
        _timelineBuilt = true;

        if (primarySnapshotJson != null)
        {
            var snap = JsonUtility.FromJson<NetworkSnapshot>(primarySnapshotJson.text);
            if (snap != null) _timeline.Add(snap);
        }

        if (comparisonSnapshotJsons != null)
        {
            foreach (var asset in comparisonSnapshotJsons)
            {
                if (asset == null) continue;
                var snap = JsonUtility.FromJson<NetworkSnapshot>(asset.text);
                if (snap != null) _timeline.Add(snap);
            }
        }

        // Default: last real transition if there's history to diff,
        // otherwise step 0 (the only snapshot, compared against itself).
        _pairIndex = PairCount;
        RebuildDisplayKeys();
        if (_displayKeys.Count > 0) _currentKey = _displayKeys[_routeIndex];
    }

    // Number of "real" transitions in the timeline (oldest-to-newest links).
    // The step cycle has PairCount + 1 stops total: step 0 is the OLD-vs-OLD
    // self-compare, and steps 1..PairCount are the real transitions.
    private int PairCount => Mathf.Max(0, _timeline.Count - 1);

    private NetworkSnapshot OldSide =>
        _timeline.Count == 0 ? null :
        _pairIndex <= 0 ? _timeline[0] : _timeline[Mathf.Clamp(_pairIndex - 1, 0, _timeline.Count - 1)];

    private NetworkSnapshot NewSide =>
        _timeline.Count == 0 ? null :
        _timeline[Mathf.Clamp(_pairIndex, 0, _timeline.Count - 1)];

    private static string KeyOf(RouteDataSnapshot r) =>
        !string.IsNullOrEmpty(r?.routeId) ? r.routeId : r?.routeNumber ?? "";

    private static RouteDataSnapshot FindInSnapshot(NetworkSnapshot snap, string key) =>
        snap?.routes.FirstOrDefault(r => KeyOf(r) == key);

    /// <summary>Steps through the full history cycle: 0 (OLD vs OLD,
    /// self-compare) -> 1 (OLD vs NEW1) -> 2 (NEW1 vs NEW2) -> ... ->
    /// PairCount (newest transition) -> wraps back to 0. If only one
    /// snapshot is loaded, PairCount is 0 and this is a no-op — stays
    /// at step 0.</summary>
    private void StepHistory(int dir)
    {
        if (_timeline.Count == 0) return;
        if (PairCount == 0) { _pairIndex = 0; return; }

        // Remember which route was selected (by key, not index) so it
        // survives the step even though the display list can change shape.
        if (_displayKeys.Count > 0 && _routeIndex < _displayKeys.Count)
            _currentKey = _displayKeys[_routeIndex];

        int range = PairCount + 1; // steps 0..PairCount inclusive
        _pairIndex = (_pairIndex + dir + range) % range;
        RebuildDisplayKeys();

        // Re-find the same route in the new list; only fall back to 0 if
        // it's genuinely gone from both sides of the new step.
        int found = _currentKey != null ? _displayKeys.IndexOf(_currentKey) : -1;
        _routeIndex = found >= 0 ? found : 0;
    }

    private void RebuildDisplayKeys()
    {
        _displayKeys = new List<string>();
        if (NewSide == null && OldSide == null) return;

        if (NewSide != null)
            foreach (var r in NewSide.routes)
            {
                string key = KeyOf(r);
                if (!_displayKeys.Contains(key)) _displayKeys.Add(key);
            }

        // Old-side-only routes (discontinued as of this specific
        // transition) appended after, so they're still reachable by paging.
        if (OldSide != null)
        {
            var seen = new HashSet<string>(_displayKeys);
            foreach (var r in OldSide.routes)
            {
                string key = KeyOf(r);
                if (seen.Add(key)) _displayKeys.Add(key);
            }
        }
    }

    private void StepRoute(int dir)
    {
        if (_displayKeys.Count == 0) return;
        _routeIndex = (_routeIndex + dir + _displayKeys.Count) % _displayKeys.Count;
        _currentKey = _displayKeys[_routeIndex];
    }

    private void OnGUI()
    {
        if (!_visible) return;
        BuildStyles();
        EnsureLoaded();

        if (_timeline.Count == 0 || _displayKeys.Count == 0)
        {
            GUI.Label(new Rect(20, 20, 500, 30), "No snapshot loaded -- assign primarySnapshotJson.", _footnote);
            return;
        }

        string key = _displayKeys[_routeIndex];
        var newData = FindInSnapshot(NewSide, key);
        var oldData = FindInSnapshot(OldSide, key);

        bool isNew          = newData != null && oldData == null && OldSide != null;
        bool isDiscontinued = newData == null && oldData != null;
        var route           = newData ?? oldData; // discontinued routes render their last-known (old-side) data
        if (route == null) return;

        float x = (Screen.width  - sheetWidth)  * 0.5f;
        float y = (Screen.height - sheetHeight) * 0.5f;
        var sheet = new Rect(x, y, sheetWidth, sheetHeight);

        // [RESTYLE] Card-based MDT dark panel with soft shadow/bevel chrome
        // (MDT_UITheme.DrawPanel) instead of a flat solid-yellow rect -- same
        // panel treatment as the rest of the app's UI (BusSelectMenu, the
        // dashboard HUD), so this reads as one consistent product instead of
        // a separate "paper printout" skin bolted on the side.
        MDT_UITheme.DrawPanel(sheet);
        DrawHeader(sheet, route, oldData, isNew, isDiscontinued);
        DrawMap(sheet, route);
        DrawFootnotes(sheet, route);
        DrawNavArrows(sheet);

        if (!isNew && !isDiscontinued && oldData != null)
            DrawDiffPanel(sheet, route, oldData);
    }

    // ── Header: giant route number + name, destinations, variants, badges ──────
    private void DrawHeader(Rect sheet, RouteDataSnapshot route, RouteDataSnapshot previous,
                             bool isNew, bool isDiscontinued)
    {
        // [RESTYLE] Giant number now sits inside its own rounded chip, tinted
        // by the route's own captured color (routeColorR/G/B) when set,
        // instead of bare black text floating on the yellow field -- gives
        // every sheet an at-a-glance route-color identity like the live map
        // route badges already have.
        Color routeColor = new Color(route.routeColorR, route.routeColorG, route.routeColorB,
                                      route.routeColorA > 0f ? route.routeColorA : 1f);
        if (routeColor.maxColorComponent < 0.05f) routeColor = MDT_UITheme.TextCyan; // uncaptured/black default -> fall back to theme accent, not invisible-on-dark

        var numChip = new Rect(sheet.x + 16, sheet.y + 12, 200, 88);
        MDT_UITheme.DrawRoundedRectBordered(numChip, 14f, Color.Lerp(routeColor, Color.black, 0.55f), routeColor, 2);
        GUI.Label(numChip, route.routeNumber, _giantRoute);

        if (!string.IsNullOrEmpty(route.routeName))
            GUI.Label(new Rect(numChip.x + 4, numChip.yMax + 4, numChip.width, 20), route.routeName, _subHeader);

        // Destination header, right of the number chip.
        float headerX = sheet.x + 230;
        GUI.Label(new Rect(headerX, sheet.y + 16, sheet.width - 250, 26),
            $"↗ OUTBOUND  ·  {UpperOrDash(route.destinationNameOutbound)}", _header);
        GUI.Label(new Rect(headerX, sheet.y + 42, sheet.width - 250, 26),
            $"↙ INBOUND  ·  {UpperOrDash(route.destinationNameInbound)}", _header);

        if (route.variants != null && route.variants.Count > 0)
        {
            string variantList = string.Join("   ", route.variants.Select(v =>
                $"[{v.variantLetter}]" +
                (!string.IsNullOrEmpty(v.destinationNameOutboundOverride) ? $" → {v.destinationNameOutboundOverride}" : "")));
            GUI.Label(new Rect(headerX, sheet.y + 68, sheet.width - 250, 22),
                $"VARIANTS  {variantList}", _subHeader);
        }

        // Badges stack top-right as rounded pills with a leading icon glyph
        // instead of flat solid-color rectangles.
// Shift badge start position down from sheet.y + 10 to sheet.y + 42
float by = sheet.y + 42;
const float bw = 220, bh = 22, bgap = 4;

        if (previous != null && !isNew && !isDiscontinued && DiffFields(route, previous).Count > 0)
        {
            DrawBadge(sheet, by, bw, bh, $"● CHANGED SINCE {OldSide?.label}", DIFF_RED);
            by += bh + bgap;
        }

        if (previous != null && !isNew && !isDiscontinued && !string.IsNullOrEmpty(route.routeId) &&
            previous.routeId == route.routeId && previous.routeNumber != route.routeNumber)
        {
            DrawBadge(sheet, by, bw, bh, $"↻ RENAMED  {previous.routeNumber} → {route.routeNumber}", RENAME_BLUE);
            by += bh + bgap;
        }

        if (isNew)
        {
            DrawBadge(sheet, by, bw, bh, $"✦ NEW AS OF {NewSide?.label}", NEW_GREEN);
            by += bh + bgap;
        }

        if (isDiscontinued)
        {
            DrawBadge(sheet, by, bw, bh, $"✕ DISCONTINUED  ·  GONE BY {NewSide?.label}", DIFF_RED);
        }

        MDT_UITheme.DrawDivider(sheet.x + 12, sheet.y + 100, sheet.width - 24);
    }

    private void DrawBadge(Rect sheet, float y, float w, float h, string label, Color color)
    {
        var r = new Rect(sheet.x + sheet.width - w - 12, y, w, h);
        Color bg = Color.Lerp(color, Color.black, 0.55f);
        MDT_UITheme.DrawRoundedRectBordered(r, h * 0.5f, bg, color, 1);
        GUI.Label(r, label, MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleCenter, color));
    }

    private static string UpperOrDash(string s) => string.IsNullOrEmpty(s) ? "—" : s.ToUpperInvariant();

    // ── Static map: fitted to this route's own node bounds ─────────────────────
    private void DrawMap(Rect sheet, RouteDataSnapshot route)
    {
        var mapRect = new Rect(sheet.x + 16, sheet.y + 106, sheet.width - 32, sheet.height - 220);
        DrawRouteMap(mapRect, route, MDT_UITheme.BGMid, MDT_UITheme.TextPrimary, MDT_UITheme.TextCyan, 3f);
    }

    /// <summary>
    /// Reusable fit-to-bounds route line-map renderer -- draws both
    /// directions' polylines plus stop dots, scaled/centered to fill
    /// `rect` regardless of the route's real-world size. Fully static and
    /// self-contained (no instance styles, no viewer state) so callers like
    /// MainMenu's route-card mini previews can use the exact same rendering
    /// as the full service sheet, just at thumbnail size with thinner lines.
    /// Safe to call every OnGUI -- everything here is MDT_UITheme.DrawRect/
    /// DrawLine (the shared 1x1 white texture), never a baked GUIStyle
    /// background, so there's nothing for the texture-cache eviction fix in
    /// MDT_UITheme to even interact with.
    /// </summary>
    public static void DrawRouteMap(Rect rect, RouteDataSnapshot route, Color bgTint, Color lineColor, Color stopColor, float lineWidth = 3f)
    {
        MDT_UITheme.DrawRoundedRect(rect, 10f, bgTint);

        if (route == null)
        {
            GUI.Label(rect, "(no snapshot)", MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleCenter, MDT_UITheme.TextDim));
            return;
        }

        var allPoints = new List<Vector3>();
        foreach (var n in route.outboundNodes) allPoints.Add(n.ToVector3());
        foreach (var n in route.inboundNodes)  allPoints.Add(n.ToVector3());
        if (allPoints.Count < 2)
        {
            GUI.Label(rect, "(no geometry)", MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleCenter, MDT_UITheme.TextDim));
            return;
        }

        // Fit-to-bounds: unlike MDT_LiveMap's WorldToScreen (which uses a
        // fixed viewRadius for a live, pannable/zoomable map), this is
        // static and per-route -- computes its own bounding box from THIS
        // route's nodes each call and scales/centers to fill `rect` with a
        // margin, so every route fills the same box regardless of how
        // physically large or small it actually is.
        float minX = allPoints.Min(p => p.x), maxX = allPoints.Max(p => p.x);
        float minZ = allPoints.Min(p => p.z), maxZ = allPoints.Max(p => p.z);
        float spanX = Mathf.Max(maxX - minX, 1f);
        float spanZ = Mathf.Max(maxZ - minZ, 1f);

        float margin = Mathf.Min(rect.width, rect.height) * 0.10f;
        float scale = Mathf.Min((rect.width - margin * 2) / spanX, (rect.height - margin * 2) / spanZ);
        // Center the (possibly non-square) scaled route within rect rather
        // than pinning to the top-left corner, so small/thin routes don't
        // look off-balance in a square thumbnail.
        float usedW = spanX * scale, usedH = spanZ * scale;
        float padX = (rect.width  - margin * 2 - usedW) * 0.5f;
        float padY = (rect.height - margin * 2 - usedH) * 0.5f;

        Vector2 ToScreen(Vector3 world)
        {
            float dx = (world.x - minX) * scale;
            float dz = (maxZ - world.z) * scale; // flip Z so "up" on screen = +Z, matches live map convention
            return new Vector2(rect.x + margin + padX + dx, rect.y + margin + padY + dz);
        }

        DrawPolyline(route.outboundNodes, ToScreen, lineColor, lineWidth);
        DrawPolyline(route.inboundNodes, ToScreen, lineColor, lineWidth);
        DrawStops(route.outboundStops, ToScreen, stopColor, lineWidth);
        DrawStops(route.inboundStops, ToScreen, stopColor, lineWidth);
    }

    private static void DrawPolyline(List<RouteNodeSnapshot> nodes, System.Func<Vector3, Vector2> toScreen, Color color, float width)
    {
        for (int i = 0; i < nodes.Count - 1; i++)
        {
            Vector2 a = toScreen(nodes[i].ToVector3());
            Vector2 b = toScreen(nodes[i + 1].ToVector3());
            MDT_UITheme.DrawLine(a, b, color, width);
        }
    }

    private static void DrawStops(List<RouteStopSnapshot> stops, System.Func<Vector3, Vector2> toScreen, Color color, float lineWidth)
    {
        // Stop world positions are only populated if the exporter had a
        // live CityManager to resolve against (see RouteStopSnapshot.From)
        // -- silently skip dots for stops that didn't resolve rather than
        // drawing them all stacked at the origin.
        float r = Mathf.Clamp(lineWidth * 1.4f, 2f, 5f);
        foreach (var s in stops)
        {
            if (s.worldX == 0f && s.worldZ == 0f) continue;
            Vector2 p = toScreen(new Vector3(s.worldX, s.worldY, s.worldZ));
            float rr = s.isTimepoint ? r * 1.5f : r;
            MDT_UITheme.DrawRoundedRect(new Rect(p.x - rr, p.y - rr, rr * 2, rr * 2), rr, color);
        }
    }

    // ── Footnotes: headway, trip time, time blocks, qualifiers ──────────────────
    private void DrawFootnotes(Rect sheet, RouteDataSnapshot route)
    {
        float fy = sheet.y + sheet.height - 104;
        var card = new Rect(sheet.x + 16, fy - 6, sheet.width - 32, 96);
        MDT_UITheme.DrawRoundedRect(card, 10f, MDT_UITheme.BGMid);

        GUI.Label(new Rect(sheet.x + 28, fy, sheet.width - 56, 20),
            $"⏱ Headway A/Z: {route.headwayFromAMinutes:0}/{route.headwayFromZMinutes:0} min   |   " +
            $"Trip: {route.oneWayTripMinutes:0} min   |   " +
            $"Hours: {MinutesToClock(route.operatingStartMinutes)}–{MinutesToClock(route.operatingEndMinutes)}",
            _footnote);

        // Time blocks -- schedule windows rendered as "0-4, 5-10" style
        // ranges (hour blocks), one line, so the whole day's structure
        // reads at a glance without needing the full headway table. This is
        // the same "windows or flat headway" fallback BusRouteData.
        // GetActiveSchedule uses live -- here it's just descriptive text
        // over the whole day rather than a single "right now" value.
        string blocks = (route.scheduleWindows == null || route.scheduleWindows.Count == 0)
            ? "flat headway, all service hours"
            : string.Join("   ", route.scheduleWindows.Select(w =>
                $"{HourOf(w.windowStartMinutes)}-{HourOf(w.windowEndMinutes)}h ({w.label} @{w.headwayFromAMinutes:0}/{w.headwayFromZMinutes:0})"));

        GUI.Label(new Rect(sheet.x + 28, fy + 22, sheet.width - 56, 40), $"🕐 {blocks}", _timeBlock);

        // A short turn is a variant flagged isShortTurn; it carries the '~' symbol ("116~").
        var shortTurnVariants = route.variants != null ? route.variants.Where(v => v.isShortTurn).ToList() : null;
        if (shortTurnVariants != null && shortTurnVariants.Count > 0)
        {
            string st = string.Join("   ", shortTurnVariants.Select(v =>
                $"{route.routeNumber}{BusRouteData.ShortTurnSymbol} turns back at {(string.IsNullOrEmpty(v.turnbackStopCode) ? "?" : v.turnbackStopCode)}"));
            GUI.Label(new Rect(sheet.x + 28, fy + 60, sheet.width - 56, 20), $"↪ Short Turn: {st}", _footnote);
        }

        // Qualifiers weren't shown anywhere before — small line, only
        // appears when actually set, so routes without one don't waste
        // space with an empty line.
        if (!string.IsNullOrEmpty(route.routeQualifierOutbound) || !string.IsNullOrEmpty(route.routeQualifierInbound))
            GUI.Label(new Rect(sheet.x + 28, fy + 80, sheet.width - 56, 18),
                $"Qualifiers — Outbound: {UpperOrDash(route.routeQualifierOutbound)}   Inbound: {UpperOrDash(route.routeQualifierInbound)}",
                _subHeader);
    }

    private static int HourOf(float minutesOfDay) => Mathf.FloorToInt(minutesOfDay / 60f);

    private static string MinutesToClock(float minutesOfDay)
    {
        int h = Mathf.FloorToInt(minutesOfDay / 60f) % 24;
        int m = Mathf.FloorToInt(minutesOfDay % 60f);
        return $"{h:00}:{m:00}";
    }

    // ── Nav arrows ────────────────────────────────────────────────────────────
    private void DrawNavArrows(Rect sheet)
    {
        // On-screen Close button (Top-Right of Sheet)
var closeBtn = new Rect(sheet.x + sheet.width - 36, sheet.y + 10, 26, 26);
if (GUI.Button(closeBtn, "✕", _btnClose)) _visible = false;
        var leftBtn  = new Rect(sheet.x - 50, sheet.y + sheet.height * 0.5f - 20, 40, 40);
        var rightBtn = new Rect(sheet.x + sheet.width + 10, sheet.y + sheet.height * 0.5f - 20, 40, 40);

        if (GUI.Button(leftBtn, "◀", _btnNav)) StepRoute(-1);
        if (GUI.Button(rightBtn, "▶", _btnNav)) StepRoute(1);

        if (PairCount > 0)
        {
            var histLeftBtn  = new Rect(sheet.x - 50, sheet.y + 10, 40, 30);
            var histRightBtn = new Rect(sheet.x + sheet.width + 10, sheet.y + 10, 40, 30);
            if (GUI.Button(histLeftBtn, "[", _btnHist)) StepHistory(-1);
            if (GUI.Button(histRightBtn, "]", _btnHist)) StepHistory(1);
        }

        string historyStatus;
        if (PairCount == 0)
            historyStatus = $"single snapshot [{NewSide?.label}] — load more in comparisonSnapshotJsons for history";
        else if (_pairIndex == 0)
            historyStatus = $"ORIGINAL: [{NewSide?.label}] vs itself  (no changes yet — press ] for first diff)";
        else
            historyStatus = $"comparing [{OldSide?.label}] → [{NewSide?.label}]   (step {_pairIndex}/{PairCount}, [ ] to step)";

        GUI.Label(new Rect(sheet.x, sheet.y + sheet.height + 6, sheet.width, 20),
            $"Route {_routeIndex + 1} / {_displayKeys.Count}   —   {historyStatus}",
            _footnote);
    }

    /// <summary>capturedAtUtc is stored as a round-trip ISO 8601 string
    /// (DateTime "o" format) so it survives JSON exactly -- this just
    /// renders it readably instead of the raw ISO string.</summary>
    private static string FormatDate(string isoUtc)
    {
        if (string.IsNullOrEmpty(isoUtc)) return "(no date)";
        if (System.DateTime.TryParse(isoUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToLocalTime().ToString("MMM d, yyyy  h:mm tt");
        return isoUtc; // fallback: show raw string rather than hide it
    }

    // ── Diff panel ────────────────────────────────────────────────────────────
    private void DrawDiffPanel(Rect sheet, RouteDataSnapshot route, RouteDataSnapshot baseline)
    {
        var changes = DiffFields(route, baseline);
        if (changes.Count == 0) return;

        float py = sheet.y + 108;
        var panel = new Rect(sheet.x + sheet.width - 300, py, 284, Mathf.Min(200, 24 + changes.Count * 18));
        MDT_UITheme.DrawRoundedRectBordered(panel, 10f, MDT_UITheme.BGTooltip, DIFF_RED, 1);
        GUI.Label(new Rect(panel.x + 8, panel.y + 2, panel.width - 16, 16), "CHANGES", _diffBanner);

        for (int i = 0; i < changes.Count; i++)
            GUI.Label(new Rect(panel.x + 8, panel.y + 20 + i * 18, panel.width - 16, 18), changes[i], _diffLine);
    }

    /// <summary>Compares the fields that actually matter for a service-update
    /// readout -- destinations, terminals, headway, trip time, stop counts,
    /// node counts -- and returns one human-readable line per difference.
    /// Deliberately field-level rather than a raw JSON diff, since "stop
    /// count changed 42→45" is what a rider/planner needs, not a JSON patch.</summary>
    private static List<string> DiffFields(RouteDataSnapshot a, RouteDataSnapshot b)
    {
        var changes = new List<string>();
        void Check(string name, object av, object bv)
        {
            if (!Equals(av, bv)) changes.Add($"{name}: {bv} → {av}");
        }

        Check("Dest Outbound", a.destinationNameOutbound, b.destinationNameOutbound);
        Check("Dest Inbound", a.destinationNameInbound, b.destinationNameInbound);
        Check("Terminal A", a.terminalACode, b.terminalACode);
        Check("Terminal Z", a.terminalZCode, b.terminalZCode);
        Check("Headway A", a.headwayFromAMinutes, b.headwayFromAMinutes);
        Check("Headway Z", a.headwayFromZMinutes, b.headwayFromZMinutes);
        Check("One-Way Trip", a.oneWayTripMinutes, b.oneWayTripMinutes);
        Check("Outbound Stops", a.outboundStops?.Count ?? 0, b.outboundStops?.Count ?? 0);
        Check("Inbound Stops", a.inboundStops?.Count ?? 0, b.inboundStops?.Count ?? 0);
        Check("Outbound Nodes", a.outboundNodes?.Count ?? 0, b.outboundNodes?.Count ?? 0);
        Check("Inbound Nodes", a.inboundNodes?.Count ?? 0, b.inboundNodes?.Count ?? 0);
        Check("Variant Count", a.variants?.Count ?? 0, b.variants?.Count ?? 0);

        return changes;
    }

    // ── Styles ────────────────────────────────────────────────────────────────
    private void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        _giantRoute = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        _giantRoute.normal.textColor = MDT_UITheme.TextPrimary;

        _header    = MDT_UITheme.MakeLabel(16, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _subHeader = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextSecond);
        _footnote  = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _timeBlock = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.UpperLeft,  MDT_UITheme.TextDim);
        _diffLine  = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.MiddleLeft, DIFF_RED);
        _diffBanner = MDT_UITheme.MakeLabel(11, FontStyle.Bold,  TextAnchor.MiddleLeft, DIFF_RED);

        // [RESTYLE] Nav buttons now go through MDT_UITheme.MakeButton, same
        // as every other persistent button in the app, instead of bare
        // GUI.skin.button defaults. Built once here (guarded by
        // _stylesBuilt), so these are the small, fixed, built-once case
        // SetBgRounded's pin mechanism is designed for -- safe from the
        // eviction "turns to boxes" bug without any RAM growth concern.
        _btnNav  = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextPrimary, 18, FontStyle.Bold);
        _btnHist = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 14, FontStyle.Bold);
        _btnClose = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, DIFF_RED, 14, FontStyle.Bold);
    }
}