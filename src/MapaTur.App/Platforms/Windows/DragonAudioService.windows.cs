using System.Diagnostics;

using Windows.Media.Core;
using Windows.Media.Playback;

namespace MapaTur.App.Services;

/// <summary>
/// Windows implementation: synthesizes the effects as 16-bit mono WAVs into the app cache on first use
/// (deterministic seeds; the "-v1" filenames version the recipes so a tweak regenerates instead of
/// replaying a stale cache) and plays them through WinRT MediaPlayers.
///
/// Hardened after the 2026-07-09 23:06 APPCRASH (0xc000027b stowed exception in CoreMessagingXP during the
/// first fire-breath): every player is wired ONCE at init — its own MediaSource that is never reassigned or
/// disposed afterwards (source churn at 22 shots/s is exactly the async surface that blew up), SMTC
/// integration off (CommandManager is a known stowed-exception source for game SFX), MediaFailed observed →
/// one log line. One-shots play from small per-sound voice banks with a rate limit; a shot just rewinds the
/// next voice. Any failure latches audio off — sound must never take the dragon down.
/// </summary>
public sealed partial class DragonAudioService
{
    private const int SampleRate = 44100;

    private static readonly Stopwatch AudioClock = Stopwatch.StartNew();

    private bool initialized;
    private bool initFailed;
    private bool playFailureLogged;
    private bool fireActive;
    private MediaPlayer? firePlayer;
    private OneShotBank? boomBank;
    private OneShotBank? hissBank;
    private OneShotBank? flapBank;
    private OneShotBank? roarBank;

    partial void SetFireActiveImpl(bool active)
    {
        if (active == fireActive || !EnsureInitialized())
        {
            return;
        }

        fireActive = active;
        try
        {
            if (active)
            {
                firePlayer!.PlaybackSession.Position = TimeSpan.Zero; // consistent attack on every breath
                firePlayer.Play();
            }
            else
            {
                firePlayer!.Pause();
            }
        }
        catch (Exception ex)
        {
            LogPlayFailureOnce(ex);
        }
    }

    partial void PlayExplosionImpl(float power, float distanceMeters)
    {
        if (EnsureInitialized())
        {
            boomBank!.Play(DistanceAttenuation(distanceMeters) * (0.55 + (0.35 * Math.Clamp(power / 2.2f, 0f, 1f))));
        }
    }

    partial void PlaySteamImpl(float distanceMeters)
    {
        if (EnsureInitialized())
        {
            hissBank!.Play(0.55 * DistanceAttenuation(distanceMeters));
        }
    }

    partial void PlayFlapImpl(float vigor)
    {
        if (EnsureInitialized())
        {
            flapBank!.Play(0.25 + (0.25 * Math.Clamp(vigor, 0f, 1.5f)));
        }
    }

    partial void PlayRoarImpl(float volume)
    {
        if (EnsureInitialized())
        {
            roarBank!.Play(Math.Clamp(volume, 0f, 1f));
        }
    }

    // Chase-cam sounds sit ~15 m away (full volume); a fireball bursting on a far slope tails off smoothly.
    private static double DistanceAttenuation(float meters)
        => Math.Clamp(60f / (10f + Math.Max(0f, meters)), 0.0, 1.0);

    private bool EnsureInitialized()
    {
        if (initialized)
        {
            return true;
        }

        if (initFailed)
        {
            return false;
        }

        try
        {
            string dir = Path.Combine(Microsoft.Maui.Storage.FileSystem.CacheDirectory, "dragon-audio");
            Directory.CreateDirectory(dir);
            string fireWav = EnsureWav(dir, "fire-loop-v1.wav", SynthFireLoop);
            string flapPath = EnsureWav(dir, "flap-v1.wav", SynthFlap);
            string boomPath = EnsureWav(dir, "boom-v1.wav", SynthBoom);
            string hissPath = EnsureWav(dir, "hiss-v1.wav", SynthHiss);
            string roarPath = EnsureWav(dir, "roar-v1.wav", SynthRoar);

            firePlayer = MakePlayer(fireWav, looping: true);
            firePlayer.Volume = 0.4;
            // Voice counts: the breath stream explodes ~22×/s, so booms need real overlap; the rest are sparse.
            boomBank = new OneShotBank(boomPath, voices: 3, minIntervalMs: 90, MakePlayer, LogPlayFailureOnce);
            hissBank = new OneShotBank(hissPath, voices: 2, minIntervalMs: 150, MakePlayer, LogPlayFailureOnce);
            flapBank = new OneShotBank(flapPath, voices: 2, minIntervalMs: 160, MakePlayer, LogPlayFailureOnce);
            roarBank = new OneShotBank(roarPath, voices: 2, minIntervalMs: 1500, MakePlayer, LogPlayFailureOnce);

            initialized = true;
            Serilog.Log.Information("[DragonAudio] initialised — 5 procedural effects in {Dir}", dir);
            return true;
        }
        catch (Exception ex)
        {
            initFailed = true;
            Serilog.Log.Warning(ex, "[DragonAudio] init failed — dragon sounds disabled this session");
            return false;
        }
    }

