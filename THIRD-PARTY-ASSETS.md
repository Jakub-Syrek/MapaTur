# Third-party assets

## 3D models

### `src/MapaTur.App/Resources/Raw/dragon.glb` — ridden dragon (F7 flight)

A rigged, low-poly dragon (79-bone skeleton, ~2.5k triangles) used for the F7 dragon-flight mode. Loaded and
CPU-skinned at runtime via `SkinnedModel` (SharpGLTF); the wing-beat is driven **procedurally** (the download has
no baked animation clip), so the model just needs the rig.

- **License:** Creative Commons Attribution (CC-BY) — *attribution required*.
- **Source / author:** ⚠️ TO CONFIRM — downloaded from Sketchfab ("Dragon Rigged"). Fill in the exact model URL
  and author here before any public distribution, and surface the credit in the app's About/credits screen.

If we cannot confirm a CC-BY/CC0 source with proper attribution, swap in a CC0 dragon (e.g. Quaternius Ultimate
Monsters) so the licensing is unambiguous.

### `src/MapaTur.App/Resources/Raw/dragon-animated.glb` — animated dragon variant (F7 flight)

A rigged, textured dragon (219-bone skeleton, ~19.5k triangles) with three baked animation loops
(`idle` 12.4 s, `running` 10.0 s, `flying` 13.1 s). Selected via the "🐉 Smok (F7)" chip in the Widok panel;
the F7 flight plays the `flying` loop through `SkinnedModel.Pose`. Base colour comes from the embedded
`KHR_materials_pbrSpecularGlossiness` diffuse texture (alpha-masked wing membranes).

- **License:** ⚠️ TO CONFIRM — downloaded as "animated-dragon-three-motion-loops.zip" (Sketchfab, exporter
  "Microsoft GLTF Exporter 2.8.3.32", 2022). Fill in the exact model URL, author, and license here before any
  public distribution, and surface the credit in the app's About/credits screen.
- Note: the GLB is ~35 MB (embedded textures + three clips) — fine for the desktop build; revisit
  (decimate/re-encode textures) before bundling into the mobile package.

### `tests/MapaTur.Application.Tests/TestData/Fox.glb` — skinning-engine test model (tests only)

The Khronos "Fox" glTF sample asset, used only by the `SkinnedModel` unit tests (never shipped in the app).

- Model: PixelMannen (CC0). Rig + animation: [@tomkranis] (CC-BY 4.0).
- Source: <https://github.com/KhronosGroup/glTF-Sample-Assets> (Models/Fox).
