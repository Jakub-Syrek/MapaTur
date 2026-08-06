# P0 bench A/B poolingu GL (2026-08-06): TEN SAM exe, dwa przebiegi env —
#   A (baseline): MAPATUR_GL_POOL=0  → oczekiwana reprodukcja wycieku (+~1 GB ws / lot F9)
#   B (pooling):  domyślnie          → kryterium: ws i commit D3D bez monotonicznego wzrostu
# Przed startem: APP-LOCK.md musi być zajęte przez Ciebie; żadna instancja MapaTur.App nie może działać.
# Użycie:  .\bench-p0-pooling.ps1 -Variant A   (albo B)  [-Runs 8]
param(
    [Parameter(Mandatory = $true)][ValidateSet('A', 'B')][string]$Variant,
    [int]$Runs = 8
)

$exe = 'C:\Repos\MapaTur\src\MapaTur.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MapaTur.App.exe'
if (-not (Test-Path $exe)) { throw "Brak exe: $exe — najpierw build." }
if (Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue) { throw 'MapaTur.App już działa — sprawdź APP-LOCK.' }

$outDir = 'C:\Repos\MapaTur\dev\p0-pooling'
New-Item -ItemType Directory -Force $outDir | Out-Null
$csv = Join-Path $outDir ("bench-{0}-{1}.csv" -f $Variant, (Get-Date -Format 'MMdd-HHmm'))

# Wypis daty exe do logu pomiaru — pułapka „stary exe" (memory: desktop-rebuild-stale-exe-trap).
Write-Host "[bench] exe: $exe ($( (Get-Item $exe).LastWriteTime ))"
Write-Host "[bench] wariant $Variant, $Runs lotów F9, CSV: $csv"

$env:MAPATUR_BENCH_F9 = "$Runs"
$env:MAPATUR_MAXIMIZE = '1'
$env:MAPATUR_F9_RECORD = '0'
Remove-Item Env:MAPATUR_AUTOSHOT_SEC -ErrorAction SilentlyContinue  # autoshot = ~400 ms stall, fałszuje perf
Remove-Item Env:MAPATUR_GL_POOL -ErrorAction SilentlyContinue
Remove-Item Env:MAPATUR_GL_POOL_DISABLE -ErrorAction SilentlyContinue
if ($Variant -eq 'A') { $env:MAPATUR_GL_POOL = '0' }

$app = Start-Process -FilePath $exe -PassThru
Write-Host "[bench] MapaTur.App PID $($app.Id) wystartowal; sampler w tej konsoli."

& (Join-Path $PSScriptRoot 'bench-mem-sampler.ps1') -OutCsv $csv -IntervalSec 6

# Bench sam quituje po serii; dobij, gdyby coś wisiało (zasada: nie zostawiać apki z benchem żywej).
Start-Sleep -Seconds 5
Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host "[bench] koniec wariantu $Variant → $csv"
