using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Application.Terrain;

/// <summary>Flat-shaded triangle soup of the sculpted rock skin, interleaved for the renderer:
/// 9 floats per vertex (pos3 + faceNormal3 + tint3), positions in climb space (real metres).</summary>
public sealed record ClimbRockSkin(float[] Interleaved, int VertexCount);

/// <summary>
/// Drapes ONE continuous sculpted surface over the steep wall (plan F2a): a world-aligned lattice of the
/// base terrain, displaced along the surface normal by <see cref="ClimbRockReliefField"/> — cracks, ledge
/// breaks, granite facets — with every climb hold BLENDED INTO the same surface: near a hold the skin
/// morphs to that hold's exact protrusion (the solver's ContactOffset), so a jug is a knob OF the rock, a
/// foot edge is a notch IN it, and hands land on sculpture that is part of the mountain — not on a pile
/// of separate blobs (the user's verdict on v1). The skin is outward-only (lift + relief, never below the
/// base surface), so the opaque terrain never occludes it and the untouched climb physics never puts the
/// body inside it. Slope-gated: flat ground gets no skin at all. Deterministic in world position — a
/// regrown window reproduces the shared region verbatim (the lattice sits on absolute step multiples).
/// </summary>
public static class ClimbRockSkinMesh
{
    /// <summary>Minimum lift over the base surface — keeps the skin in front of the terrain depth-wise.</summary>
    public const float BaseLiftMeters = 0.02f;

    /// <summary>Sculpt band above the lift: crack floors sit at the lift, ridge faces this much proud.
    /// 0.20 → 0.40 after the user verdict "wciąż za słaba rzeźba"; still under the 0.5 m pelvis offset the
    /// untouched physics keeps off the base wall (holds locally suppress the skin, so limbs stay clean).</summary>
    public const float ReliefAmplitudeMeters = 0.40f;

    /// <summary>Cells whose every corner displaces less than this emit no triangles (skin fades out).</summary>
    public const float MinVisibleDisplacementMeters = 0.015f;

    /// <summary>
    /// The displacement law — the skin's single source of truth, exposed for tests: how far off the BASE
    /// surface (along its normal) the sculpted skin sits at <paramref name="basePoint"/>. Slope-gated
    /// relief plus the hold blend; at a hold's exact position this returns the hold's protrusion.
    /// </summary>
    public static float SurfaceDisplacementMeters(
        Vector3 basePoint, float slopeGrade, IReadOnlyList<ClimbHold> nearbyHolds, Vector3 gravity)
    {
        float fade = SmoothStep(0.60f, 0.90f, slopeGrade);
        float field = fade * (BaseLiftMeters + (ReliefAmplitudeMeters * ClimbRockReliefField.Relief01(basePoint)));

        // Hold blend: the strongest nearby hold pulls the surface toward ITS protrusion (cubic falloff).
        // A jug rises above the field, a 4 cm foot edge dips below it — both read as features of one rock.
        float bestWeight = 0f;
        float bestProtrusion = 0f;
        foreach (ClimbHold hold in nearbyHolds)
        {
            float radius = MathF.Max(0.30f, hold.UsableWidthMeters * 0.9f);
            float distance = Vector3.Distance(basePoint, hold.Position);
            if (distance >= radius)
            {
                continue;
            }

            float u = 1f - (distance / radius);
            float weight = u * u * (3f - (2f * u));
            if (weight > bestWeight)
            {
                bestWeight = weight;
                bestProtrusion = ClimbHoldImprintMesh.ProtrusionMeters(hold);
            }
        }

        return field + ((bestProtrusion - field) * bestWeight);
    }

