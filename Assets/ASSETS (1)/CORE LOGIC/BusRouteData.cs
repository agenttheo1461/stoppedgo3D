using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public struct RouteStopBinding
{
    [Tooltip("Must match BusStopData.stopCode")]
    public string stopCode;
    
    [Tooltip("How many minutes into the trip the bus should reach this stop.")]
    public float minutesFromStart;

    [Tooltip("Timepoints are the small subset of stops that actually get a published time and that on-time performance/schedule-holding is measured against — real schedules don't publish or hold to every single stop, only these.")]
    public bool isTimepoint;
}
public enum ArticulatedRequirement
{
    Prohibited,  // Completely forbids articulated buses
    Allowed,     // Indifferent (can use standard or articulated)
    Preferred,   // Prefers articulated, but falls back to standard if none are available
    Mandatory    // Must be articulated
}
// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE NODE  —  a single waypoint on a bus route path
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class RouteNode
{
    public Vector3 position;
    [Tooltip("Part of a Bezier curve segment. Consecutive true nodes form one curve.")]
    public bool isCurve;
}
[System.Serializable]
public class ScheduleWindow
{
    public string label = "AM Peak";
    [Tooltip("Raw minutes-of-day, e.g. 4:00 = 240, 6:30 = 390")]
    public float windowStartMinutes = 240f;
    public float windowEndMinutes   = 390f;
    public float headwayFromAMinutes = 30f;
    public float headwayFromZMinutes = 30f;

    [Tooltip("Shifts this window's departure grid by this many minutes, so a variant/short-turn sharing the same headway as the mainline can genuinely ALTERNATE with it instead of departing at the exact same clock minutes. Without this, departures always snap to absolute-time multiples of the headway (e.g. every :00/:30) regardless of windowStartMinutes -- windowStartMinutes only trims which of those multiples fall inside the window, it does NOT phase-shift them. Set this to headway/2 (e.g. 15 for a 30min headway) to interleave evenly with a same-headway window elsewhere; 0 = no shift, the old behavior.")]
    public float departureOffsetMinutes = 0f;

    [Tooltip("Percent of the route/variant's own oneWayTripMinutes to use for trips scheduled in THIS window. 100 = unchanged. oneWayTripMinutes is already calibrated around peak conditions, so a genuine peak window should stay close to 100 (~95-110); quieter windows (overnight, etc.) should scale down toward the roads-are-empty end (as low as ~50). Feeds two things off this one value: scheduling math (BusScheduler.TripMinutesForSlot -- dispatch timing, retirement pacing, required-bus estimates) AND how fast a bus physically drives during this window (BusScheduler.GetScheduleSpeedMultiplier, read by NPCBusController), so the two stay consistent with each other.")]
    public float tripTimeMultiplierPercent = 100f;
}
// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE VARIANT DATA — Structural overrides for specific lines (e.g., Variant A/B)
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class RouteVariantData
{
    public string variantLetter = "A";

    [Tooltip("Show this variant's letter BEFORE the route number (e.g. 'N136' instead of '136N') on consoles, menus, maps and the LCD boards. Off = the normal 'number then letter' (34A, 87A). Short turns ('~') ignore this.")]
    public bool letterInFront = false;

    [Header("Short Turn")]
    [Tooltip("Marks this variant as THE short turn of the route: a trip pattern that turns back at an intermediate stop instead of running to the far terminal. It behaves exactly like any other variant (own stops, path, schedule, vehicle rules, destination) with three differences: its letter is always '~' (consoles and the 2D driver board show e.g. '116~'; LCD boards show the plain route number and this variant's destination), it always uses its own schedule and route data, and anything you leave empty is filled in from the mainline (see Turnback Stop Code).")]
    public bool isShortTurn = false;
    [Tooltip("The MAINLINE outbound stop this short turn turns back at. Leave the Outbound/Inbound Stops Overrides empty and they are built from the mainline for you: outbound = first stop up to and including this stop, inbound = from this stop (or the Inbound Stop Code below) back to the start. Also becomes this variant's Z terminal.")]
    public string turnbackStopCode;
    [Tooltip("Optional. The stop on the mainline INBOUND list where the return trip starts, when the turnback stop is on the other side of the road and has a different code. Blank = same code as the Turnback Stop Code.")]
    public string turnbackInboundStopCode;

    [Header("Path Overrides (optional)")]
    public bool overrideRoute = false; 
    public List<RouteNode> outboundNodesOverride;
    public List<RouteNode> inboundNodesOverride;

    // These hold the custom timings for this variant
    public List<RouteStopBinding> outboundStopsOverride = new List<RouteStopBinding>();
    public List<RouteStopBinding> inboundStopsOverride = new List<RouteStopBinding>();

    public string terminalACodeOverride;
    public string terminalZCodeOverride;
    [Header("Destination Overrides (optional)")]
public string destinationNameOutboundOverride;
public string destinationNameInboundOverride;

    [Header("Schedule Overrides (optional)")]
    public bool overrideSchedule = false;

    public float operatingStartMinutes;
    public float operatingEndMinutes;

    public float headwayFromAMinutes;
    public float headwayFromZMinutes;

    public float oneWayTripMinutes;

    [Tooltip("Same structure as the mainline route's own scheduleWindows — if populated, overrides the flat headwayFromA/ZMinutes above with time-of-day-specific headways for THIS variant only. Only takes effect when overrideSchedule is true.")]
    public List<ScheduleWindow> scheduleWindows = new List<ScheduleWindow>();
    public bool UsesTimeOfDayWindows => scheduleWindows != null && scheduleWindows.Count > 0;

    /// <summary>This variant's own oneWayTripMinutes (falling back to the
    /// mainline's if unset/zero — some variants only override schedule
    /// timing, not trip duration), scaled by whichever of THIS variant's own
    /// scheduleWindows contains minuteOfDay. Mirrors BusRouteData.EffectiveTripMinutes
    /// but resolved against the variant's own windows, not the mainline's.</summary>
    public float EffectiveTripMinutes(float mainlineTripMinutes, float minuteOfDay)
    {
        float baseTrip = oneWayTripMinutes > 0f ? oneWayTripMinutes : mainlineTripMinutes;
        return baseTrip * (BusRouteData.ResolveTripTimeMultiplierPercent(scheduleWindows, minuteOfDay) / 100f);
    }

    [Header("Vehicle Restrictions (optional)")]
    [Tooltip("If true, overrides the mainline's articulatedPolicy/allowedFleetSeries for THIS variant only — e.g. an overnight 'N' variant that should only ever draw older, non-articulated buses.")]
    public bool overrideVehicleRestrictions = false;
    public ArticulatedRequirement articulatedPolicyOverride = ArticulatedRequirement.Allowed;
    public List<int> allowedFleetSeriesOverride = new List<int>();

    // Runtime Resolved Stops for this variant
    [System.NonSerialized] public List<BusStopData> resolvedOutboundStops = new List<BusStopData>();
    [System.NonSerialized] public List<BusStopData> resolvedInboundStops  = new List<BusStopData>();

    // ── Redirects to resolve NPCBusController errors (CS1061) ─────────────────
    public List<RouteNode> outboundNodes => outboundNodesOverride;
    public List<RouteNode> inboundNodes => inboundNodesOverride;

    public List<string> outboundStopCodesOverride => outboundStopsOverride?.ConvertAll(b => b.stopCode) ?? new List<string>();
    public List<string> inboundStopCodesOverride  => inboundStopsOverride?.ConvertAll(b => b.stopCode) ?? new List<string>();
}

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS ROUTE DATA  —  ScriptableObject asset
// ═══════════════════════════════════════════════════════════════════════════════
[CreateAssetMenu(menuName = "Transit/Bus Route", fileName = "Route_XX")]
public class BusRouteData : ScriptableObject
{
    [Header("Identity")]
    [Tooltip("Stable identity for snapshot/rename tracking — set this once per route and never change it, even if routeNumber changes later (e.g. 36 renamed to 136 should KEEP the same routeId). Leave blank and it'll just fall back to routeNumber matching in the snapshot viewer, same as before this field existed.")]
    public string routeId;
    public string routeNumber;
    public string routeName;
    public Color  routeColor = Color.blue;
[Header("Vehicle Restrictions")]
[Tooltip("Controls how this route handles articulated (bendy) buses.")]
public ArticulatedRequirement articulatedPolicy = ArticulatedRequirement.Allowed;
[Tooltip("Controls how this route handles 35ft mini buses (XD35/XN35). Same enum as " +
         "articulatedPolicy: Prohibited = banned from 35ft, Mandatory = ONLY 35ft, " +
         "Preferred = prefers 35ft but falls back, Allowed = no restriction.")]
public ArticulatedRequirement miniBusPolicy = ArticulatedRequirement.Allowed;
[Header("Night Fleet (00:00\u201305:00)")]
[Tooltip("If non-empty, restricts this route to buses in these fleet-series hundred-blocks " +
         "SPECIFICALLY during the 00:00\u201305:00 night window (same format as allowedFleetSeries, " +
         "e.g. entering 900 restricts night service to fleet #900-999). Outside that window, or " +
         "if left empty, normal allowedFleetSeries rules apply as usual. Only takes effect on " +
         "calls that pass a minuteOfDay — see IsBusAllowed's optional parameter.")]
public List<int> nightFleetSeries = new List<int>();

public const float NightWindowStartMinutes = 0f;
public const float NightWindowEndMinutes   = 300f; // 05:00

/// <summary>True if minuteOfDay (0-1439, wrapped) falls in the 00:00-05:00 night window.</summary>
public static bool IsNightWindow(float minuteOfDay) =>
    minuteOfDay >= NightWindowStartMinutes && minuteOfDay < NightWindowEndMinutes;

[Tooltip("If non-empty, ONLY buses whose fleet number falls in one of these " +
         "hundred-series blocks can ever be picked for this route — e.g. entering " +
         "1900 restricts to fleet #1900-1999, entering both 1900 and 2100 allows " +
         "either series. Leave empty to allow any series (still subject to " +
         "articulatedPolicy above). SPECIAL VALUE 99: a wildcard meaning \"also allow " +
         "old buses (see Old Bus Range below) whose home depot already serves this " +
         "route\" (via DepotData.ServesRoute) — no need to hand-list every old block.")]
public List<int> allowedFleetSeries = new List<int>();

public const int OLD_BUS_DEPOT_WILDCARD = 99;

[Tooltip("Fleet numbers in this inclusive range count as \"old buses\" for the 99 " +
         "wildcard above. Defaults match the pre-numbered legacy fleet (900s-1400s).")]
public int oldBusRangeStart = 901;
public int oldBusRangeEnd   = 1499;
    [Header("Variant Configurations")]
    [Tooltip("List of all possible layout variants for this bus route.")]
    public List<RouteVariantData> variants = new List<RouteVariantData>(); 

