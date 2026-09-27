using System;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BREAKDOWN TYPES — 12 real breakdowns across 4 categories, plus 4 cosmetic
//  "sub-breakdowns" that never stop the bus, just add a persistent audio/feel
//  quirk for their duration (engine/tx character issues, not mechanical stops).
// ═══════════════════════════════════════════════════════════════════════════════
public enum BreakdownType
{
    // ── Electric ──
    BatteryTrip,        // full shutdown -- HV pack trips offline
    InverterFault,       // power cuts in/out, no full shutdown
    HVILFault,            // full shutdown -- safety interlock trips

    // ── Fuel ──
    EngineFailure,       // full shutdown, smoke
    Overheating,          // smoke, reduced power, precedes EngineFailure
    FuelPumpFailure,      // engine sputters/stalls, no shutdown
    Leak,                 // AC fuel/refrigerant leak -- AC dies, no shutdown

    // ── Pneumatic ──
    AirBrakeFailure,      // forced stop, no electrical shutdown
    DoorPneumaticFailure, // doors stuck -- the 1000 Series' whole thing
    SuspensionAirFailure, // bus sits low/uneven, involuntary "kneel"

    // ── Air / Mechanical ──
    ACFailure,             // AC only, no other effect
    FlatTire,              // limp mode, POP sound

    // ── Cosmetic sub-breakdowns (engine/tx character quirks — bus runs
    //    normally the entire time, these never trigger shutdown/limp/doors) ──
    DeepRPMGroan,          // hella deep, loud groan that tracks RPM
    StuckGear,             // tx hangs in one ratio, won't shift normally
    RoughIdleLope,         // uneven, lopey idle -- shakes at a stop
    TurboWhistleLeak,      // continuous high-pitched boost-leak whistle
    TransmissionFailure,
}

public enum BreakdownCategory { Electric, Fuel, Pneumatic, Mechanical, Cosmetic }
public enum ShutdownSeverity { None, Partial, Full } // None=cosmetic/no effect on driving, Partial=limp mode, Full=complete stop+shutdown

[System.Serializable]
public class ActiveBreakdown
{
    public BreakdownType type;
    public float         remainingMinutes;
    public float         totalMinutes;        // for progress display
    public bool          requiresReplacement; // replace-tier: doesn't auto-resume, limps to depot instead
    public float         triggeredAtGameTime; // BusScheduler.GameTimeMinutes at trigger

    public float Progress => totalMinutes > 0f
        ? Mathf.Clamp01(1f - remainingMinutes / totalMinutes)
        : 1f;
}

// ── Static metadata table — one entry per type, everything about how it
//    behaves lives here instead of scattered switch statements. ──────────────
public struct BreakdownMeta
{
    public BreakdownCategory category;
    public float             durationMinutes;
    public bool              requiresReplacement; // false = self-resolves after timer, true = replace-tier (limp to depot, needs MaintenanceBay to fully reset condition)
    public ShutdownSeverity  severity;
    public bool              affectsElectric;   // eligible on Electric-fuel buses
    public bool              affectsCombustion; // eligible on Diesel/CNG-fuel buses (includes hybrids)
    public string            soundCue;          // key BusAudioEngine listens for
    public string            suggestedAction;   // shown in driver console / ongoing-breakdowns UI
}

public class BusBreakdownSystem : MonoBehaviour
{
    public static BusBreakdownSystem Instance;

    // ── Timing ────────────────────────────────────────────────────────────────
    [Header("Timing Scale")]
    [Tooltip("How many real seconds equal one in-game minute.")]
    public float secondsPerGameMinute = 30f;

    [Header("Roll Chance")]
    [Tooltip("Base chance PER GAME-MINUTE for a bus at 100% part condition (multiplier 1.0) to roll a breakdown. Actual per-bus chance scales up as BusVehicleSystem part condition degrades -- see GetConditionMultiplier.")]
    public float baseChancePerMinute = 0.0001f;
    [Tooltip("At 0% part condition, chance is multiplied by this much relative to 100% condition. Linear interpolation between these two.")]
    public float maxConditionMultiplier = 12f;

    // ── Debug UI ──────────────────────────────────────────────────────────────
    [Header("Debug")]
    public bool showDebugUI = false; // [FIX] was true -- shipped visible by default

    [Header("Player-facing UI")]
    public bool showOngoingBreakdownsPanel = true;
    private Rect _playerPanelRect = new Rect(10, 10, 320, 220);

