using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.RealAsset.NifModelOracleSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A1 (design section 7.2; plan section 6, slices 5, 7, 8, 11 and 12) over every model of the cut-1a cover
///     manifest, big-endian files included: the fields the independent Python probe records are compared with what the
///     reader typed, one theory per field family, each keyed by block index (see <see cref="NifModelFieldComparisons" />
///     for the exact mapping of every family). Declined controls and <c>.kf</c> streams are skipped with a reason
///     naming hop A2.
/// </summary>
/// <remarks>
///     <para>
///         Families and their probe-to-reader mappings: nodes (name, hidden bit, rotation orthonormality and
///         determinant sign against the TRS rule, scale, billboard mode, the hidden layer set); materials
///         (NiMaterialProperty colors, glossiness, alpha and Emit Mult; NiAlphaProperty blend pairs, test function and
///         threshold, draw sort; NiStencilProperty; NiZBufferProperty; NiVertexColorProperty); shaders (type, both flag
///         words, environment-map scale, clamp mode as sampler wrapping, unlit, texture-set slots as layers and images,
///         File Name shaders); NiTexturingProperty (apply mode, maps, transforms, layer samplers); geometry streams
///         (vertex, UV-set and triangle counts, normals, colors, tangents, strip form); skins (instance type and links,
///         bone count, Has Vertex Weights, partition count, dismember body parts on the face stream); morphs (counts,
///         Relative Targets, frame names as targets).
///     </para>
///     <para>
///         Big-endian rows (the 95 X360 and PS3 files, read under the console platform of their manifest source): the
///         nodes, materials, shaders, texturing, skins and morphs families compare the same fields, because the probe
///         parses both byte orders and those blocks are laid out alike on every platform. The geometry family cannot
///         use the NiTri*Data Has flags there (0 on every retail packed block): a data block whose Additional Data
///         names a BSPackedAdditionalGeometryData block is compared through the probe's packed stream table, matched
///         against the measured layouts (<see cref="NifModelProbePackedLayouts" />), which gives the channel presence
///         (one UV set, authored normals, colors on L1/L4/L6, a tangent frame on all) and, for the skinned layouts, the
///         partition-sourced triangles; an inline console block (the 16 such shapes inside packed files, and every shape
///         of the 17 console files without a packed block) is compared exactly as a little-endian one.
///     </para>
///     <para>
///         Controls: each theory mutates one probe value of its family in a copy of the expectation (the design's own
///         A1 control for alpha, ONE/ONE rewritten to SRC_ALPHA/INV_SRC_ALPHA, for the blend family) and asserts the
///         same comparison reports a mismatch it did not report before; the geometry family adds, on a row whose
///         compared block is packed, a second mutation of the probe's packed stream table (the first stream's type),
///         under which no measured layout matches.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifModelFieldOracleTests
{
    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.Rows), MemberType = typeof(Cut1aCoverManifest))]
    public void Nodes_MatchTheProbe(string entry, string sha256)
    {
        var (file, expectation, document) = Prepare(entry, sha256);
        var comparison = Verify(file, "nodes", NifModelFieldComparisons.Nodes(document, expectation));

        // Control: one compared node's hidden bit flipped in the expectation is detected.
        var mutated = Cut1aProbeExpectations.Clone(expectation);
        var key = FirstComparedKey(mutated["avObjects"]!.AsObject(),
            BlockRows(document).Where(pair => pair.Value["node"] is JsonObject).Select(pair => pair.Key));
        var node = mutated["avObjects"]![key]!.AsObject();
        node["hidden"] = !Bool(node["hidden"]);
        AssertControl(file, comparison, NifModelFieldComparisons.Nodes(document, mutated), $"avObjects[{key}].hidden");
    }

    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.Rows), MemberType = typeof(Cut1aCoverManifest))]
    public void Materials_MatchTheProbe(string entry, string sha256)
    {
        var (file, expectation, document) = Prepare(entry, sha256);
        var comparison = Verify(file, "material properties", NifModelFieldComparisons.Materials(document, expectation));

        var mutated = Cut1aProbeExpectations.Clone(expectation);
        var description = MutateMaterials(mutated, document);
        AssertControl(file, comparison, NifModelFieldComparisons.Materials(document, mutated), description);
    }

    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.Rows), MemberType = typeof(Cut1aCoverManifest))]
    public void Shaders_MatchTheProbe(string entry, string sha256)
    {
        var (file, expectation, document) = Prepare(entry, sha256);
        var comparison = Verify(file, "shaders", NifModelFieldComparisons.Shaders(document, expectation));

        // Control: bit 0 (Specular) of one compared shader's first flag word flipped in the expectation is detected.
        var mutated = Cut1aProbeExpectations.Clone(expectation);
        var key = FirstComparedKey(mutated["shaders"]!.AsObject(), TypedBlocks(document, "shade"));
        var shader = mutated["shaders"]![key]!.AsObject();
        shader["flags1"] = Long(shader["flags1"]) ^ 1;
        AssertControl(file, comparison, NifModelFieldComparisons.Shaders(document, mutated), $"shaders[{key}].flags1");
    }

    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.Rows), MemberType = typeof(Cut1aCoverManifest))]
    public void Texturing_MatchesTheProbe(string entry, string sha256)
    {
        var (file, expectation, document) = Prepare(entry, sha256);
        var comparison = Verify(file, "NiTexturingProperty", NifModelFieldComparisons.Texturing(document, expectation));

        // Control: the first map's UV set of one compared property changed in the expectation is detected.
        var mutated = Cut1aProbeExpectations.Clone(expectation);
        var key = FirstComparedKey(mutated["texturing"]!.AsObject(), TypedBlocks(document, "texturing"));
        var maps = mutated["texturing"]![key]!["maps"]!.AsObject();
        Assert.SkipWhen(maps.Count == 0, $"{file}: the compared NiTexturingProperty has no map to mutate.");
        var map = maps.First().Value!.AsObject();
        map["uvSet"] = Int(map["uvSet"]) + 1;
        AssertControl(file, comparison, NifModelFieldComparisons.Texturing(document, mutated), $"texturing[{key}] first map uvSet");
    }

    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.Rows), MemberType = typeof(Cut1aCoverManifest))]
    public void GeometryStreams_MatchTheProbe(string entry, string sha256)
    {
        var (file, expectation, document) = Prepare(entry, sha256);
        var comparison = Verify(file, "geometry streams", NifModelFieldComparisons.Geometry(document, expectation));

        // Control: one compared data block's vertex count raised by one in the expectation is detected (on a skinned
        // packed block it is the shape vertex count, compared with the primitive's point count).
        var dataBlocks = PrimitivesByGeometryBlock(document).Values.Select(p => Int(p.Payload["dataBlock"])).ToList();
        var mutated = Cut1aProbeExpectations.Clone(expectation);
        var key = FirstComparedKey(mutated["geometryData"]!.AsObject(), dataBlocks);
        var data = mutated["geometryData"]![key]!.AsObject();
        data["numVertices"] = Int(data["numVertices"]) + 1;
        AssertControl(file, comparison, NifModelFieldComparisons.Geometry(document, mutated), $"geometryData[{key}].numVertices");

        // Control 2 (packed rows): the first stream's type of one compared packed block changed in the expectation
        // leaves the table matching no measured layout, which the comparison reports.
        if (FirstPackedKey(expectation, dataBlocks) is { } packedKey)
        {
            var packedMutated = Cut1aProbeExpectations.Clone(expectation);
            var stream = packedMutated["packed"]![packedKey]!["streams"]!.AsArray()[0]!.AsObject();
            stream["type"] = Long(stream["type"]) + 1;
            AssertControl(file, comparison, NifModelFieldComparisons.Geometry(document, packedMutated),
                $"packed[{packedKey}].streams[0].type");
        }
    }

    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.Rows), MemberType = typeof(Cut1aCoverManifest))]
    public void Skins_MatchTheProbe(string entry, string sha256)
    {
        var (file, expectation, document) = Prepare(entry, sha256);
        var comparison = Verify(file, "skins", NifModelFieldComparisons.Skins(document, expectation));

        // Control: one compared skin's bone count raised by one in the expectation is detected.
        var mutated = Cut1aProbeExpectations.Clone(expectation);
        var key = FirstComparedKey(mutated["skins"]!.AsObject(), PrimitivesByGeometryBlock(document).Values
            .Where(p => p.Payload["skin"] is JsonObject skin && Bool(skin["typed"]))
            .Select(p => Int(p.Payload["skin"]!["skinInstance"])));
        var skinFacts = mutated["skins"]![key]!.AsObject();
        skinFacts["bones"] = Int(skinFacts["bones"]) + 1;
        AssertControl(file, comparison, NifModelFieldComparisons.Skins(document, mutated), $"skins[{key}].bones");
    }

    [Theory]
    [MemberData(nameof(Cut1aCoverManifest.Rows), MemberType = typeof(Cut1aCoverManifest))]
    public void Morphs_MatchTheProbe(string entry, string sha256)
    {
        var (file, expectation, document) = Prepare(entry, sha256);
        var comparison = Verify(file, "morphs", NifModelFieldComparisons.Morphs(document, expectation));

        // Control: one compared NiMorphData's morph count raised by one in the expectation is detected.
        var mutated = Cut1aProbeExpectations.Clone(expectation);
        var key = FirstComparedKey(mutated["morphData"]!.AsObject(), PrimitivesByGeometryBlock(document).Values
            .Where(p => p.Payload["morph"] is JsonObject morph && morph["dataBlock"] is not null && morph["numMorphs"] is not null)
            .Select(p => Int(p.Payload["morph"]!["dataBlock"])));
        var morphs = mutated["morphData"]![key]!.AsObject();
        morphs["numMorphs"] = Int(morphs["numMorphs"]) + 1;
        AssertControl(file, comparison, NifModelFieldComparisons.Morphs(document, mutated), $"morphData[{key}].numMorphs");
    }

    /// <summary>
    ///     Guard, manifest row, expectation, the skip rules (declined controls and .kf streams), the read under the
    ///     console platform of a big-endian file, and its structural validation.
    /// </summary>
    private static (Cut1aCoverFile File, JsonObject Expectation, ModelDocument Document) Prepare(string entry,
        string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1aCoverManifest.Require(sha256);
        Assert.Equal(entry, file.Entry);
        var expectation = Cut1aProbeExpectations.Require(file);
        SkipUnlessComparableModel(file, expectation);
        var bytes = Cut1aFixtureResolver.Require(file);
        var document = Read(bytes, file, options: ConsoleReadOptions(file, expectation)).Document;
        SceneValidation.ValidateStructure(document);
        return (file, expectation, document);
    }

    /// <summary>Skips a row with nothing of the family to compare, else requires no mismatch and reports the counts.</summary>
    private static NifModelFieldComparison Verify(Cut1aCoverFile file, string family, NifModelFieldComparison comparison)
    {
        Assert.SkipWhen(comparison.Compared == 0,
            $"{file}: no {family} to compare" +
            (comparison.Notes.Count > 0 ? $" ({comparison.Notes.Count} element(s) the probe does not parse)" : "") + ".");
        Assert.True(comparison.Mismatches.Count == 0,
            $"{file}: {comparison.Mismatches.Count} {family} mismatch(es):{Environment.NewLine}{comparison.Report}");
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{file}: {comparison.Compared} {family} element(s) match the probe" +
            $"{(comparison.Notes.Count > 0 ? $"; {comparison.Notes.Count} not parsed by the probe" : "")}."));
        return comparison;
    }

    /// <summary>The mutated expectation must produce a mismatch the original did not.</summary>
    private static void AssertControl(Cut1aCoverFile file, NifModelFieldComparison original,
        NifModelFieldComparison control, string mutation)
    {
        Assert.True(control.Mismatches.Count > original.Mismatches.Count,
            $"{file}: the control ({mutation}) was not detected; the comparison cannot discriminate.");
    }

    /// <summary>The first expectation key among the reader's compared blocks (the family's blocks the probe parsed).</summary>
    private static string FirstComparedKey(JsonObject family, IEnumerable<int> readerBlocks)
    {
        foreach (var block in readerBlocks.Distinct().Order())
        {
            var key = block.ToString(CultureInfo.InvariantCulture);
            if (family.ContainsKey(key))
            {
                return key;
            }
        }

        throw new InvalidOperationException("The comparison reported compared elements but no compared block is keyed.");
    }

    /// <summary>
    ///     The expectation key of the first packed block a compared data block names through its Additional Data, or
    ///     null when no compared block is packed (every little-endian row, and the console files with inline streams).
    /// </summary>
    private static string? FirstPackedKey(JsonObject expectation, IEnumerable<int> dataBlocks)
    {
        if (expectation["packed"] is not JsonObject packed || expectation["geometryData"] is not JsonObject data)
        {
            return null;
        }

        foreach (var block in dataBlocks.Distinct().Order())
        {
            if (data[block.ToString(CultureInfo.InvariantCulture)] is not JsonObject probe ||
                probe["additionalData"] is null || Int(probe["additionalData"]) < 0)
            {
                continue;
            }

            var key = Int(probe["additionalData"]).ToString(CultureInfo.InvariantCulture);
            if (packed.ContainsKey(key))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>The blocks the material rows typed under one facts name (<c>shade</c>, <c>texturing</c>, ...).</summary>
    private static IEnumerable<int> TypedBlocks(ModelDocument document, string facts)
    {
        foreach (var (_, payload) in IndexedRows(document, NifModelMaterialReader.MaterialKind))
        {
            if (payload[facts] is JsonObject typed && typed["block"] is not null &&
                !(typed["typed"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && !value))
            {
                yield return Int(typed["block"]);
            }
        }
    }

    /// <summary>
    ///     One material-family mutation: the first typed NiAlphaProperty's blend pair rewritten (the design's control:
    ///     ONE/ONE becomes SRC_ALPHA/INV_SRC_ALPHA, any other pair becomes ONE/ONE), else the first NiMaterialProperty's
    ///     alpha moved by one ulp, else a stencil enable bit, a depth test bit or a vertex-color lighting mode flipped.
    /// </summary>
    private static string MutateMaterials(JsonObject mutated, ModelDocument document)
    {
        // (the reader's facts name, the expectation's family name, the mutation)
        foreach (var (facts, expectationKey, mutate) in new (string Facts, string Family, Func<JsonObject, string> Mutate)[]
                 {
                     ("alpha", "alpha", probe =>
                     {
                         var ones = Text(probe["src"]) == "ONE" && Text(probe["dst"]) == "ONE";
                         probe["src"] = ones ? "SRC_ALPHA" : "ONE";
                         probe["dst"] = ones ? "INV_SRC_ALPHA" : "ONE";
                         return "blend pair";
                     }),
                     ("material", "materials", probe =>
                     {
                         probe["alpha"] = (double)MathF.BitIncrement(Float(probe["alpha"]));
                         return "alpha one ulp up";
                     }),
                     ("stencil", "stencil", probe =>
                     {
                         probe["enable"] = !Bool(probe["enable"]);
                         return "enable flipped";
                     }),
                     ("zBuffer", "zbuffer", probe =>
                     {
                         probe["zTest"] = !Bool(probe["zTest"]);
                         return "zTest flipped";
                     }),
                     ("vertexColor", "vertexColor", probe =>
                     {
                         probe["lightingMode"] = Text(probe["lightingMode"]) == "E" ? "E_A_D" : "E";
                         return "lighting mode flipped";
                     })
                 })
        {
            var typed = TypedBlocks(document, facts).ToList();
            if (typed.Count == 0)
            {
                continue;
            }

            var key = FirstComparedKey(mutated[expectationKey]!.AsObject(), typed);
            return $"{expectationKey}[{key}]: {mutate(mutated[expectationKey]![key]!.AsObject())}";
        }

        throw new InvalidOperationException("The material comparison compared elements but no family is keyed.");
    }
}
