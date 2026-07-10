# Handoff — 2026-07-10: smok po ogniu — SKRĘCANIE (precyzja) + AUDIO 2.0

> Branch: **`feat/walk-mode`**. Bramki przed pushem: `dotnet format MapaTur.slnx --verify-no-changes` + pełne
> `dotnet test`. **NIGDY Claude jako autor/co-author.** Desktop-only (smoki nie odpalają na telefonie).
> Poprzedni handoff (plan ognia): `docs/HANDOFF-2026-07-09-hyper-real-fire.md`.

## Stan planu ognia (2026-07-10 rano)

| Krok | Stan | Werdykt usera |
|---|---|---|
| A0 HDR float FBO (RGBA16F scene/present, R11F_G11F_B10F bloom) + fallbacki `hdrUnsupported` | ✅ | działa (log `HDR scene targets ON`) |
| A1 blackbody + T⁴ + `uFireGain=1.3` (heat² → T 1300–7000 K, pow 3.5) | ✅ | przyjęte |
| B1 soft particles (1 resolve głębi/klatkę współdzielony z bramką x-ray linii; zakresy ember 1.5/flame 6/puff 10/dym 40 m) | ✅ | „ładny" |
| B2 ogień świeci (≤8 świateł: teren przed podłogą ambientu + śnieg + odbicie wody gratis + smok + dym; redukcja zachłanna score=i·r², merge 2.5R/18 m, invR2=1/(3R)²) | ✅ | „jest ok" |
| A2 world-space volumetric raymarch (elipsoida po prędkości, STEPS=20, jitter, erozja −0.35, 1-tap self-shadow) + A3 swirl/wypór (GPU) + makro-wir dymu (CPU) | ✅ wdrożone | **czeka na werdykt** |
| B3 heat-haze (maska half-res z bramką głębi + refrakcja przed bloomem + rozszczep chromatyczny) | ✅ | „super" |
| B4 scorch (≤24 splatów-uniformów w albedo, pierścień, odbicie wodne gratis; bounce-flash = puffy B2 same świecą) | ✅ | „działa" |
| C1 bloom pyramid (Karis), C2 dithering | ⏳ opcjonalne | — |

**PLAN OGNIA A0→B4 DOMKNIĘTY (2026-07-10 ~12:45).** Strojenie strumienia po werdyktach: prędkość 105 m/s,
TTL 2.2 s, cooldown 34 ms ±35%, rozrzuty ±22%/0.65–1.4×, młode kule 0.58×, obwiednia raymarczu od 0.42.

Obok planu weszły: dym z wypalających się kul (burnout), orbit kamery przy trzymanym F (2 s opóźnienia,
0.45 rad/s, powrót najkrótszą drogą), dźwięki proceduralne v1 (ryk-pętla ognia, świst skrzydeł z kości,
huk, syk, ryk smoka), pętla vsync (CompositionTarget.Rendering) dla F7/F8, odbicie wody co 2. klatkę w tych
trybach, `SustainedLowLatency` GC w locie, telemetria `[DragonPerf]` + CPU per pas w `[PassTimes]`.
**Wydajność: 12 fps → ~30 fps** (zianie w locie). Skoki 200+ ms przy burstach uploadu streamingu — otwarte.

---

## EPIC 1 — Precyzja skręcania („szarpie; duże skręty OK, małe bardzo nieprecyzyjne")

**Diagnoza (hipotezy z kodu, do potwierdzenia pomiarem):** wejście klawiszowe ←→/A,D jest BINARNE
(0/1 → pełna komenda rolki od pierwszej klatki), a każde WCIŚNIĘCIE strzałki odpala dodatkowo
**turn-entry stroke** (impuls: boczne pchnięcie + kopniak banku + macho-machnięcie skrzydłem) — dla
drobnej korekty kursu to armata na muchę: tap = impuls + pełna prędkość rolki → przestrzał → kontra →
„szarpanie". Mysz (dx→roll) nie ma expo, więc przy małych dx też jest stromo.

**Plan analityczny (wzorce z RC/flight-sim; implementacja w `DragonFlight` w Application = TDD-owalna):**
1. **Command shaping (expo)** na wejściu analogowym: `cmd = x·|x|^k` (k≈1.5–2) — płasko przy zerze
   (precyzja), pełna dynamika na skraju. Klawisze: komenda NARASTA w czasie trzymania
   (attack ~0.30–0.40 s do 1.0) zamiast skoku 0→1; puszczenie = szybszy decay (~0.15 s).
   Efekt: tap ≈ 15–25 % komendy = mała, przewidywalna korekta.
