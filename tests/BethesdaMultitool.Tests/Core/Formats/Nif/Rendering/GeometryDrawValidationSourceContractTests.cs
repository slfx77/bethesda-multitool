using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Source contracts protecting the geometry draw-validation wiring. The validator gates only the
///     cmd.* draw calls; the per-batch instance accounting (<c>startInstance += …</c>) MUST stay
///     unconditional, because every batch's instances are compacted into the shared block at fixed
///     offsets before the draw loop runs — skipping the accounting would desync every later batch's
///     <c>uInstanceBase</c>. This mirrors the invariant the grass-envelope / wind pins protect.
/// </summary>
public sealed class GeometryDrawValidationSourceContractTests
{
    [Fact]
    public void ValidatorRunsBeforeTheGeometryBindsAndGatesOnlyTheDraws()
    {
        var source = ReadRenderer();
        var loop = Extract(source, "private void DrawOpaqueBatches(", "private void DrawBlended(");

        // The validator is called before the vertex/index binds.
        SourceContract.AssertOrder(
            loop,
            "GeometryDrawValidator12.ValidateOpaqueDraw(",
            "cmd.IASetVertexBuffers(0, batchState.Submesh.EffectiveVertexBufferView);",
            "cmd.IASetIndexBuffer(batchState.Submesh.IndexBufferView);");

        // Both draw sites (main + shadow capture) are gated on the validation result.
        Assert.Contains("if (drawCount > 0 && drawLiveness)", loop, StringComparison.Ordinal);
        Assert.Contains("drawCount + shadowCount > 0 && drawLiveness", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceAccountingAdvancesUnconditionally()
    {
        var source = ReadRenderer();
        var loop = Extract(source, "private void DrawOpaqueBatches(", "private void DrawBlended(");

        // The running instance base must advance for every batch regardless of validation, and it
        // must not be wrapped in the drawLiveness gate.
        Assert.Contains("startInstance += (uint)(drawCount + shadowCount);", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("if (drawLiveness) startInstance", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationIsGatedOffByDefault()
    {
        var source = ReadRenderer();
        var loop = Extract(source, "private void DrawOpaqueBatches(", "private void DrawBlended(");

        // With diagnostics disabled the validator is short-circuited (drawLiveness stays true), so
        // the disabled hot path is a single static-readonly branch.
        Assert.Contains("!GeometryArenaDiagnostics.Enabled ||", loop, StringComparison.Ordinal);
    }

    private static string ReadRenderer()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "ReferenceRenderer12.cs");
    }

    private static string Extract(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker `{startMarker}`.");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing end marker `{endMarker}` after `{startMarker}`.");
        return source[start..end];
    }
}