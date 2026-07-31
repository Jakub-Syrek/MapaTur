using System;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="TileBorderProfileAudit"/> — the post-bake gate that catches BORDER-CONCENTRATED
/// height artefacts the bit-identity seam check is structurally blind to. The lesson it encodes (2026-07-15):
/// a per-tile-clamped kernel biased the outer 1–2 rows of EVERY tile symmetrically, so adjacent tiles agreed
/// bit-for-bit on the shared edge while a real ~1 m kink ran along every border (p95 |curvature residual| at
/// ±1 cell ≈ 1.0 m vs 0.44 m mid-tile background on the real z17 pyramid). The audit therefore measures the
/// PROFILE ACROSS the border and compares it against the same statistic computed mid-tile — any future border
/// artefact, whatever its cause, fails the bake instead of waiting for eyes.
/// </summary>
public sealed class TileBorderProfileAuditTests
{
    private const int Side = 64;
    private const double NoData = -9999.0;

    // Smooth, curved, GLOBALLY-defined ground (pixel-is-point: adjacent tiles share their boundary line).
    private static float Ground(long gc, long gr)
        => 1500f + (0.001f * gc * gc) + (0.0008f * gr * gr) + (3f * MathF.Sin(0.37f * gc) * MathF.Cos(0.31f * gr));

    private static BakedDemTile MakeTile(int tx, int ty, Func<long, long, float>? ground = null)
    {
        ground ??= Ground;
        var heights = new float[Side * Side];
        for (int r = 0; r < Side; r++)
        {
            for (int c = 0; c < Side; c++)
            {
                heights[(r * Side) + c] = ground(((long)tx * (Side - 1)) + c, ((long)ty * (Side - 1)) + r);
            }
        }

        var bounds = new MapBounds(new GeoPoint(49.0, 20.0), new GeoPoint(49.001, 20.001));
        return new BakedDemTile(17, tx, ty, Side, Side, bounds, NoData, heights);
    }

    // The measured artefact's shape: a symmetric kink — the outer row/column of EVERY tile pulled toward the
    // tile's inside. Seams stay bit-identical (both tiles computed the same shared line), the kink is real.
    private static BakedDemTile MakeKinkedTile(int tx, int ty, float kinkMeters)
    {
        BakedDemTile t = MakeTile(tx, ty);
        for (int r = 0; r < Side; r++)
        {
            t.Heights[(r * Side) + 1] += kinkMeters;          // one cell in from the west edge
            t.Heights[(r * Side) + Side - 2] -= kinkMeters;   // one cell in from the east edge
        }

        return t;
    }

    [Fact]
    public void Report_SmoothWeldedPair_ReadsBorderLikeMidTile()
    {
        var audit = new TileBorderProfileAudit();
        audit.AddEastPair(MakeTile(10, 5), MakeTile(11, 5), stride: 2);
        audit.AddSouthPair(MakeTile(10, 5), MakeTile(10, 6), stride: 2);
        audit.AddControl(MakeTile(10, 5), stride: 4);

        TileBorderProfileReport report = audit.Report();

        report.BorderProfileCount.Should().BeGreaterThan(30);
        report.ControlProfileCount.Should().BeGreaterThan(5);
        report.IsWithin(ratio: 1.3, floorMeters: 0.05).Should().BeTrue(
            "on clean data the cross-border profile is statistically the same ground as mid-tile");
    }

    [Fact]
    public void Report_SymmetricBorderKink_FailsTheGate_EvenThoughSeamsAreBitIdentical()
    {
        // THE case the bit-identity check can't see: both tiles carry the same inward bias, the shared line
        // matches exactly, and a real groove runs along the border.
        BakedDemTile west = MakeKinkedTile(10, 5, kinkMeters: 0.6f);
        BakedDemTile east = MakeKinkedTile(11, 5, kinkMeters: 0.6f);
        for (int r = 0; r < Side; r++)
        {
            west.Heights[(r * Side) + Side - 1].Should().Be(east.Heights[r * Side], "the seam itself is welded");
        }

        var audit = new TileBorderProfileAudit();
        audit.AddEastPair(west, east, stride: 2);
        audit.AddControl(west, stride: 4);

        TileBorderProfileReport report = audit.Report();

        report.IsWithin(ratio: 1.3, floorMeters: 0.05).Should().BeFalse(
            "a border-concentrated kink must trip the gate no matter how clean the seam line is");
        report.WorstBorderP95.Should().BeGreaterThan(0.5, "the injected 0.6 m kink dominates the residual");
    }

    [Fact]
    public void Report_ProfilesWithVoids_AreSkippedNotCounted()
    {
        BakedDemTile west = MakeTile(10, 5);
        BakedDemTile east = MakeTile(11, 5);
        Array.Fill(east.Heights, (float)NoData); // the whole neighbour is a void

        var audit = new TileBorderProfileAudit();
        audit.AddEastPair(west, east, stride: 2);

        audit.Report().BorderProfileCount.Should().Be(0, "a profile touching NoData measures nothing");
    }

    [Fact]
    public void IsWithin_AbsoluteFloor_KeepsAFlatRegionFromFlakingTheGate()
    {
        // Dead-flat ground has a ~0 control p95; without the absolute floor any femtometre of border noise
        // would read as an "infinite ratio" and flake the bake.
        var audit = new TileBorderProfileAudit();
        audit.AddEastPair(MakeTile(10, 5, (_, _) => 1500f), MakeTile(11, 5, (_, _) => 1500f), stride: 2);
        audit.AddControl(MakeTile(10, 5, (_, _) => 1500f), stride: 4);

        audit.Report().IsWithin(ratio: 1.3, floorMeters: 0.05).Should().BeTrue();
    }
}