using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Tes3;
using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Tes3;

/// <summary>
///     Morrowind (TES3) plugins carry no real FormIDs, so each is parsed with file-local synthetic ones
///     that collide across plugins, and each synthesizes its own exterior worldspace. These tests cover
///     the load-order merge fix: a shared exterior worldspace FormID (so every plugin's exterior cells
///     fold into one worldspace) plus per-plugin namespacing (so unrelated records don't overwrite each
///     other), matching how Morrowind.esm + Tribunal.esm + Bloodmoon.esm should combine.
/// </summary>
public class Tes3LoadOrderMergeTests
{
    [Fact]
    public void Namespace_LeavesSharedWorldspaceButStampsPerPluginRecords()
    {
        // Only the shared exterior worldspace (0xFF-prefixed) survives unchanged so the merge folds it;
        // everything else — including land textures — is stamped with the load index.
        Assert.Equal(
            WorldspaceRecord.Tes3SyntheticExteriorFormId,
            Tes3FormIdScheme.Namespace(WorldspaceRecord.Tes3SyntheticExteriorFormId, 5));
        Assert.Equal(0x05000001u, Tes3FormIdScheme.Namespace(0x00000001u, 5));
        Assert.Equal(0u, Tes3FormIdScheme.Namespace(0u, 5));
    }

    [Fact]
    public void Namespace_StampsLtexPerPlugin_SoReusedIndicesStayDistinct()
    {
        // Morrowind land-texture indices are per-plugin: Bloodmoon reuses low indices for different
        // textures. The LTEX/TXST bases must sit in the namespaced (non-0xFF) range so the same index in
        // two plugins gets distinct FormIDs and a later plugin can't overwrite an earlier one's palette.
        Assert.NotEqual(
            Tes3FormIdScheme.LtexFormIdBase + 2,
            Tes3FormIdScheme.Namespace(Tes3FormIdScheme.LtexFormIdBase + 2, 5));
        Assert.NotEqual(
            Tes3FormIdScheme.Namespace(Tes3FormIdScheme.LtexFormIdBase + 2, 1),
            Tes3FormIdScheme.Namespace(Tes3FormIdScheme.LtexFormIdBase + 2, 2));
        // The load index lands in the high byte; the LTEX index is preserved in the low bits.
        Assert.Equal(
            (5u << 24) | (Tes3FormIdScheme.LtexFormIdBase + 2),
            Tes3FormIdScheme.Namespace(Tes3FormIdScheme.LtexFormIdBase + 2, 5));
    }

    [Fact]
    public void Namespacer_IsNoOpForNonTes3AndIndexZero()
    {
        var nonTes3 = new RecordCollection { IsTes3 = false, Cells = [ExteriorCell(1, 0, 0)] };
        Assert.Same(nonTes3, Tes3LoadOrderNamespacer.Namespaced(nonTes3, 3));

        var basePlugin = MakeTes3Plugin(ExteriorCell(1, 0, 0));
        Assert.Same(basePlugin, Tes3LoadOrderNamespacer.Namespaced(basePlugin, 0));
    }

    [Fact]
    public void TwoPlugins_FoldIntoOneWorldspace_WithoutCollidingCells()
    {
        // Both plugins independently number cells 1,2 (file-local) — without namespacing the merge would
        // treat plugin B's cells as overrides of plugin A's and drop two cells.
        var vvardenfell = MakeTes3Plugin(ExteriorCell(1, 0, 0), ExteriorCell(2, 1, 0));
        var solstheim = MakeTes3Plugin(ExteriorCell(1, 20, 20), ExteriorCell(2, 21, 20));

        var a = Tes3LoadOrderNamespacer.Namespaced(vvardenfell, 0); // base (unstamped)
        var b = Tes3LoadOrderNamespacer.Namespaced(solstheim, 1); // stamped 0x01
        var merged = a.MergeWith(b).RelinkWorldspaceCells();

        var ws = Assert.Single(merged.Worldspaces);
        Assert.Equal(WorldspaceRecord.Tes3SyntheticExteriorFormId, ws.FormId);
        Assert.Equal(4, ws.Cells.Count); // all four cells survive; nothing overwritten

        // Bounds span both plugins (X 0..21, Y 0..20). Morrowind map is +Y north: NW=(minX,maxY),
        // SE=(maxX,minY).
        Assert.Equal((short)0, ws.MapNWCellX);
        Assert.Equal((short)20, ws.MapNWCellY);
        Assert.Equal((short)21, ws.MapSECellX);
        Assert.Equal((short)0, ws.MapSECellY);
    }