    [Header("Fleet Management")]
    [Tooltip("Hard cap on how many buses can be assigned to this route.")]
    public int maxBusesAllowed = 5;

    // ── Path Nodes ────────────────────────────────────────────────────────────
    [Header("Route Path — Outbound (Terminal A → Terminal Z)")]
    public List<RouteNode> outboundNodes = new List<RouteNode>();

    [Header("Route Path — Inbound (Terminal Z → Terminal A)")]
    public List<RouteNode> inboundNodes = new List<RouteNode>();

    [Header("Dead Run Path (optional)")]
    public List<RouteNode> deadRunNodes = new List<RouteNode>();

    // ── Stop Sequences ────────────────────────────────────────────────────────
    [Header("Stop Sequences & Timings (Mainline)")]
    public List<RouteStopBinding> outboundStops = new List<RouteStopBinding>();
    public List<RouteStopBinding> inboundStops = new List<RouteStopBinding>();

    // ── Terminals ─────────────────────────────────────────────────────────────
    [Header("Terminals")]
    public string terminalACode;
    public string terminalZCode;

    public string destinationNameOutbound = ""; // e.g. "CITY CENTRE"
public string destinationNameInbound  = ""; // e.g. "NORTH DEPOT"

    [Tooltip("Small qualifier line shown BELOW the destination on the board -- e.g. 'VIA EVERGREEN', 'LIMITED', 'MAX'. Leave blank for no third line. Separate per direction since a via-street or express designation often only applies one way.")]
    public string routeQualifierOutbound = "";
    public string routeQualifierInbound  = "";

    // ── Schedule ──────────────────────────────────────────────────────────────
    [Header("Schedule")]
    public float operatingStartMinutes  = 255f;   // 04:15
    public float operatingEndMinutes    = 1215f;  // 20:15
    public float headwayFromAMinutes    = 15f;
    public float headwayFromZMinutes    = 15f;
    public float oneWayTripMinutes      = 30f;
[Header("Time-of-Day Service Windows (optional)")]
[Tooltip("If populated, overrides the flat headwayFromAMinutes/Z fields. " +
         "Windows should be contiguous/non-overlapping for clean handoffs " +
         "(e.g. 4:00–6:30 @30, 6:45–20:15 @15).")]
public List<ScheduleWindow> scheduleWindows = new();

public bool UsesTimeOfDayWindows => scheduleWindows != null && scheduleWindows.Count > 0;
    // ── Properties ────────────────────────────────────────────────────────────
    public float CycleTimeMinutes => oneWayTripMinutes * 2f;

    // [FIX] This previously always computed from the flat headwayFromAMinutes/Z
    // fields even when scheduleWindows was populated — but UsesTimeOfDayWindows
    // routes are supposed to have those flat fields OVERRIDDEN by the windows
    // (per the tooltip above). Any route using a RouteScheduleTemplates preset
    // was silently computing fleet requirements off stale/wrong headway numbers.
    // Now uses the TIGHTEST (minimum) headway across all windows when present —
    // that's the peak requirement, which is what actually sets the bus count.
    public int RequiredBusCount
    {
        get
        {
            float tightestHeadway = Mathf.Min(headwayFromAMinutes, headwayFromZMinutes);

            if (UsesTimeOfDayWindows)
            {
                float tightestWindowHeadway = float.MaxValue;
                foreach (var w in scheduleWindows)
                {
                    float h = Mathf.Min(w.headwayFromAMinutes, w.headwayFromZMinutes);
                    if (h > 0f && h < tightestWindowHeadway) tightestWindowHeadway = h;
                }
                if (tightestWindowHeadway < float.MaxValue) tightestHeadway = tightestWindowHeadway;
            }

            return Mathf.CeilToInt(CycleTimeMinutes / Mathf.Max(0.1f, tightestHeadway));
        }
    }

    /// <summary>Finds which of `windows` contains minuteOfDay (0-1439, wraps
    /// past midnight the same way BusScheduler's own window resolution does)
    /// and returns its tripTimeMultiplierPercent. Returns 100 (no scaling) if
    /// minuteOfDay falls outside every window, or if `windows` is empty --
    /// callers don't need to special-case either. Static and shared across
    /// mainline/variant/short-turn windows since they're all the same
    /// ScheduleWindow type.</summary>
    public static float ResolveTripTimeMultiplierPercent(List<ScheduleWindow> windows, float minuteOfDay)
    {
        if (windows == null || windows.Count == 0) return 100f;
        foreach (var w in windows)
        {
            float start = w.windowStartMinutes;
            float end = w.windowEndMinutes > start ? w.windowEndMinutes : w.windowEndMinutes + 1440f;
            float m = minuteOfDay;
            if (m < start && end > 1440f) m += 1440f;
            if (m >= start && m < end) return w.tripTimeMultiplierPercent;
        }
        return 100f;
    }

    /// <summary>oneWayTripMinutes scaled by whichever mainline scheduleWindow
    /// contains minuteOfDay. Falls back to the flat oneWayTripMinutes
    /// unscaled if this route doesn't use time-of-day windows, or minuteOfDay
    /// isn't in any of them.</summary>
    public float EffectiveTripMinutes(float minuteOfDay) =>
        oneWayTripMinutes * (ResolveTripTimeMultiplierPercent(scheduleWindows, minuteOfDay) / 100f);

    // ── Resolved Runtime Data (not serialized) ────────────────────────────────
    [System.NonSerialized] public List<BusStopData> resolvedOutboundStops = new List<BusStopData>();
    [System.NonSerialized] public List<BusStopData> resolvedInboundStops  = new List<BusStopData>();
    [System.NonSerialized] public BusStopData       resolvedTerminalA;
    [System.NonSerialized] public BusStopData       resolvedTerminalZ;

    // ── Public Helper Core Engine API ──────────────────────────────────────────

    public int GetCurrentBusCap() => maxBusesAllowed;

    // ── [FIX] Cache-busting on manual edits ──────────────────────────────────
    // BusTrackerService now keeps a stop -> serving-routes registry, rebuilt
    // proactively (RebuildStopServiceIndex) rather than resolved lazily via a
    // per-route cache that had to be remembered to invalidate. OnValidate is
    // a ScriptableObject callback Unity calls automatically the instant ANY
    // serialized field on this asset changes in the Inspector — including
    // nested fields like a variant's stop list — whether that edit happens in
    // Edit Mode or while Play Mode is already running (this project's normal
    // hand-editing workflow). So triggering a full index rebuild here means
    // a newly hand-added stop shows up immediately, no restart needed.
    private void OnEnable() => PrepareShortTurnVariants();

    private void OnValidate()
    {
        PrepareShortTurnVariants();
        if (BusTrackerService.Instance != null)
            BusTrackerService.Instance.RebuildStopServiceIndex();
    }

    public RouteVariantData GetVariant(string letter)
    {
        if (string.IsNullOrEmpty(letter)) return null;
        return variants.Find(v => v.variantLetter.Equals(letter, System.StringComparison.OrdinalIgnoreCase));
    }

    // ── Active-schedule display helper ───────────────────────────────────────
    // [ADD] Single source of truth for "what headway/window is active right
    // now" for UI (MainMenu route cards, RouteSnapshotViewerUI, etc.) so
    // every screen resolves variant-override-vs-mainline, and
    // windows-vs-flat-headway, the same way instead of each caller
    // reimplementing the fallback logic slightly differently.
    //
    // Priority, matching the override semantics elsewhere on this class:
    //   1. variant.overrideSchedule + variant windows containing `minuteOfDay`
    //   2. variant.overrideSchedule + variant's flat headway (no window covers now,
    //      or variant has no windows at all)
    //   3. mainline windows containing `minuteOfDay`
    //   4. mainline's flat headway (no window covers now, or route has no windows)
    //
    // Returns both the resolved headway (minutes) and a ready-to-display
    // label, so callers don't need to know which branch fired.
    public struct ActiveSchedule
    {
        public float headwayMinutes;
        public string windowLabel;   // null when running off the flat headway, not a window
        public bool  fromVariant;
    }

