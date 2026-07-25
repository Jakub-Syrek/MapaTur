using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Faza B of the sub-1m plan (docs/PLAN-sub-1m-geometry.md): synthesises VIRTUAL z18/z19 tiles from the real
/// baked z17 pyramid — a Catmull-Rom upsample of the z17 ancestor plus a deterministic sub-grid displacement,
/// so the walk-mode ground and near-field dragon flights get geometry below the 0.78 m data floor without any
/// new data. Two hard contracts, both inherited from fix B and the granite lessons:
///
/// <list type="bullet">
/// <item><b>Real amplitude, procedural pattern.</b> The displacement amplitude is MEASURED — the ancestor's
/// per-cell curvature (|z − mean(4-neighbours)|, capped) — so a lake or groomed slope synthesises exactly
/// zero bumps while rock gets up to the cap; only the PATTERN is procedural.</item>
/// <item><b>Fixed absolute lattice.</b> The pattern is value noise whose lattice is the GLOBAL node grid of a
/// fixed zoom (z17 nodes for the coarse octave, z18 nodes for the fine octave), hashed with integer mixing —
/// deterministic (same key ⇒ bit-identical tile: the walk ground IS the rendered ground), continuous across
/// every tile boundary, immune to the GLSL-sin() precision class, and never the variable-cell-size mistake
/// that striped granite v4–v7.</item>
/// </list>
///
/// Seam safety by construction: the parent is rastered inside a 2-cell NEIGHBOUR HALO
/// (<see cref="BakedTileMeshBuilder.AsRasterWithHalo"/>), sampling positions are EXACT dyadic grid fractions
/// (<see cref="DemRasterResampler.SampleCatmullRomAt"/> — a shared-edge node evaluates at t=0 and reproduces
/// the welded ancestor value bit-for-bit), the noise lattice is global, and the curvature at a border cell is
/// computed from the same welded values by both parents — adjacent children join bit-exactly, within one
/// parent and across welded parents. The halo also removes the old per-parent artefacts: Catmull-Rom taps no
/// longer clamp at the parent edge, and the border curvature is REAL instead of a defined-0 edge row (which
/// used to silence the displacement in a ~1-parent-cell band along every z17 border — a smooth strip
/// repeating on the ~200 m grid). At the pyramid rim (no neighbour) the halo falls back to an edge-slope
/// reflection: deterministic, and there is no adjacent child there to disagree with.
///
/// The synthesised tile carries an ALL-ZERO <see cref="BakedDemTile.DetailRms"/>: the displaced geometry now
/// carries the micro-relief, so the shader's NativeMicroDetail bump must not fire on top (anti-double-bump).
/// Tiles are never written to disk — they are synthesised on demand (and RAM-cached by the same
/// <see cref="BakedDemTileCache"/> that fronts real tiles).
/// </summary>
public static class VirtualDemTileSynthesizer
{
    /// <summary>Max |displacement| (metres) of the coarse octave (z17-node lattice, ~0.8–1.6 m features) —
    /// applied to both z18 and z19 tiles.</summary>
    public const float CoarseOctaveCapMeters = 0.35f;

    /// <summary>Max |displacement| (metres) of the fine octave (z18-node lattice, ~0.4–0.8 m features) —
    /// applied to z19 tiles on top of the coarse octave.</summary>
    public const float FineOctaveCapMeters = 0.15f;

    // Curvature → amplitude gains (amplitude = min(curvature × gain, cap)). The measured z17 curvature on
    // Tatra rock is ~0.1–0.8 m, so rock rides at/near the caps while meadows (~0.02 m) stay subtle.
    private const float CoarseOctaveGain = 0.9f;
    private const float FineOctaveGain = 0.5f;

    // Distinct hash salts per octave so the two lattices are uncorrelated.
    private const ulong CoarseSalt = 0x9E3779B97F4A7C15UL;
    private const ulong FineSalt = 0xC2B2AE3D27D4EB4FUL;

    // Halo width (in parent cells) the parent raster carries from its 8 neighbours: Catmull-Rom reaches 2
    // taps past a node, and the curvature stencil reads ±1 around the bilinear lookup's +1 — both exactly 2.
    private const int ParentHaloCells = 2;

    // Noise lattices are ROTATED against the tile grid and scaled by near-irrational factors. Value noise
    // sampled on its own axis-aligned lattice alternates full-variance corners with averaged midpoints —
    // a perfectly regular 2-node-pitch grain (user: "równomierne granulowania", 2026-07-10 night). A constant
    // rotation desynchronises the lattice from the sampling grid (no exact periodicity survives), while
    // staying a pure function of the GLOBAL node coordinates — determinism and the bit-exact seams are
    // untouched, and the lattice CELL SIZE stays constant (the granite v4–v7 variable-cell stripe lesson).
    private const double CoarseAngleRadians = 0.5847;
    private const double CoarseScale = 0.9473;
    private const double FineAngleRadians = -0.9273;
    private const double FineScale = 1.0317;

