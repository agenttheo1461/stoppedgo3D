using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ROUTE HISTORY LOGGER
//
//  Records every completed trip (player AND NPC) into one JSON file:
//      <persistentDataPath>/route_history.json
//
//  {
//    "routes": [ { routeNumber, laps, playerLaps, npcLaps, totalTripMinutes, totalLateMinutes, lastDay } ],
//    "trips":  [ { day, route, variant, outbound, bus, fleet, player, scheduled, departed, arrived,
//                  tripMinutes, lateMinutes } ]      (newest last, capped at maxStoredTrips = 2^24)
//  }
//
//  Totals in "routes" are never trimmed; only the trip list is capped. Auto-creates itself, hooks
//  BusScheduler.OnSlotCompleted, saves every few seconds when dirty and on quit/pause.
// ═══════════════════════════════════════════════════════════════════════════════
public class RouteHistoryLogger : MonoBehaviour
{
    public static RouteHistoryLogger Instance { get; private set; }

    [Header("Storage")]
    public string fileName = "route_history.json";
    // [FIX] Was 1 << 24 (16.7M) -- high enough that RemoveRange below never
    // actually fired in a real session. Every completed trip (player AND
    // every NPC bus) got appended and kept forever, so the list grew
    // unbounded for as long as the session ran; Save() JSON-serializes and
    // synchronously writes the WHOLE list every saveIntervalSeconds, so that
    // write got slower and slower the longer play continued -- the "lag
    // after extended periods" symptom. 2000 trips is far more than enough
    // for the per-route totals (which are separately accumulated and never
    // trimmed) to stay meaningful, while keeping Save()'s payload bounded.
    public int    maxStoredTrips = 2000;
    public float  saveIntervalSeconds = 20f;

    [Serializable] public class RouteTotals
    {
        public string routeNumber;
        public int    laps;
        public int    playerLaps;
        public int    npcLaps;
        public float  totalTripMinutes;
        public float  totalLateMinutes;
        public int    lastDay;
    }

    [Serializable] public class TripRecord
    {
        public int    day;
        public string route;
        public string variant;
        public bool   outbound;
        public int    bus;
        public int    fleet;
        public bool   player;
        public float  scheduled;    // absolute game minutes
        public float  departed;     // absolute game minutes, -1 if unknown
        public float  arrived;      // absolute game minutes
        public float  tripMinutes;  // arrived - departed, -1 if unknown
        public float  lateMinutes;
    }

    [Serializable] private class HistoryFile
    {
        public List<RouteTotals> routes = new List<RouteTotals>();
        public List<TripRecord>  trips  = new List<TripRecord>();
    }

    private HistoryFile _data = new HistoryFile();
    private readonly Dictionary<string, RouteTotals> _byRoute = new Dictionary<string, RouteTotals>();
    private bool _dirty;
    private float _nextSave;
    private BusScheduler _hooked;

    private string FilePath => Path.Combine(Application.persistentDataPath, fileName);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("RouteHistoryLogger").AddComponent<RouteHistoryLogger>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Load();
    }

    private void Update()
    {
        // The scheduler can be created after us / recreated on scene load: (re)hook whenever it changes.
        var sched = BusScheduler.Instance;
        if (sched != _hooked)
        {
            if (_hooked != null) _hooked.OnSlotCompleted -= OnSlotCompleted;
            _hooked = sched;
            if (_hooked != null) _hooked.OnSlotCompleted += OnSlotCompleted;
        }

        if (_dirty && Time.unscaledTime >= _nextSave) Save();
    }

    private void OnSlotCompleted(TimetableSlot slot, bool isPlayer, int busID)
    {
        if (slot == null || string.IsNullOrEmpty(slot.routeNumber)) return;

        float now = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : -1f;
        int fleet = -1;
        var rec = BusManager.Instance != null ? BusManager.Instance.GetRecord(busID) : null;
        if (rec != null && rec.controller != null) fleet = rec.controller.fleetNumber;

        float departed = slot.actualDeparture;
        float tripMin = (departed >= 0f && now >= departed) ? now - departed : -1f;

        var t = new TripRecord
        {
            day = slot.dayNumber, route = slot.routeNumber, variant = slot.variantLetter ?? "",
            outbound = slot.isOutbound, bus = busID, fleet = fleet, player = isPlayer,
            scheduled = slot.scheduledDeparture, departed = departed, arrived = now,
            tripMinutes = tripMin, lateMinutes = slot.latenessMinutes,
        };
        _data.trips.Add(t);
        if (_data.trips.Count > maxStoredTrips) _data.trips.RemoveRange(0, _data.trips.Count - maxStoredTrips);

        string key = slot.routeNumber;
        if (!_byRoute.TryGetValue(key, out var tot))
        {
            tot = new RouteTotals { routeNumber = key };
            _byRoute[key] = tot; _data.routes.Add(tot);
        }
        tot.laps++;
        if (isPlayer) tot.playerLaps++; else tot.npcLaps++;
        if (tripMin >= 0f) tot.totalTripMinutes += tripMin;
        tot.totalLateMinutes += slot.latenessMinutes;
        tot.lastDay = slot.dayNumber;

        _dirty = true;
        if (_nextSave < Time.unscaledTime) _nextSave = Time.unscaledTime + saveIntervalSeconds;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _data = JsonUtility.FromJson<HistoryFile>(File.ReadAllText(FilePath)) ?? new HistoryFile();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[RouteHistoryLogger] Could not read {FilePath}: {e.Message}. Starting fresh (old file kept as .bad).");
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
            _data = new HistoryFile();
        }
        _byRoute.Clear();
        foreach (var r in _data.routes) if (r != null && !string.IsNullOrEmpty(r.routeNumber)) _byRoute[r.routeNumber] = r;
    }

    public void Save()
    {
        try
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(_data));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
            _dirty = false;
            _nextSave = Time.unscaledTime + saveIntervalSeconds;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[RouteHistoryLogger] Save failed: {e.Message}");
            _nextSave = Time.unscaledTime + saveIntervalSeconds;
        }
    }

    private void OnApplicationPause(bool paused) { if (paused && _dirty) Save(); }
    private void OnApplicationQuit()             { if (_dirty) Save(); }

    private void OnDestroy()
    {
        if (_hooked != null) _hooked.OnSlotCompleted -= OnSlotCompleted;
        if (Instance == this) Instance = null;
    }
}
