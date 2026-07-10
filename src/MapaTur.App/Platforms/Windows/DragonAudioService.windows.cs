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
    private OneShotBank? roarBank;       // the big cry (real sample when staged)
    private OneShotBank? growlLongBank;  // soft soar variants — quieter menace between roars
    private OneShotBank? growlShortBank;
    private int roarVariantCounter;

    // Flight bed: three looping layers volume-ridden by the flight state (wind ∝ speed+bank, wing flutter ∝
    // flap activity, ground rush ∝ proximity×speed). Kept PLAYING while any level is audible and paused when
    // everything zeroes — starting/stopping per tick would stutter; volume is the control surface.
    private MediaPlayer? windLoop;
    private MediaPlayer? wingLoop;
    private MediaPlayer? groundLoop;
    private float bedWindCurrent;
    private float bedWingCurrent;
    private float bedGroundCurrent;
    private bool bedPlaying;
    private const double BedWindGain = 0.5;
    private const double BedWingGain = 0.45;
    private const double BedGroundGain = 0.55;

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
        if (!EnsureInitialized())
        {
            return;
        }

        // Loud calls (flight entry, kill) get THE roar; quiet soaring calls alternate the softer growls so
        // the ambient cadence doesn't wear a single sample out.
        if (volume >= 0.55f)
        {
            roarBank!.Play(Math.Clamp(volume, 0f, 1f));
        }
        else
        {
            OneShotBank bank = (++roarVariantCounter & 1) == 0 ? growlLongBank! : growlShortBank!;
            bank.Play(Math.Clamp(volume + 0.1f, 0f, 1f));
        }
    }

    partial void SetFlightBedImpl(float windLevel, float wingLevel, float groundLevel)
    {
        if (!EnsureInitialized())
        {
            return;
        }

        // Smooth toward the targets (callers pass raw per-tick values; 15%/call at 30-60 Hz ≈ a soft fader)
        // and only touch the WinRT Volume property on audible change — property sets marshal to the engine.
        bedWindCurrent += (Math.Clamp(windLevel, 0f, 1f) - bedWindCurrent) * 0.15f;
        bedWingCurrent += (Math.Clamp(wingLevel, 0f, 1f) - bedWingCurrent) * 0.15f;
        bedGroundCurrent += (Math.Clamp(groundLevel, 0f, 1f) - bedGroundCurrent) * 0.15f;

        bool audible = bedWindCurrent > 0.01f || bedWingCurrent > 0.01f || bedGroundCurrent > 0.01f
            || windLevel > 0f || wingLevel > 0f || groundLevel > 0f;
        try
        {
            if (audible && !bedPlaying)
            {
                windLoop!.Play();
                wingLoop!.Play();
                groundLoop!.Play();
                bedPlaying = true;
            }
            else if (!audible && bedPlaying)
            {
                windLoop!.Pause();
                wingLoop!.Pause();
                groundLoop!.Pause();
                bedPlaying = false;
                bedWindCurrent = bedWingCurrent = bedGroundCurrent = 0f;
            }

            if (bedPlaying)
            {
                UpdateLoopVolume(windLoop!, bedWindCurrent * BedWindGain);
                UpdateLoopVolume(wingLoop!, bedWingCurrent * BedWingGain);
                UpdateLoopVolume(groundLoop!, bedGroundCurrent * BedGroundGain);
            }
        }
        catch (Exception ex)
        {
            LogPlayFailureOnce(ex);
        }
    }

    private static void UpdateLoopVolume(MediaPlayer player, double volume)
    {
        if (Math.Abs(player.Volume - volume) > 0.005)
        {
            player.Volume = Math.Clamp(volume, 0.0, 1.0);
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
            // Real recorded samples (Pixabay "Dragon Studio" pack, MauiAssets under Resources/Raw/dragon-audio)
            // take priority; the procedural synthesis stays as the fallback so a missing asset never
            // silences the dragon. Booms/hiss/whoosh + wind/rush beds remain synthesized by design.
            string? fireSample = FindAsset("fire-breath.mp3");
            string? roarSample = FindAsset("roar-epic.mp3");
            string? wingBedSample = FindAsset("wings-flapping.mp3");
            string firePath = fireSample ?? EnsureWav(dir, "fire-loop-v1.wav", SynthFireLoop);
            string roarPath = roarSample ?? EnsureWav(dir, "roar-v2.wav", SynthRoar); // v2: formant beast voice (v1 buzzed)
            string growlLongPath = FindAsset("growl-long.mp3") ?? roarPath;
            string growlShortPath = FindAsset("growl-short.mp3") ?? roarPath;
            string wingBedPath = wingBedSample ?? EnsureWav(dir, "wing-loop-v2.wav", SynthWingLoop);
            string flapPath = EnsureWav(dir, "flap-v1.wav", SynthFlap);
            string boomPath = EnsureWav(dir, "boom-v1.wav", SynthBoom);
            string hissPath = EnsureWav(dir, "hiss-v1.wav", SynthHiss);
            string windPath = EnsureWav(dir, "wind-loop-v2.wav", SynthWindLoop); // v2: airy two-band (v1 chugged like a train)
            string groundPath = EnsureWav(dir, "ground-rush-v1.wav", SynthGroundRush);

            firePlayer = MakePlayer(firePath, looping: true);
            firePlayer.Volume = fireSample is null ? 0.4 : 0.55;
            windLoop = MakePlayer(windPath, looping: true);
            windLoop.Volume = 0.0;
            wingLoop = MakePlayer(wingBedPath, looping: true);
            wingLoop.Volume = 0.0;
            groundLoop = MakePlayer(groundPath, looping: true);
            groundLoop.Volume = 0.0;
            // Voice counts: the breath stream explodes ~22×/s, so booms need real overlap; the rest are sparse.
            boomBank = new OneShotBank(boomPath, voices: 3, minIntervalMs: 90, MakePlayer, LogPlayFailureOnce);
            hissBank = new OneShotBank(hissPath, voices: 2, minIntervalMs: 150, MakePlayer, LogPlayFailureOnce);
            flapBank = new OneShotBank(flapPath, voices: 2, minIntervalMs: 160, MakePlayer, LogPlayFailureOnce);
            roarBank = new OneShotBank(roarPath, voices: 2, minIntervalMs: 1500, MakePlayer, LogPlayFailureOnce);
            growlLongBank = new OneShotBank(growlLongPath, voices: 1, minIntervalMs: 2500, MakePlayer, LogPlayFailureOnce);
            growlShortBank = new OneShotBank(growlShortPath, voices: 1, minIntervalMs: 1200, MakePlayer, LogPlayFailureOnce);

            initialized = true;
            Serilog.Log.Information(
                "[DragonAudio] initialised — fire={Fire} roar={Roar} wings={Wings} (+synth whoosh/boom/hiss/wind/rush) in {Dir}",
                fireSample is null ? "synth" : "sample",
                roarSample is null ? "synth" : "sample",
                wingBedSample is null ? "synth" : "sample", dir);
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

    // Locates a staged MauiAsset by probing the layouts Windows deploys them under (unpackaged exe dir,
    // packaged Assets, raw Resources mirror). Null = not shipped → the caller falls back to synthesis.
    private static string? FindAsset(string name)
    {
        foreach (string candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "dragon-audio", name),
            Path.Combine(AppContext.BaseDirectory, "Assets", "dragon-audio", name),
            Path.Combine(AppContext.BaseDirectory, "Resources", "Raw", "dragon-audio", name),
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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
    /// Dragon roar v2 — a beast VOICE, not a harmonic buzz (v1 read as flatulence). Recipe: a rough glottal
    /// pulse-train excitation with per-cycle JITTER (period walk) and SHIMMER (amplitude walk) — the
    /// biological roughness a clean oscillator lacks — plus throat breath noise, shaped by three moving
    /// FORMANT band-passes (a closing-mouth /ɑ/→/ɔ/ tract), a chaotic falling pitch contour and only a faint
    /// sub layer for chest weight. Mild tanh finish.
    /// </summary>
    private static float[] SynthRoar()
    {
        const float seconds = 2.2f;
        int n = (int)(SampleRate * seconds);
        float[] s = new float[n];
        var rng = new Random(33);

        var f1 = default(Biquad);
        var f2 = default(Biquad);
        var f3 = default(Biquad);

        float excitation = 0f;
        float excitationDecay = MathF.Exp(-1f / (0.0022f * SampleRate)); // ~2.2 ms pulse ring-down
        int samplesToPulse = 0;
        float pitchWalk = 0f;
        float subPhase = 0f;

        for (int i = 0; i < n; i++)
        {
            float t = (float)i / SampleRate;

            // Chaotic pitch contour: barked attack, sagging body, falling tail + a random walk (no clean glide).
            float f0Base = t < 0.12f
                ? 130f + (45f * (t / 0.12f))
                : t < 1.3f
                    ? 175f - (60f * ((t - 0.12f) / 1.18f))
                    : 115f - (45f * MathF.Min((t - 1.3f) / 0.9f, 1f));
            pitchWalk = (pitchWalk * 0.999f) + ((((float)rng.NextDouble() * 2f) - 1f) * 0.35f);
            float f0 = MathF.Max(50f, f0Base + pitchWalk);

            // Rough glottal source: a decaying pulse per period, period ±10% jitter, amplitude ±25% shimmer.
            if (samplesToPulse <= 0)
            {
                float jitter = 0.9f + (0.2f * (float)rng.NextDouble());
                samplesToPulse = Math.Max(8, (int)(SampleRate / f0 * jitter));
                excitation += 0.75f + (0.5f * (float)rng.NextDouble());
            }

            samplesToPulse--;
            excitation *= excitationDecay;
            float white = ((float)rng.NextDouble() * 2f) - 1f;
            float source = (excitation * (1f + (0.3f * white))) + (white * 0.22f); // pulses + throat breath

            // Vocal-tract formants, drifting down as the mouth closes over the roar.
            float close = MathF.Min(t / 1.6f, 1f);
            float y1 = f1.Bandpass(source, 480f - (110f * close), 6f);
            float y2 = f2.Bandpass(source, 1050f - (270f * close), 7f);
            float y3 = f3.Bandpass(source, 2300f - (350f * close), 8f);
            float voice = (y1 * 1.0f) + (y2 * 0.6f) + (y3 * 0.28f);

            // Faint sub for chest weight — support, never the star (the star was the fart).
            subPhase += 2f * MathF.PI * (f0 * 0.5f) / SampleRate;
            float sub = MathF.Sin(subPhase) * 0.22f;

            float env = MathF.Min(t / 0.06f, 1f) * (t < 1.35f ? 1f - (0.15f * (t / 1.35f)) : 0.85f * MathF.Exp(-(t - 1.35f) / 0.4f));
            s[i] = MathF.Tanh(1.3f * (voice + sub)) * env;
        }

        HighPass(s, 55f);
        Normalize(s, 0.9f);
        return s;
    }

    /// <summary>Minimal RBJ band-pass biquad with per-call coefficient refresh (offline synthesis only).</summary>
    private struct Biquad
    {
        private float x1, x2, y1, y2;

        public float Bandpass(float x, float fc, float q)
        {
            float w0 = 2f * MathF.PI * fc / SampleRate;
            float alpha = MathF.Sin(w0) / (2f * q);
            float a0 = 1f + alpha;
            float b0 = alpha / a0;
            float b2 = -alpha / a0;
            float a1 = (-2f * MathF.Cos(w0)) / a0;
            float a2 = (1f - alpha) / a0;
            float y = (b0 * x) + (b2 * x2) - (a1 * y1) - (a2 * y2);
            x2 = x1;
            x1 = x;
            y2 = y1;
            y1 = y;
            return y;
        }
    }

    /// <summary>
    /// Speed-wind loop v2 (3.2 s): TWO bands — a 120–900 Hz airflow body plus a quiet 1.8–5.2 kHz airy hiss
    /// (real wind has that top; a single low band read as a TRAIN) — with barely-there, non-rhythmic
    /// flutter (rhythmic broadband pumping = wheels on rails). Integer-cycle LFOs keep the loop seamless.
    /// </summary>
    private static float[] SynthWindLoop()
    {
        const float seconds = 3.2f;
        int n = (int)(SampleRate * seconds);
        int fade = (int)(0.2f * SampleRate);
        int total = n + fade;
        float[] s = new float[total];
        var rng = new Random(41);
        float lpB1 = 0f, lpB2 = 0f, lpH1 = 0f, lpH2 = 0f;
        float aB1 = 1f - MathF.Exp(-2f * MathF.PI * 900f / SampleRate);
        float aB2 = 1f - MathF.Exp(-2f * MathF.PI * 120f / SampleRate);
        float aH1 = 1f - MathF.Exp(-2f * MathF.PI * 5200f / SampleRate);
        float aH2 = 1f - MathF.Exp(-2f * MathF.PI * 1800f / SampleRate);
        for (int i = 0; i < total; i++)
        {
            float t01 = (float)i / n;
            float white = ((float)rng.NextDouble() * 2f) - 1f;
            lpB1 += aB1 * (white - lpB1);
            lpB2 += aB2 * (white - lpB2);
            lpH1 += aH1 * (white - lpH1);
            lpH2 += aH2 * (white - lpH2);
            float body = lpB1 - lpB2;   // 120–900 Hz airflow
            float airy = lpH1 - lpH2;   // 1.8–5.2 kHz hiss
            float sway = 1f + (0.05f * MathF.Sin(2f * MathF.PI * 2f * t01))
                + (0.03f * MathF.Sin((2f * MathF.PI * 5f * t01) + 1.9f)); // ±8 % max, incommensurate-feeling
            s[i] = ((body * 0.9f) + (airy * 0.38f)) * sway;
        }

        for (int k = 0; k < fade; k++)
        {
            float w = (float)k / fade;
            s[k] = (s[n + k] * (1f - w)) + (s[k] * w);
        }

        Array.Resize(ref s, n);
        Normalize(s, 0.85f);
        return s;
    }

    /// <summary>Wing-membrane flutter loop v2 (2.8 s): the 5 Hz beat pump toned way down (0.22 depth) — the
    /// rhythm belongs to the discrete whooshes; a loud loop pump read as a train's chug.</summary>
    private static float[] SynthWingLoop()
    {
        return SynthNoiseLoop(
            seconds: 2.8f, seed: 43, lowCutHz: 250f, highCutHz: 1500f,
            (t01) => (0.78f + (0.22f * MathF.Sin(2f * MathF.PI * 14f * t01)))       // 14 cycles / 2.8 s = 5 Hz, gentle
                * (0.88f + (0.12f * MathF.Sin((2f * MathF.PI * 7f * t01) + 1.3f)))); // slow turbulence ride
    }

    /// <summary>Ground-proximity rush loop (2.6 s): brighter hiss of terrain racing past — seamless.</summary>
    private static float[] SynthGroundRush()
    {
        return SynthNoiseLoop(
            seconds: 2.6f, seed: 47, lowCutHz: 500f, highCutHz: 3000f,
            (t01) => 0.8f + (0.12f * MathF.Sin(2f * MathF.PI * 6f * t01))
                + (0.08f * MathF.Sin((2f * MathF.PI * 11f * t01) + 0.7f)));
    }

    // Shared band-passed noise loop generator: white noise → band (lowCut..highCut) × an amplitude envelope
    // whose LFOs must run INTEGER cycles per loop (the callers' contract), then the tail-extension blend
    // makes the noise itself seamless too.
    private static float[] SynthNoiseLoop(float seconds, int seed, float lowCutHz, float highCutHz, Func<float, float> amp)
    {
        int n = (int)(SampleRate * seconds);
        int fade = (int)(0.2f * SampleRate);
        int total = n + fade;
        float[] s = new float[total];
        var rng = new Random(seed);
        float lpHigh = 0f;
        float lpLow = 0f;
        float aHigh = 1f - MathF.Exp(-2f * MathF.PI * highCutHz / SampleRate);
        float aLow = 1f - MathF.Exp(-2f * MathF.PI * lowCutHz / SampleRate);
        for (int i = 0; i < total; i++)
        {
            float white = ((float)rng.NextDouble() * 2f) - 1f;
            lpHigh += aHigh * (white - lpHigh);
            lpLow += aLow * (white - lpLow);
            float band = lpHigh - lpLow; // pass band between the two cutoffs
            s[i] = band * amp((float)i / n);
        }

        for (int k = 0; k < fade; k++)
        {
            float w = (float)k / fade;
            s[k] = (s[n + k] * (1f - w)) + (s[k] * w);
        }

        Array.Resize(ref s, n);
        Normalize(s, 0.85f);
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