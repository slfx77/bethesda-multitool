using System.Text.Json.Nodes;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Starfield.StarfieldMeshModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     Default-suite checks of the cut-2 cover (plan section 8, slice 1): the checked-in manifest's shape and counts, the
///     expectations' pairing with it, the parser's refusals on synthetic manifests, the resolver's byte verification, and
///     the synthetic A1/A3 oracle: the reader's document of four builder streams, digested by
///     <see cref="Cut2MeshDigests" />, against digests the independent Python tool computed for the same bytes (pinned
///     below; regenerate with <c>starfield_mesh_cover.py</c>'s <c>file_expectation</c> on the probe writer's identical
///     stream). The tangent digests carry glTF's w (-1 for code 3, +1 for code 0); the engine's reading is the control.
/// </summary>
public class Cut2MeshCoverManifestTests
{
    [Fact]
    public void TheManifest_IsThePairwiseCoverWithItsEdgesAndDeclineControls()
    {
        Assert.SkipWhen(Cut2MeshCoverManifest.Path is null, $"{Cut2MeshCoverManifest.RelativePath} is not in this checkout.");
        var files = Cut2MeshCoverManifest.Files;

        Assert.Equal(60, files.Count(static f => f.Role == Cut2MeshCoverFile.CoverRole));
        Assert.Equal(7, files.Count(static f => f.Role == Cut2MeshCoverFile.EdgeRole));
        Assert.Equal(12, files.Count(static f => f.Role == Cut2MeshCoverFile.DeclineRole));
        // Plan section 8: 8 tail-less files, 26 with LOD lists (25 cover, 1 edge), weightsPerVertex 0 to 8 all present.
        Assert.Equal(8, files.Count(static f => f.Tail == false));
        Assert.Equal(26, files.Count(static f => f.LodCount > 0));
        for (var weights = 0; weights <= 8; weights++)
        {
            var cell = $"|w{weights}|";
            Assert.Contains(files, f => f.Cell is { } c && c.Contains(cell, StringComparison.Ordinal));
        }

        // Six decline controls pass the version dword, so only a deeper check refuses them.
        Assert.Equal(6, files.Count(static f => f.PassesVersion));
        Assert.All(files.Where(static f => f.IsMesh), static f =>
            Assert.StartsWith("geometries/", f.Primary.Entry, StringComparison.Ordinal));
    }

    [Fact]
    public void TheExpectations_PairOneToOneWithTheManifest()
    {
        Assert.SkipWhen(Cut2MeshCoverManifest.Path is null || Cut2MeshExpectations.Path is null,
            "The cut-2 manifest or expectations are not in this checkout.");
        var files = Cut2MeshCoverManifest.Files;
        var records = Cut2MeshExpectations.Records;

        Assert.Equal(files.Select(static f => f.Sha256).Order(StringComparer.Ordinal),
            records.Keys.Order(StringComparer.Ordinal));
        foreach (var file in files)
        {
            var record = records[file.Sha256];
            Assert.Null(record["unresolved"]);
            Assert.Equal(file.Size, record["size"]!.GetValue<long>());
            var probe = record["probe"]!.AsObject();
            if (file.IsMesh)
            {
                Assert.Equal("Supported", probe["kind"]!.GetValue<string>());
                Assert.NotNull(record["digests"]!["positions"]);
                Assert.NotNull(record["digests"]!["indices"]);
                Assert.Equal(file.Tail, record["header"]!["tail"]!.GetValue<bool>());
            }
            else
            {
                Assert.Equal("NotAModel", probe["kind"]!.GetValue<string>());
                Assert.Null(record["digests"]);
            }
        }
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("uncovered")]
    [InlineData("sha")]
    [InlineData("duplicateName")]
    [InlineData("duplicateSha")]
    [InlineData("source")]
    [InlineData("declineExpectsSupported")]
    [InlineData("size")]
    public void Parse_RefusesAManifestThatBreaksARule(string rule)
    {
        var manifest = SyntheticManifest();
        Assert.Equal(2, Cut2MeshCoverManifest.Parse(manifest.ToJsonString()).Count);
        var file = manifest["files"]![0]!.AsObject();
        var control = manifest["declineControls"]![0]!.AsObject();
        switch (rule)
        {
            case "schema":
                manifest["schema"] = "cut2-starfield-mesh-cover/1";
                break;
            case "uncovered":
                manifest["uncoveredItems"] = new JsonArray("0:v2");
                break;
            case "sha":
                file["sha256"] = "0A" + new string('0', 62);
                break;
            case "duplicateName":
                control["name"] = file["name"]!.GetValue<string>();
                break;
            case "duplicateSha":
                control["sha256"] = file["sha256"]!.GetValue<string>();
                break;
            case "source":
                file["source"] = Cut2MeshCoverManifest.SourcePrefix + "Starfield - Meshes02.ba2";
                break;
            case "declineExpectsSupported":
                control["expect"] = "Supported";
                break;
            case "size":
                file["size"] = 0;
                break;
        }

        Assert.Throws<InvalidDataException>(() => Cut2MeshCoverManifest.Parse(manifest.ToJsonString()));
    }

