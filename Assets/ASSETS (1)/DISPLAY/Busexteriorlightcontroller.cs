using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  BUS EXTERIOR LIGHT CONTROLLER
//
//  Headlights / brake lights / turn signals / hazards, pulled OUT of
//  BusDashboardHUD and given the same treatment BusInteriorLightController
//  already has: attach to each bus prefab, wire renderers/lights/materials
//  per bus type in the inspector, own its own state and blink timer.
//
//  BusDashboardHUD used to hold all these fields directly and drive the
//  materials/lights itself every frame regardless of which physical bus was
//  active -- meaning every bus effectively shared ONE set of light wiring
//  through whatever happened to be assigned on the HUD object. Same failure
//  mode interior lighting had before it got its own per-bus component. This
//  fixes it the same way: the HUD now just resolves this component off the
//  active bus (same as it already does for BusInteriorLightController) and
//  calls into it -- it owns none of the actual light state anymore.
// ─────────────────────────────────────────────────────────────────────────────
public class BusExteriorLightController : MonoBehaviour
{
    [Header("Headlights")]
    public Renderer[] headlightRenderers;
    public Material   headlightOnMat;
    public Material   headlightOffMat;
    public Light[]    headlights;

    [Header("Brake Lights")]
    public Light[]     brakeLights;
    public Renderer[]  brakeRenderers; // optional -- most rigs just use Light[], some also swap a lit-lens material
    public Material    brakeOnMat;
    public Material    brakeOffMat;
    [Tooltip("Dim red 'running light' material shown on the brakeRenderers whenever headlights are on but the brakes AREN'T applied -- real taillight lenses glow dim red as running lights and only go full-bright on brake. Leave null to fall back to brakeOffMat (old behavior: tail lens stays dark until braking).")]
    public Material    tailRunningMat;

    [Header("Turn Signals")]
    public Renderer[] leftSignalRenderers;
    public Renderer[] rightSignalRenderers;
    public Material   signalOnMat;
    public Material   signalOffMat;
    public Light[]    leftSignalLights;
    public Light[]    rightSignalLights;
    public float       blinkInterval = 0.5f;

    // ── Public state (read by the dashboard for button highlighting) ────────
    public bool HeadlightsOn  { get; private set; }
    public bool BrakeOn       { get; private set; }
    public bool LeftSignalOn  { get; private set; }
    public bool RightSignalOn { get; private set; }
    public bool HazardsOn     { get; private set; }

    [Header("Blinker sound (player bus only)")]
    [Tooltip("Optional: assign a real recording. The Xcelsior plays the SAME single 'tick' on every flash pulse (no tick/tock pair). Left empty, a small glass-bead 'tink' is synthesized in code.")]
    public AudioClip blinkClip;
    [Tooltip("The 'sucking on your teeth' c/tsk sound layered onto the tick. 0 = plain tick.")]
    [Range(0f, 1f)] public float blinkTskAmount = 0.40f;
    [Range(0f, 1f)] public float blinkSoundVolume = 0.55f;

    private AudioSource _blinkSource;
    private bool _engineTickMaybePlaying;
    // Master trim on the blinker sound: the whole tick+tsk plays at 60% of whatever blinkSoundVolume is set to
    // (applied in code so old serialized inspector values don't undo it).
    private const float BlinkVolumeTrim = 0.6f;
    private static AudioClip _genBead;
    private static float _genBeadInterval = -1f;

    private bool  _blinkState;
    private float _blinkTimer;
    private bool  _headlightsDirty = true;
    private bool  _lastHeadlightsOn;

    // [BATTERY] Same gate as BusInteriorLightController -- no battery, no
    // exterior lights either, regardless of headlight/signal/hazard state.
    // Self-resolved off whichever controller is on this GameObject so it
    // works for NPC buses too, not just the possessed one.
    //
    // [FIX] Was caching the BusAudioEngine instance itself once in Awake().
    // Unity doesn't guarantee component execution order -- if
    // NPCBusController/BusSimulationController assigns its own audioEngine
    // field later than THIS component's Awake() runs (its own Start(), or
    // lazily on spawn), we'd grab null and cache it permanently, and every
    // battery check downstream (`_audioEngine == null`) silently treated
    // "no reference yet" as "battery's fine" forever. Cache the stable
    // controller references instead and read .audioEngine off them live
    // every time -- correct regardless of when the engine gets assigned.
    private NPCBusController        _npc;
    private BusSimulationController _sim;

