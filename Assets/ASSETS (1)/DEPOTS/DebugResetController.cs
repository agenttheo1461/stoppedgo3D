using UnityEngine;

/// <summary>
/// Drop this on any persistent GameObject (e.g. next to BusScheduler /
/// FleetDispatcher). Pressing Ctrl+F7 re-derives every NPC bus's correct
/// position from the timetable (scheduledDeparture vs. AbsoluteGameMinutes
/// right now) and snaps it there — the same math used on load to resolve
/// mid-trip buses, just re-run on demand. Useful after buses drift, fling,
/// clip through geometry, or after long alt-tab/offline periods where you
/// want an instant re-sync without waiting for a day rollover.
///
/// Does not touch slot assignments, lap counts, or the player's own bus —
/// it only repositions.
/// </summary>
public class DebugResetController : MonoBehaviour
{
    private void Update()
    {
        if (MainMenu.BlocksInput || !SettingsData.DeveloperMode) return; // [FIX Bug 43] + developer-mode setting
        if (KeyBindings.DebugModifierHeld && Input.GetKeyDown(KeyBindings.Current.debugResetBuses))
            ResetAllBuses();
    }

    private void ResetAllBuses()
    {
        if (BusScheduler.Instance == null)
        {
            Debug.LogWarning("[DebugResetController] Ctrl+F7 pressed but BusScheduler.Instance is null.");
            return;
        }
        BusScheduler.Instance.ResnapAllBusesToSchedule();
        BusSelectMenu.Instance?.TriggerNPCResync();
    }
}