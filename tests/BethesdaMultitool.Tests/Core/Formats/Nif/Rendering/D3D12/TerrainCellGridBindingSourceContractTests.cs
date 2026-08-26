using System.Text.RegularExpressions;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Pins that every terrain pass hands the vertex shader its cell grid.
///     <para>
///         A source-contract test, which the repo treats as a last resort — justified here on the
///         stated exception: this is D3D12 call ordering inside <c>TerrainRenderer12</c>, which is
///         <c>#if WINDOWS_GUI</c> and therefore unreachable from the cross-platform test TFM, and
///         the defect it guards is silent. Since world X and Y left the vertex, a draw that binds a
///         cell's geometry without binding its grid root constants renders that cell on whatever
///         grid the <i>previous</i> draw left in the root arguments. Nothing errors; the cell simply
///         appears stacked on another one. There are three such passes today (colour, shadow depth,
///         water mirror) and adding a fourth is exactly the moment this is easy to forget.
///     </para>
/// </summary>
public sealed class TerrainCellGridBindingSourceContractTests
{
    private static string Renderer => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", "TerrainRenderer12.cs");

    [Fact]
    public void Every_pass_that_binds_a_cells_geometry_also_binds_its_grid()
    {
        var source = Renderer;

        var geometryBinds = Regex.Count(source, @"IASetVertexBuffers\(\s*0\s*,\s*entry\.VertexView");
        var gridBinds = Regex.Count(source, @"BindCellGrid\(\s*cmd\s*,\s*entry\s*\)");

        Assert.True(geometryBinds >= 3, $"expected at least the colour/shadow/mirror passes, found {geometryBinds}");
        Assert.Equal(geometryBinds, gridBinds);
    }

    [Fact]
    public void The_grid_is_bound_as_root_constants_rather_than_a_per_cell_constant_buffer()
    {
        // The shadow and depth-only passes deliberately bind no per-cell CB — a ring allocation per
        // cell per cascade was the streaming pressure that path was written to avoid. Root constants
        // are what let the grid reach those passes without reintroducing it.
        var source = Renderer;

        Assert.Contains("SetGraphicsRoot32BitConstants(GpuRootSignature12.Slots.TerrainCellGridConstants",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_vertex_shader_reconstructs_the_position_it_no_longer_receives()
    {
        // The other half of the contract: the layout supplies no X/Y, so the shader has to build
        // them. TerrainVertexLayoutTests checks the signature agreement against the compiled shader;
        // this pins that the reconstruction is guarded against contraction, which is what keeps a
        // cell's edge column bit-identical to its neighbour's.
        var shader = SourceContract.ReadSource(SourceContract.ShaderPath("terrain_textured.vert.hlsl"));

        Assert.Contains("SV_VertexID", shader, StringComparison.Ordinal);
        Assert.Contains("register(b4)", shader, StringComparison.Ordinal);
        Assert.Matches(@"precise float worldX\s*=", shader);
        Assert.Matches(@"precise float worldY\s*=", shader);
    }
}
