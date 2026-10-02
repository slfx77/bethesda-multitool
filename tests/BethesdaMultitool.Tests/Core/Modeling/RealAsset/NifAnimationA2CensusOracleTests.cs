using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B, hop A2-anim (cut-1b slice 9, plan section 3): the header block census of every cover-manifest file
///     against the animation coverage classification the stage computes (<see cref="NifModelAnimationCoverage.ClassifyFile" />
///     over the reader's decisions): every animation block of the probe's header table lands in exactly one
///     classification, Typed or NativeOnly, with a code from the plan section 2.1 list or the reader's own decisions
///     (<see cref="NifAnimationCensusOracle" />), and no block is left unclassified.
/// </summary>
/// <remarks>
///     <para>
///         Controls that must fail, recorded in the receipt: one block removed from a file's classification (the census
///         then names it unclassified), and a controller type from the census <c>controllersByType</c> removed from the
///         table (the completeness check then names it). The census controller types are those of
///         <c>TestOutput/cut1b-prep-20260925/corpus-receipt-all.json</c> (65,764 distinct files), as the slice-8 table
///         test carries them; this hop also anchors them in data, requiring every controller type the manifest's headers
///         name to be one of them.
///     </para>
///     <para>Receipt: <c>TestOutput/cut1b-slice9-&lt;date&gt;/NifAnimationA2CensusOracleTests/</c>.</para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifAnimationA2CensusOracleTests
{
    private const string Hop = nameof(NifAnimationA2CensusOracleTests);
    private const string RemovalControl = "LOOP sequence";

    /// <summary>The census <c>controllersByType</c> keys (see the remarks).</summary>
    private static readonly string[] CensusControllerTypes =
    [
        "BSFrustumFOVController", "BSMaterialEmittanceMultController", "BSPSysMultiTargetEmitterCtlr",
        "BSRefractionFirePeriodController", "BSRefractionStrengthController", "BSTreadTransfController",
        "NiAlphaController", "NiBSBoneLODController", "NiControllerManager", "NiFloatExtraDataController",
        "NiGeomMorpherController", "NiLightColorController", "NiLightDimmerController", "NiMaterialColorController",
        "NiMultiTargetTransformController", "NiPSysEmitterCtlr", "NiPSysEmitterDeclinationCtlr",
        "NiPSysEmitterDeclinationVarCtlr", "NiPSysEmitterInitialRadiusCtlr", "NiPSysEmitterLifeSpanCtlr",
        "NiPSysEmitterPlanarAngleCtlr", "NiPSysEmitterPlanarAngleVarCtlr", "NiPSysEmitterSpeedCtlr",
        "NiPSysGravityStrengthCtlr", "NiPSysInitialRotAngleCtlr", "NiPSysInitialRotSpeedCtlr",
        "NiPSysInitialRotSpeedVarCtlr", "NiPSysModifierActiveCtlr", "NiPSysResetOnLoopCtlr", "NiPSysUpdateCtlr",
        "NiTextureTransformController", "NiTransformController", "NiVisController", "bhkBlendController"
    ];

    [Theory]
    [MemberData(nameof(Cut1bCoverManifest.Rows), MemberType = typeof(Cut1bCoverManifest))]
    public void EveryManifestFile_EveryAnimationBlockIsClassifiedExactlyOnce(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var read = Cut1bAnimationStage.Load(entry, sha256);
        var classifications = NifModelAnimationCoverage.ClassifyFile(read.State, read.Result,
            TestContext.Current.CancellationToken);

        var diffs = NifAnimationCensusOracle.Check(read.State.Schema, BlockTypes(read), read.Expectation,
            classifications);

        var codes = new JsonObject();
        foreach (var group in classifications.GroupBy(static c => c.Code).OrderBy(static g => g.Key,
                     StringComparer.Ordinal))
        {
            codes[group.Key] = group.Count();
        }

        Cut1bHopReceipt.Row(Hop, read.File, new JsonObject
        {
            ["animationBlocks"] = classifications.Count,
            ["typed"] = classifications.Count(static c => c.IsTyped),
            ["nativeOnly"] = classifications.Count(static c => !c.IsTyped),
            ["codes"] = codes,
            ["diffCount"] = diffs.Count,
            ["diffs"] = new JsonArray(diffs.Take(40).Select(static d => (JsonNode?)JsonValue.Create(d)).ToArray())
        });
        Assert.True(diffs.Count == 0, $"{read}:\n  " + string.Join("\n  ", diffs.Take(40)));
    }

    /// <summary>One theory row per 20.0.0.4 manifest file (the five FNV .kf carried since cut 2).</summary>
    public static TheoryData<string, string> LegacyKfRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var file in Cut1bCoverManifest.Files)
        {
            if (file.Key.StartsWith("20.0.0.4/", StringComparison.Ordinal))
            {
                rows.Add(file.Entry, file.Sha256);
            }
        }

        return rows;
    }

    /// <summary>
    ///     Cut 2, the five 20.0.0.4 rows: every NiStringPalette of the file is Typed, because every controlled block
    ///     bound its target through it (D-kf2: the palette is what target resolution consumes), and the file's every
    ///     animation block is classified once (the theory above). Control: with no decision about it the same block
    ///     takes the plan 2.1 table row, NativeOnly with <see cref="NifModelAnimationCoverage.StringPaletteCode" />, so
    ///     the Typed outcome is the reader's decision and not the table's default.
    /// </summary>
    [Theory]
    [MemberData(nameof(LegacyKfRows))]
    public void LegacyKfRows_TheStringPaletteIsTypedThroughItsBindings(string entry, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var read = Cut1bAnimationStage.Load(entry, sha256);
        Assert.False(read.File.IsDeclinedControl, $"{read}: still a decline control.");
        var classifications = NifModelAnimationCoverage.ClassifyFile(read.State, read.Result,
            TestContext.Current.CancellationToken);
        var palettes = classifications.Where(c => read.State.Blocks[c.Block].Type == "NiStringPalette").ToList();

        Assert.NotEmpty(palettes);
        Assert.All(palettes, palette => Assert.True(palette.IsTyped,
            $"{read}: NiStringPalette block {palette.Block} is {palette.Kind} ('{palette.Reason}')."));
        Assert.NotEmpty(read.Result.Clips);

        var undecided = NifModelAnimationCoverage.Classify(read.State.Schema, palettes[0].Block, "NiStringPalette", []);
        Assert.NotNull(undecided);
        Assert.False(undecided.Value.IsTyped);
        Assert.Equal(NifModelAnimationCoverage.StringPaletteCode, undecided.Value.Code);
    }

    [Fact]
    public void Control_OneBlockRemovedFromTheClassification_IsReportedUnclassified()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var read = Cut1bAnimationStage.LoadControl(RemovalControl);
        var classifications = NifModelAnimationCoverage.ClassifyFile(read.State, read.Result,
            TestContext.Current.CancellationToken);
        Assert.Empty(NifAnimationCensusOracle.Check(read.State.Schema, BlockTypes(read), read.Expectation,
            classifications));
        Assert.NotEmpty(classifications);

        var removed = classifications[0];
        var reduced = classifications.Skip(1).ToList();
        var diffs = NifAnimationCensusOracle.Check(read.State.Schema, BlockTypes(read), read.Expectation, reduced);

        var expected = $"block {removed.Block} ({read.State.Blocks[removed.Block].Type}): unclassified";
        Cut1bHopReceipt.Control(Hop, "oneBlockRemovedFromTheClassification", new JsonObject
        {
            ["entry"] = read.File.Entry,
            ["sha256"] = read.File.Sha256,
            ["removedBlock"] = removed.Block,
            ["removedCode"] = removed.Code,
            ["detected"] = diffs.Contains(expected, StringComparer.Ordinal),
            ["diffs"] = new JsonArray(diffs.Select(static d => (JsonNode?)JsonValue.Create(d)).ToArray())
        });
        Assert.Equal(expected, Assert.Single(diffs));
    }

    [Fact]
    public void TableCompleteness_EveryCensusControllerTypeHasARow_AndTheManifestNamesNoOther()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var schema = NifSchema.LoadEmbedded();
        Assert.Empty(NifAnimationCensusOracle.MissingRows(CensusControllerTypes,
            type => NifModelAnimationCoverage.TableRow(schema, type)));

        var manifestTypes = ManifestControllerTypes(schema);
        Assert.SkipWhen(manifestTypes.Count == 0,
            $"{Cut1bProbeExpectations.RelativePath} is not present, so the manifest's controller types are unknown.");
        var outside = manifestTypes.Where(type => !CensusControllerTypes.Contains(type, StringComparer.Ordinal))
            .ToList();

        var removed = CensusControllerTypes[0];
        var missing = NifAnimationCensusOracle.MissingRows(CensusControllerTypes,
            type => string.Equals(type, removed, StringComparison.Ordinal)
                ? null
                : NifModelAnimationCoverage.TableRow(schema, type));
        Cut1bHopReceipt.Control(Hop, "controllerTypeMissingFromTheTable", new JsonObject
        {
            ["removedType"] = removed,
            ["detected"] = missing.Contains(removed, StringComparer.Ordinal),
            ["missing"] = new JsonArray(missing.Select(static t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["censusTypes"] = CensusControllerTypes.Length,
            ["manifestControllerTypes"] = new JsonArray(manifestTypes.Select(static t => (JsonNode?)JsonValue.Create(t))
                .ToArray()),
            ["manifestTypesOutsideTheCensus"] = new JsonArray(outside.Select(static t => (JsonNode?)JsonValue.Create(t))
                .ToArray())
        });
        Assert.Equal(removed, Assert.Single(missing));
        Assert.True(outside.Count == 0, "Manifest controller types outside the census: " + string.Join(", ", outside));
    }

    /// <summary>The read state's block type names, in block order.</summary>
    private static List<string> BlockTypes(Cut1bAnimationRead read)
    {
        return read.State.Blocks.Select(static block => block.Type).ToList();
    }

    /// <summary>Every NiTimeController type the probe's header tables name across the manifest, sorted.</summary>
    private static List<string> ManifestControllerTypes(NifSchema schema)
    {
        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Cut1bCoverManifest.Files)
        {
            JsonObject record;
            try
            {
                record = Cut1bProbeExpectations.Require(file);
            }
            catch (Exception exception) when (exception.GetType().Name.Contains("Skip", StringComparison.Ordinal))
            {
                continue;
            }

            if (record["blockTypeNames"] is not JsonArray names)
            {
                continue;
            }

            foreach (var name in names)
            {
                var type = name!.GetValue<string>();
                if (schema.Inherits(type, "NiTimeController"))
                {
                    types.Add(type);
                }
            }
        }

        return types.ToList();
    }
}
