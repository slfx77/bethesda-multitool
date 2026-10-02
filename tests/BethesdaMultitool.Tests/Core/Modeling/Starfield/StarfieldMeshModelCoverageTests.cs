using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Starfield;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Starfield.StarfieldMeshModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     The coverage census and the native-state rows (cut-2 plan sections 3.4 and 3.5, slice 4) against the independent
///     writer's own bookkeeping of what it wrote.
/// </summary>
public class StarfieldMeshModelCoverageTests
{
    /// <summary>The data sections that are census elements when non-empty (the plan's section 3.4 table).</summary>
    private static readonly string[] CensusSections =
        ["indices", "positions", "uv0", "uv1", "colors", "normals", "tangents", "weights", "meshlets", "cull"];

    public static TheoryData<string> Builders()
    {
        return new TheoryData<string> { "full", "quad", "tailless", "version0" };
    }

    [Theory]
    [MemberData(nameof(Builders))]
    public void TheCensusIsEveryNonEmptyDataSectionTheWriterWrote(string name)
    {
        var builder = Builder(name);
        var (bytes, written) = builder.BuildWithLayout();
        var expected = written
            .Where(static s => s.Count > 0 && (CensusSections.Contains(s.Name) || s.Name.StartsWith("lod:", StringComparison.Ordinal)))
            .Select(static s => s.Name)
            .ToArray();

        var coverage = Read(bytes).Coverage;

        Assert.Equal(expected, coverage.Elements.Select(static e => e.Identity));
        Assert.All(coverage.Elements, static e => Assert.Equal(StarfieldMeshModelCoverage.ElementKind, e.Kind));
        Assert.Equal(0, coverage.DroppedCount);
        foreach (var classification in coverage.Classifications)
        {
            var (kind, reason) = classification.ElementIdentity switch
            {
                "meshlets" => (ModelSourceCoverageKind.NativeOnly, StarfieldMeshModelCoverage.MeshletsReason),
                "cull" => (ModelSourceCoverageKind.NativeOnly, StarfieldMeshModelCoverage.CullReason),
                _ => (ModelSourceCoverageKind.Typed, (string?)null)
            };
            Assert.Equal(kind, classification.Kind);
            Assert.Equal(reason, classification.Reason);
        }

        // Control: the census follows the file. The tail-less stream has no meshlet or cull element and the empty LOD
        // list and empty UV1 set of the quad are no elements, while the full stream has both tail elements and lod:1.
        if (name == "tailless")
        {
            Assert.DoesNotContain(coverage.Elements, static e => e.Identity is "meshlets" or "cull");
        }

        if (name == "full")
        {
            Assert.Contains(coverage.Elements, static e => e.Identity == "cull");
            Assert.Contains(coverage.Elements, static e => e.Identity == "lod:1");
            Assert.DoesNotContain(coverage.Elements, static e => e.Identity == "lod:2");
        }
    }

