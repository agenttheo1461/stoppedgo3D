using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS VEHICLE SYSTEM — merged replacement for the old separate BusFuelSystem
//  and BusMaintenanceSystem components.
//
//  [REDESIGN] A physical bus is one vehicle with one fuel tank and one set of
//  wearing parts — there was never a real reason for these to be two separate
//  MonoBehaviours each doing their own GetComponent<NPCBusController>/
//  GetComponent<BusSimulationController> lookup, their own TryGetLiveState,
//  their own OnEnable catch-up pass. One component, one lookup, same data.
//
//  [BREAKDOWN 3.0] Stat block changed from Engine/Transmission/Wheels/Brakes/AC
//  to Engine/FuelBattery/Transmission/Pneumatics/AC — one stat per
//  BusBreakdownSystem category so eligibility/odds can be rolled per-category
//  instead of off one flat WorstCondition number. Wheels/Brakes are gone as
//  standalone stats (FlatTire now rolls off Pneumatics, same reservoir as the
//  rest of the air system; brake WEAR physically still happens, it's just not
//  its own breakdown-eligibility stat anymore).
//
//  Condition tiers (BusCondition, see FleetRosterData) are now CAPS, not flat
//  starting values -- Neglected/Worn/Standard/Pristine define the maximum any
//  stat can be repaired back up to, not a fixed number every stat starts
//  pinned at. A fresh bus starts AT its cap (nothing's worn it down yet); a
//  repair (MaintenanceBay, or an NPC's free depot repair) restores every stat
//  back up to that same cap, never above it.
// ═══════════════════════════════════════════════════════════════════════════════
[DisallowMultipleComponent]
public class BusVehicleSystem : MonoBehaviour
{
    // ── Fuel / Energy ────────────────────────────────────────────────────────
    public enum FuelSystemType { Diesel, CNG, Electric }

    [Header("Fuel Type (set once per bus prefab, or via FleetSeriesDefinition on spawn)")]
    public FuelSystemType fuelType = FuelSystemType.Diesel;
    [Tooltip("Hybrid-diesel-electric (XDE): still fuels with Diesel, just at better MPG. This does NOT change fuelType — set fuelType = Diesel and check this box instead.")]
    public bool isHybridAssist = false;

    [Header("Tank / Battery")]
    [Tooltip("Gallons (Diesel/CNG-as-DGE) or kWh (Electric).")]
    public float capacityUnits = 130f;
    [Range(0f, 1f)] public float currentFraction = 1f;

    [Header("Consumption")]
    [Tooltip("Miles per gallon (Diesel/CNG) — ignored if fuelType is Electric.")]
    public float milesPerGallon = 4.0f;
    [Tooltip("kWh consumed per mile — ignored unless fuelType is Electric.")]
    public float kwhPerMile = 2.2f;

    [Header("Fuel Warnings")]
    [Range(0f, 0.5f)] public float lowFuelFraction = 0.15f;
    public bool isLow { get; private set; }
    public System.Action onLowFuel;
    public System.Action onEmpty;

    // ── Wear / Condition ─────────────────────────────────────────────────────
    // [BREAKDOWN 3.0] One stat per BusBreakdownSystem category.
    public enum Part { Engine, FuelBattery, Transmission, Pneumatics, AC }
    private const int PART_COUNT = 5;

    [System.Serializable]
    public struct PartWear
    {
        [Range(0f, 100f)] public float condition; // 100 = perfect, 0 = failed
    }

    [Header("Condition (100 = perfect ceiling; actual ceiling is maxCondition, see below)")]
    public PartWear engine      = new PartWear { condition = 100f };
    public PartWear fuelBattery = new PartWear { condition = 100f };
    public PartWear transmission = new PartWear { condition = 100f };
    public PartWear pneumatics  = new PartWear { condition = 100f };
    public PartWear ac          = new PartWear { condition = 100f };

    [Header("Condition Ceiling (BREAKDOWN 3.0)")]
    [Tooltip("The MAX any stat above can currently be repaired up to -- set from this bus's BusCondition tier (Neglected=50, Worn=75, Standard=90, Pristine=100, see FleetMetadata.ConditionToNumeric). A repair restores every stat to exactly this value, never higher. Wear can still push a stat below this at any time.")]
    [Range(0f, 100f)] public float maxCondition = 100f;

