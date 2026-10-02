using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Resolves Bethesda shader sources and permutations, caches DXBC, and records compilation
///     diagnostics. Shared owns native compilation and its include/blob lifetimes. This adapter
///     remains available to the portable test build; native compilation requires Windows.
/// </summary>
internal static class GpuShaderCompiler12
{
    /// <summary>Logical-name prefix stamped onto every shader by the csproj EmbeddedResource item.</summary>
    private const string ResourcePrefix = "BethesdaMultitool.Shaders.";

    private static readonly Logger Log = Logger.Instance;

    private static readonly Lazy<FrozenDictionary<string, string>> Index = new(BuildIndex);

    private static readonly EmbeddedShaderSourceProvider Sources = new();

    private static readonly Lazy<GpuShaderBytecodePack12?> ShippedBytecodePack = new(
        LoadShippedBytecodePack,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<string, ReadOnlyMemory<byte>> BytecodeCache = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, byte> MissingShippedKeys = new(StringComparer.Ordinal);

    private static long _compileCount;
    private static long _cacheHitCount;
    private static long _precompiledHitCount;
    private static double _totalCompileMilliseconds;

    /// <summary>
    ///     Shared compiler settings, including source encoding, that participate in the shipped-pack
    ///     fingerprint. The same unbounded-descriptor permission is passed to compilation.
    /// </summary>
    internal static string BytecodeCompilerContract { get; } = ShaderCompiler.GetCompilerContract(
        ShaderCompiler.AllowUnboundedDescriptorTables);

    /// <summary>Shader file name → manifest resource name. Exact, case-insensitive on the file name.</summary>
    internal static FrozenDictionary<string, string> ResourceIndex => Index.Value;

    /// <summary>Gets the number of successful source compilations, including concurrent cache misses.</summary>
    internal static long CompileCount => Interlocked.Read(ref _compileCount);

    /// <summary>Gets the number of requests satisfied by an existing process-lifetime cache entry.</summary>
    internal static long CacheHitCount => Interlocked.Read(ref _cacheHitCount);

    /// <summary>Gets the number of shipped-pack entries first installed in the process-lifetime cache.</summary>
    internal static long PrecompiledHitCount => Interlocked.Read(ref _precompiledHitCount);

    /// <summary>
    ///     Compiles <paramref name="fileName" /> (e.g. <c>reference.frag.hlsl</c>), returning DXBC
    ///     bytecode. Repeat requests for the same (file, entry, profile, macros) are served from a
    ///     process-lifetime cache.
    /// </summary>
    /// <param name="fileName">Exact embedded shader file name, resolved case-insensitively.</param>
    /// <param name="entryPoint">HLSL entry point for the requested permutation.</param>
    /// <param name="profile">Native compiler target profile.</param>
    /// <param name="macros">Permutation definitions, extended by the active shadow-comparison policy.</param>
    /// <returns>Read-only DXBC retaining the same backing storage for every cached permutation.</returns>
    /// <exception cref="FileNotFoundException">The embedded shader cannot be resolved.</exception>
    /// <exception cref="InvalidOperationException">Source compilation fails after pack fallback.</exception>
    internal static ReadOnlyMemory<byte> Compile(
        string fileName, string entryPoint, string profile, params ShaderMacro[] macros)
    {
        var effectiveMacros = ShadowComparisonPcf12.ApplyRuntimeOptIn(fileName, profile, macros);
        var key = BuildCacheKey(fileName, entryPoint, profile, effectiveMacros);
        if (BytecodeCache.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref _cacheHitCount);
            ShadowComparisonPcf12.TraceSuccessfulShader(
                fileName, entryPoint, profile, effectiveMacros, key, cached.Span, true);
            return cached;
        }

        var shippedPack = ShippedBytecodePack.Value;
        if (shippedPack is not null && shippedPack.TryGetBytecode(key, out var precompiled))
        {
            // TryAdd makes the race accounting unambiguous. Read-only views retain the pack-owned
            // backing storage without cloning it, and this dictionary never removes a key.
            var added = BytecodeCache.TryAdd(key, precompiled);
            var selectedPrecompiled = added ? precompiled : BytecodeCache[key];
            if (added)
            {
                Interlocked.Increment(ref _precompiledHitCount);
                Log.Debug(
                    "GpuShaderCompiler12: shipped DXBC {0} [{1}/{2}{3}] " +
                    "(precompiled={4} cacheHits={5})",
                    fileName, entryPoint, profile, DescribeMacros(effectiveMacros),
                    PrecompiledHitCount, CacheHitCount);
            }
            else
            {
                Interlocked.Increment(ref _cacheHitCount);
            }

            ShadowComparisonPcf12.TraceSuccessfulShader(
                fileName, entryPoint, profile, effectiveMacros, key, selectedPrecompiled.Span,
                !added);
            return selectedPrecompiled;
        }

        if (shippedPack is not null && MissingShippedKeys.TryAdd(key, 0))
        {
            // A successfully validated pack has the exact ShaderPermutations key set. Reaching this
            // branch therefore identifies a production compile request missing from that inventory.
            Log.Warn(
                "GpuShaderCompiler12: shipped pack has no key '{0}'; compiling source. " +
                "Add the runtime permutation to ShaderPermutations.All.",
                key);
        }

        ReadOnlyMemory<byte> bytecode = CompileSource(
            ReadSource(fileName), fileName, entryPoint, profile, effectiveMacros);
        // GetOrAdd rather than indexer assignment: a concurrent duplicate compile is wasteful but
        // harmless, and callers must all observe the same backing memory.
        var selectedBytecode = BytecodeCache.GetOrAdd(key, bytecode);
        ShadowComparisonPcf12.TraceSuccessfulShader(
            fileName, entryPoint, profile, effectiveMacros, key, selectedBytecode.Span,
            !selectedBytecode.Equals(bytecode));
        return selectedBytecode;
    }

