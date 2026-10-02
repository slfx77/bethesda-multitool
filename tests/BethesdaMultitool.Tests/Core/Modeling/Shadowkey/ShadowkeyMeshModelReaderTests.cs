using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Shadowkey.ShadowkeyModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     The mesh reader's document (cut-2 plan sections 3.1 to 3.3, 3.6 and 3.7; slice 4) on builder records: the UV
///     vertex domain, the exact texture coordinates, the skin images and their lossless PNGs, the refusals, the coverage
///     and the native rows. The golden records' digests are the Python oracle's
///     (<c>tools/scripts/gate2/shadowkey_cover.py selfcheck</c>, <c>golden.*.digests</c>), computed from the same bytes
///     by an independent decoder.
/// </summary>
public class ShadowkeyMeshModelReaderTests
{
    /// <summary>The oracle's SHA-256 of the golden static record's bytes (the two builders agree on them).</summary>
    private const string GoldenStaticSha256 = "f3879f2b43d65ff54a7198d793e4d92e802a44aca3f33f9c08851503ed211679";

    /// <summary>The oracle's SHA-256 of the golden animated record's bytes.</summary>
    private const string GoldenAnimatedSha256 = "ad3c4c0622772ca397be6f883799e49a4ee47351b010feb9f18902f388f31f98";

    private const string GoldenPositions = "02ca7ecb6ca20bb8e854068db96f9fa42bac9decde2ebe19c0c0b23a590050dc";
    private const string GoldenTexCoords = "bf403068835604b62bd14d794b118b1331ac1b25eb6da833d36154a32b9cd21c";
    private const string GoldenPointIndices = "52419ed8db123aad9f4143c0f28a0e3e1a31ebe662abe9187720caa27bc0f53d";
    private const string GoldenTriangles = "c6ecee0add222b110a2c50d9ba7558528d37dbf759f71da9357853f6dd7718f2";
    private const string GoldenSkin1Original = "c3e6c89fa00a4469c49f763b319303f78f63330f865e341eebcd1f4d25a120dd";
    private const string GoldenSkin1Rgba = "09fa9502036d7ece2c86a1c63db412f72910a466238e3e7d668f579093b7c91c";

    [Fact]
    public void TheBuilders_AgreeWithTheOracleOnTheGoldenBytes()
    {
        Assert.Equal(GoldenStaticSha256, Digest(ShadowkeyTestBuilder.GoldenStatic()));
        Assert.Equal(GoldenAnimatedSha256, Digest(ShadowkeyTestBuilder.GoldenAnimated()));
    }

    [Fact]
    public void Read_ProducesOneStructurallyValidSceneWithProvenance()
    {
        var bytes = ShadowkeyTestBuilder.GoldenStatic();

        var document = ReadMesh(bytes).Document;

        SceneValidation.ValidateStructure(document);
        Assert.Equal(ShadowkeyModelFormatMetadata.MeshFormatId, document.SourceFormat);
        Assert.Equal("175_arrow", document.Name);
        Assert.Equal(new[] { 0 }, Assert.Single(document.Scenes).RootNodeIndices);
        var node = Assert.Single(document.Nodes);
        Assert.Equal(0, node.MeshIndex);
        Assert.Equal(SceneNodeRole.Transform, node.Role);
        Assert.Single(document.Meshes);
        Assert.Empty(document.LayerSets);
        Assert.Empty(document.Animations);
        Assert.Empty(document.Diagnostics);
        var sampler = Assert.Single(document.Samplers);
        Assert.Equal(SceneTextureWrap.Repeat, sampler.WrapU);
        Assert.Equal(SceneTextureWrap.Repeat, sampler.WrapV);
        var material = Assert.Single(document.Materials);
        Assert.True(material.Unlit);
        Assert.False(material.DoubleSided);
        Assert.Equal(SceneAlphaMode.Opaque, material.AlphaMode);
        Assert.Equal(new SceneTextureBinding(0, 0), material.Texture);
        Assert.Same(ShadowkeyModelUnits.Units, document.Units);
        Assert.Same(ShadowkeyModelUnits.MeshBasis, document.SourceBasis);
        Assert.Equal(DefaultMeshPath, document.SourceProvenance!.RelativePath);
        Assert.Equal(GoldenStaticSha256, document.SourceProvenance.Sha256);
    }

