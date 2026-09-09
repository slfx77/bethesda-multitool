using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     The single entry point for compiling an embedded HLSL shader. Replaces a dozen copy-pasted
///     private <c>CompileEmbeddedShader</c> helpers that had each drifted apart.
///     <para>
///         NOT guarded by <c>#if WINDOWS_GUI</c> on purpose: the <c>net10.0</c> test build must reach
///         it so tests and the runtime can never diverge on flags, lookup, or include handling again
///         (<c>GpuTonemapPass12</c> sets the same precedent; the Vortice.D3DCompiler package
///         reference is unconditional).
///     </para>
/// </summary>
internal static class GpuShaderCompiler12
{
    /// <summary>
    ///     <c>D3DCOMPILE_ENABLE_UNBOUNDED_DESCRIPTOR_TABLES</c>. Not present in Vortice's
    ///     <see cref="ShaderFlags" /> enum, hence the cast.
    ///     <para>
    ///         THE ONLY OCCURRENCE OF THIS CONSTANT IN THE REPO, and it is applied UNCONDITIONALLY.
    ///         It previously varied by SIX divergent rules that each tried to infer the need by
    ///         substring-scanning the top-level shader text — <c>Contains("textures[]")</c>,
    ///         <c>Contains("[] : register")</c>, an explicit <see cref="ShaderFlags.None" />, a short
    ///         overload passing no flags at all, a test-side regex, and one always-on test. Two
    ///         consequences made that untenable: in one shader the check was satisfied by a *comment*
    ///         rather than a declaration, and any future move of a <c>[] : register</c> declaration
    ///         into a shared header would have silently stopped every heuristic from firing, giving
    ///         FXC error X3596 — which the GUI turns into a warning and a blank viewport, not a crash.
    ///     </para>
    ///     <para>
    ///         Unconditional is correct because the flag only PERMITS unbounded descriptor ranges; it
    ///         is inert for a shader that declares none. <c>FnvTerrainNormalMapTests</c> already
    ///         proved this by compiling always-on for a long time.
    ///     </para>
    /// </summary>
    private const ShaderFlags EnableUnboundedDescriptorTables = (ShaderFlags)0x00100000;

    /// <summary>Logical-name prefix stamped onto every shader by the csproj EmbeddedResource item.</summary>
    private const string ResourcePrefix = "BethesdaMultitool.Shaders.";

    private static readonly Logger Log = Logger.Instance;

    private static readonly Lazy<FrozenDictionary<string, string>> Index = new(BuildIndex);

