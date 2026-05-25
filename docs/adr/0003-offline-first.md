# 3. Offline-first data strategy

Date: 2026-05-25

## Status

Accepted

## Context

The primary use case is hiking in mountainous areas where cellular coverage is unreliable or absent. The application must remain fully functional with no network connection: viewing maps, importing GPS tracks, planning routes, and recording trips.

## Decision

All data required for in-field operation is stored locally before the trip:

1. **Map tiles** are packaged as MBTiles files per region. The user downloads a region archive over Wi-Fi from a known mirror, and the application registers it as a tile source.
2. **Trail data** (OSM `route=hiking`, `route=foot`, `route=bicycle` for off-trail context) is fetched via Overpass API into a regional SQLite database, indexed with SpatiaLite R-Tree.
3. **Elevation data** is bundled as SRTM 1-arc tiles per region, indexed by 1-degree grid.
4. **Routing graph** is precomputed from trail data and stored in the same SQLite as serialized adjacency lists for fast cold-start.
5. **User data** (planned routes, imported TCX tracks, waypoints) is stored in a separate SQLite database to keep user content cleanly separated from immutable reference data.

Online features are additive and degrade gracefully:
- Region updates pull a delta from Overpass when connectivity returns.
- Crowdsourced off-trail submissions are queued locally and synced on reconnection.

## Consequences

Positive:
- Application works in the field with zero network.
- Storage layout supports per-region downloads, so users only pay storage cost for areas they use.
- Test data is trivially bundled (commit a small Tatry-region MBTiles + SQLite into `testdata/`).

Negative:
- Preprocessing pipeline is required to produce regional archives. For v1 this is a manual operator workflow; automation is deferred.
- Storage budget per region can be large (≈ 200–500 MB for a typical mountain range with all layers). Users must opt in to which regions to install.

## Related

- [[adr-0001-clean-architecture]]
- [[adr-0002-tech-stack]]