    [Header("Wear Rates (condition lost per unit — tune to taste)")]
    [Tooltip("Condition lost per mile driven, engine/transmission base rate.")]
    public float drivetrainWearPerMile = 0.015f;
    [Tooltip("Extra condition lost per mile when accel input is harsh (>0.7), engine only.")]
    public float engineHarshAccelPenalty = 0.05f;
    [Tooltip("Condition lost per mile, Pneumatics (tires/air system combined).")]
    public float wheelWearPerMile = 0.02f;
    [Tooltip("Condition lost per second of braking, scaled by bkPd intensity (0..1) -- feeds Pneumatics (air brakes share the same reservoir as the rest of the pneumatic system).")]
    public float brakeWearPerBkPdSecond = 0.35f;
    [Tooltip("Condition lost per hour of A/C runtime.")]
    public float acWearPerRuntimeHour = 0.8f;
    [Tooltip("Condition lost per mile, Fuel/Battery system wear (fuel delivery components / battery pack degradation).")]
    public float fuelBatteryWearPerMile = 0.01f;

    [Header("Condition Warnings")]
    [Range(0f, 50f)] public float warnThreshold = 30f;
    public System.Action<Part> onPartNeedsService;

    // ── Shared live-state plumbing ──────────────────────────────────────────
    // Two possible drivers of this same physical bus — BusSimulationController
    // for whichever bus is currently player-possessed, NPCBusController for
    // autonomous driving. Both commonly coexist on one GameObject with only
    // one actually .enabled at a time — this reads whichever one is live
    // rather than assuming it's always one or the other.
    private NPCBusController _npc;
    private BusSimulationController _player;
    private float _lastRealTime;
    private bool  _emptyFired;
    private bool[] _warnedAlready = new bool[PART_COUNT];

    public float CurrentUnits => capacityUnits * currentFraction;
    public float RangeMilesRemaining => fuelType == FuelSystemType.Electric
        ? CurrentUnits / Mathf.Max(0.01f, kwhPerMile)
        : CurrentUnits * Mathf.Max(0.1f, milesPerGallon);

    /// <summary>Worst stat's condition — single overall "check engine" style
    /// indicator. BusBreakdownSystem now rolls per-CATEGORY odds off the
    /// individual stats (see GetCondition/GetConditionFraction below)
    /// instead of this, but it's kept for anything that just wants one
    /// number (UI summaries, etc).</summary>
    public float WorstCondition => Mathf.Min(engine.condition, fuelBattery.condition, transmission.condition, pneumatics.condition, ac.condition);

    void Awake()
    {
        _npc = GetComponent<NPCBusController>();
        _player = transform.root.GetComponentInChildren<BusSimulationController>(true);
        if (_npc == null && _player == null)
            Debug.LogError($"[BusVehicleSystem] '{name}' has neither NPCBusController nor BusSimulationController.");
    }

    // Pulls the shared subset of fields off whichever controller is actually
    // live right now, regardless of which class it is. One lookup instead of
    // the two nearly-identical ones the old separate components each had.
    private bool TryGetLiveState(out float spd, out float accel, out float bkPd, out bool running, out bool acKilled)
    {
        if (_player != null && _player.enabled)
        {
            spd = _player.spd; accel = _player.accel; bkPd = _player.bkPd; running = _player.running;
            acKilled = _player.audioEngine == null || !_player.audioEngine.batteryOn || !_player.audioEngine.acComfortOn;
            return true;
        }
        if (_npc != null && _npc.enabled)
        {
            spd = _npc.spd; accel = _npc.accel; bkPd = _npc.bkPd; running = _npc.running;
            acKilled = _npc.audioEngine == null || !_npc.audioEngine.batteryOn || !_npc.audioEngine.acComfortOn;
            return true;
        }
        spd = accel = bkPd = 0f; running = acKilled = false;
        return false;
    }

