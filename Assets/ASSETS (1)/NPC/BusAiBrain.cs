using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusAIBrain.cs  v2   —   per-bus mini AI + social honk system
//
//  WHAT'S IN HERE
//  ──────────────
//  Mood Engine          frustration / impatience / alertness evolve every frame.
//
//  Live Personality     BrainUpdate() mutates _bus.personality fields that
//                       FollowRoute already reads (speedMultiplier, dwellMultiplier,
//                       aggressiveness, brakingBias).  Zero changes to FollowRoute
//                       needed — the existing code just naturally behaves differently
//                       because personality is different.
//
//  Micro-Events         Slacker distraction dips, SpeedRacer departure punches,
//                       Stubborn dwell-cutting, Cautious early-creep inhibit.
//                       Each fires probabilistically, smoothly, once per trigger.
//
//  Decision Layer       Honk decisions gated by (archetype × mood × situation).
//
//  Social Bus           Static event — any brain can broadcast a honk, every
//                       brain within socialRadius decides whether to react.
//
//  Exchange Cap         Back-and-forth honking maxes at 2 volleys then cools.
//
//  Greeting Pass        Same-route, opposite-direction buses toot each other.
//
//  BusNPCHonk DSP       Spatial three-tone air horn per sample.
//                       Manual distance attenuation in OnAudioFilterRead
//                       (bypasses Unity spatialization bug with synth audio).
//                       Fleet-series pitch table: 2000s gritty low → 2300s bright.
//                       Per-bus ±8 Hz variance — no two buses identical.
//
//  NO NPCBusController CHANGES REQUIRED — drop component and go.
// ═══════════════════════════════════════════════════════════════════════════════

public enum HonkType
{
    ShortBeep,        // polite single tap
    DoubleBeep,       // two quick blasts — "come on"
    LongBlast,        // full lean — maximum frustration
    Greeting,         // friendly pass-toot, same route opposite dir
    AcknowledgeHonk   // honking back at someone who honked at you
}

// ══════════════════════════════════════════════════════════════════════════════
public class BusAIBrain : MonoBehaviour
{
    // ── Live mood — watch these in the inspector during play ───────────────
    [Header("Live Mood  (read-only in play)")]
    [Range(0f, 1f)] public float frustration;
    [Range(0f, 1f)] public float impatience;
    [Range(0f, 1f)] public float alertness;

    [Header("AI Config")]
    public float frustrationBuildRate = 0.09f;
    public float frustrationDecayRate = 0.18f;
    [Tooltip("Metres — buses within this sphere can hear and react to honks.")]
    public float socialRadius         = 80f;
    [Tooltip("Seconds between expensive social-scan OverlapSphere calls.")]
    public float socialScanInterval   = 2.5f;

    [Header("Honk Volume")]
    [Range(0f, 1f)] public float honkVolume = 0.80f;

    // ── Refs ───────────────────────────────────────────────────────────────
    private NPCBusController _bus;
    private BusNPCHonk       _honk;

    // ── Personality baseline (snapshot from Awake, restored on destroy) ────
    private struct PersonalityBaseline
    {
        public float speedMult, dwellMult, aggressiveness, brakingBias, speedVariance;
    }
    private PersonalityBaseline _base;

    // ── Mood state ─────────────────────────────────────────────────────────
    private float _blockedTime    = 0f;
    private float _latenessMins   = 0f;
    private float _honkCooldown   = 0f;
    private float _socialTimer    = 0f;
    private int   _exchangeCount  = 0;
    private float _exchangeTimer  = 0f;

    // ── Micro-event state ──────────────────────────────────────────────────
    private float _microEventTimer   = 0f;   // time until next micro-event roll
    private float _microSpeedPenalty = 0f;   // Slacker distraction: active speed dip
    private float _microSpeedTimer   = 0f;   // how long the dip lasts
    private bool  _punchoutActive    = false; // SpeedRacer/Express post-stop burst
    private float _punchoutTimer     = 0f;