    // One fully-wired player per voice: source set ONCE and never touched again, SMTC/CommandManager off
    // (game SFX must not grab the media keys — and it is a known stowed-exception source), failures observed.
    private MediaPlayer MakePlayer(string wavPath, bool looping)
    {
        var player = new MediaPlayer
        {
            IsLoopingEnabled = looping,
            AutoPlay = false,
            AudioCategory = MediaPlayerAudioCategory.GameEffects,
        };
        player.CommandManager.IsEnabled = false;
        player.MediaFailed += OnMediaFailed;
        player.Source = MediaSource.CreateFromUri(new Uri(wavPath));
        return player;
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        if (playFailureLogged)
        {
            return;
        }

        playFailureLogged = true;
        Serilog.Log.Warning("[DragonAudio] media failed: {Error} {Msg} (further failures muted)", args.Error, args.ErrorMessage);
    }

    private void LogPlayFailureOnce(Exception ex)
    {
        if (playFailureLogged)
        {
            return;
        }

        playFailureLogged = true;
        Serilog.Log.Warning(ex, "[DragonAudio] playback failed (further failures muted)");
    }

    /// <summary>
    /// A small bank of pre-wired voices for one sound: Play() rate-limits, then rewinds and starts the next
    /// voice round-robin. No source churn ever happens after construction.
    /// </summary>
    private sealed class OneShotBank
    {
        private readonly MediaPlayer[] voices;
        private readonly double minIntervalMs;
        private readonly Action<Exception> onFailure;
        private int next;
        private double lastPlayMs = double.MinValue;

        public OneShotBank(string wavPath, int voices, double minIntervalMs, Func<string, bool, MediaPlayer> makePlayer, Action<Exception> onFailure)
        {
            this.voices = new MediaPlayer[voices];
            for (int i = 0; i < voices; i++)
            {
                this.voices[i] = makePlayer(wavPath, false);
            }

            this.minIntervalMs = minIntervalMs;
            this.onFailure = onFailure;
        }

        public void Play(double volume)
        {
            double now = AudioClock.Elapsed.TotalMilliseconds;
            if (now - lastPlayMs < minIntervalMs)
            {
                return; // a salvo of stream impacts collapses into the ringing voices already playing
            }

            lastPlayMs = now;
            try
            {
                MediaPlayer voice = voices[next];
                next = (next + 1) % voices.Length;
                voice.Volume = Math.Clamp(volume, 0.0, 1.0);
                voice.PlaybackSession.Position = TimeSpan.Zero;
                voice.Play();
            }
            catch (Exception ex)
            {
                onFailure(ex);
            }
        }
    }

    private static string EnsureWav(string dir, string name, Func<float[]> synth)
    {
        string path = Path.Combine(dir, name);
        if (!File.Exists(path))
        {
            WriteWav16(path, synth());
        }

        return path;
    }

    // ── synthesis ── all mono 44.1 kHz float with deterministic seeds, normalised at the end.

