using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class MeshBufferPoolTests
{
    [Fact]
    public void Rent_AfterReturn_ReusesTheSameArrayOfThatLength()
    {
        var pool = new MeshBufferPool();
        Vector3[] first = pool.RentVector3(100);

        pool.Return(first);
        Vector3[] second = pool.RentVector3(100);

        second.Should().BeSameAs(first, "a returned buffer of the same length must be reused, not reallocated");
    }

    [Fact]
    public void Rent_DifferentLength_DoesNotReuse()
    {
        var pool = new MeshBufferPool();
        pool.Return(pool.RentVector3(100));

        pool.RentVector3(64).Length.Should().Be(64, "a different length must get its own exact-size array");
    }

    [Fact]
    public void Rent_ReturnsExactRequestedLength()
    {
        var pool = new MeshBufferPool();

        pool.RentVector3(50).Length.Should().Be(50);
        pool.RentUInt32(73).Length.Should().Be(73);
        pool.RentSingle(128).Length.Should().Be(128);
    }

    [Fact]
    public void Return_EmptyOrNull_IsIgnored()
    {
        var pool = new MeshBufferPool();

        pool.Return((Vector3[]?)null);
        pool.Return(Array.Empty<Vector3>());

        pool.RentVector3(0).Length.Should().Be(0); // no crash, fresh empty
    }
}