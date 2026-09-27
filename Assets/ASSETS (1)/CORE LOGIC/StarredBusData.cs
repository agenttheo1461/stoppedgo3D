using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  STARRED BUS DATA
//
//  Persists which fleet numbers the player has starred as ones they'd like to
//  potentially drive -- browsed/edited in BusRosterWindow, consulted by
//  StarredBusAvailabilityNotifier. Same file-based persistence pattern as
//  ShiftMakerData/RouteHistoryLogger.
// ═══════════════════════════════════════════════════════════════════════════════
public class StarredBusData : MonoBehaviour
{
    public static StarredBusData Instance { get; private set; }

    [Serializable] private class StarFile
    {
        public List<int> fleetNumbers = new List<int>();
    }

    public string fileName = "starred_buses.json";
    private StarFile _data = new StarFile();
    private readonly HashSet<int> _set = new HashSet<int>();
    public IReadOnlyCollection<int> Starred => _set;

    private string FilePath => Path.Combine(Application.persistentDataPath, fileName);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("StarredBusData").AddComponent<StarredBusData>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Load();
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    public bool IsStarred(int fleetNumber) => _set.Contains(fleetNumber);

    public void Toggle(int fleetNumber)
    {
        if (!_set.Remove(fleetNumber)) _set.Add(fleetNumber);
        _data.fleetNumbers = new List<int>(_set);
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _data = JsonUtility.FromJson<StarFile>(File.ReadAllText(FilePath)) ?? new StarFile();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[StarredBusData] Could not read {FilePath}: {e.Message}. Starting with none starred.");
            _data = new StarFile();
        }
        _set.Clear();
        foreach (var f in _data.fleetNumbers) _set.Add(f);
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonUtility.ToJson(_data, true)); }
        catch (Exception e) { Debug.LogWarning($"[StarredBusData] Save failed: {e.Message}"); }
    }
}
