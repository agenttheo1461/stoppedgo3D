using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  STARRED BUS AVAILABILITY NOTIFIER
//
//  Every checkIntervalRealMinutes (real-world time), looks at every starred
//  fleet number (BusRosterWindow) that's currently idle, and -- if any are --
//  picks ONE at random to nudge the player about. Deliberately not
//  exhaustive/guaranteed: a bus being starred and idle doesn't mean it WILL
//  get picked this cycle, which is why starring several improves your odds
//  rather than reserving any specific one (see BusRosterWindow's own header).
//  Fires through both NotificationToast (foreground) and
//  PlatformNotifications (real OS push, so it still reaches you backgrounded
//  on Android/iOS/macOS), with the bus's own pre-rendered series icon.
// ═══════════════════════════════════════════════════════════════════════════════
public class StarredBusAvailabilityNotifier : MonoBehaviour
{
    public static StarredBusAvailabilityNotifier Instance { get; private set; }

    public float checkIntervalRealMinutes = 15f;
    private float _nextCheckRealtime;

    private static readonly string[] MessageTemplates =
    {
        "#{0} is available!",
        "#{0} needs a driver!",
    };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("StarredBusAvailabilityNotifier").AddComponent<StarredBusAvailabilityNotifier>();
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
        if (StarredBusData.Instance == null || BusManager.Instance == null) return;

        var candidates = new List<(int fleetNumber, FleetMetadata.Entry meta)>();
        foreach (var fleetNumber in StarredBusData.Instance.Starred)
        {
            var rec = BusManager.Instance.GetAllRecords().FirstOrDefault(r => r?.controller != null && r.controller.fleetNumber == fleetNumber);
            if (rec == null || !rec.isIdle) continue;
            candidates.Add((fleetNumber, FleetMetadata.Get(fleetNumber)));
        }
        if (candidates.Count == 0) return;

        var pick = candidates[Random.Range(0, candidates.Count)];
        string template = MessageTemplates[Random.Range(0, MessageTemplates.Length)];
        string message = string.Format(template, pick.fleetNumber);

        Texture2D icon = null;
        if (pick.meta != null)
        {
            if (!string.IsNullOrEmpty(pick.meta.seriesName)) icon = Resources.Load<Texture2D>("SeriesIcons/" + pick.meta.seriesName);
            if (icon == null && !string.IsNullOrEmpty(pick.meta.busType)) icon = Resources.Load<Texture2D>("SeriesIcons/" + pick.meta.busType);
        }

        NotificationToast.Show("Starred Bus", message, icon);
        PlatformNotifications.ScheduleAt($"starred_{pick.fleetNumber}_{Time.realtimeSinceStartup}", "Starred Bus", message, System.DateTime.Now.AddSeconds(2));
    }
}
