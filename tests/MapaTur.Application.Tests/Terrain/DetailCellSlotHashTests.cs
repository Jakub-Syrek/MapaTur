using FluentAssertions;

namespace MapaTur.Application.Tests.Terrain;

public sealed class DetailCellSlotHashTests
{
    [Fact]
    public void Build_MapsEveryResidentCellWithoutDependingOnTheLayerCap()
    {
        var cells = new List<MapaTur.Application.Terrain.DetailCellSlot>(192);
        int slot = 0;
        for (int y = 120; y < 132; y++)
        {
            for (int x = 80; x < 96; x++)
            {
                cells.Add(new(x, y, slot, (byte)(slot % 256)));
                slot++;
            }
        }

        MapaTur.Application.Terrain.DetailCellSlotHash table =
            MapaTur.Application.Terrain.DetailCellSlotHash.Build(cells, tableSize: 384, maxProbe: 12);

        table.Count.Should().Be(192);
        table.MaxProbeUsed.Should().BeLessThanOrEqualTo(12);
        foreach (var cell in cells)
        {
            table.TryGet(cell.Ci, cell.Cj, out int actualSlot, out byte actualAlpha).Should().BeTrue();
            actualSlot.Should().Be(cell.Slot);
            actualAlpha.Should().Be(cell.Alpha);
        }
    }

    [Fact]
    public void Build_HandlesSparseAndNegativeGridCoordinates()
    {
        MapaTur.Application.Terrain.DetailCellSlotHash table =
            MapaTur.Application.Terrain.DetailCellSlotHash.Build(
            [
                new(-11, 700, 3, 255),
                new(1400, -9, 71, 128),
                new(42, 43, 191, 1),
            ],
            tableSize: 16,
            maxProbe: 6);

        table.TryGet(-11, 700, out int slotA, out byte alphaA).Should().BeTrue();
        (slotA, alphaA).Should().Be((3, (byte)255));
        table.TryGet(1400, -9, out int slotB, out byte alphaB).Should().BeTrue();
        (slotB, alphaB).Should().Be((71, (byte)128));
        table.TryGet(999, 999, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Build_RejectsDuplicateCoordinatesAndInvalidSlots()
    {
        Action duplicate = () => MapaTur.Application.Terrain.DetailCellSlotHash.Build(
        [
            new(4, 5, 1, 255),
            new(4, 5, 2, 255),
        ],
        tableSize: 8,
        maxProbe: 4);
        duplicate.Should().Throw<ArgumentException>();

        Action invalidSlot = () => MapaTur.Application.Terrain.DetailCellSlotHash.Build(
            [new(4, 5, -1, 255)],
            tableSize: 8,
            maxProbe: 4);
        invalidSlot.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PackedEntries_AreIvec4ReadyAndEmptyEntriesHaveNegativeSlot()
    {
        MapaTur.Application.Terrain.DetailCellSlotHash table =
            MapaTur.Application.Terrain.DetailCellSlotHash.Build(
                [new(7, 8, 12, 200)],
                tableSize: 8,
                maxProbe: 4);

        table.PackedEntries.Should().HaveCount(8 * 4);
        for (int i = 0; i < table.PackedEntries.Length; i += 4)
        {
            int storedSlot = table.PackedEntries[i + 2];
            if (storedSlot < 0)
            {
                table.PackedEntries[i + 3].Should().Be(0);
            }
        }
    }

    [Fact]
    public void Build_PacksMinimumLodBesidePromotionAlpha()
    {
        MapaTur.Application.Terrain.DetailCellSlotHash table =
            MapaTur.Application.Terrain.DetailCellSlotHash.Build(
                [new(7, 8, 12, 200, MinimumLod: 2)],
                tableSize: 8,
                maxProbe: 4);

        table.TryGet(7, 8, out int slot, out byte alpha, out byte minimumLod).Should().BeTrue();
        (slot, alpha, minimumLod).Should().Be((12, (byte)200, (byte)2));
    }

    [Fact]
    public void Build_KeepsTheProbeBoundForMovingCircularAndSparseResidencySets()
    {
        var random = new Random(1701);
        for (int frame = 0; frame < 120; frame++)
        {
            var coords = new HashSet<(int X, int Y)>();
            int cx = 300 + frame, cy = 220 - (frame / 3);
            while (coords.Count < 192)
            {
                int x = cx + random.Next(-18, 19);
                int y = cy + random.Next(-18, 19);
                if (((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= 18 * 18)
                {
                    coords.Add((x, y));
                }
            }

            var cells = coords.Select((p, slot) =>
                new MapaTur.Application.Terrain.DetailCellSlot(p.X, p.Y, slot, 255)).ToArray();
            MapaTur.Application.Terrain.DetailCellSlotHash table =
                MapaTur.Application.Terrain.DetailCellSlotHash.Build(cells, 384, 12);

            table.MaxProbeUsed.Should().BeLessThanOrEqualTo(12);
        }
    }
}
