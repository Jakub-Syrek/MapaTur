# Handoff — 2026-07-10: SMOCZY EPIK DOMKNIĘTY (ogień A0→B4 + sterowanie + audio + perf) — GRUBY

> **Stan wyjściowy dla nowej sesji.** Branch `feat/walk-mode`, ZMERGOWANY do `main` (2026-07-10 po południu).
> Bramki przed każdym pushem: `dotnet format MapaTur.slnx --verify-no-changes` + pełne `dotnet test`
> (Domain 144 / Routing 29 / Infrastructure 142 / Application 1438 = **1753**) + build OBU TFM
> (`net10.0-windows10.0.19041.0` **i** `net10.0-android` — android się PSUŁ, patrz §7). **NIGDY Claude jako
> autor/co-author.** Desktop: ubić `MapaTur.App*` przed buildem (lock exe), sprawdzać datę `MapaTur.App.dll`
> NIE `.exe`; logi `win-x64\logs` (utf-8). Smoki/ogień/audio = **desktop-only** (mobile nie odpala F7/F8).
> Poprzednie handoffy tej linii: `HANDOFF-2026-07-09-hyper-real-fire.md` (plan ognia),
> `HANDOFF-2026-07-10-dragon-polish.md` (status + epiki), `HANDOFF-2026-07-09-offtrail-dragon.md`.

## 0. Co dowieziono w tej sesji (commity `5f7272b` → `6baf672` + merge)

| Obszar | Stan | Werdykt usera |
|---|---|---|
| Ogień: **A0** HDR float FBO | ✅ | działa (log `HDR scene targets ON`) |
| Ogień: **A1** blackbody + T^3.5 + `uFireGain` | ✅ | przyjęte |
| Ogień: **B1** soft particles | ✅ | „ładny" |
| Ogień: **B2** ≤8 świateł dynamicznych | ✅ | „jest ok" |
| Ogień: **A2+A3** world-space raymarch + curl | ✅ | „spoko" + strojenie |
| Ogień: **B3** heat haze | ✅ | „super" |
| Ogień: **B4** scorch | ✅ | „działa" |
| Strumień: szybszy/nieregularny/zlany | ✅ | „dobra" |
| Skręcanie precyzyjne (expo+spring+bramka) | ✅ | „jest ok" (+tap podkręcony) |
| Audio 2.0 (bed + sample Pixabay) | ✅ | „jest spoko" |
| Perf lotu 12→~30 fps | ✅ | mierzalne w `[DragonPerf]` |
| TFM android naprawiony | ✅ | build green |

## 1. OGIEŃ — mapa techniczna (wszystko w `Terrain3DGlRenderer.cs` + `Terrain3DView.xaml.cs`)

**Łańcuch HDR (A0):** scena/resolve/present = **RGBA16F** (`WantHdrTargets` sonduje
`GL_EXT_color_buffer_float` raz na kontekst; KAŻDY niekompletny HDR-FBO latchuje `hdrUnsupported` i cała
rodzina wraca RAZEM do Rgba8 — formaty resolve-blitu muszą się zgadzać); mipy bloom/godray =
**R11F_G11F_B10F** (`MakeColorTexture(hdr)`); `postColorTex` ZOSTAJE **Rgba8** — to jedyny hand-off do Skii
(`GRGlTextureInfo` z `GL_RGBA8` na sztywno w view!), a composite/pass-through z ACES = jedyny krok HDR→LDR.
Hack progu blooma 0.72 tylko na LDR-fallbacku. Nagrywanie wideo czyta `lastPresentedFbo` (post-FBO, zawsze
LDR — ReadPixels UNSIGNED_BYTE z float-FBO by padło).

**Kolor fizyczny (A1):** `blackbodyLinear(T)` = fit Kim et al. (Planckian locus → CIE xy → XYZ → linear
sRGB, clamp 1667–10000 K); `fireEmit(heat)`: `T = mix(1300, 7000, heat²)` (heat² = ciasny biały rdzeń),
chroma znormalizowana luminancją × `uFireGain·(T/2600)^3.5`. `FireGain = 1.3f` (const, zakres 1–3).

