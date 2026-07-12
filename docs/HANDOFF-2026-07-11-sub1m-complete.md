# Handoff — 2026-07-10/11: EPIC SUB-1M DOMKNIĘTY (z17 realne + z18/z19 syntetyczne) + pakiet perf

> Branch: **`feat/walk-mode`** (NIC nie commitowane — całość w working tree, decyzja usera kiedy).
> Bramki przed pushem: `dotnet format MapaTur.slnx --verify-no-changes` + pełne testy (stan na koniec sesji:
> **1475+148+144 = 1767/1767 zielone**, format czysty na wszystkich zmienianych plikach).
> **NIGDY Claude jako autor/co-author.** Plan epicu: `docs/PLAN-sub-1m-geometry.md`; produkcja danych:
> `docs/TILE-PRODUCTION.md` §1.3, §2.4, §2.5.

## Werdykty usera
- Faza A (z17): „dobrze wygląda" (10.07 wieczór).
- Artefakty (piszczałki/strukturka/kurtyna): naprawione, **„geometria wygląda dobrze, nie widzę błędów"** (11.07).
- Perf: „dużo lepiej", zostało „leciutko rwie" (zdiagnozowane — patrz OTWARTE).

## Co weszło (chronologicznie, wszystko na feat/walk-mode)

### Dane (odtwarzalne wg TILE-PRODUCTION)
1. **Sonda z17** (`Z17ProbeRunner`, §1.3): GO — mediana self_dec ~0.73 m na skale; przy okazji odkryta
   rejestracja pixel-centre WCS (czytamy node — rozjazd pół komórki, świadomie odroczony).
2. **Download z17 PL**: `Z17DownloadRunner` (`MAPATUR_DOWNLOAD_Z17=1`) — finalnie **8 029 kafli 512 px**
   (supersampling ×2, patrz niżej); surowe bajty WCS w cache (`{y}_512.tif`), NIC nie trzeba re-fetchować.
3. **SK z17**: `bake-sk-dmr5-tiles.py --zoom 17` → **18 673 kafli** DMR5 (pixel-centre jak GUGiK);
   ⚠️ lekcja −999-slabów (bramka `<−900`). Razem cache z17 = **25 312 tifów**.
4. **Bake `[17]`**: `MAPATUR_BAKE_ZOOMS=17` + `MAPATUR_BAKE_ZEROSTRIP=48` (audyt pasów: `audit-dem-tile-strips.py`,
   638 runów 25–96 kom.) + `MAPATUR_BAKE_DEALIAS=1` (wariant 3 usera: Gaussian σ0.5 + bramka ścian >54°→σ1.6;
   `DemRasterDealias`, TDD 6/6; ⚠️ skala komórki MUSI być stałą regionu — bounds okna łamały bit-identity szwów).
   25 312/25 312 .bdt, szwy bit-exact, weryfikacja tif→bdt liczbowa.
5. **Fix źródła — supersampling sub-natywny** (`GugikNmtDemTileSource`): z≥17 fetch 512 px + własny
   `LowPassDownsample`; ⚠️ **fallback odczytu do legacy `{y}.tif`** (chroni SK DMR5 przed osieroceniem — §B1);
   ⚠️ **maska zero-void ≤0.5→sentinel PRZED Gaussianem, restore 0 PO** (bez niej smar 100–900 m zabijał
   naprawę pasów — regresja 11.07 rano, kurtyny kolców na granicy pokrycia). Finalne metryki plecionki:
   Kozia 0.141→**0.053**, ściana Mylnych 0.167→**0.068**, pas dropoutu →**0.014**, SK 0.021 (nietknięte).

### Aplikacja (desktop; telefon nietknięty — maxZoom 16)
6. **Streaming z13→z19**: `BakedStreamMaxZoom=19` desktop; ring per poziom (`RingRadiusOverrideMeters`
   {17:700, 18:350, 19:130 m} + clamp monotoniczny); `surfaceOwnershipMinZoom=16` (anty-„lotnisko" przy
   rzadkim pokryciu fine — klasa §0); skirty zakotwiczone na realnym z17.
