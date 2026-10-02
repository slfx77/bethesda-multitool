using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using ImageMagick;
using SharpGLTF.Schema2;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels.Shadowkey;

/// <summary>Pins the Shadowkey neutral snapshot against the three things this format does differently.</summary>
/// <remarks>
///     The NIF adapter crosses a Z-up basis, reads normalised UVs and takes materials per face. Shadowkey does
///     none of those: its second component is already up, its UVs are 8.8 fixed point in texels that wrap, and
///     its extra textures are alternative whole-mesh skins. Each of those is asserted here, because reusing the
///     NIF path's assumptions would silently corrupt correct data rather than fail.
/// </remarks>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class ShadowkeyNeutralSceneAdapterTests
{
    private const string RetailPackSha256 = "3b49517651b9edbf78e99660ef0a0f5f9d398146d11eecb7ee12df81c8fbd310";

    /// <summary>
    ///     Builds a one-frame, one-face record through the real parser, so the fixture exercises the same layout
    ///     walk the retail pack does rather than a hand-built object the parser would never produce.
    /// </summary>
    private static ShadowkeyMesh Mesh(
        int frames = 1,
        int sequences = 0,
        int skins = 1,
        ushort textureSize = 4,
        short upExtent = 100,
        bool independentCorners = false,
        ushort[]? skinTexels = null)
    {
        const int vertexCount = 3;
        const int uvCount = 3;
        const int faceCount = 1;
        var bytes = new List<byte>();

        void U16(int value)
        {
            bytes.Add((byte)(value & 0xFF));
            bytes.Add((byte)((value >> 8) & 0xFF));
        }

        // Header: tag, frames, vertices, uvs, faces, 3*vertices, trailer.
        U16(ShadowkeyMesh.FormatTag);
        U16(frames);
        U16(vertexCount);
        U16(uvCount);
        U16(faceCount);
        U16(3 * vertexCount);
        U16(ShadowkeyMesh.HeaderTrailer);

        // Frame-major positions. The SECOND component is up, which is the whole point of the basis case.
        for (var frame = 0; frame < frames; frame++)
        {
            U16(0); U16(0); U16(frame * 11);
            U16(100); U16(0); U16(frame * 11);
            U16(0); U16(upExtent); U16(frame * 11);
        }

        // UVs: 8.8 fixed point in texels. 256 is exactly one texel; 2048 is eight texels, which is beyond a
        // four-texel skin and must survive as a wrap rather than being clamped.
        U16(0); U16(0);
        U16(256); U16(0);
        U16(2048); U16(512);

        // One face: three vertex indices then three UV indices.
        if (independentCorners)
        {
            U16(2); U16(0); U16(1);
            U16(1); U16(2); U16(0);
        }
        else
        {
            U16(0); U16(1); U16(2);
            U16(0); U16(1); U16(2);
        }

        // Textures: count, width, height, then skins of width*height 0x0RGB texels.
        U16(skins);
        U16(textureSize);
        U16(textureSize);
        for (var skin = 0; skin < skins; skin++)
        {
            for (var texel = 0; texel < textureSize * textureSize; texel++)
            {
                U16(skinTexels is null ? 0x0123 + skin : skinTexels[texel % skinTexels.Length]);
            }
        }

        U16(sequences);
        for (var sequence = 0; sequence < sequences; sequence++)
        {
            U16(0); U16(frames); U16(5);
        }

        return ShadowkeyMesh.Parse(bytes.ToArray(), "fixture");
    }

    private static ModelDocument Adapt(ShadowkeyMesh mesh, int frame = 0, int skin = 0,
        string? sourceIdentity = null, bool magentaIsTransparent = false)
    {
        Assert.True(ShadowkeyNeutralSceneAdapter.TryAdapt(mesh, frame, skin, out var document, out var reason,
            TestContext.Current.CancellationToken, sourceIdentity, magentaIsTransparent), reason);
        Assert.Null(reason);
        return document;
    }

    private static string? Decline(ShadowkeyMesh mesh, int frame = 0, int skin = 0)
    {
        Assert.False(ShadowkeyNeutralSceneAdapter.TryAdapt(mesh, frame, skin, out var document, out var reason,
            TestContext.Current.CancellationToken));
        Assert.Null(document);
        return reason;
    }

    [Fact]
    public void TheUpAxisIsCarriedUnchanged_BecauseShadowkeyIsAlreadyYUp()
    {
        // The third vertex is 100 units up, stored in the SECOND component. glTF is also Y-up, so it must
        // still be in Y afterwards. An adapter that reused the NIF path's Z-up rotation would move it into Z
        // and quietly lay every Shadowkey model on its side.
        var positions = Adapt(Mesh(upExtent: 100)).Meshes[0].Primitives[0].Vertices;
        var raised = positions.First(vertex =>
            vertex.Position.LengthSquared() > 0f && MathF.Abs(vertex.Position.X) < 1e-4f);

        Assert.Equal(100f, raised.Position.Y, 3);
        Assert.Equal(0f, raised.Position.Z, 3);
    }

    [Fact]
    public void TexelUvsAreNormalisedByTheSkinSize()
    {
        // 256 in 8.8 fixed point is exactly one texel. On a four-texel skin that is 0.25 normalised.
        var uvs = Adapt(Mesh(textureSize: 4)).Meshes[0].Primitives[0].Vertices;

        Assert.Equal(0f, uvs[0].TexCoord.X, 4);
        Assert.Equal(0.25f, uvs[1].TexCoord.X, 4);
    }

    [Fact]
    public void UvsBeyondTheSkinSurviveAsWrapRatherThanBeingClamped()
    {
        // 2048 in 8.8 is eight texels, which is twice a four-texel skin. The game wraps, so 2.0 must reach
        // the document intact; clamping it to 1.0 would move that corner onto the wrong part of the skin.
        var uvs = Adapt(Mesh(textureSize: 4)).Meshes[0].Primitives[0].Vertices;

        Assert.Equal(2f, uvs[2].TexCoord.X, 4);
        Assert.True(uvs[2].TexCoord.X > 1f, "A wrapped coordinate must not be clamped into range.");
    }

    [Fact]
    public void TheSamplerWraps_BecauseTheCoordinatesDo()
    {
        var sampler = Assert.Single(Adapt(Mesh()).Samplers);

        Assert.Equal(SceneTextureWrap.Repeat, sampler.WrapU);
        Assert.Equal(SceneTextureWrap.Repeat, sampler.WrapV);
    }

    [Fact]
    public void NormalsAreExactPerFaceAndNeverZero()
    {
        foreach (var vertex in Adapt(Mesh()).Meshes[0].Primitives[0].Vertices)
        {
            Assert.Equal(1f, vertex.Normal.Length(), 4);
        }
    }

    [Fact]
    public void TheDocumentSatisfiesTheSharedValidator()
    {
        SceneValidation.Validate(Adapt(Mesh()), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TheSelectedSkinBecomesTheMaterialImage()
    {
        var document = Adapt(Mesh(skins: 3), skin: 2);

        Assert.Single(document.Images);
        Assert.Equal("fixture.skin2", document.Images[0].Name);
        Assert.NotNull(document.Materials[0].Texture);
    }

    [Fact]
    public void KeyframeAnimationDeclinesRatherThanInventingAFrameRate()
    {
        var reason = Decline(Mesh(frames: 4, sequences: 1));

        Assert.NotNull(reason);
        Assert.Contains("unrecorded units", reason);
    }

    [Fact]
    public void AFrameOutsideTheRecordDeclines()
    {
        Assert.Equal("A frame outside the record's range retains its native handling.", Decline(Mesh(), frame: 9));
    }

    [Fact]
    public void ASkinOutsideTheSetDeclines()
    {
        Assert.Equal("A skin outside the record's set retains its native handling.", Decline(Mesh(), skin: 9));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColorKeyChoicePreservesExactPngPixelsAndEncodedAlpha(bool keying)
    {
        var mesh = Mesh(textureSize: 2, skinTexels: [0x0F0F, 0x0123, 0x0FFF, 0x0000]);
        var document = keying ? Adapt(mesh, magentaIsTransparent: true) : Adapt(mesh);
        byte[] expected = [255, 0, 255, keying ? (byte)0 : (byte)255,
            17, 34, 51, 255, 255, 255, 255, 255, 0, 0, 0, 255];

        Assert.Equal(expected, Pixels(Assert.Single(document.Images).CopyContent()));
        var material = Assert.Single(document.Materials);
        Assert.Equal(keying ? SceneAlphaMode.Mask : SceneAlphaMode.Opaque, material.AlphaMode);
        Assert.True(material.Unlit);
        Assert.False(material.DoubleSided);
        var encoded = Shared(document);
        var encodedMaterial = Assert.Single(encoded.LogicalMaterials);
        Assert.Equal(keying ? AlphaMode.MASK : AlphaMode.OPAQUE, encodedMaterial.Alpha);
        if (keying)
        {
            Assert.Equal(0.5f, encodedMaterial.AlphaCutoff);
        }
        Assert.Equal(expected, Pixels(Assert.Single(encoded.LogicalImages).Content.Content.ToArray()));
    }

    [Fact]
    public void SelectedFrameAndIndependentCornerIndicesSurviveEncodedGeometryAndWrap()
    {
        var document = Adapt(Mesh(frames: 2, independentCorners: true), frame: 1);
        Vector3[] positions = [new(0, 100, 11), new(0, 0, 11), new(100, 0, 11)];
        Vector2[] uvs = [new(0.25f, 0), new(2, 0.5f), Vector2.Zero];
        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        Assert.Equal(positions, primitive.Vertices.Select(vertex => vertex.Position));
        Assert.Equal(uvs, primitive.Vertices.Select(vertex => vertex.TexCoord));
        Assert.All(primitive.Vertices, vertex => Assert.Equal(Vector3.UnitZ, vertex.Normal));
        Assert.Empty(document.Animations);
        var encoded = Shared(document);
        var draw = Assert.Single(Assert.Single(encoded.LogicalMeshes).Primitives);
        var encodedPositions = draw.GetVertexAccessor("POSITION").AsVector3Array();
        var encodedUvs = draw.GetVertexAccessor("TEXCOORD_0").AsVector2Array();
        var indices = draw.GetIndices();
        Assert.Equal(3, indices.Count);
        var first = Array.IndexOf(positions, encodedPositions[(int)indices[0]]);
        Assert.InRange(first, 0, 2);
        for (var corner = 0; corner < 3; corner++)
        {
            Assert.Equal(positions[(first + corner) % 3], encodedPositions[(int)indices[corner]]);
            Assert.Equal(uvs[(first + corner) % 3], encodedUvs[(int)indices[corner]]);
        }
        AssertEncodedRepeatSampler(encoded);
        Assert.Equal(Matrix4x4.Identity, Assert.Single(encoded.LogicalNodes, node => node.Mesh is not null).WorldMatrix);
    }

    [Fact]
    public void CallerOccurrenceAndSelectionAreRetainedWithoutInferringIdentityFromLabel()
    {
        const string identity = "caller-pack-A/slot-7\"exact";
        var mesh = Mesh(frames: 2, skins: 3);
        var document = Adapt(mesh, frame: 1, skin: 2, sourceIdentity: identity);
        Assert.Equal(identity, document.SourceIdentity);
        Assert.Equal("fixture", document.Name);
        foreach (var json in new[] { document.ExtrasJson, Assert.Single(document.Nodes).ExtrasJson })
        {
            using var metadata = JsonDocument.Parse(Assert.IsType<string>(json));
            Assert.Equal(identity, metadata.RootElement.GetProperty("sourceIdentity").GetString());
            Assert.Equal(1, metadata.RootElement.GetProperty("selectedFrame").GetInt32());
            Assert.Equal(2, metadata.RootElement.GetProperty("selectedSkin").GetInt32());
            Assert.Equal(2, metadata.RootElement.GetProperty("sourceFrameCount").GetInt32());
            Assert.False(metadata.RootElement.GetProperty("magentaIsTransparent").GetBoolean());
        }
        var anonymous = Adapt(mesh);
        Assert.Null(anonymous.SourceIdentity);
        using var anonymousMetadata = JsonDocument.Parse(anonymous.ExtrasJson!);
        Assert.Equal(JsonValueKind.Null, anonymousMetadata.RootElement.GetProperty("sourceIdentity").ValueKind);
        Assert.Equal("caller-pack-B/slot-7", Adapt(mesh, sourceIdentity: "caller-pack-B/slot-7").SourceIdentity);
        var encoded = Shared(document);
        Assert.Equal(identity, encoded.Extras!["multitoolSceneSource"]!["identity"]!.GetValue<string>());
        var occurrence = Assert.Single(encoded.LogicalNodes).Extras!;
        Assert.Equal(identity, occurrence["sourceIdentity"]!.GetValue<string>());
        Assert.Equal(1, occurrence["selectedFrame"]!.GetValue<int>());
        Assert.Equal(2, occurrence["selectedSkin"]!.GetValue<int>());
    }

    [Fact]
    public void AdaptationLeavesSourceBuffersUntouchedAndOwnsSelectedSkinPixels()
    {
        var mesh = Mesh(frames: 2, skins: 2);
        var before = JsonSerializer.Serialize(mesh);
        var vertices = mesh.Vertices;
        var uvs = mesh.Uvs;
        var faces = mesh.Faces;
        var skins = mesh.Textures.Skins.ToArray();
        var document = Adapt(mesh, frame: 1, skin: 1);
        Assert.Equal(before, JsonSerializer.Serialize(mesh));
        Assert.Same(vertices, mesh.Vertices);
        Assert.Same(uvs, mesh.Uvs);
        Assert.Same(faces, mesh.Faces);
        for (var skin = 0; skin < skins.Length; skin++)
        {
            Assert.Same(skins[skin], mesh.Textures.Skins[skin]);
        }
        var expected = Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 17, 34, 68, 255 }).ToArray();
        Assert.Equal(expected, Pixels(document.Images[0].CopyContent()));
        // Deliberately mutate the caller's source only after proving adaptation did not touch it.
        mesh.Textures.Skins[1][0] = ShadowkeyMesh.MagentaColourKey;
        Assert.Equal(expected, Pixels(document.Images[0].CopyContent()));
    }

    [Fact]
    public void CanceledAdaptationDoesNotTouchSourceBuffers()
    {
        var mesh = Mesh();
        var before = JsonSerializer.Serialize(mesh);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ShadowkeyNeutralSceneAdapter.TryAdapt(
            mesh, 0, 0, out _, out _, cancellation.Token));
        Assert.Equal(before, JsonSerializer.Serialize(mesh));
    }

    private static ModelRoot Shared(ModelDocument document) => ModelRoot.ParseGLB(GltfExporter.Encode(
        SceneGltfBuilder.Build(document, GltfExportIntent.Interchange, TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken));

    private static byte[] Pixels(byte[] png)
    {
        using var decoded = new MagickImage(png);
        using var pixelView = decoded.GetPixels();
        return Assert.IsType<byte[]>(pixelView.ToByteArray(PixelMapping.RGBA));
    }

    private static void AssertEncodedRepeatSampler(ModelRoot encoded)
    {
        var channel = Assert.IsType<MaterialChannel>(Assert.Single(encoded.LogicalMaterials).FindChannel("BaseColor"));
        var texture = Assert.IsType<Texture>(channel.Texture);
        Assert.Same(Assert.Single(encoded.LogicalTextures), texture);
        // SharpGLTF UseTextureSampler returns null for all-default settings. glTF defaults both axes
        // to REPEAT; an explicit CLAMP_TO_EDGE or MIRRORED_REPEAT must still fail these assertions.
        Assert.Equal(TextureWrapMode.REPEAT, texture.Sampler?.WrapS ?? TextureWrapMode.REPEAT);
        Assert.Equal(TextureWrapMode.REPEAT, texture.Sampler?.WrapT ?? TextureWrapMode.REPEAT);
    }

    [Trait("Category", BucketBTestGuard.Category)]
    [Theory]
    [InlineData(0, "fern.bin",
        "05ca3d00f4277a85419a1d2d6c326e619771e273a95b333c72446308fd71ce6a",
        "59af9cbd1cdcbaab64877cc6068a5d783877ca01fe5892ce098a0159601c0716",
        "d9a45682a4782ae4a5e62418746a080951146f9e534b47372963d9bdc9ed42b7",
        "29e14c6049b91854ae84b08d5b75d4963621185538ed19b5a194bccac6080d86",
        "e0b25e323b5fd62b3fec23dbe231fdb54c7067b430a307fabeade0836417ec3b")]
    [InlineData(175, "arrow.bin",
        "ab7d0963b256f8208100df97f6b3da7025ff146c32bc02fb8c19025a52cc234e",
        "bbb0af6fd43aec414d9c74f16a0ba051dbc919bb9150f14946c3c84f585e1622",
        "df28d68226a4574a9a468a4bbd3766844bb4162a9061bbb610c849c99eaf9813",
        "22b1180ae9ef628816298ec0f4319a8b10ad9fc64b3932516f075c32d6bb869b",
        "22b1180ae9ef628816298ec0f4319a8b10ad9fc64b3932516f075c32d6bb869b")]
    public void NamedRetailStaticRecordsPreserveIndependentGeometryAndPixels(
        int slot, string name, string recordHash, string positionHash, string uvHash,
        string opaquePixelHash, string keyedPixelHash)
    {
        var pack = ReadRetailPack();
        Assert.Equal(name, pack.Entries[slot].FileName);
        Assert.Equal(recordHash, Hash(pack.GetEntryBytes(slot).Span));
        var mesh = Assert.IsType<ShadowkeyMesh>(pack.GetMesh(slot));
        var before = JsonSerializer.Serialize(mesh);
        var identity = $"sha256:{RetailPackSha256}/slot:{slot}";

        foreach (var keying in new[] { false, true })
        {
            var document = Adapt(mesh, sourceIdentity: identity, magentaIsTransparent: keying);
            Assert.Equal(identity, document.SourceIdentity);
            var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
            Assert.Equal(mesh.Faces.Count * 3, primitive.Vertices.Count);
            Assert.Equal(positionHash, FloatHash(primitive.Vertices.SelectMany(vertex =>
                new[] { vertex.Position.X, vertex.Position.Y, vertex.Position.Z })));
            Assert.Equal(uvHash, FloatHash(primitive.Vertices.SelectMany(vertex =>
                new[] { vertex.TexCoord.X, vertex.TexCoord.Y })));
            var pixelHash = keying ? keyedPixelHash : opaquePixelHash;
            Assert.Equal(pixelHash, Hash(Pixels(Assert.Single(document.Images).CopyContent())));

            var encoded = Shared(document);
            Assert.Equal(keying ? AlphaMode.MASK : AlphaMode.OPAQUE,
                Assert.Single(encoded.LogicalMaterials).Alpha);
            Assert.Equal(pixelHash, Hash(Pixels(Assert.Single(encoded.LogicalImages).Content.Content.ToArray())));
            var draw = Assert.Single(Assert.Single(encoded.LogicalMeshes).Primitives);
            var positions = draw.GetVertexAccessor("POSITION").AsVector3Array();
            var uvs = draw.GetVertexAccessor("TEXCOORD_0").AsVector2Array();
            Assert.Equal(
                TriangleSignatures(primitive.Vertices.Select(vertex => (vertex.Position, vertex.TexCoord))),
                TriangleSignatures(draw.GetIndices().Select(index => (positions[(int)index], uvs[(int)index]))));
            Assert.Equal(Matrix4x4.Identity,
                Assert.Single(encoded.LogicalNodes, node => node.Mesh is not null).WorldMatrix);
            AssertEncodedRepeatSampler(encoded);
        }
        Assert.Equal(before, JsonSerializer.Serialize(mesh));
        Assert.Equal(recordHash, Hash(pack.GetEntryBytes(slot).Span));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"slot {slot} {name}: {mesh.Faces.Count} triangles; independent positions, UVs, opaque/keyed pixels and encoded winding preserved");
    }

    // These goldens were derived independently from the original little-endian record fields, not from
    // ToUnrolledTriangles, DecodeSkin, the adapter, or the GLB builder. BinaryWriter emits little-endian floats.
    private static string FloatHash(IEnumerable<float> values)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var value in values)
            {
                writer.Write(value);
            }
        }
        return Hash(bytes.ToArray());
    }

    private static string[] TriangleSignatures(IEnumerable<(Vector3 Position, Vector2 Uv)> corners)
    {
        return corners.Select(corner => string.Join(",", new[]
            {
                corner.Position.X, corner.Position.Y, corner.Position.Z, corner.Uv.X, corner.Uv.Y
            }.Select(value => value.ToString("R", CultureInfo.InvariantCulture))))
            .Chunk(3).Select(triangle =>
            {
                Assert.Equal(3, triangle.Length);
                // Only cyclic rotation is permitted: reversal changes the authored winding.
                return new[]
                {
                    $"{triangle[0]}|{triangle[1]}|{triangle[2]}",
                    $"{triangle[1]}|{triangle[2]}|{triangle[0]}",
                    $"{triangle[2]}|{triangle[0]}|{triangle[1]}"
                }.Min(StringComparer.Ordinal)!;
            }).Order(StringComparer.Ordinal).ToArray();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ShadowkeyModelPack ReadRetailPack()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.ShadowkeyRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Shadowkey (system/apps/6R51)"));
        var index = ReadBounded(Path.Combine(root!, "models.idx"), 4 * 1024,
            "7b67e1d26b3d0037b748622fceb340fb4556bb638490b985b2c4b0f62bf0ae71");
        var bytes = ReadBounded(Path.Combine(root!, "models.huge"), 5 * 1024 * 1024, RetailPackSha256);
        var names = ReadBounded(Path.Combine(root!, "models.txt"), 16 * 1024,
            "a595b31863e96777083b6c1294c81c27d7ba4334a6ffb8332162246c96fffb24");
        return ShadowkeyModelPack.Parse(index, bytes, Encoding.UTF8.GetString(names), "models.huge");
    }

    private static byte[] ReadBounded(string path, int maximumBytes, string sha256)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        Assert.InRange(stream.Length, 1L, maximumBytes);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        Assert.Equal(-1, stream.ReadByte());
        Assert.Equal(sha256, Hash(bytes));
        return bytes;
    }

    [Trait("Category", BucketBTestGuard.Category)]
    [Fact]
    public void RealShadowkeyRecords_AdaptOrDeclineWithAKnownReason()
    {
        var pack = ReadRetailPack();
        const string animationReason =
            "Keyframe animation retains its native handling: the pack's sequence rates are in " +
            "unrecorded units, so a clip cannot be timed without inventing a frame duration.";

        var carried = 0;
        var declined = 0;
        var empty = 0;
        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < pack.Count; index++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            var record = pack.GetMesh(index);
            if (record is null)
            {
                Assert.True(pack.Entries[index].IsEmpty);
                empty++;
                continue;
            }
            if (ShadowkeyNeutralSceneAdapter.TryAdapt(record, 0, 0, out var document, out var reason,
                    TestContext.Current.CancellationToken))
            {
                Assert.NotNull(document);
                carried++;
            }
            else
            {
                Assert.Null(document);
                Assert.False(string.IsNullOrWhiteSpace(reason));
                Assert.Equal(animationReason, reason);
                reasons[reason!] = reasons.GetValueOrDefault(reason!) + 1;
                declined++;
            }
        }

        Assert.Equal(237, pack.Count);
        Assert.Equal(11, empty);
        Assert.Equal(193, carried);
        Assert.Equal(33, declined);
        Assert.Equal(pack.Count, carried + declined + empty);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"slots {pack.Count}, records {carried + declined}, carried {carried}, declined {declined}, empty {empty}");
        foreach (var entry in reasons)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"  {entry.Value}x {entry.Key}");
        }
    }
}
