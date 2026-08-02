# HANDOFF 2026-08-02 (dzień) — P0 zmierzone: menu OK na świeżo, wyciek = osad sterownika; harness bez UI

**START następnej sesji: memory `app-unusable-route-planning-p0` + PROTOKÓŁ TESTÓW
`app-test-harness-gaps` (status.json, zero myszki — twarde żądanie usera, patrz
`ui-tests-via-programmatic-hooks`). main = `11236a2` (LOKALNY, niepushnięty; bramki: testy
Application 1769/1769 zielone, format na zmienionych plikach zrobiony).**

## Nowe narzędzia (ZACOMMITOWANE, używać zamiast klikania)

- `%TEMP%\mapatur-status.json` co 2 s (pisany z wątku tła — przeżywa zwis UI): pid, uptimeSec,
  `uiBeatAgeMs` (>1000 = UI wisi), `uiWorstLagMs60s`, renderFps, activeSection, heapMB, wsMB,
  gen2, `glTex/glBuf/glVao/glFbo/glRbo` (żywe zasoby GL z GlTrack), warning/errorCount + 5
  ostatnich ostrzeżeń. Sprawdzanie „czy apka stoi/co z pamięcią/jakie błędy" = JEDEN odczyt pliku.
- `MAPATUR_UI_SCRIPT="90:6,105:0"` — sekcje menu programowo (SelectSectionCommand, ta sama
  ścieżka co chipy); `[UiScript]`/`[UiBeat]` w logu. `MAPATUR_MAXIMIZE=1` — pełny ekran od startu.
- `MAPATUR_GL_FINISH_SEC=n` — wymuszony gl.Finish co n s (diagnostyka osadu sterownika).
- Sampler krzywej pamięci: pętla PS czytająca status.json → CSV (przykłady: `dev/p0-morning/*.csv`).
- INCYDENTY rano (nie powtórzyć): `open_application` odpalił DRUGĄ instancję (memory
  `computer-use-never-open-application`); klikanie myszą w UI zakazane na stałe.

## Zmierzone dziś (liczby w dev/p0-morning/, nie powtarzać)

1. **Menu (task #1):** świeża sesja, zachód statycznie (poza usera z nocy), build z reuse cieni:
   4 przełączenia sekcji skryptem — ZERO lagów UI >150 ms poza autoshotami (autoshot = ~270-400 ms
   stall na zrzut PNG, wyłączać przy pomiarach płynności). Menu NIE jest zepsute przez d48d958.
   „Nieklikalność" u usera = najpewniej stan sesji po wycieku (swap przy ws→30 GB). Domknięcie
   task #1 = po naprawie wycieku zmierzyć menu po 30 min lotu.
   Uwaga osobna: renderFps ~9-10 przy statycznej kamerze (sumGpu ~8 ms) — pętla invalidate-driven;
   w locie 30-60 FPS. Czy 10 FPS statycznie ogranicza płynność animacji menu — NIEZBADANE.
2. **Kolory zachodu (kryterium P0 #2):** autoshot Rohaczy przy WŁĄCZONYCH cieniach:
   meanR=70,2 meanG=78,4 meanB=73,5, sat med 0.157 / mean 0.229 → G>R ✓, sat ≥0.12 ✓.
3. **Wyciek (task #2) — repro + atrybucja, ROOT-CAUSE OTWARTY:**
   - Bench F9 8 runów: ws end/run 20,5→22,8→24,0→23,8→24,3→26,5→26,9→28,1 GB (szczyt 29,96 —
     strefa nocnej śmierci). **+~1 GB na IDENTYCZNY lot.** Heap zarządzany stały (max 11,5-13 GB)
     ⇒ wyciek natywny. Statycznie (30 min): ws stały ~11,3 GB ⇒ pompuje TYLKO ruch kamery.
   - **Commit D3D per proces (licznik `GPU Process Memory\Dedicated Usage`): 8,4 GB (baseline po
     starcie) → 16,2-16,9 GB na karcie 16 GB** — spillover do RAM = rosnący ws; nocny „OOM/TDR-kill
     bez wyjątku" pasuje.
   - WYKLUCZONE pomiARAMI: warstwy `MAPATUR_KILL=det1m,det25arr,det05arr` (rośnie tak samo),
     `mosaic,baseskin` (rośnie; UWAGA: KILL bramkuje TYLKO rysowanie, nie streaming!),
     `MAPATUR_KILL_SHADOW=1` (rośnie), maski szlaków/wody (fix TexStorage2D wdrożony — krzywa
     bez zmian), nasze obiekty GL (GlTrack: **glTex=27 STAŁE**, glBuf 5,6-9,8k fluktuacja z
     rotacją kafli — bilans zdrowy), fence'y sterownika (gl.Finish co 2 s — krzywa bez zmian).
   - Audyt kodu (workflow 15 agentów): create/release par w rendererze SPARowane; kandydaci
     odrzuceni z dowodami (rotacja kafli zwalnia bufory, Reclaim ortho co klatkę, context-lost
     nigdy nie odpalił — 0 trafień w logach, Skia compose z using).
   - ⇒ **Osad w pulach ANGLE/D3D11 albo sterownika NVIDIA** przy ogromnym wolumenie
     tworzenia/kasowania różnorozmiarowych zasobów w locie (~7k żywych VBO w rotacji, tekstury
     ortho tier-change, TexSubImage3D do tablic det). Kierunki następnej rundy:
     (a) pooling/reuse buforów mesh kafli (stałe rozmiary klas wielkości zamiast Gen/Delete
     tysięcy unikatów), (b) analogicznie staging tekstur ortho (jedna staging para na cel),
     (c) test na czystym ANGLE env (MAPATUR bez NVIDIA overlay?), (d) D3D11 debug layer /
     DXGI budget log. Wolumen da się teraz mierzyć TANIO: GlTrack + dedicated counter.
4. **Fix masek (wdrożony, zostaje):** TexImage2D-respecyfikacja masek szlaków/wody
   (~107 MB/rebuild co 500 m lotu, ~37-163 rebuildów/sesję) → immutable TexStorage2D +
   TexSubImage2D. Churn alokacji -~17 GB/sesję. Wizualnie NIEODEBRANE przez usera (ta sama
   treść, ta sama ścieżka próbkowania — ale sweep wizualny szlaków/wody przy okazji następnej
   sesji z apką obowiązuje).

## Stan tasków rundy

1. Menu 1 FPS — zmierzone na świeżo (czyste), domknięcie po naprawie wycieku (30-min test).
2. Wyciek — repro + charakterystyka + wykluczenia GOTOWE; root-cause w sterowniku/ANGLE OTWARTY.
3. Zachód 20× pełny render kaskad + inwariant kolor↔cień — NIERUSZONE.
4. Push main — czeka na zieleń P0 (bez zmian).

## Nie ruszać / konteksty

- sk25/det05 ODEBRANE; RMP3 ZAMROŻONE; artefakty Codeksa chronione; APP-LOCK/C2C bez zmian
  (dziennik APP-LOCK uzupełniony o dzisiejsze okno + incydent 2 instancji).
- `dev/p0-morning/` — krzywe pomiarowe CSV + autoshoty (dowody dnia, nie kasować).
- Kierunek strukturalny usera (po P0): derywacja det25/bazy z detalu — bez zmian.
