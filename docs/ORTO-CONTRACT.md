# KONTRAKT-ORTO — stałe zasady pul i grafiki orto (ZATWIERDZONE 2026-07-20)

> Ranga: jak `TERRAIN-GRAPHICS-CHECKLIST.md`. **Weryfikowany przy KAŻDYM grzebaniu przy orto** —
> każda zmiana dotykająca ścieżki orto (shader, streaming, pule, dane) przechodzi punkty §1–§5
> PRZED oddaniem builda userowi. Zmiana samego kontraktu wymaga zgody usera.

## §1. Piksel jest święty
Ekran pokazuje piksele źródła. Korekcje koloru wykonujemy RAZ, po stronie DANYCH (bake + audyt
skryptem na plikach — np. `audit-ortho-blue-cast.py`), gdzie są mierzalne i odwracalne. Shader ma
dokładnie DWA tryby:
- **domyślny** — „warunkowa harmonizacja tonu": detal ZASTĘPUJE bazę w całości (ostro na każdym
  dystansie — zachowanie z przyjętego demo 5 cm); dodatkowo ton jest ściągany do bazy WYŁĄCZNIE
  tam, gdzie niskie częstotliwości detalu odstają od bazy o więcej niż próg (niezgodność nalotów,
  np. zgnieciony cień); poniżej progu transform jest DOKŁADNĄ identycznością;
- **RAW** (klawisz `9`) — surowe piksele warstwy detalu, diagnostyka.

**Żadnego nowego filtra/transformu w shaderze bez jawnej zgody usera.**

## §2. Budżety z hardware'u, nie z kapelusza
Pule liczone RAZ przy starcie z odpytanego sprzętu (VRAM przez GL gdy dostępne, RAM z systemu),
stałymi PROPORCJAMI, w jednym miejscu, logowane jedną linią startową (`[OrtoBudget] …`).
Proporcje (zmiana = edycja tego pliku + zgoda usera):
- budżet orto VRAM = clamp(35% dedykowanego VRAM, 2 GB, 8 GB); fallback bez zapytania:
  desktop 6 GB / mobile 3 GB;
- det05 cap = 55% budżetu / rozmiar celi; det25 cap = 30% budżetu / rozmiar celi;
  backing det25 = max(4, ⅔ capu det05);
- cache dekodowanych kafli (RAM) = clamp(3% RAM fizycznego, 512 MB, 2 GB).

Stan przejściowy (dopóki zapytanie VRAM nie wdrożone): wartości progowe desktop/mobile z 2026-07-20
(det05 9/3 cele, ring 600/350 m, det25 12/8, budżet 6/3 GB, cache 1.5 GB/512 MB).

## §3. Finest-wins tylko tam, gdzie wnosi sygnał; ton z JEDNEGO źródła (bazy)
Zamrożone. Warstwa drobniejsza nigdy nie przemalowuje tonu (to gwarantuje §1-A z konstrukcji);
strefy bez realnego sygnału w detalu (zgnieciony cień) pokazują ton bazy bez wymyślania koloru.

## §4. Cztery punkty kotwiczne + samoweryfikacja PRZED oddaniem builda
Lista zamknięta:
| kotwica | co pilnuje |
|---|---|
| (a) showcase MO / schronisko | never-regress wzorca 5 cm |
| (b) ściana Mnicha z bliska | ostrość/stabilność detalu pod kątem (aniso + pasmo HF) |
| (c) Dolinka za Mnichem | szew nalotów / strefa zgniecionego cienia |
| (d) panorama z daleka | spójność tonu baza↔detal w skali masywu |

Mechanizm: `MAPATUR_CAM_PRESET=mo|mnich|dolinka|panorama` ustawia kamerę na kotwicę przy starcie;
asystent robi PASYWNE zrzuty (bez kradzieży okna) przed/po zmianie i porównuje. Build trafia do
usera dopiero, gdy wszystkie cztery kotwice są czyste.

## §5. Zamknięte obszary są zamknięte (FROZEN)
Dotknięcie pozycji z listy wymaga: powodu wypisanego PRZED zmianą + przełącznika A/B na klawiszu
(rollback jednym klawiszem, nie rebuildem).

**FROZEN (stan na 2026-07-20):**
- **kompozycja detalu = DE-BLUE BEZWZGLĘDNY + WARUNKOWA HARMONIZACJA** (`deblueShadow` + mode 1
  w `applyOrthoDetail`/`applyOrthoDet05Array`; A/B: `9`): (1) `deblueShadow` usuwa niebieski cień
  PER-PIKSEL na KAŻDEJ warstwie (twarda reguła; luma-gated — czerń nietknięta; ZERO dodawania zieleni;
  no-op na oświetlonym → showcase czysty); (2) detal ZASTĘPUJE bazę w całości, ton ściągany do bazy
  tylko przy niezgodności EKSPOZYCJI nalotów (porównanie de-blue↔de-blue, próg 0.10..0.28; nie doda
  niebieskiego z powrotem); poniżej progu = identyczność (showcase MO verbatim). ⚠️ jeziora: de-blue
  TYLKO na warstwach detalu, baza wyświetlana bez de-blue → woda zostaje niebieska;
