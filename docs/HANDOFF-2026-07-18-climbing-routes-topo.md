# HANDOFF 2026-07-18 — drogi z topo na Mnichu + asekuracja-sprzęt + poprawki renderu + 5 cm det05 DOMYŚLNE

Sesja 07-18. Czytaj RAZEM z `docs/HANDOFF-2026-07-17-climber3d-takeover.md` (poprzednik — stack Climber3d w apce),
`docs/HANDOFF-2026-07-16-seams-ortho-memory.md` (§5 kolejka orto, §3 twarde reguły), pamięci `climber3d-takeover-epic`,
`ortho-5cm-hardening-order`, `ortho-no-shadows-hard-rule`.

**⭐ ZACOMMITNIĘTE + WYPCHNIĘTE:** `f40e96b` na `feat/walk-mode` (autor Jakub Syrek, ZERO atrybucji AI), push
`cfdfe8f..f40e96b → origin/feat/walk-mode` (48 commitów: mój 1 + 47 zaległych z poprzednich sesji). Testy:
**82 Climbing + 1677 Application green**. `dotnet format`: final-newline moich plików naprawiony
(`insert_final_newline=false`); shaderów GLSL NIE ruszać (pre-existing wyjątek — App project nigdy nie przechodzi
format-verify czysto). NIE weszło (gitignored/celowo): `dem/` (12 GB kafli), `data/climber/` (licencja),
`.githooks/`, `dev/`, `fix-recording.ps1`, `testdata/maps/z17-repair-backup/`.

---

## 0. TL;DR

- **⭐ 5 cm orto (det05) DOMYŚLNE** — cała kolejka z handoffu 07-16 §5 domknięta: regen `_coverage.txt` (8 906 cel
  z 343 077 kafli, skrypt `testdata/maps/regen-det05-coverage.py`) → audyt blue-cast (0/400 fill, blue raw
  korygowany shaderem mode 1) → sync repo→AppData (268 188 kafli, 12 GB) → streaming domyślny z fallbackiem na
  statyczną mozaikę MO. MO showcase zweryfikowany jako pokryty (nie regresuje). `MAPATUR_DET05_STREAM=0` = statyczna.
- **Asekuracja = prawdziwy sprzęt**: lina ze zwisem grawitacyjnym + ekspresy (plakietka+taśma+2 karabinki),
  lina przez DOLNE karabinki, wpięcie z PRZODU uprzęży. Nowy pass GL (`GearRibbon`/`GearRing`).
- **Klasyczne drogi na Mnichu z KUPIONEGO topo Głazka** ("Mnich i Mniszek", wyd. II): 16 dróg ściany wsch. +
  12 pn./pn.-zach., prześledzone ze zdjęcia na **powierzchnię ściany** (model u,v → offset, dx z DEM).
  Podświetlone cienkie linie + nazwy + gwarantowane chwyty wzdłuż linii.
