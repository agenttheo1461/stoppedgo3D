using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  GAME MODE — Drive (the normal game) or Manage (the Route Manager).
//
//  In Manage mode the player can't drive or walk anywhere. Everything that belongs
//  to driving is switched off, the 3D view and the engine sounds are silenced, and
//  the only things left are the NPC buses running the timetable and the manager
//  screen. The pause/main menu still works, and it's how you switch back.
//
//  Anything that already respects MainMenu.BlocksInput (debug keys, map and tracker
//  hotkeys, the shift board key…) is blocked too, because BlocksInput is true in
//  Manage mode.
// ═══════════════════════════════════════════════════════════════════════════════
public enum GameModeKind { Drive, Manage }

public static class GameMode
{
    public static GameModeKind Current { get; internal set; } = GameModeKind.Drive;
    public static bool IsManage => Current == GameModeKind.Manage;
    public static event Action<GameModeKind> Changed;

    internal static void Raise() => Changed?.Invoke(Current);

    public static void EnterManage()  => ManagerMode.Ensure().Enter();
    public static void ExitToDrive()  { if (ManagerMode.Instance != null) ManagerMode.Instance.Exit(); }
}

public class ManagerMode : MonoBehaviour
{
    public static ManagerMode Instance { get; private set; }

    public static ManagerMode Ensure()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("RouteManager");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<ManagerMode>();
        go.AddComponent<ManagerUI>();
        return Instance;
    }

    private readonly List<Behaviour> _switchedOff = new List<Behaviour>();
    private readonly List<Camera>    _camerasOff  = new List<Camera>();
    private float _savedVolume = 1f;
    private bool  _savedCursorVisible;
    private CursorLockMode _savedLock;
    private bool  _busy;

    // The one camera that stays on (Unity shows a "no camera rendering" warning if none do).
    private Camera _keepCam; private int _keepMask; private CameraClearFlags _keepFlags; private Color _keepBg;

    private float _actTick;

    private void Awake()
    {
        // Put the saved plan back on the timetable once the scheduler has settled, not during its start-up.
        StartCoroutine(ApplySavedPlanLater());
    }

    private IEnumerator ApplySavedPlanLater()
    {
        yield return new WaitForSecondsRealtime(25f);
        for (int tries = 0; tries < 20 && (BusScheduler.Instance == null || BusManager.Instance == null || SimClock.Instance == null); tries++)
            yield return new WaitForSecondsRealtime(2f);
        try { ManagerPlan.ApplyAll(out _, out _); }
        catch (Exception e) { Debug.LogWarning("[RouteManager] Couldn't apply the saved plan: " + e.Message); }
    }

    private void Update()
    {
        ManagerScore.Tick();
        _actTick -= Time.unscaledDeltaTime;
        if (_actTick <= 0f) { _actTick = 1f; ManagerActions.Tick(); }
        if (GameMode.IsManage) AudioListener.volume = 0f; // nothing else may turn the sound back up
    }

    // ── Enter ────────────────────────────────────────────────────────────────
    public void Enter()
    {
        if (_busy || GameMode.IsManage) return;
        _busy = true;
        LoadingScreen.Run(ManagerWords.ModeName, EnterSteps());
    }

    private IEnumerator EnterSteps()
    {
        LoadingScreen.Set(0.08f, "Closing the menu");
        yield return null;
        MainMenu.Instance?.Close();

        LoadingScreen.Set(0.22f, "Ending your shift");
        yield return null;
        PlayerHandoff.Instance?.EndShiftFully();

        LoadingScreen.Set(0.40f, "Switching off driving");
        yield return null;
        SwitchOffDriving();

        LoadingScreen.Set(0.58f, "Loading your saved schedules");
        yield return null;
        ManagerPlan.ApplyAll(out int applied, out var problems);
        ManagerUI.PostNote(applied > 0 ? $"Put {applied} saved trip{(applied == 1 ? "" : "s")} back on the timetable." : null,
                           problems.Count > 0 ? problems.Count + " saved trip(s) couldn't be placed. See the Plan tab." : null);

        LoadingScreen.Set(0.78f, "Drawing the map");
        yield return null;
        MDT_LiveMap.Instance?.ManagerBegin();

        LoadingScreen.Set(0.95f, "Almost there");
        yield return null;

        GameMode.Current = GameModeKind.Manage;
        GameMode.Raise();
        _busy = false;
    }

    // ── Exit ─────────────────────────────────────────────────────────────────
    public void Exit()
    {
        if (_busy || !GameMode.IsManage) return;
        _busy = true;
        LoadingScreen.Run("Back to driving", ExitSteps());
    }

    private IEnumerator ExitSteps()
    {
        LoadingScreen.Set(0.2f, "Putting driving back");
        yield return null;
        MDT_LiveMap.Instance?.ManagerEnd();
        SwitchOnDriving();

        LoadingScreen.Set(0.7f, "Opening the main menu");
        yield return null;
        GameMode.Current = GameModeKind.Drive;
        GameMode.Raise();
        ManagerActions.ClearUndo();
        MainMenu.Instance?.Open();
        _busy = false;
    }

    /// <summary>Leave Manage mode instantly (no loading screen, no menu). Used when PLAY is pressed in the main menu,
    /// so the normal route/bus claim that follows runs with driving switched back on.</summary>
    public void ExitForPlay()
    {
        if (!GameMode.IsManage) return;
        MDT_LiveMap.Instance?.ManagerEnd();
        SwitchOnDriving();
        GameMode.Current = GameModeKind.Drive;
        GameMode.Raise();
        ManagerActions.ClearUndo();
    }

    // ── What gets switched off ───────────────────────────────────────────────
    private void Off<T>() where T : Behaviour
    {
        foreach (var c in FindObjectsByType<T>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (c != null && c.enabled) { c.enabled = false; _switchedOff.Add(c); }
    }

    private void SwitchOffDriving()
    {
        _switchedOff.Clear(); _camerasOff.Clear();

        // Close any window that could still be open.
        ShiftBoardMenu.Instance?.Close();
        DispatchConsole.Instance?.Close();
        MDT_LiveMap.Instance?.Close();
        TimetableOverlay.Instance?.Close();
        RouteSnapshotViewerUI.Instance?.Close();
        ShiftMakerWindow.Instance?.Close();
        BusRosterWindow.Instance?.Close();
        SettingsWindow.Instance?.Close();

        // Driving and the screens that belong to it.
        Off<PlayerHandoff>();
        Off<ShiftRunner>();
        Off<CameraFollow25D>();
        Off<BusDashboardHUD>();
        Off<DriverConsole>();
        Off<StopHUD>();
        Off<DispatchConsole>();
        Off<MobileTouchHUD>();
        Off<ShiftHUD>();
        Off<ShiftEventOverlay>();
        Off<ShiftBoardMenu>();
        Off<ShiftSummaryCard>();
        Off<TimetableOverlay>();
        Off<AlertsCenterWindow>();
        Off<LanConnectHUD>();
        Off<MDT_UI_Controller>();

        // Nothing is drawn in 3D. One camera stays on but sees nothing, so Unity doesn't complain about having none.
        _keepCam = Camera.main != null ? Camera.main : null;
        foreach (var cam in Camera.allCameras)
        {
            if (cam == null || !cam.enabled) continue;
            if (_keepCam == null) _keepCam = cam;
            if (cam == _keepCam) continue;
            cam.enabled = false; _camerasOff.Add(cam);
        }
        if (_keepCam != null)
        {
            _keepMask = _keepCam.cullingMask; _keepFlags = _keepCam.clearFlags; _keepBg = _keepCam.backgroundColor;
            _keepCam.cullingMask = 0;
            _keepCam.clearFlags = CameraClearFlags.SolidColor;
            _keepCam.backgroundColor = new Color(0.115f, 0.205f, 0.215f, 1f);
        }

        // No sound, and every bus drops to its cheapest state (no drawing, no audio, slowest update).
        _savedVolume = AudioListener.volume;
        AudioListener.volume = 0f;
        AudioListener.pause = true;
        NPCBusController.ManagerFarMode = true;

        _savedCursorVisible = Cursor.visible; _savedLock = Cursor.lockState;
        Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
    }

    private void SwitchOnDriving()
    {
        foreach (var c in _switchedOff) if (c != null) c.enabled = true;
        _switchedOff.Clear();
        foreach (var cam in _camerasOff) if (cam != null) cam.enabled = true;
        _camerasOff.Clear();
        if (_keepCam != null) { _keepCam.cullingMask = _keepMask; _keepCam.clearFlags = _keepFlags; _keepCam.backgroundColor = _keepBg; _keepCam = null; }
        NPCBusController.ManagerFarMode = false;
        AudioListener.pause = false;
        AudioListener.volume = _savedVolume <= 0f ? 1f : _savedVolume;
        Cursor.visible = _savedCursorVisible; Cursor.lockState = _savedLock;
    }
}
