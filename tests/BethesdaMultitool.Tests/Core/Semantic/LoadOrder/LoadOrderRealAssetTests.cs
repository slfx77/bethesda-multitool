using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Semantic.LoadOrder;

/// <summary>The complete 2011 build contains two distinct DLC plugins with overlapping local IDs.</summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public sealed class LoadOrderRealAssetTests
{
    [Fact]
    public void Complete_2011_bundle_keeps_both_dead_money_terminal_identities()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.NewVegasBuilds.X360Proto2011();
        var early = RealAssetPaths.NewVegasBuilds.X360Proto2011("DeadMansHand.esm");
        var final = RealAssetPaths.NewVegasBuilds.X360Proto2011("DeadMoney.esm");
        Assert.SkipUnless(root != null && early != null && final != null,
            RealAssetPaths.SkipMessage("Complete 2011 New Vegas prototype bundle"));
        var order = PluginLoadOrder.Open([root!, early!, final!]);
        var index = LoadOrderRecordIndex.Build(order, TestContext.Current.CancellationToken);
        var candidates = index.FindByEditorId("NVDLC01VaultMainInfoDownloadTerminal");
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, candidate => Assert.Equal("TERM", candidate.Winner.Signature));
        Assert.Contains(candidates, c => c.Winner.Plugin == "DeadMansHand.esm" && (c.LoadOrderFormId >> 24) == 1);
        Assert.Contains(candidates, c => c.Winner.Plugin == "DeadMoney.esm" && (c.LoadOrderFormId >> 24) == 2);
        Assert.Equal(0x0200DAD2u, index.ResolveTarget("DeadMoney.esm:0x0100DAD2", order));
        Assert.Throws<ArgumentException>(() => index.ResolveTarget("NVDLC01VaultMainInfoDownloadTerminal", order));
    }
}
