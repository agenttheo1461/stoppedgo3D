using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Standalone backup tool for CityManager's hand-authored Inspector data
/// (roads, stops, manual intersections, + top-level settings). Read-only —
/// never writes back to CityManager, so it can't corrupt or override anything
/// you've set up. Every export is a NEW timestamped file, so older backups
/// are never overwritten either.
///
/// Attach to the SAME GameObject as your CityManager.
/// </summary>
[RequireComponent(typeof(CityManager))]
public class CityDataExporter : MonoBehaviour
{
    [Header("Backup Folder (inside Assets/)")]
    public string backupFolder = "CityBackups";

    private CityManager _city;

    private void Awake() => _city = GetComponent<CityManager>();

#if UNITY_EDITOR
    [ContextMenu("Export City Data Backup")]
    public void ExportBackup()
    {
        if (_city == null) _city = GetComponent<CityManager>();
        if (_city == null)
        {
            Debug.LogError("[CityDataExporter] No CityManager found on this GameObject.");
            return;
        }

        var snapshot = BuildSnapshot(_city);
        string json  = JsonUtility.ToJson(snapshot, true);

        string dir = Path.Combine(Application.dataPath, backupFolder);
        Directory.CreateDirectory(dir);

        string fileName = $"CityBackup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json";
        string fullPath = Path.Combine(dir, fileName);

        File.WriteAllText(fullPath, json);
        AssetDatabase.Refresh();

        int totalHeads = 0;
        foreach (var i in snapshot.intersections)
            totalHeads += i.headsANear.Count + i.headsAFar.Count + i.headsBNear.Count + i.headsBFar.Count;

        Debug.Log($"[CityDataExporter] Exported {snapshot.roads.Count} roads, " +
                  $"{snapshot.stops.Count} stops, {snapshot.intersections.Count} intersections " +
                  $"({totalHeads} tuned-light heads across all of them) " +
                  $"→ Assets/{backupFolder}/{fileName}");
    }
#endif

    private CityDataSnapshot BuildSnapshot(CityManager city)
    {
        var snap = new CityDataSnapshot
        {
            exportedAt                  = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            sceneName                   = gameObject.scene.name,
            spawnIntersections          = city.spawnIntersections,
            includeTrafficLights        = city.includeTrafficLights,
            leftHandTraffic             = city.leftHandTraffic,
            intersectionCheckResolution = city.intersectionCheckResolution,
            junctionPavementSize        = city.junctionPavementSize,
            stopDetectRadius            = city.stopDetectRadius,
        };

        foreach (var r in city.roadDefinitions)
        {
            snap.roads.Add(new RoadBackup
            {
                roadName            = r.roadName,
                roadCode            = r.roadCode,
                roadWidth           = r.roadWidth,
                meshResolution      = r.meshResolution,
                canHaveIntersection = r.canHaveIntersection,
                isOneWay            = r.isOneWay,
                reverseFlow         = r.reverseFlow,
                hasSidewalks        = r.hasSidewalks,
                hideCenterLine       = r.hideCenterLine,
                curveMode           = r.curveMode.ToString(),
                greenTime           = r.greenTime,
                roadMaterialName    = r.roadMaterial != null ? r.roadMaterial.name : "",
                roadColor           = r.roadColor,
                controlPoints       = new List<Vector3>(r.controlPoints ?? new List<Vector3>()),
                mathStart           = r.mathStart,
                mathEnd             = r.mathEnd,
                waveform            = r.waveform.ToString(),
                amplitude           = r.amplitude,
                frequency           = r.frequency,
                phase               = r.phase,
                sampleCount         = r.sampleCount,
            });
        }

        foreach (var s in city.stopDefinitions)
        {
            snap.stops.Add(new StopBackup
            {
                stopCode       = s.stopCode,
                stopName       = s.stopName,
                parentRoadCode = s.parentRoadCode,
                tValue         = s.tValue,
                hasShelter     = s.hasShelter,
                isTerminal     = s.isTerminal,
                isLayover      = s.isLayover,
                isAccessible   = s.isAccessible,
            });
        }

        foreach (var i in city.manualIntersections)
        {
            snap.intersections.Add(new IntersectionBackup
            {
                roadACode      = i.roadACode,
                roadBCode      = i.roadBCode,
                position       = i.position,
                lightNorth     = i.lightNorth,
                lightSouth     = i.lightSouth,
                lightEast      = i.lightEast,
                lightWest      = i.lightWest,
                roadAGreenTime = i.roadAGreenTime,
                roadBGreenTime = i.roadBGreenTime,
                yellowTime     = i.yellowTime,
                // [ADD] Tuned lights (item C) -- headsANear/headsAFar/
                // headsBNear/headsBFar didn't exist when this exporter was
                // first written; a backup taken before this would have
                // silently dropped any tuned-light setup with no warning.
                headsANear     = BackupHeads(i.headsANear),
                headsAFar      = BackupHeads(i.headsAFar),
                headsBNear     = BackupHeads(i.headsBNear),
                headsBFar      = BackupHeads(i.headsBFar),
            });
        }

        return snap;
    }

