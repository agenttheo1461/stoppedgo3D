using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ALERTS CENTER
//
//  The single modular aggregation point for "everything currently wrong with
//  service" -- route alerts (breakdowns/road events, via RouteServiceAlertMonitor)
//  and live events (via LiveEventManager), read by AlertsCenterWindow. Adding a
//  future third source (a strike calendar, a special-event schedule, whatever)
//  is one more foreach appended to GetAll() -- nothing that reads AlertEntry
//  needs to know or care where an entry came from.
// ═══════════════════════════════════════════════════════════════════════════════
public struct AlertEntry
{
    public string routeNumber;
    public string sourceLabel; // "Route Alert" or "Live Event" (or whatever a future source calls itself)
    public string message;
    public Color  bgColor;
}

public static class AlertsCenter
{
    public static readonly Color RouteAlertColor = new Color(0.95f, 0.85f, 0.10f, 0.96f); // yellow, matches the existing banner
    public static readonly Color LiveEventColor  = new Color(0.35f, 0.65f, 0.95f, 0.96f); // blue -- visually distinct from a breakdown/road-event banner

    public static List<AlertEntry> GetAll()
    {
        var list = new List<AlertEntry>();

        foreach (var a in RouteServiceAlertMonitor.GetAllActive())
        {
            list.Add(new AlertEntry
            {
                routeNumber  = a.routeNumber,
                sourceLabel  = "Route Alert",
                message      = a.message,
                bgColor      = RouteAlertColor,
            });
        }

        if (LiveEventManager.Instance != null)
        {
            foreach (var ev in LiveEventManager.Instance.GetActive())
            {
                list.Add(new AlertEntry
                {
                    routeNumber = ev.routeNumber,
                    sourceLabel = "Live Event",
                    message     = LiveEventManager.Instance.FormatMessage(ev),
                    bgColor     = LiveEventColor,
                });
            }
        }

        return list;
    }
}
