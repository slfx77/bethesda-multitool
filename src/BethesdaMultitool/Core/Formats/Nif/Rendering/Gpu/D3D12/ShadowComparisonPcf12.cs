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
    internal static ShaderMacro[] ApplyRuntimeOptIn(
        string fileName, string profile, ShaderMacro[] macros)
    {
        return Apply(fileName, profile, macros, RuntimeEnvironmentValue);
    }

    /// <summary>Pure overload for policy tests; <paramref name="environmentValue" /> is untrusted.</summary>
    internal static ShaderMacro[] Apply(
        string fileName, string profile, ShaderMacro[] macros, string? environmentValue)
    {
        if (!string.Equals(environmentValue, "1", StringComparison.Ordinal) ||
            !profile.StartsWith("ps_", StringComparison.Ordinal) ||
            macros.Any(m => string.Equals(m.Name, ShaderMacroName, StringComparison.Ordinal)))
        {
            return macros;
        }

        // Adding an otherwise-unused macro still creates a distinct bytecode-pack/cache key. Keep
        // the experiment on permutations that actually include shadow_sampling.hlsli; otherwise an
        // opt-in run would fall out of the validated pack and invoke FXC for unrelated pixel shaders.
        if (!IsShadowReceiverPermutation(fileName, macros))
        {
            return macros;
        }

        return [.. macros, new ShaderMacro(ShaderMacroName, "1")];
    }

    private static bool IsShadowReceiverPermutation(string fileName, ShaderMacro[] macros)
    {
        if (!ShadowReceiverPixelShaders.Contains(fileName))
        {
            return false;
        }

        // water_fo4.frag.hlsl includes the shared shadow implementation only inside its
        // architectural branch. The classic FO4 and FO76-optics permutations are not receivers.
        return !fileName.Equals("water_fo4.frag.hlsl", StringComparison.OrdinalIgnoreCase) ||
               macros.Any(static macro =>
                   string.Equals(macro.Name, "FO4_WATER_ARCHITECTURAL", StringComparison.Ordinal) &&
                   string.Equals(macro.Definition, "1", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Emits proof of the bytecode actually returned to a production shadow-receiver caller.
    ///     This is deliberately invoked only after compilation or cache retrieval succeeds.
    /// </summary>
    /// <param name="fileName">Embedded shader name.</param>
    /// <param name="entryPoint">Compiled HLSL entry point.</param>
    /// <param name="profile">Native compiler target profile.</param>
    /// <param name="effectiveMacros">Definitions actually used by the selected permutation.</param>
    /// <param name="cacheKey">Exact runtime permutation identity.</param>
    /// <param name="bytecode">Borrowed read-only DXBC hashed without copying or retaining the span.</param>
    /// <param name="cacheHit">Whether the returned bytecode was already cached.</param>
    internal static void TraceSuccessfulShader(
        string fileName,
        string entryPoint,
        string profile,
        ShaderMacro[] effectiveMacros,
        string cacheKey,
        ReadOnlySpan<byte> bytecode,
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
    /// <param name="sessionId">Trace session identity used to deduplicate shader proofs.</param>
    /// <param name="fileName">Embedded shader name.</param>
    /// <param name="entryPoint">Compiled HLSL entry point.</param>
    /// <param name="profile">Native compiler target profile.</param>
    /// <param name="effectiveMacros">Definitions actually used by the selected permutation.</param>
    /// <param name="cacheKey">Exact runtime permutation identity.</param>
    /// <param name="bytecode">Borrowed read-only DXBC hashed without copying or retaining the span.</param>
    /// <param name="cacheHit">Whether the returned bytecode was already cached.</param>
    /// <param name="fields">New trace fields, or null for an inapplicable or previously traced permutation.</param>
    /// <returns>True when a new shadow-receiver proof was constructed.</returns>
    internal static bool TryBuildTraceProof(
        string sessionId,
        string fileName,
        string entryPoint,
        string profile,
        ShaderMacro[] effectiveMacros,
        string cacheKey,
        ReadOnlySpan<byte> bytecode,
        bool cacheHit,
        out IReadOnlyDictionary<string, object?>? fields)
    {
        fields = null;
        if (!profile.StartsWith("ps_", StringComparison.Ordinal) ||
            !IsShadowReceiverPermutation(fileName, effectiveMacros))
        {
            return false;
        }

        var proofKey = $"{sessionId}\0{cacheKey}";
        if (!TracedShaderKeys.TryAdd(proofKey, 0))
        {
            return false;
        }

        var comparisonMacro = effectiveMacros.FirstOrDefault(macro =>
            string.Equals(macro.Name, ShaderMacroName, StringComparison.Ordinal));
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
