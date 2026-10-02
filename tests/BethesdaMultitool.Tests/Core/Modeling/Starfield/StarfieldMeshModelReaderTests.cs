using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Core.Modeling.Starfield;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Starfield.StarfieldMeshModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     The reader's document (cut-2 plan section 3, slice 4): geometry by the correctly rounded routes, the tangent sign
///     (glTF's handedness), the reader's refusals, the placeholder material and the provenance, on builder streams with
///     expectations computed independently (<see cref="ExactFloat32" />, the builder's own values, glTF's UV frame in
///     binary64).
/// </summary>
public class StarfieldMeshModelReaderTests
{
    [Fact]
    public void Read_ProducesOneStructurallyValidSceneWithProvenance()
    {
        var bytes = StarfieldMeshTestBuilder.Quad().Build();

        var result = Read(bytes);
        var document = result.Document;

        SceneValidation.ValidateStructure(document);
        Assert.Equal(StarfieldMeshModelFormatMetadata.FormatId, document.SourceFormat);
        Assert.Equal(DefaultName, document.Name);
        var scene = Assert.Single(document.Scenes);
        Assert.Equal(new[] { 0 }, scene.RootNodeIndices);
        var node = Assert.Single(document.Nodes);
        Assert.Equal(0, node.MeshIndex);
        Assert.Equal(SceneNodeRole.Transform, node.Role);
        Assert.Equal(Matrix4x4.Identity, node.LocalTransform);
        Assert.Single(document.Meshes);
        Assert.Empty(document.LayerSets);
        Assert.Empty(document.Images);
        Assert.Empty(document.Skins);
        Assert.Empty(document.Animations);
        Assert.Equal(DefaultPath, document.SourceProvenance!.RelativePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), document.SourceProvenance.Sha256);
        Assert.Contains(document.Diagnostics, d => d.Code == StarfieldMeshModelDiagnostics.MaterialInNif);
    }

    [Fact]
    public void Positions_AreCorrectlyRounded_NotTheLegacyTwoRoundingProduct()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        var bytes = builder.Build();

        var primitive = Primary(Read(bytes).Document);

        for (var i = 0; i < builder.Positions.Length; i++)
        {
            var (x, y, z) = builder.Positions[i];
            var position = primitive.Vertices[i].Position;
            Assert.Equal(Bits(Exact(x)), Bits(position.X));
            Assert.Equal(Bits(Exact(y)), Bits(position.Y));
            Assert.Equal(Bits(Exact(z)), Bits(position.Z));
        }

        // Control: the legacy member (two float32 roundings) differs on vertex 1's X (q = 20000), so the comparison above
        // would fail on a reader that typed the renderer's positions.
        var legacy = StarfieldMeshFile.Parse(bytes)!.Positions;
        Assert.NotEqual(Bits(legacy[3]), Bits(primitive.Vertices[1].Position.X));

        static float Exact(short q)
        {
            return ExactFloat32.Round(new BigInteger(q) * 4, 32767);
        }
    }

    [Fact]
    public void NormalsAndTangents_AreTheDec4Channels_WithTheGltfHandednessInW()
    {
        var primitive = Primary(Read(StarfieldMeshTestBuilder.Quad().Build()).Document);
        var small = ExactFloat32.Round(1, 1023);

        Assert.Equal(SceneNormalMode.Vertex, primitive.NormalMode);
        Assert.Equal(SceneNormalProvenanceKind.Authored, primitive.NormalProvenance!.Kind);
        foreach (var vertex in primitive.Vertices)
        {
            Assert.Equal(new Vector3(small, small, 1f), vertex.Normal);
            Assert.Equal(Vector4.One, vertex.Color);
        }

        // Code 3 is glTF's -1: the stored bitangent sign negated (see the UV-frame test below).
        Assert.Equal(Enumerable.Repeat(new Vector4(1f, small, small, -1f), 4), primitive.Tangents!.Values);
    }

    /// <summary>
    ///     The typed w is glTF's handedness on both W codes: <c>cross(N, T) x w</c> runs along glTF's bitangent, -dP/dv,
    ///     the top of the image for glTF's top-left UV origin (the stored UVs reach TEXCOORD_0 unflipped). The builder
    ///     quad's V runs along +Y (an image mirrored top to bottom in glTF's terms) and stores code 3, so w is -1. The
    ///     control is cover 01's layout: V running against +Y (an upright, unmirrored image) with code 0, so w is +1. A
    ///     constant w fails one of the two, and the engine's reading (+1 for code 3, -1 for code 0) fails both.
    /// </summary>
    [Fact]
    public void TheTangentW_IsGltfHandedness_AgreeingWithTheGltfUvFrameOnBothCodes()
    {
        var mirrored = StarfieldMeshTestBuilder.Quad();
        var upright = StarfieldMeshTestBuilder.Quad();
        upright.Uv0 =
        [
            (StarfieldMeshTestBuilder.HalfZero, StarfieldMeshTestBuilder.HalfOne),
            (StarfieldMeshTestBuilder.HalfOne, StarfieldMeshTestBuilder.HalfOne),
            (StarfieldMeshTestBuilder.HalfOne, StarfieldMeshTestBuilder.HalfZero),
            (StarfieldMeshTestBuilder.HalfZero, StarfieldMeshTestBuilder.HalfZero)
        ];
        upright.Tangents = [.. Enumerable.Repeat(StarfieldMeshTestBuilder.PackDec4(1023, 512, 512, 0), 4)];

        foreach (var (builder, expected) in new[] { (mirrored, -1f), (upright, 1f) })
        {
            var primitive = Primary(Read(builder.Build()).Document);
            var bitangents = GltfUvBitangents(primitive);
            for (var i = 0; i < primitive.Vertices.Count; i++)
            {
                var tangent = primitive.Tangents!.Values[i];
                var derived = Vector3.Cross(primitive.Vertices[i].Normal, new Vector3(tangent.X, tangent.Y, tangent.Z));
                Assert.Equal(expected, tangent.W);
                Assert.True(Vector3.Dot(derived * tangent.W, bitangents[i]) > 0,
                    $"vertex {i} (w {tangent.W}) disagrees with glTF's UV frame.");
            }
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void TangentCodesOneAndTwo_AreInvalidData(uint code)
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        builder.Tangents![1] = StarfieldMeshTestBuilder.PackDec4(1023, 512, 512, code);

        var error = Assert.Throws<InvalidDataException>(() => Read(builder.Build()));

        Assert.Contains("tangents at 0x", error.Message, StringComparison.Ordinal);
        // Control: code 0 is a bitangent sign, glTF's +1.
        builder.Tangents[1] = StarfieldMeshTestBuilder.PackDec4(1023, 512, 512, 0);
        Assert.Equal(1f, Primary(Read(builder.Build()).Document).Tangents!.Values[1].W);
    }

    [Fact]
    public void NormalWOneWithoutTheTail_IsInvalidData_AndWZeroWithoutItIsNot()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        var cut = builder.Build()[..builder.TailOffset()];

        var error = Assert.Throws<InvalidDataException>(() => Read(cut));

        Assert.Contains("truncated at the meshlet tail", error.Message, StringComparison.Ordinal);
        var control = Read(StarfieldMeshTestBuilder.Quad(tail: false).Build()).Document;
        SceneValidation.ValidateStructure(control);
    }

    [Fact]
    public void NormalWZeroWithTheTail_IsReadWithADiagnostic()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        builder.Normals = [.. builder.Normals!.Select(static code => code & 0x3FFFFFFFu)];

        var document = Read(builder.Build()).Document;

        Assert.Contains(document.Diagnostics, d => d.Code == StarfieldMeshModelDiagnostics.TailWithoutNormalW);
        // Control: the retail pairing (W 1 with the tail) raises none.
        Assert.DoesNotContain(Read(StarfieldMeshTestBuilder.Quad().Build()).Document.Diagnostics,
            d => d.Code == StarfieldMeshModelDiagnostics.TailWithoutNormalW);
    }

    public static TheoryData<string> Corruptions()
    {
        return new TheoryData<string>
        {
            "version3", "scale0", "truncated", "noIndices", "indexCount%3", "index=vertexCount", "uv0Count",
            "weightsCount", "lodCount%3", "lodIndex=vertexCount", "trailingByte"
        };
    }

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void CorruptStreams_AreInvalidData_NamingTheFieldAndOffset(string corruption)
    {
        var bytes = Corrupt(corruption);

        var error = Assert.Throws<InvalidDataException>(() => Read(bytes));

        Assert.StartsWith("Starfield .mesh ", error.Message, StringComparison.Ordinal);
        Assert.Contains(" at 0x", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("weightsPerVertex9")]
    [InlineData("lodCount9")]
    public void StreamsBeyondTheProbeBounds_AreNotSupported(string beyond)
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        if (beyond == "weightsPerVertex9")
        {
            builder.WeightsPerVertex = 9;
            builder.Weights = [.. Enumerable.Repeat(((ushort)0, (ushort)7281), 36)];
        }
        else
        {
            builder.Lods = [.. Enumerable.Repeat(new ushort[] { 0, 1, 2 }, 9)];
        }

        Assert.Throws<NotSupportedException>(() => Read(builder.Build()));
    }

    [Fact]
    public void ADeclaredLengthOverTheBudget_IsRefusedBeforeReading()
    {
        var bytes = StarfieldMeshTestBuilder.Quad().Build();
        var source = new InMemoryAssetSource(SourceId);
        source.Add(DefaultPath, bytes);
        var item = new ModelSourceItem(source, new AssetEntry(new AssetReference(SourceId, DefaultPath),
            StarfieldMeshModelReader.MaximumSourceBytes + 1L));
        using var input = new MemoryStream(bytes, false);
        var context = new ModelReadContext(item, input, new NoCacheScope());

        Assert.Throws<NotSupportedException>(() => new StarfieldMeshModelReader().Read(item, context,
            CancellationToken.None));
        Assert.Equal(0L, input.Position);
    }

    [Fact]
    public void AnotherGameOption_Throws_AndStarfieldOrAutoReads()
    {
        var bytes = StarfieldMeshTestBuilder.Quad().Build();

        Assert.Throws<ArgumentException>(() => Read(bytes, Game("fnv")));
        Assert.Throws<ArgumentException>(() => Read(bytes, Game("daggerfall")));
        Assert.Equal(1.0, Read(bytes, Game("starfield")).Document.Units!.MetersPerUnit);
        Assert.Equal(1.0, Read(bytes, Game("Starfield")).Document.Units!.MetersPerUnit);
        Assert.Equal(1.0, Read(bytes, Game("auto")).Document.Units!.MetersPerUnit);
    }

    [Fact]
    public void Material_IsTheNeutralPlaceholder()
    {
        var document = Read(StarfieldMeshTestBuilder.Quad().Build()).Document;

        var material = Assert.Single(document.Materials);
        Assert.Equal(StarfieldMeshModelMaterials.PlaceholderName, material.Name);
        Assert.Equal(Vector4.One, material.BaseColor);
        Assert.Equal(SceneAlphaMode.Opaque, material.AlphaMode);
        Assert.False(material.DoubleSided);
        Assert.False(material.Unlit);
        Assert.Equal(SceneLightingModel.MetallicRoughness, material.LightingModel);
        Assert.Equal(0f, material.MetallicFactor);
        Assert.Equal(1f, material.RoughnessFactor);
        Assert.Null(material.Texture);
        Assert.Equal(0, Primary(document).MaterialIndex);
    }

    [Fact]
    public void VersionsZeroAndOne_AreRead_FromTheReferenceLayout()
    {
        var zero = Read(StarfieldMeshTestBuilder.Quad(version: 0).Build()).Document;
        var one = StarfieldMeshTestBuilder.Quad(version: 1);
        one.Lods = [[0, 1, 2]];

        SceneValidation.ValidateStructure(zero);
        Assert.Single(zero.Nodes);
        var withLod = Read(one.Build()).Document;
        SceneValidation.ValidateStructure(withLod);
        Assert.Equal(2, withLod.Nodes.Count);
    }

    [Fact]
    public void InspectionAndPreview_ReadTheSameDocument()
    {
        var bytes = StarfieldMeshFileLayoutTests.FullBuilder().Build();
        var reader = new StarfieldMeshModelReader();

        var conversion = Read(bytes).Document;
        var inspection = Read(bytes, purpose: ModelReadPurpose.Inspection).Document;
        var preview = Read(bytes, purpose: ModelReadPurpose.Preview).Document;

        Assert.True(reader.SupportsInspectionWithoutPixelDecoding);
        foreach (var other in new[] { inspection, preview })
        {
            Assert.Equal(Primary(conversion).Vertices, Primary(other).Vertices);
            Assert.Equal(Primary(conversion).Indices, Primary(other).Indices);
            Assert.Equal(conversion.Nodes.Count, other.Nodes.Count);
            Assert.Equal(conversion.NativeStates.Select(s => s.PayloadJson), other.NativeStates.Select(s => s.PayloadJson));
        }
    }

    /// <summary>
    ///     Per-vertex UV-derived bitangents as glTF orients them: -dP/dV accumulated over the triangles, in binary64.
    ///     glTF's tangent-space +Y is the top of the image, and with its top-left UV origin V grows downward, so the
    ///     bitangent is the negated V derivative.
    /// </summary>
    internal static Vector3[] GltfUvBitangents(ScenePrimitive primitive)
    {
        var sums = new double[primitive.Vertices.Count, 3];
        for (var t = 0; t < primitive.Indices.Count; t += 3)
        {
            var (a, b, c) = (primitive.Indices[t], primitive.Indices[t + 1], primitive.Indices[t + 2]);
            var (p0, p1, p2) = (primitive.Vertices[a].Position, primitive.Vertices[b].Position,
                primitive.Vertices[c].Position);
            var (w0, w1, w2) = (primitive.Vertices[a].TexCoord, primitive.Vertices[b].TexCoord,
                primitive.Vertices[c].TexCoord);
            double[] e1 = [p1.X - (double)p0.X, p1.Y - (double)p0.Y, p1.Z - (double)p0.Z];
            double[] e2 = [p2.X - (double)p0.X, p2.Y - (double)p0.Y, p2.Z - (double)p0.Z];
            var du1 = w1.X - (double)w0.X;
            var dv1 = w1.Y - (double)w0.Y;
            var du2 = w2.X - (double)w0.X;
            var dv2 = w2.Y - (double)w0.Y;
            var det = du1 * dv2 - du2 * dv1;
            if (Math.Abs(det) < 1e-12)
            {
                continue;
            }

            foreach (var corner in new[] { a, b, c })
            {
                for (var k = 0; k < 3; k++)
                {
                    sums[corner, k] -= (e2[k] * du1 - e1[k] * du2) / det;
                }
            }
        }

        var result = new Vector3[primitive.Vertices.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new Vector3((float)sums[i, 0], (float)sums[i, 1], (float)sums[i, 2]);
        }

        return result;
    }

    private static uint Bits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    private static byte[] Corrupt(string corruption)
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        switch (corruption)
        {
            case "version3":
                builder.Version = 3;
                break;
            case "scale0":
                builder.Scale = 0f;
                break;
            case "truncated":
                return builder.Build()[..40];
            case "noIndices":
                builder.Indices = [];
                builder.Meshlets = [(4, 0, 0, 0)];
                break;
            case "indexCount%3":
                builder.Indices = [0, 1, 2, 0, 2];
                break;
            case "index=vertexCount":
                builder.Indices = [0, 1, 4, 0, 2, 3];
                break;
            case "uv0Count":
                builder.Uv0 = builder.Uv0![..3];
                break;
            case "weightsCount":
                builder.WeightsPerVertex = 1;
                builder.Weights = [(0, 65535), (0, 65535), (0, 65535)];
                break;
            case "lodCount%3":
                builder.Lods = [[0, 1]];
                break;
            case "lodIndex=vertexCount":
                builder.Lods = [[0, 1, 4]];
                break;
            case "trailingByte":
                builder.Trailing = [0];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, "Unknown corruption.");
        }

        return builder.Build();
    }
}