    void OnEnable()
    {
        if (_lastRealTime > 0f)
        {
            float elapsed = Time.time - _lastRealTime;
            if (elapsed > 1f)
            {
                // Same "historical" catch-up pattern the two original
                // components each had -- assume a modest average service
                // speed while pooled/disabled rather than pretending zero
                // time passed.
                float assumedAvgMph = 12f;
                float assumedMiles  = assumedAvgMph * (elapsed / 3600f) * 0.4f;
                ConsumeForMiles(assumedMiles);
                WearDrivetrain(assumedMiles);
            }
        }
        _lastRealTime = Time.time;
    }

    void Update()
    {
        _lastRealTime = Time.time;

        if (!TryGetLiveState(out float spd, out float accel, out float bkPd, out bool running, out bool acKilled))
            return;

        float dt = Time.deltaTime;
        float miles = spd * (dt / 3600f);

        if (miles > 0f)
        {
            ConsumeForMiles(miles);
            WearDrivetrain(miles, accel);
        }

        if (bkPd > 0.05f)
            pneumatics.condition = Mathf.Max(0f, pneumatics.condition - bkPd * brakeWearPerBkPdSecond * dt);

        if (running && !acKilled)
            ac.condition = Mathf.Max(0f, ac.condition - acWearPerRuntimeHour * (dt / 3600f));

        CheckWarnings();
    }

    /// <summary>Shared engine/transmission/pneumatics(tire)/fuel-battery wear
    /// application for both the live-tick path and the offline catch-up
    /// path above -- was inlined twice with slightly different field names
    /// before this rewrite; now one method, one set of rates.</summary>
    private void WearDrivetrain(float miles, float accel = 0f)
    {
        float engineWear = miles * drivetrainWearPerMile;
        if (accel > 0.7f) engineWear += miles * engineHarshAccelPenalty;
        engine.condition       = Mathf.Max(0f, engine.condition       - engineWear);
        transmission.condition = Mathf.Max(0f, transmission.condition - miles * drivetrainWearPerMile * 0.5f);
        pneumatics.condition   = Mathf.Max(0f, pneumatics.condition   - miles * wheelWearPerMile);
        fuelBattery.condition  = Mathf.Max(0f, fuelBattery.condition  - miles * fuelBatteryWearPerMile);
    }

    // ── Fuel ──────────────────────────────────────────────────────────────────
    private void ConsumeForMiles(float miles)
    {
        if (miles <= 0f || capacityUnits <= 0f) return;

        float unitsUsed = fuelType == FuelSystemType.Electric
            ? miles * kwhPerMile
            : miles / Mathf.Max(0.1f, milesPerGallon);

        currentFraction = Mathf.Clamp01(currentFraction - unitsUsed / capacityUnits);

        bool lowNow = currentFraction <= lowFuelFraction;
        if (lowNow && !isLow) onLowFuel?.Invoke();
        isLow = lowNow;

        if (currentFraction <= 0f && !_emptyFired) { _emptyFired = true; onEmpty?.Invoke(); }
        else if (currentFraction > 0f) _emptyFired = false;
    }

    public bool AcceptsStationType(FuelSystemType stationType) => stationType == fuelType;

    /// <summary>Adds units (gallons/DGE/kWh) over time — call every frame while refueling with unitsPerSecond * Time.deltaTime.</summary>
    public void AddFuelUnits(float units)
    {
        if (capacityUnits <= 0f) return;
        currentFraction = Mathf.Clamp01(currentFraction + units / capacityUnits);
        if (currentFraction > lowFuelFraction) isLow = false;
    }

    public string GetFuelLabel() => fuelType switch
    {
        FuelSystemType.Diesel  => isHybridAssist ? "Diesel (Hybrid)" : "Diesel",
        FuelSystemType.CNG     => "CNG",
        FuelSystemType.Electric => "Electric",
        _ => "?"
    };

    // ── Condition ────────────────────────────────────────────────────────────
    private void CheckWarnings()
    {
        CheckOne(Part.Engine,       engine.condition,       0);
        CheckOne(Part.FuelBattery,  fuelBattery.condition,  1);
        CheckOne(Part.Transmission, transmission.condition, 2);
        CheckOne(Part.Pneumatics,   pneumatics.condition,   3);
        CheckOne(Part.AC,           ac.condition,           4);
    }

