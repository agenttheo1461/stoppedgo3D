using UnityEngine;

/// <summary>
/// v1.0 — Persistent, mostly-UI corner widget. Two states:
///   Off duty: small "SHIFT BOARD — press ESC" chip + mini profile line.
///   On duty:  route/direction chip + a screen-space bearing arrow and
///             live distance to PlayerHandoff.DistToTargetTerminal (the terminal
///             a claimed leg starts from). This is the direct answer to
///             "not taking me to depot" — once a bus is claimed there is
///             ALWAYS an on-screen indicator of where to physically drive,
///             instead of a silent walk-up rendezvous with nothing shown.
///
/// Pure OnGUI, same visual language as the rest of the MDT_UITheme suite.
/// Reads the main camera for bearing math; falls back to the player bus
/// transform's own forward if no camera is tagged MainCamera.
/// </summary>
public class ShiftHUD : MonoBehaviour
{
    public static ShiftHUD Instance { get; private set; }

    [Header("Placement")]
    public Vector2 anchorOffset = new Vector2(16, 16);
    public float chipWidth = 300f;

    private bool _stylesReady;
    private GUIStyle _lblTitle, _lblDim, _lblRoute, _lblDist, _lblWarn;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnGUI()
    {
        EnsureStyles();

        bool onDuty = PlayerHandoff.Instance != null && PlayerHandoff.Instance.IsOnDuty;
        if (onDuty) DrawOnDutyStrip();
        else DrawOffDutyChip();
    }

    private void DrawOffDutyChip()
    {
        var r = new Rect(anchorOffset.x, anchorOffset.y, chipWidth, 56f);
        MDT_UITheme.DrawRoundedRect(r, 18f, MDT_UITheme.BGDeep);

        GUI.Label(new Rect(r.x + 12, r.y + 6, r.width - 24, 20), "SHIFT BOARD", _lblTitle);
        GUI.Label(new Rect(r.x + 12, r.y + 26, r.width - 24, 18), $"[{ShiftBoardMenu.Instance?.toggleKey}] to open", _lblDim);

        // [FIX] Rewired off DriverProgression (rank-based, now retired) onto
        // PointsManager -- no rank concept anymore, just level + lifetime
        // points, matching the "levels only, drive everything" design.
        var pm = PointsManager.Instance;
        if (pm != null)
            GUI.Label(new Rect(r.x + 12, r.y + 40, r.width - 24, 16),
                $"Level {pm.level} · {pm.lifetimePoints:N0} pts", _lblDim);
    }

    private void DrawOnDutyStrip()
    {
        var ph = PlayerHandoff.Instance;
        bool hasTarget = ph != null && ph.TargetTerminalPosition.HasValue;
        float h = hasTarget ? 112f : 76f; // +20 for the block-window chip
        var r = new Rect(anchorOffset.x, anchorOffset.y, chipWidth, h);
        MDT_UITheme.DrawRoundedRect(r, 18f, MDT_UITheme.BGDeep);

        string routeLine = "ON SHIFT";
        if (ph != null && !string.IsNullOrEmpty(ph.ActiveRoute))
            routeLine = $"{ph.ActiveRouteLabel}  ·  {(ph.IsOutbound ? "A→Z" : "Z→A")}";
        GUI.Label(new Rect(r.x + 12, r.y + 6, r.width - 24, 22), routeLine, _lblRoute);

        // Time remaining in this route's operating window — the closest
        // real equivalent to "shift block time left" for board-driven
        // claims, which don't use DailyShiftGenerator's fixed calendar
        // blocks (those only apply to the RandomFresh/planned modes).
        DrawWindowRemaining(r, ph);

        if (!hasTarget)
        {
            GUI.Label(new Rect(r.x + 12, r.y + 48, r.width - 24, 18), "No active terminal target.", _lblDim);
            return;
        }

        Transform playerT = ph.playerBus != null ? ph.playerBus.transform : null;
        GUI.Label(new Rect(r.x + 12, r.y + 48, r.width - 24, 18), ph.TargetTerminalName, _lblDim);

        // Distance comes straight from PlayerHandoff's own (already-correct,
        // already used by the console's ARRIVE command) calculation — no
        // separate distance math duplicated here.
        float distanceM = ph.DistToTargetTerminal;
        GUI.Label(new Rect(r.x + 12, r.y + 66, r.width - 24, 20),
            distanceM >= 1000f ? $"{distanceM / 1000f:0.0} km" : $"{distanceM:0} m", _lblDist);

        if (playerT != null)
        {
            Vector3 toTarget = ph.TargetTerminalPosition.Value - playerT.position;
            toTarget.y = 0f;
            float bearingDeg = Vector3.SignedAngle(playerT.forward, toTarget, Vector3.up);
            DrawBearingArrow(new Rect(r.x + r.width - 56, r.y + 50, 40, 40), bearingDeg);
        }
    }

