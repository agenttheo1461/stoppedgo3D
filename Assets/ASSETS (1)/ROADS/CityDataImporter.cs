using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Restores CityManager's roadDefinitions / stopDefinitions / manualIntersections
/// (and top-level settings) from a CityDataExporter backup JSON. Always asks for
/// confirmation before replacing existing data, auto-exports a safety snapshot of
/// whatever's currently in the scene first, and runs through Undo.RecordObject so
/// the whole restore can be undone with Ctrl+Z if something looks wrong.
///
/// Attach to the SAME GameObject as your CityManager (and ideally CityDataExporter too).
/// </summary>
[RequireComponent(typeof(CityManager))]
public class CityDataImporter : MonoBehaviour
{
    private CityManager _city;
    private void Awake() => _city = GetComponent<CityManager>();

#if UNITY_EDITOR
    [ContextMenu("Restore From Backup...")]
    public void RestoreFromBackup()
    {
        if (_city == null) _city = GetComponent<CityManager>();
        if (_city == null)
        {
            Debug.LogError("[CityDataImporter] No CityManager found on this GameObject.");
            return;
        }

        string startDir = Path.Combine(Application.dataPath, "CityBackups");
        if (!Directory.Exists(startDir)) startDir = Application.dataPath;

        string path = EditorUtility.OpenFilePanel("Select City Backup", startDir, "json");
        if (string.IsNullOrEmpty(path)) return; // cancelled

        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception e)
        {
            Debug.LogError($"[CityDataImporter] Couldn't read file: {e.Message}");
            return;
        }

        CityDataSnapshot snap;
        try { snap = JsonUtility.FromJson<CityDataSnapshot>(json); }
        catch (Exception e)
        {
            Debug.LogError($"[CityDataImporter] Couldn't parse JSON — is this a valid backup file? {e.Message}");
            return;
        }

        if (snap == null || (snap.roads.Count == 0 && snap.stops.Count == 0 && snap.intersections.Count == 0))
        {
            Debug.LogWarning("[CityDataImporter] Backup file appears empty — aborting, nothing changed.");
            return;
        }

        bool hasExistingData = _city.roadDefinitions.Count > 0
                             || _city.stopDefinitions.Count > 0
                             || _city.manualIntersections.Count > 0;

        if (hasExistingData)
        {
            bool proceed = EditorUtility.DisplayDialog(
                "Restore City Data",
                $"CityManager currently has {_city.roadDefinitions.Count} roads, " +
                $"{_city.stopDefinitions.Count} stops, {_city.manualIntersections.Count} intersections.\n\n" +
                $"Backup '{Path.GetFileName(path)}' (exported {snap.exportedAt}) has " +
                $"{snap.roads.Count} roads, {snap.stops.Count} stops, {snap.intersections.Count} intersections.\n\n" +
                "Restoring will REPLACE all current data. The current state will be auto-backed-up " +
                "first, and this is also Undo-able (Ctrl+Z) if you change your mind.\n\nContinue?",
                "Restore", "Cancel");
            if (!proceed) return;

            // Safety net — snapshot whatever's in the scene right now before touching it.
            var exporter = GetComponent<CityDataExporter>();
            if (exporter != null) exporter.ExportBackup();
            else Debug.LogWarning("[CityDataImporter] No CityDataExporter on this GameObject — " +
                                   "skipped pre-restore safety backup.");
        }

        Undo.RecordObject(_city, "Restore City Data From Backup");
        ApplySnapshot(_city, snap);
        EditorUtility.SetDirty(_city);

        int totalHeads = 0;
        foreach (var i in snap.intersections)
            totalHeads += i.headsANear.Count + i.headsAFar.Count + i.headsBNear.Count + i.headsBFar.Count;

        Debug.Log($"[CityDataImporter] Restored {snap.roads.Count} roads, {snap.stops.Count} stops, " +
                  $"{snap.intersections.Count} intersections ({totalHeads} tuned-light heads) " +
                  $"from {Path.GetFileName(path)} (exported {snap.exportedAt}).");

