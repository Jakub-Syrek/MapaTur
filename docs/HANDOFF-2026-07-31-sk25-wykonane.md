# HANDOFF 2026-07-31 wieczór — sk25 WYKONANE data-side + bake, CZEKA WERDYKT USERA

## ══ STAN: warstwa pośrednia 25 cm SK żyje w apce, user ma ją ocenić ══

**Gałąź `perf/pano-streaming` = main + `1b35bba`** (narzędzia sk25 + recepta TILE-PRODUCTION §12).
**Apka DZIAŁA dla usera (PID 25244, build z HEAD)** — stan blokady sprawdzać WYŁĄCZNIE w `C:\Repos\APP-LOCK.md`.

| co | stan |
|---|---|
| det25 | **65 691 kafli** (39 851 GUGiK PL + 25 840 ZBGIS SK + 177 straddlerów zmergowanych) |
| opk det25 | 1 105 pakietów / 66 796 stron, verify-full **BAD=0**, 7,79 GB (compact-tail L2) |
| opk det1m | 87 pakietów (było 54 — **strona SK dostała warstwę 1 m**), verify-full BAD=0 |
| opk det05 | przyrostowo 80 pakietów po naprawach znaków; 1 008 237 stron zgodne |
| znaki wodne sk25 | 1 497 pozycji katalogu rozliczone (4 przebiegi + okno NCC); verify-post: ≥0.65 = 0 |
| bonus det05 | **73 znaki @5 cm przeoczone przez skan 5 cm naprawione** (284 kafle) + zdegenerowana instancja rok2022 (4 kafle) |
| runtime | żywy log: det25 **resident 128 / empty 0** (rano w tym samym miejscu 37/91); sumGpu 14–18 ms |

Pełna recepta krok-po-kroku + WSZYSTKIE lekcje dnia: **`docs/TILE-PRODUCTION.md` §12**
(m.in.: pełny dekod-skan po fetchu obowiązkowy — 48 zerowych plików; wstawka z sk05-harm zamiast
median-filla @25 cm; okienkowy argmax NCC; katalog 25 cm jako czulszy detektor znaków dla 5 cm na
lasach; metryka pasmowa kłamie na cenzurowanych rozkładach — werdykty tonu per-piksel).

## ══ CO CZEKA NA USERA (pierwsze przy następnej sesji) ══

**WERDYKT WIZUALNY sk25** — apka stoi. Gdzie patrzeć:
1. **Gierlach** (tam zmierzono motywację: 0/25 kafli det25 na SK) — odlecieć >3,2 km od szczytu:
   strona SK powinna trzymać 25 cm zamiast spadać do bazy ~1,5 m/px;
2. przejście pierścienia det05→det25 na stronie SK (ton ciągły? A/B zmierzone: mediana −2…−8 lumy
   w głębokim cieniu, wizualnie zgodne);
3. granica PL|SK w warstwie 25 cm (177 straddlerów — pas ~128 m przy granicy ma treść po obu stronach);
4. brak znaków „© GKÚ, NLC" na 25 cm (i na 5 cm na LASACH — tam było 73 przeoczonych).

Werdykt „ok" → domknąć task #5, zaktualizować memory. Werdykt negatywny → rollbacki w §12 (wszystko
odwracalne bez ponownego fetchu).

## ══ KOLEJKA (po werdykcie sk25) ══

1. **Sprzątanie ~15,4 GB** — `opk/det25-prerim`+`det1m-prerim` (8,4 GB) i `gpu-cache` (6,8 GB)
   czekają na „kasuj" usera od 07-25. Do tego dochodzą nowe kandydatury po dzisiejszym dniu:
   `sk25-harm-prewm`, `det25-premerge`, stare backupy — policzyć i zaproponować listą.
2. **Instalka** — runtime ~185 GB; realny pakiet ~132 GB, jeśli test „czysta instalacja bez katalogu
   webp det05" przejdzie (runtime jest już `.opk`-only — sprawdzić, czy setup nie gate'uje się na
   istnieniu katalogu kafli).
3. **Východ-2025 (15 cm)** — sprawdzać REST-em (TILE-PRODUCTION §11 krok 0); po publikacji rdzeń
   (Rysy/Gierlach/Łomnica, dziś 2022/20 cm) odświeżyć jednym przebiegiem pipeline'u (sk05 i sk25!).
4. **Szew PL|SK z bliska** — deshadow strony POLSKIEJ (R4); materiał w pamięci epiki
   `ortho-deshadow-luminance-field`.
5. **☀ SŁOŃCE I ŚWIATŁO — nowy punkt od usera (07-31: „musimy dopieścić słońce jeszcze i światło
   w przyszłości")** — oświetlenie sceny (nie piksele orto — ton ZAMROŻONY); start od `Atmosphere.cs`
   + epic golden-hour; memory `sun-light-polish-future`.

## ══ WSPÓŁPRACA Z CODEXEM ══

Zasady i pliki: patrz `HANDOFF-2026-07-31-sk-det05-domkniete-wspolpraca-codex.md` (sekcja współpracy
— NADAL AKTUALNA). Stan na wieczór 07-31: Codex przygotowuje **rebase `codex/realistic-rock-material`
na main b301603** (C2C-033/035), wpis o wyniku doda po domknięciu; potem jego integracja runtime RMP3
(wspólna strefa: końcowy wybór geometrii + maska materiału w rendererze — uzgadniać przez kanał).
Dziś w oknie: bake det25 pisze pakiety w JEGO formacie compact-tail L2 (czytnik zgodny z legacy) —
dlatego pierwszy bake po integracji był pełny, nie przyrostowy (skip po srcHash nie widzi v4/L1 → L2).

## Twarde zasady procesu — bez zmian (lista w poprzednim handoffie); nowe z dziś w §12.
