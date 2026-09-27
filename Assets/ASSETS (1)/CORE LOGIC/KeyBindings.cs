using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Single source of truth for every keyboard binding in the game.
/// Defaults live HERE in code (KeyBindData field initializers). Put one
/// KeyBindings component in the scene to edit any binding from the
/// inspector; with none in the scene the code defaults are used. Nothing
/// else in the project stores its own KeyCode any more, so a stale scene
/// value on some other component can no longer override these.
///
/// Not covered: steering (Unity Input Manager "Horizontal" axis), mouse
/// buttons/wheel/look, and the driver console's typed commands.
/// </summary>
[DisallowMultipleComponent]
public class KeyBindings : MonoBehaviour
{
    public static KeyBindings Instance { get; private set; }

    [Tooltip("Edit any binding here. Right-click the component header > Reset All Keybinds to restore the code defaults.")]
    public KeyBindData binds = new KeyBindData();

    private static readonly KeyBindData _defaults = new KeyBindData();

    /// <summary>The live bindings: the scene component's if present, else code defaults.</summary>
    public static KeyBindData Current => Instance != null ? Instance.binds : _defaults;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        LoadRebinds();
    }

    // [ADD] In-game rebinding persistence. KeyBindData was Inspector-only
    // before -- no runtime UI ever existed to change a binding, so nothing
    // needed saving. Reflection over its public KeyCode fields means a
    // future binding added to KeyBindData is automatically covered here too,
    // no separate list to keep in sync. See SettingsWindow for the actual
    // rebinding UI.
    private const string PrefPrefix = "KeyBind_";

    private static IEnumerable<FieldInfo> BindableFields() =>
        typeof(KeyBindData).GetFields(BindingFlags.Public | BindingFlags.Instance);

    public void SetBinding(string fieldName, KeyCode value)
    {
        var field = typeof(KeyBindData).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        if (field == null || field.FieldType != typeof(KeyCode)) return;
        field.SetValue(binds, value);
        PlayerPrefs.SetInt(PrefPrefix + fieldName, (int)value);
    }

    public void ResetBindingToDefault(string fieldName)
    {
        var field = typeof(KeyBindData).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        if (field == null || field.FieldType != typeof(KeyCode)) return;
        var defaultValue = (KeyCode)field.GetValue(_defaults);
        field.SetValue(binds, defaultValue);
        PlayerPrefs.DeleteKey(PrefPrefix + fieldName);
    }

    private void LoadRebinds()
    {
        foreach (var field in BindableFields())
        {
            if (field.FieldType != typeof(KeyCode)) continue;
            string prefKey = PrefPrefix + field.Name;
            if (!PlayerPrefs.HasKey(prefKey)) continue;
            field.SetValue(binds, (KeyCode)PlayerPrefs.GetInt(prefKey));
        }
    }

    private void OnDestroy() { if (Instance == this) Instance = null; }

    [ContextMenu("Reset All Keybinds")]
    private void ResetAll() => binds = new KeyBindData();

    // ── Helpers ─────────────────────────────────────────────────────────
    public static bool ThrottleHeld => Input.GetKey(Current.throttle) || Input.GetKey(Current.throttleAlt);
    public static bool BrakeHeld    => Input.GetKey(Current.brake)    || Input.GetKey(Current.brakeAlt);

    /// <summary>True while the given modifier is held. Left/Right Shift and
    /// Ctrl are treated as the same key.</summary>
    public static bool Held(KeyCode k)
    {
        if (Input.GetKey(k)) return true;
        switch (k)
        {
            case KeyCode.LeftShift:    return Input.GetKey(KeyCode.RightShift);
            case KeyCode.RightShift:   return Input.GetKey(KeyCode.LeftShift);
            case KeyCode.LeftControl:  return Input.GetKey(KeyCode.RightControl);
            case KeyCode.RightControl: return Input.GetKey(KeyCode.LeftControl);
        }
        return false;
    }

    public static bool DebugModifierHeld => Held(Current.debugModifier);
    public static bool ShiftModifierHeld => Held(Current.altModifier);
}

