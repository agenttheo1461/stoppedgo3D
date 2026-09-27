using System;
using UnityEngine;
// ─────────────────────────────────────────────────────────────────────────────
//  BUS DASHBOARD HUD
//
//  [REDESIGN] This was the oldest surviving UI in the project -- hand-rolled
//  flat MDT_UITheme.DrawRect() panels with hardcoded per-call colors, no
//  rounding, no shadow, no shared style helpers, while literally everything
//  else (timetable/tracker UI, console, live map) had already moved onto
//  MDT_UITheme's DrawPanel/DrawRoundedRect/DrawSoftShadow/MakeLabel. Redone
//  to actually use that theme consistently: soft-shadowed rounded panels,
//  themed text colors, a proper pill-toggle look instead of a flat
//  color-swap square. Also dropped the leftover "DashboardHUD alive, bus=..."
//  debug label that was still printing every frame.
//
//  [EXTERIOR LIGHTS] Headlight/signal/brake/hazard state and material/light
//  application used to live directly on this component -- meaning every bus
//  shared ONE set of light wiring through whatever was assigned on the HUD
//  object, the same problem interior lighting had before it got its own
//  per-bus component. Now mirrors BusInteriorLightController exactly:
//  BusExteriorLightController lives on each bus prefab, wired per bus type,
//  and this HUD just resolves it off the active bus and calls into it. This
//  class owns no light state of its own anymore.
//
//  [A/C] The "A/C cycling mode (30s on / 30s off)" toggle used to be buried
//  in Driver.cs's Extra Settings popup, disconnected from the A/C slider it
//  actually governs. Moved onto this panel, right next to the slider.
// ─────────────────────────────────────────────────────────────────────────────
public class BusDashboardHUD : MonoBehaviour
{
    // No longer wired by hand — auto-resolved every frame from whichever
    // bus is actually possessed. Leave this field for manual override/testing
    // only; it gets overwritten as soon as a real possession exists.
    public BusSimulationController bus;
    public bool     showControls = true;
    // ── Center utility button ─────────────────────────────────────────────────
    public enum CenterButtonMode { ParkingBrake, AllisonEcoMode, None }
    [Header("Center Button (gear panel)")]
    public CenterButtonMode centerButtonMode = CenterButtonMode.ParkingBrake;

    // Interior/exterior lights now live per-bus (BusInteriorLightController /
    // BusExteriorLightController, wired per bus type in the inspector, same
    // pattern as BikeRackController) — resolved from the active bus each
    // time it changes.
    private BusInteriorLightController _interiorLights;
    private BusExteriorLightController _exteriorLights;

    // [ADD] Camera lock toggles -- CameraFollow25D lives on the camera rig,
    // not on the bus itself, so this isn't resolved per-possession like the
    // light controllers above; just found once and cached (Camera.main
    // doesn't change bus-to-bus the way the light components do).
    private CameraFollow25D _camera;

    private bool    _drawerOpen;
    private Rect    _drawerRect;
    private bool    _draggingDrawer;
    private Vector2 _dragOffset;

    // ── Styles ────────────────────────────────────────────────────────────────
    private GUIStyle _styleSpeed, _styleSpeedUnit, _styleGearActive, _styleGearInactive, _styleSmall, _styleHeading;
    private bool     _stylesBuilt;