    // ── Social scan ────────────────────────────────────────────────────────
    private readonly List<BusAIBrain> _nearbyBrains  = new(12);
    private readonly Collider[]       _socialBuf      = new Collider[32];
    private readonly HashSet<int>     _greetedBusIDs  = new();
    private NPCBusController.BusState _prevState;

    // ── Static event ───────────────────────────────────────────────────────
    public static event Action<BusAIBrain, Vector3, HonkType> OnHonk;

    private static readonly List<BusAIBrain> _allBrains = new();
    public  static IReadOnlyList<BusAIBrain> AllBrains  => _allBrains;

    // ══════════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ══════════════════════════════════════════════════════════════════════
    private void Awake()
    {
        _bus = GetComponent<NPCBusController>();
        if (_bus == null) { enabled = false; return; }

        // Snapshot personality so we can drift from it and come back
        var p = _bus.personality;
        _base = new PersonalityBaseline
        {
            speedMult     = p.speedMultiplier,
            dwellMult     = p.dwellMultiplier,
            aggressiveness= p.aggressiveness,
            brakingBias   = p.brakingBias,
            speedVariance = p.speedVariance
        };

        BuildHonkDSP();
        _allBrains.Add(this);
        OnHonk += HandleRemoteHonk;

        // Stagger first micro-event roll so all buses don't fire at t=0
        _microEventTimer = UnityEngine.Random.Range(3f, 12f);
    }

    private void OnDestroy()
    {
        // Restore personality to baseline when brain is removed
        if (_bus != null)
        {
            var p = _bus.personality;
            p.speedMultiplier = _base.speedMult;
            p.dwellMultiplier = _base.dwellMult;
            p.aggressiveness  = _base.aggressiveness;
            p.brakingBias     = _base.brakingBias;
            p.speedVariance   = _base.speedVariance;
        }
        _allBrains.Remove(this);
        OnHonk -= HandleRemoteHonk;
    }

