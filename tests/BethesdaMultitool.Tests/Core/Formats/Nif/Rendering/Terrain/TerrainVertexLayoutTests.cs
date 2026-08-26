using System.Globalization;
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
///     declares — at every blend-quad width, since a cell now picks its own.
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
    /// <summary>Every blend-quad width the renderer can select, including the depth-only 0.</summary>
    public static TheoryData<int> BlendQuadCounts =>
        [.. Enumerable.Range(0, TerrainVertexLayout.MaxBlendQuads + 1)];

    /// <summary>Widths that carry weights — the ones a colour PSO is ever built for.</summary>
    public static TheoryData<int> ColorBlendQuadCounts =>
        [.. Enumerable.Range(1, TerrainVertexLayout.MaxBlendQuads)];

    /// <summary>
    ///     Bytes per element for the formats this layout uses. Written out rather than resolved
    ///     through a production helper, so the tiling checks below are against independently known
    ///     sizes.
    /// </summary>
    private static int FormatBytes(Format format) => format switch
    {
        Format.R32G32B32A32_Float => 16,
        Format.R16G16B16A16_UNorm => 8,
        Format.R32G32B32_Float => 12,
        Format.R16G16_SNorm => 4,
        Format.R32_Float => 4,
        Format.R8G8B8A8_UNorm => 4,
        _ => throw new NotSupportedException($"Unexpected terrain input format {format}.")
    };

    [Theory]
    [MemberData(nameof(BlendQuadCounts))]
    public void The_geometry_stream_tiles_the_terrain_vertex_with_no_gap_or_overlap(int blendQuadCount)
    {
        var elements = TerrainVertexLayout.ElementsFor(blendQuadCount)
            .Where(e => e.Slot == TerrainVertexLayout.GeometrySlot)
            .OrderBy(e => e.AlignedByteOffset)
            .ToArray();

        // The geometry half is the same at every width — a variant that dropped or reordered it
        // would still bind, and would still decode something.
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

    [Theory]
    [MemberData(nameof(BlendQuadCounts))]
    public void The_blend_weight_stream_tiles_its_stride_with_no_gap_or_overlap(int blendQuadCount)
    {
        var elements = TerrainVertexLayout.ElementsFor(blendQuadCount)
            .Where(e => e.Slot == TerrainVertexLayout.BlendWeightSlot)
            .OrderBy(e => e.AlignedByteOffset)
            .ToArray();

        Assert.Equal(blendQuadCount, elements.Length);

        var cursor = 0;
        foreach (var element in elements)
        {
            Assert.Equal(cursor, (int)element.AlignedByteOffset);
            cursor += FormatBytes(element.Format);
        }

        // The stride is what the vertex-buffer view is bound with. If it exceeded the elements, the
        // input assembler would step past a vertex's weights into the next vertex's.
        Assert.Equal((int)TerrainVertexLayout.BlendWeightStrideFor(blendQuadCount), cursor);
    }

    [Theory]
    [MemberData(nameof(BlendQuadCounts))]
    public void Every_element_is_a_distinct_texcoord_index(int blendQuadCount)
    {
        // A duplicated semantic silently shadows one attribute with another's data.
        var elements = TerrainVertexLayout.ElementsFor(blendQuadCount);
        var semantics = elements.Select(e => (e.SemanticName, e.SemanticIndex)).ToArray();

        Assert.Equal(semantics.Length, semantics.Distinct().Count());
        Assert.All(elements, e => Assert.Equal("TEXCOORD", e.SemanticName));

        // Contiguous from 0: the weight quads have to start at TEXCOORD3 and count up, because the
        // shader's declarations are fixed names and a gap would leave one of them unsupplied.
        Assert.Equal(
            Enumerable.Range(0, elements.Length).ToArray(),
            elements.Select(e => (int)e.SemanticIndex).Order().ToArray());
    }

    [Fact]
    public void The_narrowest_variants_are_the_point_of_the_exercise()
    {
        // The saving 3d exists for, stated as bytes rather than as a ratio: a cell painted with four
        // or fewer land textures — the common case — carries a quarter of the weight bytes the fixed
        // 16-slot stream sent, and the depth passes carry none.
        Assert.Equal(0u, TerrainVertexLayout.BlendWeightStrideFor(0));
        Assert.Equal(8u, TerrainVertexLayout.BlendWeightStrideFor(1));
        Assert.Equal(32u, TerrainVertexLayout.BlendWeightStrideFor(TerrainVertexLayout.MaxBlendQuads));
    }

    [Fact]
    public void A_width_outside_the_supported_range_is_rejected_rather_than_clamped()
    {
        // Clamping would hand back a layout that does not match the shader the caller is about to
        // pair it with, which is the silent-mis-decode failure this file exists to prevent.
        Assert.Throws<ArgumentOutOfRangeException>(() => TerrainVertexLayout.ElementsFor(-1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TerrainVertexLayout.ElementsFor(TerrainVertexLayout.MaxBlendQuads + 1));
    }

    [Fact]
    public void The_geometry_stream_is_far_narrower_than_the_shared_mesh_vertex_it_replaced()
    {
        // Regression direction: if slot 0 ever widens back past ~24 bytes, someone has re-added a
        // field the terrain shaders do not read, and the residency budget silently loses ground.
        var slotZeroBytes = TerrainVertexLayout.ElementsFor(TerrainVertexLayout.MaxBlendQuads)
            .Where(e => e.Slot == TerrainVertexLayout.GeometrySlot)
            .Sum(e => FormatBytes(e.Format));

        Assert.True(slotZeroBytes <= 16, $"terrain slot 0 grew back to {slotZeroBytes} bytes");
    }

    [Trait("Category", TestCategories.ShaderCompile)]
    [Theory]
    [MemberData(nameof(BlendQuadCounts))]
    public void The_layout_supplies_every_input_the_compiled_vertex_shader_declares(int blendQuadCount)
    {
        // The check a source pin cannot make: reflect the ACTUAL compiled signature. An input the
        // shader declares and the layout omits is the one mismatch D3D12 rejects outright, so this
        // is the difference between a startup failure in the user's hands and a failing test here.
        ShaderCompileTestGuard.SkipUnlessEnabled();

        var bytecode = GpuShaderCompiler12.Compile(
            "terrain_textured.vert.hlsl", "main", "vs_5_1",
            [new ShaderMacro("TERRAIN_BLEND_QUADS", blendQuadCount.ToString(CultureInfo.InvariantCulture))]);
        using var reflection = Compiler.Reflect<ID3D12ShaderReflection>(bytecode);

        var supplied = TerrainVertexLayout.ElementsFor(blendQuadCount)
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

    [Trait("Category", TestCategories.ShaderCompile)]
    [Theory]
    [MemberData(nameof(ColorBlendQuadCounts))]
    public void The_pixel_shader_reads_exactly_what_its_vertex_shader_writes(int blendQuadCount)
    {
        // VSOutput and PSInput are separate declarations behind the same macro. If one gained a
        // guard the other did not, the PSO would fail to link — but only for that one width, which
        // could easily be a width no worldspace on this machine happens to use.
        ShaderCompileTestGuard.SkipUnlessEnabled();

        ShaderMacro[] macros = [new ShaderMacro("TERRAIN_BLEND_QUADS", blendQuadCount.ToString(CultureInfo.InvariantCulture))];
        using var vertex = Compiler.Reflect<ID3D12ShaderReflection>(
            GpuShaderCompiler12.Compile("terrain_textured.vert.hlsl", "main", "vs_5_1", macros));
        using var pixel = Compiler.Reflect<ID3D12ShaderReflection>(
            GpuShaderCompiler12.Compile("terrain_textured.frag.hlsl", "main", "ps_5_1", macros));

        var written = Signature(vertex, output: true);
        var read = Signature(pixel, output: false);

        var unwritten = read.Except(written, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(unwritten.Length == 0,
            $"pixel shader reads varyings the vertex shader never writes: {string.Join(", ", unwritten)}");

        // The weight count itself, so a macro that silently failed to reach one stage is caught even
        // when both stages happen to agree on being wrong in the same direction. TEXCOORD3..6 are
        // the weight varyings; 0-2 and 7 are the normal, colour, UV and world position.
        var weightVaryings = read.Count(s => s is "TEXCOORD3" or "TEXCOORD4" or "TEXCOORD5" or "TEXCOORD6");
        Assert.Equal(blendQuadCount, weightVaryings);
    }

    private static List<string> Signature(ID3D12ShaderReflection reflection, bool output)
    {
        var names = new List<string>();
        var count = output ? reflection.Description.OutputParameters : reflection.Description.InputParameters;
        for (var i = 0u; i < count; i++)
        {
            var parameter = output
                ? reflection.GetOutputParameterDescription(i)
                : reflection.GetInputParameterDescription(i);
            if (parameter.SystemValueType != SystemValueType.Undefined)
            {
                continue;
            }

            names.Add($"{parameter.SemanticName}{parameter.SemanticIndex}");
        }

        return names;
    }
}
