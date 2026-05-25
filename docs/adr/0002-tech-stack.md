# 2. Technology stack

Date: 2026-05-25

## Status

Accepted

## Context

The application must run on Android, iOS, Windows and macOS, operate offline in mountain environments without connectivity, render large vector and raster map data, and support route planning over a graph of hiking trails.

## Decision

| Concern | Choice | Alternatives considered |
|---|---|---|
| UI framework | .NET MAUI (.NET 10) | Avalonia (no native mobile), WPF (Windows only), Blazor Hybrid (heavier, web focus) |
| Map rendering | Mapsui + BruTile | MapLibre Native (immature .NET bindings), SkiaSharp from scratch (reinvent the wheel) |
| Geometry | NetTopologySuite | Custom implementations (error-prone), proj.net only (no topology) |
| Local storage | SQLite + SpatiaLite extension | LiteDB (no spatial indexes), file-only (no queries) |
| Offline tiles | MBTiles | Filesystem of PNGs (slow, no metadata), PMTiles (less .NET tooling) |
| Trail data source | OpenStreetMap via Overpass API | Geoportal/PZGS (licensing complexity), commercial APIs (cost, online dependency) |
| Elevation data | SRTM 1-arc GeoTIFF tiles | Online elevation APIs (offline requirement breaks) |
| Routing | Custom A* with pluggable cost functions | OSRM/Valhalla (server-based, not embeddable), GraphHopper Java (out of stack) |
| TCX parsing | System.Xml.Linq + custom mapper | Generic libraries are oversized and unmaintained |
| MVVM | CommunityToolkit.Mvvm source generators | ReactiveUI (heavier), hand-rolled INotifyPropertyChanged (boilerplate) |
| DI | Microsoft.Extensions.DependencyInjection | Autofac (more features than needed), no DI (poor testability) |
| Logging | Serilog with file sink | Microsoft.Extensions.Logging alone (no structured sinks) |
| Testing | xUnit + FluentAssertions + NSubstitute + FsCheck.Xunit | NUnit (equivalent, xUnit preferred for parallelism), MSTest (less idiomatic) |
| CI/CD | GitHub Actions | Azure DevOps (heavier setup) |

## Consequences

Positive:
- Stack is fully open-source and free for commercial use.
- All choices have active maintainers in .NET 10 ecosystem.
- Cross-platform with one codebase.

Negative:
- MAUI iOS/macOS builds require a Mac runner in CI for signed artifacts.
- Mapsui in MAUI on iOS occasionally needs version-pinning workarounds.
- Building MBTiles packages requires an offline preprocessing pipeline (out of scope for v1, manual for now).

## Related

- [[adr-0001-clean-architecture]]