    public ActiveSchedule GetActiveSchedule(float minuteOfDay, RouteVariantData variant = null)
    {
        minuteOfDay = ((minuteOfDay % 1440f) + 1440f) % 1440f;

        if (variant != null && variant.overrideSchedule)
        {
            if (variant.UsesTimeOfDayWindows)
            {
                var w = FindWindow(variant.scheduleWindows, minuteOfDay);
                if (w != null)
                    return new ActiveSchedule { headwayMinutes = Mathf.Min(w.headwayFromAMinutes, w.headwayFromZMinutes), windowLabel = w.label, fromVariant = true };
            }
            // Variant windows exist but none cover right now, or variant has
            // no windows at all -- either way, fall back to its own flat
            // headway rather than silently falling through to the mainline's.
            return new ActiveSchedule { headwayMinutes = Mathf.Min(variant.headwayFromAMinutes, variant.headwayFromZMinutes), windowLabel = null, fromVariant = true };
        }

        if (UsesTimeOfDayWindows)
        {
            var w = FindWindow(scheduleWindows, minuteOfDay);
            if (w != null)
                return new ActiveSchedule { headwayMinutes = Mathf.Min(w.headwayFromAMinutes, w.headwayFromZMinutes), windowLabel = w.label, fromVariant = false };
        }

        return new ActiveSchedule { headwayMinutes = Mathf.Min(headwayFromAMinutes, headwayFromZMinutes), windowLabel = null, fromVariant = false };
    }

    private static ScheduleWindow FindWindow(List<ScheduleWindow> windows, float minuteOfDay)
    {
        foreach (var w in windows)
        {
            float start = w.windowStartMinutes;
            float end   = w.windowEndMinutes;
            if (end <= start) end += 1440f; // window crosses midnight
            float adj = minuteOfDay < start ? minuteOfDay + 1440f : minuteOfDay;
            if (adj >= start && adj <= end) return w;
        }
        return null;
    }

