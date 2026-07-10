using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

#pragma warning disable CA1822, S2325 // Deliberately instance-shaped for UI service injection and tests.

namespace BethesdaMultitool;

/// <summary>Inputs for a DMP-to-ESM conversion run plus whether (and how) to pack assets into a BSA.</summary>
internal sealed record DmpToEsmConversionJob(
    DmpToEsmInputs Inputs,
    bool PackAssets,
    AssetPackingOptions? AssetPackingOptions);

/// <summary>Output of a conversion job: the plugin build result and the optional asset-packing result.</summary>
internal sealed record DmpToEsmConversionJobResult(
    PluginBuildResult ConversionResult,
    AssetPackingResult? AssetPackingResult);

/// <summary>
///     Runs DMP-to-ESM conversion and optional asset packing away from the WinUI
///     code-behind. The tab remains responsible for UI state and file pickers only.
/// </summary>
internal sealed class DmpToEsmConversionJobService
{
    public async Task<DmpToEsmConversionJobResult> RunAsync(
        DmpToEsmConversionJob job,
        IConversionProgressSink sink,
        CancellationToken cancellationToken)
    {
        var registry = RecordEncoderRegistry.CreateDefault();
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

        return new DmpToEsmConversionJobResult(conversion, packing);
    }

    private static async Task<AssetPackingResult?> RunAssetPackingAsync(
        PluginBuildResult conversionResult,
        AssetPackingOptions? options,
        IConversionProgressSink sink,
        CancellationToken cancellationToken)
    {
        if (conversionResult.OutputPath is null)
        {
            sink.Warn("AssetPacking", "Skipping asset packing — no ESM output path");
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

        options = options with { ConvertedEsmPath = conversionResult.OutputPath };
        return await Task.Run(
            () => AssetPackingService.PackAsync(options, sink, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }
}
