using FalloutXbox360Utils.Core.Semantic;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Semantic;

/// <summary>
///     Architectural smoke test: every supported format must load through the unified
///     SemanticFileLoader and produce a populated RecordCollection. New format adapters
///     should add a case here so the architectural rule is enforced by CI.
/// </summary>
public sealed class UnifiedPipelineIntegrationTests(SampleFileFixture samples)
{
    [Fact]
    public async Task Xbox360EsmLoadsThroughUnifiedPipeline()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 ESM sample not available");

        using var result = await SemanticFileLoader.LoadAsync(samples.Xbox360FinalEsm!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Records);
        Assert.True(result.Records.Cells.Count > 0,
            "Xbox 360 ESM should produce at least one CellRecord through the unified pipeline");
        Assert.True(result.Records.Worldspaces.Count > 0,
            "Xbox 360 ESM should produce at least one WorldspaceRecord");
    }

    [Fact]
    public async Task PcEsmLoadsThroughUnifiedPipeline()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC ESM sample not available");

        using var result = await SemanticFileLoader.LoadAsync(samples.PcFinalEsm!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Records);
        Assert.True(result.Records.Cells.Count > 0,
            "PC ESM should produce at least one CellRecord through the unified pipeline " +
            "(no separate PC ESM analyzer needed; EsmFileAnalyzer detects endian via magic)");
        Assert.True(result.Records.Worldspaces.Count > 0,
            "PC ESM should produce at least one WorldspaceRecord");
    }

    [Fact]
    public async Task DmpLoadsThroughUnifiedPipeline()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug DMP sample not available");

        using var result = await SemanticFileLoader.LoadAsync(samples.DebugDump!, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result.Records);
        Assert.True(result.Records.Cells.Count > 0,
            "DMP should produce at least one CellRecord through the unified pipeline " +
            "(any consumer that wants per-cell DMP data must read RecordCollection.Cells, " +
            "not call the raw scanner directly)");
    }
}