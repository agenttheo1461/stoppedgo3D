using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// ═══════════════════════════════════════════════════════════════════════════════
//  FLEET ROSTER IMPORTER
//  One-click builder for the finalized 316-bus roster + depot split, so you
//  don't have to hand-enter 18 FleetSeriesDefinition rows with correctly
//  ordered DepotAllocation chunks in the Inspector.
//
//  HOW TO USE:
//   1. Add this component to any GameObject in the scene (or a dummy one).
//   2. Drag in the target FleetRosterData asset and the 5 DepotData assets.
//   3. Drag in a prefab per bus body type (XD40/XD60/XDE40/XDE60/XN40/XN60/XE40/XE60).
//      If a body type's prefab is left empty, that series is skipped with a
//      warning (matches BuildSlots' existing null-prefab skip behavior).
//   4. Right-click the component header → "Import Finalized Roster", or use
//      the Tools menu item below.
//
//  This OVERWRITES def.series on the target asset. Back it up / use source
//  control before running.
// ═══════════════════════════════════════════════════════════════════════════════
public class FleetRosterImporter : MonoBehaviour
{
    [Header("Target")]
    public FleetRosterData targetRoster;

    [Header("Depots (all 5 required)")]
    public DepotData fairway;
    public DepotData transitway;
    public DepotData northwest;
    public DepotData central;
    public DepotData sterling;

    [Header("Prefabs by body type")]
    public GameObject prefabXD40;
    public GameObject prefabXD60;
    public GameObject prefabXDE40;
    public GameObject prefabXDE60;
    public GameObject prefabXN40;
    public GameObject prefabXN60;
    public GameObject prefabXE40;
    public GameObject prefabXE60;

    private struct Chunk
    {
        public DepotData depot;
        public int count;
        public Chunk(DepotData d, int c) { depot = d; count = c; }
    }

    private struct SeriesSpec
    {
        public string name, busType;
        public int start;
        public BusSimulationController.EngineType engine;
        public TransmissionType tx;
        public int modelYear;
        public bool artic;
        public GameObject prefab;
        public Chunk[] chunks;
    }

