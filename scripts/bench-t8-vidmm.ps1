# Task #8 (2026-08-25) — slad alokacyjny VidMm wokol KANONICZNEGO biegu T8U (bench-t8-draws.ps1,
# nietkniety). Nagrywa ETW DxgKrnl (profil dev/t8-vidmm/vidmm.wprp: alokacje 33/34/39/40 + commit 371
# ze stackwalkiem) przez caly bieg, po biegu dekoduje do XML tracerptem. Cel: rozstrzygnac mechanizm
# "nachylenie gpuDed ~ 2x upload MB/min" NA POZIOMIE ALOKACJI (handoff 08-14, punkt 1 kolejki).
#
# wpr wymaga ADMINA, ale apka benchowa MUSI zostac nie-elevated (to samo srodowisko co biegi 08-14),
# wiec elevacja jest w OSOBNYM procesie-helperze: jeden UAC, helper startuje wpr, czeka na flage,
# zatrzymuje wpr i konczy. Komunikacja przez pliki-flagi w dev/t8-vidmm.
# Usage: pwsh .\scripts\bench-t8-vidmm.ps1     (bieg = wariant U; przed startem APP-LOCK zajety!)
# NOTE: pwsh 7+. Bieg ~10 min + tracerpt (do kilku min przy duzym ETL).
$ErrorActionPreference = 'Stop'
$repo = 'C:\Repos\MapaTur'
$outDir = Join-Path $repo 'dev\t8-vidmm'
New-Item -ItemType Directory -Force $outDir | Out-Null

$ts = Get-Date -Format 'MMdd-HHmm'
$etl = Join-Path $outDir "vidmm-T8U-$ts.etl"
$xml = Join-Path $outDir "vidmm-T8U-$ts.xml"
$wprp = Join-Path $outDir 'vidmm.wprp'
$startedFlag = Join-Path $outDir 'wpr-started.flag'
$stopFlag = Join-Path $outDir 'wpr-stop.flag'
$doneFlag = Join-Path $outDir 'wpr-done.flag'
$errFile = Join-Path $outDir 'wpr-error.txt'
Remove-Item $startedFlag, $stopFlag, $doneFlag, $errFile -ErrorAction SilentlyContinue

if (Get-Process -Name 'MapaTur.App' -ErrorAction SilentlyContinue) { throw 'MapaTur.App already running - check APP-LOCK.' }

# Helper (elevated): start wpr -> flaga started -> czekaj na flage stop -> wpr -stop -> flaga done.
$helper = Join-Path $outDir "wpr-helper-$ts.ps1"
@"
`$ErrorActionPreference = 'Stop'
try {
    wpr.exe -cancel 2>`$null | Out-Null   # sprzatnij ewentualna wiszaca sesje (ignoruj blad braku)
} catch {}
try {
    wpr.exe -start "$wprp!VidMm" -filemode
    if (`$LASTEXITCODE -ne 0) { throw "wpr -start exit `$LASTEXITCODE" }
    New-Item -ItemType File '$startedFlag' | Out-Null
    while (-not (Test-Path '$stopFlag')) { Start-Sleep -Seconds 2 }
    wpr.exe -stop "$etl"
    if (`$LASTEXITCODE -ne 0) { throw "wpr -stop exit `$LASTEXITCODE" }
    New-Item -ItemType File '$doneFlag' | Out-Null
} catch {
    `$_ | Out-File '$errFile'
    exit 1
}
"@ | Set-Content -Encoding utf8 $helper

Write-Host '[vidmm] Startuje elevated helper wpr (JEDEN prompt UAC - kliknij Tak)...'
Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $helper

$deadline = (Get-Date).AddSeconds(120)
while (-not (Test-Path $startedFlag)) {
    if (Test-Path $errFile) { throw "wpr -start padl: $(Get-Content $errFile -Raw)" }
    if ((Get-Date) -gt $deadline) { throw 'Timeout na UAC/wpr -start (120 s).' }
    Start-Sleep -Seconds 2
}
Write-Host '[vidmm] ETW nagrywa. Odpalam kanoniczny bieg T8U...'

try {
    & (Join-Path $repo 'scripts\bench-t8-draws.ps1') -Variant U
} finally {
    # Zawsze zatrzymaj nagrywanie, nawet gdy bench padl (ETL czesciowy > zadnego).
    New-Item -ItemType File $stopFlag -Force | Out-Null
    $deadline = (Get-Date).AddSeconds(300)   # wpr -stop scala bufory - potrafi trwac
    while (-not (Test-Path $doneFlag)) {
        if (Test-Path $errFile) { Write-Warning "wpr -stop padl: $(Get-Content $errFile -Raw)"; break }
        if ((Get-Date) -gt $deadline) { Write-Warning 'Timeout na wpr -stop (300 s).'; break }
        Start-Sleep -Seconds 3
    }
}
if (-not (Test-Path $etl)) { throw "Brak ETL: $etl" }
Write-Host "[vidmm] ETL: $etl ($([math]::Round((Get-Item $etl).Length/1MB,1)) MB). Dekoduje tracerptem (bez admina)..."
tracerpt.exe $etl -o $xml -of XML -lr -y | Out-Null
if (-not (Test-Path $xml)) { throw "tracerpt nie zapisal $xml" }
Write-Host "[vidmm] XML: $xml ($([math]::Round((Get-Item $xml).Length/1MB,1)) MB)"
Write-Host "[vidmm] Analiza: python dev\t8-vidmm\analyze-vidmm.py `"$xml`" --bench-csv <CSV z dev\t8-draws>"
