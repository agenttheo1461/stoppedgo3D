using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  DRIVER CONSOLE  v4  +  STOP HUD  v2  +  BUS DASHBOARD HUD (optimized)
//  — All three former MonoBehaviours merged into one file.
//
//  CLASSES IN THIS FILE
//  ────────────────────
//  DriverConsole   — bottom console panel + absorbed StopHUD pill (top-center)
//  BusDashboardHUD — speedometer, gear panel, lighting drawer
//  StopHUD         — static compatibility shim; forwards to DriverConsole.Instance
//
//  MIGRATION NOTES
//  ───────────────
//  • Remove the old StopHUD.cs and BusDashboardHUD.cs from your project.
//  • DriverConsole GameObject keeps the DriverConsole component.
//  • BusDashboardHUD remains its own component on whatever GameObject you prefer.
//  • StopHUD.Instance?.Refresh() still compiles unchanged (shim at bottom).
//  • RefreshStopHUD() kept as alias for any existing PlayerHandoff calls.
//
//  OPTIMIZATIONS vs PREVIOUS VERSIONS
//  ────────────────────────────────────
//  • StopHUD absorbed — one fewer MonoBehaviour, single OnGUI pass, shared styles.
//  • BusDashboardHUD: dirty flags prevent per-frame material/light swaps.
//  • BusDashboardHUD: SlotButton flatBg branch was identical either way — removed.
//  • StopHUD.DrawPanel() now delegates to MDT_UITheme helpers — no duplication.
//  • EnsureStyles() unified (DriverConsole + former StopHUD styles in one block).
// ═══════════════════════════════════════════════════════════════════════════════

// ─────────────────────────────────────────────────────────────────────────────
//  DRIVER CONSOLE  (absorbs StopHUD)
// ─────────────────────────────────────────────────────────────────────────────
public class DriverConsole : MonoBehaviour
{
    public static DriverConsole Instance { get; private set; }

    // ── Log entry ─────────────────────────────────────────────────────────────
    private struct LogEntry
    {
        public string text, tag, time;
    }

    // ── Inspector: Console panel ──────────────────────────────────────────────
    [Header("Console Panel")]
    [Range(300, 1000)] public int panelWidth  = 620;
    [Range(200, 560)]  public int panelHeight = 380;
    [Range(50, 500)]   public int maxLogEntries = 200;

    // ── Inspector: Stop HUD pill (formerly StopHUD.cs) ───────────────────────
    [Header("Stop HUD Pill")]
    [Tooltip("Show pill within this many metres of the next stop.")]
    public float pillShowRadius  = 50f;
    public float pillWidth       = 360f;
    public float pillHeight      = 64f;
    public float pillTopMargin   = 18f;

    // ── Layout constants ──────────────────────────────────────────────────────
    private const float Pad     = 6f;
    private const float HeaderH = 36f;
    private const float FooterH = 30f;
    // [ADD] Items 15/16 -- ButtonH/ButtonH2/SideRowH are real tap targets
    // (main command row, secondary-commands row, side Actions panel rows
    // respectively) -- scaled + floored via MDT_UITheme so this panel is
    // no longer laid out purely for a mouse. HeaderH/FooterH/StatusStripH/
    // SideGap/SidePanelW are chrome/spacing, not tap targets, left as-is.
    private static float ButtonH  => MDT_UITheme.ScaledTouchSize(34f);
    private static float ButtonH2 => MDT_UITheme.ScaledTouchSize(28f); // second (secondary-commands) button row
    private const float StatusStripH = 20f; // live doors/lateness/kneel/ignition strip

    // ── Side panel (contextual: mini-console pre-route, actions once on a route) ─
    private const float SideGap     = 8f;
    private const float SidePanelW  = 220f;
    private static float SideRowH   => MDT_UITheme.ScaledTouchSize(26f); // vertical action-button row height

    // ── Log ───────────────────────────────────────────────────────────────────
    private readonly List<LogEntry> _log = new();
    private Vector2 _logScroll;
    private bool    _autoScroll = true;

    // ── Panel position / drag ─────────────────────────────────────────────────
    private float   _panelX, _panelY;
    private bool    _minimized;
    private bool    _isDragging;
    private Vector2 _dragOffset;

    // ── Input row ─────────────────────────────────────────────────────────────
    private string           _inputText  = "";
    private readonly List<string> _history = new();
    private int              _historyIdx = -1;
    private const string     InputControl = "DCInput";
    private bool             _focusInput;

    // ── Join / slot-picker ────────────────────────────────────────────────────
    private string            _joinRouteInput    = "";
    private string            _joinVariantInput  = "";
    private BusRouteData      _joinRouteInfo;
    private bool              _joinRouteValid;
    private bool              _joinOutbound = true;
    private List<TimetableSlot> _upcomingSlots = new();
    private TimetableSlot     _selectedSlot;

    // ── Help overlay ──────────────────────────────────────────────────────────
    private bool _helpOpen;
    private Vector2 _helpScroll;

    // ── Actions panel ─────────────────────────────────────────────────────────
    private Vector2 _actionsScroll;

    // ── Styles ────────────────────────────────────────────────────────────────
    private bool     _stylesReady;
    private GUIStyle _labelSecond, _labelDim, _labelCyan, _labelAmber, _labelBoard,
                     _labelGreen,  _labelRed,  _labelWhite, _labelHeading,
                     _labelRoute,  _pillTitle, _pillBody,   _pillBracket, _pillAda;
    private GUIStyle _btnPrimary, _btnSecond, _btnDisabled, _btnDanger,
                     _btnSuccess,  _inputStyle;
    private GUIStyle _btnIcon, _btnIconSel; // header icon buttons
    private GUIStyle _gaugeLabelStyle;      // centered label drawn over level bars
    private GUIStyle _helpCmd, _helpDesc;   // help overlay styles

    private float _pulseTimer;

    // ── Gauges popup (fuel + maintenance) ────────────────────────────────────
    private bool _gaugesOpen;
    // ── Extra Settings popup ──────────────────────────────────────────────────
    private bool _settingsOpen;
    // [CHANGE] Was two separate cached refs (BusFuelSystem/BusMaintenanceSystem)
    // -- merged into one BusVehicleSystem component, same as everywhere else.
    private BusVehicleSystem _cachedVehicle;
    private BusKneelBody _cachedKneel;
    private GameObject _cachedBusObj;
    private const float GaugeRowH = 30f;
    private const float GaugePad  = 10f;
    private const float GaugeW    = 230f;

