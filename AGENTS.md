# AGENTS.md — wytyczne dla każdej sesji i każdego agenta MapaTur

## P0 — architektura renderingu i streamingu

Cały projekt jest uznawany za niesprawny, dopóki nie zapewnia jednocześnie jakości obrazu i płynności. Wszystkie inne zadania, tuning shaderów, deshadow, nowe funkcje i kosmetyka są wstrzymane.

Nie pokazuj więcej półrozwiązań do akceptacji. Nie pytaj użytkownika o mikrozgody. Zaplanuj kompletną architekturę, wykonaj ją konsekwentnie end-to-end i sam odrzucaj warianty, które nie przechodzą bramki produktu.

### Docelowa architektura

1. Wszystkie ortofotomapy mają być przygotowane offline do formatu bezpośrednio używalnego przez GPU: kompresja BC, komplet mipów, małe strony streamingu i gotowy indeks przestrzenny.
2. Runtime nie może wykonywać produkcyjnego dekodowania setek WebP, kompozycji wielkich cel, kodowania BC ani generowania mipów. Pierwsza wizyta ma korzystać z prebake'u, a nie tworzyć cache podczas oglądania.
3. Prebake obejmuje cały docelowy obszar, nie tylko miejsca wcześniej odwiedzone. Pusty cache runtime nie może oznaczać 10–60 sekund oczekiwania.
4. Jednostką streamingu nie może być monolit liczący setki MB. Dane mają być podzielone na małe strony pozwalające na niezauważalne doczytywanie i wymianę.
5. LOD wybieramy według rozmiaru teksela na ekranie, dla całego widocznego kadru:
   - 5 cm tam, gdzie rzeczywiście wpływa na piksel ekranu;
   - 25 cm jako ciągła warstwa średniego dystansu;
   - baza tylko tam, gdzie jej rozdzielczość jest wystarczająca ekranowo.

   Panorama o widoczności 10 km nie może mieć ostrego obszaru tylko 100 m przed kamerą i rozmytej reszty.
6. Rezydencja ma być stabilna. Minimalny obrót kamery nie może usuwać nadal widocznych stron. Wymagane są histereza, ochrona widocznych stron, prefetch i stabilny dobór LOD.
7. I/O, dekompresja i upload mają działać asynchronicznie. Wątek renderujący nie może wykonywać ciężkiej produkcji danych ani czekać na synchroniczny transfer.
8. Budżety RAM i VRAM wynikają z dostępnego sprzętu i profilu, nie ze sztywnych, przypadkowych limitów.

### Kolejność realizacji

1. Najpierw dokument architektury zawierający format danych, rozmiar strony, poziomy LOD, cache, kolejki I/O/uploadu, politykę rezydencji i budżety pamięci.
2. Następnie narzędzie pełnego prebake'u.
3. Potem runtime czytający wyłącznie gotowe strony GPU.
4. Następnie stabilna rezydencja i screen-space LOD.
5. Na końcu pełny bake Tatr i test end-to-end.

Nie zbaczaj do kolejnych lokalnych tweaków, dopóki ta ścieżka nie jest ukończona.

### Twarda bramka odbioru

Test wykonujemy na skompilowanym EXE, danych w AppData i monitorze DELL P2722H. Iiyama jest nietykalna.

Kandydat przechodzi dopiero wtedy, gdy:

- pierwsza wizyta korzysta z prebake'u i nie wymaga wielosekundowej kompozycji;
- panorama Morskiego Oka jest ostra w całym użytecznym kadrze;
- poruszenie myszą nie powoduje utraty detalu;
- nie ma niebieskich plam, brei, pustych stron ani brutalnych przejść LOD;
- cold i warm działają poprawnie;
- nie ma powtarzalnych przycięć niszczących użyteczność;
- jakość bliskiego planu nie została pogorszona;
- obraz i wydajność przechodzą jednocześnie.

Wynik ciepłego cache'u nie może maskować wad pierwszej wizyty. Zielone logi, benchmark F9 i cache-hit nie zastępują testu rzeczywistego scenariusza użytkownika.

### Zasada komunikacji

Nie przepraszaj, nie powtarzaj opisu problemu i nie zapowiadaj kolejnego „przełomowego fixu". Raportuj dopiero:

- ukończony element architektury;
- wynik testu end-to-end;
- konkretne niespełnione kryteria;
- rollback, jeśli kandydat nie przechodzi.

Uzupełnienie: pełny zestaw 18 stałych zasad — [`docs/ZASADY-MAPATUR.md`](docs/ZASADY-MAPATUR.md). W konflikcie z nimi P0 wygrywa.

---

## Testing Conventions

### TDD Workflow
- Always write failing tests BEFORE implementation
- Use AAA pattern: Arrange-Act-Assert
- One assertion per test when possible
- Test names describe behavior: "should_return_empty_when_no_items"

### Test-First Rules
- When I ask for a feature, write tests first
- Tests should FAIL initially (no implementation exists)
- Only after tests are written, implement minimal code to pass
