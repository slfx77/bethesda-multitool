using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Public orchestration entrypoint for DMP-to-ESM conversion. Delegates to
///     <see cref="PluginBuilder" />, which runs the plan-then-serialize pipeline.
/// </summary>
public sealed class PluginConversionPipeline
{
    private readonly PluginBuilder _builder;

    /// <summary>Creates the pipeline with the record-encoder registry and an optional progress sink.</summary>
    public PluginConversionPipeline(RecordEncoderRegistry registry, IConversionProgressSink? sink = null)
    {
        _builder = new PluginBuilder(registry, sink);
    }

    /// <summary>Runs the full DMP-to-ESM conversion, writing the plugin to the path in <paramref name="inputs" />.</summary>
    public Task<PluginBuildResult> BuildAsync(DmpToEsmInputs inputs, CancellationToken ct = default)
    {
        return _builder.BuildAsync(inputs, ct);
    }
}
