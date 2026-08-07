# P0 shadow-regression bisect (2026-08-06 night): reproduce the in-session shading degradation
# with autoshots, under one of four env configs. NOT a perf bench - autoshot stalls are fine here.
#   default -> pooling+PBO on (expected: degradation reproduces)
#   notiles -> MAPATUR_GL_POOL_DISABLE=tiles   (mesh-unit pool off, rest on)
#   nopbo   -> MAPATUR_GL_POOL_DISABLE=pbo    (PBO fencing+B3 routing off, rest on)
#   legacy  -> MAPATUR_GL_POOL=0              (everything legacy end-to-end)
# Usage: pwsh -File bench-p0-shadow.ps1 -Config default [-Runs 8]
param(
    [Parameter(Mandatory = $true)][ValidateSet('default', 'notiles', 'nopbo', 'legacy')][string]$Config,
    [int]$Runs = 8
)

$exe = 'C:\Repos\MapaTur\src\MapaTur.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MapaTur.App.exe'
if (-not (Test-Path $exe)) { throw "Missing exe: $exe - build first." }
if (Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue) { throw 'MapaTur.App already running - check APP-LOCK.' }

$shotDir = "C:\Repos\MapaTur\dev\p0-shadow-bisect\$Config"
New-Item -ItemType Directory -Force $shotDir | Out-Null
Write-Host "[shadow-bisect] exe: $exe ($( (Get-Item $exe).LastWriteTime ))"
Write-Host "[shadow-bisect] config=$Config runs=$Runs shots->$shotDir"

$env:MAPATUR_BENCH_F9 = "$Runs"
$env:MAPATUR_MAXIMIZE = '1'
$env:MAPATUR_F9_RECORD = '0'
$env:MAPATUR_SHOT_DIR = $shotDir
$env:MAPATUR_AUTOSHOT_SEC = '20'
Remove-Item Env:MAPATUR_GL_POOL -ErrorAction SilentlyContinue
Remove-Item Env:MAPATUR_GL_POOL_DISABLE -ErrorAction SilentlyContinue
switch ($Config) {
    'notiles' { $env:MAPATUR_GL_POOL_DISABLE = 'tiles' }
    'nopbo' { $env:MAPATUR_GL_POOL_DISABLE = 'pbo' }
    'legacy' { $env:MAPATUR_GL_POOL = '0' }
}

$app = Start-Process -FilePath $exe -PassThru
Write-Host "[shadow-bisect] PID $($app.Id) started; waiting for bench to finish..."
$app.WaitForExit()

Start-Sleep -Seconds 5
Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$n = (Get-ChildItem $shotDir -Filter *.png -ErrorAction SilentlyContinue).Count
Write-Host "[shadow-bisect] config=$Config done, $n shots in $shotDir"
