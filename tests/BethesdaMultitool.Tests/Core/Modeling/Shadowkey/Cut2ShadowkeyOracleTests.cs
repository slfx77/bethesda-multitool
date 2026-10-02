using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Shadowkey.ShadowkeyModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     Bucket B for the cut-2 Shadowkey readers (plan sections 8 and 9): every manifest row resolved from the retail
///     application directory by size and SHA-256, then compared with the checked-in expectations of the independent Python
///     decoder (<see cref="Cut2ShadowkeyExpectations" />): both probes on every row and control, hop A1 (header fields,
///     the section walk, the document shape, the diagnostics), hop A2 (the coverage census) and hop A3 (every typed array
///     by its digest; per placement the matrix; the blocked-vertex count through the document's own matrices), each with
///     the control that must fail; and hop B's shape on every multi-skin row (the GLB writer converts the record, and
///     unselected exclusive morph tracks are reported without changing selected playback).
/// </summary>
/// <remarks>
///     Rows skip only when the application directory is absent (<see cref="Root" />);
///     with it present, a missing file or other bytes fail. Nothing here runs Python.
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Cut2ShadowkeyOracleTests
{
    public static TheoryData<string, string> SlotRows()
    {
        return Cut2ShadowkeyCoverManifest.SlotRows();
    }

    public static TheoryData<string, string> ZoneRows()
    {
        return Cut2ShadowkeyCoverManifest.ZoneRows();
    }

    public static TheoryData<string, string> DeclineRows()
    {
        return Cut2ShadowkeyCoverManifest.DeclineRows();
    }

    public static TheoryData<string, string> MultiSkinSlotRows()
    {
        return Cut2ShadowkeyCoverManifest.MultiSkinSlotRows();
    }

    /// <summary>The Bucket-B guard, then the application directory (or a skip naming it).</summary>
    private static string Root()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        return Cut2ShadowkeyFixtureResolver.RequireRoot();
    }

    [Fact]
    public void ThePackPins_Resolve_AndAnAlteredDigitDoesNot()
    {
        var root = Root();
        foreach (var (name, pin) in Cut2ShadowkeyCoverManifest.Cover.Pack)
        {
            Cut2ShadowkeyFixtureResolver.RequireFile(root, name, pin);
        }

        var huge = Cut2ShadowkeyCoverManifest.Cover.Pack["models.huge"];
        var altered = huge with { Sha256 = huge.Sha256[..^1] + (huge.Sha256[^1] == '0' ? '1' : '0') };
        Assert.Null(Cut2ShadowkeyFixtureResolver.TryReadFile(root, "models.huge", altered, out var reason));
        Assert.StartsWith(Cut2ShadowkeyFixtureResolver.DigestMismatchPrefix, reason, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SlotRows))]
    public async Task SlotProbe_AgreesWithTheIndependentProbe(string name, string sha256)
    {
        var root = Root();
        var row = Cut2ShadowkeyCoverManifest.RequireSlot(name);
        Assert.Equal(sha256, row.Sha256);
        var bytes = Cut2ShadowkeyFixtureResolver.Require(root, row);
        var expected = Cut2ShadowkeyExpectations.Require(sha256, name)["probe"]!.AsObject();

        // The candidate Shared's own helper builds (64 KiB prefix, completeness from EOF), not a hand-made one.
        var source = new InMemoryAssetSource("cut2");
        var candidate = await ModelSourceCandidate.CreateAsync(new ModelSourceItem(source, source.Add(row.Entry, bytes)));
        var result = new ShadowkeyMeshModelReader().Probe(candidate);

        Assert.Equal(expected["complete"]!.GetValue<bool>(), candidate.IsComplete);
        Assert.Equal(expected["kind"]!.GetValue<string>(), result.Kind.ToString());
        Assert.Equal(expected["confidence"]!.GetValue<string>(), result.Confidence.ToString());
        Assert.Equal(expected["evidence"]!.GetValue<string>(), result.Evidence!.Description);
    }

    [Theory]
    [MemberData(nameof(DeclineRows))]
    public void DeclineControls_AreNotAModelUnderBothProbes(string name, string sha256)
    {
        var root = Root();
        var row = Cut2ShadowkeyCoverManifest.RequireDecline(name);
        Assert.Equal(sha256, row.Sha256);
        var bytes = Cut2ShadowkeyFixtureResolver.Require(root, row);
        var path = row.Path ?? "models.huge-slot.bin";

        Assert.Equal(ModelProbeKind.NotAModel, new ShadowkeyMeshModelReader().Probe(Candidate(bytes, path)).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, new ShadowkeyZoneModelReader().Probe(Candidate(bytes, path)).Kind);
        var expected = Cut2ShadowkeyExpectations.Require(sha256, name);
        Assert.Equal("NotAModel", expected["probe"]!["kind"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(SlotRows))]
    public void SlotDocument_A1A2A3_AgreeWithTheIndependentDecoder(string name, string sha256)
    {
        var root = Root();
        var row = Cut2ShadowkeyCoverManifest.RequireSlot(name);
        var bytes = Cut2ShadowkeyFixtureResolver.Require(root, row);
        var expected = Cut2ShadowkeyExpectations.Require(sha256, name);

        var result = ReadMesh(bytes, path: row.Entry);
        var document = result.Document;
        var mismatches = new List<string>();

        // A1: the header row, the section walk, the document shape and the diagnostics.
        SceneValidation.ValidateStructure(document);
        var header = JsonNode.Parse(document.NativeStates
            .Single(static s => s.Kind == ShadowkeyModelNativeState.MeshHeaderKind).PayloadJson)!;
        var eh = expected["header"]!;
        Compare(mismatches, "frames", eh["frames"], header["frames"]);
        Compare(mismatches, "vertices", eh["vertices"], header["vertices"]);
        Compare(mismatches, "uvCount", eh["uvCount"], header["uvCount"]);
        Compare(mismatches, "faceCount", eh["faceCount"], header["faceCount"]);
        Compare(mismatches, "skins", eh["skins"], header["skins"]);
        Compare(mismatches, "width", eh["width"], header["width"]);
        Compare(mismatches, "height", eh["height"], header["height"]);
        Compare(mismatches, "textureHeader", eh["textureHeader"], header["textureHeader"]);
        Compare(mismatches, "closed", eh["closed"], header["closed"]);
        Compare(mismatches, "signedVolumeX6", eh["signedVolumeX6"], header["signedVolumeX6"]);
        var sections = new JsonArray(header["sections"]!.AsArray()
            .Select(static s => (JsonNode?)new JsonArray(JsonValue.Create(s!["name"]!.GetValue<string>()),
                JsonValue.Create(s["offset"]!.GetValue<int>()), JsonValue.Create(s["length"]!.GetValue<int>())))
            .ToArray());
        Compare(mismatches, "sections", expected["sections"], sections);
        var sequences = JsonNode.Parse(document.NativeStates
            .Single(static s => s.Kind == ShadowkeyModelNativeState.MeshSequencesKind).PayloadJson)!["sequences"]!.AsArray()
            .Select(static s => (JsonNode?)new JsonArray(JsonValue.Create(s!["start"]!.GetValue<int>()),
                JsonValue.Create(s["end"]!.GetValue<int>()), JsonValue.Create(s["rate"]!.GetValue<int>()))).ToArray();
        Compare(mismatches, "sequences", eh["sequences"], new JsonArray(sequences));
        var shape = expected["document"]!;
        Compare(mismatches, "nodes", shape["nodes"], document.Nodes.Count);
        Compare(mismatches, "meshes", shape["meshes"], document.Meshes.Count);
        Compare(mismatches, "layerSets", shape["layerSets"], document.LayerSets.Count);
        Compare(mismatches, "animations", shape["animations"], document.Animations.Count);
        Compare(mismatches, "images", shape["images"], document.Images.Count);
        var primitive = document.Meshes[0].Primitives[0];
        Compare(mismatches, "targets", shape["targets"], primitive.MorphTargets.Count);
        Compare(mismatches, "vertexCount", shape["vertexCount"], primitive.Vertices.Count);
        Compare(mismatches, "diagnostics",
            expected["diagnostics"], new JsonArray(document.Diagnostics.Select(static d => d.Code).Distinct()
                .Order(StringComparer.Ordinal).Select(static c => (JsonNode?)JsonValue.Create(c)).ToArray()));

        // A2: the coverage census and its classes.
        var coverage = new JsonArray(result.Coverage.Classifications
            .Select(static c => (JsonNode?)new JsonArray(JsonValue.Create(c.ElementIdentity), JsonValue.Create(c.Kind.ToString())))
            .ToArray());
        Compare(mismatches, "coverage", expected["coverage"], coverage);

        // A3: every typed array by its digest.
        var digests = expected["digests"]!;
        Compare(mismatches, "positions", digests["positions"], Float32Digest(Positions(primitive)));
        Compare(mismatches, "texCoords", digests["texCoords"], Float32Digest(TexCoords(primitive)));
        Compare(mismatches, "pointIndices", digests["pointIndices"], Int32Digest(primitive.PointIndices!.Values));
        Compare(mismatches, "pointCount", digests["pointCount"], primitive.PointIndices.PointCount);
        Compare(mismatches, "triangles", digests["triangles"], Int32Digest(primitive.Indices));
        Compare(mismatches, "targets", digests["targets"],
            primitive.MorphTargets.Count == 0 ? null : Float32Digest(Targets(primitive)));
        var frames = JsonNode.Parse(document.NativeStates
            .Single(static s => s.Kind == ShadowkeyModelNativeState.MeshFramesKind).PayloadJson)!;
        var unused = digests["unusedVertices"]!;
        Compare(mismatches, "unused vertices", unused["indices"], frames["unusedVertices"]);
        var unusedStream = document.NativeStates.Where(static s => s.Kind == ShadowkeyModelNativeState.MeshUnusedPositionsKind)
            .SelectMany(static s => JsonNode.Parse(s.PayloadJson)!["values"]!.AsArray().Select(static v => v!.GetValue<int>()))
            .ToList();
        Compare(mismatches, "unused positions", unused["positions"],
            unusedStream.Count == 0 ? null : Int32Digest(unusedStream));
        var skins = digests["skins"]!.AsArray();
        Compare(mismatches, "skin count", skins.Count, document.Images.Count);
        for (var k = 0; k < Math.Min(skins.Count, document.Images.Count); k++)
        {
            var png = StandardPng(document.Images[k]);
            Compare(mismatches, $"skin {k} original", skins[k]!["original"], document.Images[k].Source!.Original!.Sha256);
            Compare(mismatches, $"skin {k} rgba", skins[k]!["rgba"], Digest(png.Rgba()));
            Compare(mismatches, $"skin {k} colors", skins[k]!["colors"], png.ColorType == 3 ? png.Palette.Length / 3 : -1);
        }

        var clips = digests["clips"]!.AsArray();
        Compare(mismatches, "clip count", clips.Count, document.Animations.Count);
        for (var c = 0; c < Math.Min(clips.Count, document.Animations.Count); c++)
        {
            var clip = document.Animations[c];
            var track = clip.MorphTracks[0];
            Compare(mismatches, $"clip {c} name", clips[c]!["name"], clip.Name);
            Compare(mismatches, $"clip {c} keys", clips[c]!["keys"], track.Times.Count);
            Compare(mismatches, $"clip {c} rate", clips[c]!["rate"], (int)clip.Timing!.RawRate!.Value);
            Compare(mismatches, $"clip {c} tracks", clips[c]!["tracks"], clip.MorphTracks.Count);
            Compare(mismatches, $"clip {c} times", clips[c]!["times"], Float32Digest(track.Times));
            Compare(mismatches, $"clip {c} weights", clips[c]!["weights"], Float32Digest(track.Weights));
            Assert.All(clip.MorphTracks, t => Assert.Equal(track.Weights, t.Weights));
        }

        Assert.True(mismatches.Count == 0, $"{name}: {string.Join("; ", mismatches)}");

        // Control: one position word changed in a copy changes the positions digest (unless the vertex is unused).
        var altered = bytes.ToArray();
        altered[14] ^= 0x01;
        var alteredPrimitive = ReadMesh(altered, path: row.Entry).Document.Meshes[0].Primitives[0];
        if (primitive.PointIndices.Values.Contains(0))
        {
            Assert.NotEqual(digests["positions"]!.GetValue<string>(), Float32Digest(Positions(alteredPrimitive)));
        }
    }

    /// <summary>
    ///     Hop B's shape on every multi-skin cover row (cut-2 review finding 1; rows 20, 22, 56, 59, 63, 69 and 230): the
    ///     Shared GLB writer converts the record with its default layers. With a track on every skin, it reports only
    ///     the unselected exclusive tracks as omitted while saved GLB keys, weights and clip durations remain intact.
    /// </summary>
    [Theory]
    [MemberData(nameof(MultiSkinSlotRows))]
    public async Task MultiSkinSlot_WritesAGlb_AndPreservesPlaybackWhenExclusiveTracksAreOmitted(string name, string sha256)
    {
        var root = Root();
        var row = Cut2ShadowkeyCoverManifest.RequireSlot(name);
        Assert.Equal(sha256, row.Sha256);
        var document = ReadMesh(Cut2ShadowkeyFixtureResolver.Require(root, row), path: row.Entry).Document;
        Assert.True(document.LayerSets.Count > 1, $"{name} has {document.LayerSets.Count} skin layer sets.");
        var directory = Directory.CreateTempSubdirectory("bmt-cut2-shadowkey-hop-b-").FullName;
        try
        {
            var (rows, written, refusal) = await WriteGlbAsync(document, directory, "default");
            Assert.True(refusal is null, $"{name}: {refusal}");
            Assert.Equal(ModelItemOutcome.Converted, written!.Outcome);
            Assert.DoesNotContain(rows, static r => r.ReasonCode == "draw.morph-target-suppressed");
            AssertGlbMorphPlayback(document, Path.Combine(directory, "default.glb"), 0);

            var everyDocument = WithTracksOnEverySkin(document);
            var (everyRows, everyWritten, everyRefusal) = await WriteGlbAsync(everyDocument, directory, "every-skin");
            Assert.Null(everyRefusal);
            Assert.Equal(ModelItemOutcome.Converted, everyWritten!.Outcome);
            AssertExclusiveMorphOmissions(everyDocument, everyRows, Enumerable.Range(1, document.Nodes.Count - 1).ToArray());
            AssertGlbMorphPlayback(everyDocument, Path.Combine(directory, "every-skin.glb"), 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(ZoneRows))]
    public async Task ZoneProbe_AgreesWithTheIndependentProbe(string name, string sha256)
    {
        var root = Root();
        var row = Cut2ShadowkeyCoverManifest.RequireZone(name);
        var bytes = Cut2ShadowkeyFixtureResolver.RequireFile(root, row.Stem + ".zmp", row.Files[row.Stem + ".zmp"]);
        var expected = Cut2ShadowkeyExpectations.Require(sha256, name)["probe"]!.AsObject();

        var source = new InMemoryAssetSource("cut2");
        var candidate = await ModelSourceCandidate.CreateAsync(new ModelSourceItem(source, source.Add(row.Stem + ".zmp", bytes)));
        var result = new ShadowkeyZoneModelReader().Probe(candidate);

        Assert.Equal(expected["complete"]!.GetValue<bool>(), candidate.IsComplete);
        Assert.Equal(expected["kind"]!.GetValue<string>(), result.Kind.ToString());
        Assert.Equal(expected["confidence"]!.GetValue<string>(), result.Confidence.ToString());
        Assert.Equal(expected["evidence"]!.GetValue<string>(), result.Evidence!.Description);
    }

    [Theory]
    [MemberData(nameof(ZoneRows))]
    public void ZoneDocument_A1A2A3_AgreeWithTheIndependentDecoder(string name, string sha256)
    {
        var root = Root();
        var row = Cut2ShadowkeyCoverManifest.RequireZone(name);
        foreach (var (file, pin) in row.Files)
        {
            Cut2ShadowkeyFixtureResolver.RequireFile(root, file, pin);
        }

        var expected = Cut2ShadowkeyExpectations.Require(sha256, name);
        var result = ReadRetailZone(root, row.Stem);
        var document = result.Document;
        var mismatches = new List<string>();
        SceneValidation.ValidateStructure(document);

        // A1: the grid and its header row.
        var header = JsonNode.Parse(document.NativeStates
            .Single(static s => s.Kind == ShadowkeyModelNativeState.ZoneHeaderKind).PayloadJson)!;
        var grid = expected["grid"]!;
        foreach (var field in new[] { "name", "author", "description", "width", "height" })
        {
            Compare(mismatches, "grid " + field, grid[field], header[field]);
        }

        // A3: the terrain by the independent face rule.
        var terrain = document.Meshes.Single(static m => m.Name.EndsWith(" terrain", StringComparison.Ordinal));
        foreach (var (kind, facts) in expected["terrain"]!.AsObject())
        {
            var primitive = terrain.Primitives.SingleOrDefault(p => p.Name == kind);
            Compare(mismatches, $"terrain {kind} quads", facts!["quads"], primitive is null ? 0 : primitive.Vertices.Count / 4);
            Compare(mismatches, $"terrain {kind} positions", facts["positions"],
                primitive is null ? null : Float32Digest(Positions(primitive)));
        }

        // A1/A3: the palette, its key and its light table.
        var palette = Assert.Single(document.Palettes);
        var ep = expected["palette"]!;
        Compare(mismatches, "palette original", ep["original"], palette.OriginalSha256);
        Compare(mismatches, "palette entries", ep["entries"],
            Digest(palette.Entries.SelectMany(static e => new[] { e.R, e.G, e.B, e.A }).ToArray()));
        Compare(mismatches, "palette transparent", ep["transparent"],
            new JsonArray(palette.TransparentIndices.Select(static i => (JsonNode?)JsonValue.Create(i)).ToArray()));
        Compare(mismatches, "zlu", ep["zlu"], Assert.Single(palette.AuxiliaryTables).Sha256);

        // A3: the textures, stored and top row first.
        var textures = expected["textures"]!.AsArray();
        var images = document.Images.Where(static i => i.Source?.Container == ShadowkeyModelImages.TextureContainer).ToList();
        Compare(mismatches, "texture count", textures.Count, images.Count);
        for (var n = 0; n < Math.Min(textures.Count, images.Count); n++)
        {
            Compare(mismatches, $"texture {n} original", textures[n]!["original"], images[n].Source!.Original!.Sha256);
            Compare(mismatches, $"texture {n} indices", textures[n]!["indices"], Digest(StandardPng(images[n]).Samples));
        }

        // A3: the sky as a mesh record.
        var sky = expected["sky"]!;
        var skyMesh = document.Meshes.Single(static m => m.Name.EndsWith(" sky", StringComparison.Ordinal));
        var skyPrimitive = skyMesh.Primitives[0];
        Compare(mismatches, "sky positions", sky["digests"]!["positions"], Float32Digest(Positions(skyPrimitive)));
        Compare(mismatches, "sky texCoords", sky["digests"]!["texCoords"], Float32Digest(TexCoords(skyPrimitive)));
        Compare(mismatches, "sky triangles", sky["digests"]!["triangles"], Int32Digest(skyPrimitive.Indices));
        var skyImage = document.Images[skyPrimitive.MaterialIndex is { } m ? document.Materials[m].Texture!.Value.ImageIndex : -1];
        Compare(mismatches, "sky image original", sky["digests"]!["skins"]![0]!["original"], skyImage.Source!.Original!.Sha256);
        Compare(mismatches, "sky image rgba", sky["digests"]!["skins"]![0]!["rgba"], Digest(StandardPng(skyImage).Rgba()));

        // A3: every placement's matrix, and the blocked-vertex count through the document's own matrices.
        var cells = JsonNode.Parse(document.NativeStates
            .Single(static s => s.Kind == ShadowkeyModelNativeState.ZoneCellsKind).PayloadJson)!;
        var width = cells["width"]!.GetValue<int>();
        var height = cells["height"]!.GetValue<int>();
        var flags = cells["flags"]!.AsArray().Select(static f => f!.GetValue<int>()).ToArray();
        var nodes = document.Nodes.Where(static n => n.Name.StartsWith("ent:", StringComparison.Ordinal))
            .ToDictionary(static n => n.Name, StringComparer.Ordinal);
        var blocked = 0;
        var blockedLegacy = 0;
        var placements = expected["placements"]!["rows"]!.AsArray();
        foreach (var placement in placements)
        {
            var fields = placement!.AsArray();
            var index = fields[0]!.GetValue<int>();
            var resolved = fields[2]!.GetValue<int>() == 1;
            var identity = ShadowkeyZoneComposition.EntityIdentity(index);
            Compare(mismatches, identity + " resolved", resolved, nodes.ContainsKey(identity));
            if (!resolved || !nodes.TryGetValue(identity, out var node))
            {
                continue;
            }

            var matrix = node.LocalTransform;
            var m00 = (float)fields[8]!.GetValue<double>();
            var m01 = (float)fields[9]!.GetValue<double>();
            var quarter = fields[7]!.GetValue<int>() % ShadowkeyModelUnits.QuarterTurn == 0;
            if (!Close(matrix.M11, m00, quarter) || !Close(matrix.M12, m01, quarter) || matrix.M31 != matrix.M12 ||
                matrix.M32 != -matrix.M11 || matrix.M23 != fields[3]!.GetValue<int>() / 256f ||
                matrix.M41 != fields[4]!.GetValue<int>() || matrix.M42 != fields[5]!.GetValue<int>() ||
                matrix.M43 != fields[6]!.GetValue<int>())
            {
                mismatches.Add($"{identity} matrix ({matrix.M11}, {matrix.M12}) against ({m00}, {m01})");
            }

            var placed = document.Meshes[document.Nodes[Assert.Single(node.Children)].MeshIndex!.Value].Primitives[0];
            foreach (var point in Points(placed))
            {
                blocked += Blocked(point, matrix.M11, matrix.M12, matrix.M31, matrix.M32, matrix.M41, matrix.M42,
                    width, height, flags);
                blockedLegacy += Blocked(point, matrix.M11, -matrix.M12, matrix.M12, matrix.M11, matrix.M41, matrix.M42,
                    width, height, flags);
            }
        }

        var expectedBlocked = expected["blockedVertices"]!;
        Compare(mismatches, "blocked vertices", expectedBlocked["document"], blocked);
        Compare(mismatches, "blocked vertices (legacy map)", expectedBlocked["legacy"], blockedLegacy);

        // A2 and the diagnostics.
        var coverage = new JsonArray(result.Coverage.Classifications
            .Select(static c => (JsonNode?)new JsonArray(JsonValue.Create(c.ElementIdentity), JsonValue.Create(c.Kind.ToString())))
            .ToArray());
        Compare(mismatches, "coverage", expected["coverage"], coverage);
        Compare(mismatches, "diagnostics", expected["diagnostics"],
            new JsonArray(document.Diagnostics.Select(static d => d.Code).Distinct().Order(StringComparer.Ordinal)
                .Select(static c => (JsonNode?)JsonValue.Create(c)).ToArray()));

        Assert.True(mismatches.Count == 0, $"{name}: {string.Join("; ", mismatches)}");
    }

    [Fact]
    public void AcrossTheZoneCover_TheLegacyMapBlocksMoreVertices_AndASwappedYawIsCaught()
    {
        _ = Root();
        Assert.SkipWhen(Cut2ShadowkeyExpectations.Path is null, "The cut-2 Shadowkey expectations are not in this checkout.");
        var zones = Cut2ShadowkeyCoverManifest.Cover.Zones.Select(static z => Cut2ShadowkeyExpectations.Records[z.Sha256])
            .ToList();

        var document = zones.Sum(static z => z["blockedVertices"]!["document"]!.GetValue<int>());
        var legacy = zones.Sum(static z => z["blockedVertices"]!["legacy"]!.GetValue<int>());
        Assert.True(legacy > document, $"legacy {legacy} against {document}");

        // A yaw of the other sign changes m01 on an odd quarter turn (exact, so no libm rounding enters).
        var turned = zones.SelectMany(static z => z["placements"]!["rows"]!.AsArray())
            .Select(static r => r!.AsArray()).First(static r => r[2]!.GetValue<int>() == 1 &&
                                                                Math.Abs(r[7]!.GetValue<int>() % 32768) == 16384);
        var (m00, m01) = ShadowkeyModelUnits.RotationEntries(-turned[7]!.GetValue<int>(), (ushort)turned[3]!.GetValue<int>());
        Assert.Equal((float)turned[8]!.GetValue<double>(), m00);
        Assert.NotEqual((float)turned[9]!.GetValue<double>(), m01);
    }

    /// <summary>Reads a retail zone through the real directory, as the shell reads a loose <c>.zmp</c>.</summary>
    private static ModelReadResult ReadRetailZone(string root, string stem)
    {
        var source = new FolderAssetSource(root);
        var path = Path.Combine(root, stem + ".zmp");
        var item = new ModelSourceItem(source,
            new AssetEntry(new AssetReference(source.Id, stem + ".zmp"), new FileInfo(path).Length, Provenance: path));
        using var input = File.OpenRead(path);
        var context = new ModelReadContext(item, input, new NoCacheScope());
        return new ShadowkeyZoneModelReader().Read(item, context, CancellationToken.None);
    }

    /// <summary>The frame-0 position of every record vertex some face names (each point once).</summary>
    private static IEnumerable<Vector3> Points(ScenePrimitive primitive)
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < primitive.Vertices.Count; i++)
        {
            if (seen.Add(primitive.PointIndices!.Values[i]))
            {
                yield return primitive.Vertices[i].Position;
            }
        }
    }

    /// <summary>1 when the point lands in a blocked or off-grid cell (the oracle's rule, in binary64).</summary>
    private static int Blocked(Vector3 point, float m11, float m12, float m31, float m32, float m41, float m42,
        int width, int height, int[] flags)
    {
        var qx = (double)point.X * m11 + (double)point.Y * 0.0 + (double)point.Z * m31 + m41;
        var qy = (double)point.X * m12 + (double)point.Y * 0.0 + (double)point.Z * m32 + m42;
        var ix = (int)Math.Floor(qx / 256);
        var iy = (int)Math.Floor(qy / 256);
        return ix < 0 || iy < 0 || ix >= width || iy >= height || (flags[iy * width + ix] & 2) != 0 ? 1 : 0;
    }

    /// <summary>Exact on a quarter turn; within one float32 step otherwise (the libm cosine may differ in its last bit).</summary>
    private static bool Close(float actual, float expected, bool exact)
    {
        return exact ? actual == expected : Math.Abs(actual - expected) <= MathF.BitIncrement(Math.Abs(expected)) - Math.Abs(expected);
    }

    private static void Compare(List<string> mismatches, string field, JsonNode? expected, object? actual)
    {
        var actualNode = actual switch
        {
            null => null,
            JsonNode node => node,
            string text => JsonValue.Create(text),
            int number => JsonValue.Create(number),
            bool flag => JsonValue.Create(flag),
            _ => JsonValue.Create(actual.ToString())
        };
        if (!JsonNode.DeepEquals(expected, actualNode))
        {
            mismatches.Add($"{field}: expected {expected?.ToJsonString() ?? "null"}, read {actualNode?.ToJsonString() ?? "null"}");
        }
    }
}