    // [FIX] Was just checking "_npc != null" and preferring it -- but
    // NPCBusController stays present (just disabled/inert) on the player's
    // OWN possessed bus after a handoff, per BusDisplaySourceResolver's own
    // comment on this exact trap: "NPCBusController goes inert on
    // possession, so its own state can't be trusted." That meant this was
    // always reading the NPC's own stale, never-touched audioEngine instead
    // of BusSimulationController's -- the one the dashboard's BAT button and
    // ignition system actually drive -- so the battery toggle silently had
    // no effect on lights. Same possession check BusDisplaySourceResolver
    // uses: prefer the sim's engine when PlayerHandoff says THIS bus is the
    // one currently being driven, otherwise fall back to the NPC's.
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

    /// <summary>Finds this bus's exterior light controller from ANY component on it: same object, then
    /// children, then parents, then anywhere under the bus root. (On articulated buses the controller
    /// sits on the front section, not the prefab root, so a plain GetComponent misses it.)</summary>
    public static BusExteriorLightController Find(Component c)
    {
        if (c == null) return null;
        var r = c.GetComponent<BusExteriorLightController>();
        if (r == null) r = c.GetComponentInChildren<BusExteriorLightController>(true);
        if (r == null) r = c.GetComponentInParent<BusExteriorLightController>(true);
        if (r == null) r = c.transform.root.GetComponentInChildren<BusExteriorLightController>(true);
        return r;
    }

    /// <summary>All signals off and the lenses reset. Toggles hazards on then off first so the lens
    /// materials/lights are forced through a full on->off cycle, clearing any stuck lit state at spawn.</summary>
    public void ResetSignalsClean()
    {
        SetHazards(true);
        SetHazards(false);
        SetLeftSignal(false);
        SetRightSignal(false);
        _blinkState = false;
        _blinkTimer = 0f;
        ApplySignalMaterials();
    }

    // ── Blinker sound: tick (lens on) / tock (lens off), only while the PLAYER is driving this bus ──
    private bool IsPlayerDrivenBus =>
        _sim != null && PlayerHandoff.Instance != null && PlayerHandoff.Instance.IsOnDuty && PlayerHandoff.Instance.playerBus == _sim;