    [Fact]
    public void WithoutNamespacing_CollidingCellsAreLost()
    {
        // Guards the regression: merging two TES3 plugins on raw file-local FormIDs (no namespacing)
        // drops the colliding cells — this is the bug the namespacer fixes.
        var a = MakeTes3Plugin(ExteriorCell(1, 0, 0), ExteriorCell(2, 1, 0));
        var b = MakeTes3Plugin(ExteriorCell(1, 20, 20), ExteriorCell(2, 21, 20));

        var merged = a.MergeWith(b).RelinkWorldspaceCells();

        Assert.Equal(2, Assert.Single(merged.Worldspaces).Cells.Count); // collision overwrote two cells
    }

    [Fact]
    public void ResolvePlacedModels_BackfillsTes3RefByEditorId()
    {
        // Bug 7 (Fort Frostmoth): a Bloodmoon REFR places an Imperial-fort STAT defined in the Morrowind
        // master. TES3 refs are editor-id strings resolved per-plugin at parse time, so the overlay's
        // placed ref has a null ModelPath AND BaseFormId 0 (its base lives in another plugin) and the
        // renderer drops it — "missing entirely". After merge, the editor-id fallback must re-resolve it.
        var master = new RecordCollection
        {
            IsTes3 = true,
            GenericRecords =
            [
                new GenericEsmRecord
                {
                    FormId = 0x000010, RecordType = "STAT", EditorId = "ex_imp_loaddoor_01",
                    ModelPath = @"x\ex_imp_loaddoor_01.nif"
                }
            ]
        };
        var overlay = new RecordCollection
        {
            IsTes3 = true,
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x000BB4, GridX = -22, GridY = 17,
                    WorldspaceFormId = WorldspaceRecord.Tes3SyntheticExteriorFormId, CellWorldSize = 8192f,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = 0x000C00, RecordType = "REFR",
                            BaseEditorId = "ex_imp_loaddoor_01", ModelPath = null, BaseFormId = 0
                        }
                    ]
                }
            ]
        };

        var merged = master.MergeWith(overlay).ResolvePlacedModels();

        var placed = Assert.Single(Assert.Single(merged.Cells).PlacedObjects);
        Assert.Equal(@"x\ex_imp_loaddoor_01.nif", placed.ModelPath); // backfilled from the master STAT
        Assert.Equal(0x000010u, placed.BaseFormId); // and its FormId resolved too
    }

    [Fact]
    public void ResolvePlacedModels_BackfillsByFormIdFromMergedIndex()
    {
        // The general (all-games) path: a ref with a valid cross-plugin BaseFormID but no baked ModelPath
        // — e.g. a TES4 mod placing a vanilla static — resolves through the merged ModelPathIndex.
        var rc = new RecordCollection
        {
            IsTes3 = false,
            ModelPathIndex = new Dictionary<uint, string> { [0x55] = @"meshes\clutter\rock01.nif" },
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x10,
                    PlacedObjects = [new PlacedReference { FormId = 2, BaseFormId = 0x55, ModelPath = null }]
                }
            ]
        };

        rc.ResolvePlacedModels();

        Assert.Equal(@"meshes\clutter\rock01.nif",
            Assert.Single(Assert.Single(rc.Cells).PlacedObjects).ModelPath);
    }

    [Fact]
    public void ResolvePlacedModels_LeavesAlreadyResolvedRefUntouched()
    {
        // A ref that already carries its own mesh is never clobbered by a same-id/same-FormID base.
        var rc = new RecordCollection
        {
            IsTes3 = true,
            ModelPathIndex = new Dictionary<uint, string> { [0x55] = @"index\other.nif" },
            GenericRecords = [new GenericEsmRecord { FormId = 1, EditorId = "x", ModelPath = @"base\x.nif" }],
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x10,
                    PlacedObjects =
                    [
                        new PlacedReference
                            { FormId = 2, BaseFormId = 0x55, BaseEditorId = "x", ModelPath = @"own\keep.nif" }
                    ]
                }
            ]
        };

        rc.ResolvePlacedModels();

        Assert.Equal(@"own\keep.nif", Assert.Single(Assert.Single(rc.Cells).PlacedObjects).ModelPath);
    }

    [Fact]
    public void Namespacer_KeepsEachPluginsLandTexturePalette_WhenIndicesCollide()
    {
        // Morrowind land-texture indices are per-plugin: both Morrowind.esm and Bloodmoon.esm define a
        // land texture at index 2, but they are DIFFERENT textures (MW idx 2 = the dirt road; BM idx 2 =
        // sand). The synthetic LTEX/TXST FormIDs therefore collide across plugins, and each cell's 16×16
        // VTEX grid references them. Namespacing must stamp the LTEX/TXST records AND the cells'
        // VtexTextureFormIds per plugin so the merge keeps both palettes and each plugin's cell resolves
        // its own texture — otherwise Bloodmoon (loaded later) overwrites Morrowind and Vvardenfell paths
        // render as Bloodmoon sand (the reported bug).
        const uint ltex2 = Tes3FormIdScheme.LtexFormIdBase + 2;
        const uint txst2 = Tes3FormIdScheme.LtexTextureSetFormIdBase + 2;

        var morrowind = MakeTes3LandTexturePlugin(ltex2, txst2, 0, 0, "dirt road", @"Tx_dirtroad_01.tga");
        var bloodmoon = MakeTes3LandTexturePlugin(ltex2, txst2, 20, 20, "sand", @"Tx_sand_02.tga");

        var a = Tes3LoadOrderNamespacer.Namespaced(morrowind, 0); // base (unstamped)
        var b = Tes3LoadOrderNamespacer.Namespaced(bloodmoon, 1); // stamped 0x01
        var merged = a.MergeWith(b).RelinkWorldspaceCells();

        // Both plugins' LTEX/TXST records survive — neither overwrote the other.
        Assert.Equal(2, merged.LandTextures.Count);
        Assert.Equal(2, merged.TextureSets.Count);

        // Resolve each cell's VTEX vertex through LTEX -> TXST -> diffuse; each kept its own texture.
        var diffuseByLtex = merged.LandTextures.ToDictionary(
            lt => lt.FormId,
            lt => merged.TextureSets.First(ts => ts.FormId == lt.TextureSetFormId).DiffuseTexture);

        var mwCell = merged.Cells.Single(c => c.GridX == 0 && c.GridY == 0);
        var bmCell = merged.Cells.Single(c => c.GridX == 20 && c.GridY == 20);

        Assert.Equal(@"Tx_dirtroad_01.tga", diffuseByLtex[mwCell.LandVisualData!.VtexTextureFormIds![0]]);
        Assert.Equal(@"Tx_sand_02.tga", diffuseByLtex[bmCell.LandVisualData!.VtexTextureFormIds![0]]);
    }

    private static RecordCollection MakeTes3LandTexturePlugin(
        uint ltexFormId, uint txstFormId, int gridX, int gridY, string editorId, string diffuse)
    {
        var grid = new uint[16 * 16];
        Array.Fill(grid, ltexFormId);
        var cell = new CellRecord
        {
            FormId = 0x100, // file-local; namespacing separates the two plugins' cells
            GridX = gridX,
            GridY = gridY,
            WorldspaceFormId = WorldspaceRecord.Tes3SyntheticExteriorFormId,
            CellWorldSize = 8192f,
            LandVisualData = new LandVisualData { VtexTextureFormIds = grid }
        };
        return new RecordCollection
        {
            IsTes3 = true,
            Cells = [cell],
            Worldspaces =
                Tes3RecordParser.BuildWorldspaces([cell], WorldspaceRecord.Tes3SyntheticExteriorFormId),
            LandTextures =
            [
                new LandscapeTextureRecord { FormId = ltexFormId, EditorId = editorId, TextureSetFormId = txstFormId }
            ],
            TextureSets =
                [new TextureSetRecord { FormId = txstFormId, EditorId = editorId, DiffuseTexture = diffuse }]
        };
    }

    private static RecordCollection MakeTes3Plugin(params CellRecord[] cells)
    {
        var cellList = cells.ToList();
        return new RecordCollection
        {
            IsTes3 = true,
            Cells = cellList,
            Worldspaces = Tes3RecordParser.BuildWorldspaces(cellList, WorldspaceRecord.Tes3SyntheticExteriorFormId)
        };
    }

    private static CellRecord ExteriorCell(uint formId, int gridX, int gridY)
    {
        return new CellRecord
        {
            FormId = formId,
            GridX = gridX,
            GridY = gridY,
            Flags = 0x00,
            WorldspaceFormId = WorldspaceRecord.Tes3SyntheticExteriorFormId,
            CellWorldSize = 8192f
        };
    }
}