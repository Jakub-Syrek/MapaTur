## Terrain graphics — MANDATORY before baking tiles / touching the terrain pipeline

Before you (re)generate or bake any DEM / ortho / z16 tiles, OR change the terrain load / repair / render
pipeline, **read [`docs/TERRAIN-GRAPHICS-CHECKLIST.md`](docs/TERRAIN-GRAPHICS-CHECKLIST.md) and apply EVERY
relevant item — comprehensively, across ALL render paths at once.** Do not fix one path/symptom and forget
the siblings; that is the recurring failure that makes us re-bake in circles. After any change, run the
checklist's verification (cache audit + visual sweep at multiple spots), not just the one location you were on.

## Mobile (re)install — MANDATORY: verify the 1 m tile cache is COMPLETE

After EVERY mobile install / reinstall / data restore (anything that could touch the phone's z16 cache),
**verify the phone has the FULL 1 m tile set, not a sparse subset.** A reinstall or a partial package leaves
holes → the terrain renders "oble" (rounded peaks/trails) because most tiles fall back to the coarse base,
even though per-tile detail + budget are fine. Symptom in the on-screen LOD badge / log: `cache-only z16:
requested=144, cached=7` (i.e. ≈7/144) instead of ~full.

Check (Debug build → `run-as` works; adb at `C:\Program Files (x86)\Android\android-sdk\platform-tools`):
```
# phone tile count (PKG = com.companyname.mapatur.app)
adb exec-out run-as PKG sh -c 'find files/dem-cache/gugik/16 -type f | wc -l'
# desktop reference count (the comprehensive set lives here)
find "C:/Users/<user>/AppData/Local/User Name/com.companyname.mapatur.app/Data/dem-cache/gugik/16" -type f | wc -l
```
The phone count MUST match the desktop (e.g. 7338). If the phone is short, push the missing tiles from the
desktop (the bundled package alone is only ~4265 tiles and has gaps over the Orla Perć core):
```
# diff: list both (relative paths under 16/), comm -23 desk phone > missing.txt
tar --force-local -C "<DESK>/dem-cache/gugik/16" -cf missing.tar -T missing.txt   # ~800 MB
adb push missing.tar /data/local/tmp/ && adb shell chmod 644 /data/local/tmp/missing.tar
adb exec-out run-as PKG sh -c 'cd files/dem-cache/gugik/16 && tar -xf /data/local/tmp/missing.tar'
adb shell rm /data/local/tmp/missing.tar
```
NOTE: the `adb exec-out run-as ... tar -xf -` STDIN pipe HANGS — use the push-to-/data/local/tmp + run-as
extract path above (app-uid can read the 644 tmp file). The per-tile build only re-runs on a camera move,
so after pushing, pan the camera to see `cached` jump and the 1 m detail fill in.

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
