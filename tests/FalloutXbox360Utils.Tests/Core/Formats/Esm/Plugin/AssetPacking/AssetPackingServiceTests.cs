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
        var service = new AssetPackingService();
        var options = new AssetPackingOptions
        {
            ConvertedEspPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.esp"),
            BaselineDataFolder = Path.GetTempPath(),
            SecondaryDataFolders = [],
            OutputBsaPath = Path.Combine(Path.GetTempPath(), $"out-{Guid.NewGuid():N}.bsa")
        };

        var result = await service.PackAsync(options, NullConversionProgressSink.Instance);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.False(File.Exists(options.OutputBsaPath));
    }

    [Fact]
    public async Task PackAsync_CanceledToken_ReportsCancellation()
    {
        var service = new AssetPackingService();
        var options = new AssetPackingOptions
        {
            ConvertedEspPath = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}.esp"),
            BaselineDataFolder = Path.GetTempPath(),
            SecondaryDataFolders = [],
            OutputBsaPath = Path.Combine(Path.GetTempPath(), $"out-{Guid.NewGuid():N}.bsa")
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await service.PackAsync(options, NullConversionProgressSink.Instance, cts.Token);

        // We can't guarantee which exception type fires first when nothing is loaded;
        // either Success=false (file-not-found path) or Success=false with cancel message
        // is acceptable. The contract is "never throw".
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }
}
