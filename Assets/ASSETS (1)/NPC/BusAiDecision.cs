using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(NPCBusController))]
public class BusAIDecisionCore : MonoBehaviour
{
    [Header("Learning")]
    public float learningRate = 0.015f;
    [Range(0.05f, 0.5f)] public float maxDrift = 0.22f;

    [Header("Chatter")]
    public bool  enableChatter   = true;
    public float chatterCooldown = 14f;       // ↑ was 6 — fewer, more meaningful lines
    public float chatterRadius   = 100f;

    private NPCBusController _bus;
    private BusAIBrain       _brain;

    private float _skillScore = 0.5f;
    private float _driftAggr, _driftSpeed, _driftBrake;
    private DriverPersonality _baseSnapshot;

    private float _prevLateness;
    private float _blockedStreak;
    private float _lastRewardTick;
    private const float REWARD_TICK_INTERVAL = 4f;

    // ── Chatter — rolled ONCE per cooldown window instead of every frame ────
    private float _chatterTimer;
    private float _chatterRollTimer; // separate, slower roll clock — fixes "arguing over nothing"
    private const float CHATTER_ROLL_INTERVAL = 5f; // only consider speaking every 5s, not every frame

    private static readonly List<BusAIDecisionCore> _allCores = new();
    public static event Action<string> OnChatterLine;

    // ── Line banks — picked randomly so it's not the same sentence every time ──
    private static readonly string[] _stuckLinesGeneric =
    {
        "this traffic isn't moving.",
        "stuck behind someone again.",
        "come on, let's go.",
        "anyone else backed up out here?",
        "not loving this gap.",
    };
    private static readonly string[] _stuckLinesStubborn =
    {
        "MOVE IT.",
        "I don't have all day.",
        "seriously, come on.",
    };
    private static readonly string[] _stuckLinesCautious =
    {
        "no rush, we'll get there.",
        "just taking it easy back here.",
    };
    private static readonly string[] _lateLinesGeneric =
    {
        "running behind schedule.",
        "gotta make up some time.",
        "yeah, I know I'm late.",
        "trying to catch back up.",
    };
    private static readonly string[] _lateLinesExpress =
    {
        "pushing hard to get back on time.",
        "no stops missed, just gotta move.",
    };
    private static readonly string[] _goodRunLines =
    {
        "smooth run today.",
        "right on schedule.",
        "good day out here so far.",
        "nice and easy run.",
    };
    private static readonly string[] _greetingLines =
    {
        "how's your side of the route looking?",
        "anything going on out your way?",
        "all clear over there?",
        "how's traffic where you're at?",
    };
    private static readonly string[] _honkReactGeneric =
    {
        "alright, alright, I hear you.",
        "yeah yeah, I'm going.",
        "relax back there.",
    };
    private static readonly string[] _honkReactStubborn =
    {
        "real mature, lean on the horn.",
        "honking won't make traffic move faster.",
    };
    private static readonly string[] _greetingReplyLines =
    {
        "hey there!",
        "good to see you out here.",
        "stay safe out there.",
    };
    private static readonly string[] _earlyBirdLines =
    {
        "already ahead of schedule, as usual.",
        "early is on time, on time is late.",
        "no point sitting around at the terminal.",
    };
    private static readonly string[] _rookieLines =
    {
        "still figuring out this route, bear with me.",
        "was that my stop back there?",
        "dispatch, uh, which lane am I supposed to be in?",
        "first week on this run.",
    };
    private static readonly string[] _veteranLines =
    {
        "twenty years on this route, seen it all.",
        "right on the timepoint, like always.",
        "you learn every pothole eventually.",
    };

    private void Awake()
    {
        _bus   = GetComponent<NPCBusController>();
        _brain = GetComponent<BusAIBrain>();
        if (_bus == null) { enabled = false; return; }
    }

    private void Start()
    {
        _baseSnapshot = new DriverPersonality
        {
            archetype       = _bus.personality.archetype,
            speedMultiplier = _bus.personality.speedMultiplier,
            dwellMultiplier = _bus.personality.dwellMultiplier,
            aggressiveness  = _bus.personality.aggressiveness,
            brakingBias     = _bus.personality.brakingBias,
            speedVariance   = _bus.personality.speedVariance,
        };
        _allCores.Add(this);
        _chatterRollTimer = UnityEngine.Random.Range(0f, CHATTER_ROLL_INTERVAL); // desync buses
    }

