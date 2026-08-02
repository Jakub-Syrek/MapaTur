using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

using MapaTur.Application.Diagnostics;

using Serilog.Core;
using Serilog.Events;

namespace MapaTur.App.Services;

/// <summary>
/// Machine-readable app diagnostics for the test harness (na żądanie usera 08-02: stan apki sprawdzalny
/// programistycznie, bez UI/screenshotów). Two clocks cooperate:
/// <list type="bullet">
/// <item>a UI-thread heartbeat (dispatcher timer, 250 ms) stamps <see cref="lastUiBeatMs"/> and executes
/// <c>MAPATUR_UI_SCRIPT</c> steps through the same command path as the top-bar chips;</item>
/// <item>a background timer (2 s) — immune to a hung UI thread — writes <c>%TEMP%\mapatur-status.json</c>
/// with liveness, UI lag, memory and recent warnings. A stale <c>uiBeatAgeMs</c> with a fresh
/// <c>statusUtc</c> = the UI thread is stalled; a stale file = the process is gone.</item>
/// </list>
/// </summary>
public static class HarnessDiag
{
    private const int HeartbeatIntervalMs = 250;
    private const int UiLagLogThresholdMs = 150; // spójnie z frame-gap watchdogiem renderera
    private const int StatusIntervalMs = 2000;
    private const int RecentWarningLimit = 5;

    private static readonly long ProcessStartMs = Environment.TickCount64;
    private static readonly ConcurrentQueue<string> RecentWarnings = new();
    private static readonly string StatusPath = Path.Combine(Path.GetTempPath(), "mapatur-status.json");

    private static long lastUiBeatMs = -1;
    private static long worstUiLagMs;          // max lag w bieżącym oknie 60 s
    private static long worstUiLagPrevMs;      // max lag poprzedniego okna (żeby zwis sprzed chwili nie znikał od razu)
    private static long worstUiLagWindowStartMs;
    private static int warningCount;
    private static int errorCount;
    private static int attached;
    private static Timer? statusTimer;
    private static IReadOnlyList<UiScriptStep> scriptSteps = [];
    private static int scriptIndex;
    private static Action<int>? selectSection;
    private static Func<int>? readActiveSection;

    /// <summary>Render-loop FPS (smoothedFps z Terrain3DView) — publikowane per klatkę, czytane przy zapisie statusu.</summary>
    public static double RenderFps { get; set; }

    /// <summary>
    /// Hooks the harness to the live page. Safe to call again after a soft restart (language switch
    /// rebuilds the page) — delegates are swapped, timers are started once per process.
    /// </summary>
    public static void AttachUi(IDispatcher dispatcher, Action<int> selectSectionAction, Func<int> activeSectionGetter)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        selectSection = selectSectionAction;
        readActiveSection = activeSectionGetter;

        if (Interlocked.Exchange(ref attached, 1) == 1)
        {
            return;
        }

        scriptSteps = UiScriptParser.Parse(Environment.GetEnvironmentVariable("MAPATUR_UI_SCRIPT"));
        if (scriptSteps.Count > 0)
        {
            Serilog.Log.Information("[UiScript] {Count} kroków: {Script}", scriptSteps.Count,
                Environment.GetEnvironmentVariable("MAPATUR_UI_SCRIPT"));
        }

        IDispatcherTimer beat = dispatcher.CreateTimer();
        beat.Interval = TimeSpan.FromMilliseconds(HeartbeatIntervalMs);
        beat.IsRepeating = true;
        beat.Tick += (_, _) => OnUiBeat();
        beat.Start();

        statusTimer = new Timer(_ => WriteStatus(), null, StatusIntervalMs, StatusIntervalMs);
    }

    private static void OnUiBeat()
    {
        long now = Environment.TickCount64;
        if (lastUiBeatMs >= 0)
        {
            long lag = now - lastUiBeatMs - HeartbeatIntervalMs;
            if (now - worstUiLagWindowStartMs >= 60_000)
            {
                worstUiLagPrevMs = worstUiLagMs;
                worstUiLagMs = 0;
                worstUiLagWindowStartMs = now;
            }
            if (lag > worstUiLagMs)
            {
                worstUiLagMs = lag;
            }
            if (lag > UiLagLogThresholdMs)
            {
                Serilog.Log.Information("[UiBeat] wątek UI zamulony: lag {Lag}ms (sekcja {Section})",
                    lag, readActiveSection?.Invoke() ?? -1);
            }
        }
        lastUiBeatMs = now;

        double uptimeSec = (now - ProcessStartMs) / 1000.0;
        while (scriptIndex < scriptSteps.Count && uptimeSec >= scriptSteps[scriptIndex].AtSeconds)
        {
            UiScriptStep step = scriptSteps[scriptIndex++];
            Serilog.Log.Information("[UiScript] sekcja {Section} @ t={T:F1}s (plan {At}s)", step.Section, uptimeSec, step.AtSeconds);
            selectSection?.Invoke(step.Section);
        }
    }

    private static void WriteStatus()
    {
        try
        {
            long now = Environment.TickCount64;
            var status = new
            {
                pid = Environment.ProcessId,
                statusUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                uptimeSec = Math.Round((now - ProcessStartMs) / 1000.0, 1),
                uiBeatAgeMs = lastUiBeatMs < 0 ? -1 : now - lastUiBeatMs,
                uiWorstLagMs60s = Math.Max(worstUiLagMs, worstUiLagPrevMs),
                renderFps = Math.Round(RenderFps, 1),
                activeSection = readActiveSection?.Invoke() ?? -1,
                heapMB = GC.GetTotalMemory(false) / (1024 * 1024),
                wsMB = Environment.WorkingSet / (1024 * 1024),
                gcGen2 = GC.CollectionCount(2),
                glTex = GlTrack.TexAlive,
                glBuf = GlTrack.BufAlive,
                glVao = GlTrack.VaoAlive,
                glFbo = GlTrack.FboAlive,
                glRbo = GlTrack.RboAlive,
                warningCount,
                errorCount,
                recentWarnings = RecentWarnings.ToArray(),
            };

            string tmp = StatusPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(status));
            File.Move(tmp, StatusPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // diagnostyka nigdy nie może ubić apki; kolejny tick spróbuje ponownie
        }
    }

    /// <summary>Feeds the warning/error ring visible in the status file. Called by the Serilog sink.</summary>
    public static void RecordLogEvent(LogEventLevel level, string message)
    {
        if (level >= LogEventLevel.Error)
        {
            Interlocked.Increment(ref errorCount);
        }
        else
        {
            Interlocked.Increment(ref warningCount);
        }

        string entry = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss} {level} {message}");
        RecentWarnings.Enqueue(entry.Length <= 240 ? entry : entry[..240]);
        while (RecentWarnings.Count > RecentWarningLimit && RecentWarnings.TryDequeue(out _))
        {
        }
    }
}

/// <summary>Serilog sink mirroring Warning+ events into <see cref="HarnessDiag"/> (status file's recentWarnings).</summary>
public sealed class HarnessDiagSink : ILogEventSink
{
    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level >= LogEventLevel.Warning)
        {
            HarnessDiag.RecordLogEvent(logEvent.Level, logEvent.RenderMessage(CultureInfo.InvariantCulture));
        }
    }
}