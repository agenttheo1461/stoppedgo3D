using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  MANAGER PLAN — the multi-day schedule you build by hand, saved on this device.
//
//  You only place the FIRST trip of each bus ("bus 1903 starts Route 12 at 06:30,
//  A to Z"). The scheduler fills in the rest of that bus's round trips by itself,
//  alternating A to Z and Z to A, so a 60-trip route takes about 30 entries, not 60+.
//
//  Entries are saved in a small versioned JSON file. They are applied to the real
//  timetable when the manager opens and every time a new day comes into range, so a
//  plan made for "tomorrow" is waiting when tomorrow's timetable is built.
// ═══════════════════════════════════════════════════════════════════════════════
[Serializable]
public class PlanEntry
{
    public int    day;            // absolute game day number
    public string route;
    public string variant = "";
    public bool   outbound;       // true = A to Z
    public int    startMinute;    // minute of that day, 0-1439
    public int    fleetNumber;    // the bus (stable across sessions)
}

[Serializable]
public class PlanFile
{
    public int version = ManagerPlan.CurrentVersion;
    public List<PlanEntry> entries = new List<PlanEntry>();
}

public static class ManagerPlan
{
    public const int CurrentVersion = 1;

    private static PlanFile _file;
    private static bool _applyHooked;

    private static string FilePath => Path.Combine(Application.persistentDataPath, "ManagerPlan.json");

    public static List<PlanEntry> Entries { get { EnsureLoaded(); return _file.entries; } }

