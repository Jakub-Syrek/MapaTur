using FluentAssertions;

namespace MapaTur.Application.Tests.Terrain;

public sealed class LineShaderCullingContractTests
{
    [Fact]
    public void VertexShader_CullsWholeSegmentsOutsideLineRadiusBeforeRasterization()
    {
        string source = File.ReadAllText(FindRendererSource());

        source.Should().Contain("uniform vec3 uCameraPos;");
        source.Should().Contain("uniform float uMaxDist;");
        source.Should().Contain("if (min(distA, distB) > uMaxDist)");
    }

    private static string FindRendererSource()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "MapaTur.App", "Services", "Terrain3DGlRenderer.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Terrain3DGlRenderer.cs");
    }
}