    [Fact]
    public void TheVertexDomain_IsTheUvList_WithTheOraclesDigests()
    {
        var primitive = Assert.Single(ReadMesh(ShadowkeyTestBuilder.GoldenStatic()).Document.Meshes[0].Primitives);

        // Five UVs make five vertices: not the record's four vertices, and not the six unrolled corners (the control).
        Assert.Equal(5, primitive.Vertices.Count);
        Assert.NotEqual(3 * 2, primitive.Vertices.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 0 }, primitive.PointIndices!.Values);
        Assert.Equal(4, primitive.PointIndices.PointCount);
        Assert.Equal(new[] { 0, 1, 2, 4, 2, 3 }, primitive.Indices);
        Assert.Equal(GoldenPositions, Float32Digest(Positions(primitive)));
        Assert.Equal(GoldenTexCoords, Float32Digest(TexCoords(primitive)));
        Assert.Equal(GoldenPointIndices, Int32Digest(primitive.PointIndices.Values));
        Assert.Equal(GoldenTriangles, Int32Digest(primitive.Indices));
        // Vertex 4 is UV 4 = (1024, 256) on a 2x2 texture: (2, 0.5), past 1 and kept; its position is vertex 0's.
        Assert.Equal(new Vector2(2f, 0.5f), primitive.Vertices[4].TexCoord);
        Assert.Equal(primitive.Vertices[0].Position, primitive.Vertices[4].Position);
        Assert.Equal(SceneNormalMode.Flat, primitive.NormalMode);
        Assert.Equal(SceneNormalProvenanceKind.Flat, primitive.NormalProvenance!.Kind);
        Assert.All(primitive.Vertices, static v => Assert.Equal(Vector3.Zero, v.Normal));
    }

    [Fact]
    public void AnotherOwner_ChangesThePointIndices_ThatIsTheControl()
    {
        // Face 1 names vertices (3, 2, 0) for UVs (4, 2, 3), so UV 4 belongs to vertex 3 and UV 3 to vertex 0.
        ushort[] faces = [0, 1, 2, 0, 1, 2, 3, 2, 0, 4, 2, 3];
        var bytes = new ShadowkeyTestBuilder.Record
        {
            Positions = ShadowkeyTestBuilder.GoldenFrame0, Uvs = ShadowkeyTestBuilder.GoldenUvs, Faces = faces,
            Texels = ShadowkeyTestBuilder.GoldenSkin1
        }.Build();

        var primitive = ReadMesh(bytes).Document.Meshes[0].Primitives[0];

        Assert.Equal(new[] { 0, 1, 2, 0, 3 }, primitive.PointIndices!.Values);
        Assert.NotEqual(GoldenPointIndices, Int32Digest(primitive.PointIndices.Values));
    }

    [Fact]
    public void TexCoords_DivideEachAxisByItsOwnSide()
    {
        var bytes = new ShadowkeyTestBuilder.Record
        {
            Positions = ShadowkeyTestBuilder.GoldenFrame0, Uvs = [0, 0, 512, 0, 512, 1024, 0, 1024, 768, 256],
            Faces = ShadowkeyTestBuilder.GoldenFaces, Width = 2, Height = 4,
            Texels = [.. Enumerable.Range(0, 8).Select(static k => (ushort)k)]
        }.Build();

        var primitive = ReadMesh(bytes).Document.Meshes[0].Primitives[0];

        Assert.Equal(new Vector2(1f, 1f), primitive.Vertices[2].TexCoord);
        Assert.Equal(new Vector2(1.5f, 0.25f), primitive.Vertices[4].TexCoord);
        // Control: dividing V by the width gives 2, not 1.
        Assert.NotEqual(1024 / (256f * 2), primitive.Vertices[2].TexCoord.Y);
    }

    [Fact]
    public void TheSkin_KeepsItsStoredBlock_AndAnIndexedPngThatDecodesToTheTexels()
    {
        var image = Assert.Single(ReadMesh(ShadowkeyTestBuilder.GoldenStatic()).Document.Images);
        var source = image.Source!;

        Assert.Equal(ShadowkeyModelImages.SkinContainer, source.Container);
        Assert.Equal(GoldenSkin1Original, source.Original!.Sha256);
        Assert.Equal(SceneTextureChannels.Rgb, source.Descriptor.Channels);
        Assert.Equal(16, source.Descriptor.BitsPerPixel);
        Assert.Equal(SceneImageOrigin.SourceReference, source.Origin);
        Assert.Null(source.PaletteIndex);
        var png = StandardPng(image);
        Assert.Equal(3, png.ColorType);
        Assert.Equal((2, 2), (png.Width, png.Height));
        // PLTE: the distinct texels in first-appearance order, each nibble x17.
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC }, png.Palette);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, png.Samples);
        Assert.Empty(png.Alpha);
        Assert.Equal(GoldenSkin1Rgba, Digest(png.Rgba()));
        Assert.Equal(ExpandTexels(ShadowkeyTestBuilder.GoldenSkin1), png.Rgba());
        // Control: x16 instead of x17 gives other colors.
        Assert.NotEqual(ExpandTexels(ShadowkeyTestBuilder.GoldenSkin1, 16), png.Rgba());
    }

    [Fact]
    public void ASkinWithMoreThan256Colors_GetsAnRgbPng()
    {
        var texels = Enumerable.Range(0, 17 * 16).Select(static k => (ushort)k).ToArray();
        var bytes = new ShadowkeyTestBuilder.Record
        {
            Positions = ShadowkeyTestBuilder.GoldenFrame0, Uvs = ShadowkeyTestBuilder.GoldenUvs,
            Faces = ShadowkeyTestBuilder.GoldenFaces, Width = 17, Height = 16, Texels = texels
        }.Build();

        var image = Assert.Single(ReadMesh(bytes).Document.Images);

        var png = StandardPng(image);
        Assert.Equal(2, png.ColorType);
        Assert.Equal(ExpandTexels(texels), png.Rgba());
        Assert.Equal(ShadowkeyModelImages.SkinTruecolorNote, image.Source!.StandardPayload!.Note);
    }

    [Fact]
    public void AUvOwnedByTwoVertices_OrByNone_IsInvalidData()
    {
        var shared = new ShadowkeyTestBuilder.Record
        {
            Positions = ShadowkeyTestBuilder.GoldenFrame0, Uvs = ShadowkeyTestBuilder.GoldenUvs,
            Faces = [0, 1, 2, 0, 1, 2, 0, 2, 3, 1, 2, 3], Texels = ShadowkeyTestBuilder.GoldenSkin1
        }.Build();
        var unused = new ShadowkeyTestBuilder.Record
        {
            Positions = ShadowkeyTestBuilder.GoldenFrame0, Uvs = ShadowkeyTestBuilder.GoldenUvs,
            Faces = [0, 1, 2, 0, 1, 2, 0, 2, 3, 0, 2, 3], Texels = ShadowkeyTestBuilder.GoldenSkin1
        }.Build();

        Assert.Contains("used by vertex", Assert.Throws<InvalidDataException>(() => ReadMesh(shared)).Message,
            StringComparison.Ordinal);
        Assert.Contains("used by no face", Assert.Throws<InvalidDataException>(() => ReadMesh(unused)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ACorruptRecord_IsInvalidData_AndAnotherGameIsRefused()
    {
        var bytes = ShadowkeyTestBuilder.GoldenStatic();
        var corrupt = bytes.ToArray();
        corrupt[0] = 6;

        Assert.Throws<InvalidDataException>(() => ReadMesh(corrupt));
        Assert.Throws<ArgumentException>(() => ReadMesh(bytes, Game("fnv")));
        Assert.Equal("175_arrow", ReadMesh(bytes, Game("shadowkey")).Document.Name);
    }

    [Fact]
    public void ADeclaredLengthOverTheBudget_IsRefusedBeforeReading()
    {
        var source = new InMemoryAssetSource(SourceId);
        var entry = new AssetEntry(new AssetReference(SourceId, "big.bin"), ShadowkeyMeshModelReader.MaximumSourceBytes + 1L);
        var item = new ModelSourceItem(source, entry);
        using var input = new MemoryStream([], false);
        var context = new ModelReadContext(item, input, new NoCacheScope());

        Assert.Throws<NotSupportedException>(() => new ShadowkeyMeshModelReader().Read(item, context, CancellationToken.None));
    }

    [Fact]
    public void TheCoverage_IsTheRecordsOwnSectionList()
    {
        var still = ReadMesh(ShadowkeyTestBuilder.GoldenStatic()).Coverage;
        var animated = ReadMesh(ShadowkeyTestBuilder.GoldenAnimated()).Coverage;

        Assert.Equal(new[] { "header", "positions:0", "uvs", "faces", "texture-header", "skin:0", "sequence:0" },
            still.Elements.Select(static e => e.Identity));
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, still.GetClassification("sequence:0").Kind);
        Assert.Equal(6, still.TypedCount);
        Assert.Equal(0, still.DroppedCount);
        Assert.Equal(new[]
            {
                "header", "positions:0", "positions:1", "uvs", "faces", "texture-header", "skin:0", "skin:1", "sequence:0",
                "sequence:1"
            },
            animated.Elements.Select(static e => e.Identity));
        Assert.Equal(animated.TotalCount, animated.TypedCount);
        Assert.All(animated.Elements, static e => Assert.Equal(ShadowkeyModelCoverage.MeshElementKind, e.Kind));
    }

    [Fact]
    public void TheNativeRows_StateTheWalk_AndKeepRawBytesOnlyAtFullDetail()
    {
        var bytes = ShadowkeyTestBuilder.GoldenAnimated();

        var metadata = ReadMesh(bytes).Document;
        var full = ReadMesh(bytes, detail: ModelNativeDetail.Full).Document;

        var header = JsonNode.Parse(metadata.NativeStates.Single(static s => s.Kind == ShadowkeyModelNativeState.MeshHeaderKind)
            .PayloadJson)!;
        Assert.Equal(2, header["frames"]!.GetValue<int>());
        Assert.Equal("Counted", header["textureHeader"]!.GetValue<string>());
        Assert.Equal(GoldenAnimatedSha256, header["sha256"]!.GetValue<string>());
        Assert.Equal(8, header["sections"]!.AsArray().Count);
        Assert.Equal(0, header["unusedVertices"]!.GetValue<int>());
        var sequences = JsonNode.Parse(metadata.NativeStates
            .Single(static s => s.Kind == ShadowkeyModelNativeState.MeshSequencesKind).PayloadJson)!;
        Assert.Equal(3, sequences["sequences"]![1]!["rate"]!.GetValue<int>());
        var frames = metadata.NativeStates.Single(static s => s.Kind == ShadowkeyModelNativeState.MeshFramesKind);
        Assert.Equal(new SceneElementRef(SceneElementKind.Primitive, 0, 0), frames.Target);
        Assert.False(frames.HasRawContent);
        var fullFrames = full.NativeStates.Single(static s => s.Kind == ShadowkeyModelNativeState.MeshFramesKind);
        Assert.True(fullFrames.HasRawContent);
        Assert.Equal(bytes.AsSpan(14, 48).ToArray(), fullFrames.CopyRawContent());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(14, 48))),
            JsonNode.Parse(fullFrames.PayloadJson)!["positionsSha256"]!.GetValue<string>());
    }

    [Fact]
    public void MagentaTexels_StayOpaque_WithTheDiagnostic()
    {
        var document = ReadMesh(ShadowkeyTestBuilder.GoldenAnimated()).Document;

        Assert.Contains(document.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.MagentaOpaque);
        Assert.All(document.Materials, static m => Assert.Equal(SceneAlphaMode.Opaque, m.AlphaMode));
        var png = StandardPng(document.Images[0]);
        Assert.Empty(png.Alpha);
        Assert.Equal(255, png.Rgba()[15]);
        Assert.DoesNotContain(ReadMesh(ShadowkeyTestBuilder.GoldenStatic()).Document.Diagnostics,
            static d => d.Code == ShadowkeyModelDiagnostics.MagentaOpaque);
    }

    [Fact]
    public void InspectionAndConversion_ReadTheSameDocument()
    {
        Assert.True(new ShadowkeyMeshModelReader().SupportsInspectionWithoutPixelDecoding);
        var bytes = ShadowkeyTestBuilder.GoldenAnimated();
        var source = new InMemoryAssetSource(SourceId);
        var item = new ModelSourceItem(source, source.Add(DefaultMeshPath, bytes));
        using var input = new MemoryStream(bytes, false);
        var context = new ModelReadContext(item, input, new NoCacheScope(), purpose: ModelReadPurpose.Inspection);

        var inspection = new ShadowkeyMeshModelReader().Read(item, context, CancellationToken.None).Document;
        var conversion = ReadMesh(bytes).Document;

        Assert.Equal(conversion.Images.Count, inspection.Images.Count);
        Assert.Equal(conversion.Images[0].Source!.StandardPayload!.Payload.Sha256,
            inspection.Images[0].Source!.StandardPayload!.Payload.Sha256);
        Assert.Equal(Float32Digest(Positions(conversion.Meshes[0].Primitives[0])),
            Float32Digest(Positions(inspection.Meshes[0].Primitives[0])));
    }

    /// <summary>
    ///     The record vertices no face names (cut-2 review finding 3): the UV domain carries none of them, so the frames
    ///     row names them and an unused-positions row holds their per-frame positions at the default Metadata detail, and
    ///     the census gives them their own NativeOnly element instead of claiming them under the Typed positions.
    ///     Control: the golden animated record, whose vertices are all used, has neither the element nor the rows.
    /// </summary>
    [Fact]
    public void UnusedVertices_KeepTheirPositionsInNativeState_AndTheirOwnCensusElement()
    {
        short[] extra0 = [9, 8, 7];
        short[] extra1 = [19, 18, 17];
        var bytes = new ShadowkeyTestBuilder.Record
        {
            Frames = 2,
            Positions = [.. ShadowkeyTestBuilder.GoldenFrame0, .. extra0, .. ShadowkeyTestBuilder.GoldenFrame1, .. extra1],
            Uvs = ShadowkeyTestBuilder.GoldenUvs, Faces = ShadowkeyTestBuilder.GoldenFaces,
            Texels = ShadowkeyTestBuilder.GoldenSkin1, Sequences = [(0, 2, 5)]
        }.Build();

        var result = ReadMesh(bytes);
        var golden = ReadMesh(ShadowkeyTestBuilder.GoldenAnimated());

        Assert.Equal(new[]
            {
                "header", "positions:0", "positions:1", ShadowkeyModelCoverage.UnusedVerticesIdentity, "uvs", "faces",
                "texture-header", "skin:0", "sequence:0"
            },
            result.Coverage.Elements.Select(static e => e.Identity));
        var classification = result.Coverage.GetClassification(ShadowkeyModelCoverage.UnusedVerticesIdentity);
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, classification.Kind);
        Assert.Equal(5, result.Document.Meshes[0].Primitives[0].PointIndices!.PointCount);
        var frames = JsonNode.Parse(result.Document.NativeStates
            .Single(static s => s.Kind == ShadowkeyModelNativeState.MeshFramesKind).PayloadJson)!;
        Assert.Equal(new[] { 4 }, frames["unusedVertices"]!.AsArray().Select(static v => v!.GetValue<int>()));
        Assert.Equal(1, frames["unusedPositionRows"]!.GetValue<int>());
        var row = Assert.Single(result.Document.NativeStates,
            static s => s.Kind == ShadowkeyModelNativeState.MeshUnusedPositionsKind);
        Assert.False(row.HasRawContent);
        var payload = JsonNode.Parse(row.PayloadJson)!;
        Assert.Equal(0L, payload["first"]!.GetValue<long>());
        Assert.Equal(new[] { 9, 8, 7, 19, 18, 17 }, payload["values"]!.AsArray().Select(static v => v!.GetValue<int>()));

        Assert.DoesNotContain(golden.Coverage.Elements,
            static e => e.Identity == ShadowkeyModelCoverage.UnusedVerticesIdentity);
        Assert.DoesNotContain(golden.Document.NativeStates,
            static s => s.Kind == ShadowkeyModelNativeState.MeshUnusedPositionsKind);
    }

    /// <summary>A closed tetrahedron, one UV per vertex, faces wound outward or (reversed) inward.</summary>
    private static byte[] Tetrahedron(bool reversed)
    {
        ushort[] outward = [0, 2, 1, 0, 2, 1, 0, 1, 3, 0, 1, 3, 0, 3, 2, 0, 3, 2, 1, 2, 3, 1, 2, 3];
        ushort[] inward = [0, 1, 2, 0, 1, 2, 0, 3, 1, 0, 3, 1, 0, 2, 3, 0, 2, 3, 1, 3, 2, 1, 3, 2];
        return new ShadowkeyTestBuilder.Record
        {
            Positions = [0, 0, 0, 256, 0, 0, 0, 256, 0, 0, 0, 256], Uvs = [0, 0, 512, 0, 0, 512, 512, 512],
            Faces = reversed ? inward : outward, Texels = ShadowkeyTestBuilder.GoldenSkin1
        }.Build();
    }

    /// <summary>
    ///     A closed record wound clockwise seen from outside carries the reversed-winding diagnostic, and the header row
    ///     states the closed test, the signed volume and the single-sided provenance (cut-2 review finding 5). Controls:
    ///     the same tetrahedron wound outward, and the open golden record, carry no diagnostic.
    /// </summary>
    [Fact]
    public void AClosedRecordWoundInward_IsDiagnosed_AndTheHeaderStatesTheSignedVolume()
    {
        var inward = ReadMesh(Tetrahedron(reversed: true)).Document;
        var outward = ReadMesh(Tetrahedron(reversed: false)).Document;
        var open = ReadMesh(ShadowkeyTestBuilder.GoldenStatic()).Document;

        JsonNode Header(ModelDocument document)
        {
            return JsonNode.Parse(document.NativeStates.Single(static s => s.Kind == ShadowkeyModelNativeState.MeshHeaderKind)
                .PayloadJson)!;
        }

        Assert.Contains(inward.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.ReversedWinding);
        Assert.True(Header(inward)["closed"]!.GetValue<bool>());
        // 6V = p . (q x r) over the one face off the origin: 256^3, negative when wound inward.
        Assert.Equal(-16_777_216L, Header(inward)["signedVolumeX6"]!.GetValue<long>());
        Assert.Equal(ShadowkeyMeshDocumentBuilder.SidednessEvidence, Header(inward)["sidedness"]!.GetValue<string>());
        Assert.All(inward.Materials, static m => Assert.False(m.DoubleSided));

        Assert.DoesNotContain(outward.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.ReversedWinding);
        Assert.Equal(16_777_216L, Header(outward)["signedVolumeX6"]!.GetValue<long>());
        Assert.DoesNotContain(open.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.ReversedWinding);
        Assert.False(Header(open)["closed"]!.GetValue<bool>());
    }

    /// <summary>
    ///     A record the probe cannot fully see (larger than its 64 KiB prefix, the prefix ending before the texture header)
    ///     probes Supported and Tentative, so the read decides; no skin, a zero texture side or a zero rate must then fail
    ///     as invalid data naming the byte, not as an argument error (cut-2 review finding 14). Control: the same record
    ///     with one skin, both sides 2 and rate 5 reads.
    /// </summary>
    [Theory]
    [InlineData("skins")]
    [InlineData("width")]
    [InlineData("rate")]
    public void AnUndocumentableRecordPastTheProbePrefix_IsInvalidDataNamingTheByte(string defect)
    {
        byte[] Large(int skins, int width, int rate)
        {
            var positions = new short[2 * 6000 * 3];
            positions[3] = 256;
            positions[7] = 256;
            return new ShadowkeyTestBuilder.Record
            {
                Frames = 2, Positions = positions, Uvs = [0, 0, 512, 0, 0, 512], Faces = [0, 1, 2, 0, 1, 2],
                Skins = skins, Width = width, Height = 2, Texels = new ushort[skins * width * 2],
                Sequences = [(0, 2, rate)]
            }.Build();
        }

        var bytes = defect switch
        {
            "skins" => Large(0, 2, 5),
            "width" => Large(1, 0, 5),
            _ => Large(1, 2, 0)
        };

        Assert.True(bytes.Length > ModelSourceCandidate.MaximumProbeBytes);
        var probe = ShadowkeyMeshModelProbe.Probe(bytes.AsSpan(0, ModelSourceCandidate.MaximumProbeBytes), bytes.Length,
            complete: false);
        Assert.Equal((ModelProbeKind.Supported, ModelProbeConfidence.Tentative), (probe.Kind, probe.Confidence));
        var error = Assert.Throws<InvalidDataException>(() => ReadMesh(bytes, path: "large.bin"));
        Assert.Contains("at byte", error.Message, StringComparison.Ordinal);
        Assert.Equal("large", ReadMesh(Large(1, 2, 5), path: "large.bin").Document.Name);
    }
}