- **det05 = TEXTURE ARRAY, wybór celi PER-FRAGMENT** (2 slice'y A/B, po ≤8 warstw — żaden pojedynczy
  zasób GPU >4.29 GB; KAŻDA alokacja `glGetError`-weryfikowana → degradacja, nie białe dziury);
- **focus detalu = promień patrzenia PRZYCIĘTY do ≤800 m + wygładzony w czasie** (τ=0.45 s, snap >2.5 km):
  ring/koło fade'u siedzą na pierwszym planie, nie skaczą za myszą ani za tłem (`StreamOrthoDetail`);
- **histereza rezydencji** (`TwoLevelDetailResidencyPolicy`, sticky window cap+6): rezydentna cela trzyma
  slot przy drobnym ruchu; wymiana dopiero przy realnym przejściu — koniec „przeładowywania";
- **fade-in 300 ms** per cela det05 (per-slot alpha) — świeża cela wjeżdża płynnie, nie strzela;
- pule desktop: det05 cap 12 (slice'y), budżet orto 9 GB, cache dekodowania 2 GB (mobile 3/512 MB);
- de-blue bazy po stronie danych (§3.11 TILE-PRODUCTION) + audyt po każdym fetchu,
- overlay det05/det25 on/off (A/B: `0`),
- coverage-gate det05 (`_coverage.txt`, geometria `OrthoDetailGrid(0.05,16,6)`, próg 16/256),
- reguły z pamięci: never-regress showcase MO, ORTO bez wypalonych cieni (data-side).

## Historia decyzji
- 2026-07-20: kontrakt zatwierdzony przez usera; domyślny tryb = A (ton z bazy).
- 2026-07-20 (później): werdykt usera — „ton z bazy" ZREGRESOWAŁ showcase MO (na dystansie HF→0,
  zostawała goła baza = zielone rozmycie). Prawo domyślne zmienione na WARUNKOWĄ HARMONIZACJĘ:
  pełne zastąpienie bazy detalem + korekta tonu tylko przy niezgodności nalotów (identyczność
  poniżej progu). Wniosek na stałe: każda propozycja prawa koloru MUSI być oceniona na WSZYSTKICH
  czterech kotwicach (w tym dystansowej) zanim trafi do usera.
- 2026-07-20: det05 jako TEXTURE ARRAY z wyborem celi per-fragment (usunięta przyczyna „10% hires":
  per-draw bindowana była jedna cela na kafel terenu); det25 ma tę samą wadę — do konwersji.
- 2026-07-20: incydent „białe kwadraty / pętla doczytywania" = JEDEN zasób GPU 4.295 GB (12 warstw
  8192²+mip) przekroczył 32-bitowy limit rozmiaru zasobu; alokacja padła CICHO (brak `glGetError`),
  lepki błąd zatruł alokacje kafli terenu. Fix: slice'y ≤8 warstw + weryfikacja `glGetError`. Wniosek
  na stałe: KAŻDA duża alokacja GL sprawdzana przez `glGetError`; karta 16 GB nie „kończy się" na 6 GB
  — to był limit per-zasób, nie sumaryczny VRAM.
- 2026-07-20: WERDYKT USERA „jest dużo lepiej" po komplecie: focus near-field+wygładzony, histereza,
  fade-in, 2-slice array. STAN ZAMROŻONY — nie ruszać bez powodu wypisanego przed zmianą (§5).
- 2026-07-20: REGRES twardej reguły — „warunkowa harmonizacja" (bez bezwzględnego de-blue) puściła
  niebieski cień na skałach (Rysy/Czarny Staw; zmierzone: strefa MA det05, więc niebieski był w danych
  5cm). Fix: przywrócony `deblueShadow` bezwzględny PRZED harmonizacją, w obu ścieżkach. WNIOSEK NA
  STAŁE: de-blue jest BEZWZGLĘDNY (per-piksel, każda warstwa), harmonizacja szwu to DRUGI, osobny krok
  — nie wolno ich mylić ani zastępować jednym drugim.
- OTWARTE (nie zaczęte): (1) det25 → texture array per-fragment (ta sama wada co det05 miał),
  (2) §2 zapytanie VRAM z hardware zamiast stałych desktop/mobile, (3) skorowidze GUGiK — rocznik
  bezcieniowy (overcast) dla strefy za Mnichem NIE ISTNIEJE (sprawdzone: 5cm tylko z 2021; patrz
  memory `gugik-ortho-5cm-tatry-archive`); alternatywnych źródeł 5cm brak (EU=2m, satelita=30cm,
  dron w TPN zakazany).
