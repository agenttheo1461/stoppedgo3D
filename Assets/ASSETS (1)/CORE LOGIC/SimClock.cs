using System;
using UnityEngine;

/// <summary>
/// v2.0 — Single authoritative time source. Game time is a pure function
/// of real UTC time: 12:00 AM UTC = 12:00 AM GTST, 12:00 PM UTC = end of
/// 1 full GTST day (24 game-hours in 12 real-hours = fixed 2x multiplier).
/// No session state, no drift, no GameTimeScale dependency.
///
/// Fires OnGameDayRolled whenever GameDayNumber changes so the shift
/// generator (and anything else) can regenerate exactly on schedule —
/// twice per real day, once at each 12-hour UTC boundary.
/// </summary>
public class SimClock : MonoBehaviour
{
    public static SimClock Instance { get; private set; }
    public const float TIME_MULTIPLIER = 2f;

    private static readonly DateTime Epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public event Action<int> OnGameDayRolled;

    private int _lastKnownDay = -1;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        _lastKnownDay = GameDayNumber;
    }

    private void Update()
    {
        int currentDay = GameDayNumber;
        if (currentDay != _lastKnownDay)
        {
            _lastKnownDay = currentDay;
            OnGameDayRolled?.Invoke(currentDay);
        }
    }

    /// <summary>Game-minutes since GTST midnight, 0-1440, derived fresh
    /// from UTC every call. No drift, no offline catch-up math needed.</summary>
    public float GameTimeMinutes
    {
        get
        {
            double secondsSinceUtcMidnight = DateTime.UtcNow.TimeOfDay.TotalSeconds;
            double gameMinutesSinceMidnight = (secondsSinceUtcMidnight / 60.0) * TIME_MULTIPLIER;
            return (float)(gameMinutesSinceMidnight % 1440.0);
        }
    }

    /// <summary>GTST day number, purely derived from a fixed epoch so
    /// it's stable across sessions and clients. Increments twice per
    /// real day due to the 2x multiplier.</summary>
    public int GameDayNumber
    {
        get
        {
            double totalRealDays = (DateTime.UtcNow - Epoch).TotalDays;
            return (int)(totalRealDays * TIME_MULTIPLIER);
        }
    }

    /// <summary>Absolute game-minutes matching TimetableSlot.scheduledDeparture's
    /// scale (dayNumber * 1440 + minute-of-day) — NOT the same as
    /// GameTimeMinutes, which wraps every 1440. Comparing scheduledDeparture
    /// against raw GameTimeMinutes was the root cause of buses spawning at
    /// the end of their route: a day-2 slot (e.g. 2880+) minus a wrapped
    /// 0-1440 clock value produces a huge, meaningless "elapsed" number.
    /// Always use THIS for any comparison against scheduledDeparture.</summary>
    public float AbsoluteGameMinutes => GameDayNumber * 1440f + GameTimeMinutes;

    public string GameTimeString
    {
        get
        {
            float m = GameTimeMinutes;
            int h = Mathf.FloorToInt(m / 60f) % 24;
            int mi = Mathf.FloorToInt(m % 60f);
            return $"{h:D2}:{mi:D2}";
        }
    }

    /// <summary>Real elapsed seconds since this SimClock object was created
    /// this session — display-only, not used for any game-time math.</summary>
    private float _sessionRealTime;
    private void LateUpdate() => _sessionRealTime += Time.deltaTime;

    public float RealTimeElapsed => _sessionRealTime;

    public string RealTimeString
    {
        get
        {
            int t = Mathf.FloorToInt(_sessionRealTime);
            return $"{t / 3600:D2}:{(t % 3600) / 60:D2}:{t % 60:D2}";
        }
    }
}