# HANDOFF — Nocne niebo (realna astronomia: gwiazdy + Księżyc + planety + światło + podpisy)

User chce „niebo jak w realu, z podpisami, wyłączane z panelu". **Decyzje (zatwierdzone):** pełny realizm
(realne pozycje), lokalizacja = centrum sceny (Tatry ~49.25°N, 19.95°E), godzina z suwaka, **data dzisiejsza**;
podpisy gwiazd + gwiazdozbiorów + **planet** + Księżyca; **Słońce też z realnego modelu** (spójne; po tym
re-tune złotej godziny). Backlog/plan też w `docs/TODO-golden-hour-effects.md`, tracker = zadanie #7.

## ✅ Zrobione i NA MAIN (tip `129aad1`, CI), wszystko TDD (~30 testów), ZERO zmian renderu
Faza A (rdzeń astronomiczny, `src/MapaTur.Application/Terrain/`):
- `AstronomicalTime` — Julian Date, GMST/LMST (Meeus 7/12). Wejście: data+godzina UTC.
- `CelestialCoordinates.EquatorialToWorld(raH, decDeg, lstH, latDeg)` → wektor świata (X-wsch, Y-płn, Z-góra); **Z>0 = nad horyzontem**.
- `SolarPosition.Equatorial(jd)` + `ApparentLongitudeDegrees(jd)` (Meeus 25).
- `LunarPosition.Equatorial(jd)` + `IlluminatedFraction(jd)` (Schlyter + perturbacje; faza 0=nów,1=pełnia).
- `PlanetaryPosition.Equatorial(Planet, jd)` (Merkury–Saturn).
- `StarCatalog.Parse(csv)` + `StarCatalogData.Bundled` (29 jasnych nazwanych, baked w C# — device-safe, jak lakes) + `scripts/generate-star-catalog.py` (pełne pole HYG, mag≤6, uruchamiane offline) + `data/stars.csv`.
- B1 `NightSky.StarDirections(stars, jd, latDeg, lonDeg)` → lista (Vector3 Direction, float Magnitude). Test e2e: Polaris→due-north@alt=lat.

## ✅ B3 + podpisy + linie — ZROBIONE i NA MAIN (tip `e51e1e8`, CI), device-confirmed (Wielki Wóz)
- **B3.1 czas** (`ea97e56`, TDD): `CentralEuropeanTime.UtcOffsetHours` (CET/CEST wg reguły EU DST — ostatnia niedziela III/X, liczone per-rok) + `NightSky.StarDirectionsForLocalDate(stars, y,m,d, localHour, lat, lon)` (localHour→UTC→jd→`StarDirections`; ciągły wzór JD sam roluje dzień przy ujemnej godzinie). UtcNow zostaje w App.
- **B3.2 pas GL** (`95e0cf1`): point-sprite'y w sky-pass `Terrain3DGlRenderer` (po gradiencie, depth off, additive blend), `EnsureStarBuffer` (cache po jd-inputach, cull Z≤0, re-upload tylko gdy zmiana), bramka `nightFactor`. `Render(..., DateOnly? localDate)`; widok podaje `DateTime.Now`; lat/lon = `tiles[0].ProjectionAnchor`.
  - ⚠️ **GOTCHA far-plane** (kosztowała długi debug): trik w=0 `uViewProj*vec4(dir,0)` daje `NDC.z=(f+n)/(f-n) > 1` → **całe niebo ucinane przez far-clip** (clipping działa MIMO wyłączonego depth-testu; mechanizm/blend/point-size działały, gwiazdy znikały). FIX = przypnij głębię do far-plane: `gl_Position = vec4(clip.xy, clip.w, clip.w)`. Reużyj to dla Księżyca/planet.
- **B3.3 podpisy (E częściowo)** (`dc0d6ef`, TDD): `StarLabelProjector` (kierunek→ekran IDENTYCZNIE jak `Camera3D.ProjectToScreen`, ale wejście w=0 i bez testu `ndcZ`; cull pod-horyzont/za-kamerą/poza-ekran) + `Terrain3DCanvasRenderer.DrawStarLabels` (overlay Skia, halo+fill, jak etykiety szczytów). Podpisy trafiają w punkty GL (te same `clip.xy/clip.w`).
- **B3.4 linie gwiazdozbiorów (E częściowo)** (`e51e1e8`, TDD): `ConstellationLines` (topologia Wozu po nazwach + resolver na segmenty ekranowe; segment tylko gdy OBA końce na ekranie) + `DrawConstellationLines` (Skia, pod podpisami).

## ⬜ ZOSTAŁO
- C **Księżyc**: dysk-billboard w kierunku `LunarPosition`→world (jak słońce), faza (terminator wg kąta do Słońca / `IlluminatedFraction`) + chłodna poświata. **Reużyj fix far-plane (w=0 + z=w).** ⚠️ **device-verify: Księżyc bywa pod horyzontem / nów dla danej daty — sprawdź `LunarPosition` ZANIM oczekujesz widoczności** (gwiazdy są zawsze, Księżyc nie). Planety jako jasne „gwiazdy" w innym kolorze (`PlanetaryPosition` gotowe).
- D **Światło księżyca**: słaby, chłodno-niebieski term kierunkowy w shaderze terenu (Lambert od Moon × faza × wysokość), gdy `uSunDir.z<0`.
- E **reszta podpisów**: planety + Księżyc; więcej gwiazdozbiorów (katalog ma pełny kształt tylko dla Ursa Major — dorzuć gwiazdy do `StarCatalogData.Bundled` lub gęste pole ze `scripts/generate-star-catalog.py`).
- F **Toggle panelu**: „Niebo nocne" (+ „Podpisy") w panelu Pogoda/Widok (wzorem suwaków/chipów; teraz gwiazdy+podpisy+linie są ZAWSZE on nocą). Potem **upgrade Słońca** na realny model (zastąp uproszczony łuk w `Atmosphere` — UWAGA: bramka nocy gwiazd używa jego `SunDirection.Z`) + **re-tune złotej godziny**.

## ⚠️ Gotchas (twarde lekcje tej sesji)
- **GLES/Adreno ≠ desktop**: desktop-log NIE wystarcza; ZAWSZE wgraj na telefon przed „done". Sampler różnych typów na jednej jednostce = Adreno odrzuca draw (patrz fix `c264e01`). Star pass jest prostszy (bez FBO/samplerów) — ryzyko mniejsze, ale i tak device-verify.
- **camera-relative frame** terenu (`uModelOffset=-camera.Target`): dla gwiazd użyj w=0 (kierunek), żeby NIE wsiąkły w tę ramkę.
- **Deploy**: keystore `%USERPROFILE%\mapatur.keystore` (hasło w `docs/HANDOFF-2026-06-17-dropout-cablecar.md`, wczytuj z pliku do zmiennej — literał w komendzie = blok credential-leak); `ApplicationVersion ≥ 113`; `-p:EmbedAssembliesIntoApk=true`; build desktopu: ubij `MapaTur*` + `-p:WindowsPackageType=None`. „wgrane i potwierdzone" = versionCode+pid+lastUpdateTime.
- **Bramka push**: format 4 projektów (Domain/Application/Infrastructure/Routing) `--verify-no-changes` + pełne testy zielone. `.editorconfig` bez końcowego newline.
