using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Modeling.Shadowkey;
using BethesdaMultitool.Tests.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Shadowkey.ShadowkeyModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     The zone reader's composed document (cut-2 plan section 4; slices 7 and 8) on builder zone sets: the terrain of
///     the one face rule against the Python oracle's golden digests, the palette with its key and light table, the
///     textures, the sky, the placements with their matrices and the refusals, each with its control.
/// </summary>
public class ShadowkeyZoneModelReaderTests
{
    // The Python oracle's terrain digests of golden_zone (shadowkey_cover.py selfcheck, golden.terrain).
    private const string GoldenFloor = "054006c9c8b326f46fb9b992009847b67787f69a09418c08b2059bb87b975896";
    private const string GoldenCeiling = "bc511a5de5a3b75a1028964d2598a01456511a7c2071bc1ca7915b8d16c1f278";
    private const string GoldenWall = "c94379188361a927c46a466a79a1ab3e8c5a2a1c642e57e26a5ffc2002a4731a";
    private const string GoldenRiser = "12cbe8f1cdc294cefe142ab5f41aca69e0ba1fff3baa42f3eede391c02bf4426";
    private const string GoldenDownstand = "49b580f961f2326a6395a4f91ad87dc35f3b96836542064d111491f2cf6cd6f5";

    private static ScenePrimitive Terrain(ModelDocument document, string kind)
    {
        var mesh = document.Meshes.Single(static m => m.Name.EndsWith(" terrain", StringComparison.Ordinal));
        return mesh.Primitives.Single(p => p.Name == kind);
    }

    private static ShadowkeyTestBuilder.Zone PlacedZone(int extraPlacements = 0)
    {
        ShadowkeyTestBuilder.Placement[] placements =
        [
            new(256, 256, 0, 16384, 256, 100),
            new(300, 400, 10, 8192, 512, 200, Angle0: 5),
            new(100, 100, 0, 0, 256, 100),
            new(50, 50, 0, 0, 256, 300),
            new(60, 60, 0, 0, 256, 999)
        ];
        return new ShadowkeyTestBuilder.Zone
        {
            Records = [ShadowkeyTestBuilder.GoldenStatic(), ShadowkeyTestBuilder.GoldenAnimated()],
            Entities = [(100, 0), (200, 1), (300, 5)],
            Placements = [.. placements, .. Enumerable.Repeat(new ShadowkeyTestBuilder.Placement(10, 10, 0, 0, 256, 100),
                extraPlacements)]
        };
    }

    [Fact]
    public void Read_ComposesOneStructurallyValidZone()
    {
        var files = new ShadowkeyTestBuilder.Zone().Build();

        var result = ReadZone(files);
        var document = result.Document;

        SceneValidation.ValidateStructure(document);
        Assert.Equal(ShadowkeyModelFormatMetadata.ZoneFormatId, document.SourceFormat);
        Assert.Equal("testzone", document.Name);
        Assert.Same(ShadowkeyModelUnits.Units, document.Units);
        Assert.Same(ShadowkeyModelUnits.ZoneBasis, document.SourceBasis);
        Assert.Equal("zones/testzone.zmp", document.SourceProvenance!.RelativePath);
        Assert.Equal(Digest(files["testzone.zmp"]), document.SourceProvenance.Sha256);
        Assert.Contains("multitoolComposition", document.ExtrasJson!, StringComparison.Ordinal);
        Assert.Contains(document.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.TileTexturingUnresolved);
    }