[Serializable]
public class KeyBindData
{
    [Header("Driving")]
    public KeyCode throttle    = KeyCode.W;
    public KeyCode throttleAlt = KeyCode.UpArrow;
    public KeyCode brake       = KeyCode.S;
    public KeyCode brakeAlt    = KeyCode.DownArrow;
    public KeyCode gearReverse = KeyCode.B;
    public KeyCode gearNeutral = KeyCode.N;
    public KeyCode gearDrive   = KeyCode.M;
    public KeyCode parkingBrake = KeyCode.P;
    public KeyCode kickdown    = KeyCode.Space;
    [Tooltip("Modifier used for stall hold (while driving) and rear-door (with the door key).")]
    public KeyCode altModifier = KeyCode.LeftShift;

    [Header("Bus functions")]
    [Tooltip("Front door. Alt modifier + this = rear door.")]
    public KeyCode door        = KeyCode.Alpha1;
    public KeyCode relief      = KeyCode.Alpha8;
    public KeyCode status      = KeyCode.Alpha9;
    public KeyCode kneel       = KeyCode.K;
    [Tooltip("Deploys/retracts the wheelchair lift ramp. Only usable stopped, kneeling, in neutral, parking brake set, front door open, and only when a wheelchair passenger actually needs it.")]
    public KeyCode rampDeploy  = KeyCode.R;
    public KeyCode ignition    = KeyCode.I;
    public KeyCode leftSignal  = KeyCode.Q;
    public KeyCode rightSignal = KeyCode.E;
    public KeyCode hazards     = KeyCode.F;
    [Tooltip("Rights the bus if it's been flipped or tilted (keeps position + heading).")]
    public KeyCode unstuck     = KeyCode.Backspace;
    public KeyCode honk        = KeyCode.H;

    [Header("Cameras")]
    public KeyCode cameraFirstPerson = KeyCode.F1;
    public KeyCode cameraOrbitFollow = KeyCode.F2;

    [Header("Screens")]
    public KeyCode busSelectMenu   = KeyCode.Alpha0;
    public KeyCode shiftBoard      = KeyCode.Escape;
    public KeyCode timetable       = KeyCode.T;
    public KeyCode liveMap         = KeyCode.L;
    public KeyCode trackerUI       = KeyCode.U;
    public KeyCode dispatchConsole = KeyCode.Tab;
    public KeyCode destinationSign = KeyCode.F6;
    [Tooltip("Toggles the route-coloured guide ribbon + chevrons + stop circles on the road. None = no key.")]
    public KeyCode guideArrows     = KeyCode.G;
    [Tooltip("None = no key (opened from the main menu).")]
    public KeyCode snapshotViewer  = KeyCode.None;
    [Tooltip("Settings doesn't touch shift state, so unlike Main Menu it's safe to open with a direct key at any time, not just from the Main Menu button.")]
    public KeyCode settingsMenu    = KeyCode.F5;

    [Header("Timetable (while open)")]
    public KeyCode timetablePrevRoute = KeyCode.LeftArrow;
    public KeyCode timetableNextRoute = KeyCode.RightArrow;
    public KeyCode timetableVariant   = KeyCode.V;
    public KeyCode timetableMyStop    = KeyCode.J;
    public KeyCode timetableFutureOnly = KeyCode.O;

    [Header("Live map (while open)")]
    public KeyCode mapRecenter = KeyCode.C;
    public KeyCode mapZoomFit  = KeyCode.Z;

    [Header("Snapshot viewer (while open)")]
    public KeyCode snapshotPrevRoute   = KeyCode.LeftArrow;
    public KeyCode snapshotNextRoute   = KeyCode.RightArrow;
    public KeyCode snapshotPrevHistory = KeyCode.LeftBracket;
    public KeyCode snapshotNextHistory = KeyCode.RightBracket;

    [Header("Debug (hold the debug modifier)")]
    public KeyCode debugModifier   = KeyCode.LeftControl;
    public KeyCode debugDumpSchedule = KeyCode.F4;
    public KeyCode debugResetBuses   = KeyCode.F7;
    [Tooltip("Needs debug modifier + Shift as well.")]
    public KeyCode debugNpcTest      = KeyCode.F12;
}
