using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  LIVE EVENT MANAGER
//
//  Owns every LiveServiceEvent: persistence (JsonUtility, same file-based
//  pattern as FavoriteRouteData/StarredBusData), the scheduler multiplier
//  hook (BusScheduler.EmitWindowDirection calls GetServiceMultiplier),
//  message formatting (shared by the console, RouteRosterWindow-style UI,
//  and AlertsCenterWindow), and the notification tie-in ("tell me Route 199
//  has reduced service from now until next week").
//
//  Untested in Unity.
// ═══════════════════════════════════════════════════════════════════════════════
public class LiveEventManager : MonoBehaviour
{
    public static LiveEventManager Instance { get; private set; }

    [Serializable] private class EventFile { public List<LiveServiceEvent> events = new List<LiveServiceEvent>(); }

    public string fileName = "live_events.json";
    private EventFile _data = new EventFile();

    [Tooltip("How often (real seconds) to check for events that just ended, for the end-of-event toast.")]
    public float endCheckIntervalRealSeconds = 60f;
    private float _nextEndCheckRealtime;
    private readonly HashSet<string> _endedNotified = new HashSet<string>();

    private string FilePath => Path.Combine(Application.persistentDataPath, fileName);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("LiveEventManager").AddComponent<LiveEventManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Load();
        _nextEndCheckRealtime = Time.realtimeSinceStartup + endCheckIntervalRealSeconds;
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    private void Update()
    {
        if (Time.realtimeSinceStartup < _nextEndCheckRealtime) return;
        _nextEndCheckRealtime = Time.realtimeSinceStartup + endCheckIntervalRealSeconds;
        CheckForJustEnded();
    }

    // ── CRUD ───────────────────────────────────────────────────────────────
    public LiveServiceEvent CreateEvent(string routeNumber, LiveEventEffect effect, float days, float fraction,
                                         string label, string detourLocationText, Vector2 detourWorldPos, bool hasDetourWorldPos,
                                         bool outboundAffected = true, bool inboundAffected = true, List<string> skippedStopCodes = null)
    {
        var now = DateTime.UtcNow;
        var ev = new LiveServiceEvent
        {
            routeNumber        = routeNumber,
            effect             = effect,
            fraction           = Mathf.Clamp01(fraction),
            label              = label ?? "",
            detourLocationText = detourLocationText ?? "",
            detourWorldPos     = detourWorldPos,
            hasDetourWorldPos  = hasDetourWorldPos,
            skippedStopCodes   = skippedStopCodes ?? new List<string>(),
            outboundAffected   = outboundAffected,
            inboundAffected    = inboundAffected,
            startUtcTicks      = now.Ticks,
            endUtcTicks        = now.AddDays(Mathf.Max(0.01f, days)).Ticks,
        };
        _data.events.Add(ev);
        Save();

        NotificationToast.Show($"Route {routeNumber} — live event", FormatMessage(ev));
        return ev;
    }

    /// <summary>Activates a reusable LiveServiceEventTemplate asset by its asset name (case-insensitive) --
    /// looked up from Resources/LiveEventTemplates/, the same "author once in the Project window, trigger
    /// as many times as you want" idea FavoriteRouteData-style JSON records don't give you. daysOverride
    /// &lt;= 0 uses the template's own defaultDays.</summary>
    public LiveServiceEvent ActivateTemplate(string templateName, float daysOverride = -1f)
    {
        var templates = Resources.LoadAll<LiveServiceEventTemplate>("LiveEventTemplates");
        foreach (var t in templates)
            if (t != null && string.Equals(t.name, templateName, StringComparison.OrdinalIgnoreCase))
                return t.Activate(daysOverride);
        return null;
    }