    [Fact]
    public void TheHeaderRow_RecordsTheWalkTheScaleAndTheWCensus()
    {
        var builder = StarfieldMeshFileLayoutTests.FullBuilder();
        var (bytes, written) = builder.BuildWithLayout();

        var document = Read(bytes).Document;

        var header = Assert.Single(document.NativeStates, static s => s.Kind == StarfieldMeshModelNativeState.HeaderKind);
        Assert.Equal(SceneElementKind.Document, header.Target.Kind);
        Assert.Equal(StarfieldMeshModelNativeState.PayloadVersion, header.Version);
        var payload = JsonNode.Parse(header.PayloadJson)!.AsObject();
        var sections = payload["sections"]!.AsArray()
            .Select(static s => (s!["name"]!.GetValue<string>(), s["offset"]!.GetValue<int>(), s["length"]!.GetValue<int>(),
                s["count"]!.GetValue<uint>()))
            .ToArray();
        Assert.Equal(written, sections);
        Assert.Equal("0x40800000", payload["scaleBits"]!.GetValue<string>()); // 4.0
        Assert.True(payload["meshletTail"]!.GetValue<bool>());
        Assert.Equal(1u, payload["firstNormalW"]!.GetValue<uint>());
        Assert.Equal(new[] { 0, 4, 0, 0 }, payload["normalW"]!.AsArray().Select(static n => n!.GetValue<int>()));
        Assert.Equal(new[] { 1, 0, 0, 3 }, payload["tangentW"]!.AsArray().Select(static n => n!.GetValue<int>()));
        Assert.Equal(new[] { 3, 0 }, payload["lodIndexCounts"]!.AsArray().Select(static n => n!.GetValue<int>()));
        Assert.Equal(1, payload["uv1NonFinite"]!.GetValue<int>());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), payload["sha256"]!.GetValue<string>());

        var quantization = Assert.Single(document.NativeStates, static s => s.Kind == StarfieldMeshModelNativeState.QuantizationKind);
        Assert.Equal(new SceneElementRef(SceneElementKind.Primitive, 0, 0), quantization.Target);
        Assert.Equal(20000, JsonNode.Parse(quantization.PayloadJson)!["quantizedMaxAbs"]!.GetValue<int>());
    }

    [Fact]
    public void TheMeshletAndCullRows_HoldEveryRecord_WithRawBytesOnlyAtFullDetail()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        builder.Meshlets = [(3, 0, 1, 0), (3, 3, 1, 4)];
        builder.Cull = [[1f, 2f, 3f, 0.5f, 0.25f, 0.125f], [-1f, -0f, 7.5f, 1e-3f, 2f, 3f]];
        var (bytes, written) = builder.BuildWithLayout();
        var meshletRecords = Records(bytes, written, "meshlets");
        var cullRecords = Records(bytes, written, "cull");

        var metadata = Read(bytes).Document;
        var full = Read(bytes, detail: ModelNativeDetail.Full).Document;

        var meshlets = Assert.Single(metadata.NativeStates, static s => s.Kind == StarfieldMeshModelNativeState.MeshletsKind);
        var payload = JsonNode.Parse(meshlets.PayloadJson)!.AsObject();
        Assert.Equal(2, payload["count"]!.GetValue<int>());
        Assert.Equal(new uint[] { 3, 3 }, payload["vertexCount"]!.AsArray().Select(static n => n!.GetValue<uint>()));
        Assert.Equal(new uint[] { 0, 3 }, payload["vertexOffset"]!.AsArray().Select(static n => n!.GetValue<uint>()));
        Assert.Equal(new uint[] { 1, 1 }, payload["triangleCount"]!.AsArray().Select(static n => n!.GetValue<uint>()));
        Assert.Equal(new uint[] { 0, 4 }, payload["triangleOffset"]!.AsArray().Select(static n => n!.GetValue<uint>()));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(meshletRecords)), payload["recordsSha256"]!.GetValue<string>());

        var cull = Assert.Single(metadata.NativeStates, static s => s.Kind == StarfieldMeshModelNativeState.CullKind);
        var records = JsonNode.Parse(cull.PayloadJson)!["records"]!.AsArray()
            .SelectMany(static r => r!.AsArray().Select(static v => BitConverter.SingleToUInt32Bits(v!.GetValue<float>())))
            .ToArray();
        Assert.Equal(builder.Cull.SelectMany(static r => r).Select(BitConverter.SingleToUInt32Bits), records);

        // Raw bytes: absent at metadata detail, the exact section records at full detail.
        Assert.False(meshlets.HasRawContent);
        Assert.False(cull.HasRawContent);
        var fullMeshlets = Assert.Single(full.NativeStates, static s => s.Kind == StarfieldMeshModelNativeState.MeshletsKind);
        var fullCull = Assert.Single(full.NativeStates, static s => s.Kind == StarfieldMeshModelNativeState.CullKind);
        Assert.True(fullMeshlets.HasRawContent);
        Assert.Equal(meshletRecords, fullMeshlets.CopyRawContent());
        Assert.Equal(cullRecords, fullCull.CopyRawContent());
        Assert.Equal(written.Single(static s => s.Name == "cull").Offset, fullCull.SourceLocation!.ByteOffset);
    }

    [Fact]
    public void ATailLessStream_HasNoMeshletOrCullRow()
    {
        var document = Read(StarfieldMeshTestBuilder.Quad(tail: false).Build()).Document;

        Assert.DoesNotContain(document.NativeStates, static s =>
            s.Kind is StarfieldMeshModelNativeState.MeshletsKind or StarfieldMeshModelNativeState.CullKind);
        var header = JsonNode.Parse(document.NativeStates[0].PayloadJson)!;
        Assert.False(header["meshletTail"]!.GetValue<bool>());
        Assert.Equal(0u, header["firstNormalW"]!.GetValue<uint>());
    }

    private static StarfieldMeshTestBuilder Builder(string name)
    {
        return name switch
        {
            "full" => StarfieldMeshFileLayoutTests.FullBuilder(),
            "quad" => StarfieldMeshTestBuilder.Quad(),
            "tailless" => StarfieldMeshTestBuilder.Quad(tail: false),
            "version0" => StarfieldMeshTestBuilder.Quad(version: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown builder.")
        };
    }

    private static byte[] Records(byte[] bytes, IReadOnlyList<(string Name, int Offset, int Length, uint Count)> written,
        string name)
    {
        var section = written.Single(s => s.Name == name);
        return bytes[(section.Offset + 4)..(section.Offset + section.Length)];
    }
}
