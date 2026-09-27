using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[Serializable]
public class ShiftSaveData
{
    public List<ShiftAssignment> assignments = new();
}

/// <summary>
/// v3.0 — Same public API as before (Save/Load), unchanged call sites.
/// The only real change lives in WHAT gets passed in: ShiftAssignment now
/// carries per-block completed/missed status instead of a bare index, so a
/// loaded save is meaningful again (see ShiftAssignment.cs / DailyShiftGenerator.cs
/// for why the old index-only version silently desynced on every reload).
///
/// Also now saves on every block transition (completed/missed) via
/// FleetDispatcher, not just on quit — OnApplicationQuit is unreliable on
/// mobile (the OS can kill the process without ever calling it), which was
/// part of why progress "didn't take" between sessions.
/// </summary>
public static class SaveService
{
    private static string Path => System.IO.Path.Combine(Application.persistentDataPath, "shift_save.json");

    public static void Save(ShiftSaveData data)
    {
        try
        {
            WriteAtomic(Path, JsonUtility.ToJson(new Wrapper(data), true));
            Debug.Log($"[SaveService] Saved {data.assignments.Count} bus shift records.");
        }
        catch (Exception e) { Debug.LogError($"[SaveService] Save failed: {e}"); }
        
    }

    /// <summary>[FIX] File.WriteAllText is not atomic — a process kill
    /// mid-write (very possible on mobile, which is the whole reason SaveNow
    /// is called on every block transition instead of relying solely on
    /// OnApplicationQuit) leaves a truncated/corrupt JSON file that then
    /// fails to parse on next load, silently discarding all progress. Write
    /// to a temp file first, then swap it in — the swap itself is as close
    /// to atomic as the OS gives us, and a failure during it just leaves the
    /// OLD save intact instead of a half-written new one.</summary>
    private static void WriteAtomic(string path, string contents)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static ShiftSaveData Load()
    {
        if (!File.Exists(Path)) return null;
        try
        {
            var wrapper = JsonUtility.FromJson<Wrapper>(File.ReadAllText(Path));
            return wrapper?.data;
        }
        catch (Exception e) { Debug.LogError($"[SaveService] Load failed: {e}"); return null; }
    }

    [Serializable] private class Wrapper { public ShiftSaveData data; public Wrapper(ShiftSaveData d) => data = d; }

    // ── Day-level bus-to-slot pre-assignments ───────────────────────────
    // Separate file per day so reloading mid-session doesn't touch other
    // days, and so a save simply doesn't exist yet for days not reached.
    private static string DayPath(int dayNumber) =>
        System.IO.Path.Combine(Application.persistentDataPath, $"day_assign_{dayNumber}.json");

    public static void SaveDayAssignments(DayAssignmentSave data)
    {
        try
        {
            data.planVersion = DayAssignmentSave.CurrentPlanVersion;
            WriteAtomic(DayPath(data.dayNumber), JsonUtility.ToJson(data, true));
            Debug.Log($"[SaveService] Saved day {data.dayNumber} assignments ({data.entries.Count} slots).");
        }
        catch (Exception e) { Debug.LogError($"[SaveService] SaveDayAssignments failed: {e}"); }
    }

    public static DayAssignmentSave LoadDayAssignments(int dayNumber)
    {
        string path = DayPath(dayNumber);
        if (!File.Exists(path)) return null;
        try
        {
            var loaded = JsonUtility.FromJson<DayAssignmentSave>(File.ReadAllText(path));
            // A plan written by older assignment logic is replayed verbatim by PreAssignBusesForRouteDay, so a
            // fix to that logic would never show up for any day that already had a file. Ignore (the day is
            // rebuilt and re-saved) anything not written by the current version.
            if (loaded != null && loaded.planVersion != DayAssignmentSave.CurrentPlanVersion)
            {
                Debug.Log($"[SaveService] Day {dayNumber} assignments are plan v{loaded.planVersion} (current v{DayAssignmentSave.CurrentPlanVersion}) — rebuilding.");
                return null;
            }
            return loaded;
        }
        catch (Exception e)
        {
            // [FIX] A corrupt/truncated file used to throw here and return
            // null silently — every day's pre-assignment would then look
            // "missing" and PreAssignBusesForRouteDay would re-roll from
            // scratch with no warning why buses reshuffled. Delete the bad
            // file so it doesn't keep failing to parse forever, and log loud.
            Debug.LogError($"[SaveService] LoadDayAssignments: {path} is corrupt, discarding it ({e.Message}).");
            try { File.Delete(path); } catch { /* best effort */ }
            return null;
        }
    }
}

[Serializable]
public class DayAssignmentEntry
{
    public string routeNumber;
    public string variantLetter;
    public bool isOutbound;
    public float scheduledDeparture;
    public int fleetNumber;
}

[Serializable]
public class DayAssignmentSave
{
    /// <summary>Bump whenever BusScheduler's pre-assignment logic changes in a way that should not be
    /// masked by plans saved under the old logic. v2: night-only buses (nightFleetSeries) now enter the pool.</summary>
    public const int CurrentPlanVersion = 2;

    public int planVersion; // deliberately 0 by default: JsonUtility leaves it 0 for files written before versioning
    public int dayNumber;
    public List<DayAssignmentEntry> entries = new();
}