    /// <summary>
    /// Synthesises the virtual tile for <paramref name="key"/> (zoom must be 1 or 2 levels below
    /// <paramref name="realMaxZoom"/>) from its real ancestor via <paramref name="loadTile"/>. Null when the
    /// zoom is out of range or the ancestor is not available — the caller treats it exactly like a missing
    /// baked tile (the quadtree then stays on the coarser level).
    /// </summary>
    /// <param name="key">The virtual tile's slippy address (z18 or z19).</param>
    /// <param name="realMaxZoom">The finest REAL baked zoom (17).</param>
    /// <param name="loadTile">Loads a real baked tile by key (the RAM-cached loader in production).</param>
    /// <exception cref="ArgumentNullException"><paramref name="loadTile"/> is null.</exception>
    public static BakedDemTile? Synthesize(
        DemTileKey key, int realMaxZoom, Func<DemTileKey, BakedDemTile?> loadTile)
    {
        ArgumentNullException.ThrowIfNull(loadTile);

        int shift = key.Zoom - realMaxZoom;
        if (shift is < 1 or > 2)
        {
            return null;
        }

        BakedDemTile? ancestor = loadTile(new DemTileKey(realMaxZoom, key.X >> shift, key.Y >> shift));
        if (ancestor is null)
        {
            return null;
        }

        // The parent inside a halo of its neighbours' real cells: the Catmull-Rom taps and the curvature
        // stencil then have genuine ground beyond the parent border instead of a clamp/defined-0 edge. 2 cells
        // covers both consumers exactly (CR reaches 2 taps past a node; the curvature stencil ±1 around the
        // bilinear lookup's +1). Same LRU-backed loader as the ancestor itself — 8 RAM-hit lookups.
        DemRaster parent = BakedTileMeshBuilder.AsRasterWithHalo(ancestor, loadTile, ParentHaloCells);
        float[] curvature = CurvatureGrid(parent);

        int cols = ancestor.Columns;
        int rows = ancestor.Rows;
        (double west, double south, double east, double north) = SlippyTileMath.TileBounds(key.X, key.Y, key.Zoom);
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        float noData = (float)ancestor.NoDataValue;
        var heights = new float[cols * rows];

        // Global node coordinates: node c of tile x at zoom z sits at global index x·(N−1)+c — adjacent
        // tiles SHARE their boundary node index, so everything keyed on these coordinates is seam-free.
        // In ancestor units a child node advances by 1/2^shift of an ancestor cell.
        double step = 1.0 / (1 << shift);
        long globalCol0 = (long)key.X * (cols - 1);
        long globalRow0 = (long)key.Y * (rows - 1);

        for (int r = 0; r < rows; r++)
        {
            // The child node's position on the ancestor grid (ancestor-cell units, node registration) — an
            // EXACT dyadic fraction, so sampling by GRID position (not geo) keeps a node-coincident child at
            // t=0 exactly: the welded ancestor value comes back bit-for-bit and cross-parent seams stay
            // bit-exact by construction. +ParentHaloCells shifts into the haloed raster's frame.
            double parentRow = ((key.Y - ((long)ancestor.TileY << shift)) * (rows - 1) + r) * step;
            for (int c = 0; c < cols; c++)
            {
                double parentCol = ((key.X - ((long)ancestor.TileX << shift)) * (cols - 1) + c) * step;
                double smooth = DemRasterResampler.SampleCatmullRomAt(
                    parent, ParentHaloCells + parentCol, ParentHaloCells + parentRow);
                int i = (r * cols) + c;
                if (smooth == noData)
                {
                    heights[i] = noData; // a parent void stays a hole — never fabricate ground
                    continue;
                }

                double curv = Bilinear(
                    curvature, parent.Columns, parent.Rows,
                    ParentHaloCells + parentCol, ParentHaloCells + parentRow);

                // Global child-node index, expressed in each octave lattice's own units and ROTATED off the
                // grid (see the lattice constants above) so the pattern never synchronises with the mesh.
                long gc = globalCol0 + c;
                long gr = globalRow0 + r;
                double cx = gc / (double)(1L << shift);
                double cy = gr / (double)(1L << shift);
                double coarseAmp = Math.Min(curv * CoarseOctaveGain, CoarseOctaveCapMeters);
                double displacement = coarseAmp * ValueNoise(
                    Rotate(cx, cy, CoarseAngleRadians, CoarseScale, out double cyR), cyR, CoarseSalt);
                if (shift == 2)
                {
                    double fx = gc / 2.0;
                    double fy = gr / 2.0;
                    double fineAmp = Math.Min(curv * FineOctaveGain, FineOctaveCapMeters);
                    displacement += fineAmp * ValueNoise(
                        Rotate(fx, fy, FineAngleRadians, FineScale, out double fyR), fyR, FineSalt);
                }

                heights[i] = (float)(smooth + displacement);
            }
        }

        // DetailRms all-zero (NOT null): rides the existing mesh plumbing as an explicit "no shader
        // micro-bump" grid — null would fall through to NativeMicroDetail and double-bump the displacement.
        return new BakedDemTile(
            key.Zoom, key.X, key.Y, cols, rows, bounds, ancestor.NoDataValue, heights,
            detailRms: new float[cols * rows]);
    }

