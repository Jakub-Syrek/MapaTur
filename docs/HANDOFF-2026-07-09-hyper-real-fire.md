# Handoff — 2026-07-09 (wieczór): Ogień smoka → HIPER-REALISTYCZNY (desktop-only, GLES 3.0)

> Stan wejściowy nowej sesji. Branch roboczy: **`feat/walk-mode`** (zmergowany do `main` w `2f94bd0`).
> Bramki przed pushem: `dotnet format MapaTur.slnx --verify-no-changes` + pełne `dotnet test` (Domain 144 /
> Routing 29 / Infrastructure 142 / Application 1432). **NIGDY Claude jako autor/co-author.** Build desktop:
> `dotnet build src/MapaTur.App/MapaTur.App.csproj -f net10.0-windows10.0.19041.0`; ubić `MapaTur.App*` przed
> buildem; `Start-Process` exe; logi w `win-x64\logs` (utf-8). Sprawdzaj datę `MapaTur.App.dll` NIE `.exe`
> (pułapka stale-exe). Poprzednie handoffy tej linii: `docs/HANDOFF-2026-07-09-offtrail-dragon.md`.
>
> **⚠️ Ograniczenie od usera:** **DESKTOP ONLY** — smoki/ogień NIE odpalają się na telefonie. Nie budżetuj pod
> mobile. API zostaje **GLES 3.0** (GLSL `#version 300 es`, brak compute), ale zakładaj mocne GPU desktop:
> ciężki raymarching per-piksel, HDR float FBO, dodatkowe passy i sim po CPU są **wszystkie na stole**.
> Optymalizuj pod **maksymalny realizm**, nie pod budżet klatki.

