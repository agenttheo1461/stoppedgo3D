using System.Threading;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════════════════════
//  BusAudioEngine.BlinkerTick  --  the turn-signal / hazard tick, rendered INSIDE the engine's own audio
//  buffer (mixed on top of whatever ProcessAudio produced) instead of through a separate AudioSource.
//  That means it goes out through the same source, volume and spatial setup as the rest of the bus's
//  sound, so you hear it wherever you hear the bus -- not just in one spot.
//
//  One identical, short, soft tick per flash change (lens on AND lens off). Synthesized per sample:
//  a rounded attack, three quickly-damped low-mid partials, and a little softened noise for the tap.
//  Triggered from the main thread (BusExteriorLightController) via TriggerBlinkerTick(); the audio thread
//  picks the request up on its next buffer. StopBlinkerTick() cuts a tick that is still ringing.
// ═══════════════════════════════════════════════════════════════════════════════
public partial class BusAudioEngine
{
    [Range(0f, 1f)] public float blinkerTickVolume = 0.55f;
    [Tooltip("Amount of the added 'tsk' (the 'sucking on your teeth' c-sound layered on the tick): a small dental pop plus a short bright hiss. 0 = plain tick.")]
    [Range(0f, 1f)] public float blinkerTskAmount = 0.40f;
    private float _tskPrev;
    private uint  _tskSeed = 987654321u;

    private int _blinkTickRequests;   // written by the main thread
    private int _blinkTickStop;       // 1 = cut the current tick
    private int _blinkTickSeen;       // audio-thread copy of _blinkTickRequests
    private int _blinkTickPos = -1;   // samples rendered so far; -1 = idle
    private float _blinkTickLp;       // one-pole low-passed noise state
    private uint  _blinkTickSeed = 12345u;

    private float[] _tickSamples;   // the real recorded tick (BlinkerTickSamples); null = fall back to the synthesized one

    /// <summary>Main thread: play one tick.</summary>
    public void TriggerBlinkerTick()
    {
        if (_tickSamples == null) _tickSamples = BlinkerTickSamples.Get(); // decode on the main thread, never on the audio thread
        Interlocked.Increment(ref _blinkTickRequests);
    }

    /// <summary>Main thread: cut any tick that is still sounding, right now.</summary>
    public void StopBlinkerTick() => Interlocked.Exchange(ref _blinkTickStop, 1);

    /// <summary>Audio thread: add the tick to the buffer ProcessAudioCore already filled.</summary>
    private void MixBlinkerTick(float[] data, int channels)
    {
        if (Interlocked.Exchange(ref _blinkTickStop, 0) == 1)
        {
            _blinkTickPos = -1;
            _blinkTickSeen = Volatile.Read(ref _blinkTickRequests); // discard anything queued
            return;
        }
        int req = Volatile.Read(ref _blinkTickRequests);
        if (req != _blinkTickSeen)
        {
            _blinkTickSeen = req;
            _blinkTickPos = 0;
            _blinkTickLp = 0f;
            _blinkTickSeed = 12345u;
            _tskPrev = 0f;
            _tskSeed = 987654321u;
        }
        if (_blinkTickPos < 0) return;

        double sr = SR > 0 ? SR : 44100.0;

        // ── Real recorded tick (preferred): linear-interpolated playback, resampled to the output rate ──
        var rec = _tickSamples;
        if (rec != null && rec.Length > 1)
        {
            double step = BlinkerTickSamples.SampleRate / sr;             // source samples per output sample
            int recTotal = (int)(rec.Length / step);
            float rvol = blinkerTickVolume;
            for (int i = 0; i < data.Length; i += channels)
            {
                if (_blinkTickPos >= recTotal) { _blinkTickPos = -1; break; }
                double p = _blinkTickPos * step;
                int i0 = (int)p; int i1 = i0 + 1 < rec.Length ? i0 + 1 : i0;
                float frac = (float)(p - i0);
                float smp = (rec[i0] + (rec[i1] - rec[i0]) * frac) * rvol;

                // 'tsk' layer: a dental-click pop (~1.1 kHz, 4 ms) + a short bright hiss (first-difference-filtered noise,
                // ~12 ms) -- the teeth-sucking "c" sound riding on the front of the tick.
                if (blinkerTskAmount > 0f)
                {
                    float tt = (float)(_blinkTickPos / sr);
                    float hissEnv = (tt < 0.0006f ? tt / 0.0006f : 1f) * Mathf.Exp(-tt / 0.006f);
                    _tskSeed = _tskSeed * 1664525u + 1013904223u;
                    float nz = ((_tskSeed >> 9) / (float)(1u << 23)) * 2f - 1f;
                    float hiss = (nz - _tskPrev) * 0.5f; _tskPrev = nz;      // emphasise the top end
                    float pop = Mathf.Sin(2f * Mathf.PI * 1100f * tt) * Mathf.Exp(-tt / 0.004f) * 0.55f;
                    smp += (hiss * hissEnv + pop) * blinkerTskAmount * rvol;
                }
                for (int c = 0; c < channels; c++) data[i + c] += smp;
                _blinkTickPos++;
            }
            return;
        }

        const float dur = 0.17f, hold = 0.060f, tau = 0.075f;
        int total = (int)(sr * dur);
        float vol = blinkerTickVolume * 0.8f;

        for (int i = 0; i < data.Length; i += channels)
        {
            if (_blinkTickPos >= total) { _blinkTickPos = -1; break; }
            float t = (float)(_blinkTickPos / sr);

            float attack = Mathf.Clamp01(t / 0.003f);
            attack = attack * attack * (3f - 2f * attack);
            float release = t < hold ? 1f : 0.5f + 0.5f * Mathf.Cos(Mathf.PI * Mathf.Clamp01((t - hold) / (dur - hold)));
            float body = Mathf.Exp(-t / tau) * release;

            // Warmer / lower than before (was ~3.1 kHz and "glassy"): centre ~1.9 kHz with a woody 1.2 kHz body
            // and only a whisper of top. Each partial is doubled with a slightly detuned copy for the soft blur.
            float v = 0f;
            v += (Mathf.Sin(2f * Mathf.PI * 1900f * 0.993f * t) + Mathf.Sin(2f * Mathf.PI * 1900f * 1.007f * t)) * 0.5f * 0.55f;
            v += (Mathf.Sin(2f * Mathf.PI * 1250f * 0.993f * t) + Mathf.Sin(2f * Mathf.PI * 1250f * 1.007f * t)) * 0.5f * 0.38f;
            v += (Mathf.Sin(2f * Mathf.PI * 2900f * 0.993f * t) + Mathf.Sin(2f * Mathf.PI * 2900f * 1.007f * t)) * 0.5f * 0.10f;

            _blinkTickSeed = _blinkTickSeed * 1664525u + 1013904223u;
            float noise = ((_blinkTickSeed >> 9) / (float)(1u << 23)) * 2f - 1f;
            _blinkTickLp += (noise - _blinkTickLp) * 0.18f;
            float click = _blinkTickPos < 100 ? _blinkTickLp * (1f - _blinkTickPos / 100f) * 0.30f : 0f;

            float s = (v * body * attack + click) * vol;
            for (int c = 0; c < channels; c++) data[i + c] += s;
            _blinkTickPos++;
        }
    }
}
