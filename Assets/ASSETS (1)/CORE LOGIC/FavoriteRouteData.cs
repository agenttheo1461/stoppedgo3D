using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  FAVORITE ROUTE DATA
//
//  Persists which route numbers the player has favorited -- toggled from
//  ShiftBoardMenu's route cards, MainMenu's route cards, or RouteRosterWindow
//  (Settings ▸ Manage favorite routes). A favorited route always shows in
//  MainMenu's list (even if it wouldn't otherwise make the "soonest 8" cut)
//  and, when clicked there, offers a direction/timing chooser instead of
//  jumping straight into bus selection. FavoriteRouteAvailabilityNotifier
//  also reads this to occasionally nudge about one that's ready to go.
//
//  Keyed by plain route number (BusRouteData.routeNumber, no variant letter)
//  -- favoriting "116" covers all of 116's variants/short turns together.
//  Same file-based persistence pattern as StarredBusData/ShiftMakerData.
// ═══════════════════════════════════════════════════════════════════════════════
public class FavoriteRouteData : MonoBehaviour
{
    public static FavoriteRouteData Instance { get; private set; }

    [Serializable] private class FavFile
    {
        public List<string> routeNumbers = new List<string>();
    }

    public string fileName = "favorite_routes.json";
    private FavFile _data = new FavFile();
    private readonly HashSet<string> _set = new HashSet<string>();
    public IReadOnlyCollection<string> Favorites => _set;

    private string FilePath => Path.Combine(Application.persistentDataPath, fileName);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("FavoriteRouteData").AddComponent<FavoriteRouteData>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Load();
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    public bool IsFavorite(string routeNumber) => !string.IsNullOrEmpty(routeNumber) && _set.Contains(routeNumber);

    public void Toggle(string routeNumber)
    {
        if (string.IsNullOrEmpty(routeNumber)) return;
        if (!_set.Remove(routeNumber)) _set.Add(routeNumber);
        _data.routeNumbers = new List<string>(_set);
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _data = JsonUtility.FromJson<FavFile>(File.ReadAllText(FilePath)) ?? new FavFile();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[FavoriteRouteData] Could not read {FilePath}: {e.Message}. Starting with none favorited.");
            _data = new FavFile();
        }
        _set.Clear();
        foreach (var r in _data.routeNumbers) if (!string.IsNullOrEmpty(r)) _set.Add(r);
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonUtility.ToJson(_data, true)); }
        catch (Exception e) { Debug.LogWarning($"[FavoriteRouteData] Save failed: {e.Message}"); }
    }
}