## Co zjechało na `main` w tej sesji (NIE robić od nowa)
Commit `35bbe6d` (merge `2f94bd0`): **stado AI** (3 smoki, `DragonAiPilot` + 5 testów, toggle „Smoki AI") +
**walka ogniem** (naprowadzanie ±10°, homing, zabijanie, ogień z pozowanej kości głowy) + **przebudowa ognia**
(premultiplied additive, wolumetryczny billboard z FBM/domain-warp, rampa temperaturowa, eksplozja etapowa
flash→puff→shock→iskry→dym, **para nad jeziorem**). Szczegóły: `[[walk-and-dragon-modes]]` w pamięci.

---

## Plik(i) w grze
- `src/MapaTur.App/Services/Terrain3DGlRenderer.cs` — `fireProgram` FS/VS (`EnsureFireProgram` ≈6302, fragment
  ognia ≈6356–6418), `DrawFireballs`/`UploadAndDrawFireList` (≈6466), tworzenie FBO (`MakeColorTexture` ≈4461,
  present/post ≈4236/4406, MSAA renderbuffer ≈4899), blit głębi do `ghostDepthTex` (≈3735–3745, **po**
  `DrawFireballs` przy 3688), terrain FS `lightSum`/`lit` (476–482 / 671), hack progu blooma (3924/3930),
  `AcesGlsl` (≈983), `BloomBlurFragmentShaderSource` (≈1022).
- `src/MapaTur.App/Views/Terrain3DView.xaml.cs` — sim CPU: `StepDragonFire` (≈5699), `SpawnFireBurst` (≈5883),
  `SpawnSteamBurst`, `StepFireParticles` (≈6021), `FireParticle`/`FireKind`, `DragonFireMaxBalls = 72` (2060).

**Fakt platformowy który wiąże KAŻDY shader poniżej:** desktop renderuje przez **SKGLView → ANGLE → D3D11**
(SwapChainPanel). Dwie twarde zasady: (a) `EXT_color_buffer_float` **JEST** dostępny (float render targets działają)
— ale zprobuj raz i trzymaj `hdrUnsupported` fallback na Rgba8 (wzór istniejących `*Unsupported`); (b) **każda
pętla raymarch/light MUSI mieć stałą compile-time granicę + `break`** — pętla o zmiennej liczbie iteracji NIE
kompiluje się na ANGLE→D3D11.

## Co już dobre (zostawić)
- **Additive premultiplied ognia jest poprawne z konstrukcji** (`One,One` + `col*coverage, coverage`) — nakładki
  sumują się LINIOWO; stary bug `SrcAlpha,One` (kwadratowanie) naprawiony. Zostawić blend.
- **6 rozdzielonych rodzajów** (`vKind`): FLASH(1) / SHOCK(2) / EMBER(3) / PUFF(4) / SMOKE(5)/STEAM(6) pass alfa /
  FLAME(0). Nowa robota wchodzi w te branche.
- **Sin-free hash value-noise + 2-oktawowy fbm + domain warp** (`h21`/`vn`/`fbm`) z per-kula `nOff` + rotacją.
  Rozszerzać do 3D, nie wymieniać.
- **Velocity-stretch** (kometa), kaskada eksplozji, iskry balistyczne, rozdzielony dym/para — zrobione.
- **Infrastruktura głębi + tonemap JUŻ JEST**: `ghostDepthTex` (unit 7) z liniaryzacją (1456–1487), ACES w
  kompozycie blooma. **Reużywamy**, nie budujemy od nowa.

## Pozostałe „to naklejka" tells
1. **Fałszywa grubość** — PUFF/FLAME cieniują dysk 2D przez `z=sqrt(1-r*r)`; zero realnej głębi/parallaxu.
2. **8-bit clamp** — wszystkie FBO `Rgba8` → nakładki i biało-gorący rdzeń **klipują do płaskiej bieli** ZANIM ACES
   zadziała; energia `T⁴` i uczciwy bloom niemożliwe (bloom udawany progiem 0.72).
3. **Cięcie skały** — ogień depth-tested, ale bez soft-fade → ostra krawędź sprite'a na grani/smoku.
4. **Ogień nic nie oświetla** — czysty additive nie dodaje radiancji terenowi/brzuchowi smoka/dymowi.
5. **Ruch „przesuwanej tapety"** — gęstość tylko scrolluje `q.y -= t`; brak wirów/wyporu/grzyba.
6. **Malowany kolor** — ręczne rampy; jasność nie śledzi temperatury.
7. **Ostre tło** — brak heat-haze (grań/niebo za pióropuszem idealnie ostre).
8. **Płaskie krążki sadzy** (SMOKE) — bez grubości/samocieniowania, tnie teren.

---

## PLAN

### Faza A — największe skoki realizmu
**Największa pojedyncza dźwignia = A2 (prawdziwy per-fragment volumetric raymarch).** Zamienia płaskie świecące
naklejki w realny gaz z wewnętrzną głębią. **Bramkowana przez A0** (HDR float FBO, tanie) → rób A0 pierwsze, A1
obok, potem A2.

**A0 — HDR float scene FBO end-to-end (enabler; NAJPIERW).** `Rgba8 → Rgba16f` (`HalfFloat`) dla łańcucha
**scene/resolve/present** (alfa potrzebna do premultiplied-OVER dymu + niebieska mantysa rdzenia); **`R11F_G11F_B10F`**
dla **mipów blooma** (bez alfy, taniej). MSAA renderbuffer → `Rgba16f`; resolve `BlitFramebuffer` zostaje `NEAREST`
tego samego formatu. **`postColorTex` ZOSTAJE `Rgba8`** (to LDR hand-off który Skia owija jako `SKImage`; kompozyt/ACES
= jedyny krok HDR→LDR). Zprobuj `EXT_color_buffer_float`, latch `hdrUnsupported`→fallback. Potem **usuń hack
`bloomThreshold = min(threshold, 0.72)` (3930)**, próg ~1.0–1.2 → bloomują tylko fragmenty realnie >1.
*Zabija #2.* **transformative / medium.** Ryzyko: każde FBO próbkujące inne musi zgadzać format; `R11F_G11F_B10F`
BEZ alfy (audytuj `BlitFramebuffer` po zamianie).

**A1 — Blackbody emission + energia T⁴ (razem z A0).** Do PUFF (≈6386) i FLAME (≈6411): `vec3 blackbodyLinear(float T)`
(Planckian locus → CIE xy → linear sRGB, fit Krystek/Kim ≈30 FLOP; albo LUT `256×1`). `float T = mix(1300.0, 7000.0,
heat)`. Rozdziel barwę od jasności: `chroma = bb/luminance(bb)`; `radiance = uFireGain * pow(T/2600.0, 4.0)`;
`emit = chroma*radiance`. **Usuń** boosty rdzenia `col *= 1.0 + 2.5*smoothstep(...)` (6389/6414); zostaw form-shading
`0.55+0.45*z`. Wyjście premultiplied. Uniform `uFireGain` (start 1.0–3.0) — jasność ognia w ACES shoulder **bez**
ruszania globalnej ekspozycji (albedo terenu ~[0,1]). *Zabija #6.* **transformative / low** (czyta się dobrze tylko
na HDR z A0; na Rgba8 klipuje natychmiast).