7. **Wirtualne kafle z18/z19** (`VirtualDemTileSynthesizer`, TDD 9/9): CR-upsample z17
   (`DemRasterResampler`, TDD 7/7) + displacement o ZMIERZONEJ amplitudzie (krzywizna rodzica; cap
   0.35/0.15 m); szum = value-noise na globalnej kracie int (hash SplitMix64), **krata OBRÓCONA**
   (anty-„równomierny groszek"); amplituda=0 na krawędzi rodzica ⇒ szwy bit-exact; NoData propagowane;
   `DetailRms=0` (anty-podwójny-bump). Sampler stóp/floora dzieli TĘ SAMĄ syntezę (stopy = render).
8. **Pakiet perf** (werdykt „dużo lepiej"): ringi wirtualne eye-only (`EyeAnchoredRingMinZoom` — dryf
   look-at mielił 24 kafle/s), bramka prędkości >25 m/update z histerezą 8 (`fastMotionSuppressMinZoom`),
   RAM-cache syntez 1.5 GB, budżet BAJTOWY uploadów 8 MB/klatkę (ms-budżet mierzył tani CPU-call),
   **nieblokujący sampler wysokości** (`AsyncWarmingTileLoader` + retry-null: koniec z odczytem .bdt /
   syntezą na wątku klatki — gapy 170–320 ms przy BEZCZYNNYM CPU/GPU, trafna diagnoza usera).
9. Drobne: wiatr smoka `BedWindGain` 0.5→0.3.

## OTWARTE (kolejność wg wartości)
1. **Burza alokacji ~450 MB/s przy przelocie** → gen2 GC = gap ~340 ms („leciutko rwie"). Podejrzani
   NAZWANI: nie-poolowane `indexList` (~1.5 MB/kafel, BuildBlock) + staging uploadu (~3 MB/kafel,
   UploadTile). Robota Z PROFILEREM, nie strzał.
2. **Crash AV 0xc0000005 w coreclr** (11.07 15:18, 1×): kontekst = masowe ewikcje w locie; wykluczone
   use-after-return puli i thread-safety cache. **LocalDumps WŁĄCZONE** (HKLM, full, max 3,
   `%LOCALAPPDATA%\CrashDumps`) — przy nawrocie analizować DUMP.
3. **Fire-freeze ~2 s przy zianiu** — prawdopodobnie zdjęty przez nieblokujący sampler; ZWERYFIKOWAĆ.
4. Duch rejestracji pixel/node na przejściu ringów (~0.4 m na grani) — obserwować; fix globalny = osobna
   świadoma decyzja (PLAN §Faza A).
5. Siadanie szlaków/linii na powierzchni z17 (punch-lista #2) — sprawdzić przy okazji.
6. **Prerenderowane filmy** („Renderuj film") — zlecony epic, spec w ROADMAP §M12; brać po perf.
7. Bramka „z19 tylko walk" (v1: ring 130 m zawsze) i ewentualny margin-stitch syntez — jeśli werdykty
   kiedyś wskażą.

## Twarde lekcje sesji (już w TILE-PRODUCTION/pamięci — tu skrót)
- **zeroStripMaxCells i inne progi rastrowe bywają w KOMÓRKACH** — finer zoom = przeskaluj albo audytuj.
- **Każdy nowy krok przetwarzania rastrów GUGiK musi jawnie obsłużyć flat-0 ZANIM cokolwiek uśredni**
  (zero-void lesson, edycja supersamplingowa).
- **Bit-identity szwów nie przeżywa ŻADNEJ per-okno arytmetyki** — stałe skalarne liczyć raz na region.
- **Metryki cross-fetch są skażone rejestracją/dryfem** — decyzje z metryk SELF-konsystentnych (sonda §1.3).
- **Gapy przy bezczynnym CPU/GPU = wątek klatki CZEKA** (I/O/lock), nie liczy — profiluj czekanie, nie kod.