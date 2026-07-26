using FluentAssertions;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Shader-side guards for the steep-rock rescue. These prevent the two observed failure modes from silently
/// returning: a hard dominant-plane switch and cell-wise Voronoi facets painted over the orthophoto.
/// </summary>
public sealed class TerrainRockShaderTests
{
    private static string ShaderSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "MapaTur.App", "Services", "Terrain3DGlRenderer.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Nie znaleziono Terrain3DGlRenderer.cs idąc w górę od " + AppContext.BaseDirectory);
    }

    [Fact]
    public void should_build_rock_from_a_continuous_world_space_projection()
    {
        ShaderSource().Should().Contain("RockSample sampleRock(vec3 worldPos");
    }

    [Fact]
    public void should_use_a_scanned_world_aligned_material_instead_of_synthesizing_the_surface()
    {
        string source = ShaderSource();

        source.Should().Contain("uniform sampler2D uRockMaterial");
        source.Should().Contain("sampleScannedRockTriplanar");
        source.Should().Contain("rock026-albedo-height.png");
    }

    [Fact]
    public void should_reuse_the_legacy_det25_sampler_slot_within_the_gles_limit()
    {
        ShaderSource().Should().NotContain("uniform sampler2D uOrthoDet25;");
    }

    [Fact]
    public void should_restore_the_reflection_texture_after_the_rock_terrain_pass()
    {
        ShaderSource().Should().Contain("BindReflectionTextureForLakes(gl, reflectionDrawn)");
    }

    [Fact]
    public void should_derive_detail_normal_from_the_continuous_height_field()
    {
        ShaderSource().Should().Contain("perturbRockNormal");
    }

    [Fact]
    public void should_use_mipmapped_scan_height_for_stable_relief()
    {
        string source = ShaderSource();

        source.Should().Contain("scanned.a");
        source.Should().Contain("TextureMinFilter.LinearMipmapLinear");
    }

    [Fact]
    public void should_not_use_the_cellwise_voronoi_facet_material()
    {
        string source = ShaderSource();

        source.Should().NotContain("float cF1 = 8.0");
        source.Should().NotContain("float fF1 = 8.0");
    }
}
