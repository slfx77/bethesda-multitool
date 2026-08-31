using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     Pins the Starfield external-geometry (<c>.mesh</c>) decode. The fixture mirrors the exact shape
///     of a real retail blob — 6 vertices / 2 triangles, version 2, position scale 4.0, both UV sets,
///     no colours, normals + tangents, no weights, no LODs, one meshlet and one cull record — because
///     the trailing meshlet/cull section is absent from the public NifSkope reference and a decoder
///     that stops early still "works" on the attributes while silently mis-reporting its end offset.
/// </summary>
public class StarfieldMeshFileTests
{
    private const float Scale = 4.0f;

    [Fact]
    public void Parse_DecodesAttributesAndConsumesEveryByte()
    {
        var blob = BuildMesh();

        var mesh = StarfieldMeshFile.Parse(blob);

        Assert.NotNull(mesh);
        // Landing on the exact final byte is the real assertion: it proves the field ORDER is right,
        // not merely that each individual reader is self-consistent.
        Assert.Equal(blob.Length, mesh!.BytesConsumed);
        Assert.Equal([0, 1, 2, 3, 4, 5], mesh.Triangles);
        Assert.Equal(6 * 3, mesh.Positions.Length);
        Assert.Equal(6 * 2, mesh.Uvs!.Length);
        Assert.Equal(6 * 2, mesh.Uvs2!.Length);
        Assert.Null(mesh.VertexColors);
        Assert.Equal(6 * 3, mesh.Normals!.Length);
        Assert.Equal(6 * 3, mesh.Tangents!.Length);
        Assert.Equal(0, mesh.WeightsPerVertex);
    }

    [Fact]
    public void Parse_AppliesTheFileScaleToInt16SnormPositions()
    {
        // 16383 ≈ half of the 32767 SNORM range, so the decoded value is ~half the file scale.
        var blob = BuildMesh(firstVertex: (16383, -16384, 0));

        var mesh = StarfieldMeshFile.Parse(blob);

        Assert.NotNull(mesh);
        Assert.Equal(Scale * 16383f / 32767f, mesh!.Positions[0], 4);
        Assert.Equal(Scale * -16384f / 32767f, mesh.Positions[1], 4);
        Assert.Equal(0f, mesh.Positions[2], 5);
    }

    /// <summary>
    ///     10/10/10/2 channels are unsigned [0,1023] mapped onto [-1,1]. 1023 → +1, 0 → -1, 512 → ~0.
    /// </summary>
    [Fact]
    public void Parse_UnpacksDec4NormalsToTheSignedRange()
    {
        var packed = 1023u | (0u << 10) | (512u << 20);
        var blob = BuildMesh(firstNormal: packed);

        var mesh = StarfieldMeshFile.Parse(blob);

        Assert.NotNull(mesh);
        Assert.Equal(1f, mesh!.Normals![0], 3);
        Assert.Equal(-1f, mesh.Normals[1], 3);
        Assert.Equal(0.001f, mesh.Normals[2], 2);
    }

    [Theory]
    [InlineData(3u)] // version above the known maximum
    [InlineData(99u)]
    public void Parse_RejectsUnknownVersion(uint version)
    {
        Assert.Null(StarfieldMeshFile.Parse(BuildMesh(version)));
    }

    [Fact]
    public void Parse_RejectsNonPositiveScale()
    {
        // The format itself treats scale <= 0 as invalid, so a zero here is a corrupt blob, not a
        // degenerate-but-drawable one.
        Assert.Null(StarfieldMeshFile.Parse(BuildMesh(scale: 0f)));
    }

    [Fact]
    public void Parse_RejectsTruncatedBlobWithoutThrowing()
    {
        var blob = BuildMesh();
        for (var cut = 1; cut < blob.Length; cut += 7)
        {
            Assert.Null(StarfieldMeshFile.Parse(blob.AsSpan(0, cut)));
        }
    }

    /// <summary>
    ///     Counts come straight off disk and are unvalidated. A count near uint.MaxValue must return
    ///     null, never overflow an int multiply into a small positive length or throw OutOfMemory.
    /// </summary>
    [Fact]
    public void Parse_RejectsAbsurdCountsWithoutThrowingOrAllocating()
    {
        var blob = BuildMesh();
        // Overwrite the index count (dword at offset 4) with a value that would overflow a naive
        // int multiply by the 2-byte element size.
        BitConverter.GetBytes(0xFFFF_FFF0u).CopyTo(blob, 4);

        Assert.Null(StarfieldMeshFile.Parse(blob));
    }

