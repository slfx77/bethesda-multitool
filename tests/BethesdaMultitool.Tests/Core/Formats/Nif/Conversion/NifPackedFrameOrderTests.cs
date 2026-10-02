using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.GeometryAnalysis;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Conversion;

/// <summary>
///     The legacy Xbox-to-PC converter's tangent-frame order on hand-laid packed blocks: the LOWER-offset frame stream
///     is the PC second array, nif.xml "Bitangents", and the higher-offset stream the PC first array, "Tangents"
///     (<c>NifPackedGeometryLayout</c>'s Bitangent and Tangent channels; measured 2026-09-28 against the PC file of every
///     X360 packed shape, TestOutput/nif-tangent-frame-20260928/converter). Before that fix the extractor named them the
///     other way round and NifGeometryWriter wrote the lower-offset stream first, so a converted file's first array ran
///     along +dP/du where retail PC's runs along +dP/dv.
/// </summary>
/// <remarks>
///     The blocks are laid out here from the measured layouts (stride, stream types and offsets), never from the
///     production layout table. Each vertex carries different values in the two frame channels, so the swapped naming
///     fails every assertion on every vertex (asserted as the control). The retail comparison of converted files with
///     their PC twins is <see cref="NifConverterFrameOrderRetailTests" /> (Bucket B).
/// </remarks>
public sealed class NifPackedFrameOrderTests
{
    private const uint Half4 = 16;
    private const uint Half2 = 14;
    private const uint Byte4 = 28;

    private static readonly float[] Normals =
    [
        0f, 0f, 1f,
        0.7071f, 0f, 0.7071f,
        0f, 0.6f, 0.8f,
        -0.5773f, -0.5773f, 0.5773f
    ];

    /// <summary>The lower-offset frame channel (retail: the PC "Bitangents", along +dP/du).</summary>
    private static readonly float[] LowerChannel =
    [
        0f, 1f, 0f,
        0f, 1f, 0f,
        0f, -0.8f, 0.6f,
        0f, 0.7071f, 0.7071f
    ];

    /// <summary>The higher-offset frame channel (retail: the PC "Tangents", along +dP/dv).</summary>
    private static readonly float[] HigherChannel =
    [
        1f, 0f, 0f,
        0.7071f, 0f, -0.7071f,
        1f, 0f, 0f,
        0.8165f, -0.4082f, 0.4082f
    ];

    public static TheoryData<string> Layouts => ["L1", "L2", "L3", "L4"];

