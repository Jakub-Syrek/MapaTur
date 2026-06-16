## Terrain graphics — MANDATORY before baking tiles / touching the terrain pipeline

Before you (re)generate or bake any DEM / ortho / z16 tiles, OR change the terrain load / repair / render
pipeline, **read [`docs/TERRAIN-GRAPHICS-CHECKLIST.md`](docs/TERRAIN-GRAPHICS-CHECKLIST.md) and apply EVERY
relevant item — comprehensively, across ALL render paths at once.** Do not fix one path/symptom and forget
the siblings; that is the recurring failure that makes us re-bake in circles. After any change, run the
checklist's verification (cache audit + visual sweep at multiple spots), not just the one location you were on.

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
