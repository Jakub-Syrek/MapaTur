# Proceduralna rzeźba skał pod wspinaczkę — PLAN

> Analiza 2026-07-19 (sesja po HANDOFF-2026-07-18-climbing-routes-topo). Cel: ściana wspinaczkowa ma
> pokazywać RZEŹBĘ (chwyty, rysy, półki), a nie gładki DEM z kolorowymi kulkami.

## 1. Stan wyjściowy (fakty z kodu)

- Najdrobniejsza realna geometria = ~1 m (`SampleWalkGround` → FineElevationSampler). Wszystko poniżej
  1 m NIE istnieje w danych — mikrorzeźba musi być w 100% proceduralna.
- Fizyka jest gotowa: `ClimbSurfacePatch` to mesh trójkątowy z BVH („can represent true overhangs");
  `TryReplaceSurface` przy wzroście patcha wymaga odtworzenia CHWYTÓW (id + pozycja ±5 cm), ale mesh
  może się różnić („hold identity … never in the visual terrain LOD").
- Chwyty to abstrakcyjne punkty: hash-scatter ~2,5/m² (kod sam nazywa go placeholderem,
  `GripClimbController.cs` ~609) + gwarantowane drabinki dróg (`ClimbingRouteHoldSeeder`, co 0,38 m).
- **Fizyka już IMPLIKUJE rzeźbę**: `ClimbHold.ContactOffsetMeters` = jug 21 cm, sloper 18 cm, crimp
  14 cm, foot edge 4 cm od ściany. Dziś dłoń trzyma powietrze — rzeźba = zmaterializowanie tych offsetów.
- `ProceduralBoulderMesh` (icosphere + loby + facety, deterministyczny) — gotowy fundament, niewpięty.

## 2. Inwariant nadrzędny — jedno źródło prawdy

Rzeźba = JEDNA deterministyczna funkcja pozycji świata `R(x,y,z)`, konsumowana przez wszystkie ścieżki
naraz (odpowiednik §C.10 sagi rowków): mesh wizualny, patch fizyczny (clearance ≥ 0.015, contactErr
≤ 0.09), generator chwytów, linie tras + siatka `M` (muszą próbkować powierzchnię PO displacement).
Rozjazd wizual↔fizyka = powrót klasy błędów „tak się człowiek nie składa".

## 3. Filozofia: holds-first (potem hybryda)

- **Holds-first (wybrane)**: chwyty pozostają źródłem semantyki; geometria to ODCISK chwytu per typ
  (jug = klamra na pełne 21 cm, crimp = listewka, sloper = kopułka, pocket = wgłębienie/rant,
  foot edge = półeczka `UsableWidthMeters`). Zgodność wizual↔fizyka z definicji, drogi z topo
  gwarantowane, TDD-owalne (punkt chwytny odcisku == `ContactPoint` ± ε).
- Geometry-first (detekcja chwytów z krzywizny mesha) odrzucone: nie gwarantuje przejść, niestabilne
  przy re-tessellacji. Docelowo ekstrakcja dodatkowych chwytów ANALITYCZNIE z pola R (rysa → linia
  jam-holdów, lip półki → FootEdge) — bez mesh-processingu.

## 4. Pole R — trzy skale (przepis, granit tatrzański)

| Skala | Formy | Technika |
|---|---|---|
| Makro 1–10 m | rysy, kominy, zacięcia, półki | 2–3 rodziny quasi-równoległych płaszczyzn spękań (orientacja per masyw + jitter; SDF → profil rowka; przecięcia → kominy); terracing wzdłuż spadku → półki |
| Mezo 0,2–1 m | łuski, bloczki, facety | ridged multifractal + domain warp + facetowanie; ⚠️ lekcja granitu v7: stała krata + jitter + warp, NIGDY zmienny rozmiar komórki w `floor(pos/size)` |
| Mikro 2–20 cm | chwyty | odciski per typ, seed = id chwytu (stabilny hash, NIE `string.GetHashCode`) |

Render: overlay-skin na stromiznach (>~37–45°, spójnie z `MinForwardSlopeGrade` i rampą granitu),
w ringu wokół wspinacza, displacement wzdłuż NORMALNYCH (nie Z), materiał granit v7. Displacement
bazowego heightfieldu ODPADA (73° ściana, brak przewieszeń, blast radius całego pipeline'u terenu).
Patch fizyczny może zostać na siatce 0,5 m displacowany tym samym R — gęste 5–10 cm potrzebuje tylko
wizual; mikro-detal niesie analityczny `ContactPoint`.

## 5. Fazy (każda mała, build → werdykt usera)

1. **F1 — odciski chwytów** ✅ zrobione, WERDYKT USERA: ODRZUCONE wizualnie („sterta chwytów w kolorkach,
   nie górska rzeźba") — per-chwytowe bloby nie czytają się jako skała. `ClimbHoldImprintMesh` zostaje
   (dostarcza `ProtrusionMeters` — jedyne źródło wysokości chwytu — i generator blobów na później);
   lekcja: rzeźba musi być JEDNĄ ciągłą powierzchnią, nie sumą stempli.
2. **F2a — ciągła skóra skalna (wizual)** ✅ (TEN commit): `ClimbRockReliefField.Relief01` (JEDNO pole:
   2 rodziny spękań + poziome przełamy/półki + facety na stałej kracie 1,1 m z domain warp — lekcja v7)
   + `ClimbRockSkinMesh` — world-aligned siatka 0,15 m w oknie ±15 m wokół wspinacza, displacement
   WZDŁUŻ normalnej = `fade(slope)·(0,02 + 0,20·Relief01)`, chwyty WBLENDOWANE w tę samą powierzchnię
   (przy chwycie skóra morfuje dokładnie do `ProtrusionMeters` — jug=guz skały, foot edge=nacięcie).
   Outward-only (nigdy pod bazową powierzchnię → teren nie zasłania, ciało nie wchodzi w skałę),
   slope-gate (płaskie = zero skóry), jednolity granit (ciemniej w szczelinach = pseudo-AO), kropki
   TYLKO aktywne kontakty + kandydaci wybranej kończyny. Fizyka NIETKNIĘTA (apex chwytu = ContactPoint).
3. **F2b — displacement patcha fizyki** 0,5 m tą samą funkcją (skóra staje się też fizyczna między
   chwytami); trasy/siatka `M` próbkują powierzchnię po displacement; bramki contactErr/clearance
   monitorowane (log `climb.whole_body_solved`); headless przejście drabinek dróg z topo.
3. **F3 — ekstrakcja chwytów z R** (rysy → jam/crimp, lipy → FootEdge) zastępuje część hash-scatter;
   scatter zostaje fallbackiem gęstości.
4. **F4 — mikro-shader** (parallax + AO w rysach) zlany z granitem v7, tylko w ringu.

Synergia z otwartym pkt. 1 handoffu 07-18 (proceduralne głazy): wspólny generator/materiał/instancing;
scatter wolnostojących głazów wg `PLAN-ortho-scatter.md` §2.

## 6. Bramki zgodności (checklist przed każdą fazą)

- contactErr ≤ 0.09: odcisk przechodzi przez `ContactPoint` (test TDD F1).
- clearance ≥ 0.015: mierzyć pass-rate przed/po F2.
- `TryReplaceSurface`: determinizm w pozycji świata (spełnione automatycznie).
- Exaggeration: budowa w real space, `Z*exag` na uploadzie/w shaderze (normalna: `n.z / zs`).
- Kolejność passów: teren → **odciski** → trasy → kropki chwytów → wspinacz.
- MINIMUM TERENU: nowa warstwa = komplet korekt zanim user zobaczy (ambient floor — nigdy czarne
  facety; feather brzegu ringu w F2; brak dziur — seat 10 cm).
- Woda nietknięta (tylko stromizny); flatW poza zasięgiem.
- Perf: log czasu budowy batcha + `climb.patch_grown`.

## 7. Ryzyka

1. Rozjazd wizual↔fizyka — jedna funkcja + testy zgodności + logi contactErr.
2. Regres przejść dróg z topo po F2 — test headless.
3. Perf grow (BVH + rebuild batcha) — mierzyć.
4. Estetyka („wysypka bąbli" przy 2,5 chwytach/m²) — wariacja skali z quality, obrót z seeda,
   facety; werdykt usera po F1 decyduje o gęstości/rozmiarach.
