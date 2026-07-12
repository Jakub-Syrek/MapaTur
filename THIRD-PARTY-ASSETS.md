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

### `src/MapaTur.App/Resources/Raw/hiker.glb` — 3rd-person walk-mode avatar (F8)

A rigged, textured low-poly humanoid — KayKit "Character Pack: Adventurers", the `Rogue_Hooded` character —
used as the 3rd-person avatar in walk mode. 12 meshes, a single skin (~54-node rig), and 76 baked animation
clips (`Idle`, `Walking_A/B/C`, `Running_A/B`, `Jump_Start/Idle/Land`, dodges, and more). Loaded and
CPU-skinned at runtime via `SkinnedModel` (SharpGLTF), exactly like the dragon. ~3.6 MB (single base-colour
atlas texture) — a normal repo blob (Resources/Raw is not Git LFS; LFS is scoped to `data/**`).

- **License:** CC0 1.0 Universal (public domain) — no attribution required; credited here as courtesy.
- **Source / author:** Kay Lousberg — <https://kaylousberg.itch.io/kaykit-adventurers>
  (mirror: <https://github.com/KayKit-Game-Assets/KayKit-Character-Pack-Adventures-1.0>, CC0 `LICENSE.txt`).

### `src/MapaTur.App/Resources/Raw/arrow.glb` — crossbow bolt (walk-mode F)

The KayKit "Adventurers" `arrow` prop — a static, unskinned mesh with the shared gradient atlas texture —
repacked from the pack's `arrow.gltf` + `arrow.bin` into a self-contained GLB. Fired as a ballistic projectile
when the walk-mode avatar shoots (F), and drawn through the same CPU-skinned GL path as the character.

- **License:** CC0 1.0 Universal (public domain) — no attribution required; credited here as courtesy.
- **Source / author:** Kay Lousberg — <https://kaylousberg.itch.io/kaykit-adventurers>.

## Sound effects

### `src/MapaTur.App/Resources/Raw/dragon-audio/*.mp3` — dragon voice + wings (F7 flight, desktop)

Five recorded effects layered over the procedural audio (which remains the fallback when an asset is
missing): `roar-epic.mp3` (flight entry / kill cry), `growl-long.mp3` + `growl-short.mp3` (soaring calls),
`fire-breath.mp3` (held-F fire loop), `wings-flapping.mp3` (wing-flutter bed).

- **License:** Pixabay Content License — free for commercial use, no attribution required, don't resell as-is.
- **Source / author:** Pixabay sound effects, uploader "Dragon Studio" (original file names
  `dragon-studio-*-{364475,364481,364483,364612,478385}.mp3`).

### `tests/MapaTur.Application.Tests/TestData/Fox.glb` — skinning-engine test model (tests only)

The Khronos "Fox" glTF sample asset, used only by the `SkinnedModel` unit tests (never shipped in the app).

- Model: PixelMannen (CC0). Rig + animation: [@tomkranis] (CC-BY 4.0).
- Source: <https://github.com/KhronosGroup/glTF-Sample-Assets> (Models/Fox).
