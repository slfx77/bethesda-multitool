using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

public sealed class SubmittedInstanceMatrixMatcherTests
{
    [Fact]
    public void FindExactMatch_ReturnsDrawLocalIndexInsideANonzeroWindow()
    {
        var target = Matrix4x4.CreateTranslation(13, 21, 34);
        Matrix4x4[] instances = [target, Matrix4x4.Identity, Matrix4x4.Identity, target, target];

        Assert.Equal(1, SubmittedInstanceMatrixMatcher.FindExactMatch(instances, 2, 2, target));
    }

    [Fact]
    public void FindExactMatch_ExcludesMatchingMatricesBeforeAndAfterTheSubmittedWindow()
    {
        var target = Matrix4x4.CreateTranslation(13, 21, 34);
        Matrix4x4[] instances = [target, Matrix4x4.Identity, Matrix4x4.Identity, target];

        Assert.Equal(-1, SubmittedInstanceMatrixMatcher.FindExactMatch(instances, 1, 2, target));
        Assert.Equal(0, SubmittedInstanceMatrixMatcher.FindExactMatch(instances, 3, 1, target));
    }

    [Fact]
    public void FindExactMatch_RejectsEvenOneCoefficientOneUlpAway()
    {
        var target = Matrix4x4.CreateTranslation(13, 21, 34);
        var other = target;
        other.M43 = MathF.BitIncrement(other.M43);

        Assert.Equal(-1, SubmittedInstanceMatrixMatcher.FindExactMatch([other], 0, 1, target));
    }

    [Fact]
    public void FindExactMatch_RequiresTheExactRelativeMatrixNotTheAbsolutePlacement()
    {
        var absolute = Matrix4x4.CreateTranslation(8193, 16386, 35);
        var relative = absolute;
        relative.Translation -= new Vector3(8192, 16384, 0);

        Assert.Equal(-1, SubmittedInstanceMatrixMatcher.FindExactMatch([relative], 0, 1, absolute));
        Assert.Equal(0, SubmittedInstanceMatrixMatcher.FindExactMatch([relative], 0, 1, relative));
    }

    [Fact]
    public void FindExactMatch_RejectsAnEmptySpan()
    {
        var target = Matrix4x4.Identity;

        Assert.Equal(-1, SubmittedInstanceMatrixMatcher.FindExactMatch([], 0, 1, target));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(2, 2)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(1, int.MaxValue)]
    public void FindExactMatch_RejectsInvalidWindowsWithoutOverflow(int start, int count)
    {
        var target = Matrix4x4.Identity;
        Matrix4x4[] instances = [target, target, target];

        Assert.Equal(-1, SubmittedInstanceMatrixMatcher.FindExactMatch(instances, start, count, target));
    }

    [Fact]
    public void NativeAdtPolicyReceivesTheAuthoredComparisonAndRuntimeCutoutGate()
    {
        var renderer = RendererSource("ReferenceRenderer12.cs");
        var eligibility = SourceContract.Extract(renderer,
            "private FnvActiveAdtBaseEligibility ResolveFnvActiveAdtBaseEligibility(",
            "private string? ResolveFnvActiveAdtBaseFallbackReason()");
        SourceContract.AssertOrder(eligibility,
            "submesh.AlphaBlend,", "submesh.AlphaTest,", "submesh.MaterialAlpha,",
            "submesh.MaterialAlphaController is not null,", "submesh.ClassicBasicShaderMode,",
            "submesh.AlphaTestFunction,", "_fnvActiveAdtFogSupported);");
        SourceContract.AssertOrder(renderer,
            "state.Z = FnvActiveAdtBasePolicy.ApplyRuntimeFlags(",
            "allowAlphaTested: FnvAdtAlphaTestEnabled);");
        Assert.Contains("EnvironmentVariables.Get(EnvironmentVariables.Viewer.FnvAdtAlphaTest) != \"0\";",
            RendererSource("ReferenceRenderer12.FnvDiagnostics.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void NativeTraceRequiresPostDrawSubmittedMatricesAndRefreshesRecreatedCacheObjects()
    {
        var renderer = RendererSource("ReferenceRenderer12.cs");
        var overrides = SourceContract.Extract(renderer,
            "private void TraceReferenceTextureOverrides(", "private void ProcessShadowBatchReference(");
        SourceContract.AssertOrder(overrides,
            "var matches = mesh.Submeshes.Where(",
            "RegisterFnvAdTarget(reference, shapeName, textureOverride, diffuseKey, normalKey, matches);",
            "if (!_referenceOverrideTraceLogged.Add(signature))");

        Assert.Equal(1, SourceContract.CountOccurrences(renderer, "ObserveFnvAdTargetInstancedDraw("));
        Assert.Equal(1, SourceContract.CountOccurrences(renderer, "ObserveFnvAdTargetIndividualDraw("));
        SourceContract.AssertOrder(renderer,
            "cmd.DrawIndexedInstanced((uint)batchState.Submesh.IndexCount, (uint)drawCount, 0, 0, 0);",
            "ObserveFnvAdTargetInstancedDraw(",
            "drawInstanceCpuBase, drawStartInstance, drawCount);");
        SourceContract.AssertOrder(renderer,
            "cmd.DrawIndexedInstanced((uint)effectiveIndexCount, 1, 0, 0, 0);",
            "ObserveFnvAdTargetIndividualDraw(draw, textureState, alphaState, effectiveIndexCount);");

        var diagnostics = RendererSource("ReferenceRenderer12.FnvDiagnostics.cs");
        Assert.Contains("!OpaqueIndirectRequested && !StaticOpaquePacketRequested", diagnostics,
            StringComparison.Ordinal);
        Assert.Contains("if (matches.Length != 1 || (diffuseKey is null && normalKey is null))", diagnostics,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(diagnostics,
            "relativeWorld.Translation -= _frameRenderOrigin;",
            "SubmittedInstanceMatrixMatcher.FindExactMatch(",
            "instances, (int)drawStartInstance, drawCount, relativeWorld);",
            "if (matchIndex < 0) return;",
            "EmitFnvAdTargetDraw(");
        Assert.Contains("draw.PhysicsLiteSeed != target.ReferenceFormId || draw.SourceWorld != target.WorldMatrix",
            diagnostics, StringComparison.Ordinal);
    }

    private static string RendererSource(string name)
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", name);
    }
}
