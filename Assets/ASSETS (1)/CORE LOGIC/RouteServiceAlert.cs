using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE SERVICE ALERT
//
//  A rider-facing banner for a route + direction -- yellow background, black
//  text, shown on the live map and/or tracker -- posted automatically when a
//  bus breaks down (player OR NPC) or a road event affects a route. Expires on
//  its own once it's been up for 2x the estimated delay, same as a real
//  transit agency's temporary service-alert banner.
//
//  Auto-creates itself and subscribes to BusBreakdownSystem.OnBreakdownTriggered
//  and RoadEvent.Activated -- nothing else needs to call into this directly to
//  get an alert posted; DrawServiceAlertBanners(...) is the one thing UI code
//  (MDT_LiveMap, TrackerUI) calls to render whatever's currently active for a
//  given route.
// ═══════════════════════════════════════════════════════════════════════════════
public class RouteServiceAlert
{
    public string routeNumber;
    public bool   outbound;      // true = "Outbound", false = "Inbound"
    public string message;       // full rider-facing line, e.g. "...due to a mechanical malfunction. Approximate delay: 8 min."
    public float  postedAtAbsMin;
    public float  expiresAtAbsMin;

    public bool IsExpired(float nowAbsMin) => nowAbsMin >= expiresAtAbsMin;
}

public class RouteServiceAlertMonitor : MonoBehaviour
{
    public static RouteServiceAlertMonitor Instance { get; private set; }

