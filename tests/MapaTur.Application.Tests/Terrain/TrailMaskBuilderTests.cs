using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class TrailMaskBuilderTests
{
    // A 100 m x 100 m window at 1 texel / metre, so texel (i,j) centre sits at world (i+0.5, j+0.5). The distance
    // field stores A=255 on a line centre, ramping linearly to A=0 at MaxDistanceMeters (here 8 m) and beyond.
    private const float MaxDistance = 8.0f;

    private static TrailMaskRequest Request(params MaskPolyline[] lines) => new()
    {
        WorldMinX = 0f,
        WorldMinY = 0f,
        WorldSizeX = 100f,
        WorldSizeY = 100f,
        Width = 100,
        Height = 100,
        MaxDistanceMeters = MaxDistance,
        Lines = lines,
    };

    private static MaskPolyline Line(byte r, byte g, byte b, int priority, params (float x, float y)[] pts) =>
        new(pts.Select(p => new Vector3(p.x, p.y, 0f)).ToArray(), r, g, b, priority);

    // Violet route colour used by the route pass (matches TrailMaskInput.RouteColor).
    private const byte RouteR = 0x7C, RouteG = 0x3A, RouteB = 0xED;

    private static MaskRoute Route(float dash, float gap, float blend, float radius, params (float x, float y)[] pts) =>
        new(pts.Select(p => new Vector3(p.x, p.y, 0f)).ToArray(), RouteR, RouteG, RouteB, dash, gap, blend, radius);

    private static TrailMaskRequest RequestWith(MaskRoute? route, params MaskPolyline[] lines) => new()
    {
        WorldMinX = 0f,
        WorldMinY = 0f,
        WorldSizeX = 100f,
        WorldSizeY = 100f,
        Width = 100,
        Height = 100,
        MaxDistanceMeters = MaxDistance,
        Lines = lines,
        Route = route,
    };

    [Fact]
    public void Build_NullRequest_Throws()
    {
        Action act = () => TrailMaskBuilder.Build(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Build_OutputMatchesRequestedDimensions()
    {
        var mask = TrailMaskBuilder.Build(Request());

        mask.Width.Should().Be(100);
        mask.Height.Should().Be(100);
        mask.Rgba.Should().HaveCount(100 * 100 * 4);
        mask.WorldMinX.Should().Be(0f);
        mask.WorldSizeX.Should().Be(100f);
    }

    [Fact]
    public void Build_NoLines_AllPixelsZeroDistance()
    {
        var mask = TrailMaskBuilder.Build(Request());

        for (var i = 3; i < mask.Rgba.Length; i += 4)
        {
            mask.Rgba[i].Should().Be(0); // A=0 == "no line" everywhere
        }
    }

    [Fact]
    public void Build_PixelOnLineCentre_IsMaxAlphaWithLineColour()
    {
        // Horizontal red line at y = 50.
        var mask = TrailMaskBuilder.Build(Request(Line(200, 30, 30, 0, (10, 50), (90, 50))));

        // Texel (50,49) centre = (50.5, 49.5), distance 0.5 m to the line → A ≈ 255*(1 - 0.5/8) ≈ 239.
        var (r, g, b, a) = mask.PixelAt(50, 49);
        a.Should().BeGreaterThan(230);
        r.Should().Be(200);
        g.Should().Be(30);
        b.Should().Be(30);
    }

    [Fact]
    public void Build_PixelFarBeyondMaxDistance_IsZero()
    {
        var mask = TrailMaskBuilder.Build(Request(Line(200, 30, 30, 0, (10, 50), (90, 50))));

        mask.PixelAt(0, 0).A.Should().Be(0); // corner is >8 m from the y=50 line
    }

    [Fact]
    public void Build_DistanceFieldIsContinuous_NoGapsWithinTheBand()
    {
        // Every texel within MaxDistance of the y=50 line must be written (A>0) — a continuous band, not dots.
        var mask = TrailMaskBuilder.Build(Request(Line(200, 30, 30, 0, (10, 50), (90, 50))));

        // Column x=50: rows whose centre is within 8 m of y=50 (i.e. j in [43, 57]) must all be non-zero.
        for (var j = 43; j <= 57; j++)
        {
            mask.PixelAt(50, j).A.Should().BeGreaterThan(0, $"row {j} is within the distance band");
        }
    }

    [Fact]
    public void Build_AlphaDecreasesWithDistance()
    {
        // The closer texel must have a HIGHER alpha than the farther one (A encodes proximity).
        var mask = TrailMaskBuilder.Build(Request(Line(200, 30, 30, 0, (10, 50), (90, 50))));

        var near = mask.PixelAt(50, 49).A; // ~0.5 m
        var far = mask.PixelAt(50, 45).A;  // ~4.5 m

        near.Should().BeGreaterThan(far);
        far.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Build_RowOrientation_MinYIsRowZero()
    {
        // Vertical line spanning y in [80, 95] — only the NORTHERN (high-Y) rows should light up.
        var mask = TrailMaskBuilder.Build(Request(Line(50, 50, 200, 0, (50, 80), (50, 95))));

        mask.PixelAt(50, 85).A.Should().BeGreaterThan(0);   // north — covered
        mask.PixelAt(50, 20).A.Should().Be(0);              // south — empty
    }

    [Fact]
    public void Build_CoincidentLines_HigherPriorityWinsColour()
    {
        // Two co-located lines; the higher-priority (1) must win the colour where they coincide.
        var trail = Line(200, 30, 30, 0, (10, 50), (90, 50));
        var exposed = Line(255, 140, 0, 1, (10, 50), (90, 50));

        var mask = TrailMaskBuilder.Build(Request(trail, exposed));

        var (r, g, b, a) = mask.PixelAt(50, 49);
        a.Should().BeGreaterThan(230);
        r.Should().Be(255);
        g.Should().Be(140);
        b.Should().Be(0);
    }

    [Fact]
    public void Build_DistinctLines_NearestColourWinsRegardlessOfPriority()
    {
        // A LOW-priority line passes right through the texel; a HIGH-priority line runs several metres away. The
        // distance field must keep the NEAREST line's colour (so a trail under the texel still draws), not let the
        // far high-priority line claim it.
        var nearLowPriority = Line(200, 30, 30, 0, (10, 50), (90, 50));   // ~0.5 m from texel (50,49)
        var farHighPriority = Line(255, 140, 0, 5, (10, 56), (90, 56));   // ~6.5 m away

        var mask = TrailMaskBuilder.Build(Request(nearLowPriority, farHighPriority));

        var (r, g, b, _) = mask.PixelAt(50, 49);
        (r, g, b).Should().Be(((byte)200, (byte)30, (byte)30));
    }

    [Fact]
    public void Build_NaNBreak_DoesNotBridgeTheGap()
    {
        // One polyline with a NaN break: segment ends at x=40, resumes at x=60. The gap centre (x=50) must be far
        // from any line (both endpoints are >8 m away), so A=0 there.
        var broken = new MaskPolyline(
            new[]
            {
                new Vector3(10, 50, 0),
                new Vector3(40, 50, 0),
                new Vector3(float.NaN, float.NaN, float.NaN),
                new Vector3(60, 50, 0),
                new Vector3(90, 50, 0),
            },
            200, 30, 30, 0);

        var mask = TrailMaskBuilder.Build(Request(broken));

        mask.PixelAt(20, 49).A.Should().BeGreaterThan(0);   // first segment
        mask.PixelAt(80, 49).A.Should().BeGreaterThan(0);   // second segment
        mask.PixelAt(50, 49).A.Should().Be(0);               // gap centre — not bridged, and >8 m from each end
    }

    [Fact]
    public void Build_ScratchOverload_MatchesAllocatingOverload()
    {
        var request = Request(Line(200, 30, 30, 0, (10, 50), (90, 50)));
        var allocating = TrailMaskBuilder.Build(request);

        var rgba = new byte[100 * 100 * 4];
        var bestPriority = new int[100 * 100];
        var bestDistance = new float[100 * 100];
        var scratch = TrailMaskBuilder.Build(request, rgba, bestPriority, bestDistance);

        scratch.Rgba.Should().Equal(allocating.Rgba);
    }

    [Fact]
    public void Build_ScratchOverload_WrapsTheProvidedBuffer()
    {
        var rgba = new byte[100 * 100 * 4];
        var bestPriority = new int[100 * 100];
        var bestDistance = new float[100 * 100];

        var mask = TrailMaskBuilder.Build(Request(), rgba, bestPriority, bestDistance);

        mask.Rgba.Should().BeSameAs(rgba);
    }

    [Fact]
    public void Build_ScratchOverload_ReusedBufferDoesNotLeakStalePixels()
    {
        var rgba = new byte[100 * 100 * 4];
        var bestPriority = new int[100 * 100];
        var bestDistance = new float[100 * 100];

        // First build paints a line; the second build (no lines) over the SAME buffers must clear it.
        TrailMaskBuilder.Build(Request(Line(200, 30, 30, 0, (10, 50), (90, 50))), rgba, bestPriority, bestDistance);
        var second = TrailMaskBuilder.Build(Request(), rgba, bestPriority, bestDistance);

        for (var i = 3; i < second.Rgba.Length; i += 4)
        {
            second.Rgba[i].Should().Be(0);
        }
    }

    [Fact]
    public void Build_ScratchOverload_OversizedBufferIsAccepted()
    {
        // Buffers sized for a larger texture (the renderer reallocates only when dimensions grow) must still work.
        var rgba = new byte[200 * 200 * 4];
        var bestPriority = new int[200 * 200];
        var bestDistance = new float[200 * 200];

        var mask = TrailMaskBuilder.Build(Request(Line(200, 30, 30, 0, (10, 50), (90, 50))), rgba, bestPriority, bestDistance);

        mask.PixelAt(50, 49).A.Should().BeGreaterThan(230);
    }

    [Fact]
    public void Build_ScratchOverload_TooSmallBuffer_Throws()
    {
        var rgba = new byte[10];
        var bestPriority = new int[100 * 100];
        var bestDistance = new float[100 * 100];

        Action act = () => TrailMaskBuilder.Build(Request(), rgba, bestPriority, bestDistance);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_RouteOnTrail_DashTexelsBlendTowardViolet_TrailShowsThrough()
    {
        // A red trail and the route share the y=50 line. On a DASH stretch the route recolours the trail texel
        // toward violet (~60%) but keeps it partly red ⇒ translucent, and keeps the trail's high alpha.
        var trail = Line(200, 30, 30, 0, (10, 50), (90, 50));
        var route = Route(dash: 12f, gap: 8f, blend: 0.6f, radius: 2f, (10, 50), (90, 50));

        var mask = TrailMaskBuilder.Build(RequestWith(route, trail));

        // Texel (50,49): route arc ≈ 40.5 m → phase 0.5 m → inside a dash; ~0.5 m from the line.
        var (r, g, b, a) = mask.PixelAt(50, 49);
        a.Should().BeGreaterThan(230);             // trail's distance/alpha preserved → still a crisp line
        r.Should().BeLessThan(200);                // pulled down from pure red toward violet
        b.Should().BeGreaterThan(30);              // blue raised toward violet
        r.Should().BeGreaterThan(RouteR);          // but NOT fully violet — the trail shows through (translucent)
        b.Should().BeLessThan(RouteB);
    }

    [Fact]
    public void Build_RouteOnTrail_GapTexelsKeepTrailColour()
    {
        // The same red trail + route; a texel that falls in a GAP (and outside any dash's tight paint radius) must
        // keep the pure trail colour ⇒ the route reads as dashed.
        var trail = Line(200, 30, 30, 0, (10, 50), (90, 50));
        var route = Route(dash: 12f, gap: 8f, blend: 0.6f, radius: 2f, (10, 50), (90, 50));

        var mask = TrailMaskBuilder.Build(RequestWith(route, trail));

        // Texel (25,49): route arc ≈ 15.5 m → phase 15.5 m → in the gap (≥12); nearest dash edges are at x=22 and
        // x=30, both > 2 m (the paint radius) away → untouched by the route pass.
        var (r, g, b, _) = mask.PixelAt(25, 49);
        (r, g, b).Should().Be(((byte)200, (byte)30, (byte)30));
    }

    [Fact]
    public void Build_RouteOffTrail_WritesDashedFieldOnBareTerrain()
    {
        // No trail at all — the route runs over bare terrain. Its DASH texels must still be written into the field
        // (so the dashed route is visible), while a gap/far texel stays empty.
        var route = Route(dash: 12f, gap: 8f, blend: 0.6f, radius: 2f, (10, 50), (90, 50));

        var mask = TrailMaskBuilder.Build(RequestWith(route));

        var (r, g, b, a) = mask.PixelAt(50, 49); // arc ≈ 40.5 → dash, ~0.5 m from the line
        a.Should().BeGreaterThan(0);
        (r, g, b).Should().Be((RouteR, RouteG, RouteB)); // violet route colour written into the field
        mask.PixelAt(25, 49).A.Should().Be(0);           // gap (and >2 m from a dash) → not written
        mask.PixelAt(0, 0).A.Should().Be(0);             // far corner → empty
    }

    [Fact]
    public void Build_NullRoute_LeavesTrailUnchanged()
    {
        var trail = Line(200, 30, 30, 0, (10, 50), (90, 50));

        var withNull = TrailMaskBuilder.Build(RequestWith(route: null, trail));
        var plain = TrailMaskBuilder.Build(Request(trail));

        withNull.Rgba.Should().Equal(plain.Rgba); // a null route is a no-op
    }
}