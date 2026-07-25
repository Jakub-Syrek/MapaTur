# HANDOFF 2026-07-22 — De-shadow orto: MO=V2 (preview) ZAMROŻONE, batch reszty Tatr (R3 gate)

**Jednozdaniowo:** Kocioł Morskiego Oka odcieniony jako **V2** (staging `det05-deshadow-mo-v2`, runtime-test
zielony); **V2 ZAMROŻONE**; teraz **batch reszty Tatr w 4 regionach** — R3 to gate techniczny (fetch referencji
w toku), po nim R1/R2/R4 razem. Cel: usuwać wypalone cienie nalotu 2021 5cm, offline (bake danych), z
wieloletniej referencji 25cm; user = jedyny sędzia wizualny; ja dostarczam POMIAR.

Branch: `feat/walk-mode` (cała praca orto w `dev/ortho-deshadow/`, untracked; kafle w `dem/ortho-detail/tatry/`).
Poprzednie handoffy: `HANDOFF-2026-07-21-ortho-deshadow-rysy.md` (start), pamięć `ortho-deshadow-luminance-field`.

---

## 1. STAN TERAZ (co zrobione / co w locie)

- **Kocioł MO = ZAKOŃCZONY PODGLĄDOWO (V2).** Staging `dem/ortho-detail/tatry/det05-deshadow-mo-v2` = **3874 kafli**
  (2698 korekcja + 1176 pierścień-kopia-źródła). Pole `scratchpad/glob2_field.npz`. Runtime-test w skompilowanej
  apce ZIELONY (served>0, brak double-correction po fix).
- **Rysy PoC (STARA metoda) = `det05-deshadow` (200 kafli)** — nietknięty, referencja/rollback, **POZA runtime**.
  (Kompozyt PoC>V2 miał szew 0.78 stop → user wybrał V2 na całym kotle; PoC zostaje tylko na dysku.)
- **Batch: R3 fetch referencji KOMPLETNY** (fetch domknął się 07-22). `ref_manifest_R3.json` = 199 plików,
  90 arkuszy, wszystkie 6 lat (2009=24, 2015=91, 2018=24, 2019=25, 2022=24, 2024=24); `mo-ref/` = 6.8 GB.
  Następny krok = od razu `region_pipeline.py R3` (fetch NIE trzeba wznawiać).
- Pipeline batch GOTOWY: `region_pipeline.py` (pole+arkusz+audyt), `bake_region.py` (bake+ring+audyt).

---

## 2. ZAMROŻONE V2 — parametry (NIE ruszać bez zgody usera)

- **Luminancja: gain max 2.0× = 1 stopień** (clamp na końcu). Mnożenie liniowego RGB, chroma bez zmian.
- **Chroma: 50%** neutralizacji, **WYŁĄCZNIE z materiału 2021** (OKLab a,b; cast=gładka chroma cienia − target
  oświetlonej skały 2021; **żadnej chromy z roczników referencyjnych**). Luminancja dokładnie stała (rescale Y).
- **Referencja 25cm: 2009/2015/2018/2019/2022/2024** (2021=5cm=CEL). PRIOR: 2009/2015=1.0 (główne), 2019=0.75,
  2022/2024=0.85, 2018=0.6 (śnieżny — bramkowany). NIE „rok jaśniejszy niż 2021"=oświetlenie.
- **LOKALNA normalizacja wielonalotowa:** gładkie pole `k(x,y)` (delta rok−2021 na WSPÓLNIE oświetlonych
  STABILNYCH powierzchniach, ekstrapolowane σ~140m). NIE jeden globalny skalar k.
