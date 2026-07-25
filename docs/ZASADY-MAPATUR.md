# Stałe zasady MapaTur

Obowiązują wszystkie przyszłe prace nad MapaTur — każdą sesję i każdego agenta. Nie wolno ich zmieniać
bez wyraźnej zgody użytkownika (ustanowione 2026-07-23).

1. Kryterium sukcesu jest podwójne: aplikacja musi jednocześnie dobrze wyglądać i działać płynnie. Poprawa wydajności kosztem obrazu albo obrazu kosztem wydajności jest odrzucana.
2. Liczy się efekt w rzeczywistej aplikacji desktopowej. Testujemy skompilowane `MapaTur.App.exe`, na danych z AppData, w finalnym rendererze. Test skryptu, zielony log, metryka offline ani diagnostyczny obraz nie oznaczają sukcesu.
3. Użytkownik jest ostatecznym sędzią wizualnym. Metryki mają pomagać diagnozować, ale nie mogą unieważniać widocznego problemu.
4. Nie ogłaszaj hipotezy jako rozwiązania. BC, PBO, LOD, cache, maska czy nowy algorytm są tylko kandydatami, dopóki nie przejdą pełnego testu przed/po.
5. Każda zmiana musi być odwracalna. Pracuj na osobnej gałęzi, zachowuj działający baseline i przygotuj prosty rollback. Nie nadpisuj źródłowych danych ani zatwierdzonych wariantów.
6. Zakaz whack-a-mole. Naprawiając problem B, nie wolno pogorszyć A. Przed implementacją zapisz niezmienniki, które muszą pozostać spełnione, a po zmianie przetestuj cały zestaw regresji.
7. Mniej iteracji, większe pakiety pracy. Nie pytaj o zgodę na każdy techniczny detal i nie pokazuj kolejnych półproduktów. Samodzielnie odrzucaj warianty niespełniające kryteriów. Pokazuj dopiero skonsolidowany kandydat albo uczciwy raport o niepowodzeniu.
8. Porównanie zawsze w identycznych warunkach: ta sama kompilacja, kamera, trasa, pora dnia, ustawienia renderera i dane. Obowiązkowo cold cache oraz warm cache.
9. Streaming musi zachowywać stabilność obrazu. Minimalny ruch lub obrót kamery nie może usuwać nadal widocznych danych, obniżać ostrości ani rozpoczynać wielosekundowego ponownego ładowania.
10. Ostry detal ma obejmować użyteczny kadr, nie małą plamę wokół kamery. Bliskie 5 cm nie usprawiedliwia rozmytej większości panoramy. Warstwy 5 cm, 25 cm i baza mają tworzyć ciągły, stabilny LOD bez brei, niebieskich plam i widocznych przejść.
11. Ciężkie przetwarzanie assetów wykonujemy offline. Runtime nie powinien ponownie dekodować setek WebP, komponować wielkich cel ani generować mipów przy każdym powrocie kamery. Dane mają być przygotowane do bezpośredniego streamingu w formacie GPU, z gotowymi mipami.
12. Jednostki streamingu muszą być małe. Monolityczne cele liczące setki MB są niedopuszczalne. Streaming, cache i rezydencja muszą działać stronami wystarczająco małymi, aby wymiana była niezauważalna.
13. Nie blokuj wątku renderującego. Dekodowanie, kompozycja i przygotowanie mipów odbywają się poza nim. Transfery GPU mają korzystać z właściwego mechanizmu asynchronicznego i kontrolowanego budżetu.
14. Wykorzystuj dostępny sprzęt. Na mocnym desktopie wielosekundowe doczytywanie, niskie użycie GPU i przycięcia nie mogą być tłumaczone „limitem sprzętu" bez profilu dowodzącego faktycznego nasycenia zasobu.
15. Stały gate odbioru panoramy:
    - brak oczekiwania 10–15 sekund na ostrość;
    - brak utraty detalu przy lekkim ruchu myszy;
    - brak zatrzymań rzędu 150–300 ms;
    - brak rozmytej większości widoku;
    - brak niebieskich plam, pustych pól i szwów LOD;
    - brak pogorszenia jakości bliskiego planu;
    - obraz i płynność muszą przejść jednocześnie.
16. Nie naprawiaj danych i renderera w ciemno równocześnie. Każda anomalia musi być przypisana do konkretnej warstwy: źródłowe orto, bake, loader, LOD, materiał, syntetyczne światło albo postprocessing.
17. Środowisko testowe jest stałe: testy i automatyzacja wyłącznie na monitorze DELL P2722H. Monitor Iiyama PL3461WQ należy do użytkownika — zakaz przesuwania okien, klikania, przechwytywania go i zakłócania pracy.
18. Nie kończ komunikatu kolejną obietnicą „następny fix już rozwiąże problem". Raportuj: co zmierzono, co rzeczywiście działa w aplikacji, co odrzucono i jakie ograniczenia nadal widać.
19. **Konflikt wytycznych rozstrzyga WYŁĄCZNIE użytkownik — zawsze pytaj.** Gdy dwie zasady, dwa kryteria albo zasada i pomiar wskazują przeciwne decyzje (klasyczny przypadek: zasada 1 — obraz kontra płynność), agentowi NIE WOLNO wybrać strony samodzielnie ani „na podstawie liczby". Zatrzymaj się, przedstaw obie opcje z konkretnym kosztem każdej i poczekaj na decyzję. Dotyczy to również **cofania stanu, który użytkownik już widział i zaakceptował** — takiego stanu nie wolno wycofać na podstawie własnego pomiaru agenta; jeżeli pomiar mówi coś niepokojącego, pokazujesz pomiar i pytasz, a stan zostaje do czasu odpowiedzi.
    Przykład, który tę zasadę ustanowił (2026-07-25): agent zbudował det05 z 96 celami, użytkownik latał na tym nad Morskim Okiem i miał detal w całym kadrze — po czym agent SAM cofnął to do 48 cel, uzasadniając „terrain 18,7 ms, 32 FPS, łamie płynność". Użytkownik odkrył regresję dzień później. Po przywróceniu 96 zmierzono `terrain 0,55 ms / sumGpu 4,78 ms` i werdykt brzmiał „jest dużo lepiej" — czyli agent nie tylko przekroczył swoje uprawnienia, ale zrobił to na podstawie pomiaru, który się nie odtworzył.
