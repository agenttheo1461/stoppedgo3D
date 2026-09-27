using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  SETTINGS DATA
//
//  Every player-facing setting, backed by PlayerPrefs (these are all simple
//  scalars, unlike ShiftMakerData/StarredBusData's richer lists which use
//  their own JSON files). Read directly as static properties -- no Instance,
//  no scene object needed, since PlayerPrefs itself is the persistence and
//  there's no per-frame state to own.
// ═══════════════════════════════════════════════════════════════════════════════
public static class SettingsData
{
    private const string KeyLightTheme      = "Settings_LightTheme";
    private const string KeyDeveloperMode   = "Settings_DeveloperMode";
    private const string KeyNotifyShift     = "Settings_NotifyShiftCountdown";
    private const string KeyNotifyStarred   = "Settings_NotifyStarredBuses";
    private const string KeyUseOsPush       = "Settings_UseOsPush";
    private const string KeyUiScaleOverride = "Settings_UiScaleOverride";
    private const string KeyUseMetric       = "Settings_UseMetricUnits";
    private const string KeyQualityLevel    = "Settings_QualityLevel";
    private const string KeyShapeIndicators = "Settings_ShapeIndicators";

    public static bool LightTheme
    {
        get => PlayerPrefs.GetInt(KeyLightTheme, 0) != 0;
        set => PlayerPrefs.SetInt(KeyLightTheme, value ? 1 : 0);
    }

    /// <summary>Defaults ON in the Editor (so existing debug-key workflows
    /// keep working with zero setup) and OFF in an actual player-facing
    /// build, where a stray Ctrl+F7 shouldn't do anything.</summary>
    public static bool DeveloperMode
    {
        get => PlayerPrefs.GetInt(KeyDeveloperMode, Application.isEditor ? 1 : 0) != 0;
        set => PlayerPrefs.SetInt(KeyDeveloperMode, value ? 1 : 0);
    }

    public static bool NotifyShiftCountdown
    {
        get => PlayerPrefs.GetInt(KeyNotifyShift, 1) != 0;
        set => PlayerPrefs.SetInt(KeyNotifyShift, value ? 1 : 0);
    }

    public static bool NotifyStarredBuses
    {
        get => PlayerPrefs.GetInt(KeyNotifyStarred, 1) != 0;
        set => PlayerPrefs.SetInt(KeyNotifyStarred, value ? 1 : 0);
    }

    /// <summary>If false, only NotificationToast (in-game, foreground) fires
    /// -- PlatformNotifications.ScheduleAt/Cancel become no-ops. Lets a
    /// player opt out of real OS push without losing in-game reminders.</summary>
    public static bool UseOsPush
    {
        get => PlayerPrefs.GetInt(KeyUseOsPush, 1) != 0;
        set => PlayerPrefs.SetInt(KeyUseOsPush, value ? 1 : 0);
    }

    /// <summary>0 = auto (MDT_UITheme's own Screen.height-based computation);
    /// otherwise an explicit multiplier the player chose.</summary>
    public static float UiScaleOverride
    {
        get => PlayerPrefs.GetFloat(KeyUiScaleOverride, 0f);
        set => PlayerPrefs.SetFloat(KeyUiScaleOverride, value);
    }

    /// <summary>Primary speed-display unit. Default false -- the dashboard's
    /// existing default is mph (US transit convention, see BusDashboardHUD).</summary>
    public static bool UseMetricUnits
    {
        get => PlayerPrefs.GetInt(KeyUseMetric, 0) != 0;
        set => PlayerPrefs.SetInt(KeyUseMetric, value ? 1 : 0);
    }

    /// <summary>-1 = leave Unity's own project-configured default alone.</summary>
    public static int QualityLevel
    {
        get => PlayerPrefs.GetInt(KeyQualityLevel, -1);
        set => PlayerPrefs.SetInt(KeyQualityLevel, value);
    }

    /// <summary>Adds a distinct SHAPE to every semantic status color (green
    /// /amber/red) alongside the color itself, so status is never conveyed
    /// by hue alone -- see MDT_UITheme.DrawStatusIndicator.</summary>
    public static bool ShapeIndicators
    {
        get => PlayerPrefs.GetInt(KeyShapeIndicators, 0) != 0;
        set => PlayerPrefs.SetInt(KeyShapeIndicators, value ? 1 : 0);
    }

    /// <summary>Deletes the diagnostic/history save files (route_history.json,
    /// old day_assign_*.json) -- deliberately NOT shift_plan.json/
    /// starred_buses.json/shift_save.json, which are the player's own
    /// created content/progress, not logs.</summary>
    public static void ClearDiagnosticData()
    {
        string dir = Application.persistentDataPath;
        TryDelete(System.IO.Path.Combine(dir, "route_history.json"));
        try
        {
            foreach (var f in System.IO.Directory.GetFiles(dir, "day_assign_*.json"))
                TryDelete(f);
        }
        catch (System.Exception e) { Debug.LogWarning($"[SettingsData] ClearDiagnosticData: couldn't list {dir}: {e.Message}"); }
    }

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch (System.Exception e) { Debug.LogWarning($"[SettingsData] Could not delete {path}: {e.Message}"); }
    }
}