    // ── Console dispatch ─────────────────────────────────────────────────────
    //  liveevent add <route> <reduced|noservice|additional|modified|detour> <days> [fraction] [label/location]
    //  liveevent template <templateAssetName> [days]
    //  liveevent list
    //  liveevent end <id-prefix>   (id-prefix is the short id shown by 'list')
    //
    //  All the parsing/creation logic lives here rather than in whatever console called it -- a live
    //  event is authored content (route + effect + duration), not a player-driving action, so it
    //  shouldn't live inside a player-bus class. The caller only needs to hand over the raw command
    //  parts plus (optionally) the player's current world position, for "detour ... auto".
    public (bool ok, string message) HandleConsoleCommand(string[] parts, Vector3? playerWorldPos)
    {
        if (parts.Length < 2) return (false, "Usage: liveevent add|template|list|end ...");
        string sub = parts[1].ToLower();

        if (sub == "list")
        {
            var active = GetActive();
            if (active.Count == 0) return (true, "No live events active.");
            return (true, string.Join("\n", active.Select(ev => $"[{ev.id.Substring(0, 8)}] {FormatMessage(ev)}")));
        }

        if (sub == "end")
        {
            if (parts.Length < 3) return (false, "Usage: liveevent end <id>");
            bool removed = EndEvent(parts[2]);
            return removed
                ? (true, "Live event ended.")
                : (false, "No matching live event found (use the short id from 'liveevent list').");
        }

        if (sub == "template")
        {
            if (parts.Length < 3) return (false, "Usage: liveevent template <templateAssetName> [days]");
            float daysOverride = -1f;
            if (parts.Length >= 4 && float.TryParse(parts[3], out float d)) daysOverride = d;
            var ev = ActivateTemplate(parts[2], daysOverride);
            return ev != null
                ? (true, $"Live event created from template '{parts[2]}': {FormatMessage(ev)}")
                : (false, $"No template asset named '{parts[2]}' found in Resources/LiveEventTemplates/ (or LiveEventManager isn't ready).");
        }

        if (sub == "add")
        {
            if (parts.Length < 5)
                return (false, "Usage: liveevent add <route> <reduced|noservice|additional|modified|detour> <days> [fraction] [label/location]");

            string route = parts[2];
            string effectStr = parts[3].ToLower();
            if (!float.TryParse(parts[4], out float days)) return (false, "Days must be a number, e.g. 7");

            LiveEventEffect effect;
            switch (effectStr)
            {
                case "reduced":                      effect = LiveEventEffect.ReducedService; break;
                case "noservice": case "suspended":  effect = LiveEventEffect.NoService; break;
                case "additional": case "extra":     effect = LiveEventEffect.AdditionalService; break;
                case "modified":                     effect = LiveEventEffect.ModifiedService; break;
                case "detour":                       effect = LiveEventEffect.Detour; break;
                default:
                    return (false, $"Unknown effect '{effectStr}'. Use reduced, noservice, additional, modified, or detour.");
            }

            string rest = parts.Length > 5 ? string.Join(" ", parts, 5, parts.Length - 5) : "";
            float fraction = 0.5f;
            if ((effect == LiveEventEffect.ReducedService || effect == LiveEventEffect.AdditionalService) && rest.Length > 0)
            {
                var restParts = rest.Split(new[] { ' ' }, 2);
                if (float.TryParse(restParts[0], out float parsedFraction))
                {
                    fraction = Mathf.Clamp01(parsedFraction);
                    rest = restParts.Length > 1 ? restParts[1] : "";
                }
            }

            string detourLocationText = "";
            Vector2 detourWorldPos = Vector2.zero;
            bool hasDetourWorldPos = false;
            if (effect == LiveEventEffect.Detour)
            {
                if (string.IsNullOrEmpty(rest) || rest.Trim().ToLower() == "auto")
                {
                    if (playerWorldPos.HasValue)
                    {
                        detourWorldPos = new Vector2(playerWorldPos.Value.x, playerWorldPos.Value.z);
                        hasDetourWorldPos = true;
                    }
                    rest = "";
                }
                else
                {
                    detourLocationText = rest;
                    rest = "";
                }
            }

            var newEvent = CreateEvent(route, effect, days, fraction, rest, detourLocationText, detourWorldPos, hasDetourWorldPos);
            return (true, $"Live event created: {FormatMessage(newEvent)}");
        }

        return (false, "Usage: liveevent add|template|list|end ...");
    }

