using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FalloutXbox360Utils.CLI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

internal sealed class ReferenceDecodedMeshDiskCache12
{
    internal const int CacheFormatVersion = 1;
    // Bumped 1→2: the decoded float output changed when HalfToFloat moved from a Math.Pow
    // approximation to the exact hardware (BitConverter.UInt16BitsToHalf) conversion — the low bits
    // differ, so any cache written by the old decoder must be invalidated. Bump this whenever the
    // decode output bytes can change.
    internal const int DecoderVersion = 2;

    private const int MaxSubmeshes = 16_384;
    private const int MaxVerticesPerSubmesh = 2_000_000;
    private const int MaxIndicesPerSubmesh = 6_000_000;
    private const int MaxStringBytes = 8 * 1024;
    private const int MaxKeyBytes = 64 * 1024;
    private const string FileExtension = ".fdmc";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FNVMC12\0");

    // Soft on-disk size ceiling (default 4 GB, env FALLOUT_VIEWER_MESH_CACHE_MAX_MB). Enforced by a
    // best-effort background prune at construction: oldest files deleted until under 80% of the cap.
    // Required now that the cache is enabled by default — without it the dir would grow unbounded.
    private static readonly long MaxCacheBytes = ResolveMaxCacheBytes();

    private long _hits;
    private long _misses;
    private long _stores;

    internal ReferenceDecodedMeshDiskCache12(string cacheDirectory)
    {
        CacheDirectory = cacheDirectory;
    }

    internal string CacheDirectory { get; }

    internal long Hits => Interlocked.Read(ref _hits);
    internal long Misses => Interlocked.Read(ref _misses);
    internal long Stores => Interlocked.Read(ref _stores);

    internal static ReferenceDecodedMeshDiskCache12? CreateFromEnvironment()
    {
        // Enabled by DEFAULT — this is a shader-cache-style win: the cold first run writes decoded
        // meshes, every warm run after skips the entire parse→convert→extract→build chain (the bulk
        // of cold-load CPU cost). Disable with FALLOUT_VIEWER_PERSISTENT_MESH_CACHE=0/false/off.
        if (IsDisabled(Environment.GetEnvironmentVariable("FALLOUT_VIEWER_PERSISTENT_MESH_CACHE")))
        {
            return null;
        }

        var cacheDirectory = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_MESH_CACHE_DIR");
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.GetTempPath();
            }

