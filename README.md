# MapaTur

Offline-first hiking and tourist map application built with .NET MAUI.

## Features (planned)

- Offline maps via MBTiles (OpenStreetMap)
- Official hiking trails (OSM `route=hiking`, color-coded by `osmc:symbol`)
- Off-trail routing with configurable cost functions
- TCX track import from Garmin devices
- Route planning with A* over a trail graph
- Elevation profile with time estimation (Naismith's rule + Tobler hiking function)
- GPX export

## Architecture

Clean Architecture with four layers:

- `MapaTur.Domain` — entities, value objects, domain services (pure C#)
- `MapaTur.Application` — use cases and ports (no IO)
- `MapaTur.Infrastructure` — SQLite, HTTP, file system, parsers
- `MapaTur.Routing` — graph routing engine (A* / Dijkstra)
- `MapaTur.App` — .NET MAUI presentation (MVVM)

See [`docs/adr/`](docs/adr) for architecture decisions and [`docs/ROADMAP.md`](docs/ROADMAP.md) for milestones.

## Build

```bash
dotnet build
dotnet test
```

Requires .NET 10 SDK and MAUI workload (`dotnet workload install maui`).

## Localization

UI is available in English (default) and Polish. The application picks the language from
`CultureInfo.CurrentUICulture` at startup (configured by the host OS).

## Privacy

MapaTur is offline-first and does not collect telemetry. The only outbound network call is
the Overpass trail download you explicitly trigger. See [docs/PRIVACY.md](docs/PRIVACY.md)
for the full policy.

## License

Copyright (c) Jakub Syrek. All rights reserved.