    private void BuildHonkDSP()
    {
        var go = new GameObject("NPCHonk");
        go.transform.SetParent(transform, false);
        _honk = go.AddComponent<BusNPCHonk>();
        _honk.Init(_bus.busID, _bus.fleetNumber, honkVolume, transform);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  UPDATE
    // ══════════════════════════════════════════════════════════════════════
    private void Update()
    {
        if (_bus == null || _bus.State == NPCBusController.BusState.Idle) return;

        float dt = Time.deltaTime;
        UpdateMood(dt);
        UpdatePersonality(dt);
        UpdateMicroEvents(dt);
        UpdateCooldowns(dt);
        MakeDecisions();
        UpdateSocialScan(dt);
        DetectStopDeparture();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  MOOD ENGINE
    // ══════════════════════════════════════════════════════════════════════
    private void UpdateMood(float dt)
    {
        bool dwellState = _bus.State == NPCBusController.BusState.AtStop ||
                          _bus.State == NPCBusController.BusState.AtTerminal;

        bool rollingBlocked = _bus.spd < 1.2f && !dwellState;

        if (rollingBlocked)
        {
            _blockedTime += dt;
            frustration   = Mathf.MoveTowards(frustration, 1f,
                                frustrationBuildRate * FrustrationMult() * dt);
        }
        else
        {
            _blockedTime = 0f;
            float decayMult = dwellState ? 2.5f : 1f; // venting frustration at a stop
            frustration = Mathf.MoveTowards(frustration, 0f,
                              frustrationDecayRate * decayMult * dt);
        }

        _latenessMins = BusScheduler.Instance?.GetLatenessMinutes(_bus.busID) ?? 0f;
        float wantImpatience = Mathf.Clamp01(_latenessMins / 12f);
        impatience = Mathf.MoveTowards(impatience, wantImpatience, 0.05f * dt);

        alertness = Mathf.MoveTowards(alertness, 0f, 0.20f * dt);
    }

    private float FrustrationMult() => _bus.personality.archetype switch
    {
        DriverArchetype.Stubborn   => 2.2f,
        DriverArchetype.SpeedRacer => 2.0f,
        DriverArchetype.Express    => 1.5f,
        DriverArchetype.Normal     => 1.0f,
        DriverArchetype.Cautious   => 0.5f,
        DriverArchetype.Slacker    => 0.35f,
        _                          => 1.0f
    };

    // ══════════════════════════════════════════════════════════════════════
    //  LIVE PERSONALITY MUTATION
    //  Writes directly into _bus.personality so that FollowRoute, StopDwell,
    //  and braking code all pick up the mood-driven values automatically.
    //  All values are smoothed so changes feel organic, not stepped.
    // ══════════════════════════════════════════════════════════════════════
    private void UpdatePersonality(float dt)
    {
        var p    = _bus.personality;
        var arch = p.archetype;
        float fr = frustration;
        float im = impatience;
        float al = alertness;

        // ── Speed multiplier ───────────────────────────────────────────────
        float targetSpeed = _base.speedMult;
        switch (arch)
        {
            case DriverArchetype.SpeedRacer:
                // Gets faster with frustration AND impatience — always pushing
                targetSpeed = _base.speedMult * (1f + fr * 0.28f + im * 0.22f);
                break;
            case DriverArchetype.Express:
                // Lateness makes Express push harder; frustration barely adds
                targetSpeed = _base.speedMult * (1f + im * 0.20f + fr * 0.08f);
                break;
            case DriverArchetype.Slacker:
                // Micro-event adds a penalty (distraction) — see UpdateMicroEvents
                targetSpeed = _base.speedMult * (1f - _microSpeedPenalty);
                break;
            case DriverArchetype.Stubborn:
                // Stubborn doesn't speed up — they hold firm, sometimes slower
                targetSpeed = _base.speedMult * (1f - im * 0.05f);
                break;
            case DriverArchetype.Cautious:
                // Gets slower when alert (something happened nearby)
                targetSpeed = _base.speedMult * (1f - al * 0.15f);
                break;
        }
        // Apply micro punchout (SpeedRacer/Express departure burst)
        if (_punchoutActive)
            targetSpeed *= Mathf.Lerp(1.3f, 1f, 1f - _punchoutTimer / 3f);

        p.speedMultiplier = Mathf.MoveTowards(p.speedMultiplier, targetSpeed, 0.6f * dt);

        // ── Dwell multiplier ───────────────────────────────────────────────
        float targetDwell = _base.dwellMult;
        switch (arch)
        {
            case DriverArchetype.Express:
                // Express cuts dwell hard when running late
                targetDwell = _base.dwellMult * Mathf.Lerp(1f, 0.55f, im);
                break;
            case DriverArchetype.SpeedRacer:
                targetDwell = _base.dwellMult * Mathf.Lerp(1f, 0.65f, im * 0.8f + fr * 0.2f);
                break;
            case DriverArchetype.Slacker:
                // Slacker lingers — doors stay open longer, just chilling
                targetDwell = _base.dwellMult * Mathf.Lerp(1f, 1.45f, 1f - im);
                break;
            case DriverArchetype.Stubborn:
                // Stubborn does the full dwell NO MATTER WHAT — then leaves
                // (dwellMult stays at baseline even when frustrated)
                targetDwell = _base.dwellMult;
                break;
        }
        p.dwellMultiplier = Mathf.MoveTowards(p.dwellMultiplier, targetDwell, 0.4f * dt);

        // ── Aggressiveness ─────────────────────────────────────────────────
        float targetAggr = _base.aggressiveness;
        switch (arch)
        {
            case DriverArchetype.Stubborn:   targetAggr = _base.aggressiveness + fr * 0.35f; break;
            case DriverArchetype.SpeedRacer: targetAggr = _base.aggressiveness + fr * 0.40f + im * 0.20f; break;
            case DriverArchetype.Express:    targetAggr = _base.aggressiveness + im * 0.25f; break;
            case DriverArchetype.Cautious:   targetAggr = _base.aggressiveness - al * 0.20f; break;
        }
        p.aggressiveness = Mathf.Clamp(
            Mathf.MoveTowards(p.aggressiveness, targetAggr, 0.5f * dt), 0f, 1f);

        // ── Braking bias ───────────────────────────────────────────────────
        // Higher = brakes later and harder.  SpeedRacer gets lazier with braking
        // when frustrated; Cautious always brakes early, even more so when alert.
        float targetBrake = _base.brakingBias;
        switch (arch)
        {
            case DriverArchetype.SpeedRacer: targetBrake = _base.brakingBias + fr * 0.22f; break;
            case DriverArchetype.Stubborn:   targetBrake = _base.brakingBias + fr * 0.12f; break;
            case DriverArchetype.Cautious:   targetBrake = _base.brakingBias - 0.15f - al * 0.10f; break;
            case DriverArchetype.Slacker:
                // Slacker randomly varies braking — sometimes catches it late
                targetBrake = _base.brakingBias + _microSpeedPenalty * 0.3f;
                break;
        }
        p.brakingBias = Mathf.Clamp(
            Mathf.MoveTowards(p.brakingBias, targetBrake, 0.3f * dt), 0f, 1f);

        // ── Speed variance (micro-jitter within FollowRoute) ───────────────
        // More variance when alert or frustrated — driver is less smooth
        float targetVar = _base.speedVariance + (fr + al) * 0.04f;
        if (arch == DriverArchetype.Slacker) targetVar += 0.05f;
        if (arch == DriverArchetype.Cautious) targetVar = Mathf.Max(0f, targetVar - 0.03f);
        p.speedVariance = Mathf.Clamp(
            Mathf.MoveTowards(p.speedVariance, targetVar, 0.2f * dt), 0f, 0.3f);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  MICRO-EVENTS — the "human moments"
    //  Fires periodically with archetype-dependent probability.
    //  These create the small unpredictable things that make buses feel alive.
    // ══════════════════════════════════════════════════════════════════════
    private void UpdateMicroEvents(float dt)
    {
        // ── Slacker distraction speed dip ─────────────────────────────────
        if (_microSpeedTimer > 0f)
        {
            _microSpeedTimer -= dt;
            if (_microSpeedTimer <= 0f) _microSpeedPenalty = 0f;
        }

        // ── SpeedRacer punchout after stop ────────────────────────────────
        if (_punchoutActive)
        {
            _punchoutTimer -= dt;
            if (_punchoutTimer <= 0f) { _punchoutActive = false; _punchoutTimer = 0f; }
        }

        // ── Roll for next micro-event ──────────────────────────────────────
        _microEventTimer -= dt;
        if (_microEventTimer > 0f) return;
        _microEventTimer = UnityEngine.Random.Range(8f, 25f); // 8-25s between rolls

        var arch = _bus.personality.archetype;
        bool inService = _bus.State == NPCBusController.BusState.InService;
        if (!inService) return;

        switch (arch)
        {
            case DriverArchetype.Slacker:
                // Randomly zones out — brief speed reduction, like glancing at phone
                if (UnityEngine.Random.value < 0.45f)
                {
                    _microSpeedPenalty = UnityEngine.Random.Range(0.07f, 0.18f);
                    _microSpeedTimer   = UnityEngine.Random.Range(3f, 7f);
                }
                break;

            case DriverArchetype.Stubborn:
                // Random unnecessary honk — just because
                if (UnityEngine.Random.value < 0.12f && _honkCooldown <= 0f)
                    DoHonk(HonkType.ShortBeep, 12f);
                break;

            case DriverArchetype.SpeedRacer:
                // Randomly honks at nothing in particular when frustrated
                if (frustration > 0.3f && UnityEngine.Random.value < 0.20f && _honkCooldown <= 0f)
                    DoHonk(HonkType.ShortBeep, 8f);
                break;
        }
    }

    // ── Detect transition OUT of AtStop → trigger departure behaviors ──────
    private void DetectStopDeparture()
    {
        bool wasAtStop = _prevState == NPCBusController.BusState.AtStop ||
                         _prevState == NPCBusController.BusState.AtTerminal;
        bool nowMoving = _bus.State == NPCBusController.BusState.InService;

        if (wasAtStop && nowMoving)
        {
            var arch = _bus.personality.archetype;
            // SpeedRacer and Express get a departure punch — hammers it off the stop
            if ((arch == DriverArchetype.SpeedRacer || arch == DriverArchetype.Express) &&
                !_punchoutActive)
            {
                _punchoutActive = true;
                _punchoutTimer  = UnityEngine.Random.Range(2.0f, 3.5f);
            }
        }
        _prevState = _bus.State;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DECISION LAYER — honk decisions
    // ══════════════════════════════════════════════════════════════════════
    private void MakeDecisions()
    {
        if (_honkCooldown > 0f) return;
        var arch = _bus.personality.archetype;

        // ── Blocked honk ──────────────────────────────────────────────────
        if (_blockedTime >= BlockedHonkThreshold(arch) && frustration >= 0.38f)
        {
            DoHonk(DecideBlockedHonkType(arch), BlockedHonkCooldown(arch));
            return;
        }

        // ── Red-light rage — Stubborn/SpeedRacer only ─────────────────────
        if (_blockedTime >= 15f && frustration >= 0.75f &&
            (arch == DriverArchetype.Stubborn || arch == DriverArchetype.SpeedRacer))
        {
            DoHonk(HonkType.LongBlast, 9f);
            return;
        }

        // ── Impatient at stop — Express/SpeedRacer announce themselves ────
        if (impatience >= 0.70f && _bus.spd < 1f &&
            _bus.State == NPCBusController.BusState.AtStop &&
            (arch == DriverArchetype.Express || arch == DriverArchetype.SpeedRacer))
        {
            DoHonk(HonkType.ShortBeep, 11f);
            return;
        }
    }

    private float BlockedHonkThreshold(DriverArchetype a) => a switch
    {
        DriverArchetype.Stubborn   => 4.0f,
        DriverArchetype.SpeedRacer => 3.0f,
        DriverArchetype.Express    => 5.5f,
        DriverArchetype.Normal     => 9.0f,
        DriverArchetype.Cautious   => 20.0f,
        DriverArchetype.Slacker    => 25.0f,
        _                          => 9.0f
    };

    private float BlockedHonkCooldown(DriverArchetype a) => a switch
    {
        DriverArchetype.Stubborn   => 5.0f,
        DriverArchetype.SpeedRacer => 4.0f,
        DriverArchetype.Express    => 7.0f,
        _                          => 11.0f
    };

    private HonkType DecideBlockedHonkType(DriverArchetype a)
    {
        bool maxFrustration = frustration >= 0.78f;
        return a switch
        {
            DriverArchetype.Stubborn   => maxFrustration ? HonkType.LongBlast   : HonkType.DoubleBeep,
            DriverArchetype.SpeedRacer => maxFrustration ? HonkType.LongBlast   : HonkType.DoubleBeep,
            DriverArchetype.Express    => maxFrustration ? HonkType.DoubleBeep  : HonkType.ShortBeep,
            DriverArchetype.Slacker    => HonkType.ShortBeep,
            _                          => maxFrustration ? HonkType.DoubleBeep  : HonkType.ShortBeep
        };
    }

    private void UpdateCooldowns(float dt)
    {
        if (_honkCooldown  > 0f) _honkCooldown  -= dt;
        if (_exchangeTimer > 0f) { _exchangeTimer -= dt; if (_exchangeTimer <= 0f) _exchangeCount = 0; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SOCIAL SCAN — throttled, not per-frame
    // ══════════════════════════════════════════════════════════════════════
    private void UpdateSocialScan(float dt)
    {
        _socialTimer -= dt;
        if (_socialTimer > 0f) return;
        _socialTimer = socialScanInterval;

        _nearbyBrains.Clear();
        int n = Physics.OverlapSphereNonAlloc(
            transform.position, socialRadius, _socialBuf, ~0,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < n; i++)
        {
            if (_socialBuf[i] == null) continue;
            var brain = _socialBuf[i].GetComponentInParent<BusAIBrain>();
            if (brain != null && brain != this && !_nearbyBrains.Contains(brain))
                _nearbyBrains.Add(brain);
        }

        CheckGreetings();
    }

    private void CheckGreetings()
    {
        if (_bus.CurrentRoute == null || _honkCooldown > 0f) return;
        if (_bus.personality.archetype == DriverArchetype.Cautious) return;

        foreach (var brain in _nearbyBrains)
        {
            if (brain._bus.CurrentRoute == null) continue;
            if (brain._bus.CurrentRoute.routeNumber != _bus.CurrentRoute.routeNumber) continue;
            if (brain._bus.IsOutbound == _bus.IsOutbound) continue;
            if (_greetedBusIDs.Contains(brain._bus.busID)) continue;

            float chance = _bus.personality.archetype switch
            {
                DriverArchetype.Slacker    => 0.75f,
                DriverArchetype.Normal     => 0.55f,
                DriverArchetype.Express    => 0.35f,
                DriverArchetype.SpeedRacer => 0.18f,
                DriverArchetype.Stubborn   => 0.15f,
                _                          => 0.40f
            };

            if (UnityEngine.Random.value < chance)
            {
                DoHonk(HonkType.Greeting, 14f);
                _greetedBusIDs.Add(brain._bus.busID);
            }
        }
        if (_greetedBusIDs.Count > 60) _greetedBusIDs.Clear();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  HONK — TRANSMIT
    // ══════════════════════════════════════════════════════════════════════
    private void DoHonk(HonkType type, float cooldown)
    {
        _honkCooldown = cooldown;
        _honk?.Play(type);
        OnHonk?.Invoke(this, transform.position, type);
    }

    public void TriggerHonk(HonkType type = HonkType.ShortBeep) => DoHonk(type, 3f);

    // ══════════════════════════════════════════════════════════════════════
    //  HONK — RECEIVE
    // ══════════════════════════════════════════════════════════════════════
    private void HandleRemoteHonk(BusAIBrain sender, Vector3 pos, HonkType type)
    {
        if (sender == this) return;
        if (Vector3.Distance(transform.position, pos) > socialRadius) return;

        alertness = Mathf.Min(1f, alertness + 0.45f);
        var arch  = _bus.personality.archetype;

        if (type == HonkType.Greeting)
        {
            float chance = arch switch
            {
                DriverArchetype.Slacker    => 0.85f,
                DriverArchetype.Normal     => 0.65f,
                DriverArchetype.Express    => 0.40f,
                DriverArchetype.SpeedRacer => 0.22f,
                DriverArchetype.Stubborn   => 0.18f,
                DriverArchetype.Cautious   => 0.08f,
                _                          => 0.45f
            };
            if (UnityEngine.Random.value < chance && _honkCooldown <= 0f)
                StartCoroutine(DelayedHonk(HonkType.Greeting,
                    UnityEngine.Random.Range(0.3f, 1.4f), 13f));
            return;
        }

        // Only react to honks aimed rearward at us
        Vector3 toSender = sender.transform.position - transform.position;
        float   dot      = Vector3.Dot(toSender.normalized, -transform.forward);
        if (dot < 0.25f) return;
        if (_exchangeCount >= 2) return;

        if (type == HonkType.LongBlast || type == HonkType.DoubleBeep)
        {
            float reactChance = arch switch
            {
                DriverArchetype.Stubborn   => 0.82f,
                DriverArchetype.SpeedRacer => 0.50f,
                DriverArchetype.Normal     => 0.28f,
                DriverArchetype.Express    => 0.22f,
                DriverArchetype.Slacker    => 0.12f,
                DriverArchetype.Cautious   => 0.04f,
                _                          => 0.22f
            };

            if (UnityEngine.Random.value < reactChance && _honkCooldown <= 0f)
            {
                HonkType reply = (arch == DriverArchetype.Stubborn || arch == DriverArchetype.SpeedRacer)
                    ? HonkType.DoubleBeep
                    : HonkType.AcknowledgeHonk;
                StartCoroutine(DelayedHonk(reply, UnityEngine.Random.Range(0.6f, 2.2f), 8f));
                _exchangeCount++;
                _exchangeTimer = 12f;
            }
        }
    }

    private IEnumerator DelayedHonk(HonkType type, float delay, float cooldown)
    {
        yield return new WaitForSeconds(delay);
        if (_honkCooldown <= 0f) DoHonk(type, cooldown);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DEBUG
    // ══════════════════════════════════════════════════════════════════════
    public string GetMoodReport() =>
        $"[Bus#{_bus.busID}|{_bus.personality.archetype}] " +
        $"fr={frustration:0.00} im={impatience:0.00} al={alertness:0.00} " +
        $"spdMult={_bus.personality.speedMultiplier:0.00} " +
        $"dwellMult={_bus.personality.dwellMultiplier:0.00} " +
        $"blocked={_blockedTime:0.0}s punchout={_punchoutActive}";
}


// ══════════════════════════════════════════════════════════════════════════════
//  BusNPCHonk — child-GO MonoBehaviour — spatial air horn DSP
//
//  DISTANCE ATTENUATION BUG FIX:
//  OnAudioFilterRead inserts BEFORE Unity's spatializer in the DSP chain.
//  The AudioSource rolloff settings never touch the synthesized signal.
//  We solve this the same way NPCBusController.ProcessAudio() does it:
//  cache listener + bus positions on the main thread, apply a hand-rolled
//  linear falloff curve inside OnAudioFilterRead.
// ══════════════════════════════════════════════════════════════════════════════
public class BusNPCHonk : MonoBehaviour
{
    // ── Fleet-series pitch table ───────────────────────────────────────────
    // (fundamental Hz, upper partial Hz, top partial Hz, waveshaping grit 0-1)
    private static readonly (float f1, float f2, float f3, float grit)[] SeriesTuning =
    {
        (198f, 264f, 330f, 0.50f),   // 2000s — low, rough, old air horn
        (220f, 277f, 370f, 0.30f),   // 2100s — mid classic (matches player honk)
        (233f, 311f, 415f, 0.15f),   // 2200s — modern clean
        (246f, 329f, 440f, 0.07f),   // 2300s — bright, newest
    };

    // Distance rolloff — matches old AudioSource linear range
    private const float MIN_DIST = 4f;
    private const float MAX_DIST = 90f;

    private AudioSource _src;
    private double      _sr;
    private Transform   _busRoot;    // parent bus transform
    private Transform   _listener;

    // Cached on main thread, read on audio thread
    private float _cachedDist = 0f;

    // Horn character
    private float _f1, _f2, _f3, _grit, _masterVol;

    // Envelope
    private float _vol       = 0f;
    private float _targetVol = 0f;
    private const float ATTACK  = 0.035f;
    private const float RELEASE = 0.10f;

    // Playback
    private float    _playTimer    = 0f;
    private HonkType _currentType;
    private bool     _doublePhase2 = false;

    private double _ph1, _ph2, _ph3;

    // ── Duration by type ──────────────────────────────────────────────────
    private static float Dur(HonkType t) => t switch
    {
        HonkType.ShortBeep        => 0.22f,
        HonkType.DoubleBeep       => 0.22f,  // first pulse; second fires via coroutine
        HonkType.LongBlast        => 1.40f,
        HonkType.Greeting         => 0.28f,
        HonkType.AcknowledgeHonk  => 0.18f,
        _                         => 0.24f
    };

    public void Init(int busID, int fleetNumber, float volume, Transform busRoot)
    {
        _busRoot  = busRoot;
        _listener = Camera.main != null ? Camera.main.transform : busRoot;

        // Build AudioSource — spatial settings don't do the attenuation work
        // (see the header comment above) but we still want 3D panning
        _src = gameObject.AddComponent<AudioSource>();
        _src.spatialBlend  = 1f;
        _src.rolloffMode   = AudioRolloffMode.Linear;
        _src.maxDistance   = MAX_DIST;
        _src.minDistance   = MIN_DIST;
        _src.volume        = 1f;   // attenuation handled manually in OAFR
        _src.loop          = true;
        _src.clip          = AudioClip.Create("NPCHonkBuf", 2048, 1, 44100, false);
        _src.Play();

        _sr         = AudioSettings.outputSampleRate;
        _masterVol  = volume;

        int seriesIdx = Mathf.Clamp((fleetNumber / 100) - 20, 0, SeriesTuning.Length - 1);
        var (f1, f2, f3, grit) = SeriesTuning[seriesIdx];

        // ±8 Hz unique variance per bus so no two buses sound identical
        float var = ((busID * 7919) % 17) / 17f * 16f - 8f;
        _f1   = f1 + var;
        _f2   = f2 + var * 1.26f;
        _f3   = f3 + var * 1.68f;
        _grit = grit;
    }

    // ── Called on main thread every frame — keeps cached dist fresh ────────
    private void Update()
    {
        float dt = Time.deltaTime;

        // Cache distance on main thread (safe for audio thread to read)
        if (_busRoot != null && _listener != null)
            _cachedDist = Vector3.Distance(_busRoot.position, _listener.position);

        // Envelope timer
        if (_playTimer > 0f)
        {
            _playTimer -= dt;
            if (_playTimer <= 0f)
            {
                if (_currentType == HonkType.DoubleBeep && !_doublePhase2)
                {
                    _doublePhase2 = true;
                    _targetVol    = 0f;
                    StartCoroutine(FireSecondBeep(0.14f));
                }
                else
                {
                    _targetVol = 0f;
                }
            }
        }

        float rate = _targetVol > _vol ? (1f / ATTACK) : (1f / RELEASE);
        _vol = Mathf.MoveTowards(_vol, _targetVol, rate * dt);
    }

    private IEnumerator FireSecondBeep(float gap)
    {
        yield return new WaitForSeconds(gap);
        _playTimer = Dur(HonkType.ShortBeep);
        _targetVol = 1f;
    }

    public void Play(HonkType type)
    {
        _currentType  = type;
        _doublePhase2 = false;
        _playTimer    = Dur(type);
        _targetVol    = 1f;
    }

    // ── DSP — audio thread ─────────────────────────────────────────────────
    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (_vol < 0.001f) { Array.Clear(data, 0, data.Length); return; }

        // Manual linear rolloff — the main point of this whole comment block
        float dist  = _cachedDist;
        float atten = Mathf.Clamp01(1f - (dist - MIN_DIST) / Mathf.Max(1f, MAX_DIST - MIN_DIST));
        if (atten < 0.001f) { Array.Clear(data, 0, data.Length); return; }

        double invSR = 1.0 / _sr;
        double volD  = _vol * _masterVol * atten;
        double drive = 1.0 + _grit * 2.8;
        double norm  = _grit > 0.02f ? Math.Tanh(drive) : 1.0;

        for (int i = 0; i < data.Length; i += channels)
        {
            double s1 = Math.Sin(2.0 * Math.PI * _ph1);
            double s2 = Math.Sin(2.0 * Math.PI * _ph2);
            double s3 = Math.Sin(2.0 * Math.PI * _ph3);

            double mix = s1 * 0.55 + s2 * 0.38 + s3 * 0.22;
            if (_grit > 0.02f) mix = Math.Tanh(mix * drive) / norm;

            float smp = (float)(mix * 0.32 * volD);
            for (int c = 0; c < channels; c++) data[i + c] = smp;

            _ph1 = (_ph1 + _f1 * invSR) % 1.0;
            _ph2 = (_ph2 + _f2 * invSR) % 1.0;
            _ph3 = (_ph3 + _f3 * invSR) % 1.0;
        }
    }
}