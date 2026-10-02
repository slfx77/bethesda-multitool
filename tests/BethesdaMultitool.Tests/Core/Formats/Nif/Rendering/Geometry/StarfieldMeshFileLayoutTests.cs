using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     Cut-2 slice 0 (plan <c>docs/design/cut2-starfield-mesh-reader-plan-20260928.md</c>, sections 1 and 6): the
///     decoder's optional meshlet tail, its raw members and section walk, and the two numeric routes, against the
///     independent writer <see cref="StarfieldMeshTestBuilder" /> and the integer-only rounding of
///     <see cref="ExactFloat32" />. <see cref="StarfieldMeshFileTests" /> keeps pinning the legacy contract unchanged.
/// </summary>
public class StarfieldMeshFileLayoutTests
{
    [Fact]
    public void Decode_ATailLessStreamParsesToItsLastByte()
    {
        var bytes = StarfieldMeshTestBuilder.Quad(tail: false).Build();

        var mesh = StarfieldMeshFile.Parse(bytes);

        Assert.NotNull(mesh);
        Assert.False(mesh.HasMeshletTail);
        Assert.Equal(bytes.Length, mesh.BytesConsumed);
        Assert.Empty(mesh.Meshlets);
        Assert.Empty(mesh.CullRecords);
        Assert.DoesNotContain(mesh.Sections, section => section.Name is "meshlets" or "cull");

        // Control: bytes after the LOD section are read as the tail, so three stray bytes (a truncated meshlet count)
        // fail the parse instead of being accepted as a tail-less stream.
        Assert.Null(StarfieldMeshFile.Parse(bytes.Concat(new byte[3]).ToArray()));
    }

    [Fact]
    public void Decode_ACutAtTheTailBoundaryParsesAsTailLess_TheLegacyContract()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        var bytes = builder.Build();
        var boundary = builder.TailOffset();

        var cut = StarfieldMeshFile.Parse(bytes.AsSpan(0, boundary));

