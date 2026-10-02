using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

/// <summary>Independent audit pins for the OWB rats and their differing prototype inventory chain.</summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public sealed class ActorTemplateRetailTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RadRat_HealthAndInventoryAreBuildSpecific(bool prototype)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var relative = prototype
            ? "Builds/Fallout - New Vegas (2011-2-15, X360 - Prototype)/FalloutNV/Data/OldWorldBlues.esm"
            : "Builds/Fallout - New Vegas (2022-5-24, Steam - Final)/Data/OldWorldBlues.esm";
        var path = RealAssetPaths.SampleFile(relative);
        Assert.SkipUnless(path is not null, RealAssetPaths.SkipMessage(relative));
        var loaded = await RealAssetEsmCache.LoadAsync(path, TestContext.Current.CancellationToken);
        var identity = new ActorSourceIdentity { PrimaryFileName = "OldWorldBlues.esm",
            Masters = [new("FalloutNV.esm", false)] };
        var inspector = new ActorInspector(loaded.Records, identity);
        var rat = inspector.Inspect(0x01014D8D, true)!;
        var health = Assert.Single(rat.EffectiveStatistics, s => s.Key == "Health");
        Assert.Equal("675", health.Value);
        Assert.Equal("Authored", health.Provenance);
        Assert.Contains(rat.EffectiveStatistics, s => s.Key == "AttackDamage" && s.Value == "15");
        Assert.Null(rat.InventoryNotice);
        if (prototype) Assert.Empty(rat.Inventory);
        else
        {
            var row = Assert.Single(rat.Inventory);
            Assert.Equal(0x001293A2u, row.ItemFormId);
            Assert.Equal(0x01014308u, row.SourceActor);
            var lobotomite = inspector.Inspect(0x01013057, false)!;
            // Raw decompressed records at file offsets 0x138FD9 and 0x139C81 both contain
            // 0016BB9D. Presence cannot distinguish inherited from leftover local CNTO.
            // ACBS bytes 22..23 are FD 03 (child) and 41 00 (source): UseInventory is
            // set only on the child. The source's CNTO order is independently different.
            var local = Assert.Single(loaded.Records.Npcs, n => n.FormId == 0x01013057);
            var source = Assert.Single(loaded.Records.Npcs, n => n.FormId == 0x0101304B);
            Assert.Equal((ushort)0x03FD, local.Stats!.TemplateFlags);
            Assert.Equal((ushort)0x0041, source.Stats!.TemplateFlags);
            Assert.Equal(new uint[] { 0x0016BB9D, 0x0101387F, 0x0101320D, 0x01013880 },
                local.Inventory.Select(i => i.ItemFormId));
            var inheritedOrder = new uint[] { 0x01013880, 0x0101320D, 0x0101387F, 0x0016BB9D };
            Assert.Equal(inheritedOrder, source.Inventory.Select(i => i.ItemFormId));
            var inventoryGroup = Assert.Single(lobotomite.TemplateGroups,
                g => g.Group == ActorTemplateGroup.UseInventory);
            Assert.Equal("Inherited", inventoryGroup.Status);
            Assert.Equal(0x0101304Bu, inventoryGroup.SourceActor);
            Assert.Equal(inheritedOrder, lobotomite.Inventory.Select(i => i.ItemFormId));
            Assert.All(lobotomite.Inventory, i =>
            {
                Assert.Equal(0x0101304Bu, i.SourceActor);
                Assert.Equal(1L, i.Count);
            });
            var leveled = inspector.Inspect(0x0101692C, true)!;
            Assert.Contains(leveled.TemplateGroups, g => g.Group == ActorTemplateGroup.UseStats && g.Status == "LeveledTemplate");
            var absent = inspector.Inspect(0x01014DF2, true)!;
            Assert.Contains(absent.TemplateGroups, g => g.Status == "MasterNotLoaded" && g.SourcePlugin == "FalloutNV.esm");
        }
    }
}