    /// <summary>Time remaining before the current route's own operating
    /// window closes — shown as a small countdown chip. Not the same thing
    /// as DailyShiftGenerator's fixed calendar blocks (those don't apply to
    /// board-driven ad-hoc claims), but the closest real signal for "how
    /// much longer is it worth claiming another leg on this route."</summary>
    private void DrawWindowRemaining(Rect r, PlayerHandoff ph)
    {
        if (ph == null || string.IsNullOrEmpty(ph.ActiveRoute) || BusScheduler.Instance == null || SimClock.Instance == null)
            return;

        var route = BusScheduler.Instance.GetRouteData(ph.ActiveRoute);
        if (route == null) return;

        float now = SimClock.Instance.AbsoluteGameMinutes;
        float rel = now % 1440f;
        float opEnd = route.operatingEndMinutes;
        float opStart = route.operatingStartMinutes;
        if (opEnd <= opStart) opEnd += 1440f;
        float relAdj = rel < opStart ? rel + 1440f : rel;
        float remaining = opEnd - relAdj;
        if (remaining < 0f || remaining > 1440f) return; // route data looks off — skip rather than show garbage

        string text = remaining <= 60f
            ? $"Route active for {Mathf.CeilToInt(remaining)} more min"
            : $"Route active until {BusScheduler.MinutesToTimeString(opEnd % 1440f)}";
        GUI.Label(new Rect(r.x + 12, r.y + 26, r.width - 24, 18), text,
            remaining <= 30f ? _lblWarn : _lblDim);
    }

    /// <summary>Simple rotated triangle pointing toward the waypoint,
    /// relative to the bus's current forward (0° = straight ahead / top of
    /// the widget, 90° = turn right, -90° = turn left, 180° = behind you).</summary>
    private void DrawBearingArrow(Rect box, float bearingDeg)
    {
        var matrixBackup = GUI.matrix;
        Vector2 pivot = new Vector2(box.x + box.width * 0.5f, box.y + box.height * 0.5f);
        GUIUtility.RotateAroundPivot(bearingDeg, pivot);

        // Minimal triangle via three thin rects — avoids needing a custom
        // mesh/texture just for an arrow glyph.
        float cx = pivot.x, top = box.y, bottom = box.yMax;
        MDT_UITheme.DrawRect(new Rect(cx - 1.5f, top, 3, box.height), MDT_UITheme.TextCyan);
        MDT_UITheme.DrawRect(new Rect(cx - 7f, top + 10, 14, 3), MDT_UITheme.TextCyan);
        MDT_UITheme.DrawRect(new Rect(cx - 4f, top + 4, 8, 3), MDT_UITheme.TextCyan);

        GUI.matrix = matrixBackup;
    }

    private void EnsureStyles()
    {
        if (_stylesReady) return; _stylesReady = true;
        _lblTitle = MDT_UITheme.MakeLabel(12, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _lblDim   = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextDim);
        _lblRoute = MDT_UITheme.MakeLabel(13, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
        _lblDist  = MDT_UITheme.MakeLabel(16, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextGreen);
        _lblWarn  = MDT_UITheme.MakeLabel(10, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextAmber);
    }
}