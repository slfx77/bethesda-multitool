using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Core.Modeling.Starfield;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     Bucket B for the cut-2 Starfield <c>.mesh</c> reader (plan sections 6 and 7, slices 0 to 5): every row of the
///     cover manifest resolved from the retail archives by size and SHA-256, then compared with the checked-in
///     expectations of the independent Python decoder (<see cref="Cut2MeshExpectations" />): the probe on every row, hop
///     A1 (header fields, the section walk, the W census, the stream names and bytes, the meshlet and cull values), hop A2
///     (the coverage census) and hop A3 (positions, normals, tangents, both UV sets and the triangles of every LOD), each
///     with the control that must fail: an altered SHA-256 digit, the legacy two-rounding positions, the engine's W map,
///     the stored BGRA order, one flipped index byte, a stream with its UV1 set toggled.
/// </summary>
/// <remarks>
///     A row skips only when every archive it names is absent from this machine (<see cref="Cut2MeshFixtureResolver.IsAbsent" />);
///     with Starfield installed, an entry the archive layer does not find, or bytes that do not reproduce the pin, fail.
///     Nothing here runs Python. The expectations were generated from the same archives
///     (<c>starfield_mesh_cover.py expectations</c>, 79 records, 0 unresolved).
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Cut2MeshOracleTests
{
    /// <summary>Every manifest row.</summary>
    public static TheoryData<string, string> AllRows()
    {
        return Cut2MeshCoverManifest.Rows();
    }

    /// <summary>The cover and edge files.</summary>
    public static TheoryData<string, string> MeshRows()
    {
        return Cut2MeshCoverManifest.MeshRows();
    }

    /// <summary>The retail decline controls.</summary>
    public static TheoryData<string, string> DeclineRows()
    {
        return Cut2MeshCoverManifest.DeclineRows();
    }

    [Theory]
    [MemberData(nameof(AllRows))]
    public void EveryRowResolvesToItsPinnedBytes_AndAnAlteredDigitDoesNot(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut2MeshCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);

        var bytes = Cut2MeshFixtureResolver.TryResolve(file, out var tried);
        var reason = string.Join("; ", tried);
        Assert.SkipWhen(bytes is null && Cut2MeshFixtureResolver.IsAbsent(tried),
            $"{file}: {reason}. " + RealAssetPaths.SkipMessage("the Starfield install (cut-2 .mesh cover)"));
        Assert.True(bytes is not null, $"{file}: {reason}");

        Assert.Equal(file.Size, bytes.LongLength);
        Assert.Equal(file.Sha256, Cut2MeshFixtureResolver.Sha256(bytes));
        var altered = file with { Sha256 = sha256[..^1] + (sha256[^1] == '0' ? '1' : '0') };
        Assert.Null(Cut2MeshFixtureResolver.TryResolve(altered, out var alteredTried));
        Assert.Contains(alteredTried,
            static t => t.StartsWith(Cut2MeshFixtureResolver.DigestMismatchPrefix, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AllRows))]
    public async Task Probe_AgreesWithTheIndependentProbeOnEveryRow(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut2MeshCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var expected = Cut2MeshExpectations.Require(file)["probe"]!.AsObject();
        var bytes = Cut2MeshFixtureResolver.Require(file);

        // The candidate Shared's own helper builds (64 KiB prefix, completeness from EOF), not a hand-made one.
        var source = new InMemoryAssetSource("cut2");
        var candidate = await ModelSourceCandidate.CreateAsync(
            new ModelSourceItem(source, source.Add("probe/" + file.Primary.Entry, bytes)));
        var result = new StarfieldMeshModelReader().Probe(candidate);

        Assert.Equal(expected["complete"]!.GetValue<bool>(), candidate.IsComplete);
        Assert.Equal(expected["kind"]!.GetValue<string>(), result.Kind.ToString());
        Assert.Equal(expected["confidence"]?.GetValue<string>() ?? nameof(ModelProbeConfidence.None),
            result.Confidence.ToString());
        var reason = expected["reason"]?.GetValue<string>();
        if (result.Kind == ModelProbeKind.Supported && result.Confidence == ModelProbeConfidence.Confirmed)
        {
            Assert.Equal(reason == "no meshlet tail",
                result.Evidence!.Description.EndsWith(StarfieldMeshModelProbe.NoTailNote, StringComparison.Ordinal));
        }

        if (result.Kind == ModelProbeKind.Unsupported)
        {
            Assert.Equal("truncated", reason);
        }
    }

    [Theory]
    [MemberData(nameof(DeclineRows))]
    public void DeclineControls_AreNotAModel_AndThoseThatPassTheVersionAreRefusedDeeper(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut2MeshCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var bytes = Cut2MeshFixtureResolver.Require(file);

        var result = Probe(bytes);

        Assert.Equal(ModelProbeKind.NotAModel, result.Kind);
        // The manifest's flag is checked against the bytes, so a control said to pass the version dword really does
        // (a version-only probe would claim it).
        Assert.Equal(file.PassesVersion, bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) <= 2);
    }

    [Theory]
    [MemberData(nameof(MeshRows))]
    public void A1_HeaderWalkStreamsAndNativeValuesAgreeWithTheIndependentDecoder(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, expected, _, result) = ReadRow(name, sha256);
        var document = result.Document;
        var header = expected["header"]!.AsObject();
        var counts = expected["counts"]!.AsObject();
        var mismatches = new List<string>();

        var native = JsonNode.Parse(Cut2MeshDigests.Row(document, StarfieldMeshModelNativeState.HeaderKind)!.PayloadJson)!;
        Compare(mismatches, "version", header["version"], native["version"]);
        Compare(mismatches, "indexCount", header["indexCount"], native["indexCount"]);
        Compare(mismatches, "vertexCount", header["vertexCount"], native["vertexCount"]);
        Compare(mismatches, "weightsPerVertex", header["weightsPerVertex"], native["weightsPerVertex"]);
        Compare(mismatches, "scaleBits", header["scaleBits"], native["scaleBits"]);
        Compare(mismatches, "tail", header["tail"], native["meshletTail"]);
        Compare(mismatches, "firstNormalW", header["firstNormalW"], native["firstNormalW"]);
        Compare(mismatches, "lodIndexCounts", header["lodIndexCounts"], native["lodIndexCounts"]);
        Compare(mismatches, "normalW", counts["normalW"], native["normalW"]);
        Compare(mismatches, "tangentW", counts["tangentW"], native["tangentW"]);
        Compare(mismatches, "sections", Sections(expected["sections"]!), Sections(native["sections"]!));
        Compare(mismatches, "byteLength", JsonValue.Create(file.Size), native["byteLength"]);

        var primitive = document.Meshes[0].Primitives[0];
        var streams = primitive.Attributes.Select(static s => s.Name).ToArray();
        var expectedStreams = expected["streams"]!.AsArray().Select(static s => s!.GetValue<string>()).ToArray();
        if (!streams.SequenceEqual(expectedStreams))
        {
            mismatches.Add($"streams: reader [{string.Join(", ", streams)}], oracle [{string.Join(", ", expectedStreams)}]");
        }

        var digests = Digests(document, expected);
        var oracle = expected["digests"]!.AsObject();
        foreach (var key in expectedStreams.Concat(["meshlets", "cull"]))
        {
            if (oracle[key]?.GetValue<string>() is { } want && digests.GetValueOrDefault(key) != want)
            {
                mismatches.Add($"{key}: reader {digests.GetValueOrDefault(key) ?? "absent"}, oracle {want}");
            }
        }

        Assert.True(mismatches.Count == 0, $"{file}: {mismatches.Count} A1 difference(s): {string.Join("; ", mismatches)}");
    }

    /// <summary>
    ///     The control for A1's color comparison: a reader that kept the stored B, G, R, A order would fail it, but only
    ///     on a row whose colors are not symmetric in red and blue. Measured from the independent decoder's expectations
    ///     (2026-09-28): 17 cover rows carry colors; on 11 of them every vertex has R == B, so both orders give the same
    ///     bytes and the row cannot discriminate; 6 rows can. The count is pinned so that a regenerated cover that loses
    ///     every discriminating row fails here instead of leaving the channel order untested on retail data.
    /// </summary>
    [Fact]
    public void ColorOrderControl_SixOfTheSeventeenColorRowsDistinguishTheStoredOrder()
    {
        var withColors = 0;
        var discriminating = 0;
        foreach (var record in Cut2MeshExpectations.Records.Values)
        {
            if (record["controls"]?["colorAsStoredDigest"]?.GetValue<string>() is not { } stored)
            {
                continue;
            }

            withColors++;
            var asRead = record["digests"]?[StarfieldMeshModelGeometry.ColorAttribute]?.GetValue<string>();
            Assert.NotNull(asRead);
            discriminating += stored == asRead ? 0 : 1;
        }

        Assert.Equal(17, withColors);
        Assert.Equal(6, discriminating);
    }

    [Theory]
    [MemberData(nameof(MeshRows))]
    public void A2_TheCoverageIsTheIndependentSectionList(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, expected, bytes, result) = ReadRow(name, sha256);
        var census = expected["census"]!.AsArray().Select(static c => c!.GetValue<string>()).ToArray();
        var coverage = result.Coverage;

        Assert.Equal(census, coverage.Elements.Select(static e => e.Identity));
        Assert.Equal(census.Where(static c => c is "meshlets" or "cull"),
            coverage.Classifications.Where(static c => c.Kind == ModelSourceCoverageKind.NativeOnly)
                .Select(static c => c.ElementIdentity));
        Assert.Equal(0, coverage.DroppedCount);

        // Control: the reader's census follows the bytes, not a fixed list. The same stream with its UV1 set toggled
        // (zero half pairs added when it has none, the set removed when it has one) is read with exactly the uv1 element
        // added or removed, so it is no longer the oracle's list for these bytes.
        var toggled = StarfieldMeshModelTestSupport.Read(ToggleUv1(bytes, expected), path: file.Primary.Entry).Coverage
            .Elements.Select(static e => e.Identity).ToArray();
        var expectedToggled = census.ToList();
        if (!expectedToggled.Remove("uv1"))
        {
            var uv0 = expectedToggled.IndexOf("uv0");
            expectedToggled.Insert((uv0 >= 0 ? uv0 : expectedToggled.IndexOf("positions")) + 1, "uv1");
        }

        Assert.Equal(expectedToggled.ToArray(), toggled);
    }

    [Theory]
    [MemberData(nameof(MeshRows))]
    public void A3_EveryVertexArrayAndTriangleListAgreesWithTheIndependentDecoder(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var (file, expected, bytes, result) = ReadRow(name, sha256);
        var document = result.Document;
        SceneValidation.ValidateStructure(document);
        var oracle = expected["digests"]!.AsObject();
        var controls = expected["controls"]!.AsObject();

        var digests = Digests(document, expected);

        var geometryKeys = oracle.Select(static p => p.Key)
            .Where(static k => k is "indices" or "positions" or "normals" or "tangents" or "uv0" or "uv1" ||
                               k.StartsWith("lod.", StringComparison.Ordinal))
            .ToList();
        var mismatches = geometryKeys
            .Where(k => digests.GetValueOrDefault(k) != oracle[k]!.GetValue<string>())
            .Select(k => $"{k}: reader {digests.GetValueOrDefault(k) ?? "absent"}, oracle {oracle[k]}")
            .ToList();
        // The reader states no array the oracle does not.
        mismatches.AddRange(digests.Keys.Where(k => oracle[k] is null).Select(static k => $"{k}: reader only"));
        Assert.True(mismatches.Count == 0, $"{file}: {mismatches.Count} A3 difference(s): {string.Join("; ", mismatches)}");

        // Control 1: the engine's W map (glTF's w negated) reproduces the oracle's swapped digest, never the real one.
        if (document.Meshes[0].Primitives[0].Tangents is { } tangents)
        {
            var swapped = Cut2MeshDigests.Tangents(tangents, -1f);
            Assert.Equal(controls["swappedTangentsDigest"]!.GetValue<string>(), swapped);
            Assert.NotEqual(oracle["tangents"]!.GetValue<string>(), swapped);
        }

        // Control 2: the legacy decode (the renderer's two roundings) is what the oracle's legacy route says, and it
        // differs from the reader's positions exactly on the files where that route differs.
        var legacy = StarfieldMeshFile.Parse(bytes);
        Assert.NotNull(legacy);
        var legacyDigest = Cut2MeshDigests.Floats(legacy.Positions);
        Assert.Equal(controls["legacyPositionsDigest"]!.GetValue<string>(), legacyDigest);
        Assert.Equal(controls["legacyPositionComponentsDiffer"]!.GetValue<int>() > 0,
            legacyDigest != oracle["positions"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(MeshRows))]
    public void TheLegacyDecoderParsesEveryCoverFile_TailLessOnesIncluded(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut2MeshCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var bytes = Cut2MeshFixtureResolver.Require(file);

        var mesh = StarfieldMeshFile.Parse(bytes);

        // Slice 0: the 8 tail-less cover files returned null before the optional-tail fix.
        Assert.NotNull(mesh);
        Assert.Equal(bytes.Length, mesh.BytesConsumed);
        Assert.Equal(file.Tail, mesh.HasMeshletTail);
    }

    [Fact]
    public void A3Controls_DiscriminateOnTheCover()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var meshes = Cut2MeshCoverManifest.Files.Where(static f => f.IsMesh).ToList();
        Assert.SkipWhen(meshes.Count == 0, "The cut-2 manifest is not in this checkout.");

        // The legacy-position control can fail somewhere: the oracle finds the routes different on most cover files.
        var differing = meshes.Count(static f =>
            Cut2MeshExpectations.Require(f)["controls"]!["legacyPositionComponentsDiffer"]!.GetValue<int>() > 0);
        Assert.True(differing > meshes.Count / 2, $"only {differing} of {meshes.Count} files separate the position routes");

        // One flipped index byte (a bit that keeps the index in range) is seen by the index comparison and nowhere else.
        var file = meshes[0];
        var bytes = (byte[])Cut2MeshFixtureResolver.Require(file).Clone();
        var mesh = StarfieldMeshFile.Parse(bytes)!;
        var vertexCount = mesh.QuantizedPositions.Length / 3;
        var at = Enumerable.Range(0, mesh.Indices.Length).First(i => (mesh.Indices[i] ^ 1) < vertexCount);
        bytes[8 + at * 2] ^= 0x01;
        var expected = Cut2MeshExpectations.Require(file);
        var flipped = Digests(StarfieldMeshModelTestSupport.Read(bytes, path: file.Primary.Entry).Document, expected);
        var oracle = expected["digests"]!.AsObject();
        Assert.NotEqual(oracle["indices"]!.GetValue<string>(), flipped["indices"]);
        Assert.Equal(oracle["positions"]!.GetValue<string>(), flipped["positions"]);
    }

    /// <summary>
    ///     The typed tangent w is glTF's handedness across the cover: <c>cross(N, T) x w</c> runs along glTF's bitangent,
    ///     -dP/dv of the stored UVs (<see cref="StarfieldMeshModelReaderTests.GltfUvBitangents" />), on 96% of the UV-framed
    ///     vertices (the expectations' frame counts sum to 301,980 of 314,182). The control is the engine's sign (w
    ///     negated), which agrees on 12,202 only.
    /// </summary>
    [Fact]
    public void TheTangentW_AgreesWithTheGltfUvFrame_AcrossTheCover()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        long valid = 0, agree = 0, swapped = 0, oracleAgree = 0, oracleValid = 0;
        foreach (var file in Cut2MeshCoverManifest.Files.Where(static f => f.IsMesh))
        {
            var expected = Cut2MeshExpectations.Require(file);
            if (expected["frame"] is not JsonObject frame)
            {
                continue;
            }

            var primitive = Primary(Read(file, Cut2MeshFixtureResolver.Require(file)).Document);
            var bitangents = StarfieldMeshModelReaderTests.GltfUvBitangents(primitive);
            for (var i = 0; i < primitive.Vertices.Count; i++)
            {
                var normal = primitive.Vertices[i].Normal;
                var tangent = primitive.Tangents!.Values[i];
                var direction = new Vector3(tangent.X, tangent.Y, tangent.Z);
                if (bitangents[i].Length() <= 1e-9f || direction.Length() <= 0.01f || normal.Length() <= 0.01f)
                {
                    continue;
                }

                valid++;
                var dot = Vector3.Dot(Vector3.Cross(normal, direction), bitangents[i]);
                agree += Math.Sign(dot) == Math.Sign(tangent.W) ? 1 : 0;
                swapped += Math.Sign(dot) == -Math.Sign(tangent.W) ? 1 : 0;
            }

            oracleAgree += frame["agree"]!.GetValue<long>();
            oracleValid += frame["valid"]!.GetValue<long>();
        }

        Assert.SkipWhen(valid == 0, "No cover file resolved.");
        // Plan section 0.2 measured 94.25% agreement on a 3,604-file sample for the stored sign (code 3 = +1) against
        // +dP/dv, which is the same agreement for glTF's sign (code 3 = -1) against -dP/dv. The cover agrees the same
        // way, the engine's sign (the control) does not, and the reader's frame count tracks the oracle's.
        Assert.True(agree > 9 * swapped, $"agree {agree}, swapped {swapped}, valid {valid}");
        Assert.InRange(Math.Abs(agree - oracleAgree), 0, Math.Max(1, oracleValid / 100));
    }

    private static (Cut2MeshCoverFile File, JsonObject Expected, byte[] Bytes, ModelReadResult Result) ReadRow(
        string name, string sha256)
    {
        var file = Cut2MeshCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var expected = Cut2MeshExpectations.Require(file);
        var bytes = Cut2MeshFixtureResolver.Require(file);
        return (file, expected, bytes, Read(file, bytes));
    }

    /// <summary>
    ///     The stream with its UV1 set toggled, placed by the oracle's own section list: n zero half pairs written when the
    ///     set is empty, the set removed when it is not. Every section is count-prefixed and the tail holds no absolute
    ///     offsets, so the result is a valid stream whose only difference is its UV1 set.
    /// </summary>
    private static byte[] ToggleUv1(byte[] bytes, JsonObject expected)
    {
        var uv1 = expected["sections"]!.AsArray().Single(static s => s!["name"]!.GetValue<string>() == "uv1")!;
        var offset = uv1["offset"]!.GetValue<int>();
        var count = uv1["count"]!.GetValue<int>();
        var vertices = expected["header"]!["vertexCount"]!.GetValue<int>();
        var written = count == 0 ? vertices : 0;
        var result = new byte[bytes.Length - 4 * count + 4 * written];
        bytes.AsSpan(0, offset).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), (uint)written);
        bytes.AsSpan(offset + 4 + 4 * count).CopyTo(result.AsSpan(offset + 4 + 4 * written));
        return result;
    }

    private static ModelReadResult Read(Cut2MeshCoverFile file, byte[] bytes)
    {
        return StarfieldMeshModelTestSupport.Read(bytes, path: file.Primary.Entry);
    }

    private static ScenePrimitive Primary(ModelDocument document)
    {
        return StarfieldMeshModelTestSupport.Primary(document);
    }

    private static ModelProbeResult Probe(byte[] bytes)
    {
        return StarfieldMeshModelTestSupport.Probe(bytes);
    }

    private static Dictionary<string, string> Digests(ModelDocument document, JsonObject expected)
    {
        var header = expected["header"]!.AsObject();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, count) in new[]
                 {
                     ("normals", "normalsCount"), ("tangents", "tangentsCount"), ("uv0", "uv0Count"), ("uv1", "uv1Count")
                 })
        {
            if (header[count]!.GetValue<int>() > 0)
            {
                present.Add(key);
            }
        }

        return Cut2MeshDigests.Compute(document, header["lodIndexCounts"]!.AsArray().Count, present);
    }

    private static JsonNode Sections(JsonNode sections)
    {
        return new JsonArray(sections.AsArray().Select(static s => (JsonNode?)JsonValue.Create(
            $"{s!["name"]}@{s["offset"]}+{s["length"]}x{s["count"]}")).ToArray());
    }

    private static void Compare(List<string> mismatches, string field, JsonNode? expected, JsonNode? actual)
    {
        var want = expected?.ToJsonString() ?? "null";
        var got = actual?.ToJsonString() ?? "null";
        if (!string.Equals(want, got, StringComparison.Ordinal))
        {
            mismatches.Add($"{field}: reader {got}, oracle {want}");
        }
    }
}
