# HANDOFF TECHNICZNY — Biomy wysokościowe + Woda na jeziorach 3D (MapaTur)

> **Dokument ŻYWY, przyrastający.** Nie resetować do zera. Nie usuwać historycznych decyzji ani błędnych ścieżek.
> Aktualizować istniejące sekcje, oznaczać status: 🟢 POTWIERDZONE · 🟡 HIPOTEZA · 🔴 OBALONE · ⚫ PORZUCONE · 🔵 AKTYWNIE ROZWIJANE.
> Każda sesja: wczytaj → porównaj z aktualną wiedzą → zaktualizuj → zachowaj historię → dodaj wnioski.
> **Ochrona przed fałszywym sukcesem:** żadne „naprawione/gotowe/fixed" zanim nie ma: nowego builda + restartu apki + zrzutu usera + potwierdzenia usera. Inaczej: „hipoteza / oczekiwany efekt / niezweryfikowane".

Repo: `C:\Repos\MapaTur` · Urządzenie testowe: **RFCY1198TTX** (Galaxy S25 Ultra) · adb: `C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe`
Sesje udokumentowane tu: **S1 = 2026-06-09** (biomy + cała saga wody).

---

## 1. AKTUALNY STAN PROJEKTU (fakty, koniec S1)

**Git:** `main` HEAD = **`615edef`** (biome stage 2). Drzewo robocze: **niezacommitowane** zmiany w `Terrain3DGlRenderer.cs` (nowa woda na obrysie OSM).
Gałąź backup: **`water-backup-d9799c1`** (= `d9799c1`) — zawiera tuning palety biomów + heurystyczną wodę + planar reflection.

