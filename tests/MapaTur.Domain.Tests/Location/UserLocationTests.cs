using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Location;

namespace MapaTur.Domain.Tests.Location;

public sealed class UserLocationTests
{
    private static readonly GeoPoint Kasprowy = new(49.2326, 19.9819, 1987.0);
    private static readonly DateTimeOffset T = new(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidFix_PreservesPositionAndTimestamp()
    {
        UserLocation fix = UserLocation.Create(Kasprowy, accuracyMeters: 5.0, timestamp: T);

        fix.Position.Should().Be(Kasprowy);
        fix.AccuracyMeters.Should().Be(5.0);
        fix.Timestamp.Should().Be(T);
    }

    [Fact]
    public void Create_ZeroAccuracy_IsAllowed_BecauseSomePlatformsReportUnknownAsZero()
    {
        Action create = () => UserLocation.Create(Kasprowy, accuracyMeters: 0.0, timestamp: T);

        create.Should().NotThrow();
    }

    [Fact]
    public void Create_NegativeAccuracy_Throws()
    {
        Action create = () => UserLocation.Create(Kasprowy, accuracyMeters: -1.0, timestamp: T);

        create.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("accuracyMeters");
    }

    [Fact]
    public void Create_NonFiniteAccuracy_Throws()
    {
        Action create = () => UserLocation.Create(Kasprowy, accuracyMeters: double.PositiveInfinity, timestamp: T);

        create.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("accuracyMeters");
    }
}