        // The decoder does not read normal W, so this truncation parses; the model reader refuses it by that rule.
        Assert.NotNull(cut);
        Assert.False(cut.HasMeshletTail);
        Assert.Equal(boundary, cut.BytesConsumed);
        // Control: one byte earlier the LOD section itself is cut and the parse fails.
        Assert.Null(StarfieldMeshFile.Parse(bytes.AsSpan(0, boundary - 1)));
        Assert.True(StarfieldMeshFile.Parse(bytes)!.HasMeshletTail);
    }

    [Fact]
    public void Decode_RawMembersEqualTheWrittenValues()
    {
        var builder = FullBuilder();
        var bytes = builder.Build();

        var mesh = StarfieldMeshFile.Parse(bytes);

        Assert.NotNull(mesh);
        Assert.Equal(2u, mesh.Version);
        Assert.Equal((uint)builder.Indices.Length, mesh.IndexCount);
        Assert.Equal(builder.Indices, mesh.Indices);
        Assert.Equal(BitConverter.SingleToUInt32Bits(builder.Scale), BitConverter.SingleToUInt32Bits(mesh.Scale));
        Assert.Equal(builder.Positions.SelectMany(p => new[] { p.X, p.Y, p.Z }), mesh.QuantizedPositions);
        Assert.Equal(builder.Uv0!.SelectMany(p => new[] { p.U, p.V }), mesh.Uv0Bits);
        Assert.Equal(builder.Uv1!.SelectMany(p => new[] { p.U, p.V }), mesh.Uv1Bits);
        Assert.Equal(builder.Colors!.SelectMany(c => new[] { c.B, c.G, c.R, c.A }), mesh.ColorBytes);
        Assert.Equal(builder.Normals, mesh.NormalCodes);
        Assert.Equal(builder.Tangents, mesh.TangentCodes);
        Assert.Equal(builder.Weights!.SelectMany(p => new[] { p.Bone, p.Weight }), mesh.WeightPairs);
        Assert.Equal((int)builder.WeightsPerVertex, mesh.WeightsPerVertex);
        Assert.Equal(builder.Lods.Length, mesh.LodIndexLists.Count);
        for (var lod = 0; lod < builder.Lods.Length; lod++)
        {
            Assert.Equal(builder.Lods[lod], mesh.LodIndexLists[lod]);
        }

        Assert.True(mesh.HasMeshletTail);
        Assert.Equal(builder.Meshlets.SelectMany(m => new[] { m.VertexCount, m.VertexOffset, m.TriangleCount, m.TriangleOffset }),
            mesh.Meshlets);
        Assert.Equal(builder.Cull.SelectMany(static r => r).Select(BitConverter.SingleToUInt32Bits),
            mesh.CullRecords.Select(BitConverter.SingleToUInt32Bits));

        // Control: one weight byte changed in the written stream is seen in the raw member.
        var weights = mesh.Sections.Single(s => s.Name == "weights");
        var changed = (byte[])bytes.Clone();
        changed[weights.Offset + 4 + 2] ^= 0x01;
        var reparsed = StarfieldMeshFile.Parse(changed);
        Assert.NotNull(reparsed);
        Assert.NotEqual(mesh.WeightPairs, reparsed.WeightPairs);
        Assert.Equal(mesh.WeightPairs![1] ^ 0x01, reparsed.WeightPairs![1]);
    }

    public static TheoryData<string> Layouts()
    {
        return new TheoryData<string> { "full", "version0", "version1", "tailless", "lod-empty" };
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Decode_SectionsMatchTheWritersBookkeeping(string layout)
    {
        var builder = LayoutBuilder(layout);
        var (bytes, written) = builder.BuildWithLayout();

        var mesh = StarfieldMeshFile.Parse(bytes);

        Assert.NotNull(mesh);
        Assert.Equal(written, mesh.Sections.Select(s => (s.Name, s.Offset, s.Length, s.Count)));
        Assert.Equal(bytes.Length, mesh.Sections[^1].Offset + mesh.Sections[^1].Length);
    }

    [Fact]
    public void Decode_VersionZeroHasNoLodSection_AndRelabellingItBreaksTheWalk()
    {
        var builder = StarfieldMeshTestBuilder.Quad(version: 0);
        var bytes = builder.Build();

        var mesh = StarfieldMeshFile.Parse(bytes);

        Assert.NotNull(mesh);
        Assert.Empty(mesh.LodIndexLists);
        Assert.DoesNotContain(mesh.Sections, section => section.Name == "lods");
        Assert.True(mesh.HasMeshletTail);
        Assert.Equal(bytes.Length, mesh.BytesConsumed);

        // Control: the same bytes labelled version 1 make the decoder read the meshlet count as a LOD count, so the
        // walk no longer reproduces the writer's sections (it fails or lands elsewhere).
        var relabelled = (byte[])bytes.Clone();
        relabelled[0] = 1;
        var wrong = StarfieldMeshFile.Parse(relabelled);
        Assert.True(wrong is null || wrong.BytesConsumed != bytes.Length || wrong.Meshlets.Length != mesh.Meshlets.Length);
    }

    [Fact]
    public void Decode_ReportsTheFieldAndOffsetOfAFailure()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        var (bytes, written) = builder.BuildWithLayout();
        var normals = written.Single(s => s.Name == "normals");

        Assert.Null(StarfieldMeshFile.Decode(bytes.AsSpan(0, normals.Offset + 6), out var failure));

        Assert.NotNull(failure);
        Assert.Equal("normals", failure.Field);
        Assert.Equal(normals.Offset, failure.Offset);
        // Control: the whole stream reports no failure.
        Assert.NotNull(StarfieldMeshFile.Decode(bytes, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Dec4Channel_IsCorrectlyRoundedOnEveryCode_AndTheLegacyRouteIsNot()
    {
        var legacyDiffers = 0;
        for (var v = 0u; v < 1024; v++)
        {
            var exact = ExactFloat32.Round(new BigInteger(2 * (int)v - 1023), 1023);
            Assert.Equal(BitConverter.SingleToUInt32Bits(exact),
                BitConverter.SingleToUInt32Bits(StarfieldMeshFile.Dec4Channel(v)));
            var legacy = v / 511.5f - 1f;
            if (BitConverter.SingleToUInt32Bits(legacy) != BitConverter.SingleToUInt32Bits(exact))
            {
                legacyDiffers++;
            }
        }

        // Control: the legacy float32 route (StarfieldMeshFile.Normals) differs on 556 codes (receipt
        // plan_measure_dec4.json), so a comparison against the exact table can tell the two routes apart.
        Assert.Equal(556, legacyDiffers);
        // Only the low 10 bits are read: code 511 is the zero sentinel, -1/1023, not zero.
        Assert.Equal(StarfieldMeshFile.Dec4Channel(511), StarfieldMeshFile.Dec4Channel(511u | (1u << 10)));
        Assert.Equal(-1f / 1023f, StarfieldMeshFile.Dec4Channel(511), 7);
    }

    [Fact]
    public void PositionMeters_IsCorrectlyRoundedOnEveryStoredValue_AndTheLegacyRouteIsNot()
    {
        var legacyDiffers = 0;
        const float scale = 4f;
        for (var q = short.MinValue; ; q++)
        {
            var exact = ExactFloat32.Round(new BigInteger(q) * 4, 32767);
            Assert.Equal(BitConverter.SingleToUInt32Bits(exact),
                BitConverter.SingleToUInt32Bits(StarfieldMeshFile.PositionMeters(q, scale)));
            var legacy = q * (scale / 32767f);
            if (BitConverter.SingleToUInt32Bits(legacy) != BitConverter.SingleToUInt32Bits(exact))
            {
                legacyDiffers++;
            }

            if (q == short.MaxValue)
            {
                break;
            }
        }

        // Control: the legacy two-rounding product differs on 1,536 of the 65,536 values at a power-of-two scale
        // (measured by the cut-2 oracle's numpy route, which the retail census reproduces at 5.99% of components).
        Assert.Equal(1536, legacyDiffers);
    }

    [Theory]
    [InlineData(25f)]
    [InlineData(8.120366096496582f)]
    [InlineData(262144f)]
    public void PositionMeters_IsCorrectlyRoundedAtTheRetailScalesThatAreNotSmall(float scale)
    {
        // The scale is a float32; its exact rational value is its significand over a power of two.
        var bits = BitConverter.SingleToUInt32Bits(scale);
        var exponent = (int)((bits >> 23) & 0xFF) - 127 - 23;
        var significand = new BigInteger((bits & 0x7FFFFF) | 0x800000);
        for (var q = -32768; q <= 32767; q += 7)
        {
            var numerator = exponent >= 0 ? q * significand << exponent : q * significand;
            var denominator = exponent >= 0 ? new BigInteger(32767) : new BigInteger(32767) << -exponent;
            Assert.Equal(BitConverter.SingleToUInt32Bits(ExactFloat32.Round(numerator, denominator)),
                BitConverter.SingleToUInt32Bits(StarfieldMeshFile.PositionMeters((short)q, scale)));
        }
    }

    [Fact]
    public void LegacyMembers_KeepTheRendererRouteBitForBit()
    {
        var mesh = StarfieldMeshFile.Parse(StarfieldMeshTestBuilder.Quad().Build());

        Assert.NotNull(mesh);
        // Pinned from the numpy float32 route (q * (scale / 32767f), v / 511.5f - 1f); the renderer's disk cache holds
        // these values, so a change here needs a DecoderVersion bump.
        Assert.Equal(0xBF9C4138u, BitConverter.SingleToUInt32Bits(mesh.Positions[0])); // q = -10000
        Assert.Equal(0xBF9C4138u, BitConverter.SingleToUInt32Bits(mesh.Positions[1]));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(mesh.Positions[2]));
        Assert.Equal(0x401C4138u, BitConverter.SingleToUInt32Bits(mesh.Positions[3])); // q = 20000
        Assert.Equal(0x3A802000u, BitConverter.SingleToUInt32Bits(mesh.Normals![0])); // code 512
        Assert.Equal(0x3F800000u, BitConverter.SingleToUInt32Bits(mesh.Normals[2])); // code 1023
        Assert.Equal(0x3F800000u, BitConverter.SingleToUInt32Bits(mesh.Tangents![0]));
        Assert.Equal(1f, mesh.BitangentSigns![0]);
        Assert.Equal(new[] { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f }, mesh.Uvs);

        // Control: the one-rounding route differs from the legacy member on exactly these components.
        Assert.Equal(0x401C4139u, BitConverter.SingleToUInt32Bits(StarfieldMeshFile.PositionMeters(20000, 4f)));
        Assert.Equal(0x3A802008u, BitConverter.SingleToUInt32Bits(StarfieldMeshFile.Dec4Channel(512)));
    }

    /// <summary>A stream with every optional section: UV1, colors, two weights per vertex, two LOD lists, the tail.</summary>
    internal static StarfieldMeshTestBuilder FullBuilder()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        builder.Uv1 = [(0x3800, 0x3400), (0x3C00, 0x0000), (0x7C00, 0x3C00), (0x0000, 0x3800)];
        builder.Colors = [(10, 20, 30, 255), (40, 50, 60, 128), (70, 80, 90, 0), (100, 110, 120, 7)];
        builder.WeightsPerVertex = 2;
        builder.Weights = [(3, 65534), (3, 1), (5, 65535), (0, 0), (7, 40000), (8, 25535), (1, 32768), (2, 32767)];
        builder.Lods = [[0, 1, 2], []];
        builder.Normals![3] = StarfieldMeshTestBuilder.PackDec4(511, 511, 511, 1);
        builder.Tangents![2] = StarfieldMeshTestBuilder.PackDec4(511, 511, 511, 0);
        return builder;
    }

    private static StarfieldMeshTestBuilder LayoutBuilder(string layout)
    {
        return layout switch
        {
            "full" => FullBuilder(),
            "version0" => StarfieldMeshTestBuilder.Quad(version: 0),
            "version1" => WithLods(StarfieldMeshTestBuilder.Quad(version: 1), [[0, 1, 2, 0, 2, 3]]),
            "tailless" => StarfieldMeshTestBuilder.Quad(tail: false),
            "lod-empty" => WithLods(StarfieldMeshTestBuilder.Quad(), [[], [0, 1, 2]]),
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Unknown layout.")
        };
    }

    private static StarfieldMeshTestBuilder WithLods(StarfieldMeshTestBuilder builder, ushort[][] lods)
    {
        builder.Lods = lods;
        return builder;
    }
}