        // Materials are object references and don't survive JSON — flag anything that had one.
        foreach (var r in snap.roads)
        {
            if (!string.IsNullOrEmpty(r.roadMaterialName))
                Debug.LogWarning($"[CityDataImporter] Road '{r.roadCode}' used material " +
                                  $"'{r.roadMaterialName}' — reassign it manually, materials " +
                                  "aren't stored in backups.");
        }
    }
#endif

    private void ApplySnapshot(CityManager city, CityDataSnapshot snap)
    {
        city.spawnIntersections          = snap.spawnIntersections;
        city.includeTrafficLights        = snap.includeTrafficLights;
        city.leftHandTraffic              = snap.leftHandTraffic;
        city.intersectionCheckResolution = snap.intersectionCheckResolution;
        city.junctionPavementSize        = snap.junctionPavementSize;
        city.stopDetectRadius            = snap.stopDetectRadius;

        var roads = new List<RoadSegmentDefinition>();
        foreach (var r in snap.roads)
        {
            roads.Add(new RoadSegmentDefinition
            {
                roadName            = r.roadName,
                roadCode            = r.roadCode,
                roadWidth           = r.roadWidth,
                meshResolution      = r.meshResolution,
                canHaveIntersection = r.canHaveIntersection,
                isOneWay            = r.isOneWay,
                reverseFlow         = r.reverseFlow,
                hasSidewalks        = r.hasSidewalks,
                curveMode           = ParseEnum(r.curveMode, RoadCurveMode.ControlPoints),
                greenTime           = r.greenTime,
                roadMaterial        = null, // can't round-trip — see warning log after restore
                roadColor           = r.roadColor, // [ADD] round-trips fully, unlike Material
                controlPoints       = new List<Vector3>(r.controlPoints ?? new List<Vector3>()),
                mathStart           = r.mathStart,
                mathEnd             = r.mathEnd,
                waveform            = ParseEnum(r.waveform, MathWaveform.Sine),
                amplitude           = r.amplitude,
                frequency           = r.frequency,
                phase               = r.phase,
                sampleCount         = r.sampleCount,
            });
        }

        var stops = new List<BusStopData>();
        foreach (var s in snap.stops)
        {
            stops.Add(new BusStopData
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

        var intersections = new List<IntersectionDefinition>();
        foreach (var i in snap.intersections)
        {
            intersections.Add(new IntersectionDefinition
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
                // [ADD] Tuned lights (item C) -- restores empty (default
                // single-light behavior) for any backup taken before this
                // field existed, same as a fresh intersection would.
                headsANear     = RestoreHeads(i.headsANear),
                headsAFar      = RestoreHeads(i.headsAFar),
                headsBNear     = RestoreHeads(i.headsBNear),
                headsBFar      = RestoreHeads(i.headsBFar),
            });
        }

        city.roadDefinitions     = roads;
        city.stopDefinitions     = stops;
        city.manualIntersections = intersections;
    }

    /// <summary>Mirrors CityDataExporter.BackupHeads in reverse. Missing/
    /// null input (a backup from before tuned lights existed) just returns
    /// an empty list -- the intersection falls back to default single-light
    /// behavior exactly like it would if these fields were never touched.</summary>
    private static List<TrafficLightHead> RestoreHeads(List<TrafficLightHeadBackup> heads)
    {
        var result = new List<TrafficLightHead>();
        if (heads == null) return result;
        foreach (var h in heads)
        {
            if (h == null) continue;
            result.Add(new TrafficLightHead
            {
                enabled               = h.enabled,
                symbol                = ParseEnum(h.symbol, TrafficLightSymbol.Circle),
                startPercent          = h.startPercent,
                endPercent            = h.endPercent,
                greenStartSeconds     = h.greenStartSeconds,
                greenDurationSeconds  = h.greenDurationSeconds,
            });
        }
        return result;
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        return Enum.TryParse(value, out T result) ? result : fallback;
    }
}