    [Fact]
    public void Verify_RejectsOtherBytes_ForTheSameEntry()
    {
        var bytes = StarfieldMeshTestBuilder.Quad().Build();
        var sha = Cut2MeshFixtureResolver.Sha256(bytes);

        Assert.True(Cut2MeshFixtureResolver.Verify(bytes, bytes.Length, sha, "quad", out _));
        // Control: the same path read from an archive that holds other bytes (a tail-less copy) is rejected by digest,
        // and a truncated copy by size.
        var other = StarfieldMeshTestBuilder.Quad(tail: false).Build();
        Assert.False(Cut2MeshFixtureResolver.Verify(other, other.Length, sha, "quad", out var digestReason));
        Assert.StartsWith(Cut2MeshFixtureResolver.DigestMismatchPrefix, digestReason, StringComparison.Ordinal);
        Assert.False(Cut2MeshFixtureResolver.Verify(bytes[..^1], bytes.Length, sha, "quad", out var sizeReason));
        Assert.StartsWith("size ", sizeReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResolverSkipsOnlyWhenEveryCandidateArchiveIsAbsent()
    {
        var absent = Cut2MeshFixtureResolver.AbsentPrefix + "A.ba2 :: geometries/a/b.mesh #3";

        Assert.True(Cut2MeshFixtureResolver.IsAbsent([absent, absent]));
        // Controls: with Starfield installed, an entry the archive layer does not find, or other bytes, is a failure,
        // not a skip, even beside an absent candidate; and an empty list proves nothing.
        Assert.False(Cut2MeshFixtureResolver.IsAbsent(
            [absent, Cut2MeshFixtureResolver.EntryNotFoundPrefix + "B.ba2 :: geometries/a/b.mesh #9"]));
        Assert.False(Cut2MeshFixtureResolver.IsAbsent(
            [Cut2MeshFixtureResolver.DigestMismatchPrefix + "A.ba2 :: geometries/a/b.mesh #3"]));
        Assert.False(Cut2MeshFixtureResolver.IsAbsent(["size 10, not 180: A.ba2 :: geometries/a/b.mesh #3"]));
        Assert.False(Cut2MeshFixtureResolver.IsAbsent([]));
    }

    [Fact]
    public void ExpectationsParse_RefusesARepeatedDigestAndAnotherSchema()
    {
        const string header = """{"schema":"cut2-starfield-mesh-expectations/1","rules":{}}""";
        const string record = """{"schema":"cut2-starfield-mesh-expectations/1","sha256":"ab","size":1}""";

        Assert.Single(Cut2MeshExpectations.Parse([header, record]));
        Assert.Throws<InvalidDataException>(() => Cut2MeshExpectations.Parse([header, record, record]));
        Assert.Throws<InvalidDataException>(() =>
            Cut2MeshExpectations.Parse([record.Replace("/1", "/0", StringComparison.Ordinal)]));
    }

    public static TheoryData<string> SyntheticStreams()
    {
        return new TheoryData<string> { "quad", "tailless", "full", "emptyTail" };
    }

    [Theory]
    [MemberData(nameof(SyntheticStreams))]
    public void TheReadersDigests_EqualThePythonOraclesOnTheSameBytes(string stream)
    {
        var (builder, sha, digests) = Synthetic(stream);
        var bytes = builder.Build();
        Assert.Equal(sha, Cut2MeshFixtureResolver.Sha256(bytes));

        var document = Read(bytes).Document;
        var present = new HashSet<string>(StringComparer.Ordinal) { "normals", "tangents", "uv0" };
        if (builder.Uv1 is not null)
        {
            present.Add("uv1");
        }

        var computed = Cut2MeshDigests.Compute(document, builder.Lods.Length, present);

        Assert.Equal(digests.OrderBy(static p => p.Key, StringComparer.Ordinal),
            computed.OrderBy(static p => p.Key, StringComparer.Ordinal));

        // Control: the engine's tangent map (w negated) and the legacy positions do not reproduce the oracle's digests.
        Assert.NotEqual(digests["tangents"], Cut2MeshDigests.Tangents(document.Meshes[0].Primitives[0].Tangents!, -1f));
        var legacy = BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry.StarfieldMeshFile.Parse(bytes)!.Positions;
        Assert.NotEqual(digests["positions"], Cut2MeshDigests.Floats(legacy));
    }

    /// <summary>The builder streams and the Python oracle's SHA-256 of their bytes and their digests.</summary>
    private static (StarfieldMeshTestBuilder Builder, string Sha, Dictionary<string, string> Digests) Synthetic(
        string stream)
    {
        const string indices = "fe78c65211dd0b56a97024fb61111e686ef1fe054aa132ba58e2891ac496f1ee";
        const string positions = "c9ecab0c58dd786077a9d2cece82ae03dc9bf817defed23977a7782c2548cff4";
        const string uv0 = "eeb394f1726eac2bb9656979b5b3190f0fc2bfc9a3df8e179d13da2ebc8c513a";
        const string meshlets = "9eda69073c7d3efd5c3b8ccdf5d3cbad0a7261c88731a44db7d157551f6ab227";
        const string cull = "f501143438c020f5a0e926caa1d082ef0c4ebb9bbda0dbf3583736775f55d22c";
        const string quadNormals = "3f2318833ba1b2110c1095312ef268e9fe273b71ef14438158b96f0e062de4a9";
        const string quadTangents = "3e54110fd06da60627f4ff1da5963b6f4d5d8261938874e0565e1f417f26179e";
        const string noBytes = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        return stream switch
        {
            "quad" => (StarfieldMeshTestBuilder.Quad(),
                "4d94b02d74a65ce4959a698dfaa15c9549cba57b98bb07d6e23cd01b8260d1bd",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["indices"] = indices, ["positions"] = positions, ["normals"] = quadNormals,
                    ["tangents"] = quadTangents, ["uv0"] = uv0, ["meshlets"] = meshlets, ["cull"] = cull
                }),
            "tailless" => (StarfieldMeshTestBuilder.Quad(tail: false),
                "d922c974025366a98fdcf443c6326aa88ae020f0e3bceaecd348d477ff799166",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["indices"] = indices, ["positions"] = positions, ["normals"] = quadNormals,
                    ["tangents"] = quadTangents, ["uv0"] = uv0
                }),
            "full" => (StarfieldMeshFileLayoutTests.FullBuilder(),
                "f844800a4de5ad6546b797dca0d642386af61c447c3dfbba8863748b2ce12166",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["indices"] = indices,
                    ["positions"] = positions,
                    ["normals"] = "5cb33bc8a607942f6fb1994bf5a6f803de0a967a97f37ee98c069abb53e91c06",
                    ["tangents"] = "6fb26c411b38eb1277d62d39dd6a301c3c5c33a4da7266240990bc7265eff784",
                    ["uv0"] = uv0,
                    ["uv1"] = "cedc13c643bfb6e4143fbfec3b066f03f9c931cebee30220da4e8ccea734fdaa",
                    ["starfield.uv1.raw"] = "fa4fcd3514f5dc07df5016a56bbc2d2c3fcf00777e4c73817a27ec48ce18d3cb",
                    ["starfield.color"] = "1d5f34e4e59b3d54aeb2d15042e5cdf39c5513ebc64e6a8f38353d83cd76753e",
                    ["starfield.bone.0"] = "810171eb2c1c149cc7a8e4da3d9a5639d8f8f4028ada239c5c75885ea912bffd",
                    ["starfield.weight.0"] = "35569532f64d46841e6b20f5dc2af01ed36fb3235c93e9d0f7360796280d5e8a",
                    ["starfield.bone.1"] = "e8cf0ede4819fd28e02aec0b4424003d0cea18b925d36f4deea6586985bde7ce",
                    ["starfield.weight.1"] = "7d93d50eaec4f1c60fba606dd99629e405003a585e5b34e912f46bf461f23393",
                    ["lod.1"] = "ad5dc1478de06a4c2728ea528bd9361a4b945e92a414bf4d180cedaaeaa5f4cc",
                    ["lod.2"] = noBytes,
                    ["meshlets"] = meshlets,
                    ["cull"] = cull
                }),
            // The tail with no records (review finding 7): both digests are stated, as the SHA-256 of no bytes.
            "emptyTail" => (EmptyTail(),
                "eaba8aa8da007022ae42424c45bd474aa2b52c1c4319c54552138a1db048392d",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["indices"] = indices, ["positions"] = positions, ["normals"] = quadNormals,
                    ["tangents"] = quadTangents, ["uv0"] = uv0, ["meshlets"] = noBytes, ["cull"] = noBytes
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(stream), stream, "Unknown stream.")
        };
    }

    /// <summary>The quad with the meshlet and cull tail present but holding no records.</summary>
    private static StarfieldMeshTestBuilder EmptyTail()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        builder.Meshlets = [];
        builder.Cull = [];
        return builder;
    }

    /// <summary>A minimal valid manifest: one file row and one decline control.</summary>
    private static JsonObject SyntheticManifest()
    {
        return new JsonObject
        {
            ["schema"] = Cut2MeshCoverManifest.Schema,
            ["uncoveredItems"] = new JsonArray(),
            ["files"] = new JsonArray(new JsonObject
            {
                ["role"] = "cover", ["name"] = "cover 01", ["source"] = Cut2MeshCoverManifest.SourcePrefix + "A.ba2",
                ["archive"] = "A.ba2", ["entry"] = "geometries/a/b.mesh", ["index"] = 3, ["size"] = 180,
                ["sha256"] = new string('a', 64), ["cell"] = "v2|w0|lod0|nocol|nouv1|m1", ["tail"] = true,
                ["lodCount"] = 0, ["tags"] = new JsonArray("normalW:1"),
                ["alsoIn"] = new JsonArray(new JsonObject
                {
                    ["source"] = Cut2MeshCoverManifest.SourcePrefix + "B.ba2", ["archive"] = "B.ba2",
                    ["entry"] = "geometries/a/b.mesh", ["index"] = 9
                })
            }),
            ["declineControls"] = new JsonArray(new JsonObject
            {
                ["role"] = "decline", ["name"] = "control extension:nif", ["kind"] = "extension:nif",
                ["source"] = Cut2MeshCoverManifest.SourcePrefix + "A.ba2", ["archive"] = "A.ba2",
                ["entry"] = "meshes/a.nif", ["index"] = 4, ["size"] = 100, ["sha256"] = new string('b', 64),
                ["expect"] = "NotAModel", ["passesVersion"] = false
            })
        };
    }
}