    // ── Events ────────────────────────────────────────────────────────────────
    public static event Action<int, BreakdownType, bool> OnBreakdownTriggered;
    public static event Action<int, BreakdownType>       OnBreakdownCleared;
    public static event Action<int>                      OnBusFullyRestored;
    // [ADD] Fired specifically when a replace-tier breakdown's timer expires --
    // the bus needs to limp home to depot rather than resume normal service.
    // NPCBusController/PlayerHandoff subscribe to actually drive that behavior.
    public static event Action<int, BreakdownType>       OnReplaceTierExpired;

    // ── Internal state ────────────────────────────────────────────────────────
    private readonly Dictionary<int, List<ActiveBreakdown>> _activeBreakdowns =
        new Dictionary<int, List<ActiveBreakdown>>();
    // [ADD] Tracks whether a bus's replace-tier breakdown has already expired
    // and is now in "limping home" state -- distinct from IsBusLocked, since
    // the breakdown itself has technically cleared but the bus still can't
    // resume normal service until it reaches a MaintenanceBay.
    private readonly HashSet<int> _limpingHome = new HashSet<int>();
    // Per-bus last-rolled game-minute, so TryRollBreakdown only actually rolls
    // once per elapsed game-minute per bus regardless of how often it's called.
    private readonly Dictionary<int, float> _lastRollGameMinute = new Dictionary<int, float>();

    // ── GUI layout ────────────────────────────────────────────────────────────
    private Vector2 _scroll;
    private Rect    _windowRect = new Rect(10, 300, 340, 400);

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    // ── Tick: count down all active breakdowns ────────────────────────────────
    private void Update()
    {
        float dtMinutes = Time.deltaTime / secondsPerGameMinute;

        var expiredBuses = new List<int>();
        var keys = new List<int>(_activeBreakdowns.Keys);

        foreach (int busID in keys)
        {
            var list = _activeBreakdowns[busID];

            for (int i = list.Count - 1; i >= 0; i--)
            {
                list[i].remainingMinutes -= dtMinutes;

                if (list[i].remainingMinutes <= 0f)
                {
                    BreakdownType cleared = list[i].type;
                    bool wasReplaceTier    = list[i].requiresReplacement;
                    list.RemoveAt(i);

                    OnBreakdownCleared?.Invoke(busID, cleared);
                    Debug.Log($"[BREAKDOWN] Bus#{busID} → {cleared} resolved.");

                    // [ADD] Replace-tier breakdowns don't just clear and resume
                    // normally -- the bus needs to limp back to depot, and only
                    // a MaintenanceBay visit fully resets condition afterward.
                    if (wasReplaceTier)
                    {
                        _limpingHome.Add(busID);
                        OnReplaceTierExpired?.Invoke(busID, cleared);
                        Debug.Log($"[BREAKDOWN] Bus#{busID} timer expired on replace-tier {cleared} -- limping home to depot.");
                    }
                }
            }

            if (list.Count == 0)
            {
                expiredBuses.Add(busID);
                OnBusFullyRestored?.Invoke(busID);
                Debug.Log($"[BREAKDOWN] Bus#{busID} fully restored.");
            }
        }

        foreach (int busID in expiredBuses)
            _activeBreakdowns.Remove(busID);
    }

    // ── Debug overlay (dev only) ─────────────────────────────────────────────
    private void OnGUI()
    {
        if (showDebugUI)
            _windowRect = GUI.Window(9901, _windowRect, DrawDebugWindow, "Breakdown Monitor (DEV)");

        if (showOngoingBreakdownsPanel)
            DrawPlayerPanel();
    }

    // [ADD] Real player-facing UI -- shows only the player's OWN bus's active
    // breakdowns, with type, time left, and what to actually do about it.
    // Separate from the dev-only fleet-wide debug monitor above.
    private void DrawPlayerPanel()
    {
        int playerBusID = PlayerHandoff.Instance != null ? PlayerHandoff.Instance.PlayerBusID : -1;
        if (playerBusID < 0) return;

        var list = GetBreakdownInfo(playerBusID);
        bool limping = _limpingHome.Contains(playerBusID);
        if (list.Count == 0 && !limping) return; // nothing to show -- panel stays hidden when the player's bus is fine

        _playerPanelRect = GUI.Window(9902, _playerPanelRect, id =>
        {
            GUI.DragWindow(new Rect(0, 0, 320, 20));
            GUILayout.Space(4);

            if (limping)
            {
                GUILayout.Label("⚠ Limping to depot -- visit a Maintenance Bay.", new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.6f, 0.2f) } });
            }

