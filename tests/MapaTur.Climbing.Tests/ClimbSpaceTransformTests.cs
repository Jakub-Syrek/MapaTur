using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Climbing.Tests;

public sealed class ClimbSpaceTransformTests
{
    [Fact]
    public void Gravity_should_be_mapatur_z_down()
    {
        Assert.Equal(new Vector3(0f, 0f, -9.81f), ClimbWorld.Gravity);
    }

    [Fact]
    public void ToClimbPoint_should_divide_z_by_exaggeration()
    {
        ClimbSpaceTransform transform = new(2.0f);

        Vector3 climb = transform.ToClimbPoint(new Vector3(10f, 20f, 30f));

        Assert.Equal(new Vector3(10f, 20f, 15f), climb);
    }

    [Fact]
    public void ToRenderPoint_should_multiply_z_by_exaggeration()
    {
        ClimbSpaceTransform transform = new(1.5f);

        Vector3 render = transform.ToRenderPoint(new Vector3(10f, 20f, 30f));

        Assert.Equal(new Vector3(10f, 20f, 45f), render);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    public void Point_roundtrip_should_return_original(float exaggeration)
    {
        ClimbSpaceTransform transform = new(exaggeration);
        Vector3 original = new(123.4f, -56.7f, 1987.5f);

        Vector3 roundTrip = transform.ToClimbPoint(transform.ToRenderPoint(original));

        AssertApprox(original, roundTrip, 1e-3f);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.5f)]
    [InlineData(2.0f)]
    public void ToClimbNormal_should_match_normal_of_deescalated_triangle(float exaggeration)
    {
        // A steep, slanted triangle in climb space (real metres).
        Vector3 a = new(0f, 0f, 0f);
        Vector3 b = new(1f, 0f, 2f);
        Vector3 c = new(0f, 1f, 1.5f);
        Vector3 climbNormal = Vector3.Normalize(Vector3.Cross(b - a, c - a));

        ClimbSpaceTransform transform = new(exaggeration);
        Vector3 ra = transform.ToRenderPoint(a);
        Vector3 rb = transform.ToRenderPoint(b);
        Vector3 rc = transform.ToRenderPoint(c);
        Vector3 renderNormal = Vector3.Normalize(Vector3.Cross(rb - ra, rc - ra));

        Vector3 recovered = transform.ToClimbNormal(renderNormal);

        AssertApprox(climbNormal, recovered, 1e-5f);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(2.0f)]
    public void Normal_roundtrip_should_return_original(float exaggeration)
    {
        ClimbSpaceTransform transform = new(exaggeration);
        Vector3 original = Vector3.Normalize(new Vector3(0.3f, -0.5f, 0.8f));

        Vector3 roundTrip = transform.ToClimbNormal(transform.ToRenderNormal(original));

        AssertApprox(original, roundTrip, 1e-5f);
    }

    [Fact]
    public void ToClimbNormal_should_return_unit_length()
    {
        ClimbSpaceTransform transform = new(1.8f);

        Vector3 normal = transform.ToClimbNormal(new Vector3(0.2f, 0.4f, 0.6f));

        Assert.Equal(1f, normal.Length(), 4);
    }

    [Fact]
    public void Identity_should_leave_point_and_normal_unchanged()
    {
        Vector3 point = new(5f, 6f, 7f);
        Vector3 normal = Vector3.Normalize(new Vector3(0.1f, 0.2f, 0.9f));

        AssertApprox(point, ClimbSpaceTransform.Identity.ToClimbPoint(point), 1e-6f);
        AssertApprox(normal, ClimbSpaceTransform.Identity.ToClimbNormal(normal), 1e-6f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Constructor_should_reject_non_positive_exaggeration(float exaggeration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClimbSpaceTransform(exaggeration));
    }

    private static void AssertApprox(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.True(
            Vector3.Distance(expected, actual) <= tolerance,
            $"expected {expected}, got {actual} (tolerance {tolerance})");
    }
}