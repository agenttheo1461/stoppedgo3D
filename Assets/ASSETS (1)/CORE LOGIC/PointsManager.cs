using UnityEngine;

/// <summary>
/// PointsManager v2.0
///
/// POINTS
/// - +1 point per passenger boarding
/// - +1 point per passenger alighting
/// - +10 guaranteed points per completed route/lap
///
/// LEG SCORE
/// round(sqrt(boarding pax) + stops + (alighting pax * 1.5))
///
/// XP
/// - +50 guaranteed XP per completed route/lap
/// - Lap XP = round(sqrt(legScore) * 10)
///
/// SHIFT XP
/// - +50 XP for completing a shift
/// - +15 XP per completed lap in that shift
///   This is separate from lap XP.
///
/// LEVEL REQUIREMENTS
/// - Levels 1-25:   1,000 XP
/// - Levels 26-75:  1,500 XP
/// - Levels 76-150: 2,500 XP
/// - Levels 151+:   5,000 XP
///
/// Maximum level = int.MaxValue.
/// </summary>
public class PointsManager : MonoBehaviour
{
    public static PointsManager Instance { get; private set; }

    // ============================================================
    // PLAYERPREFS
    // ============================================================

    private const string PrefsLifetimeKey = "PointsManager.LifetimePoints";
    private const string PrefsXPKey = "PointsManager.XP";
    private const string PrefsLevelKey = "PointsManager.Level";
    private const string PrefsXPNextKey = "PointsManager.XPToNextLevel";

    // ============================================================
    // CONSTANTS
    // ============================================================

    private const int BASE_ROUTE_POINTS = 10;
    private const int BASE_ROUTE_XP = 50;

    private const int SHIFT_BASE_XP = 500;
    private const int SHIFT_XP_PER_LAP = 50;

    private const int LEVEL_XP_1_TO_25 = 1500;
    private const int LEVEL_XP_26_TO_75 = 2500;
    private const int LEVEL_XP_76_TO_150 = 5000;
    private const int LEVEL_XP_151_PLUS = 7500;

    // ============================================================
    // POINTS
    // ============================================================

    [Header("Points")]
    public int sessionPoints;
    public int lifetimePoints;

    // ============================================================
    // XP
    // ============================================================

    [Header("XP")]
    public int xp;
    public int level = 1;
    public int xpToNextLevel = LEVEL_XP_1_TO_25;

    // ============================================================
    // CURRENT LEG
    // ============================================================

    private int _legPaxTotal;
    private int _legAlightedTotal;
    private int _legStops;