    private void OnDestroy() => _allCores.Remove(this);

    // ── Dynamic label — always current bus ID + current route, no caching ────
    private string Label()
    {
        string route = _bus.CurrentRoute != null ? _bus.CurrentRoute.routeNumber : "—";
        return $"Bus #{_bus.busID} (Route {route})";
    }

    private void Update()
    {
        if (_bus == null || _bus.State == NPCBusController.BusState.Idle) return;
        float dt = Time.deltaTime;

        UpdateRewardLoop(dt);
        ApplyLearnedDrift(dt);

        if (enableChatter)
        {
            _chatterTimer -= dt;
            _chatterRollTimer -= dt;
            if (_chatterRollTimer <= 0f)
            {
                _chatterRollTimer = CHATTER_ROLL_INTERVAL;
                EvaluateChatterTriggers(); // only ever rolled here — once per 5s, not per frame
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  REWARD / PUNISHMENT  (unchanged logic, smarter thresholds below feed off it)
    // ══════════════════════════════════════════════════════════════════════
    private void UpdateRewardLoop(float dt)
    {
        bool blockedNow = _bus.IsBlockedByBus || _bus.IsBlockedByLight;
        _blockedStreak = blockedNow ? _blockedStreak + dt : Mathf.Max(0f, _blockedStreak - dt * 2f);

        _lastRewardTick += dt;
        if (_lastRewardTick < REWARD_TICK_INTERVAL) return;
        _lastRewardTick = 0f;

        float lateness = BusScheduler.Instance?.GetLatenessMinutes(_bus.busID) ?? 0f;
        float latenessDelta = _prevLateness - lateness;
        _prevLateness = lateness;

        float reward = 0f;
        reward += Mathf.Clamp(latenessDelta, -1f, 1f) * 0.5f;
        reward -= Mathf.Clamp01(lateness / 10f) * 0.3f;
        reward -= Mathf.Clamp01(_blockedStreak / 10f) * 0.4f;
        reward += blockedNow ? 0f : 0.15f;

        _skillScore = Mathf.Clamp01(_skillScore + reward * learningRate);
    }

    private void ApplyLearnedDrift(float dt)
    {
        float confidence = (_skillScore - 0.5f) * 2f;

        float targetAggrDrift  = confidence * maxDrift * 0.6f;
        float targetSpeedDrift = confidence * maxDrift * 0.4f;
        float targetBrakeDrift = -confidence * maxDrift * 0.5f;

        _driftAggr  = Mathf.MoveTowards(_driftAggr,  targetAggrDrift,  dt * 0.05f);
        _driftSpeed = Mathf.MoveTowards(_driftSpeed, targetSpeedDrift, dt * 0.05f);
        _driftBrake = Mathf.MoveTowards(_driftBrake, targetBrakeDrift, dt * 0.05f);

        var p = _bus.personality;
        p.aggressiveness  = Mathf.Clamp01(p.aggressiveness + _driftAggr * dt * 0.1f);
        p.speedMultiplier = Mathf.Clamp(p.speedMultiplier + _driftSpeed * dt * 0.1f,
                                         _baseSnapshot.speedMultiplier - maxDrift,
                                         _baseSnapshot.speedMultiplier + maxDrift);
        p.brakingBias = Mathf.Clamp01(p.brakingBias + _driftBrake * dt * 0.1f);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CHATTER — smarter triggers, more variety
    //
    //  Fixes vs old version:
    //  • Rolled once per CHATTER_ROLL_INTERVAL (5s), not every frame — old
    //    code re-rolled a 1-2% chance every single frame once cooldown hit
    //    zero, which at 60fps meant effectively guaranteed firing within a
    //    second or two of any threshold being crossed. That's the "arguing
    //    over nothing."
    //  • Thresholds raised and now require SUSTAINED conditions (streak
    //    must hold across multiple rolls), not just an instantaneous dip.
    //  • Each category picks a random line from a bank instead of always
    //    saying the same sentence.
    // ══════════════════════════════════════════════════════════════════════
    private float _stuckRollStreak = 0f; // how many consecutive rolls we've been stuck
    private float _lateRollStreak  = 0f;

    private void EvaluateChatterTriggers()
    {
        if (_chatterTimer > 0f) return;
        var arch = _bus.personality.archetype;

        bool stillBlocked = _blockedStreak > 6f;
        _stuckRollStreak = stillBlocked ? _stuckRollStreak + 1f : 0f;

        float lateness = BusScheduler.Instance?.GetLatenessMinutes(_bus.busID) ?? 0f;
        bool stillLate = lateness > 7f;
        _lateRollStreak = stillLate ? _lateRollStreak + 1f : 0f;

        // Stuck for 2+ consecutive rolls (≥10s sustained, not a blip) and a real dice roll
        if (_stuckRollStreak >= 2f && UnityEngine.Random.value < 0.30f)
        {
            string[] pool = arch switch
            {
                DriverArchetype.Stubborn => _stuckLinesStubborn,
                DriverArchetype.Cautious => _stuckLinesCautious,
                DriverArchetype.Rookie   => _rookieLines,
                _                        => _stuckLinesGeneric,
            };
            Say($"\"{Pick(pool)}\"");
            return;
        }

        // Late for 2+ consecutive rolls
        if (_lateRollStreak >= 2f && UnityEngine.Random.value < 0.25f)
        {
            string[] pool = arch == DriverArchetype.Express ? _lateLinesExpress : _lateLinesGeneric;
            Say($"\"{Pick(pool)}\"");
            return;
        }

        // Doing great — rarer, celebratory; flavored per archetype
        if (lateness < 0.5f && _skillScore > 0.78f && UnityEngine.Random.value < 0.12f)
        {
            string[] pool = arch switch
            {
                DriverArchetype.EarlyBird => _earlyBirdLines,
                DriverArchetype.Veteran   => _veteranLines,
                _                         => _goodRunLines,
            };
            Say($"\"{Pick(pool)}\"");
            return;
        }

        // Same-route opposite-direction greeting — needs an actual nearby match
        foreach (var other in _allCores)
        {
            if (other == this || other._bus == null) continue;
            if (other._bus.CurrentRoute == null || _bus.CurrentRoute == null) continue;
            if (other._bus.CurrentRoute.routeNumber != _bus.CurrentRoute.routeNumber) continue;
            if (other._bus.IsOutbound == _bus.IsOutbound) continue;

            float dist = Vector3.Distance(transform.position, other.transform.position);
            if (dist > chatterRadius) continue;

            if (UnityEngine.Random.value < 0.10f)
            {
                // [FIX M1] This Debug.Log was unconditional — one BusAIDecisionCore
                // per NPC bus, firing every ~14-20s indefinitely for the whole
                // session, scaling with fleet size. Gated behind _bus.verboseLogging
                // now; OnChatterLine still fires unconditionally since it feeds UI.
                string line = Pick(_greetingLines);
                if (_bus.verboseLogging) Debug.Log($"[Chatter] {Label()} → {other.Label()}: \"{line}\"");
                OnChatterLine?.Invoke($"{Label()} → {other.Label()}: {line}");
                _chatterTimer = chatterCooldown + UnityEngine.Random.Range(0f, 4f);
            }
            return;
        }
    }

    private string Pick(string[] pool) => pool[UnityEngine.Random.Range(0, pool.Length)];

    private void Say(string line)
    {
        _chatterTimer = chatterCooldown + UnityEngine.Random.Range(0f, 6f);
        string full = $"{Label()}: {line}";
        // [FIX M1] Same unconditional-log issue as the greeting branch above —
        // gated behind _bus.verboseLogging; OnChatterLine still fires unconditionally.
        if (_bus.verboseLogging) Debug.Log($"[Chatter] {full}");
        OnChatterLine?.Invoke(full);
    }

    public void ReactToHonkReceived(HonkType type)
    {
        if (!enableChatter || _chatterTimer > 0f) return;
        if (UnityEngine.Random.value > 0.10f) return; // was 0.15 — tone it down further

        var arch = _bus.personality.archetype;
        if (type == HonkType.LongBlast)
        {
            string[] pool = arch == DriverArchetype.Stubborn ? _honkReactStubborn : _honkReactGeneric;
            Say($"\"{Pick(pool)}\"");
        }
        else if (type == HonkType.Greeting)
        {
            Say($"\"{Pick(_greetingReplyLines)}\"");
        }
    }

    public string GetDebugReport() =>
        $"[{Label()} | {_bus.personality.archetype}] skill={_skillScore:0.00} " +
        $"driftAggr={_driftAggr:+0.00;-0.00} driftSpd={_driftSpeed:+0.00;-0.00} " +
        $"driftBrk={_driftBrake:+0.00;-0.00} blockedStreak={_blockedStreak:0.0}s";
}