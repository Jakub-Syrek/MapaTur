# Wznowienie fetchu 5 cm dla REGIONU C (19.50,49.10,20.40,49.40) — cel z memory goal-whole-tatras-5cm.
# Fetchery sa wznawialne (pomijaja to, co juz na dysku). Uruchamiac w OSOBNYM, odlaczonym procesie
# (memory: dlugie fetche gina z sesja agenta): Start-Process pwsh -File <ten plik> -WindowStyle Hidden
# Kolejnosc: SK (sk05, BEZ --strip-km — §0-B) -> PL (det05). Logi w dev/fetch-logs/.
$ErrorActionPreference = 'Continue'
Set-Location 'C:\Repos\MapaTur'
$env:PYTHONIOENCODING = 'utf-8'
$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
"START $(Get-Date)" | Out-File "dev\fetch-logs\regionC-resume-$stamp.log"
python testdata\maps\fetch-ortho-detail.py --region C --level sk05 --workers 8 *>> "dev\fetch-logs\sk05-regionC-resume-$stamp.log"
"SK DONE $(Get-Date) exit=$LASTEXITCODE" | Out-File "dev\fetch-logs\regionC-resume-$stamp.log" -Append
python testdata\maps\fetch-ortho-detail.py --region C --level det05 --workers 6 *>> "dev\fetch-logs\det05-regionC-resume-$stamp.log"
"PL DONE $(Get-Date) exit=$LASTEXITCODE" | Out-File "dev\fetch-logs\regionC-resume-$stamp.log" -Append