**Raymarch (A2+A3):** FLAME(0)+PUFF(4) = wolumetryczne; FLASH/SHOCK/EMBER/dym = tanie 2D. VS daje
`vCenter/vRadius/vAxis/vStretch` (elipsoida: oś = prędkość świata, ra=R·st, rc=R/√st — zgodnie z quad
stretchem; quad ×1.15 na perspektywiczny rant). FS: przecięcie elipsoidy analitycznie, **STEPS=20** ze
stałą granicą + break (twarda reguła ANGLE), jitter startu `h21(gl_FragCoord+vSeed)`, całka
emisja-absorpcja front-to-back, `sig=2.5/R`, przerwanie przy tr<0.02. **Pole gęstości WSPÓLNE w przestrzeni
świata**: `q=wp·0.14; q+=swirl3(q·0.55)·1.2; q.z-=uTime·1.4` + `fbm3` (h31/vn3, 2 oktawy), erozja
`clamp(n·1.5−0.35)`, obwiednia `1−smoothstep(0.42,1,rn)` (0.42 = tłusty rdzeń → sąsiedzi się ZLEWAJĄ; przy
0.30 strumień był różańcem). ZERO per-kulowych offsetów w polu 3D — inaczej fuzja umiera. Samocień 1-tap
(`fbm3(q+0.7z)`). CPU-makro-wir dymu w `StepFireParticles`.

**Soft particles (B1):** `ResolveSceneDepthToGhost` = JEDEN blit głębi na klatkę (po smokach, przed ogniem;
`ghostDepthFrameValid` reset w Render), współdzielony z bramką x-ray szlaków (która wcześniej blitowała
sama). Fire FS `sceneGapMeters()` — identyczna liniaryzacja jak w line-shaderze. Zakresy: ember /1.5 m,
flame+raymarch /6–8, puff 10, dym-para /40 (alfa-only, straight alpha!).

**Światła (B2):** `PushFireLights` (view) — zachłanne scalanie sprite'ów do ≤8: score=I·r², merge
w promieniu max(2.5R,18 m), pozycja ważona; kolor (1.0,0.58,0.24)·I·flicker; `invR2=1/(3R)²`. Terrain FS:
pętla przed podłogą ambientu (C.3 checklisty NIETKNIĘTA), wrap-diffuse (dot+0.25)/1.25, atenuacja
(1/(1+d²·invR2))², próbkuje `vStableWorldPos` (C.1 — rama absolutna). Śnieg ma osobny tor → `snowLit +=
snowAlbedo·fireGlow·0.8`. Smoczy FS (vWp) + smoke-branch fire FS — te same uniformy. Upload PRZED pasem
odbić → tafla świeci gratis. ⚠️ pułapka Silk.NET: `uFireCount` jako FLOAT (Uniform1(int) na GLSL float =
cichy no-op).

**Heat haze (B3):** maska half-res (R16F na HDR / R8; `fireHeatProgram` = fire-VS + mini-FS z bramką głębi
w shaderze → zasłonięty ogień nie grzeje; falloff rozciągnięty W GÓRĘ), rysowana Z TEGO SAMEGO VBO zaraz po
pass 1 (`fireListVertexCount`); stage refrakcji PIERWSZY w RunPostProcess (bloom dostaje zniekształcony
obraz): gradient vn-szumu scrollujący w górę × min(heat,1.6) × `HazeStrength=0.0045` + rozszczep R/B
1.15/0.85. `hazeMaskValidThisFrame` twardo zerowana co klatkę (bez ognia = zero falowania).

