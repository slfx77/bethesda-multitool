using System.Reflection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Structural guards over the shader inventory. All UNGATED and compiler-free (milliseconds), so
///     they run in CI — which matters because CI does NOT set <c>RUN_SHADER_COMPILE_TESTS</c>, and so
///     compiles almost no HLSL. These convert three failure modes that previously only appeared at
///     runtime, as a silently missing 3D view, into build-time test failures.
/// </summary>
public sealed class ShaderInventoryTests
{
    private const string ResourcePrefix = "BethesdaMultitool.Shaders.";

    private static readonly string ShaderDirectory = Path.Combine(
        SourceContract.RepoRoot, "src", "BethesdaMultitool",
        "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders");

    private static string[] EmbeddedShaderNames() =>
        [.. typeof(ShaderPermutation).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourcePrefix.Length..])];

    [Fact]
    public void EveryShaderOnDiskIsEmbedded()
    {
        // The csproj glob is the single point of failure between "I added a shader" and "the shader
        // exists at runtime". It was previously `Shaders\*.hlsl` — single star, .hlsl only — so a
        // shader in a subdirectory, or any .hlsli header, was silently NOT embedded and surfaced only
        // as a FileNotFoundException the first time that code path ran. There is no build error for
        // this, which is exactly why it needs a test.
        var onDisk = Directory
            .EnumerateFiles(ShaderDirectory, "*.hlsl*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var embedded = EmbeddedShaderNames().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = onDisk.Except(embedded, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        Assert.True(
            missing.Length == 0,
            $"On disk but NOT embedded (widen the csproj glob): {string.Join(", ", missing)}");

        var orphaned = embedded.Except(onDisk, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        Assert.True(
            orphaned.Length == 0,
            $"Embedded but no longer on disk (stale obj/ artifacts?): {string.Join(", ", orphaned)}");
    }

    [Fact]
    public void ShaderFilenamesAreGloballyUnique()
    {
        // Runtime lookup is an exact dictionary hit keyed on the flat file name, and the csproj's
        // explicit LogicalName makes a collision a build error. This asserts the invariant directly
        // so the failure names the offender instead of surfacing as a duplicate-resource build error.
        var duplicates = EmbeddedShaderNames()
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Order()
            .ToArray();

        Assert.True(duplicates.Length == 0, $"Duplicate shader file names: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void EveryEntryShaderIsReachedByAPermutation()
    {
        // Kills the rot that produced the misnamed EveryRemainingEmbeddedRenderingEntryPointCompiles:
        // a hand-maintained 26-tuple list that new shaders were simply never added to. Every entry
        // point (everything that is not an .hlsli header) must appear in ShaderPermutations.All, so a
        // new shader is either covered or it fails here.
        var covered = ShaderPermutations.All
            .Select(p => p.File)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entryShaders = EmbeddedShaderNames()
            .Where(n => n.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase));

        var uncovered = entryShaders
            .Where(n => !covered.Contains(n))
            .Order()
            .ToArray();

        Assert.True(
            uncovered.Length == 0,
            $"Embedded entry shaders absent from ShaderPermutations.All: {string.Join(", ", uncovered)}");
    }

    [Fact]
    public void EveryPermutationNamesAnEmbeddedShader()
    {
        var embedded = EmbeddedShaderNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dangling = ShaderPermutations.All
            .Where(p => !embedded.Contains(p.File))
            .Select(p => $"{p.File} [{p.EntryPoint}]")
            .Order()
            .ToArray();

        Assert.True(dangling.Length == 0, $"Permutations naming no embedded shader: {string.Join(", ", dangling)}");
    }

    [Fact]
    public void PermutationsAreDistinct()
    {
        // A duplicate permutation is a compile that buys nothing. The stale FO76_WATER entry was
        // exactly this: a macro that appears in no .hlsl file, producing a byte-identical duplicate
        // of the FO4 permutation while implying FO76 had its own coverage.
        var duplicates = ShaderPermutations.All
            .GroupBy(p => $"{p.File}|{p.EntryPoint}|{p.Profile}|" +
                          string.Join(",", p.Macros.Select(m => $"{m.Name}={m.Definition}").Order(StringComparer.Ordinal)),
                StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.True(duplicates.Length == 0, $"Duplicate permutations: {string.Join(" ; ", duplicates)}");
    }

    [Fact]
    public void UnboundedDescriptorTableFlagHasExactlyOneDecisionSite()
    {
        // D3DCOMPILE_ENABLE_UNBOUNDED_DESCRIPTOR_TABLES used to be inferred by substring-scanning each
        // shader's TOP-LEVEL text, via six divergent rules across ~14 call sites — one of which was
        // satisfied by a comment rather than a declaration. Any future move of a `[] : register`
        // declaration into a shared header would have stopped them firing, giving FXC X3596, which the
        // GUI degrades to a warning and a blank viewport. It is now unconditional in one place.
        var compiler = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuShaderCompiler12.cs");
        Assert.Contains(
            "private const ShaderFlags EnableUnboundedDescriptorTables = (ShaderFlags)0x00100000;",
            compiler,
            StringComparison.Ordinal);
        Assert.Contains("EnableUnboundedDescriptorTables,", compiler, StringComparison.Ordinal);

        // No renderer may re-derive the flag from shader text, and none may call the D3D compiler
        // directly — both would reintroduce a second decision site.
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(SourceContract.RepoRoot, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals("GpuShaderCompiler12.cs", StringComparison.Ordinal)) continue;
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Compiler.Compile(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("(ShaderFlags)0x00100000", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ResourceIndexResolvesEveryPermutationByExactName()
    {
        // Lookup used to be EndsWith()+FirstOrDefault(), which resolved a suffix collision by
        // metadata order at runtime while the tests' .Single() threw on the same input. Exact-name
        // resolution removes the ambiguity; this proves every permutation actually resolves.
        foreach (var permutation in ShaderPermutations.All)
        {
            Assert.True(
                GpuShaderCompiler12.ResourceIndex.ContainsKey(permutation.File),
                $"No embedded resource for '{permutation.File}'.");
        }
    }
}
