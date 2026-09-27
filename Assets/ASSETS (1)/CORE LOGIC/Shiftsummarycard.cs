using UnityEngine;

/// <summary>
/// v2.0 — End-of-shift summary card. One rounded rectangle, center screen,
/// holding everything earned this session: XP gained (with the per-source
/// breakdown), new total XP, level state, laps, blocks, real + game
/// time taken, on-time flag.
///
/// Per spec: this card does NOT auto-fade and is NOT dismissable by
/// keyboard — the ONLY way past it is the on-screen ✕ button. That's
/// deliberate: the session's results deserve a full stop, not a toast.
/// This is why it's a SEPARATE component from ShiftEventOverlay — that one
/// still owns the quick, non-blocking per-lap "LAP COMPLETE" cards, which
/// SHOULD auto-fade since they fire mid-session and shouldn't block play.
///
/// v2 change: no longer depends on PlayerShiftDirector/ShiftSessionStats
/// (removed from the project). ShiftRunner calls Show(...) directly with
/// plain parameters at the point HandlePlayerShiftEnded already computes
/// them — this card is pure presentation, no polling, no event coupling.
///
/// [FIX] Rewired off DriverProgression (rank-based, retired) onto
/// PointsManager -- no rank band/MAX RANK concept anymore, just a direct
/// xp/xpToNextLevel fraction for the progress bar, and lifetimePoints
/// shown alongside XP since points are now a separate persistent currency.
/// </summary>
public class ShiftSummaryCard : MonoBehaviour
{
    public static ShiftSummaryCard Instance { get; private set; }

    [Range(380, 640)] public int cardW = 460;
    [Range(320, 640)] public int cardH = 430;

    private bool _visible;

    // Snapshot of everything the card needs, taken at Show() time.
    private string _routeLabel;
    private int _xpGained, _totalXp, _newLevel;
    private bool _leveledUp;
    private string _xpBreakdown;
    private int _lapsCompleted, _blocksTaken;
    private bool _onTimeToFirstBlock;
    private float _gameMinutesTaken, _realSecondsTaken;

    private bool _stylesReady;
    private GUIStyle _eyebrow, _big, _label, _value, _xpBig, _mono;
    private GUIStyle _closeBtn;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    /// <summary>Called by ShiftRunner.HandlePlayerShiftEnded once XP has
    /// been awarded. Takes a full snapshot — no live references to
    /// PointsManager/PlayerHandoff are held past this call, so nothing
    /// breaks if the session resets state before the player dismisses.</summary>
    public void Show(string routeLabel, int xpGained, int totalXp, bool leveledUp, int newLevel,
                      string xpBreakdown, int lapsCompleted, int blocksTaken, bool onTimeToFirstBlock,
                      float gameMinutesTaken, float realSecondsTaken)
    {
        _routeLabel = routeLabel;
        _xpGained = xpGained;
        _totalXp = totalXp;
        _leveledUp = leveledUp;
        _newLevel = newLevel;
        _xpBreakdown = xpBreakdown;
        _lapsCompleted = lapsCompleted;
        _blocksTaken = blocksTaken;
        _onTimeToFirstBlock = onTimeToFirstBlock;
        _gameMinutesTaken = gameMinutesTaken;
        _realSecondsTaken = realSecondsTaken;
        _visible = true;
    }

