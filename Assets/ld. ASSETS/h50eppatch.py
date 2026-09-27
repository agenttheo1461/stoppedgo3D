
import sys, shutil

FILE   = 'BusAudioEngine.cs'
BACKUP = FILE + '.bak'

with open(FILE, 'r', encoding='utf-8') as f:
    lines = f.readlines()

shutil.copy(FILE, BACKUP)
ok = True

# ─────────────────────────────────────────────────────────────────────────────
def replace_block(lines, start_tag, end_tag, new_text, label):
    """Find the first line containing start_tag, scan forward for end_tag,
       replace the inclusive range with new_text (a list of str lines)."""
    global ok
    try:
        si = next(i for i, l in enumerate(lines) if start_tag in l)
    except StopIteration:
        print(f'[FAIL] {label}: start marker not found  ({start_tag!r})')
        ok = False
        return lines
    try:
        ei = next(i for i, l in enumerate(lines) if i >= si and end_tag in l)
    except StopIteration:
        print(f'[FAIL] {label}: end marker not found after line {si+1}  ({end_tag!r})')
        ok = False
        return lines
    print(f'[ OK ] {label}  (lines {si+1}–{ei+1})')
    return lines[:si] + new_text + lines[ei+1:]

def insert_after(lines, tag, new_text, label):
    """Insert new_text lines immediately after the first line containing tag."""
    global ok
    try:
        i = next(i for i, l in enumerate(lines) if tag in l)
    except StopIteration:
        print(f'[FAIL] {label}: anchor not found  ({tag!r})')
        ok = False
        return lines
    print(f'[ OK ] {label}  (after line {i+1})')
    return lines[:i+1] + new_text + lines[i+1:]

def delete_block(lines, start_tag, end_tag, label):
    return replace_block(lines, start_tag, end_tag, [], label)

# ══════════════════════════════════════════════════════════════════════════════
# PATCH 1 — new H50EP state fields
# ══════════════════════════════════════════════════════════════════════════════
P1_NEW = [
    '    private float  h50_pipeAirVol  = 0f;  // breathy air volume smoother\n',
    '    private float  h50_moanVol     = 0f;  // OOOUUUHH moan volume smoother\n',
    '    private double h50_moanLFO     = 0.0; // moan AM LFO phase (~1.1 Hz OUH rhythm)\n',
]
lines = insert_after(lines, 'private float h50EngineRevZone = 0f;', P1_NEW,
                     'PATCH 1 — H50EP state fields')

# ══════════════════════════════════════════════════════════════════════════════
# PATCH 2 — remove old unconditional H50EP phase-advance block
# ══════════════════════════════════════════════════════════════════════════════
lines = delete_block(lines,
    'ph_h50Pipe1 += 120.0  * invSR',           # start marker
    'if (ph_h50Pipe3 > Math.PI * 2.0) ph_h50Pipe3 -= Math.PI * 2.0;',  # end marker
    'PATCH 2 — remove unconditional H50EP phase advances + resHz')