    /// <summary>
    ///     Compiles caller-prepared shader text with the production flags and embedded include
    ///     policy. The result is independently owned and is not added to the process cache.
    /// </summary>
    /// <param name="source">Complete HLSL source, including any caller-prepared variations.</param>
    /// <param name="sourceName">Source identity used in compiler diagnostics.</param>
    /// <param name="entryPoint">HLSL entry point.</param>
    /// <param name="profile">Native compiler target profile.</param>
    /// <param name="macros">Exact definitions passed to Shared without runtime policy changes.</param>
    /// <returns>Caller-owned DXBC with no outstanding native compiler resources.</returns>
    /// <exception cref="PlatformNotSupportedException">Native compilation is requested outside Windows.</exception>
    /// <exception cref="InvalidOperationException">Compilation fails; source, macro, and native diagnostics are retained.</exception>
    internal static byte[] CompileSource(
        string source, string sourceName, string entryPoint, string profile, params ShaderMacro[] macros)
    {
        var started = Stopwatch.GetTimestamp();
        byte[] bytecode;
        try
        {
            bytecode = ShaderCompiler.CompileSource(
                new ShaderSource(sourceName, source), entryPoint, profile, Sources, macros,
                ShaderCompiler.AllowUnboundedDescriptorTables);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                $"HLSL compile failed for {sourceName} [{entryPoint}/{profile}{DescribeMacros(macros)}]: {exception.Message}",
                exception);
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Interlocked.Increment(ref _compileCount);
        double totalCompileMilliseconds;
        lock (Index)
        {
            _totalCompileMilliseconds += elapsed;
            totalCompileMilliseconds = _totalCompileMilliseconds;
        }

        Log.Debug(
            "GpuShaderCompiler12: {0} [{1}/{2}{3}] {4:0.0}ms (compiles={5} cacheHits={6} total={7:0}ms)",
            sourceName, entryPoint, profile, DescribeMacros(macros), elapsed,
            CompileCount, CacheHitCount, totalCompileMilliseconds);
        return bytecode;
    }

