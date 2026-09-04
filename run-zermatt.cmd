@echo off
rem MapaTur - start w regionie ZERMATT (pilot Alp, PLAN-ALPY). Wymaga danych regionu w AppData:
rem dem\zermatt.dem, dem\zermatt-ortho-r*-c*.png, dem-cache\swisstopo, ortho-detail\zermatt\opk\det25
rem (recepty: docs/TILE-PRODUCTION-ALPY.md). Kamera i trasa zapisuja sie pod kluczami *.zermatt,
rem niezaleznie od Tatr. Plik celowo w czystym ASCII (cmd + UTF-8 = smieci w komunikatach).
set "MAPATUR_REGION=zermatt"
set "MAPATUR_REGION_CHOSEN=1"
set "EXE=%~dp0src\MapaTur.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MapaTur.App.exe"
if not exist "%EXE%" (
  echo Brak %EXE%
  echo Zbuduj: dotnet build src\MapaTur.App\MapaTur.App.csproj -f net10.0-windows10.0.19041.0 -p:WindowsPackageType=None
  pause
  exit /b 1
)
start "" "%EXE%"