# ══════════════════════════════════════════════════════════════════════════════
# PATCH 3 — replace old pipe/motor overlay block with the new pipe-blow engine
# ══════════════════════════════════════════════════════════════════════════════
P3_NEW = r"""
// ── H50EP: pipe-blow engine + startup oscillators + OOOUUUHH moan ─────────
// All H50EP-specific phases are managed inside this block.
// H40EP leaves all h50* state variables at 0 and is unaffected.
if (isH50)
{
    // Silence the ISL9 diesel — pipe resonance IS the engine for H50EP
    engineSample = 0.0;
    // AC-independent volume scale, but respects stop-start fade
    float h50PipeMul = npcVolumeScale * engVolPersonality * h4x_engVolMul;

    // ── PIPE ENGINE ───────────────────────────────────────────────────────
    // Fundamental frequency tracks normalized RPM (rn).
    // Revving up = more airflow = louder blow + brighter tone.
    double pipeFundHz  = 100.0 + rn * 190.0 + ld * 48.0;
    float  pipeToneVol = (0.16f + rn * 0.52f + ld * 0.30f) * h50PipeMul;
    float  pipeAirTgt  = (0.06f + rn * 0.38f + ld * 0.22f) * h50PipeMul;
    h50_pipeAirVol    += (pipeAirTgt - h50_pipeAirVol) * (float)(invSR * 55.0);

    double pS1 = Math.Sin(2.0 * Math.PI * ph_h50Pipe1);
    double pS2 = Math.Sin(2.0 * Math.PI * ph_h50Pipe2);
    double pS3 = Math.Sin(2.0 * Math.PI * ph_h50Pipe3);

    // Tonal resonance (pipe harmonics) + noise_lp (the actual "blow")
    engineSample = pS1 * pipeToneVol * 0.82
                 + pS2 * pipeToneVol * 0.28
                 + pS3 * pipeToneVol * 0.08
                 + noise_lp * h50_pipeAirVol;

    ph_h50Pipe1 = (ph_h50Pipe1 + pipeFundHz       * invSR) % 1.0;
    ph_h50Pipe2 = (ph_h50Pipe2 + pipeFundHz * 2.0 * invSR) % 1.0;
    ph_h50Pipe3 = (ph_h50Pipe3 + pipeFundHz * 3.0 * invSR) % 1.0;

    // ── STARTUP OSCILLATORS (0–7 km/h arc) ───────────────────────────────
    // Three clear sines spin up from rest.  They fade in from 0–5 km/h,
    // hold peak 5–7 km/h, then fade out as the OOOUUUHH moan takes over.
    float startupIn    = Mathf.Clamp01(spd / 5f);
    float startupOut   = 1f - Mathf.Clamp01((spd - 7f) / 5f);
    float startupT     = startupIn * startupOut;
    float startupVol   = startupT * (0.10f + ld * 0.06f) * h50PipeMul;
    double sOscRamp    = Mathf.Clamp01(spd / 10f);
    double sOscBase    = 70.0 + sOscRamp * 90.0 + ld * 22.0;  // 70–160 Hz
    if (startupVol > 0.0005f)
    {
        engineSample += Math.Sin(2.0 * Math.PI * ph_h50_mot1) * startupVol * 0.55
                     + Math.Sin(2.0 * Math.PI * ph_h50_mot2) * startupVol * 0.30
                     + Math.Sin(2.0 * Math.PI * ph_h50_mot3) * startupVol * 0.15;
    }
    ph_h50_mot1 = (ph_h50_mot1 + sOscBase       * invSR) % 1.0;
    ph_h50_mot2 = (ph_h50_mot2 + sOscBase * 1.5 * invSR) % 1.0;
    ph_h50_mot3 = (ph_h50_mot3 + sOscBase * 2.1 * invSR) % 1.0;
    ph_h50_mot4 = (ph_h50_mot4 + sOscBase * 2.8 * invSR) % 1.0;
    ph_h50_mot5 = (ph_h50_mot5 + sOscBase * 3.5 * invSR) % 1.0;

    // ── OOOUUUHH PIPE MOAN (~7 km/h+) ────────────────────────────────────
    // Slow AM-modulated duct resonance.  ~1.1 Hz LFO → "OUH OUH OUH"
    // rhythm.  Fades in from 7 km/h; gets deeper and fuller under load.
    float moanFade    = Mathf.Clamp01((spd - 7f) / 5f);
    float moanTarget  = moanFade * (0.14f + ld * 0.11f) * h50PipeMul;
    h50_moanVol      += (moanTarget - h50_moanVol) * (float)(invSR * 4.5);
    h50_moanLFO      += invSR * (1.10 + ld * 0.45);
    if (h50_moanLFO > 1.0) h50_moanLFO -= 1.0;

    if (h50_moanVol > 0.0008f)
    {
        double moanEnv = (Math.Sin(2.0 * Math.PI * h50_moanLFO) + 1.0) * 0.5;
        moanEnv        = moanEnv * moanEnv; // squared → punchy "OUH" peak shape
        double moanHz  = 52.0 + spd * 1.1 + ld * 20.0;
        engineSample  += (  Math.Sin(2.0 * Math.PI * ph_h50_howl) * 0.70
                          + Math.Sin(2.0 * Math.PI * ph_h50_res1) * 0.34
                          + noise_lp * 0.14)
                       * h50_moanVol * moanEnv;
        ph_h50_howl = (ph_h50_howl + moanHz       * invSR) % 1.0;
        ph_h50_res1 = (ph_h50_res1 + moanHz * 2.0 * invSR) % 1.0;
    }
    else
    {
        ph_h50_howl = (ph_h50_howl + 52.0  * invSR) % 1.0;
        ph_h50_res1 = (ph_h50_res1 + 104.0 * invSR) % 1.0;
    }
    ph_h50_res2 = (ph_h50_res2 + 165.0 * invSR) % 1.0;
}
""".splitlines(keepends=True)

lines = replace_block(lines,
    'float pipe1 = Mathf.Sin((float)ph_h50Pipe1);',   # start
    'pipeMod;',                                         # end  (pipeShout last line)
    P3_NEW,
    'PATCH 3 — pipe-blow engine + startup oscillators + OOOUUUHH moan')

# ══════════════════════════════════════════════════════════════════════════════
# PATCH 4 — remove pipeWhine / pipeShout / pipeNoise txSample additions
# ══════════════════════════════════════════════════════════════════════════════
lines = delete_block(lines,
    'txSample += pipeWhine * pipeWhineVol',
    'txSample += pipeNoise;',
    'PATCH 4 — remove pipeWhine/pipeShout/pipeNoise txSample lines')

# ─────────────────────────────────────────────────────────────────────────────
if ok:
    with open(FILE, 'w', encoding='utf-8') as f:
        f.writelines(lines)
    print(f'\n✅  All 4 patches applied.  Backup → {BACKUP}')
else:
    shutil.copy(BACKUP, FILE)
    print(f'\n⚠️   One or more patches failed — file restored from backup.')
    print('    Check the [FAIL] lines above; apply those hunks manually.')
    sys.exit(1)