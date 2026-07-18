# HANDOFF 2026-07-18 — drogi wspinaczkowe z topo na Mnichu + asekuracja jako sprzęt + poprawki renderu

Sesja 07-18. Czytaj RAZEM z `docs/HANDOFF-2026-07-17-climber3d-takeover.md` (poprzednik — cały stack Climber3d
żywy w apce) i pamięcią `climber3d-takeover-epic`. Wszystko w working tree, **NIE commitnięte** (czeka na
zgodę usera). Testy: **82 Climbing + 1677 Application green**.

---

## 0. TL;DR

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

---

## 1. Nowe pliki (Application)

| Plik | Rola |
|---|---|
| `TatraClimbingRoutes.cs` | Katalog dróg (Mnich wsch.+pn.-zach., Mniszek [OFF], Mnich Małołącki) z Master Topo; `SnapToLocalMaximum` (dwustopniowy: prominentne topy + dopasowanie do skatalogowanej elewacji, `ListProminentTops` diag); `BuildWorldRoutes`; `Massifs` |
| `ClimbProtectionGeometry.cs` | Lina (zwis paraboliczny między przelotami, napięta do uprzęży) + ekspresy przez dolne karabinki; TDD 10 testów |
| `ClimbLimbSelection.cs` | `PickOwner` — klik w dzielony chwyt cyklicznie wybiera kończyny (2 ręce/2 stopy); TDD 6 testów |
| `ClimbingRouteHoldSeeder.cs` | Gwarantowane chwyty wzdłuż linii drogi (+ testy) |
| `TerrainOcclusion.IsVisibleFine` | Okluzja etykiet po DETALU (`SampleWalkGround`), nie po grubej bazie |

Testy: `ClimbProtectionGeometryTests`, `ClimbLimbSelectionTests`, `ClimbSessionSurfaceGrowthTests`,
`TatraClimbingRoutesTests`, `ClimbingRouteHoldSeederTests`.

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
  okluzja fine (`IsPeakVisible`/`IsPoiVisible`/lakes → `IsVisibleFine`, sekwencyjnie).

## 3. Model odwzorowania topo → teren (klucz)

Ściana wsch. = **trójkątny wachlarz** od linii podstawy do wierzchołka. `EastFaceBase(u)` (u=0 płd/MPW,
u=1 płn/Górne Półki): BS=(42,-80), BC=(51,0), BN=(36,55). Offset drogi = `Base(u)*(1-v)` (v=0 podstawa,
v=1 szczyt). Ślady dróg czytane ze zdjęcia topo w siatce (%), mapowane przez `topo_to_uv`. Z DEM (dump
`mnich-dem-dump.csv`): ściana spada 2069→1810 m na 88 m poziomo, ~73°. Skrypty odczytu:
`scratchpad/trace_routes2.py`. **Markery kalibracyjne używają TEJ SAMEJ `EastFaceBase`** — leżą tam gdzie drogi.

## 4. OTWARTE (kolejność do usera)

1. **Werdykt stopy** — czy 22° wystarczy, czy podeszwa nadal się wywija na dalekim bocznym chwycie
   (pełny stem). Jeśli nadal → źródło w **rollu goleni w dwukostnym IK** (`SolveTwoBoneLimb`) — głębszy,
   ostrożny fix z testami. NIE robić na ślepo.
2. **Dostrojenie przebiegów** wg markerów — user podaje offsety zgięć (`+dx,+dy`), przepisuję 1:1.
3. **Mniszek** — wrócić z pewną, odrębną pozycją turni (teraz OFF, dane w `TatraClimbingRoutes.Mniszek`).
4. **Markery + kalibracja ściany pn.-zach.** (Klasyczna, Orłowskiego, Kant Hakowy — na starym modelu).
5. Fazy ruchu + interpolacja + solver na workera (z poprzedniego handoffu, nadal otwarte).

## 5. Komendy / kotwice

```powershell
# build+run (stale-exe trap!)
Get-Process MapaTur.App -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 1
dotnet build src/MapaTur.App/MapaTur.App.csproj -c Debug -f net10.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=false
Start-Process -NoNewWindow dotnet -ArgumentList 'run','--project','src/MapaTur.App','-f','net10.0-windows10.0.19041.0','-p:WindowsAppSDKSelfContained=false','--no-build'
# logi kotwiczenia / pozy
Get-Content src/MapaTur.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/logs/mapatur-*.log -Tail 80 | Select-String "\[Climb\]"
```

Klawisze: `C` = sesja wspinaczki, `M` = siatka kalibracyjna on/off, dwuklik = wybór kończyny→ruch.
Kotwica Mnicha: `TatraClimbingRoutes.MnichSummit` (DEM-verified, NIE seedować z OSM). Kupione topo:
`scratchpad/topo-glazek-page{1,2,3}.png` (renders PDF Głazka).
