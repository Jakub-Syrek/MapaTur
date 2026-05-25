# 1. Clean Architecture with four layers

Date: 2026-05-25

## Status

Accepted

## Context

MapaTur is a long-lived application with non-trivial domain logic (route planning, geometry, file parsing) and multiple infrastructure concerns (SQLite, HTTP, file system, GPS, native MAUI services). Without a clear layering strategy, business rules tend to leak into UI code and platform adapters, which makes the system difficult to test, port to new platforms, and reason about.

## Decision

We adopt Clean Architecture with the following four layers and dependency rule (arrows point inward only):

```
Presentation (MapaTur.App, MAUI)
        |
        v
Application (MapaTur.Application)  --->  Routing (MapaTur.Routing)
        |                                       |
        v                                       v
                    Domain (MapaTur.Domain)
        ^
        |
Infrastructure (MapaTur.Infrastructure)
```

- **Domain** contains entities, value objects and domain services. Pure C#, no IO, no framework references.
- **Application** orchestrates use cases and defines ports (interfaces) for infrastructure. No IO either.
- **Infrastructure** implements the ports: SQLite, HTTP clients (Overpass), file system, XML parsers.
- **Routing** is a separate domain-aligned module because the graph engine is large and benefits from independent evolution and testing. It depends only on Domain.
- **Presentation** is the MAUI app. It consumes Application use cases via DI and contains no business logic.

## Consequences

Positive:
- Domain and Application are trivially unit-testable (no mocks for IO needed).
- Swapping the map library, storage engine, or even the UI framework affects only the outer layers.
- New developers can read the Domain layer first to understand the product.

Negative:
- More projects to maintain than a single-assembly approach.
- Some ceremony writing ports and adapters for simple cases. Accepted as the price of testability and platform portability.

## Related

- [[adr-0002-tech-stack]]
- [[adr-0003-offline-first]]