    /// <summary>Reads an embedded shader's text by exact file name.</summary>
    /// <param name="fileName">File name resolved case-insensitively against the manifest index.</param>
    /// <returns>The complete embedded UTF-8 shader text.</returns>
    /// <exception cref="FileNotFoundException">No shader has the requested embedded file name.</exception>
    internal static string ReadSource(string fileName)
    {
        if (!Index.Value.TryGetValue(fileName, out var resourceName))
        {
            throw new FileNotFoundException(
                $"Embedded shader '{fileName}' not found. Known: {string.Join(", ", Index.Value.Keys.Order())}");
        }

        using var stream = typeof(GpuShaderCompiler12).Assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Indexes the assembly's flat shader names and rejects ambiguous manifest entries.</summary>
    /// <returns>An immutable case-insensitive file-name-to-resource-name lookup.</returns>
    /// <exception cref="InvalidOperationException">Two embedded resources have the same shader file name.</exception>
    private static FrozenDictionary<string, string> BuildIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in typeof(GpuShaderCompiler12).Assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            var fileName = resource[ResourcePrefix.Length..];
            // Reject duplicate logical names rather than silently selecting one resource.
            if (!map.TryAdd(fileName, resource))
            {
                throw new InvalidOperationException(
                    $"Duplicate embedded shader name '{fileName}' ('{map[fileName]}' vs '{resource}').");
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Loads and validates the optional physical DXBC pack unless source compilation is forced.</summary>
    /// <returns>The compatible pack, or null when absent, disabled, or rejected with a diagnostic.</returns>
    /// <exception cref="OutOfMemoryException">The pack cannot be loaded within available memory.</exception>
    private static GpuShaderBytecodePack12? LoadShippedBytecodePack()
    {
        if (EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ShaderSourceCompile))
        {
            Log.Info(
                "GpuShaderCompiler12: {0}=1; shipped DXBC is disabled and source compilation is forced.",
                EnvironmentVariables.Viewer.ShaderSourceCompile);
            return null;
        }

        var path = Path.Combine(AppContext.BaseDirectory, GpuShaderBytecodePack12.DefaultFileName);
        if (!File.Exists(path))
        {
            // Expected for tests-only/non-Windows output trees and explicit pack-disabled builds:
            // retaining embedded sources is the deliberate recovery fallback, not a startup error.
            Log.Debug(
                "GpuShaderCompiler12: shipped DXBC pack not found at '{0}'; source compilation is enabled.",
                path);
            return null;
        }

        try
        {
            var expectedKeys = GpuShaderBytecodePack12.CurrentPermutationKeys();
            var expectedFingerprint = GpuShaderBytecodePack12.ComputeCurrentFingerprint();
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            var pack = GpuShaderBytecodePack12.Read(stream, expectedFingerprint, expectedKeys);
            Log.Info(
                "GpuShaderCompiler12: loaded {0} shipped DXBC permutations from '{1}'.",
                pack.Count,
                path);
            return pack;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never let packaging damage turn into a blank renderer: the embedded HLSL compiler is
            // intentionally still shipped, and every rejected pack safely takes that path.
            Log.Warn(
                "GpuShaderCompiler12: rejected shipped DXBC pack '{0}' ({1}: {2}); " +
                "source compilation remains enabled.",
                path,
                ex.GetType().Name,
                ex.Message);
            return null;
        }
    }

    /// <summary>Builds the stable shader permutation identity used by both runtime caching and shipped packs.</summary>
    /// <param name="fileName">Embedded shader name.</param>
    /// <param name="entryPoint">HLSL entry point.</param>
    /// <param name="profile">Native compiler target profile.</param>
    /// <param name="macros">Permutation definitions, sorted for order-independent identity.</param>
    /// <returns>A stable ordinal cache key with the existing delimiter and macro conventions.</returns>
    internal static string BuildCacheKey(
        string fileName, string entryPoint, string profile, ShaderMacro[] macros)
    {
        return $"{fileName}|{entryPoint}|{profile}|{DescribeMacros(macros)}";
    }

    /// <summary>Order-independent macro description, so equivalent permutations share a cache slot.</summary>
    /// <param name="macros">Definitions to describe, or null for no definitions.</param>
    /// <returns>An empty string or an ordinally sorted comma-separated list with a leading space.</returns>
    private static string DescribeMacros(ShaderMacro[]? macros)
    {
        if (macros is null || macros.Length == 0) return string.Empty;
        return " " + string.Join(
            ",",
            macros.Select(m => $"{m.Name}={m.Definition}").Order(StringComparer.Ordinal));
    }
}
