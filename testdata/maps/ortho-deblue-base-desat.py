"""De-blue an ORTHO BASE mosaic on disk with the SAME law the shader uses on detail layers.

WHY THIS EXISTS (2026-08-28, decyzja usera). The base mosaic is the one ortho layer with NO shader path:
`deblueShadow()` in Terrain3DGlRenderer is called only on det25/det1m/det05 colour — on the base it appears
solely inside `deblueShadow(baseC)` when computing the tone delta, and that result never reaches the output.
So the HARD RULE "orto bez wypalonych cieni" (TILE-PRODUCTION, w. 432) can only be satisfied for the base by
correcting it ON DISK, before the user ever sees it.

The older §3.13 script (`ortho-deblue-shadow.py`) is NOT the law to use for new bases:
  - it applies `G += 0.35*ex`, which the r1-c3 rollback identified as the GREEN-PAINT bug (it produces green
    instead of removing the cast) — see the shader comment at Terrain3DGlRenderer.cs deblueShadow();
  - it is hardwired to the Tatra 4x2 grid and to `*.pre-colorfix.bak`;
  - it does `convert("RGB")`, which would DESTROY the alpha NoData mask (Zermatt's Italian flank is a=0) and
    walk straight into the known black-triangles-at-coverage-edge bug.
TILE-PRODUCTION w. 441 reserved the switch for the user ("przy nastepnym re-bake bazy rozwazyc przejscie na
desaturacje - decyzja usera"); asked and granted 2026-08-28 for the Zermatt base.

THE LAW — a line-by-line port of `deblueShadow()` from the renderer, so base and detail agree in shadow:
    ex   = max(0, B - max(R, G))                  # blue excess = the sky-lit shadow cast
    lum  = 0.299R + 0.587G + 0.114B
    lift = smoothstep(0.05, 0.16, lum)            # crushed black untouched (no chroma-noise amplification)
    B   -= 0.85 * ex * lift                       # pull blue down to the R/G level; NEVER add green
    sw   = smoothstep(0.005, 0.06, ex)
    grey = mean(R, G, B)                          # NOTE: computed AFTER B was lowered, exactly as in GLSL
    rgb  = mix(rgb, grey, 0.35 * sw * lift)       # mild desaturation toward neutral, in shadow only
Everything runs in 0..1 float, per pixel, identical everywhere => seam-safe. Lit ground and genuinely
dark-green forest have ex~0 and come out bit-identical.

ALPHA IS CARRIED THROUGH UNTOUCHED. Reversible: the untouched original is kept as `<file>.pre-deblue.bak`
and is always used as the source, so re-running is idempotent and never stacks corrections.

HOW MANY PASSES — measured on the Zermatt base 2026-08-28, worst tile r1-c0 (raw mean 11.94/255):
    pass 1 -> 2.31   pass 2 -> 1.03   pass 3 -> 0.68     (audit gate: mean < 1.0)
One pass leaves the ~15% of the cast that KB=0.85 deliberately keeps, so it FAILS the gate on the
worst tiles; 3 is the first setting where every tile clears it with margin. The cast never reaches
the Tatra base's 0.10 because the luma gate protects crushed black on purpose — that floor is by
design, not a miss. Cost of the extra passes is small and was measured too: shadow saturation
0.299 -> 0.183 after pass 1 (that IS the cast removal) and only -> 0.153 after pass 3, lit ground
0.067 -> 0.062, shadow luma 63.2 -> 62.7 (nothing washes out). Run the Zermatt base with --passes 3.

BOTH COPIES. The base lives twice: the generator masters in `<repo>/dem/` (gitignored) and the seeded
copy in AppData. Correct BOTH, or the next re-seed silently reinstates the raw cast.

Usage:
  python testdata/maps/ortho-deblue-base-desat.py --dir <dem-dir> --pattern "zermatt-ortho-*.png" --passes 3
  python testdata/maps/ortho-deblue-base-desat.py --dir <dem-dir> --pattern "..." --restore
"""

import argparse
import glob
import os
import shutil
import sys

import numpy as np

try:
    from PIL import Image
except ImportError:
    print("Pillow required: pip install Pillow", file=sys.stderr)
    sys.exit(2)

Image.MAX_IMAGE_PIXELS = None

BACKUP_SUFFIX = ".pre-deblue.bak"
STRIP = 1024  # rows per chunk: 8192-wide RGBA float32 strip is ~130 MB, so RAM stays flat