**A2 — Prawdziwy per-billboard volumetric raymarch, próbkowany w przestrzeni ŚWIATA (DŹWIGNIA).** VS dodaje
`out vec3 vWorldPos` + `flat out vec3 vCenter`; `DrawFireballs` dodaje `uniform vec3 uCamPos`. Zamiast dysku —
całka emisja-absorpcja: rekonstruuj `rd = normalize(vWorldPos - uCamPos)`, analitycznie przetnij sferę (lub
**elipsoidę** wyrównaną do `vVel` dla komet — transformuj `ro/rd` do ramki elipsoidy → rozciągnięty kształt staje
się realną bryłą 3D), marsz **`const int STEPS = 32`** front-to-back:
```glsl
vec3 acc=vec3(0.0); float tr=1.0; float t=t0+dt*jitter;
for(int i=0;i<STEPS;i++){ vec3 wp=ro+rd*t;
  float d = densityAt(wp,vCenter,R,vSeed);            // envelope × world-space fbm3, erozja -0.35
  if(d>0.001){ float T=tempAt(wp,d);
    vec3 emit = blackbodyLinear(T)*pow(T/2600.0,4.0)*uFireGain*lightMarch(wp);
    float a=1.0-exp(-d*sig*dt); acc+=tr*emit*a; tr*=1.0-a; if(tr<0.02) break; }
  t+=dt; }
frag=vec4(acc*vIntensity, 1.0-tr);
```
**Krytyczne:** próbkuj pole gęstości w **przestrzeni ŚWIATA** (`wp`), NIE per-billboard UV — sąsiednie impostory w
tym samym regionie świata czytają tę samą gęstość i **zlewają się w jedną spójną turbulentną kolumnę** zamiast
sznurka koralików. Rozszerz `fbm` do 3D (`fbm3`/`vn3` z `h21`). Skaluj `STEPS` rzutowanym rozmiarem ekranowym.
EMBER/FLASH/SHOCK zostają na tanim 2D. *Zabija #1.* **transformative / high.** Ryzyko: stała granica pętli + `break`
(ANGLE); jitter startu (blue-noise/hash) zamienia banding na drobny szum.

**A3 — Curl-noise buoyant advection (wewnątrz A2).** W `densityAt` warpuj pozycję przez pole curl (bezdywergentne)
przed fbm3: `q += curlNoise(q)*1.8; q += vec3(0,0,uTime*1.6)` (+Z = wypór); amplituda curl rośnie z wysokością w
burście (końce wirują szybciej). CPU: w `StepFireParticles` dodaj analityczny 2D curl do `Vel` dymu/puffów → makro
pióropusz też wiruje. *Zabija #5.* **high / medium.**

### Faza B — głębia, światło, haze (osadzenie w scenie)
**B1 — Soft-particle depth fade (reużyj `ghostDepthTex`).** Wynieś blit głębi (3735–3745) do
`ResolveSceneDepthToGhost(...)` i wołaj **raz zaraz po `DrawAiDragons` (3687), PRZED `DrawFireballs`** (ogień zanika
też na smokach). Do fire FS: `uniform sampler2D uSceneDepth; uniform vec2 uViewport,uDepthNearFar; uniform float
uSoftRange;` → `float fade=clamp((linS-linF)/uSoftRange,0,1); frag*=fade;` (skaluje premultiplied kolor I alfę). W
A2 też `t1 = min(t1, linS)` (bryła zatrzymuje się na skale). `uSoftRange` (m): ember 1.5 / flame 6 / puff 10 /
smoke 40. Guard `fade=1` gdy `ghostDepthOk` false. *Zabija #3.* **high / low** (ogień musi próbkować ROZWIĄZANY
single-sample `ghostDepthTex`, nie MSAA — blit to daje).

