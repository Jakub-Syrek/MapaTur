@echo off
rem MapaTur - start w regionie TATRY (domyslny). Niezalezny od run-zermatt.cmd: kazdy region ma
rem wlasna zapisana kamere i przystanki trasy (MountainRegion.PreferenceKey), wiec przelaczanie
rem nie gubi pozycji. MAPATUR_REGION czyscimy JAWNIE, zeby odziedziczona zmienna z powloki nie
rem przelaczyla regionu po cichu. Plik celowo w czystym ASCII (cmd + UTF-8 = smieci w komunikatach).
set "MAPATUR_REGION="
set "MAPATUR_REGION_CHOSEN=1"
set "EXE=%~dp0src\MapaTur.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\MapaTur.App.exe"
if not exist "%EXE%" (
  echo Brak %EXE%
  echo Zbuduj: dotnet build src\MapaTur.App\MapaTur.App.csproj -f net10.0-windows10.0.19041.0 -p:WindowsPackageType=None
  pause
  exit /b 1
)
start "" "%EXE%"