    public static TheoryData<string> StaticLayouts => ["L1", "L2"];

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Extract_NamesTheLowerOffsetFrameStreamBitangents(string layout)
    {
        var packed = Extract(Layout(layout), LowerChannel, HigherChannel);

        AssertRounded(Normals, packed.Normals, "normals");
        AssertRounded(LowerChannel, packed.Bitangents, "Bitangents (the lower-offset stream)");
        AssertRounded(HigherChannel, packed.Tangents, "Tangents (the higher-offset stream)");

        // Control: the two channels differ on every vertex, so the previous naming (lower = Tangents) fails the two
        // assertions above on every vertex.
        AssertDifferOnEveryVertex(LowerChannel, HigherChannel);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Extract_ReadsATangentChannelTheUnitLengthSampleRejected(string layout)
    {
        // Vertex 0's higher-offset vector is short, so the sample of the first vertices averages 0.81 and rejects the
        // stream (one retail X360 shape does this, vhallsm1waywinr01.nif shape 10). The stream is still read, from
        // its channel, and keeps its stored values.
        var shortHigher = (float[])HigherChannel.Clone();
        shortHigher[0] = 0.25f;
        var packed = Extract(Layout(layout), LowerChannel, shortHigher);

        AssertRounded(LowerChannel, packed.Bitangents, "Bitangents (the lower-offset stream)");
        AssertRounded(shortHigher, packed.Tangents, "Tangents (the rejected higher-offset stream, read from its channel)");
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Extract_ReadsABitangentChannelTheUnitLengthSampleRejected(string layout)
    {
        var shortLower = (float[])LowerChannel.Clone();
        shortLower[1] = 0.25f;
        var packed = Extract(Layout(layout), shortLower, HigherChannel);

        AssertRounded(shortLower, packed.Bitangents, "Bitangents (the rejected lower-offset stream, read from its channel)");
        AssertRounded(HigherChannel, packed.Tangents, "Tangents (the higher-offset stream)");
    }

    [Fact]
    public void Extract_WithoutAPartnerChannel_CompletesTheTangentsFromTheBitangents()
    {
        // A table with one frame channel: the stream is the Bitangents and the Tangents are completed with the retail
        // relation of the majority handedness, T = cross(B, N).
        var packed = Extract(new PackedLayout(28, [(Half4, 0), (Half4, 8), (Half2, 16), (Half4, 20)], 8, 20, null, null),
            LowerChannel, null);

        AssertRounded(LowerChannel, packed.Bitangents, "Bitangents (the only frame stream)");
        Assert.NotNull(packed.Tangents);
        for (var v = 0; v < 4; v++)
        {
            var b = RoundedVector(LowerChannel, v);
            var n = RoundedVector(Normals, v);
            float[] expected = [b[1] * n[2] - b[2] * n[1], b[2] * n[0] - b[0] * n[2], b[0] * n[1] - b[1] * n[0]];
            for (var c = 0; c < 3; c++)
            {
                Assert.True(Math.Abs(expected[c] - packed.Tangents[v * 3 + c]) <= 1e-6f,
                    $"vertex {v} component {c}: Tangents {packed.Tangents[v * 3 + c]} is not cross(B, N) {expected[c]}");
            }
        }

        // Control: the stored stream is not written as the first array.
        Assert.NotEqual(RoundedVector(LowerChannel, 0), packed.Tangents[..3]);
    }

    [Theory]
    [MemberData(nameof(StaticLayouts))]
    public void Convert_WritesTheStoredFrameArraysInRetailOrder(string layout)
    {
        var data = NifPackedFixture.Quad(layout == "L1" ? NifPackedFixture.QuadColors : null);
        var result = NifConverter.Convert(new NifPackedFixture { Layout = layout, Data = data }.Build());

        Assert.True(result.Success, result.ErrorMessage);
        var shape = Assert.Single(NifStoredFrameArrays.Read(Assert.IsType<byte[]>(result.OutputData)));
        Assert.Equal(data.VertexCount, shape.VertexCount);
        Assert.NotNull(shape.Tangents);
        Assert.NotNull(shape.Bitangents);
        for (var v = 0; v < data.VertexCount; v++)
        {
            for (var c = 0; c < 3; c++)
            {
                Assert.Equal((float)(Half)data.Tangents[v * 3 + c], shape.Tangents.Get(v, c));
                Assert.Equal((float)(Half)data.Bitangents[v * 3 + c], shape.Bitangents.Get(v, c));
            }
        }

        // Control: the fixture's two arrays differ on every vertex, so the swapped order fails on every vertex.
        AssertDifferOnEveryVertex(data.Tangents, data.Bitangents);
    }

    private static PackedLayout Layout(string id)
    {
        return id switch
        {
            "L1" => new PackedLayout(40, [(Half4, 0), (Half4, 8), (Byte4, 16), (Half2, 20), (Half4, 24), (Half4, 32)],
                8, 24, 32, null),
            "L2" => new PackedLayout(36, [(Half4, 0), (Half4, 8), (Half2, 16), (Half4, 20), (Half4, 28)], 8, 20, 28,
                null),
            "L3" => new PackedLayout(48,
                [(Half4, 0), (Half4, 8), (Byte4, 16), (Half4, 20), (Half2, 28), (Half4, 32), (Half4, 40)], 20, 32, 40,
                8),
            "L4" => new PackedLayout(52,
                [(Half4, 0), (Half4, 8), (Byte4, 16), (Half4, 20), (Byte4, 28), (Half2, 32), (Half4, 36), (Half4, 44)],
                20, 36, 44, 8),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "not a half-precision layout")
        };
    }

    private static PackedGeometryData Extract(PackedLayout layout, float[] lower, float[]? higher)
    {
        var block = PackedBlock(layout, lower, higher);
        var packed = NifPackedDataExtractor.Extract(block, 0, block.Length, true);
        Assert.NotNull(packed);
        Assert.Equal(4, packed.NumVertices);
        return packed;
    }

    /// <summary>
    ///     A big-endian BSPackedAdditionalGeometryData body: the vertex count, the stream table (type, unit size, total
    ///     size, stride, block index 0, offset, flags 0), then one data block holding every vertex at the layout's stride.
    ///     Positions are small halves, the skinned layouts' weights are (1, 0, 0, 0), bytes and UVs are zero.
    /// </summary>
    private static byte[] PackedBlock(PackedLayout layout, float[] lower, float[]? higher)
    {
        const int count = 4;
        var vertices = new byte[count * layout.Stride];
        for (var v = 0; v < count; v++)
        {
            var at = v * layout.Stride;
            WriteHalf4(vertices, at, 0.5f * v, 0.25f, -1f);
            WriteHalf4(vertices, at + layout.Normal, Normals[v * 3], Normals[v * 3 + 1], Normals[v * 3 + 2]);
            WriteHalf4(vertices, at + layout.Lower, lower[v * 3], lower[v * 3 + 1], lower[v * 3 + 2]);
            if (layout.Higher is { } higherOffset && higher is not null)
            {
                WriteHalf4(vertices, at + higherOffset, higher[v * 3], higher[v * 3 + 1], higher[v * 3 + 2]);
            }

            if (layout.Weights is { } weights)
            {
                WriteHalf4(vertices, at + weights, 1f, 0f, 0f, 0f);
            }
        }

        var block = new List<byte>();
        AddU16(block, count);
        AddU32(block, (uint)layout.Streams.Length);
        foreach (var (type, offset) in layout.Streams)
        {
            var unit = type == Half4 ? 8u : 4u;
            AddU32(block, type);
            AddU32(block, unit);
            AddU32(block, unit * count);
            AddU32(block, (uint)layout.Stride);
            AddU32(block, 0);
            AddU32(block, (uint)offset);
            block.Add(0);
        }

        AddU32(block, 1); // data blocks
        block.Add(1); // has data
        AddU32(block, (uint)vertices.Length); // block size
        AddU32(block, 1); // inner blocks
        AddU32(block, 0); // inner block offset
        AddU32(block, 1); // data sizes
        AddU32(block, (uint)vertices.Length);
        block.AddRange(vertices);
        AddU32(block, 0); // shader index
        AddU32(block, (uint)vertices.Length); // total size
        return block.ToArray();
    }

    private static void WriteHalf4(byte[] buffer, int at, float x, float y, float z, float w = 1f)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(at), BitConverter.HalfToUInt16Bits((Half)x));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(at + 2), BitConverter.HalfToUInt16Bits((Half)y));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(at + 4), BitConverter.HalfToUInt16Bits((Half)z));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(at + 6), BitConverter.HalfToUInt16Bits((Half)w));
    }

    private static void AddU16(List<byte> block, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        block.AddRange(bytes.ToArray());
    }

    private static void AddU32(List<byte> block, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        block.AddRange(bytes.ToArray());
    }

    private static float[] RoundedVector(float[] values, int vertex)
    {
        return [(float)(Half)values[vertex * 3], (float)(Half)values[vertex * 3 + 1], (float)(Half)values[vertex * 3 + 2]];
    }

    /// <summary>The extracted array equals the source rounded to binary16 (the console precision), bit for bit.</summary>
    private static void AssertRounded(float[] source, float[]? extracted, string what)
    {
        Assert.NotNull(extracted);
        Assert.Equal(source.Length, extracted.Length);
        for (var i = 0; i < source.Length; i++)
        {
            Assert.True((float)(Half)source[i] == extracted[i],
                $"{what}: component {i} is {extracted[i]}, expected {(float)(Half)source[i]}");
        }
    }

    private static void AssertDifferOnEveryVertex(float[] a, float[] b)
    {
        for (var v = 0; v < a.Length / 3; v++)
        {
            Assert.NotEqual(RoundedVector(a, v), RoundedVector(b, v));
        }
    }

    /// <summary>A hand-laid packed layout: stride, streams (type, offset), and the channel offsets the test writes.</summary>
    private sealed record PackedLayout(
        int Stride,
        (uint Type, int Offset)[] Streams,
        int Normal,
        int Lower,
        int? Higher,
        int? Weights);
}