    // ── Load / save ──────────────────────────────────────────────────────────
    private static void EnsureLoaded()
    {
        if (_file != null) return;
        _file = new PlanFile();
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonUtility.FromJson<PlanFile>(File.ReadAllText(FilePath));
                // A different version means the layout changed: start fresh instead of misreading it.
                if (loaded != null && loaded.version == CurrentVersion && loaded.entries != null) _file = loaded;
                else if (loaded != null) Debug.Log($"[ManagerPlan] Saved plan is version {loaded.version}, current is {CurrentVersion} — starting a fresh plan.");
            }
        }
        catch (Exception e) { Debug.LogWarning("[ManagerPlan] Couldn't read the saved plan: " + e.Message); }

        PruneOld();
        HookDayRoll();
    }

    public static void Save()
    {
        EnsureLoaded();
        try { File.WriteAllText(FilePath, JsonUtility.ToJson(_file, true)); }
        catch (Exception e) { Debug.LogWarning("[ManagerPlan] Couldn't save the plan: " + e.Message); }
    }

    private static void HookDayRoll()
    {
        if (_applyHooked || SimClock.Instance == null) return;
        SimClock.Instance.OnGameDayRolled += OnDayRolled;
        _applyHooked = true;
    }

    private static void OnDayRolled(int newDay)
    {
        PruneOld();
        // The scheduler builds the new far day in its own handler; give it a moment, then apply.
        if (ManagerMode.Instance != null) ManagerMode.Instance.StartCoroutine(ApplyLater());
        else ApplyAll(out _, out _);
    }

    private static System.Collections.IEnumerator ApplyLater()
    {
        yield return new WaitForSeconds(2f);
        ApplyAll(out _, out _);
    }

    private static void PruneOld()
    {
        if (_file == null || SimClock.Instance == null) return;
        int today = SimClock.Instance.GameDayNumber;
        _file.entries.RemoveAll(e => e.day < today);
    }

    // ── Editing ──────────────────────────────────────────────────────────────
    private static bool SameTrip(PlanEntry a, PlanEntry b) =>
        a.day == b.day && a.route == b.route && a.variant == b.variant && a.outbound == b.outbound && a.startMinute == b.startMinute;

    public static void Add(PlanEntry e)
    {
        EnsureLoaded();
        _file.entries.RemoveAll(x => SameTrip(x, e));
        _file.entries.Add(e);
        Save();
    }

    public static void RemoveTrip(int day, string route, string variant, bool outbound, int startMinute)
    {
        EnsureLoaded();
        var probe = new PlanEntry { day = day, route = route, variant = variant ?? "", outbound = outbound, startMinute = startMinute };
        if (_file.entries.RemoveAll(x => SameTrip(x, probe)) > 0) Save();
    }

    public static void ClearDay(int day)
    {
        EnsureLoaded();
        if (_file.entries.RemoveAll(e => e.day == day) > 0) Save();
    }

    public static List<PlanEntry> ForDay(int day) => Entries.Where(e => e.day == day).OrderBy(e => e.startMinute).ToList();

    public static PlanEntry FromSlot(TimetableSlot s, int fleetNumber) => new PlanEntry
    {
        day = s.dayNumber,
        route = s.routeNumber,
        variant = s.variantLetter ?? "",
        outbound = s.isOutbound,
        startMinute = Mathf.RoundToInt(s.scheduledDeparture % 1440f),
        fleetNumber = fleetNumber,
    };

    // ── Applying the plan to the real timetable ──────────────────────────────
    /// <summary>Put every saved entry onto the timetable where its trip exists and is still empty.
    /// `problems` lists entries that could not be applied, in plain words.</summary>
    /// <summary>Entries from the last ApplyAll that could not be placed, in plain words.</summary>
    public static List<string> LastProblems { get; private set; } = new List<string>();

    public static void ApplyAll(out int applied, out List<string> problems)
    {
        applied = 0; problems = new List<string>();
        LastProblems = problems;
        var sched = BusScheduler.Instance;
        if (sched == null || BusManager.Instance == null) return;

        foreach (var e in Entries.ToList())
        {
            var slot = FindTripFor(sched, e);
            if (slot == null) continue; // the timetable for that day isn't built yet — try again later

            int busID = BusManager.Instance.GetBusIDByFleetNumber(e.fleetNumber);
            if (busID < 0) { problems.Add($"Bus {e.fleetNumber} wasn't found."); continue; }

            if (slot.assignedBusID == busID) { ManagerLocks.LockSlot(slot); ManagerLocks.LockBus(busID); continue; } // already in place

            // A trip the automatic scheduler already gave to another bus, that hasn't started and isn't about to:
            // the manager's plan wins, so take it off that bus first.
            float nowAbs = SimClock.Instance != null ? SimClock.Instance.AbsoluteGameMinutes : 0f;
            if (slot.state == SlotState.AssignedNPC && !ManagerLocks.IsSlotLocked(slot) && slot.scheduledDeparture > nowAbs + 10f)
                sched.ManagerFreeTrip(slot, out _);

            if (slot.state != SlotState.Unassigned)
            { problems.Add($"Route {e.route} at {ManagerWords.Clock(e.startMinute)} already has another bus."); continue; }

            if (sched.ManagerAssignTrip(slot, busID, out string why)) applied++;
            else problems.Add($"Route {e.route} at {ManagerWords.Clock(e.startMinute)} with bus {e.fleetNumber}: {why}");
        }
    }

    private static TimetableSlot FindTripFor(BusScheduler sched, PlanEntry e)
    {
        TimetableSlot best = null; float bestDelta = float.MaxValue;
        var slots = sched.AllSlots;
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (s.routeNumber != e.route || s.dayNumber != e.day || s.isOutbound != e.outbound) continue;
            if ((s.variantLetter ?? "") != (e.variant ?? "")) continue;
            float delta = Mathf.Abs((s.scheduledDeparture % 1440f) - e.startMinute);
            if (delta < bestDelta) { bestDelta = delta; best = s; }
        }
        return bestDelta <= 3f ? best : null;
    }

    // ── Auto schedule ────────────────────────────────────────────────────────
    /// <summary>Fill the empty trips of one route and day with parked buses that are allowed to run it.
    /// Each bus gets a first trip; the scheduler chains its later round trips.</summary>
    public static string AutoSchedule(BusRouteData route, int day, out int placed)
    {
        placed = 0;
        var sched = BusScheduler.Instance;
        if (route == null || sched == null || BusManager.Instance == null || SimClock.Instance == null)
            return "The game isn't ready yet.";

        float now = SimClock.Instance.AbsoluteGameMinutes;
        var used = new HashSet<int>();
        string stopReason = null;

        for (int guard = 0; guard < 500; guard++)
        {
            // The earliest trip that is still empty and hasn't left.
            TimetableSlot next = null;
            foreach (var s in sched.ManagerTripsFor(route.routeNumber, day))
            {
                if (s.state != SlotState.Unassigned || s.scheduledDeparture < now + 1f) continue;
                next = s; break;
            }
            if (next == null) break;

            int pick = PickParkedBusFor(route, next, used, out string whyNot);
            if (pick < 0) { stopReason = whyNot; break; }

            if (!sched.ManagerAssignTrip(next, pick, out string why)) { used.Add(pick); stopReason = why; if (used.Count > 200) break; continue; }
            used.Add(pick);
            placed++;
            int fleet = BusManager.Instance.GetRecord(pick)?.controller?.fleetNumber ?? -1;
            if (fleet >= 0) Add(FromSlot(next, fleet));
        }

        if (placed == 0 && stopReason == null) return "Every trip already has a bus.";
        string text = $"Placed {placed} bus{(placed == 1 ? "" : "es")}.";
        if (stopReason != null) text += " Stopped: " + stopReason;
        return text;
    }

    /// <summary>A parked bus that may run this trip, nearest to the start of the route first.</summary>
    public static int PickParkedBusFor(BusRouteData route, TimetableSlot trip, HashSet<int> skip, out string whyNot)
    {
        whyNot = "No parked bus is allowed to run this route.";
        var startStop = CityManager.Instance != null && !string.IsNullOrEmpty(route.terminalACode)
            ? CityManager.Instance.GetStop(route.terminalACode) : null;
        Vector3 start = startStop != null ? startStop.GetWorldPosition() : Vector3.zero;

        int best = -1; float bestDist = float.MaxValue; int considered = 0;
        var busyIndex = BusScheduler.Instance.ManagerBusyIndex();
        float window = BusScheduler.Instance.ManagerChainWindow(trip);
        foreach (var rec in BusManager.Instance.GetAllRecords())
        {
            if (rec.controller == null || (skip != null && skip.Contains(rec.busID))) continue;
            if (!IsParked(rec)) continue;
            if (BusScheduler.Instance.ManagerBusOnTripNow(rec.busID)) continue;
            if (!BusScheduler.ManagerBusFreeBetween(busyIndex, rec.busID, trip.scheduledDeparture - 1f, trip.scheduledDeparture + window, out _)) continue;
            if (BusScheduler.FreeAgentBusIDs.Contains(rec.busID)) continue;
            considered++;

            if (!RouteRules.Check(rec.controller.fleetNumber, route, trip.variantLetter, trip.scheduledDeparture % 1440f, out string why)) { whyNot = why; continue; }
            if (!BusScheduler.Instance.ManagerCanAssign(rec.busID, route, trip.variantLetter, trip.scheduledDeparture % 1440f, out string why2)) { whyNot = why2; continue; }

            float d = start == Vector3.zero ? 0f : Vector3.Distance(rec.controller.transform.position, start);
            if (d < bestDist) { bestDist = d; best = rec.busID; }
        }
        if (considered == 0) whyNot = "There are no parked buses left.";
        return best;
    }

    public static bool IsParked(BusRecord rec)
    {
        if (rec?.controller == null) return false;
        var st = rec.controller.State;
        return st == NPCBusController.BusState.Idle || st == NPCBusController.BusState.WaitingAtDepot;
    }
}