def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def deblue_desat(rgb01):
    """Port of deblueShadow() — input/output float32 RGB in 0..1."""
    r, g, b = rgb01[..., 0], rgb01[..., 1], rgb01[..., 2]
    ex = np.maximum(0.0, b - np.maximum(r, g))
    lum = (0.299 * r) + (0.587 * g) + (0.114 * b)
    lift = smoothstep(0.05, 0.16, lum)

    out = rgb01.copy()
    out[..., 2] = np.clip(b - (0.85 * ex * lift), 0.0, 1.0)

    sw = smoothstep(0.005, 0.06, ex)
    grey = out.mean(axis=-1)  # AFTER the blue pull, as in the shader
    w = (0.35 * sw * lift)[..., None]
    return np.clip((out * (1.0 - w)) + (grey[..., None] * w), 0.0, 1.0)


def cast_stats(rgb_u8, alpha_u8):
    """mean/p95 blue-excess over COVERED shadow pixels — the audit-ortho-blue-cast statistic."""
    cov = alpha_u8 > 8 if alpha_u8 is not None else np.ones(rgb_u8.shape[:2], dtype=bool)
    px = rgb_u8[cov].astype(np.float32)
    if px.size == 0:
        return 0.0, 0.0, 0.0
    lum = px @ np.array([0.299, 0.587, 0.114], dtype=np.float32)
    dark = lum < 110.0
    if not dark.any():
        return 0.0, 0.0, 0.0
    ex = np.clip(px[dark, 2] - np.maximum(px[dark, 0], px[dark, 1]), 0.0, None)
    return float(ex.mean()), float(np.percentile(ex, 95)), float(dark.mean())


def deblue_passes(rgb01, passes):
    """The SAME law applied N times. Pass 2+ is an exact identity wherever ex~0 (lit ground, forest,
    crushed black), so it only keeps eating the blue excess that KB=0.85 deliberately left behind.
    Needed because a single pass leaves ~15% of the cast, and the base — having no shader path — must
    itself satisfy the audit gate (mean < 1.0/255), not merely be 'as corrected as the detail layers'."""
    for _ in range(passes):
        rgb01 = deblue_desat(rgb01)
    return rgb01


def process(path, sample_step, passes=1):
    backup = path + BACKUP_SUFFIX
    if not os.path.exists(backup):
        shutil.copy2(path, backup)
    src = Image.open(backup)
    had_alpha = src.mode in ("RGBA", "LA") or "transparency" in src.info
    src = src.convert("RGBA" if had_alpha else "RGB")
    w, h = src.size
    arr = np.asarray(src)
    out = np.empty_like(arr)

    for y in range(0, h, STRIP):
        y1 = min(h, y + STRIP)
        chunk = arr[y:y1, :, :3].astype(np.float32) / 255.0
        out[y:y1, :, :3] = np.clip(deblue_passes(chunk, passes) * 255.0 + 0.5, 0, 255).astype(np.uint8)
        if had_alpha:
            out[y:y1, :, 3] = arr[y:y1, :, 3]  # alpha carried through untouched

    s = sample_step
    a_before = arr[::s, ::s, 3] if had_alpha else None
    before = cast_stats(arr[::s, ::s, :3], a_before)
    after = cast_stats(out[::s, ::s, :3], a_before)
    Image.fromarray(out).save(path)
    return before, after, (w, h), had_alpha


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--pattern", required=True)
    ap.add_argument("--sample-step", type=int, default=16, help="pixel stride for the before/after statistic")
    ap.add_argument("--passes", type=int, default=1, help="how many times to apply the law (see deblue_passes)")
    ap.add_argument("--restore", action="store_true", help="put the .pre-deblue.bak originals back and exit")
    args = ap.parse_args()

    files = sorted(glob.glob(os.path.join(args.dir, args.pattern)))
    files = [f for f in files if not f.endswith(BACKUP_SUFFIX)]
    if not files:
        print(f"no files matching {args.pattern} under {args.dir}")
        sys.exit(1)

    if args.restore:
        for f in files:
            b = f + BACKUP_SUFFIX
            if os.path.exists(b):
                shutil.copy2(b, f)
                print(f"  restored {os.path.basename(f)}")
        return

    print(f"przebiegow prawa: {args.passes}")
    print(f"{'plik':32s} {'alpha':>5s} {'ex_mean':>17s} {'ex_p95':>15s} {'ciemne':>7s}")
    tot_b = tot_a = 0.0
    for f in files:
        before, after, size, had_alpha = process(f, args.sample_step, args.passes)
        tot_b += before[0]
        tot_a += after[0]
        print(f"{os.path.basename(f):32s} {'tak' if had_alpha else 'nie':>5s} "
              f"{before[0]:7.2f} -> {after[0]:6.2f} {before[1]:6.1f} -> {after[1]:5.1f} {before[2]*100:6.1f}%")
    n = len(files)
    print(f"\n  SREDNIA ex: {tot_b / n:.2f} -> {tot_a / n:.2f} /255   (baza musi czytac ~0, prog audytu < 1.0)")
    print(f"  cofniecie: --restore  (originaly leza obok jako *{BACKUP_SUFFIX})")


if __name__ == "__main__":
    main()