    /// <summary>
    /// Breath roar: a fluttering brown-noise body with scattered crackle pops, band-shaped. Generated with a
    /// 150 ms tail EXTENSION that is faded into the head, so the looping player crosses the seam silently
    /// (the flutter LFOs run integer cycles per loop and are seamless by construction).
    /// </summary>
    private static float[] SynthFireLoop()
    {
        const float seconds = 2.4f;
        int n = (int)(SampleRate * seconds);
        int fade = (int)(0.15f * SampleRate);
        int total = n + fade;
        float[] s = new float[total];
        var rng = new Random(1234);
        float brown = 0f;
        for (int i = 0; i < total; i++)
        {
            float white = ((float)rng.NextDouble() * 2f) - 1f;
            brown = (brown + (0.09f * white)) * 0.988f;
            float t01 = (float)i / n; // by the LOOP length — the extension continues the phase past 1.0
            float flutter = 0.72f + (0.18f * MathF.Sin(2f * MathF.PI * 7f * t01))
                + (0.10f * MathF.Sin((2f * MathF.PI * 13f * t01) + 1.7f));
            s[i] = brown * 3.4f * flutter;
        }

        // Crackle: ~34 short decaying white-noise pops; one may spill into the extension → faded into the head.
        var pops = new Random(4321);
        for (int p = 0; p < 34; p++)
        {
            int start = pops.Next(n);
            int len = (int)(SampleRate * (0.002f + (0.004f * (float)pops.NextDouble())));
            float amp = 0.35f + (0.45f * (float)pops.NextDouble());
            for (int k = 0; k < len && start + k < total; k++)
            {
                float w = ((float)pops.NextDouble() * 2f) - 1f;
                s[start + k] += amp * w * MathF.Exp(-k / (0.0035f * SampleRate));
            }
        }

        LowPass(s, 2000f);
        HighPass(s, 45f);

        // Head ← tail-extension blend: sample n follows n−1 seamlessly, so mixing the head toward the
        // extension makes wrap(n−1 → 0) sound exactly like the straight run (n−1 → n).
        for (int k = 0; k < fade; k++)
        {
            float w = (float)k / fade;
            s[k] = (s[n + k] * (1f - w)) + (s[k] * w);
        }

        Array.Resize(ref s, n);
        Normalize(s, 0.9f);
        return s;
    }

    /// <summary>One wing-beat: a noise whoosh whose lowpass sweeps with the stroke + a soft 82 Hz air thump.</summary>
    private static float[] SynthFlap()
    {
        const float seconds = 0.6f;
        int n = (int)(SampleRate * seconds);
        float[] s = new float[n];
        var rng = new Random(77);
        float lp = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float swing = MathF.Sin(MathF.PI * MathF.Min(t / 0.45f, 1f));
            swing *= swing;
            float fc = 350f + (2600f * swing);
            float a = 1f - MathF.Exp(-2f * MathF.PI * fc / SampleRate);
            float white = ((float)rng.NextDouble() * 2f) - 1f;
            lp += a * (white - lp);
            s[i] = lp * swing * 1.6f;
            if (t >= 0.16f)
            {
                float tt = t - 0.16f;
                s[i] += 0.5f * MathF.Sin(2f * MathF.PI * 82f * tt) * MathF.Exp(-tt / 0.06f);
            }
        }

