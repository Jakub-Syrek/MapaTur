# HANDOFF 2026-08-14 — task #8 (pełzanie commit GPU): runda falsyfikacji — draws ODPADA, nośnik = czas przemiatania

**START następnej sesji: memory `app-unusable-route-planning-p0` + ten plik. Werdykt taska #9
(zaćmienie, build c609189) NADAL OTWARTY po stronie usera — poranna instancja (PID 3792,
DATE=08-12 TIME=19.6) zamknięta ~09:26 bez werdyktu. main LOKALNY, niepushnięty od 25e7510
(b69cd40 ctx-loss, 9f649d1+c609189 zaćmienie, 2d425cc --cols, commit instrumentacji z tej sesji);
push po werdykcie #9, bramki każdego commitu: build 0/0, testy 1901/1901, format verify.**

## Wynik rundy w jednym akapicie

Hipoteza wiodąca z 08-08 („pełzanie gpuDed ∝ draw calls przez renamy dynamicznych CB w ANGLE")
jest SFALSYFIKOWANA pomiarem: przy tej samej scenie i tym samym tempie orbity (niziny/pogórze,
3°/s) zbicie fps 13,9× i draws/min 3,1× obniżyło nachylenie gpuDed tylko o ~18% (śr. 410→332
MB/min). Nośnik pełzania jest proporcjonalny do CZASU przemiatania kamery, nie do klatek ani
draw calls. Padły też: churn obiektów tekstur (0 zdarzeń/min podczas pełzania) i masowy churn
obiektów buforów (160–207 Gen/min przy płaskim ALIVE — o rzędy za mało na story per-zdarzenie).
NOWA twarda regularność (4/4 biegi): **nachylenie gpuDed ≈ 2,0× wolumen uploadu (mesh+orto)
MB/min** (1,88–2,17×), przy czym shared memory równocześnie MALEJE — treść przechodzi przez
staging do świeżych alokacji dedicated, a stare backingi zostają w cache'u sterownika. UWAGA:
mnożnik 2× NIE ekstrapoluje na reżim F9 (mesh 5,7 GB/min → zmierzone +789 MB/min, nie +11 GB)
— zależność jest reżimowa, mechanizm do rozstrzygnięcia śledzeniem alokacji, nie kolejnym env.

## Pomiary (CSV: dev/t8-draws; okno werdyktu = 2. połowa orbity, uptime 300..510 s)

| bieg | czapa | fps śr. | draws/min | genBuf/min | genTex/min | upload MB/min* | gpuDed MB/min | ws MB/min |
|---|---|---|---|---|---|---|---|---|
| U  (1922) | — | 300,8 | 1 081 718 | — | — | 197 | **+397** | +5 |
| U3 (1947) | — | 304,6 | 1 094 574 | 207 | 0 | 195 | **+424** | +7 |
| C  (1932) | 33 ms | 22,0 | 352 514 | — | — | 171 | **+331** | −24 |
| C3 (1956) | 33 ms | 21,7 | 355 168 | 160 | 0 | 177 | **+333** | −18 |

*upload = Δ(upMeshMB+upOrthoMB)/min z liczników GlTrack; upMask=0 na tej scenie.
Scena: skok `MAPATUR_JUMPS` na 51.0,20.0 → clamp do pogórza ~13,9 km na płn. (bez warstw
detalu — wierna replika LOWLAND z 08-08), orbita `MAPATUR_ORBIT=90:420:3`. Replikacja par
wzorowa (397/424 i 331/333). ws płaski w obu wariantach — ring PBO trzyma (P0 nie wraca).

## Co ta runda dodała do kodu (wszystko za bramkami 0/0 + 1901/1901)

- **Licznik draws w status.json** (`GlTrack.CountDraw()` nad każdym z 30 miejsc draw w
  `Terrain3DGlRenderer`), pilnowany testem źródła `DrawCallInstrumentationTests` — każde NOWE
  miejsce draw bez licznika wywala test (ta sama topologia ryzyka co literał capa 48).
- **Liczniki zdarzeń** `genTex/delTex/genBuf/delBuf` (skumulowane) w GlTrack/status.json —
  ALIVE nie widzi churnu przy Gen≈Delete.
- **Czapa `MAPATUR_FRAME_MS` obejmuje orbitę harnessu** (guard w `ServiceHarnessOrbit` — bez
  niego orbita self-invalidowała się do ~300 fps i LOWFPS 08-08 był NIEWAŻNY; tempo obrotu
  liczone z czasu rzeczywistego, więc czapa nie zmienia przemiatania).
- Narzędzia: `scripts/bench-t8-draws.ps1` (wariant U/C, samosprzątający się w <10 min),
  `dev/t8-draws/analyze-t8.py`, sampler rozszerzony o kolumny draws+zdarzenia.

## OTWARTE po tej rundzie (kolejność wg siły)

1. **Mechanizm 2× uploadu**: rozstrzygnąć NA POZIOMIE ALOKACJI, nie env — D3D11 debug layer /
   DXGI budget / ETW VidMm (kierunek (b) z kolejki 08-08). Pytanie: czym są rosnące committed
   alokacje dedicated (staging ANGLE? backingi BufferData trzymane przez cache sterownika przy
   niedopasowaniu rozmiarów?), czemu shared przy tym maleje i czemu mnożnik łamie się w F9.
2. **Zagadka draws/klatkę 60 (U) vs 267 (C)** — przy czapie renderer wydaje ~4,5× więcej draws
   na klatkę (podejrzenie: bramki reuse cieni/odbicia od sygnatury kamery; sygnatura = RÓWNOŚĆ
   pozycji, więc każdy ruch je unieważnia — ale czemu więc U ich nie wydaje?). Wyjaśnić per-pass
   licznikami draws zanim draws posłuży za metrykę w innym benchu.
3. **Realna szkodliwość** (pytanie z 08-08 wciąż bez odpowiedzi): committed to nie physical;
   CRIT30 (28 min) nie wykazał szkody użytkowej. Zanim jakikolwiek refactor (batching/UBO —
   dziś NIEUZASADNIONY, bo draws to ~10% nachylenia): test 30–60 min ruchu w Tatrach — plateau
   czy pełzanie do stutteru?
4. Kandydat (a) z 08-08 (cap puli kafli 4→8 GB) — nadal niezmierzony; po tej rundzie słabszy
   (zdarzeń mało), ale spójny z „⅓ cięcia" z B5.

## Nie ruszać / konteksty

- Werdykt #9 = user; po nim push całości (5 commitów) jedną bramką.
- dev/t8-draws — dowody pomiarowe, nie kasować (jak dev/p0-pooling).
- Rekonstrukcja §15 TILE-PRODUCTION (Rohacze; na gałęzi była §14) — działa w OSOBNEJ sesji usera (chip); nie dublować.
- sk25/det05 ODEBRANE, RMP3 ZAMROŻONE — bez zmian.
