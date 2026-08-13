using System.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class ObjectIndexBuilderTests
{
    [Fact]
    public void BuildAndEnrich_ResolvesScolModelPathOntoPlacedReference()
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

        BuildAndEnrich(cells, staticCollections: [scol]);

        Assert.Equal("SCOL\\SSHQExterior03.NIF", cells[0].PlacedObjects[0].ModelPath);
    }

    [Fact]
    public void BuildAndEnrich_ResolvesPwatModelPathOntoPlacedReference()
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

        BuildAndEnrich(cells, placeableWaters: [pwat]);

        Assert.Equal("Water\\NVCleanWater1x402.NIF", cells[0].PlacedObjects[0].ModelPath);
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

    private static void BuildAndEnrich(
        List<CellRecord> cells,
        List<StaticCollectionRecord>? staticCollections = null,
        List<PlaceableWaterRecord>? placeableWaters = null,
        List<TreeRecord>? trees = null)
    {
        ObjectIndexBuilder.BuildAndEnrich(
            [], [], [], [], [],
            staticCollections ?? [],
            placeableWaters ?? [],
            trees ?? [],
            [], [], [], [], [], [],
            [], [], [], [], [], [],
            cells, [], new Dictionary<uint, string>(),
            new Stopwatch());
    }
}
