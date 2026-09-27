using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  MOBILE TOUCH HUD  —  v1.0
//
//  Touch-first control layer for mobile builds: a variable-intensity brake
//  pedal (bottom-left) and gas pedal (bottom-right) you can slide up/down
//  while holding, momentary LEFT/RIGHT turn buttons that behave like holding
//  A/D, a HONK button, and a top toggle strip for the four big overlays
//  (Shift Board, Bus Select, Timetable, Live Map) since none of those have
//  an on-screen way to open on a touch device otherwise.
//
//  Visual language matches BusDashboardHUD / ShiftEventOverlay: rounded
//  panels via MDT_UITheme, soft shadow under anything that floats over the
//  3D view, themed text colors, no ad-hoc GUI.Box calls.
//
//  MULTI-TOUCH: OnGUI's Event.current only ever reports ONE simulated
//  pointer on mobile, which is exactly wrong for a control scheme where you
//  need gas + steering held down at the same time. So input here is read
//  directly off Input.touches (falling back to the mouse for in-editor
//  testing) in Update(), independently of OnGUI's draw pass — each active
//  finger is matched against whichever control rect it started in and
//  tracked by its fingerId until it lifts.
//
//  ⚠ TWO ASSUMED FIELD NAMES — please check these compile / do the right
//  thing against your actual BusSimulationController (that script wasn't
//  among the files I was given, so these are inferred, not confirmed):
//    • bus.bkPd    — CONFIRMED. Used as-is by BusDashboardHUD/BusSelectMenu.
//    • bus.gasPd   — ASSUMED, by symmetry with "bkPd" (brake pedal → gas
//                    pedal). Rename the one line below if it doesn't match.
//    • steering    — UNKNOWN. No steer/throttle-axis field showed up
//                    anywhere in the 11 files I read, which means A/D
//                    steering is most likely read straight off
//                    Input.GetKey(KeyCode.A/D) *inside* BusSimulationController
//                    itself, with no public field to poke from outside.
//                    The turn buttons below compute a clean -1/0/+1
//                    _steerAxis every frame either way — wire it in
//                    ApplyToBus() once you tell me (or paste) the real
//                    steering entry point.
// ─────────────────────────────────────────────────────────────────────────────
public class MobileTouchHUD : MonoBehaviour
{
    public static MobileTouchHUD Instance { get; private set; }

    public bool showMobileControls = true;
    public BusSimulationController bus;

    // ── Layout tuning ────────────────────────────────────────────────────
    [Header("Pedals")]
    public float pedalWidth  = 72f;
    public float pedalHeight = 230f;
    public float pedalMarginSide   = 20f;
    public float pedalMarginBottom = 90f;

    [Header("Turn buttons")]
    public float turnBtnSize = 66f;

    [Header("Top toggle strip")]
    public float toggleBtnW = 76f;
    public float toggleBtnH = 38f;
    public float toggleMarginTop = 14f;

    [Header("Camera Controls")]
    public float camBtnW = 64f;
    public float camBtnH = 38f;
    public float touchLookDragSensitivity = 0.08f;
    public float mouseLookDragSensitivity = 0.15f;
    public bool invertLookY = false;
    private CameraFollow25D _camera;

    // [ADD] Items 15/16 -- was this component's own private copy of this
    // exact formula; now shared via MDT_UITheme so DriverConsole/
    // BusDashboardHUD/MDT_LiveMap scale identically instead of each having
    // (or not having) their own copy. minUIScale/maxUIScale Inspector
    // fields kept for backward compatibility with any tuned values, but the
    // shared MDT_UITheme.MinUIScale/MaxUIScale are what actually apply now.
    [Header("Mobile UI Scale")]
    public float minUIScale = 0.9f, maxUIScale = 2.4f;
    private float UIScale => MDT_UITheme.UIScale;

    private bool _stylesReady;
    private float _lastStyleScale;
    private GUIStyle _lblPedalName, _lblPedalPct, _lblToggle, _lblTurn, _lblHonk;

    private const int NONE = int.MinValue;
    private int   _brakeFinger = NONE, _accelFinger = NONE;
    private int   _leftFinger  = NONE, _rightFinger  = NONE;
    private float _brakeIntensity, _accelIntensity;
    private bool  _turnLeftHeld, _turnRightHeld;
    private bool  _busToggleTriggeredByTouch;

