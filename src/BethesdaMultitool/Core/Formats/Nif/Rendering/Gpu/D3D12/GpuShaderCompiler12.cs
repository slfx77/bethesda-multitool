using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
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
    private static readonly Logger Log = Logger.Instance;

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

    private static readonly Lazy<FrozenDictionary<string, string>> Index = new(BuildIndex);

    private static readonly ConcurrentDictionary<string, byte[]> BytecodeCache = new(StringComparer.Ordinal);

    private static long _compileCount;
    private static long _cacheHitCount;
    private static double _totalCompileMilliseconds;

    /// <summary>Shader file name → manifest resource name. Exact, case-insensitive on the file name.</summary>
    internal static FrozenDictionary<string, string> ResourceIndex => Index.Value;

    internal static long CompileCount => Interlocked.Read(ref _compileCount);

    internal static long CacheHitCount => Interlocked.Read(ref _cacheHitCount);

    /// <summary>
    ///     Compiles <paramref name="fileName" /> (e.g. <c>reference.frag.hlsl</c>), returning DXBC
    ///     bytecode. Repeat requests for the same (file, entry, profile, macros) are served from a
    ///     process-lifetime cache.
    /// </summary>
    internal static byte[] Compile(
        string fileName, string entryPoint, string profile, params ShaderMacro[] macros)
    {
        var key = BuildCacheKey(fileName, entryPoint, profile, macros);
        if (BytecodeCache.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref _cacheHitCount);
            return cached;
        }

        var bytecode = CompileSource(ReadSource(fileName), fileName, entryPoint, profile, macros);
        // GetOrAdd rather than indexer assignment: a concurrent duplicate compile is wasteful but
        // harmless, and callers must all observe the same array instance.
        return BytecodeCache.GetOrAdd(key, bytecode);
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
        var result = Compiler.Compile(
            source,
            macros,
            EmbeddedShaderInclude.Instance,
            entryPoint,
            sourceName,
            profile,
            EnableUnboundedDescriptorTables,
            EffectFlags.None,
            out Blob? bytecode,
            out Blob? errors);

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
            lock (Index) { _totalCompileMilliseconds += elapsed; }
            // Compile cost was previously never measured anywhere, so nobody could tell that
            // water.frag.hlsl was being compiled nine times per startup.
            Log.Debug(
                "GpuShaderCompiler12: {0} [{1}/{2}{3}] {4:0.0}ms (compiles={5} cacheHits={6} total={7:0}ms)",
                sourceName, entryPoint, profile, DescribeMacros(macros), elapsed,
                CompileCount, CacheHitCount, _totalCompileMilliseconds);
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

    private static string BuildCacheKey(
        string fileName, string entryPoint, string profile, ShaderMacro[] macros) =>
        $"{fileName}|{entryPoint}|{profile}|{DescribeMacros(macros)}";

    /// <summary>Order-independent macro description, so equivalent permutations share a cache slot.</summary>
    private static string DescribeMacros(ShaderMacro[]? macros)
    {
        if (macros is null || macros.Length == 0) return string.Empty;
        return " " + string.Join(
            ",",
            macros.Select(m => $"{m.Name}={m.Definition}").Order(StringComparer.Ordinal));
    }
}
