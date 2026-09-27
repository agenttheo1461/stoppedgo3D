using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROAD EVENT REGISTRY  v2
//
//  CHANGES vs v1
//  ─────────────
//  · GetActiveEventForBusLookahead now returns the severity alongside the event
//    so NPCBusController can decide whether to divert or just slow down.
//  · Minor severity events never trigger a diversion — buses slow through.
//  · AppliesToRoute check deduplicated (was checked twice in v1).
//  · RefreshEvents is called by RoadEventSpawner on activate/clear so the
//    registry always reflects dynamic state without polling.
// ═══════════════════════════════════════════════════════════════════════════════
public class RoadEventRegistry : MonoBehaviour
{
    public static RoadEventRegistry Instance { get; private set; }

    private List<RoadEvent> _events = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start() => RefreshEvents();

    public void RefreshEvents()
    {
        _events = new List<RoadEvent>(FindObjectsOfType<RoadEvent>());
        Debug.Log($"[RoadEventRegistry] Refreshed — {_events.Count} road event(s) known.");
    }

    // ── Primary query used by NPCBusController ────────────────────────────────
    /// <summary>
    /// Returns the first active road event visible to this bus given its
    /// lookahead projection, or null if none.
    ///
    /// For MINOR severity the bus should slow down but NOT divert.
    /// Check ev.severity after this call; if Minor, use GetSpeedCapFraction()
    /// and skip BeginDetour.
    /// </summary>
    public RoadEvent GetActiveEventForBusLookahead(
        Vector3 busPos,
        Vector3 travelDir,
        float   currentSpeedKph,
        int     busID,
        bool    isOutbound,
        string  routeNumber,
        float   lookaheadSeconds)
    {
        float   speedMs       = currentSpeedKph / 3.6f;
        float   lookaheadDist = Mathf.Max(0f, speedMs * lookaheadSeconds);
        Vector3 lookaheadPos  = busPos + travelDir.normalized * lookaheadDist;

        foreach (var ev in _events)
        {
            if (ev == null || !ev.isActive)              continue;
            if (!ev.AppliesToRoute(routeNumber))         continue;
            if (ev.IsBusDiverted(busID))                 continue;

            bool currentInRange   = ev.IsInRange(busPos);
            bool lookaheadInRange = ev.IsInRange(lookaheadPos);
            if (!currentInRange && !lookaheadInRange)    continue;

            // For minor events: only flag if a diversion isn't needed
            // (caller checks severity and decides what to do)
            // For moderate/major: require a detour to be configured
            if (ev.severity != RoadEventSeverity.Minor)
            {
                var detour = ev.GetDetourPositions(isOutbound, busPos.y);
                if (detour == null || detour.Count == 0) continue;
            }

            return ev;
        }

        return null;
    }

    /// <summary>
    /// Simple range check without lookahead — used by the minor-severity
    /// slow-through logic in NPCBusController.
    /// </summary>
    public RoadEvent GetActiveEventInRange(Vector3 busPos, int busID,
                                            bool isOutbound, string routeNumber)
    {
        foreach (var ev in _events)
        {
            if (ev == null || !ev.isActive)      continue;
            if (!ev.AppliesToRoute(routeNumber)) continue;
            if (ev.IsBusDiverted(busID))         continue;
            if (!ev.IsInRange(busPos))           continue;
            return ev;
        }
        return null;
    }

    // ── Stop / route awareness (tracker, timetable, PlayerHandoff, scheduler) ────
    /// <summary>True if any active event closes this stop for the route + direction.</summary>
    public bool IsStopClosed(string stopCode, string routeNumber, bool outbound)
    {
        foreach (var ev in _events)
            if (ev != null && ev.ClosesStop(stopCode, routeNumber, outbound)) return true;
        return false;
    }

    /// <summary>The active event closing this stop for the route + direction, or null.</summary>
    public RoadEvent GetClosingEvent(string stopCode, string routeNumber, bool outbound)
    {
        foreach (var ev in _events)
            if (ev != null && ev.ClosesStop(stopCode, routeNumber, outbound)) return ev;
        return null;
    }

    /// <summary>True if any active, non-minor event applies to this route (any direction).</summary>
    public bool RouteHasActiveDisruption(string routeNumber)
    {
        foreach (var ev in _events)
            if (ev != null && ev.isActive && ev.severity != RoadEventSeverity.Minor && ev.AppliesToRoute(routeNumber))
                return true;
        return false;
    }

    /// <summary>Total extra minutes active diversions add to a trip on this route + direction.</summary>
    public float GetExtraMinutes(string routeNumber, bool outbound)
    {
        float total = 0f;
        foreach (var ev in _events)
        {
            if (ev == null || !ev.isActive || ev.severity == RoadEventSeverity.Minor) continue;
            if (!ev.AppliesToRoute(routeNumber)) continue;
            var d = ev.GetDetourPositions(outbound);
            if (d == null || d.Count == 0) continue;       // no detour authored for this direction -> no diversion
            total += Mathf.Max(0f, ev.detourExtraMinutes);
        }
        return total;
    }

    /// <summary>Every stop code closed by any active event for this route + direction.</summary>
    public List<string> GetClosedStopCodes(string routeNumber, bool outbound, IEnumerable<string> routeStopCodes)
    {
        var result = new List<string>();
        if (routeStopCodes == null) return result;
        foreach (var code in routeStopCodes)
            if (IsStopClosed(code, routeNumber, outbound)) result.Add(code);
        return result;
    }

    public List<RoadEvent> GetAllActiveEvents()
    {
        var result = new List<RoadEvent>();
        foreach (var ev in _events)
            if (ev != null && ev.isActive) result.Add(ev);
        return result;
    }
}