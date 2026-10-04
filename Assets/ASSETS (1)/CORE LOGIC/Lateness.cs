using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Dynamic lateness ([G6]) and express-dead-run candidate
/// tracking, standalone. Ticks off a snapshot the facade hands it — never
/// touches SlotPool internals directly.</summary>
public class LatenessTracker
{
    public float dwellCutoffMinutes = 3f;
    public float minLateDwellMultiplier = 0.3f;
    public float expressDeadRunThresholdMinutes = 480f;

    private readonly HashSet<int> _expressDeadRunBuses = new();

    public void Tick(IReadOnlyDictionary<int, TimetableSlot> slotByBus, float gameTimeMinutes,
                      Func<int, NPCBusController> resolveController, Func<string, BusRouteData> getRouteData,
                      Func<TimetableSlot, float> tripMinutesFor = null)
    {
        foreach (var kv in slotByBus)
        {
            int busID = kv.Key;
            var slot = kv.Value;
            if (BusScheduler.IsPlayer(busID)) continue;

            if (slot.state == SlotState.AssignedNPC && slot.actualDeparture < 0f)
            {
                slot.latenessMinutes = Mathf.Max(0f, gameTimeMinutes - slot.scheduledDeparture);
            }
            else if (slot.state == SlotState.InService && slot.actualDeparture >= 0f)
            {
                var ctrl = resolveController(busID);
                if (ctrl == null) continue;

                var routeData = getRouteData(slot.routeNumber);
                // The trip time the TIMETABLE uses for this departure (it is shorter in the overnight window, longer at peak),
                // not the flat all-day figure, or every bus in a scaled window reads early or late all trip long.
                float tripMins = tripMinutesFor != null ? tripMinutesFor(slot) : (routeData?.oneWayTripMinutes ?? 45f);
                float progress = Mathf.Clamp01(ctrl.GetRouteProgressFraction());

                float expectedElapsed = progress * tripMins;
                float actualElapsed = gameTimeMinutes - slot.actualDeparture;
                slot.latenessMinutes = actualElapsed - expectedElapsed;
            }
        }
    }

    public float GetAdjustedDwell(TimetableSlot slot, float baseDwell)
    {
        if (slot == null) return baseDwell;
        float lateness = slot.latenessMinutes;
        if (lateness <= dwellCutoffMinutes) return baseDwell;
        float t = Mathf.Clamp01((lateness - dwellCutoffMinutes) / dwellCutoffMinutes);
        return baseDwell * Mathf.Lerp(1f, minLateDwellMultiplier, t);
    }

    public bool IsExpressDeadRun(int busID) => _expressDeadRunBuses.Contains(busID);
    public void MarkExpressDeadRun(int busID) => _expressDeadRunBuses.Add(busID);
    public void ClearExpressDeadRun(int busID) => _expressDeadRunBuses.Remove(busID);

    public List<int> FindExpressCandidates(IReadOnlyDictionary<int, TimetableSlot> slotByBus)
    {
        var result = new List<int>();
        foreach (var kv in slotByBus)
        {
            if (BusScheduler.IsPlayer(kv.Key) || kv.Key < 0) continue;
            if (_expressDeadRunBuses.Contains(kv.Key)) continue;
            if (kv.Value.latenessMinutes < expressDeadRunThresholdMinutes) continue;
            result.Add(kv.Key);
        }
        return result;
    }
}