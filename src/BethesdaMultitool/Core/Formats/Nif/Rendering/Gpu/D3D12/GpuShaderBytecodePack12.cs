using BethesdaMultitool.Core.Utils;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Applies Bethesda's shader inventory and atomic publication policy to the Shared DXBC pack.</summary>
/// <remarks>Shared owns binary validation, fingerprinting, serialization, and bytecode storage.
/// This adapter retains the application's sidecar name, embedded sources, permutation inventory,
/// and same-directory atomic writer.</remarks>
internal sealed class GpuShaderBytecodePack12
{
    /// <summary>The physical sidecar name used by interactive builds and published applications.</summary>
    internal const string DefaultFileName = "BethesdaMultitool.Shaders.dxbcpack";

    private readonly ShaderBytecodePack _pack;

    /// <summary>Retains a validated Shared pack without copying its bytecode.</summary>
    /// <param name="pack">Pack owning the independently read and validated bytecode.</param>
    private GpuShaderBytecodePack12(ShaderBytecodePack pack)
    {
        _pack = pack;
    }

    /// <summary>Gets the number of validated shader permutations.</summary>
    internal int Count => _pack.Count;

    /// <summary>Looks up one exact permutation without exposing or copying the pack's owned array.</summary>
    /// <param name="key">Case-sensitive shader permutation key.</param>
    /// <param name="bytecode">Read-only pack bytes, or empty memory when the key is absent.</param>
    /// <returns>True when the exact permutation exists.</returns>
    internal bool TryGetBytecode(string key, out ReadOnlyMemory<byte> bytecode)
    {
        return _pack.TryGetBytecode(key, out bytecode);
    }

    /// <summary>Computes the identity of this assembly's sources, permutation inventory, and compiler contract.</summary>
    /// <returns>A newly allocated SHA-256 fingerprint including all embedded HLSL and HLSLI text.</returns>
    internal static byte[] ComputeCurrentFingerprint()
    {
        var sources = GpuShaderCompiler12.ResourceIndex.Keys.Select(name =>
            KeyValuePair.Create(name, GpuShaderCompiler12.ReadSource(name)));
        var permutationKeys = CurrentPermutationKeys();
        return ComputeFingerprint(sources, permutationKeys, GpuShaderCompiler12.BytecodeCompilerContract);
    }

    /// <summary>Builds the exact authoritative shader key inventory using runtime cache-key rules.</summary>
    /// <returns>A newly allocated array of ordinally sorted permutation keys.</returns>
    internal static string[] CurrentPermutationKeys()
    {
        return
        [
            .. ShaderPermutations.All
                .Select(permutation => GpuShaderCompiler12.BuildCacheKey(
                    permutation.File,
                    permutation.EntryPoint,
                    permutation.Profile,
                    permutation.Macros))
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>Computes a deterministic fingerprint without requiring native shader compilation.</summary>
    /// <param name="sources">Source identities and complete text, including relevant includes.</param>
    /// <param name="permutationKeys">Exact shader permutation keys.</param>
    /// <param name="compilerContract">Compiler version, source encoding, API, and flags.</param>
    /// <returns>A newly allocated SHA-256 fingerprint using the Shared format-1 contract.</returns>
    internal static byte[] ComputeFingerprint(
        IEnumerable<KeyValuePair<string, string>> sources,
        IEnumerable<string> permutationKeys,
        string compilerContract)
    {
        return ShaderBytecodePack.ComputeFingerprint(sources, permutationKeys, compilerContract);
    }

    /// <summary>Writes the deterministic binary representation without taking ownership of the stream.</summary>
    /// <param name="output">Writable destination left open after serialization.</param>
    /// <param name="fingerprint">Exact 32-byte source and inventory fingerprint.</param>
    /// <param name="entries">Caller-owned DXBC arrays, borrowed without mutation or copying.</param>
    /// <exception cref="InvalidDataException">Bytecode is invalid or exceeds the Shared format limits.</exception>
    internal static void Write(
        Stream output,
        byte[] fingerprint,
        IReadOnlyDictionary<string, byte[]> entries)
    {
        ShaderBytecodePack.Write(output, fingerprint, entries);
    }

    /// <summary>Publishes through the same-directory atomic writer, preserving the previous pack on failure.</summary>
    /// <param name="targetPath">Physical sidecar path, normalized before creating the output directory.</param>
    /// <param name="fingerprint">Exact source and inventory fingerprint.</param>
    /// <param name="entries">Caller-owned compiled arrays kept stable until publication completes.</param>
    /// <param name="cancellationToken">Cancels preparation or publication before replacing the destination.</param>
    /// <returns>A task completing when the fully flushed pack has been published.</returns>
    /// <exception cref="ArgumentException">The target has no concrete output directory.</exception>
    /// <exception cref="InvalidDataException">An entry fails Shared pack validation.</exception>
    internal static Task WriteAtomicallyAsync(
        string targetPath,
        byte[] fingerprint,
        IReadOnlyDictionary<string, byte[]> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var fullPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Shader-pack output requires a concrete directory.", nameof(targetPath));
        }

        Directory.CreateDirectory(directory);
        return AtomicFileWriter.WriteAsync(
            fullPath,
            (temporaryPath, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.SequentialScan);
                Write(stream, fingerprint, entries);
                stream.Flush(true);
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>Reads a Shared pack only when its fingerprint and exact key set match the current assembly.</summary>
    /// <param name="input">Readable stream, left open after the independent pack storage is prepared.</param>
    /// <param name="expectedFingerprint">Exact 32-byte fingerprint for the current application.</param>
    /// <param name="expectedKeys">Complete expected permutation inventory without duplicates.</param>
    /// <returns>An adapter retaining the validated Shared-owned bytecode.</returns>
    /// <exception cref="InvalidDataException">The pack is stale, corrupt, truncated, oversized, or has a different inventory.</exception>
    internal static GpuShaderBytecodePack12 Read(
        Stream input,
        byte[] expectedFingerprint,
        IEnumerable<string> expectedKeys)
    {
        return new GpuShaderBytecodePack12(ShaderBytecodePack.Read(input, expectedFingerprint, expectedKeys));
    }
}