    [ContextMenu("Import Finalized Roster")]
    public void ImportFinalizedRoster()
    {
        if (targetRoster == null) { Debug.LogError("[FleetRosterImporter] No target FleetRosterData assigned."); return; }
        if (fairway == null || transitway == null || northwest == null || central == null || sterling == null)
        {
            Debug.LogError("[FleetRosterImporter] All 5 depots must be assigned.");
            return;
        }

        var E = BusSimulationController.EngineType.L9; // placeholder to appease switch below if needed

        var specs = new List<SeriesSpec>
        {
            // ── 1000s ────────────────────────────────────────────────────────
            new SeriesSpec {
                name = "1000 Series", busType = "XD40", start = 1001,
                engine = BusSimulationController.EngineType.ISL, tx = TransmissionType.B500R,
                modelYear = 2010, artic = false, prefab = prefabXD40,
                chunks = new[] { new Chunk(fairway, 10), new Chunk(central, 2), new Chunk(sterling, 1) }
            },
            new SeriesSpec {
                name = "1000 Series (Artic)", busType = "XDE60", start = 1051,
                engine = BusSimulationController.EngineType.ISL, tx = TransmissionType.ZH50EP,
                modelYear = 2010, artic = true, prefab = prefabXDE60,
                chunks = new[] { new Chunk(fairway, 6), new Chunk(sterling, 2) }
            },
            // ── 1100s ────────────────────────────────────────────────────────
            new SeriesSpec {
                name = "1100 Series", busType = "XN40", start = 1101,
                engine = BusSimulationController.EngineType.ISLG, tx = TransmissionType.B400R,
                modelYear = 2012, artic = false, prefab = prefabXN40,
                chunks = new[] { new Chunk(fairway, 8), new Chunk(central, 1), new Chunk(sterling, 1) }
            },
            // ── 1400s ────────────────────────────────────────────────────────
            new SeriesSpec {
                name = "1400 Series", busType = "XDE40", start = 1401,
                engine = BusSimulationController.EngineType.B67, tx = TransmissionType.BAE,
                modelYear = 2013, artic = false, prefab = prefabXDE40,
                chunks = new[] { new Chunk(fairway, 24), new Chunk(central, 3), new Chunk(sterling, 3) }
            },
            new SeriesSpec {
                name = "1400 Series (Artic)", busType = "XDE60", start = 1451,
                engine = BusSimulationController.EngineType.B67, tx = TransmissionType.HDS300,
                modelYear = 2013, artic = true, prefab = prefabXDE60,
                chunks = new[] { new Chunk(transitway, 10) }
            },
            // ── 1500s ────────────────────────────────────────────────────────
            new SeriesSpec {
                name = "1500 Series", busType = "XD40", start = 1501,
                engine = BusSimulationController.EngineType.ISL9, tx = TransmissionType.B400R,
                modelYear = 2015, artic = false, prefab = prefabXD40,
                chunks = new[] { new Chunk(central, 5), new Chunk(sterling, 5) }
            },
            // ── 1600s ────────────────────────────────────────────────────────
            new SeriesSpec {
                name = "1600 Series", busType = "XD40", start = 1601,
                engine = BusSimulationController.EngineType.L9, tx = TransmissionType.D8645,
                modelYear = 2016, artic = false, prefab = prefabXD40,
                chunks = new[] { new Chunk(central, 3), new Chunk(sterling, 2) }
            },
            new SeriesSpec {
                name = "1600 Series (Artic)", busType = "XD60", start = 1651,
                engine = BusSimulationController.EngineType.L9, tx = TransmissionType.B500R,
                modelYear = 2016, artic = true, prefab = prefabXD60,
                chunks = new[] { new Chunk(central, 5), new Chunk(sterling, 5) }
            },
            // ── 1700s ────────────────────────────────────────────────────────
            new SeriesSpec {
                name = "1700 Series", busType = "XDE40", start = 1701,
                engine = BusSimulationController.EngineType.L9, tx = TransmissionType.H40EP,
                modelYear = 2017, artic = false, prefab = prefabXDE40,
                chunks = new[] { new Chunk(central, 8), new Chunk(sterling, 7) }
            },
            new SeriesSpec {
                name = "1700 Series (Artic)", busType = "XDE60", start = 1751,
                engine = BusSimulationController.EngineType.L9, tx = TransmissionType.H50EP,
                modelYear = 2017, artic = true, prefab = prefabXDE60,
                chunks = new[] { new Chunk(central, 5), new Chunk(sterling, 5) }
            },
            // ── 1900s ────────────────────────────────────────────────────────
            new SeriesSpec {
                name = "1900 Series", busType = "XD40", start = 1901,
                engine = BusSimulationController.EngineType.L9, tx = TransmissionType.Voith,
                modelYear = 2019, artic = false, prefab = prefabXD40,
                chunks = new[] { new Chunk(northwest, 12), new Chunk(central, 4), new Chunk(sterling, 4) }
            },
            // ── 2000s ────────────────────────────────────────────────────────
            new SeriesSpec {
                name = "2000 Series", busType = "XN40", start = 2001,
                engine = BusSimulationController.EngineType.L9N, tx = TransmissionType.B3400xFE,
                modelYear = 2020, artic = false, prefab = prefabXN40,
                chunks = new[] { new Chunk(central, 10), new Chunk(sterling, 10) }
            },
            // ── 2100s (two TX sub-blocks) ────────────────────────────────────
            new SeriesSpec {
                name = "2100 Series", busType = "XN40", start = 2101,
                engine = BusSimulationController.EngineType.L9N, tx = TransmissionType.Voith,
                modelYear = 2021, artic = false, prefab = prefabXN40,
                chunks = new[] { new Chunk(northwest, 10), new Chunk(central, 10) }
            },
            new SeriesSpec {
                name = "2100 Series (ZF)", busType = "XN40", start = 2121,
                engine = BusSimulationController.EngineType.L9N, tx = TransmissionType.ZF,
                modelYear = 2021, artic = false, prefab = prefabXN40,
                chunks = new[] { new Chunk(sterling, 10) }
            },
            // ── 2200s ────────────────────────────────────────────────────────
            new SeriesSpec {
                name = "2200 Series", busType = "XE40", start = 2201,
                engine = BusSimulationController.EngineType.XE40, tx = TransmissionType.Electric,
                modelYear = 2023, artic = false, prefab = prefabXE40,
                chunks = new[] { new Chunk(northwest, 20), new Chunk(central, 8), new Chunk(sterling, 7) }
            },
            new SeriesSpec {
                // "Tasty Voith" — D864.6art race-inverter whine, see BusAudioEngine.
                name = "2200 Series (CNG Artic)", busType = "XN60", start = 2251,
                engine = BusSimulationController.EngineType.L9N, tx = TransmissionType.D8646Art,
                modelYear = 2023, artic = true, prefab = prefabXN60,
                chunks = new[] { new Chunk(northwest, 1), new Chunk(central, 15), new Chunk(sterling, 14) }
            },
            // ── 2300s ────────────────────────────────────────────────────────
            new SeriesSpec {
                name = "2300 Series", busType = "XE40", start = 2301,
                engine = BusSimulationController.EngineType.XE40, tx = TransmissionType.Electric,
                modelYear = 2024, artic = false, prefab = prefabXE40,
                chunks = new[] { new Chunk(northwest, 5), new Chunk(central, 13), new Chunk(sterling, 12) }
            },
            new SeriesSpec {
                name = "2300 Series (Artic)", busType = "XE60", start = 2351,
                engine = BusSimulationController.EngineType.XE60, tx = TransmissionType.ZF, // GetTxFor forces "zfave130" for XE60 regardless
                modelYear = 2024, artic = true, prefab = prefabXE60,
                chunks = new[] { new Chunk(central, 15), new Chunk(sterling, 15) }
            },
        };

        var newSeries = new List<FleetSeriesDefinition>();
        int grandTotal = 0;

        foreach (var s in specs)
        {
            int count = 0;
            foreach (var c in s.chunks) count += c.count;
            grandTotal += count;

            if (s.prefab == null)
                Debug.LogWarning($"[FleetRosterImporter] '{s.name}' has no prefab assigned for body type {s.busType} — series will be skipped by BuildSlots until you assign one.");

            var def = new FleetSeriesDefinition
            {
                seriesName = s.name,
                busType = s.busType,
                startFleetNumber = s.start,
                busCount = count, // fallback if allocations ever get cleared
                engineType = s.engine,
                transmission = s.tx,
                modelYear = s.modelYear,
                isArticulated = s.artic,
                prefab = s.prefab,
                depotAllocations = new List<DepotAllocation>()
            };

            foreach (var c in s.chunks)
                def.depotAllocations.Add(new DepotAllocation { depot = c.depot, busCount = c.count });

            newSeries.Add(def);
        }

        targetRoster.series = newSeries;
        Debug.Log($"[FleetRosterImporter] Imported {newSeries.Count} series, {grandTotal} total buses onto '{targetRoster.name}'.");

#if UNITY_EDITOR
        EditorUtility.SetDirty(targetRoster);
        AssetDatabase.SaveAssets();
#endif
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(FleetRosterImporter))]
public class FleetRosterImporterEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        GUILayout.Space(8);
        var importer = (FleetRosterImporter)target;
        if (GUILayout.Button("Import Finalized Roster (316 buses)", GUILayout.Height(32)))
        {
            importer.ImportFinalizedRoster();
        }
    }
}
#endif