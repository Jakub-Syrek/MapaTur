## STAŁE ZASADY MAPATUR — MANDATORY, read FIRST, every session

**[`docs/ZASADY-MAPATUR.md`](docs/ZASADY-MAPATUR.md) — 18 stałych zasad ustanowionych przez użytkownika
(2026-07-23). Obowiązują KAŻDĄ pracę nad MapaTur i KAŻDEGO agenta. Nie wolno ich zmieniać bez wyraźnej
zgody użytkownika.** Sedno: podwójne kryterium sukcesu (wygląd ORAZ płynność, w skompilowanym exe na danych
z AppData, user = ostateczny sędzia wizualny); zmiany odwracalne, mierzone przed/po cold+warm w identycznych
warunkach; zakaz whack-a-mole (spisz niezmienniki przed zmianą); ciężkie przetwarzanie assetów OFFLINE
(runtime nie dekoduje setek WebP, nie generuje mipów przy powrocie kamery); małe jednostki streamingu; nie
blokuj wątku renderu; stały gate odbioru panoramy (bez 10–15 s ostrzenia, bez utraty detalu od ruchu myszy,
bez zacięć 150–300 ms, bez rozmytej większości kadru); testy TYLKO na monitorze DELL P2722H (Iiyama = user).

## ⚠ DWAJ AGENCI NA JEDNEJ MASZYNIE — BLOKADA APKI (zasada 20, obowiązkowa)

Gdy równolegle pracuje więcej niż jeden agent (Claude / Codex, różne gałęzie i worktree), **przed
uruchomieniem `MapaTur.App` albo `OrthoBake`/`RockBake` przeczytaj i zaktualizuj
[`C:\Repos\APP-LOCK.md`](file:///C:/Repos/APP-LOCK.md)** (plik leży POZA repo, bo gałęzie są różne).
Zajmujesz → ustaw ZAJĘTE i dopisz do dziennika. **Po teście ZAMKNIJ apkę i ustaw WOLNE.** Nigdy nie
zamykaj cudzej instancji i nie uruchamiaj drugiej obok. Powód: bake potrzebuje ~8 GB RAM i zamkniętej
apki (inaczej `Unable to allocate pixels`), dwie instancje to 2×8 GB VRAM na karcie 16 GB, a katalog
danych w AppData jest WSPÓLNY — podmiana kafli/`_coverage_p16.txt`/`.opk` w cudzej sesji potrafi
sprawić, że drugiemu agentowi zniknie cała warstwa 5 cm. Pełne brzmienie: `docs/ZASADY-MAPATUR.md` §20.

## Terrain graphics — MANDATORY before baking tiles / touching the terrain pipeline

Before you (re)generate or bake any DEM / ortho / z16 tiles, OR change the terrain load / repair / render
pipeline, **read [`docs/TERRAIN-GRAPHICS-CHECKLIST.md`](docs/TERRAIN-GRAPHICS-CHECKLIST.md) and apply EVERY
relevant item — comprehensively, across ALL render paths at once.** Do not fix one path/symptom and forget
the siblings; that is the recurring failure that makes us re-bake in circles. After any change, run the
checklist's verification (cache audit + visual sweep at multiple spots), not just the one location you were on.

DATA-side tile work (bake / merge / colour correction on DEM or ortho files) follows
[`docs/TILE-PRODUCTION.md`](docs/TILE-PRODUCTION.md) — the reproducible step-by-step pipeline. **Every new
graphics process you run on tile data MUST be documented there immediately** (command, input/output,
numeric verification), so the whole production can be replayed end-to-end.

## Mobile (re)install — MANDATORY: verify the 1 m tile cache is COMPLETE

After EVERY mobile install / reinstall / data restore (anything that could touch the phone's z16 cache),
**verify the phone has the FULL 1 m tile set, not a sparse subset.** A reinstall or a partial package leaves
holes → the terrain renders "oble" (rounded peaks/trails) because most tiles fall back to the coarse base,
even though per-tile detail + budget are fine. Symptom in the on-screen LOD badge / log: `cache-only z16:
requested=144, cached=7` (i.e. ≈7/144) instead of ~full.

Check (Debug build → `run-as` works; adb at `C:\Program Files (x86)\Android\android-sdk\platform-tools`):
```
# phone tile count (PKG = com.companyname.mapatur.app)
adb exec-out run-as PKG sh -c 'find files/dem-cache/gugik/16 -type f | wc -l'
# desktop reference count (the comprehensive set lives here)
find "C:/Users/<user>/AppData/Local/User Name/com.companyname.mapatur.app/Data/dem-cache/gugik/16" -type f | wc -l
```
The phone count MUST match the desktop (e.g. 7338). If the phone is short, push the missing tiles from the
desktop (the bundled package alone is only ~4265 tiles and has gaps over the Orla Perć core):
```
# diff: list both (relative paths under 16/), comm -23 desk phone > missing.txt
tar --force-local -C "<DESK>/dem-cache/gugik/16" -cf missing.tar -T missing.txt   # ~800 MB
adb push missing.tar /data/local/tmp/ && adb shell chmod 644 /data/local/tmp/missing.tar
adb exec-out run-as PKG sh -c 'cd files/dem-cache/gugik/16 && tar -xf /data/local/tmp/missing.tar'
adb shell rm /data/local/tmp/missing.tar
```
NOTE: the `adb exec-out run-as ... tar -xf -` STDIN pipe HANGS — use the push-to-/data/local/tmp + run-as
extract path above (app-uid can read the 644 tmp file). The per-tile build only re-runs on a camera move,
so after pushing, pan the camera to see `cached` jump and the 1 m detail fill in.

## Route „przez Granaty" zamiast Żlebem Kulczyńskiego — recurring, DON'T re-diagnose

If a planned ridge route descends **via Granaty** instead of the **Żleb Kulczyńskiego** (or "enters the żleb and
turns back"), it is a **DATA** regression, not code — and it **recurs after re-downloading trails**. The full
routing fix is already in code (`simplificationEpsilonMeters: 0.0`, `OverpassResponseParser` member-stitching,
`TrailRoutePlanner` snap, `RouteProfile.ShortestDistance`) — **do NOT touch or revert it.** A "Pobierz szlaki"
re-download can drop the żleb's lower connector (Kozia Dolinka → Czarny Staw) → the descent dead-ends → route
goes around via Granaty. **Fix = re-download trails with the WHOLE route area in frame (zoom out so the valley
below the żleb is visible).** Full procedure + 10-second DB diagnostic in
[`docs/TRAIL-ROUTING-ZLEB.md`](docs/TRAIL-ROUTING-ZLEB.md) — read it before spending any time on routing.

## Testing Conventions

### TDD Workflow
- Always write failing tests BEFORE implementation
- Use AAA pattern: Arrange-Act-Assert
- One assertion per test when possible
- Test names describe behavior: "should_return_empty_when_no_items"

### Test-First Rules
- When I ask for a feature, write tests first
- Tests should FAIL initially (no implementation exists)
- Only after tests are written, implement minimal code to pass