    private static readonly Lazy<GpuShaderBytecodePack12?> ShippedBytecodePack = new(
        LoadShippedBytecodePack,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly ConcurrentDictionary<string, byte[]> BytecodeCache = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, byte> MissingShippedKeys = new(StringComparer.Ordinal);

    private static long _compileCount;
    private static long _cacheHitCount;
    private static long _precompiledHitCount;
    private static double _totalCompileMilliseconds;

    /// <summary>
    ///     Compile settings that participate in the shipped-pack fingerprint. Keep the API revision
    ///     in this one decision site if compiler behavior changes; the actual enum values are derived
    ///     from the same constants passed to <c>Compiler.Compile</c> below.
    /// </summary>
    internal static string BytecodeCompilerContract { get; } = string.Create(
        CultureInfo.InvariantCulture,
        $"Vortice.D3DCompiler={typeof(Compiler).Assembly.GetName().Version};Compiler.Compile/v1;ShaderFlags={(int)EnableUnboundedDescriptorTables};EffectFlags={(int)EffectFlags.None}");

    /// <summary>Shader file name → manifest resource name. Exact, case-insensitive on the file name.</summary>
    internal static FrozenDictionary<string, string> ResourceIndex => Index.Value;

    internal static long CompileCount => Interlocked.Read(ref _compileCount);

    internal static long CacheHitCount => Interlocked.Read(ref _cacheHitCount);

    internal static long PrecompiledHitCount => Interlocked.Read(ref _precompiledHitCount);

    /// <summary>
    ///     Compiles <paramref name="fileName" /> (e.g. <c>reference.frag.hlsl</c>), returning DXBC
    ///     bytecode. Repeat requests for the same (file, entry, profile, macros) are served from a
    ///     process-lifetime cache.
    /// </summary>
    internal static byte[] Compile(
        string fileName, string entryPoint, string profile, params ShaderMacro[] macros)
    {
        var effectiveMacros = ShadowComparisonPcf12.ApplyRuntimeOptIn(fileName, profile, macros);
        var key = BuildCacheKey(fileName, entryPoint, profile, effectiveMacros);
        if (BytecodeCache.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref _cacheHitCount);
            ShadowComparisonPcf12.TraceSuccessfulShader(
                fileName, entryPoint, profile, effectiveMacros, key, cached, true);
            return cached;
        }

        var shippedPack = ShippedBytecodePack.Value;
        if (shippedPack is not null && shippedPack.TryGetBytecode(key, out var precompiled))
        {
            // TryAdd makes the race accounting unambiguous. Entries are immutable process-lifetime
            // arrays, and this dictionary never removes a key.
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
                fileName, entryPoint, profile, effectiveMacros, key, selectedPrecompiled,
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

        var bytecode = CompileSource(
            ReadSource(fileName), fileName, entryPoint, profile, effectiveMacros);
        // GetOrAdd rather than indexer assignment: a concurrent duplicate compile is wasteful but
        // harmless, and callers must all observe the same array instance.
        var selectedBytecode = BytecodeCache.GetOrAdd(key, bytecode);
        ShadowComparisonPcf12.TraceSuccessfulShader(
            fileName, entryPoint, profile, effectiveMacros, key, selectedBytecode,
            !ReferenceEquals(selectedBytecode, bytecode));
        return selectedBytecode;
    }

    /// <summary>
    ///     Compiles shader text that did not come from the manifest — used by tests that mutate a
    ///     source before compiling. Shares the flag rule and include handling with
    ///     <see cref="Compile" /> so a test can never validate a different configuration than ships;
    ///     deliberately NOT cached.
    /// </summary>
    internal static byte[] CompileSource(
        string source, string sourceName, string entryPoint, string profile, params ShaderMacro[] macros)
    {
        var started = Stopwatch.GetTimestamp();
        // Owned by this call and disposed with it — see EmbeddedShaderInclude for why a shared
        // instance cannot be used from more than one thread.
        using var include = new EmbeddedShaderInclude();
        var result = Compiler.Compile(
            source,
            macros,
            include,
            entryPoint,
            sourceName,
            profile,
            EnableUnboundedDescriptorTables,
            EffectFlags.None,
            out var bytecode,
            out var errors);

        if (result.Failure || bytecode is null)
        {
            var errorText = errors?.AsString() ?? "(no error blob)";
            errors?.Dispose();
            bytecode?.Dispose();
            throw new InvalidOperationException(
                $"HLSL compile failed for {sourceName} [{entryPoint}/{profile}{DescribeMacros(macros)}]: {errorText}");
        }

        errors?.Dispose();
        try
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Interlocked.Increment(ref _compileCount);
            double totalCompileMilliseconds;
            lock (Index)
            {
                _totalCompileMilliseconds += elapsed;
                totalCompileMilliseconds = _totalCompileMilliseconds;
            }

            // Compile cost was previously never measured anywhere, so nobody could tell that
            // water.frag.hlsl was being compiled nine times per startup.
            Log.Debug(
                "GpuShaderCompiler12: {0} [{1}/{2}{3}] {4:0.0}ms (compiles={5} cacheHits={6} total={7:0}ms)",
                sourceName, entryPoint, profile, DescribeMacros(macros), elapsed,
                CompileCount, CacheHitCount, totalCompileMilliseconds);
            return bytecode.AsBytes().ToArray();
        }
        finally
        {
            bytecode.Dispose();
        }
    }

    /// <summary>Reads an embedded shader's text by exact file name.</summary>
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

    private static FrozenDictionary<string, string> BuildIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in typeof(GpuShaderCompiler12).Assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            var fileName = resource[ResourcePrefix.Length..];
            // The csproj's flat LogicalName already makes a duplicate file name a build error; this
            // is the belt to that braces, and it fails at startup rather than picking one silently.
            if (!map.TryAdd(fileName, resource))
            {
                throw new InvalidOperationException(
                    $"Duplicate embedded shader name '{fileName}' ('{map[fileName]}' vs '{resource}').");
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

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

    internal static string BuildCacheKey(
        string fileName, string entryPoint, string profile, ShaderMacro[] macros)
    {
        return $"{fileName}|{entryPoint}|{profile}|{DescribeMacros(macros)}";
    }

    /// <summary>Order-independent macro description, so equivalent permutations share a cache slot.</summary>
    private static string DescribeMacros(ShaderMacro[]? macros)
    {
        if (macros is null || macros.Length == 0) return string.Empty;
        return " " + string.Join(
            ",",
            macros.Select(m => $"{m.Name}={m.Definition}").Order(StringComparer.Ordinal));
    }
}
