using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  SETTINGS DATA
//
//  Every player-facing setting, backed by PlayerPrefs (these are all simple
//  scalars, unlike ShiftMakerData/StarredBusData's richer lists which use
//  their own JSON files).
//
//  [FIX] The getters used to call PlayerPrefs.GetInt/GetFloat directly on
//  every read. That's fine from Awake/Start/OnGUI/Update, but MDT_UITheme's
//  color properties read SettingsData.LightTheme/ShapeIndicators on every
//  access, and at least one MonoBehaviour (ShiftEventOverlay) reads an
//  MDT_UITheme color in an INSTANCE FIELD INITIALIZER -- which Unity
//  evaluates during the object's construction/deserialization, before
//  Awake, at a point where calling PlayerPrefs throws
//  ("GetInt is not allowed to be called from a MonoBehaviour constructor").
//  A lazy C# static constructor wouldn't reliably fix this either -- it's
//  still triggered on first access, which could still BE that same unsafe
//  moment. So every value is loaded ONCE, up front, via
//  RuntimeInitializeLoadType.SubsystemRegistration -- the earliest hook
//  Unity offers, guaranteed to run before any scene object is constructed --
//  and the properties below just return the cached value. Setters still
//  write through to PlayerPrefs (only ever called from SettingsWindow's
//  OnGUI, always safe) and update the cache in the same call.
// ═══════════════════════════════════════════════════════════════════════════════
public static class SettingsData
{
    private const string KeyLightTheme      = "Settings_LightTheme";
    private const string KeyDeveloperMode   = "Settings_DeveloperMode";
    private const string KeyNotifyShift     = "Settings_NotifyShiftCountdown";
    private const string KeyNotifyStarred   = "Settings_NotifyStarredBuses";
    private const string KeyNotifyFavRoutes = "Settings_NotifyFavoriteRoutes";
    private const string KeyUseOsPush       = "Settings_UseOsPush";
    private const string KeyUiScaleOverride = "Settings_UiScaleOverride";
    private const string KeyUseMetric       = "Settings_UseMetricUnits";
    private const string KeyQualityLevel    = "Settings_QualityLevel";
    private const string KeyShapeIndicators = "Settings_ShapeIndicators";

    private static bool _loaded;
    // Cached fields start at each setting's correct logical default (not
    // just the C# type default -- notably NotifyShift/NotifyStarred/UseOsPush
    // default true and QualityLevel defaults -1) so that even in the
    // pathological case where something reads a setting before
    // SubsystemRegistration has fired, it gets the right answer WITHOUT
    // touching PlayerPrefs -- there's no unsafe fallback path at all, since
    // LoadAll() below only ever overwrites these with the real persisted
    // value once it's safe to ask.
    private static bool _cLightTheme = false;
    private static bool _cDeveloperMode = Application.isEditor;
    private static bool _cNotifyShift = true;
    private static bool _cNotifyStarred = true;
    private static bool _cNotifyFavRoutes = true;
    private static bool _cUseOsPush = true;
    private static bool _cUseMetric = false;
    private static bool _cShapeIndicators = false;
    private static float _cUiScaleOverride = 0f;
    private static int _cQualityLevel = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void LoadAll()
    {
        if (_loaded) return;
        _loaded = true;
        _cLightTheme      = PlayerPrefs.GetInt(KeyLightTheme, 0) != 0;
        _cDeveloperMode   = PlayerPrefs.GetInt(KeyDeveloperMode, Application.isEditor ? 1 : 0) != 0;
        _cNotifyShift     = PlayerPrefs.GetInt(KeyNotifyShift, 1) != 0;
        _cNotifyStarred   = PlayerPrefs.GetInt(KeyNotifyStarred, 1) != 0;
        _cNotifyFavRoutes = PlayerPrefs.GetInt(KeyNotifyFavRoutes, 1) != 0;
        _cUseOsPush       = PlayerPrefs.GetInt(KeyUseOsPush, 1) != 0;
        _cUseMetric       = PlayerPrefs.GetInt(KeyUseMetric, 0) != 0;
        _cShapeIndicators = PlayerPrefs.GetInt(KeyShapeIndicators, 0) != 0;
        _cUiScaleOverride = PlayerPrefs.GetFloat(KeyUiScaleOverride, 0f);
        _cQualityLevel    = PlayerPrefs.GetInt(KeyQualityLevel, -1);
    }

    public static bool LightTheme
    {
        get { return _cLightTheme; }
        set { _cLightTheme = value; PlayerPrefs.SetInt(KeyLightTheme, value ? 1 : 0); }
    }

    /// <summary>Defaults ON in the Editor (so existing debug-key workflows
    /// keep working with zero setup) and OFF in an actual player-facing
    /// build, where a stray Ctrl+F7 shouldn't do anything.</summary>
    public static bool DeveloperMode
    {
        get { return _cDeveloperMode; }
        set { _cDeveloperMode = value; PlayerPrefs.SetInt(KeyDeveloperMode, value ? 1 : 0); }
    }

    public static bool NotifyShiftCountdown
    {
        get { return _cNotifyShift; }
        set { _cNotifyShift = value; PlayerPrefs.SetInt(KeyNotifyShift, value ? 1 : 0); }
    }

    public static bool NotifyStarredBuses
    {
        get { return _cNotifyStarred; }
        set { _cNotifyStarred = value; PlayerPrefs.SetInt(KeyNotifyStarred, value ? 1 : 0); }
    }

    public static bool NotifyFavoriteRoutes
    {
        get { return _cNotifyFavRoutes; }
        set { _cNotifyFavRoutes = value; PlayerPrefs.SetInt(KeyNotifyFavRoutes, value ? 1 : 0); }
    }

    /// <summary>If false, only NotificationToast (in-game, foreground) fires
    /// -- PlatformNotifications.ScheduleAt/Cancel become no-ops. Lets a
    /// player opt out of real OS push without losing in-game reminders.</summary>
    public static bool UseOsPush
    {
        get { return _cUseOsPush; }
        set { _cUseOsPush = value; PlayerPrefs.SetInt(KeyUseOsPush, value ? 1 : 0); }
    }

    /// <summary>0 = auto (MDT_UITheme's own Screen.height-based computation);
    /// otherwise an explicit multiplier the player chose.</summary>
    public static float UiScaleOverride
    {
        get { return _cUiScaleOverride; }
        set { _cUiScaleOverride = value; PlayerPrefs.SetFloat(KeyUiScaleOverride, value); }
    }

    /// <summary>Primary speed-display unit. Default false -- the dashboard's
    /// existing default is mph (US transit convention, see BusDashboardHUD).</summary>
    public static bool UseMetricUnits
    {
        get { return _cUseMetric; }
        set { _cUseMetric = value; PlayerPrefs.SetInt(KeyUseMetric, value ? 1 : 0); }
    }

    /// <summary>-1 = leave Unity's own project-configured default alone.</summary>
    public static int QualityLevel
    {
        get { return _cQualityLevel; }
        set { _cQualityLevel = value; PlayerPrefs.SetInt(KeyQualityLevel, value); }
    }

    /// <summary>Adds a distinct SHAPE to every semantic status color (green
    /// /amber/red) alongside the color itself, so status is never conveyed
    /// by hue alone -- see MDT_UITheme.DrawStatusIndicator.</summary>
    public static bool ShapeIndicators
    {
        get { return _cShapeIndicators; }
        set { _cShapeIndicators = value; PlayerPrefs.SetInt(KeyShapeIndicators, value ? 1 : 0); }
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
