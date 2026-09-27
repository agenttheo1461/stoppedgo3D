using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  SHIFT COUNTDOWN NOTIFIER
//
//  Watches the player's currently assigned upcoming departure and reminds
//  them at 30/15/10 REAL-WORLD minutes before it (converted via SimClock's
//  exact game-time-is-a-function-of-real-time relationship, not an
//  estimate). Fires both channels every time:
//    - NotificationToast, so it shows up if the game is open and foregrounded.
//    - PlatformNotifications, scheduled well in advance for the real OS to
//      deliver even if the app is backgrounded/closed by then (Android/iOS
//      for real; macOS via the native bridge, unverified -- see
//      PlatformNotifications.cs's header comment).
//  Re-schedules automatically whenever the assigned slot changes (a new
//  pick, a handoff, chain top-up) so stale reminders for a slot the player
//  no longer holds never fire.
// ═══════════════════════════════════════════════════════════════════════════════
public class ShiftCountdownNotifier : MonoBehaviour
{
    public static ShiftCountdownNotifier Instance { get; private set; }

    private static readonly int[] ReminderMinutes = { 30, 15, 10 };

    private TimetableSlot _watchedSlot;
    private bool[] _toastFired = new bool[ReminderMinutes.Length];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        new GameObject("ShiftCountdownNotifier").AddComponent<ShiftCountdownNotifier>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    private void Update()
    {
        if (BusScheduler.Instance == null || PlayerHandoff.Instance == null || SimClock.Instance == null) return;
        if (!PlayerHandoff.Instance.IsOnDuty) { ClearWatch(); return; }

        var slot = BusScheduler.Instance.GetCurrentSlotForBus(PlayerHandoff.Instance.PlayerBusID);
        if (slot != _watchedSlot)
        {
            ClearWatch();
            _watchedSlot = slot;
            if (_watchedSlot != null) ScheduleOsReminders(_watchedSlot);
        }
        if (_watchedSlot == null) return;

        float realMinutesRemaining = (_watchedSlot.scheduledDeparture - SimClock.Instance.AbsoluteGameMinutes) / SimClock.TIME_MULTIPLIER;

        for (int i = 0; i < ReminderMinutes.Length; i++)
        {
            if (_toastFired[i]) continue;
            if (realMinutesRemaining > ReminderMinutes[i]) continue; // not there yet
            if (realMinutesRemaining < ReminderMinutes[i] - 2f) continue; // missed the window (e.g. big frame hitch) -- skip rather than fire stale
            _toastFired[i] = true;
            NotificationToast.Show("Upcoming Shift", $"Route {_watchedSlot.FullRouteLabel} departs in about {ReminderMinutes[i]} minutes.");
        }
    }

    private void ClearWatch()
    {
        if (_watchedSlot != null)
            for (int i = 0; i < ReminderMinutes.Length; i++)
                PlatformNotifications.Cancel(OsId(_watchedSlot, ReminderMinutes[i]));

        _watchedSlot = null;
        for (int i = 0; i < _toastFired.Length; i++) _toastFired[i] = false;
    }

    private void ScheduleOsReminders(TimetableSlot slot)
    {
        for (int i = 0; i < ReminderMinutes.Length; i++)
        {
            var fireAt = SimClock.AbsoluteGameMinutesToRealLocalTime(slot.scheduledDeparture).AddMinutes(-ReminderMinutes[i]);
            PlatformNotifications.ScheduleAt(OsId(slot, ReminderMinutes[i]), "Upcoming Shift",
                $"Route {slot.FullRouteLabel} departs in about {ReminderMinutes[i]} minutes.", fireAt);
        }
    }

    /// <summary>Stable per-slot-per-threshold id so a re-schedule cleanly replaces the old one instead of stacking duplicates.</summary>
    private static string OsId(TimetableSlot slot, int minutes) =>
        $"shift_{slot.routeNumber}_{slot.variantLetter}_{slot.isOutbound}_{slot.scheduledDeparture}_{minutes}";
}
