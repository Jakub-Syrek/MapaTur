# HANDOFF 2026-08-02 (wieczór) — ARCHEOLOGIA 44 HANDOFFÓW: co zgubiliśmy i co wraca do planu

**Powstał na polecenie usera: „przeleć teraz wszystkie handoffy, zweryfikuj co zostało pominięte,
zapomniane, opuszczone, i wygeneruj handoff najbardziej pełny tak by zgubione rzeczy wróciły do planu".**

Metoda: 44 handoffy + 8 planów przeczytane równolegle (5 agentów), **214 pozycji** wyłowionych,
z tego 27 zweryfikowanych **w kodzie i historii gita** (nie w dokumentach — te bywają nieaktualne).
Pełna lista z cytatami: `dev/handoff-archeologia.json`. Weryfikacja pozostałych 187 pozycji
przerwana limitem sesji — **te są oznaczone jako NIEZWERYFIKOWANE i wymagają sprawdzenia przed
podjęciem**, bo część może być już zrobiona.

Rozkład: 109 zadań otwartych, 50 długów technicznych, 22 werdykty usera, 17 zamrożonych,
12 obietnic „wrócimy", 4 regresje. Wagą wysoką oznaczono 40.

---

## 0. META-PROBLEM: mechanizm gubienia (najważniejsza pozycja tego handoffu)