    [Fact]
    public void TheTerrain_IsTheOneFaceRule_WithTheOraclesGoldenDigests()
    {
        var document = ReadZone(ShadowkeyTileQuadsTests.GoldenZone().Build(), "golden").Document;

        Assert.Equal(GoldenFloor, Float32Digest(Positions(Terrain(document, "floor"))));
        Assert.Equal(GoldenCeiling, Float32Digest(Positions(Terrain(document, "ceiling"))));
        Assert.Equal(GoldenWall, Float32Digest(Positions(Terrain(document, "wall"))));
        Assert.Equal(GoldenRiser, Float32Digest(Positions(Terrain(document, "riser"))));
        Assert.Equal(GoldenDownstand, Float32Digest(Positions(Terrain(document, "downstand"))));
        var floor = Terrain(document, "floor");
        Assert.Equal(20, floor.Vertices.Count);
        Assert.Equal(Enumerable.Repeat(4, 5), floor.Faces!.FaceSizes);
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, floor.Indices.Take(6));
        Assert.Equal(SceneNormalMode.Flat, floor.NormalMode);
        // Mesh units: the far corner of cell (2, 1) is (768, 512).
        Assert.Contains(floor.Vertices, static v => v.Position == new Vector3(768, 512, 0x100));
    }

    [Fact]
    public void ThePalette_IsTypedWithItsKeyAndLightTable_AndTheKeyNeedsTheTable()
    {
        var keyed = ReadZone(new ShadowkeyTestBuilder.Zone().Build()).Document;
        var unpinned = ReadZone(new ShadowkeyTestBuilder.Zone { LightTablePinsKey = false }.Build()).Document;
        var plain = ShadowkeyTestBuilder.Zone.DefaultPalette();
        plain[19] = 0x10;
        var unkeyed = ReadZone(new ShadowkeyTestBuilder.Zone { Palette = plain }.Build()).Document;

        var palette = Assert.Single(keyed.Palettes);
        Assert.Equal(ScenePaletteEncoding.Rgb8, palette.Encoding);
        Assert.Equal(256, palette.Entries.Count);
        Assert.Equal(new ScenePaletteEntry(0xFF, 0x00, 0xFF, 255), palette.Entries[6]);
        Assert.Equal(new ScenePaletteEntry(3, 252, 1, 255), palette.Entries[3]);
        Assert.Equal(new[] { 6 }, palette.TransparentIndices);
        Assert.Equal(ShadowkeyModelImages.PaletteKeyRule, palette.TransparencyRule!.Id);
        Assert.Equal(SceneValueProvenance.Assumed, palette.TransparencyRule.Provenance);
        Assert.Equal(768, palette.OriginalByteLength);
        var table = Assert.Single(palette.AuxiliaryTables);
        Assert.Equal(("zlu", 131072), (table.Name, table.ByteLength));
        // Controls: a light table that does not pin the entry, or a palette without magenta, declares no key.
        Assert.Empty(Assert.Single(unpinned.Palettes).TransparentIndices);
        Assert.Empty(Assert.Single(unkeyed.Palettes).TransparentIndices);
    }

    [Fact]
    public void TheTextures_KeepTheStoredRows_AndThePngIsTopRowFirst()
    {
        var files = new ShadowkeyTestBuilder.Zone().Build();
        var stored = ShadowkeyCompressedFile.Inflate(files["testzone.ztx"], "testzone.ztx");

        var document = ReadZone(files).Document;

        // The composed zone also holds the sky's skin image (cut-2 review finding 10), so count the textures by their
        // container, as the Bucket-B oracle does: the builder writes two .ztx textures and one 2x2 sky skin.
        var textures = document.Images.Where(static i => i.Source?.Container == ShadowkeyModelImages.TextureContainer)
            .ToList();
        Assert.Equal(2, textures.Count);
        Assert.Equal(3, document.Images.Count);
        Assert.Single(document.Images, static i => i.Source?.Container == ShadowkeyModelImages.SkinContainer);
        var image = textures[1];
        Assert.Equal(0, image.Source!.PaletteIndex);
        Assert.Equal(stored.AsSpan(1 + 16384, 16384).ToArray(), image.Source.Original!.Content.ToArray());
        var png = StandardPng(image);
        Assert.Equal(3, png.ColorType);
        // The builder writes stored row r of texture 1 as the value 128 + r, so the top row of the picture is 255.
        Assert.Equal(255, png.Samples[0]);
        Assert.Equal(128, png.Samples[127 * 128]);
        // Control: the stored order would put 128 first.
        Assert.NotEqual(stored[1 + 16384], png.Samples[0]);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 255, 255, 0 }, png.Alpha);
        Assert.Equal(768, png.Palette.Length);
    }

    [Fact]
    public void TheMaterials_AreFiveNeutralKindsThenOnePerSurface_UsedByNoPrimitive()
    {
        var document = ReadZone(new ShadowkeyTestBuilder.Zone { SurfaceTextures = [1, 0, 1] }.Build()).Document;

        var terrainMaterials = document.Materials.Take(5).ToList();
        Assert.All(terrainMaterials, static m => Assert.Null(m.Texture));
        var surfaces = document.Materials.Where(static m => m.Name.Contains(".surface", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, surfaces.Count);
        Assert.Equal(new[] { 1, 0, 1 }, surfaces.Select(static m => m.Texture!.Value.ImageIndex));
        Assert.All(surfaces, static m => Assert.Equal(SceneAlphaMode.Mask, m.AlphaMode));
        var used = document.Meshes.SelectMany(static m => m.Primitives).Select(static p => p.MaterialIndex).ToHashSet();
        Assert.DoesNotContain(used, index => index is { } i && document.Materials[i].Name.Contains(".surface",
            StringComparison.Ordinal));
    }

    [Fact]
    public void TheSky_IsPlacedCameraCentered_AndAnUncountedHeaderIsDiagnosed()
    {
        var counted = ReadZone(new ShadowkeyTestBuilder.Zone().Build()).Document;
        var uncounted = ReadZone(new ShadowkeyTestBuilder.Zone
        {
            Sky = ShadowkeyTestBuilder.Zone.DefaultSky(counted: false)
        }.Build()).Document;

        var sky = counted.Nodes.Single(static n => n.Name == ShadowkeyZoneComposition.SkyIdentity);
        Assert.True(sky.Presentation!.IsSky);
        Assert.Equal(ShadowkeyModelUnits.MeshToZone, sky.LocalTransform);
        // Its locations index the inflated payload, so the element names the file (cut-2 review finding 6), as a terrain
        // texture's does; the offset is inside the inflated payload, not the compressed file.
        var skin = counted.Images.Single(static i => i.Source?.Container == ShadowkeyModelImages.SkinContainer);
        Assert.Equal("inflated:testzone.zsk:skin:0", skin.Source!.Location!.ElementIdentity);
        Assert.EndsWith("testzone.zsk", skin.Source.Location.AssetReference!.Path, StringComparison.Ordinal);
        var texture = counted.Images.First(static i => i.Source?.Container == ShadowkeyModelImages.TextureContainer);
        Assert.Equal("inflated:testzone.ztx", texture.Source!.Location!.ElementIdentity);
        Assert.DoesNotContain(counted.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.SkyTextureHeader);
        Assert.Contains(uncounted.Diagnostics, static d => d.Code == ShadowkeyModelDiagnostics.SkyTextureHeader);
    }

    [Fact]
    public void Placements_CarryTheirMatrices_AndShareTheirSlotsMesh()
    {
        var zone = PlacedZone();
        var files = zone.Build();

        var result = ReadZone(files);
        var document = result.Document;

        SceneValidation.ValidateStructure(document);
        for (var i = 0; i < 3; i++)
        {
            var node = document.Nodes.Single(n => n.Name == ShadowkeyZoneComposition.EntityIdentity(i));
            var placement = zone.Placements[i];
            var entity = new ShadowkeyEntity(placement.X, placement.Y, placement.Z, placement.Angle0, placement.Angle1,
                placement.Angle2, placement.Scale, placement.EntityId, "", "");
            Assert.Equal(ShadowkeyModelUnits.PlacementMatrix(entity), node.LocalTransform);
        }

        // Control: the legacy (x, z, y) map with +yaw is another matrix for the quarter turn.
        var turned = document.Nodes.Single(static n => n.Name == "ent:0").LocalTransform;
        var legacy = Matrix4x4.CreateRotationZ(16384 * MathF.Tau / 65536f);
        Assert.NotEqual(legacy.M12, turned.M12);
        Assert.Equal(new Vector3(256, 255, 0), Vector3.Transform(Vector3.UnitX, turned));

        // Rows 0 and 2 place slot 0: their placed nodes share one mesh.
        int MeshOf(string identity)
        {
            var root = document.Nodes.Single(n => n.Name == identity);
            return document.Nodes[Assert.Single(root.Children)].MeshIndex!.Value;
        }

        Assert.Equal(MeshOf("ent:0"), MeshOf("ent:2"));
        Assert.NotEqual(MeshOf("ent:0"), MeshOf("ent:1"));
        // The animated slot is placed at frame 0, skin 0: no targets, no clips.
        Assert.Empty(document.Meshes[MeshOf("ent:1")].Primitives[0].MorphTargets);
        Assert.Empty(document.Animations);
    }

    [Fact]
    public void UnresolvedPlacements_AreNativeStateOnly_WithTheDiagnostics()
    {
        var result = ReadZone(PlacedZone().Build());
        var document = result.Document;

        Assert.DoesNotContain(document.Nodes, static n => n.Name is "ent:3" or "ent:4");
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, result.Coverage.GetClassification("ent:3").Kind);
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, result.Coverage.GetClassification("ent:4").Kind);
        Assert.Equal(ModelSourceCoverageKind.Typed, result.Coverage.GetClassification("ent:2").Kind);
        var codes = document.Diagnostics.Select(static d => d.Code).ToHashSet();
        Assert.Contains(ShadowkeyModelDiagnostics.PlacementUnresolved, codes);
        Assert.Contains(ShadowkeyModelDiagnostics.ChiralityAssumed, codes);
        Assert.Contains(ShadowkeyModelDiagnostics.StaticPlacements, codes);
        Assert.Contains(ShadowkeyModelDiagnostics.PitchRollUnapplied, codes);
        Assert.Contains(ShadowkeyModelDiagnostics.PlacedSkinDefault, codes);
        Assert.Contains(ShadowkeyModelDiagnostics.PlacedMagentaOpaque, codes);
        var rows = document.NativeStates.Where(static s => s.Kind == ShadowkeyModelNativeState.ZonePlacementsKind)
            .SelectMany(static s => JsonNode.Parse(s.PayloadJson)!["records"]!.AsArray()).ToList();
        Assert.Equal(5, rows.Count);
        Assert.False(rows[4]!["resolved"]!.GetValue<bool>());
        Assert.Equal(5, rows[1]!["angles"]![0]!.GetValue<int>());
        // The entities.txt definition's kind and name (the builder writes every row as kind 1, "!thing"), not the .ent
        // instance name, which "name" carries (cut-2 review findings 2 and 9).
        Assert.Equal("!thing", rows[0]!["entityName"]!.GetValue<string>());
        Assert.Equal(1, rows[0]!["entityKind"]!.GetValue<int>());
        Assert.Equal("p0", rows[0]!["name"]!.GetValue<string>());
        // Control: id 999 is in no entities.txt row, so both are null (a present .ent row still names it "p4").
        Assert.Null(rows[4]!["entityName"]);
        Assert.Null(rows[4]!["entityKind"]);
        Assert.Equal("p4", rows[4]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void TheCoverage_AndTheNativeRows_FollowTheZonesFiles()
    {
        var files = new ShadowkeyTestBuilder.Zone { WithSta = true, Placements = [new(10, 10, 0, 0, 256, 100)] }.Build();

        var metadata = ReadZone(files);
        var full = ReadZone(files, detail: ModelNativeDetail.Full);

        Assert.Equal(new[]
            {
                "zmp:header", "zmp:cells", "zcp:heights", "zcp:slots-edges-extra-shade", "sur", "ztx:0", "ztx:1", "pal",
                "zlu", "zfg", "zsk", "ent:0", "ent:angles-0-1", "zon", "pth", "stn", "sta", "models-list"
            },
            metadata.Coverage.Elements.Select(static e => e.Identity));
        Assert.Equal(0, metadata.Coverage.DroppedCount);
        var kinds = metadata.Document.NativeStates.Select(static s => s.Kind).ToHashSet();
        foreach (var kind in new[]
                 {
                     ShadowkeyModelNativeState.ZoneHeaderKind, ShadowkeyModelNativeState.ZoneCellsKind,
                     ShadowkeyModelNativeState.ZonePrototypesKind, ShadowkeyModelNativeState.ZoneSurfacesKind,
                     ShadowkeyModelNativeState.ZoneFogKind, ShadowkeyModelNativeState.ZoneTriggersKind,
                     ShadowkeyModelNativeState.ZonePathsKind, ShadowkeyModelNativeState.ZoneLocksKind,
                     ShadowkeyModelNativeState.ZoneStaKind, ShadowkeyModelNativeState.ZonePlacementsKind,
                     ShadowkeyModelNativeState.MeshHeaderKind
                 })
        {
            Assert.Contains(kind, kinds);
        }

        Assert.False(metadata.Document.NativeStates.Single(static s => s.Kind == ShadowkeyModelNativeState.ZoneCellsKind)
            .HasRawContent);
        Assert.Equal(6 * 4, full.Document.NativeStates.Single(static s => s.Kind == ShadowkeyModelNativeState.ZoneCellsKind)
            .RawByteLength);
    }

    [Fact]
    public void AMissingCompanion_OrAStreamThatDoesNotEndAtTheLastByte_IsInvalidData()
    {
        var missing = new ShadowkeyTestBuilder.Zone { Omit = [".zcp"] }.Build();
        var trailing = new ShadowkeyTestBuilder.Zone().Build();
        trailing["testzone.zlu"] = [.. trailing["testzone.zlu"], 0];

        Assert.Contains("testzone.zcp", Assert.Throws<InvalidDataException>(() => ReadZone(missing)).Message,
            StringComparison.Ordinal);
        Assert.Contains("Adler-32", Assert.Throws<InvalidDataException>(() => ReadZone(trailing)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MorePlacementsThanTheComposerAdmits_AreRefusedBeforeComposing()
    {
        var files = PlacedZone(ModelSceneComposerLimit() - 3 - 2 + 1).Build();

        var error = Assert.Throws<NotSupportedException>(() => ReadZone(files));

        Assert.Contains("placements", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherGame_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => ReadZone(new ShadowkeyTestBuilder.Zone().Build(), options: Game("redguard")));
    }

    /// <summary>
    ///     A prototype only blocked cells use (cut-2 review finding 4): the terrain draws none of its corner heights, so the
    ///     prototypes row keeps every record's corners, the cells row keeps every cell's prototype index (the join to the
    ///     surface slots and edge bytes), and the census gives those heights their own NativeOnly element. Control: with
    ///     the second cell open the element disappears, and its heights reach the terrain.
    /// </summary>
    [Fact]
    public void APrototypeOnlyBlockedCellsUse_KeepsItsCornersInNativeState_AndItsOwnCensusElement()
    {
        var raised = new ShadowkeyTestBuilder.Prototype([0x100, 0x110, 0x120, 0x130], [0x300, 0x310, 0x320, 0x330]);
        ShadowkeyTestBuilder.Zone TwoCells(byte secondFlags)
        {
            return new ShadowkeyTestBuilder.Zone
            {
                Width = 2, Height = 1, Cells = [(0, 0), (secondFlags, 1)],
                Prototypes = [ShadowkeyTestBuilder.Prototype.Flat, raised]
            };
        }

        var blocked = ReadZone(TwoCells(ShadowkeyMapCell.BlockedFlag).Build());
        var open = ReadZone(TwoCells(0).Build());

        Assert.Equal(ModelSourceCoverageKind.NativeOnly,
            blocked.Coverage.GetClassification(ShadowkeyModelCoverage.UnplacedHeightsIdentity).Kind);
        Assert.Equal(new[] { "zmp:header", "zmp:cells", "zcp:heights", ShadowkeyModelCoverage.UnplacedHeightsIdentity },
            blocked.Coverage.Elements.Take(4).Select(static e => e.Identity));
        var cells = JsonNode.Parse(blocked.Document.NativeStates
            .Single(static s => s.Kind == ShadowkeyModelNativeState.ZoneCellsKind).PayloadJson)!;
        Assert.Equal(new[] { 0, 1 }, cells["prototype"]!.AsArray().Select(static v => v!.GetValue<int>()));
        var prototypes = JsonNode.Parse(blocked.Document.NativeStates
            .Single(static s => s.Kind == ShadowkeyModelNativeState.ZonePrototypesKind).PayloadJson)!;
        Assert.Equal(new[] { 0x100, 0x110, 0x120, 0x130 },
            prototypes["floorCorners"]![1]!.AsArray().Select(static v => v!.GetValue<int>()));
        Assert.Equal(new[] { 0x300, 0x310, 0x320, 0x330 },
            prototypes["ceilingCorners"]![1]!.AsArray().Select(static v => v!.GetValue<int>()));
        // Without the native arrays those heights would exist nowhere: no terrain vertex carries 0x110.
        Assert.DoesNotContain(TerrainHeights(blocked.Document), static z => z == 0x110);

        Assert.DoesNotContain(open.Coverage.Elements,
            static e => e.Identity == ShadowkeyModelCoverage.UnplacedHeightsIdentity);
        Assert.Contains(TerrainHeights(open.Document), static z => z == 0x110);
    }

    private static IEnumerable<float> TerrainHeights(ModelDocument document)
    {
        return document.Meshes.Single(static m => m.Name.EndsWith(" terrain", StringComparison.Ordinal)).Primitives
            .SelectMany(static p => p.Vertices).Select(static v => v.Position.Z);
    }

    private static int ModelSceneComposerLimit()
    {
        return Slfx77.Multitool.Core.Models.Composition.ModelSceneComposer.MaximumPlacements;
    }
}