            foreach (var b in list)
            {
                var meta = GetMeta(b.type);
                GUILayout.Space(2);
                GUILayout.Label($"{b.type}", new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });
                GUILayout.Label($"  {b.remainingMinutes:F1} min remaining");
                GUILayout.Label($"  → {meta.suggestedAction}");
            }
        }, "Bus Status");
    }

    private void DrawDebugWindow(int id)
    {
        GUI.DragWindow(new Rect(0, 0, 340, 20));
        GUILayout.Space(4);

        if (_activeBreakdowns.Count == 0)
        {
            GUILayout.Label("  No active breakdowns.");
        }
        else
        {
            _scroll = GUILayout.BeginScrollView(_scroll);

            foreach (var kv in _activeBreakdowns)
            {
                int busID = kv.Key;
                var list  = kv.Value;

                string label = $"Bus #{busID}";
                if (BusRegistry.ActiveBuses.TryGetValue(busID, out var ctrl) && ctrl != null)
                    label += $"  (fleet {ctrl.fleetNumber})";

                bool needsReplace = false;
                foreach (var b in list) if (b.requiresReplacement) { needsReplace = true; break; }

                GUIStyle headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                GUILayout.Label(label + (needsReplace ? "  ⚠ REPLACE" : ""), headerStyle);

                foreach (var b in list)
                {
                    float pct = b.Progress * 100f;
                    string bar = ProgressBar(b.Progress, 12);
                    GUILayout.Label($"  {b.type,-18}  {b.remainingMinutes:F1} min  [{bar}] {pct:F0}%");
                }

                GUILayout.Space(4);
            }

            GUILayout.EndScrollView();
        }

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Force Random", GUILayout.Height(22)))
            TriggerRandomBreakdownForTest();
        GUILayout.EndHorizontal();
    }

    private static string ProgressBar(float t, int width)
    {
        int filled = Mathf.RoundToInt(t * width);
        return new string('█', filled) + new string('░', width - filled);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═════════════════════════════════════════════════════════════════════════

    public bool IsBusLocked(int busID) =>
        _activeBreakdowns.TryGetValue(busID, out var list) && list.Count > 0;

    /// <summary>True once a replace-tier breakdown's timer has expired and the
    /// bus is limping home to depot -- distinct from IsBusLocked.</summary>
    public bool IsLimpingHome(int busID) => _limpingHome.Contains(busID);

    public bool RequiresReplacement(int busID)
    {
        if (!_activeBreakdowns.TryGetValue(busID, out var list)) return false;
        foreach (var b in list)
            if (b.requiresReplacement) return true;
        return false;
    }

    /// <summary>True if any CURRENT active breakdown on this bus causes a full
    /// stop/shutdown (severity Full) -- distinct from Partial (limp mode) or
    /// None (cosmetic, bus drives completely normally).</summary>
    public bool RequiresFullShutdown(int busID)
    {
        if (!_activeBreakdowns.TryGetValue(busID, out var list)) return false;
        foreach (var b in list)
            if (GetMeta(b.type).severity == ShutdownSeverity.Full) return true;
        return false;
    }

    public bool RequiresLimpMode(int busID)
    {
        if (!_activeBreakdowns.TryGetValue(busID, out var list)) return false;
        foreach (var b in list)
            if (GetMeta(b.type).severity == ShutdownSeverity.Partial) return true;
        return false;
    }

    public IReadOnlyList<ActiveBreakdown> GetBreakdownInfo(int busID)
    {
        if (_activeBreakdowns.TryGetValue(busID, out var list))
            return list.AsReadOnly();
        return Array.Empty<ActiveBreakdown>();
    }

    public void TriggerBreakdown(int busID, BreakdownType type)
    {
        if (IsBusLocked(busID))
        {
            Debug.Log($"[BREAKDOWN] Bus#{busID} already locked — skipping new {type}.");
            return;
        }

        if (!_activeBreakdowns.ContainsKey(busID))
            _activeBreakdowns[busID] = new List<ActiveBreakdown>();

        foreach (var existing in _activeBreakdowns[busID])
            if (existing.type == type) return;

        var meta = GetMeta(type);
        float now = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : 0f;

        var breakdown = new ActiveBreakdown
        {
            type                = type,
            remainingMinutes    = meta.durationMinutes,
            totalMinutes        = meta.durationMinutes,
            requiresReplacement = meta.requiresReplacement,
            triggeredAtGameTime = now
        };

        _activeBreakdowns[busID].Add(breakdown);

        ApplyConditionDamage(busID, type, meta.requiresReplacement);

        OnBreakdownTriggered?.Invoke(busID, type, meta.requiresReplacement);
        Debug.Log($"[BREAKDOWN] Bus#{busID} → {type}  ({meta.durationMinutes} min, replace={meta.requiresReplacement}, sound={meta.soundCue})");
    }

    /// <summary>[ADD] Connects breakdowns to the vehicle's actual condition
    /// stats for the first time -- previously a breakdown was purely a
    /// timed event (drives effects, clears on a timer or MaintenanceBay
    /// visit) with zero lasting effect on BusVehicleSystem's condition
    /// numbers. Per instruction: a requiresReplacement-tier breakdown
    /// (engine/battery/transmission failures, the ones that send a bus
    /// limping home) knocks 80% off the affected part's condition; anything
    /// lighter (limp-mode types that don't require replacement, plus the
    /// four true cosmetic sound-quirks) knocks 10% off. "Off base" means
    /// relative to THIS bus's own maxCondition ceiling, same convention
    /// RepairToMax/GetConditionFraction already use -- a Neglected bus's
    /// 50%-cap "full" engine takes a real hit relative to ITS OWN ceiling,
    /// not toward a flat 100 that bus was never at.
    ///
    /// Applied here (inside TriggerBreakdown itself, not a subscriber to
    /// OnBreakdownTriggered) so it's guaranteed to run for every trigger
    /// path -- the organic per-minute roll AND the manual "breakdown"
    /// console test command both funnel through this one method.</summary>
    private static readonly System.Collections.Generic.Dictionary<BreakdownType, BusVehicleSystem.Part> _typeToPart =
        new System.Collections.Generic.Dictionary<BreakdownType, BusVehicleSystem.Part>
        {
            { BreakdownType.BatteryTrip,          BusVehicleSystem.Part.FuelBattery },
            { BreakdownType.InverterFault,         BusVehicleSystem.Part.FuelBattery },
            { BreakdownType.HVILFault,             BusVehicleSystem.Part.FuelBattery },
            { BreakdownType.EngineFailure,         BusVehicleSystem.Part.Engine },
            { BreakdownType.Overheating,           BusVehicleSystem.Part.Engine },
            { BreakdownType.FuelPumpFailure,       BusVehicleSystem.Part.FuelBattery },
            { BreakdownType.Leak,                  BusVehicleSystem.Part.AC }, // AC refrigerant leak -- see the enum's own comment
            { BreakdownType.AirBrakeFailure,       BusVehicleSystem.Part.Pneumatics },
            { BreakdownType.DoorPneumaticFailure,  BusVehicleSystem.Part.Pneumatics },
            { BreakdownType.SuspensionAirFailure,  BusVehicleSystem.Part.Pneumatics },
            { BreakdownType.ACFailure,             BusVehicleSystem.Part.AC },
            { BreakdownType.FlatTire,              BusVehicleSystem.Part.Pneumatics },
            { BreakdownType.DeepRPMGroan,          BusVehicleSystem.Part.Engine },
            { BreakdownType.StuckGear,             BusVehicleSystem.Part.Transmission },
            { BreakdownType.RoughIdleLope,         BusVehicleSystem.Part.Engine },
            { BreakdownType.TurboWhistleLeak,      BusVehicleSystem.Part.Engine },
            { BreakdownType.TransmissionFailure,   BusVehicleSystem.Part.Transmission },
        };

    private void ApplyConditionDamage(int busID, BreakdownType type, bool requiresReplacement)
    {
        if (!_typeToPart.TryGetValue(type, out var part)) return;

        var vehicle = BusManager.Instance?.GetRecord(busID)?.controller?.GetComponent<BusVehicleSystem>();
        if (vehicle == null) return;

        float damageFraction = requiresReplacement ? 0.80f : 0.10f;
        float newCondition = vehicle.GetCondition(part) - damageFraction * vehicle.maxCondition;
        vehicle.SetCondition(part, newCondition);
    }

    /// <summary>Immediately removes all breakdowns for a bus AND clears
    /// limping-home state -- call this from MaintenanceBay when a bus's
    /// condition gets reset, not just when a timer expires.</summary>
    public void ClearAllBreakdowns(int busID)
    {
        _limpingHome.Remove(busID);

        if (!_activeBreakdowns.TryGetValue(busID, out var list)) return;

        foreach (var b in list)
            OnBreakdownCleared?.Invoke(busID, b.type);

        _activeBreakdowns.Remove(busID);
        OnBusFullyRestored?.Invoke(busID);
        Debug.Log($"[BREAKDOWN] Bus#{busID} manually cleared.");
    }

    /// <summary>Call once the bus has actually reached a MaintenanceBay and had
    /// its condition reset -- clears limping-home state specifically. Does NOT
    /// touch _activeBreakdowns since a limping bus has none by definition.</summary>
    public void OnReachedMaintenanceBay(int busID) => _limpingHome.Remove(busID);

    // [REDESIGN] This used to be a thin 4-line helper only NPCBusController
    // called, while a near-duplicate of the REAL effect logic (engine/
    // battery/AC shutdown, doors, sound flags, smoke) was separately
    // hand-copied into both NPCBusController.ApplyBreakdownAudioOverrides
    // AND PlayerHandoff.UpdatePlayerBreakdowns. That duplication was the
    // direct cause of every bug found so far — the player's engine-state
    // fix, the door timing, the sound flags all had to be written twice,
    // and it was trivial for one copy to drift from the other or just be
    // missing entirely.
    //
    // Now EVERYTHING about applying a breakdown to a vehicle lives in this
    // one method. NPCBusController and BusSimulationController both call
    // this exact same method every tick, passing their own local fields by
    // ref — there is no second copy of this logic anywhere else in the
    // codebase anymore.
    /// <summary>The single source of truth for what an active breakdown does
    /// to a bus. Called every tick by both NPCBusController and
    /// BusSimulationController — audio flags, engine/battery/AC shutdown,
    /// door behavior, and speed/accel/brake all handled here, once.</summary>
    public void ApplyBreakdownToVehicle(int busID, BusAudioEngine audioEngine,
                                          BusDoorSet frontDoorSet, ref bool doorsOpen,
                                          ref float spd, ref float accel, ref float bkPd,
                                          ref bool doorsHandledFlag, float dt,
                                          out bool engineForcedOff, out bool batteryForcedOff, out bool acForcedOff)
    {
        bool anyActive = IsBusLocked(busID);
        engineForcedOff = batteryForcedOff = acForcedOff = false;

        // ── Sound-effect flags — reset every call, re-set below only if a
        // matching breakdown is actually active. ──────────────────────────
        if (audioEngine != null)
        {
            audioEngine.bd_batteryGroan = audioEngine.bd_deepRpmGroan = audioEngine.bd_stuckGear =
                audioEngine.bd_roughIdleLope = audioEngine.bd_turboWhistleLeak = false;
        }

        if (!anyActive)
        {
            if (audioEngine != null) audioEngine.bd_flatTirePop = false;
            // Breakdown cleared — close back up if we were the ones who
            // opened the doors, reset the gate for next time.
            if (doorsHandledFlag)
            {
                doorsHandledFlag = false;
                doorsOpen = false;
                frontDoorSet?.Close();
            }
            return;
        }

        bool engineOff = false, batteryKilled = false, acKilled = false, popNow = false;

        foreach (var b in GetBreakdownInfo(busID))
        {
            var meta = GetMeta(b.type);
            if (meta.severity == ShutdownSeverity.Full) engineOff = true;
            if (b.type == BreakdownType.BatteryTrip || b.type == BreakdownType.HVILFault) batteryKilled = true;
            if (b.type == BreakdownType.ACFailure || b.type == BreakdownType.Leak) acKilled = true;
            if (b.type == BreakdownType.FlatTire && b.remainingMinutes > b.totalMinutes - 0.05f) popNow = true;

            if (audioEngine == null) continue;
            if (b.type == BreakdownType.BatteryTrip) audioEngine.bd_batteryGroan = true;
            if (b.type == BreakdownType.DeepRPMGroan) audioEngine.bd_deepRpmGroan = true;
            if (b.type == BreakdownType.StuckGear) audioEngine.bd_stuckGear = true;
            if (b.type == BreakdownType.RoughIdleLope) audioEngine.bd_roughIdleLope = true;
            if (b.type == BreakdownType.TurboWhistleLeak) audioEngine.bd_turboWhistleLeak = true;
        }

        if (audioEngine != null)
        {
            audioEngine.bd_flatTirePop = popNow;
            if (engineOff) audioEngine.engineState = BusAudioEngine.EngineRunState.Off;
            if (batteryKilled) audioEngine.batteryOn = false;
            if (acKilled) audioEngine.acComfortOn = false;
        }
        engineForcedOff = engineOff; batteryForcedOff = batteryKilled; acForcedOff = acKilled;

        // ── Doors — front-only, and only once the bus has actually stopped. ──
        if (engineOff)
        {
            if (!doorsHandledFlag && spd < 0.3f)
            {
                doorsHandledFlag = true;
                doorsOpen = true;
                frontDoorSet?.Open();
            }
        }

        // ── Speed/accel/brake — unconditional override, applied AFTER
        // whatever the caller's own driving logic computed this frame, so it
        // can't be silently skipped by some other state/branch (this was
        // the exact bug behind NPCs driving straight through EngineFailure). ──
        if (engineOff)
        {
            accel = 0f;
            bkPd  = 1f;
            spd   = Mathf.MoveTowards(spd, 0f, 25f * dt);
        }
        else if (RequiresLimpMode(busID))
        {
            spd = Mathf.Min(spd, 15f);
        }
    }

    public void ApplyBreakdownEffect(int busID, ref float spd, ref float accel, ref float bkPd)
    {
        if (!RequiresFullShutdown(busID)) return;
        spd   = 0f;
        accel = 0f;
        bkPd  = 1f;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CONDITION-WEIGHTED, DRIVETRAIN-AWARE, PER-MINUTE ROLLING
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>The one call site NPCBusController/PlayerHandoff should use --
    /// handles the per-game-minute cadence gate internally (safe to call every
    /// frame; it only actually rolls once per elapsed game-minute per bus),
    /// reads BusVehicleSystem condition to weight the chance, and filters
    /// eligible types by BusVehicleSystem.fuelType so an electric bus can never
    /// roll EngineFailure and a diesel bus can never roll BatteryTrip.</summary>
    public void TryRollBreakdown(int busID, BusVehicleSystem vehicle)
    {
        if (IsBusLocked(busID) || IsLimpingHome(busID)) return;
        if (BusScheduler.Instance == null) return;

        float nowMinute = BusScheduler.Instance.GameTimeMinutes;
        float lastRoll  = _lastRollGameMinute.TryGetValue(busID, out var v) ? v : nowMinute - 1f;
        if (nowMinute - lastRoll < 1f) return; // only once per elapsed game-minute
        _lastRollGameMinute[busID] = nowMinute;

        float chance = baseChancePerMinute * GetConditionMultiplier(vehicle);
        if (UnityEngine.Random.value >= chance) return;

        var eligible = GetEligibleTypes(vehicle);
        if (eligible.Count == 0) return;

        var type = eligible[UnityEngine.Random.Range(0, eligible.Count)];
        TriggerBreakdown(busID, type);
    }

    /// <summary>Worst-part-condition-driven multiplier. A pristine bus (100%
    /// on every part) rolls at baseChancePerMinute. A bus with a badly-worn
    /// part rolls up to maxConditionMultiplier times as often -- this is what
    /// actually ties breakdown odds to how a SPECIFIC bus has been driven,
    /// not just a flat per-series number.</summary>
    private float GetConditionMultiplier(BusVehicleSystem vehicle)
    {
        if (vehicle == null) return 1f;
        float worst = vehicle.WorstCondition; // 100 = perfect, 0 = failed
        float t = 1f - Mathf.Clamp01(worst / 100f); // 0 at perfect, 1 at failed
        return Mathf.Lerp(1f, maxConditionMultiplier, t);
    }

    private static List<BreakdownType> GetEligibleTypes(BusVehicleSystem vehicle)
    {
        var result = new List<BreakdownType>();
        bool isElectric = vehicle != null && vehicle.fuelType == BusVehicleSystem.FuelSystemType.Electric;

        foreach (BreakdownType t in Enum.GetValues(typeof(BreakdownType)))
        {
            var meta = GetMeta(t);
            if (meta.category == BreakdownCategory.Cosmetic) continue; // sub-breakdowns roll separately, see TryRollCosmeticSubBreakdown
            if (isElectric && !meta.affectsElectric) continue;
            if (!isElectric && !meta.affectsCombustion) continue;
            result.Add(t);
        }
        return result;
    }

    // ── Test helper ───────────────────────────────────────────────────────────
    public void TriggerRandomBreakdownForTest()
    {
        if (BusRegistry.ActiveBuses == null || BusRegistry.ActiveBuses.Count == 0)
        {
            Debug.LogWarning("[TEST] No active buses in registry.");
            return;
        }

        var keys  = new List<int>(BusRegistry.ActiveBuses.Keys);
        int busID = keys[UnityEngine.Random.Range(0, keys.Count)];
        var type  = (BreakdownType)UnityEngine.Random.Range(
            0, System.Enum.GetValues(typeof(BreakdownType)).Length);

        TriggerBreakdown(busID, type);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  METADATA TABLE — everything about how each type behaves lives here
    // ═════════════════════════════════════════════════════════════════════════
    public static BreakdownMeta GetMeta(BreakdownType type) => type switch
    {
        BreakdownType.BatteryTrip => new BreakdownMeta
        {
            category = BreakdownCategory.Electric, durationMinutes = 20f, requiresReplacement = true,
            severity = ShutdownSeverity.Full, affectsElectric = true, affectsCombustion = false,
            soundCue = "battery_trip_zh50ep", suggestedAction = "Bus is dead — wait for relief or limp-home to depot once cleared."
        },
        BreakdownType.InverterFault => new BreakdownMeta
        {
            category = BreakdownCategory.Electric, durationMinutes = 12f, requiresReplacement = true,
            severity = ShutdownSeverity.Partial, affectsElectric = true, affectsCombustion = false,
            soundCue = "inverter_fault_sizzle", suggestedAction = "Power cutting in/out — limp carefully, head toward depot."
        },
        BreakdownType.HVILFault => new BreakdownMeta
        {
            category = BreakdownCategory.Electric, durationMinutes = 15f, requiresReplacement = true,
            severity = ShutdownSeverity.Full, affectsElectric = true, affectsCombustion = false,
            soundCue = "hvil_fault_click", suggestedAction = "High-voltage interlock tripped — full stop required, wait for relief."
        },
        BreakdownType.EngineFailure => new BreakdownMeta
        {
            category = BreakdownCategory.Fuel, durationMinutes = 30f, requiresReplacement = true,
            severity = ShutdownSeverity.Full, affectsElectric = false, affectsCombustion = true,
            soundCue = "engine_failure_smoke", suggestedAction = "Engine's dead — wait for relief or limp-home to depot once cleared."
        },
        BreakdownType.Overheating => new BreakdownMeta
        {
            category = BreakdownCategory.Fuel, durationMinutes = 18f, requiresReplacement = true,
            severity = ShutdownSeverity.Partial, affectsElectric = false, affectsCombustion = true,
            soundCue = "overheat_smoke_hiss", suggestedAction = "Engine's running hot — reduce speed, head to depot before it gets worse."
        },
        BreakdownType.FuelPumpFailure => new BreakdownMeta
        {
            category = BreakdownCategory.Fuel, durationMinutes = 10f, requiresReplacement = false,
            severity = ShutdownSeverity.Partial, affectsElectric = false, affectsCombustion = true,
            soundCue = "fuel_pump_sputter", suggestedAction = "Engine sputtering — keep driving gently, should clear on its own."
        },
        BreakdownType.Leak => new BreakdownMeta
        {
            category = BreakdownCategory.Fuel, durationMinutes = 15f, requiresReplacement = false,
            severity = ShutdownSeverity.None, affectsElectric = false, affectsCombustion = true,
            soundCue = "ac_fuel_leak_hiss", suggestedAction = "AC refrigerant leak — no driving impact, will clear on its own."
        },
        BreakdownType.AirBrakeFailure => new BreakdownMeta
        {
            category = BreakdownCategory.Pneumatic, durationMinutes = 25f, requiresReplacement = true,
            severity = ShutdownSeverity.Full, affectsElectric = true, affectsCombustion = true,
            soundCue = "air_brake_hiss", suggestedAction = "Air brakes failed — full stop required, wait for relief."
        },
        BreakdownType.DoorPneumaticFailure => new BreakdownMeta
        {
            category = BreakdownCategory.Pneumatic, durationMinutes = 12f, requiresReplacement = false,
            severity = ShutdownSeverity.None, affectsElectric = true, affectsCombustion = true,
            soundCue = "door_pneumatic_clunk", suggestedAction = "Doors stuck — keep driving, should free up on its own."
        },
        BreakdownType.SuspensionAirFailure => new BreakdownMeta
        {
            category = BreakdownCategory.Pneumatic, durationMinutes = 12f, requiresReplacement = false,
            severity = ShutdownSeverity.None, affectsElectric = true, affectsCombustion = true,
            soundCue = "suspension_air_hiss", suggestedAction = "Riding low/uneven — no driving impact, will clear on its own."
        },
        BreakdownType.ACFailure => new BreakdownMeta
        {
            category = BreakdownCategory.Mechanical, durationMinutes = 15f, requiresReplacement = false,
            severity = ShutdownSeverity.None, affectsElectric = true, affectsCombustion = true,
            soundCue = "ac_failure_hum_stop", suggestedAction = "AC's out — no driving impact, will clear on its own."
        },
        BreakdownType.FlatTire => new BreakdownMeta
        {
            category = BreakdownCategory.Mechanical, durationMinutes = 10f, requiresReplacement = false,
            severity = ShutdownSeverity.Partial, affectsElectric = true, affectsCombustion = true,
            soundCue = "flat_tire_pop", suggestedAction = "Flat tire — limp mode, keep speed low until it clears."
        },

        // ── Cosmetic sub-breakdowns — bus runs completely normally, these
        //    just add a persistent audio/feel quirk for their duration.
        BreakdownType.DeepRPMGroan => new BreakdownMeta
        {
            category = BreakdownCategory.Cosmetic, durationMinutes = 20f, requiresReplacement = false,
            severity = ShutdownSeverity.None, affectsElectric = false, affectsCombustion = true,
            soundCue = "deep_rpm_groan", suggestedAction = "Just a weird noise — nothing to do, runs fine."
        },
        BreakdownType.StuckGear => new BreakdownMeta
        {
            category = BreakdownCategory.Cosmetic, durationMinutes = 20f, requiresReplacement = false,
            severity = ShutdownSeverity.None, affectsElectric = false, affectsCombustion = true,
            soundCue = "stuck_gear_whine", suggestedAction = "Transmission's hanging in one gear — runs fine, just won't shift right."
        },
        BreakdownType.RoughIdleLope => new BreakdownMeta
        {
            category = BreakdownCategory.Cosmetic, durationMinutes = 15f, requiresReplacement = false,
            severity = ShutdownSeverity.None, affectsElectric = false, affectsCombustion = true,
            soundCue = "rough_idle_lope", suggestedAction = "Rough idle — nothing to do, runs fine once moving."
        },
        BreakdownType.TurboWhistleLeak => new BreakdownMeta
        {
            category = BreakdownCategory.Cosmetic, durationMinutes = 20f, requiresReplacement = false,
            severity = ShutdownSeverity.None, affectsElectric = false, affectsCombustion = true,
            soundCue = "turbo_whistle_leak", suggestedAction = "Boost leak whistle — nothing to do, runs fine."
        },

        _ => new BreakdownMeta { category = BreakdownCategory.Mechanical, durationMinutes = 10f, requiresReplacement = false, severity = ShutdownSeverity.None, affectsElectric = true, affectsCombustion = true, soundCue = "", suggestedAction = "" }
    };

    /// <summary>Separate low-odds roll for the 4 cosmetic sub-breakdowns --
    /// kept independent of the main 12-type roll since these are meant to be
    /// rare flavor events, not part of the "is this bus actually in trouble"
    /// math. Combustion-only (all 4 are engine/tx character quirks).</summary>
    public void TryRollCosmeticSubBreakdown(int busID, BusVehicleSystem vehicle)
    {
        if (vehicle == null || vehicle.fuelType == BusVehicleSystem.FuelSystemType.Electric) return;
        if (IsBusLocked(busID)) return; // don't layer a cosmetic quirk on top of a real breakdown

        float nowMinute = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeMinutes : 0f;
        string key = "cosmetic_" + busID;
        float lastRoll = _lastRollGameMinute.TryGetValue(key.GetHashCode(), out var v) ? v : nowMinute - 1f;
        if (nowMinute - lastRoll < 1f) return;
        _lastRollGameMinute[key.GetHashCode()] = nowMinute;

        float chance = baseChancePerMinute * 0.5f * GetConditionMultiplier(vehicle); // cosmetic quirks slightly rarer than real breakdowns
        if (UnityEngine.Random.value >= chance) return;

        var cosmetics = new[] { BreakdownType.DeepRPMGroan, BreakdownType.StuckGear, BreakdownType.RoughIdleLope, BreakdownType.TurboWhistleLeak };
        var type = cosmetics[UnityEngine.Random.Range(0, cosmetics.Length)];
        TriggerBreakdown(busID, type);
    }
}