2. **Bank-target + krytycznie tłumione śledzenie 2. rzędu**: komenda ustawia CEL banku
   `φ_t = cmd·φ_max`, a fizyka śledzi go sprężyną `φ̈ = ωn²(φ_t − φ) − 2ζωn·φ̇` z ζ=1 (zero oscylacji,
   zero szarpnięcia — gładkie wejście i wyjście z zakrętu). ωn dobrać tak, by pełny bank w ~0.6–0.8 s.
3. **Slew-rate limit na φ_t** (opcjonalny bezpiecznik przeciw skokom celu przy nagłej zmianie znaku).
4. **Impuls turn-entry TYLKO dla dużych zakrętów**: bramka `|cmd| > 0.6` LUB trzymanie > 0.25 s —
   tapy NIGDY nie odpalają strokes/impulsów.
5. **Skoordynowany zakręt zostaje**: `ψ̇ = g·tan(φ)/V` już jest w fizyce (tan(roll)/speed) — nie ruszać;
   cała poprawa idzie przez kształtowanie φ.
6. **Testy NAJPIERW** (Application): tap 100 ms → |Δψ| mały i monotoniczny (bez przestrzału); trzymanie →
   płynne dojście do φ_max bez oscylacji (ζ=1); puszczenie → powrót do poziomu bez przejścia przez 0 z
   przestrzałem; impuls nie odpala poniżej bramki.

Źródła wzorców: rates/expo (Betaflight/FPV): oscarliang.com/rates, blog.uavmodel.com (2026 guide);
krytycznie tłumione 2. rzędu / input shaping: embeddedrelated.com/showarticle/671.php.

## EPIC 2 — AUDIO 2.0 — ✅ FAZA 1+SAMPLE ZROBIONE (12.07.10 po południu)
Zrobione: (1) **flight bed** — 3 pętle sterowane stanem lotu (wiatr ∝ prędkość+bank ‹v2: dwa pasma, bez
„pociągu"›, trzepot ∝ flapActivity, rush przelotu ∝ AGL×V), miękki fader, pauza przy zerze; (2) **prawdziwe
sample** (Pixabay „Dragon Studio", MauiAsset `Resources/Raw/dragon-audio/`, fallback=synteza): epic roar
(wejście/kill) + 2 growle naprzemienne (szybowanie) + fire-breath loop + wings-flapping bed; licencja w
`THIRD-PARTY-ASSETS.md`; (3) świst machnięć SYNC z kości (wcześniej). Zostało z listy niżej: pkt 4 (świst
nisko nad ziemią — JEST), doppler AI / echo dolin / tłumienie kierunkowe — na kiedyś.

## (oryginalny plan) EPIC 2 — AUDIO 2.0 („teraz jest basic minimum")

Cel: naturalny dźwięk bestii. Warstwy (wszystko desktop-only, architektura DragonAudioService zostaje):
1. **Ryk naturalny** — decyzja: (a) syntezę rozbudować (formanty/warstwy, pitch-drift, pre-growl wdech) —
   ograniczony sufit realizmu; (b) **sample CC0** (freesound/sonniss GDC packs) + warstwa syntezy pod spodem
   — rekomendowane. Kilka wariantów ryku (krótkie/długie/agresywne), dobór losowo-deterministyczny.
2. **Szum skrzydeł ciągły** — pętla „membrane flutter + air rumble" ze skalowaniem głośności/pitchu po
   `flapActivity` i prędkości; słyszalny zawsze w locie, nie tylko przy machnięciu.
3. **Świst machnięcia zawsze SYNC** — tracker kości skrzydła już działa (arm/fire po prędkości pionowej
   końcówki w przestrzeni modelu); dołożyć wariant dźwięku wg tempa (pace) i siły + downstroke „thump".
4. **Świst przelotu nad ziemią** — pętla gated wysokością AGL (np. < 15 m) i prędkością; głośność ∝
   V/AGL, lekki pitch-up przy nurkowaniu (pseudo-Doppler).
5. **Wiatr prędkości** — pętla szumu filtrowanego, cutoff/gain ∝ prędkości lotu; świst przy dużym banku.
6. Drobne: doppler dla smoków AI, echo huku w dolinach (pogłos od AGL), tłumienie ryku ognia gdy kamera
   za smokiem vs z boku.

## Otwarte poza epikami
- Werdykt A2/A3 (wolumetryczny ogień) → potem B3 (haze), B4 (scorch), C1/C2.
- Skoki 200+ ms przy uploadach streamingu w locie (budżet uploadu/klatkę do przycięcia).
- Churn alokacji ~29 gen0/s w locie (przegląd alokacji pętli, kiedyś).
- Crash 22:42 (0xc0000005 w Microsoft.UI.Xaml, jednorazowy, przed audio/vsync) — bez repro; jeśli wróci:
  LocalDumps + analiza dumpa.
- Bug Spacji (panele + skrzydła naraz) — patrz poprzedni handoff §koniec.
