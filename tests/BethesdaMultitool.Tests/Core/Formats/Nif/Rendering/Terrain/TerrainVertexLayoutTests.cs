using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Tests.Helpers;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Shader;
using Vortice.DXGI;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Guards the terrain vertex ABI from both sides: the layout must tile
///     <see cref="TerrainVertex" /> exactly, and it must supply every input the compiled shader
///     declares.
///     <para>
///         This matters more than a normal layout because the failure is silent. D3D12 does not
///         object to an input element pointing at the wrong offset — it reads whatever bytes are
///         there, so a stale offset renders folded geometry with no error raised anywhere. It only
///         errors for an input the shader declares and the layout omits, which is the one case a
///         PSO-creation test would catch.
///     </para>
/// </summary>
public sealed class TerrainVertexLayoutTests
{
    /// <summary>
    ///     Bytes per element for the formats this layout uses. Written out rather than resolved
    ///     through a production helper, so the tiling checks below are against independently known
    ///     sizes.
    /// </summary>
    private static int FormatBytes(Format format) => format switch
    {
        Format.R32G32B32A32_Float => 16,
        Format.R32G32B32_Float => 12,
        Format.R16G16_SNorm => 4,
        Format.R32_Float => 4,
        Format.R8G8B8A8_UNorm => 4,
        _ => throw new NotSupportedException($"Unexpected terrain input format {format}.")
    };

    [Fact]
    public void The_geometry_stream_tiles_the_terrain_vertex_with_no_gap_or_overlap()
    {
        var elements = TerrainVertexLayout.Elements
            .Where(e => e.Slot == TerrainVertexLayout.GeometrySlot)
            .OrderBy(e => e.AlignedByteOffset)
            .ToArray();

        Assert.Equal(3, elements.Length);

        var cursor = 0;
        foreach (var element in elements)
        {
            Assert.Equal(cursor, (int)element.AlignedByteOffset);
            cursor += FormatBytes(element.Format);
        }

        // Landing exactly on the struct size is what proves there is no trailing field the layout
        // forgot and no padding the stride would skip past.
        Assert.Equal(TerrainVertex.SizeInBytes, cursor);
    }

    [Fact]
    public void The_blend_weight_stream_tiles_its_stride_with_no_gap_or_overlap()
    {
        var elements = TerrainVertexLayout.Elements
            .Where(e => e.Slot == TerrainVertexLayout.BlendWeightSlot)
            .OrderBy(e => e.AlignedByteOffset)
            .ToArray();

        Assert.Equal(4, elements.Length); // 16 layer weights as four float4s

        var cursor = 0;
        foreach (var element in elements)
        {
            Assert.Equal(cursor, (int)element.AlignedByteOffset);
            cursor += FormatBytes(element.Format);
        }

        Assert.Equal((int)TerrainVertexLayout.BlendWeightStride, cursor);
    }

    [Fact]
    public void Every_element_is_a_distinct_texcoord_index()
    {
        // A duplicated semantic silently shadows one attribute with another's data.
        var semantics = TerrainVertexLayout.Elements
            .Select(e => (e.SemanticName, e.SemanticIndex))
            .ToArray();

        Assert.Equal(semantics.Length, semantics.Distinct().Count());
        Assert.All(TerrainVertexLayout.Elements, e => Assert.Equal("TEXCOORD", e.SemanticName));
        Assert.Equal(
            Enumerable.Range(0, TerrainVertexLayout.Elements.Length).ToArray(),
            TerrainVertexLayout.Elements.Select(e => (int)e.SemanticIndex).Order().ToArray());
    }

    [Fact]
    public void The_geometry_stream_is_far_narrower_than_the_shared_mesh_vertex_it_replaced()
    {
        // Regression direction: if slot 0 ever widens back past ~24 bytes, someone has re-added a
        // field the terrain shaders do not read, and the residency budget silently loses ground.
        var slotZeroBytes = TerrainVertexLayout.Elements
            .Where(e => e.Slot == TerrainVertexLayout.GeometrySlot)
            .Sum(e => FormatBytes(e.Format));

        Assert.True(slotZeroBytes <= 16, $"terrain slot 0 grew back to {slotZeroBytes} bytes");
    }

    [Trait("Category", TestCategories.ShaderCompile)]
    [Fact]
    public void The_layout_supplies_every_input_the_compiled_vertex_shader_declares()
    {
        // The check a source pin cannot make: reflect the ACTUAL compiled signature. An input the
        // shader declares and the layout omits is the one mismatch D3D12 rejects outright, so this
        // is the difference between a startup failure in the user's hands and a failing test here.
        ShaderCompileTestGuard.SkipUnlessEnabled();

        var bytecode = GpuShaderCompiler12.Compile("terrain_textured.vert.hlsl", "main", "vs_5_1", []);
        using var reflection = Compiler.Reflect<ID3D12ShaderReflection>(bytecode);

        var supplied = TerrainVertexLayout.Elements
            .Select(e => $"{e.SemanticName}{e.SemanticIndex}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var declared = new List<string>();
        for (var i = 0u; i < reflection.Description.InputParameters; i++)
        {
            var parameter = reflection.GetInputParameterDescription(i);
            if (parameter.SystemValueType != SystemValueType.Undefined)
            {
                continue; // SV_VertexID and friends come from the IA, not a vertex buffer
            }

            declared.Add($"{parameter.SemanticName}{parameter.SemanticIndex}");
        }

        Assert.NotEmpty(declared);
        var missing = declared.Where(d => !supplied.Contains(d)).ToArray();
        Assert.True(missing.Length == 0, $"shader declares inputs the layout does not supply: {string.Join(", ", missing)}");

        // And the other direction, which D3D12 tolerates but which means we are uploading bytes
        // nothing reads — exactly the waste this phase existed to remove.
        var unread = supplied.Where(s => !declared.Contains(s, StringComparer.OrdinalIgnoreCase)).ToArray();
        Assert.True(unread.Length == 0, $"layout supplies inputs the shader never reads: {string.Join(", ", unread)}");
    }
}
