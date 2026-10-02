using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>Checks BMT's embedded-source policy and diagnostic context through the shared compiler boundary.</summary>
public sealed class SharedShaderCompilerAdapterTests
{
    /// <summary>The shipped-pack fingerprint identifies the exact shared compiler revision and encoding contract.</summary>
    [Fact]
    public void CompilerContractMatchesSharedImplementation()
    {
        Assert.Equal(ShaderCompiler.GetCompilerContract(), GpuShaderCompiler12.BytecodeCompilerContract);
    }

    /// <summary>Root and nested requests use BMT's flat, case-insensitive embedded index regardless of parent location.</summary>
    /// <param name="requestedName">A bare or directory-qualified spelling of the same embedded header.</param>
    [Theory]
    [InlineData("fog.hlsli")]
    [InlineData("FOG.HLSLI")]
    [InlineData("unrelated/nested/FOG.HLSLI")]
    public void ProviderResolvesFlatNamesWithoutParentDirectoryCoupling(string requestedName)
    {
        var provider = new EmbeddedShaderSourceProvider();
        var expected = GpuShaderCompiler12.ReadSource("fog.hlsli");
        var root = provider.Load(requestedName, null);
        var nested = provider.Load(requestedName, "another/tree/root.hlsl");
        Assert.Equal(expected, root.Text);
        Assert.Equal(expected, nested.Text);
        Assert.Equal("fog.hlsli", root.Name, ignoreCase: true);
        Assert.Equal(root.Name, nested.Name);
        var atmosphere = provider.Load("ATMOSPHERE.HLSLI", nested.Name);
        Assert.Equal(GpuShaderCompiler12.ReadSource("atmosphere.hlsli"), atmosphere.Text);
    }

    /// <summary>A missing flat include retains its exact requested basename rather than falling through to unrelated resources.</summary>
    [Fact]
    public void ProviderReportsMissingEmbeddedHeader()
    {
        var provider = new EmbeddedShaderSourceProvider();
        var failure = Assert.Throws<FileNotFoundException>(() =>
            provider.Load("unrelated/__adapter_missing_header__.hlsli", "reference.frag.hlsl"));
        Assert.Contains("__adapter_missing_header__.hlsli", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The production BMT adapter compiles a directory-qualified header and its real nested atmosphere include.</summary>
    [Fact]
    [Trait("Category", TestCategories.ShaderCompile)]
    public void ProductionAdapterCompilesNestedEmbeddedIncludes()
    {
        RequireNativeCompiler();
        const string source = """
            #include "unrelated/nested/FOG.HLSLI"
            float4 main(float4 position : SV_Position) : SV_Target
            {
                return float4(ApplyFogAtDistance(float3(0.2, 0.4, 0.6), position.x), 1);
            }
            """;
        var bytecode = GpuShaderCompiler12.CompileSource(source, "adapter-nested-include.hlsl", "main", "ps_5_1");
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.True(bytecode.Length > 4);
        Assert.Equal("DXBC", Encoding.ASCII.GetString(bytecode, 0, 4));
    }

    /// <summary>Native include failures retain both BMT's permutation context and the original managed missing-source diagnostic.</summary>
    [Fact]
    [Trait("Category", TestCategories.ShaderCompile)]
    public void MissingIncludePreservesNativeAndManagedDiagnostics()
    {
        RequireNativeCompiler();
        const string source = """
            #include "nested/__adapter_missing_header__.hlsli"
            float4 main() : SV_Target { return float4(0, 1, 0, 1); }
            """;
        var failure = Assert.Throws<InvalidOperationException>(() => GpuShaderCompiler12.CompileSource(
            source, "adapter-missing-include.hlsl", "main", "ps_5_1", new ShaderMacro("ADAPTER_CASE", "7")));
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.Contains("adapter-missing-include.hlsl", failure.Message, StringComparison.Ordinal);
        Assert.Contains("main/ps_5_1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ADAPTER_CASE=7", failure.Message, StringComparison.Ordinal);
        var sharedFailure = Assert.IsType<InvalidOperationException>(failure.InnerException);
        var sourceFailure = Assert.IsType<FileNotFoundException>(sharedFailure.InnerException);
        Assert.Contains("__adapter_missing_header__.hlsli", sharedFailure.Message, StringComparison.Ordinal);
        Assert.Contains("__adapter_missing_header__.hlsli", sourceFailure.Message, StringComparison.Ordinal);
    }

    /// <summary>A macro-selected compile failure preserves its actual definition alongside the native shader diagnostic.</summary>
    [Fact]
    [Trait("Category", TestCategories.ShaderCompile)]
    public void MacroFailureRetainsBmtPermutationContext()
    {
        RequireNativeCompiler();
        const string source = """
            #ifndef ADAPTER_VALUE
            #define ADAPTER_VALUE float4(0, 1, 0, 1)
            #endif
            float4 main() : SV_Target { return ADAPTER_VALUE; }
            """;
        var valid = GpuShaderCompiler12.CompileSource(source, "adapter-macro-context.hlsl", "main", "ps_5_1");
        Assert.NotEmpty(valid);
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var failure = Assert.Throws<InvalidOperationException>(() => GpuShaderCompiler12.CompileSource(
            source, "adapter-macro-context.hlsl", "main", "ps_5_1",
            new ShaderMacro("ADAPTER_VALUE", "undefined_adapter_value")));
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        Assert.Contains("adapter-macro-context.hlsl", failure.Message, StringComparison.Ordinal);
        Assert.Contains("main/ps_5_1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("ADAPTER_VALUE=undefined_adapter_value", failure.Message, StringComparison.Ordinal);
        var sharedFailure = Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Contains("undefined_adapter_value", sharedFailure.Message, StringComparison.Ordinal);
    }

    /// <summary>Honors the existing opt-in compiler gate and cancellation before any synchronous native work.</summary>
    private static void RequireNativeCompiler()
    {
        ShaderCompileTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The shared shader compiler requires Windows D3DCompiler.");
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
    }
}