**B2 — Ogień jako dynamiczne światło (teren + smok + dym).** CPU (po `StepDragonFire`): zredukuj sprite'y do `N≤8`
świateł (score `intensity*radius²`, greedy-merge w promieniu, top 8; użyj **eksagerowanego-Z** world-pos jak
sprite'y). `uFireColor[i] = tempRamp(heat)*intensity*flicker` (deterministyczny per seed). Terrain FS — wstrzyknij
po zbudowaniu `lightSum` (przed podłogą ambientu, ≈482), pętla po `uFireCount` z `att=1/(1+d2*invR2)`² i wrap-floor
0.25; `lit = base*lightSum` (671) łapie automatycznie, a pass odbicia w wodzie reużywa ten sam program → ląduje na
obu ścieżkach za darmo (reguła checklisty „wszystkie ścieżki" spełniona gratis). Smok FS — `out vec3 vWorldPos` +
ta sama pętla. Dym (6371) — te same uniformy w centrum billboardu. *Zabija #4 i #8.* **transformative / high**
(głównie plumbing: cache `GetUniformLocation("uFirePos[0]")`, `Uniform3(loc,8,ptr)`/klatkę). Ryzyko: tablice stałego
rozmiaru + pętla gated `uFireCount` = rdzeń ES3.0. **⚠️ dotyka terrain shadera → obowiązuje `docs/TERRAIN-GRAPHICS-CHECKLIST.md`.**

**B3 — Heat-haze (refrakcja).** Pierwszy stage w `RunPostProcess` (czyta `presentColorTex` po resolve ≈3904).
Half-res `R16F/RG16F` maska ciepła (przerysuj billboardy ognia pisząc `intensity` + niewidzialne „plume" sprite'y
w górę); full-res haze FS offsetuje UV sceny gradientem curl/fbm scrollującym W GÓRĘ (konwekcja) × lokalne ciepło +
lekki chromatic split; do blooma karm zniekształcony obraz. **Depth-gate** przez unit 7 (skała/smok z przodu nie
rozmywa). Gate na `fireballs.Count > 0`. *Zabija #7.* **medium–high / medium** (`uHazeStrength` kilka px max).

**B4 — Scorch + bounce-flash przy trafieniu w teren (opcjonalny polish).** W gałęzi trafienia terenu `StepDragonFire`
(≈5834): (a) dodatkowe **bounce-flash** światło do B2 nad kraterem, zanik ~0.18 s; (b) **trwały scorch**: additive-darken
splat do top-down `R8` pola world-XY; terrain FS próbkuje przez istniejące `uOrthoMinXY/MaxXY` → `base *= 1.0 - 0.6*scorch`.
Cap ~16. **medium / medium.**

### Faza C — pełny volumetric / baked (tylko gdy A/B nie wystarczy)
- **C1 — Bloom pyramid (Karis + dual-filter, CoD/Jimenez)** zamiast single half-res 9-tap; Karis-average na PIERWSZYM
  downsampleu (kill HDR fireflies bez twardego clampa). Mipy `R11F_G11F_B10F`. **high / high** (po A0).
- **C2 — Blue-noise dithered march + TPDF output dither** — czyste 24–32 kroki + brak bandingu przy 8-bit
  `postColorTex`. **low–medium / low.**
- **C3 — Baked 3D-tex fluid sim TYLKO dla hero-blastu** (EmberGen/Mantaflow → ~48 klatek `64³ RGBA16F` `TEXTURE_3D`;
  reużyj pętlę A2, `densityAt`→`mix(texture(uVol0),texture(uVol1),f)`). `sampler3D`/`TexImage3D` **rdzeń ES3.0**,
  ~4 MB. **transformative dla grzyba zabójczego blastu / very-high** (pipeline assetów + seamless loop).

## Rekomendowana kolejność (każdy niezależnie shippable + testowalny)
1. **A0** (HDR FBO) — keystone; nic w kolorze/energii nie działa póki >1 nie przeżyje. Zweryfikuj że ogień nie
   klipuje do bieli i hack 0.72 zniknął.
2. **A1** (blackbody + T⁴ + `uFireGain`) — natychmiastowy dramatyczny skok koloru na A0.
3. **B1** (soft depth fade) — low effort, high payoff, reużywa sprawdzoną infra; zabija najbardziej widoczny tell.
4. **B2** (ogień oświetla świat) — największy skok **osadzenia**; głównie plumbing.
5. **A2 (world-space raymarch) + A3 (curl)** — transformująca dźwignia geometrii; najwyższy effort po de-riskowaniu.
6. **B3 (haze)** → **B4 (scorch)** — polish.
7. **C1/C2** — hardening jakości. **C3** — tylko jeśli user chce film-grade *blast zabójczy* i akceptuje pipeline.

## Decyzje konfliktów (rozstrzygnięte w planie)
- **Proceduralny raymarch vs baked flipbook →** proceduralny (A2), baked TYLKO dla hero-blastu (C3). Proceduralny =
  100% przypadków, zero pipeline'u, parametryczny (każdy blast inny per seed).
- **Per-billboard raymarch vs single-box metaball (UBO 72 kul) →** per-billboard z próbkowaniem world-space (A2) =
  ~80% fuzji za dużo mniejsze ryzyko integracji. Metaball UBO = fallback Fazy C jeśli A2 pokaże szwy/pulsowanie.
- **`R11F_G11F_B10F` vs `RGBA16F` →** scene/resolve/present = **`RGBA16F`** (alfa + niebieska mantysa); mipy blooma
  = **`R11F_G11F_B10F`**. Oba za jednym probe + fallback.
- **Tonemap →** zostaw Narkowicz default + instant rollback (jego wczesne orange→white desaturuje rdzeń — pożądane
  dla żarzenia); Hill-fitted jako A/B za istniejącym `uTonemap`. **NIE podnoś globalnej ekspozycji** — użyj `uFireGain`.

## Otwarte pytania do usera
- Budżet perf: A2 (32 kroki × 4-oktawowy fbm3) + B2 (8 świateł na obu ścieżkach terenu) = desktop-fine ale nie darmowe.
  OK bramkować A2 rzutowanym rozmiarem i dalekie kule na tanim 2D?
- C3 pipeline: chcesz baked hero-blast (czas bake EmberGen/Blender + kilka wariantów seedów) czy w pełni proceduralny
  A2 wystarczy? (rekomendacja: odłóż do czasu aż A/B wyjdą).
- Tonemap A/B: biało-gorący zdesaturowany rdzeń (Narkowicz) czy bardziej-nasycony pomarańcz (Hill)? (rekomendacja:
  shipnij oba, wybór w apce przez `uTonemap`).
- Heat-haze zakres: tylko nad widocznym płomieniem, czy kolumna w górę (ciepło się unosi)?

## Params do strojenia
`uFireGain` 1.0–3.0 · blackbody `T = mix(1300,7000,heat)`, rdzeń do 7000–9000K, `pow(T/2600, 3.0–4.0)` ·
raymarch `STEPS=32`, `sig ≈ 2.5/R`, shadowSteps ≤4 (1 oktawa) · `uSoftRange` ember1.5/flame6/puff10/smoke40 ·
curl `uCurlAmp≈1.8`, wypór +Z ≈1.6; CPU wind-couple dym ~0.8/s, puff ~0.2/s, wypór ~6 m/s² zanik ~0.9s ·
światła `N=8`, `invR2=1/(3R)²`, wrap-floor 0.25 · `bloomThreshold ~1.0–1.2` (drop 0.72) · `uHazeStrength` kilka px ·
ember `T0≈1600K`, cool `exp(-Age/0.35)`.

## Źródła (SOTA — zweryfikuj przed implementacją)
- Volumetric raymarch: `mini.gmshaders.com/p/volumetric`, `blog.maximeheckel.com/posts/real-time-cloudscapes-with-volumetric-raymarching/`
- Blackbody: `github.com/zubetto/BlackBodyRadiation`, `giangrandi.ch/optics/blackbody`
- Soft particles: `dev.to/keaukraine/implementing-soft-particles-in-webgl-and-opengl-es-3l6e`
- Curl noise: `emildziewanowski.com/curl-noise/`
- Bloom: Jimenez/CoD „Next Generation Post Processing in Call of Duty: Advanced Warfare"
- Baked sim (compute-only — bake offline, NIE odpali w ES3.0): `jangafx.com/software/embergen`, Blender Mantaflow,
  Niagara Fluids

---

## Otwarty punkt NIE-ognia (do dobicia lub odpuszczenia)
**Bug Spacji** — w locie F7 Spacja chowa panele I macha skrzydłami; user chce tylko skrzydła. Fix (📷 button
`IsTabStop=false` + `AllowFocusOnInteraction=false` + focus na kanwę przy wejściu w lot) **NIE potwierdzony że
działa** — dołożone logi `[UI] ToggleUi invoked` i `[Dragon] Space (flap/takeoff)`. Recepta: user naciska Spację w
locie → odczytać log; jeśli leci `[UI] ToggleUi` to jednak coś woła komendę (inny sfokusowany przycisk / akcelerator).
