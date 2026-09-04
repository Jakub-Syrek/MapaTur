# HANDOFF 2026-08-28 — sesja 08-25→28: task #8 ✅, det1m ✅, rejestr regionów, pilot ZERMATT żywy

**START następnej sesji: memory `alps-version-plan` + `app-unusable-route-planning-p0` + ten plik.**
Plan-matka: [`PLAN-ALPY.md`](PLAN-ALPY.md). Recepty danych Alp: [`TILE-PRODUCTION-ALPY.md`](TILE-PRODUCTION-ALPY.md)
(§A0–A5 wykonane i spisane). Protokoły pomiarowe task #8: `dev/t8-vidmm/NOTATKI-0826.md`.

## Stan w jednym akapicie

**AKTUALIZACJA 2026-08-28 wieczorem: werdykt #9 ODEBRANY („zaćmienie wygląda dobrze") i main
JEST JUŻ NA `origin` — `5eedd01..fc593f3`, 20 commitów, bramki zielone (format 4/4, testy
2268/2268, 0 błędów).** Poniższy akapit opisuje stan sprzed pusha.

Na lokalnym main leży **22 commity bez pusha** (push po werdykcie usera dla taska #9 — zaćmienie,
build c609189, wciąż otwarty). Task #8 (pełzanie commit GPU) DOMKNIĘTY: przyczyną były pady XAML nad
SwapChainPanelem → pady rysuje Skia (+30,5 vs +397 MB/min). Martwa od migracji compact-tail warstwa
det1m WSKRZESZONA (`Det1mChainComposer`, odebrana przez usera). Rejestr regionów (P-A1/A2) działa:
Tatry = wpis #1 pinowany bit-w-bit, `zermatt` = wpis #2. Pilot Zermatt renderuje: geometria z16
z swissALTI3D + baza orto SWISSIMAGE + nazwy 113 szczytów z OSM; przełącznik `MAPATUR_REGION=zermatt`.

## Kolejność commitów tej sesji (wszystkie za bramkami build 0/0 + testy + format verify)

