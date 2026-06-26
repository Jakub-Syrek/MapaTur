# Trasa „przez Granaty" zamiast Żlebem Kulczyńskiego — recurring data bug + fix

**Objaw:** planowana trasa przez grań (np. Murowaniec → Zawrat → Kozi Wierch → punkt na szlaku → Murowaniec)
prowadzi z powrotem **przez Granaty** zamiast zejść **Żlebem Kulczyńskiego**, albo „wchodzi w żleb i zawraca".
Pojawia się **po ponownym pobraniu szlaków** („Pobierz szlaki"). Wraca co jakiś czas.

## To są DANE, nie kod
Cały fix routingu żlebu jest w kodzie i NIE należy go ruszać:
- `MauiProgram.cs`: `SqliteTrailRepository(..., simplificationEpsilonMeters: 0.0)` — pełna geometria, junctiony
  zachowane (uproszczenie 10 m gubiło je → graf nie łączył szlaków).
- `OverpassResponseParser`: składa człony relacji (`FlushSegment`), `EndpointMatchTolerance`.
- `TrailRoutePlanner`: snap drobnych dziur na skrzyżowaniach.
- `MapPageViewModel`: planer woła `RouteProfile.ShortestDistance` (NIE `FastestTime` — ten omijał stromy żleb).

## Root cause gdy znika
Żleb jest w bazie jako **3 osobne trasy** spotykające się w węźle `49.2209,20.0328` (dokładnie ten sam punkt):
- `1593577` „Kozia Dolinka - Żleb Kulczyńskiego" — od węzła **w dół** do `49.2222,20.0272`,
- `3349371` „Kozi Wierch - Żleb Kulczyńskiego" — od Koziego do węzła,
- `3349373` „Żleb Kulczyńskiego - Skrajny Granat" — od węzła w górę ku Granatom.

Zejście (`1593577`) kończy się na `49.2222,20.0272`. **Żeby trasa zeszła żlebem, MUSI tam dochodzić dalsza
trasa-łącznik w dół** (Kozia Dolinka → Czarny Staw/Murowaniec). Gdy ponowne „Pobierz szlaki" **zgubi ten dolny
łącznik** (mniejszy kadr / inna odpowiedź Overpass), żleb **kończy się ślepo** → planer schodzi przez Granaty
(tamta strona jest połączona przez `3349373`).

## Fix (nic nie tyka kodu, nie cofa niczego)
**Pobierz szlaki ponownie z CAŁYM obszarem trasy w kadrze** — Murowaniec ↔ żleb ↔ Czarny Staw (oddal kamerę tak,
by dolina poniżej żlebu też była widoczna). To dociąga z OSM dolny łącznik → żleb znów schodzi.

## Jak zdiagnozować w 10 s (zamiast godzin)
Baza: `…\com.companyname.mapatur.app\Data\mapatur-trails.db` (tabela `trails`, geometria = punkty `lat,lon`
rozdzielone `;`). Sprawdź, czy do końca zejścia żlebu dochodzi inna trasa:
```python
import sqlite3
con = sqlite3.connect(DB); cur = con.cursor()
tol = 0.0003  # ~30 m
for i,n,g in cur.execute('select id,name,geometry from trails where geometry is not null'):
    ps = [p.split(',') for p in g.split(';')]
    for a,b in (ps[0], ps[-1]):
        if abs(float(a)-49.2222016) < tol and abs(float(b)-20.0271949) < tol:
            print(i, n)   # only 1593577 ⇒ łącznik ZGUBIONY ⇒ pobierz szlaki szerzej
```
Jeśli wypisze **tylko `1593577`** — dolny łącznik zniknął, pobierz szlaki ponownie (szerszy kadr).
Jeśli wypisze też inną trasę — łącznik jest, problem leży gdzie indziej (NIE zaczynaj od zera).