- **Saga kotwicy DOMKNIĘTA**: iglica Mnicha w naszym DEM = `49.192532, 20.054851` (znaleziona skanem
  prominentnych topów; węzeł OSM „Mnich" jest ~175 m OBOK, na zboczu — NIE seedować z niego). Mniszek
  **wyłączony** (w DEM zlewa się z Mnichem przez niski próg MPW → kotwiczył na ścianie Mnicha; dane zostają).
- **Siatka kalibracyjna** na ścianie (5 m, TYLKO na detalu, etykiety offsetu, klawisz `M`).
- **Poprawki renderu**: kolor per-droga, cienka linia ekranowa, na skale, nazwa na swojej linii; wspinacz NAD
  chwytami, trasy POD chwytami; nazwy szczytów/schronisk/jezior NIE prześwitują przez skałę (okluzja po detalu).
- **Anatomia**: bramka chwytu zaostrzona (błąd kontaktu 0.16→0.09 m); orientacja stopy 45°→22° od neutralnej.
- **Proceduralny głaz** (`ProceduralBoulderMesh`): generator bryły z ziarna (icosphere+loby+facety), WIP — NIE
  wpięty w scatter/render (fundament pod „proceduralne skały jak na zdjęciu").

---

## 0a. ORTO 5 cm det05 — DOMYŚLNE (kolejka domknięta)

**Kolejka z HANDOFF-07-16 §5 / HANDOFF-07-15 §9.3 — wszystkie kroki zrobione i zweryfikowane liczbowo:**
1. **Regen `_coverage.txt`**: skrypt `testdata/maps/regen-det05-coverage.py` (odtwarza `OrthoDetailGrid`
   1:1 — cela pokrywa kafle `[6·c, 6·c+16)`, klucz `ci*100000+cj`, próg ≥243/256). Skan 343 077 kafli →
   **8 906 cel** (8 812 pełnych). Stary plik miał ~400 cel (07-14).
2. **Audyt** `audit-ortho-blue-cast.py --dir <det05> --pattern *.webp`: **0/400 black/white fill**; blue raw
   ~2.6/255 — koryguje shader `applyOrthoDetail` **mode 1** (domyślny, reguła de-blue). To oczekiwany stan.
3. **Sync repo→AppData**: `robocopy` (exit 1 = sukces), **268 188 skopiowanych** (12.14 GB), 0 FAILED →
   AppData = pełne **343 077** + świeży `_coverage.txt`.
4. **Domyślne wpięcie**: `Terrain3DView.SetupDet05Streaming` zwraca teraz `bool`; domyślnie streaming, fallback
   na `LoadOrthoDetailMosaics` (statyczna mozaika MO) gdy brak kafli/coverage (świeże instalki/mobile);
   `MAPATUR_DET05_STREAM=0` wymusza statyczną. Log: `det05 streaming ON — ring 350m, cap 3 cells, coverage-gated`
   + `wired ... (8906 covered cells)`. **MO showcase zweryfikowany**: 9 cel wokół okna MO = wszystkie POKRYTE.

**Zasięg 5 cm = pobrany bbox** (`19.80,49.17,20.10,49.30` — wschodnie Tatry wokół MO), nie cały masyw. Reszta
zostaje na det25 (25 cm), finest-wins. ⚠️ **Mobile/inne maszyny**: bez 15 GB det05 sync → fallback na statyczną
mozaikę MO (nie regres). Twarda reguła orto: de-blue na KAŻDEJ warstwie (shader mode 1), audyt po każdym fetchu.

---

## 1. Nowe pliki (Application)

| Plik | Rola |
|---|---|
| `TatraClimbingRoutes.cs` | Katalog dróg (Mnich wsch.+pn.-zach., Mniszek [OFF], Mnich Małołącki) z Master Topo; `SnapToLocalMaximum` (dwustopniowy: prominentne topy + dopasowanie do skatalogowanej elewacji, `ListProminentTops` diag); `BuildWorldRoutes`; `Massifs` |
| `ClimbProtectionGeometry.cs` | Lina (zwis paraboliczny między przelotami, napięta do uprzęży) + ekspresy przez dolne karabinki; TDD 10 testów |
| `ClimbLimbSelection.cs` | `PickOwner` — klik w dzielony chwyt cyklicznie wybiera kończyny (2 ręce/2 stopy); TDD 6 testów |
| `ClimbingRouteHoldSeeder.cs` | Gwarantowane chwyty wzdłuż linii drogi (+ testy) |
| `TerrainOcclusion.IsVisibleFine` | Okluzja etykiet po DETALU (`SampleWalkGround`), nie po grubej bazie |
| `ProceduralBoulderMesh.cs` | Generator bryły głazu z ziarna (icosphere + loby + facety, deterministyczny). **WIP — niewpięty.** |
| `testdata/maps/regen-det05-coverage.py` | Regen `_coverage.txt` det05 (odtwarza `OrthoDetailGrid` 1:1) — patrz §0a |

Testy: `ClimbProtectionGeometryTests`, `ClimbLimbSelectionTests`, `ClimbSessionSurfaceGrowthTests`,
`TatraClimbingRoutesTests`, `ClimbingRouteHoldSeederTests`. (`ProceduralBoulderMesh` bez testu — WIP.)

## 2. Zmienione pliki

- `ClimbSession.cs` — `TryReplaceSurface` (patch rośnie w trakcie sesji; kontakty przemapowane po HoldId; test
  `ClimbSessionSurfaceGrowthTests`).
- `GripClimbController.cs` — wzrost patcha (`GrowPatchIfNearEdge`), cykl wyboru kończyny, **bramka anatomii
  zaostrzona** (`VerifyHardConstraintsAfterGrab`: `contactErr <= 0.09`), `SetClimbingRoutes`.
- `ClimberSkinnedModel.cs` — `OrientFoot`: `maximumRotationFromNeutralDegrees: 22f` (było 45; but edguje
  zamiast się wykręcać). **Reszta rigu verbatim — NIE ruszać.**
- `Terrain3DGlRenderer.cs` — pass `GearRibbon` (ScreenSpace = stała szerokość ekranowa) + `GearRing`;
  `SetClimbHoldMarkers` (depth-tested pass chwytów); kolejność: trasy → chwyty → wspinacz.
- `Terrain3DView.xaml.cs` — `EnsureClimbingRoutes` (kotwiczenie per masyw + re-snap podczas streamingu),
  `BuildClimbProtection`, `BuildClimbRouteOverlay` (kolor golden-angle, screen-space, nazwa na linii),
  siatka kalibracyjna na powierzchni ściany (`EastFaceBase`, fine-only, `M` toggle), `DumpMnichDemField`,
  okluzja fine (`IsPeakVisible`/`IsPoiVisible`/lakes → `IsVisibleFine`, sekwencyjnie),
  **`SetupDet05Streaming` → `bool` + domyślny streaming 5 cm z fallbackiem** (§0a).
- `MapPageViewModel.cs` + `MapPage.xaml` + `AppStrings`/`AppResources[.pl].resx` — przełącznik warstwy
  **„Drogi wspinaczkowe"** (`ShowClimbingRoutes`, persisted) w panelu.

## 3. Model odwzorowania topo → teren (klucz)

Ściana wsch. = **trójkątny wachlarz** od linii podstawy do wierzchołka. `EastFaceBase(u)` (u=0 płd/MPW,
u=1 płn/Górne Półki): BS=(42,-80), BC=(51,0), BN=(36,55). Offset drogi = `Base(u)*(1-v)` (v=0 podstawa,
v=1 szczyt). Ślady dróg czytane ze zdjęcia topo w siatce (%), mapowane przez `topo_to_uv`. Z DEM (dump
`mnich-dem-dump.csv`): ściana spada 2069→1810 m na 88 m poziomo, ~73°. Skrypty odczytu:
`scratchpad/trace_routes2.py`. **Markery kalibracyjne używają TEJ SAMEJ `EastFaceBase`** — leżą tam gdzie drogi.

## 4. OTWARTE (kolejność do usera)

1. **Proceduralne głazy** (user wybrał ten kierunek dla „jak na zdjęciu") — `ProceduralBoulderMesh` gotowy;
   dołożyć: scatter (slope 25–50°, wg koloru orto — przepis w `PLAN-ortho-scatter.md §2`), instanced render
   z granitowym cieniem, toggle „Głazy". Alternatywa świateł: golden hour + ciepło/zimno + rim light (tanie).
2. **Werdykt stopy** — czy 22° wystarczy, czy podeszwa nadal się wywija na dalekim bocznym chwycie
   (pełny stem). Jeśli nadal → źródło w **rollu goleni w dwukostnym IK** (`SolveTwoBoneLimb`) — głębszy,
   ostrożny fix z testami. NIE robić na ślepo.
3. **Dostrojenie przebiegów** wg markerów — user podaje offsety zgięć (`+dx,+dy`), przepisuję 1:1.
4. **Mniszek** — wrócić z pewną, odrębną pozycją turni (teraz OFF, dane w `TatraClimbingRoutes.Mniszek`).
5. **Markery + kalibracja ściany pn.-zach.** (Klasyczna, Orłowskiego, Kant Hakowy — na starym modelu).
6. **5 cm poza pobrany bbox** — jeśli chcemy 5 cm na całym masywie: dofetch `fetch-ortho-detail.py` szerszy
   bbox → regen coverage → audyt → sync (kolejka jak w §0a). Teraz pokryte tylko wschodnie Tatry.
7. Fazy ruchu + interpolacja + solver na workera (z poprzedniego handoffu, nadal otwarte).

## 5. Komendy / kotwice

```powershell
# build+run (stale-exe trap!)
Get-Process MapaTur.App -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 1
dotnet build src/MapaTur.App/MapaTur.App.csproj -c Debug -f net10.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=false
Start-Process -NoNewWindow dotnet -ArgumentList 'run','--project','src/MapaTur.App','-f','net10.0-windows10.0.19041.0','-p:WindowsAppSDKSelfContained=false','--no-build'
# logi kotwiczenia / pozy / orto
Get-Content src/MapaTur.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/logs/mapatur-*.log -Tail 80 | Select-String "\[Climb\]|\[OrthoDetail05\]"

# --- ORTO 5 cm pipeline (po kolei; <AD> = C:\Users\<user>\AppData\Local\User Name\com.companyname.mapatur.app\Data) ---
python testdata/maps/regen-det05-coverage.py "dem/ortho-detail/tatry/det05"                 # 1. coverage
python testdata/maps/audit-ortho-blue-cast.py --dir "dem/ortho-detail/tatry/det05" --pattern "*.webp" --sample 400  # 2. audyt (OBOWIĄZKOWY)
robocopy "dem/ortho-detail/tatry/det05" "<AD>\dem\ortho-detail\tatry\det05" /E /MT:16 /NFL /NDL /NJH /NP   # 3. sync (exit 1 = OK)
```

Klawisze: `C` = sesja wspinaczki, `M` = siatka kalibracyjna on/off, dwuklik = wybór kończyny→ruch.
Kotwica Mnicha: `TatraClimbingRoutes.MnichSummit` (DEM-verified, NIE seedować z OSM). Kupione topo:
`scratchpad/topo-glazek-page{1,2,3}.png` (renders PDF Głazka). Orto 5 cm: domyślne; `MAPATUR_DET05_STREAM=0` =
statyczna mozaika MO. Geometria coverage = `OrthoDetailGrid(0.05, 16, 6)`, klucz `ci*100000+cj`, próg ≥243/256.
