using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Batching stron RMP2 w trybie „kolor z orto" (2026-09-05, „leć ten batching pilot"): pomiar ON→OFF dał
/// CPU +11 ms/klatkę przy 391 stronach × 3 passy = 1173 draw calli. Strony tej samej komórki orto i tej samej
/// grupy komórek (GroupCells × GroupCells) mają być scalone w JEDEN bufor/draw; podczas streamingu grupa „brudna"
/// (zmienił się skład) rysuje swoje strony pojedynczo, aż zostanie przebudowana (budżet przebudów na klatkę).
/// </summary>
public sealed class ScannedRockPageBatcherTests
{
    private static TerrainVertexPack Pack(int vertices, int triangles, float x0)
    {
        var pos = new Vector3[vertices];
        var nrm = new Vector3[vertices];
        var col = new uint[vertices];
        var tex = new float[vertices * 2];
        var det = new float[vertices];
        for (int i = 0; i < vertices; i++)
        {
            pos[i] = new Vector3(x0 + i, i, 0f);
            nrm[i] = Vector3.UnitZ;
            col[i] = 0xFF00FF00u | (uint)i;
            tex[i * 2] = x0 * 0.001f;
            tex[(i * 2) + 1] = i * 0.01f;
            det[i] = 0f;
        }

        var idx = new uint[triangles * 3];
        for (int t = 0; t < triangles; t++)
        {
            idx[t * 3] = (uint)(t % vertices);
            idx[(t * 3) + 1] = (uint)((t + 1) % vertices);
            idx[(t * 3) + 2] = (uint)((t + 2) % vertices);
        }

        return new TerrainVertexPack(pos, col, nrm, tex, det, idx);
    }

    private static ScannedRockPageStub Stub(int x, int y, byte lod, int ortho) =>
        new(new ScannedRockPageKey(x, y, lod), ortho, new Vector3(x * 55f, y * 55f, 1000f), new Vector3((x * 55f) + 55f, (y * 55f) + 55f, 1100f));

    [Fact]
    public void group_key_uses_floor_division_so_negative_page_indices_do_not_straddle_zero()
    {
        ScannedRockPageBatcher.GroupKeyFor(new ScannedRockPageKey(7, -1, 0), orthoTileIndex: 6, groupCells: 4)
            .Should().Be(new ScannedRockGroupKey(6, 1, -1));
        ScannedRockPageBatcher.GroupKeyFor(new ScannedRockPageKey(-4, -5, 2), orthoTileIndex: 6, groupCells: 4)
            .Should().Be(new ScannedRockGroupKey(6, -1, -2));
        ScannedRockPageBatcher.GroupKeyFor(new ScannedRockPageKey(3, 3, 1), orthoTileIndex: 2, groupCells: 4)
            .Should().Be(new ScannedRockGroupKey(2, 0, 0));
    }

    [Fact]
    public void group_key_separates_ortho_cells_because_the_base_texture_is_bound_per_draw()
    {
        ScannedRockPageBatcher.GroupKeyFor(new ScannedRockPageKey(1, 1, 0), 6, 4)
            .Should().NotBe(ScannedRockPageBatcher.GroupKeyFor(new ScannedRockPageKey(1, 1, 0), 7, 4));
    }

    [Fact]
    public void merge_concatenates_attributes_and_offsets_indices_by_vertex_base()
    {
        TerrainVertexPack a = Pack(vertices: 5, triangles: 2, x0: 100f);
        TerrainVertexPack b = Pack(vertices: 3, triangles: 1, x0: 200f);

        TerrainVertexPack m = ScannedRockPageBatcher.Merge([a, b]);

        m.Positions.Should().HaveCount(8);
        m.Normals.Should().HaveCount(8);
        m.Colors.Should().HaveCount(8);
        m.TexCoords.Should().HaveCount(16);
        m.Detail.Should().HaveCount(8);
        m.Indices.Should().HaveCount(9);
        m.Positions[5].Should().Be(b.Positions[0]);
        m.Colors[7].Should().Be(b.Colors[2]);
        m.TexCoords[10].Should().Be(b.TexCoords[0]);
        m.Indices.Take(6).Should().Equal(a.Indices);
        m.Indices.Skip(6).Should().Equal(b.Indices.Select(i => i + 5u));
    }

    [Fact]
    public void merge_of_single_pack_returns_equal_content()
    {
        TerrainVertexPack a = Pack(4, 2, 0f);

        TerrainVertexPack m = ScannedRockPageBatcher.Merge([a]);

        m.Positions.Should().Equal(a.Positions);
        m.Indices.Should().Equal(a.Indices);
    }

