using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  PlayerDestinationBoard
//
//  Drives a BusDestinationBoard on the PLAYER's bus. BusDestinationBoard on
//  its own only auto-polls a sibling NPCBusController -- fine for AI buses,
//  useless once the player possesses one, since PlayerHandoff drives that
//  bus instead. This component:
//
//   1. Auto-syncs the player's board from PlayerHandoff's live shift state
//      (route / destination / qualifier / out-of-service) whenever the
//      player is on a real assigned slot -- same content BusDestinationBoard
//      would show on an NPC, just sourced from PlayerHandoff instead.
//   2. Adds a manual override menu (default key: F6) so the player can pick
//      a route number, pick/type a destination, set a qualifier, or force
//      NOT IN SERVICE / blank -- for cosmetic runs, screenshots, deadheading
//      to a spot with no assigned slot, etc. Manual picks stick until the
//      player clears the override or picks "Auto (Follow Shift)".
//   3. Extends the destination pool with a handful of always-available
//      "flavor" destinations that aren't tied to any real route, plus a
//      block of FUTURE placeholder route numbers. Placeholder numbers are
//      just labels here -- the instant a real BusRouteData asset with that
//      routeNumber exists in the project, GetKnownRouteNumbers() below
//      finds it as a real route and the placeholder version is skipped
//      automatically. No manual list-pruning needed when 66/110/299/225
//      go from "planned" to "actually built".
//
//  FIX: this used to depend on PlayerShiftDirector/BusSchedulerPlayerService
//  (a from-scratch shift-system rebuild that was reverted). Rewired onto the
//  real, current owner of shift state — PlayerHandoff — plus BusScheduler
//  for the actual TimetableSlot (PlayerHandoff itself only exposes route/
//  direction strings, not the slot object, so the slot is looked up the
//  same way ShiftRunner/HandOff's own HasPendingChainLeg already does:
//  BusScheduler.TryGetAssignedSlot(PlayerBusID, ...)).
// ═══════════════════════════════════════════════════════════════════════════════
public class PlayerDestinationBoard : MonoBehaviour
{
    [Header("Wiring")]
    [Tooltip("Board on the player's bus. Auto-found in children if left empty.")]
    public BusDestinationBoard playerBoard;
    public bool autoFindPlayerBoard = true;

    [Header("Menu")]
    public KeyCode menuToggleKey => KeyBindings.Current.destinationSign;

    [Header("Extra Flavor Destinations (not tied to any route)")]
    public List<string> extraDestinations = new List<string>
    {
        "POINT NEMO", "TEST", "CENTRAL BLOCK", "PAINT DISTRICT", "FARMAWAY VALLEY"
    };

    [Header("Future Route Numbers (placeholders)")]
    [Tooltip("Shown in the picker as selectable route numbers even before a real BusRouteData asset for them exists. The moment a real route with that number is created, it silently takes over -- these are only ever a fallback.")]
    public List<string> futureRouteNumbers = new List<string> { "66", "110", "299", "225" };

    // ── State ────────────────────────────────────────────────────────────────
    private bool _menuOpen;
    private bool _manualOverride;

    private string _manualRoute      = "";
    private string _manualDest       = "";
    private string _manualQualifier  = "";

    private Vector2 _routeScroll;
    private Vector2 _destScroll;
    private string  _destSearch = "";

    private string _lastSyncedRoute, _lastSyncedDest, _lastSyncedQual;
    private bool   _lastSyncedOOS;

    // ═════════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════════
    private void Awake()
    {
        if (autoFindPlayerBoard && playerBoard == null)
            playerBoard = GetComponentInChildren<BusDestinationBoard>();

        if (playerBoard != null)
            playerBoard.autoReadFromController = false; // we drive it, not NPCBusController polling
    }

    private void OnEnable()
    {
        if (PlayerHandoff.Instance != null)
            PlayerHandoff.Instance.OnShiftStateChanged += HandleShiftStateChanged;
    }

    private void OnDisable()
    {
        if (PlayerHandoff.Instance != null)
            PlayerHandoff.Instance.OnShiftStateChanged -= HandleShiftStateChanged;
    }

    private void HandleShiftStateChanged(PlayerHandoff.PlayerShiftState _) => SyncFromShift();

