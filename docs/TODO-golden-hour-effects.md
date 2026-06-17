# TODO — efekty „golden hour" w rendererze MapaTur (bez Unreala)

Cel: przenieść do naszego renderera GLES 3.0 (`Terrain3DGlRenderer.cs`, rysujący na FBO Skii w SKGLView)
efekty, które robią różnicę o złotej godzinie — **bez** przepisywania na Unreal, bez ruszania ~24 k linii
logiki domenowej i 1153 testów.

Co już mamy (potwierdzone w shaderze): aerial-perspective fog (`uFogColor`/`uFogDensity`), ciepłe słońce +
chłodny sky-fill (`uSunColor`/`uSkyAmbient`), halo słońca w stylu Mie, model `Atmosphere.cs` (time-of-day →
sun dir/kolory/ambient/fog). Realna luka: fizyczna poświata, smugi światła i prawdziwe cienie.

## Zasady realizacji (twarde)
- **TDD**: test FAIL najpierw (AAA, nazwa opisuje zachowanie), potem minimalna implementacja.
- **Bramka przed KAŻDYM commitem**: `dotnet format --verify-no-changes` na zmienionych projektach +
  pełne testy green. (`.editorconfig` zabrania końcowego newline — format-check to wyłapie.)
- **Bez `git push` bez wyraźnej zgody** (zwł. na `main`).
- **Bez „gotowe ✅" dopóki user (lub obserwowalny test) nie potwierdzi efektu na urządzeniu.**
- Diagnostyka/logowanie wbudowane od startu (czytelny sygnał z telefonu).
- Pułapka SKGLView: na końcu klatki wrócić bind FBO Skii + `ResetContext`, pilnować stanu GL co klatkę.
- Mobile/S25: bufory post-process w pół-rozdzielczości, 2 kaskady cieni.

## Kolejność (po kolei)

### Krok 1 — Poświata pod słońcem (glow w shaderze nieba)  ⟵ START
- [ ] TDD: w `Atmosphere.cs` właściwości glow (intensywność/szerokość) rosnące monotonicznie, gdy słońce
      schodzi ku horyzontowi; zero, gdy słońce pod horyzontem. Test najpierw w `AtmosphereTests.cs`.
- [ ] Uniform(y) w `Terrain3DGlRenderer.cs` + użycie w shaderze nieba (okolice halo, ~:453).
- [ ] Wpięcie w `Terrain3DView.xaml.cs` (upload per-frame).
- [ ] Bramka + weryfikacja wzrokowa na urządzeniu.
- Quick win, zero nowej infrastruktury, niskie ryzyko.

### Krok 2 — Fundament FBO / post-process (offscreen RT)
- [ ] Własne FBO + tekstury color/depth, fullscreen-quad pass, downsample; helper rozmiarów (TDD).
- [ ] Twarde pilnowanie stanu GL + powrót do FBO Skii + `ResetContext`; logowanie completeness.
- Fundament dla kroków 3 i 4.

### Krok 3 — Bloom (poświata bleed)   [zależy od kroku 2]
- [ ] bright-pass → mip blur → additive composite; próg/intensywność z `Atmosphere` (TDD na krzywą).

### Krok 4 — Smugi światła (god rays, screen-space)   [zależy od kroku 2]
- [ ] maska okluzji → radial blur w stronę słońca → additive; rzut słońca do screen-space (TDD).
- [ ] Brak smug, gdy słońce za kamerą/wysoko. Upgrade później: wolumetryczne (po kroku 5).

### Krok 5 — Lepsze cienie (Cascaded Shadow Maps)
- [ ] 2–3 kaskady, depth z POV słońca, `sampler2DShadow` + PCF + bias; splity/macierze (TDD).
- [ ] Strojenie bias/filtr na urządzeniu. Odblokowuje wolumetryczne smugi.

## Czego NIE robimy (i nie trzeba)
Prawdziwy Lumen (dynamiczne GI z ray-tracingiem) i prawdziwe Virtual Shadow Maps — to systemy silnikowe,
nie przenosimy 1:1 na GLES. Kroki 1–5 odtwarzają *postrzegany* rezultat o złotej godzinie na urządzeniu.

## Status
- ✅ **Krok 1 (poświata)** — `Atmosphere.SunGlowIntensity/Width` + term w shaderze nieba. Commity
  `06e4a10` (feat) + `eed18d2` (ten TODO). Wgrane na S25 (v102), user: „jest ok".
- ✅ **Krok 2 (fundament FBO)** — helper `PostProcessBufferSizing` (`f410488`, TDD 13 testów) +
  etap post-process `RunPostProcess`/`EnsurePostBuffers` w rendererze (`6b28d84`, pass-through za
  kill-switchem, graceful fallback). Desktop-log-verified: `[GL3D] post-process stage active`,
  FBO kompletny. Reszta etapu (bloom/godrays) buduje na `RunPostProcess`.
- ✅ **Krok 3 (bloom)** — `Atmosphere.BloomIntensity/Threshold` (TDD) + bright-pass → separable blur
  (half-res ping-pong) → additive composite w `RunPostProcess` (`544203f`). Desktop-log-verified
  („bloom active 1424x713 half 712x356"), na S25 v104, user: „wygląda ok".
- ⏭️ **Następne: Krok 4 (smugi światła / god rays)** — maska okluzji → radial blur ku słońcu (screen-space)
  → additive, w `RunPostProcess` (reużywa fundamentu). Rzut słońca do screen-space (TDD).

### Recepta deployu / weryfikacji (potwierdzona w tej sesji)
- APK: `dotnet build src/MapaTur.App -f net10.0-android -c Release -p:EmbedAssembliesIntoApk=true
  -p:ApplicationVersion=<N≥104>` **+ keystore** (`%USERPROFILE%\mapatur.keystore`, alias `mapatur`,
  hasło w tym handoffie — bez niego `INSTALL_FAILED_UPDATE_INCOMPATIBLE`). Skasuj stary `*.apk`.
- Build lokalny / desktop: najpierw ubij `MapaTur*` (blokady DLL); `-p:WindowsPackageType=None`.
- Logi: **Serilog → plik** `…/win-x64/logs/mapatur-<data>.log` (desktop, dostępny na PC) — NIE logcat,
  NIE Android external (Release nie jest debuggable → brak `run-as`). Renderer init na desktopie
  ~40 s po starcie. Weryfikuj GL na desktopie (ten sam renderer) przez log.
- Wszystkie commity LOKALNE, **nie pushnięte** (czekają na zgodę).
