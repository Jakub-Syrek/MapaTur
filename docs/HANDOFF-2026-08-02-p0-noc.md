# HANDOFF 2026-08-02 (noc) — P0: apka niezdatna do planowania tras; nowe reguły procesu

**START: przeczytaj `memory/app-unusable-route-planning-p0.md` (ultimatum usera + kryteria
odbioru) i sekcję ŚWIATŁO/CIEŃ/KOLOR w CLAUDE.md. Obowiązują nowe reguły:
`verify-or-ask-never-guess` (żadnych twierdzeń bez świeżego pomiaru; obietnica = najpierw zapis),
`tasks-immutable-done-criteria` (kryteria done niezmienne; kafle = jedna święta recepta).**

## Stan ostry (zweryfikowany ~3:30)

- **Apka NIE DZIAŁA** (żaden proces). User nie wybrał wersji do uruchomienia: build z fixem
  reuse cieni (main `d48d958`) vs wczorajszy `4ecb89f`. NIE uruchamiać bez jego decyzji.
  **`bin\` jest w NIEPEWNEJ rewizji** (w nocy było przełączanie HEAD) — przed startem OBOWIĄZKOWO
  rebuild z wybranej rewizji + weryfikacja daty dll (pułapka stale-exe).
- Worktree: **main** = `d48d958` (LOKALNY, niepushnięty, bramki format+testy NIEZROBIONE).
  `2bc3e88` = zawór `MAPATUR_KILL_SHADOW` — **ODBARWIA scenę (sprzężenie kolor↔cień), nie używać**.
- Przy buildzie z `d48d958` user zgłosił **menu 1 FPS** — przyczyna NIEUSTALONA (punkt
  odniesienia „kiedy działało" nieuzyskany; hipotez nie weryfikowano). NIE zgadywać: albo
  dotnet-trace wątku UI podczas klikania menu, albo A/B buildów `4ecb89f`↔`d48d958` z pomiarem.

## Zmierzone tej nocy (nie powtarzać pomiarów)

- Reuse cieni po sygnaturze sceny (`d48d958`): statycznie shadow 110→0,00 ms, sumGpu 118→8-10 ms,
  kolory OK (autoshot `dev/shadow-fix-proof/shot-20260802-000658-198.png`, Rohacze, sat 0.183).
- Dane orto ZDROWE na całej ścieżce: webp det25 zachód kolorowe; strony BC1 w .opk = źródło
  co do 1 jednostki RGB; taile L2 naturalne (sonda `…scratchpad/opk-color-probe.py`, pip zstandard).
  „Szarość" była sprzężeniem kolor↔cień przy wyłączonym passie (A/B USERA — jego diagnoza).
- Wyciek: ws 18,8→28,6 GB przy lataniu na zachodzie → śmierć procesu bez wyjątku (log 08-01
  23:48). Bisekcja warstw NIEZACZĘTA.
- Pakiety det25 zachodu istnieją (1105, komplet grup przy 49.21,19.75) — rebake niczego nie zgubił.

## Kolejność rundy (kryteria odbioru w memory P0 — wszystkie 3 muszą być zmierzone)

1. Menu 1 FPS: ustalić przyczynę POMIAREM (dotnet-trace UI / A/B buildów). Dopiero potem decyzja
   o losie `d48d958`.
2. Wyciek RAM (bisekcja `MAPATUR_KILL=det1m,det25arr,det05arr,mosaic,baseskin`; obserwować
   ortho far-masters 43→85 i BakedStream).
3. Zachód 20× w pełnym renderze kaskad + docelowo inwariant rozdzielności kolor↔cień.
4. Po zieleni P0: bramki format+testy → push `d48d958`; potem kolejka z
   `HANDOFF-2026-07-31-sk25-wykonane.md` (instalka czeka na odmrożenie RMP3 — decyzja usera).

## Nie ruszać / konteksty

- sk25/det05 ODEBRANE (nie cofać); RMP3 ZAMROŻONE (`rmp3-rocks-takeover`); artefakty Codeksa
  chronione. APP-LOCK/C2C protokoły bez zmian.
- Kierunek strukturalny od usera (po P0): derywować det25/bazę z detalu — jeden ton bez
  harmonizacji; proces kafli = JEDNA recepta (TILE-PRODUCTION), koniec wariacji.
- Koszt: user rozliczył sesję z ~860k tokenów „na nic" — pracować oszczędnie: mniej pętli
  narzędzi, krótkie komunikaty, zero spekulacji.
