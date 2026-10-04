using System;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  LIVE SERVICE EVENT
//
//  A calendar-scoped service change on a route -- "Route 199 reduced service
//  for a week", "Route 25 outbound detoured at NW 27th Ave" -- completely
//  separate from RoadEvent (which is spatial/short-lived and drives NPC
//  diversion physically). This is authored (console command for now, see
//  PlayerHandoff.HandleLiveEventCommand), stored by REAL start/end time
//  (DateTime.UtcNow, as ticks -- JsonUtility can't serialize DateTime
//  directly), and converted to the game's AbsoluteGameMinutes scale on demand
//  via SimClock.RealLocalTimeToAbsoluteGameMinutes. Because GameDayNumber is
//  a pure function of a fixed epoch + real UTC time (see SimClock), an event
//  authored with a real end date stays correct across app restarts with zero
//  extra bookkeeping -- there's nothing to "resume," the math just works.
//
//  Deliberately just data + one enum. Everything else (persistence, the
//  scheduler hook, message formatting, notifications) lives in
//  LiveEventManager so this stays a clean, modular unit new effect types can
//  be added onto without touching unrelated systems -- exactly the "hella
//  modular" requirement this was built to satisfy.
// ═══════════════════════════════════════════════════════════════════════════════
public enum LiveEventEffect
{
    ReducedService,     // scheduler emits fewer slots for the affected direction(s)/days
    NoService,          // scheduler emits zero slots -- full suspension
    AdditionalService,  // scheduler emits more slots (shorter headway)
    ModifiedService,    // alert-only for now -- no scheduling change, just rider-facing text
    Detour,             // alert-only -- "slight" detour announcement, no physical reroute
}

[Serializable]
public class LiveServiceEvent
{
    public string id = Guid.NewGuid().ToString();
    public string routeNumber;
    public LiveEventEffect effect;

    [Tooltip("ReducedService: fraction of service CUT (0.5 = half as many buses). AdditionalService: fraction ADDED (0.5 = 50% more buses). Unused by NoService/ModifiedService/Detour.")]
    [Range(0f, 1f)] public float fraction = 0.5f;

    public bool outboundAffected = true;
    public bool inboundAffected  = true;

    [Tooltip("Free-text cause/description shown in parentheses after the alert, e.g. \"storm cleanup\".")]
    public string label = "";

    // ── Detour-only ────────────────────────────────────────────────────────
    [Tooltip("Typed by hand -- wins over detourWorldPos if non-empty. \"or i can just type it myself.\"")]
    public string detourLocationText = "";
    public Vector2 detourWorldPos;
    public bool    hasDetourWorldPos;
    public List<string> skippedStopCodes = new List<string>();

    // ── Real-time span (UTC ticks -- JsonUtility-safe DateTime) ────────────
    public long startUtcTicks;
    public long endUtcTicks;

    [NonSerialized] public string cachedDetourStreetName; // resolved lazily, never persisted

    public DateTime StartUtc => new DateTime(startUtcTicks, DateTimeKind.Utc);
    public DateTime EndUtc   => new DateTime(endUtcTicks, DateTimeKind.Utc);

    public bool IsActiveAt(DateTime utcNow) => utcNow >= StartUtc && utcNow < EndUtc;

    /// <summary>Whether this event overlaps the given game-day at all, for the scheduler's
    /// per-day slot generation -- dayNumber's own minute range converted back to real time
    /// via the same epoch math everything else in SimClock uses.</summary>
    public bool OverlapsGameDay(int dayNumber)
    {
        float dayStartAbs = dayNumber * 1440f;
        float dayEndAbs   = dayStartAbs + 1440f;
        float evStartAbs  = SimClock.RealLocalTimeToAbsoluteGameMinutes(StartUtc);
        float evEndAbs    = SimClock.RealLocalTimeToAbsoluteGameMinutes(EndUtc);
        return evEndAbs > dayStartAbs && evStartAbs < dayEndAbs;
    }
}
