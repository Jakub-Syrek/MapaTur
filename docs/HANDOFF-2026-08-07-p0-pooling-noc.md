# HANDOFF 2026-08-07 (noc) — P0: spirala śmierci ws PRZERWANA; commit GPU wciąż rośnie; regresja cieni domknięta

**START następnej sesji: memory `app-unusable-route-planning-p0` + ten plik. main = `6262f73`
(LOKALNY, 7 commitów niepushniętych od 2afbaa4; bramki każdego: build 0/0, testy Application
1844/1844, format). Push po werdykcie usera na wizual + decyzji o gpuDed.**

## Wynik nocy w jednym akapicie

Wyciek pamięci PROCESU (ws +1 GB/lot → śmierć ~30 GB) jest naprawiony: winowajcą był wolumen
klienckich uploadów tekstur (maski ~4 GB/lot, baza orto, det25) — po przekierowaniu ich przez
fence'owany ring PBO ws jest PŁASKI przez 8 lotów F9 (trzy przebiegi z rzędu). Regresja cieni
zgłoszona przez usera („płasko") domknięta: pula jednostek mesh wyłączona domyślnie + naprawiony
przedpotopowy bug podwójnego Return w MeshBufferPool; sweep wizualny 4 rejonów czysty. OTWARTE:
commit GPU (licznik `GPU Process Memory\Dedicated Usage`) nadal rośnie w locie ~+0,5–0,8 GB/min
niezależnie od wszystkiego, co wyłączaliśmy — to pierwotna choroba z 08-02, teraz bez lustra w ws.

## Pomiary (CSV: dev/p0-pooling; zrzuty: dev/p0-shadow-bisect; wszystkie na tym samym exe, env A/B)

| bench | build | konfiguracja | ws koniec | ws nachylenie | gpuDed koniec | gpuDed nachyl. | pboWaits | hit/miss |
|---|---|---|---|---|---|---|---|---|
| A  | 2afbaa4 | GL_POOL=0 (legacy)        | 32,1 GB | +0,9 GB/lot | ~16,0 szczyt | rośnie | 0 | — |
| B  | 2afbaa4 | pooling v1                | 31,4 GB | +0,9 GB/lot | 16,5 szczyt | rośnie | 54 | 27,6% |
| B2 | 524edcd | pooling v2 (1 wymiar klasy, cap 4 GB) | 36,0 GB | +0,9 GB/lot | 18,1 szczyt | rośnie | 39 | 75% |
| B3 | 40b265d | tiles OFF + PBO routing (maski/orto/det25) | 26,7 GB | **PŁASKI** | 19,2 | +789 MB/min* | 901 | — |
| B4 | 6262f73 | + ring 12 + skan slotów   | 26,8 GB | **−28 MB/min** | 23,2 | +789 MB/min | **4** | — |
| B5 | 6262f73 | + MAPATUR_GL_POOL_TILES=1 | 31,1 GB | +43 MB/min | 18,7 | **+527 MB/min** | 23 | 81,6% |

*nachylenia = regresja liniowa 2. połowy przebiegu. Wnioski twarde: (1) churn buforów mesh
SFALSYFIKOWANY jako źródło wycieku ws (A≈B≈B2 mimo skrajnie różnego wolumenu Gen/Delete);
(2) klienckie uploady tekstur = źródło ws (B3: routing przez PBO ⇒ ws płaski); (3) orphany PBO
zabite skanem slotów (901→4) — bez wpływu na gpuDed; (4) pula kafli tnie nachylenie gpuDed o ⅓,
ale przy capie 4 GB żyje w cyklu evict→miss (+230 missów/min stale).

## Regresja cieni usera („rozjebany cień, jest płasko") — DOMKNIĘTA (werdykt wizualny usera otwarty)

Polowanie 4-tropowe (workflow, wyniki w tasks/w9lhv1ktu.output): pass cieni OCZYSZCZONY (depth-only
z aPos, IndexCount, sygnatura bez zmian semantyki), ring PBO OCZYSZCZONY (7/7 call site'ów
natychmiastowa konsumpcja; korupcja dawałaby złe KOLORY, nie płaskość). Nośnik: (a) pula jednostek
mesh — zatrute normalne krążą godzinami, lambert=0 ⇒ kaskady CSM mnożą zero (pass „działa", obraz
płaski); (b) przedpotopowy (od bae64e7) hazard: ReturnBuffersToPool bez idempotencji + Push bez
detekcji duplikatów ⇒ jedna tablica u DWÓCH najemców (wyzwalacz: context-loss przy resize/maximize
→ masowy re-upload). FIX `40b265d`: pula kafli za opt-in `MAPATUR_GL_POOL_TILES=1`; Return
idempotentny + guard duplikatów (TDD); throttled warning `[GL3D] re-upload kafla ze ZWRÓCONYCH
tablic` — jak się pojawia w logu usera, to jest context-loss re-upload (ścieżka do naprawy niżej).
F9×8 NIE reprodukuje objawu (43 autoshoty, kontrast faz stabilny między okrążeniami). Sweep 4
rejonów (Rohacze/Kasprowy/Gierlach/wschód) na buildzie fix: czysty (dev/p0-shadow-bisect/sweep-fix).

## Env (wszystko od buildu 145fbea+)

- `MAPATUR_GL_POOL=0` — pełne legacy end-to-end (pule + fence'y + routing PBO).
- `MAPATUR_GL_POOL_DISABLE=pbo,staging,lines` — wyłączanie podsystemów (pbo = też routing B3).
- `MAPATUR_GL_POOL_TILES=1` — opt-in puli jednostek mesh (domyślnie OFF po regresji).
- Telemetria status.json: `glPoolMB/glPoolHit/glPoolMiss/pboWaits`, `glVboMB` (zeruje się przy
  context-loss). Sampler+bench: `scripts/bench-mem-sampler.ps1`, `bench-p0-pooling.ps1`,
  `bench-p0-shadow.ps1` (pwsh, NIE powershell 5.1 — parser padał na UTF-8 bez BOM).

## DOPISEK 08-08 rano — runda „commit GPU" (zlecona przez usera): 3 falsyfikacje, pole zawężone

Benche T1/T1-retry/LOWLAND (dev/p0-pooling, build 5a460e6/c77cd97 z licznikami uploadu per
konsument w status.json — upDet05MB/upDet25MB/upOrthoMB/upMaskMB/upMeshMB):
1. **IDXGIDevice3::Trim SFALSYFIKOWANY**: serwis DxgiDriverTrim (EGL→ID3D11Device ANGLE→QI;
   UWAGA: IID z pamięci był błędny — E_NOINTERFACE; poprawny z dxgi1_3.h:2258 =
   6007896c-3244-4afd-bf18-a6d3beda5023). 30 wywołań Trim co 30 s → nachylenie gpuDed
   +911 MB/min (baseline B4 bez Trim: +789) — zero efektu. Kadencja env: MAPATUR_DXGI_TRIM_SEC
   (domyślnie 30; do rozważenia wyłączenie skoro nie działa).
2. **Wolumeny zmierzone (F9)**: mesh ~5,7 GB/min ≫ det05 ~2,1 ≫ maski ~1,5 ≫ det25 0,5 ≫ orto 0,2.
3. **LOWLAND (orbita 51.0,20.0 — bez warstw detalu) SFALSYFIKOWAŁ hipotezę wolumenu**: uploady
   ~zero (mesh 60 MB/min, det05/maski +0), ws płaski 10,6 GB — a gpuDed dalej +336 MB/min.
   Pełzanie NIE zależy od uploadów ani alokacji buforów/tekstur (liczniki GlTrack stałe).
4. **Hipoteza wiodąca po eliminacjach**: residuum ∝ liczbie DRAW CALLS / aktualizacji uniformów —
   ANGLE tłumaczy glUniform na dynamiczne constant buffery D3D11 (MAP_DISCARD → rename backing
   w sterowniku per draw). Pasuje do F9 (~800 przy dziesiątkach tys. draws/min) i nizin (336 przy
   podobnym rzędzie draws/min z 300+ fps), do stałych liczników i do głuchoty na Trim.
   Kierunki weryfikacji następnej rundy: (a) korelacja slope↔draws/min (licznik draw calls do
   status.json — trywialny); (b) ANGLE feature-flags dot. buffer pool/discard; (c) test z czapą
   FPS (timer 16→33 ms wstecz LUB vsync) — niziny przy 30 fps powinny zjechać ~10×, jeśli teoria
   dobra; (d) redukcja per-draw uniformów w passie terenu (batching).
5. **Pytanie o realną szkodliwość**: nocny (przypadkowo wielogodzinny) przebieg przeżył sen
   maszyny + poranne loty; fps zdrowe, ws stabilny — commit to licznik COMMITTED, nie physical.
   Zanim polowanie dalej: test 30 min + menu po lotach (kryterium taska #1) rozstrzygnie, czy
   pełzanie w ogóle szkodzi użytkowo w realnym horyzoncie sesji.

## DOPISEK 08-08 ~10:00 — macierz domknięta + CRIT30: kryteria użytkowe SPEŁNIONE

LOWFPS (niziny @21 fps): **statycznie gpuDed −0 MB/min (IDEALNIE płasko)**, orbita +649 (tryb
continuous samo-invaliduje — fps 296 mimo FRAME_MS=33; env MAPATUR_FRAME_MS działa na timer
główny, nie na pętle walk/dragon/orbit). WNIOSEK MACIERZY (8 przebiegów): wzrost commitu WYŁĄCZNIE
przy ruchu kamery — ANGLE dedupuje niezmienione uniformy (statycznie zero CB-updates), przy ruchu
brudne uniformy → MAP_DISCARD → rename backingów w sterowniku. Task #8 utworzony (batching/UBO).

CRIT30 (16 lotów F9, 28,6 min, bench-CRIT30-0808-0917.csv): **ws 23,4→25,2 GB (+32 MB/min ≈ szum),
menu po 28 min lotu: 47 ms i 0 ms (zero wisów), proces przeżył, fps 21–70**. Commit GPU: +0,9 GB/min
bez plateau, koniec 31 GB committed — bez żadnej szkody użytkowej w oknie testu; przy staniu wzrost
staje. Kryteria użytkowe taska #1 SPEŁNIONE pomiarem; sub-kryterium „commit D3D stabilny" NIE —
przeniesione do taska #8 z zawężonym mechanizmem. Werdykt zamknięcia P0 = user.

## Kolejka następnej sesji (P0 wciąż otwarte przez gpuDed)

1. **Werdykt wizualny usera** na buildzie `6262f73` (normalne użycie; degradacja wymagała
   context-loss — patrzeć na warning w logu).
2. **gpuDed root-cause** — kandydaci wg siły: (a) cache zwolnionych alokacji sterownika przy
   pozostałym churnie (B5: miss-y z eksmisji puli — podnieść cap 4→8 GB ALBO trim powolny i
   zmierzyć, czy nachylenie →0); (b) DXGI budget log / D3D11 debug layer (kierunek z 08-02);
   (c) czysty test env ANGLE. UWAGA: „Dedicated Usage" 19–23 GB na karcie 16 GB = committed,
   nie physical — realna szkodliwość do oceny testem 30–60 min (czy plateau, czy pełznie w
   nieskończoność + czy wraca stutter).
3. **Context-loss re-upload** (przedpotopowy): właściwy fix = rebuild meshy przez streaming
   zamiast re-uploadu ze zwróconych tablic (renderer→VM sygnał po utracie kontekstu).
4. Test 30 min + menu po lotach (kryterium taska #1) → werdykt usera → push main.
5. Za P0: kolejka zatwierdzona (cienie statyczna kamera → efemeryda NOAA → mgła → niebo H-W →
   ambient SH) + task #7 (tryb nagrywania 4:5/9:16 — zatwierdzony „1 2 3").

## Nie ruszać / konteksty

- sk25/det05 ODEBRANE, RMP3 ZAMROŻONE, artefakty Codeksa chronione — bez zmian.
- dev/p0-pooling, dev/p0-shadow-bisect — dowody pomiarowe nocy, nie kasować.
- Lekcja procesowa nocy: po KAŻDEJ zmianie w torze renderu sweep wizualny PRZED oddaniem
  (raz pominięty = regresja u usera); duplikaty w pulach wykrywać na wejściu, nie objawowo.