**Działa (🟢 zweryfikowane na urządzeniu w S1):**
- Biomy wysokościowe: `BiomeClassifier` (TDD, Application.Terrain) + maska w terrain fragment shaderze + przełącznik „Biomy". Strefy hala/piarg/skała/śnieg/lód po elev+slope+aspect.
- Woda heurystyczna `flatW × darkW` malowana po terenie — kształt brzegu brał się z `darkW` (ciemność ortho) i **pasował do zdjęcia**; user zaakceptował wygląd przy kątach hero (commit `d9799c1`: „góry się zajebiście odbijają"). ⚠️ ma moczary (patrz niżej).
- Planar reflection gór w wodzie (FBO, render terenu odbity względem płaszczyzny tafli) — `d9799c1`, user-approved.
- OSM `natural=water` obrys Morskiego Oka **nakłada się 1:1 na bundlowane ortofoto** (user potwierdził „jest ok"). 🟢
- Woda na **obrysie OSM** (wielokąt → ear-clipping → płaski mesh, głębokość per-pixel radialnie) — **woda tylko wewnątrz obrysu = z definicji ZERO moczarów**.

**Nie działa / cofnięte:**
- Heurystyka detekcji wody (flatW×darkW i pochodne) ma **nieusuwalne moczary** (woda na ciemnym płaskim lesie/dnie doliny). Cała heurystyczna ścieżka **PORZUCONA** na rzecz obrysów OSM.
- `main` NIE ma obecnie: tuningu palety biomów, wody, reflection (poszły do `water-backup-d9799c1` po `git reset --hard 615edef`).

**Niezweryfikowane (oczekuje na zrzut usera):**
- Najnowszy build wody: **ear-clipping triangulacja + radialna głębokość per-pixel** (ma usunąć „3 białe promienie" z nakładających się trójkątów wachlarza). Build wgrany + apka zrestartowana, **brak potwierdzenia z obrazu**.

**Znane ograniczenia:**
- Obrys wody na razie tylko **Morskie Oko** (zahardkodowany wielokąt 204 pkt z OSM way 27952583). Pozostałe stawy (Czarny Staw, Pięć Stawów…) — obrysy pobrane do `lakes.json`, NIE wpięte.
- Woda jest w **bloku debug** (`uDebugPoly`), nie ma jeszcze przełącznika UI ani danych dla wielu jezior; poziom tafli zahardkodowany 1395 m (Morskie Oko).
- Reflection gór NIE jest jeszcze wpięta w nową wodę na obrysie (etap 2 zaplanowany; kod jest w backupie).

---

## 2. ARCHITEKTURA (wiedza potwierdzona)

**Rendering:** `Terrain3DGlRenderer` rysuje OpenGL ES na kontekście SKGLView (Skia). Z pamięci projektu: bindować FBO Skii (nie 0), własny stan GL co klatkę, `GRContext.ResetContext` po renderze, **GLES nie czyta depth**. MSAA FBO opcjonalne (`DepthComponent24` — **bez stencila** → brak stencil-fill wielokątów).

**Geometria terenu:** kafle (`TileBuffers`, VAO/VBO/EBO). Wierzchołek terenu: `aPos(vec3,loc0) aColor(vec4,loc1) aNormal(vec3,loc2) aTex(vec2,loc3)` = 12 floatów. `vWorldPos = aPos`.

**Układ świata:** `LocalTangentProjection.GeoToWorld(geo, elevM, ProjectionAnchor, verticalExaggeration)`; X=wschód, Y=północ, Z=góra. **Z = elevM × exaggeration (BEZ offsetu Z)** 🟢. XY w metrach względem kotwicy rastra. Mesh: `vertex.z = raster[c,r] * exaggeration`. `tiles[0].GeoToWorld(geo, elevM)` daje świat dla danego geo (ten sam przelicznik co teren → OSM się zgrywa).

**Ortofoto:** bundlowane, cięte na komórki (per-tile tekstury, `OrthoTileIndex`), upload+mipmaps+16×aniso. Woda na zdjęciu jest **ciemna** (stąd `darkW` jako kształt jeziora).

**LOD / dane:** teren 1 m TYLKO w „LOD demo" (okolice Morskiego Oka). Domyślne auto-ładowanie = baza 30 m (Beskidy). A/B wody/biomów RÓB w LOD demo.

**Woda (obecna, nowa ścieżka — w drzewie roboczym):**
- Wielokąt obrysu (OSM) → `EarClipXy` (triangulacja bez nakładania, O(n²)) → płaski mesh na `waterElev = 1395+4 m × exagg` w layoucie terenu.
- Rysowany przez **ten sam program terenu** z flagą `uDebugPoly=1` (osobny VAO/VBO, `DrawArrays Triangles`), blend SrcAlpha, **depth-write OFF, depth-test ON** (misa przycina taflę gdzie dno > płaszczyzna).
- Shader (per-pixel): `depthF` z **odległości od środka** (uniformy `uLakeCenter`,`uLakeRadius`) — NIE z wierzchołków; turkus brzeg→granat środek; fresnel; odbicie nieba (gradient `uSkyAmbient`, BEZ planar reflection na razie); glint USUNIĘTY (robił białą smugę); `waterAlpha = mix(0.15, 0.82, smoothstep(depthF))` (szklane brzegi).

**Threading/pamięć:** standard renderera; mesh wody przebudowywany w `Render` (tani, ~kilkaset trójkątów).

---

## 3. CHRONOLOGIA (S1, 2026-06-09)

| Sesja | Zmiana | Powód | Efekt | Status |
|---|---|---|---|---|
| S1 | `BiomeClassifier`+paleta (TDD, 18 testów) `615edef..` | task: biomy wysokościowe | logika stref hala/piarg/skała/śnieg/lód | 🟢 |
| S1 | Maska biome w shaderze + przełącznik „Biomy" (`615edef`) | render stref | działa, device-OK | 🟢 |
| S1 | Tuning palety (oliwka, ciepły piarg, śnieg 2325) + **woda flatW×darkW** (`1cdec93`) | estetyka + woda na jeziorach | woda na zdjęciu OK pod kątem hero; **wprowadzono moczary** | 🟢 biomy / 🔴 woda-heurystyka |
| S1 | Planar reflection gór (`d9799c1`) | „wow" odbicia | „góry się zajebiście odbijają" (user) | 🟢 wizualnie; moczary zostały |
| S1 | Próby naprawy moczarów: wywalenie flatW → footprint-koło → flat mesh-dysk → DEM lift → blueW | usunąć moczary/patchwork | **SPIRALA REGRESJI** — każda naprawa psuła kolejną rzecz | 🔴/⚫ wszystkie, niezacommitowane, wyrzucone |
| S1 | `git reset --hard 615edef` + backup `water-backup-d9799c1` | powrót do stanu bez moczarów | `main` = teren+biomy bez wody | 🟢 |
| S1 | Woda z **OSM `natural=water`** obrys → magenta debug → fill → ear-clip + radial depth | jedyna droga bez moczarów | obrys pasuje do ortho (user „jest ok"); fill bez moczarów; promienie z fan→ ear-clip | 🔵 ostatni krok niezweryfikowany |

---

## 4. BŁĘDNE HIPOTEZY

1. **Hipoteza:** „Patchwork na tafli naprawię usuwając/luzując `flatW`." — wydawało się: patchwork pochodził z `flatW` (szwy normalnych kafli). **Sfalsyfikowane:** po usunięciu flatW pojawił się FLOOD/moczary i „kółko". **Dowód:** kolejne zrzaty z wodą na ciemnym lesie. **Status: 🔴 OBALONE.** `flatW` to JEDYNY rozróżnik woda vs ciemny las.
2. **Hipoteza:** „Footprint-koło jako jedyna bramka (×darkW, bez flatW) ograniczy wodę do jeziora." **Sfalsyfikowane:** `darkW` wypełnił ciemny las **wewnątrz** dysku → na ekranie idealne **kółko** zamiast kształtu jeziora. **Status: 🔴 OBALONE.**
3. **Hipoteza:** „Płaski mesh-dysk wody + depth-test = czysta tafla bez patchworku." **Częściowo:** patchwork zniknął, ale dno (LOD-stepped) **przebija** płaski dysk / z-fighting → ukośne pasy; lift wysoki = woda nad linią brzegu, lift niski = przebicia; łagodny brzeg = rozlanie ~100 m. **Status: 🔴 OBALONE jako finalne** (porzucone na rzecz obrysu OSM).
4. **Hipoteza:** „`blueW` (niebieski≥zielony) odróżni wodę od ciemnego lasu." **Sfalsyfikowane:** moczary zostały — to ciemny **neutralny** las/dno (b≈g), nie zielony. **Status: 🔴 OBALONE.**
5. **Hipoteza (user, S1):** „Promienie to artefakt triangulacji/interpolacji vertexów, nie problem jeziora." **Potwierdzone diagnozą:** centroid-fan na **wklęsłym** wielokącie → **nakładające się trójkąty** w zatokach → podwójny blend = jasne promienie. **Status: 🟢 POTWIERDZONE** (fix ear-clipping w toku, wizualnie niezweryfikowany).
6. **Hipoteza:** „Z ortofoto da się łatwo zczytać kształt jeziora." **Sfalsyfikowane:** per-piksel ciemny las = ciemna woda; segmenter CPU = ta sama dwuznaczność + duża robota. **Status: 🔴 OBALONE** (rozwiązanie = realne obrysy OSM, nie zdjęcie).

---

## 5. WNIOSKI INŻYNIERSKIE (potwierdzone)

- **Shader/woda:** kolor/fresnel/odbicie/głębokość licz **per-pixel** z funkcji ciągłych (np. odległość od środka), NIE interpoluj z wierzchołków — interpolacja po dużych/wachlarzowych trójkątach robi widoczne załamania.
- **Triangulacja:** centroid-fan działa tylko dla wypukłych wielokątów; dla **wklęsłych** (Morskie Oko ma zatoki) trójkąty się nakładają → przy blendzie podwójne krycie = jasne promienie. Używać **ear-clipping**.
- **Detekcja wody ze zdjęcia/geometrii jest niejednoznaczna:** ciemna woda ≈ ciemny płaski las; łagodny brzeg myli każdy próg poziomu. Jedyne pewne źródło „gdzie woda" = **realny obrys (OSM `natural=water`)**.
- **Obrysy OSM zgrywają się z bundlowanym ortho/DEM** (ten sam `GeoToWorld`). 🟢
- **GLES:** depth RB = `DepthComponent24` → **brak stencila** (nie zrobisz stencil-fill wielokąta). MSAA + blend na nakładających się trójkątach = jasne szwy.
- **Płaski mesh wody vs dno DEM:** dno (LOD) ma schodki ±kilka m wokół poziomu tafli; mały lift → przebicia/z-fighting, duży lift → woda wchodzi w górę brzegu. Obrys-mesh + clip eliminuje to przez kształt, nie przez lift.
- **Środowisko dev (Windows):** brak `python`, brak `jq`; jest `node` (ścieżki Windows `C:/...`, NIE `/tmp`), `curl` (sieć działa). Overpass: ograniczać **bbox** — jest kilka „Morskie Oko" (m.in. Warszawa-Łazienki 52.2N).

---

## 6. ROOT CAUSE ANALYSIS

**Problem A — „moczary" (woda tam gdzie jej nie ma).**
- Objaw: niebieska/turkusowa „woda" na ciemnym płaskim lesie / dnie doliny, plamy/kółka.
- Fałszywe tropy: usunięcie flatW, footprint-koło, blueW, poziom DEM, mesh-dysk.
- **Rzeczywista przyczyna:** detekcja `flatW×darkW` z natury klasyfikuje „ciemne+płaskie" jako wodę, a **ciemny płaski las/dno doliny spełnia oba** warunki. Heurystyka obrazowa NIE rozróżni wody od ciemnego płaskiego lasu.
- Dowód: każdy wariant heurystyki dawał albo moczary, albo dziury/kółko; obrys OSM (z definicji „woda tylko wewnątrz") → moczary znikają.
- Status: 🟢 POTWIERDZONE. Rozwiązanie = obrysy OSM.

**Problem B — „3 białe promienie" na tafli.**
- Objaw: jasne linie promieniste ze środka jeziora.
- Fałszywe tropy: glint (sun-path smuga — to był osobny, usunięty artefakt), depthF z wierzchołków (poprawione, promienie zostały).
- **Rzeczywista przyczyna:** centroid-fan na **wklęsłym** wielokącie → trójkąty **nachodzą** w zatokach → przy alpha-blend podwójne krycie = jasne kliny (tyle promieni, ile dużych wklęsłości).
- Dowód: po przejściu na ear-clipping (trójkąty bez nakładania) — oczekiwane zniknięcie (NIEZWERYFIKOWANE z obrazu na koniec S1).
- Status: 🟡→🔵.

**Problem C — biała smuga na wodzie (osobny od promieni).**
- Objaw: jedna jasna diagonalna linia.
- Przyczyna: **sun-path glint** (`pow(reflFlat·sun, N)`) tworzy pasek odbicia słońca na płaskiej tafli.
- Fix: usunięty glint. Status: 🟢 (potwierdzone usunięcie przez usera — „usuń tą dziwną białą linię").

---

## 7. LEKCJE NA PRZYSZŁOŚĆ

❌ **Nie usuwaj `flatW`** żeby naprawić patchwork. — Powód: flatW odróżnia wodę (płaską) od ciemnego lasu (na stoku). — Dowód: po usunięciu → flood/moczary/kółko.
❌ **Nie próbuj heurystyką (ortho/slope) wykryć dokładnego kształtu jeziora.** — Powód: ciemna woda ≈ ciemny płaski las; łagodny brzeg myli poziom. — Dowód: 6+ wariantów, wszystkie z moczarami lub złym kształtem.
❌ **Nie używaj centroid-fan dla wklęsłych wielokątów.** — Powód: trójkąty się nakładają → blend artefakty (promienie). — Dowód: 3 promienie = 3 zatoki; ear-clipping je usuwa.
❌ **Nie interpoluj depth/shore/fresnel z wierzchołków na rzadkiej siatce.** — Powód: widoczne załamania na krawędziach trójkątów. — Dowód: promienie zostały mimo gęstości; znikły dopiero przy depth per-pixel + ear-clip.
❌ **Nie ogłaszaj sukcesu przed zrzutem usera.** — Powód: build green/instalacja ≠ efekt na ekranie. — Dowód: cała sesja; user wprost: „co 5 minut ogłaszasz sukces".
❌ **Nie wszczynaj spirali na problem 8/10.** — Powód: patchwork (kosmetyk) wywołał kaskadę regresji. — Dowód: patchwork→flood→koło→mesh→OSM, jezioro przestało być jeziorem.
❌ **Nie używaj `python` na tym Windowsie; nie zakładaj `/tmp` dla node.** — Dowód: brak pythona; node czyta `C:\tmp`.

---

## 8. OSTATNI ZNANY DOBRY STAN

- **Bez moczarów:** commit **`615edef`** (`main`), 2026-06-09. Dobry, bo: teren = czyste ortofoto (woda = realne zdjęcie, ZERO heurystyki = zero moczarów), biomy stage 2 działają. Ograniczenie: brak wody jako efektu (jeziora to tylko zdjęcie), brak tuningu palety biomów (oliwka jest dopiero w `1cdec93`/backupie).
- **Z wodą (user-approved wizualnie):** commit **`d9799c1`** (gałąź `water-backup-d9799c1`), 2026-06-09. Dobry pod kątami hero: flatW×darkW (kształt z ortho pasuje do zdjęcia) + planar reflection gór + tuning palety biomów. Ograniczenie: **moczary** pod innymi kątami; patchwork z góry.

## 9. OSTATNI ZNANY ZŁY STAN

- Cała seria post-`d9799c1` (footprint-koło / flat mesh-dysk / blueW / DEM-lift), 2026-06-09, **niezacommitowana, wyrzucona** przez `git reset --hard 615edef`. Objawy: moczary, kółko, ukośne pasy z-fightingu, rozlanie 100 m na łagodnym brzegu, biała smuga, promienie. Przyczyna: usunięcie flatW + próba dokładnego kształtu heurystyką/kołem. Porzucone bo: jezioro przestało przypominać jezioro (spirala regresji).

---

## 10. DECYZJE PRODUKTOWE (user)

| Decyzja | Status | Data | Powód |
|---|---|---|---|
| Biomy wysokościowe jako następny krok | zrealizowane | S1 | roadmap materiałów |
| Woda na jeziorach + planar reflection gór | zrealizowane (backup) | S1 | „wow", film |
| „Real mapka — woda tam gdzie woda, nie science-fiction" | obowiązujące | S1 | jakość mapy |
| Wyrzucić heurystyczną wodę, zrobić od nowa na realnych obrysach | obowiązujące | S1 | moczary nieusuwalne heurystyką |
| Backup starego kodu wody przed resetem | zrealizowane (`water-backup-d9799c1`) | S1 | „może coś z tego użyjesz" |
| Reset do wersji bez moczarów (`615edef`) | zrealizowane | S1 | powtórzone 5× |
| Woda: turkus mniej neonowy, brzegi szklane/przezroczyste, BEZ białej smugi/promieni | obowiązujące, w toku | S1 | wygląd |

---

## 11. EKSPERYMENTY

| Cel | Zmiana | Wynik | Wniosek | Status |
|---|---|---|---|---|
| Odróżnić wodę od lasu | `blueW` = b−g ≥ próg | moczary zostały | las jest neutralny (b≈g), nie zielony | 🔴 |
| Dokładny kształt | footprint-dysk × darkW | „kółko" (darkW wypełnia las w dysku) | dysk≠kształt jeziora; bez flatW las wchodzi | 🔴 |
| Płaska tafla bez patchworku | osobny mesh-dysk + depth clip | patchwork zniknął, ale z-fighting/przebicia, lift trade-off, 100 m rozlania | obrys, nie dysk | ⚫ |
| Eliminacja z-fightingu | lift dysku 2→14 m | pasy słabsze, ale woda nad linią brzegu | obrys, nie lift | ⚫ |
| Realny kształt | OSM `natural=water` obrys, magenta debug | obrys nakłada się na ortho (user „jest ok") | OSM się zgrywa → droga właściwa | 🟢 |
| Wypełnienie obrysu | centroid-fan fill | promienie (nakładanie trójkątów w zatokach) | ear-clipping | 🔴 fan / 🔵 ear-clip |
| Usunięcie promieni | depthF radialny per-pixel + ear-clipping | NIEZWERYFIKOWANE z obrazu | czekać na zrzut | 🔵 |

---

## 12. REGRESJE

| Zmiana | Co zepsuła | Jak wykryto | Jak naprawiono | Status |
|---|---|---|---|---|
| Usunięcie/luzowanie flatW | flood/moczary, kółko | zrzuty usera | revert do `615edef` + obrys OSM | 🟢 |
| Centroid-fan na wklęsłym wielokącie | 3 białe promienie | zrzut usera + hipoteza | ear-clipping (w toku) | 🔵 |
| Mocny sun-path glint | biała smuga | zrzut | usunięty glint | 🟢 |
| Reset `615edef` | zdjął z `main` tuning palety biomów + reflection | analiza git (1cdec93 łączył paletę+wodę) | są na `water-backup-d9799c1` (cherry-pick możliwy) | 🟡 do decyzji usera |

---

## 13. ANTI-HALLUCINATION CHECK

🟢 **WIEMY:** flatW×darkW = działająca detekcja w `d9799c1`; flatW odróżnia wodę od lasu; heurystyka nie da dokładnego kształtu; OSM obrys zgrywa się z ortho (user-confirmed); centroid-fan na wklęsłym → promienie; GLES bez stencila; node/curl OK, brak pythona/jq; `Z=elev×exagg` bez offsetu; woda 1m tylko w LOD demo.

🟡 **PODEJRZEWAMY:** ear-clipping + radialna głębokość usuwa promienie (build wgrany, brak zrzutu); planar reflection z backupu da się wpiąć w wodę-obrys; głębokość radialna „wystarczy" dla nieokrągłych jezior.

⚫ **NIE WIEMY:** jak wygląda woda-obrys dla pozostałych stawów (nie wpięte); czy poziom tafli per-jezioro będzie zgadzał się z ortho (sezonowość/DEM); FPS przy wielu jeziorach + reflection; czy ear-clip nie zostawia artefaktów na bardzo wklęsłych obrysach.

---

## 14. SESJA RETROSPEKTYWNA (S1)

- **Największa strata czasu:** próby naprawy moczarów/patchworku heurystyką (footprint, mesh-dysk, blueW, lift) — spirala regresji.
- **Błędne założenia:** że flatW można usunąć; że da się wykryć kształt jeziora ze zdjęcia/geometrii; że problem jest „mały" (8/10), a nie architektoniczny.
- **Pierwszy sygnał złego kierunku:** gdy usunięcie flatW (na patchwork) dało flood — to był moment, by się COFNĄĆ, a nie iść dalej.
- **Jak dojść 10× szybciej:** po pierwszym moczarze od razu uznać „heurystyka nie rozróżni wody od ciemnego lasu" → realne obrysy OSM od początku; zachować flatW; nie wszczynać spirali na kosmetyk.
- **Czego nie powtarzać:** blueW, footprint-koło bez flatW, centroid-fan na wklęsłym wielokącie, lift jako lek na z-fighting, segmentacja ortho per-piksel.

---

## 15. OTWARTE TEMATY

**P0 (krytyczne dla „wody od nowa"):**
- **Dokończyć wodę na obrysie OSM** — opis: ear-clip fill już jest, zweryfikować z obrazu (promienie), wpiąć **planar reflection** z `water-backup-d9799c1`, dodać **pozostałe stawy** (obrysy w `lakes.json`), poziom tafli **per-jezioro** (mapa name→elev), wyciągnąć z bloku `uDebugPoly` w normalną ścieżkę + ewentualny przełącznik. Koszt: średni. Ryzyko: alignment poziomu tafli per-jezioro; FPS. Zależności: `lakes.json` (mam), backup reflection. Kierunek: tabela `(name, polygon, elevM)` bundlowana jako zasób (jak gazetteer szczytów), nie hardcode.

**P1 (ważne):**
- Decyzja: czy przywrócić na `main` **tuning palety biomów** (oliwka) z `water-backup-d9799c1` bez wody (cherry-pick `BiomeClassifier.cs`). Koszt: mały. Ryzyko: niski.
- Sprzątanie: untracked zrzuty w repo (`lake-*.jpg`, `water-*.png`, `reflection-*.png`, `biome-*.png`, `*-test.*`, `*.txt`, `lakes.json`) — scratch S1, do usunięcia.

**P2 (kosmetyka, wg listy usera, PO usunięciu promieni):**
- Glossy: roughness↓, specular↑, fresnel↑, subtelna normal-mapa fal, sun-glint **skoncentrowany** (nie pasek), ewentualnie bloom tylko na highlightach. ⚠️ glint ostrożnie — wcześniej smuga/konfetti.

---

## 16. MEMORY UPDATE

### Długoterminowe fakty o projekcie (warte pamiętać za 6–12 mies.)
- **Woda na jeziorach = realne obrysy OSM `natural=water`, NIE heurystyka.** Heurystyka (ciemne+płaskie) z natury robi moczary (ciemny płaski las = ciemna woda). Obrys = woda tylko wewnątrz = zero moczarów. Obrysy zgrywają się z bundlowanym ortho przez `GeoToWorld`.
- **Jeśli ktoś chce heurystykę:** `flatW × darkW` to znany działający kompromis (d9799c1) — flatW odróżnia wodę od lasu, darkW daje kształt z ortho; **nigdy nie usuwać flatW** (→ flood). Ma moczary pod niektórymi kątami.
- **Triangulacja wielokątów jezior: ear-clipping** (nie centroid-fan — nakłada się na wklęsłościach → promienie). Głębokość/kolor **per-pixel**, nie z wierzchołków.
- **GeoToWorld:** Z=elev×exaggeration bez offsetu; XY metry; ten sam przelicznik dla OSM i terenu.
- **Overpass:** ograniczać bbox (kilka „Morskie Oko", m.in. Warszawa). Dev: node (ścieżki C:/), curl OK; brak pythona/jq.
- **Backup wody:** gałąź `water-backup-d9799c1` (flatW×darkW + planar reflection + tuning palety biomów). `main` zresetowany do `615edef` (bez wody).
- **GLES tu:** brak stencila (DepthComponent24); MSAA+blend na nakładających się trójkątach = jasne szwy.

---

## 17. CO POWINIEN WIEDZIEĆ NOWY ENGINEER W 5 MINUT

1. Repo `C:\Repos\MapaTur`, urządzenie `RFCY1198TTX`, pełny APK przez WiFi/USB adb, **weryfikacja z OBRAZU** (Serilog nie wychodzi na Androidzie).
2. `main` = `615edef` (teren+biomy, **bez wody**). Woda „od nowa" jest **niezacommitowana** w drzewie roboczym `Terrain3DGlRenderer.cs`.
3. Stary kod wody (flatW×darkW + reflection + tuning palety) jest na gałęzi **`water-backup-d9799c1`**.
4. **Woda = realne obrysy OSM `natural=water`.** NIE rób detekcji ze zdjęcia/stoku — to robi moczary (potwierdzone wielokrotnie).
5. **Nigdy nie usuwaj `flatW`** w heurystyce — odróżnia wodę (płaską) od ciemnego lasu (na stoku).
6. Obrysy jezior zgrywają się z ortho przez `tiles[0].GeoToWorld(geo, elevM)` (Z=elev×exagg).
7. Triangulacja jezior: **ear-clipping**, nie centroid-fan (promienie na wklęsłościach).
8. Kolor/głębokość/fresnel licz **per-pixel** (uniformy), nie interpoluj z wierzchołków.
9. Woda 1 m widoczna tylko w **„LOD demo"** (Morskie Oko). Default = 30 m Beskidy.
10. Biomy: `BiomeClassifier` (Application.Terrain, TDD) + maska w terrain shaderze + przełącznik „Biomy".
11. GLES: brak stencila; MSAA+blend na nakładających się trójkątach = szwy; GLES nie czyta depth.
12. Dev box: node (ścieżki `C:/...`), curl OK; **brak python/jq**.
13. Overpass: zawsze bbox (kilka „Morskie Oko" w OSM).
14. Aktualny otwarty krok: zweryfikować ear-clip wodę z obrazu, wpiąć reflection z backupu, dodać pozostałe stawy + poziom per-jezioro.
15. Reset `615edef` zdjął z main tuning palety biomów (jest w backupie) — do ewentualnego cherry-picka.
16. Build compile-check: `dotnet build src\MapaTur.App\MapaTur.App.csproj -f net10.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=false -p:WindowsPackageType=None`.
17. Deploy: `dotnet build ... -f net10.0-android -t:Install -p:EmbedAssembliesIntoApk=true -p:AdbTarget="-s RFCY1198TTX"`.
18. **Nie ogłaszaj sukcesu** przed: build + restart apki + zrzut usera + potwierdzenie usera.
19. Nie wszczynaj spirali na kosmetyk — mały problem ≠ przebudowa.
20. Odpowiadaj po polsku (kod/commity EN); commity **bez** Claude jako autora.
