using System.Runtime.InteropServices;

namespace MapaTur.App.Platforms.Windows;

/// <summary>
/// Wyzwala nagrywanie Game Bara (Win+Shift+R) z poziomu apki — na prośbę użytkownika (2026-07-25), żeby
/// demo F9 samo zaczynało i kończyło nagranie zamiast ręcznego skrótu w ostatniej chwili przed startem.
///
/// Dlaczego Game Bar, a nie własny rekorder: użytkownik nagrywa właśnie nim, a wbudowany rekorder MP4
/// łapie tylko pass GL (bez nakładek Skia: etykiet szczytów, markerów). Game Bar bierze okno takim, jakie
/// jest na ekranie.
///
/// UWAGA (twarda zasada projektu): NIE WOLNO ubijać procesów Game Bara — `GameBarFTServer` zabity
/// wywala apkę przez APPCRASH 0xc000027b. Tu tylko wysyłamy skrót; jeśli Game Bar jest wyłączony
/// w rejestrze (`HKCU\...\GameDVR`), skrót po prostu nic nie zrobi i lot poleci bez nagrania.
/// </summary>
internal static class GameBarCapture
{
    private const int KeyeventfKeyup = 0x0002;
    private const byte VkLwin = 0x5B;
    private const byte VkShift = 0x10;
    private const byte VkR = 0x52;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, nuint dwExtraInfo);

    /// <summary>
    /// Odblokowuje nagrywanie, gdy przycisk Game Bara jest WYSZARZONY — ta sama procedura co
    /// <c>fix-recording.ps1</c> (skrypt w repo, sprawdzony przez użytkownika):
    ///   1. HKCU: AppCaptureEnabled / HistoricalCaptureEnabled / GameDVR_Enabled = 1,
    ///   2. `BcastDVRUserService_*` zawieszony w „Stop Pending" → ubicie hostującego svchost, ale WYŁĄCZNIE
    ///      po potwierdzeniu, że ten svchost hostuje TYLKO BcastDVR (zero strat ubocznych); usługa wstaje sama,
    ///   3. usługa „Stopped" → rozgrzanie przez Start-Service.
    /// NIGDY nie dotyka `GameBarFTServer` ani nakładki Game Bara — ich ubicie wywala apkę (APPCRASH 0xc000027b).
    ///
    /// Wołane w chwili naciśnięcia F9, RÓWNOLEGLE z cachowaniem sceny — kończy się, zanim lot wystartuje,
    /// więc nie dokłada ani sekundy oczekiwania. NIGDY w trakcie nagrywania (wyraźna decyzja użytkownika:
    /// „nie ma naprawiać w trakcie kręcenia").
    /// </summary>
    internal static void EnsureRecordingReadyAsync()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + RepairScript + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using System.Diagnostics.Process? proc = System.Diagnostics.Process.Start(psi);
                if (proc is null)
                {
                    return;
                }

                string outp = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(15000);
                Serilog.Log.Information("[GameBar] przygotowanie nagrywania: {Out}", outp.Replace("\r\n", " | ").Trim());
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[GameBar] przygotowanie nagrywania nie powiodlo sie — lot poleci bez naprawy");
            }
        });
    }

    // Ta sama treść co fix-recording.ps1, kroki 1-3. Trzymana jako jedna linia, bo idzie przez -Command.
    private const string RepairScript =
        "$d='HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\GameDVR';"
        + "New-ItemProperty -Path $d -Name AppCaptureEnabled -Value 1 -PropertyType DWord -Force|Out-Null;"
        + "New-ItemProperty -Path $d -Name HistoricalCaptureEnabled -Value 1 -PropertyType DWord -Force|Out-Null;"
        + "New-ItemProperty -Path 'HKCU:\\System\\GameConfigStore' -Name GameDVR_Enabled -Value 1 -PropertyType DWord -Force|Out-Null;"
        + "$s=Get-CimInstance Win32_Service -Filter \\\"Name LIKE 'BcastDVRUserService%'\\\" -ErrorAction SilentlyContinue;"
        + "if(-not $s){'svc: brak'}elseif($s.State -eq 'Stop Pending' -and $s.ProcessId){"
        + "$c=@(Get-CimInstance Win32_Service -Filter \\\"ProcessId=$($s.ProcessId)\\\"|%{$_.Name});"
        + "if($c.Count -eq 1 -and $c[0] -like 'BcastDVRUserService*'){taskkill /PID $s.ProcessId /F|Out-Null;"
        + "Start-Sleep -Seconds 2;$a=Get-CimInstance Win32_Service -Filter \\\"Name LIKE 'BcastDVRUserService%'\\\";"
        + "if($a.State -ne 'Running'){try{Start-Service -Name $a.Name -ErrorAction Stop}catch{}};"
        + "\\\"svc: odblokowany, stan=$((Get-CimInstance Win32_Service -Filter \\\"Name LIKE 'BcastDVRUserService%'\\\").State)\\\"}"
        + "else{\\\"svc: NIE ubijam — svchost hostuje takze: $($c -join ',')\\\"}}"
        + "elseif($s.State -eq 'Stopped'){try{Start-Service -Name $s.Name -ErrorAction Stop;'svc: rozgrzany'}catch{'svc: start nieudany'}}"
        + "else{\\\"svc: $($s.State) — OK\\\"}";

    /// <summary>Wysyła Win+Shift+R — Game Bar traktuje to jako start LUB stop nagrania (ten sam skrót).
    /// Zwraca false, gdy wysyłka się nie powiodła; nigdy nie rzuca (nagranie jest dodatkiem, nie warunkiem lotu).</summary>
    internal static bool ToggleRecording()
    {
        try
        {
            keybd_event(VkLwin, 0, 0, 0);
            keybd_event(VkShift, 0, 0, 0);
            keybd_event(VkR, 0, 0, 0);
            keybd_event(VkR, 0, KeyeventfKeyup, 0);
            keybd_event(VkShift, 0, KeyeventfKeyup, 0);
            keybd_event(VkLwin, 0, KeyeventfKeyup, 0);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