    /// <summary>Shared by all 4 head lists above -- converts the live
    /// TrafficLightHead objects into the plain-DTO shape JsonUtility can
    /// actually serialize (JsonUtility can't handle a List<T> field showing
    /// up nested this deep without it being its own [Serializable] class,
    /// same reasoning RoadBackup/StopBackup/IntersectionBackup already
    /// follow for the rest of CityManager's data).</summary>
    private static List<TrafficLightHeadBackup> BackupHeads(List<TrafficLightHead> heads)
    {
        var result = new List<TrafficLightHeadBackup>();
        if (heads == null) return result;
        foreach (var h in heads)
        {
            if (h == null) continue;
            result.Add(new TrafficLightHeadBackup
            {
                enabled               = h.enabled,
                symbol                = h.symbol.ToString(),
                startPercent          = h.startPercent,
                endPercent            = h.endPercent,
                greenStartSeconds     = h.greenStartSeconds,
                greenDurationSeconds  = h.greenDurationSeconds,
            });
        }
        return result;
    }
}

[Serializable]
public class CityDataSnapshot
{
    public string exportedAt;
    public string sceneName;
    public bool   spawnIntersections;
    public bool   includeTrafficLights;
    public bool   leftHandTraffic;
    public int    intersectionCheckResolution;
    public float  junctionPavementSize;
    public float  stopDetectRadius;
    public List<RoadBackup>         roads         = new();
    public List<StopBackup>         stops         = new();
    public List<IntersectionBackup> intersections = new();
}

[Serializable]
public class RoadBackup
{
    public string roadName, roadCode;
    public float  roadWidth, meshResolution;
    public bool   canHaveIntersection, isOneWay, reverseFlow, hasSidewalks, hideCenterLine;
    public string curveMode;
    public float  greenTime;
    public string roadMaterialName;
    public Color  roadColor = Color.white; // [ADD] round-trips fully, unlike Material -- see note in CityDataImporter
    public List<Vector3> controlPoints = new();
    public Vector3 mathStart, mathEnd;
    public string  waveform;
    public float   amplitude, frequency, phase;
    public int     sampleCount;
}

[Serializable]
public class StopBackup
{
    public string stopCode, stopName, parentRoadCode;
    public float  tValue;
    public bool   hasShelter, isTerminal, isLayover, isAccessible;
}

[Serializable]
public class IntersectionBackup
{
    public string  roadACode, roadBCode;
    public Vector3 position;
    public bool    lightNorth, lightSouth, lightEast, lightWest;
    public float   roadAGreenTime, roadBGreenTime, yellowTime;
    // [ADD] Tuned lights (item C) -- see CityManager.IntersectionDefinition's
    // own comment for why these are 4 independent lists (Near/Far x A/B)
    // instead of one shared list per road.
    public List<TrafficLightHeadBackup> headsANear = new();
    public List<TrafficLightHeadBackup> headsAFar  = new();
    public List<TrafficLightHeadBackup> headsBNear = new();
    public List<TrafficLightHeadBackup> headsBFar  = new();
}

[Serializable]
public class TrafficLightHeadBackup
{
    public bool   enabled;
    public string symbol; // TrafficLightSymbol.ToString() -- parsed back via ParseEnum, same pattern curveMode/waveform already use
    public float  startPercent, endPercent;
    public float  greenStartSeconds, greenDurationSeconds;
}