# Task #8 (2026-08-14) — dyskryminator "gpuDed ∝ draw calls": A/B na TYM SAMYM exe, ta sama scena
# (niziny 51.0,20.0 — zero warstw detalu, zero uploadów), ta sama orbita 3 deg/s przez 7 min
# (okno 90..510 s; cały bieg musi zmieścić się w limicie 10 min harnessu uruchamiającego,
# inaczej runner ginie PRZED własnym sprzątaniem i apka benchowa zostaje żywa).
#   U (uncapped): orbita self-invaliduje sie (~300 fps na nizinach) -> draws/min wysokie
#   C (capped):   MAPATUR_FRAME_MS=33 -> orbita jedzie na timerze animacji (~21-30 fps) -> draws/min ~10x nizsze
# Teoria renamow CB per draw przewiduje: nachylenie gpuDed skaluje sie z draws/min (fps). Jesli w C
# nachylenie NIE spada ~proporcjonalnie do fps, teoria sfalsyfikowana -> nastepny kandydat (flagi ANGLE).
# Wymaga buildu z licznikiem draws w status.json (DrawCallInstrumentationTests zielone) i czapy
# FRAME_MS na orbicie harnessu. Przed startem: APP-LOCK zajety przez Ciebie, zero instancji.
# Usage:  pwsh .\bench-t8-draws.ps1 -Variant U   (lub C)
# NOTE: pwsh (PowerShell 7+), nie Windows PowerShell 5.1 (UTF-8 bez BOM).
param(
    [Parameter(Mandatory = $true)][ValidateSet('U', 'C')][string]$Variant
)

$exe = 'C:\Repos\MapaTur\src\MapaTur.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MapaTur.App.exe'
if (-not (Test-Path $exe)) { throw "Missing exe: $exe - build first." }
if (Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue) { throw 'MapaTur.App already running - check APP-LOCK.' }

$outDir = 'C:\Repos\MapaTur\dev\t8-draws'
New-Item -ItemType Directory -Force $outDir | Out-Null
$csv = Join-Path $outDir ("bench-T8{0}-{1}.csv" -f $Variant, (Get-Date -Format 'MMdd-HHmm'))

Write-Host "[bench] exe: $exe ($( (Get-Item $exe).LastWriteTime ))"
Write-Host "[bench] variant T8$Variant, CSV: $csv"

# Wspolne: skok na niziny gdy tylko swiat istnieje (guard WorldFrame odpala skok po LOD built ~60 s),
# orbita 90->510 s @ 3 deg/s. Bez autoshota (stall ~400 ms), bez benchu F9, produkcyjne pule GL.
$env:MAPATUR_MAXIMIZE = '1'
$env:MAPATUR_JUMPS = '25:1:51.0,20.0'
$env:MAPATUR_ORBIT = '90:420:3'
Remove-Item Env:MAPATUR_AUTOSHOT_SEC -ErrorAction SilentlyContinue
Remove-Item Env:MAPATUR_BENCH_F9 -ErrorAction SilentlyContinue
Remove-Item Env:MAPATUR_GL_POOL -ErrorAction SilentlyContinue
Remove-Item Env:MAPATUR_GL_POOL_DISABLE -ErrorAction SilentlyContinue
Remove-Item Env:MAPATUR_FRAME_MS -ErrorAction SilentlyContinue
if ($Variant -eq 'C') { $env:MAPATUR_FRAME_MS = '33' }

# Keep-awake (lekcja T1 08-07: sen maszyny w srodku benchu skazil pomiar). Literal 0x80000003
# parsuje sie w PS jako ujemny int32 — stad dziesietnie: ES_CONTINUOUS|SYSTEM|DISPLAY.
Add-Type -Name Power -Namespace Win32 -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
[Win32.Power]::SetThreadExecutionState(2147483651) | Out-Null

$app = Start-Process -FilePath $exe -PassThru
Write-Host "[bench] MapaTur.App PID $($app.Id) started; orbit window t=90..510 s."

# Sampler konczy sie sam, gdy proces zniknie; my konczymy bieg po oknie orbity (510 s + zapas).
$samplerJob = Start-Job -ScriptBlock {
    param($script, $csvPath)
    & $script -OutCsv $csvPath -IntervalSec 6
} -ArgumentList (Join-Path $PSScriptRoot 'bench-mem-sampler.ps1'), $csv

Wait-Job $samplerJob -Timeout 550 | Out-Null
Stop-Job $samplerJob -ErrorAction SilentlyContinue
Remove-Job $samplerJob -Force -ErrorAction SilentlyContinue

# Zasada: nigdy nie zostawiac apki benchowej zywej (Stop-Process bywa 2x).
Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
[Win32.Power]::SetThreadExecutionState(2147483648) | Out-Null  # ES_CONTINUOUS — zdejmij keep-awake
Write-Host "[bench] variant T8$Variant done -> $csv"
