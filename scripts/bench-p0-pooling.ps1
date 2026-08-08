# P0 GL-pooling A/B bench (2026-08-06): SAME exe, two env variants:
#   A (baseline): MAPATUR_GL_POOL=0  -> expected leak reproduction (+~1 GB ws per F9 flight)
#   B (pooling):  default            -> pass criterion: ws and D3D commit not growing monotonically
# Before running: APP-LOCK.md must be claimed by you; no MapaTur.App instance may be running.
# Usage:  .\bench-p0-pooling.ps1 -Variant A   (or B)  [-Runs 8]
# NOTE: run with pwsh (PowerShell 7+). Windows PowerShell 5.1 misparses UTF-8-no-BOM scripts.
param(
    [Parameter(Mandatory = $true)][ValidateSet('A', 'B')][string]$Variant,
    [int]$Runs = 8
)

$exe = 'C:\Repos\MapaTur\src\MapaTur.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MapaTur.App.exe'
if (-not (Test-Path $exe)) { throw "Missing exe: $exe - build first." }
if (Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue) { throw 'MapaTur.App already running - check APP-LOCK.' }

$outDir = 'C:\Repos\MapaTur\dev\p0-pooling'
New-Item -ItemType Directory -Force $outDir | Out-Null
$csv = Join-Path $outDir ("bench-{0}-{1}.csv" -f $Variant, (Get-Date -Format 'MMdd-HHmm'))

# Print exe date into the measurement log - the "stale exe" trap (memory: desktop-rebuild-stale-exe-trap).
Write-Host "[bench] exe: $exe ($( (Get-Item $exe).LastWriteTime ))"
Write-Host "[bench] variant $Variant, $Runs F9 flights, CSV: $csv"

$env:MAPATUR_BENCH_F9 = "$Runs"
$env:MAPATUR_MAXIMIZE = '1'
$env:MAPATUR_F9_RECORD = '0'
Remove-Item Env:MAPATUR_AUTOSHOT_SEC -ErrorAction SilentlyContinue  # autoshot = ~400 ms stall, taints perf
Remove-Item Env:MAPATUR_GL_POOL -ErrorAction SilentlyContinue
Remove-Item Env:MAPATUR_GL_POOL_DISABLE -ErrorAction SilentlyContinue
if ($Variant -eq 'A') { $env:MAPATUR_GL_POOL = '0' }

# Keep-awake for the bench window (per-process API, not a system setting): the 08-07 T1 run was
# contaminated by an overnight machine sleep mid-bench (uptime 584 -> 30769 s).
Add-Type -Name Power -Namespace Win32 -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
# UWAGA: literal 0x80000003 w PS parsuje sie jako UJEMNY int32 i konwersja na uint pada
# (MethodException, keep-awake nie dzialal) — stad dziesietnie: 2147483651 = ES_CONTINUOUS|SYSTEM|DISPLAY.
[Win32.Power]::SetThreadExecutionState(2147483651) | Out-Null

$app = Start-Process -FilePath $exe -PassThru
Write-Host "[bench] MapaTur.App PID $($app.Id) started; sampler runs in this console."

& (Join-Path $PSScriptRoot 'bench-mem-sampler.ps1') -OutCsv $csv -IntervalSec 6

# The bench quits by itself after the series; kill leftovers (rule: never leave a bench app alive).
Start-Sleep -Seconds 5
Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
[Win32.Power]::SetThreadExecutionState(2147483648) | Out-Null  # ES_CONTINUOUS — zdejmij keep-awake
Write-Host "[bench] variant $Variant done -> $csv"