    [Fact]
    public void ExternalBsGeometry_UsesOnlySupportedMaterialColorComposition()
    {
        byte[] decodedRgba =
        [
            10, 20, 30, 40,
            50, 60, 70, 80,
            90, 100, 110, 120,
            130, 140, 150, 160,
            170, 180, 190, 200,
            210, 220, 230, 240
        ];
        var meshBlob = BuildMesh(vertexColors: decodedRgba);
        var (nifData, nif) = BuildExternalBsGeometry(@"test\colourmesh");

        RenderableSubmesh Extract(StarfieldMaterialColorPolicy policy)
        {
            return Assert.IsType<RenderableSubmesh>(NifBlockParsers.ExtractSubmesh(
                nifData,
                nif,
                shapeIndex: 0,
                dataIndex: 0,
                worldTransforms: new Dictionary<int, Matrix4x4>(),
                externalMeshLoader: path => path == @"test\colourmesh" ? meshBlob : null,
                starfieldColorPolicy: policy));
        }

        // ParamBool selects the decoded stream as tint, and Multiply is exactly the renderer's
        // sample.rgb * vertexColor.rgb lane.
        var decodedMultiply = Extract(new StarfieldMaterialColorPolicy(
            true, true, StarfieldMaterialColorOverrideMode.Multiply, 0x00FFFFFFu));
        Assert.True(decodedMultiply.UseVertexColors);
        Assert.False(decodedMultiply.UseVertexAlphaForOpacity);
        Assert.Equal(
            decodedRgba.Select((value, index) => index % 4 == 3 ? byte.MaxValue : value).ToArray(),
            decodedMultiply.VertexColors);

        // With vertex tint disabled, constant Multiply is representable by repeating the authored
        // RGB through that same lane after the reference renderer's sRGB expansion. Material alpha
        // is ignored by Multiply, and the external mesh's unrelated bytes are not used.
        var constantMultiply = Extract(new StarfieldMaterialColorPolicy(
            true,
            false,
            StarfieldMaterialColorOverrideMode.Multiply,
            new Vector4(0.25f, 0.5f, 0.75f, 0.5f)));
        Assert.True(constantMultiply.UseVertexColors);
        Assert.False(constantMultiply.UseVertexAlphaForOpacity);
        Assert.Equal(
            Enumerable.Repeat(new byte[] { 13, 55, 133, 255 }, 6).SelectMany(x => x).ToArray(),
            constantMultiply.VertexColors);
        Assert.Equal(default(StarfieldMaterialColorRenderState),
            constantMultiply.StarfieldMaterialColor);

        // Constant Lerp uses a distinct persistent operation because affine composition cannot be
        // baked into the multiplicative vertex lane. RGB expands exactly once; alpha remains the
        // material blend weight and never becomes vertex/output opacity.
        var constantLerpPolicy = new StarfieldMaterialColorPolicy(
            true,
            false,
            StarfieldMaterialColorOverrideMode.Lerp,
            new Vector4(0.25f, 0.5f, 0.75f, 0.4f));
        Assert.True(constantLerpPolicy.TryResolveConstantLerp(out var expectedLinearTint));
        var constantLerp = Extract(constantLerpPolicy);
        Assert.False(constantLerp.UseVertexColors);
        Assert.False(constantLerp.UseVertexAlphaForOpacity);
        Assert.Null(constantLerp.VertexColors);
        Assert.Equal(StarfieldMaterialColorRenderMode.ConstantLerp,
            constantLerp.StarfieldMaterialColor.Mode);
        Assert.Equal(expectedLinearTint, constantLerp.StarfieldMaterialColor.LinearTint);

        // Vertex-driven Lerp needs a separate per-vertex mask and remains fail-closed. Unresolved
        // policy does too: neither may accidentally inherit the constant operation above.
        foreach (var unsupported in new[]
                 {
                     new StarfieldMaterialColorPolicy(
                         true, true, StarfieldMaterialColorOverrideMode.Lerp, 0x00FFFFFFu),
                     default
                 })
        {
            var withheld = Extract(unsupported);
            Assert.False(withheld.UseVertexColors);
            Assert.Null(withheld.VertexColors);
            Assert.Equal(default(StarfieldMaterialColorRenderState),
                withheld.StarfieldMaterialColor);
        }
    }