    private void Update()
    {
        if (Input.GetKeyDown(menuToggleKey))
            _menuOpen = !_menuOpen;

        if (!_manualOverride)
            SyncFromShift();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  AUTO SYNC FROM PLAYERHANDOFF
    // ═════════════════════════════════════════════════════════════════════════
    private void SyncFromShift()
    {
        if (playerBoard == null) return;

        var ph = PlayerHandoff.Instance;
        // "On some real duty" mirrors the same off-duty test used elsewhere
        // in the project (ShiftState == OffDuty || Relieved means nothing
        // active to show).
        bool hasActiveLeg = ph != null
            && ph.ShiftState != PlayerHandoff.PlayerShiftState.OffDuty
            && ph.ShiftState != PlayerHandoff.PlayerShiftState.Relieved;

        TimetableSlot leg = null;
        if (hasActiveLeg && BusScheduler.Instance != null)
            BusScheduler.Instance.TryGetAssignedSlot(ph.PlayerBusID, out leg);

        if (leg == null)
        {
            if (_lastSyncedOOS) return;
            _lastSyncedOOS = true;
            playerBoard.SetOutOfService();
            return;
        }

        BusRouteData route = BusScheduler.Instance != null
            ? BusScheduler.Instance.GetRouteData(leg.routeNumber)
            : null;

        if (route == null)
        {
            if (_lastSyncedOOS) return;
            _lastSyncedOOS = true;
            playerBoard.SetOutOfService();
            return;
        }

        bool outbound = leg.isOutbound;
        string dest      = route.GetDestinationName(outbound, route.GetVariant(leg.variantLetter));
        string qualifier = outbound ? route.routeQualifierOutbound  : route.routeQualifierInbound;
        string routeNum  = route.routeNumber ?? leg.routeNumber;

        if (routeNum == _lastSyncedRoute && dest == _lastSyncedDest && qualifier == _lastSyncedQual && !_lastSyncedOOS)
            return;

        _lastSyncedRoute = routeNum;
        _lastSyncedDest  = dest;
        _lastSyncedQual  = qualifier;
        _lastSyncedOOS   = false;

        playerBoard.SetRoute(routeNum, dest, qualifier);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ROUTE / DESTINATION POOLS
    // ═════════════════════════════════════════════════════════════════════════

    // All real BusRouteData assets currently loaded (in scene, in Resources,
    // or anywhere else Unity has them resident). This is how a placeholder
    // future route number gets quietly overridden the moment a real one
    // with the same number exists -- it just shows up in this set.
    private List<BusRouteData> GetAllLoadedRoutes()
    {
        return Resources.FindObjectsOfTypeAll<BusRouteData>()
            .Where(r => r != null && !string.IsNullOrEmpty(r.routeNumber))
            .ToList();
    }

    private List<string> GetKnownRouteNumbers()
    {
        var real = GetAllLoadedRoutes().Select(r => r.routeNumber).ToHashSet();

        var result = new List<string>(real);
        foreach (var future in futureRouteNumbers)
            if (!real.Contains(future))
                result.Add(future); // still just a placeholder -- no real route claims this number yet

        result.Sort(System.StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private List<string> GetKnownDestinations()
    {
        var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var r in GetAllLoadedRoutes())
        {
            if (!string.IsNullOrEmpty(r.destinationNameOutbound)) set.Add(r.destinationNameOutbound);
            if (!string.IsNullOrEmpty(r.destinationNameInbound))  set.Add(r.destinationNameInbound);
        }
        foreach (var extra in extraDestinations)
            if (!string.IsNullOrEmpty(extra)) set.Add(extra);

        var list = set.ToList();
        list.Sort(System.StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // Is this route number a real, resolvable BusRouteData, or still just
    // a future placeholder with no actual asset behind it?
    private BusRouteData ResolveRealRoute(string routeNumber)
    {
        return GetAllLoadedRoutes().Find(r =>
            string.Equals(r.routeNumber, routeNumber, System.StringComparison.OrdinalIgnoreCase));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  MANUAL CONTROL
    // ═════════════════════════════════════════════════════════════════════════
    private void ApplyManual()
    {
        if (playerBoard == null) return;
        _manualOverride = true;
        playerBoard.SetRoute(_manualRoute, _manualDest, _manualQualifier);
    }

    private void ApplyOutOfService()
    {
        if (playerBoard == null) return;
        _manualOverride = true;
        _manualRoute = "";
        _manualDest  = "NOT IN SERVICE";
        playerBoard.SetOutOfService();
    }

    private void ApplyBlank()
    {
        if (playerBoard == null) return;
        _manualOverride = true;
        _manualRoute = _manualDest = _manualQualifier = "";
        playerBoard.SetBlank();
    }

    private void ApplyRandom()
    {
        var routes = GetKnownRouteNumbers();
        var dests  = GetKnownDestinations();
        if (routes.Count == 0 || dests.Count == 0) return;

        _manualRoute = routes[Random.Range(0, routes.Count)];

        // If the random route happens to be a real one, prefer one of ITS
        // actual destinations/qualifiers so the result is plausible instead
        // of a real route number paired with an unrelated place name.
        var real = ResolveRealRoute(_manualRoute);
        if (real != null)
        {
            bool outbound = Random.value < 0.5f;
            _manualDest      = outbound ? real.destinationNameOutbound : real.destinationNameInbound;
            _manualQualifier = outbound ? real.routeQualifierOutbound  : real.routeQualifierInbound;
            if (string.IsNullOrEmpty(_manualDest))
                _manualDest = dests[Random.Range(0, dests.Count)];
        }
        else
        {
            _manualDest      = dests[Random.Range(0, dests.Count)];
            _manualQualifier = "";
        }

        ApplyManual();
    }

    private void ReturnToAuto()
    {
        _manualOverride  = false;
        _lastSyncedRoute = _lastSyncedDest = _lastSyncedQual = null;
        _lastSyncedOOS   = false;
        SyncFromShift();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  MENU
    // ═════════════════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        if (!_menuOpen) return;

        const float w = 340f, h = 480f;
        var rect = new Rect(20f, 20f, w, h);
        GUILayout.BeginArea(rect, GUI.skin.box);

        GUILayout.Label($"<b>Destination Board</b>  [{menuToggleKey}]", GuiRichLabel());
        GUILayout.Label(_manualOverride ? "Mode: MANUAL" : "Mode: AUTO (following shift)");

        GUILayout.Space(6f);
        if (GUILayout.Button("Auto (Follow Shift)")) ReturnToAuto();
        if (GUILayout.Button("NOT IN SERVICE"))       ApplyOutOfService();
        if (GUILayout.Button("Blank"))                ApplyBlank();
        if (GUILayout.Button("🎲 Random Route + Destination")) ApplyRandom();

        GUILayout.Space(8f);
        GUILayout.Label("Route Number");
        _routeScroll = GUILayout.BeginScrollView(_routeScroll, GUILayout.Height(90f));
        GUILayout.BeginHorizontal();
        int col = 0;
        foreach (var num in GetKnownRouteNumbers())
        {
            bool isReal = ResolveRealRoute(num) != null;
            if (GUILayout.Button(isReal ? num : $"{num}*", GUILayout.Width(50f)))
            {
                _manualRoute = num;
                var real = ResolveRealRoute(num);
                if (real != null)
                {
                    // pull that route's real outbound dest/qualifier as a default starting point
                    _manualDest      = real.destinationNameOutbound;
                    _manualQualifier = real.routeQualifierOutbound;
                }
                ApplyManual();
            }
            col++;
            if (col % 5 == 0) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); }
        }
        GUILayout.EndHorizontal();
        GUILayout.EndScrollView();
        GUILayout.Label("* = future route, no real line built yet");

        GUILayout.Space(8f);
        GUILayout.Label("Destination");
        _destSearch = GUILayout.TextField(_destSearch);
        _destScroll = GUILayout.BeginScrollView(_destScroll, GUILayout.Height(120f));
        foreach (var dest in GetKnownDestinations())
        {
            if (!string.IsNullOrEmpty(_destSearch) &&
                dest.IndexOf(_destSearch, System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (GUILayout.Button(dest))
            {
                _manualDest = dest;
                ApplyManual();
            }
        }
        GUILayout.EndScrollView();

        GUILayout.Space(8f);
        GUILayout.Label("Qualifier (VIA ..., LIMITED, MAX, etc.)");
        _manualQualifier = GUILayout.TextField(_manualQualifier);

        GUILayout.Space(4f);
        GUILayout.Label("Custom Route # / Destination");
        _manualRoute = GUILayout.TextField(_manualRoute);
        _manualDest  = GUILayout.TextField(_manualDest);
        if (GUILayout.Button("Apply Custom")) ApplyManual();

        GUILayout.EndArea();
    }

    private GUIStyle GuiRichLabel()
    {
        var style = new GUIStyle(GUI.skin.label) { richText = true };
        return style;
    }
}