    private void CheckOne(Part part, float condition, int idx)
    {
        bool needsService = condition <= warnThreshold;
        if (needsService && !_warnedAlready[idx]) { _warnedAlready[idx] = true; onPartNeedsService?.Invoke(part); }
        else if (!needsService) _warnedAlready[idx] = false;
    }

    public float GetCondition(Part part) => part switch
    {
        Part.Engine       => engine.condition,
        Part.FuelBattery  => fuelBattery.condition,
        Part.Transmission => transmission.condition,
        Part.Pneumatics   => pneumatics.condition,
        Part.AC           => ac.condition,
        _ => 100f
    };

    /// <summary>0-1 fraction of THIS bus's own ceiling (maxCondition), not of
    /// a flat 100 -- what BusBreakdownSystem's per-category odds should read.
    /// A Neglected bus sitting at its own full 50% cap is "healthy" for that
    /// bus (fraction = 1), not perpetually "half broken."</summary>
    public float GetConditionFraction(Part part) =>
        maxCondition > 0f ? Mathf.Clamp01(GetCondition(part) / maxCondition) : 0f;

    public void SetCondition(Part part, float value)
    {
        value = Mathf.Clamp(value, 0f, 100f);
        switch (part)
        {
            case Part.Engine:       engine.condition       = value; break;
            case Part.FuelBattery:  fuelBattery.condition  = value; break;
            case Part.Transmission: transmission.condition = value; break;
            case Part.Pneumatics:   pneumatics.condition   = value; break;
            case Part.AC:           ac.condition           = value; break;
        }
    }

    /// <summary>Restores every stat to this bus's own maxCondition ceiling --
    /// NOT a flat 100. Called by MaintenanceBay (player repairs) and by
    /// NPCBusController's free depot repair (NPC breakdown resolution).
    /// Both now go through this one method instead of each hardcoding their
    /// own "reset to 100" value.</summary>
    public void RepairToMax()
    {
        engine.condition = fuelBattery.condition = transmission.condition = pneumatics.condition = ac.condition = maxCondition;
    }

    /// <summary>Sets fuel type AND this bus's condition ceiling in one call --
    /// this is what FleetRosterData.BuildSlots calls on spawn so a series'
    /// age/condition tier actually reflects in the physical bus. A fresh bus
    /// starts AT its ceiling (nothing's worn it down yet).</summary>
    public void ApplySeriesDefaults(FuelSystemType type, bool hybridAssist, float conditionCap)
    {
        fuelType = type;
        isHybridAssist = hybridAssist;
        maxCondition = Mathf.Clamp(conditionCap, 0f, 100f);
        RepairToMax();
    }

#if UNITY_EDITOR
    [ContextMenu("Apply Real-World Defaults: Diesel 40ft")]
    private void ApplyDiesel40() { fuelType = FuelSystemType.Diesel; capacityUnits = 130f; milesPerGallon = 4.0f; isHybridAssist = false; }
    [ContextMenu("Apply Real-World Defaults: Diesel 60ft Artic")]
    private void ApplyDiesel60() { fuelType = FuelSystemType.Diesel; capacityUnits = 150f; milesPerGallon = 2.75f; isHybridAssist = false; }
    [ContextMenu("Apply Real-World Defaults: CNG 40ft")]
    private void ApplyCNG40() { fuelType = FuelSystemType.CNG; capacityUnits = 120f; milesPerGallon = 3.5f; isHybridAssist = false; }
    [ContextMenu("Apply Real-World Defaults: Hybrid-Diesel (XDE)")]
    private void ApplyHybrid() { fuelType = FuelSystemType.Diesel; capacityUnits = 130f; milesPerGallon = 5.5f; isHybridAssist = true; }
    [ContextMenu("Apply Real-World Defaults: Electric 40ft")]
    private void ApplyElectric40() { fuelType = FuelSystemType.Electric; capacityUnits = 400f; kwhPerMile = 2.2f; isHybridAssist = false; }
#endif
}