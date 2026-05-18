using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

#pragma warning disable CA1822, S2325 // Deliberately instance-shaped for UI service injection and tests.

namespace FalloutXbox360Utils;

internal sealed record DmpToEspConversionJob(
    DmpToEspInputs Inputs,
    bool PackAssets,
    AssetPackingOptions? AssetPackingOptions);

internal sealed record DmpToEspConversionJobResult(
    PluginBuildResult ConversionResult,
    AssetPackingResult? AssetPackingResult);

/// <summary>
///     Runs DMP-to-ESP conversion and optional asset packing away from the WinUI
///     code-behind. The tab remains responsible for UI state and file pickers only.
/// </summary>
internal sealed class DmpToEspConversionJobService
{
    public async Task<DmpToEspConversionJobResult> RunAsync(
        DmpToEspConversionJob job,
        IConversionProgressSink sink,
        CancellationToken cancellationToken)
    {
        var registry = RecordEncoderRegistry.CreateV23Default();
        var pipeline = new PluginConversionPipeline(registry, sink);

        var conversion = await Task.Run(
            () => pipeline.BuildAsync(job.Inputs, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        AssetPackingResult? packing = null;
        if (conversion.Success && job.PackAssets)
        {
            packing = await RunAssetPackingAsync(
                conversion,
                job.AssetPackingOptions,
                sink,
                cancellationToken).ConfigureAwait(false);
        }

        return new DmpToEspConversionJobResult(conversion, packing);
    }

    private static async Task<AssetPackingResult?> RunAssetPackingAsync(
        PluginBuildResult conversionResult,
        AssetPackingOptions? options,
        IConversionProgressSink sink,
        CancellationToken cancellationToken)
    {
        if (conversionResult.OutputPath is null)
        {
            sink.Warn("AssetPacking", "Skipping asset packing — no ESP output path");
            return null;
        }

        if (options is null)
        {
            sink.Warn("AssetPacking",
                "Asset packing was enabled but no complete asset packing options were provided");
            return null;
        }

        if (options.SecondaryDataFolders.Count == 0)
        {
            sink.Warn("AssetPacking",
                "Asset packing was enabled but no secondary data folders were provided");
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.OutputBsaPath))
        {
            sink.Warn("AssetPacking", "Asset packing was enabled but no output BSA path was provided");
            return null;
        }

        var service = new AssetPackingService();
        options = options with { ConvertedEspPath = conversionResult.OutputPath };
        return await Task.Run(
            () => service.PackAsync(options, sink, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }
}
