using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B for cut-1c slice 5 (plan section 8, row 5; section 9): hops A6 and A2 over every static <c>.3D</c> row of
///     the cover manifest, against the independent gate-1c oracle <c>tools/scripts/gate1c/xngine_probe.py</c> (driven
///     through <c>xngine_probe_json.py</c>), which shares no code with the reader; the A6 control (one flipped UV byte is
///     detected); and the probe's answer on every manifest row, decline and edge controls included.
/// </summary>
/// <remarks>
///     A6 compares, per row: the payload digest; every header field; every plane's count, unknown byte, key and header
///     tail; per drawn plane every corner's position (Y negated), normal (/256, Y negated), source point index and stored
///     u and v in the reader's reversed face order, and the triangle stream (the probe's reference triangles, reversed);
///     the omitted planes and emptied keys and the primitive order; the plane-data digest; the drawn planes whose corners
///     repeat a source point (slice-5 review finding 1: the <c>bmt.xngine.uv-rule</c> count and ordinals against the
///     probe's corners); the whole stored-UV stream (drawn and omitted planes) by the probe's own digest; and the points no
///     drawn plane names against <c>bmt.xngine.unreferenced-points</c> (slice-6 review finding 2: their indices and
///     stored coordinates; 3D.BS6 ESPEAR.3D has 9). A2 compares the coverage census and its NativeOnly set with the
///     element list the probe's header counts and M-T tiling predict, and the point list's reason with the probe's
///     unreferenced points. A row whose container is not on this machine
///     skips; a machine without Python and numpy skips with that reason.
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Cut1cXnGineOracleTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _owned = [];

    /// <summary>The static .3D rows, as (row name, payload SHA-256).</summary>
    public static TheoryData<string, string> StaticRows()
    {
        return Cut1cXnGineCover.StaticRows();
    }

    /// <summary>Every manifest row.</summary>
    public static TheoryData<string, string> AllRows()
    {
        return Cut1cCoverManifest.Rows();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in _owned)
        {
            await owned.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(StaticRows))]
    public async Task A6_EveryFieldAgreesWithTheIndependentProbe(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var read = await Cut1cXnGineCover.ReadAsync(file, ModelNativeDetail.Full, _owned);
        var probe = Cut1cXnGineCover.Probe(read.Bytes, read.PlaneHeaderLength, file.ToString());

        var mismatches = CompareA6(read.Result.Document, probe, read.PlaneHeaderLength);

        Assert.True(mismatches.Count == 0,
            $"{file} ({read.Route}): {mismatches.Count} A6 difference(s): {string.Join("; ", mismatches.Take(20))}");
        Assert.Equal(file.Sha256, probe["payload_sha256"]!.GetValue<string>());
        Assert.False(probe["shape_test_3dc"]!.GetValue<bool>());
    }

    /// <summary>
    ///     The A6 control: ARCH3D 44005 with one stored u byte flipped (plane 0, corner 1; the offset computed from the
    ///     header) is probed again, and the comparison against the reader's document of the UNFLIPPED record reports
    ///     exactly the stored-UV differences, so hop A6 can see a one-byte UV change.
    /// </summary>
    [Fact]
    public async Task A6Control_AFlippedUvByteIsDetected()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require("Daggerfall 44005");
        var read = await Cut1cXnGineCover.ReadAsync(file, ModelNativeDetail.Full, _owned);
        var flipped = (byte[])read.Bytes.Clone();
        var planeList = BinaryPrimitives.ReadInt32LittleEndian(flipped.AsSpan(60));
        var at = planeList + 8 + 8 + 4;
        flipped[at] ^= 0x01;

        var original = Cut1cXnGineCover.Probe(read.Bytes, 8, file.ToString());
        var doctored = Cut1cXnGineCover.Probe(flipped, 8, file + " (flipped)");

        Assert.Empty(CompareA6(read.Result.Document, original, 8));
        var differences = CompareA6(read.Result.Document, doctored, 8);
        Assert.Contains(differences, d => d.StartsWith("plane 0 corner", StringComparison.Ordinal) && d.Contains("uv16"));
        Assert.Contains(differences, d => d.StartsWith("payload", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.StartsWith("uv stream", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(StaticRows))]
    public async Task A2_TheCoverageIsTheIndependentElementList(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var read = await Cut1cXnGineCover.ReadAsync(file, ModelNativeDetail.Metadata, _owned);
        var probe = Cut1cXnGineCover.Probe(read.Bytes, read.PlaneHeaderLength, file.ToString());
        var (expected, nativeOnly) = ExpectedCensus(probe);
        var coverage = read.Result.Coverage;
        var unreferenced = UnreferencedPoints(probe);

        Assert.Equal(expected, coverage.Elements.Select(e => e.Identity).ToArray());
        Assert.Equal(unreferenced.Length == 0 ? null : XnGineModelCoverage.UnreferencedPointsReason(unreferenced.Length),
            coverage.GetClassification(XnGineModelCoverage.PointsElement).Reason);
        Assert.Equal(nativeOnly.Order(StringComparer.Ordinal), coverage.Classifications
            .Where(c => c.Kind == ModelSourceCoverageKind.NativeOnly).Select(c => c.ElementIdentity)
            .Order(StringComparer.Ordinal));
        Assert.Equal(0, coverage.DroppedCount);

        // Control: the reader's census with one element removed is not the probe's list.
        var removed = coverage.Elements.Select(e => e.Identity).Where((_, i) => i != 3).ToArray();
        Assert.NotEqual(expected, removed);
    }

    /// <summary>
    ///     The probe on every manifest row, from the bytes the manifest pins: the static rows Supported (Tentative exactly
    ///     when the record exceeds the 64 KiB prefix: 451 and MENUA001), the .3DC rows NotAModel (the shape test), the
    ///     3dfx row Unsupported with the manifest's decline reason, the MZ stray and the empty segment NotAModel.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllRows))]
    public void Probe_AnswersEveryManifestRowAsThePlanSays(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var bytes = Cut1cFixtureResolver.Require(file);
        var length = Math.Min(bytes.Length, ModelSourceCandidate.MaximumProbeBytes);
        var candidate = new ModelSourceCandidate(
            new AssetEntry(new AssetReference("cut1c", "probe/" + file.Entry), bytes.Length), bytes.AsSpan(0, length),
            length == bytes.Length);

        var result = new XnGineModelReader().Probe(candidate);

        if (Cut1cXnGineCover.IsStaticMesh(file))
        {
            Assert.Equal(ModelProbeKind.Supported, result.Kind);
            Assert.Equal(bytes.Length > ModelSourceCandidate.MaximumProbeBytes
                ? ModelProbeConfidence.Tentative
                : ModelProbeConfidence.Confirmed, result.Confidence);
            Assert.StartsWith(XnGineModelFormatMetadata.DescribeVariant(file.Tag!,
                    string.Equals(file.Game, "Battlespire", StringComparison.Ordinal) ? 10 : 8),
                result.Evidence!.Description, StringComparison.Ordinal);
        }
        else if (file.Declined is { } declined && file.Tag is "v4.0" or "v5.0")
        {
            Assert.Equal(ModelProbeKind.Unsupported, result.Kind);
            Assert.Equal(declined, result.Reason);
        }
        else
        {
            Assert.Equal(ModelProbeKind.NotAModel, result.Kind);
        }
    }

    /// <summary>
    ///     The points no plane that yields a triangle names, ascending, from the probe's corners and <c>no_triangle</c>
    ///     flags alone (slice-6 review finding 2).
    /// </summary>
    private static int[] UnreferencedPoints(JsonObject probe)
    {
        var named = probe["planes"]!.AsArray().Select(p => p!.AsObject())
            .Where(p => !p["no_triangle"]!.GetValue<bool>())
            .SelectMany(p => p["corners"]!.AsArray().Select(c => c![0]!.GetValue<int>()))
            .ToHashSet();
        return Enumerable.Range(0, probe["header"]!["point_count"]!.GetValue<int>()).Where(p => !named.Contains(p))
            .ToArray();
    }

    /// <summary>The census the probe predicts: its header counts and tiling (plan section 3.3), and the NativeOnly subset.</summary>
    private static (string[] Elements, List<string> NativeOnly) ExpectedCensus(JsonObject probe)
    {
        var header = probe["header"]!.AsObject();
        var elements = new List<string> { "header", "points", "normals" };
        var nativeOnly = new List<string>();
        var planeCount = header["plane_count"]!.GetValue<int>();
        for (var k = 0; k < planeCount; k++)
        {
            elements.Add(string.Create(CultureInfo.InvariantCulture, $"plane:{k}"));
        }

        foreach (var plane in probe["no_triangle_planes"]!.AsArray())
        {
            nativeOnly.Add(string.Create(CultureInfo.InvariantCulture, $"plane:{plane!["plane"]!.GetValue<int>()}"));
        }

        var tiling = probe["tiling"]!.AsObject();
        if (tiling["plane_data_state"]!.GetValue<string>() == "fits")
        {
            elements.Add("plane-data");
            nativeOnly.Add("plane-data");
        }

        if (header["object_data_count"]!.GetValue<int>() > 0)
        {
            elements.Add("object-data");
            nativeOnly.Add("object-data");
        }

        foreach (var gap in tiling["gaps"]!.AsArray())
        {
            var id = string.Create(CultureInfo.InvariantCulture,
                $"unclaimed:{gap![0]!.GetValue<int>()}-{gap[1]!.GetValue<int>()}");
            elements.Add(id);
            nativeOnly.Add(id);
        }

        return ([.. elements], nativeOnly);
    }

    /// <summary>Every A6 difference between the reader's document and the probe's extraction (empty when they agree).</summary>
    private static List<string> CompareA6(ModelDocument document, JsonObject probe, int planeHeaderLength)
    {
        var differences = new List<string>();
        void Check(bool same, string what)
        {
            if (!same)
            {
                differences.Add(what);
            }
        }

        Check(probe["payload_sha256"]!.GetValue<string>() == document.SourceProvenance!.Sha256,
            "payload digest differs");
        var header = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.HeaderKind);
        var probeHeader = probe["header"]!.AsObject();
        (string Reader, string Probe)[] fields =
        [
            ("tag", "tag"), ("pointCount", "point_count"), ("planeCount", "plane_count"), ("radius", "radius"),
            ("plus16", "h16"), ("plus20", "h20"), ("planeDataOffset", "plane_data_offset"),
            ("objectDataOffset", "object_data_offset"), ("objectDataCount", "object_data_count"), ("plus36", "h36"),
            ("plus40", "h40"), ("plus44", "h44"), ("pointListOffset", "point_list_offset"),
            ("normalListOffset", "normal_list_offset"), ("plus56", "h56"), ("planeListOffset", "plane_list_offset")
        ];
        foreach (var (reader, oracle) in fields)
        {
            Check(header[reader]!.ToJsonString() == probeHeader[oracle]!.ToJsonString(), $"header {reader} differs");
        }

        Check(header["planeListEnd"]!.GetValue<int>() == probe["plane_list_end"]!.GetValue<int>(), "header planeListEnd differs");
        Check(header["recordLength"]!.GetValue<int>() == probe["length"]!.GetValue<int>(), "header recordLength differs");

        var planesRow = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.PlanesKind)["planes"]!.AsArray();
        var probePlanes = probe["planes"]!.AsArray();
        var points = probe["points"]!.AsArray();
        var normals = probe["normals"]!.AsArray();
        var drawn = DrawnPlanes(document);
        var omittedRow = XnGineModelTestSupport.Rows(document, XnGineModelNativeState.OmittedPlanesKind)
            .Select(row => JsonNode.Parse(row.PayloadJson)!.AsObject()).SingleOrDefault();
        var omittedByPlane = omittedRow?["planes"]!.AsArray()
            .ToDictionary(p => p!["ordinal"]!.GetValue<int>(), p => p!.AsObject()) ?? new Dictionary<int, JsonObject>();
        Check(planesRow.Count == probePlanes.Count, "plane count differs");
        using var uvStream = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> row = stackalloc byte[16];
        var uvRows = 0;
        for (var k = 0; k < Math.Min(planesRow.Count, probePlanes.Count); k++)
        {
            var mine = planesRow[k]!.AsObject();
            var theirs = probePlanes[k]!.AsObject();
            var count = theirs["count"]!.GetValue<int>();
            Check(mine["pointCount"]!.GetValue<int>() == count, $"plane {k} count differs");
            Check(mine["unknown1"]!.GetValue<int>() == theirs["unknown1"]!.GetValue<int>(), $"plane {k} unknown1 differs");
            Check(mine["textureKey"]!.GetValue<uint>() == theirs["key"]!.GetValue<uint>(), $"plane {k} key differs");
            var tail = mine["headerTail"]!.GetValue<string>();
            Check((planeHeaderLength == 10 ? tail[4..] : tail) == theirs["tail"]!.GetValue<string>(),
                $"plane {k} header tail differs");

            var corners = theirs["corners"]!.AsArray();
            var noTriangle = theirs["no_triangle"]!.GetValue<bool>();
            Check(noTriangle != drawn.ContainsKey(k), $"plane {k} drawn state differs (probe no_triangle {noTriangle})");
            Check(noTriangle == omittedByPlane.ContainsKey(k), $"plane {k} omitted-planes membership differs");
            var stored = new (int Point, int U, int V)[count];
            if (drawn.TryGetValue(k, out var plane))
            {
                var normal = normals[k]!.AsArray();
                var expectedNormal = new Vector3(normal[0]!.GetValue<int>() / 256f, -(float)normal[1]!.GetValue<int>() / 256f,
                    normal[2]!.GetValue<int>() / 256f);
                for (var j = 0; j < count; j++)
                {
                    var q = j == 0 ? 0 : count - j;
                    var corner = corners[q]!.AsArray();
                    var point = points[corner[0]!.GetValue<int>()]!.AsArray();
                    var expected = new Vector3(point[0]!.GetValue<int>(), -(float)point[1]!.GetValue<int>(),
                        point[2]!.GetValue<int>());
                    Check(plane.Vertices[j].Position == expected, $"plane {k} corner {q} position differs");
                    Check(plane.Vertices[j].Normal == expectedNormal, $"plane {k} corner {q} normal differs");
                    Check(plane.Points[j] == corner[0]!.GetValue<int>(), $"plane {k} corner {q} point index differs");
                    Check(plane.Uv[j] == (corner[1]!.GetValue<int>(), corner[2]!.GetValue<int>()),
                        $"plane {k} corner {q} uv16 differs");
                    stored[q] = (plane.Points[j], plane.Uv[j].U, plane.Uv[j].V);
                }

                var expectedTriangles = theirs["triangles"]!.AsArray()
                    .Select(t => (t![0]!.GetValue<int>(), t[2]!.GetValue<int>(), t[1]!.GetValue<int>())).ToList();
                var actualTriangles = plane.Triangles.Select(t =>
                    (Corner(count, t.A), Corner(count, t.B), Corner(count, t.C))).ToList();
                Check(expectedTriangles.SequenceEqual(actualTriangles), $"plane {k} triangle stream differs");
            }
            else if (omittedByPlane.TryGetValue(k, out var omitted))
            {
                var pts = omitted["points"]!.AsArray();
                var us = omitted["u"]!.AsArray();
                var vs = omitted["v"]!.AsArray();
                for (var q = 0; q < Math.Min(count, pts.Count); q++)
                {
                    stored[q] = (pts[q]!.GetValue<int>(), us[q]!.GetValue<int>(), vs[q]!.GetValue<int>());
                }
            }

            for (var q = 0; q < count; q++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(row, k);
                BinaryPrimitives.WriteInt32LittleEndian(row[4..], q);
                BinaryPrimitives.WriteInt32LittleEndian(row[8..], stored[q].Point);
                BinaryPrimitives.WriteInt16LittleEndian(row[12..], (short)stored[q].U);
                BinaryPrimitives.WriteInt16LittleEndian(row[14..], (short)stored[q].V);
                uvStream.AppendData(row);
                uvRows++;
            }
        }

        var uv = probe["uv_stream"]!.AsObject();
        Check(uvRows == uv["rows"]!.GetValue<int>() &&
              Convert.ToHexStringLower(uvStream.GetHashAndReset()) == uv["sha256"]!.GetValue<string>(),
            "uv stream digest differs");

        var emptied = probe["emptied_keys"]!.AsArray().Select(k => k!.GetValue<uint>()).ToHashSet();
        var omittedKeys = omittedRow?["omittedTextureKeys"]!.AsArray().Select(k => k!["textureKey"]!.GetValue<uint>())
            .ToHashSet() ?? new HashSet<uint>();
        Check(emptied.SetEquals(omittedKeys), "emptied texture keys differ");
        var order = probe["texture_keys_first_use"]!.AsArray().Select(k => k!.GetValue<uint>())
            .Where(k => !emptied.Contains(k)).Select(k => MaterialName(k, planeHeaderLength)).ToArray();
        Check(order.SequenceEqual(document.Materials.Select(m => m.Name)), "primitive order differs");

        // The drawn planes whose corners repeat a source point (slice-5 review finding 1), from the probe's corners.
        var probeRepeated = probePlanes.Select(p => p!.AsObject())
            .Where(p => !p["no_triangle"]!.GetValue<bool>() && RepeatsPoint(p["corners"]!.AsArray()))
            .Select(p => p["index"]!.GetValue<int>()).ToArray();
        var rule = XnGineModelTestSupport.Payload(document, XnGineModelNativeState.UvRuleKind);
        var readerRepeated = rule["repeatedPointPlaneOrdinals"] is JsonArray ordinals
            ? ordinals.Select(o => o!.GetValue<int>()).ToArray()
            : null;
        Check(readerRepeated is not null && probeRepeated.SequenceEqual(readerRepeated) &&
              rule["repeatedPointPlanes"]!.GetValue<int>() == probeRepeated.Length, "repeated-point planes differ");

        // The points no drawn plane names (slice-6 review finding 2) against the unreferenced-points row.
        var unreferenced = UnreferencedPoints(probe);
        var unreferencedRows = XnGineModelTestSupport.Rows(document, XnGineModelNativeState.UnreferencedPointsKind);
        if (unreferenced.Length == 0)
        {
            Check(unreferencedRows.Count == 0, "unreferenced-points row present only in the reader");
        }
        else if (unreferencedRows.Count != 1)
        {
            Check(false, "unreferenced-points row missing");
        }
        else
        {
            var listed = JsonNode.Parse(unreferencedRows[0].PayloadJson)!["points"]!.AsArray();
            var expectedPoints = unreferenced.Select(p => (p, points[p]!.AsArray().Select(v => v!.GetValue<int>()).ToArray()))
                .ToArray();
            var actualPoints = listed.Select(p => (p!["point"]!.GetValue<int>(),
                p["position"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray())).ToArray();
            Check(expectedPoints.Length == actualPoints.Length && expectedPoints.Zip(actualPoints).All(pair =>
                    pair.First.Item1 == pair.Second.Item1 && pair.First.Item2.SequenceEqual(pair.Second.Item2)),
                "unreferenced points differ");
        }

        var planeData = XnGineModelTestSupport.Rows(document, XnGineModelNativeState.PlaneDataKind);
        if (probe["plane_data"] is JsonObject probeData)
        {
            var payload = planeData.Count == 1 ? JsonNode.Parse(planeData[0].PayloadJson)!.AsObject() : null;
            Check(payload is not null && payload["sha256"]!.GetValue<string>() == probeData["sha256"]!.GetValue<string>() &&
                  payload["start"]!.GetValue<int>() == probeData["offset"]!.GetValue<int>(), "plane data differs");
        }
        else
        {
            Check(planeData.Count == 0, "plane data present only in the reader");
        }

        return differences;
    }

    /// <summary>True when two of a probe plane's corners (<c>[point, u, v]</c>) name the same source point.</summary>
    private static bool RepeatsPoint(JsonArray corners)
    {
        var points = corners.Select(c => c![0]!.GetValue<int>()).ToList();
        return points.Distinct().Count() < points.Count;
    }

    /// <summary>The source corner of a face-local vertex (the reader's reversed order), restated from the plan.</summary>
    private static int Corner(int count, int vertex)
    {
        return vertex == 0 ? 0 : count - vertex;
    }

    /// <summary>The legacy material name of a probe key, restated: (key &gt;&gt; 7, key &amp; 0x7F) or the u32's words.</summary>
    private static string MaterialName(uint key, int planeHeaderLength)
    {
        var (archive, record) = planeHeaderLength == 10 ? (key >> 16, key & 0xFFFF) : (key >> 7, key & 0x7F);
        return string.Create(CultureInfo.InvariantCulture, $"TEXTURE.{archive:D3}#{record}");
    }

    /// <summary>One drawn plane: its face's vertices, source points and stored UVs in face order, and its triangles.</summary>
    private sealed record DrawnPlane(
        IReadOnlyList<SceneVertex> Vertices,
        IReadOnlyList<int> Points,
        IReadOnlyList<(int U, int V)> Uv,
        IReadOnlyList<(int A, int B, int C)> Triangles);

    /// <summary>The document's drawn planes by source plane ordinal.</summary>
    private static Dictionary<int, DrawnPlane> DrawnPlanes(ModelDocument document)
    {
        var result = new Dictionary<int, DrawnPlane>();
        var geometry = XnGineLegacyComparison.ReaderPlanes(document);
        foreach (var primitive in document.Meshes.SelectMany(m => m.Primitives))
        {
            var ordinals = XnGineModelTestSupport.PlaneOrdinals(primitive);
            var uv = XnGineModelTestSupport.StoredUv(primitive);
            var faces = primitive.Faces!;
            var cursor = 0;
            for (var f = 0; f < faces.FaceCount; f++)
            {
                var size = faces.FaceSizes[f];
                var corners = Enumerable.Range(cursor, size).Select(c => faces.CornerIndices[c]).ToArray();
                result[ordinals[f]] = new DrawnPlane(geometry[ordinals[f]].Vertices,
                    corners.Select(c => primitive.PointIndices!.Values[c]).ToArray(),
                    Enumerable.Range(cursor, size).Select(c => ((int)uv[c].U, (int)uv[c].V)).ToArray(),
                    geometry[ordinals[f]].Triangles);
                cursor += size;
            }
        }

        return result;
    }
}
