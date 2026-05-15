using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Public orchestration entrypoint for DMP-to-ESP conversion. The pipeline owns the
///     composed conversion services; legacy <see cref="PluginBuilder"/> remains as the
///     lower-level implementation while the remaining emission stages are extracted.
/// </summary>
public sealed class PluginConversionPipeline
{
#pragma warning disable CS0618
    private readonly PluginBuilder _builder;
#pragma warning restore CS0618

    public PluginConversionPipeline(RecordEncoderRegistry registry, IConversionProgressSink? sink = null)
    {
#pragma warning disable CS0618
        _builder = new PluginBuilder(registry, sink);
#pragma warning restore CS0618
    }

    public Task<PluginBuildResult> BuildAsync(DmpToEspInputs inputs, CancellationToken ct = default)
    {
        return _builder.BuildAsync(inputs, ct);
    }
}
