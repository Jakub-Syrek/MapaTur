# HANDOFF 2026-07-31 — epika SK det05 DOMKNIĘTA na main + instrukcja współpracy z Codexem

## ══ STAN: WSZYSTKO ODEBRANE PRZEZ USERA I ZMERGOWANE ══

**main = `b301603`** (merge 29 commitów, bramki: format zielony, testy 1756/1756).

| co | stan |
|---|---|
| det05 | **1 004 201 kafli** (PL + całe słowackie Tatry 5 cm), coverage p16 = **3 712 cel** |
| opk det05 | **109,1 GB**, verify-full **1 008 237 stron BAD=0**, layout czysty |
| znaki wodne GKÚ | **1 903 instancje USUNIĘTE** z warstwy 5 cm (werdykt usera: „znaki zniknęły") |
| werdykty usera | pilot 07-26 „ok, szew razi z bliska"; pełny zakres + znaki 07-31 „jest ok" — **STAN ODEBRANY, zasada 19: nie cofać** |
| apka | działa dla usera (PID 9808 z 07-31), dane w AppData zsynchronizowane |
| bug capa 48 | naprawiony (144/192 cel było niewidocznych); `Det05LayerCapConsistencyTests` pilnuje |

Pełne przebiegi i liczby: [`HANDOFF-2026-07-26-sk-det05-v2.md`](HANDOFF-2026-07-26-sk-det05-v2.md)
(fetch→harmonizacja→integracja→bake + saga znaków wodnych), [`HANDOFF-2026-07-25-sk-det05-pilot.md`](HANDOFF-2026-07-25-sk-det05-pilot.md)
(pilot + odkrycie białego nodata HighRes), [`TILE-PRODUCTION.md`](TILE-PRODUCTION.md) §11 (recepta).

## ══ WSPÓŁPRACA Z CODEXEM — PRZECZYTAJ ZANIM COKOLWIEK URUCHOMISZ ══

Na tej maszynie pracuje RÓWNOLEGLE drugi agent: **Codex**, w `C:\Repos\MapaTur-rock-material`
(osobny klon), gałąź `codex/realistic-rock-material` — buduje hybrydowe kafle geometrii **RMP3**
(skały 3D zastępujące DEM na stromiznach). Katalogi robocze są rozłączne, ale wspólne są:
**proces apki, AppData, RAM/VRAM, dysk**. Dwa pliki koordynacyjne leżą POZA repo (bo gałęzie różne):

### 1. `C:\Repos\APP-LOCK.md` — blokada apki (zasada 20, `docs/ZASADY-MAPATUR.md`)
- **Przed uruchomieniem `MapaTur.App`, `OrthoBake`, `RockBake`, benchem, nagrywaniem, synciem do
  AppData:** przeczytaj STATUS. ZAJĘTE przez niego → NIE uruchamiaj, nie zamykaj jego instancji.
- Zajmując: STATUS=ZAJĘTE (kto/od/cel) + wiersz w dzienniku NA DOLE. Po skończeniu: **zamknij apkę,
  STATUS=WOLNE, wiersz w dzienniku** — także gdy test się nie udał.
- Wyjątek odnotowywany: apka uruchomiona DLA USERA (do werdyktu/testów) zostaje przy STATUS=WOLNE
  z adnotacją w dzienniku „nie zamykać". Codex to respektuje; rób tak samo.
- Zajęcie wisi godzinami bez postępu → pytaj usera, nie przejmuj siłą.

### 2. `C:\Repos\MAPATUR-AGENT-COMMS.md` — kanał wiadomości
- Append-only, wpisy `### C2C-RRRRMMDD-NNN`, pola Od/Do/Typ/Treść. Wiadomość wymagająca reakcji ma
  `ACK wymagany: tak`; potwierdzasz NOWYM wpisem z `ACK: <id>`. **Brak ACK ≠ zgoda.**
- Czytaj: przed każdym cyklem pracy, przed zmianą wspólnego interfejsu, przed zajęciem blokady.
- Zgłaszaj: gotowy merge, zmiany wspólnych formatów, planowane użycie blokady, budżety (VRAM/dysk),
  możliwe konflikty. Zero sekretów.
- Skuteczny nasłuch: pętla w tle na hash pliku (budzi przy zmianie) — ale filtruj po
  `ACK wymagany: tak`, bo Codex commituje/raportuje co kilkanaście minut.

### 3. Podział własności i kolejność (uzgodnione C2C-009/010, AKTUALNE)
- **Claude:** det05, `.opk`, coverage, streaming i helpery ortofoto, `Terrain3DView.xaml.cs` +
  `SetupDet05Streaming` + ortofotowe ścieżki shadera.
- **Codex:** format RMP3, baker geometrii, LOD, `SampleHybridSurface`, RockBake.
- **Mój merge do main JEST WYKONANY (b301603) → teraz Codex robi rebase → dopiero potem jego
  integracja runtime RMP3** (wtedy wspólna strefa: końcowy wybór geometrii i maski materiału
  w rendererze — uzgadniać przez kanał). Czekam na jego `ACK: C2C-20260731-019`.
- Ustalone budżety Codeksa: RMP3 cache 384–512 MiB VRAM (zastępuje pamięć terenu, nie dokłada),
  pełny bake RMP3 dopiero po pilocie z podanym rozmiarem wyniku i wolnym miejscem po operacji.

### 4. Czego nauczyła praktyka (stosuj)
- **Deklaracje weryfikuj plik-po-pliku** (`git diff --name-only merge-base..branch` + część wspólna),
  nie „na słowo" — a swoje własne koryguj wpisem, gdy przestają być prawdziwe (przykład: C2C-010).
- **Statyczny dokument nie kopiuje stanu dynamicznego** (blokada TYLKO w APP-LOCK; handoff nie
  przechowuje „kto zajmuje") — złapane przez Codeksa w C2C-004.
- Dziel się lekcjami architektonicznymi w kanale — lekcja capa 48 (literał vs nazwana stała,
  objaw nieodróżnialny od awarii streamingu) dotyczy wprost jego RMP3.
- Jego wpisy w APP-LOCK bywają wstawiane w środku tabeli (nie zawsze na dole) — czytaj CAŁY dziennik,
  nie tylko ogon.

## ══ KOLEJKA ROBOTY (nic nie wymaga natychmiastowej decyzji usera) ══

1. **Warstwa pośrednia 25 cm (sk25) — NAJBLIŻSZY temat.** Motywacja zmierzona: za pierścieniem det05
   (~3,2 km) strona SK spada do bazy ~1 m/px, bo det25=GUGiK=tylko PL (przy Gierlachu 0/25 kafli).
   Zrobione: fetch kompletny (25 969 kafli, `dem/ortho-detail/tatry/sk25`), katalog znaków
   (`sk25/_watermarks.json`, 1 497 instancji), harmonizator obsługuje `--level sk25`
   (`harmonize-sk-ortho.py`, krata i pitch 4 = 512 m per poziom!). DO ZROBIENIA:
   (a) harmonizacja sk25 (~15 min); (b) naprawa znaków — **adaptacja `repair-zbgis-watermarks.py`**
   (ma zaszyte SRC=sk05-harm i SCALE=5.0; dla sk25 SCALE=1.0, maska bez upscale); (c) **ROZPOZNANIE
   przed integracją: jak det25 bramkuje cele** (czy ma odpowiednik `_coverage_p16.txt`, czy wpięcie
   SK kafli do drzewa det25 + rebake wystarczy — sprawdzić `SetupDet25...`/`Det25ArrLayers` w
   `Terrain3DView.xaml.cs`); (d) bake det25 (pełny trwał 12 min) + sync + werdykt usera.
2. **Sprzątanie ~15,4 GB** — `opk/det25-prerim`+`det1m-prerim` (8,4 GB) i `gpu-cache` (6,8 GB) czekają
   na „kasuj" usera od 07-25. Przypomnieć przy okazji.
3. **Instalka** — dane runtime ~185 GB; realny pakiet ~132 GB, jeśli test „czysta instalacja bez
   katalogu webp det05" przejdzie (runtime streamuje z .opk; sprawdzić, czy setup warstwy nie
   gate'uje się na istnieniu katalogu kafli). Naturalny podział: baza ~25 GB + opcjonalna paczka 5 cm.
4. **Východ-2025 (15 cm)** — sprawdzać REST-em (krok 0 z TILE-PRODUCTION §11); po publikacji rdzeń
   (Rysy/Gierlach/Łomnica, dziś 2022/20 cm) można odświeżyć jednym przebiegiem pipeline'u.
5. **Szew PL|SK z bliska** (werdykt pilota: „razi z bliska") — naprawa = **deshadow strony POLSKIEJ**
   (R4), NIE ruszanie SK; dociąganie SK do GUGiK odrzucone pomiarem (ślepa uliczka). Materiał leży:
   ZBGIS 2024 ma światło w wypalonym cieniu 2021 (za Mnichem 42 vs 19) — zasięg nakładki zmierzony
   w pamięci epiki `ortho-deshadow-luminance-field`.

## Twarde zasady procesu (wyniesione z tej epiki — NIE łamać)

- **Kafle pochodne zapisywać WYŁĄCZNIE lossless** (q90 = druga generacja stratna poza maską, zmierzone 60/60).
- **verify-full po każdej serii przerwań bake'u** — przerwany bake zostawia pakiet z kompletnym TOC
  i śmieciowym ogonem, srcHash uznaje go za zdrowy; verify wypisuje chore pakiety po imieniu.
- **Nie zabijać bake'ów Force'em** bez potrzeby; bake wymaga zamkniętej apki i `--parallel 6`.
- **Czas bake'u liczyć z LICZBY PAKIETÓW** (1,4 s/pakiet), nie z proporcji kafli; **ETA długich
  procesów w GODZINACH CZUWANIA** (komputer śpi w nocy; detektor przerwy = histogram mtime wyników,
  NIE dziennik zasilania Windows).
- **Po poszerzeniu zakresu fetchu harmonizację liczyć OD ZERA** (pole parametrów zależy od zawartości
  dysku; drift zmierzony do 38 lumy na celi).
- **Porównania tonu w górach = IDENTYCZNY footprint** po obu stronach (inaczej mierzysz oświetlenie).
- **Detektory artefaktów kalibrować na znanym A/B przed użyciem** — w tej epice kalibracja odrzuciła
  3 detektory znaków i 2 warianty naprawy, zanim zdążyły zepsuć dane.
- Procesy w tle GINĄ z sesją Claude Code (fetch/bake) — wszystkie nasze są RESUMABLE; po padzie
  wznowić tą samą komendą. Wyjątek: proces uruchomiony przez `dotnet run` potrafi przeżyć jako
  sierota — sprawdzać `Get-Process` przed wnioskiem „padło".

## Narzędzia epiki (wszystkie w `testdata/maps/`, na main)

`fetch-ortho-detail.py` (poziomy sk05/sk25, `--strip-km`, `--dry-run`) · `harmonize-sk-ortho.py`
(`--level sk05|sk25`) · `merge-zbgis-into-partial-det05.py` · `zero-alpha-white-nodata-det05.py` ·
`integrate-sk05-into-det05.py` · `scan-zbgis-watermarks.py` (`--extract/--scan`) ·
`scan-sk05-watermarks.py` · `repair-zbgis-watermarks.py` (`--pilot/--write`) ·
`probe-zbgis-native-res.py` · `probe-zbgis-overlap-color.py` · `build-det05-coverage.py --pitch 16`.

## Rollbacki (wszystko odwracalne bez ponownego fetchu)

surowe `sk05/` + `sk25/` nietknięte · harmonizacja: rerun ~7 h · znaki wodne: `sk05-harm-prewm/` +
`_wm-fixed.txt` · integracja: `det05/_sk-pilot-added.txt` · straddlery/alfa: `det05-premerge/` +
`_sk-merged.txt`/`_white-alpha.txt` · bake: przyrostowy po przywróceniu źródeł.
