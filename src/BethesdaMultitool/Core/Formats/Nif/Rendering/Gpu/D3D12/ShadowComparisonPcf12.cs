using System.Collections.Concurrent;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using Vortice.Direct3D;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Process-start shader opt-in for the comparison-sampler shadow-PCF experiment. The default
///     deliberately supplies no macro, preserving the established GatherRed shader bytecode.
/// </summary>
internal static class ShadowComparisonPcf12
{
    internal const string EnvironmentVariable = EnvironmentVariables.Viewer.ShadowComparisonPcf;
    internal const string ShaderMacroName = "SHADOW_COMPARISON_PCF";
    internal const string TraceEventName = "shadow-comparison-pcf-shader";

    private static readonly HashSet<string> ShadowReceiverPixelShaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "reference.frag.hlsl",
            "terrain_textured.frag.hlsl",
            "reference_grass_oblivion.frag.hlsl",
            "reference_grass_fnv.frag.hlsl",
            "water_fo4.frag.hlsl"
        };

    // One proof per real bytecode-cache identity in each configured profiling session. A shader
    // can be requested by several PSO factories, but repeating the same proof would only bloat the
    // JSONL and make an A/B gate accidentally count requests instead of effective permutations.
    private static readonly ConcurrentDictionary<string, byte> TracedShaderKeys =
        new(StringComparer.Ordinal);

    // Freeze the experiment choice for the process lifetime. PSOs compiled later in a long scene
    // must not silently mix branches if an in-process diagnostic mutates the environment.
    private static readonly string? RuntimeEnvironmentValue =
        Environment.GetEnvironmentVariable(EnvironmentVariable);

    /// <summary>
    ///     Adds the experiment macro only to pixel shaders and only for the exact value <c>1</c>.
    ///     An explicit caller macro wins, which keeps compiler tests able to force either branch.
    /// </summary>
    internal static ShaderMacro[] ApplyRuntimeOptIn(string profile, ShaderMacro[] macros) =>
        Apply(profile, macros, RuntimeEnvironmentValue);

    /// <summary>Pure overload for policy tests; <paramref name="environmentValue" /> is untrusted.</summary>
    internal static ShaderMacro[] Apply(
        string profile, ShaderMacro[] macros, string? environmentValue)
    {
        if (!string.Equals(environmentValue, "1", StringComparison.Ordinal) ||
            !profile.StartsWith("ps_", StringComparison.Ordinal) ||
            macros.Any(m => string.Equals(m.Name, ShaderMacroName, StringComparison.Ordinal)))
        {
            return macros;
        }

        return [.. macros, new ShaderMacro(ShaderMacroName, "1")];
    }

    /// <summary>
    ///     Emits proof of the bytecode actually returned to a production shadow-receiver caller.
    ///     This is deliberately invoked only after compilation or cache retrieval succeeds.
    /// </summary>
    internal static void TraceSuccessfulShader(
        string fileName,
        string entryPoint,
        string profile,
        ShaderMacro[] effectiveMacros,
        string cacheKey,
        byte[] bytecode,
        bool cacheHit)
    {
        if (!RendererProfilerTrace.IsEnabled ||
            !TryBuildTraceProof(
                RendererProfilerTrace.SessionId,
                fileName,
                entryPoint,
                profile,
                effectiveMacros,
                cacheKey,
                bytecode,
                cacheHit,
                out var fields))
        {
            return;
        }

        RendererProfilerTrace.Event(TraceEventName, fields);
    }

    /// <summary>
    ///     Builds and de-duplicates a proof without requiring a live trace writer. Kept internal so
    ///     policy, schema, and de-duplication can be tested without compiling a shader or creating a
    ///     D3D device.
    /// </summary>
    internal static bool TryBuildTraceProof(
        string sessionId,
        string fileName,
        string entryPoint,
        string profile,
        ShaderMacro[] effectiveMacros,
        string cacheKey,
        byte[] bytecode,
        bool cacheHit,
        out IReadOnlyDictionary<string, object?>? fields)
    {
        fields = null;
        if (!profile.StartsWith("ps_", StringComparison.Ordinal) ||
            !ShadowReceiverPixelShaders.Contains(fileName))
        {
            return false;
        }

        var proofKey = $"{sessionId}\0{cacheKey}";
        if (!TracedShaderKeys.TryAdd(proofKey, 0))
        {
            return false;
        }

        var comparisonMacro = effectiveMacros.FirstOrDefault(
            macro => string.Equals(macro.Name, ShaderMacroName, StringComparison.Ordinal));
        var macroDefinition = comparisonMacro.Name is null ? null : comparisonMacro.Definition;
        var normalizedMacros = string.Join(
            ",",
            effectiveMacros
                .Select(macro => $"{macro.Name}={macro.Definition}")
                .Order(StringComparer.Ordinal));

        fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["shaderFile"] = fileName,
            ["entryPoint"] = entryPoint,
            ["profile"] = profile,
            ["macros"] = normalizedMacros,
            ["shadowComparisonPcfEffective"] = string.Equals(
                macroDefinition, "1", StringComparison.Ordinal),
            ["shadowComparisonPcfMacro"] = macroDefinition,
            ["bytecodeSha256"] = Convert.ToHexString(SHA256.HashData(bytecode)),
            ["bytecodeBytes"] = bytecode.Length,
            ["cacheHit"] = cacheHit
        };
        return true;
    }
}
