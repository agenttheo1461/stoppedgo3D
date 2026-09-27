using System;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  BUS INTERIOR LIGHT CONTROLLER
//  Attach to each bus prefab. Zones/renderers/lights are wired per bus type
//  in the inspector, same pattern as BikeRackController.
// ─────────────────────────────────────────────────────────────────────────────
public class BusInteriorLightController : MonoBehaviour
{
    [Flags]
    public enum LightZone
    {
        None       = 0,
        RightFront = 1 << 0,
        LeftFront  = 1 << 1,
        MidRight   = 1 << 2,
        MidLeft    = 1 << 3,
        BackRight  = 1 << 4,
        BackLeft   = 1 << 5,
        All        = RightFront | LeftFront | MidRight | MidLeft | BackRight | BackLeft
    }

    public enum InteriorLightMode
    {
        Off,
        AllOn,
        LeftAndBackRightOnly,
        MidBackRightAndLeft   // front lights off
    }

    [Serializable]
    public class LightZoneGroup
    {
        public LightZone  zone;
        public Renderer[] renderers;
        public Light[]    lights;
    }

    [Header("Materials")]
    public Material onMat;
    public Material offMat;

    [Header("Zones (wire this bus type's lights per zone)")]
    public LightZoneGroup[] zones = new LightZoneGroup[]
    {
        new LightZoneGroup { zone = LightZone.RightFront },
        new LightZoneGroup { zone = LightZone.LeftFront  },
        new LightZoneGroup { zone = LightZone.MidRight   },
        new LightZoneGroup { zone = LightZone.MidLeft    },
        new LightZoneGroup { zone = LightZone.BackRight  },
        new LightZoneGroup { zone = LightZone.BackLeft   },
    };

    [Header("Mode → Zone mapping (per bus type)")]
    public LightZone zonesLeftAndBackRightOnly = LightZone.LeftFront | LightZone.BackLeft | LightZone.BackRight;
    public LightZone zonesMidBackRightAndLeft  = LightZone.MidRight  | LightZone.MidLeft   | LightZone.BackRight | LightZone.BackLeft;

    [Header("Door Override")]
    [Tooltip("Intensity for every light while the door is open (all zones forced on).")]
    public float doorOpenIntensity = 1f;
    [Tooltip("Intensity for whichever zones are on once the door closes and the mode's normal state resumes.")]
    public float normalOnIntensity = 0.3f;

    public InteriorLightMode CurrentMode { get; private set; } = InteriorLightMode.Off;
    public bool              IsDoorOpen  { get; private set; }

    // [BATTERY] Interior lights are electronics -- no battery, no lights,
    // full stop, regardless of what mode/door-state was selected. Mirrors
    // the same battery gate BusExteriorLightController now has. Resolved
    // self-sufficiently off whichever controller (NPC or player) is on this
    // same GameObject so this works for every bus, not just the possessed
    // one -- BusDashboardHUD only ever resolves the player's active bus.
    //
    // [FIX] Was caching the BusAudioEngine instance once in Awake(). Unity
    // doesn't guarantee component execution order -- if the controller
    // assigns its own audioEngine field AFTER this Awake() runs, we'd cache
    // null forever and the battery check below (`_audioEngine == null`)
    // would silently read that as "no reference, assume power's fine" for
    // the rest of the bus's life. Cache the stable controller references
    // instead and read .audioEngine off them live every frame.
    private NPCBusController        _npc;
    private BusSimulationController _sim;

    // [FIX] Same trap as BusExteriorLightController -- NPCBusController
    // stays present (just inert) on the player's own possessed bus, so
    // preferring it unconditionally meant this was reading the NPC's own
    // stale audioEngine instead of the one the dashboard's BAT button
    // actually drives. Mirrors BusDisplaySourceResolver's own possession
    // check: prefer the sim's engine when PlayerHandoff confirms THIS bus
    // is the one currently being driven, otherwise fall back to the NPC's.
    private BusAudioEngine AudioEngine
    {
        get
        {
            bool isPlayerDrivingThisBus =
                _sim != null
                && PlayerHandoff.Instance != null
                && PlayerHandoff.Instance.IsOnDuty
                && PlayerHandoff.Instance.playerBus == _sim;

            if (isPlayerDrivingThisBus) return _sim.audioEngine;
            return _npc != null ? _npc.audioEngine : (_sim != null ? _sim.audioEngine : null);
        }
    }
    private bool           _lastBatteryOn = true;

    private InteriorLightMode _lastAppliedMode;
    private bool              _lastAppliedDoorOpen;
    private bool              _dirty = true;

    private void Awake()
    {
        _npc = GetComponent<NPCBusController>();
        _sim = transform.root.GetComponentInChildren<BusSimulationController>(true);
        Apply();
    }

    private void Update()
    {
        bool batteryOn = AudioEngine == null || AudioEngine.batteryOn;
        if (batteryOn != _lastBatteryOn)
        {
            _lastBatteryOn = batteryOn;
            _dirty = true;
            Apply();
        }
    }

    public void SetMode(InteriorLightMode mode)
    {
        CurrentMode = mode;
        Apply();
    }

    public void CycleMode()
    {
        SetMode((InteriorLightMode)(((int)CurrentMode + 1) % 4));
    }

    /// <summary>Call from door open/close events. Door open forces every zone on
    /// at full intensity; door closed reverts to whatever mode was set, dimmed.</summary>
    public void SetDoorOpen(bool open)
    {
        IsDoorOpen = open;
        Apply();
    }

    private LightZone GetActiveZones()
    {
        switch (CurrentMode)
        {
            case InteriorLightMode.Off:                  return LightZone.None;
            case InteriorLightMode.AllOn:                return LightZone.All;
            case InteriorLightMode.LeftAndBackRightOnly: return zonesLeftAndBackRightOnly;
            case InteriorLightMode.MidBackRightAndLeft:  return zonesMidBackRightAndLeft;
            default:                                     return LightZone.None;
        }
    }

    private void Apply()
    {
        if (CurrentMode == _lastAppliedMode && IsDoorOpen == _lastAppliedDoorOpen && !_dirty) return;
        _lastAppliedMode     = CurrentMode;
        _lastAppliedDoorOpen = IsDoorOpen;
        _dirty               = false;

        if (zones == null) return;

        bool batteryOn = AudioEngine == null || AudioEngine.batteryOn;

        // Door open overrides the mode entirely — every zone on, full intensity.
        // Door closed reverts to whatever mode is selected, dimmed.
        // Battery off overrides EVERYTHING above it -- no power, no lights,
        // even if the door is standing open.
        LightZone active    = !batteryOn ? LightZone.None : IsDoorOpen ? LightZone.All : GetActiveZones();
        float     intensity = IsDoorOpen ? doorOpenIntensity : normalOnIntensity;

        foreach (var group in zones)
        {
            if (group == null) continue;
            bool on  = (active & group.zone) != 0;
            var  mat = on ? onMat : offMat;

            if (mat != null && group.renderers != null)
                foreach (var r in group.renderers)
                    if (r != null && r.sharedMaterial != mat) r.material = mat;

            if (group.lights != null)
                foreach (var l in group.lights)
                {
                    if (l == null) continue;
                    l.enabled = on;
                    if (on) l.intensity = intensity;
                }
        }
    }
}