    public bool EndEvent(string idPrefix)
    {
        var ev = _data.events.FirstOrDefault(e => e.id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase));
        if (ev == null) return false;
        _data.events.Remove(ev);
        Save();
        return true;
    }

    /// <summary>Every event currently in its real-time window, right now.</summary>
    public List<LiveServiceEvent> GetActive()
    {
        var now = DateTime.UtcNow;
        return _data.events.Where(e => e.IsActiveAt(now)).ToList();
    }

    public List<LiveServiceEvent> GetActiveForRoute(string routeNumber)
    {
        var now = DateTime.UtcNow;
        return _data.events.Where(e => e.routeNumber == routeNumber && e.IsActiveAt(now)).ToList();
    }

    // ── Scheduler hook ───────────────────────────────────────────────────────
    /// <summary>Combined multiplier for this route+direction+day from every active ReducedService/
    /// NoService/AdditionalService event covering it. 0 = suspended, &lt;1 = thinner, &gt;1 = denser,
    /// 1 = no active effect. ModifiedService/Detour never touch this -- they're alert-only.</summary>
    public float GetServiceMultiplier(string routeNumber, bool outbound, int dayNumber)
    {
        float multiplier = 1f;
        foreach (var ev in _data.events)
        {
            if (ev.routeNumber != routeNumber) continue;
            if (outbound && !ev.outboundAffected) continue;
            if (!outbound && !ev.inboundAffected) continue;
            if (ev.effect != LiveEventEffect.ReducedService && ev.effect != LiveEventEffect.NoService && ev.effect != LiveEventEffect.AdditionalService) continue;
            if (!ev.OverlapsGameDay(dayNumber)) continue;

            multiplier *= EffectMultiplier(ev);
        }
        return multiplier;
    }

    private static float EffectMultiplier(LiveServiceEvent ev) => ev.effect switch
    {
        LiveEventEffect.NoService         => 0f,
        LiveEventEffect.ReducedService    => Mathf.Clamp01(1f - ev.fraction),
        LiveEventEffect.AdditionalService => 1f + Mathf.Max(0f, ev.fraction),
        _                                 => 1f,
    };

    // ── Notifications ─────────────────────────────────────────────────────
    private void CheckForJustEnded()
    {
        var now = DateTime.UtcNow;
        foreach (var ev in _data.events)
        {
            if (ev.IsActiveAt(now) || now < ev.EndUtc) continue; // still active, or hasn't started/ended yet
            if (!_endedNotified.Add(ev.id)) continue; // already notified
            NotificationToast.Show($"Route {ev.routeNumber} — live event ended", $"{DescribeEffect(ev.effect)} on Route {ev.routeNumber} is back to normal.");
        }
    }

    private static string DescribeEffect(LiveEventEffect effect) => effect switch
    {
        LiveEventEffect.ReducedService    => "Reduced service",
        LiveEventEffect.NoService         => "Service suspension",
        LiveEventEffect.AdditionalService => "Additional service",
        LiveEventEffect.ModifiedService   => "Modified service",
        LiveEventEffect.Detour            => "The detour",
        _                                 => "The event",
    };

    // ── Message formatting (shared by console, AlertsCenterWindow) ────────
    public string FormatMessage(LiveServiceEvent ev)
    {
        string dirWord = ev.outboundAffected && ev.inboundAffected ? "Outbound and inbound"
            : ev.outboundAffected ? "Outbound" : "Inbound";
        string untilLocal = ev.EndUtc.ToLocalTime().ToString("ddd, MMM d");
        string labelSuffix = string.IsNullOrEmpty(ev.label) ? "" : $" ({ev.label})";

        switch (ev.effect)
        {
            case LiveEventEffect.Detour:
            {
                string where = !string.IsNullOrEmpty(ev.detourLocationText) ? ev.detourLocationText : ResolveStreetName(ev);
                string msg = $"{dirWord} buses on Route {ev.routeNumber} detoured at {where}.";
                if (ev.skippedStopCodes != null && ev.skippedStopCodes.Count > 0)
                    msg += $" Skipped stops: {ResolveStopNames(ev.skippedStopCodes)}.";
                return msg + labelSuffix;
            }
            case LiveEventEffect.NoService:
                return $"{dirWord} Route {ev.routeNumber} service suspended until {untilLocal}.{labelSuffix}";
            case LiveEventEffect.ReducedService:
                return $"{dirWord} Route {ev.routeNumber} running reduced service until {untilLocal}.{labelSuffix}";
            case LiveEventEffect.AdditionalService:
                return $"{dirWord} Route {ev.routeNumber} running additional service until {untilLocal}.{labelSuffix}";
            case LiveEventEffect.ModifiedService:
                return $"{dirWord} Route {ev.routeNumber} service modified until {untilLocal}.{labelSuffix}";
            default:
                return $"Route {ev.routeNumber}: live event.{labelSuffix}";
        }
    }

    private static string ResolveStreetName(LiveServiceEvent ev)
    {
        if (!ev.hasDetourWorldPos) return "an unspecified location";
        if (!string.IsNullOrEmpty(ev.cachedDetourStreetName)) return ev.cachedDetourStreetName;

        var graph = CityManager.Instance != null ? CityManager.Instance.Graph : null;
        if (graph != null && graph.FindNearestEdgePoint(new Vector3(ev.detourWorldPos.x, 0f, ev.detourWorldPos.y), out var edge, out _)
            && edge.segment != null && !string.IsNullOrEmpty(edge.segment.roadName))
            ev.cachedDetourStreetName = edge.segment.roadName;
        else
            ev.cachedDetourStreetName = "an unspecified location";
        return ev.cachedDetourStreetName;
    }

    /// <summary>Shared by LiveServiceEvent (Detour) and RouteServiceAlert (RoadEvent skip lists) --
    /// resolves stop codes to their real names where CityManager knows them, falling back to the
    /// raw code otherwise.</summary>
    public static string ResolveStopNames(List<string> stopCodes)
    {
        if (stopCodes == null || stopCodes.Count == 0) return "";
        var names = new List<string>(stopCodes.Count);
        foreach (var code in stopCodes)
        {
            var stop = CityManager.Instance != null ? CityManager.Instance.GetStop(code) : null;
            names.Add(stop != null && !string.IsNullOrEmpty(stop.stopName) ? stop.stopName : code);
        }
        return string.Join(", ", names);
    }

    // ── Persistence ────────────────────────────────────────────────────────
    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _data = JsonUtility.FromJson<EventFile>(File.ReadAllText(FilePath)) ?? new EventFile();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[LiveEventManager] Could not read {FilePath}: {e.Message}. Starting with no live events.");
            _data = new EventFile();
        }
        if (_data.events == null) _data.events = new List<LiveServiceEvent>();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonUtility.ToJson(_data, true)); }
        catch (Exception e) { Debug.LogWarning($"[LiveEventManager] Save failed: {e.Message}"); }
    }
}