    /// <summary>
    /// Builds the skin for a square window (side 2·<paramref name="halfSpanMeters"/>) around
    /// <paramref name="centerXY"/>. The lattice is aligned to absolute multiples of
    /// <paramref name="stepMeters"/>, so windows built at different centres agree exactly where they overlap.
    /// </summary>
    public static ClimbRockSkin Build(
        Vector2 centerXY,
        float halfSpanMeters,
        float stepMeters,
        Func<Vector2, float?> sampleGround,
        IReadOnlyList<ClimbHold> holds,
        Vector3 gravity)
    {
        ArgumentNullException.ThrowIfNull(sampleGround);
        ArgumentNullException.ThrowIfNull(holds);

        float minX = MathF.Floor((centerXY.X - halfSpanMeters) / stepMeters) * stepMeters;
        float minY = MathF.Floor((centerXY.Y - halfSpanMeters) / stepMeters) * stepMeters;
        int cells = Math.Max(1, (int)MathF.Ceiling(2f * halfSpanMeters / stepMeters));
        int gridSize = cells + 1;

        // Base elevations with TWO extra rings: ring 1 lets every visible vertex take central-difference
        // SMOOTH normals over the displaced surface, ring 2 feeds the base normals of ring-1 vertices.
        var elevation = new float[gridSize + 4, gridSize + 4];
        for (int j = -2; j <= gridSize + 1; j++)
        {
            for (int i = -2; i <= gridSize + 1; i++)
            {
                var xy = new Vector2(minX + (i * stepMeters), minY + (j * stepMeters));
                elevation[i + 2, j + 2] = sampleGround(xy) ?? float.NaN;
            }
        }

        // 1 m XY buckets of the holds — the influence radius is well under a metre, so 3×3 cells suffice.
        Dictionary<(int X, int Y), List<ClimbHold>> holdGrid = [];
        foreach (ClimbHold hold in holds)
        {
            (int X, int Y) cell = ((int)MathF.Floor(hold.Position.X), (int)MathF.Floor(hold.Position.Y));
            if (!holdGrid.TryGetValue(cell, out List<ClimbHold>? bucket))
            {
                bucket = [];
                holdGrid[cell] = bucket;
            }

            bucket.Add(hold);
        }

        // Displaced surface over the grid PLUS one ring, so smooth normals get central differences
        // everywhere visible. Logical index i,j ∈ [-1, gridSize] → array [i+1, j+1].
        var displaced = new Vector3[gridSize + 2, gridSize + 2];
        var displacement = new float[gridSize + 2, gridSize + 2];
        var baseNormal = new Vector3[gridSize + 2, gridSize + 2];
        List<ClimbHold> nearby = [];
        for (int j = -1; j <= gridSize; j++)
        {
            for (int i = -1; i <= gridSize; i++)
            {
                float z = elevation[i + 2, j + 2];
                float east = elevation[i + 3, j + 2], west = elevation[i + 1, j + 2];
                float north = elevation[i + 2, j + 3], south = elevation[i + 2, j + 1];
                if (float.IsNaN(z) || float.IsNaN(east) || float.IsNaN(west) || float.IsNaN(north) || float.IsNaN(south))
                {
                    displacement[i + 1, j + 1] = float.NaN;
                    continue;
                }

                var basePoint = new Vector3(minX + (i * stepMeters), minY + (j * stepMeters), z);
                float dzdx = (east - west) / (2f * stepMeters);
                float dzdy = (north - south) / (2f * stepMeters);
                var normal = Vector3.Normalize(new Vector3(-dzdx, -dzdy, 1f));
                float grade = MathF.Sqrt((dzdx * dzdx) + (dzdy * dzdy));

                nearby.Clear();
                (int X, int Y) cell = ((int)MathF.Floor(basePoint.X), (int)MathF.Floor(basePoint.Y));
                for (int cy = cell.Y - 1; cy <= cell.Y + 1; cy++)
                {
                    for (int cx = cell.X - 1; cx <= cell.X + 1; cx++)
                    {
                        if (holdGrid.TryGetValue((cx, cy), out List<ClimbHold>? bucket))
                        {
                            nearby.AddRange(bucket);
                        }
                    }
                }

                float d = SurfaceDisplacementMeters(basePoint, grade, nearby, gravity);
                displacement[i + 1, j + 1] = d;
                displaced[i + 1, j + 1] = basePoint + (normal * d);
                baseNormal[i + 1, j + 1] = normal;
            }
        }

        // SMOOTH per-vertex normals over the DISPLACED surface ("strasznie pixelowane" verdict on flat
        // shading: a facet normal per 0.15 m triangle made the whole wall glitter at grid frequency).
        // The sculpture itself — canyons, blocks — carries the structure; shading interpolates.
        var vertexNormal = new Vector3[gridSize, gridSize];
        for (int j = 0; j < gridSize; j++)
        {
            for (int i = 0; i < gridSize; i++)
            {
                if (float.IsNaN(displacement[i + 1, j + 1]))
                {
                    continue;
                }

                if (float.IsNaN(displacement[i + 2, j + 1]) || float.IsNaN(displacement[i, j + 1])
                    || float.IsNaN(displacement[i + 1, j + 2]) || float.IsNaN(displacement[i + 1, j]))
                {
                    vertexNormal[i, j] = baseNormal[i + 1, j + 1]; // window edge / off-coverage neighbour
                    continue;
                }

                Vector3 dx = displaced[i + 2, j + 1] - displaced[i, j + 1];
                Vector3 dy = displaced[i + 1, j + 2] - displaced[i + 1, j];
                Vector3 n = Vector3.Cross(dx, dy);
                vertexNormal[i, j] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : baseNormal[i + 1, j + 1];
            }
        }

        // Emit smooth-shaded triangles for every cell that is at least partly visible.
        var interleaved = new List<float>(cells * cells * 54); // 2 tris × 3 verts × 9 floats per cell
        for (int j = 0; j < cells; j++)
        {
            for (int i = 0; i < cells; i++)
            {
                float d00 = displacement[i + 1, j + 1], d10 = displacement[i + 2, j + 1];
                float d01 = displacement[i + 1, j + 2], d11 = displacement[i + 2, j + 2];
                if (float.IsNaN(d00) || float.IsNaN(d10) || float.IsNaN(d01) || float.IsNaN(d11))
                {
                    continue;
                }

                if (d00 < MinVisibleDisplacementMeters && d10 < MinVisibleDisplacementMeters
                    && d01 < MinVisibleDisplacementMeters && d11 < MinVisibleDisplacementMeters)
                {
                    continue;
                }

                EmitVertex(interleaved, displaced[i + 1, j + 1], vertexNormal[i, j], d00);
                EmitVertex(interleaved, displaced[i + 2, j + 1], vertexNormal[i + 1, j], d10);
                EmitVertex(interleaved, displaced[i + 1, j + 2], vertexNormal[i, j + 1], d01);
                EmitVertex(interleaved, displaced[i + 2, j + 1], vertexNormal[i + 1, j], d10);
                EmitVertex(interleaved, displaced[i + 2, j + 2], vertexNormal[i + 1, j + 1], d11);
                EmitVertex(interleaved, displaced[i + 1, j + 2], vertexNormal[i, j + 1], d01);
            }
        }

        return new ClimbRockSkin([.. interleaved], interleaved.Count / 9);
    }

    private static void EmitVertex(List<float> interleaved, Vector3 position, Vector3 normal, float displacementMeters)
    {
        // Granite tint from the sculpt itself: canyon floors and foot notches darken like ambient
        // occlusion, proud block faces lighten. One hue — never per-hold colours.
        float shade = 0.55f + (0.75f * Math.Clamp((displacementMeters - BaseLiftMeters) / ReliefAmplitudeMeters, 0f, 1f));
        interleaved.Add(position.X);
        interleaved.Add(position.Y);
        interleaved.Add(position.Z);
        interleaved.Add(normal.X);
        interleaved.Add(normal.Y);
        interleaved.Add(normal.Z);
        interleaved.Add(0.50f * shade);
        interleaved.Add(0.485f * shade);
        interleaved.Add(0.46f * shade);
    }

    private static float SmoothStep(float lo, float hi, float v)
    {
        float t = Math.Clamp((v - lo) / (hi - lo), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}