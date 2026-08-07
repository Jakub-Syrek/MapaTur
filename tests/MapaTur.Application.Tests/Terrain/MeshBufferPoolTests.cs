using System.Numerics;

using FluentAssertions;

// UWAGA 2026-08-07: testy duplikatów niżej to regresja z polowania na „płaskie cieniowanie" —
// podwójny Return tej samej tablicy rozdawał ją DWÓM najemcom naraz (wzajemne nadpisywanie
// normalnych/UV), patrz TerrainMesh3D.ReturnBuffersToPool (idempotencja) + guard w Push.
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
    public void Return_SameArrayTwice_HandsItOutOnlyOnce()
    {
        var pool = new MeshBufferPool();
        Vector3[] buffer = pool.RentVector3(100);

        pool.Return(buffer);
        pool.Return(buffer); // np. drugi UploadTile tej samej referencji kafla (context-loss)

        Vector3[] first = pool.RentVector3(100);
        Vector3[] second = pool.RentVector3(100);

        first.Should().BeSameAs(buffer);
        second.Should().NotBeSameAs(buffer, "podwójny Return nie może rozdać jednej tablicy dwóm najemcom naraz");
    }

    [Fact]
    public void Return_SameByteArrayTwice_CountsBytesOnce()
    {
        var pool = new MeshBufferPool();
        byte[] buffer = pool.RentBytes(1024);

        pool.Return(buffer);
        pool.Return(buffer);

        pool.PooledByteBytes.Should().Be(1024, "duplikat nie może zawyżać księgowości bajtów puli");
        pool.RentBytes(1024).Should().BeSameAs(buffer);
        pool.RentBytes(1024).Should().NotBeSameAs(buffer);
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