    // ============================================================
    // AWAKE / PERSISTENCE
    // ============================================================

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        LoadProgress();
        UpdateXPRequirement();
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause)
            SaveProgress();
    }

    private void OnApplicationQuit()
    {
        SaveProgress();
    }

    // ============================================================
    // SAVE / LOAD
    // ============================================================

    public void SaveProgress()
    {
        PlayerPrefs.SetInt(PrefsLifetimeKey, lifetimePoints);
        PlayerPrefs.SetInt(PrefsXPKey, xp);
        PlayerPrefs.SetInt(PrefsLevelKey, level);
        PlayerPrefs.SetInt(PrefsXPNextKey, xpToNextLevel);

        PlayerPrefs.Save();
    }

    public void LoadProgress()
    {
        lifetimePoints = PlayerPrefs.GetInt(PrefsLifetimeKey, 0);
        xp = PlayerPrefs.GetInt(PrefsXPKey, 0);
        level = PlayerPrefs.GetInt(PrefsLevelKey, 1);
        xpToNextLevel = PlayerPrefs.GetInt(
            PrefsXPNextKey,
            GetXPRequirementForLevel(level)
        );

        // Safety
        if (level < 1)
            level = 1;

        if (level >= int.MaxValue)
        {
            level = int.MaxValue;
            xpToNextLevel = 0;
            xp = 0;
        }
        else
        {
            UpdateXPRequirement();
        }
    }

    // ============================================================
    // RESET
    // ============================================================

    public void ResetProgress()
    {
        sessionPoints = 0;
        lifetimePoints = 0;

        xp = 0;
        level = 1;
        xpToNextLevel = LEVEL_XP_1_TO_25;

        PlayerPrefs.DeleteKey(PrefsLifetimeKey);
        PlayerPrefs.DeleteKey(PrefsXPKey);
        PlayerPrefs.DeleteKey(PrefsLevelKey);
        PlayerPrefs.DeleteKey(PrefsXPNextKey);

        PlayerPrefs.Save();
    }

    // ============================================================
    // PASSENGERS
    // ============================================================

    /// <summary>
    /// Called when passengers board.
    ///
    /// Each boarding passenger gives +1 point.
    /// </summary>
    public void RegisterBoarding(int pax)
    {
        if (pax <= 0)
            return;

        _legPaxTotal += pax;

        // +1 point per boarding passenger
        sessionPoints += pax;
        lifetimePoints += pax;

        SaveProgress();
    }

    /// <summary>
    /// Called when passengers alight.
    ///
    /// Each alighting passenger gives +1 point.
    /// </summary>
    public void RegisterAlighting(int pax)
    {
        if (pax <= 0)
            return;

        _legAlightedTotal += pax;

        // +1 point per alighting passenger
        sessionPoints += pax;
        lifetimePoints += pax;

        SaveProgress();
    }

    // ============================================================
    // STOPS
    // ============================================================

    public void RegisterStop()
    {
        _legStops++;
    }

    // ============================================================
    // COMPLETE ROUTE / LAP
    // ============================================================

    /// <summary>
    /// Completes one route/lap.
    ///
    /// LEG SCORE:
    /// round(
    ///     sqrt(boarding pax)
    ///     + stops
    ///     + (alighting pax * 1.5)
    /// )
    ///
    /// POINTS:
    /// - +10 guaranteed points for completing the route/lap.
    /// - Boarding/alighting passenger points were already awarded
    ///   when those passengers were registered.
    ///
    /// XP:
    /// - +50 guaranteed route XP.
    /// - + lap XP based on the calculated leg score.
    ///
    /// Returns the calculated leg score.
    /// </summary>
    public int EndLegAndCalculate(out int xpGained, out bool leveledUp)
    {
        // --------------------------------------------------------
        // LEG SCORE
        // --------------------------------------------------------

        float baseScore =
            Mathf.Sqrt(_legPaxTotal) +
            _legStops;

        int alightBonus =
            Mathf.RoundToInt(_legAlightedTotal * 1.5f);

        int legScore =
            Mathf.RoundToInt(baseScore + alightBonus);

        // --------------------------------------------------------
        // GUARANTEED ROUTE POINTS
        // --------------------------------------------------------

        sessionPoints += BASE_ROUTE_POINTS;
        lifetimePoints += BASE_ROUTE_POINTS;

        // --------------------------------------------------------
        // LAP XP
        //
        // round(sqrt(legScore) * 10)
        // --------------------------------------------------------

        int lapXP =
            Mathf.RoundToInt(
                Mathf.Sqrt(Mathf.Max(0, legScore)) * 10f
            );

        // --------------------------------------------------------
        // TOTAL XP FOR THIS ROUTE/LAP
        //
        // 50 base XP + calculated lap XP
        // --------------------------------------------------------

        xpGained = BASE_ROUTE_XP + lapXP;

        // Add XP and directly receive whether a level-up occurred.
        leveledUp = AddXP(xpGained);

        // --------------------------------------------------------
        // RESET LEG COUNTERS
        // --------------------------------------------------------

        _legPaxTotal = 0;
        _legAlightedTotal = 0;
        _legStops = 0;

        SaveProgress();

        return legScore;
    }

    /// <summary>
    /// Compatibility overload for existing callers.
    /// </summary>
    public int EndLegAndCalculate()
    {
        return EndLegAndCalculate(out _, out _);
    }

    // ============================================================
    // XP
    // ============================================================

    /// <summary>
    /// Adds XP and handles all required level-ups.
    ///
    /// Returns true if at least one level-up occurred.
    /// </summary>
    private bool AddXP(int amount)
    {
        if (amount <= 0 || level >= int.MaxValue)
            return false;

        bool leveledUp = false;

        xp += amount;

        while (level < int.MaxValue)
        {
            UpdateXPRequirement();

            // Not enough XP for another level.
            if (xp < xpToNextLevel)
                break;

            // Spend the XP required for this level.
            xp -= xpToNextLevel;

            level++;

            leveledUp = true;

            // Maximum possible level reached.
            if (level >= int.MaxValue)
            {
                level = int.MaxValue;
                xp = 0;
                xpToNextLevel = 0;
                break;
            }
        }

        UpdateXPRequirement();
        SaveProgress();

        return leveledUp;
    }

    // ============================================================
    // XP REQUIREMENTS
    // ============================================================

    /// <summary>
    /// Returns the XP required to advance FROM the specified level.
    ///
    /// Level 1-25   = 1,000 XP
    /// Level 26-75  = 1,500 XP
    /// Level 76-150 = 2,500 XP
    /// Level 151+   = 5,000 XP
    /// </summary>
    public int GetXPRequirementForLevel(int targetLevel)
    {
        if (targetLevel >= int.MaxValue)
            return 0;

        if (targetLevel <= 25)
            return LEVEL_XP_1_TO_25;

        if (targetLevel <= 75)
            return LEVEL_XP_26_TO_75;

        if (targetLevel <= 150)
            return LEVEL_XP_76_TO_150;

        return LEVEL_XP_151_PLUS;
    }

    private void UpdateXPRequirement()
    {
        if (level >= int.MaxValue)
        {
            xpToNextLevel = 0;
            return;
        }

        xpToNextLevel = GetXPRequirementForLevel(level);
    }

    // ============================================================
    // SHIFT COMPLETION
    // ============================================================

    /// <summary>
    /// Awards the separate shift-completion XP.
    ///
    /// SHIFT XP:
    /// 50 base XP
    /// + 15 XP per completed lap
    ///
    /// This is completely separate from the XP awarded
    /// for completing each individual route/lap.
    /// </summary>
    public int AwardShiftXP(int lapsCompleted)
    {
        if (lapsCompleted < 0)
            lapsCompleted = 0;

        if (level >= int.MaxValue)
            return 0;

        int shiftXP =
            SHIFT_BASE_XP +
            (SHIFT_XP_PER_LAP * lapsCompleted);

        AddXP(shiftXP);

        return shiftXP;
    }

    // ============================================================
    // SESSION
    // ============================================================

    /// <summary>
    /// Resets only the current session points.
    /// Lifetime points and XP are untouched.
    /// </summary>
    public void ResetSession()
    {
        sessionPoints = 0;
    }
}