            cacheDirectory = Path.Combine(
                root,
                "FalloutXbox360Utils",
                "ReferenceDecodedMeshCache12",
                $"v{DecoderVersion}");
        }

        var cache = new ReferenceDecodedMeshDiskCache12(cacheDirectory);
        cache.SchedulePrune();
        return cache;
    }

    private static long ResolveMaxCacheBytes()
    {
        const long defaultMb = 4096;
        var raw = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_MESH_CACHE_MAX_MB");
        var mb = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultMb;
        return mb * 1024L * 1024L;
    }

    /// <summary>Best-effort background size-cap enforcement: if the cache dir exceeds the cap, delete
    /// the oldest files (by last-write time) until it is under 80% of the cap. Runs off-thread so it
    /// never blocks renderer startup; failures are swallowed (cache pruning must never break rendering).</summary>
    internal void SchedulePrune() => Task.Run(Prune);

    private void Prune()
    {
        try
        {
            var dir = new DirectoryInfo(CacheDirectory);
            if (!dir.Exists)
            {
                return;
            }

            var files = dir.EnumerateFiles("*" + FileExtension, SearchOption.AllDirectories).ToList();
            var total = files.Sum(static f => f.Length);
            if (total <= MaxCacheBytes)
            {
                return;
            }

            var target = (long)(MaxCacheBytes * 0.8);
            foreach (var f in files.OrderBy(static f => f.LastWriteTimeUtc))
            {
                if (total <= target)
                {
                    break;
                }

                try
                {
                    var len = f.Length;
                    f.Delete();
                    total -= len;
                }
                catch
                {
                    // Best-effort; skip files we can't delete (in use, perms).
                }
            }

            Logger.Instance.Info(
                "ReferenceDecodedMeshDiskCache12: pruned to {0:N0} MB (cap {1:N0} MB).",
                total / (1024 * 1024), MaxCacheBytes / (1024 * 1024));
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("ReferenceDecodedMeshDiskCache12: prune failed: {0}", ex.Message);
        }
    }

    /// <summary>Logs cumulative hit/miss/store counts — call at teardown to confirm the cache is
    /// working (warm runs should show a high hit ratio).</summary>
    internal void LogStatistics()
    {
        var hits = Hits;
        var misses = Misses;
        var total = hits + misses;
        if (total == 0)
        {
            return;
        }

        Logger.Instance.Info(
            "ReferenceDecodedMeshDiskCache12: {0:N0} hits, {1:N0} misses ({2:P1} hit rate), {3:N0} stores.",
            hits, misses, hits / (double)total, Stores);
    }

    internal bool TryLoad(
        NpcMeshArchiveLookupMetadata metadata,
        out ReferenceDecodedMeshDiskCacheEntry12 entry)
    {
        entry = default;
        var keyText = BuildKeyText(metadata);
        var path = GetCachePath(keyText);
        if (!File.Exists(path))
        {
            Interlocked.Increment(ref _misses);
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8);

            var magic = reader.ReadBytes(Magic.Length);
            if (!magic.SequenceEqual(Magic))
            {
                throw new InvalidDataException("Decoded mesh cache magic mismatch.");
            }

            var formatVersion = reader.ReadInt32();
            var decoderVersion = reader.ReadInt32();
            if (formatVersion != CacheFormatVersion || decoderVersion != DecoderVersion)
            {
                throw new InvalidDataException("Decoded mesh cache version mismatch.");
            }

            var storedKey = ReadRequiredString(reader, MaxKeyBytes);
            if (!string.Equals(storedKey, keyText, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Decoded mesh cache key mismatch.");
            }

            var isNegative = reader.ReadBoolean();
            if (isNegative)
            {
                entry = new ReferenceDecodedMeshDiskCacheEntry12(null, IsNegative: true);
                Interlocked.Increment(ref _hits);
                return true;
            }

            var mesh = ReadMesh(reader);
            entry = new ReferenceDecodedMeshDiskCacheEntry12(mesh, IsNegative: false);
            Interlocked.Increment(ref _hits);
            return true;
        }
        catch
        {
            TryDelete(path);
            entry = default;
            Interlocked.Increment(ref _misses);
            return false;
        }
    }

    internal void Store(NpcMeshArchiveLookupMetadata metadata, ReferenceDecodedMeshPayload12? payload)
    {
        var keyText = BuildKeyText(metadata);
        var path = GetCachePath(keyText);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var tempPath = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            using (var stream = File.Create(tempPath))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(CacheFormatVersion);
                writer.Write(DecoderVersion);
                WriteString(writer, keyText, MaxKeyBytes);
                writer.Write(payload is null);
                if (payload is not null)
                {
                    WriteMesh(writer, payload);
                }
            }

            File.Move(tempPath, path, overwrite: true);
            Interlocked.Increment(ref _stores);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("ReferenceDecodedMeshDiskCache12: cache write failed: {0}", ex.Message);
            TryDelete(tempPath);
        }
    }

    internal string GetCachePath(NpcMeshArchiveLookupMetadata metadata) =>
        GetCachePath(BuildKeyText(metadata));

    private string GetCachePath(string keyText)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyText))).ToLowerInvariant();
        return Path.Combine(CacheDirectory, hash[..2], hash + FileExtension);
    }

    private static string BuildKeyText(NpcMeshArchiveLookupMetadata metadata)
    {
        var builder = new StringBuilder(512);
        Append("format", CacheFormatVersion);
        Append("decoder", DecoderVersion);
        Append("path", metadata.NormalizedPath);
        Append("found", metadata.Found ? "1" : "0");
        Append("archiveSet", metadata.ArchiveSetIdentity);
        Append("archive", metadata.ArchivePath ?? "");
        Append("archiveLength", FormatNullable(metadata.ArchiveLength));
        Append("archiveWriteUtcTicks", FormatNullable(metadata.ArchiveLastWriteUtcTicks));
        Append("fileNameHash", FormatNullable(metadata.FileNameHash));
        Append("fileRawSize", FormatNullable(metadata.FileRawSize));
        Append("fileSize", FormatNullable(metadata.FileSize));
        Append("fileOffset", FormatNullable(metadata.FileOffset));
        return builder.ToString();

        void Append(string name, object value)
        {
            builder.Append(name);
            builder.Append('=');
            builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            builder.Append('\n');
        }
    }

    private static string FormatNullable(long? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";

    private static string FormatNullable(uint? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";

    private static string FormatNullable(ulong? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";

    private static bool IsDisabled(string? value) =>
        string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);

    private static void WriteMesh(BinaryWriter writer, ReferenceDecodedMeshPayload12 mesh)
    {
        ValidateRange(mesh.Submeshes.Count, 0, MaxSubmeshes, nameof(mesh.Submeshes));
        writer.Write(mesh.Submeshes.Count);
        foreach (var submesh in mesh.Submeshes)
        {
            WriteSubmesh(writer, submesh);
        }
    }

    private static ReferenceDecodedMeshPayload12 ReadMesh(BinaryReader reader)
    {
        var submeshCount = ReadInt32(reader, 0, MaxSubmeshes);
        var submeshes = new List<ReferenceDecodedSubmeshPayload12>(submeshCount);
        for (var i = 0; i < submeshCount; i++)
        {
            submeshes.Add(ReadSubmesh(reader));
        }

        if (submeshes.Count == 0)
        {
            throw new InvalidDataException("Decoded mesh cache payload has no submeshes.");
        }

        return new ReferenceDecodedMeshPayload12(submeshes);
    }

    private static void WriteSubmesh(BinaryWriter writer, ReferenceDecodedSubmeshPayload12 submesh)
    {
        ValidateRange(submesh.Vertices.Length, 0, MaxVerticesPerSubmesh, nameof(submesh.Vertices));
        ValidateRange(submesh.Indices.Length, 0, MaxIndicesPerSubmesh, nameof(submesh.Indices));

        writer.Write(submesh.Vertices.Length);
        foreach (var vertex in submesh.Vertices)
        {
            WriteVector3(writer, vertex.Position);
            WriteVector3(writer, vertex.Normal);
            WriteVector2(writer, vertex.TexCoord);
            WriteVector4(writer, vertex.VertexColor);
            WriteVector3(writer, vertex.Tangent);
            WriteVector3(writer, vertex.Bitangent);
        }

        writer.Write(submesh.Indices.Length);
        foreach (var index in submesh.Indices)
        {
            writer.Write(index);
        }

        WriteNullableString(writer, submesh.DiffuseTexturePath, MaxStringBytes);
        WriteNullableString(writer, submesh.NormalMapTexturePath, MaxStringBytes);
        writer.Write(submesh.HasBump);
        writer.Write((int)submesh.AlphaRenderMode);
        writer.Write(submesh.AlphaBlend);
        writer.Write(submesh.AlphaTest);
        writer.Write(submesh.AlphaTestThreshold);
        writer.Write(submesh.AlphaTestFunction);
        writer.Write(submesh.SrcBlendMode);
        writer.Write(submesh.DstBlendMode);
        writer.Write(submesh.MaterialAlpha);
        writer.Write(submesh.DoubleSided);
        writer.Write(submesh.IsEmissive);
        WriteVector3(writer, submesh.LocalBoundsCenter);
    }

    private static ReferenceDecodedSubmeshPayload12 ReadSubmesh(BinaryReader reader)
    {
        var vertexCount = ReadInt32(reader, 0, MaxVerticesPerSubmesh);
        var vertices = new GpuMeshUploader.GpuVertex[vertexCount];
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i].Position = ReadVector3(reader);
            vertices[i].Normal = ReadVector3(reader);
            vertices[i].TexCoord = ReadVector2(reader);
            vertices[i].VertexColor = ReadVector4(reader);
            vertices[i].Tangent = ReadVector3(reader);
            vertices[i].Bitangent = ReadVector3(reader);
        }

        var indexCount = ReadInt32(reader, 0, MaxIndicesPerSubmesh);
        var indices = new ushort[indexCount];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = reader.ReadUInt16();
        }

        var diffuseTexturePath = ReadNullableString(reader, MaxStringBytes);
        var normalMapTexturePath = ReadNullableString(reader, MaxStringBytes);
        var hasBump = reader.ReadBoolean();
        var alphaRenderModeValue = reader.ReadInt32();
        if (!Enum.IsDefined(typeof(NifAlphaRenderMode), alphaRenderModeValue))
        {
            throw new InvalidDataException("Invalid alpha render mode in decoded mesh cache.");
        }

        return new ReferenceDecodedSubmeshPayload12(
            vertices,
            indices,
            diffuseTexturePath,
            normalMapTexturePath,
            hasBump,
            (NifAlphaRenderMode)alphaRenderModeValue,
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadSingle(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadSingle(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            ReadVector3(reader));
    }

    private static void WriteVector2(BinaryWriter writer, Vector2 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    private static Vector2 ReadVector2(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle());

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteVector4(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }

    private static Vector4 ReadVector4(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteNullableString(BinaryWriter writer, string? value, int maxBytes)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        WriteString(writer, value, maxBytes);
    }

    private static void WriteString(BinaryWriter writer, string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        ValidateRange(bytes.Length, 0, maxBytes, nameof(value));
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadRequiredString(BinaryReader reader, int maxBytes) =>
        ReadNullableString(reader, maxBytes) ??
        throw new InvalidDataException("Expected a non-null cache string.");

    private static string? ReadNullableString(BinaryReader reader, int maxBytes)
    {
        var length = ReadInt32(reader, -1, maxBytes);
        if (length < 0)
        {
            return null;
        }

        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new EndOfStreamException();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static int ReadInt32(BinaryReader reader, int min, int max)
    {
        var value = reader.ReadInt32();
        ValidateRange(value, min, max, nameof(value));
        return value;
    }

    private static void ValidateRange(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new InvalidDataException($"{name} is out of range.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cache cleanup is best-effort; a failed delete should not affect rendering.
        }
    }
}

internal readonly record struct ReferenceDecodedMeshDiskCacheEntry12(
    ReferenceDecodedMeshPayload12? Mesh,
    bool IsNegative);

internal sealed record ReferenceDecodedMeshPayload12(
    IReadOnlyList<ReferenceDecodedSubmeshPayload12> Submeshes);

internal sealed record ReferenceDecodedSubmeshPayload12(
    GpuMeshUploader.GpuVertex[] Vertices,
    ushort[] Indices,
    string? DiffuseTexturePath,
    string? NormalMapTexturePath,
    bool HasBump,
    NifAlphaRenderMode AlphaRenderMode,
    bool AlphaBlend,
    bool AlphaTest,
    float AlphaTestThreshold,
    byte AlphaTestFunction,
    byte SrcBlendMode,
    byte DstBlendMode,
    float MaterialAlpha,
    bool DoubleSided,
    bool IsEmissive,
    Vector3 LocalBoundsCenter);
