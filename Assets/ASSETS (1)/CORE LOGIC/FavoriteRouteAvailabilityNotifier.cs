using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  FAVORITE ROUTE AVAILABILITY NOTIFIER
//
//  Every checkIntervalRealMinutes (real-world time), looks at every favorited
//  route (FavoriteRouteData) that currently has an upcoming departure with at
//  least one idle eligible bus, and -- if any do -- picks ONE at random to
//  nudge the player about. Same "not exhaustive/guaranteed" framing as
//  StarredBusAvailabilityNotifier: favoriting several routes improves your
//  odds of a relevant nudge rather than reserving any specific one.
// ═══════════════════════════════════════════════════════════════════════════════
public class FavoriteRouteAvailabilityNotifier : MonoBehaviour
{
    public static FavoriteRouteAvailabilityNotifier Instance { get; private set; }

    public float checkIntervalRealMinutes = 15f;
    [Tooltip("A departure counts as \"available\" only if it's within this many game-minutes.")]
    public float lookaheadGameMinutes = 45f;
    private float _nextCheckRealtime;

    private static readonly string[] MessageTemplates =
    {
        "Route {0} is available!",
        "Route {0} needs a driver!",
    };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("FavoriteRouteAvailabilityNotifier").AddComponent<FavoriteRouteAvailabilityNotifier>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        _nextCheckRealtime = Time.realtimeSinceStartup + checkIntervalRealMinutes * 60f;
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    private void Update()
    {
        if (Time.realtimeSinceStartup < _nextCheckRealtime) return;
        _nextCheckRealtime = Time.realtimeSinceStartup + checkIntervalRealMinutes * 60f;
        CheckOnce();
    }

    private void CheckOnce()
    {
        if (!SettingsData.NotifyFavoriteRoutes) return;
        if (FavoriteRouteData.Instance == null || BusScheduler.Instance == null || SimClock.Instance == null) return;
        // Player's already got the menu open looking at exactly this -- don't also fire a toast over it.
        if (MainMenu.Instance != null && MainMenu.Instance.IsOpen) return;

        float now = SimClock.Instance.AbsoluteGameMinutes;
        int day = SimClock.Instance.GameDayNumber;
        float lead = BusScheduler.BoardingLeadFor(now);

        var candidates = new List<string>();
        foreach (var routeNumber in FavoriteRouteData.Instance.Favorites)
        {
            var route = BusScheduler.Instance.managedRoutes?.FirstOrDefault(r => r != null && r.routeNumber == routeNumber);
            if (route == null) continue;

            var upcoming = BusScheduler.Instance.GetTodaysBusesForRoute(routeNumber, day)
                .Where(e => e.scheduledDeparture >= now + lead && e.scheduledDeparture <= now + lookaheadGameMinutes)
                .OrderBy(e => e.scheduledDeparture)
                .FirstOrDefault();
            if (upcoming.scheduledDeparture <= 0f) continue;
            if (CountIdleEligible(routeNumber) <= 0) continue;

            candidates.Add(routeNumber);
        }
        if (candidates.Count == 0) return;

        string pick = candidates[Random.Range(0, candidates.Count)];
        string template = MessageTemplates[Random.Range(0, MessageTemplates.Length)];
        string message = string.Format(template, pick);

        NotificationToast.Show("Favorite Route", message, null);
        PlatformNotifications.ScheduleAt($"favroute_{pick}_{Time.realtimeSinceStartup}", "Favorite Route", message, System.DateTime.Now.AddSeconds(2));
    }

    private static int CountIdleEligible(string routeNumber)
    {
        if (BusManager.Instance == null || DepotManager.Instance == null) return 0;
        int count = 0;
        foreach (var record in BusManager.Instance.GetAllRecords())
        {
            if (record == null || record.controller == null || !record.isIdle) continue;
            if (!DepotManager.Instance.CanServeRoute(record.controller.fleetNumber, routeNumber)) continue;
            count++;
        }
        return count;
    }
}
