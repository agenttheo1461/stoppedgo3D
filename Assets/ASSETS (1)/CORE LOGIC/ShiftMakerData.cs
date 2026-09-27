using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  SHIFT MAKER DATA
//
//  Persists the player's own hand-built multi-day plan -- a list of (route,
//  variant, direction, which day, a flexible time-of-day WINDOW) entries --
//  to one JSON file so it survives between playthroughs. Same file-based
//  pattern as RouteHistoryLogger (auto-creates itself, loads on Awake, saves
//  whenever the plan actually changes).
//
//  This is deliberately just the planned WISH LIST, not real TimetableSlots --
//  slots are regenerated fresh each day, so an entry is resolved against
//  whatever's actually running at pick time (see
//  MainMenu.AppendResolvedCustomEntries): nearest Unassigned departure, on
//  that route/direction, on the target day (dayOffset relative to whichever
//  day the plan is being consulted on -- 0 = that day, 1 = the day after,
//  etc. -- so a plan replays sensibly across a different playthrough's own
//  day numbering instead of being pinned to one absolute day), whose
//  time-of-day falls inside [windowStartMinutes, windowEndMinutes].
// ═══════════════════════════════════════════════════════════════════════════════
public class ShiftMakerData : MonoBehaviour
{
    public static ShiftMakerData Instance { get; private set; }

    [Serializable]
    public class CustomShiftEntry
    {
        public string routeNumber;
        public string variantLetter = "";
        public bool   outbound;
        public int    dayOffset;          // 0 = the day this is consulted on, 1 = the day after, etc.
        public float  windowStartMinutes; // time-of-day window, 0-1439
        public float  windowEndMinutes;

        public string TimeLabel
        {
            get
            {
                string a = BusScheduler.MinutesToTimeString(((windowStartMinutes % 1440f) + 1440f) % 1440f);
                string b = BusScheduler.MinutesToTimeString(((windowEndMinutes % 1440f) + 1440f) % 1440f);
                return $"{a}–{b}";
            }
        }
        public string DayLabel => dayOffset <= 0 ? "Today" : dayOffset == 1 ? "+1 day" : $"+{dayOffset} days";
        public string RouteLabel => string.IsNullOrEmpty(variantLetter) ? $"Route {routeNumber}" : $"Route {routeNumber}{variantLetter}";
        public string DirLabel => outbound ? "A → Z" : "Z → A";
    }

    [Serializable] private class PlanFile
    {
        public List<CustomShiftEntry> entries = new List<CustomShiftEntry>();
    }

    public string fileName = "shift_plan.json";
    private PlanFile _data = new PlanFile();
    public IReadOnlyList<CustomShiftEntry> Entries => _data.entries;

    private string FilePath => Path.Combine(Application.persistentDataPath, fileName);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("ShiftMakerData").AddComponent<ShiftMakerData>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Load();
    }

    public void AddEntry(string routeNumber, string variantLetter, bool outbound, int dayOffset, float windowStartMinutes, float windowEndMinutes)
    {
        _data.entries.Add(new CustomShiftEntry
        {
            routeNumber = routeNumber,
            variantLetter = variantLetter ?? "",
            outbound = outbound,
            dayOffset = Mathf.Max(0, dayOffset),
            windowStartMinutes = windowStartMinutes,
            windowEndMinutes = windowEndMinutes,
        });
        Save();
    }

    public void RemoveEntry(int index)
    {
        if (index < 0 || index >= _data.entries.Count) return;
        _data.entries.RemoveAt(index);
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _data = JsonUtility.FromJson<PlanFile>(File.ReadAllText(FilePath)) ?? new PlanFile();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ShiftMakerData] Could not read {FilePath}: {e.Message}. Starting with an empty plan.");
            _data = new PlanFile();
        }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonUtility.ToJson(_data)); }
        catch (Exception e) { Debug.LogWarning($"[ShiftMakerData] Save failed: {e.Message}"); }
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }
}