- **BRAMKA ABSOLUTNEGO OŚWIETLENIA:** referencja liczy się jako „oświetlona" tylko gdy REALNIE jasna (próg =
  mediana oświetlonej skały −0.8) + neutralna (B<0.05). To rozstrzyga naprawialność (Kazalnica: illum=0% → NIE
  naprawialna — „żaden dostępny nalot nie przeszedł konserwatywnej bramki").
- **Maski (spójne przestrzennie, NIE pojedynczy piksel):** woda=raster poligonów gazetteera (171 jezior OSM,
  `MountainLakeData.Lakes.g.cs`); zieleń=ExG low-pass+slope<32; śnieg=two-tier (hard=wyklucz, uncertain=obniż
  confidence) — temporalny: „rok gładszy niż wieloletni konsensus" (tex_anom WYMAGANE) + norm. ekspozycji.
- **ROI twardy limit** = kandydat dark+cool+STROMY (≥38°, próg apki „>45°=skała"); korekcja tylko w ROI.
- **Feather → 0 WEWNĄTRZ pokrycia det05** (erozja granicy 20m; poza det05 renderer ma fallback 25cm/bazę).
- **V2 → fallback surowy det05.** Pierścień stagingu = KOPIA źródła (exact 0) — brak szwu na granicy runtime.

---

## 3. SKRYPTY (`dev/ortho-deshadow/`, untracked, uruchamialne)

**Batch (aktualne):**
- `scope_batch.py` — WFS scan całego pokrycia → ile arkuszy/GB (25cm-only=16.8GB) + podział na 4 regiony;
  zapisuje `scratchpad/scope_ref_index.json` (WSZYSTKIE arkusze: godlo,url,piksel,mb,clon,clat).
- `fetch_region.py {R1..R4}` — pobiera 25cm dla regionu (lon band +0.03 halo) do `mo-ref/{rok}/`, SKIP
  istniejących (reuse cache); manifest `ref_manifest_{REG}.json`.
- `region_pipeline.py {REG} [--write]` — ZAMROŻONE V2: pole (dynamiczne arkusze=glob `mo-ref/{rok}/*.tif`)
  → `scratchpad/field_{REG}.npz` + arkusz `region_{REG}_beforeafter.png` (2021|V2 normalna+unlit) + auto-audyt
  (maska %, woda/zieleń-w-masce %, gain-krawędź, pole-max).
- `bake_region.py {REG} --write` — field_{REG}.npz → kafle → `det05-deshadow-mo-v2`; sel=bake, pierścień=kopia
  źródła; auto-audyt (pierścień exact-0, max szew systematyczny).

**Historyczne (MO, referencja metody — NIE usuwać):**
- `global_field2.py` = FROZEN V2 dla MO (źródło logiki region_pipeline). `bake_v2.py`+`fix_ring.py` = bake MO.
- `audit_v2c.py` = audyt V2-only (kompozyt V2>det05). `compare_poc_v2.py` = PoC vs V2. `snow_v3.py` = detektor
  śniegu two-tier. `candmap.py` = ROI/wykluczenia. `enum_ref/download_ref/verify_ref/audit_ref` = fetch+audyt MO.
- Rysy PoC (stara metoda): `lumfield.py`(lum), `chroma.py`(chroma), `diag.py`+`union_mask.py`(maska), `bake.py`.
- ODRZUCONE (NIE wracać): `gain_field.py` (per-crop k = biased naprawialność), `global_field.py` (globalny k
  + globalne percentyle = rozlanie 27% + zła Kazalnica).

**Dane/pole:** `scratchpad/*.npz` (glob2_field=MO, field_R3=po run; nat_*=cache natywnych cropów; snowstack_*).
Diagnostyki (persist): `dev/ortho-deshadow/diagnostics/*.png`.

---

## 4. DANE / ŚCIEŻKI (KRYTYCZNE — runtime czyta z AppData, NIE z repo)

- **Źródło det05:** `dem/ortho-detail/tatry/det05` (343077 kafli) — NIE nadpisywać.
- **Staging V2:** `dem/ortho-detail/tatry/det05-deshadow-mo-v2` (3874, rośnie z regionami).
- **Rysy PoC:** `dem/ortho-detail/tatry/det05-deshadow` (200) — rollback, POZA runtime.
- **Referencja 25cm:** `dev/ortho-deshadow/mo-ref/{rok}/{godlo}.tif` (współdzielona wszystkimi regionami; cache).
- **★ APPDATA (runtime!):** apka czyta z `C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\
  Data\dem\ortho-detail\tatry\...` — NIE z repo `dem\`. Zmianę DANYCH trzeba SKOPIOWAĆ do AppData (patrz pamięć
  [[dev-app-instance-and-data-dir]]). V2 MO już skopiowane (3874). Nowe regiony → dokopiować po zatwierdzeniu.

---

## 5. RUNTIME (podgląd w skompilowanej apce)

- **Loader** (`Terrain3DView.xaml.cs` `SetupDet05Streaming`, ZBUDOWANY, DLL 22:16): env
  `MAPATUR_DET05_DESHADOW_PREVIEW=1` + `MAPATUR_DET05_DESHADOW_DIR=det05-deshadow-mo-v2` (domyślnie
  `det05-deshadow`=rollback); fallback zawsze det05. Auto-ustawia `OrthoDetailColorMode=0`+`BakedShadowComp=0`
  gdy preview ON (żeby nie było podwójnej korekcji). Dedup double-fire klawiszy dodany.
- **Uruchomienie:** testuj KOMPILOWANY exe `src\MapaTur.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\
  MapaTur.App.exe`; **UBIĆ apkę PRZED build** (inaczej stale DLL — lock win-x64). Env-vary w tej samej powłoce.
- **Klawisze:** `9`=OrthoDetailColorMode (0=raw/V2 jak zapieczone, 1=+shader de-blue=double), `1`=BakedShadowComp,
  `0`=detail on/off, `F2`=albedo/unlit, `F1`=finalny. Log: `deshadow-preview served: N`.
- Apka obecnie DZIAŁA (pid 32040, overlay=mo-v2). Logi: `...\win-x64\logs\mapatur-RRRRMMDD.log`.

---

## 6. BATCH PLAN + NASTĘPNE KROKI (ścisłe)

Referencja 25cm-only = **16.8 GB / 245 arkuszy / 6 lat**. 4 regiony wg lon (`REGIONS` w skryptach):
R1 19.796–19.874 (Czerwone Wierchy, 4.7GB) · R2 19.874–19.952 (Giewont/Kasprowy, 2.8GB) ·
**R3 19.952–20.030 (Świnica/Zawrat/Orla Perć, 4.3GB) = GATE** · R4 20.030–20.108 (Rysy/Mięgusz, 5.1GB, zawiera MO).

**Decyzja usera (07-21): R3 na próbę (gate), potem R1/R2/R4 RAZEM bez pośrednich kontroli. Tylko TWARDE błędy.**

NEXT (dokładnie):
1. ~~Fetch R3~~ ZROBIONE (199 plików, 90 arkuszy, 6 lat). Zaczynamy od pola.
2. **Pole+arkusz R3:** `python region_pipeline.py R3` → obejrzeć `region_R3_beforeafter.png` + audyt
   (maska %, woda/zieleń-w-masce ~0, pole-max, gain-krawędź=0).
3. **Bake R3:** `python bake_region.py R3 --write` → kafle do `det05-deshadow-mo-v2`; audyt (pierścień exact-0,
   szew<0.05).
4. **JEDEN arkusz R3 do usera** = gate. Poprawiać TYLKO: widoczny szew / korekcja jeziora-roślinności-śniegu /
   przepał-utrata detalu / brak danych. Subtelne różnice AKCEPTOWAĆ.
5. **Jeśli R3 zielone → R1/R2/R4 razem:** `fetch_region.py R1;R2;R4` (~12GB), potem `region_pipeline.py`+
   `bake_region.py` dla każdego; zbiorczy zestaw 3 arkuszy. Reuse cache MO/R3.
6. Po batchu: dokopiować staging do AppData, jedna zbiorcza kontrola wizualna, potem ewentualna promocja z
   preview do danych docelowych.

---

## 7. HARD-WON LEKCJE (NIE powtarzać)

- **„rok jaśniejszy niż 2021" ≠ OŚWIETLENIE** — może być ekspozycja/radiometria/haze/mniej-głęboki-cień. Wymaga
  LOKALNEJ normalizacji na wspólnie oświetlonych + BRAMKI absolutnego oświetlenia. (Dwa razy się na tym potknąłem.)
- **Per-crop offset k = biased** (mało oświetlonych pikseli kalibracyjnych) → fałszywa naprawialność.
  **Globalny SKALAR k też zły** (różne materiały/naloty). → lokalne gładkie pole k.
- **Globalne percentyle rozlewają klasyfikację** (maska 4%→27%). Statystyki 2021 licz TYLKO po ważnych pikselach
  (maskuj nodata) i twardo ograniczaj do ROI.
- **Pole MUSI featherować do 0 WEWNĄTRZ pokrycia det05** (poza det05 jest fallback 25cm — „brak kafli" ≠ „brak obrazu").
- **Mieszanie dwóch metod korekcji = szew** (PoC 0.78 stop). Jedna metoda na spójnym obszarze.
- **Maski materiałowe NIE z pojedynczego piksela** — spójne przestrzennie (low-pass/morfologia/komponenty).
- **Śnieg:** temporalny (tex_anom vs konsensus) + norm. ekspozycji; NIE ukrywać kropek morfologią (objaw, nie przyczyna).
- **Build przy DZIAŁAJĄCEJ apce = stale DLL** (lock win-x64) → ubić apkę PRZED build, sprawdzać datę DLL.
- **Runtime czyta AppData, nie repo** — kopiować dane do AppData.
- **Klawisze fire'owały 2×** (handler na 2 elementach) → toggle się kasował; dedup dodany.

---

## 8. TRYB PRACY (user 07-21, HARD — koniec strojenia pojedynczej ściany)

Hurtem, nie per-ściana. Jeden przebieg per region, JEDEN arkusz PRZED/PO, poprawki TYLKO dla twardych błędów
(woda/śnieg/zieleń/szew/przepał/brak-danych). Subtelne różnice koloru i odchylenia poniżej progów AKCEPTOWAĆ.
NIE cztery godziny na jedną maskę. Światło render-side (chłodny syntetyczny cień) = ODŁOŻONE (fix render-side:
ambient/skylight/kolor cienia/ekspozycja/AO, NIE dalsze niszczenie orto).