**Scorch (B4):** ≤24 splatów-UNIFORMÓW (`uScorchPos[24] vec2 + uScorchParam[24]` x=r², y=siła) — zero
tekstur; pierścień w view (`dragonScorchPos/Param/Next/Count/Dirty`), wpis przy trafieniu w teren
(r=6+4·power, siła 0.75), `SetScorchMarks` przy dirty. FS: `base = mix(base, base·(0.16,0.14,0.13),
clamp(scorch,0,0.85))` PRZED `lit=base·lightSum` → światło (w tym łuna ognia!) gra po zwęglonym albedo;
smoothstep d² bez sqrt. Odbicie wodne gratis. Bounce-flash NIE istnieje osobno — puffy eksplozji same są
światłami B2 przez swój lifetime.

**Emiter (strojenie po werdyktach):** catch-up (`while cooldown<=0`: lateBy=-cooldown; pozycja
+dir·spd·(lateBy−dt) i Age=lateBy−dt, bo pętla integracji ZARAZ doda vel·dt — bez tego „ogień 5 m przed
pyskiem" ∝ vel×frame-time); cooldown 0.034 s ×(0.65+0.7·hash) (metronom = maszyna); spd 105 m/s ponad smoka
(nurkowanie nie dogania własnego ognia), TTL 2.2 s, rozrzut prędkości ±22%, rozmiar 0.65–1.4×, młode kule
0.58× bazy (overlap!), cap 88. Burnout-smoke z kul TTL (dziedziczą 22% pędu). `DragonSnoutOffsetMeters=5`.

## 2. STEROWANIE (DragonFlight — czyste, 29 testów)

Wejście → `yawCommand` ładowane atak **0.28 s** / rozładowanie **0.18 s** (tap 100 ms ≈ 29–36% komendy);
**expo 1.3** (`sign·|c|^expo`); cel banku = shaped·MaxRoll śledzony **krytycznie tłumioną sprężyną**
(`rollVelocity += (ωn²(target−roll) − 2ωn·v)·dt`, ωn=6, anti-windup na ograniczniku, freeze przy
holdPitch bez wejścia). `YawCommand` public → view bramkuje turn-entry stroke na przekroczeniu **0.6**
(`dragonPrevYawCommand`, kierunek=sign(cmd) — mysz też uzbraja); **usunięty instant +50% max-roll kick
z TurnImpulse** (główny winowajca szarpania). Zakręt skoordynowany `ψ̇=g·tan(φ)/V` bez zmian. Testy-piny:
tap ≤7° bez lurchu, monotoniczny bank, powrót bez przejścia przez 0, bramka, expo-proporcja, discharge.

## 3. AUDIO (DragonAudioService — partial: wspólne API + Platforms/Windows impl)

**Architektura:** partial class — na Androidzie puste partiale znikają w kompilacji (zero #if przy
wywołaniach). WinRT `MediaPlayer`: **źródła wpinane RAZ** (podmiana/Dispose MediaSource per strzał =
APPCRASH 0xc000027b stowed-exception w CoreMessagingXP — 23:06 z 09.07!), `CommandManager.IsEnabled=false`
(SMTC), `MediaFailed` obserwowane, banki głosów z rate-limitami (boom 3×/90 ms, hiss 2×/150, flap 2×/160,
roar 2×/1500, growle 1×/2500 i 1×/1200).

**Sample (Pixabay „Dragon Studio", licencja w THIRD-PARTY-ASSETS.md):** MauiAsset
`Resources/Raw/dragon-audio/*.mp3` → deploy `<exe>\dragon-audio\` → `FindAsset` sonduje 3 układy; fallback
ZAWSZE synteza. Routing: PlayRoar ≥0.55 = `roar-epic`, <0.55 = growle naprzemienne; fire-loop =
`fire-breath.mp3` (vol 0.55); wing-bed = `wings-flapping.mp3`.

**Synteza (fallback + warstwy):** WAV 16-bit mono 44.1 kHz do cache, nazwy WERSJONOWANE (`-v2` = regeneracja).
Pętle bezszwowe = generacja n+fade i wtopienie ogona w głowę + LFO o CAŁKOWITYCH cyklach. Flight bed:
`SetFlightBed(wind,wing,ground)` co tick (fader 15%/call, pauza przy zerze): wiatr = **dwa pasma**
(120–900 Hz ciało + 1.8–5.2 kHz syk, falowanie ≤8%) — ⚠️ jedno niskie pasmo + rytmiczne AM = „POCIĄG";
trzepot = pasmo 250–1500 z pompą 5 Hz **0.22** (rytm robią dyskretne fuchy); rush = 500–3000 Hz. Poziomy
w view: wind=((V−22)/100)^1.4·(1+0.35|sin roll|), wing=flapActivity/1.4, rush=(1−(AGL−24)/36)·V/70,
Flying-only. Roar-synth v2 (fallback): impulsy krtaniowe z jitterem ±10% i shimmerem ±25% + 3 wędrujące
formanty biquad (480/1050/2300 Hz) + chaotyczny kontur f0 — ⚠️ stos harmonicznych z AM = „PIERDZENIE";
głos bestii wymaga jitter+shimmer+formantów ALBO sampli.

**Świst machnięć SYNC:** tracker kości końcówki skrzydła (wybór: nazwa zawiera „wing", max poziomy zasięg,
log `[Dragon] flap-sound wing bone`); prędkość pionowa W PRZESTRZENI MODELU (izolacja od przechyłów), próg
±`LocalExtent·0.3`; upstroke uzbraja → początek downstroke strzela (szczyt dźwięku ~0.2 s = środek
zamachu). Wariant Animated IGNORUJE dragonFlapPhase (klip!) — trigger fazowy tylko dla klasyka.

## 4. KAMERA I UX LOTU

Orbit przy trzymanym F: po **2 s** (`DragonFireOrbitDelaySeconds`) eye-azymut narasta 0.45 rad/s z rampą
1.5 s; lookAt przechodzi cosinusem z „30 m przed smoka" na smoka; puszczenie = powrót NAJKRÓTSZĄ drogą
(wrap!) 2.5/s. Zgina TYLKO azymut oka — chase-cam headingu nietknięty; kąt 0 = tor identyczny.

## 5. WYDAJNOŚĆ (12 → ~30 fps; metodologia: NAJPIERW zmierz)

1. **Pętla vsync** `CompositionTarget.Rendering` dla F7/F8 (`StartVsyncLoop`, self-stop) — DispatcherTimer
   16 ms DUDNI z kompozytorem ~16.7 ms (dropnięta/podwójna klatka co ~¼ s = „sztywno"); timery zostają
   nie-startowane na Windows (pola muszą być przypisane — CS0649).
2. **Dedup paintów**: `OnAnimationTick` nie invaliduje gdy `vsyncLoopActive` (było 127 paintów/58 ticków —
   połowa UI-thread w śmietnik).
3. **Odbicie wody co 2. klatkę** w F7/F8 (`ThrottleReflection` + `reflectionValidLastFrame`; pomiar: pas
   odbić 8–12 ms GPU + 5–7 ms CPU).
4. `SustainedLowLatency` GC w locie (gen2 przy 3–7 GB heapu = pauzy 100–700 ms), Interactive przy wyjściu.
5. Cache jezior per WorldFrame (RebuildLakeCache leciał CO TICK przy żywej kuli).
6. **Telemetria**: `[DragonPerf]` co 5 s (avg/p95/worst/spikes/gc/ticks + paint prep/gl/ovl/renderMax) +
   `[PassTimes]` z CPU per pas (`GpuBegin/End` stemplują CPU; pas `Dragons` dodany — był NIEopomiarowany;
   kubeł `setup` = Render-entry→pierwszy pas). Czytaj to ZANIM cokolwiek utniesz.

**OTWARTE perf:** skoki 200–250 ms (renderMax) przy burstach uploadu streamingu nad świeżym terenem —
następny cel: budżet uploadów/klatkę. Churn gen0 ~29/s w locie (przegląd alokacji kiedyś). GPU przy ogniu
HDR+raymarch na 3440×1360 ≈ 14–18 ms — dźwignia: STEPS wg rozmiaru ekranowego.

## 6. CO GRA W TLE (bez zmian, ale wiedz że jest)

Watcher logów (Monitor `tail -F` na dzienny log; ⚠️ log ROLUJE o północy — przepiąć po dacie!), wzorzec:
`link failed|incomplete|falling back|bloom active|HDR scene|Fireball|APPCRASH`. Cichy catch w view
(GL→Skia) LOGUJE teraz warning. `[DragonAudio] initialised — fire=sample roar=sample wings=sample`.

## 7. TFM ANDROID — jak NAPRAWIONO i jak nie zepsuć

Region `#if WINDOWS` (klawiatura/mysz/sim smoka, ~5300–6700+) miał w środku platform-czyste helpery wołane
z kodu widocznego na Androidzie → CS0103/CS0649/CS0169 (drzewo NIE budowało się na android od sesji 07-09;
CI tego nie widziało bo niepushnięte). Wzorce naprawy: (a) **wyspy** `#endif`/`#if WINDOWS` eksportujące
czyste helpery (`SampleRenderedMeshElevation`, `WrapAngleRad`, `Frac`); (b) mikro-`#if WINDOWS` na
wywołaniach desktop-only (`StepDragonFire`, exit-`SetFireLights`); (c) `#pragma warning disable
CS0649/CS0169` z komentarzem na polach/strukturach karmionych TYLKO z regionu Windows. **Przed każdym
pushem buduj też `-f net10.0-android`.**

## 8. STROJENIE — ściąga wartości

Ogień: FireGain 1.3 · STEPS 20 · sig 2.5/R · obwiednia 0.42 · erozja −0.35 · pole 0.14/świat, swirl 0.55/1.2,
wypór 1.4 · soft ember1.5/flame6–8/puff10/dym40 · światła ≤8, merge 2.5R/18 m, reach 3R, kolor
(1,0.58,0.24) · haze 0.0045, maska half-res, heat: flame/puff 1.0 flash 0.7 ember 0.25 · scorch ≤24,
r=6+4·power, siła 0.75, char (0.16,0.14,0.13) cap 0.85. Emiter: cooldown 0.034±35%, spd +105, TTL 2.2,
jitter 0.65–1.4×, młode 0.58×, cap 88. Sterowanie: attack 0.28/release 0.18/expo 1.3/ωn 6/bramka 0.6/
MaxRoll 1.0. Audio: gain fire 0.55 bed 0.5/0.45/0.55 · roar próg 0.55 · flap vol 0.25+0.25·vigor ·
atenuacja 60/(10+d). Kamera-orbit: delay 2 s, 0.45 rad/s, rampa 1.5 s, powrót 2.5/s.

## 9. OTWARTE / NASTĘPNE

- **C1** bloom pyramid (Karis+dual-filter, mipy R11F) i **C2** blue-noise dither — opcjonalny hardening.
- Skoki uploadu streamingu (§5) + churn gen0.
- Audio-polish: doppler smoków AI, echo dolin, kierunkowe tłumienie ryku ognia.
- **Bug Spacji** (panele+skrzydła; fix niepotwierdzony — logi `[UI] ToggleUi`/`[Dragon] Space` są).
- Crash 22:42 09.07 (0xc0000005 Microsoft.UI.Xaml, PRZED audio/vsync, jednorazowy) — jeśli wróci: LocalDumps.
- ⚠️ **Atrybucja modeli 3D** (dragon.glb CC-BY „TO CONFIRM", dragon-animated.glb) — PRZED publiczną dystrybucją.
- Mobile-weryfikacja shaderów terenu z tej sesji (światła/scorch kompilują się na GLES — sprawdzić na Adreno).
- `data/*.glb|zip` + `testdata/tracks/*.gpx` — niezacommitowane pliki usera (assety robocze / prywatne trasy).
