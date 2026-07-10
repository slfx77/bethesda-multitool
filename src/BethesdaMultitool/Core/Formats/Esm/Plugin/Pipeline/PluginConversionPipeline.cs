using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;

/// <summary>
///     Public orchestration entrypoint for DMP-to-ESM conversion. The pipeline owns the
///     composed conversion services; legacy <see cref="PluginBuilder" /> remains as the
///     lower-level implementation while the remaining emission stages are extracted.
/// </summary>
public sealed class PluginConversionPipeline
{
#pragma warning disable CS0618
    private readonly PluginBuilder _builder;
#pragma warning restore CS0618

    /// <summary>Creates the pipeline with the record-encoder registry and an optional progress sink.</summary>
    public PluginConversionPipeline(RecordEncoderRegistry registry, IConversionProgressSink? sink = null)
    {
#pragma warning disable CS0618
        _builder = new PluginBuilder(registry, sink);
#pragma warning restore CS0618
    }

    /// <summary>Runs the full DMP-to-ESM conversion, writing the plugin to the path in <paramref name="inputs" />.</summary>
    public Task<PluginBuildResult> BuildAsync(DmpToEsmInputs inputs, CancellationToken ct = default)
    {
        return _builder.BuildAsync(inputs, ct);
    }
}
