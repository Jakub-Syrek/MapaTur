# MapaTur.Climbing

Renderer-independent climbing math: typed holds, contact/occupancy rules, quasi-static
force/moment equilibrium, whole-body climb solving, anatomy guardrails.

Ported from `Climber3d.Core` at `C:\Repos\Climber3d`, commit `7335a954fcec4cef1230e59687f24835eed685f0`
(tag `mapatur-handoff-baseline`, 2026-07-16). Integration contract and migration plan:
`Climber3d/docs/MAPATUR_INTEGRATION_HANDOFF.md`.

Hard rules for callers:

- All solver inputs are **real-world metres**, X-east / Y-north / Z-up, gravity `(0, 0, -9.81)`.
  Vertical exaggeration must never reach this project — convert with `ClimbSpaceTransform`.
- Hold identity must be stable across terrain LOD swaps (no vertex/triangle indices).
- No MAUI, OpenGL, DEM, or rig-format dependencies may be added here.