    // Per-cell curvature |z − mean(valid 4-neighbours)| in metres — the MEASURED displacement amplitude
    // source, computed over the HALOED parent raster: a cell on the parent BORDER has real neighbours on both
    // sides, so its curvature is genuine and — because both adjacent parents read the same welded values into
    // the same stencil slots — identical across the seam. Only the halo's outermost ring reads 0 (short of a
    // stencil), and that ring sits ParentHaloCells beyond the border, out of reach of every lookup.
    private static float[] CurvatureGrid(DemRaster parent)
    {
        int cols = parent.Columns;
        int rows = parent.Rows;
        float noData = parent.NoDataValue;
        var curvature = new float[cols * rows];
        for (int r = 1; r < rows - 1; r++)
        {
            for (int c = 1; c < cols - 1; c++)
            {
                float z = parent[c, r];
                float w = parent[c - 1, r];
                float e = parent[c + 1, r];
                float n = parent[c, r - 1];
                float s = parent[c, r + 1];
                if (z == noData || w == noData || e == noData || n == noData || s == noData)
                {
                    continue;
                }

                curvature[(r * cols) + c] = Math.Abs(z - ((w + e + n + s) / 4f));
            }
        }

        return curvature;
    }

    private static double Bilinear(float[] grid, int cols, int rows, double col, double row)
    {
        col = Math.Clamp(col, 0.0, cols - 1.0);
        row = Math.Clamp(row, 0.0, rows - 1.0);
        int c0 = Math.Min((int)col, cols - 2);
        int r0 = Math.Min((int)row, rows - 2);
        double tc = col - c0;
        double tr = row - r0;
        double v00 = grid[(r0 * cols) + c0];
        double v01 = grid[(r0 * cols) + c0 + 1];
        double v10 = grid[((r0 + 1) * cols) + c0];
        double v11 = grid[((r0 + 1) * cols) + c0 + 1];
        return (v00 * (1 - tc) * (1 - tr)) + (v01 * tc * (1 - tr)) + (v10 * (1 - tc) * tr) + (v11 * tc * tr);
    }

    // Rotates and scales a lattice coordinate — a CONSTANT affine map, identical for every tile (a pure
    // function of the global coordinate ⇒ seams stay bit-exact). Returns x'; y' via out.
    private static double Rotate(double x, double y, double angleRadians, double scale, out double yRotated)
    {
        double cos = Math.Cos(angleRadians);
        double sin = Math.Sin(angleRadians);
        yRotated = ((x * sin) + (y * cos)) * scale;
        return ((x * cos) - (y * sin)) * scale;
    }

    // Value noise in [−1, 1] on the integer lattice: corner hashes blended bilinearly. Smooth (C0) with
    // feature size = one lattice cell; the lattice coordinate is a GLOBAL node index, so the field is
    // continuous across every tile and every zoom that shares the lattice.
    private static double ValueNoise(double x, double y, ulong salt)
    {
        long x0 = (long)Math.Floor(x);
        long y0 = (long)Math.Floor(y);
        double tx = x - x0;
        double ty = y - y0;
        double v00 = Hash(x0, y0, salt);
        double v01 = Hash(x0 + 1, y0, salt);
        double v10 = Hash(x0, y0 + 1, salt);
        double v11 = Hash(x0 + 1, y0 + 1, salt);
        return (v00 * (1 - tx) * (1 - ty)) + (v01 * tx * (1 - ty)) + (v10 * (1 - tx) * ty) + (v11 * tx * ty);
    }

    // SplitMix64-style integer mix → [−1, 1]. Pure integer math: deterministic on every runtime, no
    // floating-point precision cliffs (the GLSL hashT/sin() lesson does not apply here by construction).
    private static double Hash(long x, long y, ulong salt)
    {
        ulong h = ((ulong)x * 0xBF58476D1CE4E5B9UL) ^ ((ulong)y * 0x94D049BB133111EBUL) ^ salt;
        h ^= h >> 30;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27;
        h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;
        return ((h >> 11) * (1.0 / (1UL << 53)) * 2.0) - 1.0;
    }
}