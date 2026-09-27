using UnityEngine;

/// <summary>
/// v1.0 — Driver identity + lifetime stats. Deliberately NOT duplicating
/// anything PointsManager already owns (points/xp/level all stay there,
/// single source of truth) -- this only holds what nothing else tracks:
/// the player's chosen name, and lifetime accumulators (shifts completed,
/// laps completed, on-time count) that get incremented once per real
/// shift-end, from ShiftRunner.HandlePlayerShiftEnded.
///
/// Same persistence shape as PointsManager on purpose: PlayerPrefs, saved
/// on every change (not just OnApplicationQuit, which is unreliable on
/// mobile -- same lesson SaveService.cs already learned the hard way for
/// the shift-calendar save).
/// </summary>
public class DriverProfile : MonoBehaviour
{
    public static DriverProfile Instance { get; private set; }

    private const string PrefsNameKey            = "DriverProfile.Name";
    private const string PrefsShiftsKey          = "DriverProfile.TotalShiftsCompleted";
    private const string PrefsLapsKey            = "DriverProfile.TotalLapsCompleted";
    private const string PrefsOnTimeKey          = "DriverProfile.OnTimeShiftCount";
    private const string PrefsFirstPlayedKey     = "DriverProfile.FirstPlayedDate";

    [Header("Identity")]
    public string driverName = "";

    [Header("Lifetime Stats")]
    public int totalShiftsCompleted;
    public int totalLapsCompleted;
    public int onTimeShiftCount;
    public string firstPlayedDate = "";

    /// <summary>True once a name has actually been set — the Main Menu's
    /// signal for "show the name-entry screen" vs "show the normal menu
    /// with name/level already filled in."</summary>
    public bool HasProfile => !string.IsNullOrEmpty(driverName);

    /// <summary>Exact math every time (numerator/denominator), never a
    /// stored running average that could drift from repeated rounding.</summary>
    public float OnTimePercentage =>
        totalShiftsCompleted > 0 ? (onTimeShiftCount / (float)totalShiftsCompleted) * 100f : 0f;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        LoadProfile();
    }

    private void OnApplicationPause(bool pause) { if (pause) SaveProfile(); }
    private void OnApplicationQuit() => SaveProfile();

    /// <summary>Main Menu's first-launch flow calls this once, after the
    /// player types their name. Stamps firstPlayedDate the same moment,
    /// since this is by definition the first time a profile has existed.</summary>
    public void CreateProfile(string name)
    {
        driverName = name;
        firstPlayedDate = System.DateTime.UtcNow.ToString("yyyy-MM-dd");
        SaveProfile();
    }

    /// <summary>Called once per real shift-end from ShiftRunner.
    /// HandlePlayerShiftEnded -- NOT per-leg (that's PointsManager's job
    /// via EndLegAndCalculate). laps/onTime are whatever ShiftRunner's own
    /// session counters already tracked for that shift.</summary>
    public void RecordShiftCompleted(int lapsCompletedThisShift, bool onTime)
    {
        totalShiftsCompleted++;
        totalLapsCompleted += lapsCompletedThisShift;
        if (onTime) onTimeShiftCount++;
        SaveProfile();
    }

    public void SaveProfile()
    {
        PlayerPrefs.SetString(PrefsNameKey, driverName);
        PlayerPrefs.SetInt(PrefsShiftsKey, totalShiftsCompleted);
        PlayerPrefs.SetInt(PrefsLapsKey, totalLapsCompleted);
        PlayerPrefs.SetInt(PrefsOnTimeKey, onTimeShiftCount);
        PlayerPrefs.SetString(PrefsFirstPlayedKey, firstPlayedDate);
        PlayerPrefs.Save();
    }

    public void LoadProfile()
    {
        driverName            = PlayerPrefs.GetString(PrefsNameKey, "");
        totalShiftsCompleted  = PlayerPrefs.GetInt(PrefsShiftsKey, 0);
        totalLapsCompleted    = PlayerPrefs.GetInt(PrefsLapsKey, 0);
        onTimeShiftCount      = PlayerPrefs.GetInt(PrefsOnTimeKey, 0);
        firstPlayedDate       = PlayerPrefs.GetString(PrefsFirstPlayedKey, "");
    }
}