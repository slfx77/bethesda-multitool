using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Deterministic sidecar containing the DXBC for every entry in
///     <see cref="ShaderPermutations.All" />. Interactive Windows builds generate it with the same
///     compiler entry point used by the source fallback; a fresh renderer process can therefore
///     create its PSOs without invoking FXC once per shader.
/// </summary>
internal sealed class GpuShaderBytecodePack12
{
    internal const string DefaultFileName = "BethesdaMultitool.Shaders.dxbcpack";

    private const int FormatVersion = 1;
    private const int FingerprintLength = 32;
    private const int EntryDigestLength = 32;
    private const int MaxEntryCount = 4096;
    private const int MaxKeyBytes = 16 * 1024;
    private const int MaxBytecodeBytes = 64 * 1024 * 1024;
    private const long MaxPackBytes = 512L * 1024 * 1024;

    private static readonly byte[] Magic = "BMTDXBC1"u8.ToArray();
    private static readonly byte[] DxbcMagic = "DXBC"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private readonly FrozenDictionary<string, byte[]> _entries;

    private GpuShaderBytecodePack12(Dictionary<string, byte[]> entries)
    {
        _entries = entries.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal int Count => _entries.Count;

    internal bool TryGetBytecode(string key, out byte[] bytecode) =>
        _entries.TryGetValue(key, out bytecode!);

    /// <summary>
    ///     Computes the identity of the bytecode that belongs beside this assembly. It covers every
    ///     embedded HLSL/HLSLI source (so include edits count), the normalized permutation keys, the
    ///     pack format, and the exact compiler flags/API contract.
    /// </summary>
    internal static byte[] ComputeCurrentFingerprint()
    {
        var sources = GpuShaderCompiler12.ResourceIndex.Keys.Select(
            name => KeyValuePair.Create(name, GpuShaderCompiler12.ReadSource(name)));
        var permutationKeys = CurrentPermutationKeys();
        return ComputeFingerprint(sources, permutationKeys, GpuShaderCompiler12.BytecodeCompilerContract);
    }

    internal static string[] CurrentPermutationKeys() =>
    [
        .. ShaderPermutations.All
            .Select(permutation => GpuShaderCompiler12.BuildCacheKey(
                permutation.File,
                permutation.EntryPoint,
                permutation.Profile,
                permutation.Macros))
            .Order(StringComparer.Ordinal)
    ];

    /// <summary>Pure fingerprint overload used by compiler-free structural tests.</summary>
    internal static byte[] ComputeFingerprint(
        IEnumerable<KeyValuePair<string, string>> sources,
        IEnumerable<string> permutationKeys,
        string compilerContract)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(permutationKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerContract);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, $"BethesdaMultitool DXBC pack format {FormatVersion}");
        AppendString(hash, compilerContract);

        foreach (var source in sources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AppendString(hash, "source");
            AppendString(hash, source.Key);
            AppendString(hash, source.Value);
        }

        foreach (var key in permutationKeys.Order(StringComparer.Ordinal))
        {
            AppendString(hash, "permutation");
            AppendString(hash, key);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>Writes the deterministic binary representation without taking ownership of the stream.</summary>
    internal static void Write(
        Stream output,
        byte[] fingerprint,
        IReadOnlyDictionary<string, byte[]> entries)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(entries);
        if (fingerprint.Length != FingerprintLength)
        {
            throw new ArgumentException(
                $"A shader-pack fingerprint must be {FingerprintLength} bytes.", nameof(fingerprint));
        }

        if (entries.Count > MaxEntryCount)
        {
            throw new InvalidDataException(
                $"Shader pack has {entries.Count} entries; limit is {MaxEntryCount}.");
        }

        using var writer = new BinaryWriter(output, Utf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(fingerprint);
        writer.Write(entries.Count);

        long payloadBytes = 0;
        foreach (var (key, bytecode) in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(bytecode);

            var keyBytes = Utf8.GetBytes(key);
            ValidateLength(keyBytes.Length, MaxKeyBytes, "key");
            ValidateLength(bytecode.Length, MaxBytecodeBytes, "bytecode");
            ValidateDxbc(bytecode, key);
            payloadBytes = checked(payloadBytes + keyBytes.Length + bytecode.Length);
            if (payloadBytes > MaxPackBytes)
            {
                throw new InvalidDataException(
                    $"Shader-pack payload exceeds the {MaxPackBytes} byte safety limit.");
            }

            writer.Write(keyBytes.Length);
            writer.Write(keyBytes);
            writer.Write(bytecode.Length);
            writer.Write(bytecode);
            writer.Write(ComputeEntryDigest(keyBytes, bytecode));
        }

        writer.Flush();
    }

    /// <summary>
    ///     Publishes through the repository's same-directory atomic writer, so a cancelled build can
    ///     never replace a valid shipped pack with a partial one.
    /// </summary>
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
                stream.Flush(flushToDisk: true);
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    ///     Reads a pack only when its fingerprint and exact key set match the current assembly.
    ///     Corrupt, stale, incomplete, or unexpectedly extended packs fail closed to source compile.
    /// </summary>
    internal static GpuShaderBytecodePack12 Read(
        Stream input,
        byte[] expectedFingerprint,
        IEnumerable<string> expectedKeys)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        ArgumentNullException.ThrowIfNull(expectedKeys);
        if (expectedFingerprint.Length != FingerprintLength)
        {
            throw new ArgumentException(
                $"A shader-pack fingerprint must be {FingerprintLength} bytes.",
                nameof(expectedFingerprint));
        }

        var expectedKeySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in expectedKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException(
                    "Expected shader keys cannot be null, empty, or whitespace.",
                    nameof(expectedKeys));
            }

