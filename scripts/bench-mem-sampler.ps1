# P0 sampler krzywej pamięci (odtworzony 2026-08-06 — wersja z 08-02 nie została zacommitowana).
# Czyta %TEMP%\mapatur-status.json (pisany co 2 s z wątku tła apki) + zewnętrzne liczniki
# (GPU Process Memory\Dedicated/Shared Usage, PrivateMemorySize64) i skleja do CSV.
# Użycie:  .\bench-mem-sampler.ps1 -OutCsv dev\p0-pooling\bench-A.csv [-IntervalSec 6]
# Kończy się sam, gdy proces MapaTur.App zniknie (bench F9 sam quituje po serii).
param(
    [Parameter(Mandatory = $true)][string]$OutCsv,
    [int]$IntervalSec = 6
)

$statusPath = Join-Path $env:TEMP 'mapatur-status.json'
$dir = Split-Path -Parent $OutCsv
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

'ts,uptimeSec,wsMB,heapMB,privMB,gpuDedMB,gpuShMB,glTex,glBuf,glVboMB,glPoolMB,glPoolHit,glPoolMiss,pboWaits,renderFps' |
    Set-Content -Path $OutCsv

Write-Host "[sampler] czekam na proces MapaTur.App i $statusPath ..."
while (-not (Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }

while ($true) {
    $proc = Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue
    if (-not $proc) { Write-Host '[sampler] proces zniknal - koniec.'; break }
    if ($proc.Count -gt 1) { Write-Warning "[sampler] $($proc.Count) instancji MapaTur.App! Pomiar skazony."; }
    $p = $proc | Select-Object -First 1

    $s = $null
    try { $s = Get-Content $statusPath -Raw -ErrorAction Stop | ConvertFrom-Json } catch {}

    $gpuDed = 0.0; $gpuSh = 0.0
    try {
        $ded = (Get-Counter "\GPU Process Memory(pid_$($p.Id)*)\Dedicated Usage" -ErrorAction Stop).CounterSamples |
            Measure-Object -Property CookedValue -Sum
        $gpuDed = [math]::Round($ded.Sum / 1MB, 0)
    } catch {}
    try {
        $sh = (Get-Counter "\GPU Process Memory(pid_$($p.Id)*)\Shared Usage" -ErrorAction Stop).CounterSamples |
            Measure-Object -Property CookedValue -Sum
        $gpuSh = [math]::Round($sh.Sum / 1MB, 0)
    } catch {}

    $line = '{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14}' -f `
        (Get-Date -Format 'HH:mm:ss'),
        ($s.uptimeSec ?? ''), ($s.wsMB ?? [math]::Round($p.WorkingSet64 / 1MB)), ($s.heapMB ?? ''),
        ([math]::Round($p.PrivateMemorySize64 / 1MB)), $gpuDed, $gpuSh,
        ($s.glTex ?? ''), ($s.glBuf ?? ''), ($s.glVboMB ?? ''), ($s.glPoolMB ?? ''),
        ($s.glPoolHit ?? ''), ($s.glPoolMiss ?? ''), ($s.pboWaits ?? ''), ($s.renderFps ?? '')
    Add-Content -Path $OutCsv -Value $line
    Start-Sleep -Seconds $IntervalSec
}
