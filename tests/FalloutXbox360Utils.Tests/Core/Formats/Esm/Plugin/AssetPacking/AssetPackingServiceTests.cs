using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Unit-level smoke tests for <see cref="AssetPackingService"/>. End-to-end packing
///     against a real ESP fixture lives in the CLI integration test in
///     <c>tests/FalloutXbox360Utils.Tests.E2E</c> (TODO).
/// </summary>
public class AssetPackingServiceTests
{
    [Fact]
    public async Task PackAsync_NonExistentEspPath_ReturnsFailureNotThrows()
    {
        var options = new AssetPackingOptions
        {
            ConvertedEspPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.esp"),
            BaselineDataFolder = Path.GetTempPath(),
            SecondaryDataFolders = [],
            OutputBsaPath = Path.Combine(Path.GetTempPath(), $"out-{Guid.NewGuid():N}.bsa")
        };

        var result = await AssetPackingService.PackAsync(options, NullConversionProgressSink.Instance);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(File.Exists(options.OutputBsaPath));
    }

    [Fact]
    public async Task PackAsync_CanceledToken_ReportsCancellation()
    {
        var options = new AssetPackingOptions
        {
            ConvertedEspPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.esp"),
            BaselineDataFolder = Path.GetTempPath(),
            SecondaryDataFolders = [],
            OutputBsaPath = Path.Combine(Path.GetTempPath(), $"out-{Guid.NewGuid():N}.bsa")
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await AssetPackingService.PackAsync(options, NullConversionProgressSink.Instance, cts.Token);

        // We can't guarantee which exception type fires first when nothing is loaded;
        // either Success=false (file-not-found path) or Success=false with cancel message
        // is acceptable. The contract is "never throw".
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void PlanBsaOutputs_MixedAssetClasses_UsesPluginSidecarNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "v50-xex43.bsa");
        var files = new List<(string Path, byte[] Data)>
        {
            ("meshes\\armor\\ulysses\\ulysses.nif", [1]),
            ("textures\\armor\\ulysses\\ulysses_n.dds", [2]),
            ("sound\\fx\\amb\\wind.wav", [3]),
            ("sound\\voice\\v50-xex43.esp\\maleuniqueulysses\\line.ogg", [4])
        };

        var plans = AssetPackingService.PlanBsaOutputs(root, files);

        Assert.Equal([
            Path.Combine(Path.GetTempPath(), "v50-xex43 - Main.bsa"),
            Path.Combine(Path.GetTempPath(), "v50-xex43 - Textures.bsa"),
            Path.Combine(Path.GetTempPath(), "v50-xex43 - Sounds.bsa"),
            Path.Combine(Path.GetTempPath(), "v50-xex43 - Voices.bsa")
        ], plans.Select(p => p.OutputPath).ToArray());
        Assert.DoesNotContain(plans, p => p.OutputPath == root);
        Assert.All(plans, p => Assert.Single(p.Files));
    }

    [Fact]
    public void PlanBsaOutputs_SingleAssetClass_UsesRequestedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "textures-only.bsa");
        var plans = AssetPackingService.PlanBsaOutputs(root,
        [
            ("textures\\armor\\a.dds", [1]),
            ("textures\\armor\\b.dds", [2])
        ]);

        var plan = Assert.Single(plans);
        Assert.Equal(root, plan.OutputPath);
        Assert.Equal(AssetPackBucket.Textures, plan.Bucket);
        Assert.Equal(2, plan.Files.Count);
    }

    [Fact]
    public void PlanBsaOutputs_OversizedBucket_ChunksWithNumberedSidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), "big.bsa");
        var plans = AssetPackingService.PlanBsaOutputs(root,
        [
            ("textures\\armor\\a.dds", new byte[140]),
            ("textures\\armor\\b.dds", new byte[140])
        ], maxArchiveBytes: 220);

        Assert.Equal([
            Path.Combine(Path.GetTempPath(), "big - Textures.bsa"),
            Path.Combine(Path.GetTempPath(), "big - Textures2.bsa")
        ], plans.Select(p => p.OutputPath).ToArray());
        Assert.All(plans, p => Assert.Single(p.Files));
    }
}
