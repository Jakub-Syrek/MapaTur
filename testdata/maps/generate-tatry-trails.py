"""Pre-fetches the Polish Tatras hiking-trail set from Overpass into a bundled JSON file.

The app's startup auto-loader (FileSystemMapAutoLoader) picks up the first *.json whose
filename contains "trail" under <repo>/dem, so trails show on first launch without a live
Overpass download. The saved document is the raw Overpass `out:json` response, byte-for-byte
what OverpassResponseParser.Parse already consumes — same query the in-app download button
builds (OverpassQueryBuilder.BuildHikingTrailsQuery): route=hiking relations + recursed member
ways with inline geometry.

Output: <repo>/dem/tatry-trails.json (auto-loader's preferred root).

Run:
  python testdata/maps/generate-tatry-trails.py
"""

from __future__ import annotations

import os
import sys

import requests

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))

# Default output goes under <repo>/dem so the auto-loader's preferred root picks it up.
OUTPUT_PATH = os.path.join(REPO_ROOT, "dem", "tatry-trails.json")

# Tatry bbox — same extent as the DEM/hillshade pipelines so trails cover the whole terrain.
WEST, SOUTH, EAST, NORTH = 19.50, 49.10, 20.40, 49.40

# Server-side timeout baked into the query; the regional relation set is large, so allow
# more than the in-app default (60 s).
QUERY_TIMEOUT_SECONDS = 180

# Mirror OverpassEndpoints.DefaultFallbackList: try each in order until one answers.
ENDPOINTS = [
    "https://overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
    "https://lz4.overpass-api.de/api/interpreter",
    "https://overpass.private.coffee/api/interpreter",
    "https://overpass.openstreetmap.fr/api/interpreter",
]

USER_AGENT = "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)"


def build_query() -> str:
    """Replicates OverpassQueryBuilder.BuildHikingTrailsQuery for the Tatry bbox."""
    return (
        f"[out:json][timeout:{QUERY_TIMEOUT_SECONDS}];\n"
        "(\n"
        f'  relation["route"="hiking"]({SOUTH:.6f},{WEST:.6f},{NORTH:.6f},{EAST:.6f});\n'
        ");\n"
        "out body;\n"
        ">;\n"
        "out skel geom;\n"
    )


def fetch_with_failover(query: str) -> bytes:
    """POSTs the query to each endpoint in turn, returning the first successful response body."""
    last_error: Exception | None = None
    for endpoint in ENDPOINTS:
        print(f"  POST {endpoint}")
        try:
            response = requests.post(
                endpoint,
                data={"data": query},
                headers={"User-Agent": USER_AGENT},
                timeout=QUERY_TIMEOUT_SECONDS + 30,
            )
            response.raise_for_status()
            return response.content
        except requests.RequestException as error:
            print(f"    failed: {error}")
            last_error = error
    raise RuntimeError("All Overpass endpoints failed.") from last_error


def write_output(path: str, payload: bytes) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as out:
        out.write(payload)
    print(f"  wrote {path} ({len(payload) / 1024:.0f} KB)")


def main() -> int:
    print("Pre-fetching Tatry hiking trails from Overpass...")
    query = build_query()
    payload = fetch_with_failover(query)
    write_output(OUTPUT_PATH, payload)
    print("done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