    [Fact]
    public void tracker_update_groups_drawable_pages_and_marks_new_groups_dirty()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);

        tracker.Update([Stub(0, 0, 0, 6), Stub(1, 0, 0, 6), Stub(3, 3, 1, 6), Stub(4, 0, 0, 6)]);

        tracker.Groups.Should().HaveCount(2);
        var g00 = new ScannedRockGroupKey(6, 0, 0);
        var g10 = new ScannedRockGroupKey(6, 1, 0);
        tracker.MembersOf(g00).Should().BeEquivalentTo([new ScannedRockPageKey(0, 0, 0), new ScannedRockPageKey(1, 0, 0), new ScannedRockPageKey(3, 3, 1)]);
        tracker.MembersOf(g10).Should().BeEquivalentTo([new ScannedRockPageKey(4, 0, 0)]);
        tracker.IsDirty(g00).Should().BeTrue();
        tracker.IsDirty(g10).Should().BeTrue();
    }

    [Fact]
    public void take_dirty_respects_the_per_frame_budget_and_mark_built_clears_dirty()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        tracker.Update([Stub(0, 0, 0, 6), Stub(4, 0, 0, 6), Stub(8, 0, 0, 6)]);

        IReadOnlyList<ScannedRockGroupKey> first = tracker.TakeDirty(max: 2);
        foreach (ScannedRockGroupKey g in first)
        {
            tracker.MarkBuilt(g);
        }

        first.Should().HaveCount(2);
        tracker.Groups.Count(g => tracker.IsDirty(g)).Should().Be(1);
        tracker.TakeDirty(max: 5).Should().HaveCount(1);
    }

    [Fact]
    public void lod_switch_of_one_cell_dirties_only_its_group_and_unchanged_groups_stay_built()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        tracker.Update([Stub(0, 0, 0, 6), Stub(1, 0, 0, 6), Stub(4, 0, 0, 6)]);
        foreach (ScannedRockGroupKey g in tracker.TakeDirty(10))
        {
            tracker.MarkBuilt(g);
        }

        tracker.Update([Stub(0, 0, 1, 6), Stub(1, 0, 0, 6), Stub(4, 0, 0, 6)]); // komórka (0,0): LOD0 → LOD1

        tracker.IsDirty(new ScannedRockGroupKey(6, 0, 0)).Should().BeTrue();
        tracker.IsDirty(new ScannedRockGroupKey(6, 1, 0)).Should().BeFalse();
        tracker.MembersOf(new ScannedRockGroupKey(6, 0, 0)).Should().BeEquivalentTo([new ScannedRockPageKey(0, 0, 1), new ScannedRockPageKey(1, 0, 0)]);
    }

    [Fact]
    public void update_without_changes_keeps_groups_clean()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        tracker.Update([Stub(0, 0, 0, 6), Stub(1, 0, 0, 6)]);
        foreach (ScannedRockGroupKey g in tracker.TakeDirty(10))
        {
            tracker.MarkBuilt(g);
        }

        tracker.Update([Stub(1, 0, 0, 6), Stub(0, 0, 0, 6)]); // ta sama zawartość, inna kolejność

        tracker.IsDirty(new ScannedRockGroupKey(6, 0, 0)).Should().BeFalse();
    }

    [Fact]
    public void draw_units_yield_built_groups_whole_and_dirty_groups_as_single_pages()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        tracker.Update([Stub(0, 0, 0, 6), Stub(1, 0, 0, 6), Stub(4, 0, 0, 6), Stub(5, 0, 0, 6)]);
        tracker.MarkBuilt(new ScannedRockGroupKey(6, 0, 0)); // tylko pierwsza grupa zbudowana

        List<ScannedRockDrawUnit> units = tracker.DrawUnits().ToList();

        units.Should().HaveCount(3);
        units.Count(u => u.Group is not null).Should().Be(1);
        units.Single(u => u.Group is not null).Group.Should().Be(new ScannedRockGroupKey(6, 0, 0));
        units.Where(u => u.Page is not null).Select(u => u.Page!.Value)
            .Should().BeEquivalentTo([new ScannedRockPageKey(4, 0, 0), new ScannedRockPageKey(5, 0, 0)]);
    }

    [Fact]
    public void group_removed_when_no_drawable_page_remains_and_reported_for_gpu_cleanup()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        tracker.Update([Stub(0, 0, 0, 6), Stub(4, 0, 0, 6)]);
        foreach (ScannedRockGroupKey g in tracker.TakeDirty(10))
        {
            tracker.MarkBuilt(g);
        }

        tracker.Update([Stub(0, 0, 0, 6)]);

        tracker.Groups.Should().BeEquivalentTo([new ScannedRockGroupKey(6, 0, 0)]);
        tracker.TakeRemoved().Should().BeEquivalentTo([new ScannedRockGroupKey(6, 1, 0)]);
        tracker.TakeRemoved().Should().BeEmpty();
    }

    [Fact]
    public void group_bounds_are_the_union_of_member_bounds()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        tracker.Update([Stub(0, 0, 0, 6), Stub(3, 3, 0, 6)]);

        (Vector3 min, Vector3 max) = tracker.BoundsOf(new ScannedRockGroupKey(6, 0, 0));

        min.Should().Be(new Vector3(0f, 0f, 1000f));
        max.Should().Be(new Vector3(220f, 220f, 1100f));
    }
}
