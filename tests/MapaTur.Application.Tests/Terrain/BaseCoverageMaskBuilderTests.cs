using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="BaseCoverageMaskBuilder"/> — the SURFACE-OWNERSHIP component: it turns
/// the set of hole-free resident z16 tiles (world AABBs) into a conservative world-space bitmap the terrain
/// shader uses to DISCARD base-skin fragments. This is the fix for the recurring "airport" burial: the
/// box-averaged base sits 0.5–4 m ABOVE the true z16 surface on convex slopes and, always drawn, wins the
/// depth test over the streamed detail. Conservative = a texel is covered only when full detail truly
/// surrounds it (one-texel erosion), so a discarded base fragment can never expose a hole.
/// </summary>
public sealed class BaseCoverageMaskBuilderTests
{
    private const float Texel = BaseCoverageMaskBuilder.TexelSizeMeters;

    private static (Vector2 Min, Vector2 Max) Rect(float minX, float minY, float maxX, float maxY)
        => (new Vector2(minX, minY), new Vector2(maxX, maxY));

    [Fact]
    public void EmptyInput_YieldsNoMask()
    {
        BaseCoverageMask? mask = BaseCoverageMaskBuilder.Build(Array.Empty<(Vector2, Vector2)>());

        mask.Should().BeNull("no full-detail tiles ⇒ the base owns everything");
    }

    [Fact]
    public void SingleTile_CoversItsInterior_ButNotItsEdges()
    {
        // One 400×400 m tile: the centre is covered; the outer texel ring is eroded away (conservative), and
        // anything outside is never covered.
        BaseCoverageMask? mask = BaseCoverageMaskBuilder.Build(new[] { Rect(0f, 0f, 400f, 400f) });

        mask.Should().NotBeNull();
        mask!.CoveredAt(200f, 200f).Should().BeTrue("the tile interior is fully surrounded by detail");
        mask.CoveredAt(Texel * 0.5f, 200f).Should().BeFalse("the outer texel ring is eroded (conservative)");
        mask.CoveredAt(-50f, 200f).Should().BeFalse("outside the tile the base must stay");
        mask.CoveredAt(200f, 500f).Should().BeFalse("outside the tile the base must stay");
    }

    [Fact]
    public void AdjacentTiles_HaveNoGapAtTheSharedBorder()
    {
        // Two tiles sharing the x=400 border: the border area is interior of the UNION, so it stays covered —
        // erosion must only trim the union's outer boundary, never carve seams between neighbouring tiles.
        BaseCoverageMask? mask = BaseCoverageMaskBuilder.Build(new[]
        {
            Rect(0f, 0f, 400f, 400f),
            Rect(400f, 0f, 800f, 400f),
        });

        mask.Should().NotBeNull();
        mask!.CoveredAt(400f, 200f).Should().BeTrue("the shared border is union-interior — no seam");
        mask.CoveredAt(390f, 200f).Should().BeTrue();
        mask.CoveredAt(410f, 200f).Should().BeTrue();
    }

    [Fact]
    public void CoverageIsConservative_NeverReachesBeyondTheUnion()
    {
        // Every covered texel lies strictly inside the union of input rects: sample a dense grid around a
        // 2×2 tile block and assert no covered point falls outside the block.
        var rects = new[]
        {
            Rect(0f, 0f, 400f, 400f),
            Rect(400f, 0f, 800f, 400f),
            Rect(0f, 400f, 400f, 800f),
            Rect(400f, 400f, 800f, 800f),
        };
        BaseCoverageMask? mask = BaseCoverageMaskBuilder.Build(rects);

        mask.Should().NotBeNull();
        for (float x = -300f; x <= 1100f; x += 25f)
        {
            for (float y = -300f; y <= 1100f; y += 25f)
            {
                bool insideUnion = x is >= 0f and <= 800f && y is >= 0f and <= 800f;
                if (!insideUnion)
                {
                    mask!.CoveredAt(x, y).Should().BeFalse(
                        $"({x},{y}) lies outside the detail union — discarding base there would expose a hole");
                }
            }
        }

        mask!.CoveredAt(400f, 400f).Should().BeTrue("the union centre is covered");
    }
}