    private static readonly List<RouteServiceAlert> _alerts = new List<RouteServiceAlert>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("RouteServiceAlertMonitor").AddComponent<RouteServiceAlertMonitor>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        BusBreakdownSystem.OnBreakdownTriggered += OnBreakdownTriggered;
        RoadEvent.Activated += OnRoadEventActivated;
    }

    private void OnDestroy()
    {
        BusBreakdownSystem.OnBreakdownTriggered -= OnBreakdownTriggered;
        RoadEvent.Activated -= OnRoadEventActivated;
        if (Instance == this) Instance = null;
    }

    // ── Posting ───────────────────────────────────────────────────────────────
    /// <summary>Every route number/direction gets at most one active alert at a time -- a fresh one
    /// (new breakdown, or a road event re-checked) replaces rather than stacks on top of an old one.</summary>
    public static void Post(string routeNumber, bool outbound, string message, float delayMinutes)
    {
        if (string.IsNullOrEmpty(routeNumber) || SimClock.Instance == null) return;
        float now = SimClock.Instance.AbsoluteGameMinutes;
        float duration = Mathf.Max(2f, delayMinutes * 2f); // "after 2x the time of the next bus, it disappears"

        _alerts.RemoveAll(a => a.routeNumber == routeNumber && a.outbound == outbound);
        _alerts.Add(new RouteServiceAlert
        {
            routeNumber = routeNumber,
            outbound = outbound,
            message = message,
            postedAtAbsMin = now,
            expiresAtAbsMin = now + duration,
        });
    }

    /// <summary>All currently-active alerts for a route (both directions) -- for UI that groups by route.</summary>
    public static List<RouteServiceAlert> GetActiveForRoute(string routeNumber)
    {
        Prune();
        return _alerts.Where(a => a.routeNumber == routeNumber).ToList();
    }

    /// <summary>Every currently-active alert network-wide -- for a live-map-style ticker.</summary>
    public static List<RouteServiceAlert> GetAllActive()
    {
        Prune();
        return new List<RouteServiceAlert>(_alerts);
    }

    private static void Prune()
    {
        if (SimClock.Instance == null) return;
        float now = SimClock.Instance.AbsoluteGameMinutes;
        _alerts.RemoveAll(a => a.IsExpired(now));
    }

    // ── Breakdown hook (player AND NPC -- OnBreakdownTriggered fires for both) ──
    private void OnBreakdownTriggered(int busID, BreakdownType type, bool requiresReplacement)
    {
        if (BusScheduler.Instance == null || SimClock.Instance == null) return;

        var slot = BusScheduler.Instance.GetCurrentSlotForBus(busID);
        if (slot == null) return; // not on a real route right now -- nothing to announce

        float now = SimClock.Instance.AbsoluteGameMinutes;
        float delay = MinutesUntilNextDeparture(slot.routeNumber, slot.isOutbound, now);
        if (delay <= 0f) return; // no known next departure to estimate against -- nothing useful to say

        string dir = slot.isOutbound ? "Outbound" : "Inbound";
        string message = $"{dir} buses running behind schedule on Route {slot.routeNumber} due to a mechanical malfunction. " +
                          $"Approximate delay: {Mathf.CeilToInt(delay)} min.";
        Post(slot.routeNumber, slot.isOutbound, message, delay);
    }

    // ── Road event hook ──────────────────────────────────────────────────────
    private void OnRoadEventActivated(RoadEvent ev)
    {
        if (ev == null || ev.severity == RoadEventSeverity.Minor) return; // minor = slow-through, not a real service impact
        if (BusScheduler.Instance == null || SimClock.Instance == null || RoadEventRegistry.Instance == null) return;

        float now = SimClock.Instance.AbsoluteGameMinutes;
        foreach (var route in BusScheduler.Instance.managedRoutes)
        {
            if (route == null || !ev.AppliesToRoute(route.routeNumber)) continue;

            foreach (bool outbound in new[] { true, false })
            {
                var detour = ev.GetDetourPositions(outbound);
                if (detour == null || detour.Count == 0) continue; // no diversion configured for this direction -- doesn't affect it

                float delay = RoadEventRegistry.Instance.GetExtraMinutes(route.routeNumber, outbound);
                if (delay <= 0f) delay = MinutesUntilNextDeparture(route.routeNumber, outbound, now);
                if (delay <= 0f) continue;

                string dir = outbound ? "Outbound" : "Inbound";
                string message = $"{dir} buses running behind schedule on Route {route.routeNumber} due to {ReasonPhrase(ev.eventType)}. " +
                                  $"Approximate delay: {Mathf.CeilToInt(delay)} min.";

                var skipped = ev.GetSkippedStopCodes(outbound);
                if (skipped != null && skipped.Count > 0)
                    message += $" Skipped stops: {LiveEventManager.ResolveStopNames(skipped)}.";

                Post(route.routeNumber, outbound, message, delay);
            }
        }
    }

    /// <summary>Rider-facing reason phrase per event type -- distinct from RoadEvent.GetDriverConsoleMessage's
    /// driver-facing wording, same plain-English register as the breakdown message above.</summary>
    private static string ReasonPhrase(RoadEventType type) => type switch
    {
        RoadEventType.TrafficCrash   => "a traffic crash",
        RoadEventType.WaterMainBreak => "a water main break",
        RoadEventType.RoadWork       => "road work",
        RoadEventType.PowerLinDown   => "a downed power line",
        RoadEventType.FloodingRoad   => "flooding",
        RoadEventType.FireIncident   => "a fire incident",
        RoadEventType.PoliceActivity => "police activity",
        RoadEventType.DebrisOnRoad   => "debris on the roadway",
        _                            => "a road condition",
    };

    private static float MinutesUntilNextDeparture(string routeNumber, bool outbound, float nowAbsMin)
    {
        if (BusScheduler.Instance == null) return 0f;
        var next = BusScheduler.Instance.AllSlots
            .Where(s => s.routeNumber == routeNumber && s.isOutbound == outbound
                        && s.state != SlotState.Completed && s.scheduledDeparture >= nowAbsMin)
            .OrderBy(s => s.scheduledDeparture)
            .FirstOrDefault();
        return next != null ? next.scheduledDeparture - nowAbsMin : 0f;
    }

    // ── Shared rendering (yellow bg / black text) ────────────────────────────
    /// <summary>Draws every active alert for a route, stacked, at the given top-left. Returns the total
    /// height used so the caller can lay out whatever comes after it. w = full available width.</summary>
    public static float DrawForRoute(string routeNumber, float x, float y, float w)
    {
        var alerts = GetActiveForRoute(routeNumber);
        float usedH = 0f;
        var bg   = new Color(0.95f, 0.85f, 0.10f, 0.96f);
        var text = new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Bold, wordWrap = true };
        text.normal.textColor = Color.black;

        foreach (var a in alerts)
        {
            float h = 30f;
            var r = new Rect(x, y + usedH, w, h);
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, false, 0, bg, 0, 0);
            GUI.Label(new Rect(r.x + 4, r.y + 2, r.width - 8, r.height - 4), a.message, text);
            usedH += h + 2f;
        }
        return usedH;
    }
}
