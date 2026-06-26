using FluentAssertions;

using MapaTur.Application.Routing;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;

namespace MapaTur.Application.Tests.Routing;

/// <summary>
/// <see cref="RouteStopsSerializer"/> round-trips the planned route's stops through JSON so the tourist route
/// survives an app restart, and tolerates a missing or corrupt stored payload without throwing.
/// </summary>
public sealed class RouteStopsSerializerTests
{
    private static readonly IReadOnlyList<RouteWaypoint> SampleRoute = new[]
    {
        new RouteWaypoint("Morskie Oko", new GeoPoint(49.1951, 20.0706), WaypointKind.Lake, 1395.0),
        new RouteWaypoint("Rysy", new GeoPoint(49.1795, 20.0881), WaypointKind.Peak, 2499.0),
        new RouteWaypoint("Punkt na szlaku", new GeoPoint(49.2000, 20.0500), WaypointKind.TrailPoint, null),
    };

    [Fact]
    public void Roundtrip_PreservesNameLocationKindAndElevation()
    {
        string json = RouteStopsSerializer.Serialize(SampleRoute);
        IReadOnlyList<RouteWaypoint> back = RouteStopsSerializer.Deserialize(json);

        back.Should().HaveCount(3);
        back[0].Name.Should().Be("Morskie Oko");
        back[0].Location.Latitude.Should().BeApproximately(49.1951, 1e-9);
        back[0].Location.Longitude.Should().BeApproximately(20.0706, 1e-9);
        back[0].Kind.Should().Be(WaypointKind.Lake);
        back[0].ElevationMeters.Should().BeApproximately(1395.0, 1e-9);
    }

    [Fact]
    public void Roundtrip_PreservesStopOrder()
    {
        IReadOnlyList<RouteWaypoint> back = RouteStopsSerializer.Deserialize(RouteStopsSerializer.Serialize(SampleRoute));

        back.Select(s => s.Name).Should().ContainInOrder("Morskie Oko", "Rysy", "Punkt na szlaku");
    }

    [Fact]
    public void Roundtrip_PreservesNullElevation()
    {
        IReadOnlyList<RouteWaypoint> back = RouteStopsSerializer.Deserialize(RouteStopsSerializer.Serialize(SampleRoute));

        back[2].ElevationMeters.Should().BeNull();
    }

    [Fact]
    public void Deserialize_Null_ReturnsEmpty() =>
        RouteStopsSerializer.Deserialize(null).Should().BeEmpty();

    [Fact]
    public void Deserialize_Empty_ReturnsEmpty() =>
        RouteStopsSerializer.Deserialize(string.Empty).Should().BeEmpty();

    [Fact]
    public void Deserialize_Corrupt_ReturnsEmptyWithoutThrowing() =>
        RouteStopsSerializer.Deserialize("{not valid json").Should().BeEmpty();

    [Fact]
    public void Serialize_EmptyRoute_RoundtripsToEmpty()
    {
        string json = RouteStopsSerializer.Serialize(System.Array.Empty<RouteWaypoint>());

        RouteStopsSerializer.Deserialize(json).Should().BeEmpty();
    }

    [Fact]
    public void Serialize_Null_Throws() =>
        ((System.Action)(() => RouteStopsSerializer.Serialize(null!))).Should().Throw<System.ArgumentNullException>();
}