    // ── Command reference (used by ?help / help) ─────────────────────────────
    private struct CmdDoc { public string names, args, desc; }
    private static readonly CmdDoc[] _commandDocs = new[]
    {
        new CmdDoc { names = "join",       args = "<route> [variant]", desc = "Join a route and open the slot picker for it." },
        new CmdDoc { names = "arrived / arrive / a / a<n>", args = "",  desc = "Report arrival at your current target terminal (must be within range). Trailing digits (e.g. \"a3\") are currently accepted but ignored." },
        new CmdDoc { names = "depart / departing / go / d / d<n>", args = "", desc = "Depart from a terminal once your scheduled time is due (or confirm early). Trailing digits (e.g. \"d3\") are currently accepted but ignored." },
        new CmdDoc { names = "continue",   args = "",                  desc = "Continue onto the next pre-assigned leg of your chain, if one is waiting." },
        new CmdDoc { names = "board",      args = "",                  desc = "Open the shift board to pick a new route/bus." },
        new CmdDoc { names = "relief",     args = "[yes|no]",          desc = "Request relief, or answer a pending relief-swap offer." },
        new CmdDoc { names = "status",     args = "",                  desc = "Print your current shift status, route, lateness, and score." },
        new CmdDoc { names = "pax",        args = "",                  desc = "Print current onboard passenger count and stop request state." },
        new CmdDoc { names = "nextdep",    args = "",                  desc = "Print the next few scheduled departures for your route." },
        new CmdDoc { names = "breakdown",  args = "",                  desc = "Simulate a breakdown on your current bus." },
        new CmdDoc { names = "recovered",  args = "",                  desc = "Clear an active breakdown and resume service." },
        new CmdDoc { names = "<stop>/B<n>", args = "",                  desc = "Reserve terminal bay n (0-based index) at that stop, e.g. s0093/B2 = the third bay. You also auto-claim the nearest free bay on arrival." },
        new CmdDoc { names = "unstuck",    args = "",                  desc = "Right the bus if it got flipped or tilted: keeps position and heading, levels it, lifts it 1.5 m (also the Unstuck key)." },
        new CmdDoc { names = "door",       args = "",                  desc = "Toggle the front door (or use the DOORS button / hotkey)." },
        new CmdDoc { names = "rdoor",      args = "",                  desc = "Toggle the rear door on articulated/multi-door buses." },
        new CmdDoc { names = "kneel",      args = "",                  desc = "Toggle kneeling suspension for boarding." },
        new CmdDoc { names = "ignition",   args = "",                  desc = "Toggle the engine ignition on/off." },
        new CmdDoc { names = "quickstart / qs", args = "",             desc = "Battery on, close any open doors (front + rear), then crank and start the engine automatically -- same as pressing ignition twice, just timed for you. Doors are never reopened; do that yourself once you're ready." },
        new CmdDoc { names = "wedge",      args = "",                  desc = "Force the front door to a halfway 'wedged' position -- a manual fallback for a door stuck open, usable only with the battery off (use ignition/quickstart normally otherwise). Type it again to release. The DOORS button/command falls back to the same thing automatically when there's no power." },
        new CmdDoc { names = "ramp",       args = "",                  desc = "Deploy/retract the wheelchair lift ramp (also the R key) -- usable any time, same as kneel, whether or not a wheelchair passenger is actually waiting. Needs front door open, kneeling, in neutral, and parking brake set. Deploy takes ~10s (with a brief hiccup partway through), then loads/unloads any wheelchair pax over ~5s -- type ramp again once it reads 'Deployed' to raise it, which finishes over another ~10s. Regular boarding through the front door waits until the ramp is fully stowed again ONLY if a wheelchair pax was actually involved this cycle." },
        new CmdDoc { names = "eco",        args = "",                  desc = "Toggle Eco mode (or use the ECO button). On mild-hybrid transmissions (e.g. Voith DIWA 867.8 NXT), this is also the 48V hybrid system's on/off switch — stop-start, coast, boost, and brake regen only run with Eco on." },
        new CmdDoc { names = "gauges",     args = "",                  desc = "Toggle the fuel/maintenance gauges popup." },
        new CmdDoc { names = "time",       args = "",                  desc = "Print current game time and GTST day number." },
        new CmdDoc { names = "settings",   args = "",                  desc = "Toggle the extra settings popup (e.g. dashboard LED glow)." },
        new CmdDoc { names = "boardmsg / ms", args = "<text>",         desc = "Push a message to the front of every interior board's rotation for a few seconds." },
        new CmdDoc { names = "boardrt",    args = "<number> [des1|des2]", desc = "Override the route/variant number on interior LCD boards, and optionally set the destination in the same call (des1=outbound, des2=inbound). Sticky until boardclear." },
        new CmdDoc { names = "boardestination", args = "des1|des2 <route>", desc = "Set just the destination headsign override without touching the route number override. Sticky until boardclear." },
        new CmdDoc { names = "boardclear", args = "",                  desc = "Clear any pushed boardmsg/boardrt/boardestination overrides." },
        new CmdDoc { names = "boards",     args = "",                  desc = "List every interior board in the scene and what data source each is reading." },
        new CmdDoc { names = "log / schedule / route", args = "<route>", desc = "Dump a route's full manifest to the Unity console." },
        new CmdDoc { names = "clear",      args = "",                  desc = "Clear this console's log." },
        new CmdDoc { names = "help / ?help", args = "",                desc = "Show this command reference." },
    };

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        _panelX  = (Screen.width  - panelWidth)  * 0.5f;
        _panelY  = Screen.height - panelHeight - 12f;
    }

    private void Start()
    {
        PrintTagged("Console ready. Type <b>help</b> or <b>?help</b> for commands. Find a route to join.", "system");
    }

    private void Update()
    {
        _pulseTimer += Time.deltaTime * 2.5f;
        _panelX = Mathf.Clamp(_panelX, 0, Screen.width  - panelWidth);
        _panelY = Mathf.Clamp(_panelY, 0, Screen.height - panelHeight);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═════════════════════════════════════════════════════════════════════════

    public void PrintTagged(string msg, string tag = "info")
    {
        string t = BusScheduler.Instance != null ? BusScheduler.Instance.GameTimeString : "--:--";
        _log.Add(new LogEntry { text = msg, tag = tag, time = t });
        while (_log.Count > maxLogEntries) _log.RemoveAt(0);
        if (_autoScroll) _logScroll.y = float.MaxValue;
    }

    public void Print(string msg) => PrintTagged(msg, "info");

    /// <summary>Called by external systems when stop/pax state changes.
    /// OnGUI reads live state each frame so no cache flush needed.</summary>
    public void Refresh() { }

    /// <summary>Legacy alias — kept so existing PlayerHandoff calls compile.</summary>
    /// <summary>Called by ShiftRunner right after a board-driven claim
    /// succeeds — keeps the console's own join panel fields in sync, so
    /// reopening it later (after the shift ends) shows what you actually
    /// last drove instead of stale/blank fields.</summary>
    public void SetJoinFieldsFromBoard(string routeNumber, string variantLetter)
    {
        _joinRouteInput = routeNumber ?? "";
        _joinVariantInput = variantLetter ?? "";
    }

    public void RefreshStopHUD() { }

    // ═════════════════════════════════════════════════════════════════════════
    //  ONGUI
    // ═════════════════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        EnsureStyles();

        // ── Stop HUD pill (top-center) ────────────────────────────────────────
        DrawStopPill();

        // ── Driver console panel ──────────────────────────────────────────────
        if (_minimized) { DrawMinimizedBar(); _gaugesOpen = false; _helpOpen = false; _settingsOpen = false; return; }

        var panelRect = new Rect(_panelX, _panelY, panelWidth, panelHeight);
        MDT_UITheme.DrawPanel(panelRect);

        var ph0 = PlayerHandoff.Instance;
        var state0 = ph0?.ShiftState ?? PlayerHandoff.PlayerShiftState.OffDuty;
        bool offDuty = state0 == PlayerHandoff.PlayerShiftState.OffDuty
                    || state0 == PlayerHandoff.PlayerShiftState.Relieved;

        GUI.BeginGroup(panelRect);
        {
            DrawHeader(new Rect(0, 0, panelWidth, HeaderH));

            bool showStatusStrip = ph0 != null && ph0.ShiftState != PlayerHandoff.PlayerShiftState.OffDuty
                                                && ph0.ShiftState != PlayerHandoff.PlayerShiftState.Relieved;
            float stripH = showStatusStrip ? StatusStripH : 0f;
            if (showStatusStrip) DrawStatusStrip(new Rect(0, HeaderH, panelWidth, StatusStripH));

            float bodyY = HeaderH + stripH + Pad;
            // The pennant's cap eats into the right edge over the bottom
            // portion of the panel — body content stays within panelWidth
            // minus a small safety margin so nothing renders under the curve.
            // Button rows moved out to the side Actions panel — main panel body
            // now only has to leave room for the header/strip and the input row.
            float bodyH = panelHeight - HeaderH - stripH - FooterH - Pad * 3f;

            if (offDuty)
            {
                // Pre-route: route-select takes the main panel, mini-console rides the side.
                DrawJoinPanel(new Rect(Pad, bodyY, panelWidth - Pad * 2f, bodyH));
            }
            else
            {
                // On a route: console log owns the full main panel again; the side
                // panel switches over to the quick-action buttons instead.
                DrawLog(new Rect(Pad, bodyY, panelWidth - Pad * 2f, bodyH));
            }

            DrawInputRow(new Rect(Pad, bodyY + bodyH + Pad, panelWidth - Pad * 2f, FooterH));
        }
        GUI.EndGroup();

        HandleDragging(panelRect);
        DrawSidePanel(panelRect, offDuty);
        DrawGaugesPopup(panelRect);
        DrawSettingsPopup(panelRect);
        DrawHelpOverlay(panelRect);
    }

    // ── Contextual side panel ──────────────────────────────────────────────────
    //  Off duty  : mini console log, docked beside the route-select panel.
    //  On a route: quick-action buttons (ARRIVE/DEPART/DOORS/etc.), docked in
    //              the same slot — the console goes back to living in the main
    //              panel full-width, and actions get their own dedicated display.
    private void DrawSidePanel(Rect panelRect, bool offDuty)
    {
        var sideRect = new Rect(panelRect.xMax + SideGap, panelRect.y, SidePanelW, panelRect.height);
        MDT_UITheme.DrawPanel(sideRect);

        GUI.BeginGroup(sideRect);
        {
            var headerR = new Rect(0, 0, SidePanelW, HeaderH);
            MDT_UITheme.DrawRect(headerR, MDT_UITheme.BGHeader);
            GUI.Label(new Rect(10f, 0, SidePanelW - 20f, HeaderH),
                      offDuty ? "CONSOLE" : "ACTIONS", _labelSecond);

            var bodyR = new Rect(Pad, HeaderH + Pad, SidePanelW - Pad * 2f, sideRect.height - HeaderH - Pad * 2f);

            if (offDuty)
                DrawLog(bodyR);
            else
                DrawActionsPanel(bodyR);
        }
        GUI.EndGroup();
    }

    // ── Actions panel — vertical stack of the same commands that used to live
    //    in the two horizontal button rows under the console.
    private void DrawActionsPanel(Rect r)
    {
        var ph    = PlayerHandoff.Instance;
        var state = ph?.ShiftState ?? PlayerHandoff.PlayerShiftState.OffDuty;
        bool onDuty = state != PlayerHandoff.PlayerShiftState.OffDuty
                   && state != PlayerHandoff.PlayerShiftState.Relieved;
        bool kneeling = _cachedKneel != null && _cachedKneel.IsKneeling;
        var engineState = ph?.playerBus?.audioEngine != null
            ? ph.playerBus.audioEngine.engineState : BusAudioEngine.EngineRunState.Off;
        bool ecoOn = ph?.playerBus?.audioEngine != null && ph.playerBus.audioEngine.economyMode;
        bool batteryOn = ph?.playerBus?.audioEngine != null && ph.playerBus.audioEngine.batteryOn;

        var actions = new[]
        {
            (label:"A'",      cmd:"arrived",  enabled:CanArrive(state), primary:false),
            (label:"D'",      cmd:"depart",   enabled:CanDepart(state), primary:IsWaitingToDepart(state)),
            (label:"DOORS",   cmd:"door",     enabled:true,             primary:false),
            (label:"RDOOR",   cmd:"rdoor",    enabled:onDuty,           primary:false),
            (label:"KNEEL",   cmd:"kneel",    enabled:onDuty,           primary:kneeling),
            (label:"UNSTUCK", cmd:"unstuck",  enabled:ph != null && ph.playerBus != null, primary:false),
            (label:"IGNIT.",  cmd:"ignition", enabled:onDuty,           primary:engineState == BusAudioEngine.EngineRunState.Running),
            (label:"Q.START", cmd:"quickstart", enabled:onDuty && engineState != BusAudioEngine.EngineRunState.Running
                                                                    && engineState != BusAudioEngine.EngineRunState.Cranking, primary:false),
            (label:"WEDGE",   cmd:"wedge",    enabled:onDuty && ph?.playerBus?.audioEngine != null && !batteryOn, primary:false),
            // [FIX per request] Freely usable any time on duty, same as
            // KNEEL above -- no longer needs an actual wheelchair pax
            // waiting to light up.
            (label:"RAMP",    cmd:"ramp",     enabled:onDuty, primary:ph != null && ph.CurrentRampState != PlayerHandoff.RampState.Stowed),
            (label:"ECO",     cmd:"eco",      enabled:onDuty,           primary:ecoOn),
            (label:"RELIEF",  cmd:"relief",   enabled:CanRelief(ph),    primary:false),
            (label:"PAX",     cmd:"pax",      enabled:onDuty,           primary:false),
            (label:"NEXTDEP", cmd:"nextdep",  enabled:onDuty,           primary:false),
            (label:"BOARD",   cmd:"board",    enabled:true,             primary:false),
            (label:"STATUS",  cmd:"status",   enabled:true,             primary:false),
        };

        // Breakdown / recovered pill (if shown) is pinned under the action
        // list within the scrollable content, same as before.
        bool showBreak     = ph != null && ph.ShiftState == PlayerHandoff.PlayerShiftState.InService && !ph.IsBreakdown;
        bool showRecovered = ph != null && ph.IsBreakdown;
        bool showDanger    = showBreak || showRecovered;

        // [FIX] The action list (up to 12 rows) plus the breakdown pill can
        // easily exceed the side panel's visible height, and this was drawn
        // straight into `r` with no scroll view -- rows past the bottom
        // just rendered off-panel with no way to reach them. Wrap it in a
        // scroll view, same pattern as the log (_logScroll) and help
        // overlay (_helpScroll).
        float contentH = actions.Length * SideRowH + (showDanger ? SideRowH + 4f : 0f);
        float scrollW  = r.width - (contentH > r.height ? 18f : 0f);
        var viewRect   = new Rect(0, 0, scrollW, contentH);
        _actionsScroll = GUI.BeginScrollView(r, _actionsScroll, viewRect);

        float y = 0f;
        foreach (var (label, cmd, enabled, primary) in actions)
        {
            GUI.enabled = enabled;
            Color bg = primary
                ? Color.Lerp(MDT_UITheme.BGDirSel, MDT_UITheme.LEDGreen * 0.5f,
                             Mathf.Abs(Mathf.Sin(_pulseTimer * 1.5f)) * 0.4f)
                : enabled ? MDT_UITheme.BGButton : MDT_UITheme.BGMid;
            var btnR = new Rect(0, y, scrollW, SideRowH - 3f);
            MDT_UITheme.DrawRoundedRect(btnR, 8f, bg);
            var style = primary ? _btnPrimary : enabled ? _btnSecond : _btnDisabled;
            if (GUI.Button(btnR, label, style))
                PlayerHandoff.Instance?.HandleConsoleCommand(cmd);
            y += SideRowH;
        }
        GUI.enabled = true;

        if (showDanger)
        {
            string dangLbl = showRecovered ? "FIXED" : "BREAK";
            string dangCmd = showRecovered ? "recovered" : "breakdown";
            Color  dangCol = showRecovered ? MDT_UITheme.LEDGreen * 0.4f : MDT_UITheme.LEDRed * 0.4f;
            var dangR = new Rect(0, y + 4f, scrollW, SideRowH - 3f);
            MDT_UITheme.DrawRoundedRect(dangR, 8f, dangCol);
            if (GUI.Button(dangR, dangLbl, showRecovered ? _btnSuccess : _btnDanger))
                PlayerHandoff.Instance?.HandleConsoleCommand(dangCmd);
        }

        GUI.EndScrollView();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  STOP HUD PILL  (formerly StopHUD.cs)
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawStopPill()
    {
        var ph = PlayerHandoff.Instance;
        if (ph == null) return;

        var state = ph.ShiftState;
        if (state != PlayerHandoff.PlayerShiftState.InService &&
            state != PlayerHandoff.PlayerShiftState.ReliefPending) return;

        float dist = ph.DistToNextStop;
        if (dist > pillShowRadius) return;

        bool requested   = ph.StopRequested;
        // [ADD] An ADA/wheelchair event at this stop overrides what the pill
        // shows (per spec: "their stop request can override the one shown")
        // -- regular pax still board/alight normally underneath this, only
        // the DISPLAY changes. adaPending alone (no regular request at all)
        // still lights the pill -- a wheelchair pax is as real a "stop
        // requested" reason as anything else here.
        bool adaPending  = ph.AdaEventPending;
        bool showLit     = requested || adaPending;
        // Big stop-requested LED needs room on the left of the pill — bigger
        // version of the small header status dot (MDT_UITheme.DrawLED),
        // same helper, just a much larger radius and always amber/orange
        // rather than state-colored.
        float dotAreaW = showLit ? 34f : 0f;

        float x = (Screen.width - pillWidth) * 0.5f;
        var   r = new Rect(x, pillTopMargin, pillWidth, pillHeight);

        // Shadow + body
        MDT_UITheme.DrawRoundedRect(new Rect(r.x + 3, r.y + 4, r.width, r.height), 12f, new Color(0, 0, 0, 0.35f));
        MDT_UITheme.DrawRoundedRectBordered(r, 12f, new Color(0f, 0f, 0f, 0.62f), MDT_UITheme.TextCyan * new Color(1, 1, 1, 0.35f), 1);

        GUI.BeginGroup(r);

        // Big LED, pulsing while a stop is requested — stays lit (and the
        // count with it) for as long as PlayerHandoff's pax plan says
        // there's real demand here, even if you drive past without
        // stopping; grows if more people board elsewhere first. Blue
        // instead of amber specifically for the ADA-override case.
        if (showLit)
        {
            float pulse = 0.75f + 0.25f * Mathf.Abs(Mathf.Sin(_pulseTimer * 2.2f));
            Color ledColor = adaPending ? new Color(0.40f, 0.62f, 1.0f) : MDT_UITheme.LEDAmber;
            MDT_UITheme.DrawLED(new Vector2(18f, pillHeight * 0.5f), 9f, ledColor * pulse);
        }

        GUI.Label(new Rect(dotAreaW, 6, pillWidth - dotAreaW, 22), $"[{ph.NextStopName}]", _pillTitle);

        string row2;
        GUIStyle row2Style;
        if (ph.WillSkipStop)
        {
            // Skip case, spelled out explicitly instead of making the driver
            // mentally check "0 board AND 0 alight" every single stop.
            row2      = $"[SKIP]  -  0 on / 0 off  -  [{dist:F0}m]";
            row2Style = _pillBody;
        }
        else
        {
            string paxStr   = $"{ph.PredictedBoardingPax} on";
            string distStr  = $"[{dist:F0}m]";
            // [FIX] Was a single generic "[ADA PAX]" for both boarding and alighting -- say which one.
            string reasonTag = adaPending ? (ph.AdaAlightRequested ? "[ADA OFF]" : "[ADA BOARDING]") : "[STOP REQUESTED]";
            row2 = showLit
                ? $"{paxStr} / {ph.AlightCount} off  -  {reasonTag}  -  {distStr}"
                : $"{paxStr} / 0 off  -  {distStr}";
            row2Style = adaPending ? _pillAda : (requested ? _pillBracket : _pillBody);
        }
        GUI.Label(new Rect(dotAreaW, 32, pillWidth - dotAreaW, 22), row2, row2Style);

        GUI.EndGroup();
    }

    // ── Header ────────────────────────────────────────────────────────────────
    private void DrawHeader(Rect r)
    {
        MDT_UITheme.DrawHeader(r);

        var ph    = PlayerHandoff.Instance;
        var state = ph?.ShiftState ?? PlayerHandoff.PlayerShiftState.OffDuty;

        Color led = StateLEDColor(state);
        if (state == PlayerHandoff.PlayerShiftState.WaitingToDepart)
            led = Color.Lerp(MDT_UITheme.LEDAmber, MDT_UITheme.LEDGreen, Mathf.Abs(Mathf.Sin(_pulseTimer)));
        MDT_UITheme.DrawLED(new Vector2(r.x + 14, r.y + r.height * 0.5f), 5f, led);

        string headerLabel;
        if (ph != null && !string.IsNullOrEmpty(ph.ActiveRoute))
        {
            string dirTag = ph.IsOutbound ? "[A]-[Z]" : "[Z]-[A]";
            headerLabel   = $"{ph.ActiveRouteLabel} - [{ph.TargetTerminalName}] - {dirTag}";
        }
        else
        {
            headerLabel = "OFF DUTY";
        }

        GUI.Label(new Rect(r.x + 28, r.y, panelWidth - 226, r.height), headerLabel, _labelHeading);

        string timeStr = BusScheduler.Instance?.GameTimeString ?? "--:--";
        GUI.Label(new Rect(r.xMax - 158, r.y, 60, r.height), timeStr, _labelDim);

        // [ADD] Items 15/16 -- these 4 were a fixed 32x(r.height-12)
        // regardless of device, well under the ~44pt mobile touch-target
        // guideline, positioned via hand-tuned fixed offsets from r.xMax
        // that assumed that exact 32px width. Width now scales (and floors)
        // via MDT_UITheme, with the row laid out right-to-left off a
        // computed pitch instead of the old fixed offsets, so the four
        // buttons stay evenly spaced and non-overlapping at any scale.
        // Height stays capped by the header bar's own (unscaled) r.height,
        // just with a smaller margin, since growing it further would mean
        // also scaling HeaderH itself -- a much larger change than this
        // pass is scoped to.
        float iconBtnW  = MDT_UITheme.ScaledTouchSize(32f);
        float iconBtnH  = Mathf.Min(r.height - 4f, r.height - 12f + MDT_UITheme.UIScale * 8f);
        float iconBtnY  = r.y + (r.height - iconBtnH) * 0.5f;
        float iconGap   = MDT_UITheme.UIScale * 4f;
        float iconPitch = iconBtnW + iconGap;
        float edgeMargin= MDT_UITheme.UIScale * 8f;

        // Help toggle (?)
        bool helpSel = _helpOpen;
        if (GUI.Button(new Rect(r.xMax - edgeMargin - iconBtnW - iconPitch * 2f, iconBtnY, iconBtnW, iconBtnH), "?", helpSel ? _btnIconSel : _btnIcon))
            _helpOpen = !_helpOpen;

        // Gauges toggle (fuel/maintenance dropdown)
        bool gaugeSel = _gaugesOpen;
        if (GUI.Button(new Rect(r.xMax - edgeMargin - iconBtnW - iconPitch * 1f, iconBtnY, iconBtnW, iconBtnH), "⛽", gaugeSel ? _btnIconSel : _btnIcon))
            _gaugesOpen = !_gaugesOpen;

        // [NEW] Extra Settings toggle -- currently just dashboard LED glow,
        // built as its own panel (not crammed into gauges) so more settings
        // have somewhere to live later without redesigning this again.
        bool settingsSel = _settingsOpen;
        if (GUI.Button(new Rect(r.xMax - edgeMargin - iconBtnW - iconPitch * 3f, iconBtnY, iconBtnW, iconBtnH), "⚙", settingsSel ? _btnIconSel : _btnIcon))
            _settingsOpen = !_settingsOpen;

        if (GUI.Button(new Rect(r.xMax - edgeMargin - iconBtnW, iconBtnY, iconBtnW, iconBtnH), "—", _btnSecond))
            _minimized = true;
    }

    // ── Status strip (doors / lateness / kneel / ignition) ─────────────────────
    // Thin live-readout row between the header and the log — no polling
    // cache needed, OnGUI already re-reads everything every frame same as
    // the rest of this file.
    private void DrawStatusStrip(Rect r)
    {
        var ph = PlayerHandoff.Instance;
        if (ph == null || ph.playerBus == null) return;
        EnsureGaugeRefs();

        MDT_UITheme.DrawRect(r, MDT_UITheme.BGMid);

        var bus = ph.playerBus;
        string doorsTxt = $"Doors F:{(bus.doorsOpen ? "OPEN" : "shut")} R:{(bus.rearDoorsOpen ? "OPEN" : "shut")}";
        GUIStyle doorsStyle = (bus.doorsOpen || bus.rearDoorsOpen) ? _labelGreen : _labelDim;

        // [ADD] Wheelchair lift ramp readout -- no mesh yet, so this (plus
        // the ADA override on the stop pill) is the only place its progress
        // is visible at all. Rides in the doors column since the ramp is
        // itself a front-door thing; only takes any space while active.
        if (ph.CurrentRampState != PlayerHandoff.RampState.Stowed)
        {
            string rampTxt = ph.CurrentRampState switch
            {
                PlayerHandoff.RampState.Deploying  => $"  RAMP {ph.RampProgress01 * 100f:F0}%",
                PlayerHandoff.RampState.Loading    => "  RAMP: DEPLOYED — LOADING",
                PlayerHandoff.RampState.Deployed   => "  RAMP: DEPLOYED",
                PlayerHandoff.RampState.Retracting => $"  RAMP {ph.RampProgress01 * 100f:F0}%",
                _ => "",
            };
            doorsTxt  += rampTxt;
            doorsStyle = _pillAda;
        }

        // F1 = one decimal place on the raw lateness-minutes value, e.g. 4.1
        float lateness = ph.LatenessMinutes;
        string lateTxt = lateness >= 0f ? $"+{lateness:F1}m late" : $"{lateness:F1}m early";
        GUIStyle lateStyle = lateness > 5f ? _labelRed : lateness > 1f ? _labelAmber : _labelDim;

        bool kneeling = _cachedKneel != null && _cachedKneel.IsKneeling;
        string kneelTxt = _cachedKneel != null ? (kneeling ? "Kneel: DOWN" : "Kneel: up") : "Kneel: n/a";
        GUIStyle kneelStyle = kneeling ? _labelAmber : _labelDim;

        var engineState = bus.audioEngine != null ? bus.audioEngine.engineState : BusAudioEngine.EngineRunState.Off;
        string engTxt = engineState switch
        {
            BusAudioEngine.EngineRunState.Running     => "Engine: RUN",
            BusAudioEngine.EngineRunState.Cranking    => "Engine: CRANK",
            BusAudioEngine.EngineRunState.ReadyToStart=> "Engine: READY",
            _                                         => "Engine: off",
        };
        GUIStyle engStyle = engineState == BusAudioEngine.EngineRunState.Running ? _labelGreen
                           : engineState == BusAudioEngine.EngineRunState.Off    ? _labelDim
                           : _labelAmber;

        // Riders onboard / capacity, e.g. "Pax 20/65"; amber when getting full, red at/over capacity.
        int paxOn  = ph.OnboardPax;
        int paxCap = Mathf.Max(1, ph.PassengerCapacity);
        string paxTxt = $"Pax {paxOn}/{paxCap}";
        GUIStyle paxStyle = paxOn >= paxCap ? _labelRed : paxOn * 100 >= paxCap * 85 ? _labelAmber : _labelDim;

        float colW = r.width / 5f;
        GUI.Label(new Rect(r.x + 8f,          r.y, colW - 8f, r.height), doorsTxt, doorsStyle);
        GUI.Label(new Rect(r.x + colW,        r.y, colW,      r.height), lateTxt,  lateStyle);
        GUI.Label(new Rect(r.x + colW * 2f,   r.y, colW,      r.height), kneelTxt, kneelStyle);
        GUI.Label(new Rect(r.x + colW * 3f,   r.y, colW,      r.height), engTxt,   engStyle);
        GUI.Label(new Rect(r.x + colW * 4f,   r.y, colW - 8f, r.height), paxTxt,   paxStyle);
    }

    // ── Help overlay ──────────────────────────────────────────────────────────
    //  Floating panel listing every console command + description. Toggled
    //  by the header's "?" button or by typing "help"/"?help". Pops above
    //  the console when there's room, otherwise below — same placement
    //  logic as the gauges popup.
    private void DrawHelpOverlay(Rect panelRect)
    {
        if (!_helpOpen) return;

        float w = 420f;
        float rowH = 40f;
        float h = Mathf.Min(420f, 40f + _commandDocs.Length * rowH);

        float x = panelRect.x;
        float y = panelRect.y - h - 8f;
        if (y < 4f) y = panelRect.yMax + 8f;

        var r = new Rect(x, y, w, h);
        MDT_UITheme.DrawPanel(r);
        GUI.BeginGroup(r);

        GUI.Label(new Rect(GaugePad, 6, w - GaugePad * 2f - 30f, 18), "CONSOLE COMMANDS", _labelHeading);
        if (GUI.Button(new Rect(w - 32, 6, 24, 20), "✕", _btnSecond)) _helpOpen = false;

        var listArea = new Rect(0, 28, w, h - 28);
        float contentH = _commandDocs.Length * rowH;
        _helpScroll = GUI.BeginScrollView(listArea, _helpScroll, new Rect(0, 0, w - 18, contentH));

        for (int i = 0; i < _commandDocs.Length; i++)
        {
            var d = _commandDocs[i];
            float ry = i * rowH;
            if (i % 2 == 0) MDT_UITheme.DrawRect(new Rect(0, ry, w - 18, rowH), new Color(1, 1, 1, 0.02f));

            string cmdText = string.IsNullOrEmpty(d.args) ? d.names : $"{d.names} {d.args}";
            GUI.Label(new Rect(GaugePad, ry + 2, w - GaugePad * 2f, 18), cmdText, _helpCmd);
            GUI.Label(new Rect(GaugePad, ry + 20, w - GaugePad * 2f, 18), d.desc, _helpDesc);
        }
        GUI.EndScrollView();

        GUI.EndGroup();
    }

    // ── Gauges popup (fuel + maintenance) ────────────────────────────────────
    private void EnsureGaugeRefs()
    {
        var ph     = PlayerHandoff.Instance;
        var busObj = ph != null && ph.playerBus != null ? ph.playerBus.gameObject : null;
        if (busObj == _cachedBusObj) return;

        _cachedBusObj = busObj;
        _cachedVehicle = busObj != null ? busObj.GetComponent<BusVehicleSystem>() : null;
        _cachedKneel   = busObj != null ? busObj.GetComponent<BusKneelBody>()     : null;
    }

    private void DrawGaugesPopup(Rect panelRect)
    {
        if (!_gaugesOpen) return;
        EnsureGaugeRefs();

        int    rows = 1 + 5; // fuel + engine/transmission/wheels/brakes/AC
        float  h    = 26f + rows * GaugeRowH + GaugePad;

        float x = panelRect.xMax - GaugeW;
        float y = panelRect.y - h - 8f;      // prefer popping up above the console
        if (y < 4f) y = panelRect.yMax + 8f; // not enough room — drop below instead

        var r = new Rect(x, y, GaugeW, h);
        MDT_UITheme.DrawPanel(r);
        GUI.BeginGroup(r);

        GUI.Label(new Rect(GaugePad, 6, GaugeW - GaugePad * 2f, 18), "VEHICLE STATUS", _labelHeading);
        float ry = 26f;

        if (_cachedVehicle != null)
        {
            float frac  = _cachedVehicle.currentFraction;
            string label = $"{_cachedVehicle.GetFuelLabel()}  {frac * 100f:F0}%  ·  {_cachedVehicle.RangeMilesRemaining:F0} mi left";
            MDT_UITheme.DrawLevelBar(new Rect(GaugePad, ry, GaugeW - GaugePad * 2f, 20f),
                frac, MDT_UITheme.LevelColor(frac), label, _gaugeLabelStyle);
        }
        else
        {
            GUI.Label(new Rect(GaugePad, ry, GaugeW - GaugePad * 2f, 20f), "Fuel: n/a", _labelDim);
        }
        ry += GaugeRowH;

        if (_cachedVehicle != null)
        {
            DrawGaugeRow("Engine",       _cachedVehicle.GetCondition(BusVehicleSystem.Part.Engine),       ry); ry += GaugeRowH;
            DrawGaugeRow("Transmission", _cachedVehicle.GetCondition(BusVehicleSystem.Part.Transmission), ry); ry += GaugeRowH;
            DrawGaugeRow("A/C",          _cachedVehicle.GetCondition(BusVehicleSystem.Part.AC),           ry);
        }
        else
        {
            GUI.Label(new Rect(GaugePad, ry, GaugeW - GaugePad * 2f, 20f), "Maintenance: n/a", _labelDim);
        }

        GUI.EndGroup();
    }

    private void DrawGaugeRow(string label, float condition0to100, float ry)
    {
        float frac = Mathf.Clamp01(condition0to100 / 100f);
        string text = $"{label}  {condition0to100:F0}%";
        MDT_UITheme.DrawLevelBar(new Rect(GaugePad, ry, GaugeW - GaugePad * 2f, 20f),
            frac, MDT_UITheme.LevelColor(frac), text, _gaugeLabelStyle);
    }

    // ── Extra Settings popup ──────────────────────────────────────────────────
    // Same panel pattern as DrawGaugesPopup above -- pops up near the
    // console, prefers above, drops below if there isn't room. Built as its
    // own panel rather than folded into gauges so more settings have a home
    // later without a redesign.
    private const float SettingsW      = 260f;
    private const float SettingsRowH   = 26f;
    private const float SettingsPad    = 10f;

    private void DrawSettingsPopup(Rect panelRect)
    {
        if (!_settingsOpen) return;

        int   rows = 1; // LED glow only now -- A/C cycling moved onto the dashboard's A/C panel, next to the slider it actually governs
        float h    = 26f + rows * SettingsRowH + SettingsPad;

        float x = panelRect.xMax - SettingsW;
        float y = panelRect.y - h - 8f;
        if (y < 4f) y = panelRect.yMax + 8f;

        var r = new Rect(x, y, SettingsW, h);
        MDT_UITheme.DrawPanel(r);
        GUI.BeginGroup(r);

        GUI.Label(new Rect(SettingsPad, 6, SettingsW - SettingsPad * 2f, 18), "EXTRA SETTINGS", _labelHeading);
        float ry = 26f;

        bool ledOn = BusDashboardHUD.LedGlowEnabled;
        bool ledNow = GUI.Toggle(new Rect(SettingsPad, ry, SettingsW - SettingsPad * 2f, SettingsRowH - 4f),
            ledOn, " Dashboard button LED glow (blue)");
        if (ledNow != ledOn) BusDashboardHUD.LedGlowEnabled = ledNow;

        GUI.EndGroup();
    }

    // ── Log ───────────────────────────────────────────────────────────────────
    private void DrawLog(Rect r)
    {
        MDT_UITheme.DrawInset(r, MDT_UITheme.BGMid);

        const float lineH = 16f;
        float totalH  = Mathf.Max(r.height, _log.Count * lineH + 6f);
        var   content = new Rect(0, 0, r.width - 18f, totalH);

        float prevY = _logScroll.y;
        _logScroll  = GUI.BeginScrollView(r, _logScroll, content, false, true);

        if (Event.current.type == EventType.ScrollWheel)
            _autoScroll = _logScroll.y >= totalH - r.height - 2f;

        float y = Mathf.Max(0f, totalH - _log.Count * lineH - 4f);
        for (int i = 0; i < _log.Count; i++)
        {
            var   e    = _log[i];
            float rowY = y + i * lineH;
            GUI.Label(new Rect(2f,  rowY, 40f,                   lineH), e.time, _labelDim);
            GUI.Label(new Rect(44f, rowY, content.width - 44f,   lineH), e.text, TagToStyle(e.tag));
        }

        GUI.EndScrollView();

        var btnR = new Rect(r.xMax - 22f, r.yMax - 20f, 18f, 16f);
        MDT_UITheme.DrawRect(btnR, (_autoScroll ? MDT_UITheme.LEDGreen : MDT_UITheme.LEDOff) * new Color(1,1,1,0.3f));
        if (GUI.Button(btnR, "↓", _labelDim)) { _autoScroll = true; _logScroll.y = float.MaxValue; }
    }

    // ── Join / slot-picker panel ──────────────────────────────────────────────
    private void DrawJoinPanel(Rect r)
    {
        // [ADD] Items 15/16 -- every pick button in this panel (variant,
        // direction, slot, JOIN) was a fixed 22-28px tall regardless of
        // device. rowH/rowPitch/joinBtnH below scale + floor them via
        // MDT_UITheme, with the pitch derived FROM the scaled button height
        // (not scaled independently from the old fixed pitch) so rows can't
        // start overlapping once the touch-target floor kicks in on a phone.
        float rowH     = MDT_UITheme.ScaledTouchSize(22f);
        float rowGap   = MDT_UITheme.UIScale * 4f;
        float rowPitch = rowH + rowGap;
        float joinBtnH = MDT_UITheme.ScaledTouchSize(28f);

        float x = r.x + 4f, y = r.y + 4f, w = r.width - 8f;

        GUI.Label(new Rect(x, y, w, 16f), "Route number", _labelDim);
        y += 16f;

        GUI.SetNextControlName(InputControl + "_join");
        _joinRouteInput = GUI.TextField(new Rect(x, y, w - 64f, 24f), _joinRouteInput, _inputStyle);
        if (GUI.Button(new Rect(x + w - 60f, y, 60f, 24f), "FIND", _btnSecond)) DoFindRoute();
        y += 28f;

        if (!_joinRouteValid || _joinRouteInfo == null) return;

        // ── Variant picker ────────────────────────────────────────────────────
        int varCount = _joinRouteInfo.variants?.Count ?? 0;
        if (varCount > 0)
        {
            float btnW = (w - varCount * 4f) / (varCount + 1);
            float bx   = x;

            bool mainSel = string.IsNullOrEmpty(_joinVariantInput);
            MDT_UITheme.DrawRoundedRect(new Rect(bx, y, btnW, rowH), 8f, mainSel ? MDT_UITheme.BGDirSel : MDT_UITheme.BGPill);
            if (GUI.Button(new Rect(bx, y, btnW, rowH), "Main", mainSel ? _labelGreen : _labelSecond))
                { _joinVariantInput = ""; RefreshUpcomingSlots(); }
            bx += btnW + 4f;

            foreach (var v in _joinRouteInfo.variants)
            {
                bool sel = _joinVariantInput == v.variantLetter;
                MDT_UITheme.DrawRoundedRect(new Rect(bx, y, btnW, rowH), 8f, sel ? MDT_UITheme.BGDirSel : MDT_UITheme.BGPill);
                if (GUI.Button(new Rect(bx, y, btnW, rowH), v.variantLetter, sel ? _labelGreen : _labelSecond))
                    { _joinVariantInput = v.variantLetter; RefreshUpcomingSlots(); }
                bx += btnW + 4f;
            }
            y += rowPitch;
        }

        // ── Direction toggle ──────────────────────────────────────────────────
        float dirW = (w - 4f) * 0.5f;
        MDT_UITheme.DrawRoundedRect(new Rect(x, y, dirW, rowH), 8f, _joinOutbound ? MDT_UITheme.BGDirSel : MDT_UITheme.BGPill);
        if (GUI.Button(new Rect(x, y, dirW, rowH), "A → Z", _joinOutbound ? _labelGreen : _labelSecond))
            { _joinOutbound = true; RefreshUpcomingSlots(); }

        MDT_UITheme.DrawRoundedRect(new Rect(x + dirW + 4f, y, dirW, rowH), 8f, !_joinOutbound ? MDT_UITheme.BGDirSel : MDT_UITheme.BGPill);
        if (GUI.Button(new Rect(x + dirW + 4f, y, dirW, rowH), "Z → A", !_joinOutbound ? _labelGreen : _labelSecond))
            { _joinOutbound = false; RefreshUpcomingSlots(); }
        y += rowPitch;

        // ── Upcoming slots ────────────────────────────────────────────────────
        GUI.Label(new Rect(x, y, w, 16f), "Pick a departure", _labelDim);
        y += 16f;

        if (_upcomingSlots.Count == 0)
        {
            GUI.Label(new Rect(x, y, w, 20f), "No upcoming slots.", _labelDim);
            y += rowPitch;
        }
        else
        {
            foreach (var slot in _upcomingSlots)
            {
                bool sel = _selectedSlot == slot;
                string dep = BusScheduler.MinutesToTimeString(slot.scheduledDeparture);
                MDT_UITheme.DrawRoundedRect(new Rect(x, y, w, rowH), 8f, sel ? MDT_UITheme.BGDirSel : MDT_UITheme.BGPill);
                if (GUI.Button(new Rect(x, y, w, rowH), $"{dep}  {slot.DirectionLabel}", sel ? _labelGreen : _labelSecond))
                    _selectedSlot = slot;
                y += rowPitch;
            }
        }
        y += 4f;

        // ── JOIN button ───────────────────────────────────────────────────────
        bool canJoin = _selectedSlot != null;
        GUI.enabled  = canJoin;
        MDT_UITheme.DrawRoundedRect(new Rect(x, y, w, joinBtnH), 8f, canJoin ? MDT_UITheme.BGDirSel : MDT_UITheme.BGButton);
        string joinLabel = canJoin
            ? $"JOIN {_joinRouteInput.ToUpper()}{_joinVariantInput} @ {BusScheduler.MinutesToTimeString(_selectedSlot.scheduledDeparture)}"
            : "JOIN";
        if (GUI.Button(new Rect(x, y, w, joinBtnH), joinLabel, _btnSuccess))
        {
            PlayerHandoff.Instance?.JoinRouteWithSlot(_joinRouteInput.ToUpper(), _joinVariantInput, _selectedSlot);
            _selectedSlot   = null;
            _upcomingSlots.Clear();
            _joinRouteValid = false;
            _joinRouteInfo  = null;
        }
        GUI.enabled = true;
    }

    private void DoFindRoute()
    {
        _joinRouteValid   = false;
        _joinRouteInfo    = null;
        _selectedSlot     = null;
        _upcomingSlots.Clear();
        _joinVariantInput = "";
        _joinOutbound     = true;

        string input = _joinRouteInput.Trim().ToUpper();
        if (string.IsNullOrEmpty(input)) { PrintTagged("Enter a route number.", "warn"); return; }

        PlayerHandoff.ParseRouteAndVariant(input, out string routeNum, out string varLetter);
        if (!string.IsNullOrEmpty(varLetter)) { _joinVariantInput = varLetter; _joinRouteInput = routeNum; }

        var scheduler = BusScheduler.Instance;
        if (scheduler == null) { PrintTagged("Scheduler offline.", "error"); return; }

        var route = scheduler.GetRouteData(_joinRouteInput);
        if (route == null) { PrintTagged($"Route {_joinRouteInput} not found.", "warn"); return; }

        _joinRouteInfo  = route;
        _joinRouteValid = true;
        RefreshUpcomingSlots();
    }

    private void RefreshUpcomingSlots()
    {
        _selectedSlot = null;
        _upcomingSlots.Clear();
        if (BusScheduler.Instance == null || _joinRouteInfo == null) return;
        _upcomingSlots = BusScheduler.Instance.PeekUpcomingSlots(
            _joinRouteInfo.routeNumber, _joinVariantInput, _joinOutbound, 3);
    }

    private bool CanArrive(PlayerHandoff.PlayerShiftState s) =>
        s == PlayerHandoff.PlayerShiftState.InService        ||
        s == PlayerHandoff.PlayerShiftState.ReliefPending    ||
        s == PlayerHandoff.PlayerShiftState.DeadrunToStart;

    private bool CanDepart(PlayerHandoff.PlayerShiftState s) =>
        s == PlayerHandoff.PlayerShiftState.WaitingToDepart  ||
        s == PlayerHandoff.PlayerShiftState.ArrivedAtTerminal;

    private bool IsWaitingToDepart(PlayerHandoff.PlayerShiftState s) =>
        s == PlayerHandoff.PlayerShiftState.WaitingToDepart;

    private bool CanRelief(PlayerHandoff ph) =>
        ph != null                                           &&
        ph.ShiftState != PlayerHandoff.PlayerShiftState.OffDuty   &&
        ph.ShiftState != PlayerHandoff.PlayerShiftState.Relieved  &&
        !ph.IsBreakdown;

    // ── Input row ─────────────────────────────────────────────────────────────
    private void DrawInputRow(Rect r)
    {
        MDT_UITheme.DrawRoundedRect(r, 8f, MDT_UITheme.BGMid);

        float sendW  = 50f;
        var   fieldR = new Rect(r.x + 4f, r.y + 3f, r.width - sendW - 10f, r.height - 6f);
        var   sendR  = new Rect(r.xMax - sendW, r.y + 3f, sendW - 2f, r.height - 6f);

        if (Event.current.type == EventType.KeyDown)
        {
            if (Event.current.keyCode == KeyCode.UpArrow && _history.Count > 0)
            {
                _historyIdx = Mathf.Min(_historyIdx + 1, _history.Count - 1);
                _inputText  = _history[_history.Count - 1 - _historyIdx];
                Event.current.Use();
            }
            else if (Event.current.keyCode == KeyCode.DownArrow)
            {
                if (_historyIdx > 0) { _historyIdx--; _inputText = _history[_history.Count - 1 - _historyIdx]; }
                else                 { _historyIdx = -1; _inputText = ""; }
                Event.current.Use();
            }
            else if (Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                SubmitInput();
                Event.current.Use();
            }
        }

        GUI.SetNextControlName(InputControl);
        MDT_UITheme.DrawRoundedRect(fieldR, 6f, MDT_UITheme.BGDeep);
        MDT_UITheme.DrawRect(new Rect(fieldR.x, fieldR.yMax - 1, fieldR.width, 1),
                             MDT_UITheme.TextCyan * new Color(1,1,1,0.4f));

        _inputText = GUI.TextField(fieldR, _inputText, _inputStyle);

        if (string.IsNullOrEmpty(_inputText) && Event.current.type == EventType.Repaint)
            GUI.Label(fieldR, "  command…", _labelDim);

        if (GUI.Button(sendR, "GO", _btnPrimary)) SubmitInput();
        if (_focusInput) { GUI.FocusControl(InputControl); _focusInput = false; }
    }

    private static bool IsLetterThenDigits(string lc, char letter)
    {
        if (lc.Length < 2 || lc[0] != letter) return false;
        for (int i = 1; i < lc.Length; i++)
            if (!char.IsDigit(lc[i])) return false;
        return true;
    }

    private void SubmitInput()
    {
        string cmd = _inputText.Trim();
        if (string.IsNullOrEmpty(cmd)) return;

        if (_history.Count == 0 || _history[^1] != cmd) _history.Add(cmd);
        if (_history.Count > 50) _history.RemoveAt(0);

        _historyIdx  = -1;
        _inputText   = "";
        _autoScroll  = true;
        _logScroll.y = float.MaxValue;

        // Locally-handled meta-commands (don't need PlayerHandoff at all).
        string lc = cmd.ToLowerInvariant();
        if (lc == "help" || lc == "?help" || lc == "?")
        {
            _helpOpen = true;
            PrintTagged("Command reference opened — see the panel above/below the console.", "system");
            return;
        }
        if (lc == "clear")
        {
            _log.Clear();
            PrintTagged("Log cleared.", "system");
            return;
        }
        if (lc == "gauges")
        {
            _gaugesOpen = !_gaugesOpen;
            return;
        }
        if (lc == "settings")
        {
            _settingsOpen = !_settingsOpen;
            return;
        }
        if (lc == "eco")
        {
            // Handled fully locally, same tier as "gauges" above -- economyMode
            // is a plain bool directly on BusAudioEngine, no PlayerHandoff
            // command-switch entry needed. This is the same field that's
            // already wired everywhere else in BusAudioEngine (RPM ceilings,
            // kickdown gating); on mild-hybrid transmissions (e.g. the Voith
            // DIWA 867.8 NXT) it does double duty as that system's own
            // on/off switch (stop-start/coast/boost/regen).
            var ae = PlayerHandoff.Instance?.playerBus?.audioEngine;
            if (ae == null) { PrintTagged("No active bus to toggle Eco mode on.", "system"); return; }
            ae.economyMode = !ae.economyMode;
            PrintTagged($"Eco mode {(ae.economyMode ? "ON" : "OFF")}.", "system");
            return;
        }

        // [SHORTHAND] "a"/"arrive"/"a<digits>" all forward to "arrived", and
        // "d"/"d<digits>" forward to "depart" -- matches the A'/D' side-panel
        // buttons. NOTE: arrived/depart don't currently take an argument
        // downstream in PlayerHandoff, so any trailing digits (the "3" in
        // "a3") are accepted but dropped rather than forwarded -- if that
        // number is meant to target a specific terminal/leg, PlayerHandoff's
        // HandleConsoleCommand needs a parameter added to actually use it;
        // right now "a3" and "a" behave identically.
        if (lc == "a" || lc == "arrive" || IsLetterThenDigits(lc, 'a'))
        {
            PlayerHandoff.Instance?.HandleConsoleCommand("arrived");
            return;
        }
        if (lc == "d" || IsLetterThenDigits(lc, 'd'))
        {
            PlayerHandoff.Instance?.HandleConsoleCommand("depart");
            return;
        }

        if (lc == "time")
        {
            // Now that the old settings menu (which used to expose the game
            // clock) is decommissioned, this is the only way to see game
            // time without it. SimClock is the single authoritative source
            // per the SimClock refactor -- GameTimeString/GameDayNumber,
            // nothing local computed here.
            if (SimClock.Instance == null)
            {
                PrintTagged("SimClock not present in scene.", "warn");
                return;
            }
            PrintTagged($"Game time: <b>{SimClock.Instance.GameTimeString}</b>  (GTST day {SimClock.Instance.GameDayNumber})", "system");
            return;
        }

        if (lc == "boardmsg" || lc.StartsWith("boardmsg ") || lc == "ms" || lc.StartsWith("ms "))
        {
            // Same tier as gauges/eco above -- BusBoardDebugChannel is a plain
            // static channel, no PlayerHandoff entry needed. "ms" is just a
            // quick-type alias for "boardmsg" (per spec). Use the ORIGINAL
            // (non-lowercased) text after the keyword so the message keeps
            // whatever casing the driver typed, only uppered at push time to
            // match the LED-board look.
            string keyword = lc.StartsWith("boardmsg") ? "boardmsg" : "ms";
            string text = cmd.Length > keyword.Length ? cmd.Substring(keyword.Length).Trim() : "";
            if (string.IsNullOrEmpty(text))
            {
                PrintTagged($"Usage: {keyword} <text>", "warn");
                return;
            }
            BusBoardDebugChannel.Push(text.ToUpperInvariant());
            PrintTagged($"Pushed to all interior boards: \"{text.ToUpperInvariant()}\"", "system");
            return;
        }
        if (lc == "boardrt" || lc.StartsWith("boardrt "))
        {
            // [GROUPED] boardrt now optionally takes a trailing des1/des2 too,
            // so one command sets both the route number AND destination
            // headsign instead of needing boardrt + boardestination back to
            // back: "boardrt 95 des1" == old "boardrt 95" + "boardestination des1 95".
            // Plain "boardrt <number>" with no des1/des2 still works exactly
            // as before -- route override only, destination untouched.
            string rest = cmd.Length > "boardrt".Length ? cmd.Substring("boardrt".Length).Trim() : "";
            if (string.IsNullOrEmpty(rest))
            {
                PrintTagged("Usage: boardrt <number> [des1|des2]", "warn");
                return;
            }
            string[] rtParts = rest.Split(new[] { ' ' }, 2, System.StringSplitOptions.RemoveEmptyEntries);
            string num = rtParts[0];
            BusBoardDebugChannel.SetRouteOverride(num.ToUpperInvariant());

            if (rtParts.Length < 2)
            {
                PrintTagged($"Board route override set to \"{num.ToUpperInvariant()}\". Clear with boardclear.", "system");
                return;
            }

            string desArg = rtParts[1].Trim().ToLowerInvariant();
            if (desArg != "des1" && desArg != "des2")
            {
                PrintTagged($"Board route override set to \"{num.ToUpperInvariant()}\". (Unrecognized trailing arg \"{rtParts[1]}\" ignored -- expected des1 or des2.)", "warn");
                return;
            }
            bool outbound = desArg == "des1";
            var route = BusScheduler.Instance != null ? BusScheduler.Instance.GetRouteData(num) : null;
            if (route == null)
            {
                PrintTagged($"Board route override set to \"{num.ToUpperInvariant()}\", but route {num} not found for destination lookup -- destination unchanged.", "warn");
                return;
            }
            string dest = outbound ? route.destinationNameOutbound : route.destinationNameInbound;
            BusBoardDebugChannel.SetDestinationOverride(dest);
            PrintTagged($"Board route override set to \"{num.ToUpperInvariant()}\", destination set to \"{dest}\" ({(outbound ? "outbound" : "inbound")}). Clear with boardclear.", "system");
            return;
        }
        if (lc == "boardestination" || lc.StartsWith("boardestination "))
        {
            // "boardestination des1 <route>" -> that route's OUTBOUND terminal.
            // "boardestination des2 <route>" -> that route's INBOUND terminal.
            string rest = cmd.Length > "boardestination".Length ? cmd.Substring("boardestination".Length).Trim() : "";
            string[] parts = rest.Split(new[] { ' ' }, 2, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || (parts[0].ToLowerInvariant() != "des1" && parts[0].ToLowerInvariant() != "des2"))
            {
                PrintTagged("Usage: boardestination des1 <route>  |  boardestination des2 <route>", "warn");
                return;
            }
            bool outbound = parts[0].ToLowerInvariant() == "des1";
            string routeNum = parts[1].Trim();
            var route = BusScheduler.Instance != null ? BusScheduler.Instance.GetRouteData(routeNum) : null;
            if (route == null)
            {
                PrintTagged($"Route {routeNum} not found.", "warn");
                return;
            }
            string dest = outbound ? route.destinationNameOutbound : route.destinationNameInbound;
            BusBoardDebugChannel.SetDestinationOverride(dest);
            PrintTagged($"Board destination override set to \"{dest}\" (route {routeNum}, {(outbound ? "outbound" : "inbound")}). Clear with boardclear.", "system");
            return;
        }
        if (lc == "boardclear")
        {
            BusBoardDebugChannel.Clear();
            PrintTagged("All board overrides cleared.", "system");
            return;
        }
        if (lc == "boards")
        {
            var lcds    = FindObjectsOfType<BusInteriorLCDBoard>();
            var scrolls = FindObjectsOfType<BusInteriorScrollBoard>();
            PrintTagged($"{lcds.Length} LCD board(s), {scrolls.Length} scroll board(s) in scene.", "system");
            foreach (var b in lcds)    PrintTagged($"  LCD    '{b.name}' -> {b.SourceDescription}", "info");
            foreach (var b in scrolls) PrintTagged($"  SCROLL '{b.name}' -> {b.SourceDescription}", "info");
            return;
        }

        PlayerHandoff.Instance?.HandleConsoleCommand(cmd);
    }

    // ── Minimized bar ─────────────────────────────────────────────────────────
    private void DrawMinimizedBar()
    {
        var ph    = PlayerHandoff.Instance;
        var state = ph?.ShiftState ?? PlayerHandoff.PlayerShiftState.OffDuty;

        var barRect = new Rect(_panelX, _panelY + panelHeight - 32f, panelWidth, 32f);
        MDT_UITheme.DrawPanel(barRect);
        GUI.BeginGroup(barRect);

        MDT_UITheme.DrawStatusIndicator(new Vector2(16f, 16f), 5f, StateLEDColor(state), StateLEDShape(state));

        string label = ph != null && !string.IsNullOrEmpty(ph.ActiveRoute)
            ? $"{ph.ActiveRouteLabel}  |  {StateDisplayName(state)}"
            : "DRIVER CONSOLE";
        GUI.Label(new Rect(30f, 6f, panelWidth - 100f, 20f), label, _labelSecond);

        if (GUI.Button(new Rect(panelWidth - 50f, 3f, 44f, 26f), "SHOW", _btnSecond))
            _minimized = false;

        GUI.EndGroup();
    }

    // ── Dragging ──────────────────────────────────────────────────────────────
    private void HandleDragging(Rect panelRect)
    {
        var headerRect = new Rect(panelRect.x, panelRect.y, panelRect.width - 40f, HeaderH);
        var e = Event.current;

        if (e.type == EventType.MouseDown && headerRect.Contains(e.mousePosition) && !_isDragging)
        {
            _isDragging = true;
            _dragOffset = e.mousePosition - new Vector2(_panelX, _panelY);
            e.Use();
        }
        if (_isDragging)
        {
            if (e.type == EventType.MouseDrag)
            {
                _panelX = e.mousePosition.x - _dragOffset.x;
                _panelY = e.mousePosition.y - _dragOffset.y;
                e.Use();
            }
            if (e.type == EventType.MouseUp) { _isDragging = false; e.Use(); }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STYLES  (merged DriverConsole + StopHUD styles)
    // ═════════════════════════════════════════════════════════════════════════
    private void EnsureStyles()
    {
        if (_stylesReady) return;
        _stylesReady = true;

        // Console styles
        _labelSecond  = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.UpperLeft,  MDT_UITheme.TextSecond);
        _labelDim     = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.UpperLeft,  MDT_UITheme.TextDim);
        _labelCyan    = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextCyan);
        _labelAmber   = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.UpperLeft,  MDT_UITheme.TextAmber);
        _labelBoard   = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.UpperLeft,  new Color(0.45f, 0.65f, 1.0f, 1f));
        _labelGreen   = MDT_UITheme.MakeLabel(11, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextGreen);
        _labelRed     = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.UpperLeft,  MDT_UITheme.TextRed);
        _labelWhite   = MDT_UITheme.MakeLabel(12, FontStyle.Bold,   TextAnchor.UpperLeft,  MDT_UITheme.TextWhite);
        _labelHeading = MDT_UITheme.MakeLabel(12, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);
        _labelRoute   = MDT_UITheme.MakeLabel(12, FontStyle.Bold,   TextAnchor.MiddleLeft, MDT_UITheme.TextCyan);

        // Stop pill styles (formerly StopHUD.EnsureStyles)
        _pillTitle   = MDT_UITheme.MakeLabel(14, FontStyle.Bold,   TextAnchor.MiddleCenter, MDT_UITheme.TextCyan);
        _pillBody    = MDT_UITheme.MakeLabel(13, FontStyle.Normal, TextAnchor.MiddleCenter, MDT_UITheme.TextPrimary);
        _pillBracket = MDT_UITheme.MakeLabel(13, FontStyle.Bold,   TextAnchor.MiddleCenter, MDT_UITheme.TextAmber);
        // ADA/wheelchair -- blue, per spec ("they are blue"). No mesh/visual
        // pax type exists yet, so this text color is the only place that
        // distinction currently shows up.
        _pillAda     = MDT_UITheme.MakeLabel(13, FontStyle.Bold,   TextAnchor.MiddleCenter, new Color(0.40f, 0.62f, 1.0f));

        // Buttons
        _btnPrimary  = MDT_UITheme.MakeButton(MDT_UITheme.BGDirSel, MDT_UITheme.TextCyan,   10);
        _btnSecond   = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 10);
        _btnDisabled = MDT_UITheme.MakeButton(MDT_UITheme.BGMid,    MDT_UITheme.TextDim,    10);
        _btnDanger   = MDT_UITheme.MakeButton(new Color(0.4f, 0.05f, 0.05f, 1f), MDT_UITheme.TextRed,   10, FontStyle.Bold);
        _btnSuccess  = MDT_UITheme.MakeButton(new Color(0.05f, 0.3f, 0.12f, 1f), MDT_UITheme.TextGreen, 10, FontStyle.Bold);

        // Small rounded icon buttons for the header (help, gauges toggle, etc).
        _btnIcon = MDT_UITheme.MakeButton(MDT_UITheme.BGButton, MDT_UITheme.TextSecond, 12);
        MDT_UITheme.SetBgRounded(_btnIcon, MDT_UITheme.BGButton, MDT_UITheme.BGButton * 1.35f, MDT_UITheme.BGButton * 0.7f, 32, 24, 8f);
        _btnIconSel = MDT_UITheme.MakeButton(MDT_UITheme.BGDirSel, MDT_UITheme.TextGreen, 12);
        MDT_UITheme.SetBgRounded(_btnIconSel, MDT_UITheme.BGDirSel, MDT_UITheme.BGDirSel * 1.35f, MDT_UITheme.BGDirSel * 0.7f, 32, 24, 8f);

        _gaugeLabelStyle = MDT_UITheme.MakeLabel(9, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextWhite);

        _helpCmd  = MDT_UITheme.MakeLabel(12, FontStyle.Bold,   TextAnchor.UpperLeft, MDT_UITheme.TextCyan);
        _helpDesc = MDT_UITheme.MakeLabel(10, FontStyle.Normal, TextAnchor.UpperLeft, MDT_UITheme.TextDim);

        _inputStyle = new GUIStyle(GUI.skin.textField)
        {
            fontSize  = 11,
            fontStyle = FontStyle.Normal,
            alignment = TextAnchor.MiddleLeft,
        };
        _inputStyle.normal.textColor   = MDT_UITheme.TextPrimary;
        _inputStyle.focused.textColor  = MDT_UITheme.TextWhite;
        _inputStyle.normal.background  = MDT_UITheme.Tex(MDT_UITheme.BGDeep);
        _inputStyle.focused.background = MDT_UITheme.Tex(MDT_UITheme.BGMid);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    private GUIStyle TagToStyle(string tag) => tag switch
    {
        "warn"    => _labelAmber,
        "error"   => _labelRed,
        "success" => _labelGreen,
        "system"  => _labelCyan,
        "board"   => _labelBoard,
        _         => _labelSecond,
    };

    private Color StateLEDColor(PlayerHandoff.PlayerShiftState s) => s switch
    {
        PlayerHandoff.PlayerShiftState.InService          => MDT_UITheme.LEDGreen,
        PlayerHandoff.PlayerShiftState.WaitingToDepart    => MDT_UITheme.LEDAmber,
        PlayerHandoff.PlayerShiftState.ArrivedAtTerminal  => MDT_UITheme.LEDAmber,
        PlayerHandoff.PlayerShiftState.DeadrunToStart     => MDT_UITheme.LEDAmber,
        PlayerHandoff.PlayerShiftState.ReliefPending      => MDT_UITheme.LEDRed,
        PlayerHandoff.PlayerShiftState.Breakdown          => MDT_UITheme.LEDRed,
        _                                                  => MDT_UITheme.LEDOff,
    };

    // [ADD] Shape-redundant accessibility encoding, paired with StateLEDColor
    // above -- circle for the "good/running" states, triangle for
    // "caution/waiting", square for "alert" -- see MDT_UITheme.DrawStatusIndicator.
    private MDT_UITheme.StatusShape StateLEDShape(PlayerHandoff.PlayerShiftState s) => s switch
    {
        PlayerHandoff.PlayerShiftState.InService          => MDT_UITheme.StatusShape.Circle,
        PlayerHandoff.PlayerShiftState.WaitingToDepart    => MDT_UITheme.StatusShape.Triangle,
        PlayerHandoff.PlayerShiftState.ArrivedAtTerminal  => MDT_UITheme.StatusShape.Triangle,
        PlayerHandoff.PlayerShiftState.DeadrunToStart     => MDT_UITheme.StatusShape.Triangle,
        PlayerHandoff.PlayerShiftState.ReliefPending      => MDT_UITheme.StatusShape.Square,
        PlayerHandoff.PlayerShiftState.Breakdown          => MDT_UITheme.StatusShape.Square,
        _                                                  => MDT_UITheme.StatusShape.Circle,
    };

    private string StateDisplayName(PlayerHandoff.PlayerShiftState s) => s switch
    {
        PlayerHandoff.PlayerShiftState.OffDuty            => "Off Duty",
        PlayerHandoff.PlayerShiftState.DeadrunToStart     => "Dead Run",
        PlayerHandoff.PlayerShiftState.WaitingToDepart    => "Awaiting Dep.",
        PlayerHandoff.PlayerShiftState.InService          => "In Service",
        PlayerHandoff.PlayerShiftState.ArrivedAtTerminal  => "At Terminal",
        PlayerHandoff.PlayerShiftState.ReliefPending      => "Relief Pending",
        PlayerHandoff.PlayerShiftState.Relieved           => "Relieved",
        PlayerHandoff.PlayerShiftState.Breakdown          => "Breakdown",
        _                                                  => s.ToString(),
    };

    internal static void ParseRouteAndVariant(string input, out string route, out string variant)
        => PlayerHandoff.ParseRouteAndVariant(input, out route, out variant);
}

// ─────────────────────────────────────────────────────────────────────────────
//  STOP HUD  —  compatibility shim (MonoBehaviour so Unity scenes don't break)
//  StopHUD logic is now inside DriverConsole. Any GameObject that still carries
//  a StopHUD component will just self-remove the component on Awake.
//  All callers (e.g. StopHUD.Instance?.Refresh()) continue to compile unchanged.
// ─────────────────────────────────────────────────────────────────────────────
public class StopHUD : MonoBehaviour
{
    public static DriverConsole Instance => DriverConsole.Instance;
    private void Awake() => Destroy(this); // hand off to DriverConsole; nothing else to do
    public void Refresh() { }              // no-op; kept so call sites compile
}