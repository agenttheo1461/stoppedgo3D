using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public enum RoadCurveMode { ControlPoints, MathEquation }
public enum MathWaveform { Sine, Cosine, Arc, Polynomial, Zigzag }
public enum TrafficLightState { Red, Yellow, Green }

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS STOP DATA
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class BusStopData
{
    [Header("Identity")]
    public string stopCode;
    public string stopName;

    [Header("Road Link")]
    public string parentRoadCode;
    [Range(0f, 1f)] public float tValue;

    [Header("Properties")]
    public bool hasShelter;
    public bool isTerminal;
    public bool isLayover;
    public bool isAccessible = true;

    [System.NonSerialized] public RoadSegment resolvedRoad;
    [System.NonSerialized] public GameObject  markerSphere;

    // ── Serving-routes registry ────────────────────────────────────────────
    // Which (route, direction) pairs actually stop here — pushed onto the
    // stop directly by BusTrackerService.RebuildStopServiceIndex() rather
    // than re-derived by scanning every route each time someone clicks this
    // stop. Rebuilt proactively (on a timer + whenever a route asset is
    // edited), so a stop click is just reading data already sitting here,
    // always current, instead of a lazy per-route cache that can go stale.
    [System.Serializable]
    public struct ServingRoute
    {
        public BusRouteData route;
        public bool         outbound;
    }
    [System.NonSerialized] public List<ServingRoute> servingRoutes = new List<ServingRoute>();

    private const float CurbOffset  = 2.5f;
    private const float SphereHeight = 15f;

public Vector3 GetWorldPosition()
{
    if (resolvedRoad == null) return Vector3.zero;
    // [FIX] Was resolvedRoad.EvaluatePosition(tValue) — the RAW, unflattened
    // spline position. RoadSegment.BuildMeshChunks() snaps any near-ground
    // sampled point (real Y in [AutoFlatMinY, AutoFlatMaxY]) up to
    // AutoFlatHeight before building the visible/collidable mesh, but that
    // snapping only ever lived inside BuildMeshChunks — nothing else could
    // see it. So every stop on a road authored with y=0 control points (i.e.
    // almost all of them) sat at the raw y=0 while the actual road surface
    // right next to it had been snapped up to 0.7 — stops were consistently
    // embedded below the visible road by a fixed amount, which reads exactly
    // like "stuck at a fixed height" once you go looking for a road that
    // isn't flat. EvaluateSurfacePosition applies the same snap so stops
    // always sit on whatever height the road mesh actually ended up at,
    // flat or elevated.
    Vector3 pos     = resolvedRoad.EvaluateSurfacePosition(tValue);
    Vector3 tangent = resolvedRoad.EvaluateTangent(tValue);
    Vector3 right   = Vector3.Cross(Vector3.up, tangent).normalized;

    float activeOffset = 3.0f; // Safe fallback just in case CityManager is offline
    
    if (CityManager.Instance != null)
    {
        var def = CityManager.Instance.roadDefinitions.Find(r => r.roadCode == parentRoadCode);
        if (def != null)
        {
            // YOUR MATH: Match the pedestrian network
            activeOffset = (def.roadWidth / 2f) + 0.5f;
            
            // Safety measure: If this is a rural road with NO sidewalks, 
            // maybe push the stop further into the grass so they aren't hit by traffic
            if (!def.hasSidewalks) 
            {
                activeOffset += 1.5f; 
            }
        }
    }

    return pos + right * activeOffset;
}
    public Vector3    GetSpherePosition() => GetWorldPosition() + Vector3.up * SphereHeight;
    public Quaternion GetWorldRotation()
    {
        if (resolvedRoad == null) return Quaternion.identity;
        return Quaternion.LookRotation(resolvedRoad.EvaluateTangent(tValue), Vector3.up);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  CITY MANAGER
// ═══════════════════════════════════════════════════════════════════════════════
public class CityManager : MonoBehaviour
{
    // [ADD] Bake-once cache: a content hash over roadDefinitions/
    // stopDefinitions/manualIntersections, stamped after a full BuildCity()
    // rebuild. Stored as a plain serialized field on this MonoBehaviour --
    // not a separate .asset -- so it (and the generated road/intersection/
    // stop GameObjects sitting under this transform) travel with the scene
    // itself: save the scene once after building in the Editor, and every
    // later load (Play Mode, or a real game launch) with unchanged source
    // data reuses what's already there instead of destroying and
    // regenerating it. See BuildCity()/ComputeCitySourceHash() below.
    [SerializeField, HideInInspector] private string _cachedCityBuildHash = "";

    /// <summary>[ADD] Loose end from the bake-once cache: ComputeCitySourceHash
    /// only covers roadDefinitions/stopDefinitions/manualIntersections, so a
    /// road-only Material swap (or anything else outside that hash) won't
    /// invalidate the cache on its own. Right-click CityManager in the
    /// Inspector → "Force Rebuild City" to blow away the stamped hash and
    /// force BuildCity()'s next call to do a real full rebuild regardless of
    /// whether the hash would otherwise have matched.</summary>
    [ContextMenu("Force Rebuild City (ignore mesh cache)")]
    public void ForceRebuildCity()
    {
        _cachedCityBuildHash = "";
        BuildCity();
        Debug.Log("[CityManager] Force-rebuilt — mesh cache cleared and regenerated from scratch.");
    }

    public List<IntersectionDefinition> manualIntersections = new List<IntersectionDefinition>();

    [Header("Bus References (Player)")]
    public Transform               busTransform;
    public BusSimulationController busController;

    [Header("Prefabs")]
    public GameObject stopPrefab;

    [Header("Routes (ScriptableObject assets)")]
    public BusRouteData[] routes;

    [Header("Stop Detection")]
    public float stopDetectRadius = 80f;

    [Header("Procedural Intersections")]
    public bool spawnIntersections  = true;
    public bool includeTrafficLights = true;
    public bool leftHandTraffic     = false;
    public int  intersectionCheckResolution = 50;

    /// <summary>Marks an auto-detected crossing/T-junction between two road
    /// codes as MAJOR (order doesn't matter). Manual junctions have their
    /// own isMajorIntersection checkbox directly on IntersectionDefinition;
    /// auto-detected ones have no per-instance object to attach a bool to,
    /// so they're matched by which two roads meet there instead.</summary>
    [System.Serializable]
    public class MajorIntersectionMarker
    {
        public string roadACode;
        public string roadBCode;
    }
    [Tooltip("Auto-detected crossings/T-junctions between these road-code pairs stay on their normal full light cycle overnight instead of flashing. Manual junctions use their own 'Is Major Intersection' checkbox instead.")]
    public List<MajorIntersectionMarker> majorIntersectionRoadPairs = new List<MajorIntersectionMarker>();

    private bool IsAutoIntersectionMajor(string codeA, string codeB)
    {
        if (majorIntersectionRoadPairs == null) return false;
        foreach (var m in majorIntersectionRoadPairs)
        {
            if (m == null) continue;
            if ((m.roadACode == codeA && m.roadBCode == codeB) ||
                (m.roadACode == codeB && m.roadBCode == codeA))
                return true;
        }
        return false;
    }
    public float junctionPavementSize = 12f;
    [Tooltip("Universal arm length for every junction star, in world units. Arms taper to a point at the tip, so length was never actually bound by road width -- it's now independent of it entirely. This is the base length before junctionSizeMultiplier and curve-shrink are applied.")]
    public float junctionArmBaseLength = 9f;
    [Tooltip("Overall scale of the star-shaped junction pad. Was hardcoded at 0.6 -- raise this to make every junction pad bigger.")]
    public float junctionSizeMultiplier = 0.9f;

    [Header("Traffic Light Materials (Optional)")]
    public Material lightMatRed;
    public Material lightMatYellow;
    public Material lightMatGreen;
    public Material lightMatPavement;
    public Material lightMatPole;

    [Header("Stop Sphere Materials")]
    public Material regularStopMaterial;
    public Material terminalStopMaterial;

    [Header("Road Network")]
    public List<RoadSegmentDefinition> roadDefinitions = new List<RoadSegmentDefinition>();

    [Header("Road Center Line Markings")]
    [Tooltip("Flat yellow line prefab, authored long and straight (as if for one dead-straight road). It gets CHOPPED into short segments (see lineChopFactor) and re-instanced along every road's actual spline, so it can follow curves instead of cutting corners.")]
    public GameObject yellowLinePrefab;
    [Tooltip("LEAVE AT 0 -- the prefab's real length along local +Z is measured automatically from its mesh bounds, which is what the stretch math needs. Only set this by hand to override that measurement.")]
    public float yellowLinePrefabLength = 0f;
    [Tooltip("Max line pieces spawned per clear stretch. This is the hard cap on instance count -- pieces get stretched longer to fill the stretch rather than more pieces being added.")]
    public int maxSegmentsPerRun = 12;
    [Tooltip("Preferred world length of each stretched line piece. Bigger = fewer objects. A curved stretch still gets at least a few pieces so it can follow the curve; the cap above always wins.")]
    public float targetSegmentLength = 25f;
    [Range(0.01f, 1f)]
    [Tooltip("Fraction of the prefab's original length each spawned segment is scaled down to (e.g. 0.1 = each instance is 1/10th the original prefab's length). Smaller = hugs curves tighter, but spawns more instances.")]
    public float lineChopFactor = 0.1f;
    [Tooltip("Alternate solid/gap every other chopped segment for a classic dashed center line. Off (default) = solid unbroken line made of butt-joined chopped segments -- this is almost always what you want since dashing is what makes segment 0001/0003/etc. appear 'missing'.")]
    public bool dashedCenterLine = false;
    public float lineYOffset = 0.02f;
    [Tooltip("Multiplier applied to each intersection's arm length when carving out the no-line box around it -- >1 pulls the line back further from the junction than the pavement mesh itself.")]
    public float intersectionLineClearance = 1.15f;

    [Header("Center Line Intersection Cover")]
    [Tooltip("At every auto-detected crossing/T-junction -- the SAME detection used to clear the yellow line -- spawn a plain pavement shape using the crossing roads' own material and NO traffic lights, just to guarantee no line remnant can ever peek through. Shape follows real geometry: a square/diamond for a true 4-arm crossing, a triangle for a 3-arm T-junction. Two roads that both just dead-end at the same spot get no cover shape at all -- the line clearing handles that case tightly on its own instead.")]
    public bool spawnCenterLineCoverDiamonds = true;
    [Tooltip("Optional override material for the cover diamonds. Leave null to reuse whichever of the two crossing roads is wider.")]
    public Material centerLineCoverMaterial;
    [Tooltip("Local height above the road surface the cover mesh sits at -- must be higher than lineYOffset above so it actually covers the line rather than sitting underneath it.")]
    public float centerLineCoverYOffset = 0.03f;

    [Header("Paint All Roads")]
    [Tooltip("Applied to every road's roadMaterial when you run 'Paint All Roads' below -- for actual textured roads (asphalt, baked lane markings). Doesn't touch individual roads until you actually run it.")]
    public Material paintAllRoadsMaterial;

    /// <summary>Applies paintAllRoadsMaterial to every road in one shot, then
    /// rebuilds so the change is visible immediately, then exports a fresh
    /// backup so the persisted data (not just the live scene) reflects the
    /// new material too -- otherwise a restore from an older backup would
    /// silently undo the paint job.</summary>
    [ContextMenu("Paint All Roads")]
    public void PaintAllRoads()
    {
        if (paintAllRoadsMaterial == null)
        {
            Debug.LogWarning("[CityManager] Paint All Roads: no paintAllRoadsMaterial assigned -- nothing to do.");
            return;
        }

        foreach (var def in roadDefinitions)
        {
            def.roadMaterial = paintAllRoadsMaterial;
        }

        Debug.Log($"[CityManager] Painted {roadDefinitions.Count} road(s) → material {paintAllRoadsMaterial.name}.");
        BuildCity();

#if UNITY_EDITOR
        var exporter = GetComponent<CityDataExporter>();
        if (exporter != null)
        {
            exporter.ExportBackup();
            Debug.Log("[CityManager] Backup exported with the new road material.");
        }
        else
        {
            Debug.LogWarning("[CityManager] No CityDataExporter on this GameObject -- " +
                              "paint applied to the live scene, but no backup was written. " +
                              "Add a CityDataExporter component if you want paint jobs to persist to backups automatically.");
        }
#endif
    }

    [Header("Bus Stops")]
    public List<BusStopData> stopDefinitions = new List<BusStopData>();

    // ── Runtime ───────────────────────────────────────────────────────────────
    private RoadSegment[]  _roads;
    private BusStopData[]  _stops;

    /// Public read-only view of all built road segments — used by RoadGraph to build
    /// the pathfinding graph without duplicating road-building logic.
    public IReadOnlyList<RoadSegment> AllRoads => _roads;
    public IReadOnlyList<RoadSegmentDefinition> AllRoadDefinitions => roadDefinitions;

    /// The auto-built pathfinding graph — geometric intersections/endpoints only,
    /// completely independent of traffic-light junctions (see DetectAndSpawnIntersections,
    /// which is a separate visual/timing system). Built once after roads exist.
    public RoadGraph Graph { get; private set; }

    private Dictionary<string, RoadSegment> _roadByCode = new();
    private Dictionary<string, BusStopData> _stopByCode = new();

    private List<GameObject> _spawnedStops  = new();
    private List<GameObject> _markerSpheres = new();
    private List<GameObject> _spawnedLineSegments = new List<GameObject>();
    private List<Vector3>    _spawnedIntersectionPositions = new List<Vector3>();

    /// <summary>Oriented rectangular no-paint zone around one intersection --
    /// forward/right are the two crossing roads' real directions (not
    /// world axes), so this correctly follows the junction even when roads
    /// meet at an oblique angle, not just a perfect 90°.</summary>
    private struct IntersectionClearBox
    {
        public Vector3 center;
        public Vector3 forward;           // roadA direction (normalized)
        public Vector3 right;             // roadB direction (normalized)
        public float   halfExtentForward; // reach along `forward`
        public float   halfExtentRight;   // reach along `right`

        /// When true this is a plain circular (XZ) zone of `radius` instead
        /// of an oriented rectangle. Used for junctions discovered via the
        /// RoadGraph, where the meeting point is known reliably but there's
        /// no single meaningful pair of "the two crossing roads" to orient a
        /// rectangle by (a node can join 3, 4, 5+ road segments).
        public bool  useRadius;
        public float radius;
    }
    private readonly List<IntersectionClearBox> _intersectionClearBoxes = new List<IntersectionClearBox>();

    /// <summary>Everything needed to build a cover diamond at one detected
    /// crossing/T-junction -- captured at the exact moment RegisterClearBox
    /// finds it, so the diamond's arm geometry (real/phantom detection via
    /// tA/tB) is derived from the same data the line-clearing box already
    /// used, guaranteeing the cover and the line gap line up.</summary>
    private struct DiamondSpec
    {
        public Vector3     pos;
        public Vector3     dirA, dirB;
        public float       widthA, widthB;
        public float       tA, tB;
        public RoadSegment roadA, roadB;
    }
    private readonly List<DiamondSpec> _centerLineCoverSpecs = new List<DiamondSpec>();

    /// <summary>Shared real/phantom-arm threshold: a road with t within this
    /// distance of 0 or 1 at a junction is treated as ENDING there rather
    /// than passing through. Used everywhere arm count is derived (clear-box
    /// sizing, cover diamonds, lit junction meshes) so all three always
    /// agree on how many arms a given spot actually has.</summary>
    private const float ArmEndpointEps = 0.03f;

    private Vector3 _lastBusPos = Vector3.one * float.MaxValue;

    private Material _autoBlue;
    private Material _autoYellow;
    private Material _autoLightRed, _autoLightYellow, _autoPave;

    private readonly List<JunctionLaneData> _junctionLanes = new();
public static CityManager Instance;
    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void Awake()
    {
        Instance = this;
        BuildMarkerMaterials();
        PopulateDefaultsIfEmpty();
    }
/// <summary>World-space positions of every auto-detected/manual road intersection,
/// used by SidewalkNetwork to place pedestrian-crossing junction hubs.</summary>
public List<Vector3> GetSpawnedIntersectionPositions() => _spawnedIntersectionPositions;
    private void Start()
    {
        BuildCity();
    }

    /// <summary>The exact pipeline Start() used to run inline, extracted so
    /// it can also be called directly from Edit Mode. Start() only ever
    /// fires in Play Mode — BusStopMaker/BusRouteMaker/CityLineMaker are now
    /// EditorWindows with no Play button in their workflow at all, so
    /// without this, roadDefinitions/stopDefinitions/etc never actually get
    /// built into live RoadSegment/lookup data outside Play Mode, which is
    /// exactly why those tools were showing "Road not built yet" forever.
    ///
    /// Calling this from a button in those tools (rather than automatically
    /// on every OnGUI/OnEnable) keeps rebuild timing explicit and in your
    /// control, since this does real work — mesh generation, intersection
    /// detection, pathfinding graph construction — not something you want
    /// silently re-running on every repaint.</summary>
    public void BuildCity()
    {
        BuildRoadsFromDefinitions();
        BuildLookups();
        ResolveReferences();

        // [ADD] Bake-once cache gate for road mesh GENERATION specifically --
        // deliberately narrow. BuildRoadMeshes() is the one step in this
        // whole pipeline confirmed to have no other side effect than
        // spawning "Road_<code>" mesh containers (no bookkeeping list, no
        // other field it populates) -- see its own body. Every other step
        // below (intersections, line markings, stops) populates a private,
        // non-serialized tracking list (_spawnedIntersectionPositions,
        // _intersectionClearBoxes, _spawnedStops, etc.) that would go stale
        // after a fresh process launch if skipped, even though the
        // GameObjects it tracks survived via scene serialization -- those
        // lists are NOT Unity-serialized (private, no [SerializeField]), so
        // they'd silently desync from the scene. Only the road meshes are
        // safe to skip regenerating; everything else still runs in full,
        // every time, exactly as before this cache existed.
        string sourceHash = ComputeCitySourceHash();
        bool canReuseRoadMeshes = !string.IsNullOrEmpty(_cachedCityBuildHash)
            && sourceHash == _cachedCityBuildHash
            && HasExistingRoadMeshContainers();

        // [FIX] Wipes every child GameObject before rebuilding — see
        // ClearAllBuiltChildren's own comment. This used to only clear
        // "Road_*" containers specifically, but manual/auto-detected
        // junctions and placed stop props (further down this method) had
        // the exact same accumulate-on-repeated-Edit-mode-build problem.
        // [ADD] Now preserves the existing "Road_*" mesh containers when
        // canReuseRoadMeshes is true, since those are exactly what's being
        // kept instead of regenerated below.
        ClearAllBuiltChildren(preserveRoadMeshes: canReuseRoadMeshes);
        _spawnedLineSegments.Clear();
        _intersectionClearBoxes.Clear();
        _centerLineCoverSpecs.Clear();

        BuildTrafficMaterials();
        ApplyPerRoadMaterialOverrides();
        if (!canReuseRoadMeshes)
            BuildRoadMeshes();

        // Auto-builds a fully connected pathfinding graph from raw road geometry —
        // every crossing and T-junction becomes a node whether or not it has a
        // traffic light. Independent of DetectAndSpawnIntersections() below, which
        // only handles the visual/timing layer for lit junctions.
        Graph = RoadGraph.Build(_roads, roadDefinitions);

        // Manual junctions defined in Inspector
        foreach (var def in manualIntersections)
            SpawnManualJunction(def);

        // Auto-detect intersections from road geometry (THIS was missing the call)
        if (spawnIntersections)
            DetectAndSpawnIntersections();

        // Every intersection above (manual + auto-detected) has already
        // registered its clearance box by the time we get here. On top of
        // that, scan EVERY road pair for geometric crossings unconditionally
        // (see ComputeGeometricLineClearBoxes) so a crossing with no visual
        // junction pad -- spawnIntersections off, canHaveIntersection off on
        // one of the roads, etc. -- still gets its line marking clipped.
        ComputeGeometricLineClearBoxes();
        ComputeGraphNodeLineClearBoxes();
        SpawnCenterLineMarkings();
        SpawnCenterLineCoverDiamonds();

        PlaceStops();
        ResolveRoutes();

        _cachedCityBuildHash = sourceHash;
    }

    /// <summary>Whether a previous build's road mesh containers are still
    /// sitting under this transform (survives if the scene was saved after
    /// building) -- the thing BuildCity() reuses instead of regenerating
    /// when the source hash also matches.</summary>
    private bool HasExistingRoadMeshContainers()
    {
        foreach (Transform child in transform)
            if (child.name.StartsWith("Road_")) return true;
        return false;
    }

    /// <summary>Cheap content hash over the Inspector-authored source data
    /// BuildCity() actually consumes to generate meshes/intersections/stops
    /// -- roadDefinitions, stopDefinitions, manualIntersections. Used to gate
    /// the bake-once cache above: any edit to this data changes the hash and
    /// forces a full rebuild next time BuildCity() runs. Deliberately does
    /// NOT hash roadMaterial/roadColor (a material swap doesn't change mesh
    /// topology or the pathfinding graph) -- swapping only a road's material
    /// won't by itself invalidate the cache; a real geometry/lane/stop edit
    /// will.</summary>
    private string ComputeCitySourceHash()
    {
        var sb = new System.Text.StringBuilder();

        if (roadDefinitions != null)
        {
            foreach (var r in roadDefinitions)
            {
                if (r == null) { sb.Append("null;"); continue; }
                sb.Append(r.roadCode).Append('|').Append(r.roadWidth).Append('|').Append(r.meshResolution)
                  .Append('|').Append(r.canHaveIntersection).Append('|').Append(r.isOneWay).Append('|').Append(r.reverseFlow)
                  .Append('|').Append(r.hasSidewalks).Append('|').Append(r.hideCenterLine)
                  .Append('|').Append(r.laneCount).Append('|').Append(r.laneWidth).Append('|').Append(r.speedLimit)
                  .Append('|').Append(r.curveMode).Append('|').Append(r.greenTime);
                if (r.controlPoints != null)
                    foreach (var p in r.controlPoints)
                        sb.Append('|').Append(p.x).Append(',').Append(p.y).Append(',').Append(p.z);
                sb.Append('|').Append(r.mathStart).Append('|').Append(r.mathEnd).Append('|').Append(r.waveform)
                  .Append('|').Append(r.amplitude).Append('|').Append(r.frequency).Append('|').Append(r.phase).Append('|').Append(r.sampleCount);
                sb.Append(';');
            }
        }

        sb.Append("##STOPS##");
        if (stopDefinitions != null)
        {
            foreach (var s in stopDefinitions)
            {
                if (s == null) { sb.Append("null;"); continue; }
                sb.Append(s.stopCode).Append('|').Append(s.stopName).Append('|').Append(s.parentRoadCode).Append('|').Append(s.tValue)
                  .Append('|').Append(s.hasShelter).Append('|').Append(s.isTerminal).Append('|').Append(s.isLayover).Append('|').Append(s.isAccessible)
                  .Append(';');
            }
        }

        sb.Append("##MANUAL_INTERSECTIONS##");
        if (manualIntersections != null)
        {
            foreach (var m in manualIntersections)
            {
                if (m == null) { sb.Append("null;"); continue; }
                sb.Append(m.roadACode).Append('|').Append(m.roadBCode).Append('|').Append(m.position)
                  .Append('|').Append(m.isMajorIntersection)
                  .Append('|').Append(m.lightNorth).Append('|').Append(m.lightSouth).Append('|').Append(m.lightEast).Append('|').Append(m.lightWest)
                  .Append('|').Append(m.roadAGreenTime).Append('|').Append(m.roadBGreenTime).Append('|').Append(m.yellowTime)
                  .Append(';');
            }
        }

        sb.Append("##FLAGS##").Append(spawnIntersections);

        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            byte[] hash  = md5.ComputeHash(bytes);
            return System.Convert.ToBase64String(hash);
        }
    }

    /// <summary>Edit Mode has no "runs exactly once per session" guarantee
    /// the way Play Mode's Start() does — you can click "Build City Now" as
    /// many times as you want while iterating. Every generated GameObject
    /// (road mesh containers, manual/auto-detected junctions, placed stop
    /// props — everything BuildCity() below creates) was piling up as
    /// duplicates on each repeated Edit-mode call, since none of it had any
    /// cleanup step. This was never a problem in Play Mode since Start()
    /// only ever fires once per session. Now wipes every child of this
    /// object before rebuilding, so repeated builds replace cleanly instead
    /// of accumulating. If you have hand-placed objects parented under
    /// CityManager that AREN'T meant to be regenerated, move them out from
    /// under this transform first — this clears everything underneath it,
    /// no exceptions.</summary>
    private void ClearAllBuiltChildren(bool preserveRoadMeshes = false)
    {
        var toRemove = new List<GameObject>();
        foreach (Transform child in transform)
        {
            if (preserveRoadMeshes && child.name.StartsWith("Road_")) continue;
            toRemove.Add(child.gameObject);
        }
        foreach (var go in toRemove)
            DestroyImmediate(go);
    }

    private void Update()
    {
        if (busTransform == null || busController == null) return;
        if (Vector3.Distance(busTransform.position, _lastBusPos) < 5f) return;
        _lastBusPos = busTransform.position;
        RefreshNearbyStops();
    }

    // ── Manual junction ───────────────────────────────────────────────────────
    private void SpawnManualJunction(IntersectionDefinition def)
    {
        var roadA = GetRoad(def.roadACode);
        var roadB = GetRoad(def.roadBCode);
        if (roadA == null || roadB == null) return;

        var roadDefA = roadDefinitions.Find(r => r.roadCode == def.roadACode);
        var roadDefB = roadDefinitions.Find(r => r.roadCode == def.roadBCode);
        if (roadDefA == null || roadDefB == null) return;

        Vector3 dirA = roadA.EvaluateTangent(0.5f).normalized;
        Vector3 dirB = roadB.EvaluateTangent(0.5f).normalized;

        CreateProceduralJunction(
            def.position, dirA, dirB,
            roadA.roadWidth, roadB.roadWidth,
            roadA.roadCode, roadB.roadCode,
            roadDefA, roadDefB,
            def.roadAGreenTime, def.roadBGreenTime,
            0.5f, 0.5f, // manual junctions carry no endpoint info -- assume a full through-crossing on both roads
            def.isMajorIntersection,
            def.headsANear, def.headsAFar, def.headsBNear, def.headsBFar); // [ADD] tuned lights -- empty by default, see IntersectionDefinition
    }

    // ── Auto intersection detection ───────────────────────────────────────────
    private void DetectAndSpawnIntersections()
    {
        _spawnedIntersectionPositions.Clear();
        for (int i = 0; i < _roads.Length; i++)
        {
            if (!roadDefinitions[i].canHaveIntersection) continue;
            for (int j = i + 1; j < _roads.Length; j++)
            {
                if (!roadDefinitions[j].canHaveIntersection) continue;
                FindIntersectionsBetweenRoads(_roads[i], _roads[j], roadDefinitions[i], roadDefinitions[j]);
            }
        }
    }

    private void FindIntersectionsBetweenRoads(RoadSegment roadA, RoadSegment roadB,
                                                RoadSegmentDefinition defA, RoadSegmentDefinition defB)
    {
        var ptsA = new List<Vector3>();
        var ptsB = new List<Vector3>();

        for (int i = 0; i <= intersectionCheckResolution; i++)
        {
            float t = (float)i / intersectionCheckResolution;
            ptsA.Add(roadA.EvaluatePosition(t));
            ptsB.Add(roadB.EvaluatePosition(t));
        }

        for (int a = 0; a < ptsA.Count - 1; a++)
        {
            for (int b = 0; b < ptsB.Count - 1; b++)
            {
                if (!LineIntersection2D(ptsA[a], ptsA[a+1], ptsB[b], ptsB[b+1], out Vector3 intersect))
                    continue;

                bool exists = false;
                foreach (var p in _spawnedIntersectionPositions)
                    if (Vector3.Distance(p, intersect) < 5f) { exists = true; break; }

                if (exists) continue;

                _spawnedIntersectionPositions.Add(intersect);

                float   tA    = (float)a / intersectionCheckResolution;
                float   tB    = (float)b / intersectionCheckResolution;
                Vector3 dirA  = roadA.EvaluateTangent(tA).normalized;
                Vector3 dirB  = roadB.EvaluateTangent(tB).normalized;

                // Use per-road green times from the definition if set, else default 10s
                float greenA = defA.greenTime > 0f ? defA.greenTime : 10f;
                float greenB = defB.greenTime > 0f ? defB.greenTime : 10f;

                CreateProceduralJunction(
                    intersect, dirA, dirB,
                    roadA.roadWidth, roadB.roadWidth,
                    roadA.roadCode, roadB.roadCode,
                    defA, defB,
                    greenA, greenB,
                    tA, tB,
                    IsAutoIntersectionMajor(roadA.roadCode, roadB.roadCode),
                    null, null, null, null); // [ADD] auto-detected intersections have no IntersectionDefinition to hang tuned-light data on -- always default single-light behavior
            }
        }
    }

    // ── Procedural junction builder ───────────────────────────────────────────
    private void CreateProceduralJunction(
        Vector3 pos, Vector3 dirA, Vector3 dirB,
        float widthA, float widthB,
        string codeA, string codeB,
        RoadSegmentDefinition defA, RoadSegmentDefinition defB,
        float timeA, float timeB,
        float tA, float tB,
        bool isMajor,
        List<TrafficLightHead> headsANear = null, List<TrafficLightHead> headsAFar = null,
        List<TrafficLightHead> headsBNear = null, List<TrafficLightHead> headsBFar = null)
    {
        pos.y = RoadSegment.AutoFlatHeight;

        var junctionRoot = new GameObject($"Junction_{codeA}_X_{codeB}");
        junctionRoot.transform.SetParent(transform);
        junctionRoot.transform.position = pos;
        junctionRoot.transform.rotation = Quaternion.LookRotation(dirA, Vector3.up);

        float dynSize = Mathf.Max(widthA, widthB, junctionPavementSize);

        // [ADD] T-junction / partial-arm detection. A road that genuinely
        // PASSES THROUGH the junction (t is somewhere in the middle) has
        // real geometry on both sides. A road that ENDS at the junction
        // (t sits right at 0 or 1) only has real geometry on the side it
        // actually continues from -- the other "arm" would just be
        // pointing at empty space where no road exists. This is what "only
        // for spaces the road actually comes into" means: don't draw a
        // full symmetric 4-arm star when only 2 or 3 of those arms
        // correspond to a real road.
        bool roadA_hasNeg = tA > ArmEndpointEps;        // road A has real geometry behind this point
        bool roadA_hasPos = tA < 1f - ArmEndpointEps;    // ...and/or ahead of it
        bool roadB_hasNeg = tB > ArmEndpointEps;
        bool roadB_hasPos = tB < 1f - ArmEndpointEps;

        // [ADD] Curved roads: a straight-line arm extension can visibly
        // diverge from a tightly-curving road well before it reaches
        // armLength. Estimate local curvature (how much the tangent turns
        // over a short stretch either side of the junction) and shrink
        // that road's arm length accordingly -- a straight road keeps the
        // full length, a tight curve gets pulled in so the straight
        // approximation stays close to the real curve near the junction.
        // [FIX] Previously each road computed its OWN arm length from its
        // own curve shrink, so a straight road (shrink=1) paired with even
        // a mildly curving one (shrink down to 0.4) produced dramatically
        // unequal arm lengths -- one pair of points reaching far past the
        // other. A junction pad should read as one consistent shape, so
        // every arm now shares a single length: the tightest shrink of the
        // two roads is applied uniformly. This also fixes the "generates
        // weirdly on angled roads" symptom, since a tightly curving road no
        // longer drags only ITS OWN arms in while leaving the other road's
        // arms oversized relative to it.
        // [FIX] Length was previously derived from dynSize (max road
        // width), which meant a wide road produced long arms and a narrow
        // road produced short ones -- but the arm TAPERS TO A POINT at the
        // tip, so nothing about the tip is actually constrained by road
        // width. Width only matters near the base. So length is now a
        // fixed universal constant (junctionArmBaseLength), completely
        // decoupled from whatever width either road happens to be.
        RoadSegment roadA_seg = GetRoad(codeA);
        RoadSegment roadB_seg = GetRoad(codeB);
        float sharedShrink = Mathf.Min(EstimateCurveShrink(roadA_seg, tA), EstimateCurveShrink(roadB_seg, tB));
        float sharedArmLen = junctionArmBaseLength * junctionSizeMultiplier * sharedShrink;
        float armLenA = sharedArmLen;
        float armLenB = sharedArmLen;

        // [ADD] Register a no-paint box for the center-line marking pass --
        // an oriented rectangle (per your ask: "7x9, 7x3, 7x7, whatever box")
        // sized off each road's own arm reach at this junction rather than a
        // fixed constant, so a big multi-lane crossing gets a bigger clear
        // zone than a narrow side-street T-junction automatically.
        _intersectionClearBoxes.Add(new IntersectionClearBox
        {
            center             = pos,
            forward            = dirA.normalized,
            right              = dirB.normalized,
            halfExtentForward  = Mathf.Max(widthA * 0.5f, armLenA) * intersectionLineClearance,
            halfExtentRight    = Mathf.Max(widthB * 0.5f, armLenB) * intersectionLineClearance,
        });

        Vector3 dirA_local = Vector3.forward; // junctionRoot's own forward IS dirA by construction (LookRotation(dirA, up) above)
        Vector3 dirB_local = junctionRoot.transform.InverseTransformDirection(dirB);

        // [REDESIGN] Down to exactly 2 possible pavement shapes: a triangle
        // for a 3-arm T-junction (vertices sitting right on each road's own
        // centerline) or a 45°-rotated square/diamond for a true 4-arm
        // crossing. A 2-arm meeting (two dead-ends touching, no real
        // through-traffic shape) gets NO pavement mesh at all -- the line
        // clearing logic handles that spot on its own (see RegisterClearBox).
        // See BuildSimpleJunctionMesh for the actual shape math.
        var activeArms = new List<(Vector3 dir, float width, float armLen)>();
        if (roadA_hasPos) activeArms.Add((dirA_local, widthA, armLenA));
        if (roadA_hasNeg) activeArms.Add((-dirA_local, widthA, armLenA));
        if (roadB_hasPos) activeArms.Add((dirB_local, widthB, armLenB));
        if (roadB_hasNeg) activeArms.Add((-dirB_local, widthB, armLenB));

        if (activeArms.Count >= 3)
        {
            var pave = new GameObject("PavementMesh");
            pave.transform.SetParent(junctionRoot.transform, false);
            pave.transform.localPosition = new Vector3(0f, 0.01f, 0f);
            var mf = pave.AddComponent<MeshFilter>();
            var mr = pave.AddComponent<MeshRenderer>();
            // [CHANGE] Was always the flat gray _autoPave material. Now
            // matches whichever crossing road is wider, material AND color
            // tint, same technique RoadSegment.BuildMeshChunks uses for its
            // own roadColor tint -- so the junction pad blends into the road
            // instead of standing out as a distinct gray patch.
            mr.sharedMaterial = CreateRoadMatchedMaterial(widthA >= widthB ? roadA_seg : roadB_seg);
            mf.sharedMesh = BuildSimpleJunctionMesh(activeArms, dirA_local, dirB_local);
        }

        // Lane waypoints — only for arms that actually exist.
        float lhsFactor   = leftHandTraffic ? -1f : 1f;
        float laneDistance = dynSize * 0.75f;
        Vector3 rightA    = Vector3.Cross(Vector3.up, dirA).normalized;
        Vector3 rightB    = Vector3.Cross(Vector3.up, dirB).normalized;
        float   offA      = widthA * 0.25f * lhsFactor;
        float   offB      = widthB * 0.25f * lhsFactor;

        var laneRoot = new GameObject("LaneWaypoints");
        laneRoot.transform.SetParent(junctionRoot.transform);

        Transform CreatePoint(string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(laneRoot.transform);
            go.transform.position = position;
            return go.transform;
        }

        Transform roadAEntry = roadA_hasNeg ? CreatePoint($"{codeA}_Entry", pos - dirA * laneDistance + rightA * offA) : null;
        Transform roadAExit  = roadA_hasPos ? CreatePoint($"{codeA}_Exit",  pos + dirA * laneDistance - rightA * offA) : null;
        Transform roadBEntry = roadB_hasNeg ? CreatePoint($"{codeB}_Entry", pos - dirB * laneDistance + rightB * offB) : null;
        Transform roadBExit  = roadB_hasPos ? CreatePoint($"{codeB}_Exit",  pos + dirB * laneDistance - rightB * offB) : null;

        _junctionLanes.Add(new JunctionLaneData {
            roadACode = codeA, roadBCode = codeB,
            roadAEntry = roadAEntry, roadAExit = roadAExit,
            roadBEntry = roadBEntry, roadBExit = roadBExit });

        if (!includeTrafficLights) return;

        var controller = junctionRoot.AddComponent<ProceduralJunctionController>();
        var nsLights   = new List<GameObject>();
        var ewLights   = new List<GameObject>();
        var nsHeads    = new List<JunctionHeadRuntime>();
        var ewHeads    = new List<JunctionHeadRuntime>();

        float flatLen = 5.5f, flatH = 0.5f;

        void SpawnLight(Vector3 dir, float width, bool posSide, RoadSegmentDefinition def, List<GameObject> list)
        {
            float   distOff  = (dynSize * 0.5f) + (flatLen * 0.5f);
            Vector3 centre   = pos + dir * (posSide ? distOff : -distOff);
            float   lhsF     = leftHandTraffic ? -1f : 1f;
            float   laneOff  = def.isOneWay ? 0f : (width * 0.25f * lhsF);
            Vector3 roadRight = Vector3.Cross(Vector3.up, dir).normalized;
            float   sideCorr = posSide ? -1f : 1f;
            Vector3 bias     = roadRight * laneOff * sideCorr;

            var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slab.name = $"Light_{(posSide ? "Far" : "Near")}";
            slab.transform.SetParent(junctionRoot.transform, true);
            slab.transform.position   = centre + bias + Vector3.up * (flatH * 0.5f + 0.01f);
            slab.transform.localScale = new Vector3(def.isOneWay ? width * 0.95f : width * 0.45f, flatH, flatLen);
            slab.transform.rotation   = Quaternion.LookRotation(posSide ? -dir : dir, Vector3.up);
            if (slab.TryGetComponent<Collider>(out var col)) col.isTrigger = true;
            list.Add(slab);
        }

        // [ADD] Tuned lights (item C) -- when heads are configured for this
        // side, spawn one smaller slab per head at its own manually-authored
        // percent-of-road-width range instead of the single default slab.
        // headsA/headsB is ONE shared list authored once, but a road has TWO
        // physical approaches (posSide false/true -- e.g. northbound AND
        // southbound on the same road), so this runs once per approach with
        // the SAME heads list both times.
        //
        // [FIX] The default single light's lane-offset bias intentionally
        // flips sign between the two approaches (sideCorr below) -- correct
        // FOR IT, since its job is "stay in the correct driving lane," and
        // each direction's own right-hand lane is physically on the
        // opposite side of the road's centerline from the other direction's.
        // Heads originally copied that same flip, which meant "0%" landed on
        // a DIFFERENT physical side of the road depending on which of the
        // two approaches was being spawned, even though both approaches
        // share the exact same headsA/headsB list in the Inspector --
        // reported back as "doesn't rotate to show the direction correctly."
        // Heads deliberately do NOT flip: 0%/100% always mean the same two
        // physical edges of the road regardless of which approach is being
        // spawned, so what you see in the editor (item E's gizmo/handles)
        // matches both approaches consistently, not just one of them.
        void SpawnHeadedLights(Vector3 dir, bool posSide, float roadWidth, List<TrafficLightHead> heads, List<GameObject> list, List<JunctionHeadRuntime> headList)
        {
            float   distOff   = (dynSize * 0.5f) + (flatLen * 0.5f);
            Vector3 centre    = pos + dir * (posSide ? distOff : -distOff);
            // [FIX] Canonicalized -- see TrafficLightHead.CanonicalRoadDir's
            // own comment. centre above deliberately keeps using the raw
            // dir (still needs the real direction for near/far placement
            // along the road); only the LATERAL axis needs canonicalizing.
            Vector3 roadRight = Vector3.Cross(Vector3.up, TrafficLightHead.CanonicalRoadDir(dir)).normalized;

            foreach (var head in heads)
            {
                if (!head.enabled) continue;
                Vector3 bias = roadRight * head.GetLateralOffset(roadWidth, posSide);

                var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
                slab.name = $"Light_{(posSide ? "Far" : "Near")}_{head.symbol}";
                slab.transform.SetParent(junctionRoot.transform, true);
                slab.transform.position   = centre + bias + Vector3.up * (flatH * 0.5f + 0.01f);
                slab.transform.localScale = new Vector3(head.GetHeadWidth(roadWidth), flatH, flatLen);
                slab.transform.rotation   = Quaternion.LookRotation(posSide ? -dir : dir, Vector3.up);
                if (slab.TryGetComponent<Collider>(out var col)) col.isTrigger = true;
                list.Add(slab);
                headList.Add(new JunctionHeadRuntime { go = slab, config = head });
            }
        }

        bool ShouldSpawn(RoadSegmentDefinition
         def, bool posSide) =>
            !def.isOneWay || (def.reverseFlow ? posSide : !posSide);

        // [FIX] Was spawning a light on every side of every road
        // unconditionally, including sides where the road doesn't actually
        // exist (a T-junction's phantom arm) -- a light floating in space
        // pointed at nothing. Now gated on the same real/phantom detection
        // the star mesh and lane waypoints use.
        // [CHANGED] Near and Far are independently configurable now (see
        // IntersectionDefinition's headsANear/headsAFar/headsBNear/headsBFar
        // comment) -- each checks/uses its own list instead of one shared
        // list forced onto both physical posts.
        bool hasHeadsANear = headsANear != null && headsANear.Count > 0;
        bool hasHeadsAFar  = headsAFar  != null && headsAFar.Count  > 0;
        bool hasHeadsBNear = headsBNear != null && headsBNear.Count > 0;
        bool hasHeadsBFar  = headsBFar  != null && headsBFar.Count  > 0;
        if (roadA_hasNeg && ShouldSpawn(defA, false))
        {
            if (hasHeadsANear) SpawnHeadedLights(dirA, false, widthA, headsANear, nsLights, nsHeads);
            else                SpawnLight(dirA, widthA, false, defA, nsLights);
        }
        if (roadA_hasPos && ShouldSpawn(defA, true))
        {
            if (hasHeadsAFar) SpawnHeadedLights(dirA, true, widthA, headsAFar, nsLights, nsHeads);
            else                SpawnLight(dirA, widthA, true, defA, nsLights);
        }
        if (roadB_hasNeg && ShouldSpawn(defB, false))
        {
            if (hasHeadsBNear) SpawnHeadedLights(dirB, false, widthB, headsBNear, ewLights, ewHeads);
            else                SpawnLight(dirB, widthB, false, defB, ewLights);
        }
        if (roadB_hasPos && ShouldSpawn(defB, true))
        {
            if (hasHeadsBFar) SpawnHeadedLights(dirB, true, widthB, headsBFar, ewLights, ewHeads);
            else                SpawnLight(dirB, widthB, true, defB, ewLights);
        }

        controller.Initialize(nsLights, ewLights, _autoLightRed, _autoLightYellow, timeA, timeB, 3f, isMajor, nsHeads, ewHeads);
    }

    /// <summary>How much a road's tangent turns over a short stretch either
    /// side of t -- used to shrink a junction arm's length for tightly
    /// curving roads so the straight-line arm approximation stays close to
    /// the real curve near the junction, instead of visibly diverging from
    /// it. Returns 1 (no shrink) for a straight road or when road is null.</summary>
    private float EstimateCurveShrink(RoadSegment road, float t)
    {
        if (road == null) return 1f;
        const float dt = 0.02f;
        Vector3 tan0 = road.EvaluateTangent(Mathf.Clamp01(t - dt));
        Vector3 tan1 = road.EvaluateTangent(Mathf.Clamp01(t + dt));
        float turnDeg = Vector3.Angle(tan0, tan1);
        // 0° turn (straight) -> full length. 40°+ turn over this short a
        // stretch is a genuinely tight curve -> shrink toward 40% length.
        float t01 = Mathf.Clamp01(turnDeg / 40f);
        return Mathf.Lerp(1f, 0.4f, t01);
    }

    /// <summary>Builds an instanced copy of `road`'s own material, unmodified --
    /// no roadColor tint applied. Falls back to CityManager's default pavement
    /// material if the road has no material of its own, and to a plain
    /// Standard-shader material if even that isn't set.</summary>
    private Material CreateRoadMatchedMaterial(RoadSegment road)
    {
        Material baseMat = road?.roadMaterial;
        var instanced = baseMat != null ? new Material(baseMat)
                      : (_autoPave != null ? new Material(_autoPave) : new Material(Shader.Find("Standard")));
        return instanced;
    }

    /// <summary>[REDESIGN] Replaces the old smooth astroid star with exactly
    /// 2 possible flat shapes, chosen purely by how many real arms this spot
    /// has (n < 3 is never called -- callers skip the mesh entirely for a
    /// 2-arm meeting):
    ///
    /// n == 3 (T-junction): a plain TRIANGLE whose 3 vertices sit directly
    /// on each road's own centerline, at that road's arm-reach distance --
    /// "points line up with the central point of the road."
    ///
    /// n == 4 (true crossing): a SQUARE rotated so its corners point along
    /// the ACTUAL bisector between roadA and roadB -- for a perfect
    /// perpendicular crossing that bisector sits at exactly 45° from each
    /// road (the classic diamond look), and for anything less than perfectly
    /// perpendicular the bisector naturally shifts to compromise between
    /// both roads' real directions instead of needing a hand-tuned angle
    /// formula.</summary>
    private Mesh BuildSimpleJunctionMesh(List<(Vector3 dir, float width, float armLen)> activeArms,
                                          Vector3 dirA_local, Vector3 dirB_local)
    {
        int n = activeArms.Count;

        if (n == 3)
        {
            var mesh3 = new Mesh { name = "JunctionTriangle" };
            Vector3 v0 = activeArms[0].dir.normalized * activeArms[0].armLen;
            Vector3 v1 = activeArms[1].dir.normalized * activeArms[1].armLen;
            Vector3 v2 = activeArms[2].dir.normalized * activeArms[2].armLen;

            Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0);
            int[] tris3 = normal.y >= 0f ? new[] { 0, 1, 2 } : new[] { 0, 2, 1 };

            mesh3.vertices  = new[] { v0, v1, v2 };
            mesh3.uv        = new[] { new Vector2(0.5f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f) };
            mesh3.triangles = tris3;
            mesh3.RecalculateNormals();
            mesh3.RecalculateBounds();
            return mesh3;
        }

        // n == 4 (or any other count -- treated the same way as a
        // reasonable fallback rather than a hard failure): a square/diamond
        // whose corner direction is the real bisector between the two
        // crossing roads, corners reaching out to the shared arm length.
        float armLen4 = activeArms[0].armLen;
        Vector3 bisector = (dirA_local.normalized + dirB_local.normalized);
        if (bisector.sqrMagnitude < 0.0001f)
            bisector = Vector3.Cross(Vector3.up, dirA_local).normalized; // roads parallel/opposite -- fall back to perpendicular
        else
            bisector.Normalize();

        Vector3 RotateY(Vector3 v, float deg)
        {
            float rad = deg * Mathf.Deg2Rad;
            float s = Mathf.Sin(rad), c = Mathf.Cos(rad);
            return new Vector3(v.x * c + v.z * s, v.y, -v.x * s + v.z * c);
        }

        Vector3 c0 = bisector * armLen4;
        Vector3 c1 = RotateY(bisector, 90f)  * armLen4;
        Vector3 c2 = RotateY(bisector, 180f) * armLen4;
        Vector3 c3 = RotateY(bisector, 270f) * armLen4;

        var mesh4 = new Mesh { name = "JunctionSquare" };
        mesh4.vertices  = new[] { Vector3.zero, c0, c1, c2, c3 };
        mesh4.uv        = new[] { new Vector2(0.5f,0.5f), new Vector2(1f,0.5f), new Vector2(0.5f,1f), new Vector2(0f,0.5f), new Vector2(0.5f,0f) };
        mesh4.triangles = new[] { 0,1,2,  0,2,3,  0,3,4,  0,4,1 };
        mesh4.RecalculateNormals();
        mesh4.RecalculateBounds();
        return mesh4;
    }

    // ── Line intersection ─────────────────────────────────────────────────────
    private bool LineIntersection2D(Vector3 p1, Vector3 p2, Vector3 p3, Vector3 p4, out Vector3 intersection)
    {
        intersection = Vector3.zero;
        float det = (p4.z - p3.z) * (p2.x - p1.x) - (p4.x - p3.x) * (p2.z - p1.z);
        if (Mathf.Abs(det) < 0.0001f) return false;
        float ua = ((p4.x - p3.x) * (p1.z - p3.z) - (p4.z - p3.z) * (p1.x - p3.x)) / det;
        float ub = ((p2.x - p1.x) * (p1.z - p3.z) - (p2.z - p1.z) * (p1.x - p3.x)) / det;
        if (ua < 0f || ua > 1f || ub < 0f || ub > 1f) return false;
        intersection.x = p1.x + ua * (p2.x - p1.x);
        intersection.y = p1.y + ua * (p2.y - p1.y);
        intersection.z = p1.z + ua * (p2.z - p1.z);
        return true;
    }

    // ── Road building ─────────────────────────────────────────────────────────
    private void BuildRoadsFromDefinitions()
    {
        _roads = new RoadSegment[roadDefinitions.Count];
        for (int i = 0; i < roadDefinitions.Count; i++)
        {
            var def = roadDefinitions[i];
            _roads[i] = new RoadSegment {
                roadName        = def.roadName,
                roadCode        = def.roadCode,
                roadWidth       = def.roadWidth,
                meshResolution  = def.meshResolution,
                roadColor       = def.roadColor,
                controlPoints   = def.BuildControlPoints()
            };
        }
        _stops = stopDefinitions.ToArray();
    }

    private void BuildTrafficMaterials()
    {
        var std      = Shader.Find("Standard");
        _autoPave        = lightMatPavement ?? CreateSimpleMaterial(std, new Color(0.2f, 0.2f, 0.2f));
        _autoLightRed    = lightMatRed    ?? CreateEmissiveMaterial(Color.red,    Color.red    * 0.8f);
        _autoLightYellow = lightMatYellow ?? CreateEmissiveMaterial(Color.yellow, Color.yellow * 0.8f);
    }

    private Material CreateSimpleMaterial(Shader s, Color col)
    { var m = new Material(s); m.color = col; return m; }

    private static Material CreateEmissiveMaterial(Color albedo, Color emission)
    {
        var m = new Material(Shader.Find("Standard"));
        m.color = albedo;
        m.EnableKeyword("_EMISSION");
        m.SetColor("_EmissionColor", emission);
        return m;
    }

    private void BuildMarkerMaterials()
    {
        _autoBlue   = regularStopMaterial  ?? CreateEmissiveMaterial(Color.blue,   new Color(0f,  0.2f, 1f)  * 0.6f);
        _autoYellow = terminalStopMaterial ?? CreateEmissiveMaterial(Color.yellow, new Color(1f, 0.85f, 0f)  * 0.6f);
    }

    private void BuildLookups()
    {
        _roadByCode.Clear();
        foreach (var r in _roads) if (!string.IsNullOrEmpty(r.roadCode)) _roadByCode[r.roadCode] = r;
        _stopByCode.Clear();
        foreach (var s in _stops) if (!string.IsNullOrEmpty(s.stopCode)) _stopByCode[s.stopCode] = s;
    }

    private void ResolveReferences()
    {
        foreach (var s in _stops)
            if (_roadByCode.TryGetValue(s.parentRoadCode, out var road)) s.resolvedRoad = road;
    }

private void ResolveRoutes()
    {
        if (routes == null) return;
        foreach (var route in routes)
        {
            // A short turn is a variant: make sure its stops/terminal/times are filled in from the mainline, and
            // its path is cut from the mainline path at the turnback stop, before its lists are resolved below.
            route.PrepareShortTurnVariants();
            route.DeriveShortTurnNodes(code => _stopByCode.TryGetValue(code, out var st) && st.resolvedRoad != null
                ? (Vector3?)st.GetWorldPosition() : (Vector3?)null);

            route.resolvedOutboundStops.Clear();
            route.resolvedInboundStops.Clear();
            
            // Loop through bindings using the helper method
            var outboundBindings = route.GetStopBindings(true);
            foreach (var binding in outboundBindings)
                if (_stopByCode.TryGetValue(binding.stopCode, out var s)) route.resolvedOutboundStops.Add(s);

            var inboundBindings = route.GetStopBindings(false);
            foreach (var binding in inboundBindings)
                if (_stopByCode.TryGetValue(binding.stopCode, out var s)) route.resolvedInboundStops.Add(s);

            if (!string.IsNullOrEmpty(route.terminalACode) && _stopByCode.TryGetValue(route.terminalACode, out var tA)) route.resolvedTerminalA = tA;
            if (!string.IsNullOrEmpty(route.terminalZCode) && _stopByCode.TryGetValue(route.terminalZCode, out var tZ)) route.resolvedTerminalZ = tZ;

            // [FIX] Variants never got their own resolvedOutboundStops/resolvedInboundStops
            // populated — RouteVariantData.GetStops() was always silently falling through
            // to the MAINLINE's resolved list regardless of overrideRoute, which meant a
            // variant's dead-run terminal, tracker ETAs, etc. resolved against the wrong
            // stop sequence. Resolve each variant's own binding lists into its own cache.
            if (route.variants != null)
            {
                foreach (var variant in route.variants)
                {
                    if (variant == null) continue;
                    variant.resolvedOutboundStops.Clear();
                    variant.resolvedInboundStops.Clear();

                    var vOutboundBindings = route.GetStopBindings(true, variant);
                    foreach (var binding in vOutboundBindings)
                        if (_stopByCode.TryGetValue(binding.stopCode, out var s)) variant.resolvedOutboundStops.Add(s);

                    var vInboundBindings = route.GetStopBindings(false, variant);
                    foreach (var binding in vInboundBindings)
                        if (_stopByCode.TryGetValue(binding.stopCode, out var s)) variant.resolvedInboundStops.Add(s);
                }
            }
        }
    }

// [ADD] Split out of BuildRoadMeshes so the road-mesh cache below can skip
// the expensive mesh generation while still applying per-road material
// overrides every time -- CreateRoadMatchedMaterial (used by junctions/line
// markings, which are NOT cached) reads road.roadMaterial, so this can't be
// skipped along with the mesh generation without breaking their material
// matching for a road whose mesh came from the cache.
private void ApplyPerRoadMaterialOverrides()
{
    for (int i = 0; i < _roads.Length; i++)
    {
        var def = i < roadDefinitions.Count ? roadDefinitions[i] : null;
        if (def?.roadMaterial != null)
            _roads[i].roadMaterial = def.roadMaterial;
    }
}

private void BuildRoadMeshes()
{
    for (int i = 0; i < _roads.Length; i++)
    {
        var road = _roads[i];

        var container = new GameObject($"Road_{road.roadCode}");
        container.transform.SetParent(transform, false);
        road.BuildMeshChunks(container.transform);
    }
}

    // ── Center line markings ──────────────────────────────────────────────────

    /// <summary>Registers a clearance box for EVERY geometric crossing
    /// between EVERY pair of roads, completely independent of
    /// spawnIntersections / canHaveIntersection / includeTrafficLights --
    /// those only control whether a VISUAL junction pad + traffic light get
    /// built. A line marking must never run through a crossing whether or
    /// not that crossing happens to also have a junction pad, so this scan
    /// runs unconditionally every build, same auto-detection idea as
    /// DetectAndSpawnIntersections but decoupled from it on purpose so
    /// disabling junctions elsewhere can never leave a road pair unclamped
    /// here.</summary>
    private void ComputeGeometricLineClearBoxes()
    {
        if (_roads == null) return;

        // [FIX] Adaptive resolution -- intersectionCheckResolution is a
        // single global setting tuned for the junction/traffic-light system
        // and was way too coarse for very long roads (e.g. a 600m road at
        // res=50 samples every 12m, which can straddle right past a narrow
        // crossing without ever landing a sample inside it). Sample at
        // roughly one point per 2 world units per road instead, so no road
        // -- long, short, straight, or curved -- can slip a real crossing
        // between samples.
        var sampled  = new Vector3[_roads.Length][];
        var resPer   = new int[_roads.Length];
        for (int i = 0; i < _roads.Length; i++)
        {
            int res = Mathf.Clamp(Mathf.CeilToInt(_roads[i].ApproximateLength() / 2f), 40, 400);
            var pts = new Vector3[res + 1];
            for (int k = 0; k <= res; k++) pts[k] = _roads[i].EvaluatePosition((float)k / res);
            sampled[i] = pts;
            resPer[i]  = res;
        }

        for (int i = 0; i < _roads.Length; i++)
        {
            var roadA = _roads[i];
            var defA  = i < roadDefinitions.Count ? roadDefinitions[i] : null;
            float widthA = defA?.roadWidth ?? roadA.roadWidth;
            var ptsA = sampled[i];
            int resA = resPer[i];

            for (int j = i + 1; j < _roads.Length; j++)
            {
                var roadB = _roads[j];
                var defB  = j < roadDefinitions.Count ? roadDefinitions[j] : null;
                float widthB = defB?.roadWidth ?? roadB.roadWidth;
                var ptsB = sampled[j];
                int resB = resPer[j];

                // Pass 1: true X-crossings, where the two roads actually
                // pass through each other.
                for (int a = 0; a < ptsA.Length - 1; a++)
                {
                    for (int b = 0; b < ptsB.Length - 1; b++)
                    {
                        if (!LineIntersection2D(ptsA[a], ptsA[a + 1], ptsB[b], ptsB[b + 1], out Vector3 hit))
                            continue;

                        float tA = (float)a / resA;
                        float tB = (float)b / resB;
                        RegisterClearBox(roadA, roadB, tA, tB, widthA, widthB, hit);
                    }
                }

                // [FIX] Pass 2: T-junctions. A road that DEAD-ENDS onto
                // another road's middle -- the single most common shape in
                // a street grid, and exactly what a straight side street
                // does when it terminates at a main road -- never produces
                // a segment-segment CROSSING, so Pass 1 above misses it
                // entirely. That's why straight roads were sailing straight
                // through their own intersections uninterrupted once they
                // became one continuous stretched piece: most of those
                // "intersections" were T-junctions, not crossings, and had
                // never gotten a clear box registered at all. Explicitly
                // check both of each road's endpoints against the other
                // road's sampled path (same idea as RoadGraph's
                // TryAddEndpointCut).
                CheckEndpointTJunction(roadA, roadB, widthA, widthB, ptsB, resB);
                CheckEndpointTJunction(roadB, roadA, widthB, widthA, ptsA, resA);
            }
        }
    }

    /// <summary>Seeds a circular clear zone at every RoadGraph node that
    /// actually joins more than one road -- i.e. every real junction.
    ///
    /// [FIX] This exists because the pairwise scans above are a SECOND,
    /// parallel implementation of crossing detection, and any case they miss
    /// silently leaves a junction painted through. RoadGraph.Build already
    /// solves exactly this problem (it cuts every road at every crossing and
    /// T-junction to create nodes) and is the code the whole pathfinding
    /// system depends on, so it's the better-tested source of truth. Reusing
    /// its output here means the line-clearing agrees with the junction
    /// topology the rest of the game already believes in, instead of relying
    /// on my own duplicate geometry pass catching every case.
    ///
    /// Runs unconditionally -- Graph is built earlier in BuildCity() from raw
    /// road geometry regardless of spawnIntersections / canHaveIntersection.</summary>
    private void ComputeGraphNodeLineClearBoxes()
    {
        if (Graph == null || Graph.nodes == null) return;

        foreach (var node in Graph.nodes)
        {
            if (node == null || node.outgoing == null) continue;

            // Count how many DISTINCT road segments meet here. A node with
            // only one distinct segment is just a dead-end//road terminus,
            // not a junction -- clearing there would eat the line at the end
            // of every road for no reason.
            RoadSegment firstSeg = null;
            bool multipleRoads = false;
            float widest = 0f;
            foreach (var e in node.outgoing)
            {
                if (e?.segment == null) continue;
                if (firstSeg == null) firstSeg = e.segment;
                else if (!ReferenceEquals(e.segment, firstSeg)) multipleRoads = true;
                widest = Mathf.Max(widest, e.segment.roadWidth);
            }
            if (!multipleRoads) continue;

            float radius = Mathf.Max(widest * 0.5f, junctionArmBaseLength * junctionSizeMultiplier)
                           * intersectionLineClearance;

            _intersectionClearBoxes.Add(new IntersectionClearBox
            {
                center    = node.position,
                useRadius = true,
                radius    = radius,
            });
        }
    }

    private void RegisterClearBox(RoadSegment roadA, RoadSegment roadB, float tA, float tB,
                                   float widthA, float widthB, Vector3 hit)
    {
        Vector3 dirA = roadA.EvaluateTangent(tA);
        Vector3 dirB = roadB.EvaluateTangent(tB);
        if (dirA.sqrMagnitude < 0.0001f || dirB.sqrMagnitude < 0.0001f) return;

        // Same real/phantom arm test used everywhere else (cover diamonds,
        // lit junction meshes) so the box sizing here always agrees with
        // whether this spot actually gets a pavement shape.
        bool aHasNeg = tA > ArmEndpointEps, aHasPos = tA < 1f - ArmEndpointEps;
        bool bHasNeg = tB > ArmEndpointEps, bHasPos = tB < 1f - ArmEndpointEps;
        int armCount = (aHasPos ? 1 : 0) + (aHasNeg ? 1 : 0) + (bHasPos ? 1 : 0) + (bHasNeg ? 1 : 0);

        float halfFwd, halfRight;
        if (armCount <= 2)
        {
            // [ADD] Two dead-ends meeting -- no cover shape gets built here
            // (see BuildCoverDiamond/CreateProceduralJunction, both skip
            // n<3), so a generous junction-sized gap would just be an empty
            // rectangle with nothing hiding it. Clip tight instead, right at
            // the edge of the OTHER road's own width, so each road's line
            // runs to the halfway point of the road it's meeting and both
            // lines lead cleanly in and out of the same spot.
            halfFwd   = widthB * 0.5f;
            halfRight = widthA * 0.5f;
        }
        else
        {
            // Box dimensions come straight from the two roads' own widths
            // (your "7x9, 7x3, 7x7, whatever" idea) -- how wide the CROSSING
            // road is determines how far this road's line needs to be
            // clipped back.
            halfFwd   = Mathf.Max(widthB * 0.5f, junctionArmBaseLength * junctionSizeMultiplier) * intersectionLineClearance;
            halfRight = Mathf.Max(widthA * 0.5f, junctionArmBaseLength * junctionSizeMultiplier) * intersectionLineClearance;
        }

        _intersectionClearBoxes.Add(new IntersectionClearBox
        {
            center            = hit,
            forward           = dirA.normalized,
            right             = dirB.normalized,
            halfExtentForward = halfFwd,
            halfExtentRight   = halfRight,
        });

        // Capture everything SpawnCenterLineCoverDiamonds needs to build a
        // cover mesh at this exact spot, using the same tA/tB real/phantom
        // arm data as the line clearing above -- so the cover and the gap
        // in the yellow line are always in agreement.
        if (spawnCenterLineCoverDiamonds)
        {
            _centerLineCoverSpecs.Add(new DiamondSpec
            {
                pos = hit, dirA = dirA.normalized, dirB = dirB.normalized,
                widthA = widthA, widthB = widthB, tA = tA, tB = tB,
                roadA = roadA, roadB = roadB,
            });
        }
    }

    /// <summary>Checks whether either endpoint of `endpointRoad` lands close
    /// to `otherRoad`'s sampled path (a T-junction / dead-end onto that
    /// road) and, if so, registers a clear box there too -- these never show
    /// up as a Pass-1 segment crossing since the endpoint road doesn't
    /// continue past the meeting point.</summary>
    private void CheckEndpointTJunction(RoadSegment endpointRoad, RoadSegment otherRoad,
                                         float endpointWidth, float otherWidth,
                                         Vector3[] otherPts, int otherRes)
    {
        float snapTolerance = Mathf.Max(endpointWidth, otherWidth) * 0.75f + 1f;

        foreach (float tEnd in EndpointTValues)
        {
            Vector3 endPos = endpointRoad.EvaluatePosition(tEnd);

            float bestDist = float.MaxValue;
            int   bestB    = -1;
            for (int b = 0; b < otherPts.Length; b++)
            {
                float d = Vector3.Distance(otherPts[b], endPos);
                if (d < bestDist) { bestDist = d; bestB = b; }
            }
            if (bestB < 0 || bestDist > snapTolerance) continue;

            float tB = (float)bestB / otherRes;
            RegisterClearBox(endpointRoad, otherRoad, tEnd, tB, endpointWidth, otherWidth, otherRoad.EvaluatePosition(tB));
        }
    }

    private static readonly float[] EndpointTValues = { 0f, 1f };

    /// <summary>True if `worldPos` falls inside ANY registered intersection's
    /// clearance box (see IntersectionClearBox / CreateProceduralJunction).
    /// Test is a simple oriented-rectangle check along each box's own
    /// forward/right axes, so it stays correct for crossings at any angle,
    /// not just perfect 4-way perpendiculars.</summary>
    private bool IsInsideAnyIntersectionBox(Vector3 worldPos)
    {
        for (int i = 0; i < _intersectionClearBoxes.Count; i++)
        {
            var box = _intersectionClearBoxes[i];
            Vector3 d = worldPos - box.center;
            d.y = 0f; // compare on the ground plane only -- road surface Y
                      // is snapped/offset independently and must not affect
                      // whether a point counts as "in the junction".

            if (box.useRadius)
            {
                if (d.sqrMagnitude <= box.radius * box.radius) return true;
                continue;
            }

            float alongForward = Vector3.Dot(d, box.forward);
            float alongRight   = Vector3.Dot(d, box.right);
            if (Mathf.Abs(alongForward) <= box.halfExtentForward &&
                Mathf.Abs(alongRight)   <= box.halfExtentRight)
                return true;
        }
        return false;
    }

    /// <summary>For every road, splits it into "clear runs" (stretches not
    /// blocked by an intersection box) and spawns line markings per run:
    /// a small capped number of stretched pieces tiling the run
    /// (SpawnTiledLineRun), or -- in dashed mode only -- a chain of short
    /// chopped instances (SpawnChoppedLineRun). Any road/run that's fully
    /// inside a junction box contributes nothing, so intersections stay
    /// unpainted automatically.</summary>
    /// <summary>Builds a plain pavement shape over every crossing/T-junction
    /// captured in _centerLineCoverSpecs -- same shape math as the
    /// manual/lights junction system (BuildSimpleJunctionMesh +
    /// EstimateCurveShrink), reused directly: a square/diamond for a true
    /// 4-arm crossing, a triangle for a 3-arm T-junction. Two roads that
    /// both just dead-end at the same spot get skipped entirely (see
    /// BuildCoverDiamond). No traffic lights, no lane waypoints -- purely a
    /// cover mesh, painted with the crossing roads' own material + tint so
    /// it blends in and guarantees no yellow line remnant can ever show
    /// through.</summary>
    private void SpawnCenterLineCoverDiamonds()
    {
        if (!spawnCenterLineCoverDiamonds || _centerLineCoverSpecs.Count == 0) return;

        var root = new GameObject("CenterLineIntersectionCovers");
        root.transform.SetParent(transform, false);

        // Pass 1 (true crossings) and Pass 2 (T-junctions) can occasionally
        // register two specs almost on top of each other -- e.g. a road
        // ending right where two others cross. Skip a spec too close to one
        // already built so covers don't stack.
        var built = new List<Vector3>();

        foreach (var spec in _centerLineCoverSpecs)
        {
            bool dup = false;
            foreach (var p in built)
                if (Vector3.Distance(p, spec.pos) < 1.5f) { dup = true; break; }
            if (dup) continue;
            built.Add(spec.pos);

            BuildCoverDiamond(spec, root.transform);
        }
    }

    private void BuildCoverDiamond(DiamondSpec spec, Transform parent)
    {
        Vector3 pos = spec.pos;
        pos.y = RoadSegment.AutoFlatHeight;

        // Same real/phantom arm detection used everywhere else (shared
        // ArmEndpointEps): a road that ENDS at this point (t near 0 or 1)
        // only has geometry on the side it actually continues from.
        bool aHasNeg = spec.tA > ArmEndpointEps;
        bool aHasPos = spec.tA < 1f - ArmEndpointEps;
        bool bHasNeg = spec.tB > ArmEndpointEps;
        bool bHasPos = spec.tB < 1f - ArmEndpointEps;

        var junctionRoot = new GameObject($"Cover_{spec.roadA.roadCode}_X_{spec.roadB.roadCode}");
        junctionRoot.transform.SetParent(parent, false);
        junctionRoot.transform.position = pos;
        junctionRoot.transform.rotation = Quaternion.LookRotation(spec.dirA, Vector3.up);

        float sharedShrink = Mathf.Min(EstimateCurveShrink(spec.roadA, spec.tA), EstimateCurveShrink(spec.roadB, spec.tB));
        float armLen = junctionArmBaseLength * junctionSizeMultiplier * sharedShrink;

        Vector3 dirA_local = Vector3.forward;
        Vector3 dirB_local = junctionRoot.transform.InverseTransformDirection(spec.dirB);

        var arms = new List<(Vector3 dir, float width, float armLen)>();
        if (aHasPos) arms.Add((dirA_local, spec.widthA, armLen));
        if (aHasNeg) arms.Add((-dirA_local, spec.widthA, armLen));
        if (bHasPos) arms.Add((dirB_local, spec.widthB, armLen));
        if (bHasNeg) arms.Add((-dirB_local, spec.widthB, armLen));

        // [CHANGE] Was arms.Count < 2. A 2-arm meeting (two dead-ends
        // touching) is now explicitly "no shape at all" per your call --
        // RegisterClearBox already tightened the line clearance for exactly
        // this case, so there's nothing left for a cover mesh to hide.
        if (arms.Count < 3) { DestroyImmediate(junctionRoot); return; }

        var cover = new GameObject("CoverMesh");
        cover.transform.SetParent(junctionRoot.transform, false);
        cover.transform.localPosition = new Vector3(0f, centerLineCoverYOffset, 0f);

        var mf = cover.AddComponent<MeshFilter>();
        var mr = cover.AddComponent<MeshRenderer>();
        // Explicit override still wins verbatim (no auto-tint) if you set
        // one; otherwise build a road-matched material + tint the same way
        // the lit junction pad does, so the two systems look identical.
        mr.sharedMaterial = centerLineCoverMaterial != null
            ? centerLineCoverMaterial
            : CreateRoadMatchedMaterial(spec.widthA >= spec.widthB ? spec.roadA : spec.roadB);
        mf.sharedMesh = BuildSimpleJunctionMesh(arms, dirA_local, dirB_local);
    }

    private void SpawnCenterLineMarkings()
    {
        if (yellowLinePrefab == null || _roads == null) return;

        // [FIX] Measure the prefab ONCE up front instead of trusting a
        // hand-typed length. See MeasureLinePrefab -- a wrong
        // yellowLinePrefabLength made every stretched piece overshoot the
        // road by exactly the ratio of the typed value to the real one,
        // which is the "line extends past the road" symptom.
        if (!MeasureLinePrefab(out float prefabLocalLenZ, out float prefabLocalCenterZ))
        {
            Debug.LogWarning("[CityManager] yellowLinePrefab has no readable mesh bounds -- " +
                             "can't measure its length, so center lines were skipped. " +
                             "Assign a prefab with a MeshFilter, or set yellowLinePrefabLength by hand.");
            return;
        }

        var root = new GameObject("CenterLineMarkings");
        root.transform.SetParent(transform, false);

        float choppedLen = Mathf.Max(0.05f, prefabLocalLenZ * lineChopFactor);

        for (int i = 0; i < _roads.Length; i++)
        {
            var road = _roads[i];
            var def  = i < roadDefinitions.Count ? roadDefinitions[i] : null;
            if (def != null && def.hideCenterLine) continue;
            if (road.ApproximateLength() < 0.01f) continue;

            foreach (var (tStart, tEnd) in ComputeClearRuns(road))
            {
                if (tEnd - tStart < 0.0005f) continue;

                if (dashedCenterLine)
                    SpawnChoppedLineRun(road, tStart, tEnd, choppedLen, root.transform);
                else
                    SpawnTiledLineRun(road, tStart, tEnd, prefabLocalLenZ, prefabLocalCenterZ, root.transform);
            }
        }
    }

    /// <summary>Measures the line prefab's REAL extent along its local +Z
    /// from its combined mesh bounds, plus how far its pivot sits from that
    /// extent's center. Both matter: length drives the stretch factor, and
    /// pivot offset decides where the piece has to be positioned so it
    /// actually lands centered on the span it's meant to cover.
    ///
    /// [FIX] Previously the length came from the hand-entered
    /// yellowLinePrefabLength and the pivot was assumed to be dead center.
    /// Either being wrong makes pieces overhang the end of the road --
    /// a prefab whose real length is 20 but typed as 10 comes out exactly
    /// twice as long as the span it was supposed to fill. Measuring removes
    /// the guess entirely (and removes a setting you'd otherwise have to get
    /// right by hand for every prefab).</summary>
    private bool MeasureLinePrefab(out float localLenZ, out float localCenterZ)
    {
        localLenZ = 0f; localCenterZ = 0f;
        if (yellowLinePrefab == null) return false;

        var filters = yellowLinePrefab.GetComponentsInChildren<MeshFilter>(true);
        bool any = false;
        float minZ = float.MaxValue, maxZ = float.MinValue;

        var rootT = yellowLinePrefab.transform;
        foreach (var mf in filters)
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;

            // Express each child's mesh bounds in the PREFAB ROOT's local
            // space, so a line built out of several child pieces (or a child
            // that's rotated/offset relative to the root) still measures
            // correctly rather than only working for a single centered mesh.
            Bounds b = mesh.bounds;
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? b.min.x : b.max.x,
                    (c & 2) == 0 ? b.min.y : b.max.y,
                    (c & 4) == 0 ? b.min.z : b.max.z);
                Vector3 inRoot = rootT.InverseTransformPoint(mf.transform.TransformPoint(corner));
                if (inRoot.z < minZ) minZ = inRoot.z;
                if (inRoot.z > maxZ) maxZ = inRoot.z;
                any = true;
            }
        }

        if (!any || maxZ - minZ < 1e-5f)
        {
            // Fall back to the manual override if there's nothing measurable.
            if (yellowLinePrefabLength > 0f) { localLenZ = yellowLinePrefabLength; localCenterZ = 0f; return true; }
            return false;
        }

        localLenZ    = maxZ - minZ;
        localCenterZ = (minZ + maxZ) * 0.5f;

        // Explicit override still wins if you set it to something non-zero.
        if (yellowLinePrefabLength > 0f) localLenZ = yellowLinePrefabLength;
        return true;
    }

    /// <summary>Fills [tStart, tEnd] with a SMALL, capped number of pieces,
    /// each stretched along Z to exactly cover its own sub-span so the run
    /// is tiled edge-to-edge with no gap and no overhang.
    ///
    /// This is the "limit the count, stretch what we have to fill the area"
    /// approach: piece count is driven by targetSegmentLength and hard-capped
    /// by maxSegmentsPerRun, so a 600m straight road is a handful of objects
    /// instead of hundreds -- while a curved road still gets enough pieces
    /// that the chain visibly follows the curve, since each piece only ever
    /// spans its own short chord.</summary>
    private void SpawnTiledLineRun(RoadSegment road, float tStart, float tEnd,
                                    float prefabLocalLenZ, float prefabLocalCenterZ, Transform parent)
    {
        // Arc length of this run, so piece count reflects real world size.
        float runLen = 0f;
        const int lenSteps = 24;
        Vector3 prev = road.EvaluatePosition(tStart);
        for (int i = 1; i <= lenSteps; i++)
        {
            Vector3 next = road.EvaluatePosition(Mathf.Lerp(tStart, tEnd, i / (float)lenSteps));
            runLen += Vector3.Distance(prev, next);
            prev = next;
        }
        if (runLen < 0.01f) return;

        // A straight run is perfectly represented by ONE stretched piece
        // (its chord IS its curve), so it doesn't need the curve-following
        // minimum that a curved run does.
        bool straight = road.controlPoints != null && road.controlPoints.Length == 2;

        int wanted = Mathf.CeilToInt(runLen / Mathf.Max(0.1f, targetSegmentLength));
        if (!straight) wanted = Mathf.Max(wanted, 4);
        int segCount = Mathf.Clamp(wanted, 1, Mathf.Max(1, maxSegmentsPerRun));

        for (int s = 0; s < segCount; s++)
        {
            float tA = Mathf.Lerp(tStart, tEnd, s       / (float)segCount);
            float tB = Mathf.Lerp(tStart, tEnd, (s + 1) / (float)segCount);

            Vector3 pA = road.EvaluateSurfacePosition(tA); pA.y += lineYOffset;
            Vector3 pB = road.EvaluateSurfacePosition(tB); pB.y += lineYOffset;

            Vector3 chord = pB - pA;
            float   spanLen = chord.magnitude;
            if (spanLen < 0.001f) continue;

            Vector3 dir = chord / spanLen;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);

            var go = Instantiate(yellowLinePrefab, pA, rot, parent);
            go.name = $"Line_{road.roadCode}_{s:D3}";

            // Stretch Z so the piece's REAL measured length becomes exactly
            // this sub-span's length -- pieces butt together end to end and
            // the last one stops precisely at the run's end.
            var sc = go.transform.localScale;
            float scaleZ = spanLen / prefabLocalLenZ;
            go.transform.localScale = new Vector3(sc.x, sc.y, scaleZ);

            // Position so the piece's measured CENTER lands on the sub-span
            // midpoint, correcting for a pivot that isn't at the mesh center
            // (a pivot at one end would otherwise push the piece a full
            // half-length past where it belongs -- overhanging the road).
            Vector3 mid = (pA + pB) * 0.5f;
            go.transform.position = mid - dir * (prefabLocalCenterZ * scaleZ);

            _spawnedLineSegments.Add(go);
        }
    }

    /// <summary>Walks a road end-to-end and returns the t-ranges NOT inside
    /// any registered intersection clearance box -- i.e. exactly the
    /// stretches that should actually get a line marking. A road crossing
    /// three intersections comes back as up to four separate runs.</summary>
    private List<(float tStart, float tEnd)> ComputeClearRuns(RoadSegment road, int samples = -1)
    {
        // Same reasoning as the resolution fix in ComputeGeometricLineClearBoxes
        // -- a fixed sample count can straddle right past a narrow clear box
        // on a long road. Default to roughly one sample per meter instead.
        if (samples <= 0)
            samples = Mathf.Clamp(Mathf.CeilToInt(road.ApproximateLength()), 60, 800);

        var runs = new List<(float, float)>();
        bool prevClear = false;
        float runStart = 0f;

        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector3 pos = road.EvaluateSurfacePosition(t);
            pos.y += lineYOffset;
            bool clear = !IsInsideAnyIntersectionBox(pos);

            if (clear && !prevClear) runStart = t;
            if (!clear && prevClear) runs.Add((runStart, t));
            prevClear = clear;
        }
        if (prevClear) runs.Add((runStart, 1f));

        return runs;
    }

    /// <summary>Chain of small chopped instances covering [tStart, tEnd] on
    /// a curved road (or any road in dashed mode), each following the local
    /// tangent so the chain hugs the curve instead of cutting corners.</summary>
    private void SpawnChoppedLineRun(RoadSegment road, float tStart, float tEnd, float choppedLen, Transform parent)
    {
        float runLen = 0f;
        const int lenSteps = 24;
        Vector3 prev = road.EvaluatePosition(tStart);
        for (int i = 1; i <= lenSteps; i++)
        {
            Vector3 next = road.EvaluatePosition(Mathf.Lerp(tStart, tEnd, i / (float)lenSteps));
            runLen += Vector3.Distance(prev, next);
            prev = next;
        }
        if (runLen < 0.01f) return;

        int segCount = Mathf.Max(1, Mathf.RoundToInt(runLen / choppedLen));

        for (int s = 0; s < segCount; s++)
        {
            // Dashed mode: paint every other slot ("on"), skip the rest
            // ("gap") -- even spacing regardless of how many chopped slots
            // this run ended up with.
            if (dashedCenterLine && (s % 2 == 1)) continue;

            float tCenter = Mathf.Lerp(tStart, tEnd, (s + 0.5f) / segCount);

            Vector3 pos = road.EvaluateSurfacePosition(tCenter);
            pos.y += lineYOffset;

            Vector3 tangent = road.EvaluateTangent(tCenter);
            if (tangent.sqrMagnitude < 0.0001f) continue;

            var go = Instantiate(yellowLinePrefab, pos, Quaternion.LookRotation(tangent, Vector3.up), parent);
            go.name = $"Line_{road.roadCode}_{s:D4}";

            var sc = go.transform.localScale;
            go.transform.localScale = new Vector3(sc.x, sc.y, sc.z * lineChopFactor);

            _spawnedLineSegments.Add(go);
        }
    }

    private void PlaceStops()
    {
        if (stopPrefab == null) return;
        foreach (var s in _stops)
        {
            if (s.resolvedRoad == null) continue;
            var go     = Instantiate(stopPrefab, s.GetWorldPosition(), s.GetWorldRotation(), transform);
            go.name    = $"Stop_{s.stopCode}";
            var marker = go.GetComponent<BusStopMarker>() ?? go.AddComponent<BusStopMarker>();
            marker.Data = s;
            _spawnedStops.Add(go);
        }
    }

    private void SpawnMarkerSpheres()
    {
        var root = new GameObject("StopMarkerSpheres");
        root.transform.SetParent(transform, false);
        foreach (var s in _stops)
        {
            if (s.resolvedRoad == null) continue;
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = $"Marker_{s.stopCode}";
            sphere.transform.SetParent(root.transform, false);
            sphere.transform.position   = s.GetSpherePosition();
            sphere.transform.localScale = Vector3.one * 2.5f;
            Destroy(sphere.GetComponent<Collider>());
            sphere.GetComponent<MeshRenderer>().sharedMaterial = s.isTerminal ? _autoYellow : _autoBlue;
            s.markerSphere = sphere;
            _markerSpheres.Add(sphere);
        }
    }

    private void RefreshNearbyStops()
    {
        var nearby = new List<Transform>();
        foreach (var go in _spawnedStops)
        {
            if (go == null) continue;
            // [FIX] Was Vector3.Distance (3D, including Y). Bus height is
            // physics-driven now and can legitimately differ from a stop's
            // baked height, so this only compares X/Z — Y is unbounded.
            Vector3 delta = go.transform.position - busTransform.position;
            delta.y = 0f;
            if (delta.magnitude <= stopDetectRadius)
                nearby.Add(go.transform);
        }
        busController.UpdateStopList(nearby);
    }

    private void PopulateDefaultsIfEmpty()
    {
        if (roadDefinitions.Count > 0) return;

        roadDefinitions = new List<RoadSegmentDefinition>
        {
            new RoadSegmentDefinition { roadName="NW 27th Avenue",  roadCode="NW27",   roadWidth=7f, meshResolution=0.5f, greenTime=15f, curveMode=RoadCurveMode.ControlPoints, controlPoints=new List<Vector3>{ new Vector3(0,0,-500), new Vector3(0,0,500) } },
            new RoadSegmentDefinition { roadName="NW 36th Street",  roadCode="NW36ST", roadWidth=7f, meshResolution=0.5f, greenTime=10f, curveMode=RoadCurveMode.ControlPoints, controlPoints=new List<Vector3>{ new Vector3(-300,0,180), new Vector3(300,0,180) } },
            new RoadSegmentDefinition { roadName="Airport Loop",    roadCode="ARPLP",  roadWidth=6f, meshResolution=0.4f, greenTime=10f, curveMode=RoadCurveMode.ControlPoints, controlPoints=new List<Vector3>{ new Vector3(0,0,200), new Vector3(120,0,200), new Vector3(180,0,280), new Vector3(180,0,400) } },
            new RoadSegmentDefinition { roadName="NW 12th Avenue",  roadCode="NW12",   roadWidth=7f, meshResolution=0.5f, greenTime=10f, curveMode=RoadCurveMode.ControlPoints, controlPoints=new List<Vector3>{ new Vector3(-200,0,-400), new Vector3(-200,0,400) } },
            new RoadSegmentDefinition { roadName="NW 17th Avenue",  roadCode="NW17",   roadWidth=7f, meshResolution=0.5f, greenTime=10f, curveMode=RoadCurveMode.ControlPoints, controlPoints=new List<Vector3>{ new Vector3(-100,0,-400), new Vector3(-100,0,400) } },
            new RoadSegmentDefinition { roadName="NW 54th Street",  roadCode="NW54ST", roadWidth=9f, meshResolution=0.5f, greenTime=20f, curveMode=RoadCurveMode.ControlPoints, controlPoints=new List<Vector3>{ new Vector3(-600,0,350), new Vector3(0,0,350), new Vector3(800,0,350) } },
            new RoadSegmentDefinition { roadName="Scenic River Dr", roadCode="SRDR",   roadWidth=6f, meshResolution=0.3f, greenTime=10f, curveMode=RoadCurveMode.MathEquation, mathStart=new Vector3(200,0,-200), mathEnd=new Vector3(400,0,200), waveform=MathWaveform.Sine, amplitude=50f, frequency=1.5f, phase=0f, sampleCount=32 },
        };

        stopDefinitions = new List<BusStopData>
        {
            new BusStopData { stopCode="m0001", stopName="NW 27th Ave & NW 7th St",            parentRoadCode="NW27",   tValue=0.07f, hasShelter=true,  isAccessible=true },
            new BusStopData { stopCode="m0002", stopName="NW 27th Ave & NW 20th St",           parentRoadCode="NW27",   tValue=0.33f, hasShelter=false, isAccessible=true },
            new BusStopData { stopCode="m0003", stopName="NW 27th Ave & NW 36th St",           parentRoadCode="NW27",   tValue=0.68f, hasShelter=true,  isAccessible=true },
            new BusStopData { stopCode="m0004", stopName="NW 27th Ave & Opa-locka (Terminal)", parentRoadCode="NW27",   tValue=1.0f,  hasShelter=true,  isTerminal=true, isLayover=true, isAccessible=true },
            new BusStopData { stopCode="m0005", stopName="Airport Loop (Departures)",          parentRoadCode="ARPLP",  tValue=0.5f,  hasShelter=true,  isAccessible=true },
            new BusStopData { stopCode="m0006", stopName="36th St & 7th Ave",                  parentRoadCode="NW36ST", tValue=0.12f, hasShelter=true,  isAccessible=true },
            new BusStopData { stopCode="m0007", stopName="36th St & 17th Ave",                 parentRoadCode="NW36ST", tValue=0.33f, hasShelter=true,  isAccessible=true },
            new BusStopData { stopCode="m0008", stopName="36th St & 27th Ave",                 parentRoadCode="NW36ST", tValue=0.65f, hasShelter=false, isAccessible=true },
            new BusStopData { stopCode="m0009", stopName="36th St & 30th Ave (Terminal)",      parentRoadCode="NW36ST", tValue=0.97f, hasShelter=true,  isTerminal=true, isLayover=true, isAccessible=true },
            new BusStopData { stopCode="m5401", stopName="54th St & 60th Ave",                 parentRoadCode="NW54ST", tValue=0.05f, hasShelter=true,  isAccessible=true },
            new BusStopData { stopCode="m5402", stopName="54th St & 47th Ave",                 parentRoadCode="NW54ST", tValue=0.20f, hasShelter=false, isAccessible=true },
            new BusStopData { stopCode="m5403", stopName="54th St & 37th Ave",                 parentRoadCode="NW54ST", tValue=0.35f, hasShelter=true,  isAccessible=true },
            new BusStopData { stopCode="m5404", stopName="54th St & 27th Ave (Transfer)",      parentRoadCode="NW54ST", tValue=0.50f, hasShelter=true,  isAccessible=true },
            new BusStopData { stopCode="m5405", stopName="54th St & 17th Ave",                 parentRoadCode="NW54ST", tValue=0.65f, hasShelter=false, isAccessible=true },
            new BusStopData { stopCode="m5406", stopName="54th St & 12th Ave",                 parentRoadCode="NW54ST", tValue=0.80f, hasShelter=true,  isAccessible=true },
            new BusStopData { stopCode="m5407", stopName="54th St & 2nd Ave (Terminal)",       parentRoadCode="NW54ST", tValue=0.95f, hasShelter=true,  isTerminal=true, isLayover=true, isAccessible=true },
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────
    public BusStopData  GetStop(string code) => _stopByCode.TryGetValue(code, out var s) ? s : null;
    public RoadSegment  GetRoad(string code) => _roadByCode.TryGetValue(code, out var r) ? r : null;
    public BusStopData[] AllStops => _stops;

    // [PERF FIX] Shared cache for "find the BusStopMarker for this stop code" --
    // previously four separate call sites (three in PlayerHandoff.cs, one in
    // NPCBusController.cs) each did their own full FindObjectsOfType<BusStopMarker>()
    // scan of the whole scene, every time they needed one. Markers are static
    // scene infrastructure (never spawned/destroyed mid-session, same
    // assumption _stopByCode above already relies on), so a lazily-built,
    // session-lifetime cache is safe -- built once, on whichever call happens
    // to need it first, not tied to BuildCity()'s own timing since markers may
    // not exist yet when that runs.
    private Dictionary<string, BusStopMarker> _markerByCode;

    private void EnsureMarkerCache()
    {
        if (_markerByCode != null) return;
        _markerByCode = new Dictionary<string, BusStopMarker>();
        foreach (var m in FindObjectsByType<BusStopMarker>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (m != null && !string.IsNullOrEmpty(m.StopCode))
                _markerByCode[m.StopCode] = m;
    }

    public BusStopMarker GetStopMarker(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        EnsureMarkerCache();
        _markerByCode.TryGetValue(code, out var marker);
        return marker;
    }

    /// <summary>All currently known stop markers -- same cache as GetStopMarker,
    /// for call sites that need to enumerate rather than look up one code (e.g.
    /// nearest-terminal search).</summary>
    public IEnumerable<BusStopMarker> AllStopMarkers()
    {
        EnsureMarkerCache();
        return _markerByCode.Values;
    }

#if UNITY_EDITOR
    [Header("Gizmos")]
    [Tooltip("Draw a marker + label at every resolved stop position, even before Play Mode spawns anything.")]
    public bool drawStopGizmos = true;
    [Tooltip("Also draw the stopDetectRadius circle around each stop (helps visualize NPC/player detect range against road width).")]
    public bool drawStopDetectRadius = false;
    public float stopGizmoSphereSize = 1.2f;

    private void OnDrawGizmos()
    {
        if (roadDefinitions == null) return;
        foreach (var def in roadDefinitions)
        {
            if (def == null) continue;
            var pts = def.BuildControlPoints();
            if (pts == null || pts.Length < 2) continue;
            var tempSeg = new RoadSegment { controlPoints = pts };
            Gizmos.color = Color.yellow;
            Vector3 prev = tempSeg.EvaluatePosition(0f);
            for (int i = 1; i <= 48; i++)
            {
                Vector3 next = tempSeg.EvaluatePosition(i / 48f);
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }

        // Independent of drawStopGizmos -- these should always be visible
        // regardless of whether stop markers are toggled on.
        DrawIntersectionLightGizmos();
        DrawOneWayArrowGizmos();

        if (!drawStopGizmos || stopDefinitions == null) return;

        // Build a temp road-code lookup so this works in edit mode, before
        // ResolveStops() has run and populated resolvedRoad on each stop.
        var roadLookup = new Dictionary<string, RoadSegment>();
        if (roadDefinitions != null)
        {
            foreach (var def in roadDefinitions)
            {
                if (def == null || string.IsNullOrEmpty(def.roadCode)) continue;
                var pts = def.BuildControlPoints();
                if (pts == null || pts.Length < 2) continue;
                roadLookup[def.roadCode] = new RoadSegment { controlPoints = pts };
            }
        }

        foreach (var s in stopDefinitions)
        {
            if (s == null || string.IsNullOrEmpty(s.parentRoadCode)) continue;

            // Prefer the resolved runtime road if we have one (Play Mode),
            // otherwise fall back to the temp lookup built above (Edit Mode).
            RoadSegment road = s.resolvedRoad;
            if (road == null) roadLookup.TryGetValue(s.parentRoadCode, out road);
            if (road == null) continue;

            Vector3 pos = road.EvaluatePosition(s.tValue) + Vector3.up * 0.1f;

            Gizmos.color = s.isTerminal ? new Color(1f, 0.85f, 0f) : new Color(0.2f, 0.55f, 1f);
            Gizmos.DrawSphere(pos, stopGizmoSphereSize);
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(pos, stopGizmoSphereSize);

            if (drawStopDetectRadius)
            {
                Gizmos.color = new Color(1f, 0f, 0f, 0.25f);
                DrawFlatCircle(pos, stopDetectRadius, 32);
            }

            UnityEditor.Handles.Label(pos + Vector3.up * 2f, $"{s.stopCode}\n{s.stopName}");
        }
    }

    /// <summary>Shows which of the 4 light channels (N/S/E/W) is actually
    /// switched on for each manual intersection -- those bools
    /// (lightNorth/lightSouth/lightEast/lightWest) already existed and were
    /// already changeable per-intersection in the Inspector, they just had
    /// no visual feedback in the scene view before now.</summary>
    private void DrawIntersectionLightGizmos()
    {
        if (manualIntersections == null) return;

        // [ADD] Item E -- temp road-code lookup built fresh per gizmo pass,
        // same reasoning as the stop gizmo's own temp lookup above (works in
        // Edit Mode before BuildCity() has ever run and populated the real
        // _roadByCode -- GetRoad() would just return null until then).
        Dictionary<string, RoadSegment> tempRoads = null;
        RoadSegment TempRoad(string code)
        {
            if (tempRoads == null)
            {
                tempRoads = new Dictionary<string, RoadSegment>();
                if (roadDefinitions != null)
                    foreach (var d in roadDefinitions)
                    {
                        if (d == null || string.IsNullOrEmpty(d.roadCode)) continue;
                        var pts = d.BuildControlPoints();
                        if (pts != null && pts.Length >= 2) tempRoads[d.roadCode] = new RoadSegment { controlPoints = pts, roadWidth = d.roadWidth };
                    }
            }
            tempRoads.TryGetValue(code, out var r);
            return r;
        }

        foreach (var def in manualIntersections)
        {
            if (def == null) continue;
            Vector3 basePos = def.position + Vector3.up * 3f;
            float r = 6f;

            DrawLightDirGizmo(basePos + Vector3.forward * r, "N", def.lightNorth);
            DrawLightDirGizmo(basePos - Vector3.forward * r, "S", def.lightSouth);
            DrawLightDirGizmo(basePos + Vector3.right   * r, "E", def.lightEast);
            DrawLightDirGizmo(basePos - Vector3.right   * r, "W", def.lightWest);

            // Tuned-light heads (item C data, item E gizmo hook) -- Near and
            // Far are independent lists now, drawn offset apart along the
            // road so their two (possibly different) head sets don't
            // visually overlap at the same point.
            var roadA = TempRoad(def.roadACode);
            var roadB = TempRoad(def.roadBCode);
            const float gizmoArmOffset = 4f;
            if (roadA != null)
            {
                Vector3 dA = roadA.EvaluateTangent(0.5f).normalized;
                if (def.headsANear != null && def.headsANear.Count > 0)
                    DrawHeadGizmos(def.position - dA * gizmoArmOffset, dA, roadA.roadWidth, def.headsANear, $"Road A Near ({def.roadACode})", false);
                if (def.headsAFar != null && def.headsAFar.Count > 0)
                    DrawHeadGizmos(def.position + dA * gizmoArmOffset, dA, roadA.roadWidth, def.headsAFar, $"Road A Far ({def.roadACode})", true);
            }
            if (roadB != null)
            {
                Vector3 dB = roadB.EvaluateTangent(0.5f).normalized;
                if (def.headsBNear != null && def.headsBNear.Count > 0)
                    DrawHeadGizmos(def.position - dB * gizmoArmOffset, dB, roadB.roadWidth, def.headsBNear, $"Road B Near ({def.roadBCode})", false);
                if (def.headsBFar != null && def.headsBFar.Count > 0)
                    DrawHeadGizmos(def.position + dB * gizmoArmOffset, dB, roadB.roadWidth, def.headsBFar, $"Road B Far ({def.roadBCode})", true);
            }
        }
    }

    /// <summary>Item E -- draws each configured head at its actual spawn
    /// position (matches SpawnHeadedLights exactly). isFarSide picks which
    /// HALF of the road this list is confined to (see
    /// TrafficLightHead.GetLateralOffset's own comment on why) -- Near's
    /// arrow runs from its outer edge (0%) to the centerline (100%), Far's
    /// runs from the centerline (0%) to its own outer edge (100%), so what's
    /// drawn always matches where this specific list's heads actually
    /// spawn, not the other list's half.</summary>
    private static void DrawHeadGizmos(Vector3 junctionPos, Vector3 roadDir, float roadWidth, List<TrafficLightHead> heads, string roadLabel, bool isFarSide)
    {
        // [FIX] Canonicalized -- see TrafficLightHead.CanonicalRoadDir's own comment.
        Vector3 roadRight  = Vector3.Cross(Vector3.up, TrafficLightHead.CanonicalRoadDir(roadDir)).normalized;
        // Confined-to-own-half + mirrored -- matches GetLateralOffset. Near's
        // arrow runs centerline(0%)->outer edge(100%); Far's runs the other
        // direction, outer edge(0%)->centerline(100%), on the OTHER half.
        float   halfWidth  = roadWidth * 0.5f;
        Vector3 zeroPct    = junctionPos + roadRight * (isFarSide ? -halfWidth : 0f) + Vector3.up * 0.9f;
        Vector3 hundredPct = junctionPos + roadRight * (isFarSide ? 0f : halfWidth)  + Vector3.up * 0.9f;

        UnityEditor.Handles.color = new Color(1f, 1f, 1f, 0.5f);
        UnityEditor.Handles.DrawLine(zeroPct, hundredPct);
        UnityEditor.Handles.ConeHandleCap(0, hundredPct, Quaternion.LookRotation(roadRight, Vector3.up), 0.5f, EventType.Repaint);
        UnityEditor.Handles.Label(zeroPct    + Vector3.up * 0.5f, $"{roadLabel}\n0%");
        UnityEditor.Handles.Label(hundredPct + Vector3.up * 0.5f, "100%");

        foreach (var head in heads)
        {
            if (head == null) continue;
            Vector3 pos = junctionPos + roadRight * head.GetLateralOffset(roadWidth, isFarSide) + Vector3.up * 0.6f;
            Color col = !head.enabled ? new Color(0.5f, 0.5f, 0.5f, 0.4f)
                      : head.symbol switch
                        {
                            TrafficLightSymbol.LeftArrow    => new Color(0.3f, 0.6f, 1f),
                            TrafficLightSymbol.RightArrow   => new Color(1f, 0.6f, 0.2f),
                            TrafficLightSymbol.StraightArrow=> new Color(0.6f, 1f, 0.3f),
                            _                                => new Color(1f, 1f, 1f),
                        };
            Gizmos.color = col;
            Gizmos.DrawCube(pos, new Vector3(head.GetHeadWidth(roadWidth), 0.3f, 0.6f));
            UnityEditor.Handles.color = col;
            UnityEditor.Handles.Label(pos + Vector3.up * 0.8f,
                $"{head.symbol}  {head.startPercent:0}-{head.endPercent:0}%  {head.greenStartSeconds:0}s+{head.greenDurationSeconds:0}s");
        }
    }

    private static void DrawLightDirGizmo(Vector3 pos, string label, bool on)
    {
        Gizmos.color = on ? new Color(0.2f, 1f, 0.3f) : new Color(0.5f, 0.5f, 0.5f, 0.5f);
        Gizmos.DrawSphere(pos, 0.8f);
        Gizmos.color = Color.white;
        Gizmos.DrawWireSphere(pos, 0.8f);
        UnityEditor.Handles.color = on ? Color.green : Color.gray;
        UnityEditor.Handles.Label(pos + Vector3.up * 1.4f, label);
    }

    /// <summary>Arrow gizmos along every one-way road showing the actual
    /// direction of travel (accounting for reverseFlow), so a one-way's
    /// direction is visible in Edit Mode without needing to press Play.</summary>
    private void DrawOneWayArrowGizmos()
    {
        if (roadDefinitions == null) return;

        foreach (var def in roadDefinitions)
        {
            if (def == null || !def.isOneWay) continue;

            var pts = def.BuildControlPoints();
            if (pts == null || pts.Length < 2) continue;
            var seg = new RoadSegment { controlPoints = pts };

            const int arrowCount = 6;
            for (int i = 1; i < arrowCount; i++)
            {
                float t = i / (float)arrowCount;
                Vector3 pos = seg.EvaluatePosition(t) + Vector3.up * 0.6f;
                Vector3 dir = seg.EvaluateTangent(t);
                if (def.reverseFlow) dir = -dir;
                DrawArrowGizmo(pos, dir, Mathf.Max(2f, def.roadWidth * 0.4f));
            }
        }
    }

    private static void DrawArrowGizmo(Vector3 pos, Vector3 dir, float length)
    {
        if (dir.sqrMagnitude < 0.0001f) return;
        dir.Normalize();

        Gizmos.color = Color.cyan;
        Vector3 tip  = pos + dir * (length * 0.5f);
        Vector3 tail = pos - dir * (length * 0.5f);
        Gizmos.DrawLine(tail, tip);

        Vector3 right = Vector3.Cross(Vector3.up, dir).normalized;
        Vector3 back  = tip - dir * (length * 0.35f);
        Gizmos.DrawLine(tip, back + right * (length * 0.2f));
        Gizmos.DrawLine(tip, back - right * (length * 0.2f));
    }

    private static void DrawFlatCircle(Vector3 center, float radius, int segments)
    {
        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float t = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = center + new Vector3(Mathf.Cos(t) * radius, 0f, Mathf.Sin(t) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
#endif

    // ── Inner types ───────────────────────────────────────────────────────────
    [System.Serializable]
    public class JunctionLaneData
    {
        public string    roadACode, roadBCode;
        public Transform roadAEntry, roadAExit;
        public Transform roadBEntry, roadBExit;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  ROAD SEGMENT DEFINITION
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class RoadSegmentDefinition
{
    [Header("Identity")]
    public string roadName  = "New Road";
    public string roadCode  = "CODE";
    public float  roadWidth = 7f;
    [Range(0.1f, 1f)] public float meshResolution = 0.5f;

    [Header("Traffic Flow")]
    public bool  canHaveIntersection = true;
    public bool  isOneWay   = false;
    public bool  reverseFlow = false;
    public bool hasSidewalks = true;
    // [DESIGN] Inverted on purpose: "hide" defaults to false instead of a
    // "has" flag defaulting to true. Roads created via JsonUtility (backup
    // import tools) are allocated without ever running C# field
    // initializers -- a `= true` default would silently come back false for
    // every road that ever passed through an import path, even though
    // hand-authored roads in the Inspector would show true. A missing/
    // never-serialized bool defaults to false either way, so making false
    // mean "show the line" makes centerlines robustly on-by-default no
    // matter how the road definition was constructed.
    [Tooltip("Suppress the auto-spawned yellow center-line prefab on this one road (see CityManager's Road Center Line Markings section for the shared prefab/settings). Off (default) = this road gets a center line like every other road.")]
    public bool hideCenterLine = false;

    [Header("Lanes (for RoadGraph / traffic AI)")]
    [Tooltip("Total lanes across both directions (or all lanes if one-way). 0 = derive a reasonable default from roadWidth.")]
    public int   laneCount   = 0;
    [Tooltip("0 = derive from roadWidth / laneCount.")]
    public float laneWidth   = 0f;
    [Tooltip("Meters/sec. 0 = derive a default (~30mph) until you wire real speed zones.")]
    public float speedLimit  = 0f;

    /// Effective lane count — falls back to a width-based guess if unset, so existing
    /// roads authored before this field existed don't need to be touched by hand.
    public int laneCountOrDefault => laneCount > 0 ? laneCount : Mathf.Max(1, Mathf.RoundToInt(roadWidth / 3.5f));

    public float laneWidthOrDefault => laneWidth > 0f ? laneWidth : (roadWidth / Mathf.Max(1, laneCountOrDefault));

    public float speedLimitOrDefault => speedLimit > 0f ? speedLimit : 13.4f; // ~30mph in m/s

    [Header("Curve Mode")]
    public RoadCurveMode curveMode = RoadCurveMode.ControlPoints;

    [Header("Control Points")]
    [Tooltip("Green time in seconds for auto-spawned intersections on this road.")]
    public float greenTime = 10f;
    [Header("Visuals")]
[Tooltip("Material for this road's mesh. Leave null to use CityManager's default road material.")]
public Material roadMaterial;
[Tooltip("Tint applied on top of roadMaterial. Set individually, or use CityManager's 'Paint All Roads' tool to apply one color everywhere at once.")]
public Color roadColor = Color.white;
    public List<Vector3> controlPoints = new List<Vector3> { Vector3.zero, new Vector3(0, 0, 100) };

    [Header("Math Equation")]
    public Vector3      mathStart  = Vector3.zero;
    public Vector3      mathEnd    = new Vector3(0, 0, 200);
    public MathWaveform waveform   = MathWaveform.Sine;
    public float        amplitude  = 30f;
    public float        frequency  = 1f;
    public float        phase      = 0f;
    [Range(4, 128)] public int sampleCount = 24;

    public Vector3[] BuildControlPoints()
    {
        if (curveMode == RoadCurveMode.ControlPoints)
            return controlPoints != null && controlPoints.Count >= 2
                ? controlPoints.ToArray()
                : new[] { Vector3.zero, new Vector3(0, 0, 100) };

        Vector3 dir    = mathEnd - mathStart;
        float   length = dir.magnitude;
        if (length < 0.001f) return new[] { mathStart, mathEnd };

        Vector3 forward = dir.normalized;
        Vector3 side    = new Vector3(-forward.z, 0f, forward.x).normalized;

        var pts = new Vector3[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            float t       = (float)i / (sampleCount - 1);
            float angle   = t * frequency * Mathf.PI * 2f + phase;
            float lateral = amplitude * Waveform(angle, t);
            pts[i]        = mathStart + forward * (t * length) + side * lateral;
        }
        return pts;
    }

    private float Waveform(float angle, float t)
    {
        switch (waveform)
        {
            case MathWaveform.Sine:       return Mathf.Sin(angle);
            case MathWaveform.Cosine:     return Mathf.Cos(angle);
            case MathWaveform.Arc:        return 4f * t * (1f - t);
            case MathWaveform.Polynomial: return t * t * (3f - 2f * t) - 0.5f;
            case MathWaveform.Zigzag:     return Mathf.PingPong(angle / Mathf.PI, 1f) * 2f - 1f;
            default:                      return 0f;
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  INTERSECTION DEFINITION
// ═══════════════════════════════════════════════════════════════════════════════
[System.Serializable]
public class IntersectionDefinition
{
    public string  roadACode;
    public string  roadBCode;
    public Vector3 position;

    [Tooltip("MAJOR intersections stay on their normal full green/yellow/red cycle at night. Everything else automatically switches to 4-way flashing (yellow one axis, red the other) during SimClock's 0-5 AM hours.")]
    public bool isMajorIntersection = false;

    [Header("Traffic Light Configuration")]
    public bool lightNorth = true;
    public bool lightSouth = true;
    public bool lightEast  = true;
    public bool lightWest  = true;

    [Header("Timing (Seconds)")]
    public float roadAGreenTime = 10f;
    public float roadBGreenTime = 30f;
    public float yellowTime     = 3f;

    // ── Tuned traffic lights (item C) ──────────────────────────────────────────
    // Optional per-approach light heads -- empty by default (every existing
    // and every newly-created intersection keeps exactly today's single-
    // slab-per-side behavior until a designer explicitly adds heads here via
    // the editor). When non-empty, CreateProceduralJunction spawns one slab
    // per head instead of the single default slab, positioned/sized from
    // each head's startPercent/endPercent (percent of the road's WIDTH, not
    // lane-derived) -- since the heads are laterally spread across the
    // road, a bus naturally detects whichever one is nearest to its own
    // lane position (the existing nearest-collider obstacle check already
    // does this generically, no per-lane binding logic needed).
    //
    // [CHANGED] A road has TWO physical light posts at this junction --
    // e.g. northbound and southbound -- and these used to share ONE list
    // (headsA) forced onto both, which meant they could never have
    // different setups even though they're physically separate signals.
    // Split into Near/Far (matching CreateProceduralJunction's own posSide
    // naming: Near = posSide false, Far = posSide true) so each physical
    // post is independently configurable. Both still run on the SAME
    // roadAGreenTime/roadBGreenTime phase timing (that's the road's shared
    // signal cycle, not per-post) -- only the heads themselves (symbol,
    // width, sub-timing) are now separate per post. 0%/100% for each list
    // still mean the same two physical edges of the road (see item E's
    // gizmo arrow) -- Near and Far just aren't forced to use the same
    // values anymore.
    [Header("Tuned Lights (Editor only — leave empty for default single-light behavior)")]
    public List<TrafficLightHead> headsANear = new();
    public List<TrafficLightHead> headsAFar  = new();
    public List<TrafficLightHead> headsBNear = new();
    public List<TrafficLightHead> headsBFar  = new();
}

public enum TrafficLightSymbol { Circle, LeftArrow, RightArrow, StraightArrow }

/// <summary>One light head within a side's overall green phase (see
/// IntersectionDefinition.headsANear/headsAFar/headsBNear/headsBFar). symbol is documentation/gizmo-only
/// today -- no arrow-shaped mesh gets generated, heads still render as a
/// plain colored slab like the existing single-light default, just possibly
/// several side by side. What IS real: each head gets its own position/width
/// (own collider a bus can detect) and its own green sub-window within the
/// parent side's overall green time, via greenStartSeconds/greenDurationSeconds
/// -- e.g. a protected-left head that's only green for the first few seconds
/// of Road A's phase while the main through head stays green the whole time.</summary>
[System.Serializable]
public class TrafficLightHead
{
    public bool enabled = true;
    public TrafficLightSymbol symbol = TrafficLightSymbol.Circle;

    // [CHANGED] Was a raw world-unit lateralOffset/headWidth pair -- required
    // knowing the road's exact width to place correctly, and didn't make
    // "split the road into 3 even zones" easy to express (you'd need to
    // compute 3 world-unit offsets by hand). Percent-of-road-width range is
    // resolution-independent and reads directly as "this head covers the
    // left third," etc. -- three heads at 0-33/33-66/66-100 is the natural
    // way to think about a 3-way split. GetLateralOffset/GetHeadWidth below
    // convert to world units wherever an actual position/size is needed
    // (spawning, gizmos, handles).
    [Tooltip("Where this head's zone starts across the road, as a percent of the road's width (0 = left edge).")]
    [Range(0f, 100f)] public float startPercent = 0f;
    [Tooltip("Where this head's zone ends across the road, as a percent of the road's width (100 = right edge). Three heads at 0-33 / 33-66 / 66-100 splits the road into even thirds.")]
    [Range(0f, 100f)] public float endPercent = 100f;

    [Tooltip("Seconds into this side's green phase when this head turns green. 0 = green from the start of the phase, same as the default single light.")]
    public float greenStartSeconds = 0f;
    [Tooltip("How long this head stays green before reverting to red for the remainder of the phase. Leave large (999) to stay green the whole phase, matching today's default.")]
    public float greenDurationSeconds = 999f;

    /// <summary>[FIX] A road's tangent (RoadSegment.EvaluateTangent) points
    /// whichever way its control points happen to be authored -- there's no
    /// rule that a north-south road's points run south-to-north specifically,
    /// so roadRight (Cross(up, tangent)) flips sign purely based on authoring
    /// direction, not anything meaningful about the road. That flip mirrored
    /// every head's 0%/100% left-right mapping between an otherwise-identical
    /// "north-authored" road and a "south-authored" one (reported: "north is
    /// good but south needs inverting, same for east vs west"). Canonicalizes
    /// the tangent so its DOMINANT axis (whichever of X/Z has the larger
    /// magnitude) always points positive before anything computes roadRight
    /// from it -- every road, regardless of authoring direction, now resolves
    /// heads the same way. Used by SpawnHeadedLights, DrawHeadGizmos, and
    /// IntersectionEditorWindow.DrawHeadHandles -- NOT by the default single-
    /// light path (SpawnLight), which intentionally uses the raw, direction-
    /// sensitive tangent for its own lane-keeping logic and was never
    /// affected by this.</summary>
    public static Vector3 CanonicalRoadDir(Vector3 dir)
    {
        return Mathf.Abs(dir.x) >= Mathf.Abs(dir.z)
            ? (dir.x < 0f ? -dir : dir)
            : (dir.z < 0f ? -dir : dir);
    }

    /// <summary>World-unit offset from the road's centerline, along the
    /// road's own right-vector, for this head's CENTER point.
    ///
    /// Two requirements confirmed together: (1) each list stays confined to
    /// its OWN half of the road -- Near always resolves into the positive
    /// half [0, +halfWidth], Far always into the negative half
    /// [-halfWidth, 0], so a head can never bleed across the centerline
    /// into the opposing direction's lanes; (2) Far's percent is read
    /// backwards (0<->100, 30<->70, etc.) before mapping, so two
    /// IDENTICALLY-authored heads on Near and Far (the natural way to set
    /// up a symmetric 2-way road) land MIRRORED across the centerline
    /// instead of both sitting at the same relative spot in their own
    /// half.</summary>
    public float GetLateralOffset(float roadWidth, bool isFarSide)
    {
        float centerPercent = (Mathf.Min(startPercent, endPercent) + Mathf.Max(startPercent, endPercent)) * 0.5f;
        float halfWidth     = roadWidth * 0.5f;

        // Near: x maps straight onto its own half, 0..halfWidth.
        // Far: (x - 100) instead of -(100 - x) -- same value (they're
        // algebraically identical), written the way it was asked for.
        // x-100 is already negative for any x in [0,100], which is what
        // lands it on Far's own half (-halfWidth..0) with no separate
        // negation step needed.
        return isFarSide
            ? ((centerPercent - 100f) / 100f) * halfWidth
            : (centerPercent / 100f) * halfWidth;
    }

    /// <summary>World-unit width of this head's slab -- scaled against half
    /// the road width, matching GetLateralOffset's confined-to-one-half
    /// reference frame.</summary>
    public float GetHeadWidth(float roadWidth)
    {
        float spanPercent = Mathf.Abs(endPercent - startPercent);
        return Mathf.Max(0.1f, spanPercent / 100f * (roadWidth * 0.5f));
    }
}

/// <summary>Pairs a spawned head slab GameObject with the TrafficLightHead
/// config that governs its own green sub-window within its side's overall
/// phase. See ProceduralJunctionController.UpdateHeadVisuals.</summary>
public class JunctionHeadRuntime
{
    public GameObject go;
    public TrafficLightHead config;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  PROCEDURAL JUNCTION CONTROLLER
// ═══════════════════════════════════════════════════════════════════════════════
public class ProceduralJunctionController : MonoBehaviour
{
    private enum JunctionState { RoadA_Go, RoadA_Yellow, RoadB_Go, RoadB_Yellow }
    private JunctionState _currentState;

    private float _timeAGreen, _timeBGreen, _timeYellow;
    private float _timer;

    private Material _matRed;
    private Material _matYellow;

    private List<GameObject> _nsLights = new();
    private List<GameObject> _ewLights = new();
    // [ADD] Tuned lights (item C) -- subset of _nsLights/_ewLights that also
    // have their own TrafficLightHead sub-timing. Still included in
    // _nsLights/_ewLights too (so night-flashing and the base red/green
    // pass still apply to them the same as any other slab); this list is
    // consulted AFTER that base pass, per-head, to override with the head's
    // own green sub-window when its parent side is in its "Go" phase.
    private List<JunctionHeadRuntime> _nsHeads = new();
    private List<JunctionHeadRuntime> _ewHeads = new();

    // ── Night flashing ──────────────────────────────────────────────────
    // Same timing/spacing logic as before for everything else -- this is
    // the ONE addition: MAJOR junctions never flash, everyone else switches
    // to a classic 4-way flash (yellow one axis, red the other) during
    // SimClock's 0-5 AM hours instead of running the normal cycle.
    private bool  _isMajor;
    private const float NightFlashEndMinutes = 5f * 60f; // 5:00 AM
    private const float FlashInterval        = 0.5f;
    private float _flashTimer;
    private bool  _flashOn;
    private bool  _wasFlashing;

    public void Initialize(List<GameObject> ns, List<GameObject> ew,
                           Material red, Material yellowMat,
                           float greenA, float greenB, float yellowTime,
                           bool isMajor = false,
                           List<JunctionHeadRuntime> nsHeads = null, List<JunctionHeadRuntime> ewHeads = null)
    {
        _nsLights   = ns;
        _ewLights   = ew;
        _nsHeads    = nsHeads ?? new List<JunctionHeadRuntime>();
        _ewHeads    = ewHeads ?? new List<JunctionHeadRuntime>();
        _matRed     = red;
        _matYellow  = yellowMat;
        _timeAGreen = greenA;
        _timeBGreen = greenB;
        _timeYellow = yellowTime;
        _isMajor    = isMajor;
        SetState(JunctionState.RoadA_Go);
    }

    private void Update()
    {
        if (_matRed == null) return;

        bool flashing = !_isMajor && SimClock.Instance != null &&
                        SimClock.Instance.GameTimeMinutes < NightFlashEndMinutes;

        if (flashing)
        {
            if (!_wasFlashing) { _flashTimer = 0f; _flashOn = false; } // start crisp on transition into flash mode
            _wasFlashing = true;
            UpdateNightFlash();
            return;
        }

        if (_wasFlashing)
        {
            // Coming out of flash mode -- resume the normal timed cycle
            // fresh from RoadA_Go rather than trusting a timer that was
            // frozen for hours.
            _wasFlashing = false;
            SetState(JunctionState.RoadA_Go);
        }

        _timer -= Time.deltaTime;
        if (_timer <= 0f) AdvanceState();

        // [ADD] Item C -- ticked every frame (not just on phase transitions
        // like the base SetGroupVisuals pass) so a head's own green window
        // can start/end mid-phase, not only at the phase boundary.
        UpdateHeadVisuals();
    }

    /// <summary>Item C -- per-head sub-timing within whichever side is
    /// currently in its Go/Yellow phase. Runs after the base group-wide
    /// pass (UpdateLightVisuals, called from SetState on transitions) and
    /// fully overrides it for any head slab, since this recomputes each
    /// head's correct visual state fresh every frame regardless of what the
    /// group pass left it as. Heads on the side NOT currently in a Go phase
    /// just stay red, matching the base single-light behavior. During
    /// Yellow, all of that side's heads go yellow together -- no per-head
    /// yellow sub-window, only the green window is head-specific.</summary>
    private void UpdateHeadVisuals()
    {
        ApplyHeadWindow(_nsHeads, _currentState == JunctionState.RoadA_Go, _currentState == JunctionState.RoadA_Yellow, _timeAGreen);
        ApplyHeadWindow(_ewHeads, _currentState == JunctionState.RoadB_Go, _currentState == JunctionState.RoadB_Yellow, _timeBGreen);
    }

    private void ApplyHeadWindow(List<JunctionHeadRuntime> heads, bool inGoPhase, bool inYellowPhase, float phaseDuration)
    {
        if (heads == null || heads.Count == 0) return;
        float elapsed = phaseDuration - (inGoPhase ? _timer : 0f);

        foreach (var h in heads)
        {
            if (h.go == null || h.config == null) continue;

            if (inYellowPhase)
            {
                h.go.SetActive(true);
                var rendY = h.go.GetComponent<Renderer>();
                if (rendY != null) rendY.sharedMaterial = _matYellow;
                continue;
            }
            if (!inGoPhase)
            {
                h.go.SetActive(true);
                var rendR = h.go.GetComponent<Renderer>();
                if (rendR != null) rendR.sharedMaterial = _matRed;
                continue;
            }

            bool headGreen = elapsed >= h.config.greenStartSeconds
                           && elapsed < h.config.greenStartSeconds + h.config.greenDurationSeconds;
            if (headGreen)
            {
                h.go.SetActive(false); // matches SetGroupVisuals' "green = hidden slab" convention
            }
            else
            {
                h.go.SetActive(true);
                var rend = h.go.GetComponent<Renderer>();
                if (rend != null) rend.sharedMaterial = _matRed;
            }
        }
    }

    private void UpdateNightFlash()
    {
        _flashTimer -= Time.deltaTime;
        if (_flashTimer <= 0f)
        {
            _flashTimer = FlashInterval;
            _flashOn = !_flashOn;

            // Classic 4-way overnight flash: one street flashes yellow
            // (caution, keep moving), the crossing street flashes red
            // (caution, treat as a stop) -- same NS/EW grouping the normal
            // cycle already uses, just both blinking together instead of
            // taking turns with green.
            SetGroupVisualsFlash(_nsLights, _matYellow, _flashOn);
            SetGroupVisualsFlash(_ewLights, _matRed,    _flashOn);
        }
    }

    private static void SetGroupVisualsFlash(List<GameObject> group, Material mat, bool on)
    {
        foreach (var go in group)
        {
            if (go == null) continue;
            go.SetActive(on);
            if (!on) continue;
            var rend = go.GetComponent<Renderer>();
            if (rend != null) rend.sharedMaterial = mat;
        }
    }

    private void SetState(JunctionState newState)
    {
        _currentState = newState;
        switch (_currentState)
        {
            case JunctionState.RoadA_Go:     _timer = _timeAGreen; break;
            case JunctionState.RoadA_Yellow: _timer = _timeYellow; break;
            case JunctionState.RoadB_Go:     _timer = _timeBGreen; break;
            case JunctionState.RoadB_Yellow: _timer = _timeYellow; break;
        }
        UpdateLightVisuals();
    }

    private void AdvanceState()
    {
        switch (_currentState)
        {
            case JunctionState.RoadA_Go:     SetState(JunctionState.RoadA_Yellow); break;
            case JunctionState.RoadA_Yellow: SetState(JunctionState.RoadB_Go);     break;
            case JunctionState.RoadB_Go:     SetState(JunctionState.RoadB_Yellow); break;
            case JunctionState.RoadB_Yellow: SetState(JunctionState.RoadA_Go);     break;
        }
    }

    private void UpdateLightVisuals()
    {
        switch (_currentState)
        {
            case JunctionState.RoadA_Go:
                SetGroupVisuals(_nsLights, TrafficLightState.Green);
                SetGroupVisuals(_ewLights, TrafficLightState.Red);
                break;
            case JunctionState.RoadA_Yellow:
                SetGroupVisuals(_nsLights, TrafficLightState.Yellow);
                SetGroupVisuals(_ewLights, TrafficLightState.Red);
                break;
            case JunctionState.RoadB_Go:
                SetGroupVisuals(_nsLights, TrafficLightState.Red);
                SetGroupVisuals(_ewLights, TrafficLightState.Green);
                break;
            case JunctionState.RoadB_Yellow:
                SetGroupVisuals(_nsLights, TrafficLightState.Red);
                SetGroupVisuals(_ewLights, TrafficLightState.Yellow);
                break;
        }
    }

    private void SetGroupVisuals(List<GameObject> group, TrafficLightState state)
    {
        foreach (var go in group)
        {
            if (go == null) continue;
            if (state == TrafficLightState.Green)
            {
                go.SetActive(false);
            }
            else
            {
                go.SetActive(true);
                var rend = go.GetComponent<Renderer>();
                if (rend != null)
                    rend.sharedMaterial = state == TrafficLightState.Yellow ? _matYellow : _matRed;
            }
        }
    }

    public bool IsLightStopping(GameObject lightSlab, Vector3 busForward)
    {
        if (lightSlab == null || !lightSlab.activeInHierarchy) return false;
        float dot = Vector3.Dot(busForward.normalized, lightSlab.transform.forward.normalized);
        if (dot > -0.4f) return false;
        var mr = lightSlab.GetComponent<MeshRenderer>();
        if (mr == null) return false;
        return mr.sharedMaterial == _matRed || mr.sharedMaterial == _matYellow;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  BUS STOP MARKER
// ═══════════════════════════════════════════════════════════════════════════════
public class BusStopMarker : MonoBehaviour
{
    public BusStopData Data     { get; set; }
    public string StopCode      => Data?.stopCode   ?? "";
    public string StopName      => Data?.stopName   ?? "";
    public bool   IsTerminal    => Data?.isTerminal ?? false;
    public bool   IsLayover     => Data?.isLayover  ?? false;
}

// ═══════════════════════════════════════════════════════════════════════════════
//  CITY MANAGER EDITOR
//
//  Two jobs: (1) the Inspector-lag fix -- stopDefinitions used Unity's
//  default array drawer, which renders EVERY element's full field set
//  (foldout + ~8 labeled fields + reorder handle) simultaneously; at 500+
//  stops that's thousands of GUI controls every repaint. Replaced with a
//  paginated, one-line-per-stop list (search box, 25/page, expand-to-edit
//  per row) that only ever draws a small window of rows at once.
//  (2) items B+E -- an intersection finder (list + frame-in-Scene-view) and
//  Scene-view drag handles for each configured tuned-light head's lateral
//  position, keyed off the selected intersection below.
// ═══════════════════════════════════════════════════════════════════════════════
#if UNITY_EDITOR
[CustomEditor(typeof(CityManager))]
public class CityManagerEditor : Editor
{
    private bool _stopsFoldout = true;
    private string _stopSearch = "";
    private int _stopsPage = 0;
    private const int StopsPerPage = 25;
    private readonly HashSet<int> _expandedStops = new();

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawPropertiesExcluding(serializedObject, "m_Script", "stopDefinitions", "manualIntersections");

        EditorGUILayout.Space(10);
        DrawStopsSection();

        EditorGUILayout.Space(10);
        // [MOVED] Intersection finder + tuned-light head editing (items B/C/E)
        // now live in their own window -- Tools > City Building > Intersection
        // & Traffic Light Editor -- instead of being buried in this Inspector,
        // so they're reachable the same way any other editor tool in this
        // project is, and so adding a brand-new intersection (which needs its
        // own click-to-place-in-Scene-view workflow, not just editing existing
        // ones) has somewhere sensible to live.
        if (GUILayout.Button("Open Intersection & Traffic Light Editor...", GUILayout.Height(28)))
            IntersectionEditorWindow.ShowWindow();

        serializedObject.ApplyModifiedProperties();
    }

    // ── Stops (Inspector-lag fix) ─────────────────────────────────────────────
    private void DrawStopsSection()
    {
        var stopsProp = serializedObject.FindProperty("stopDefinitions");
        if (stopsProp == null) return;

        _stopsFoldout = EditorGUILayout.Foldout(_stopsFoldout, $"Stop Definitions ({stopsProp.arraySize})", true);
        if (!_stopsFoldout) return;

        EditorGUILayout.BeginHorizontal();
        _stopSearch = EditorGUILayout.TextField("Search (code or name)", _stopSearch);
        if (GUILayout.Button("Add Stop", GUILayout.Width(80)))
            stopsProp.InsertArrayElementAtIndex(stopsProp.arraySize);
        EditorGUILayout.EndHorizontal();

        // Filtering is just string comparisons over already-loaded
        // SerializedProperty data -- cheap even at 500+, it's the GUI
        // DRAWING (below, capped at one page) that was actually expensive.
        var matches = new List<int>(stopsProp.arraySize);
        for (int i = 0; i < stopsProp.arraySize; i++)
        {
            var el = stopsProp.GetArrayElementAtIndex(i);
            string code = el.FindPropertyRelative("stopCode")?.stringValue ?? "";
            string name = el.FindPropertyRelative("stopName")?.stringValue ?? "";
            if (string.IsNullOrEmpty(_stopSearch)
                || code.IndexOf(_stopSearch, System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf(_stopSearch, System.StringComparison.OrdinalIgnoreCase) >= 0)
                matches.Add(i);
        }

        int pageCount = Mathf.Max(1, Mathf.CeilToInt(matches.Count / (float)StopsPerPage));
        _stopsPage = Mathf.Clamp(_stopsPage, 0, pageCount - 1);

        EditorGUILayout.BeginHorizontal();
        GUI.enabled = _stopsPage > 0;
        if (GUILayout.Button("◀ Prev", GUILayout.Width(70))) _stopsPage--;
        GUI.enabled = true;
        EditorGUILayout.LabelField($"Page {_stopsPage + 1}/{pageCount}  ({matches.Count} shown)", EditorStyles.centeredGreyMiniLabel);
        GUI.enabled = _stopsPage < pageCount - 1;
        if (GUILayout.Button("Next ▶", GUILayout.Width(70))) _stopsPage++;
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        int start = _stopsPage * StopsPerPage;
        int end   = Mathf.Min(start + StopsPerPage, matches.Count);
        int deleteIndex = -1;
        for (int m = start; m < end; m++)
        {
            int i = matches[m];
            if (DrawStopRow(stopsProp, i)) deleteIndex = i;
        }
        if (deleteIndex >= 0) stopsProp.DeleteArrayElementAtIndex(deleteIndex);
    }

    /// <summary>Returns true if this row's delete button was pressed
    /// (caller deletes AFTER the loop finishes -- deleting mid-loop would
    /// shift every later index in the same repaint pass).</summary>
    private bool DrawStopRow(SerializedProperty stopsProp, int index)
    {
        var el = stopsProp.GetArrayElementAtIndex(index);
        var codeProp = el.FindPropertyRelative("stopCode");
        var nameProp = el.FindPropertyRelative("stopName");
        bool deletePressed = false;

        EditorGUILayout.BeginHorizontal();
        bool expanded = _expandedStops.Contains(index);
        if (GUILayout.Button(expanded ? "▼" : "▶", GUILayout.Width(20)))
        {
            if (expanded) _expandedStops.Remove(index); else _expandedStops.Add(index);
        }
        EditorGUILayout.LabelField($"[{index}]", GUILayout.Width(36));
        if (codeProp != null) codeProp.stringValue = EditorGUILayout.TextField(codeProp.stringValue, GUILayout.Width(90));
        if (nameProp != null) nameProp.stringValue = EditorGUILayout.TextField(nameProp.stringValue);
        if (GUILayout.Button("✕", GUILayout.Width(24))) deletePressed = true;
        EditorGUILayout.EndHorizontal();

        if (expanded && !deletePressed)
        {
            EditorGUI.indentLevel++;
            DrawIfPresent(el, "parentRoadCode");
            DrawIfPresent(el, "tValue");
            DrawIfPresent(el, "hasShelter");
            DrawIfPresent(el, "isTerminal");
            DrawIfPresent(el, "isLayover");
            DrawIfPresent(el, "isAccessible");
            EditorGUI.indentLevel--;
        }
        return deletePressed;
    }

    private static void DrawIfPresent(SerializedProperty parent, string relativeName)
    {
        var p = parent.FindPropertyRelative(relativeName);
        if (p != null) EditorGUILayout.PropertyField(p);
    }

}
#endif