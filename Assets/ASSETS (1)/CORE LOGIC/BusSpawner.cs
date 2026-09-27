using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  ENGINE CONFIG  —  defines which engine/tx combos a bus slot can have
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class EngineConfig
{
    [Tooltip("Engine type for this slot")]
    public BusSimulationController.EngineType engineType = BusSimulationController.EngineType.L9N;

    [Tooltip("Transmission string: voith | allison | bae | h40ep")]
    public string txType = "voith";
    [Tooltip("0 = off. Old-bus sound preset index — meaning depends on txType, see BusAudioEngine.DoOldBusVariantCharacter.")]
    public bool diwaOpt1_1 = false;
public bool diwaOpt1_2 = false;
public bool diwaOpt1_3 = false;
public bool diwaOpt1_4 = false; // second D864.6 character: suppressed whine til late G1, hiss window, extended G1, audible piston firing
public bool diwaOpt1_5 = false; // "the shaker": no whine at all, deeper+louder core, rapid-fire click vibration-sim at idle
public bool diwaOpt1_6 = false; // "fourth voice": whine hidden like opt1_4 but quieter, extended two-tone G1, groan-only retarder
public bool diwaOpt1_7 = false; // "fifth voice": as opt1_6 but whine never hidden -- Wandler wind-up on move-off instead
public bool diwaOpt1_8 = false; // \"strained\": harder, surging whine (D864.6)
public int oldBusVariant = 0;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS SLOT  —  one entry in the spawner list (one real bus)
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class BusSlot
{
    [Header("Identity")]
    public int    fleetNumber;          // e.g. 20001 — you set this
    public string busLabel;             // optional display name

    [Header("Model")]
    [Tooltip("The prefab to instantiate for this bus")]
    public GameObject prefab;

    [Header("Spawn")]
    public Vector3    spawnPosition;
    public Vector3    spawnRotation;    // Euler angles

    [Header("Engine (leave both lists empty to randomize from global pool)")]
    [Tooltip("Allowed engine/tx combos for this bus. If empty, uses BusSpawner.globalEnginePool")]
    public List<EngineConfig> allowedEngines = new();

    [Header("Personality Overrides (0 = use global random range)")]
    [Range(0f, 2f)]   public float speedMultiplierOverride    = 0f;
    [Range(0f, 2f)]   public float dwellMultiplierOverride    = 0f;
    [Range(0f, 1f)]   public float aggressivenessOverride     = 0f;
    [Range(0f, 100f)] public float acLevelOverride            = 0f;
    public bool useEconomyModeOverride = false;
    public bool economyModeValue       = false;

    // Runtime reference
    [HideInInspector] public GameObject spawnedInstance;
    [HideInInspector] public NPCBusController spawnedController;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS SPAWNER
// ═══════════════════════════════════════════════════════════════════════════════
public class BusSpawner : MonoBehaviour
{
    public static BusSpawner Instance { get; private set; }

    // ── Inspector ─────────────────────────────────────────────────────────────
    [Header("Bus Slots (one per NPC bus)")]
    public List<BusSlot> busSlots = new();

    [Header("Global Engine Pool (used when slot has no allowedEngines)")]
    public List<EngineConfig> globalEnginePool = new();

    [Header("Global Personality Ranges")]
    [Tooltip("Random speed multiplier range for all buses (unless overridden per slot)")]
    public Vector2 speedMultiplierRange    = new Vector2(0.88f, 1.12f);
    [Tooltip("Random dwell time multiplier range")]
    public Vector2 dwellMultiplierRange    = new Vector2(0.8f,  1.3f);
    [Tooltip("Random aggressiveness range (0=cautious, 1=aggressive following)")]
    public Vector2 aggressivenessRange     = new Vector2(0.1f,  0.7f);
    [Tooltip("Random AC level range")]
    public Vector2 acLevelRange            = new Vector2(50f,   90f);
    [Tooltip("Chance a bus spawns in economy mode")]
    [Range(0f, 1f)] public float economyModeChance = 0.0f;

    [Header("Debug")]
    public bool logSpawns = true;
public AdData[] possibleAds; // [CHANGE] Was Material[] -- drag your AdData campaign assets here instead. Each carries correctly-sized art for every board type, so the spawner never has to know or guess which Material shape goes where.

// [ADD] Tracks which buses have already had their ad boards configured.
// SetupAdBoards is now called from multiple places (this spawner's own
// NPC-dispatch flow, AND BusManager.RegisterBus for late/handoff/player
// registrations) -- without this guard, a bus that gets registered twice
// (e.g. spawned as NPC, then later handed to the player) would silently
// re-roll a brand new random ad campaign the second time, which reads as
// the wrap changing on a bus that's supposedly the same physical vehicle.
private readonly HashSet<NPCBusController> _adsConfigured = new();

    // [ADD — memory leak fix] _adsConfigured only ever had entries added,
    // never removed. Every bus ever spawned across the whole session left a
    // permanent reference here even after being destroyed (relief, depot
    // retirement, respawn cycles) -- BusSpawner kept the destroyed bus's
    // full managed object graph alive forever since nothing else clears
    // this. Same shape as the M4/M5 fixes earlier in NPCBusController.cs
    // (unsubscribed events, an uncleaned collider cache) -- another "nothing
    // runs on destroy" leak, just in a different file. NPCBusController.OnDestroy()
    // calls this so the entry actually gets removed when a bus goes away.
    public void NotifyBusDestroyed(NPCBusController ctrl)
    {
        if (ctrl != null) _adsConfigured.Remove(ctrl);
    }
    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

private void Start()
{
    if (DepotManager.Instance == null) SpawnAll();
    // else DepotManager will call SpawnAll() after populating busSlots
}
    // ── Spawn ─────────────────────────────────────────────────────────────────
    public void SpawnAll()
    {
        if (BusManager.Instance == null)
        {
            Debug.LogError("[BusSpawner] BusManager not found — spawn aborted.");
            return;
        }

        int spawned = 0;
        foreach (var slot in busSlots)
        {
            if (slot.prefab == null)
            {
                Debug.LogWarning($"[BusSpawner] Slot fleet#{slot.fleetNumber} has no prefab — skipped.");
                continue;
            }

            SpawnBus(slot);
            spawned++;
        }

        if (logSpawns)
            Debug.Log($"[BusSpawner] Spawned {spawned} / {busSlots.Count} buses.");
    }

    private void SpawnBus(BusSlot slot)
    {
        // Instantiate prefab
        var go = Instantiate(slot.prefab,
                             slot.spawnPosition,
                             Quaternion.Euler(slot.spawnRotation));
        go.name = $"Bus_{slot.fleetNumber}";
        slot.spawnedInstance = go;

        // Get or add NPCBusController
var ctrl = go.GetComponentInChildren<NPCBusController>();
        if (ctrl == null)
        {
            Debug.LogError($"[BusSpawner] Prefab for fleet#{slot.fleetNumber} has no NPCBusController.");
            Destroy(go);
            return;
        }

        slot.spawnedController = ctrl;

        // Fleet number
        ctrl.fleetNumber = slot.fleetNumber;

        // Engine / TX
        var engineCfg = PickEngine(slot);
        ctrl.engineType = engineCfg.engineType;
        ctrl.tx         = engineCfg.txType;
ctrl.oldBusVariant = engineCfg.oldBusVariant;
        // Personality
        ctrl.personality = BuildPersonality(slot);
ctrl.diwaOpt1_1 = engineCfg.diwaOpt1_1;
ctrl.diwaOpt1_2 = engineCfg.diwaOpt1_2;
ctrl.diwaOpt1_3 = engineCfg.diwaOpt1_3;
ctrl.diwaOpt1_4 = engineCfg.diwaOpt1_4;
ctrl.diwaOpt1_5 = engineCfg.diwaOpt1_5;
ctrl.diwaOpt1_6 = engineCfg.diwaOpt1_6;
ctrl.diwaOpt1_7 = engineCfg.diwaOpt1_7;
ctrl.diwaOpt1_8 = engineCfg.diwaOpt1_8;
        // AC level
        ctrl.acLevel = slot.acLevelOverride > 0f
            ? Mathf.RoundToInt(slot.acLevelOverride)
            : Mathf.RoundToInt(Random.Range(acLevelRange.x, acLevelRange.y));

        // Economy mode
        ctrl.economyMode = slot.useEconomyModeOverride
            ? slot.economyModeValue
            : (Random.value < economyModeChance);

        // Register with BusManager -- [CHANGE] was a direct busPrefabPool.Add,
        // now routed through the real registration method so this spawn path
        // and any other path that activates a bus (player handoff, late/
        // depot registration) funnel through the SAME place, which is what
        // actually calls SetupAdBoards below (see BusManager.RegisterBus).
        BusManager.Instance.RegisterBus(ctrl);
        SetupAdBoards(ctrl); // idempotent -- harmless if RegisterBus already triggered it
        if (logSpawns)
            Debug.Log($"[BusSpawner] Fleet#{slot.fleetNumber} | {ctrl.engineType}/{ctrl.tx} " +
                      $"| spd×{ctrl.personality.speedMultiplier:F2} " +
                      $"| dwell×{ctrl.personality.dwellMultiplier:F2} " +
                      $"| agg {ctrl.personality.aggressiveness:F2}");
    }

    // [ADD] Pulled out of the inline spawn flow so it can be called from
    // ANYWHERE a bus becomes active -- not just this spawner's own NPC
    // dispatch path. BusManager.RegisterBus calls this too, which is what
    // actually closes the gap for the player's own bus and handoff swaps
    // (see PlayerHandoff.SetPlayerBus -> BusManager.RegisterBus).
    // Idempotent: safe to call more than once per bus, will only ever
    // configure ad boards (and the bike rack) the first time.
    public void SetupAdBoards(NPCBusController ctrl)
    {
        if (ctrl == null || _adsConfigured.Contains(ctrl)) return;
        _adsConfigured.Add(ctrl);
        var go = ctrl.gameObject;
        var meta = FleetMetadata.Get(ctrl.fleetNumber);

        // [FIX] Was GetComponentInChildren (singular) -- only ever found and
        // set ONE ad board even on a bus with multiple plane types
        // (king/back/mini). Every ad plane on the bus needs its own board
        // set, not just whichever one the hierarchy search happened to find
        // first.
        BusAdBoard[] adBoards = go.GetComponentsInChildren<BusAdBoard>();
        BikeRackController rack = go.GetComponentInChildren<BikeRackController>();

        if (rack != null && meta != null)
            rack.SetBikeRack(meta.hasBikeRack);

        if (adBoards.Length == 0) return;

        // Let's give it a 50% chance to have an ad, and a 50% chance to be clean.
        // One campaign picked per BUS, not per board -- a real bus runs one
        // wrap/campaign across all its ad slots, not a different random
        // advertiser on the side vs. the back. Each board then resolves its
        // own correctly-typed material from that same AdData; a board whose
        // type isn't part of this campaign just turns off on its own
        // (handled inside BusAdBoard.SetAd), never falls back to a
        // wrong-shaped material from a different board's slot.
        //
        // [FIX] Was picking from ALL of possibleAds with no regard for
        // whether the campaign actually covers any board type THIS bus has.
        // A campaign that only bought King art would get rolled onto a bus
        // whose boards are all Back/Mini, every board resolves null, and
        // the whole bus goes gray even though "ads exist" and the RNG says
        // it should have one. Now we only roll from campaigns that cover at
        // least one of this bus's actual board types.
        var eligibleAds = FilterAdsForBoards(adBoards);

        AdData campaign = (eligibleAds.Count > 0 && Random.value < 0.5f)
            ? eligibleAds[Random.Range(0, eligibleAds.Count)]
            : null; // NO AD. Every board on this bus shuts its plane off.

        foreach (var board in adBoards)
            board.SetAd(campaign);
    }

    // [ADD] Only campaigns that have art for at least one board type this
    // bus actually carries are eligible -- prevents rolling a King-only
    // campaign onto a bus with no King board (or vice versa) and getting a
    // silently all-gray bus.
    private List<AdData> FilterAdsForBoards(BusAdBoard[] adBoards)
    {
        var result = new List<AdData>();
        if (possibleAds == null) return result;

        foreach (var ad in possibleAds)
        {
            if (ad == null) continue;
            foreach (var board in adBoards)
            {
                if (ad.HasMaterialFor(board.boardType))
                {
                    result.Add(ad);
                    break;
                }
            }
        }
        return result;
    }

    // ── Engine selection ──────────────────────────────────────────────────────
    private EngineConfig PickEngine(BusSlot slot)
    {
        var pool = (slot.allowedEngines != null && slot.allowedEngines.Count > 0)
            ? slot.allowedEngines
            : globalEnginePool;

        if (pool == null || pool.Count == 0)
        {
            // Fallback default
            return new EngineConfig
            {
                engineType = BusSimulationController.EngineType.L9N,
                txType     = "voith",
            };
        }

        return pool[Random.Range(0, pool.Count)];
    }

    // ── Personality builder ───────────────────────────────────────────────────
    private DriverPersonality BuildPersonality(BusSlot slot)
    {
        return new DriverPersonality
        {
            speedMultiplier = slot.speedMultiplierOverride > 0f
                ? slot.speedMultiplierOverride
                : Random.Range(speedMultiplierRange.x, speedMultiplierRange.y),

            dwellMultiplier = slot.dwellMultiplierOverride > 0f
                ? slot.dwellMultiplierOverride
                : Random.Range(dwellMultiplierRange.x, dwellMultiplierRange.y),

            aggressiveness = slot.aggressivenessOverride > 0f
                ? slot.aggressivenessOverride
                : Random.Range(aggressivenessRange.x, aggressivenessRange.y),
        };
    }

    // ── Editor helper ─────────────────────────────────────────────────────────
#if UNITY_EDITOR
    [ContextMenu("Re-Spawn All Buses (Editor)")]
    private void EditorRespawn()
    {
        // Destroy existing spawned instances
        foreach (var slot in busSlots)
        {
            if (slot.spawnedInstance != null)
                DestroyImmediate(slot.spawnedInstance);
            slot.spawnedInstance  = null;
            slot.spawnedController = null;
        }

        if (Application.isPlaying)
            SpawnAll();
        else
            Debug.Log("[BusSpawner] Enter Play Mode to spawn buses.");
    }

    [ContextMenu("Auto-Number Fleet (starts at 20001)")]
    private void AutoNumberFleet()
    {
        int n = 20001;
        foreach (var slot in busSlots)
            slot.fleetNumber = n++;
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log($"[BusSpawner] Numbered {busSlots.Count} slots starting at 20001.");
    }
#endif
}