Archeologia wykazała **czwarty przypadek tego samego wzorca**: cel ustalony z userem zawęża się
w kolejnych sesjach, bo następna sesja kopiuje parametr z poprzedniej recepty zamiast z celu.
Udokumentowane przypadki:
1. **Zakres 5 cm**: cel „całe Tatry", recepty §11/§12 miały krawędź 19.80 ⇒ Tatry Zachodnie nigdy
   nie pobrane (user: „od samego początku miała być całość. zgubiłeś to pomiędzy sesjami").
2. **Derywacja warstw zgrubnych z detalu**: user powtarzał „po raz 50", nigdzie nie było zapisane.
3. **Wybór rocznika nalotu przed fetchem**: było w `PLAN-sk-det05-zbgis.md` §1, nie zostało wykonane.
4. **22 werdykty usera bez realizacji** (sekcja 4 poniżej) — najstarszy z 20 czerwca.

**Przeciwdziałanie wdrożone dziś:** `docs/TILE-PRODUCTION.md` §0-A i §0-B (zasady nadrzędne PRZED
recepturami, commity 7c636d3 / 98c8309 / 6745762). **Do zrobienia:** ten sam wzorzec dla reszty
projektu — każdy plan ma zaczynać się od celu i różnicy do stanu, nie od parametrów.

---

## 1. STAN NA DZIŚ (2026-08-02, wieczór)

- **main = `cd7be9c`, 20+ commitów NIEPUSHNIĘTYCH.** Push zablokowany warunkiem „zieleń P0",
  który nie ma daty ani kryterium wyjścia — patrz poz. 3.6.
- **LECI W TLE: pobór 5 cm dla Słowacji** (`sk05`, ZBGIS, region C, PID 11368,
  log `dev/fetch-logs/sk05-regionC.log`). To priorytet usera. ETA ~9 h od 21:00.
  Nalot wybrany świadomie: aktualna mozaika = 2024-07-31 na zachodzie (najmniej cienia, 15 cm).
- Zrobione dziś: harness testowy bez UI (status.json + hooki), per-kaskadowe odświeżanie cieni,
  streaming zdjęty z wątku UI, mapa cienia det05, narzędzie deshadow (NIC nie zapisane — czeka
  na werdykt), oddawanie cel det05 po opuszczonym rejonie.
- APP-LOCK: ZAJĘTE przez fetch (apka i GPU WOLNE — można testować).

---

## 2. ZWERYFIKOWANE W KODZIE JAKO NADAL OTWARTE (dowody, gotowe pierwsze kroki)

Te 21 pozycji ma potwierdzenie w kodzie/historii — nie trzeba ich sprawdzać ponownie.

| # | temat | stan faktyczny | pierwszy krok |
|---|---|---|---|
| 2.1 | **Kolejka linowa: gondole nie przechylają się wzdłuż liny** | kod nietknięty od 06-17 | czerwony test `CableCarGeometry.CabinBodyEnds` |
| 2.2 | **Scroll paneli — zrobiony POŁOWICZNIE** | 5 `ScrollView`, ale PanelPogoda ma `VerticalOptions="Start"` | `MapPage.xaml:518` → `Fill`, wzorzec z PanelWidok |
| 2.3 | **Reorder przystanków tylko drag** | `CanReorderItems=True`, brak przycisków ↑↓ | 30-sekundowy test u usera, czy drag działa na desktopie |
| 2.4 | **Konsolidacja napraw rastra (`RepairForMesh`)** | metoda NIE ISTNIEJE, naprawy rozsiane | czerwony test `DemRasterRepairTests.RepairForMesh_DetailPerTileProfile` |
| 2.5 | **Paczka Railway to wciąż v1 (4265 kafli)** | manifest live = niepełny build z 06-21 | przepiąć jako v2 z pełnego cache i wgrać |
| 2.6 | **Stałe strojeniowe z 06-22 bez werdyktu** | żaden commit ich nie ruszył | werdykt wizualny usera przed jakąkolwiek zmianą |
| 2.7 | **Bias szlaków 0.09 — root cause nieprzyszpilony** | plaster żyje, temat zamrożony | zamienić stałą na wyłącznik, jeden A/B po seatingu na z16 |
| 2.8 | **Seating overlayów nie nadgonił z17** | rozjazd na najostrzejszych formacjach | czerwony test w `Trail3DWorldProjectionTests` |
| 2.9 | **Drogi eksponowane — brak potwierdzenia** | 3 z 4 rzeczy z 06-22 domknięte, ta nie | autoshot Orlej Perci + „Pobierz szlaki (widok)" |
| 2.10 | **Telefon: budżety czerwcowe, brak pomiaru bazowego** | mobile idzie ścieżką runtime-build | zbudować na androida i zmierzyć BEZ zmian w kodzie |
| 2.11 | **Pooling crop i pooling index — nadal nie zrobione** | doszły 2 rundy poolingu, te dwa kierunki nie | policzyć pauzy gen2 z watchdoga na świeżym buildzie |
| 2.12 | **Telefon: pełny re-sync z16** | źródło LOT26 wróciło na dysk, bloker zniknął | przy podpięciu telefonu policzyć kafle i dosłać brakujące |
| 2.13 | **Martwy kod Stage-0 w produkcji** | `MapPageViewModel.cs:4463-4492` nietknięte | skasować blok + 4 stałe `Stage0*` |
| 2.14 | **Żleb Kulczyńskiego: trwały fix nie istnieje** | bufor wokół trasy nie zaimplementowany | test `TrailDownloadBounds.ForViewportAndRoute(bufferDegrees: 0.05)` |
| 2.15 | **Brak przełącznika „☁ Chmury"** | `ToggleFlag` ma 22 case'y, chmur nie ma | flaga w 3 miejscach, ~15 linii |
| 2.16 | **Dane obok exe nie są szukane** | instalator obchodzi to kopiowaniem do LocalAppData | `FileSystemMapAutoLoader.BuildDefaultSearchRoots()` +2 rooty |
| 2.17 | **Profile per-urządzenie: binarny split WinUI/reszta** | 5 budżetów baked-streamingu | sprawdzić, czy telefon w ogóle ma piramidę baked |
| 2.18 | **Chmury Tier 1 (silver-lining) i Tier 3 (raymarch)** | tylko Tier 2 (cumulusy) na main | Tier 1 najtańszym cięciem: `vWorldPos` w shaderze warstwy |
| 2.19 | **Zachodnia granica mapy 19.50°E we wszystkich warstwach** | `tatry.dem` 4320×2200, W19.5 E20.4 | **decyzja usera** — zakres mapy vs koszt danych |
| 2.20 | **Line fog + distance cull to plastry** | `uMaxDist` + twardy `discard` | env-knob A/B bez przebudowy logiki |
| 2.21 | **Blady trójkątny placek na tafli Morskiego Oka** | orto detalu malowane na wodzie | (do zdiagnozowania) |

---

## 3. WYSOKI PRIORYTET, NIEZWERYFIKOWANE (sprawdzić przed podjęciem)

3.1 **Scatter lasu/kamieni z orto — rdzeń TDD gotowy, render NIGDY nie wpięty** (`EnsureForest`
zwraca pusto). Cały epik leży od 07-13. Dwa niezależne agenty wskazały to samo.
3.2 **Szew orto na grani PL/SK** — miał być domknięty przez finest-wins + przycięcie maską;
werdykt usera z dziś („szwy bardzo widoczne") mówi, że nie jest.
3.3 **Fazy ruchu wspinacza** (Preload→Release→Reach→Latch→Settle) + solver na workerze.
3.4 **Prawdziwy generator chwytów z geometrii/orto** zamiast hash-placeholdera (jawnie tymczasowy).
3.5 **Atrybucja modeli 3D smoka** (`dragon.glb` CC-BY „TO CONFIRM") — **blokada prawna przed
dystrybucją**, nie tylko dług.
3.6 **20+ commitów niepushniętych; warunek „zieleń P0" bez kryterium wyjścia** — ustalić kryterium
albo pushnąć.
3.7 **Token uploadu Railway wpisany JAWNIE do handoffu**, oznaczony „do rotacji" — **rotować**.
3.8 **Pełna bramka e2e z AGENTS.md §10** (wszystkie kryteria naraz na EXE+AppData+DELL) — nigdy
nie uruchomiona.
3.9 **PERF na telefonie 7–15 FPS** — jawnie „NIE ruszone".
3.10 **Instalka/paczki regionalne** — wstrzymane do domknięcia RMP3 przez Codexa (zamrożone).
3.11 **Zachód 20× w pełnym renderze kaskad + inwariant rozdzielności kolor↔cień** — zadanie #3
rundy P0, nietknięte.
3.12 **AKTYWNE WYŁADOWYWANIE dalekiego detalu** — werdykt usera „wciąż widzę rzeczkę 10 km z detalem".

---

## 4. WERDYKTY USERA BEZ REALIZACJI (22) — dług wobec właściciela

Najstarszy z 20 czerwca. Pełna lista w JSON; wysokie:
- pakiet pamięci lotu (commit bae64e7) — werdykt otwarty od **07-16**;
- proceduralne głazy (`ProceduralBoulderMesh` WIP, nigdy nie wpięty) — user wybrał ten kierunek;
- **gate R3 deshadow** (Świnica/Zawrat/Orla Perć) — jeden arkusz miał iść do usera jako warunek batcha;
- aktywne wyładowywanie dalekiego detalu („wciąż widzę rzeczkę 10 km z detalem");
- **Fala 2.5 — AUTO-sync danych przy instalacji**; user wprost odrzucił ręczne „pobierz";
- werdykt z 06-20: „taka karta i tyle RAMu, a detale z 2 s się doładowują przy ruchu";
- budżet uploadu na klatkę — oddane do decyzji usera i nierozstrzygnięte;
- szew PL/SK w 5 cm — ryzyko się zmaterializowało, werdykt bez odpowiedzi.

Średnie/niskie m.in.: formuła de-blue bez finalnej akceptacji, tonemapa ACES (czeka na kadr ze
śniegiem), werdykt stopy wspinacza, tacka ikon aktywności, „profesjonalne animacje ładowania",
woda P2 (glossy/fresnel/normal-mapa fal), sweep szlaków po fiksie masek TexStorage2D.

---

## 5. ZAMROŻONE ŚWIADOMIE (17) — nie są zgubione, ale mają wrócić

Scatter fazy 2–4 · wspinaczka etapy 6–7 (planner ze scoringiem, perf/mobile) · **naprawa światła
RENDER-SIDE zamiast korygowania orto** · materiał skalny na stromiznach (top-down orto na pionie) ·
pełny de-light orto · bramka „z19 tylko walk" + margin-stitch · ciemność wypalonego cienia SK
(tylko image-based L2) · synergia proceduralnych głazów · eksperyment LAZ 0,5 m (z18 ≈ 0,39 m) ·
kadencja hold-to-climb + audio/haptyka · wet/exposure slip, pitony/lina · wariant B assetu ·
chmury Tier 1/Tier 3 · instalka/paczki · **współpraca z Codexem: oczekiwany ACK C2C-20260731-019**.

---

## 6. PROPONOWANA KOLEJKA (do akceptacji usera)

1. **Dokończyć pobór SK 5 cm** (leci) → derywować z niego 25 cm i bazę (zasada §0-B) → bake → sweep.
2. **Werdykt usera na deshadow** (5 podglądów przed/po gotowych) → jeśli tak, zapis do osobnego katalogu.
3. **Domknąć P0 wydajności**: zwisy 1,5–2,8 s przy skokach + zadanie 3.11 (zachód 20×).
4. **Rotacja tokenu Railway (3.7) i atrybucja smoka (3.5)** — dwie rzeczy blokujące dystrybucję,
   tanie do zamknięcia.
5. **Push maina** po ustaleniu kryterium wyjścia (3.6).
6. Dopiero potem epiki zamrożone — scatter (3.1) ma największy stosunek gotowości do efektu:
   rdzeń TDD istnieje, brakuje wpięcia w render.

---

## 6B. WSZYSTKO JEST W LIŚCIE ZADAŃ (na polecenie usera: „dodaj wszystko do weryfikacji lub zrobienia")

Cała archeologia trafiła do listy zadań — **214 pozycji pokryte w 100 %**, w 41 zadaniach:

- **#6–#42 — pozycje WYSOKIEJ wagi, każda osobno** (prefiks `ARCH-H01`…`ARCH-H40`). Te z dowodem
  w kodzie mają gotowy pierwszy krok w opisie; niezweryfikowane mają to wprost napisane.
- **#43 `ARCH-W1`** — 22 werdykty usera do rozliczenia (każdy: zrealizowany / nieaktualny /
  świadomie odrzucony).
- **#44 `ARCH-W2`** — 85 pozycji średniej wagi do weryfikacji w kodzie.
- **#45 `ARCH-W3`** — 89 pozycji niskiej wagi jako backlog obszarowy.
- **#46 `ARCH-H41`** — 17 zamrożeń do przeglądu (odmrażamy z terminem / zostaje / porzucone).

Zadania sprzed archeologii (#1–#5) zostają: P0 menu, wyciek pamięci, zachód 20×, push maina,
całe Tatry w 5 cm.

**Zasada przy sięganiu po cokolwiek z listy:** pozycje niezweryfikowane sprawdzić NAJPIERW w kodzie —
6 z 27 zweryfikowanych okazało się już domkniętych, więc część backlogu jest fantomowa.

## 7. JAK KORZYSTAĆ Z TEGO DOKUMENTU

- Pełne dane: `dev/handoff-archeologia.json` (214 pozycji z cytatami źródłowymi i wagą).
- Pozycje z sekcji 2 mają dowód w kodzie — można brać od ręki.
- Pozycje z sekcji 3–5 są NIEZWERYFIKOWANE: **najpierw sprawdź w kodzie, czy nie zrobione**,
  dopiero potem planuj. 6 z 27 zweryfikowanych okazało się już domkniętych — proporcja może się
  powtórzyć.
- Przy podejmowaniu czegokolwiek z tej listy: zaktualizuj tę sekcję, żeby nie odkrywać jej trzeci raz.
