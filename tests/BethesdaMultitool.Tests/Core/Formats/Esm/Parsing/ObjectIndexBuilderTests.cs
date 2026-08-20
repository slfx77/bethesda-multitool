using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Model-path resolution for placed references now happens in two coordinated pieces:
///     <c>ObjectIndexBuilder.BuildIndexes</c> builds the base-object indexes BEFORE cell parsing, and
///     each <c>PlacedReference</c> is born enriched via those indexes on the parser context
///     (construction-time path); <c>EnrichAllCellViews</c> remains as the DMP-only post-pass for
///     runtime-merged placements. These pin both pieces for the two record types with a history of
///     silently falling out of the index (SCOL, PWAT).
/// </summary>
public sealed class ObjectIndexBuilderTests
{
    [Fact]
    public void BuildIndexes_ResolvesScolModelPath_AndPostPassEnrichesInPlace()
    {
        // Regression: a placed reference whose base object is a SCOL (static collection, e.g.
        // SSHQExterior03) must resolve to the SCOL's merged meshes\scol\*.nif. SCOL was omitted from
        // the model index, so these refs got no ModelPath and never rendered — masked in the viewer
        // by the imposter stand-in that sits nearby (see viewer_imposter_doubling).
        const uint scolFormId = 0x174D17;
        var scol = new StaticCollectionRecord
        {
            FormId = scolFormId,
            EditorId = "SSHQExterior03",
            ModelPath = "SCOL\\SSHQExterior03.NIF"
        };
        var cells = OneCellPlacing(scolFormId);
        var originalCell = cells[0];

        var modelIndex = new Dictionary<uint, string>();
        var boundsIndex = BuildIndexes(modelIndex, staticCollections: [scol]);
        ObjectIndexBuilder.EnrichAllCellViews(cells, [], boundsIndex, modelIndex);

        Assert.Equal("SCOL\\SSHQExterior03.NIF", cells[0].PlacedObjects[0].ModelPath);
        // In place: worldspace cell lists alias the same CellRecord instances, so enrichment must
        // never replace the cell object (that forked the two views before).
        Assert.Same(originalCell, cells[0]);
    }

    [Fact]
    public void BuildIndexes_ResolvesPwatModelPath_AndPostPassEnrichesInPlace()
    {
        // Regression: PWAT (placeable water) is the water plane for every pond, sewer and crater pool
        // whose surface is NOT the cell's XCLW plane. PWAT rode the generic-record list until it moved
        // to the typed ParsePlaceableWaters() path (needed to recover its parent WATR); that move
        // dropped it out of the model index, so every placed water plane lost its MODL and the
        // renderer discarded the reference — water simply missing in the viewer.
        const uint pwatFormId = 0x174163;
        var pwat = new PlaceableWaterRecord
        {
            FormId = pwatFormId,
            EditorId = "NVCleanWater1x402",
            ModelPath = "Water\\NVCleanWater1x402.NIF",
            WaterFormId = 0x000881EE
        };
        var cells = OneCellPlacing(pwatFormId);

        var modelIndex = new Dictionary<uint, string>();
        var boundsIndex = BuildIndexes(modelIndex, placeableWaters: [pwat]);
        ObjectIndexBuilder.EnrichAllCellViews(cells, [], boundsIndex, modelIndex);

        Assert.Equal("Water\\NVCleanWater1x402.NIF", cells[0].PlacedObjects[0].ModelPath);
    }

    [Fact]
    public void ToPlacedReference_IsBornEnriched_WhenContextCarriesTheIndexes()
    {
        // Construction-time path: RecordParser stashes the indexes on the context before cell
        // parsing, so ToPlacedReference enriches at birth and no post-pass clone is needed on a
        // plain ESM load.
        const uint scolFormId = 0x174D17;
        var scan = new BethesdaMultitool.Core.Formats.Esm.Records.EsmRecordScanResult();
        var context = new RecordParserContext(scan)
        {
            PlacedObjectModelIndex = new Dictionary<uint, string>
            {
                [scolFormId] = "SCOL\\SSHQExterior03.NIF"
            }
        };

        var extracted = new ExtractedRefrRecord
        {
            Header = new BethesdaMultitool.Core.Formats.Esm.Models.DetectedMainRecord(
                "REFR", DataSize: 0, Flags: 0, FormId: 0x200, Offset: 0, IsBigEndian: false),
            BaseFormId = scolFormId
        };

        var placed = CellLinkageHandler.ToPlacedReference(extracted, context);

        Assert.Equal("SCOL\\SSHQExterior03.NIF", placed.ModelPath);
    }

    private static List<CellRecord> OneCellPlacing(uint baseFormId)
    {
        return
        [
            new CellRecord
            {
                FormId = 0x100,
                GridX = 0,
                GridY = 0,
                PlacedObjects =
                [
                    new PlacedReference
                    {
                        FormId = 0x200, BaseFormId = baseFormId, RecordType = "REFR", X = 0, Y = 0, Z = 0
                    }
                ]
            }
        ];
    }

    private static Dictionary<uint, BethesdaMultitool.Core.Formats.Esm.Models.ObjectBounds> BuildIndexes(
        Dictionary<uint, string> modelIndex,
        List<StaticCollectionRecord>? staticCollections = null,
        List<PlaceableWaterRecord>? placeableWaters = null,
        List<TreeRecord>? trees = null)
    {
        return ObjectIndexBuilder.BuildIndexes(
            [], [], [], [], [],
            staticCollections ?? [],
            placeableWaters ?? [],
            trees ?? [],
            [], [], [], [], [], [],
            [], [], [], [], [], [],
            modelIndex);
    }
}
