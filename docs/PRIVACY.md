# MapaTur Privacy Policy

**Last updated:** 2026-05-25

MapaTur is an offline-first hiking map application. This document describes what data the application stores, what it sends over the network, and what it deliberately does **not** collect.

## Data stored on your device

MapaTur stores all data locally in the application's private storage directory (`AppDataDirectory` provided by the operating system). Nothing in this folder is shared, uploaded, or backed up unless you explicitly opt in via your OS.

| What | Where | Why |
|---|---|---|
| Tile archives (`*.mbtiles`) you open | Wherever you picked them; **not copied** by MapaTur | Map rendering |
| Hiking trail data (`mapatur-trails.db`) | `<AppData>/mapatur-trails.db` | Cache of OSM trails for offline routing |
| Exported routes (`*.gpx`) | `<AppData>/exports/` | Routes you exported to share |
| Application logs | `<AppData>/logs/mapatur-YYYY-MM-DD.log` | Local diagnostics, rotated after 7 days |

## Data transmitted over the network

MapaTur is designed to work fully offline. The only outbound network requests are made when **you explicitly press a button**:

| Trigger | Destination | What is sent |
|---|---|---|
| `Download Trails (viewport)` | `https://overpass-api.de/api/interpreter` (or a mirror you configure) | An Overpass QL query containing the **current map viewport bounding box**. No identifiers, no device info, no user account. |

The request body is a public OSM query indistinguishable from any other Overpass user. The HTTP client does not send a `User-Agent` beyond the default .NET runtime string and does not send cookies.

## What MapaTur does NOT do

- ❌ No telemetry or analytics
- ❌ No crash reporting service (logs stay on your device)
- ❌ No user accounts; no login
- ❌ No advertising
- ❌ No third-party SDKs that send data
- ❌ No background location tracking
- ❌ No contact, photo, or other personal data access

## Permissions requested

- **Location** (planned for future GPS dot feature): only used at runtime, only when the map is visible, never stored or transmitted.
- **Storage** (Android): required to open MBTiles/TCX files you pick.
- **Network** (all platforms): used exclusively for the Overpass trail download described above.

## Open source

MapaTur is open source: [github.com/Jakub-Syrek/MapaTur2](https://github.com/Jakub-Syrek/MapaTur2). You can audit every network call, every file write, and every dependency in the source tree.

## Contact

Questions about this policy: open an issue at the repository above.