    // ── Layout ────────────────────────────────────────────────────────────────
    // [ADD] Items 15/16 -- mobile touch-target sizing pass. SLOT_SIZE is a
    // real tap target (gear/lighting buttons); SLOT_GAP is just spacing
    // between them, scaled to match proportionally but with no minimum
    // floor of its own. PANEL_PAD/PANEL_RADIUS/SPEEDO_RADIUS aren't tap
    // targets (the speedo circle is already a large ~220px hit-zone on its
    // own) so they're left as fixed layout constants, unchanged.
    private static float SLOT_SIZE    => MDT_UITheme.ScaledTouchSize(46f);
    private static float SLOT_GAP     => MDT_UITheme.UIScale * 6f;
    private const float PANEL_PAD     = 10f;
    private const float PANEL_RADIUS  = 10f;
    private const float SPEEDO_RADIUS = 110f;

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private void Update()
    {
        ResolveActiveBus();
        if (bus == null) return;

        // Brake lights — the one bit of exterior-light logic that has to be
        // driven every frame from live physics state rather than a button
        // press; SetBrake() itself is dirty-guarded so this is cheap.
        if (_exteriorLights != null)
        {
            bool brakeOn = bus.bkPd >= 0.2f || bus.spd < 0.5f;
            _exteriorLights.SetBrake(brakeOn);
        }
    }
    private void ResolveActiveBus()
    {
        var menu = BusSelectMenu.Instance;
        var live = menu != null ? menu.ActiveBus : null;

        // Only accept a bus that's actually enabled & running — guards against
        // a stale reference left over after ReleaseCurrentBus() disables it.
        if (live != null && live.enabled)
        {
            bus = live;
        }
        else
        {
            bus = null;
        }
        _interiorLights = bus != null ? bus.GetComponent<BusInteriorLightController>() : null;
        _exteriorLights = bus != null ? BusExteriorLightController.Find(bus) : null;

        // [ADD] Resolved once, independent of which bus is active -- the
        // camera rig itself doesn't change on a possession swap the way the
        // light controllers do. Re-checked only when null (e.g. scene not
        // fully loaded yet on the very first frame) rather than every frame.
        if (_camera == null && Camera.main != null)
            _camera = Camera.main.GetComponent<CameraFollow25D>();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ONGUI
    // ═════════════════════════════════════════════════════════════════════════
    private void OnGUI()
    {
        ResolveActiveBus(); // OnGUI can run before Update in some frame orders — be safe
        if (!showControls || bus == null) return;
        BuildStyles();

        DrawSpeedometer();
        DrawGearPanel();

        if (_drawerOpen) DrawLightingDrawer();
    }
    // ── Speedometer ───────────────────────────────────────────────────────────
    // [FIX] Was km/h-only, needle normalized against the old 90 kph top
    // speed. Real top speed is 105 kph (~65 mph, see BusAudioEngine.
    // MAX_SPD / BusController.MAX_SPD). Primary readout is now MPH (a US
    // transit dash reads in mph), with the equivalent km/h shown smaller
    // underneath, and the needle sweep is driven off the same 65 mph max
    // so it agrees with the real top speed.
    private const float KPH_TO_MPH = 0.621371f;
    private const float MAX_MPH    = 65f; // matches 105 kph top speed

    private void DrawSpeedometer()
    {
        float cx = Screen.width  * 0.5f;
        float cy = Screen.height - 100f;
        float r  = SPEEDO_RADIUS;

        MDT_UITheme.DrawSoftShadow(new Rect(cx - r, cy - r, r * 2, r), r * 0.5f, 6f, 14f);
        DrawFilledArc( new Vector2(cx, cy), r, 180f, 180f, MDT_UITheme.BGDeep);
        DrawArcOutline(new Vector2(cx, cy), r, 180f, 180f, MDT_UITheme.BorderAccent, 2f);

        // [ADD] Settings units toggle -- primary/secondary swap, mph stays
        // the default (matches the real US-transit-dash convention this was
        // already tuned for) unless the player opts into metric.
        float mph = bus.spd * KPH_TO_MPH;
        bool metric = SettingsData.UseMetricUnits;
        GUI.Label(new Rect(cx - 80, cy - r + 24, 160, 50), metric ? $"{bus.spd:F0}" : $"{mph:F0}", _styleSpeed);
        GUI.Label(new Rect(cx - 30, cy - r + 74,  60, 16), metric ? "km/h" : "mph",                _styleSpeedUnit);
        GUI.Label(new Rect(cx - 40, cy - r + 90,  80, 16), metric ? $"{mph:F0} mph" : $"{bus.spd:F0} km/h", _styleSpeedUnit);

        for (int i = 0; i <= 10; i++)
        {
            float angleDeg = 180f + (i / 10f) * 180f;
            float rad      = angleDeg * Mathf.Deg2Rad;
            Vector2 outer  = new Vector2(cx + Mathf.Cos(rad) * (r -  4), cy + Mathf.Sin(rad) * (r -  4));
            Vector2 inner  = new Vector2(cx + Mathf.Cos(rad) * (r - 14), cy + Mathf.Sin(rad) * (r - 14));
            MDT_UITheme.DrawLine(outer, inner, MDT_UITheme.TextSecond, 1.5f);
        }

        float needleT      = Mathf.Clamp01(mph / MAX_MPH);
        float needleRad    = (180f + needleT * 180f) * Mathf.Deg2Rad;
        Vector2 needleTip  = new Vector2(cx + Mathf.Cos(needleRad) * (r - 18), cy + Mathf.Sin(needleRad) * (r - 18));
        MDT_UITheme.DrawLine(new Vector2(cx, cy), needleTip, MDT_UITheme.TextRed, 3f);
        MDT_UITheme.DrawRoundedRect(new Rect(cx - 4, cy - 4, 8, 8), 4f, MDT_UITheme.TextRed);

        if (GUI.Button(new Rect(cx - r, cy - r, r * 2, r), "", GUIStyle.none))
        {
            _drawerOpen = !_drawerOpen;
            if (_drawerOpen) _drawerRect = new Rect(cx - 160, cy - r - 220, 320, 200);
        }
    }

    // ── Gear panel ────────────────────────────────────────────────────────────
    private void DrawGearPanel()
    {
        float panelW = SLOT_SIZE * 2 + SLOT_GAP * 3 + PANEL_PAD * 2 - SLOT_GAP;
        float panelH = SLOT_SIZE * 3 + SLOT_GAP * 4 + PANEL_PAD * 2 - SLOT_GAP;
        float px     = Screen.width  - 300f;
        float py     = Screen.height - 500f;
        var   panelRect = new Rect(px, py, panelW, panelH);

        MDT_UITheme.DrawSoftShadow(panelRect, PANEL_RADIUS, 5f, 10f);
        MDT_UITheme.DrawRoundedRectBordered(panelRect, PANEL_RADIUS, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        float col0X = px + PANEL_PAD;
        float col1X = col0X + SLOT_SIZE + SLOT_GAP;
        float row0Y = py + PANEL_PAD;
        float row1Y = row0Y + SLOT_SIZE + SLOT_GAP;
        float row2Y = row1Y + SLOT_SIZE + SLOT_GAP;

        string dLabel  = (bus.tx == "voith" || bus.tx == "d8645") ? "DIWA" : "D";
        bool isDrive   = bus.currentDirection == BusSimulationController.GearDirection.Drive;
        bool isNeutral = bus.currentDirection == BusSimulationController.GearDirection.Neutral;
        bool isReverse = bus.currentDirection == BusSimulationController.GearDirection.Reverse;

        if (SlotButton(new Rect(col0X, row0Y, SLOT_SIZE, SLOT_SIZE), dLabel,  isDrive,   _styleGearActive, _styleGearInactive))
            bus.currentDirection = BusSimulationController.GearDirection.Drive;
        if (SlotButton(new Rect(col0X, row1Y, SLOT_SIZE, SLOT_SIZE), "N",     isNeutral, _styleGearActive, _styleGearInactive))
            bus.currentDirection = BusSimulationController.GearDirection.Neutral;
        if (SlotButton(new Rect(col0X, row2Y, SLOT_SIZE, SLOT_SIZE), "R",     isReverse, _styleGearActive, _styleGearInactive))
            bus.currentDirection = BusSimulationController.GearDirection.Reverse;

        DrawStaticSlot(new Rect(col1X, row0Y, SLOT_SIZE, SLOT_SIZE), GetGearLabel(), _styleGearInactive);

        float centerSize   = SLOT_SIZE * 0.67f;
        float centerOffset = (SLOT_SIZE - centerSize) * 0.5f;
        DrawCenterButton(new Rect(col1X + centerOffset, row1Y + centerOffset, centerSize, centerSize));

        float halfW = SLOT_SIZE * 0.5f;
        // ENG = the engine itself (ignition state machine: Off → Cranking →
        // ReadyToStart → Running). Highlighted whenever it's not fully
        // running, since that's the "needs attention" state.
        bool engineNotRunning = bus.audioEngine != null
            && bus.audioEngine.engineState != BusAudioEngine.EngineRunState.Running;
        if (SlotButton(new Rect(col1X, row2Y, halfW, SLOT_SIZE), GetEngineSlotLabel(), engineNotRunning, _styleGearActive, _styleSmall) &&
            bus.audioEngine != null)
            bus.audioEngine.RequestEngineToggle();

        // BAT = master electrical power (doors, dash, interior lights, A/C —
        // all the "electronics", independent of whether the engine runs).
        bool batteryOff = bus.audioEngine != null && !bus.audioEngine.batteryOn;
        if (SlotButton(new Rect(col1X + halfW, row2Y, halfW, SLOT_SIZE), "BAT", batteryOff, _styleGearActive, _styleSmall) &&
            bus.audioEngine != null)
            bus.audioEngine.batteryOn = !bus.audioEngine.batteryOn;

        DrawACPanel(px, py + panelH + SLOT_GAP, panelW);
    }

    // ── A/C panel ─────────────────────────────────────────────────────────────
    // [MOVED] "A/C cycling mode" used to live in Driver.cs's Extra Settings
    // popup, nowhere near the actual A/C slider. Now it's a small toggle
    // pill right next to the slider it governs, on this panel.
    private void DrawACPanel(float px, float py, float panelW)
    {
        const float rowH = 30f, cycleRowH = 24f;
        float totalH = rowH + cycleRowH + PANEL_PAD * 0.5f;
        var panelRect = new Rect(px, py, panelW, totalH);

        MDT_UITheme.DrawSoftShadow(panelRect, PANEL_RADIUS, 4f, 8f);
        MDT_UITheme.DrawRoundedRectBordered(panelRect, PANEL_RADIUS, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        var rowRect = new Rect(px, py, panelW, rowH);
        GUI.Label(new Rect(rowRect.x + 10, rowRect.y, 32, rowH), "A/C", _styleSmall);

        var ae = bus.audioEngine;
        int currentAC  = ae != null ? ae.acLevel : 0;
        var sliderRect = new Rect(rowRect.x + 44, rowRect.y + rowH * 0.5f - 6f, rowRect.width - 92, 12f);
        float newVal   = GUI.HorizontalSlider(sliderRect, currentAC, 0f, 100f);
        int rounded    = Mathf.RoundToInt(newVal);
        if (ae != null && rounded != ae.acLevel)
            ae.acLevel = rounded;

        GUI.Label(new Rect(rowRect.xMax - 40, rowRect.y, 36, rowH), $"{currentAC}%", _styleSmall);

        var cycleRect = new Rect(px + 8, py + rowH + 2f, panelW - 16, cycleRowH);
        bool cycleOn = ae != null && ae.acCyclingMode;
        if (SlotToggleRow(cycleRect, "CYCLING (30s ON/OFF)", cycleOn, ae != null))
            ae.acCyclingMode = !ae.acCyclingMode;
    }

    /// <summary>Thin full-width toggle row -- rounded pill on the left,
    /// label to the right. Used for the A/C cycling toggle; smaller and
    /// less shouty than a full SlotButton square for a binary setting.</summary>
    private bool SlotToggleRow(Rect rect, string label, bool active, bool interactable)
    {
        var prevEnabled = GUI.enabled;
        GUI.enabled = interactable;

        float pillW = 30f, pillH = 16f;
        var pillRect = new Rect(rect.x, rect.y + (rect.height - pillH) * 0.5f, pillW, pillH);
        Color pillCol = active ? new Color(0.30f, 0.65f, 0.55f, 1f) : MDT_UITheme.BGPill;
        MDT_UITheme.DrawRoundedRect(pillRect, pillH * 0.5f, pillCol);
        float knobD = pillH - 4f;
        float knobX = active ? pillRect.xMax - knobD - 2f : pillRect.x + 2f;
        MDT_UITheme.DrawRoundedRect(new Rect(knobX, pillRect.y + 2f, knobD, knobD), knobD * 0.5f, Color.white);

        var labelRect = new Rect(pillRect.xMax + 8, rect.y, rect.width - pillW - 8, rect.height);
        GUI.Label(labelRect, label, active ? _styleGearInactive : _styleSmall);

        bool clicked = GUI.Button(rect, "", GUIStyle.none);
        GUI.enabled = prevEnabled;
        return clicked && interactable;
    }

    private void DrawCenterButton(Rect rect)
    {
        switch (centerButtonMode)
        {
            case CenterButtonMode.ParkingBrake:
                if (SlotButton(rect, "P", bus.parkingBrake, _styleGearActive, _styleGearInactive))
                    bus.parkingBrake = !bus.parkingBrake;
                break;

            case CenterButtonMode.AllisonEcoMode:
                bool ecoCapable = bus.tx is "allison" or "b400r" or "b500r" or "b3400xfe" or "zf";
                if (ecoCapable && bus.audioEngine != null)
                {
                    bool eco = bus.audioEngine.economyMode;
                    if (SlotButton(rect, eco ? "ECO" : "PWR", eco, _styleGearActive, _styleSmall))
                    {
                        bus.audioEngine.economyMode = !bus.audioEngine.economyMode;
                        bus.economyMode             =  bus.audioEngine.economyMode;
                    }
                }
                else DrawStaticSlot(rect, "", _styleSmall);
                break;

            default:
                DrawStaticSlot(rect, "", _styleSmall);
                break;
        }
    }

    // ── Lighting drawer ───────────────────────────────────────────────────────
    private void DrawLightingDrawer()
    {
        MDT_UITheme.DrawSoftShadow(_drawerRect, PANEL_RADIUS, 5f, 10f);
        MDT_UITheme.DrawRoundedRectBordered(_drawerRect, PANEL_RADIUS, MDT_UITheme.BGDeep, MDT_UITheme.BorderAccent, 1);

        var titleBar = new Rect(_drawerRect.x, _drawerRect.y, _drawerRect.width, 26f);
        MDT_UITheme.DrawRoundedRect(titleBar, PANEL_RADIUS, MDT_UITheme.BGHeader);
        GUI.Label(new Rect(titleBar.x + 10, titleBar.y, titleBar.width - 40, 26), "CONTROLS", _styleHeading);

        if (GUI.Button(new Rect(titleBar.xMax - 28, titleBar.y + 3, 20, 20), "✕", _styleSmall))
            { _drawerOpen = false; return; }

        HandleDrawerDrag(titleBar);

        float contentY = _drawerRect.y + 34f;
        float pad      = PANEL_PAD;
        float innerW   = _drawerRect.width - pad * 2;

        bool hasExt = _exteriorLights != null;

        // Row 1: Headlights
        var headRect = new Rect(_drawerRect.x + pad, contentY, innerW, SLOT_SIZE * 0.7f);
        bool headOn = hasExt && _exteriorLights.HeadlightsOn;
        if (SlotButton(headRect, headOn ? "HEADLIGHTS: ON" : "HEADLIGHTS: OFF", headOn, _styleGearActive, _styleSmall) && hasExt)
            _exteriorLights.ToggleHeadlights();

        float row2Y = headRect.yMax + SLOT_GAP;
        float halfW = innerW * 0.5f;

        // Row 2: Signals
        var leftSigR  = new Rect(_drawerRect.x + pad,               row2Y, halfW - SLOT_GAP * 0.5f, SLOT_SIZE);
        var rightSigR = new Rect(_drawerRect.x + pad + halfW + SLOT_GAP * 0.5f, row2Y, halfW - SLOT_GAP * 0.5f, SLOT_SIZE);

        bool leftOn  = hasExt && _exteriorLights.LeftSignalOn;
        bool rightOn = hasExt && _exteriorLights.RightSignalOn;
        if (SlotButton(leftSigR, "◄ LEFT", leftOn, _styleGearActive, _styleSmall) && hasExt)
            _exteriorLights.ToggleLeftSignal();
        if (SlotButton(rightSigR, "RIGHT ►", rightOn, _styleGearActive, _styleSmall) && hasExt)
            _exteriorLights.ToggleRightSignal();

        // Row 3: Hazards + interior lights + camera locks + Main Menu
        float row3Y = row2Y + SLOT_SIZE + SLOT_GAP;
        // [FIX] Widened from 4 to 5 slots to fit the Main Menu button --
        // every existing slot was already spoken for (HAZ, interior
        // lights, 3RD/1ST camera locks), so this needed a real 5th slot
        // rather than another already-used one. Buttons get slightly
        // narrower to make room, same as the row already re-flowed when
        // the camera locks took over the last two "???" spares.
        float slotW = (innerW - SLOT_GAP * 4) / 5f;
        for (int i = 0; i < 5; i++)
        {
            var slotRect = new Rect(_drawerRect.x + pad + i * (slotW + SLOT_GAP), row3Y, slotW, SLOT_SIZE);
            if (i == 0)
            {
                bool hazOn = hasExt && _exteriorLights.HazardsOn;
                if (SlotButton(slotRect, "HAZ", hazOn, _styleGearActive, _styleSmall) && hasExt)
                    _exteriorLights.ToggleHazards();
            }
            else if (i == 1)
            {
                bool hasLights = _interiorLights != null;
                bool lightsOn  = hasLights && _interiorLights.CurrentMode != BusInteriorLightController.InteriorLightMode.Off;
                if (SlotButton(slotRect, GetInteriorLightLabel(), lightsOn, _styleGearActive, _styleSmall) && hasLights)
                {
                    // Cycle: Off → AllOn → LeftAndBackRightOnly → MidBackRightAndLeft → Off
                    _interiorLights.CycleMode();
                }
            }
            else if (i == 2)
            {
                // [ADD] 3rd Person (Orbit/F) camera lock -- was a spare "???"
                // slot. Locking blocks manual mouse-drag in orbit mode only;
                // the camera still tracks the bus's own rotation regardless
                // (see CameraFollow25D.lockOrbitCamera's own comment).
                bool hasCam = _camera != null;
                bool locked = hasCam && _camera.lockOrbitCamera;
                if (SlotButton(slotRect, locked ? "3RD: LOCKED" : "3RD: FREE", locked, _styleGearActive, _styleSmall) && hasCam)
                    _camera.lockOrbitCamera = !_camera.lockOrbitCamera;
            }
            else if (i == 3)
            {
                // [ADD] 1st Person (FPV/X) camera lock -- same idea, blocks
                // manual mouse-look only, the view still turns with the bus.
                bool hasCam = _camera != null;
                bool locked = hasCam && _camera.lockFirstPersonCamera;
                if (SlotButton(slotRect, locked ? "1ST: LOCKED" : "1ST: FREE", locked, _styleGearActive, _styleSmall) && hasCam)
                    _camera.lockFirstPersonCamera = !_camera.lockFirstPersonCamera;
            }
            else if (i == 4)
            {
                // [ADD] Main Menu, accessible anytime -- same button works
                // for both mobile (this whole HUD already renders via
                // OnGUI/touch-driven SlotButton, no separate mobile path
                // needed) and PC (same click). MainMenu.ToggleOpen() already
                // guards against popping open mid-shift on its own, so
                // there's no additional on-duty check needed here -- same
                // "let the target method own its own guard" pattern the
                // camera locks and lights buttons above already follow.
                bool menuOpen = MainMenu.Instance != null && MainMenu.Instance.IsOpen;
                if (SlotButton(slotRect, "MENU", menuOpen, _styleGearActive, _styleSmall))
                    MainMenu.Instance?.ToggleOpen();
            }
            else DrawStaticSlot(slotRect, "???", _styleSmall, active: false);
        }

        _drawerRect.height = (row3Y + SLOT_SIZE + pad) - _drawerRect.y;
    }

    private void HandleDrawerDrag(Rect titleBar)
    {
        var e = Event.current;
        if      (e.type == EventType.MouseDown && titleBar.Contains(e.mousePosition)) { _draggingDrawer = true;  _dragOffset = e.mousePosition - new Vector2(_drawerRect.x, _drawerRect.y); e.Use(); }
        else if (e.type == EventType.MouseDrag && _draggingDrawer)                    { _drawerRect.x = e.mousePosition.x - _dragOffset.x; _drawerRect.y = e.mousePosition.y - _dragOffset.y; e.Use(); }
        else if (e.type == EventType.MouseUp   && _draggingDrawer)                    { _draggingDrawer = false; e.Use(); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DRAW HELPERS
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Rounded slot button with active highlighting + optional LED glow.</summary>
    // Static so Driver.cs's Extra Settings toggle can flip it without needing
    // a direct reference to whichever dashboard instance is active.
    public static bool LedGlowEnabled = false;

    private bool SlotButton(Rect rect, string label, bool active, GUIStyle activeStyle, GUIStyle inactiveStyle)
    {
        if (LedGlowEnabled && active) DrawLedGlow(rect);

        Color bg = active ? new Color(0.20f, 0.45f, 0.55f, 1f) : MDT_UITheme.BGButton;
        Color outlineCol = active ? new Color(0.4f, 0.85f, 1f, 0.85f) : MDT_UITheme.BorderAccent;

        MDT_UITheme.DrawRoundedRectBordered(rect, 8f, bg, outlineCol, active ? 2 : 1);

        bool clicked = GUI.Button(rect, "", GUIStyle.none);
        GUI.Label(rect, label, active ? activeStyle : inactiveStyle);
        return clicked;
    }

    // Fakes a soft blue LED backlight halo -- IMGUI has no real blur, so
    // this layers a few progressively larger, progressively more
    // transparent rounded rects behind the button to approximate one.
    private static readonly Color LedGlowColor = new Color(0.25f, 0.55f, 1f, 1f);
    private void DrawLedGlow(Rect rect)
    {
        for (int i = 5; i >= 1; i--)
        {
            float pad = i * 4.5f;
            float alpha = 0.30f / i;
            var glowRect = new Rect(rect.x - pad, rect.y - pad, rect.width + pad * 2f, rect.height + pad * 2f);
            MDT_UITheme.DrawRoundedRect(glowRect, 8f + pad * 0.5f, new Color(LedGlowColor.r, LedGlowColor.g, LedGlowColor.b, alpha));
        }
    }

    // [FIX] "the numbers also don't glow" -- the gear-number readout (the
    // "1"/"2"/etc slot next to D/N/R) is drawn through THIS method, not
    // SlotButton, so it never got any glow logic in the first pass at all.
    // `active` defaults true to match its one real (non-blank/non-"???")
    // call site; the blank-placeholder call sites explicitly pass false so
    // an empty slot never lights up.
    private void DrawStaticSlot(Rect rect, string label, GUIStyle style, bool active = true)
    {
        if (LedGlowEnabled && active && !string.IsNullOrEmpty(label) && label != "???")
            DrawLedGlow(rect);
        MDT_UITheme.DrawRoundedRectBordered(rect, 8f, MDT_UITheme.BGButton, MDT_UITheme.Divider, 1);
        GUI.Label(rect, label, style);
    }

    private string GetGearLabel()
    {
        if (bus.currentDirection == BusSimulationController.GearDirection.Neutral) return "N";
        if (bus.currentDirection == BusSimulationController.GearDirection.Reverse) return "R";
        if (bus.tx is "voith" or "d8645")
            return bus.gear == 1 ? "DIWA" : bus.gear > 0 ? bus.gear.ToString() : "D";
        return bus.gear > 0 ? bus.gear.ToString() : "D";
    }

    private string GetEngineSlotLabel()
    {
        if (bus.audioEngine == null) return "ENG";
        switch (bus.audioEngine.engineState)
        {
            case BusAudioEngine.EngineRunState.Off:          return "ENG";
            case BusAudioEngine.EngineRunState.Cranking:      return "CRANK";
            case BusAudioEngine.EngineRunState.ReadyToStart:  return "START";
            case BusAudioEngine.EngineRunState.Running:       return "ENG";
            default:                                          return "ENG";
        }
    }

    private string GetInteriorLightLabel()
    {
        if (_interiorLights == null) return "INT: N/A";
        switch (_interiorLights.CurrentMode)
        {
            case BusInteriorLightController.InteriorLightMode.Off:                  return "INT: OFF";
            case BusInteriorLightController.InteriorLightMode.AllOn:                return "INT: ALL";
            case BusInteriorLightController.InteriorLightMode.LeftAndBackRightOnly: return "INT: L+BR";
            case BusInteriorLightController.InteriorLightMode.MidBackRightAndLeft:  return "INT: M+BR+L";
            default:                                                               return "INT";
        }
    }

    private void DrawFilledArc(Vector2 center, float radius, float startAngleDeg, float sweepDeg, Color color)
    {
        const int segs = 40;
        for (int i = 0; i <= segs; i++)
        {
            float rad  = (startAngleDeg + (i / (float)segs) * sweepDeg) * Mathf.Deg2Rad;
            Vector2 edge = center + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radius;
            MDT_UITheme.DrawLine(center, edge, color, radius / segs * 2.2f);
        }
    }

    private void DrawArcOutline(Vector2 center, float radius, float startAngleDeg, float sweepDeg, Color color, float thickness)
    {
        const int segs = 40;
        Vector2 prev = center + new Vector2(Mathf.Cos(startAngleDeg * Mathf.Deg2Rad), Mathf.Sin(startAngleDeg * Mathf.Deg2Rad)) * radius;
        for (int i = 1; i <= segs; i++)
        {
            float rad  = (startAngleDeg + (i / (float)segs) * sweepDeg) * Mathf.Deg2Rad;
            Vector2 next = center + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * radius;
            MDT_UITheme.DrawLine(prev, next, color, thickness);
            prev = next;
        }
        Vector2 left  = center + new Vector2(Mathf.Cos(startAngleDeg * Mathf.Deg2Rad),                    Mathf.Sin(startAngleDeg * Mathf.Deg2Rad)) * radius;
        Vector2 right = center + new Vector2(Mathf.Cos((startAngleDeg + sweepDeg) * Mathf.Deg2Rad), Mathf.Sin((startAngleDeg + sweepDeg) * Mathf.Deg2Rad)) * radius;
        MDT_UITheme.DrawLine(left, right, color, thickness);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STYLES
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildStyles()
    {
        if (_stylesBuilt) return;
        _stylesBuilt = true;

        _styleSpeed = MDT_UITheme.MakeLabel(36, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextPrimary);
        _styleSpeedUnit = MDT_UITheme.MakeLabel(11, FontStyle.Normal, TextAnchor.MiddleCenter, MDT_UITheme.TextSecond);
        _styleGearActive = MDT_UITheme.MakeLabel(16, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextPrimary);
        _styleGearInactive = MDT_UITheme.MakeLabel(16, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextSecond);
        _styleSmall = MDT_UITheme.MakeLabel(10, FontStyle.Bold, TextAnchor.MiddleCenter, MDT_UITheme.TextSecond);
        _styleHeading = MDT_UITheme.MakeLabel(11, FontStyle.Bold, TextAnchor.MiddleLeft, MDT_UITheme.TextPrimary);
    }
}