    private struct Pointer { public int id; public Vector2 pos; public bool justDown; public bool held; public bool justUp; }

    private bool _autoDetectApplied;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        if (!_autoDetectApplied)
        {
            _autoDetectApplied = true;
            showMobileControls = Application.isMobilePlatform;
        }
    }

    private void Update()
    {
        ResolveActiveBus();
        if (_camera == null && Camera.main != null) _camera = Camera.main.GetComponent<CameraFollow25D>();
        _busToggleTriggeredByTouch = false;

        var pointers = GatherPointers();

        // Pause button + its strip (and the Main Menu warning) work on PC
        // too -- clickable with the mouse -- so they run before the
        // mobile-only gate.
        UpdatePauseMenuTaps(pointers);
        if (_confirmMainMenu) { ResetDrivingPointers(); return; }

        if (!showMobileControls) return;

        // [ADD] Honk + camera-mode buttons now go through the same manual
        // per-touch tap detection TrackBusToggle already used correctly --
        // NOT GUI.Button, which is what was causing the midpoint phantom-
        // press bug (IMGUI only ever synthesizes ONE pointer position from
        // however many fingers are actually down, per this file's own
        // header note -- two real touches could produce a synthesized
        // position landing between them, triggering whatever rect happens
        // to sit at that midpoint).
        TrackTapAndFire(pointers, HonkRect(), () => { if (bus != null) BusSelectMenu.Instance?.TriggerHonk(); });
        if (_camera != null)
        {
            TrackTapAndFire(pointers, OrbitCamRect(), _camera.ToggleOrbitMode);
            TrackTapAndFire(pointers, FirstPersonCamRect(), _camera.SetFirstPersonMode);
        }
        // [FIX] SHIFT/TIME/MAP previously fired through DrawToggleButton's
        // own internal GUI.Button call inside OnGUI -- only BUS (via
        // TrackBusToggle above) was ever routed through the safe manual-
        // touch path. Consolidated: all four now fire here, uniformly,
        // through the same per-touch-id tap detection. DrawToggleButton is
        // now purely visual (see suppressGuiClick=true on every call).

        // [ADD] Drag-to-look -- feeds real per-touch deltaPosition into
        // CameraFollow25D.FeedLookDelta() instead of Input.GetAxis("Mouse
        // X"/"Mouse Y"), which is a desktop mouse-delta value that doesn't
        // behave correctly under touch-to-mouse simulation. Only tracks a
        // touch that STARTED outside every other control's rect (pedals,
        // turn buttons, toggle strip, honk, cam buttons) so dragging a
        // pedal or holding a turn button can never also spin the camera.
        TrackLookDrag(pointers);

        if (bus == null)
        {
            ResetDrivingPointers();
            return;
        }

        var brakeRect = BrakeRect();
        var accelRect = AccelRect();
        var leftRect  = TurnLeftRect();
        var rightRect = TurnRightRect();

        TrackPedal(pointers, brakeRect, ref _brakeFinger, ref _brakeIntensity);
        TrackPedal(pointers, accelRect, ref _accelFinger, ref _accelIntensity);
        TrackHold(pointers, leftRect,  ref _leftFinger,  ref _turnLeftHeld);
        TrackHold(pointers, rightRect, ref _rightFinger, ref _turnRightHeld);

        ApplyToBus();
    }

    private void TrackTapAndFire(List<Pointer> pointers, Rect rect, System.Action action)
    {
        foreach (var p in pointers)
        {
            if (!p.justDown || !rect.Contains(p.pos)) continue;
            action?.Invoke();
            return;
        }
    }

    private int    _lookFinger = NONE;
    private Vector2 _lookLastPos;

    private void TrackLookDrag(List<Pointer> pointers)
    {
        if (_camera == null) return;

        bool stillTracking = false;
        foreach (var p in pointers)
        {
            if (_lookFinger == NONE && p.justDown && !IsOverAnyControl(p.pos))
            {
                _lookFinger  = p.id;
                _lookLastPos = p.pos;
            }

            if (p.id == _lookFinger)
            {
                stillTracking = true;
                if (p.justUp) { _lookFinger = NONE; return; }
                Vector2 delta = p.pos - _lookLastPos;
                _lookLastPos = p.pos;
                if (invertLookY) delta.y = -delta.y;
                float sens = p.id == -1 ? mouseLookDragSensitivity : touchLookDragSensitivity;
                _camera.FeedLookDelta(delta * sens / Mathf.Max(0.5f, UIScale));
            }
        }
        if (!stillTracking && _lookFinger != NONE) _lookFinger = NONE;
    }

    private bool IsOverAnyControl(Vector2 pos)
    {
        if (bus != null)
        {
            if (BrakeRect().Contains(pos) || AccelRect().Contains(pos)) return true;
            if (TurnLeftRect().Contains(pos) || TurnRightRect().Contains(pos)) return true;
            if (HonkRect().Contains(pos)) return true;
        }
        if (_camera != null && (OrbitCamRect().Contains(pos) || FirstPersonCamRect().Contains(pos))) return true;
        if (PauseRect().Contains(pos)) return true;
        if (_pauseOpen) foreach (var r in ToggleRects()) if (r.Contains(pos)) return true;
        if (_keysOpen && KeysPanelRect().Contains(pos)) return true;
        if (_confirmMainMenu) return true;
        return false;
    }

    private void TrackBusToggle(List<Pointer> pointers, Rect rect)
    {
        foreach (var p in pointers)
        {
            if (!p.justDown || !rect.Contains(p.pos)) continue;
            BusSelectMenu.Instance?.ToggleMenu();
            _busToggleTriggeredByTouch = true;
            return;
        }
    }

    private void ResetDrivingPointers()
    {
        _brakeFinger = NONE;
        _accelFinger = NONE;
        _leftFinger = NONE;
        _rightFinger = NONE;
        _brakeIntensity = 0f;
        _accelIntensity = 0f;
        _turnLeftHeld = false;
        _turnRightHeld = false;
    }

    private void ResolveActiveBus()
    {
        var menu = BusSelectMenu.Instance;
        var live = menu != null ? menu.ActiveBus : null;
        if (live == null || !live.enabled)
        {
            var handoff = PlayerHandoff.Instance;
            live = handoff != null ? handoff.playerBus : null;
            if ((live == null || !live.enabled) && handoff != null)
            {
                foreach (var childBus in handoff.GetComponentsInChildren<BusSimulationController>(true))
                {
                    if (childBus != null && childBus.enabled)
                    {
                        live = childBus;
                        break;
                    }
                }
            }
        }
        bus = live != null && live.enabled ? live : null;
    }

    private void ApplyToBus()
    {
        if (bus == null) return;

        bus.bkPd  = _brakeIntensity;
        bus.gasPd = _accelIntensity;

        float steerAxis = (_turnRightHeld ? 1f : 0f) - (_turnLeftHeld ? 1f : 0f);
        bus.mobileSteerInput = steerAxis;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  POINTER HANDLING (touch-first, mouse fallback for editor testing)
    // ═════════════════════════════════════════════════════════════════════
    private readonly List<Pointer> _pointerBuf = new();

    private List<Pointer> GatherPointers()
    {
        _pointerBuf.Clear();

        if (Input.touchCount > 0)
        {
            foreach (var t in Input.touches)
            {
                var guiPos = new Vector2(t.position.x, Screen.height - t.position.y);
                bool up   = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
                bool down = t.phase == TouchPhase.Began;
                _pointerBuf.Add(new Pointer { id = t.fingerId, pos = guiPos, justDown = down, held = !up, justUp = up });
            }
            return _pointerBuf;
        }

        // Editor / desktop fallback — single mouse pointer, id = -1.
        if (Input.GetMouseButton(0) || Input.GetMouseButtonUp(0))
        {
            var mp = Input.mousePosition;
            var guiPos = new Vector2(mp.x, Screen.height - mp.y);
            _pointerBuf.Add(new Pointer
            {
                id = -1,
                pos = guiPos,
                justDown = Input.GetMouseButtonDown(0),
                held = Input.GetMouseButton(0),
                justUp = Input.GetMouseButtonUp(0)
            });
        }
        return _pointerBuf;
    }

    /// <summary>Variable-intensity pedal: while the tracked finger stays
    /// down, intensity follows how far up the rect it's dragged (0 at the
    /// bottom edge, 1 at the top). Lets go → snaps back to 0, same as a
    /// real spring-loaded pedal.</summary>
    private void TrackPedal(List<Pointer> pointers, Rect rect, ref int trackedFinger, ref float intensity)
    {
        bool stillTracking = false;
        foreach (var p in pointers)
        {
            if (trackedFinger == NONE && p.justDown && rect.Contains(p.pos))
                trackedFinger = p.id;

            if (p.id == trackedFinger)
            {
                stillTracking = true;
                if (p.justUp) { trackedFinger = NONE; intensity = 0f; return; }
                float t = Mathf.InverseLerp(rect.yMax, rect.y, p.pos.y);
                intensity = Mathf.Clamp01(t);
            }
        }
        if (!stillTracking && trackedFinger != NONE) { trackedFinger = NONE; intensity = 0f; }
    }

    /// <summary>Momentary hold button (turn L/R): held true for exactly as
    /// long as the tracked finger stays down inside the rect.</summary>
    private void TrackHold(List<Pointer> pointers, Rect rect, ref int trackedFinger, ref bool held)
    {
        bool stillTracking = false;
        foreach (var p in pointers)
        {
            if (trackedFinger == NONE && p.justDown && rect.Contains(p.pos))
                trackedFinger = p.id;

            if (p.id == trackedFinger)
            {
                stillTracking = true;
                if (p.justUp) { trackedFinger = NONE; held = false; return; }
                held = true;
            }
        }
        if (!stillTracking && trackedFinger != NONE) { trackedFinger = NONE; held = false; }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  LAYOUT — every rect scaled by UIScale so sizing/position holds up
    //  across real device resolutions instead of the old fixed pixel values
    //  that were tuned for one specific screen and ran too small (or
    //  off-screen on the margins) on anything else.
    // ═════════════════════════════════════════════════════════════════════
    private Rect BrakeRect()
    {
        float s = UIScale;
        float w = pedalWidth * s, h = pedalHeight * s, mSide = pedalMarginSide * s, mBot = pedalMarginBottom * s;
        return new Rect(Screen.width - mSide - w * 2f - 12f * s, Screen.height - mBot - h, w, h);
    }

    private Rect AccelRect()
    {
        float s = UIScale;
        float w = pedalWidth * s, h = pedalHeight * s, mSide = pedalMarginSide * s, mBot = pedalMarginBottom * s;
        return new Rect(Screen.width - mSide - w, Screen.height - mBot - h, w, h);
    }

    private Rect TurnLeftRect()
    {
        float s = UIScale;
        float size = turnBtnSize * s;
        return new Rect(pedalMarginSide * s, Screen.height - pedalMarginBottom * s - size - 40f * s, size, size);
    }

    private Rect TurnRightRect()
    {
        float s = UIScale;
        float size = turnBtnSize * s;
        return new Rect(pedalMarginSide * s + size + 12f * s, Screen.height - pedalMarginBottom * s - size - 40f * s, size, size);
    }

    private Rect HonkRect()
    {
        float s = UIScale;
        return new Rect(Screen.width * 0.5f - 26f * s, Screen.height - pedalMarginBottom * s - turnBtnSize * s - 100f * s, 52f * s, 40f * s);
    }

    // [ADD] Camera mode buttons -- sit just above the honk button, centered,
    // side by side. No fixed equivalent existed anywhere before this.
    private Rect OrbitCamRect()
    {
        float s = UIScale;
        return new Rect(Screen.width * 0.5f - camBtnW * s - 4f * s,
                         Screen.height - pedalMarginBottom * s - turnBtnSize * s - 150f * s, camBtnW * s, camBtnH * s);
    }

    private Rect FirstPersonCamRect()
    {
        float s = UIScale;
        return new Rect(Screen.width * 0.5f + 4f * s,
                         Screen.height - pedalMarginBottom * s - turnBtnSize * s - 150f * s, camBtnW * s, camBtnH * s);
    }

    // ── Pause button + strip ─────────────────────────────────────────────
    // Strip order: 0 SHIFT BOARD, 1 MAIN MENU, 2 MAP, 3 TRACKER, 4 TIME, 5 KEYS
    private const int StripCount = 7;

    private Rect PauseRect()
    {
        float s = UIScale;
        float w = 84f * s, h = toggleBtnH * s;
        return new Rect(Screen.width - 16f * s - w, toggleMarginTop * s, w, h);
    }

    /// <summary>The strip sits to the LEFT of the pause button, right-aligned to it.</summary>
    private Rect[] ToggleRects()
    {
        float s = UIScale;
        float w = toggleBtnW * 1.3f * s, h = toggleBtnH * s, gap = 8f * s;
        var pr = PauseRect();
        var rects = new Rect[StripCount];
        for (int i = 0; i < StripCount; i++)
            rects[i] = new Rect(pr.x - 10f * s - (StripCount - i) * (w + gap) + gap, pr.y, w, h);
        return rects;
    }

    private Rect ConfirmBoxRect()
    {
        float s = UIScale; float w = 420f * s, h = 190f * s;
        return new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
    }
    private Rect ConfirmCancelRect() { var b = ConfirmBoxRect(); float s = UIScale; return new Rect(b.x + 20f * s, b.yMax - 58f * s, (b.width - 60f * s) * 0.5f, 40f * s); }
    private Rect ConfirmOkRect()     { var b = ConfirmBoxRect(); float s = UIScale; var c = ConfirmCancelRect(); return new Rect(c.xMax + 20f * s, c.y, c.width, c.height); }

    private Rect KeysPanelRect()
    {
        float s = UIScale;
        var pr = PauseRect();
        float w = 320f * s;
        float h = Mathf.Min(Screen.height - pr.yMax - 30f * s, Screen.height * 0.7f);
        return new Rect(Screen.width - 16f * s - w, pr.yMax + 12f * s, w, h);
    }

    private void UpdatePauseMenuTaps(List<Pointer> pointers)
    {
        if (_confirmMainMenu)
        {
            TrackTapAndFire(pointers, ConfirmCancelRect(), () => _confirmMainMenu = false);
            TrackTapAndFire(pointers, ConfirmOkRect(), () =>
            {
                _confirmMainMenu = false; _pauseOpen = false; _keysOpen = false;
                MainMenu.Instance?.Open();
            });
            return;
        }

        TrackTapAndFire(pointers, PauseRect(), () => { _pauseOpen = !_pauseOpen; if (!_pauseOpen) _keysOpen = false; });
        if (!_pauseOpen) return;

        var r = ToggleRects();
        TrackTapAndFire(pointers, r[0], () => ShiftBoardMenu.Instance?.HandleTogglePressed());
        TrackTapAndFire(pointers, r[1], () => _confirmMainMenu = true);
        TrackTapAndFire(pointers, r[2], () => MDT_LiveMap.Instance?.HandleTogglePressed());
        TrackTapAndFire(pointers, r[3], () =>
        {
            if (_tracker == null) _tracker = FindObjectOfType<MDT_UI_Controller>();
            _tracker?.HandleTogglePressed();
        });
        TrackTapAndFire(pointers, r[4], () => TimetableOverlay.Instance?.HandleTogglePressed());
        TrackTapAndFire(pointers, r[5], () => _keysOpen = !_keysOpen);
        TrackTapAndFire(pointers, r[6], () => SettingsWindow.Instance?.Toggle());
    }

    private bool _pauseOpen, _confirmMainMenu, _keysOpen;
    private Vector2 _keysScroll;
    private MDT_UI_Controller _tracker;

    private void OnGUI()
    {
        ResolveActiveBus();
        EnsureStyles();
        // PC: only the top toggle strip is shown; pedals/turn/honk/cam stay mobile-only.
        if (!showMobileControls) { DrawPauseUI(); return; }
        if (Input.touchCount >= 2 && Event.current != null)
        {
            var et = Event.current.type;
            if (et == EventType.MouseDown || et == EventType.MouseUp || et == EventType.MouseDrag)
                Event.current.Use();
        }

        if (bus != null)
        {
            DrawPedal(BrakeRect(), "BRAKE", _brakeIntensity, MDT_UITheme.TextRed);
            DrawPedal(AccelRect(), "GAS",   _accelIntensity, MDT_UITheme.TextGreen);

            DrawTurnButton(TurnLeftRect(),  "◄", _turnLeftHeld);
            DrawTurnButton(TurnRightRect(), "►", _turnRightHeld);

            DrawHonkButton(HonkRect());
        }

        if (_camera != null)
        {
            DrawToggleButton(OrbitCamRect(),       "3RD", _camera.IsOrbitMode,       null, suppressGuiClick: true);
            DrawToggleButton(FirstPersonCamRect(), "1ST", _camera.IsFirstPersonMode, null, suppressGuiClick: true);
        }

        DrawPauseUI();
    }

    private void DrawPedal(Rect rect, string label, float intensity, Color accent)
    {
        MDT_UITheme.DrawSoftShadow(rect, 14f, 5f, 10f);
        MDT_UITheme.DrawRoundedRectBordered(rect, 14f, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);
        if (intensity > 0.001f)
        {
            float fillH = rect.height * intensity;
            var fillRect = new Rect(rect.x + 4f, rect.yMax - fillH - 4f, rect.width - 8f, fillH - 4f);
            if (fillRect.height > 0f)
                MDT_UITheme.DrawRoundedRect(fillRect, 10f, new Color(accent.r, accent.g, accent.b, 0.55f));
        }

        GUI.Label(new Rect(rect.x, rect.y + 8f, rect.width, 20f), label, _lblPedalName);
        GUI.Label(new Rect(rect.x, rect.yMax - 26f, rect.width, 20f), $"{Mathf.RoundToInt(intensity * 100f)}%", _lblPedalPct);
    }

    private void DrawTurnButton(Rect rect, string glyph, bool held)
    {
        Color bg = held ? new Color(0.20f, 0.45f, 0.55f, 1f) : MDT_UITheme.BGButton;
        Color border = held ? new Color(0.4f, 0.85f, 1f, 0.85f) : MDT_UITheme.BorderAccent;
        MDT_UITheme.DrawSoftShadow(rect, rect.width * 0.5f, 3f, 6f);
        MDT_UITheme.DrawRoundedRectBordered(rect, rect.width * 0.5f, bg, border, held ? 2 : 1);
        GUI.Label(rect, glyph, _lblTurn);
    }

    private void DrawHonkButton(Rect rect)
    {
        MDT_UITheme.DrawRoundedRectBordered(rect, 8f, MDT_UITheme.BGButton, MDT_UITheme.BorderAccent, 1);
        GUI.Label(rect, "HONK", _lblHonk);
    }

    private void DrawPauseUI()
    {
        float sc = UIScale;
        DrawToggleButton(PauseRect(), _pauseOpen ? "PAUSE ▸" : "PAUSE", _pauseOpen, suppressGuiClick: true);

        if (_pauseOpen)
        {
            var rects = ToggleRects();
            DrawToggleButton(rects[0], "SHIFT BOARD", false, suppressGuiClick: true);
            DrawToggleButton(rects[1], "MAIN MENU",   false, suppressGuiClick: true);
            DrawToggleButton(rects[2], "MAP",     MDT_LiveMap.Instance != null && MDT_LiveMap.Instance.IsVisible, suppressGuiClick: true);
            if (_tracker == null) _tracker = FindObjectOfType<MDT_UI_Controller>();
            DrawToggleButton(rects[3], "TRACKER", _tracker != null && _tracker.IsOpen, suppressGuiClick: true);
            DrawToggleButton(rects[4], "TIME",    TimetableOverlay.Instance != null && TimetableOverlay.Instance.IsVisible, suppressGuiClick: true);
            DrawToggleButton(rects[5], "KEYS",    _keysOpen, suppressGuiClick: true);
            // [ADD] Settings doesn't touch shift state (unlike Main Menu),
            // so no confirmation dialog needed -- toggled directly like the
            // other slots.
            DrawToggleButton(rects[6], "SETTINGS", SettingsWindow.Instance != null && SettingsWindow.Instance.IsOpen, suppressGuiClick: true);
        }

        if (_keysOpen) DrawKeysPanel(sc);
        if (_confirmMainMenu) DrawMainMenuWarning(sc);
    }

    private void DrawMainMenuWarning(float sc)
    {
        // Dim everything behind, then the box.
        MDT_UITheme.DrawRect(new Rect(0, 0, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.55f));
        var b = ConfirmBoxRect();
        MDT_UITheme.DrawSoftShadow(b, 14f, 6f, 12f);
        MDT_UITheme.DrawRoundedRectBordered(b, 14f, MDT_UITheme.BGDeep, MDT_UITheme.TextRed, 2);
        GUI.Label(new Rect(b.x, b.y + 12f * sc, b.width, 28f * sc), "RETURN TO MAIN MENU?",
            MDT_UITheme.MakeLabel(Mathf.RoundToInt(16 * sc), FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextRed));
        GUI.Label(new Rect(b.x + 20f * sc, b.y + 46f * sc, b.width - 40f * sc, 64f * sc),
            "Warning: this resets everything -- your current shift, bus and progress this session will be lost.",
            MDT_UITheme.MakeLabel(Mathf.RoundToInt(12 * sc), FontStyle.Normal, TextAnchor.UpperCenter, MDT_UITheme.TextPrimary));
        DrawToggleButton(ConfirmCancelRect(), "CANCEL", false, suppressGuiClick: true);
        DrawToggleButton(ConfirmOkRect(), "MAIN MENU", true, suppressGuiClick: true);
    }

    // ── Keybind list (normal-use keys only; no debug keys) ───────────────
    private struct KeyRow { public string keys; public string action; public bool header; }

    private static string KeyLabel(KeyCode k)
    {
        switch (k)
        {
            case KeyCode.None: return "-";
            case KeyCode.LeftArrow: return "←";
            case KeyCode.RightArrow: return "→";
            case KeyCode.UpArrow: return "↑";
            case KeyCode.DownArrow: return "↓";
            case KeyCode.Escape: return "Esc";
            case KeyCode.Space: return "Space";
            case KeyCode.LeftShift: case KeyCode.RightShift: return "Shift";
            case KeyCode.LeftControl: case KeyCode.RightControl: return "Ctrl";
            case KeyCode.LeftBracket: return "[";
            case KeyCode.RightBracket: return "]";
        }
        string n = k.ToString();
        if (n.StartsWith("Alpha")) return n.Substring(5);
        return n;
    }

    private static readonly List<KeyRow> _keyRows = new List<KeyRow>();

    private static void BuildKeyRows()
    {
        var k = KeyBindings.Current;
        _keyRows.Clear();
        void H(string t) => _keyRows.Add(new KeyRow { header = true, action = t });
        void R(string keys, string act) { if (!string.IsNullOrEmpty(keys)) _keyRows.Add(new KeyRow { keys = keys, action = act }); }
        void K(KeyCode key, string act) { if (key != KeyCode.None) R(KeyLabel(key), act); }

        H("DRIVING");
        R(KeyLabel(k.throttle) + " / " + KeyLabel(k.throttleAlt), "Throttle");
        R(KeyLabel(k.brake) + " / " + KeyLabel(k.brakeAlt), "Brake");
        R("A / D", "Steer");
        K(k.gearReverse, "Reverse"); K(k.gearNeutral, "Neutral"); K(k.gearDrive, "Drive");
        K(k.parkingBrake, "Parking brake"); K(k.kickdown, "Kickdown"); K(k.altModifier, "Stall hold (while driving)");

        H("BUS");
        K(k.door, "Front door");
        R(KeyLabel(k.altModifier) + "+" + KeyLabel(k.door), "Rear door");
        K(k.relief, "Request relief"); K(k.status, "Status");
        K(k.kneel, "Kneel"); K(k.ignition, "Ignition");
        K(k.leftSignal, "Left signal"); K(k.rightSignal, "Right signal");
        K(k.hazards, "Hazards"); K(k.honk, "Honk"); K(k.unstuck, "Unstuck (right the bus)");

        H("CAMERA");
        K(k.cameraFirstPerson, "First person"); K(k.cameraOrbitFollow, "Orbit / follow");

        H("SCREENS");
        K(k.busSelectMenu, "Bus select"); K(k.shiftBoard, "Shift board");
        K(k.timetable, "Timetable"); K(k.liveMap, "Live map");
        K(k.trackerUI, "Tracker"); K(k.dispatchConsole, "Dispatch console");
        K(k.destinationSign, "Destination sign");

        H("TIMETABLE (OPEN)");
        R(KeyLabel(k.timetablePrevRoute) + " / " + KeyLabel(k.timetableNextRoute), "Prev / next route");
        K(k.timetableVariant, "Cycle variant"); K(k.timetableMyStop, "Jump to my stop"); K(k.timetableFutureOnly, "Future only");

        H("LIVE MAP (OPEN)");
        K(k.mapRecenter, "Re-centre"); K(k.mapZoomFit, "Zoom to fit");
    }

    private void DrawKeysPanel(float sc)
    {
        BuildKeyRows();
        var panel = KeysPanelRect();
        MDT_UITheme.DrawSoftShadow(panel, 10f, 5f, 10f);
        MDT_UITheme.DrawRoundedRectBordered(panel, 10f, new Color(0f, 0f, 0f, 0.85f), MDT_UITheme.BorderAccent, 1);
        GUI.Label(new Rect(panel.x + 12f * sc, panel.y + 6f * sc, panel.width - 24f * sc, 22f * sc), "KEYBINDS",
            MDT_UITheme.MakeLabel(Mathf.RoundToInt(13 * sc), FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextDim));

        float rowH = 30f * sc;
        var view = new Rect(panel.x + 6f * sc, panel.y + 32f * sc, panel.width - 12f * sc, panel.height - 40f * sc);
        var content = new Rect(0, 0, view.width - 18f * sc, _keyRows.Count * rowH + 8f * sc);

        // Mouse wheel scrolls it on PC.
        if (Event.current != null && Event.current.type == EventType.ScrollWheel && view.Contains(Event.current.mousePosition))
        {
            _keysScroll.y = Mathf.Clamp(_keysScroll.y + Event.current.delta.y * 20f, 0f, Mathf.Max(0f, content.height - view.height));
            Event.current.Use();
        }

        _keysScroll = GUI.BeginScrollView(view, _keysScroll, content);
        float y = 4f * sc;
        foreach (var row in _keyRows)
        {
            if (row.header)
            {
                GUI.Label(new Rect(6f * sc, y, content.width, rowH), row.action,
                    MDT_UITheme.MakeLabel(Mathf.RoundToInt(11 * sc), FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextCyan));
            }
            else
            {
                // Key in a little square, action text next to it.
                float kw = Mathf.Max(26f, 9f * row.keys.Length + 12f) * sc;
                var kr = new Rect(6f * sc, y + 3f * sc, kw, rowH - 6f * sc);
                MDT_UITheme.DrawRoundedRectBordered(kr, 5f, MDT_UITheme.BGButton, MDT_UITheme.BorderAccent, 1);
                GUI.Label(kr, row.keys, MDT_UITheme.MakeLabel(Mathf.RoundToInt(12 * sc), FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextPrimary));
                GUI.Label(new Rect(kr.xMax + 10f * sc, y, content.width - kr.xMax - 12f * sc, rowH), row.action,
                    MDT_UITheme.MakeLabel(Mathf.RoundToInt(12 * sc), FontStyle.Normal, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary));
            }
            y += rowH;
        }
        GUI.EndScrollView();
    }

    private void DrawToggleButton(Rect rect, string label, bool active, System.Action onPress = null, bool suppressGuiClick = false)
    {
        Color bg = active ? new Color(0.20f, 0.45f, 0.55f, 1f) : MDT_UITheme.BGButton;
        Color border = active ? new Color(0.4f, 0.85f, 1f, 0.85f) : MDT_UITheme.BorderAccent;
        MDT_UITheme.DrawRoundedRectBordered(rect, 8f, bg, border, active ? 2 : 1);
        GUI.Label(rect, label, _lblToggle);
    }

    private void EnsureStyles()
    {
        float s = UIScale;
        if (_stylesReady && Mathf.Approximately(s, _lastStyleScale)) return;
        _stylesReady = true;
        _lastStyleScale = s;
        _lblPedalName = MDT_UITheme.MakeLabel(Mathf.RoundToInt(12 * s), FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextDim);
        _lblPedalPct  = MDT_UITheme.MakeLabel(Mathf.RoundToInt(15 * s), FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextPrimary);
        _lblToggle    = MDT_UITheme.MakeLabel(Mathf.RoundToInt(12 * s), FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextPrimary);
        _lblTurn      = MDT_UITheme.MakeLabel(Mathf.RoundToInt(24 * s), FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextPrimary);
        _lblHonk      = MDT_UITheme.MakeLabel(Mathf.RoundToInt(11 * s), FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextPrimary);
    }
}