    /// <summary>Softer, "melted" bead tick: the same measured partials (~3.1 / 2.7 / 4.9 kHz) but each doubled with a
    /// slightly detuned copy so they shimmer/blur instead of ringing pure, a ~2 ms rounded attack instead of a hard
    /// click, and a longer body that holds for ~50 ms before a smooth cosine release (rather than an exponential
    /// fade-out). Deterministic -- identical every time.</summary>
    private static AudioClip MakeBead(float blinkInterval)
    {
        const int sr = 44100;
        // Fixed 0.17 s tick (it fits inside any sensible blink interval, 0.5 s included).
        const float dur  = 0.17f;
        const float hold = 0.060f;       // body stays near full level this long
        const float tau  = 0.075f;
        int n = Mathf.CeilToInt(sr * dur);
        var d = new float[n];
        float[] fr = { 1900f, 1250f, 2900f };
        float[] lv = { 0.55f, 0.38f, 0.10f };
        uint seed = 12345u;
        float lp = 0f;                   // one-pole low-passed noise for a soft (not sharp) attack
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sr;
            float attack = Mathf.Clamp01(t / 0.002f);
            attack = attack * attack * (3f - 2f * attack);                       // smoothstep rise
            float release = t < hold ? 1f : 0.5f + 0.5f * Mathf.Cos(Mathf.PI * Mathf.Clamp01((t - hold) / (dur - hold)));
            float body = Mathf.Exp(-t / tau) * release;                        // long, gentle tail

            float v = 0f;
            for (int k = 0; k < fr.Length; k++)
            {
                // two slightly detuned copies -> slow beating = the "blur"
                v += Mathf.Sin(2f * Mathf.PI * fr[k] * 0.993f * t) * lv[k] * 0.5f;
                v += Mathf.Sin(2f * Mathf.PI * fr[k] * 1.007f * t) * lv[k] * 0.5f;
            }

            seed = seed * 1664525u + 1013904223u;
            float noise = ((seed >> 9) / (float)(1u << 23)) * 2f - 1f;
            lp += (noise - lp) * 0.25f;                                           // soften the noise
            float click = i < 90 ? lp * (1f - i / 90f) * 0.35f : 0f;

            d[i] = (v * body * attack + click) * 0.8f;
        }
        var clip = AudioClip.Create("blinkBead", n, 1, sr, false);
        clip.SetData(d, 0);
        return clip;
    }

    /// <summary>One identical tick per flash pulse -- both when the lenses light and when they go dark.</summary>
    private void PlayBlinkSound()
    {
        if (!IsPlayerDrivenBus) return;

        // Preferred path: render the tick inside the bus's own BusAudioEngine buffer, so it comes out of the same
        // source/volume/spatial setup as the rest of the bus and is audible everywhere (not one spot).
        var eng = AudioEngine;
        if (eng != null && blinkClip == null)
        {
            eng.blinkerTickVolume = blinkSoundVolume * BlinkVolumeTrim;
            eng.blinkerTskAmount = blinkTskAmount;
            eng.TriggerBlinkerTick();
            _engineTickMaybePlaying = true;
            return;
        }

        // Fallback (no engine yet, or a custom blinkClip assigned): plain 2D AudioSource.
        if (_blinkSource == null)
        {
            _blinkSource = gameObject.AddComponent<AudioSource>();
            _blinkSource.playOnAwake = false;
            _blinkSource.spatialBlend = 0f; // 2D: in the driver's ears regardless of where the lens is
        }
        if (blinkClip == null && (_genBead == null || !Mathf.Approximately(_genBeadInterval, blinkInterval)))
        {
            _genBead = MakeBead(blinkInterval);   // (re)generate if the blink interval changed
            _genBeadInterval = blinkInterval;
        }
        var clip = blinkClip != null ? blinkClip : _genBead;
        _blinkSource.PlayOneShot(clip, blinkSoundVolume);
    }

    /// <summary>Cuts the blinker sound dead (including any tick still ringing) the moment no signal/hazard is active.</summary>
    private void StopBlinkSoundIfOff()
    {
        if (LeftSignalOn || RightSignalOn || HazardsOn) return;
        CutBlinkSound();
    }

    private void CutBlinkSound()
    {
        if (_blinkSource != null && _blinkSource.isPlaying) _blinkSource.Stop();
        if (_engineTickMaybePlaying)
        {
            AudioEngine?.StopBlinkerTick();
            _engineTickMaybePlaying = false;
        }
    }

    private void OnEnable() => ResetSignalsClean(); // blinkers are off whenever a bus spawns / is (re)enabled

    private void Awake()
    {
        _npc = GetComponent<NPCBusController>();
        if (_npc == null) _npc = GetComponentInParent<NPCBusController>(true);
        if (_npc == null) _npc = transform.root.GetComponentInChildren<NPCBusController>(true);
        _sim = transform.root.GetComponentInChildren<BusSimulationController>(true);
        ApplyHeadlightMaterials(); // establish OFF state on spawn
        ApplyTailVisual();
    }

    private void Update()
    {
        bool batteryOn = AudioEngine == null || AudioEngine.batteryOn;
        bool batteryJustChanged = batteryOn != _lastBatteryOn;
        _lastBatteryOn = batteryOn;

        bool anyBlinking = batteryOn && (HazardsOn || LeftSignalOn || RightSignalOn);
        // Not blinking for ANY reason (signals off, or battery dead): cut the tick dead, don't let it ring out.
        if (!anyBlinking) CutBlinkSound();
        if (anyBlinking)
        {
            _blinkTimer += Time.deltaTime;
            if (_blinkTimer >= blinkInterval)
            {
                _blinkTimer = 0f;
                _blinkState = !_blinkState;
                ApplySignalMaterials();
                PlayBlinkSound();
            }
        }
        else if (_blinkState || batteryJustChanged)
        {
            StopBlinkSoundIfOff();
            _blinkState = false;
            ApplySignalMaterials();
        }

        if ((HeadlightsOn && batteryOn) != _lastHeadlightsOn || _headlightsDirty || batteryJustChanged)
        {
            _lastHeadlightsOn = HeadlightsOn && batteryOn;
            _headlightsDirty  = false;
            ApplyHeadlightMaterials();
        }

        if (batteryJustChanged && !batteryOn)
            ApplyBrakeLights(false);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PUBLIC CONTROL SURFACE -- dashboard/driver AI call these, nothing else
    //  writes to this component's state directly.
    // ═════════════════════════════════════════════════════════════════════════
    public void SetHeadlights(bool on)
    {
        if (on == HeadlightsOn) return;
        HeadlightsOn     = on;
        _headlightsDirty = true;
        ApplyTailVisual(); // [FIX] tail lens running-light state depends on headlights too, not just brake
    }
    public void ToggleHeadlights() => SetHeadlights(!HeadlightsOn);

    /// <summary>Call every frame with the current brake-should-be-lit
    /// condition (e.g. bkPd >= 0.2f || spd < 0.5f) -- only writes when it
    /// actually changes, same dirty-flag guard the old HUD code had.</summary>
    public void SetBrake(bool on)
    {
        if (on == BrakeOn) return;
        BrakeOn = on;
        bool batteryOn = AudioEngine == null || AudioEngine.batteryOn;
        // Light components (actual point/spot lights) only fire on real
        // braking -- that part was already correct. What was missing is the
        // lens MATERIAL: it used to just mirror the Light on/off state one
        // for one, so the tail lens sat dark the whole time headlights were
        // on and only lit up the instant you braked. ApplyTailVisual below
        // handles the material separately so it can show the dim
        // running-light tint while headlights are on, independent of brake.
        if (brakeLights != null)
            foreach (var l in brakeLights)
                if (l != null) l.enabled = on && batteryOn;
        ApplyTailVisual();
    }

    /// <summary>Sets the brake lens MATERIAL only (not the Light components,
    /// which SetBrake already handles) based on the current
    /// battery/headlight/brake state: full-bright when braking, dim
    /// "running light" tint when headlights are on but not braking, and
    /// fully off otherwise (or whenever the battery is dead).</summary>
    private void ApplyTailVisual()
    {
        if (brakeRenderers == null) return;
        bool batteryOn = AudioEngine == null || AudioEngine.batteryOn;

        Material mat;
        if (!batteryOn)                  mat = brakeOffMat;
        else if (BrakeOn)                mat = brakeOnMat;
        else if (HeadlightsOn)           mat = tailRunningMat != null ? tailRunningMat : brakeOffMat;
        else                              mat = brakeOffMat;

        if (mat == null) return;
        foreach (var r in brakeRenderers)
            if (r != null && r.sharedMaterial != mat) r.material = mat;
    }

    public void SetLeftSignal(bool on)
    {
        LeftSignalOn = on;
        if (on) RightSignalOn = false;
        if (!on) SetSignalGroup(leftSignalRenderers, false);
        StopBlinkSoundIfOff();
    }
    public void ToggleLeftSignal() => SetLeftSignal(!LeftSignalOn);

    public void SetRightSignal(bool on)
    {
        RightSignalOn = on;
        if (on) LeftSignalOn = false;
        if (!on) SetSignalGroup(rightSignalRenderers, false);
        StopBlinkSoundIfOff();
    }
    public void ToggleRightSignal() => SetRightSignal(!RightSignalOn);

    public void SetHazards(bool on)
    {
        HazardsOn = on;
        if (!on)
        {
            SetSignalGroup(leftSignalRenderers,  false);
            SetSignalGroup(rightSignalRenderers, false);
        }
        StopBlinkSoundIfOff();
    }
    public void ToggleHazards() => SetHazards(!HazardsOn);

    // ═════════════════════════════════════════════════════════════════════════
    //  MATERIAL / LIGHT APPLICATION -- moved verbatim from BusDashboardHUD
    // ═════════════════════════════════════════════════════════════════════════
    private void ApplyHeadlightMaterials()
    {
        bool effectiveOn = HeadlightsOn && (AudioEngine == null || AudioEngine.batteryOn);

        if (headlightRenderers != null)
        {
            var mat = effectiveOn ? headlightOnMat : headlightOffMat;
            if (mat != null)
                foreach (var r in headlightRenderers)
                    if (r != null && r.sharedMaterial != mat) r.material = mat;
        }
        if (headlights != null)
            foreach (var l in headlights)
                if (l != null) l.enabled = effectiveOn;
    }

    private void ApplyBrakeLights(bool on)
    {
        // [Superseded] Light-component on/off now happens inline in
        // SetBrake (needs the battery gate applied at toggle time, not just
        // here). Kept as a thin wrapper -- only real remaining caller is the
        // battery-just-turned-off path in Update(), which wants everything
        // forced off in one call including the tail material.
        if (brakeLights != null)
            foreach (var l in brakeLights)
                if (l != null) l.enabled = on;
        ApplyTailVisual();
    }

    private void ApplySignalMaterials()
    {
        bool leftLit  = _blinkState && (HazardsOn || LeftSignalOn);
        bool rightLit = _blinkState && (HazardsOn || RightSignalOn);

        SetSignalGroup(leftSignalRenderers,  leftLit);
        SetSignalGroup(rightSignalRenderers, rightLit);

        if (leftSignalLights  != null) foreach (var l in leftSignalLights)  if (l) l.enabled = leftLit;
        if (rightSignalLights != null) foreach (var l in rightSignalLights) if (l) l.enabled = rightLit;
    }

    private void SetSignalGroup(Renderer[] renderers, bool lit)
    {
        if (renderers == null) return;
        var mat = lit ? signalOnMat : signalOffMat;
        if (mat == null) return;
        foreach (var r in renderers)
            if (r != null && r.sharedMaterial != mat) r.material = mat;
    }
}