        Normalize(s, 0.85f);
        return s;
    }

    /// <summary>Explosion: an initial crack, a brown-noise body and a pitch-dropping sub thump, soft-clipped.</summary>
    private static float[] SynthBoom()
    {
        const float seconds = 1.4f;
        int n = (int)(SampleRate * seconds);
        float[] s = new float[n];
        var rng = new Random(5);
        float brown = 0f;
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float white = ((float)rng.NextDouble() * 2f) - 1f;
            brown = (brown + (0.09f * white)) * 0.988f;
            float crack = white * MathF.Exp(-t / 0.018f) * 1.2f;
            float body = brown * 5f * MathF.Exp(-t / 0.45f);
            float f = 92f - (58f * MathF.Min(t / 0.6f, 1f));
            phase += 2f * MathF.PI * f / SampleRate;
            float sub = MathF.Sin(phase) * 1.4f * MathF.Exp(-t / 0.38f);
            s[i] = MathF.Tanh(1.5f * (crack + body + sub));
        }

        Normalize(s, 0.95f);
        return s;
    }

    /// <summary>Steam hiss: high-passed white noise with a cooling (downward) treble sweep.</summary>
    private static float[] SynthHiss()
    {
        const float seconds = 1.8f;
        int n = (int)(SampleRate * seconds);
        float[] s = new float[n];
        var rng = new Random(9);
        float lpLow = 0f;
        float lpTop = 0f;
        float aLow = 1f - MathF.Exp(-2f * MathF.PI * 900f / SampleRate);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float white = ((float)rng.NextDouble() * 2f) - 1f;
            lpLow += aLow * (white - lpLow);
            float hp = white - lpLow; // high-passed sizzle
            float fcTop = 5500f - (3700f * MathF.Min(t / 1.2f, 1f));
            float aTop = 1f - MathF.Exp(-2f * MathF.PI * fcTop / SampleRate);
            lpTop += aTop * (hp - lpTop);
            float env = MathF.Min(t / 0.03f, 1f) * MathF.Exp(-t / 0.65f);
            s[i] = lpTop * env * 1.5f;
        }

        Normalize(s, 0.8f);
        return s;
    }

    /// <summary>
    /// Dragon roar: a deep integrated-phase fundamental (65 → 120 Hz bark, sagging body, falling tail) with
    /// 9 harmonics, a ~26 Hz wobbling growl AM, a band-passed breath/rasp bed, and tanh saturation — the
    /// saturation folds energy into the mids so the roar carries even on small speakers.
    /// </summary>
    private static float[] SynthRoar()
    {
        const float seconds = 1.9f;
        int n = (int)(SampleRate * seconds);
        float[] s = new float[n];
        var rng = new Random(31);
        float phase = 0f;
        float brown = 0f;
        float lpBreath = 0f;
        float lpBed = 0f;
        float aBed = 1f - MathF.Exp(-2f * MathF.PI * 300f / SampleRate);
        float aBreath = 1f - MathF.Exp(-2f * MathF.PI * 1600f / SampleRate);
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;
            float f0 = t < 0.25f
                ? 65f + (55f * (t / 0.25f))                          // bark up to 120 Hz
                : t < 1.1f
                    ? 120f - (10f * ((t - 0.25f) / 0.85f))           // sustained body, slight sag
                    : 110f - (45f * MathF.Min((t - 1.1f) / 0.8f, 1f)); // falling tail
            phase += 2f * MathF.PI * f0 / SampleRate;
            float growl = 1f + (0.38f * MathF.Sin((2f * MathF.PI * 26f * t) + (1.3f * MathF.Sin(2f * MathF.PI * 7.3f * t))));
            float tone = 0f;
            for (int k = 1; k <= 9; k++)
            {
                tone += MathF.Sin(phase * k) / MathF.Pow(k, 0.85f);
            }

            float white = ((float)rng.NextDouble() * 2f) - 1f;
            brown = (brown + (0.09f * white)) * 0.988f;
            lpBed += aBed * ((brown * 4f) - lpBed);      // low rumble bed
            lpBreath += aBreath * (white - lpBreath);
            float rasp = lpBreath - lpBed;               // rough band-passed breath
            float env = MathF.Min(t / 0.12f, 1f) * (t < 1.2f ? 1f : MathF.Exp(-(t - 1.2f) / 0.35f));
            s[i] = MathF.Tanh(1.6f * ((tone * 0.5f * growl) + (rasp * 0.35f))) * env;
        }

        HighPass(s, 40f);
        Normalize(s, 0.95f);
        return s;
    }

    private static void LowPass(float[] s, float cutoffHz)
    {
        float a = 1f - MathF.Exp(-2f * MathF.PI * cutoffHz / SampleRate);
        float y = 0f;
        for (int i = 0; i < s.Length; i++)
        {
            y += a * (s[i] - y);
            s[i] = y;
        }
    }

    private static void HighPass(float[] s, float cutoffHz)
    {
        float a = 1f - MathF.Exp(-2f * MathF.PI * cutoffHz / SampleRate);
        float lp = 0f;
        for (int i = 0; i < s.Length; i++)
        {
            lp += a * (s[i] - lp);
            s[i] -= lp;
        }
    }

    private static void Normalize(float[] s, float peak)
    {
        float max = 1e-6f;
        foreach (float v in s)
        {
            max = MathF.Max(max, MathF.Abs(v));
        }

        float k = peak / max;
        for (int i = 0; i < s.Length; i++)
        {
            s[i] *= k;
        }
    }

    private static void WriteWav16(string path, float[] samples)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2); // byte rate
        w.Write((short)2); // block align
        w.Write((short)16); // bits
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (float v in samples)
        {
            w.Write((short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue));
        }
    }
}