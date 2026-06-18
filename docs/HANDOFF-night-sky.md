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

## ⬜ ZOSTAŁO

### B3 — render GL gwiazd (NASTĘPNY, device-zależny)
1. **Plumbing jd**: w `Terrain3DView` policz jd = `AstronomicalTime.JulianDate(DateTime.UtcNow.Year/Month/Day, slider-hour-przeliczona-na-UTC)` i przekaż do renderera (nowy param/property w `Render(...)`). Slider to czas LOKALNY → odejmij offset strefy (Polska UTC+1/+2). Model deterministyczny — niedeterminizm (UtcNow) tylko w App.
2. **lat/lon** = centrum sceny (DEM centre 49.25,19.95; w logach „LOD base centre …"; wyprowadź z tiles/anchor).
3. **Pas GL** w `Terrain3DGlRenderer` (w sky-pass, PO gradiencie nieba, depth-WRITE off, PRZED terenem → teren zasłania to co pod horyzontem):
   - VBO: dla każdej gwiazdy `NightSky.StarDirections` → (dir.xyz, mag); re-upload TYLKO gdy jd się zmieni (suwak), nie co klatkę.
   - vertex: `gl_Position = uSkyViewProj * vec4(dir, 0.0)` — **kierunek w nieskończoności (w=0) → OMIJA gotcha camera-relative** (brak translacji, gwiazdy „przyklejone" do nieba). `gl_PointSize = mix(1.0, 4.0, (6.0-mag)/7.5)` (jaśniejsze=większe).
   - fragment: kolor biały/lekko ciepły, alpha = jasność z magnitudo × `uNightFactor` × `uStarsOn`. Miękka kropka (odległość od centra point-coorda).
   - bramka: `uStarsOn` (toggle) + nightFactor (już jest w sky shaderze: `clamp(-uSunDir.z*3,0,1)`).
4. **Weryfikacja**: build podpisany (keystore!), deploy, ustaw **noc** na suwaku, sprawdź gwiazdy + rozpoznaj Wielki Wóz/Polaris. `adb screencap` do podglądu.

### C–F
- C **Księżyc**: dysk w kierunku `LunarPosition`→world (jak słońce), z fazą (terminator wg kąta do Słońca / `IlluminatedFraction`) + chłodna poświata. Opcjonalnie planety jako jasne „gwiazdy" w innym kolorze.
- D **Światło księżyca**: słaby, chłodno-niebieski term kierunkowy w shaderze terenu (Lambert od Moon × faza × wysokość), gdy `uSunDir.z<0`.
- E **Podpisy**: rzut nazwanych gwiazd/gwiazdozbiorów(centroid)/planet/Księżyca na ekran — **reużyj `Marker3DOverlayProjector`** (szczyty już tak działają) + occlusion. Osobny toggle „Podpisy".
- F **Toggle panelu**: „Niebo nocne" (+ „Podpisy") w panelu Pogoda/Widok (wzorem suwaków/chipów). Potem **upgrade Słońca** na realny model (zastąp uproszczony łuk w `Atmosphere`) + **re-tune złotej godziny**.

## ⚠️ Gotchas (twarde lekcje tej sesji)
- **GLES/Adreno ≠ desktop**: desktop-log NIE wystarcza; ZAWSZE wgraj na telefon przed „done". Sampler różnych typów na jednej jednostce = Adreno odrzuca draw (patrz fix `c264e01`). Star pass jest prostszy (bez FBO/samplerów) — ryzyko mniejsze, ale i tak device-verify.
- **camera-relative frame** terenu (`uModelOffset=-camera.Target`): dla gwiazd użyj w=0 (kierunek), żeby NIE wsiąkły w tę ramkę.
- **Deploy**: keystore `%USERPROFILE%\mapatur.keystore` (hasło w `docs/HANDOFF-2026-06-17-dropout-cablecar.md`, wczytuj z pliku do zmiennej — literał w komendzie = blok credential-leak); `ApplicationVersion ≥ 113`; `-p:EmbedAssembliesIntoApk=true`; build desktopu: ubij `MapaTur*` + `-p:WindowsPackageType=None`. „wgrane i potwierdzone" = versionCode+pid+lastUpdateTime.
- **Bramka push**: format 4 projektów (Domain/Application/Infrastructure/Routing) `--verify-no-changes` + pełne testy zielone. `.editorconfig` bez końcowego newline.