            if (!expectedKeySet.Add(key))
            {
                throw new ArgumentException(
                    $"Expected shader key '{key}' appears more than once.",
                    nameof(expectedKeys));
            }
        }

        if (expectedKeySet.Count > MaxEntryCount)
        {
            throw new ArgumentException(
                $"Expected shader key count exceeds the {MaxEntryCount} entry safety limit.",
                nameof(expectedKeys));
        }

        try
        {
            return ReadCore(input, expectedFingerprint, expectedKeySet);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Shader bytecode pack is truncated.", ex);
        }
    }

    private static GpuShaderBytecodePack12 ReadCore(
        Stream input,
        byte[] expectedFingerprint,
        IReadOnlySet<string> expectedKeys)
    {
        if (input.CanSeek && input.Length - input.Position > MaxPackBytes)
        {
            throw new InvalidDataException(
                $"Shader pack exceeds the {MaxPackBytes} byte safety limit.");
        }

        using var reader = new BinaryReader(input, Utf8, leaveOpen: true);
        var magic = ReadExact(reader, Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Shader bytecode pack has an invalid magic value.");
        }

        var version = reader.ReadInt32();
        if (version != FormatVersion)
        {
            throw new InvalidDataException(
                $"Shader bytecode pack format {version} is not supported (expected {FormatVersion}).");
        }

        var fingerprint = ReadExact(reader, FingerprintLength);
        if (!CryptographicOperations.FixedTimeEquals(fingerprint, expectedFingerprint))
        {
            throw new InvalidDataException(
                "Shader bytecode pack fingerprint does not match this assembly's sources and inventory.");
        }

        var entryCount = reader.ReadInt32();
        if (entryCount < 0 || entryCount > MaxEntryCount)
        {
            throw new InvalidDataException($"Shader bytecode pack entry count {entryCount} is invalid.");
        }

        if (entryCount != expectedKeys.Count)
        {
            throw new InvalidDataException(
                $"Shader bytecode pack has {entryCount} entries; the current inventory requires {expectedKeys.Count}.");
        }

        var entries = new Dictionary<string, byte[]>(entryCount, StringComparer.Ordinal);
        string? previousKey = null;
        long payloadBytes = 0;
        for (var index = 0; index < entryCount; index++)
        {
            var keyLength = ReadLength(reader, MaxKeyBytes, "key");
            var keyBytes = ReadExact(reader, keyLength);
            string key;
            try
            {
                key = Utf8.GetString(keyBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Shader bytecode pack contains an invalid UTF-8 key.", ex);
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidDataException("Shader bytecode pack contains an empty key.");
            }

            if (previousKey is not null && StringComparer.Ordinal.Compare(previousKey, key) >= 0)
            {
                throw new InvalidDataException(
                    "Shader bytecode pack keys must be unique and ordinally sorted.");
            }

            var bytecodeLength = ReadLength(reader, MaxBytecodeBytes, "bytecode");
            payloadBytes = checked(payloadBytes + keyLength + bytecodeLength);
            if (payloadBytes > MaxPackBytes)
            {
                throw new InvalidDataException(
                    $"Shader-pack payload exceeds the {MaxPackBytes} byte safety limit.");
            }

            var bytecode = ReadExact(reader, bytecodeLength);
            ValidateDxbc(bytecode, key);
            var storedDigest = ReadExact(reader, EntryDigestLength);
            var actualDigest = ComputeEntryDigest(keyBytes, bytecode);
            if (!CryptographicOperations.FixedTimeEquals(storedDigest, actualDigest))
            {
                throw new InvalidDataException($"Shader bytecode pack entry '{key}' failed its digest check.");
            }

            entries.Add(key, bytecode);
            previousKey = key;
        }

        if (reader.BaseStream.ReadByte() != -1)
        {
            throw new InvalidDataException("Shader bytecode pack contains trailing data.");
        }

        var missingKey = expectedKeys.FirstOrDefault(key => !entries.ContainsKey(key));
        if (missingKey is not null)
        {
            throw new InvalidDataException(
                $"Shader bytecode pack does not contain current inventory key '{missingKey}'.");
        }

        return new GpuShaderBytecodePack12(entries);
    }

    private static int ReadLength(BinaryReader reader, int maximum, string field)
    {
        var length = reader.ReadInt32();
        ValidateLength(length, maximum, field);
        return length;
    }

    private static void ValidateLength(int length, int maximum, string field)
    {
        if (length < 0 || length > maximum)
        {
            throw new InvalidDataException(
                $"Shader bytecode pack {field} length {length} is invalid (limit {maximum}).");
        }
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new EndOfStreamException();
        }

        return bytes;
    }

    private static void ValidateDxbc(ReadOnlySpan<byte> bytecode, string key)
    {
        if (bytecode.Length < DxbcMagic.Length || !bytecode[..DxbcMagic.Length].SequenceEqual(DxbcMagic))
        {
            throw new InvalidDataException($"Shader bytecode pack entry '{key}' is not a DXBC container.");
        }
    }

    private static byte[] ComputeEntryDigest(ReadOnlySpan<byte> key, ReadOnlySpan<byte> bytecode)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendBytes(hash, key);
        AppendBytes(hash, bytecode);
        return hash.GetHashAndReset();
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        AppendBytes(hash, Utf8.GetBytes(value));
    }

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
