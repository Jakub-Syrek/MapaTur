# HANDOFF 2026-07-21 — usuwanie wypalonych cieni z ortofoto (Rysy / Czarny Staw)

**Cel:** usunąć wypalone cienie z ortofoto **2021 5 cm OFFLINE** (bake danych, NIE runtime hack), bez utraty
detalu i bez zmiany materiału/koloru. Proces prowadzony rygorystyczną, gated procedurą (user = jedyny sędzia
oceny wizualnej; ja dostarczam POMIAR, nie werdykt „na oko" — patrz pamięć `visual-verdicts-data-first`).

**Artefakty (utrwalone):** `dev/ortho-deshadow/` (untracked) — skrypty pipeline'u, pobrane arkusze 25 cm,
diagnostyki PNG. **Uruchamialne od razu.** Scratchpad sesji jest ulotny; wszystko ważne jest tu.

---

## Odrzucone podejścia (NIE wracać — udowodnione porażki)
- **Per-tile / per-pixel shift średniej** — katastrofalny patchwork klocków.
- **Chroma-only do bazy** — wzmacniała niebieski cast (dopasowanie do chorej referencji), nie usuwała cienia.
- **Kompozyt RGB 2015+2019** — widoczny poziomy szew, zielony cast 2019 radiometrycznie oderwany.
- **Bramka near-black PER-PIKSEL** — błędny no-op + cętkowane pole gain.
- **Twierdzenie „w cieniu nie ma sygnału"** — FAŁSZ. AUTO-EQ surowego cienia 2021 pokazuje prawdziwe żebra
  i spękania. `sRGB<35` = niski POZIOM sygnału, nie brak. (Mój błąd, user słusznie odrzucił.)

## Ustalenia (zmierzone)
- Priorytet: **Rysy/Czarny Staw ~`49.177,20.082`, arkusz `M-34-101-A-c-4-3`.** Footprint w skryptach:
  `CLAT,CLON,HALF = 49.1793, 20.0783, 220.0` (440 m, na polskiej skale, z dala od granicy PL/SK).
- **2021 5 cm ma odzyskiwalny detal** (na dysku: `dem/ortho-detail/tatry/det05`), ale głęboki niebieski cień.
- **Roczniki 25 cm 2015/2022/2024 dają UZUPEŁNIAJĄCE się referencje oświetlenia** (owner-map: 2024 dominuje,
  2022 obwódka górnej-lewej, 2015 klin). **2019 NIEPOTRZEBNY** (luka 2015 = już oświetlony dolny piarg).
- **Union 2015/2022/2024 ma oświetloną referencję dla ~96% cienia 2021**; ~4% (górna-lewa) bez referencji
  w żadnym nalocie — korekcja ma tam PŁYNNIE zejść do zera.
- NoData GUGiK = dokładne `(0,0,0)` (maska twarda, nie próg jasności; potwierdzone).
- **Nie przenosimy RGB ani detalu z 25 cm — tylko gładkie pole LUMINANCJI.** Chroma 2021 nietknięta do końca
  etapu luminancji. Referencyjny max gain: **2.0× = 1 stopień**.

## Maska cienia — ZATWIERDZONA (user 2026-07-21)
Klasyfikator (skrypt `union_mask.py` → diagnostyki `diag.py`):
- pracuje CAŁKOWICIE na **low-pass log-luminancji 25–40 m** → NIE reaguje na fakturę skały (kluczowa
  poprawka — wcześniejszy per-piksel dawał czerwono-zielony szum zgodny z teksturą);
- **histereza dwuprogowa** (T_LO=0.20, T_HI=0.45 stopy) na polu union, nie jeden twardy próg;
- morfologia closing/opening ~7 m, usunięcie komponentów <150 m²; klasyfikacja SPÓJNYCH obszarów;
- feather ~15 m; **bramka „2021 faktycznie ciemne w low-pass" (`dark21`)** kasuje przeciek na oświetloną skałę;
- weryfikacja: rdzeń `α=0.9` w cieniu; `α=0.5` śledzi granicę; piarg-dół i prawa grań wykluczone;
  **niewątpliwie jasne kontrole: mean maski 0.006, p95 0.007**; ogon `α=0.1` na lewej granicy = akceptowalny
  feather (box „skala-lewa" p95=0.81 obejmuje SAMĄ granicę cienia, nie czystą oświetloną skałę).

## NASTĘPNY KROK — ciągłe pole luminancji z unii (spec usera, ścisły)
1. Zachować **nieprzycięte** pola log-gain każdego rocznika (NIE clampować do 1.2 przed normalizacją — clipping
   ukryłby różnice).
2. **Robust offset ekspozycji** każdego rocznika na wiarygodnych overlapach (nie globalna mediana na ślepo —
   check 2015/2019 pokazał niestabilność std 0.78, bo różne cienie; liczyć tylko tam, gdzie OBA jasne).
3. Odrzucać obserwacje odstające (śnieg — 2018 odpadł przez śnieg; 2024 ma ~9.5% śniegu).
4. Łączyć pola **gładkimi wagami confidence, NIE `argmax`** (owner-map = tylko diagnostyka).
5. Wagi sumują się do 1, **przestrzennie wygładzone** (żeby zmiana właściciela nie tworzyła szwu).
6. W brakujących ~4% confidence i gain **płynnie → 0** (korekcja=1).
7. **Dopiero końcowe pole** clampować do 1 stopnia (gain 2.0×).
8. Praca w **linear RGB / log-luminancji**. **Chromy NIE ruszać.**

## Diagnostyka wymagana PRZED akceptacją pola (user)
1. pole PRZED normalizacją ekspozycji; 2. pole PO normalizacji+blendzie; 3. udział wag 2015/2022/2024;
4. **gradient pola** (ujawni szwy); 5. wynik 2.0×; 6. różnica `PO − 2021`.

## Po zatwierdzeniu ciągłości luminancji
Osobny etap: **neutralizacja niebieskiej chromy** pod TĄ SAMĄ maską — osobne gładkie pole w przestrzeni
chromatyczności, referencja = oświetlona skała 2015, ale **NIE kopiować jej RGB ani zieleni roślinności**.
(Zachowanie chromy 2021 było warunkiem BEZPIECZEŃSTWA eksperymentu, nie finalną metodą — mnożenie luminancji
podnosi też kolor światła nieba, dlatego 2.5× wyglądał jak niebieska plama; 2.0× = baza.)

## Diagnostyki (w `dev/ortho-deshadow/diagnostics/`)
`d1_fields.png` (excess 2015/2022/2024/union, colorbar w stopniach) · `d2_owner.png` (argmax — TYLKO diag,
nie składać) · `d3_conf.png` · `d4_core.png` (rdzeń) · `d4_soft.png` (feather) · `d5_contours.png`
(0.1/0.5/0.9 na 2021) · `poc2_shadowcrop.png` (AUTO-EQ = dowód że detal w cieniu jest realny) · `um_excess.png`.

## Dane / fetch
Arkusze 25 cm RGB (`c-4-3`, ~12–20 MB) w `dev/ortho-deshadow/rysy/{2015,2018,2019,2022,2024}.tif`.
Re-fetch: WFS Skorowidze GUGiK (patrz pamięć `gugik-ortho-5cm-tatry-archive`), filtr `kolor=RGB`, godło
`M-34-101-A-c-4-3`, `url_do_pobrania` → opendata GeoTIFF. NoData=`(0,0,0)`. CRS EPSG:2180 (geotransform w tagach
ModelTiepoint/PixelScale; lat/lon→2180 przez pyproj always_xy). 2021 5 cm = det05 na dysku (repo).

## Twarde reguły procesu (z całej sagi — patrz KONTRAKT-ORTO + pamięci)
- User = jedyny sędzia oceny wizualnej; ja OGLĄDAM każdy wynik zanim cokolwiek stwierdzę i NIE ufam liczbom
  nad obrazem (klasyfikator mylił się 2×; metryki świeciły zielono przy złym obrazie).
- Nie ogłaszać „limitu fizycznego" bez maski DEM/słońce + kontroli obrazu.
- Zero mieszania RGB między rocznikami; tylko gładkie pole luminancji.

---

## Handoff usera (verbatim, 2026-07-21 00:25)

**Cel:** usunąć wypalone cienie z ortofoto 2021 5 cm offline, bez runtime hacków i bez utraty detalu.

**Odrzucone**
- Per-tile/per-pixel shift — katastrofalny patchwork.
- Chroma-only względem bazy — wzmacniała niebieski cast i nie usuwała cienia.
- Kompozyt RGB 2015+2019 — widoczny szew i zielony cast.
- Near-black gate per piksel — błędny no-op i cętkowane pole.
- Wniosek „w cieniu nie ma sygnału" — fałszywy; AUTO-EQ pokazuje prawdziwe żebra i spękania.

**Ustalenia**
- Priorytet: Rysy/Czarny Staw, około `49.177, 20.082`, arkusz `c-4-3`.
- 2021 5 cm zawiera odzyskiwalny detal, ale głęboki niebieski cień.
- Roczniki 25 cm: 2015/2022/2024 dają uzupełniające referencje oświetlenia.
- 2019 nie jest potrzebny: luka 2015 leży na już oświetlonym dolnym piargu.
- Nie przenosimy RGB ani detalu z 25 cm — tylko gładkie pole luminancji.
- Referencyjny maksymalny gain: 2.0× = 1 stopień.
- Chroma 2021 zostaje nietknięta do zakończenia etapu luminancji.

**Maska — zatwierdzona.** Poprawiony klasyfikator:
- pracuje na low-pass log-luminancji 25–40 m;
- nie reaguje na fakturę skały;
- rdzeń `alpha=0.9` siedzi wewnątrz cienia;
- `alpha=0.5` śledzi granicę;
- dolny piarg i oświetlona prawa grań są wykluczone;
- jasne kontrole: `mean mask=0.006`, `p95=0.007`;
- ogon `alpha=0.1` na lewej granicy jest akceptowalnym featherem;
- około 4% górnego-lewego cienia nie ma referencji — korekcja ma tam płynnie zejść do zera.

**Aktualne diagnostyki**
- `d1_fields.png` — low-pass excess 2015/2022/2024/union ze skalą w stopniach.
- `d2_owner.png` — argmax właściciela; tylko diagnostyka, nie używać do składania.
- `d3_conf.png` — confidence.
- `d4_core.png` — twardy rdzeń.
- `d5_contours.png` — kontury `0.1/0.5/0.9` na 2021.

**Następny krok — zbudować ciągłe pole luminancji z unii:**
1. Zachować nieprzycięte pola log-gain każdego rocznika.
2. Robust normalizacja ekspozycji na wiarygodnych overlapach.
3. Odrzucić obserwacje odstające, np. śnieg.
4. Łączyć pola gładkimi wagami confidence, nie `argmax`.
5. Wagi mają sumować się do 1 i być przestrzennie wygładzone.
6. W brakujących 4% confidence i gain płynnie → 0/1.
7. Dopiero wynik końcowy clampować do `1 stop = 2.0×`.
8. Pracować w linear RGB/log-luminancji.
9. Nie ruszać chromy.

**Diagnostyka wymagana przed akceptacją.** Pokazać:
- pole przed normalizacją;
- pole po normalizacji i blendzie;
- wagi 2015/2022/2024;
- gradient pola ujawniający szwy;
- wynik 2.0×;
- różnicę `PO − 2021`.

Dopiero po zatwierdzeniu bezszwowej luminancji: osobny etap neutralizacji niebieskiej chromy pod tą samą maską.
