; ============================================================================
;  MapaTur — Windows desktop installer (Inno Setup)
; ----------------------------------------------------------------------------
;  Per-user, unpackaged (no MSIX) install of the self-contained win-x64 build,
;  bundled with the full-resolution Tatra terrain data.
;
;  The installed app has no repo around it, so its auto-loader
;  (FileSystemMapAutoLoader) probes ONLY FileSystem.AppDataDirectory\{maps,dem}.
;  On unpackaged MAUI Windows that AppDataDirectory resolves to
;     %LOCALAPPDATA%\User Name\com.companyname.mapatur.app\Data
;  (note the trailing \Data — the Windows App SDK ApplicationData LocalFolder),
;  confirmed by the runtime log "GUGiK DEM cache root: ...\Data\dem-cache\gugik".
;  So data MUST land in ...\Data\maps and ...\Data\dem — dropping it one level
;  up (...\maps, ...\dem) makes discovery come up empty and the app shows only
;  the synthetic demo tile. "User Name" + "com.companyname.mapatur.app" are the
;  static Publisher / Identity baked into Platforms\Windows\Package.appxmanifest,
;  so this path is identical on every machine.
;
;  Build the payload first:
;    dotnet publish src/MapaTur.App/MapaTur.App.csproj -c Release `
;      -f net10.0-windows10.0.19041.0 -p:WindowsSelfContained=true
;  Then compile this script:
;    & "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\MapaTur.iss
; ============================================================================

#define MyAppName "MapaTur"
#define MyAppVersion "1.0"
#define MyAppPublisher "Jakub Syrek"
#define MyAppURL "https://github.com/Jakub-Syrek/MapaTur"
#define MyAppExeName "MapaTur.App.exe"

; Absolute source roots (compile from anywhere).
#define RepoRoot   "C:\Repos\MapaTur"
#define PublishDir RepoRoot + "\src\MapaTur.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"

; Where the installed app looks for its data (per-user LocalAppData). The
; trailing \Data is part of FileSystem.AppDataDirectory on unpackaged Windows —
; see the header note. Do NOT drop the \Data segment.
#define DataDem  "{localappdata}\User Name\com.companyname.mapatur.app\Data\dem"
#define DataMaps "{localappdata}\User Name\com.companyname.mapatur.app\Data\maps"

[Setup]
; A fixed AppId = stable identity for upgrades / uninstall. Do not change it.
AppId={{B9F1B7B0-7C2E-4A1F-9D3A-6E5C8A2F4D17}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Per-user install: no admin prompt, and the data lands in the installing
; user's LocalAppData — exactly where the app then reads it.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=G:\app
OutputBaseFilename=MapaTur-Setup
; PNG ortho tiles are already compressed — recompressing wastes time and gains
; nothing (they carry the `nocompression` flag below). Everything else (the
; self-contained .NET DLLs, the DEM, the mbtiles) compresses well.
Compression=lzma2/normal
SolidCompression=no
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupLogging=yes

[Languages]
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; --- Application (self-contained .NET 10 + Windows App SDK) ---------------
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; --- Terrain data → per-user LocalAppData (auto-loader search roots) ------
; DEM (heightfield) — compresses, so no nocompression flag.
Source: "{#RepoRoot}\dem\tatry.dem"; DestDir: "{#DataDem}"; Flags: ignoreversion
; Full-res ortho photo tiles (8 PNG, ~2.8 GB) — already compressed.
Source: "{#RepoRoot}\dem\tatry-ortho-r*-c*.png"; DestDir: "{#DataDem}"; Flags: ignoreversion nocompression
; 2D basemap tiles (mbtiles). Top-level glob skips the .osm.pbf sources and .build/.
Source: "{#RepoRoot}\maps\*.mbtiles"; DestDir: "{#DataMaps}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
