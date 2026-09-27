using System;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusAudioEngine.Combustion  —  physically-grounded combustion voice
//
//  Replaces the old Math.Sin-stack cores for the Cummins family with a real
//  firing-EVENT model. Nothing here is a bare sine: the engine tone is a
//  pulse train (one combustion event per power stroke), the knock is broadband
//  noise gated to that pulse, and the "hollow" is a pair of 2-pole resonant
//  cavities STRUCK by the firing pulses — a fixed-pitch tube ringing on top of
//  the RPM-tracked tone, which is what actually reads as hollow to the ear.
//
//  One shared diesel core + one shared gas core, because the real engines are
//  the same iron block with different calibration. Each engine is a thin
//  parameter set over those cores:
//
//     Diesel 8.9L :  ISL (2007-09)  →  ISL9 (2010-16)  →  L9 (2017+)
//     Diesel 6.7L :  ISB6.7 (2007-16)                 →  B6.7 (2017+)
//     Nat-gas 8.9L:  ISL G / Westport (2007-16)       →  L9N (2017+)
//
//  `refine`  0..1  older→newer: raises pilot injection (softer knock), drops
//                  clatter, slightly muffles the hollow (tighter modern exhaust).
//  `disp67`  true  = 6.7L: lighter sub, brighter/higher hollow formants, revvier.
//  `westport` true = ISL G teaser: rougher idle + occasional micro-misfire.
//  `newFlyerXD` true = 2014/15 New Flyer XD40/XD60: boxier, louder, lower
//                  hollow roar (see also the shorter-gear note at the bottom).
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{
    // ── Universal idle-settle (every engine, regardless of tx) ────────────────
    private float  idleStationaryTimer = 0f;
    private float  idleSettleAmount    = 0f;
    private double idleSettleLP        = 0.0;

    // ── L9 launch-window clatter->hollow swap (0-10kph) ────────────────────────
    // Real basis: diesel mechanical clatter/nailing is loudest at idle and
    // light throttle, and audibly quiets under real load -- piston/pin
    // pressure holds things tight under load, per direct forum-confirmed
    // mechanic explanation (wrist-pin knock specifically "disappears under
    // load because piston pressure holds the pin tight"). A launch (0-10kph,
    // heavy throttle, low gear -- the highest-load, lowest-speed condition
    // this engine sees) should read as a full, resonant, RPM-tracking pull
    // through the block/exhaust cavity, not sharp injector clatter. Smoothed
    // (not a hard on/off) so there's no audible snap crossing the 10kph line.
    private float  l9LaunchGateSmooth  = 0f;
    // Small RPM flare that happens right at launch (0-10kph) before the
    // torque converter grabs/locks -- a brief little rev-up, not a
    // continuous boost. Fires once per launch (edge-triggered on throttle
    // applied from near-stationary), peaks fast, decays back out over ~1s.
    private float  l9LaunchFlareEnv    = 0f;
    private bool   l9WasLaunching      = false;

    // ── Universal high-RPM smoothing (every engine, regardless of tx) ─────────
    // As RPM climbs, real engines read a bit smoother/more continuous -- the
    // per-firing texture blurs together at speed the way it doesn't at idle.
    // Blended in via a one-pole LP of the core voice, capped per-engine off
    // CombustionParams.refine (0 old .. 1 new) so an old, raw unit doesn't
    // get polished as smooth as a modern one at the same RPM.
    private double cbHighRpmSmoothLP   = 0.0;

    // ── Universal AC-off deepening (every engine, regardless of tx) ───────────
    // Real observation: diesel AND CNG both read deeper/less airy whenever
    // the AC compressor isn't engaged. Previously `acFrac` only did two
    // things -- quiet the engine slightly (drownFactor) and add the AC's
    // OWN hiss/fan/compressor-whine layer on top (DoUniversalACDSP) -- so
    // all the "airy" character with AC on came purely from that additive
    // layer, and nothing complementary happened to the engine's own core
    // tone. This adds that other half: a heavier one-pole LP than the
    // high-RPM smoothing above (much lower cutoff -- this genuinely trims
    // brightness, not just blurs firing texture), blended in as AC drops
    // toward off, universal across every engine core.
    private double cbAcOffDeepLP       = 0.0;

    // ── Universal tx air trim (every combustion-engine tx voice) ──────────────
    // Same idea as cbAcOffDeepLP but for txSample -- every gearbox DSP
    // function (Allison whine, Voith wail, ZF, B3400xFE, etc.) leans
    // noise_hi-heavy in its own whine/hiss layers, and there's no single
    // place any of them get trimmed together. Rather than hand-editing the
    // noise_hi ratio inside dozens of separate functions, one LP blend
    // here after the whole tx dispatch chain trims the airy top end off
    // the combined gearbox voice uniformly.
    private double cbTxAirLP           = 0.0;

    // ── Universal turbo flutter (every engine, regardless of tx) ──────────────
    private float  turboSpoolEnv       = 0f;
    private bool   turboWasSpooling    = false;
    private float  turboFlutterEnv     = 0f;
    private double ph_turboSpool;
    private double ph_turboFlutter;
    private double ph_turboFlutterChatter;

    // ── 2-pole state-variable resonator (Chamberlin). Struck by the firing
    //    pulse train to produce a hollow cavity ring at a fixed pitch. ────────
    private struct Resonator
    {
        public double low, band;
        // f = 2*sin(pi*Fc/SR) ; q1 = 1/Q. Returns band-pass output.
        public double Strike(double x, double f, double q1)
        {
            low  += f * band;
            double high = x - low - q1 * band;
            band += f * high;
            // light saturation keeps a hard strike from ringing into a whistle
            if (band >  4.0) band =  4.0;
            if (band < -4.0) band = -4.0;
            return band;
        }
    }

    // Hollow cavities — one boxy (body/underframe), one tubular (exhaust).
    private Resonator cb_resBox;
    private Resonator cb_resPipe;
    // Gas throttled-intake resonance (no diesel equivalent).
    private Resonator cb_resIntake;

    // Combustion phase accumulators (own set, independent of the legacy ph_e*).
    private double ph_cb_fire;     // firing / power-stroke rate  (RPM/20 * )
    private double ph_cb_sub;      // 0.5x firing — block thrum
    private float  cb_l9nWhineVol; // [NEW] signature L9N engine whine (engine-level, DoGasCore -- see there for full rationale)
    private double ph_cb_l9nWhine;
    private float  cb_invOverlayVol; // [NEW] L9/ISL9 "inverter" overlay, see DoDieselCore
    private double ph_cb_inv1;
    private double cb_invFilterMod = 1.0; // [NEW] current filter multiplier from the overlay -- read and applied in ProcessAudio, see there
    private float  cb_idleSurgePhase; // [NEW] slow multi-second CNG idle surge, see DoGasCore
    private float  cb_cngRegVolSmooth; // [NEW] CNG two-stage pressure regulator expansion flow, see DoGasCore
    private double ph_cb_cngReg;
    private double ph_cb_rot;      // 1x crank rotation — low lug
    private double ph_cb_pilot;    // pilot pre-pulse (phase-offset from fire)
    private double ph_cb_cam;      // 0.5x firing — valvetrain tick gate
    private double ph_cb_turbo;    // turbo whistle (shaft rotation)
    private double ph_cb_turboWhine; // [NEW] turbo whine (blade-pass, distinct from whistle)
    private double ph_cb_turboHunt;// ISL9 VGT actuator hunt LFO
    private double ph_cb_body;     // gas exhaust brightness formant

    // Gas idle character
    private float  cb_wobblePhase;      // slow loping-idle LFO accumulator
    private float  cb_misfireCooldown;  // Westport micro-misfire spacing
    private float  cb_misfireEnv;       // current misfire dropout envelope

    // Turbo spool smoothing + wastegate
    private float  cb_turboVolSmooth;
    private float  cb_prevLdForWg;
    private float  cb_wastegateVol;
    private double ph_cb_wastegate;

    // Gear-mesh "mechanical" echo whine — gear 3+, every transmission (not
    // Allison-only), see DoMechanicalWhine.
    private float   cb_mechWhineVol;
    private double  ph_cb_mechWhine;
    private double  ph_cb_mechWhine2;   // detuned partner -- beating -> hoarse texture
    private float   cb_mechGroanPhase;  // slow wobble -> groan character
    private float[] cb_mechEchoBuf;
    private int     cb_mechEchoHead;

    // ── Parameter block resolved per engineType ──────────────────────────────
    // ═══════════════════════════════════════════════════════════════════════
    //  [FIX] Turbo spool was two disconnected inputs: pitch driven by rn
    //  (RPM) alone, volume driven by ld (load) alone, via separate math.
    //  That's backwards — a real turbo's shaft speed depends on EXHAUST
    //  ENERGY, which is RPM and fueling/load TOGETHER, not either one alone.
    //  Idle RPM under a hard pull doesn't spool a turbo the way high RPM
    //  does even at the same load, and a turbo that's spinning faster is
    //  BOTH louder and higher-pitched at once — they aren't independent
    //  knobs. One shared spool state now drives both.
    //
    //  Also standardizes the blade-pass ratio. It was inconsistent across
    //  variants (L9 diesel core: 1.8x, ISL9: 2.4x, L9N: 2.4x) with no
    //  physical basis for any of the three specific numbers. Sourced parts
    //  data for this turbo family (Holset HE300VG-class, the unit this
    //  codebase's own comments already confirm is shared across ISL9/L9)
    //  confirms a stock 6/6 split-blade compressor wheel — 6 primary
    //  blades. Real turbocharger acoustics research confirms whine
    //  frequency = shaft rotation rate x blade count (this is literally
    //  what separates "whine" from "whistle," which is shaft rate alone).
    //  A prior pass in this file tried a literal blade-pass multiple at a
    //  much higher whistle-frequency ceiling and reverted it as "too high"
    //  per direct listening feedback — kept that lesson: the true 6x ratio
    //  is used here, but the whistle's own frequency ceiling is kept modest
    //  (well under the real ~1600Hz+ a genuinely spooled HD turbo whistle
    //  can reach) specifically so 6x lands in an audible, not-shrill upper
    //  register instead of the far-ultrasonic range true full-boost shaft
    //  speeds (100,000-200,000 rpm) would actually produce.
    // ═══════════════════════════════════════════════════════════════════════
    private const float TURBO_BLADE_COUNT = 6f;

    private float ResolveTurboSpool(float rn, float ld)
    {
        float rpmFrac  = rn > 0.05f ? Mathf.Clamp01((rn - 0.05f) / 0.9f) : 0f;
        // Load can't spool a turbo that isn't turning, and RPM alone barely
        // spools one that isn't being fed fuel — floor keeps light-load idle
        // RPM from reading as a fully-spooled turbo, midpoint keeps a
        // moderate throttle from sounding unspooled.
        float loadGate = Mathf.Lerp(0.30f, 1.0f, Mathf.Clamp01(ld));
        return rpmFrac * loadGate;
    }

    private struct CombustionParams
    {
        public bool  isGas;
        public bool  disp67;      // 6.7L displacement class
        public float refine;      // 0 old .. 1 new
        public float knockK;      // firing-pulse sharpness exponent
        public float pilot;       // pilot-injection shadow amount (softens knock)
        public float clatterGain; // diesel nailing amount
        public float boxHz, boxQ, boxGain;    // body hollow cavity
        public float pipeHz, pipeQ, pipeGain; // exhaust hollow cavity
        public float subGain;     // block thrum weight
        public float subShakeGain; // [NEW] additional load/rev-dependent sub punch on top of subGain -- 0 for every engine except ISL9 (relies on CombustionParams p = default zero-init, same as every other field here), where it models the "shake" the reference recording has under hard load
        public bool  turboHunt;   // ISL9 loose-actuator surge
        public float turboBright; // whistle center scaler
        public bool  westport;    // gas: rough ISL G teaser
        public bool  newFlyerXD;  // ISL9 New Flyer variant
        public float rpmSmoothCap; // [NEW] per-engine ceiling on the universal high-RPM smoothing blend (DoCombustionEngine) -- set explicitly per case below, NOT derived off `refine`, since two engines can share the same refine tier (L9/B67/B72/L9N all =1.0) while still being mechanically different enough to smooth differently at redline (diesel knock vs. knock-free spark ignition, displacement class, generation within the same tier).
    }

    private CombustionParams ResolveCombustion()
    {
        CombustionParams p = default;
        switch (engineType)
        {
            // ── 8.9L diesel line ───────────────────────────────────────────
            case EngineType.ISL:   // 2007-09, oldest, rawest
                p.refine = 0.0f;  p.knockK = 3.0f; p.pilot = 0.05f; p.clatterGain = 0.030f;
                p.boxHz = 122f; p.boxQ = 5.0f; p.boxGain = 0.55f;
                p.pipeHz = 268f; p.pipeQ = 7.5f; p.pipeGain = 0.62f;
                p.subGain = 0.90f; p.turboHunt = false; p.turboBright = 0.90f;
                // [RPM SMOOTH CAP] Oldest, rawest diesel in the fleet -- mechanical
                // injection-era character (knockK=3.0, highest in the whole table),
                // should stay genuinely ragged even wound out, not politely blur.
                p.rpmSmoothCap = 0.40f;
                break;
            case EngineType.ISL9:  // 2010-16, EPA13, loose VGT → hunt
                // [RETUNED against a real reference recording — 2014 ISL9]
                // boxHz/pipeHz raised ~5% (reads a bit higher-pitched than
                // the previous tune) and boxGain/pipeGain trimmed down
                // slightly (reads lighter/less muffled) -- subGain is
                // UNCHANGED here on purpose: the "able to shake the thing"
                // quality is handled below as a dynamic, load-dependent sub
                // punch rather than a flat weight increase, since the
                // reference clip's low end comes and goes with revs/load
                // rather than sitting constantly heavy.
                // [PUSHED FURTHER] Higher and lighter again -- boxHz/pipeHz
                // up another ~6%, boxGain/pipeGain trimmed further. subGain
                // also trimmed slightly (0.85 -> 0.78) for a genuinely
                // lighter baseline weight, while subShakeGain below still
                // gives it real punch/depth dynamically under load.
                // [PUSHED AGAIN — design-intent based, NOT video-grounded]
                // Checked the reference clip's actual spectral content for
                // "higher pitched/hollow/airy" specifically: 95% of ALL
                // energy in the whole 63s clip sits below 458Hz, and the
                // single loudest high-frequency (1.5-8kHz) moment in the
                // entire recording is still 102x quieter than the loudest
                // low-frequency moment. That's almost certainly the
                // recording itself (exterior, distance-shot bus-spotting
                // footage — high frequencies attenuate far faster over
                // distance than low ones, on top of phone-mic rolloff/noise
                // suppression), not the real bus, so this video genuinely
                // can't ground a "brighter/airier" push — it barely
                // captured any high end at all, in either direction. This
                // pass instead pushes boxHz/pipeHz/boxGain/pipeGain/
                // turboBright further based on the existing hollow-
                // resonance/turbo-whistle design intent alone.
                // [REAL RESEARCH — not the reference clip, which couldn't
                // ground this] A heavy-duty diesel engine noise study
                // confirms actual noise energy concentrates in the 1000-
                // 2500Hz band ("engine block radiated noise and intake/
                // exhaust turbulence noise dominate" above 1000Hz), with
                // the 125-500Hz band (where pipeHz used to sit) being a
                // real but MINOR contributor from combustion/piston knock
                // specifically -- not the dominant character. boxHz stays
                // where it is (that minor low-frequency contribution is
                // real too), pipeHz raised into the actually-dominant
                // band so the hollow-tube layer can read as the real
                // mid-high character instead of also living down in the
                // minor low-frequency zone.
                p.refine = 0.45f; p.knockK = 2.7f; p.pilot = 0.18f; p.clatterGain = 0.020f;
                p.boxHz = 158f; p.boxQ = 5.5f; p.boxGain = 0.43f;
                p.pipeHz = 1450f; p.pipeQ = 6.5f; p.pipeGain = 0.47f;
                p.subGain = 0.78f; p.turboHunt = true;  p.turboBright = 1.15f;
                // [RPM SMOOTH CAP] Mid-tier EPA13 unit -- meaningfully more
                // refined than ISL (multi-injection over mechanical), but the
                // loose-actuator turbo hunt (turboHunt=true) is itself an
                // irregular/surging character that a heavy smoothing blend
                // would work against, so this stays well under L9's cap.
                p.rpmSmoothCap = 0.85f;
                break;
            default: // EngineType.L9 — 2017+, refined multi-injection, tight turbo
                // [REAL RESEARCH — same basis as ISL9 above] pipeHz raised
                // into the confirmed-dominant 1000-2500Hz heavy-duty diesel
                // noise band; boxHz stays as the real but minor low-
                // frequency combustion/knock contribution.
                p.refine = 1.0f;  p.knockK = 2.3f; p.pilot = 0.34f; p.clatterGain = 0.012f;
                p.boxHz = 148f; p.boxQ = 6.0f; p.boxGain = 0.48f;
                p.pipeHz = 1600f; p.pipeQ = 6.5f; p.pipeGain = 0.52f;
                p.subGain = 0.82f; p.turboHunt = false; p.turboBright = 1.20f;
                // [RPM SMOOTH CAP] The refined baseline the rest of the table
                // is judged against -- tight turbo, real multi-injection
                // (pilot=0.34, highest of any diesel here), lowest diesel
                // knockK (2.3). Gets the full diesel ceiling.
                p.rpmSmoothCap = 1.50f;
                break;

            // ── 6.7L diesel line (lighter, brighter, revvier) ──────────────
            case EngineType.ISB67: // 2007-16
                p.disp67 = true;  p.refine = 0.0f; p.knockK = 3.1f; p.pilot = 0.06f; p.clatterGain = 0.032f;
                p.boxHz = 158f; p.boxQ = 4.5f; p.boxGain = 0.46f;
                p.pipeHz = 332f; p.pipeQ = 7.0f; p.pipeGain = 0.52f;
                p.subGain = 0.60f; p.turboHunt = false; p.turboBright = 1.10f;
                // [RPM SMOOTH CAP] Same 2007-16 old-generation era as ISL
                // (knockK=3.1, actually the single highest in the fleet) but
                // the smaller/lighter 6.7L block revs quicker and thinner --
                // kept just a hair above ISL's 0.40 rather than identical,
                // since it isn't literally the same engine, just the same age.
                p.rpmSmoothCap = 0.42f;
                break;
            case EngineType.B67:   // 2017+
                p.disp67 = true;  p.refine = 1.0f; p.knockK = 2.4f; p.pilot = 0.32f; p.clatterGain = 0.013f;
                p.boxHz = 166f; p.boxQ = 5.0f; p.boxGain = 0.40f;
                p.pipeHz = 340f; p.pipeQ = 7.5f; p.pipeGain = 0.46f;
                p.subGain = 0.58f; p.turboHunt = false; p.turboBright = 1.15f;
                // [RPM SMOOTH CAP] Same 2017+ generation as L9 (tight turbo,
                // real multi-injection) but the smaller 6.7L block is a touch
                // rawer/more present per its own knockK=2.4 vs L9's 2.3 -- so
                // it sits just under L9's max rather than sharing it outright.
                p.rpmSmoothCap = 1.45f;
                break;
            case EngineType.B72:   // 2027+ (HELM platform) -- real, confirmed
                // direct successor to B6.7 (Cummins' own materials): 7.2L
                // (up from 6.7L), 240-340hp/650-1000 lb-ft, factory stop-
                // start, and explicitly "the increase in peak cylinder
                // pressure capability, allowing us to extract the energy
                // from the fuel more effectively" -- higher peak pressure
                // extracting MORE useful energy per event (rather than
                // venting it as wasted knock) reads as slightly SOFTER
                // knock than B67's, not harsher -- knockK/clatterGain
                // trimmed down a touch from B67. Still the same disp67
                // "lighter/brighter/revvier" cavity family, just a larger
                // block: boxHz/pipeHz nudged down slightly (bigger
                // displacement, marginally deeper resonance) and subGain
                // raised a touch (more low-end mass from the larger block).
                p.disp67 = true;  p.refine = 1.0f; p.knockK = 2.2f; p.pilot = 0.36f; p.clatterGain = 0.010f;
                p.boxHz = 158f; p.boxQ = 5.0f; p.boxGain = 0.41f;
                p.pipeHz = 335f; p.pipeQ = 7.5f; p.pipeGain = 0.47f;
                p.subGain = 0.64f; p.turboHunt = false; p.turboBright = 1.18f;
                // [RPM SMOOTH CAP] Newest/most refined diesel of the whole
                // fleet -- softest knock (knockK=2.2, lowest of any diesel
                // here), highest pilot-injection shadow (0.36), and per its
                // own header comment, higher peak cylinder pressure that
                // "extracts the energy from the fuel more effectively"
                // instead of venting it as knock. That's the same real
                // mechanism the smoothing blend is modeling, so this gets
                // the max ceiling, matching L9's.
                p.rpmSmoothCap = 1.50f;
                break;

            // ── 10.0L diesel (Cummins X10, 2026+, replaces L9/X12) ──────────
            case EngineType.X10:
                // [COMPLETE REBUILD] Real specs confirmed via Cummins' own
                // Feb 2025 X10 launch materials: 10.0L displacement (up from
                // L9's 8.9L), inline-6, wet-sleeve block, 320-450hp/1,000-
                // 1,650 lb-ft -- genuinely the biggest, most powerful diesel
                // in this fleet. This replaces an old bespoke sine-stack
                // core that started deep and drifted high-pitched over
                // edits; X10 is rebuilt from L9's real architecture
                // (DoX10RealCore, not this generic dispatch -- see the
                // core-selection ternary above) with parameters sized UP
                // for the bigger block, not just copy-tuned from L9.
                //
                // "Deep" here comes from real acoustic levers, not just
                // turning a knob down: boxHz sits genuinely lower than
                // every 8.9L/6.7L engine in this fleet (bigger physical
                // cavity resonates lower -- basic acoustics, a bigger drum
                // sounds deeper), and subGain is the heaviest in the fleet
                // (more low-end rotating mass). pipeHz is trimmed down from
                // L9's 1600Hz but deliberately kept inside the confirmed-
                // dominant 1000-2500Hz heavy-duty-diesel noise band from
                // the same research L9/ISL9 are grounded in -- at the LOW
                // end of that real band, not below it, so "deep" doesn't
                // contradict the acoustic research already established
                // elsewhere in this file.
                p.refine = 0.85f; p.knockK = 2.6f; p.pilot = 0.26f; p.clatterGain = 0.017f;
                p.boxHz = 128f; p.boxQ = 5.5f; p.boxGain = 0.54f;
                p.pipeHz = 1180f; p.pipeQ = 6.5f; p.pipeGain = 0.50f;
                p.subGain = 0.88f; p.turboHunt = false; p.turboBright = 0.82f; // lower turboBright -- bigger compressor wheel for the higher torque ceiling spins slower for a given boost, same reasoning as the L9N turbo research
                // [RPM SMOOTH CAP] Modern-generation EPA-class electronics
                // (refine=0.85) but genuinely the biggest, highest-peak-
                // pressure diesel here (320-450hp/1,000-1,650 lb-ft) -- also
                // deliberately detuned rougher this session (drive 1.9->2.05,
                // knock floor 0.16->0.19, per direct instruction) so it
                // shouldn't get smoothed back to L9/B72's max at redline.
                // Sits above the old-generation units but clearly under the
                // fully-refined 2017+ tier.
                p.rpmSmoothCap = 1.15f;
                break;

            case EngineType.ISLG:  // ISL G / Westport, 2007-16 (teaser)
                p.isGas = true; p.westport = true; p.refine = 0.2f; p.knockK = 1.5f; p.pilot = 0f; p.clatterGain = 0f;
                p.boxHz = 138f; p.boxQ = 4.0f; p.boxGain = 0.40f;
                p.pipeHz = 300f; p.pipeQ = 6.0f; p.pipeGain = 0.44f;
                p.subGain = 0.78f; p.turboBright = 0.85f;
                // [RPM SMOOTH CAP] Old 2007-16 ignition module (same era as
                // ISL/ISB67), but spark ignition has no compression-ignition
                // knock to begin with (knockK=1.5, clatterGain=0) -- already
                // mechanically smoother than any diesel here regardless of
                // age, plus this variant's own micro-misfire dropout (see
                // DoGasCore) benefits from a touch of RPM-driven smoothing to
                // read as settling rather than staying choppy forever. Higher
                // than the old diesels, still well under the modern tier.
                p.rpmSmoothCap = 1.05f;
                break;
            case EngineType.L9N:   // 2017+ in-house ignition
                // [REBUILT, using L9 as a base] Real spec: Cummins confirms
                // the L9N "engine block is shared with the Cummins L9
                // diesel — a full skirted block for increased rigidity and
                // strength." 8.9L, inline-6, 320 hp / 1,000 lb-ft. So this
                // now literally starts from L9's own boxHz/pipeHz/boxQ/pipeQ
                // (same physical block/cavities resonating), not a
                // separately-invented preset, with deltas only where a real
                // mechanical difference actually exists:
                //   · Spark-ignited stoichiometric combustion (not diesel
                //     compression-ignition) -> no pilot injection concept,
                //     no diesel knock/nailing at all (knockK dropped well
                //     below L9's 2.3, pilot=0, clatterGain=0).
                //   · Maintenance-free 3-way catalyst (car-like, unlike
                //     lean-burn diesel aftertreatment) -> genuinely
                //     quieter/more muffled overall, per Cummins' own
                //     "quiet operation" language repeated across every L9N
                //     spec sheet.
                //   · refine=1.0 kept from L9 (same modern EGR/ECM-era
                //     engineering generation).
                p.isGas = true; p.refine = 1.0f; p.knockK = 1.05f; p.pilot = 0f; p.clatterGain = 0f; // [SOFTENED] knockK eased further toward 1.0 -- smoother, less edge on the firing pulse
                p.boxHz = 141f; p.boxQ = 6.0f; p.boxGain = 0.31f;   // [TRIMMED] pulled back down for a quieter core -- was 0.36
                p.pipeHz = 300f; p.pipeQ = 8.5f; p.pipeGain = 0.35f; // [TRIMMED] pulled back down for a quieter core -- was 0.40
                p.subGain = 0.68f; p.turboBright = 0.90f; // [TRIMMED] subGain eased down slightly -- was adding weight/roughness underneath
                // [RPM SMOOTH CAP] Distinct from L9 despite sharing refine=1.0
                // (per direct instruction -- same tier isn't the same cap).
                // Modern ECU + spark-ignited stoichiometric combustion with
                // NO knock/pilot concept at all (knockK dropped to 1.05,
                // pilot=0, clatterGain=0) is the smoothest-firing engine in
                // the whole fleet by construction -- gets the highest cap of
                // any engine, edging out even L9/B72's diesel max.
                p.rpmSmoothCap = 1.50f;
                break;
        }
        // Fallback safety: any engine case that didn't explicitly set
        // rpmSmoothCap above (shouldn't happen -- every case now does)
        // still gets a sane default rather than 0f, which would silently
        // disable the universal high-RPM smoothing entirely for it.
        if (p.rpmSmoothCap <= 0f) p.rpmSmoothCap = 1.0f;
        // ── Hollow roar — ISL/ISL9 default character ─────────────────────────
        // This box/pipe resonance boost used to be gated behind the
        // newFlyerXD toggle only. Per spec, both ISL and ISL9 now get it as
        // their NORMAL default character (not a special-variant-only
        // effect) -- ISL gets it amplified further than ISL9, since it's
        // the older/rawer/rowdier unit of the two (see its knockK=3.0 vs
        // ISL9's 2.7 and clatterGain 0.030 vs 0.020 above -- ISL was
        // already tuned as the roughest of the family, this extends that
        // same relationship into the hollow-roar layer). newFlyerXD still
        // stacks multiplicatively on top for that specific body variant,
        // it isn't being replaced, just no longer the only way to get any
        // hollow roar at all.
        if (engineType == EngineType.ISL9)
        {
            // [RETUNED] 1.35/1.38 -> 1.22/1.25 -- reference recording reads
            // lighter/less muffled than the previous boost gave it. ISL and
            // the L9/L9N-kickdown-borrowed version below are UNCHANGED —
            // this trim is ISL9-specific only.
            // [PUSHED FURTHER] 1.22/1.25 -> 1.12/1.15 -- lighter again, per
            // "higher pitched and lighter sounding" follow-up.
            p.boxGain  *= 1.12f; p.boxQ  *= 1.08f;
            p.pipeGain *= 1.15f; p.pipeQ *= 1.05f;

            // [NEW] "able to shake the thing" — a dynamic sub-bass punch
            // that scales up specifically under hard load/high revs, not a
            // flat subGain increase. The reference clip's low end comes and
            // goes with revs rather than sitting constantly heavy, so this
            // rides on top of the base subGain (0.85, unchanged) as an
            // additional load-dependent multiplier read at the call site
            // via p.subShakeGain below.
            p.subShakeGain = 0.45f; // additional multiplier at full load/high rev, 0 at idle/light load
        }
        else if (engineType == EngineType.ISL)
        {
            // Amplified further than ISL9 -- the older, rawer engine roars harder.
            p.boxGain  *= 1.65f; p.boxQ  *= 1.15f;
            p.pipeGain *= 1.70f; p.pipeQ *= 1.10f;
        }
        else if ((engineType == EngineType.L9 || engineType == EngineType.L9N) && kickdownKey && (IsAllison() || tx == "b500r"))
        {
            // Kickdown-triggered version of the ISL9 hollow roar, for
            // L9/L9N+B400R/B500R specifically -- same boost level as
            // ISL9's permanent default, but only active while kickdown
            // is held.
            p.boxGain  *= 1.35f; p.boxQ  *= 1.08f;
            p.pipeGain *= 1.38f; p.pipeQ *= 1.05f;
        }
        // New Flyer 2014/15 XD variant — boxier, louder, lower hollow roar.
        // Stacks on top of the default boost above for ISL9 specifically.
        if (engineType == EngineType.ISL9 && newFlyerXD)
        {
            p.newFlyerXD = true;
            p.boxHz  *= 0.88f;  p.boxGain  *= 1.55f;  p.boxQ  *= 1.15f;
            p.pipeHz *= 0.90f;  p.pipeGain *= 1.60f;  p.pipeQ *= 1.10f;
        }
        return p;
    }

    // Inspector/owner-settable variant flag (NPCBus / BSC can toggle per instance).
    public bool newFlyerXD = false;

    // Firing-pulse shaper: positive half-sine raised to k → a combustion bump.
    private static double FirePulse(double phase, float k)
    {
        double s = Math.Sin(2.0 * Math.PI * phase);
        if (s <= 0.0) return 0.0;
        return Math.Pow(s, k);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  MAIN COMBUSTION VOICE — call this in place of the old L9N/L9/ISL9 blocks.
    //  Produces the engine tone into engineSample, then dispatches the existing
    //  transmission DSP into txSample exactly as the legacy branches did.
    // ═════════════════════════════════════════════════════════════════════════
    private void DoCombustionEngine(ref double engineSample, ref double txSample,
        float hz, float rn, float ld, float outRPM, bool retAct,
        float engVolPersonality, float acFrac,
        double noiseHp, double noiseClt, double noiseInd, double invSR)
    {
        CombustionParams p = ResolveCombustion();

        // [FIX] Previously the rated-tier torque bump was a FLAT multiplier
        // applied once to the outer `ld` scalar in the caller (same ratio at
        // every RPM, since both tiers share one curve shape) -- that never
        // actually produced the "lugs harder specifically down low" behavior
        // a hotter tune should have. Doing it HERE instead, weighted by rn,
        // means the tier's extra torque is front-loaded into the low-RPM
        // range (where a hotter diesel tune is actually most audible -- the
        // fat part of a real torque curve) and tapers off by mid-range,
        // rather than boosting every single `ld *` line in this function
        // uniformly regardless of where in the rev range we are.
        float tierExtra   = Mathf.Max(0f, (PeakTorqueNm(ratedTier) / PeakTorqueNm(FleetSeriesDefinition.RatedPowerTier.HP280)) - 1f);
        float lowRpmBias  = Mathf.Clamp01(1f - (rn - 0.15f) / 0.45f); // 1.0 at/under rn=0.15, tapers to 0 by rn=0.6
        ld = Mathf.Clamp01(ld * (1f + tierExtra * lowRpmBias * 1.4f));

        float drownFactor = Mathf.Min(gear <= 1 ? acFrac * 0.25f : acFrac * (0.42f + gear * 0.14f), acFrac * 0.55f);
        float engMul = (1.0f - drownFactor) * npcVolumeScale * engVolPersonality;

        double core = p.isGas ? DoGasCore(p, hz, rn, ld, engMul, noiseClt, noiseInd, invSR)
            : engineType == EngineType.ISL9 ? DoISL9Core(p, hz, rn, ld, engMul, noiseClt, noiseInd, invSR)
            : engineType == EngineType.L9   ? DoL9Core(p, hz, rn, ld, engMul, noiseClt, noiseInd, invSR)
            : engineType == EngineType.X10  ? DoX10RealCore(p, hz, rn, ld, engMul, noiseClt, noiseInd, invSR)
            : DoDieselCore(p, hz, rn, ld, engMul, noiseClt, noiseInd, invSR);

        engineSample = core;

        // [ADD] Gillig is genuinely more hollow-bodied than the Xcelsior --
        // engine and transmission whine both read louder/more present
        // through the chassis. Boosted centrally here (post-core) rather
        // than touching each individual DoXCore/hollow calculation
        // separately -- one multiplier covers every engine variant at once.
        if (isGillig) engineSample *= 1.28;

        // [ADD] Gillig post-shift stall/rattle burst -- rattly, slightly
        // detuned two-tone mis-fire-adjacent artifact, decaying fast (see
        // gilligRattleVol's 0.90/tick decay in Tick()). Added into txSample
        // (driveline-adjacent) rather than engineSample (pure combustion),
        // since it's meant to read as a mechanical/transmission artifact
        // right after the shift, not an engine-firing event.
        if (gilligRattleVol > 0.001f)
        {
            ph_gilligRattle1 += 145.0 * invSR;
            ph_gilligRattle2 += 151.0 * invSR; // close detune -> beating/rattly, not a clean tone
            double rattle = (Math.Sign(Math.Sin(2.0 * Math.PI * ph_gilligRattle1)) * 0.5
                           + Math.Sign(Math.Sin(2.0 * Math.PI * ph_gilligRattle2)) * 0.5) // square-ish -> harsher, more "rattle" than sine
                          * NextNoiseSample() * 0.5 + 0.5; // noise-modulated so it reads as mechanical clatter, not a tone
            txSample += rattle * gilligRattleVol * engMul * npcVolumeScale;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UNIVERSAL HIGH-RPM SMOOTHING  (every engine, regardless of tx)
        //  Real engines read progressively smoother/more continuous as RPM
        //  climbs -- individual firing events blur together into more of a
        //  drone at speed than they do lugging near idle. Modeled as a
        //  one-pole low-pass of the core voice, blended in proportionally
        //  to rn (normalized RPM), capped per-engine so this doesn't just
        //  flatten every engine into the same polished tone: newer/refined
        //  units (CombustionParams.refine near 1, e.g. L9/B67/B72) get up
        //  to a real 1.5x smoothing multiplier at redline, while an old,
        //  raw unit (refine near 0, e.g. ISL/ISB67) should still sound
        //  raw even revving hard, so its cap is pulled well down instead
        //  of hitting that same 1.5x ceiling.
        // ═════════════════════════════════════════════════════════════════════
        // ═════════════════════════════════════════════════════════════════════
        //  UNIVERSAL AC-OFF DEEPENING  (every engine, regardless of tx)
        //  See cbAcOffDeepLP's header comment -- AC on brightens the mix via
        //  its own additive hiss/fan/whine layer; AC off should complement
        //  that by having the engine's own core lean a bit deeper/less
        //  bright, not just sit unchanged with nothing playing on top of it.
        // ═════════════════════════════════════════════════════════════════════
        {
            cbAcOffDeepLP += (engineSample - cbAcOffDeepLP) * 0.015; // heavier cutoff than the RPM-smoothing LP -- actually trims brightness
            float acOffAmount  = 1f - Mathf.Clamp01(acFrac); // 1 = AC fully off, 0 = AC fully on
            // [BASELINE FLOOR] Per correction -- every engine reads too airy
            // even with AC fully on and rn near 0 (idle), not just when AC
            // switches off. 0.09 floor is always present regardless of AC
            // state; acOffAmount still adds more on top of it as AC drops
            // out, same as before.
            float acDeepenBlend = 0.09f + acOffAmount * 0.18f;
            engineSample = engineSample * (1.0 - acDeepenBlend) + cbAcOffDeepLP * acDeepenBlend;
        }

        {
            cbHighRpmSmoothLP += (engineSample - cbHighRpmSmoothLP) * 0.05;
            // [PER-ENGINE TABLE] p.rpmSmoothCap is now set explicitly per
            // engine case in ResolveCombustion() above, not derived off
            // `refine` -- two engines can share the same refine tier
            // (L9/B67/B72/L9N are all refine=1.0) while still needing
            // different smoothing ceilings (diesel knock vs. knock-free
            // spark ignition, displacement class, how hard this session
            // deliberately roughened X10's core, etc). See each case's own
            // comment for the real reasoning behind its number.
            float smoothBlend = Mathf.Clamp01(rn) * 0.20f * p.rpmSmoothCap;
            smoothBlend       = Mathf.Clamp01(smoothBlend);
            engineSample      = engineSample * (1.0 - smoothBlend) + cbHighRpmSmoothLP * smoothBlend;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UNIVERSAL IDLE-SETTLE  (every engine, regardless of tx)
        //  Sitting at 0 kph for a while, the distinct "chug chug chug"
        //  per-firing texture gradually smooths into a continuous "gggghuuu"
        //  hum -- a one-pole low-pass of the core voice, crossfaded in as a
        //  real settling process rather than a hard switch. Settles in over
        //  ~15-20s stationary, fades back to chuggy gradually on moving
        //  again (not an instant reset) per spec.
        // ═════════════════════════════════════════════════════════════════════
        bool stationary = spd < 0.5f;
        if (stationary) idleStationaryTimer += (float)invSR;
        else            idleStationaryTimer = 0f;

        float settleTarget = Mathf.Clamp01((idleStationaryTimer - 15f) / 5f); // ramps in 15s->20s
        // Asymmetric smoothing: settling in takes its natural ramp (handled by
        // the Clamp01 above happening over real elapsed time), but fading back
        // to chuggy on movement is deliberately SLOW (0.15/sec) rather than an
        // instant reset -- "gradual fade back to chuggy", not a hard cut.
        idleSettleAmount += (settleTarget - idleSettleAmount) * (settleTarget > idleSettleAmount ? 1f : 0.15f) * (float)invSR * 4f;
        idleSettleAmount = Mathf.Clamp01(idleSettleAmount);

        if (idleSettleAmount > 0.001f)
        {
            // One-pole low-pass -- smooths away the sharp per-firing transient
            // content, leaving the same fundamental pitch but as a continuous
            // blended tone instead of a chug. Cutoff chosen to keep pitch
            // recognizable while genuinely smoothing texture, not muffling
            // the whole engine into mush.
            idleSettleLP += (engineSample - idleSettleLP) * 0.012;
            engineSample = engineSample * (1.0 - idleSettleAmount) + idleSettleLP * idleSettleAmount;
        }
        else
        {
            idleSettleLP = engineSample; // stay ready to smoothly pick up the moment settling starts again
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UNIVERSAL TURBO FLUTTER  (every engine, regardless of tx)
        //  Real phenomenon: blip the throttle, boost starts building (rising
        //  pitch "spool" character), then release -- the turbo's still
        //  spinning with nowhere for that pressurized air to go, forcing
        //  back through the compressor as a fluttering/chattering burst
        //  before settling back to idle. Gated to the 0-5 kph launch-adjacent
        //  window specifically, per spec, not gear- or tx-specific.
        // ═════════════════════════════════════════════════════════════════════
        bool inFlutterWindow = spd >= 0f && spd <= 5f;
        if (inFlutterWindow && ld > 0.25f)
        {
            // Spooling -- throttle applied, ramp a rising pitch/volume layer.
            turboSpoolEnv += (1f - turboSpoolEnv) * 0.10f;
            turboWasSpooling = true;
        }
        else
        {
            if (turboWasSpooling && turboSpoolEnv > 0.15f && inFlutterWindow)
            {
                // Release detected while still in the window and boost had
                // actually built up -- fire the flutter burst.
                turboFlutterEnv = 1f;
            }
            turboWasSpooling = false;
            turboSpoolEnv *= 0.90f;
        }

        // [REMOVED] the audible "eeeee" spool tone -- fired on every single
        // throttle application in the 0-5kph window, which in practice meant
        // constantly on every launch, not just occasionally. turboSpoolEnv
        // itself is KEPT (silently) since the flutter burst below still uses
        // it to know whether a real blip-then-release actually happened.

        if (turboFlutterEnv > 0.005f)
        {
            // Fluttering/chattering burst -- an amplitude-modulated noise+tone
            // stutter (the "ch-ch-ch" character real turbo flutter has),
            // decaying over roughly 1-1.5s back to nothing, at which point
            // normal idle (and the idle-settle timer above) takes back over.
            double chatterRate = 11.0; // Hz -- the stutter/flutter rate itself
            double chatterMod = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * ph_turboFlutterChatter);
            double flutterTone = Math.Sin(2.0 * Math.PI * ph_turboFlutter);
            double flutterMix = flutterTone * 0.4 + noise_hi * 0.6;
            engineSample += flutterMix * chatterMod * turboFlutterEnv * 0.11 * engMul;

            ph_turboFlutter        = (ph_turboFlutter        + 650.0        * invSR) % 1.0;
            ph_turboFlutterChatter = (ph_turboFlutterChatter + chatterRate  * invSR) % 1.0;
            turboFlutterEnv *= 0.9975f; // ~1.3s decay to silence
            if (turboFlutterEnv < 0.01f) turboFlutterEnv = 0f;
        }

        // [REMOVED] injector tick / pump whine at idle -- reported as "weird
        // clatter at idle". Fields (ph_injectorTick, _injectorTickPhaseAccum)
        // left declared but now unused.

        // [REMOVED] turbo whistle at sustained cruise -- reported as "weird
        // high pitched sound during movement". Field (ph_turboWhistle,
        // _turboWhistleEnv) left declared but now unused.

        // ═════════════════════════════════════════════════════════════════════
        //  WHEEL BEARING / HUB WHINE AT SPEED  —  universal, road-speed order
        //  Mechanical, not engine-order -- scales with road speed directly,
        //  present regardless of gear/rpm. Genuinely present at any real
        //  driving speed, just very quiet until you're moving with some pace.
        // ═════════════════════════════════════════════════════════════════════
        {
            float wheelHzTarget = 8f + spd * 3.2f; // rises with road speed, not rpm
            _wheelWhineHzSmooth += (wheelHzTarget - _wheelWhineHzSmooth) * 0.05f;
            float wheelVol = Mathf.Clamp01((spd - 5f) / 40f) * 0.018f * npcVolumeScale;

            if (wheelVol > 0.0005f)
            {
                double w1 = Math.Sin(2.0 * Math.PI * ph_wheelWhine1);
                double w2 = Math.Sin(2.0 * Math.PI * ph_wheelWhine2 * 2.01);
                engineSample += (w1 * 1.0 + w2 * 0.3) * wheelVol;
                ph_wheelWhine1 = (ph_wheelWhine1 + _wheelWhineHzSmooth       * invSR) % 1.0;
                ph_wheelWhine2 = (ph_wheelWhine2 + _wheelWhineHzSmooth * 0.5 * invSR) % 1.0;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  DOOR HISS + THUNK  —  universal, one-shot on open/close transition
        //  The classic "pssh-CLUNK" pneumatic cylinder actuation. Separate
        //  from the existing ob_doorWheeze (a continuous ambient effect only
        //  on the old-bus variant) -- this is a real event-triggered one-shot
        //  on the doorsOpen state actually changing, present on every bus.
        // ═════════════════════════════════════════════════════════════════════
        if (doorsOpen != _wasDoorsOpen)
        {
            _doorHissThunkEnv = 1f;
            _wasDoorsOpen = doorsOpen;
        }
        if (_doorHissThunkEnv > 0.002f)
        {
            double hissTone = Math.Sin(2.0 * Math.PI * ph_doorHiss);
            double hiss = hissTone * 0.15 + noise_hi * 0.85;
            double thunk = Math.Sin(2.0 * Math.PI * ph_doorThunk) * Math.Exp(-(1f - _doorHissThunkEnv) * 12.0);
            engineSample += (hiss * 0.6 + thunk * 0.4) * _doorHissThunkEnv * 0.10 * npcVolumeScale;
            ph_doorHiss  = (ph_doorHiss  + 5400.0 * invSR) % 1.0;
            ph_doorThunk = (ph_doorThunk + 60.0   * invSR) % 1.0;
            _doorHissThunkEnv *= 0.985f;
            if (_doorHissThunkEnv < 0.01f) _doorHissThunkEnv = 0f;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  PARKING-BRAKE RELEASE — pneumatic "psstEEEHH", ~0.45s (matched to
        //  the l9_brakePuffVol reference length, just a bit longer).
        //
        //  [RETARGETED — trigger, per direct request] This used to fire on
        //  the parking brake toggling OFF (see _wasParkingBrakeAndStopped,
        //  now unused). That's backwards from how the animation actually
        //  reads: the pneumatic release should land on the DOOR finishing
        //  its close, matching the door-close animation, not the raw parking
        //  brake flag. Trigger is now edge-detected on doorsFullyClosed
        //  going false -> true (every door leaf reports closed) -- neither
        //  parkingBrake, bkPd, nor spd play any part in it.
        //
        //  [FIXED — envelope] This block runs once per AUDIO SAMPLE (inside
        //  ProcessAudio's per-sample loop), not once per game frame. A flat
        //  "*= 0.965f" per sample decays to silence in ~135 samples -- about
        //  3ms at 48kHz -- which is also why it read as a "tick" rather than
        //  a sustained ~3s release: the whole thing was over before a single
        //  video frame had even passed. Decay multipliers below are derived
        //  from invSR so they land on an actual real-world duration
        //  regardless of sample rate.
        // ═════════════════════════════════════════════════════════════════════
        bool doorsClosedNow = doorsFullyClosed;

        // [ADD, per direct request] Parking-brake RELEASE (true -> false)
        // also fires the puff now -- but gated on the doors already being
        // fully closed. If the brake lets go while the doors are still open
        // or mid-close, don't fire yet; latch it pending and let it fire
        // the moment doorsFullyClosed flips true below, instead of firing
        // early against an open-door animation.
        bool brakeOnNow = parkingBrake;
        if (_wasParkingBrakeOn && !brakeOnNow)
        {
            if (doorsClosedNow)
            {
                _airCrackEnv = 1f;
                _airPuffTail = 1f;
                Debug.Log($"<color=cyan>[Parking Brake Released — Air Release]</color> triggered -- spd={spd:F2}, engineType={engineType}, tx={tx}");
            }
            else
            {
                _pendingBrakeReleasePuff = true;
            }
        }
        _wasParkingBrakeOn = brakeOnNow;

        if (!_wasDoorsFullyClosed && doorsClosedNow)
        {
            _airCrackEnv = 1f;
            _airPuffTail = 1f;
            _pendingBrakeReleasePuff = false; // this door-close edge covers it -- don't double-fire
            Debug.Log($"<color=cyan>[Door Closed — Air Release]</color> triggered -- spd={spd:F2}, engineType={engineType}, tx={tx}");
        }
        else if (_pendingBrakeReleasePuff && doorsClosedNow)
        {
            // Doors were already closed by the time this ran (no edge this
            // sample) but a brake release was left waiting on them -- fire now.
            _airCrackEnv = 1f;
            _airPuffTail = 1f;
            _pendingBrakeReleasePuff = false;
            Debug.Log($"<color=cyan>[Parking Brake Released, Doors Now Closed — Air Release]</color> triggered -- spd={spd:F2}, engineType={engineType}, tx={tx}");
        }
        _wasDoorsFullyClosed = doorsClosedNow;

        // [RETIMED] Length is pegged to l9_brakePuffVol (the same "actual
        // air" reference above) instead of a flat ~3s guess -- that puff
        // decays via `*= (1 - 12*invSR)` per sample, tau = 1/12s ≈ 83ms,
        // inaudible (~1%) by about 0.38s. This should be that same length,
        // just a bit longer -- not the earlier 3-second figure.
        const float AIR_CRACK_SEC = 0.07f;  // "PSST" onset transient length
        const float AIR_TAIL_SEC  = 0.38f;  // body -- crack (0.07s) + tail
                                             // (0.38s) ≈ 0.45s, a bit longer
                                             // than the ~0.38s brake-puff reference
        float airCrackMul = Mathf.Pow(0.01f, (float)invSR / AIR_CRACK_SEC);
        float airTailMul  = Mathf.Pow(0.01f, (float)invSR / AIR_TAIL_SEC);

        // [LOUDER] Previous gains (0.34 / 0.30 * npcVolumeScale, so ~0.19 /
        // ~0.17 peak with npcVolumeScale=0.55) were in the same ballpark as
        // the idle engine core itself (eBase ~0.25-0.3 at idle) -- similar
        // RMS, so it just blended into the idle drone instead of reading as
        // its own event. A real air release is dramatically louder than the
        // engine at idle and briefly buries it. Two changes to actually get
        // that: raise both stages well above the engine's own idle level,
        // and DUCK the existing engineSample (core + everything added
        // before this block) while the release plays, so the psst/hiss
        // genuinely cuts over the idle instead of just adding onto it.
        float airEnvNow = Mathf.Max(_airCrackEnv, _airPuffTail);
        if (airEnvNow > 0.002f)
        {
            engineSample *= (1.0 - 0.55 * airEnvNow); // ducks up to 55% at full envelope
        }

        // [SWAPPED AGAIN — the actual reference] This is the same blend as
        // DoDieselTransients' l9_brakePuffVol (the "coast decel puff" that
        // fires crossing under 3kph on its own, no throttle) -- plain
        // noise_hi * 0.8 + noise_lp * 0.2, no resonant filter, no
        // high-pass/differentiator experiment. That's the reference for
        // "the actual air" character: a real puff-of-air texture, not a
        // ringing/stuttering one. Both stages share this same blend so the
        // whole release stays one continuous texture.
        double airTex = noise_hi * 0.8 + noise_lp * 0.2;

        // Stage 0 — "PSST": hard zero-attack transient, no ramp, sharp
        // ~180ms collapse. LOUD.
        if (_airCrackEnv > 0.002f)
        {
            engineSample += airTex * _airCrackEnv * 1.6 * npcVolumeScale;
            _airCrackEnv *= airCrackMul;
            if (_airCrackEnv < 0.01f) _airCrackEnv = 0f;
        }

        // Stage 1 — "EEEEHHH": brief but clearly a body, not a click
        // (crack + tail ≈ 0.45s total). Same puff texture as Stage 0.
        if (_airPuffTail > 0.002f)
        {
            engineSample += airTex * _airPuffTail * 1.2 * npcVolumeScale;
            _airPuffTail *= airTailMul; // ~0.38s to inaudible
            if (_airPuffTail < 0.008f) _airPuffTail = 0f;
        }

        // _airPuffEnv/_airRelayEnv/ph_airPuff/ph_airPuffTail/ph_airRelayBuzz/
        // ph_airRelayClick are left declared but unused -- the two stages
        // above cover the whole sound now.

        // ═════════════════════════════════════════════════════════════════════
        //  ENGINE COMPRESSION BRAKE ("JAKE BRAKE")  —  opt-in, off by default
        //  Real mechanical distinction from the hydraulic retarder sound
        //  already on Allison/Voith/ZF: this is valve-timed compression
        //  release, not fluid drag, so it reads as a rapid, harsh mechanical
        //  clatter ("machine gun"/"chainsaw" character) tied to engine RPM,
        //  not road speed. Loudest under hard braking at higher RPM (the
        //  real device is most effective there), essentially silent at idle.
        // ═════════════════════════════════════════════════════════════════════
        if (engineCompressionBrake)
        {
            bool jakeActive = retAct && bkPd > 0.4f && rpm > IDLE * 1.3f && gear > 0;
            float jakeTarget = jakeActive ? Mathf.Clamp01((rn - 0.15f) / 0.6f) * bkPd : 0f;
            // [X10 HPD BRAKE] Real, confirmed via Cummins' own X10 material:
            // a genuinely stronger two-stage High Power Density compression-
            // release brake -- "up to 320hp at 2,300rpm for the three-
            // cylinder option, and up to 475hp for the six-cylinder option,"
            // with braking power "up to a 13-liter truck via a 10-liter
            // displacement engine." Real X10 buses get the full-strength
            // six-cylinder-mode character here (heaviest jake brake in the
            // fleet); every other engine keeps the standard single-stage
            // clatter above.
            bool isX10Hpd = engineType == EngineType.X10;
            if (isX10Hpd) jakeTarget *= 1.35f;
            jakeBrakeVolSmooth += (jakeTarget - jakeBrakeVolSmooth) * (jakeActive ? 0.08f : 0.15f);

            if (jakeBrakeVolSmooth > 0.003f)
            {
                // Clatter rate tracks RPM directly (valve-timed, not fluid-
                // timed) -- roughly firing-order rate, giving the rapid
                // machine-gun character rather than a smooth tone. X10's
                // HPD brake fires a second compression-release event per
                // cycle (real: driven by recirculated exhaust manifold gas
                // rather than fresh intake charge on the second event), so
                // its clatter rate runs faster than the standard single-
                // event jake brake -- denser, more "machine gun" than
                // "chainsaw".
                double jakeRate = Mathf.Max(rpm, IDLE) / 60.0 * (isX10Hpd ? 4.5 : 3.0);
                double jakeGate = ph_jakeBrake1 < 0.5 ? 1.0 : 0.0; // hard on/off gate = percussive, not smooth
                double jakeCrack = jakeGate * (NextNoiseSample() * 0.6 + Math.Sin(2.0 * Math.PI * ph_jakeBrake2) * 0.4);
                engineSample += jakeCrack * jakeBrakeVolSmooth * (isX10Hpd ? 0.26 : 0.20) * engMul;

                ph_jakeBrake1 = (ph_jakeBrake1 + jakeRate       * invSR) % 1.0;
                ph_jakeBrake2 = (ph_jakeBrake2 + jakeRate * 2.0 * invSR) % 1.0;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  COOLING FAN  —  universal, every L/B-series Cummins engine here
        //  (L9/ISL9/L9N alike).
        //  [CORRECTED against a real New Flyer XN40 operator's manual] This
        //  was originally modeled as a viscous/pneumatic CLUTCH fan (binary
        //  engage/disengage with hysteresis) -- the real manual instead
        //  confirms an EMP MH4 radiator/CAC assembly with Fil-15 pusher-type
        //  fans that are ELECTRONICALLY CONTROLLED (there's a dedicated
        //  "Engine Fan Fault" dash indicator specifically for this system).
        //  Electronic control means continuously graduated, variable-speed
        //  operation tracking cooling demand -- not a simple on/off clutch.
        //  Rebuilt as a smoothed continuous speed target instead of a
        //  hysteresis-gated bool, with pitch AND volume both tracking
        //  commanded speed continuously. A subtle "notch" transient fires
        //  when the controller steps to a meaningfully new speed band
        //  (real electronic fan controllers commonly step through discrete
        //  bands rather than a perfectly smooth curve), in place of the old
        //  binary engagement clunk.
        // ═════════════════════════════════════════════════════════════════════
        {
            float heatTarget = Mathf.Clamp01(ld * 0.7f + rn * 0.5f - (spd / MAX_SPD) * 0.25f);
            fanClutchHeat += (heatTarget - fanClutchHeat) * (float)invSR * (heatTarget > fanClutchHeat ? 0.008f : 0.004f);

            // Continuous electronic speed command -- soft start above ~15%
            // heat, full speed by ~90%, no hysteresis gate at all (a real
            // electronically-controlled fan doesn't need one; it modulates
            // directly off the sensed heat signal).
            float fanSpeedTarget = Mathf.Clamp01((fanClutchHeat - 0.15f) / 0.75f);
            fanClutchVolSmooth += (fanSpeedTarget - fanClutchVolSmooth) * (fanSpeedTarget > fanClutchVolSmooth ? 0.006f : 0.003f);

            // Speed-band step transient -- fires once each time the smoothed
            // speed crosses a 0.33 band, a subtle stand-in for a real
            // electronic controller stepping to a new commanded band.
            int currentBand = Mathf.FloorToInt(fanClutchVolSmooth / 0.34f);
            if (currentBand != fanClutchLastBand)
            {
                fanClutchLastBand = currentBand;
                fanClutchClunkEnv = 0.4f; // much subtler than the old full engagement clunk
            }

            if (fanClutchVolSmooth > 0.001f)
            {
                double fanBladeHz = 22.0 + fanClutchVolSmooth * 34.0; // pitch now tracks commanded speed continuously
                double fanRoar = noise_lp * 0.75 + Math.Sin(2.0 * Math.PI * ph_fanClutch) * 0.25;
                engineSample += fanRoar * fanClutchVolSmooth * 0.055 * npcVolumeScale;
                ph_fanClutch = (ph_fanClutch + fanBladeHz * invSR) % 1.0;
            }

            if (fanClutchClunkEnv > 0.01f)
            {
                engineSample += noiseHp * fanClutchClunkEnv * 0.025 * npcVolumeScale;
                fanClutchClunkEnv *= 0.90f;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  AIR COMPRESSOR  —  universal, engine-driven (not the electric
        //  buses' motor-driven door/brake compressor -- a mechanically
        //  different, real reciprocating piston pump geared directly off
        //  the engine). Real spec, confirmed via a New Flyer XN40 operator's
        //  manual: Wabco twin compressor, 30.4 CFM, 11-tooth, governed by a
        //  Bendix D2 (narrow band, cuts in/out between 117-131 psi). Those
        //  are the ACTUAL real thresholds used below, not guessed numbers.
        //  Pressure drains with brake usage (a rough gameplay proxy, not a
        //  literal CFM/volume simulation) and refills whenever the
        //  compressor is charging, which only happens while the engine is
        //  actually running.
        // ═════════════════════════════════════════════════════════════════════
        {
            float drainRate = bkPd * 3.0f + 0.04f; // psi/sec-ish, gameplay-scaled, not real-time accurate
            airPressurePsi -= drainRate * (float)invSR;

            if (airPressurePsi <= 117f) airCompressorCharging = true;
            else if (airPressurePsi >= 131f) airCompressorCharging = false;

            if (airCompressorCharging)
            {
                airPressurePsi += 9f * (float)invSR;

                // Reciprocating twin-cylinder piston chuff, gear-driven off
                // the engine so its rate tracks RPM directly (not a fixed
                // motor speed the way the electric buses' compressor is).
                double chuffHz = (rpm / 60.0) * 0.9; // low order -- a twin-piston pump cycles far slower than the crank itself
                double chuffEnv = Math.Max(0.0, Math.Sin(2.0 * Math.PI * ph_airComp));
                double chuff = chuffEnv * chuffEnv * 0.030 * npcVolumeScale
                             + noise_lp * chuffEnv * 0.012 * npcVolumeScale;
                engineSample += chuff;
                ph_airComp = (ph_airComp + chuffHz * invSR) % 1.0;
            }
            airPressurePsi = Mathf.Clamp(airPressurePsi, 95f, 140f);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  DPF ACTIVE REGENERATION  —  diesel only (L9/ISL9/ISL). L9N runs a
        //  three-way catalyst instead of a DPF (confirmed via Cummins
        //  Westport's own materials), so it never regens at all. Real
        //  mechanism: the ECU periodically triggers extra post-injection
        //  fuel to raise exhaust temps and burn off trapped soot -- a
        //  genuinely hotter/rougher exhaust texture for the duration. Real
        //  regen cycles run many minutes to a half hour+; compressed here to
        //  a shorter but still real multi-minute window since this is
        //  ambient/background character, not something meant to be sat
        //  through at full real-world length. Occasional and randomized,
        //  not scheduled.
        // ═════════════════════════════════════════════════════════════════════
        bool dpfCapable = !p.isGas;
        if (dpfCapable)
        {
            if (!dpfRegenActive)
            {
                dpfRegenTimer += (float)invSR;
                if (dpfNextRegenAt <= 0f) dpfNextRegenAt = 1500f + (float)(NextNoiseSample() * 0.5 + 0.5) * 1500f; // ~25-50 real minutes
                if (dpfRegenTimer > dpfNextRegenAt) { dpfRegenActive = true; dpfRegenTimer = 0f; dpfNextRegenAt = 0f; }
            }
            else
            {
                dpfRegenTimer += (float)invSR;
                if (dpfRegenTimer > 240f) { dpfRegenActive = false; dpfRegenTimer = 0f; } // ~4 real minutes active
            }
        }
        else dpfRegenActive = false;

        if (dpfRegenActive)
        {
            // Hotter/rougher exhaust texture riding on the existing voice --
            // not a separate loud layer, just a subtle added brightness/rasp
            // that makes the exhaust read as running hot.
            engineSample += noise_hi * 0.035 * engMul;
        }

        // [REMOVED] EGR valve modulation -- the load-step click read as the
        // same "weird clatter" the injector tick was already pulled for
        // once before (same shape: a percussive click firing every time a
        // continuously-changing driving input crosses a discrete step,
        // which under normal accelerator input fires far more often than
        // the real, occasional valve-repositioning event it was meant to
        // represent). Removed rather than just quieted, since the pattern
        // itself was the problem, not the volume.

        // Engine-specific character extras that aren't part of the core voice.
        if (engineType == EngineType.L9 || engineType == EngineType.ISL9 || engineType == EngineType.ISL)
            DoDieselTransients(ref engineSample, rn, ld, engMul, invSR);

        // ── Transmission dispatch (unchanged from the legacy branches) ───────
        if (IsAllison())
        {
            // [FIX] This used to duck engineSample by up to 50% as pump
            // whine volume rose (`engineSample *= 1 - Mathf.Min(b400Duck...,
            // 0.5f)`) -- confirmed, real-world grounded: Allison whine
            // (pump or gear-mesh) has zero acoustic relationship to engine
            // volume in reality, it's a mechanically separate noise source.
            // That line WAS the "whine gets louder / engine gets quieter"
            // bug. Removed entirely -- DoB400RWhineDSP no longer even
            // outputs a duck value at all now.
            DoB400RWhineDSP(ref txSample, rn, ld, outRPM, engMul * (p.isGas ? 1.25f : 1.0f), retAct, noiseClt, invSR);
        }
        // [CONFIRMED, per direct request] "voith" and "d8646" are meant to
        // be modeled the same -- both dispatch to the identical DoVoithDSP
        // call below, on purpose, not something to redirect. (d8645/
        // d8646art are the two that stay genuinely distinct -- see their
        // own comments.)
        else if (tx == "voith")     DoVoithDSP(ref engineSample, ref txSample, rn, ld, hz * 0.90f, outRPM, engMul, retAct, invSR);
        // [FIX] Genuinely THE bug -- this whole session's D8646 rebuild
        // (in BusAudioEngine.cs) never actually mattered at runtime,
        // because THIS is the real live dispatch chain that OnAudioFilterRead
        // -> ProcessAudio -> DoCombustionEngine actually calls, in a
        // completely different file (BusAudioEngine is a partial class
        // split across BusAudioEngine.cs and this AudioLayers.cs file).
        // Every "else if (tx == \"d8646\")" branch added to BusAudioEngine.cs
        // this session was dead code as far as the real engine goes --
        // confirmed via a live debug log: opt1_4 (which aliases tx to
        // "voith") correctly reached DoVoithDSP through this exact chain,
        // but base "d8646" fell through EVERY condition below with no
        // match at all, producing total silence for the whine (and
        // everything else DoVoithDSP does). Base d8646 now dispatches to
        // the exact same DoVoithDSP call as "voith" -- matching the
        // architecture BusAudioEngine.cs was already built around, just
        // finally wired into the file that actually runs.
        else if (tx == "d8646")     DoVoithDSP(ref engineSample, ref txSample, rn, ld, hz * 0.90f, outRPM, engMul, retAct, invSR);
        // [FIX] These three were NEVER dispatched anywhere in this shared
        // path -- DoNXTDSP/DoVoith35DSP/DoD8646ArtDSP were only ever called
        // from inside the old X10-only duplicate block in BusAudioEngine.cs
        // (now removed as part of the X10 rebuild). Every other engine
        // (L9, L9N, ISL9, ISL, ISB67, B67, B72) offers "nxt"/"voith35"/
        // "d8646art" as real Custom-tab picks (see BusSelectMenu's
        // _compatTX), but selecting any of them here produced a genuinely
        // SILENT transmission -- the engine core still played, the gearbox
        // voice just never fired. Real bug, not X10-specific.
        // [REDIRECTED, per direct instruction] voith35/d8646art no longer
        // get their own standalone voice -- both now dispatch to the same
        // DoVoithDSP call as "voith"/"d8646". DoVoith35DSP/DoD8646ArtDSP
        // are left defined below (unused/orphaned) rather than deleted, in
        // case they're wanted again later.
        else if (tx == "voith35")   DoVoithDSP(ref engineSample, ref txSample, rn, ld, hz * 0.90f, outRPM, engMul, retAct, invSR);
        else if (tx == "d8646art")  DoVoithDSP(ref engineSample, ref txSample, rn, ld, hz * 0.90f, outRPM, engMul, retAct, invSR);
        // [CLARIFICATION] NXT is a Voith DIWA transmission too, just a
        // NEWER generation than D864.5/D864.6 -- not an unrelated tx family,
        // and not something to redirect onto DoVoithDSP. Stays its own
        // standalone DSP call on purpose (see DoNXTDSP's header comment).
        else if (tx == "nxt")       DoNXTDSP(ref txSample, rn, ld, hz * 0.90f, outRPM, engMul, retAct, invSR);
        else if (tx == "d8645")     DoD8645DSP(ref txSample, rn, ld, hz, outRPM, engMul, retAct, invSR);
        else if (tx == "b500r")     DoB500RDSP(ref txSample, rn, ld, hz, outRPM, engMul, noiseHp, noiseClt, invSR);
        // [FIX] b400r_g5/b500r_g5 matched NOTHING in this dispatch chain at
        // all -- not IsAllison() (only checks "allison"/"b400r" literally),
        // not any explicit branch. Those buses got ZERO transmission voice
        // whatsoever, only bare engine sound -- a much worse bug than
        // "sounds like Gen 4," genuinely silent on this whole layer. Now
        // routed to their own genuinely dedicated DSP functions (not a
        // reused Gen4 call with a volume multiplier -- see
        // DoB500RGen5DSP/DoB400RGen5DSP's own header comments for the full
        // real-research-grounded design).
        else if (tx == "b500r_g5")  DoB500RGen5DSP(ref txSample, rn, ld, hz, outRPM, engMul, noiseHp, noiseClt, invSR);
        else if (tx == "b400r_g5")  DoB400RGen5DSP(ref txSample, rn, ld, outRPM, engMul, retAct, noiseClt, invSR);
        else if (tx == "b3400xfe")  DoB3400DSP(ref txSample, rn, ld, hz, outRPM, engMul, noiseHp, noiseClt, invSR);
        else if (tx == "zf")        DoZFDSP(ref txSample, rn, ld, outRPM, engMul, noiseClt, retAct, invSR);
        else if (tx == "zfel2")     DoZFEL2DSP(ref txSample, rn, ld, outRPM, engMul, noiseClt, retAct, invSR, false);
        else if (tx == "zfel2_hd")  DoZFEL2DSP(ref txSample, rn, ld, outRPM, engMul, noiseClt, retAct, invSR, true);
        // [FIX] zh50ep was missing from this condition entirely — despite
        // DoH4xDSP's own isZombie/isH50 flags and the ZH50EP ghost-layer
        // comments elsewhere clearly expecting it to have already fired for
        // that tx. Also adding h50ep_gen5 (2750 Series) here now.
        else if (tx == "h40ep" || tx == "h50ep" || tx == "zh50ep" || tx == "h50ep_gen5")
            DoH4xDSP(ref txSample, ref engineSample, rn, ld, engMul, engVolPersonality, noiseHp, noise_hi, invSR);
        // [ADD] EP40/EP50 -- real pre-2010 generation of the same H40EP/
        // H50EP drive unit, own dedicated DSP voice (own function, per
        // direct instruction -- not DoH4xDSP reused with a flag). See
        // DoEP4xDSP's header comment for the full real-research basis.
        else if (tx == "ep40" || tx == "ep50")
            DoEP4xDSP(ref txSample, ref engineSample, rn, ld, engMul, engVolPersonality, noiseHp, noise_hi, invSR);
        // [FIX -- per instruction] eGen Flex is a real, distinct Allison
        // architecture (integrated motor-in-gearbox unit, disconnect
        // clutch, WEG-cooled inverter, LTO pack) -- reusing H40/H50EP's
        // whole voice with a few bonus layers bolted on wasn't going to
        // read as genuinely different no matter how it was reskinned. Now
        // has its own dedicated function (DoEGenFlexDSP) with its own
        // integrated gearbox-mesh voice, clutch-thump, inverter carrier
        // and regen layer -- not a DoH4xDSP call at all anymore. Its RPM
        // math (CalcEGenFlexRPM) was rebuilt the same way -- see that
        // function for the "why punchier than H40EP" reasoning.
        else if (tx == "egenflex40" || tx == "egenflex50")
            DoEGenFlexDSP(ref txSample, ref engineSample, rn, ld, engMul, engVolPersonality, noiseHp, noise_hi, invSR);
        // [FIX] "bae" was never actually dispatched from anywhere — the old
        // DoBAEEngineDSP/DoHDS300EngineDSP/DoBAEGen3EngineDSP were dead code,
        // defined but uncalled. BAE is now a real tx entry, same shape as
        // h40ep/h50ep above: fresh rebuild in BusAudioEngine.cs (DoBAEDSP) —
        // stop-start below 10kph (motor only), genset wakes up and climbs
        // RPM+volume together past that, sticks at whatever it reached for
        // as long as accel is held, cuts off on release.
        else if (tx == "bae")
            DoBAEDSP(ref txSample, ref engineSample, rn, ld, engMul, engVolPersonality, noiseHp, noise_hi, invSR);
        // [FIX] "hds300"/"baegen3" had the exact same dead-code problem "bae"
        // did — CalcHDS300RPM/CalcBAEGen3RPM fed the shared rpm dispatcher
        // fine, but nothing in this tx chain ever actually synthesized their
        // audio. Real entries now, same shape, own fresh methods.
        else if (tx == "hds300")
            DoHDS300DSP(ref txSample, ref engineSample, rn, ld, engMul, engVolPersonality, noiseHp, noise_hi, invSR);
        else if (tx == "baegen3")
            DoBAEGen3DSP(ref txSample, ref engineSample, rn, ld, engMul, engVolPersonality, noiseHp, noise_hi, invSR);

        // Gear-mesh "mechanical" echo whine — deliberately OUTSIDE the tx
        // if/else chain above: every one of those branches is one specific
        // transmission's own voice (Allison pump whine, Voith wail, etc).
        // This one isn't transmission-specific at all — it's a driveline
        // gear-mesh whine that shows up at gear 3+ regardless of which tx
        // is fitted, so it runs unconditionally here instead of being
        // folded into any single branch (or gated behind IsAllison() the
        // way the pump/converter whines above are).
        // [FIX] eGen Flex is explicitly a NO-GEARS design (per design intent
        // -- treated as a single continuously-variable parallel-hybrid drive
        // unit, not a discrete-gear transmission the way h40ep/h50ep still
        // are), so it's excluded here. Without this, eGen Flex was getting
        // the exact same gear-3+ mechanical whine as every other tx, which
        // is where the audible "4 gears" character was coming from.
        if (tx != "egenflex40" && tx != "egenflex50")
            DoMechanicalWhine(ref txSample, gear, rn, ld, engMul, p.isGas && !p.westport, invSR);

        // [REMOVED] DoAllisonG1G2Whine/DoAllisonDeepWhine -- see the removal
        // comment at their old declaration site in BusAudioEngine.cs. Both
        // grew louder with gear (backwards from real TC-lockup behavior)
        // and duplicated/contradicted each variant's own correctly-shaped
        // whine already produced by the calls above -- the direct cause of
        // B3400xFE sounding "weird". DoAllisonDoubleTap (the actual "end of
        // gear 1" lockup sound) is preserved -- called directly from inside
        // DoB400RWhineDSP/DoB3400DSP now instead.

        // ═════════════════════════════════════════════════════════════════════
        //  UNIVERSAL TX AIR TRIM  (every combustion-engine tx voice)
        //  See cbTxAirLP's header comment -- one LP blend after the whole tx
        //  dispatch chain, trimming the noise_hi-heavy top end that's baked
        //  into most whine/hiss layers across every gearbox DSP function at
        //  once, always-on (not gated by AC or RPM -- separate concern from
        //  both of those blocks).
        // ═════════════════════════════════════════════════════════════════════
        {
            cbTxAirLP += (txSample - cbTxAirLP) * 0.02;
            const double txAirTrimBlend = 0.16; // fixed, modest -- trims air without muffling gear whine identity
            txSample = txSample * (1.0 - txAirTrimBlend) + cbTxAirLP * txAirTrimBlend;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  TURBO HARDWARE — REAL RESEARCH, replaces the old "ISL9 = loose worn
    //  actuator, L9 = tight newer turbo" premise (that was DESIGN INTENT, not
    //  grounded — flagged per request to actually check it):
    //
    //  ISL9 and L9 are NOT different turbo hardware. Parts listings confirm a
    //  single Holset HE300VG VGT turbocharger is sold as compatible with BOTH
    //  "Cummins ISL9/L9 (EPA13/EPA17)" — same part, same sliding-nozzle-ring
    //  actuator design, same physical unit across both engines. They're also
    //  the same 8.9L iron block (L9 is the ISL9's direct successor on the
    //  same architecture) with the same Cummins XPI fuel system on both.
    //
    //  So the real ISL9 vs L9 distinction is CALIBRATION-GENERATION, not
    //  hardware: EPA13 (ISL9, 2010-16) vs EPA17 (L9, 2017+) engine tuning —
    //  injection timing/pilot ratio, EGR rates, and VGT actuator control
    //  strategy all changed between certs even though the turbo casting is
    //  shared. That's reflected below as: both DoISL9Core and DoL9Core use
    //  the same whistle/whine MECHANISM (shaft-rotation whistle + blade-pass
    //  whine, per the turbo-acoustics research cited inline), but a
    //  rougher/hunting actuator RESPONSE on ISL9 (older control strategy,
    //  huntMod) vs a smoothed, breathier one on L9 (refined multi-injection
    //  calibration, p.refine=1.0, no hunt) — the specific pitch/blade-pass-
    //  ratio numbers on each are still tuning choices, not measured, same as
    //  before this split; only the turbo-hardware premise itself changed.
    // ═════════════════════════════════════════════════════════════════════════

    // ═════════════════════════════════════════════════════════════════════════
    //  DIESEL CORE — firing pulse train + pilot shadow + knock + struck hollows.
    //  Serves ISL / ISB6.7 / B6.7 — the diesels that DON'T need their own
    //  dedicated core (no turboHunt, no subShake, no inverter overlay; see
    //  DoISL9Core / DoL9Core below for those two specifically).
    // ═════════════════════════════════════════════════════════════════════════
    private double DoDieselCore(CombustionParams p, float hz, float rn, float ld,
                                float engMul, double noiseClt, double noiseInd, double invSR)
    {
        float eBase = engMul * (0.42f + rn * 0.40f);

        double fire  = FirePulse(ph_cb_fire, p.knockK);
        double pilot = FirePulse(ph_cb_pilot, Mathf.Max(1.2f, p.knockK - 1.0f)) * p.pilot;

        double comb  = (fire - 0.28) + pilot;
        comb = Math.Tanh(comb * 1.8);

        // Block thrum (half-order) and low lug (rotation order) for weight.
        // Fixed 0.5x sub ratio — the load/rev-dependent "shake" deepening is
        // an ISL9-only quirk, handled in DoISL9Core instead.
        double sub = Math.Sin(2.0 * Math.PI * ph_cb_sub) * p.subGain;
        double rot = Math.Sin(2.0 * Math.PI * ph_cb_rot) * 0.30 * (0.4 + ld);

        // Diesel knock / nailing — broadband gated to the firing pulse, reduced
        // by pilot injection. This is the sharp "clack" texture of a diesel.
        float knockGate = (float)Math.Max(0.0, fire - pilot * 1.2);
        double knock = noiseClt * p.clatterGain * (0.15 + ld * 0.6) * engMul * (0.2 + knockGate * 1.4);

        // Valvetrain tick — faint high grit gated at cam (half-firing) rate.
        double camGate = Math.Max(0.0, Math.Sin(2.0 * Math.PI * ph_cb_cam));
        double vtick = noiseInd * (0.004 + rn * 0.003) * engMul * camGate;

        // Turbo whistle + whine — spool state now driven by RPM and load
        // TOGETHER (see ResolveTurboSpool), and whine sits at the real 6x
        // blade-pass ratio for this turbo's stock compressor wheel, not an
        // arbitrary multiple. Breathy air-noise treatment (not a clean
        // oscillator — real ducted airflow colors broadband noise with a
        // resonant peak).
        float turboTarget = ResolveTurboSpool(rn, ld);
        cb_turboVolSmooth += (turboTarget - cb_turboVolSmooth) * (turboTarget > cb_turboVolSmooth ? 0.010f : 0.004f);
        float turboV = cb_turboVolSmooth * (0.030f + ld * 0.050f) * engMul;

        double turboHz = (240.0 + cb_turboVolSmooth * 620.0) * p.turboBright;
        double pipeTone = Math.Sin(2.0 * Math.PI * ph_cb_turbo) * 0.30
                         + Math.Sin(2.0 * Math.PI * ph_cb_turbo * 0.5) * 0.18;
        double pipeNoise = noise_hi * 0.45 + noise_lp * 0.35;
        double turbo = (pipeTone + pipeNoise) * turboV * 0.55;
        ph_cb_turbo = (ph_cb_turbo + turboHz * invSR) % 1.0;

        double turboWhineHz = turboHz * TURBO_BLADE_COUNT;
        double turboWhine = (Math.Sin(2.0 * Math.PI * ph_cb_turboWhine) * 0.35 + noise_hi * 0.45) * turboV * 0.20;
        ph_cb_turboWhine = (ph_cb_turboWhine + turboWhineHz * invSR) % 1.0;

        // Wastegate flutter on a hard lift while spooled (older engines only —
        // simple wastegate turbos; refine>=0.6 vents smoothly, no flutter).
        float ldDrop = cb_prevLdForWg - ld;
        if (p.refine < 0.6f && ldDrop > 0.18f && cb_turboVolSmooth > 0.25f)
            cb_wastegateVol = Mathf.Min(1f, cb_wastegateVol + ldDrop);
        cb_prevLdForWg = ld;
        double wastegate = 0.0;
        if (cb_wastegateVol > 0.002f)
        {
            double wgHz  = 15.0 + (NextNoiseSample() * 0.5 + 0.5) * 9.0;
            float  wgEnv = 0.5f + 0.5f * (float)Math.Sin(2.0 * Math.PI * ph_cb_wastegate);
            wastegate = (noise_hi * 0.7 + Math.Sin(2.0 * Math.PI * ph_cb_turbo * 1.7) * 0.4)
                        * cb_wastegateVol * 0.05 * npcVolumeScale * wgEnv;
            ph_cb_wastegate = (ph_cb_wastegate + wgHz * invSR) % 1.0;
            cb_wastegateVol *= 0.9975f;
            if (cb_wastegateVol < 0.003f) cb_wastegateVol = 0f;
        }

        // ── THE HOLLOW ─────────────────────────────────────────────────────
        double strike = fire * 1.0 + knock * 0.5;
        double fBox  = 2.0 * Math.Sin(Math.PI * p.boxHz  / SR);
        double fPipe = 2.0 * Math.Sin(Math.PI * p.pipeHz / SR);
        double box   = cb_resBox.Strike(strike, fBox,  1.0 / p.boxQ);
        double pipe  = cb_resPipe.Strike(strike, fPipe, 1.0 / p.pipeQ);
        float muffle = 1f - p.refine * 0.18f;
        float hollowLoad = 0.6f + ld * 0.4f + rn * 0.15f;
        double hollow = (box * p.boxGain + pipe * p.pipeGain) * hollowLoad * muffle * eBase * 0.85;

        double outSig =
              comb   * eBase * 0.85
            + sub    * eBase * 0.55
            + rot    * eBase * 0.5
            + hollow
            + turbo
            + turboWhine
            + wastegate
            + knock
            + vtick;

        ph_cb_fire  = (ph_cb_fire  + hz             * invSR) % 1.0;
        ph_cb_pilot = (ph_cb_fire + 0.11) % 1.0;
        ph_cb_sub   = (ph_cb_sub   + hz * 0.5       * invSR) % 1.0;
        ph_cb_rot   = (ph_cb_rot   + hz / 3.0       * invSR) % 1.0;
        ph_cb_cam   = (ph_cb_cam   + hz * 0.5       * invSR) % 1.0;

        return Math.Tanh(outSig * 1.08);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ISL9 CORE — 2010-16, EPA13. Same HE300VG turbo casting as L9 (see the
    //  research note above), but the EPA13-era actuator control strategy
    //  reads as a rougher, hunting response under load, and the reference
    //  recording's dynamic "able to shake the thing" low end is unique to
    //  this engine (subShakeGain) — L9's is a flat, unshaken sub instead.
    // ═════════════════════════════════════════════════════════════════════════
    private double DoISL9Core(CombustionParams p, float hz, float rn, float ld,
                                float engMul, double noiseClt, double noiseInd, double invSR)
    {
        float eBase = engMul * (0.42f + rn * 0.40f);

        double fire  = FirePulse(ph_cb_fire, p.knockK);
        double pilot = FirePulse(ph_cb_pilot, Mathf.Max(1.2f, p.knockK - 1.0f)) * p.pilot;

        double comb  = (fire - 0.28) + pilot;
        comb = Math.Tanh(comb * 1.8);

        // "Go deep" — the sub-bass component's own FREQUENCY drops as revving
        // intensity climbs (not just volume), easing from 0.5x down toward
        // 0.30x at full load/rev — nearly an octave lower — so hard revving
        // genuinely deepens the tone. ISL9-only; models the reference clip's
        // load-dependent "shake".
        float revIntensity = Mathf.Clamp01(ld * 0.6f + rn * 0.6f);
        float subRatio = Mathf.Lerp(0.5f, 0.30f, revIntensity);
        double shakeMul = 1.0 + p.subShakeGain * revIntensity;
        double sub = Math.Sin(2.0 * Math.PI * ph_cb_sub) * p.subGain * shakeMul;
        double rot = Math.Sin(2.0 * Math.PI * ph_cb_rot) * 0.30 * (0.4 + ld);

        // Diesel knock / nailing.
        float knockGate = (float)Math.Max(0.0, fire - pilot * 1.2);
        double knock = noiseClt * p.clatterGain * (0.15 + ld * 0.6) * engMul * (0.2 + knockGate * 1.4);

        // Valvetrain tick.
        double camGate = Math.Max(0.0, Math.Sin(2.0 * Math.PI * ph_cb_cam));
        double vtick = noiseInd * (0.004 + rn * 0.003) * engMul * camGate;

        // "Inverter" overlay — HE300VG sliding-nozzle-ring VGT, present on
        // both ISL9 and L9 (same part). A genuine FILTER, not an added
        // layer: modulates the combined engine+transmission signal at the
        // final combine point in ProcessAudio (see cb_invFilterMod).
        cb_invFilterMod = 1.0;
        float invTarget = Mathf.Clamp01((rn - 0.04f) / 0.9f) * (0.032f + ld * 0.030f) * engMul;
        cb_invOverlayVol += (invTarget - cb_invOverlayVol) * (invTarget > cb_invOverlayVol ? 0.012f : 0.008f);
        if (cb_invOverlayVol > 0.001f)
        {
            double invHz = 900.0 + rn * 750.0; // mid-high register -- a whistle-in-a-duct pitch, not a low growl
            double invTone = Math.Sin(2.0 * Math.PI * ph_cb_inv1) * 0.26;
            double invAir  = noise_hi * 0.60 + noise_lp * 0.12; // mostly airy hiss, hollow body underneath
            cb_invFilterMod = 1.0 + (invTone + invAir) * cb_invOverlayVol;
            ph_cb_inv1 = (ph_cb_inv1 + invHz * invSR) % 1.0;
        }

        // Turbo whistle + whine — same HE300VG physics as L9 (shaft-rotation
        // whistle, blade-pass whine at the real 6x ratio), but EPA13 actuator control
        // hunts under load (huntMod) where L9's calibration doesn't, and the
        // whistle stays a cleaner tone here rather than L9's breathier
        // air-noise treatment.
        float turboTarget = ResolveTurboSpool(rn, ld);
        cb_turboVolSmooth += (turboTarget - cb_turboVolSmooth) * (turboTarget > cb_turboVolSmooth ? 0.010f : 0.004f);
        float turboV = cb_turboVolSmooth * (0.030f + ld * 0.050f) * engMul;
        float huntDepth = Mathf.Clamp01((ld - 0.15f) * 2.0f) * Mathf.Clamp01(1f - rn);
        double huntMod = 1.0 + Math.Sin(2.0 * Math.PI * ph_cb_turboHunt) * 0.22 * huntDepth;
        ph_cb_turboHunt = (ph_cb_turboHunt + 3.4 * invSR) % 1.0;

        // Range grounded in the reference recording's own measured peak
        // (~3.9-4.1kHz at high-load/high-rev, near-zero at idle).
        double turboHz = (360.0 + cb_turboVolSmooth * 1500.0) * p.turboBright;
        double turbo = Math.Sin(2.0 * Math.PI * ph_cb_turbo) * turboV * 0.55 * huntMod;
        ph_cb_turbo = (ph_cb_turbo + turboHz * invSR) % 1.0;

        double turboWhineHz = turboHz * TURBO_BLADE_COUNT;
        double turboWhine = Math.Sin(2.0 * Math.PI * ph_cb_turboWhine) * turboV * 0.48 * huntMod
                           * (0.7 + 0.3 * fire); // breathes with the firing pulse instead of an isolated oscillator
        ph_cb_turboWhine = (ph_cb_turboWhine + turboWhineHz * invSR) % 1.0;

        // No wastegate flutter branch here — ISL9's VGT vents through the
        // same modeled actuator hunt above rather than a separate flutter.

        // ── THE HOLLOW ─────────────────────────────────────────────────────
        double strike = fire * 1.0 + knock * 0.5;
        double fBox  = 2.0 * Math.Sin(Math.PI * p.boxHz  / SR);
        double fPipe = 2.0 * Math.Sin(Math.PI * p.pipeHz / SR);
        double box   = cb_resBox.Strike(strike, fBox,  1.0 / p.boxQ);
        double pipe  = cb_resPipe.Strike(strike, fPipe, 1.0 / p.pipeQ);
        float muffle = 1f - p.refine * 0.18f;
        float hollowLoad = 0.6f + ld * 0.4f + rn * 0.15f;
        double hollow = (box * p.boxGain + pipe * p.pipeGain) * hollowLoad * muffle * eBase * 0.85;

        double outSig =
              comb   * eBase * 0.85
            + sub    * eBase * 0.55
            + rot    * eBase * 0.5
            + hollow
            + turbo
            + turboWhine
            + knock
            + vtick;

        ph_cb_fire  = (ph_cb_fire  + hz             * invSR) % 1.0;
        ph_cb_pilot = (ph_cb_fire + 0.11) % 1.0;
        ph_cb_sub   = (ph_cb_sub   + hz * subRatio  * invSR) % 1.0;
        ph_cb_rot   = (ph_cb_rot   + hz / 3.0       * invSR) % 1.0;
        ph_cb_cam   = (ph_cb_cam   + hz * 0.5       * invSR) % 1.0;

        return Math.Tanh(outSig * 1.08);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  L9 CORE — 2017+, EPA17. Same HE300VG turbo casting as ISL9 (see the
    //  research note above), but the refined EPA17 multi-injection
    //  calibration (p.refine=1.0) means no actuator hunt and no wastegate
    //  flutter — it vents smoothly. Whistle is breathy/air-noise rather than
    //  ISL9's cleaner tone; no subShake, flat sub weight.
    // ═════════════════════════════════════════════════════════════════════════
    private double DoL9Core(CombustionParams p, float hz, float rn, float ld,
                                float engMul, double noiseClt, double noiseInd, double invSR)
    {
        float eBase = engMul * (0.42f + rn * 0.40f);

        double fire  = FirePulse(ph_cb_fire, p.knockK);
        double pilot = FirePulse(ph_cb_pilot, Mathf.Max(1.2f, p.knockK - 1.0f)) * p.pilot;

        double comb  = (fire - 0.28) + pilot;
        comb = Math.Tanh(comb * 1.8);

        // Flat sub weight — the load-dependent "shake"/deepening is
        // ISL9-only (see DoISL9Core).
        double sub = Math.Sin(2.0 * Math.PI * ph_cb_sub) * p.subGain;
        double rot = Math.Sin(2.0 * Math.PI * ph_cb_rot) * 0.30 * (0.4 + ld);

        // Diesel knock / nailing — much softer here (p.pilot=0.34, p.knockK
        // 2.3, EPA17 multi-injection cushions the pressure rise more than
        // ISL9's).
        float knockGate = (float)Math.Max(0.0, fire - pilot * 1.2);
        double knock = noiseClt * p.clatterGain * (0.15 + ld * 0.6) * engMul * (0.2 + knockGate * 1.4);

        // [LAUNCH WINDOW -- clatter out, hollow RPM rev in] 0-10kph is
        // exactly the loaded-low-speed condition where real diesel clatter
        // quiets down (see l9LaunchGateSmooth's header comment) -- smoothed
        // toward 1 while spd<=10, toward 0 above it, so nothing snaps at
        // the boundary.
        float l9LaunchTarget = spd <= 10f ? 1f : 0f;
        l9LaunchGateSmooth += (l9LaunchTarget - l9LaunchGateSmooth) * (l9LaunchTarget > l9LaunchGateSmooth ? 0.05f : 0.02f);
        knock *= (1.0 - l9LaunchGateSmooth * 0.82); // clatter cut to ~18% inside the window

        // [SMALL RPM FLARE] Per correction -- not just a continuous hollow
        // boost, an actual little rev happening once, right at launch: real
        // torque-converter automatics let engine RPM flare up briefly right
        // as throttle's applied from a stop, before the converter grabs and
        // road speed starts actually climbing with it. Edge-triggered on
        // "throttle applied from near-stationary", fast rise, ~1s decay --
        // one small bump per launch, not a sustained effect.
        bool launchingNow = spd < 2f && ld > 0.25f;
        if (launchingNow && !l9WasLaunching) l9LaunchFlareEnv = 1f;
        l9WasLaunching = launchingNow;
        // Real-duration decay (not a flat per-sample multiply, which would
        // land at a totally different length depending on sample rate) --
        // ~0.9s to inaudible, regardless of SR.
        float l9FlareDecayMul = Mathf.Pow(0.01f, (float)invSR / 0.9f);
        l9LaunchFlareEnv *= l9FlareDecayMul;
        if (l9LaunchFlareEnv < 0.01f) l9LaunchFlareEnv = 0f;

        // Valvetrain tick.
        double camGate = Math.Max(0.0, Math.Sin(2.0 * Math.PI * ph_cb_cam));
        double vtick = noiseInd * (0.004 + rn * 0.003) * engMul * camGate;

        // "Inverter" overlay — HE300VG sliding-nozzle-ring VGT, present on
        // both L9 and ISL9 (same part). Genuine FILTER, applied at the final
        // combine point in ProcessAudio (see cb_invFilterMod).
        cb_invFilterMod = 1.0;
        float invTarget = Mathf.Clamp01((rn - 0.04f) / 0.9f) * (0.032f + ld * 0.030f) * engMul;
        cb_invOverlayVol += (invTarget - cb_invOverlayVol) * (invTarget > cb_invOverlayVol ? 0.012f : 0.008f);
        if (cb_invOverlayVol > 0.001f)
        {
            double invHz = 900.0 + rn * 750.0;
            double invTone = Math.Sin(2.0 * Math.PI * ph_cb_inv1) * 0.26;
            double invAir  = noise_hi * 0.60 + noise_lp * 0.12;
            cb_invFilterMod = 1.0 + (invTone + invAir) * cb_invOverlayVol;
            ph_cb_inv1 = (ph_cb_inv1 + invHz * invSR) % 1.0;
        }

        // Turbo whistle + whine — same HE300VG physics as ISL9, but no hunt
        // (EPA17 actuator control holds steady under load) and a breathier
        // "air moving through a duct" whistle rather than a clean oscillator
        // tone — real ducted airflow colors broadband noise with a resonant
        // peak rather than a pure whistle, and L9's tighter, newer-cert
        // calibration reads as that cleaner, less tonal character.
        float turboTarget = ResolveTurboSpool(rn, ld);
        cb_turboVolSmooth += (turboTarget - cb_turboVolSmooth) * (turboTarget > cb_turboVolSmooth ? 0.010f : 0.004f);
        float turboV = cb_turboVolSmooth * (0.030f + ld * 0.050f) * engMul;

        double turboHz = (240.0 + cb_turboVolSmooth * 620.0) * p.turboBright;
        double pipeTone = Math.Sin(2.0 * Math.PI * ph_cb_turbo) * 0.30
                         + Math.Sin(2.0 * Math.PI * ph_cb_turbo * 0.5) * 0.18;
        double pipeNoise = noise_hi * 0.45 + noise_lp * 0.35;
        double turbo = (pipeTone + pipeNoise) * turboV * 0.55;
        ph_cb_turbo = (ph_cb_turbo + turboHz * invSR) % 1.0;

        double turboWhineHz = turboHz * TURBO_BLADE_COUNT;
        double turboWhine = (Math.Sin(2.0 * Math.PI * ph_cb_turboWhine) * 0.35 + noise_hi * 0.45) * turboV * 0.36
                           * (0.7 + 0.3 * fire); // breathes with the firing pulse instead of an isolated oscillator
        ph_cb_turboWhine = (ph_cb_turboWhine + turboWhineHz * invSR) % 1.0;

        // No wastegate flutter — p.refine=1.0 always vents smoothly for L9.

        // ── THE HOLLOW ─────────────────────────────────────────────────────
        double strike = fire * 1.0 + knock * 0.5;
        // [LAUNCH FLARE] The cavity's own tuned frequency gets nudged up
        // briefly by the flare envelope -- a real little pitch-up, same as
        // an actual RPM flare would do to a resonance that tracks firing
        // rate, not just a volume swell layered on top.
        float l9FlarePitchUp = 1f + l9LaunchFlareEnv * 0.10f; // ~10% pitch-up at flare peak
        double fBox  = 2.0 * Math.Sin(Math.PI * p.boxHz  * l9FlarePitchUp / SR);
        double fPipe = 2.0 * Math.Sin(Math.PI * p.pipeHz * l9FlarePitchUp / SR);
        double box   = cb_resBox.Strike(strike, fBox,  1.0 / p.boxQ);
        double pipe  = cb_resPipe.Strike(strike, fPipe, 1.0 / p.pipeQ);
        float muffle = 1f - p.refine * 0.18f; // p.refine=1.0 -> always the max muffle trim
        // [LAUNCH FLARE] One small rev-bump right at launch, not a sustained
        // boost -- l9LaunchFlareEnv is a fast-rise/~0.9s-decay one-shot
        // (see its trigger above), so this reads as "does a kinda small
        // rev" once per launch rather than staying loud for the whole
        // 0-10kph window. Still gated by l9LaunchGateSmooth so it can only
        // fire in the actual launch speed range.
        float hollowLoad = 0.6f + ld * 0.4f + rn * 0.15f + l9LaunchGateSmooth * l9LaunchFlareEnv * 0.75f;
        double hollow = (box * p.boxGain + pipe * p.pipeGain) * hollowLoad * muffle * eBase * 0.85;

        double outSig =
              comb   * eBase * 0.85
            + sub    * eBase * 0.55
            + rot    * eBase * 0.5
            + hollow
            + turbo
            + turboWhine
            + knock
            + vtick;

        ph_cb_fire  = (ph_cb_fire  + hz             * invSR) % 1.0;
        ph_cb_pilot = (ph_cb_fire + 0.11) % 1.0;
        ph_cb_sub   = (ph_cb_sub   + hz * 0.5       * invSR) % 1.0;
        ph_cb_rot   = (ph_cb_rot   + hz / 3.0       * invSR) % 1.0;
        ph_cb_cam   = (ph_cb_cam   + hz * 0.5       * invSR) % 1.0;

        return Math.Tanh(outSig * 1.08);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  X10 CORE — COMPLETE REBUILD. Real, physically-grounded architecture
    //  (same FirePulse/struck-resonator/turbo-research foundation L9/ISL9
    //  use), sized UP for the real Cummins X10: 10.0L inline-6, wet-sleeve
    //  block, 320-450hp/1,000-1,650 lb-ft -- confirmed the biggest, most
    //  powerful diesel in this fleet (replaces both L9 and X12 in the real
    //  Cummins lineup). This replaces the old bespoke sine-stack "X10 CORE
    //  (Hybrid Texture)" block, which started deep and drifted high-pitched
    //  over a series of edits aimed at making it "more like the others" --
    //  full rebuild from L9's real core as the base, not a patch.
    //
    //  What makes X10 read as genuinely bigger/deeper than L9, using real
    //  acoustic levers rather than a volume/pitch knob:
    //    . subGain (0.88, heaviest in the fleet) -- more rotating mass.
    //    . Own load-dependent DEEP-SHAKE mechanism (x10r_shakeGain), same
    //      idea as ISL9's subShakeGain but stronger -- X10's real spec sheet
    //      leads with power-to-weight and a wide 1,000-1,650 lb-ft torque
    //      plateau, so hard load should audibly dig in low, not just get
    //      louder.
    //    . boxHz sits lower than every other engine in the fleet (128Hz) --
    //      a bigger physical block cavity genuinely resonates lower.
    //    . turboBright (0.82, lowest in the fleet) -- the higher torque
    //      ceiling needs a bigger compressor wheel, which spins slower for
    //      a given boost level than a smaller L9-class turbo (same real
    //      physics already used to justify L9N's lower turbo pitch vs the
    //      diesel line).
    //    . pipeHz (1180Hz) is lower than L9's 1600Hz but still sits inside
    //      the confirmed-dominant 1000-2500Hz heavy-duty-diesel noise band
    //      from the same acoustic research L9/ISL9 already cite -- "deep"
    //      here doesn't mean abandoning that research, it means sitting at
    //      the low end of the real dominant band instead of the high end.
    // ═════════════════════════════════════════════════════════════════════════
    private float  x10r_shakeGain    = 0f;
    private double ph_x10r_shakeSub;

    // [NEW] L9N high-RPM deepening -- same real idea as X10's shakeGain/
    // ISL9's subShakeGain, but keyed specifically on rn (RPM) rather than
    // ld (load): L9N previously had ZERO rev-dependent depth at all -- its
    // `sub` layer in DoGasCore was a flat-gain sine and `hollow` only
    // tracked load, never RPM, so no matter how hard it was revved the
    // engine never got any deeper. This gives it a real low-end lift that
    // climbs specifically with RPM.
    private float  l9nRevShakeGain   = 0f;
    private double ph_l9nRevShakeSub;

    private double DoX10RealCore(CombustionParams p, float hz, float rn, float ld,
                                  float engMul, double noiseClt, double noiseInd, double invSR)
    {
        float eBase = engMul * (0.44f + rn * 0.42f); // slightly hotter base than L9's 0.42+0.40 -- bigger engine reads as more present

        double fire  = FirePulse(ph_cb_fire, p.knockK);
        double pilot = FirePulse(ph_cb_pilot, Mathf.Max(1.2f, p.knockK - 1.0f)) * p.pilot;

        // [LESS SMOOTH -- per instruction] X10 was reading a little too
        // polished/clean once routed through the real core -- nudged the
        // drive harder still (1.9 -> 2.05) so the tanh clips a bit sooner
        // and the firing edge stays a touch grittier rather than rounding
        // off, without changing the underlying pulse shape or timing.
        double comb  = (fire - 0.28) + pilot;
        comb = Math.Tanh(comb * 2.05); // slightly harder drive than L9's 1.8 -- more torque, more bite per firing event

        // -- Deep shake -- load-dependent sub-frequency drop + extra weight,
        // same real idea as ISL9's subShakeGain but built around X10's own
        // torque plateau instead of ISL9's rev-driven shake. The sub
        // oscillator's own frequency eases DOWN under hard load (toward
        // 0.35x instead of the flat 0.5x every other core uses), and its
        // gain climbs -- this is what makes hard acceleration read as the
        // engine genuinely digging in low, not just getting louder.
        float shakeTarget = Mathf.Clamp01(ld * 0.65f + rn * 0.45f);
        x10r_shakeGain += (shakeTarget - x10r_shakeGain) * (shakeTarget > x10r_shakeGain ? 0.010f : 0.006f);
        float subRatio = Mathf.Lerp(0.5f, 0.35f, x10r_shakeGain);
        double subMain  = Math.Sin(2.0 * Math.PI * ph_cb_sub) * p.subGain * (1.0 + x10r_shakeGain * 0.5f);
        double subDeep  = Math.Sin(2.0 * Math.PI * ph_x10r_shakeSub) * p.subGain * x10r_shakeGain * 0.55f;
        double sub = subMain + subDeep;
        double rot = Math.Sin(2.0 * Math.PI * ph_cb_rot) * 0.32 * (0.4 + ld); // a touch heavier than L9's 0.30 -- bigger reciprocating mass

        // Diesel knock / nailing -- a bit more present than L9's (real:
        // higher peak cylinder pressure capability for the torque ceiling,
        // even with EPA17-class multi-injection cushioning it).
        float knockGate = (float)Math.Max(0.0, fire - pilot * 1.2);
        // [LESS SMOOTH] base floor nudged 0.16 -> 0.19 so there's audible
        // grit even off-load/off-gate, not just under acceleration.
        double knock = noiseClt * p.clatterGain * (0.19 + ld * 0.62) * engMul * (0.2 + knockGate * 1.4);

        // Valvetrain tick -- six-cylinder wet-sleeve block, same mechanism.
        double camGate = Math.Max(0.0, Math.Sin(2.0 * Math.PI * ph_cb_cam));
        double vtick = noiseInd * (0.005 + rn * 0.0035) * engMul * camGate;

        // Turbo -- lower-pitched than L9's (see header: bigger compressor
        // wheel for the higher torque ceiling), breathier air-noise
        // treatment matching the diesel family's mature-calibration voice.
        float turboTarget = ResolveTurboSpool(rn, ld);
        cb_turboVolSmooth += (turboTarget - cb_turboVolSmooth) * (turboTarget > cb_turboVolSmooth ? 0.010f : 0.004f);
        float turboV = cb_turboVolSmooth * (0.032f + ld * 0.052f) * engMul;

        double turboHz = (200.0 + cb_turboVolSmooth * 520.0) * p.turboBright; // lower base/range than L9's 240+620
        double pipeTone = Math.Sin(2.0 * Math.PI * ph_cb_turbo) * 0.32
                         + Math.Sin(2.0 * Math.PI * ph_cb_turbo * 0.5) * 0.20;
        double pipeNoise = noise_hi * 0.42 + noise_lp * 0.40;
        double turbo = (pipeTone + pipeNoise) * turboV * 0.56;
        ph_cb_turbo = (ph_cb_turbo + turboHz * invSR) % 1.0;

        double turboWhineHz = turboHz * TURBO_BLADE_COUNT;
        double turboWhine = (Math.Sin(2.0 * Math.PI * ph_cb_turboWhine) * 0.34 + noise_hi * 0.44) * turboV * 0.34
                           * (0.7 + 0.3 * fire);
        ph_cb_turboWhine = (ph_cb_turboWhine + turboWhineHz * invSR) % 1.0;

        // -- THE HOLLOW -- deepest cavity resonance in the fleet --
        double strike = fire * 1.0 + knock * 0.5;
        double fBox  = 2.0 * Math.Sin(Math.PI * p.boxHz  / SR);
        double fPipe = 2.0 * Math.Sin(Math.PI * p.pipeHz / SR);
        double box   = cb_resBox.Strike(strike, fBox,  1.0 / p.boxQ);
        double pipe  = cb_resPipe.Strike(strike, fPipe, 1.0 / p.pipeQ);
        float muffle = 1f - p.refine * 0.16f;
        float hollowLoad = 0.62f + ld * 0.42f + rn * 0.16f;
        double hollow = (box * p.boxGain + pipe * p.pipeGain) * hollowLoad * muffle * eBase * 0.88;

        double outSig =
              comb   * eBase * 0.88
            + sub    * eBase * 0.60
            + rot    * eBase * 0.52
            + hollow
            + turbo
            + turboWhine
            + knock
            + vtick;

        ph_cb_fire      = (ph_cb_fire      + hz              * invSR) % 1.0;
        ph_cb_pilot     = (ph_cb_fire + 0.11) % 1.0;
        ph_cb_sub       = (ph_cb_sub       + hz * 0.5         * invSR) % 1.0;
        ph_x10r_shakeSub= (ph_x10r_shakeSub+ hz * subRatio    * invSR) % 1.0;
        ph_cb_rot       = (ph_cb_rot       + hz / 3.0         * invSR) % 1.0;
        ph_cb_cam       = (ph_cb_cam       + hz * 0.5         * invSR) % 1.0;

        return Math.Tanh(outSig * 1.10); // slightly hotter final saturation than L9's 1.08 -- bigger engine, a bit more presence overall
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GAS CORE — spark ignition. NOT a sine: still a firing pulse train, but
    //  rounded (no compression-ignition nail), plus throttled-intake honk and a
    //  brighter exhaust the diesels don't have. Keeps the loping CNG idle,
    //  afterfire pop, and load moan.
    // ═════════════════════════════════════════════════════════════════════════
    private double DoGasCore(CombustionParams p, float hz, float rn, float ld,
                             float engMul, double noiseClt, double noiseInd, double invSR)
    {
        // L9N-specific character (v2, real-world grounded): Cummins confirms
        // the L9N shares its block with the L9 diesel (same rigidity/piston
        // cooling/bearing life -- "L9 as a base" mechanically), but runs
        // spark-ignited stoichiometric combustion with cooled EGR --
        // fundamentally smoother than diesel's compression-ignition knock,
        // and multiple manufacturer sources confirm natural gas engines
        // "operate more quietly than diesel engines." isL9N (as opposed to
        // ISL G/Westport, the older teaser variant sharing this same core)
        // gets: no noise component in the intake honk (that noiseInd term
        // was the "windy" artifact -- a real throttled spark engine's
        // intake honk is a tonal resonance, not an airy/breathy one),
        // duller/lower exhaust brightness (less three-way-cat sparkle,
        // more low-end weight), and a small extra low-end push for "a bit
        // deeper." ISL G keeps its original rougher/breathier teaser
        // character untouched -- this only changes the L9N path.
        bool isL9N = p.isGas && !p.westport;

        float eBase = engMul * (0.46f + rn * 0.44f);

        // Loping idle wobble (2.2 Hz, deeper/slower for Westport), fades w/ RPM.
        cb_wobblePhase += (float)invSR;
        float wobHz    = p.westport ? 1.7f : 2.2f;
        float wobDepth = (p.westport ? 0.10f : 0.07f) * Mathf.Clamp01(1f - rn * 3f);
        double wobble  = 1.0 + Math.Sin(cb_wobblePhase * Math.PI * 2.0 * wobHz) * wobDepth;

        // Westport micro-misfire: occasional single-event dropout (rough old
        // ignition module). Purely a brief amplitude notch on the firing tone.
        // L9N's "Enhanced electronic control module" (per Cummins) is a
        // proper modern ignition system -- no misfire dropout for it at all.
        double misfire = 1.0;
        if (p.westport)
        {
            cb_misfireCooldown -= (float)invSR;
            if (cb_misfireCooldown <= 0f && (NextNoiseSample() * 0.5 + 0.5) > 0.9994)
            {
                cb_misfireEnv = 1f;
                cb_misfireCooldown = 0.4f + (float)(NextNoiseSample() * 0.5 + 0.5) * 1.2f;
            }
            if (cb_misfireEnv > 0.001f) { misfire = 1.0 - cb_misfireEnv * 0.55; cb_misfireEnv *= 0.94f; }
        }

        // Rounded spark-combustion pulse — softer than diesel, no knock noise.
        // [FURTHER SMOOTHED for L9N] gentler tanh drive so the pulse edge is
        // rounder, not just quieter -- "smoother" is a shape change, not
        // just a volume change.
        double fire = FirePulse(ph_cb_fire, p.knockK);
        double comb = Math.Tanh((fire - 0.30) * (isL9N ? 1.15f : 1.4f));

        // [NOTE] subShakeGain (ISL9's "shake"/"go deep" features) is a
        // diesel-only concept -- ISL9 always routes through DoDieselCore
        // above, never this function, so there's nothing to wire in here.
        // [RETUNED] 1.12 -> 0.98 -- L9N was skewing too deep overall: this
        // sub-boost stacked on top of ResolveCombustion's already-elevated
        // L9N subGain (0.82, now trimmed to 0.74 below) AND the reduced
        // exBright top-end (see below), so proportionally almost the whole
        // voice was low end. Trimmed here instead of just cutting subGain
        // alone, so the fix doesn't also mute the deliberate "deeper, not
        // windy" character the engine is supposed to have -- just brings it
        // back into balance against the rest of the voice.
        double sub = Math.Sin(2.0 * Math.PI * ph_cb_sub) * p.subGain * (isL9N ? 0.98f : 1.0f);

        // [NEW -- HIGH-RPM DEEPENING, L9N only] Real mechanism this stands
        // in for: more air/fuel flowing per second at higher RPM means more
        // low-frequency energy off the block/driveline, same physical idea
        // X10's shakeGain and ISL9's subShakeGain already model for the
        // diesels -- L9N just never had an equivalent. Deliberately keyed on
        // `rn` alone (not blended with `ld` the way X10's is), per request
        // that this specifically show up "especially at HIGH RPMS" rather
        // than just under hard load. A second, slightly-detuned-low sub
        // oscillator fades in and gets LOUDER as rn climbs -- not a pitch
        // shift of the existing sub (that would read as fighting the fixed
        // combustion Hz), an additional layer underneath it. Capped well
        // under X10/ISL's own shake depth (L9N stays the softest, quietest
        // engine in the fleet overall -- see rpmSmoothCap/knockK -- this
        // just stops it from being flat/static at redline, not competing
        // with the diesels for deepest voice).
        double l9nRevSub = 0.0;
        if (isL9N)
        {
            float revShakeTarget = Mathf.Clamp01((rn - 0.20f) / 0.65f); // starts building past ~20% rn, maxes near redline
            l9nRevShakeGain += (revShakeTarget - l9nRevShakeGain) * (revShakeTarget > l9nRevShakeGain ? 0.012f : 0.006f);
            l9nRevSub = Math.Sin(2.0 * Math.PI * ph_l9nRevShakeSub) * p.subGain * l9nRevShakeGain * 0.42;
            ph_l9nRevShakeSub = (ph_l9nRevShakeSub + hz * 0.38 * invSR) % 1.0; // sits below the main 0.5x sub -- genuinely deeper, not just louder
        }

        // [REBUILT] Signature L9N whine — real-world grounded, redone as a
        // MID/DEEP moan, not a high-pitched whistle. Real spec: Cummins'
        // own L9N sheet confirms a "Cummins Wastegated Turbocharger...
        // developed by Cummins Turbo Technologies with electronic control
        // for precise air handling" — and heavy-duty truck/bus-scale
        // turbochargers characteristically produce a lower, moan-like tone
        // than the thin, high-pitched whistle a small, fast-spinning car
        // turbo gives off (bigger compressor wheel, lower shaft speed for
        // the same boost). Combined with the L9N's 3-way-catalyst-muffled,
        // stoichiometric-spark-ignited "quiet operation" (Cummins' own
        // phrase, repeated across every L9N spec sheet), the whole thing
        // reads as a mid-register electronic-wastegate moan riding under
        // the engine, not a shrill accessory whistle on top of it. This
        // still lives HERE, in DoGasCore (engine-level, fires regardless of
        // transmission — b400r, zf, nxt, whatever).
        // [TONED DOWN] rn coefficient 0.55 -> 0.35 -- was getting too deep
        // too fast under hard revving. Envelope also slowed (0.015/0.008 ->
        // 0.008/0.005) to fix a reported "choppy" quality -- the faster
        // envelope was tracking rn/ld's frame-to-frame jitter closely
        // enough to step audibly instead of gliding.
        float l9nWhineTarget = isL9N ? Mathf.Clamp01(0.15f + rn * 0.35f + ld * 0.30f) : 0f;
        cb_l9nWhineVol += (l9nWhineTarget - cb_l9nWhineVol) * (l9nWhineTarget > cb_l9nWhineVol ? 0.008f : 0.005f);
        double l9nWhine = 0.0;
        if (cb_l9nWhineVol > 0.002f)
        {
            double l9nWhineHz = 130.0 + rn * 145.0 + ld * 60.0; // mid/deep register (~130-335Hz), not a treble whistle
            l9nWhine = Math.Sin(2.0 * Math.PI * ph_cb_l9nWhine) * cb_l9nWhineVol * 0.052 * engMul
                     + Math.Sin(2.0 * Math.PI * ph_cb_l9nWhine * 1.5) * cb_l9nWhineVol * 0.018 * engMul;
            ph_cb_l9nWhine = (ph_cb_l9nWhine + l9nWhineHz * invSR) % 1.0;
        }

        // [NEW] Real turbo whistle + whine for L9N — this is genuinely
        // separate from the "L9N whine" deep wastegate moan just above
        // (that's the electronic-wastegate-actuator moan; this is the
        // actual shaft-rotation/blade-pass turbo tone the diesel engines
        // already have, which L9N had none of at all before). Research:
        // Cummins' own materials confirm natural-gas turbos are SMALLER
        // than the diesel equivalent for the same engine family --
        // "a diesel engine might need an HE500 turbo, but a natural gas
        // engine could use an HE300 or HE400" -- since stoichiometric
        // combustion needs less air, which in theory means a smaller wheel
        // spinning faster for a given boost. In practice, though, direct
        // listening feedback said the initial higher-pitched version read
        // wrong -- see the pitch formula below, which now sits BELOW the
        // diesel whistle instead. No turboHunt wobble here --
        // that's specifically a loose-VGT-actuator behavior, and L9N's
        // turbo is WASTEGATED (fixed geometry, confirmed via Cummins'
        // description of a "dual wastegate port" design for NG turbos),
        // not variable geometry, so there's no sliding nozzle ring to hunt.
        float l9nTurboTarget = isL9N ? ResolveTurboSpool(rn, ld) : 0f;
        cb_turboVolSmooth += (l9nTurboTarget - cb_turboVolSmooth) * (l9nTurboTarget > cb_turboVolSmooth ? 0.010f : 0.004f);
        double l9nTurboV = cb_turboVolSmooth * (0.028f + ld * 0.045f) * engMul;
        // [LOWERED] Was set at least as high as the diesel whistle
        // (reasoning: NG turbos are physically smaller for the same engine
        // family per Cummins' own materials, and smaller wheels spin
        // faster for a given boost) -- but that's a theoretical inference,
        // and turbo whine's actual perceived pitch is notoriously hard to
        // predict from shaft-speed math alone (real turbo tones can extend
        // into the ultrasonic range, well past what's actually audible or
        // meaningful to model). Direct listening feedback says it read too
        // high in practice, so this defers to that over the theory: pulled
        // down below the diesel range instead of at/above it.
        // [LOWERED FURTHER] Was 320+vol*1400 (320-1720Hz) -- still too high
        // for "just a mid-low whine." Pulled down to a genuinely low,
        // simple register, and the paired blade-pass whine below is now
        // much quieter so this reads as ONE consistent mid-low tone
        // instead of a two-tier whistle+whine stack.
        double l9nTurboHz = 150.0 + cb_turboVolSmooth * 320.0;
        // [TURNED DOWN] Same whistle-only reduction as the diesel side.
        double l9nTurbo = Math.Sin(2.0 * Math.PI * ph_cb_turbo) * l9nTurboV * 0.55;
        ph_cb_turbo = (ph_cb_turbo + l9nTurboHz * invSR) % 1.0;

        // Whine (blade-pass) -- now the real 6x blade-count ratio, matching
        // the diesel side, for consistency across the engine family. NOTE:
        // an earlier pass here specifically found a higher pitch "read too
        // high in practice" per direct listening feedback -- l9nTurboHz's
        // own ceiling is already low (max ~470Hz vs the diesel whistle's
        // ~860Hz), so even at the real 6x ratio this tops out around
        // 2.8kHz, well below what was rejected before, but this is the one
        // spot in the turbo rework most likely to need retuning back down
        // if it still reads as too bright for the NG variant specifically.
        double l9nTurboWhineHz = l9nTurboHz * TURBO_BLADE_COUNT;
        // [QUIETED] 0.26 -> 0.09 -- this was competing with the whistle
        // enough to read as two separate tones stacked together. Now it's
        // a faint texture riding under the whistle, not a rival layer.
        // [PUSHED UP] Was 0.09 -- too buried to read as a real layer.
        // Blended with `fire` the same way the ISL9/L9 whines now are, so
        // it mixes with the engine's own firing pulse instead of sitting
        // on top as an isolated tone.
        double l9nTurboWhine = Math.Sin(2.0 * Math.PI * ph_cb_turboWhine) * l9nTurboV * 0.22
                              * (0.7 + 0.3 * fire);
        ph_cb_turboWhine = (ph_cb_turboWhine + l9nTurboWhineHz * invSR) % 1.0;

        // ═════════════════════════════════════════════════════════════════
        //  CNG PRESSURE REGULATOR EXPANSION FLOW  —  L9N only, no diesel
        //  equivalent. Grounded in the real New Flyer XN40 CNG operator's
        //  manual: the fuel system runs TWO regulators in series -- a high-
        //  pressure regulator dropping 3,600 psi to ~125 psi, then a zero-
        //  pressure regulator taking it to 0 psi for delivery to the
        //  engine's venturi gas mixer. The manual specifically notes the
        //  high-pressure regulator gets cold enough from gas expansion that
        //  ice would form and block flow without engine coolant routed
        //  through it -- that's a genuinely violent, continuous expansion
        //  process happening whenever the engine is consuming fuel, and
        //  nothing on a diesel bus does anything like it.
        //  Modeled as a fine, continuous expansion hiss that tracks fuel
        //  demand (load), NOT a periodic click/valve event -- deliberately
        //  avoiding the percussive-event pattern that got pulled twice
        //  already (injector tick, EGR click). Real regulators flow
        //  smoothly and continuously; there's nothing to tick here.
        // ═════════════════════════════════════════════════════════════════
        float cngRegTarget = isL9N ? Mathf.Clamp01(0.12f + ld * 0.55f + rn * 0.25f) * 0.020f * engMul : 0f;
        cb_cngRegVolSmooth += (cngRegTarget - cb_cngRegVolSmooth) * (cngRegTarget > cb_cngRegVolSmooth ? 0.012f : 0.008f);
        double cngRegFlow = 0.0;
        if (cb_cngRegVolSmooth > 0.0004f)
        {
            // Broadband expansion hiss (noise_hi = the finest texture
            // available here) plus a faint tonal edge from the regulator
            // body itself resonating with the flow through it.
            cngRegFlow = noise_hi * cb_cngRegVolSmooth
                       + Math.Sin(2.0 * Math.PI * ph_cb_cngReg) * cb_cngRegVolSmooth * 0.18;
            double cngRegHz = 1150.0 + ld * 380.0;
            ph_cb_cngReg = (ph_cb_cngReg + cngRegHz * invSR) % 1.0;
        }

        // Throttled intake honk — a resonant induction growl that blooms with
        // load. Diesels are unthrottled and have nothing like this.
        // [L9N FIX] Removed the noiseInd broadband term for L9N specifically
        // -- that raw noise-blend was the "windy" quality. A real spark
        // engine's throttle-body honk is a tonal air-column resonance (the
        // Strike() below already gives that), not hiss layered under it.
        // ISL G/Westport keeps the noise blend -- its rougher teaser-era
        // intake is allowed to sound breathier.
        double intakeStrike = fire * (0.4 + ld * 1.2) + (isL9N ? 0.0 : noiseInd * 0.3);
        double fIntake = 2.0 * Math.Sin(Math.PI * 210.0 / SR);
        double intake = cb_resIntake.Strike(intakeStrike, fIntake, 1.0 / 3.2) * (0.05 + ld * 0.16) * engMul;

        // Brighter three-way-cat exhaust formant (higher than diesel boom).
        // [L9N FIX] Duller and quieter for L9N -- "deeper, not windy" means
        // less top-end sparkle here specifically, letting the sub/hollow
        // low end carry more of the voice instead.
        // [RETUNED] Cut softened slightly (0.025/0.028 -> 0.030/0.030) --
        // combined with the sub trim above, this helps pull the voice back
        // toward balanced rather than skewed all the way to the bottom end.
        double exBright = Math.Sin(2.0 * Math.PI * ph_cb_body) * (isL9N ? (0.030f + ld * 0.030f) : (0.04f + ld * 0.05f)) * engMul;
        ph_cb_body = (ph_cb_body + ((isL9N ? 150.0 : 180.0) + rn * (isL9N ? 90.0 : 120.0)) * invSR) % 1.0;

        // Hollow cavities struck by the (softer) gas pulses.
        double strike = fire;
        double fBox  = 2.0 * Math.Sin(Math.PI * p.boxHz  / SR);
        double fPipe = 2.0 * Math.Sin(Math.PI * p.pipeHz / SR);
        double box   = cb_resBox.Strike(strike, fBox,  1.0 / p.boxQ);
        double pipe  = cb_resPipe.Strike(strike, fPipe, 1.0 / p.pipeQ);
        // [NEW] hollowLoad now also tracks rn for L9N specifically (was
        // ld-only for every gas engine before) -- same reasoning as
        // l9nRevSub above: the box/pipe cavity should read fuller as RPM
        // climbs, not just under load. ISL G/Westport unchanged.
        float hollowRnBoost = isL9N ? rn * 0.30f : 0f;
        double hollow = (box * p.boxGain + pipe * p.pipeGain) * (0.6 + ld * 0.4 + hollowRnBoost) * eBase * 0.5;

        // Faint gas-combustion breath instead of diesel clatter noise.
        double breath = noiseClt * (isL9N ? 0.003 : 0.006) * (0.4 + ld * 0.9) * engMul * Math.Max(0.0, fire - 0.25);

        // [NEW] Slow idle surge — real, documented CNG behavior (owner
        // reports on stoichiometric CNG idle: RPM drifts low/rough for
        // ~2s, then up to normal for ~5s, repeating -- a slow, multi-
        // second surge from the idle-speed control hunting for a stable
        // idle against the narrow stoichiometric air/fuel window, a
        // completely different timescale/cause than the existing fast
        // 2.2Hz per-cylinder lope above -- both run together, not one
        // replacing the other. ~7s period, only present near true idle
        // (fades out as soon as revs climb off idle).
        float idleSurgeGate = isL9N ? Mathf.Clamp01(1f - rn * 4f) : 0f;
        cb_idleSurgePhase += (float)invSR / 7.0f;
        cb_idleSurgePhase %= 1f;
        float idleSurge = 1f + Mathf.Sin(cb_idleSurgePhase * Mathf.PI * 2f) * 0.05f * idleSurgeGate;

        double outSig =
              comb    * eBase * (isL9N ? 0.66f : 0.80f)
            + sub     * eBase
            + l9nRevSub * eBase
            + hollow
            + intake
            + exBright
            + breath
            + l9nWhine
            + l9nTurbo
            + l9nTurboWhine
            + cngRegFlow;
        outSig *= idleSurge;

        outSig *= wobble * misfire;
        // Overall quieter for L9N -- not just individual layers dialed down,
        // the whole voice sits a bit lower in the mix, matching "operates
        // more quietly than diesel" from Cummins/Mack's own material.
        if (isL9N) outSig *= 0.88;

        // Afterfire pop on a hard lift — the CNG exhaust "bark". Softer for
        // L9N -- its modern ECM/ignition module doesn't bark as roughly as
        // the older ISL G teaser did.
        float ldDropPop = cb_prevLdForWg - ld;
        if (ldDropPop > 0.18f && rn > 0.15f) cb_wastegateVol = Mathf.Min(1f, cb_wastegateVol + ldDropPop * (isL9N ? 0.85f : 1.3f));
        cb_prevLdForWg = ld;
        if (cb_wastegateVol > 0.0015f)
        {
            double popHz  = 30.0 + (NextNoiseSample() * 0.5 + 0.5) * 40.0;
            float  popEnv = 0.5f + 0.5f * (float)Math.Sin(2.0 * Math.PI * ph_cb_wastegate);
            outSig += (noise_hi + Math.Sin(2.0 * Math.PI * ph_cb_wastegate * 1.3) * 0.5)
                      * cb_wastegateVol * (isL9N ? 0.045 : 0.06) * npcVolumeScale * popEnv;
            ph_cb_wastegate = (ph_cb_wastegate + popHz * invSR) % 1.0;
            cb_wastegateVol *= 0.995f;
            if (cb_wastegateVol < 0.003f) cb_wastegateVol = 0f;
        }

        // Deep load moan.
        double moan = Math.Sin(2.0 * Math.PI * ph_cb_rot) * (0.04 + ld * 0.05) * engMul;
        ph_cb_rot = (ph_cb_rot + (26.0 + ld * 20.0) * invSR) % 1.0;
        outSig += moan;

        ph_cb_fire = (ph_cb_fire + hz       * invSR) % 1.0;
        ph_cb_sub  = (ph_cb_sub  + hz * 0.5 * invSR) % 1.0;

        return Math.Tanh(outSig * 1.05);
    }

    // ── L9-family stylized diesel transients (groogle / stopping puff / coast
    //    groan). Ported from the legacy L9 branch so nothing is lost. ─────────
    private void DoDieselTransients(ref double engineSample, float rn, float ld, float engMul, double invSR)
    {
        bool isStopped = (spd < 0.2f);
        bool isTipIn   = (accel > 0.05f && spd <= 10f);
        if ((isStopped && !l9_wasStopped) || (isTipIn && !l9_wasAccel)) l9_groogleTimer = 0.5f;
        l9_wasStopped = isStopped;
        l9_wasAccel   = (accel > 0.05f);
        if (l9_groogleTimer > 0f) l9_groogleTimer -= (float)invSR;
        if (l9_groogleTimer > 0f)
        {
            float groogleEnv = Mathf.Clamp01(l9_groogleTimer * 2.0f);
            double bubblingAM = Math.Max(0.0, Math.Sin(2.0 * Math.PI * ph_cb_sub * 1.5));
            engineSample += (Math.Sin(2.0 * Math.PI * ph_cb_fire) * 0.6 + noise_lp * 0.4) * bubblingAM * 0.9 * groogleEnv * engMul;
        }

        bool under3 = (spd <= 3.0f);
        if (under3 && !l9_wasUnder3 && accel < 0.05f) l9_brakePuffVol = 1.0f;
        l9_wasUnder3 = under3;
        l9_brakePuffVol *= (1.0f - 12.0f * (float)invSR);
        engineSample += (noise_hi * 0.8 + noise_lp * 0.2) * Math.Max(0, l9_brakePuffVol - 0.05f) * engMul;

        float coastTarget = (accel <= 0.01f && spd > 10f) ? 1.0f : 0f;
        l9_coastGroanVol += (coastTarget - l9_coastGroanVol) * (coastTarget > l9_coastGroanVol ? 0.012f : 0.0035f);
        if (l9_coastGroanVol > 0.001f)
        {
            double groanHz = 55.0 + (rn * 20.0);
            double groanTone = Math.Tanh((Math.Sin(2.0 * Math.PI * ph_l9_coastMoan1) * 0.8
                                        + Math.Sin(2.0 * Math.PI * ph_l9_coastMoan2) * 0.5) * 1.8);
            engineSample += groanTone * l9_coastGroanVol * engMul * 0.45f;
            ph_l9_coastMoan1 = (ph_l9_coastMoan1 + groanHz        * invSR) % 1.0;
            ph_l9_coastMoan2 = (ph_l9_coastMoan2 + (groanHz*1.03) * invSR) % 1.0;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  MECHANICAL WHINE — gear-mesh driveline whine, gear 3+, EVERY
    //  transmission. Not an Allison-only quirk (unlike DoB400RWhineDSP /
    //  the other tx-specific pump/converter whines called just above this
    //  in DoCombustionEngine) — this is the gear-mesh noise itself, which
    //  exists regardless of which box is bolted behind the engine. It's
    //  most audible on L9N specifically because that engine's quieter
    //  spark-ignited/3-way-catalyst voice (see DoGasCore) doesn't mask it
    //  under diesel clatter the way the diesel engines do — same driveline
    //  noise on both, just less buried on L9N.
    //
    //  Tone itself is deliberately NOT a clean oscillator: two close-detuned
    //  tones beat against each other (hoarse), broadband noise rides under
    //  it (airy), and a slow ~4Hz wobble plus tanh saturation gives it a
    //  straining, groaning quality rather than a whistle.
    //
    //  "Echo" is a genuine delayed repeat of the tone through a short ring
    //  buffer with feedback — not a second independent oscillator — so it
    //  reads as the whine bouncing/resonating off the gearbox and floor
    //  structure rather than a chorus effect.
    // ═════════════════════════════════════════════════════════════════════════
    private void DoMechanicalWhine(ref double txSample, int gear, float rn, float ld, float engMul, bool isL9N, double invSR)
    {
        float target = gear >= 3 ? Mathf.Clamp01(0.30f + rn * 0.50f + ld * 0.30f) : 0f;
        // [SMOOTHER] Attack/release slowed way down (0.015/0.010 -> 0.006/0.005)
        // -- the target itself still snaps 0->full the instant gear hits 3, so
        // a fast coefficient meant the whine was audibly "arriving" within a
        // couple frames of the shift. Slower smoothing spreads that onset out
        // into a genuine fade-in instead of a near-instant switch-on.
        cb_mechWhineVol += (target - cb_mechWhineVol) * (target > cb_mechWhineVol ? 0.006f : 0.005f);
        if (cb_mechWhineVol < 0.001f) return;

        // Gear-mesh rate tracks rn (revving intensity), not raw rpm, so it
        // reads as driveline-driven rather than crank-driven.
        double baseHz = 240.0 + rn * 260.0;

        // HOARSE: two close-detuned tones beating against each other rather
        // than one clean oscillator -- a clean sine reads as a whistle, a
        // beating pair reads as a rough, straining mesh.
        double t1 = Math.Sin(2.0 * Math.PI * ph_cb_mechWhine);
        double t2 = Math.Sin(2.0 * Math.PI * ph_cb_mechWhine2);
        ph_cb_mechWhine  = (ph_cb_mechWhine  + baseHz         * invSR) % 1.0;
        ph_cb_mechWhine2 = (ph_cb_mechWhine2 + baseHz * 1.012 * invSR) % 1.0; // [TONED DOWN] 2.8%->1.2% detune -- was beating fast enough to read as a distinct "ouh ouh ouh" pulse instead of a blended hoarse texture

        // GROAN: slow wobble on top, like a straining mesh that isn't
        // spinning perfectly steady -- plus tanh saturation so the waveform
        // edges are rough/rasped rather than a smooth sine.
        cb_mechGroanPhase += (float)invSR * 2.6f;
        cb_mechGroanPhase %= 1f;
        float groanWob = 1f + Mathf.Sin(cb_mechGroanPhase * Mathf.PI * 2f) * 0.08f;
        double dryTone = Math.Tanh((t1 * 0.55 + t2 * 0.45) * 1.15) * groanWob;

        // Prominent — meant to read clearly at gear 3+, not as a buried
        // texture. L9N still gets a push since it's the engine this is most
        // commonly heard on (see header), but [TONED DOWN] 1.6->1.15 -- the
        // old multiplier combined with the faster beat/groan above to make
        // it stick out as a separate pulsing whine instead of blending into
        // the rest of the driveline voice.
        float vol = cb_mechWhineVol * (0.030f + ld * 0.022f) * engMul * (isL9N ? 1.15f : 1.0f);
        double dry = dryTone * vol;

        const float DELAY_SEC = 0.14f;
        int bufLen = Mathf.Max(64, Mathf.CeilToInt(DELAY_SEC / (float)invSR) + 1);
        if (cb_mechEchoBuf == null || cb_mechEchoBuf.Length != bufLen)
        {
            cb_mechEchoBuf = new float[bufLen];
            cb_mechEchoHead = 0;
        }
        int readIdx = (cb_mechEchoHead + 1) % bufLen;
        float echo = cb_mechEchoBuf[readIdx];
        // Feedback (echo folded back in at reduced gain) so repeats decay
        // over several cycles instead of a single flat delay tap.
        cb_mechEchoBuf[cb_mechEchoHead] = (float)(dry + echo * 0.35f);
        cb_mechEchoHead = readIdx;

        txSample += dry + echo * 0.55;
    }
}