    private static byte[] BuildMesh(
        uint version = 2,
        float scale = Scale,
        (short X, short Y, short Z)? firstVertex = null,
        uint? firstNormal = null,
        byte[]? vertexColors = null)
    {
        const int vertexCount = 6;
        var w = new List<byte>();

        w.AddRange(BitConverter.GetBytes(version));
        w.AddRange(BitConverter.GetBytes(6u)); // index count
        for (ushort i = 0; i < 6; i++) w.AddRange(BitConverter.GetBytes(i));
        w.AddRange(BitConverter.GetBytes(scale));
        w.AddRange(BitConverter.GetBytes(0u)); // weights per vertex

        w.AddRange(BitConverter.GetBytes((uint)vertexCount));
        for (var i = 0; i < vertexCount; i++)
        {
            var (x, y, z) = i == 0 && firstVertex is { } v ? v : ((short)(i * 100), (short)(i * -50), (short)i);
            w.AddRange(BitConverter.GetBytes((uint)((ushort)x | ((ushort)y << 16))));
            w.AddRange(BitConverter.GetBytes((ushort)z));
        }

        AddHalfPairs(w, vertexCount); // UV0
        AddHalfPairs(w, vertexCount); // UV1
        if (vertexColors is null)
        {
            w.AddRange(BitConverter.GetBytes(0u)); // no vertex colours
        }
        else
        {
            Assert.Equal(vertexCount * 4, vertexColors.Length);
            w.AddRange(BitConverter.GetBytes((uint)vertexCount));
            for (var offset = 0; offset < vertexColors.Length; offset += 4)
            {
                var bgra = (uint)(vertexColors[offset + 2]
                                  | (vertexColors[offset + 1] << 8)
                                  | (vertexColors[offset] << 16)
                                  | (vertexColors[offset + 3] << 24));
                w.AddRange(BitConverter.GetBytes(bgra));
            }
        }

        AddDec4(w, vertexCount, firstNormal); // normals
        AddDec4(w, vertexCount, null); // tangents
        w.AddRange(BitConverter.GetBytes(0u)); // no skin weights
        w.AddRange(BitConverter.GetBytes(0u)); // no LODs

        w.AddRange(BitConverter.GetBytes(1u)); // one meshlet…
        foreach (var field in new uint[] { vertexCount, 0, 2, 0 }) w.AddRange(BitConverter.GetBytes(field));
        w.AddRange(BitConverter.GetBytes(1u)); // …and one cull record (centre + extent)
        foreach (var f in new[] { 0f, 0f, 0f, 1f, 1f, 1f }) w.AddRange(BitConverter.GetBytes(f));

        return [.. w];
    }

    private static (byte[] Data, NifInfo Nif) BuildExternalBsGeometry(string meshPath)
    {
        var w = new List<byte>();
        w.AddRange(BitConverter.GetBytes(-1)); // NiObjectNET name string-table index
        w.AddRange(BitConverter.GetBytes(0u)); // no extra-data refs
        w.AddRange(BitConverter.GetBytes(-1)); // no controller
        w.AddRange(BitConverter.GetBytes(0u)); // NiAVObject flags: external mesh data
        w.AddRange(new byte[12 + 36]); // translation + rotation (unused by this direct extraction)
        w.AddRange(BitConverter.GetBytes(1f)); // scale
        w.AddRange(BitConverter.GetBytes(-1)); // no collision object
        w.AddRange(new byte[16 + 24]); // bounding sphere + box
        w.AddRange(BitConverter.GetBytes(-1)); // no skin
        w.AddRange(BitConverter.GetBytes(-1)); // no shader block in this focused fixture
        w.AddRange(BitConverter.GetBytes(-1)); // no alpha block
        w.Add(1); // LOD 0 has a mesh
        w.AddRange(new byte[12]); // indices size + vertex count + mesh flags
        var pathBytes = Encoding.ASCII.GetBytes(meshPath);
        w.AddRange(BitConverter.GetBytes((uint)pathBytes.Length));
        w.AddRange(pathBytes);

        var data = w.ToArray();
        var nif = new NifInfo
        {
            BinaryVersion = 0x14020007,
            BsVersion = 173,
            BlockCount = 1
        };
        nif.Blocks.Add(new BlockInfo
        {
            Index = 0,
            TypeIndex = 0,
            TypeName = "BSGeometry",
            Size = data.Length,
            DataOffset = 0
        });
        nif.BlockTypeNames.Add("BSGeometry");
        return (data, nif);
    }

    private static void AddHalfPairs(List<byte> w, int count)
    {
        w.AddRange(BitConverter.GetBytes((uint)count));
        for (var i = 0; i < count; i++)
        {
            var u = BitConverter.HalfToUInt16Bits((Half)(i * 0.25f));
            var v = BitConverter.HalfToUInt16Bits((Half)(i * 0.5f));
            w.AddRange(BitConverter.GetBytes((uint)(u | (v << 16))));
        }
    }

    private static void AddDec4(List<byte> w, int count, uint? first)
    {
        w.AddRange(BitConverter.GetBytes((uint)count));
        for (var i = 0; i < count; i++)
        {
            w.AddRange(BitConverter.GetBytes(i == 0 && first is { } f ? f : 512u | (512u << 10) | (1023u << 20)));
        }
    }
}