    /// <summary>"Every 15m (AM Peak)" when a window is active, "Every 15m" off a flat headway.</summary>
    public static string FormatScheduleLabel(ActiveSchedule s)
    {
        string headway = $"Every {Mathf.RoundToInt(s.headwayMinutes)}m";
        return s.windowLabel != null ? $"{headway} ({s.windowLabel})" : headway;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  SHORT TURNS -- a short turn is just a variant with isShortTurn set.
    // ═════════════════════════════════════════════════════════════════════
    /// <summary>The universal short-turn symbol: route 116's short turn is "116~".</summary>
    public const string ShortTurnSymbol = "~";

    /// <summary>What LCD/destination boards show as the route number: the label without the short-turn symbol
    /// ("116~" -> "116"). Consoles and the 2D driver board keep the symbol.</summary>
    public static string BoardRouteNumber(string routeLabel) =>
        string.IsNullOrEmpty(routeLabel) ? routeLabel : routeLabel.Replace(ShortTurnSymbol, "");

    // ═════════════════════════════════════════════════════════════════════
    //  ROUTE LABEL WITH THE VARIANT LETTER IN THE RIGHT PLACE
    //  Normally the letter follows the number ("34A"). A variant with letterInFront shows it first ("N136").
    //  Use these anywhere a route number and a variant letter are joined for DISPLAY. Keep plain concatenation for
    //  dictionary keys and logs that need a stable key.
    // ═════════════════════════════════════════════════════════════════════
    /// <summary>The route asset with this number (looked up through CityManager's route list), or null.</summary>
    public static BusRouteData FindByNumber(string routeNumber)
    {
        var cm = CityManager.Instance;
        if (cm == null || cm.routes == null || string.IsNullOrEmpty(routeNumber)) return null;
        for (int i = 0; i < cm.routes.Length; i++)
            if (cm.routes[i] != null && cm.routes[i].routeNumber == routeNumber) return cm.routes[i];
        return null;
    }

    /// <summary>"136N" normally, "N136" when that variant has letterInFront. Safe with a null/empty letter.</summary>
    public static string RouteLabel(string routeNumber, string variantLetter)
    {
        if (string.IsNullOrEmpty(variantLetter)) return routeNumber;
        return FindByNumber(routeNumber)?.LabelFor(variantLetter) ?? routeNumber + variantLetter;
    }

    /// <summary>Same as RouteLabel but for THIS route asset.</summary>
    public string LabelFor(string variantLetter)
    {
        if (string.IsNullOrEmpty(variantLetter)) return routeNumber;
        var v = GetVariant(variantLetter);
        return (v != null && v.letterInFront && variantLetter != ShortTurnSymbol) ? variantLetter + routeNumber : routeNumber + variantLetter;
    }

    /// <summary>What an NPC bus's LCD board shows as the route number: the plain number, except that a letter-in-front
    /// variant adds its letter ("N136"). Other variants keep the plain number, exactly as before.</summary>
    public string BoardNumberFor(string variantLetter)
    {
        if (string.IsNullOrEmpty(variantLetter) || variantLetter == ShortTurnSymbol) return routeNumber;
        var v = GetVariant(variantLetter);
        return (v != null && v.letterInFront) ? variantLetter + routeNumber : routeNumber;
    }

    /// <summary>Variants marked as short turns that actually carry the '~' symbol (max one per route).</summary>
    public List<RouteVariantData> GetShortTurnVariants()
    {
        var result = new List<RouteVariantData>();
        if (variants == null) return result;
        foreach (var v in variants)
            if (v != null && v.isShortTurn && v.variantLetter == ShortTurnSymbol) result.Add(v);
        return result;
    }

    /// <summary>The destination text a board should show for this direction: the variant's own override if it has
    /// one, then (for a short turn, outbound) the turnback stop's name, else the mainline destination.</summary>
    public string GetDestinationName(bool outbound, RouteVariantData variant = null)
    {
        if (variant != null)
        {
            string over = outbound ? variant.destinationNameOutboundOverride : variant.destinationNameInboundOverride;
            if (!string.IsNullOrEmpty(over)) return over;

            if (variant.isShortTurn && outbound && !string.IsNullOrEmpty(variant.turnbackStopCode))
            {
                var stop = CityManager.Instance != null ? CityManager.Instance.GetStop(variant.turnbackStopCode) : null;
                if (stop != null && !string.IsNullOrEmpty(stop.stopName)) return stop.stopName;
            }
        }
        return outbound ? destinationNameOutbound : destinationNameInbound;
    }

    /// <summary>Applies the short-turn rules to every short-turn variant: fixes its letter to '~', makes it use its
    /// own route data and schedule, and fills anything left empty from the mainline (stops, terminal, trip time,
    /// hours and headways). Safe to run repeatedly; only fills what is empty.</summary>
    public void PrepareShortTurnVariants()
    {
        if (variants == null) return;
        bool symbolTaken = false;
        foreach (var v in variants)
        {
            if (v == null || !v.isShortTurn) continue;
            if (symbolTaken) continue;             // only the first flagged variant gets '~'
            symbolTaken = true;

            v.variantLetter  = ShortTurnSymbol;
            v.overrideRoute  = true;
            v.overrideSchedule = true;

            if (string.IsNullOrEmpty(v.turnbackStopCode) || outboundStops == null || outboundStops.Count == 0) continue;

            int turnIdx = outboundStops.FindIndex(b => b.stopCode == v.turnbackStopCode);
            if (turnIdx < 0) continue;             // reported by "Validate Short Turns"

            // Outbound: start of the route through the turnback stop.
            if (v.outboundStopsOverride == null || v.outboundStopsOverride.Count == 0)
            {
                v.outboundStopsOverride = new List<RouteStopBinding>();
                for (int i = 0; i <= turnIdx; i++) v.outboundStopsOverride.Add(outboundStops[i]);
                var first = v.outboundStopsOverride[0]; first.isTimepoint = true; v.outboundStopsOverride[0] = first;
                int lastIdx = v.outboundStopsOverride.Count - 1;
                var last = v.outboundStopsOverride[lastIdx]; last.isTimepoint = true; v.outboundStopsOverride[lastIdx] = last;
            }

            // Inbound: from the turnback point back to the start. Minutes are re-based so the leg starts at 0.
            if ((v.inboundStopsOverride == null || v.inboundStopsOverride.Count == 0) && inboundStops != null && inboundStops.Count > 0)
            {
                string backCode = string.IsNullOrEmpty(v.turnbackInboundStopCode) ? v.turnbackStopCode : v.turnbackInboundStopCode;
                int backIdx = inboundStops.FindIndex(b => b.stopCode == backCode);
                if (backIdx < 0)
                {
                    // Different stop on the other side of the road: use the inbound stop nearest in trip time.
                    float target = Mathf.Max(0f, (oneWayTripMinutes > 0f ? oneWayTripMinutes : inboundStops[inboundStops.Count - 1].minutesFromStart) - outboundStops[turnIdx].minutesFromStart);
                    float bestGap = float.MaxValue;
                    for (int i = 0; i < inboundStops.Count; i++)
                    {
                        float gap = Mathf.Abs(inboundStops[i].minutesFromStart - target);
                        if (gap < bestGap) { bestGap = gap; backIdx = i; }
                    }
                    if (backIdx < 0) backIdx = 0;
                }
                v.inboundStopsOverride = new List<RouteStopBinding>();
                float baseMinutes = inboundStops[backIdx].minutesFromStart;
                for (int i = backIdx; i < inboundStops.Count; i++)
                {
                    var b = inboundStops[i];
                    b.minutesFromStart = Mathf.Max(0f, b.minutesFromStart - baseMinutes);
                    v.inboundStopsOverride.Add(b);
                }
                var f0 = v.inboundStopsOverride[0]; f0.isTimepoint = true; v.inboundStopsOverride[0] = f0;
            }

            // The turnback stop is this variant's far (Z) terminal.
            if (string.IsNullOrEmpty(v.terminalZCodeOverride)) v.terminalZCodeOverride = v.turnbackStopCode;

            // Trip time from the longer of its two legs.
            if (v.oneWayTripMinutes <= 0f)
            {
                float outMin = v.outboundStopsOverride.Count > 0 ? v.outboundStopsOverride[v.outboundStopsOverride.Count - 1].minutesFromStart : 0f;
                float inMin  = v.inboundStopsOverride != null && v.inboundStopsOverride.Count > 0 ? v.inboundStopsOverride[v.inboundStopsOverride.Count - 1].minutesFromStart : 0f;
                v.oneWayTripMinutes = Mathf.Max(1f, Mathf.Ceil(Mathf.Max(outMin, inMin)) + 1f);
            }

            // Hours and headways default to the mainline's until the short turn gets its own.
            bool noWindows = v.scheduleWindows == null || v.scheduleWindows.Count == 0;
            if (noWindows)
            {
                if (v.operatingStartMinutes <= 0f && v.operatingEndMinutes <= 0f)
                { v.operatingStartMinutes = operatingStartMinutes; v.operatingEndMinutes = operatingEndMinutes; }
                if (v.headwayFromAMinutes <= 0f) v.headwayFromAMinutes = headwayFromAMinutes;
                if (v.headwayFromZMinutes <= 0f) v.headwayFromZMinutes = headwayFromZMinutes;
            }
        }
    }

    /// <summary>Builds a short turn's path (nodes) from the mainline path when it has none of its own: outbound is the
    /// mainline path cut at the point nearest the turnback stop, inbound is the mainline path from the point nearest
    /// the return stop onward. Needs stop positions, so it runs once the city is loaded (CityManager.ResolveRoutes).</summary>
    public void DeriveShortTurnNodes(System.Func<string, Vector3?> stopPosition)
    {
        foreach (var v in GetShortTurnVariants())
        {
            if (string.IsNullOrEmpty(v.turnbackStopCode)) continue;

            if ((v.outboundNodesOverride == null || v.outboundNodesOverride.Count == 0) && outboundNodes != null && outboundNodes.Count >= 2)
            {
                var pos = stopPosition(v.turnbackStopCode);
                if (pos.HasValue) v.outboundNodesOverride = CutPathAt(outboundNodes, pos.Value, keepStart: true);
            }

            if ((v.inboundNodesOverride == null || v.inboundNodesOverride.Count == 0) && inboundNodes != null && inboundNodes.Count >= 2)
            {
                string backCode = (v.inboundStopsOverride != null && v.inboundStopsOverride.Count > 0)
                    ? v.inboundStopsOverride[0].stopCode : v.turnbackStopCode;
                var pos = stopPosition(backCode);
                if (pos.HasValue) v.inboundNodesOverride = CutPathAt(inboundNodes, pos.Value, keepStart: false);
            }
        }
    }

    /// <summary>The part of a node path before (keepStart) or after (!keepStart) the point on it nearest `p`.</summary>
    private static List<RouteNode> CutPathAt(List<RouteNode> path, Vector3 p, bool keepStart)
    {
        int bestSeg = 0; float bestDist = float.MaxValue; Vector3 bestPt = path[0].position;
        for (int i = 0; i < path.Count - 1; i++)
        {
            Vector3 a = path[i].position, b = path[i + 1].position; a.y = 0f; b.y = 0f;
            Vector3 q = new Vector3(p.x, 0f, p.z);
            Vector3 ab = b - a; float len = ab.sqrMagnitude;
            float t = len > 1e-6f ? Mathf.Clamp01(Vector3.Dot(q - a, ab) / len) : 0f;
            Vector3 c = a + ab * t; float d = (q - c).sqrMagnitude;
            if (d < bestDist) { bestDist = d; bestSeg = i; bestPt = new Vector3(c.x, path[i].position.y, c.z); }
        }
        var result = new List<RouteNode>();
        var cutNode = new RouteNode { position = bestPt, isCurve = false };
        if (keepStart)
        {
            for (int i = 0; i <= bestSeg; i++) result.Add(new RouteNode { position = path[i].position, isCurve = path[i].isCurve });
            result.Add(cutNode);
        }
        else
        {
            result.Add(cutNode);
            for (int i = bestSeg + 1; i < path.Count; i++) result.Add(new RouteNode { position = path[i].position, isCurve = path[i].isCurve });
        }
        return result;
    }

    public List<RouteNode> GetNodes(bool outbound, RouteVariantData variant = null)
    {
        if (variant != null && variant.overrideRoute)
        {
            if (outbound && variant.outboundNodesOverride != null && variant.outboundNodesOverride.Count > 0)
                return variant.outboundNodesOverride;

            if (!outbound && variant.inboundNodesOverride != null && variant.inboundNodesOverride.Count > 0)
                return variant.inboundNodesOverride;
        }
        return outbound ? outboundNodes : inboundNodes;
    }

public List<RouteStopBinding> GetStopBindings(bool outbound, RouteVariantData variant = null)
{
    // 1. Check for valid variant overrides first
    if (variant != null && variant.overrideRoute)
    {
        var overrides = outbound ? variant.outboundStopsOverride : variant.inboundStopsOverride;
        if (overrides != null && overrides.Count > 0)
            return overrides;
    }
    
    // 2. Return mainline, or an empty list if even the mainline is null
    var mainline = outbound ? outboundStops : inboundStops;
    return mainline ?? new List<RouteStopBinding>();
}

    public List<string> GetStopCodes(bool outbound, RouteVariantData variant = null)
    {
        // Check variant overrides first
        if (variant != null && variant.overrideRoute)
        {
            if (outbound && variant.outboundStopsOverride != null && variant.outboundStopsOverride.Count > 0)
                return variant.outboundStopCodesOverride;

            if (!outbound && variant.inboundStopsOverride != null && variant.inboundStopsOverride.Count > 0)
                return variant.inboundStopCodesOverride;
        }
        
        // Fallback to converting the mainline structural bindings
        var bindings = outbound ? outboundStops : inboundStops;
        return bindings?.ConvertAll(b => b.stopCode) ?? new List<string>();
    }
    #if UNITY_EDITOR
[ContextMenu("Show Route Fleet Requirements")]
private void LogRouteRequirements()
{
    Debug.Log(BuildCombinedLogLine());
}

/// <summary>
/// Everything about this route -- fleet requirements, first/last stop
/// A→Z (outbound), first/last stop Z→A (inbound), service windows, and
/// short turns -- built into ONE string so it prints as a single
/// Debug.Log line instead of the old scattered multi-line dump.
/// </summary>
/// <summary>
/// Resolves a stop code to "Stop Name (code)" via CityManager.Instance's
/// stop registry -- falls back to just the raw code if CityManager isn't
/// around (e.g. this is an editor-only asset scan with no scene loaded)
/// or the code doesn't resolve to anything, so this never throws or
/// silently drops a stop from the log.
/// </summary>
private static string StopLabel(string stopCode)
{
    if (string.IsNullOrEmpty(stopCode)) return "(none)";

    var cm = CityManager.Instance;
    if (cm == null) return stopCode; // no live CityManager (e.g. asset-only scan) -- code is all we've got

    var stop = cm.GetStop(stopCode);
    if (stop == null || string.IsNullOrEmpty(stop.stopName)) return stopCode;

    return $"{stop.stopName} ({stopCode})";
}

private string BuildCombinedLogLine()
{
    string firstOutbound = StopLabel(outboundStops != null && outboundStops.Count > 0 ? outboundStops[0].stopCode : null);
    string lastOutbound  = StopLabel(outboundStops != null && outboundStops.Count > 0 ? outboundStops[outboundStops.Count - 1].stopCode : null);
    string firstInbound  = StopLabel(inboundStops  != null && inboundStops.Count  > 0 ? inboundStops[0].stopCode : null);
    string lastInbound   = StopLabel(inboundStops  != null && inboundStops.Count  > 0 ? inboundStops[inboundStops.Count - 1].stopCode : null);
    string terminalAName = StopLabel(terminalACode);
    string terminalZName = StopLabel(terminalZCode);

    string fleetSeries = (allowedFleetSeries == null || allowedFleetSeries.Count == 0)
        ? "ANY"
        : string.Join(", ", allowedFleetSeries.ConvertAll(b => b == OLD_BUS_DEPOT_WILDCARD ? "old-buses@depot" : b.ToString()));

    string windowsPart = "none";
    if (scheduleWindows != null && scheduleWindows.Count > 0)
    {
        windowsPart = string.Join(" | ", scheduleWindows.ConvertAll(w =>
            $"{w.label} {w.windowStartMinutes}-{w.windowEndMinutes} @{w.headwayFromAMinutes}/{w.headwayFromZMinutes}"));
    }

    string shortTurnsPart = "none";
    var stVariants = GetShortTurnVariants();
    if (stVariants.Count > 0)
    {
        shortTurnsPart = string.Join(" | ", stVariants.ConvertAll(v =>
            $"{routeNumber}{ShortTurnSymbol} (turns back @{StopLabel(v.turnbackStopCode)}, " +
            $"{(v.outboundStopsOverride != null ? v.outboundStopsOverride.Count : 0)} out / {(v.inboundStopsOverride != null ? v.inboundStopsOverride.Count : 0)} in stops)"));
    }

    return $"[Route {routeNumber} - {routeName}] " +
           $"Articulated: {articulatedPolicy} | " +
           $"Max Buses: {maxBusesAllowed} | " +
           $"Fleet Series: {fleetSeries} | " +
           $"Required Buses: {RequiredBusCount} | " +
           $"Terminal A: {terminalAName} | " +
           $"Terminal Z: {terminalZName} | " +
           $"Destination Outbound: {(string.IsNullOrEmpty(destinationNameOutbound) ? "(none)" : destinationNameOutbound)} | " +
           $"Destination Inbound: {(string.IsNullOrEmpty(destinationNameInbound) ? "(none)" : destinationNameInbound)} | " +
           $"A→Z first/last stop: {firstOutbound} / {lastOutbound} | " +
           $"Z→A first/last stop: {firstInbound} / {lastInbound} | " +
           $"Service Windows: {windowsPart} | " +
           $"Short Turns: {shortTurnsPart}";
}

/// <summary>
/// Finds every BusRouteData asset in the project and logs each one as
/// its own single combined line (route info + both directions' first/
/// last stop + schedule windows + short turns), instead of one route
/// at a time via the per-asset context menu above.
/// </summary>
[ContextMenu("Log ALL Routes (Combined, One Line Each)")]
private void LogAllRoutesCombined()
{
    string[] guids = UnityEditor.AssetDatabase.FindAssets("t:BusRouteData");
    Debug.Log($"========== ALL ROUTES ({guids.Length}) ==========");
    foreach (string guid in guids)
    {
        string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
        var route = UnityEditor.AssetDatabase.LoadAssetAtPath<BusRouteData>(path);
        if (route == null) continue;
        Debug.Log(route.BuildCombinedLogLine(), route);
    }
}

/// <summary>
/// Universal allocation check — doesn't matter which route asset you right-
/// click this on, it always checks the WHOLE fleet/day via BusScheduler.
/// Same "call it from any one asset, it covers everything" pattern as
/// LogAllRoutesCombined above, just pointed at BusScheduler's live
/// per-day slot data instead of scanning route assets. Editor/play-mode
/// only since there's no _allSlots to check outside a running BusScheduler.
/// Requires BusScheduler.PrintAllocationConflictReport(int dayNumber) to
/// exist (see BusScheduler.cs) — this is just the "call it from here" hook.
/// </summary>
[ContextMenu("Check ALL Routes For Double-Booked Buses (Today)")]
private void CheckAllRoutesForDoubleBookedBuses()
{
    if (!Application.isPlaying)
    {
        Debug.LogWarning("[BusRouteData] Allocation check needs a running BusScheduler — enter Play Mode first.");
        return;
    }
    if (BusScheduler.Instance == null)
    {
        Debug.LogWarning("[BusRouteData] No BusScheduler.Instance found in the scene.");
        return;
    }
    if (SimClock.Instance == null)
    {
        Debug.LogWarning("[BusRouteData] No SimClock.Instance found — can't resolve 'today'.");
        return;
    }

    // Ignores 'this' route entirely on purpose — BusScheduler's report
    // walks every managed route for the day, this asset is just the
    // convenient place in the Inspector to trigger it from.
    BusScheduler.Instance.PrintAllocationConflictReport(SimClock.Instance.GameDayNumber);
}

// ── PASTE THE CONTEXT MENUS HERE (Inside BusRouteData) ──────────────────
    [ContextMenu("Templates/Apply 24-Hour Schedule")]
    private void ContextApply24Hour() => RouteScheduleTemplates.Apply24Hour(this);

    [ContextMenu("Templates/Apply Non-24-Hour Schedule")]
    private void ContextApplyNon24Hour() => RouteScheduleTemplates.ApplyNon24Hour(this);

    [ContextMenu("Templates/Apply Rush Hour Cruiser")]
    private void ContextApplyRushHour() => RouteScheduleTemplates.ApplyRushHourCruiser(this);

[ContextMenu("Templates/Apply Non-24hr (6am-8pm)")]
    private void ContextApply6to8() => RouteScheduleTemplates.ApplyNon24Hour_6to8(this);

    [ContextMenu("Templates/Apply Non-24hr (4am-12am)")]
    private void ContextApply4to12() => RouteScheduleTemplates.ApplyNon24Hour_4to12(this);

    [ContextMenu("Templates/Apply Night Owl (8pm-6am)")]
    private void ContextApplyNightOwl8to6() => RouteScheduleTemplates.ApplyNightOwl_8to6(this);

    [ContextMenu("Templates/Apply Night Owl Opt (11pm-4am)")]
    private void ContextApplyNightOwlOpt11to4() => RouteScheduleTemplates.ApplyNightOwlOpt_11to4(this);

    [ContextMenu("Add Short Turn Variant")]
    private void AddShortTurnVariant()
    {
        if (variants == null) variants = new List<RouteVariantData>();
        if (variants.Exists(v => v != null && v.isShortTurn))
        {
            Debug.LogWarning($"[BusRouteData:{routeNumber}] Already has a short turn variant. Only one per route gets the '{ShortTurnSymbol}' symbol.");
            return;
        }
        variants.Add(new RouteVariantData { isShortTurn = true, variantLetter = ShortTurnSymbol, overrideRoute = true, overrideSchedule = true });
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log($"[BusRouteData:{routeNumber}] Added short turn variant. Set its Turnback Stop Code (an outbound mainline stop) and, if you want, its own schedule; everything else fills in from the mainline.");
    }

    [ContextMenu("Validate Short Turns")]
    private void ValidateShortTurns()
    {
        var list = GetShortTurnVariants();
        foreach (var v in list)
        {
            if (string.IsNullOrEmpty(v.turnbackStopCode))
            { Debug.LogWarning($"[BusRouteData:{routeNumber}] Short turn has no Turnback Stop Code set."); continue; }
            if (!(outboundStops ?? new List<RouteStopBinding>()).Exists(b => b.stopCode == v.turnbackStopCode))
                Debug.LogWarning($"[BusRouteData:{routeNumber}] Short turn: Turnback Stop Code '{v.turnbackStopCode}' is not a stop on the OUTBOUND mainline.");
        }
        int flagged = 0; foreach (var v in variants) if (v != null && v.isShortTurn) flagged++;
        if (flagged > 1) Debug.LogWarning($"[BusRouteData:{routeNumber}] {flagged} variants are marked Short Turn but only ONE per route gets the '{ShortTurnSymbol}' symbol; the others are treated as ordinary variants.");
        Debug.Log($"[BusRouteData:{routeNumber}] Short turn validation complete — {list.Count} short turn(s).");
    }

    #endif
/// <summary>True if `fleetNumber` falls within one of allowedFleetSeries'
/// hundred-blocks (1900 => 1900-1999), OR qualifies via the 99 old-bus/depot
/// wildcard. Empty list = no restriction.</summary>
/// <param name="minuteOfDay">Optional wrapped 0-1439 minute-of-day for the trip being
/// checked. Pass -1 (default) to skip night-fleet gating entirely — existing callers
/// that don't have a time handy keep the old series-only behavior.</param>
public bool IsFleetSeriesAllowed(int fleetNumber, float minuteOfDay = -1f)
{
    if (nightFleetSeries != null && nightFleetSeries.Count > 0 && minuteOfDay >= 0f && IsNightWindow(minuteOfDay))
    {
        int nightBlock = (fleetNumber / 100) * 100;
        return nightFleetSeries.Contains(nightBlock);
    }

    if (allowedFleetSeries == null || allowedFleetSeries.Count == 0) return true;

    int block = (fleetNumber / 100) * 100;
    if (allowedFleetSeries.Contains(block)) return true;

    if (allowedFleetSeries.Contains(OLD_BUS_DEPOT_WILDCARD) && IsOldBusAtServingDepot(fleetNumber))
        return true;

    return false;
}

/// <summary>Fleet number falls in the configured old-bus range AND its
/// FleetMetadata home depot already serves THIS route (DepotData.ServesRoute
/// handles variant-letter stripping itself). Backing check for the 99
/// wildcard, reused by the variant overrides below.</summary>
public bool IsOldBusAtServingDepot(int fleetNumber)
{
    if (fleetNumber < oldBusRangeStart || fleetNumber > oldBusRangeEnd) return false;

    var meta = FleetMetadata.Get(fleetNumber);
    if (meta == null || meta.homeDepot == null) return false;

    return meta.homeDepot.ServesRoute(routeNumber);
}

/// <summary>Same check but for a specific variant, which may override the
/// mainline's vehicle restrictions entirely.</summary>
/// <param name="minuteOfDay">Optional wrapped 0-1439 minute-of-day for the trip being
/// checked. The night-fleet gate is a hard operational restriction that applies
/// regardless of a variant's own override list, so it's checked FIRST, before
/// falling through to the variant's (or the mainline's) series check. Pass -1
/// (default) to skip night-fleet gating entirely.</param>
public bool IsFleetSeriesAllowedForVariant(int fleetNumber, RouteVariantData variant, float minuteOfDay = -1f)
{
    if (nightFleetSeries != null && nightFleetSeries.Count > 0 && minuteOfDay >= 0f && IsNightWindow(minuteOfDay))
    {
        int nightBlock = (fleetNumber / 100) * 100;
        return nightFleetSeries.Contains(nightBlock);
    }

    if (variant == null || !variant.overrideVehicleRestrictions) return IsFleetSeriesAllowed(fleetNumber);
    if (variant.allowedFleetSeriesOverride == null || variant.allowedFleetSeriesOverride.Count == 0) return true;

    int block = (fleetNumber / 100) * 100;
    if (variant.allowedFleetSeriesOverride.Contains(block)) return true;

    if (variant.allowedFleetSeriesOverride.Contains(OLD_BUS_DEPOT_WILDCARD) && IsOldBusAtServingDepot(fleetNumber))
        return true;

    return false;
}

/// <param name="minuteOfDay">Optional wrapped 0-1439 minute-of-day for the trip being
/// checked — pass this (e.g. slot.scheduledDeparture % 1440f) to make nightFleetSeries
/// take effect. Defaults to -1 (no night-fleet gating), so existing call sites keep
/// working unchanged.</param>
public bool IsBusAllowed(int fleetNumber, float minuteOfDay = -1f)
{
    var meta = FleetMetadata.Get(fleetNumber);
    
    if (meta == null) 
    {
        // 🚩 IF YOU SEE THIS IN THE CONSOLE, THIS IS THE PROBLEM
        Debug.LogWarning($"[RouteFilter] IsBusAllowed failed for Bus #{fleetNumber}! No metadata found. Defaulting to ALLOWED.");
        return true; 
    }

    if (!IsFleetSeriesAllowed(fleetNumber, minuteOfDay))
    {
        Debug.Log($"[RouteFilter] Route {routeNumber} restricted to series {string.Join(",", allowedFleetSeries)} — Bus #{fleetNumber} rejected.");
        return false;
    }

    switch (articulatedPolicy)
    {
        case ArticulatedRequirement.Prohibited:
            bool isAllowedProhibited = !meta.isArticulated;
            Debug.Log($"[RouteFilter] Route {routeNumber} (Prohibited) checked Bus #{fleetNumber} (Artic {meta.isArticulated}) -> Allowed: {isAllowedProhibited}");
            if (!isAllowedProhibited) return false;
            break;

        case ArticulatedRequirement.Mandatory:
            bool isAllowedMandatory = meta.isArticulated;
            Debug.Log($"[RouteFilter] Route {routeNumber} (Mandatory) checked Bus #{fleetNumber} (Artic {meta.isArticulated}) -> Allowed: {isAllowedMandatory}");
            if (!isAllowedMandatory) return false;
            break;
    }

    switch (miniBusPolicy)
    {
        case ArticulatedRequirement.Prohibited:
            if (meta.is35Ft)
            {
                Debug.Log($"[RouteFilter] Route {routeNumber} bans 35ft minis — Bus #{fleetNumber} rejected.");
                return false;
            }
            break;

        case ArticulatedRequirement.Mandatory:
            if (!meta.is35Ft)
            {
                Debug.Log($"[RouteFilter] Route {routeNumber} requires 35ft minis — Bus #{fleetNumber} rejected.");
                return false;
            }
            break;
    }

    return true;
}

/// <summary>Same as IsBusAllowed but checks a variant's override policy if it has one
/// (e.g. an overnight variant restricted to older, non-articulated buses).</summary>
/// <param name="minuteOfDay">Optional wrapped 0-1439 minute-of-day for the trip being
/// checked — pass this (e.g. slot.scheduledDeparture % 1440f) to make nightFleetSeries
/// take effect. Defaults to -1 (no night-fleet gating).</param>
public bool IsBusAllowedForVariant(int fleetNumber, RouteVariantData variant, float minuteOfDay = -1f)
{
    if (variant == null || !variant.overrideVehicleRestrictions) return IsBusAllowed(fleetNumber, minuteOfDay);

    var meta = FleetMetadata.Get(fleetNumber);
    if (meta == null) return true;

    if (!IsFleetSeriesAllowedForVariant(fleetNumber, variant, minuteOfDay)) return false;

    switch (variant.articulatedPolicyOverride)
    {
        case ArticulatedRequirement.Prohibited: return !meta.isArticulated;
        case ArticulatedRequirement.Mandatory:  return meta.isArticulated;
        default: return true;
    }
}

public int GetAssignmentScore(int fleetNumber)
{
    var meta = FleetMetadata.Get(fleetNumber);
    if (meta == null) return 0;

    // If the route prefers articulated buses and this bus IS articulated, bump its priority
    if (articulatedPolicy == ArticulatedRequirement.Preferred && meta.isArticulated)
    {
        return 10; // High priority weight
    }

    return 0; // Standard weight
}
    public string GetTargetTerminalCode(bool outbound, RouteVariantData variant = null)
    {
        if (variant != null && variant.overrideRoute)
        {
            if (outbound && !string.IsNullOrEmpty(variant.terminalZCodeOverride))
                return variant.terminalZCodeOverride;

            if (!outbound && !string.IsNullOrEmpty(variant.terminalACodeOverride))
                return variant.terminalACodeOverride;
        }
        return outbound ? terminalZCode : terminalACode;
    }

    public List<BusStopData> GetStops(bool outbound, RouteVariantData variant = null)
    {
        if (variant != null && variant.overrideRoute)
        {
            // Fall back to mainline if the runtime variant list wasn't populated yet
            if (outbound && variant.resolvedOutboundStops != null && variant.resolvedOutboundStops.Count > 0)
                return variant.resolvedOutboundStops;

            if (!outbound && variant.resolvedInboundStops != null && variant.resolvedInboundStops.Count > 0)
                return variant.resolvedInboundStops;
        }
        return outbound ? resolvedOutboundStops : resolvedInboundStops;
    }

    // ── Bezier Evaluation ─────────────────────────────────────────────────────
    /// <summary>Always builds a plain straight segment between the two nodes'
    /// own baked positions. No road lookup, no live-spline snap — if you
    /// placed a node at a Vector3, that's exactly where the bus drives,
    /// full stop.</summary>
    // ═════════════════════════════════════════════════════════════════════════
    //  GRAPH-BACKED PATH BUILDING
    //
    //  The Vector3 nodes above (outboundNodes/inboundNodes) are now treated as
    //  loose directional waypoints — "go roughly this way" — not literal path
    //  geometry. The actual drivable path snaps to the real road network:
    //  for each consecutive pair of nodes, RoadGraphPathfinder finds the real
    //  route between them (real edges, real lane offsets via GetLanePosition),
    //  and the results get stitched into one continuous list. This is what
    //  fixes route paths silently drifting off the actual road — the path
    //  IS the road now, not a hand-authored curve that's supposed to
    //  approximate it.
    //
    //  NOTE: this doesn't replace BuildSegments/IRouteSegment above — that
    //  system stays as-is for anything still consuming it directly. This is
    //  a new, separate output (a flat List<Vector3>) meant to become the
    //  actual thing a bus drives; wiring THAT in means finding whatever
    //  currently calls BuildSegments/Evaluate for live driving and pointing
    //  it at this instead — that call site isn't in this file, so I can't
    //  safely touch it without seeing it.
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Builds the real, road-snapped drivable path by pathfinding
    /// between each consecutive pair of directional waypoint nodes. Returns
    /// null if any leg has no path (caller should treat that as "off-road
    /// gap" rather than silently skipping it).</summary>
    public static List<Vector3> BuildGraphBackedPath(List<RouteNode> waypointHints, RoadGraph graph,
                                                      float waypointSpacing = 2.5f, float maxSnapDistance = 15f)
    {
        if (graph == null || waypointHints == null || waypointHints.Count < 2) return null;

        var fullPath = new List<Vector3>();

        // Force the very first point to land exactly on the first waypoint
        // hint (e.g. the bus stop's real coordinate) — FindWaypoints already
        // forces its LAST point to exactly match toWorldPos (see its own
        // "Final approach into the exact destination" line), but never did
        // the equivalent for the start. Without this, the path started
        // wherever the pathfinder's nearest-edge snap happened to land,
        // which could sit noticeably off the stop's actual position.
        fullPath.Add(waypointHints[0].position);

        for (int i = 0; i < waypointHints.Count - 1; i++)
        {
            Vector3 from = waypointHints[i].position;
            Vector3 to   = waypointHints[i + 1].position;

            // Validate BEFORE trusting the pathfinder — FindNearestNode/
            // FindNearestEdgePoint always return SOMETHING, even if the true
            // nearest road is 40m away, because there's no distance cap
            // anywhere in that lookup. That's exactly how a parallel or
            // elevated road sitting near-but-not-on the intended one (e.g.
            // a causeway passing close to a lower road) can silently steal
            // the snap. FindNearestEdgePoint gives the actual nearest point
            // ON a road surface (not just nearest intersection node), which
            // is the right thing to distance-check against.
            bool fromOk = graph.FindNearestEdgePoint(from, out var fromEdge, out var fromT);
            bool toOk   = graph.FindNearestEdgePoint(to, out var toEdge, out var toT);
            float fromDist = fromOk ? Vector3.Distance(fromEdge.segment.EvaluatePosition(fromT), from) : float.MaxValue;
            float toDist   = toOk   ? Vector3.Distance(toEdge.segment.EvaluatePosition(toT), to)     : float.MaxValue;

            if (!fromOk || !toOk || fromDist > maxSnapDistance || toDist > maxSnapDistance)
            {
                Debug.LogWarning($"[BusRouteData] BuildGraphBackedPath: waypoint hint {i}/{i + 1} is too far from " +
                                  $"any real road (from: {fromDist:0.0}m, to: {toDist:0.0}m, max {maxSnapDistance}m) — " +
                                  $"likely about to snap onto the wrong nearby road. Rejecting this leg.");
                return null;
            }

            var leg = RoadGraphPathfinder.FindWaypoints(graph, from, to, waypointSpacing);
            if (leg == null || leg.Count == 0)
            {
                Debug.LogWarning($"[BusRouteData] BuildGraphBackedPath: no road path found between " +
                                  $"waypoint hints {i} ({from}) and {i + 1} ({to}) — path has a real gap here.");
                return null;
            }

            // Skip the first point of every leg after the first — it's the
            // same position as the previous leg's last point (both are
            // "the road nearest to this hint"), so keeping it would create
            // a zero-length duplicate stop in the driven path.
            fullPath.AddRange(i == 0 ? leg : leg.GetRange(1, leg.Count - 1));
        }

        return fullPath;
    }

    /// <summary>Wraps a graph-backed Vector3 path (from BuildGraphBackedPath)
    /// into a List&lt;IRouteSegment&gt; — a straight line between each
    /// consecutive pair. Since those points are only ~waypointSpacing apart,
    /// this is a very close approximation of the real curve while reusing
    /// the actual graph-sampled positions (correct road-snapping AND
    /// elevation), and it lets every existing IRouteSegment consumer
    /// (Evaluate/Tangent/Length — FollowRoute's lookahead, event checks,
    /// etc.) keep working completely unchanged.</summary>
    public static List<IRouteSegment> ToStraightSegments(List<Vector3> path)
    {
        var segments = new List<IRouteSegment>();
        if (path == null || path.Count < 2) return segments;

        for (int i = 0; i < path.Count - 1; i++)
            segments.Add(new StraightSegment(path[i], path[i + 1]));

        return segments;
    }

    private static IRouteSegment MakeSegment(RouteNode a, RouteNode b)
    {
        return new StraightSegment(a.position, b.position);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DENSIFY CURVE RUN — adds influence points along the road's actual
    //  Bézier curve (same formulas as RoadSegment.EvaluatePosition, matched
    //  per point-count) so CatmullRomSegment has enough waypoints to hug the
    //  real curve instead of bowing through just the 3-4 raw control points.
    //  This does NOT step outside BuildSegments to call anything external —
    //  it's the same De Casteljau math RoadSegment already uses, inlined
    //  here so a route curve run traces the identical shape to the road it
    //  was taken from.
    // ═════════════════════════════════════════════════════════════════════════
    // Target spacing (in world units) between injected influence points.
    // Smaller = tighter trace, more points. This replaces a flat sample
    // count so a short tight curve and a long sweeping one both get enough
    // points to actually hug the real Bézier, instead of both getting the
    // same fixed number regardless of size.
    private const float DensifyTargetSpacing = 1.0f;
    private const int   DensifyMinSamples    = 12;
    private const int   DensifyMaxSamples    = 512;

    private static List<Vector3> DensifyCurveRun(List<Vector3> pts)
    {
        int n = pts.Count;

        // 2 points: straight — RoadSegment lerps here too, Catmull-Rom on
        // just 2 points is already a straight line. Nothing to add.
        if (n <= 2) return pts;

        // 3 points: matches RoadSegment's quadratic Bézier case exactly.
        if (n == 3)
        {
            int samples = SampleCountFor(pts[0], pts[1], pts[2]);
            return SampleQuadratic(pts[0], pts[1], pts[2], samples);
        }

        // 4 points: matches RoadSegment's cubic Bézier case exactly.
        if (n == 4)
        {
            int samples = SampleCountFor(pts[0], pts[1], pts[2], pts[3]);
            return SampleCubic(pts[0], pts[1], pts[2], pts[3], samples);
        }

        // 5+ points where (n-1) % 3 == 0: matches RoadSegment's chained
        // cubic Bézier case — densify each 4-point chain segment in turn,
        // each chunk sized to its own local span.
        if ((n - 1) % 3 == 0)
        {
            var result = new List<Vector3> { pts[0] };
            int segCount = (n - 1) / 3;
            for (int seg = 0; seg < segCount; seg++)
            {
                int i = seg * 3;
                int samples = SampleCountFor(pts[i], pts[i + 1], pts[i + 2], pts[i + 3]);
                var chunk = SampleCubic(pts[i], pts[i + 1], pts[i + 2], pts[i + 3], samples);
                // Skip chunk[0] — it's the same point as the previous
                // chunk's last sample (shared joint), avoid a duplicate.
                result.AddRange(chunk.GetRange(1, chunk.Count - 1));
            }
            return result;
        }

        // Any other point count already matches RoadSegment's own
        // Catmull-Rom fallback — pass through unchanged.
        return pts;
    }

    /// <summary>Picks a sample count from the control polygon's total edge
    /// length (a cheap upper bound on the real curve length) so short/tight
    /// curves and long/sweeping ones each get influence points spaced at
    /// roughly DensifyTargetSpacing world units, clamped to a sane range.</summary>
    private static int SampleCountFor(params Vector3[] controlPts)
    {
        float polygonLength = 0f;
        for (int i = 1; i < controlPts.Length; i++)
            polygonLength += Vector3.Distance(controlPts[i - 1], controlPts[i]);

        int samples = Mathf.CeilToInt(polygonLength / DensifyTargetSpacing);
        return Mathf.Clamp(samples, DensifyMinSamples, DensifyMaxSamples);
    }

    private static List<Vector3> SampleQuadratic(Vector3 p0, Vector3 p1, Vector3 p2, int samples)
    {
        var outPts = new List<Vector3>(samples + 1);
        for (int s = 0; s <= samples; s++)
        {
            float t = s / (float)samples;
            float u = 1f - t;
            outPts.Add(u * u * p0 + 2f * u * t * p1 + t * t * p2);
        }
        return outPts;
    }

    private static List<Vector3> SampleCubic(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, int samples)
    {
        var outPts = new List<Vector3>(samples + 1);
        for (int s = 0; s <= samples; s++)
        {
            float t = s / (float)samples;
            float u = 1f - t;
            outPts.Add(u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3);
        }
        return outPts;
    }

    public List<IRouteSegment> BuildSegments(List<RouteNode> nodes)
    {
        var segments = new List<IRouteSegment>();
        if (nodes == null || nodes.Count < 2) return segments;

        int i = 0;
        while (i < nodes.Count - 1)
        {
            if (nodes[i].isCurve)
            {
                int runStart = i;
                while (i < nodes.Count && nodes[i].isCurve) i++;
                int runEnd = i - 1;

                if (runEnd > runStart)
                {
                    var pts = new List<Vector3>();
                    for (int j = runStart; j <= runEnd; j++)
                        pts.Add(nodes[j].position);

                    // A raw run of 3 or 4 (or evenly-chained 5+) curve nodes
                    // matches RoadSegment's point count for quadratic/cubic
                    // Bézier, NOT its Catmull-Rom fallback. Feeding those raw
                    // points straight into CatmullRomSegment would trace a
                    // different curve than the road actually built from the
                    // same points (Catmull-Rom passes through every point;
                    // Bézier only passes through the first/last, using the
                    // middle ones as tangent handles). DensifyCurveRun adds
                    // extra influence points ALONG the true Bézier curve
                    // first, so the same Catmull-Rom segment then hugs the
                    // real road instead of bowing off it. 2-point runs and
                    // "other n" counts already match RoadSegment's own
                    // straight-line / Catmull-Rom fallback, so they pass
                    // through unchanged.
                    pts = DensifyCurveRun(pts);
                    segments.Add(new CatmullRomSegment(pts));
                }
                else
                {
                    if (runStart + 1 < nodes.Count)
                        segments.Add(MakeSegment(nodes[runStart], nodes[runStart + 1]));
                    i = runStart + 1;
                }
            }
            else
            {
                int straightStart = i;
                i++;
                while (i < nodes.Count && !nodes[i].isCurve)
                {
                    segments.Add(MakeSegment(nodes[i - 1], nodes[i]));
                    i++;
                }
                if (i < nodes.Count && nodes[i].isCurve && i - 1 != straightStart)
                {
                    // Handled structurally
                }
                else if (i < nodes.Count && straightStart == i - 1)
                {
                    segments.Add(MakeSegment(nodes[straightStart], nodes[i < nodes.Count ? i : nodes.Count - 1]));
                }
            }
        }
        return segments;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE COMPONENT CLASSES & INTERFACES
// ═══════════════════════════════════════════════════════════════════════════════
public interface IRouteSegment
{
    float   Length      { get; }
    Vector3 Evaluate(float t);          
    Vector3 Tangent(float t);
}

public class StraightSegment : IRouteSegment
{
    private readonly Vector3 _a, _b;
    public float Length => Vector3.Distance(_a, _b);

    public StraightSegment(Vector3 a, Vector3 b) { _a = a; _b = b; }

    public Vector3 Evaluate(float t) => Vector3.Lerp(_a, _b, Mathf.Clamp01(t));
    public Vector3 Tangent(float t)  => (_b - _a).normalized;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  CATMULL-ROM SEGMENT — same math as RoadSegment.cs's CatmullRomPosition/
//  CatmullRomTangent, so a route's curve-node run traces IDENTICALLY to how
//  a road built from the same control points would. This is what
//  BuildSegments now uses instead of BezierSegment (De Casteljau) for any
//  run of consecutive isCurve nodes — same algorithm as the roads, per
//  request, not just visually similar.
// ═══════════════════════════════════════════════════════════════════════════════
public class CatmullRomSegment : IRouteSegment
{
    private readonly List<Vector3> _pts;
    private float _length = -1f;

    public CatmullRomSegment(List<Vector3> points) { _pts = points; }

    public float Length
    {
        get
        {
            if (_length >= 0f) return _length;
            _length = 0f;
            Vector3 prev = Evaluate(0f);
            const int steps = 40;
            for (int i = 1; i <= steps; i++)
            {
                Vector3 next = Evaluate(i / (float)steps);
                _length += Vector3.Distance(prev, next);
                prev = next;
            }
            return _length;
        }
    }

    public Vector3 Evaluate(float t)
    {
        t = Mathf.Clamp01(t);
        int n = _pts.Count;
        float scaled = t * (n - 1);
        int   i      = Mathf.Min((int)scaled, n - 2);
        float localT = scaled - i;

        Vector3 p0 = _pts[Mathf.Max(i - 1, 0)];
        Vector3 p1 = _pts[i];
        Vector3 p2 = _pts[Mathf.Min(i + 1, n - 1)];
        Vector3 p3 = _pts[Mathf.Min(i + 2, n - 1)];

        float t2 = localT * localT, t3 = t2 * localT;
        return 0.5f * (
              (-t3 + 2f*t2 - localT)      * p0
            + (3f*t3 - 5f*t2 + 2f)        * p1
            + (-3f*t3 + 4f*t2 + localT)   * p2
            + (t3 - t2)                    * p3);
    }

    public Vector3 Tangent(float t)
    {
        t = Mathf.Clamp01(t);
        int n = _pts.Count;
        float scaled = t * (n - 1);
        int   i      = Mathf.Min((int)scaled, n - 2);
        float localT = scaled - i;

        Vector3 p0 = _pts[Mathf.Max(i - 1, 0)];
        Vector3 p1 = _pts[i];
        Vector3 p2 = _pts[Mathf.Min(i + 1, n - 1)];
        Vector3 p3 = _pts[Mathf.Min(i + 2, n - 1)];

        float t2 = localT * localT;
        Vector3 d = (-3f*t2 + 4f*localT - 1f) * p0 * 0.5f
                  + ( 9f*t2 - 10f*localT)      * p1 * 0.5f
                  + (-9f*t2 +  8f*localT + 1f) * p2 * 0.5f
                  + ( 3f*t2 -  2f*localT)      * p3 * 0.5f;
        return d == Vector3.zero ? Vector3.forward : d.normalized;
    }
}

public class BezierSegment : IRouteSegment
{
    private readonly List<Vector3> _pts;
    private float _length = -1f;

    public BezierSegment(List<Vector3> points) { _pts = points; }

    public float Length
    {
        get
        {
            if (_length >= 0f) return _length;
            _length = 0f;
            Vector3 prev = Evaluate(0f);
            const int steps = 40;
            for (int i = 1; i <= steps; i++)
            {
                Vector3 next = Evaluate(i / (float)steps);
                _length += Vector3.Distance(prev, next);
                prev = next;
            }
            return _length;
        }
    }

    public Vector3 Evaluate(float t)
    {
        t = Mathf.Clamp01(t);
        var work = new List<Vector3>(_pts);
        int n = work.Count;
        for (int r = 1; r < n; r++)
            for (int j = 0; j < n - r; j++)
                work[j] = Vector3.Lerp(work[j], work[j + 1], t);
        return work[0];
    }

    public Vector3 Tangent(float t)
    {
        float eps = 0.001f;
        Vector3 a  = Evaluate(Mathf.Clamp01(t - eps));
        Vector3 b  = Evaluate(Mathf.Clamp01(t + eps));
        Vector3 d  = b - a;
        return d == Vector3.zero ? Vector3.forward : d.normalized;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE SCHEDULE TEMPLATES — replace a route's MAIN schedule (scheduleWindows),
//  never touches variants. Overwrites whatever windows were there before.
// ═══════════════════════════════════════════════════════════════════════════════
public static class RouteScheduleTemplates
{
    
    /// <summary>24hr: 60min 00:00–04:00, 30min 04:00–06:00, 15min 06:00–22:00, 30min 22:00–24:00.</summary>
    public static void Apply24Hour(BusRouteData route)
    {
        route.scheduleWindows = new List<ScheduleWindow>
        {
            new ScheduleWindow { label = "Owl",       windowStartMinutes = 0,    windowEndMinutes = 240,  headwayFromAMinutes = 60, headwayFromZMinutes = 60 },
            new ScheduleWindow { label = "Early AM",   windowStartMinutes = 240,  windowEndMinutes = 360,  headwayFromAMinutes = 30, headwayFromZMinutes = 30 },
            new ScheduleWindow { label = "Daytime",    windowStartMinutes = 360,  windowEndMinutes = 1320, headwayFromAMinutes = 15, headwayFromZMinutes = 15 },
            new ScheduleWindow { label = "Late Night", windowStartMinutes = 1320, windowEndMinutes = 1439, headwayFromAMinutes = 30, headwayFromZMinutes = 30 },
        };
    }

    /// <summary>Non-24hr: 30min 04:30–06:00, 15min 06:00–22:00, 30min 22:00–23:30.</summary>
    public static void ApplyNon24Hour(BusRouteData route)
    {
        route.scheduleWindows = new List<ScheduleWindow>
        {
            new ScheduleWindow { label = "Early AM", windowStartMinutes = 270,  windowEndMinutes = 360,  headwayFromAMinutes = 30, headwayFromZMinutes = 30 },
            new ScheduleWindow { label = "Daytime",  windowStartMinutes = 360,  windowEndMinutes = 1320, headwayFromAMinutes = 15, headwayFromZMinutes = 15 },
            new ScheduleWindow { label = "Evening",  windowStartMinutes = 1320, windowEndMinutes = 1410, headwayFromAMinutes = 30, headwayFromZMinutes = 30 },
        };
    }

    /// <summary>Rush-hour cruiser: 15min 06:00–09:00 and 16:00–19:00 ONLY — no
    /// service at all outside those two windows.</summary>
    public static void ApplyRushHourCruiser(BusRouteData route)
    {
        route.scheduleWindows = new List<ScheduleWindow>
        {
            new ScheduleWindow { label = "AM Rush", windowStartMinutes = 360, windowEndMinutes = 540,  headwayFromAMinutes = 15, headwayFromZMinutes = 15 },
            new ScheduleWindow { label = "PM Rush", windowStartMinutes = 960, windowEndMinutes = 1140, headwayFromAMinutes = 15, headwayFromZMinutes = 15 },
        };
    }
    /// <summary>Normal Day (06:00 - 20:00): Traditional day service with standard peak/off-peak variations.</summary>
    public static void ApplyNon24Hour_6to8(this BusRouteData route)
    {
        route.scheduleWindows = new List<ScheduleWindow>
        {
            new ScheduleWindow { label = "AM Peak",     windowStartMinutes = 360,  windowEndMinutes = 540,  headwayFromAMinutes = 15, headwayFromZMinutes = 15 }, // 06:00 - 09:00
            new ScheduleWindow { label = "Midday Base", windowStartMinutes = 540,  windowEndMinutes = 960,  headwayFromAMinutes = 30, headwayFromZMinutes = 30 }, // 09:00 - 16:00
            new ScheduleWindow { label = "PM Peak",     windowStartMinutes = 960,  windowEndMinutes = 1140, headwayFromAMinutes = 15, headwayFromZMinutes = 15 }, // 16:00 - 19:00
            new ScheduleWindow { label = "Evening Tail",windowStartMinutes = 1140, windowEndMinutes = 1200, headwayFromAMinutes = 30, headwayFromZMinutes = 30 }, // 19:00 - 20:00
        };
        Debug.Log($"Applied Non-24hr (6am-8pm) template to Route {route.routeNumber}");
    }

    /// <summary>Extended Day (04:00 - 00:00 / Midnight): Full daily cover for major transit spines.</summary>
    public static void ApplyNon24Hour_4to12(this BusRouteData route)
    {
        route.scheduleWindows = new List<ScheduleWindow>
        {
            new ScheduleWindow { label = "Early Bird",  windowStartMinutes = 240,  windowEndMinutes = 360,  headwayFromAMinutes = 30, headwayFromZMinutes = 30 }, // 04:00 - 06:00
            new ScheduleWindow { label = "Day Base",    windowStartMinutes = 360,  windowEndMinutes = 1200, headwayFromAMinutes = 15, headwayFromZMinutes = 15 }, // 06:00 - 20:00
            new ScheduleWindow { label = "Late Night",  windowStartMinutes = 1200, windowEndMinutes = 1440, headwayFromAMinutes = 30, headwayFromZMinutes = 30 }, // 20:00 - 00:00
        };
        Debug.Log($"Applied Non-24hr (4am-12am) template to Route {route.routeNumber}");
    }

    /// <summary>Standard Night Owl (20:00 - 06:00 / 8pm-6am): Takes over when daytime routes shut down.</summary>
    public static void ApplyNightOwl_8to6(this BusRouteData route)
    {
        route.scheduleWindows = new List<ScheduleWindow>
        {
            new ScheduleWindow { label = "Night Shift", windowStartMinutes = 1200, windowEndMinutes = 1439, headwayFromAMinutes = 30, headwayFromZMinutes = 30 }, // 20:00 - 24:00
            new ScheduleWindow { label = "Owl Core",    windowStartMinutes = 0,    windowEndMinutes = 360,  headwayFromAMinutes = 60, headwayFromZMinutes = 60 }, // 00:00 - 06:00
        };
        Debug.Log($"Applied Night Owl (8pm-6am) template to Route {route.routeNumber}");
    }

    /// <summary>Night Owl Optimized (23:00 - 04:00 / 11pm-4am): Strict, high-efficiency midnight coverage window.</summary>
    public static void ApplyNightOwlOpt_11to4(this BusRouteData route)
    {
        route.scheduleWindows = new List<ScheduleWindow>
        {
            new ScheduleWindow { label = "Late Night Sync",windowStartMinutes = 1380, windowEndMinutes = 1439, headwayFromAMinutes = 30, headwayFromZMinutes = 30 }, // 23:00 - 24:00
            // Note: 4am translates to 240 minutes from midnight
            new ScheduleWindow { label = "Midnight Core",  windowStartMinutes = 0,    windowEndMinutes = 240,  headwayFromAMinutes = 45, headwayFromZMinutes = 60 }, // 00:00 - 04:00
        };
        Debug.Log($"Applied Night Owl Optimized (11pm-4am) template to Route {route.routeNumber}");
    }
}