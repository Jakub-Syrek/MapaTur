# Wytyczne pracy na MapaTur — TAK JAK USTALILIŚMY (nie degenerować, nie szukać obejść)

> Spisane 2026-07-01 z realnych ustaleń sesji i **zweryfikowane** względem CLAUDE.md + pamięci (4-agentowy pass —
> zero sprzeczności). To jest kontrakt sposobu pracy. Przyszła sesja: czytasz to i **stosujesz bez rozwadniania**.
> Każdy punkt ma źródło; punkty oznaczone **[NOWE]** nie były dotąd nigdzie zapisane — nie zgub ich.

---

## A. Jeden proces do końca — ZERO obejść, sztuczek, skrótów i zmiany celu **[NOWE]**
- Kiedy jest zadanie, **realizujesz JEDEN proces do końca**. Nie zmieniasz celu w połowie („czy ty nie potrafisz
  zrealizować jednego procesu bez zmieniania targetu 5×"). Nie przeskakujesz na inny objaw, gdy bieżący jest
  trudny.
- **Nie szukasz workaroundów, trików ani dróg na skróty** („przestań wyszukiwać workaroundy… bo kręcimy się w
  kółko"). Jak coś blokuje — diagnozujesz przyczynę i rozwiązujesz ją, nie omijasz.
- Blokada środowiskowa (np. SAC blokujący DLL) ≠ pretekst do skrótu w kodzie — nazywasz ją wprost i prosisz o
  odblokowanie, nie kombinujesz obejściem.
- Rozszerza (nie zastępuje) `no-big-decisions-without-consent` („endless solo theorising instead of a decisive
  test", „ortho swap = unnecessary detour").

## B. Nie koloruj na różowo — raportuj porażki wprost **[NOWE]**
- „Nie koloruj tego na różowo." Meldujesz porażki, częściowe wyniki i wątpliwości **wprost i sucho**. Żadnego
  upiększania. Rozróżniasz **co WIEM** vs **co ZAKŁADAM** (para do `no-premature-success-claims`, ale to jest
  pozytywny obowiązek szczerości, nie tylko zakaz przechwalania).

## C. Jedna zmiana → build → apka OTWARTA → werdykt usera → dalej
- HARD: jedna zmiana, build, **apka zostaje otwarta**, czekasz na **wzrokowy werdykt usera**, dopiero kolejny
  krok. Nie bundlujesz. Nie zamykasz apki, gdy na ekranie są artefakty.
  (`work-style-ask-verify-no-charge`)

## D. Nie ogłaszaj sukcesu z proxy; mierz, nie zgaduj; close-up UJAWNIA, nie dowodzi **[częściowo NOWE]**
- Green build / `adb install OK` / log-line że code-path się odpalił **niczego nie dowodzą** o user-visible
  wyniku. Nie mówisz „Gotowe ✅" aż potwierdzi user albo test na obserwowalnym sygnale.
  (`no-premature-success-claims`, `always-diagnostics-logging`)
- **Mierz, nie twierdź.** Nie ufaj miarom doraźnym (skalującym się z rozstawem próbek, blur z artefaktami brzegu).
  Preferuj JEDNĄ metrykę robust — dla reliefu: **`RMS(z16 − upsample(boxavg(z16)))`**. **[NOWE]**
- **Close-up nie dowodzi, że jest OK — close-up UJAWNIA problem.** Weryfikuj w KILKU miejscach mapy, nie w jednym.
  **[NOWE]**

## E. Fix „B" (detail layer) — uzgodniony tradeoff, NIE regresować
- Amplituda = **100 % realny z16 residual**; wzór drobnej faktury = proceduralny (prawdziwy wzór wymaga danych
  z16-res = odrzucony koszt pamięci). „Żadnych kompromisów" = **nie regresuj tego do czystego shader-noise** ani
  nie zdejmuj bramki realnej amplitudy (`vDetail > 0.01`). Pełny opis: `docs/SMOOTH-SURFACE-BUG.md`.
  (`detail-layer-and-sac-block`)

## F. Teren: czytaj checklistę PRZED, stosuj na WSZYSTKICH ścieżkach naraz
- Przed (re)generacją/bake DEM/orto/z16 LUB zmianą load/repair/render — przeczytaj
  `docs/TERRAIN-GRAPHICS-CHECKLIST.md` i zastosuj każdy relevantny punkt **komplet, na wszystkich ścieżkach
  renderu naraz** (auto-load / ring-LOD base / single-patch detail / per-tile detail). Nie łatasz jednego
  symptomu zapominając siblingów. Po zmianie: cache audit + wzrokowy sweep w kilku miejscach.
  (`CLAUDE.md`, checklist §0/§E, `terrain-graphics-fixes-comprehensive`)

## G. Żadnego dużego/destrukcyjnego ruchu bez wyraźnego „tak"
- Re-bake całości, swap danych (ESRI↔GUGiK), revert commitów, `git push` (zwł. main), force-push, push setek MB,
  `adb uninstall`/wipe, kasowanie plików → **propozycja + koszt, CZEKAJ na „yes"**. Przed push:
  `dotnet format --verify-no-changes` (wszystkie 4 proj. src) + pełna suita green.
  (`no-big-decisions-without-consent`)

## H. 100 % znaczy 100 %, nie 99 % **[NOWE]**
- Gdy user mówi „ma pokrywać CAŁY teren", „bezbłędnie", „nie 99% a 100%" — „done" wymaga **pełnego** pokrycia.
  Dziury nazywasz jawnie z liczbami (np. „282 kafle NoData na skraju zach., 100% wymaga sąsiedniego SK LOT"), nie
  raportujesz „zrobione" przy luce.

## I. Język i atrybucja
- Odpowiedzi **po polsku**; kod, symbole, commit messages, ścieżki — po angielsku jak w repo. (`reply-in-polish`)
- **NIGDY** Claude/AI jako author/committer/co-author; **żadnego** `Co-Authored-By` / „Generated with Claude" /
  🤖. Commity wyłącznie usera — hard dealbreaker. (`no-claude-commit-author`, global CLAUDE.md)

---

### Skąd to wiadomo (weryfikacja 2026-07-01)
Pass 4 agentów potwierdził: zero sprzeczności między tymi ustaleniami a istniejącymi regułami; punkty A, B, H i
uogólnienie D („robust metric / close-up reveals") były **nigdzie nie zapisane** — dlatego ten plik + wpis w
pamięci `work-style-ask-verify-no-charge`. Reszta (C, E, F, G, I) już istniała w pamięci/CLAUDE.md/checklist i tu
jest tylko skonsolidowana, żeby była w jednym miejscu jako kontrakt.