91f0dc5 pady Skia (task #8) · 1dd9092 format · 0d6db4a PLAN-ALPY · a33b0cc P-A1 rejestr ·
e5f3b18 fix dlon kraty (0,65935→cos49,25; przy okazji ODKRYCIE martwego det1m) · 5f7579c det1m
composer+wskrzeszenie · 9437dbf P-A2 ścieżki/coverage/PickDem · 8571f48 P-B1 fetch swisstopo+datum ·
7cc2c27 P-B2 wpis zermatt+MAPATUR_REGION+zermatt.dem+FilterOrtho · 0dcbc22 P-B3 kafle z16+coverage ·
50a7334 P-B4 baza orto · b45848c P-B5 nazwy szczytów per-region.

## Werdykty usera zapisane w tej sesji (NIE cofać)

- Pady Skia: „pady wyglądają ok". det1m po hotfixie czerni: „jest ok". Próbka Zermatt/sufit 25 cm:
  zaakceptowane. Kryteria: task #8 zamknięty POMIAREM +30,5 MB/min (kryterium usera: po naprawie).
- **Task #9 (zaćmienie) 2026-08-28 ~17:45: „zaćmienie wygląda dobrze" → ODEBRANY, NIE cofać.**
  Warunki werdyktu: build `bin\Debug` z 08-28 10:11 (= `main`, zawiera `c609189`), `MAPATUR_DATE=
  2026-08-12` + `MAPATUR_TIME_HOURS=19.83` (19:50 CEST — słońce ~1°, czyli POD progiem 2° gdzie
  działa nowy taper; obscuracja ~0,47), poza usera Dolinka za Mnichem 49.1803/20.0459 z 241 m,
  pitch ~9° (prawie poziomo w horyzont). Oglądane ~54 min, 0 WRN / 0 ERR, brak APPCRASH.

## PILOT ZERMATT — jak uruchomić i co gdzie leży

- **Apka**: `MAPATUR_REGION=zermatt` (env; bez env = Tatry). Skok pod Matterhorn:
  `MAPATUR_JUMPS='50:1:45.9765,7.6582'`. Instancja usera na koniec sesji DZIAŁAŁA na Zermacie.
- **Dane źródłowe** (poza gitem): `maps/swisstopo-zermatt/{dem05,img10}` (420+420 kafli, 25 GB,
  roczniki 2024/2023 — JEDNORODNE); manifesty JSON z weryfikacją.
- **Dane apki** (AppData `…\com.companyname.mapatur.app\Data`): `dem/zermatt.dem` (baza 30 m),
  `dem/zermatt-ortho-r{0..2}-c{0..2}.png` (baza orto 3×3×8192² RGBA), `dem-cache/swisstopo/16/`
  (2156 kafli z16). Zasób nazw: `Resources/Raw/zermatt-osm-peaks.json` (bundlowany).
- Datum pionowy ZMIERZONY: ortometryczny (Matterhorn 4477,34 vs LN02 4477,5) — zero korekty.

## OTWARTE — kolejka następnej sesji (w tej kolejności)

1. **De-blue bazy orto Zermatt** (⚠ ORTO-CONTRACT hard rule, warstwa NIE „odebrana"): niebieski
   cień nalotu 2023 na ścianach N. Audyt `audit-ortho-blue-cast.py` na celach + sprawdzić, czy
   runtime mode-1 de-blue obejmuje tor bazy z auto-loadera (H3/uOrthoDetailColorMode w rendererze);
   korekta runtime albo data-side wg wyniku. Werdykt wizualny usera na koniec.
2. **§A6 det25 dla Zermatt**: piramida WebP na WŁASNEJ kracie regionu (kotwice z rejestru:
   7.58/46.08/RefLat 46.0 — NIE tatrzańskie!) z SWISSIMAGE + prebake `.opk` OrthoBakiem; potem
   piramida baked `.bdt` DEM (recon parametryzacji `TatraBakeRunner`).
3. **P-A3**: wpisy regionów z JSON + produktowe przełączanie (RegionContext) + stringi UI z nazwą
   regionu. ✅ **P-A3-lite ZROBIONE 08-29** (na prośbę usera, commit 07fc2e5): `run-tatry.cmd` /
   `run-zermatt.cmd` + `MountainRegion.PreferenceKey` (kamera i przystanki trasy per region; Tatry na
   gołym kluczu bit w bit). Dowód: 3 starty przez launchery, poza Zermatt odtworzona co do cyfry po
   sesji tatrzańskiej. Ogon „z Tatr ląduje w Zermacie" był w praktyce auto-kadrem (`TryRestoreCamera`
   odrzuca obcy DemKey); realną stratą było GUBIENIE pozy drugiego regionu — to naprawione.
4. Ogony mniejsze: włoska flanka NoData (dociągnąć źródłem IT albo Terrarium w generatorach);
   zoomy z13/z14 offline w VM z rejestru; det1m poza `OrthoVramBudgetBytes` (watch — ruszyć tylko
   przy dowodzie głodzenia); duplikaty §14 w TILE-PRODUCTION przy merge'u gałęzi quirky-morse.
5. Tatrzańskie zaległości bez zmian (region C 5 cm, instalka ~132 GB, deshadow, słońce).

## ⚠ OGON 08-29 wieczór: „ściana przed oczyma" = poza kamery POD ZIEMIĄ, nie teren

Po §A8 user zgłosił ścianę na cały ekran. Pomiar: cel kamery z logu `target z = 360 m`, a min bazy
`zermatt.dem` = 1424 m → cel pod terenem, kamera z 3076 m patrzy w dół przez przekrój terenu.
Źródło: `MAPATUR_START_POSE` przy teście launcherów zastosował się CZĘŚCIOWO (tx/az weszły, ty/tz/dist
zaklampowane → tz 358) i ta poza zapisała się pod kluczem `.zermatt`, po czym była odtwarzana przy
każdym starcie. Wypełnienie Terrarium ściany NIE wprowadziło (max skok sąsiadów w strefie IT/szwu 112 m
vs 256 m w CH; 0 skoków >150 m w wypełnieniu). Naprawa doraźna: restart z prawdziwą pozą Matterhorn
(`-4769.342;-3585.5508;3953.9727;150;-1.116051;0.42999932`) → nadpisała zapis. **DO ZROBIENIA:**
strażnik w `TryApplyEnvPose`/`TryRestoreCamera` — cel poniżej terenu (albo poniżej min bazy) = odrzuć
i auto-kadruj; dziś zła poza przechodzi i się utrwala.

## Nie ruszać / konteksty

- **Push**: ✅ ZROBIONY 08-28 (`5eedd01..fc593f3`). ZOSTAJE: gałąź `claude/quirky-morse-145976`
  (det25 derywacja, ODEBRANA) — merge do main, pliki rozłączne z main (zmierzone), tylko §14
  przenumerować. Podobnie 3 pozostałe gałęzie `claude/*` w worktree.
- **Ogon porządkowy**: 4 pliki w drzewie roboczym różnią się TYLKO końcowym znakiem nowej linii
  (`DxgiDriverTrim.cs`, `TatraTrailheadParking.cs`, `IUnmarkedPathRepository.cs`,
  `OverpassUnmarkedPathQueryBuilder.cs`). Stan NA DYSKU jest zgodny z `.editorconfig`
  (`insert_final_newline = false`) — to te same stragglery co commit `fd4ee09`, który zrobił
  dokładnie tę korektę w 4 INNYCH plikach. Do domknięcia jednym `style(format)` commitem.
- **Bug harnessu (znaleziony 08-28, NIE naprawiony)**: `MAPATUR_CLOUDS` jest martwy, gdy istnieje
  zapisane ustawienie zachmurzenia — env jest przypisywany PRZED `settingsStore.Cloudiness`
  (`MapPageViewModel.cs` ~2261), więc zapis wygrywa. `MAPATUR_TIME_HOURS` ma kolejność odwrotną
  (env wygrywa) — i tak ma być. Fix = przenieść blok env pod blok zapisu.
- Zasada 20 (APP-LOCK) działała przez całą sesję; C2C 054–059 wysłane (Codex poinformowany o padach,
  det1m i formacie paczek). Standing consent na zamykanie apki do build/test — po rundzie stawiać
  z powrotem (user siedział na Zermacie!).
- Narzędzia pomiarowe zostają: `scripts/bench-t8-vidmm.ps1`, `dev/t8-vidmm/` (EtlDump/analyze,
  NOTATKI), `dev/det1m-probe/`. ETL 8,7 GB w dev/t8-vidmm można skasować (JSONL wystarcza).
- Lekcje sesji (w memory, skrót): env debugowy nieznanego działania = nie oceniaj wizualnie;
  zrzut przed zbieżnością streamingu = śmieć; zmiana formatu danych = grep po WSZYSTKICH
  konsumentach; pliki repo zapisywać binarnie (python text-mode CRLF-ifikuje); `strings` nie
  widzi literałów .NET.
