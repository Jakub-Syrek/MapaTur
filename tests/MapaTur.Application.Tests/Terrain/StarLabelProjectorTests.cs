using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="StarLabelProjector"/>: turns above-horizon named star directions into
/// on-screen label positions, matching <see cref="Camera3D.ProjectToScreen(Vector3, Matrix4x4, float, float)"/>'s
/// convention so a label lands exactly on its GL point sprite. Stars below the horizon, behind the camera, or
/// off the viewport are dropped.
/// </summary>
public sealed class StarLabelProjectorTests
{
    // Camera at the origin looking north and slightly up (world X east, Y north, Z up).
    private static readonly Vector3 Forward = Vector3.Normalize(new Vector3(0f, 1f, 0.5f));

    private static Matrix4x4 SkyViewProjection()
    {
        Matrix4x4 view = Matrix4x4.CreateLookAt(Vector3.Zero, Forward, Vector3.UnitZ);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 1.25f, 1f, 1_000_000f);
        return view * proj;
    }

    [Fact]
    public void Project_StarInViewDirection_CentersOnScreenWithNameAndMagnitude()
    {
        var stars = new[] { (Forward, 0.03f, "Vega") };

        var labels = StarLabelProjector.Project(stars, SkyViewProjection(), 1000f, 800f);

        labels.Should().ContainSingle();
        labels[0].Name.Should().Be("Vega");
        labels[0].Magnitude.Should().BeApproximately(0.03f, 1e-3f);
        labels[0].ScreenX.Should().BeApproximately(500f, 5f);
        labels[0].ScreenY.Should().BeApproximately(400f, 5f);
    }

    [Fact]
    public void Project_BelowHorizonStar_IsOmitted()
    {
        // Same azimuth as the view, but pointing below the horizon (Z < 0).
        var stars = new[] { (Vector3.Normalize(new Vector3(0f, 1f, -0.5f)), 1.0f, "Below") };

        StarLabelProjector.Project(stars, SkyViewProjection(), 1000f, 800f).Should().BeEmpty();
    }

    [Fact]
    public void Project_StarBehindCamera_IsOmitted()
    {
        // Above the horizon but to the south — behind a north-facing camera (clip.w <= 0).
        var stars = new[] { (Vector3.Normalize(new Vector3(0f, -1f, 0.5f)), 1.0f, "Behind") };

        StarLabelProjector.Project(stars, SkyViewProjection(), 1000f, 800f).Should().BeEmpty();
    }

    [Fact]
    public void Project_OffScreenStar_IsOmitted()
    {
        // Above the horizon and in front, but far to the east — outside the viewport.
        var stars = new[] { (Vector3.Normalize(new Vector3(1f, 0.15f, 0.2f)), 1.0f, "EastEdge") };

        StarLabelProjector.Project(stars, SkyViewProjection(), 1000f, 800f).Should().BeEmpty();
    }

    [Fact]
    public void Project_NullStars_Throws()
    {
        Action act = () => StarLabelProjector.Project(null!, SkyViewProjection(), 1000f, 800f);

        act.Should().Throw<ArgumentNullException>();
    }
}