    private void OnGUI()
    {
        if (!_visible) return;
        Styles();

        // Dimmed backdrop
        GUI.color = new Color(0, 0, 0, 0.8f);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;

        float x = (Screen.width - cardW) * 0.5f;
        float y = (Screen.height - cardH) * 0.5f;
        var card = new Rect(x, y, cardW, cardH);

        // The rounded rectangle
        MDT_UITheme.DrawRoundedRect(card, 16f, MDT_UITheme.BGDeep);
        MDT_UITheme.DrawRoundedRect(new Rect(x, y, cardW, 54f), 16f, MDT_UITheme.BGHeader);

        GUI.BeginGroup(card);

        GUI.Label(new Rect(0, 8, cardW, 18), "SHIFT COMPLETE", _eyebrow);
        GUI.Label(new Rect(0, 24, cardW, 28), string.IsNullOrEmpty(_routeLabel) ? "" : $"ROUTE {_routeLabel}", _big);

        // On-screen ✕ — the ONLY dismissal
        if (GUI.Button(new Rect(cardW - 44, 10, 34, 34), "✕", _closeBtn))
            _visible = false;

        float ly = 66f;
        // XP earned — the headline number
        GUI.Label(new Rect(0, ly, cardW, 34), $"+{_xpGained} XP", _xpBig);
        ly += 38f;

        if (_leveledUp)
        {
            GUI.Label(new Rect(0, ly, cardW, 20), $"LEVEL UP — LEVEL {_newLevel}", _eyebrow);
            ly += 24f;
        }

        // Breakdown block (monospace-feel, left-aligned inside an inset)
        float insetH = string.IsNullOrEmpty(_xpBreakdown) ? 0f : 60f;
        if (insetH > 0f)
        {
            var inset = new Rect(24, ly, cardW - 48, insetH);
            MDT_UITheme.DrawRoundedRect(inset, 8f, MDT_UITheme.BGMid);
            GUI.Label(new Rect(inset.x + 12, inset.y + 8, inset.width - 24, inset.height - 16), _xpBreakdown, _mono);
            ly += insetH + 10f;
        }

        // Stat rows
        DrawRow(ref ly, "Laps completed", _lapsCompleted.ToString());
        DrawRow(ref ly, "Blocks taken this session", _blocksTaken.ToString());
        DrawRow(ref ly, "On time to first block", _onTimeToFirstBlock ? "YES" : "NO");
        DrawRow(ref ly, "Game time taken", $"{Mathf.FloorToInt(_gameMinutesTaken / 60f)}h {Mathf.FloorToInt(_gameMinutesTaken % 60f)}m");
        DrawRow(ref ly, "Real time taken", $"{Mathf.FloorToInt(_realSecondsTaken / 60f)}m {Mathf.FloorToInt(_realSecondsTaken % 60f)}s");

        var pm = PointsManager.Instance;
        if (pm != null)
        {
            DrawRow(ref ly, "Total XP", $"{_totalXp:N0}");
            DrawRow(ref ly, "Lifetime points", $"{pm.lifetimePoints:N0}");
            DrawRow(ref ly, "Level", $"{pm.level}");

            // Level progress bar along the bottom -- direct xp/xpToNextLevel
            // fraction now, no rank-band math (PriorRankLevel/NextRankLevel)
            // needed since PointsManager's leveling has no bands at all.
            float bx = 24, bw = cardW - 48, bh = 10f, by = cardH - 26;
            float frac = pm.xpToNextLevel > 0 ? Mathf.Clamp01(pm.xp / (float)pm.xpToNextLevel) : 0f;
            MDT_UITheme.DrawRoundedRect(new Rect(bx, by, bw, bh), 5f, MDT_UITheme.BGButton);
            MDT_UITheme.DrawRoundedRect(new Rect(bx, by, bw * frac, bh), 5f, MDT_UITheme.TextGreen);
        }

        GUI.EndGroup();
    }

    private void DrawRow(ref float y, string label, string value)
    {
        GUI.Label(new Rect(24, y, cardW * 0.55f, 20), label, _label);
        GUI.Label(new Rect(cardW * 0.5f, y, cardW * 0.5f - 24, 20), value, _value);
        y += 22f;
    }

    private void Styles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _eyebrow  = MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextAmber);
        _big      = MDT_UITheme.MakeLabel(19, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextCyan);
        _label    = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _value    = MDT_UITheme.MakeLabel(12, FontStyle.Bold, TextAnchor.MiddleRight, MDT_UITheme.TextPrimary);
        _xpBig    = MDT_UITheme.MakeLabel(28, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextGreen);
        _mono     = MDT_UITheme.MakeLabel(12, FontStyle.Normal, TextAnchor.UpperLeft, MDT_UITheme.TextSecond);
        _closeBtn = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextPrimary, 14, FontStyle.Bold);
    }
}