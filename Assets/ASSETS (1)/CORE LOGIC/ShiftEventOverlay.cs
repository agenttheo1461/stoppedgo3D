using System.Collections;
using UnityEngine;

/// <summary>
/// v1.0 — Lightweight full-screen event card for two moments: a lap
/// finishing, and a shift ending. Deliberately minimal — a big centered
/// title, one thin accent rule, a couple of stat lines, gone in ~2.5s.
/// No borders-within-borders, no icon soup: the FIFA World Cup 26 broadcast
/// package look this is chasing is exactly that restraint — huge confident
/// type, a single accent line, generous empty space, quick fade.
///
/// Call ShowLapComplete(...) / ShowShiftComplete(...) from ShiftRunner.
/// Purely presentational — never blocks input or pauses the sim.
/// </summary>
public class ShiftEventOverlay : MonoBehaviour
{
    public static ShiftEventOverlay Instance { get; private set; }

    [Header("Timing")]
    public float fadeInSeconds  = 0.25f;
    public float holdSeconds    = 1.8f;
    public float fadeOutSeconds = 0.45f;

    private bool _stylesReady;
    private GUIStyle _lblEyebrow, _lblTitle, _lblStat, _lblStatValue, _lblXP, _lblSub;

    private float _alpha = 0f;
    private Coroutine _routine;

    // Current card content
    private string _eyebrow = "";
    private string _title = "";
    private string _statLine = "";
    private string _xpLine = "";
    private string _subLine = "";
    private Color _accent = MDT_UITheme.TextGreen;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public void ShowLapComplete(string routeLabel, int lapNumber, int totalLaps)
    {
        _eyebrow  = $"ROUTE {routeLabel}";
        _title    = lapNumber >= totalLaps ? "FINAL LAP COMPLETE" : "LAP COMPLETE";
        _statLine = $"Lap {lapNumber} of {totalLaps}";
        _xpLine   = "";
        _subLine  = "";
        _accent   = MDT_UITheme.TextCyan;
        Trigger();
    }

    public void ShowShiftComplete(int xpGained, int newTotalXP, bool leveledUp, int newLevel, string latenessSummary = null)
    {
        _eyebrow  = "SHIFT COMPLETE";
        _title    = leveledUp ? $"LEVEL {newLevel}" : "GOOD SHIFT";
        _statLine = leveledUp ? "Rank progress!" : $"Total XP: {newTotalXP:N0}";
        _xpLine   = $"+{xpGained} XP";
        _subLine  = latenessSummary ?? "";
        _accent   = leveledUp ? MDT_UITheme.TextAmber : MDT_UITheme.TextGreen;
        Trigger();
    }

    private void Trigger()
    {
        if (_routine != null) StopCoroutine(_routine);
        _routine = StartCoroutine(FadeSequence());
    }

    private IEnumerator FadeSequence()
    {
        float t = 0f;
        while (t < fadeInSeconds) { t += Time.deltaTime; _alpha = Mathf.Clamp01(t / fadeInSeconds); yield return null; }
        _alpha = 1f;

        yield return new WaitForSeconds(holdSeconds);

        t = 0f;
        while (t < fadeOutSeconds) { t += Time.deltaTime; _alpha = 1f - Mathf.Clamp01(t / fadeOutSeconds); yield return null; }
        _alpha = 0f;
        _routine = null;
    }

    private void OnGUI()
    {
        if (_alpha <= 0.001f) return;
        EnsureStyles();

        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.32f;

        // ── Card backdrop ────────────────────────────────────────────────
        // MDT_UITheme.DrawRoundedRect bakes its color into the generated
        // texture and resets GUI.color to white before drawing, so it does
        // NOT respect an ambient GUI.color fade — alpha has to be baked
        // into the color passed in directly.
        float cardW = 460f, cardH = string.IsNullOrEmpty(_subLine) ? 168f : 194f;
        var cardR = new Rect(cx - cardW * 0.5f, cy - 70f, cardW, cardH);
        MDT_UITheme.DrawRoundedRect(cardR, 24f, new Color(0.07f, 0.07f, 0.075f, 0.92f * _alpha));

        var prevColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, _alpha);

        // Eyebrow (small caps label above the title)
        var eyebrowR = new Rect(cx - 300, cy - 54, 600, 24);
        GUI.Label(eyebrowR, _eyebrow.ToUpperInvariant(), _lblEyebrow);

        // Title — big, bold, centered
        var titleR = new Rect(cx - 400, cy - 30, 800, 56);
        GUI.Label(titleR, _title.ToUpperInvariant(), _lblTitle);

        // Thin accent rule, centered, width proportional to alpha (subtle
        // "settle in" motion without any easing curve complexity)
        float ruleW = 120f * Mathf.Clamp01(_alpha * 1.4f);
        GUI.color = new Color(_accent.r, _accent.g, _accent.b, _alpha);
        GUI.DrawTexture(new Rect(cx - ruleW * 0.5f, cy + 30, ruleW, 3), Texture2D.whiteTexture);

        GUI.color = new Color(1f, 1f, 1f, _alpha);
        var statR = new Rect(cx - 300, cy + 42, 600, 24);
        GUI.Label(statR, _statLine, _lblStat);

        if (!string.IsNullOrEmpty(_xpLine))
        {
            GUI.color = new Color(_accent.r, _accent.g, _accent.b, _alpha);
            var xpR = new Rect(cx - 300, cy + 66, 600, 26);
            GUI.Label(xpR, _xpLine, _lblXP);
        }

        if (!string.IsNullOrEmpty(_subLine))
        {
            GUI.color = new Color(1f, 1f, 1f, _alpha);
            var subR = new Rect(cx - 300, cy + 94, 600, 20);
            GUI.Label(subR, _subLine, _lblSub);
        }

        GUI.color = prevColor;
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblEyebrow   = MDT_UITheme.MakeLabel(13, FontStyle.Bold,   TextAnchor.MiddleCenter, MDT_UITheme.TextDim);
        _lblTitle     = MDT_UITheme.MakeLabel(38, FontStyle.Bold,   TextAnchor.MiddleCenter, MDT_UITheme.TextPrimary);
        _lblStat      = MDT_UITheme.MakeLabel(15, FontStyle.Normal, TextAnchor.MiddleCenter, MDT_UITheme.TextSecond);
        _lblXP        = MDT_UITheme.MakeLabel(17, FontStyle.Bold,   TextAnchor.MiddleCenter, MDT_UITheme.TextGreen);
        _lblSub       = MDT_UITheme.MakeLabel(13, FontStyle.Normal, TextAnchor.MiddleCenter, MDT_UITheme.TextSecond);
    }
}