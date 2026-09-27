using System;
using UnityEngine;
#if UNITY_ANDROID
using Unity.Notifications.Android;
#elif UNITY_IOS
using Unity.Notifications.iOS;
#elif UNITY_STANDALONE_OSX
using System.Runtime.InteropServices;
#endif

// ═══════════════════════════════════════════════════════════════════════════════
//  PLATFORM NOTIFICATIONS
//
//  Real OS-level local notifications -- fire even with the app backgrounded
//  or closed, unlike NotificationToast (in-game only, foreground only).
//  Android/iOS go through Unity's official com.unity.mobile.notifications
//  package (added to Packages/manifest.json). macOS goes through a small
//  native plugin (Assets/Plugins/macOS/MacNotifications.mm) bridging
//  UNUserNotificationCenter -- untested from here (no Xcode/macOS build
//  access in this environment), so if it doesn't fire on an actual macOS
//  build, check that: (1) the app requested notification permission at
//  least once (RequestAuthorization below does this on first call),
//  (2) System Settings > Notifications has this app allowed,
//  (3) the build is code-signed (unsigned local builds can silently have
//  notification permission denied by macOS).
//
//  No-op everywhere else (PC/Windows/Linux/WebGL/Editor-on-those-targets) --
//  NotificationToast is the only channel there, which is fine since the
//  player only sees those anyway while the game is actually running.
// ═══════════════════════════════════════════════════════════════════════════════
public static class PlatformNotifications
{
    private const string AndroidChannelId = "shift_reminders";
    private static bool _initialized;

#if UNITY_STANDALONE_OSX
    [DllImport("__Internal")] private static extern void _MacNotifications_RequestAuthorization();
    [DllImport("__Internal")] private static extern void _MacNotifications_Schedule(string identifier, string title, string body, double secondsFromNow);
    [DllImport("__Internal")] private static extern void _MacNotifications_Cancel(string identifier);
#endif

    /// <summary>Call once at startup (or lazily on first schedule) -- sets up
    /// the Android notification channel / requests iOS or macOS permission.
    /// Safe to call more than once.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

#if UNITY_ANDROID
        var channel = new AndroidNotificationChannel
        {
            Id = AndroidChannelId,
            Name = "Shift Reminders",
            Importance = Importance.Default,
            Description = "Upcoming shift and bus availability reminders.",
        };
        AndroidNotificationCenter.RegisterNotificationChannel(channel);
#elif UNITY_IOS
        var authOption = AuthorizationOption.Alert | AuthorizationOption.Badge | AuthorizationOption.Sound;
        using (var req = new AuthorizationRequest(authOption, true)) { /* fire-and-forget; iOS shows its own permission prompt */ }
#elif UNITY_STANDALONE_OSX
        try { _MacNotifications_RequestAuthorization(); }
        catch (Exception e) { Debug.LogWarning($"[PlatformNotifications] macOS RequestAuthorization failed: {e.Message}"); }
#endif
    }

    /// <summary>Schedules a real OS notification to fire at the given future
    /// local time. identifier lets a later call cancel/replace this exact
    /// one (e.g. re-scheduling because the player's assigned slot changed).</summary>
    public static void ScheduleAt(string identifier, string title, string body, DateTime fireAtLocal)
    {
        if (!SettingsData.UseOsPush) return; // player opted out of real OS push -- NotificationToast still covers foreground
        Initialize();
        double secondsFromNow = (fireAtLocal - DateTime.Now).TotalSeconds;
        if (secondsFromNow <= 0) return;

#if UNITY_ANDROID
        var notification = new AndroidNotification
        {
            Title = title,
            Text = body,
            FireTime = fireAtLocal,
        };
        AndroidNotificationCenter.SendNotificationWithExplicitID(notification, AndroidChannelId, StableIntId(identifier));
#elif UNITY_IOS
        var timeTrigger = new iOSNotificationTimeIntervalTrigger { TimeInterval = TimeSpan.FromSeconds(secondsFromNow), Repeats = false };
        var notification = new iOSNotification
        {
            Identifier = identifier,
            Title = title,
            Body = body,
            ShowInForeground = true,
            Trigger = timeTrigger,
        };
        iOSNotificationCenter.ScheduleNotification(notification);
#elif UNITY_STANDALONE_OSX
        try { _MacNotifications_Schedule(identifier, title, body, secondsFromNow); }
        catch (Exception e) { Debug.LogWarning($"[PlatformNotifications] macOS Schedule failed: {e.Message}"); }
#endif
    }

    public static void Cancel(string identifier)
    {
#if UNITY_ANDROID
        AndroidNotificationCenter.CancelNotification(StableIntId(identifier));
#elif UNITY_IOS
        iOSNotificationCenter.RemoveScheduledNotification(identifier);
#elif UNITY_STANDALONE_OSX
        try { _MacNotifications_Cancel(identifier); }
        catch (Exception e) { Debug.LogWarning($"[PlatformNotifications] macOS Cancel failed: {e.Message}"); }
#endif
    }

#if UNITY_ANDROID
    /// <summary>Android wants a stable int id per notification (for
    /// cancel/replace) -- derive one deterministically from the string
    /// identifier instead of tracking a separate counter.</summary>
    private static int StableIntId(string identifier) => identifier.GetHashCode() & 0x7FFFFFFF;
#endif
}
