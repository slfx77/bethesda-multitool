using System.Globalization;
using System.Numerics;
using System.Text;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Diagnostics;
using FalloutXbox360Utils.Core.Resources;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Persistent on-disk cache of decoded reference meshes. Enabled by DEFAULT — a
///     shader-cache-style win: the cold first run writes decoded meshes, every warm run after skips
///     the entire parse→convert→extract→build chain (the bulk of cold-load CPU cost). Disable with
///     <c>FALLOUT_VIEWER_PERSISTENT_MESH_CACHE=0</c>.
///     <para>
///         Container handling (header, key echo, negatives, atomic writes, prune, stats) lives in
///         <see cref="DiskBlobCache" />; this type owns only the payload serialization and the
///         metadata-based key. The on-disk format is byte-identical to the pre-extraction
///         implementation — existing warm caches stay valid.
///     </para>
/// </summary>
internal sealed class ReferenceDecodedMeshDiskCache12 : DiskBlobCache
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
    private const string FileExtension = ".fdmc";
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FNVMC12\0");

    // Soft on-disk size ceiling (default 4 GB, env FALLOUT_VIEWER_MESH_CACHE_MAX_MB). Enforced by a
    // best-effort background prune at construction: oldest files deleted until under 80% of the cap.
    // Required now that the cache is enabled by default — without it the dir would grow unbounded.
    private static readonly long MaxCacheBytes = ResolveMaxCacheBytes();

    internal ReferenceDecodedMeshDiskCache12(string cacheDirectory)
        : base(
            nameof(ReferenceDecodedMeshDiskCache12), cacheDirectory, MaxCacheBytes,
            Magic, CacheFormatVersion, DecoderVersion, FileExtension)
    {
    }

    internal static ReferenceDecodedMeshDiskCache12? CreateFromEnvironment()
    {
        if (IsDisabled(EnvironmentVariables.Get(EnvironmentVariables.Viewer.PersistentMeshCache)))
        {
            return null;
        }

        var cacheDirectory = EnvironmentVariables.Get(EnvironmentVariables.Viewer.MeshCacheDirectory);
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
        cache.RegisterWith(ResourceRegistry.Instance);
        cache.SchedulePrune();
        return cache;
    }

    private static long ResolveMaxCacheBytes()
    {
        const long defaultMb = 4096;
        var raw = EnvironmentVariables.Get(EnvironmentVariables.Viewer.MeshCacheMaxMegabytes);
        var mb = long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : defaultMb;
        return mb * 1024L * 1024L;
    }

    internal bool TryLoad(
        NpcMeshArchiveLookupMetadata metadata,
        out ReferenceDecodedMeshDiskCacheEntry12 entry)
    {
        if (!TryLoadCore(BuildKeyText(metadata), ReadMesh, out var mesh, out var isNegative))
        {
            entry = default;
            return false;
        }

        entry = new ReferenceDecodedMeshDiskCacheEntry12(mesh, isNegative);
        return true;
    }

    internal void Store(NpcMeshArchiveLookupMetadata metadata, ReferenceDecodedMeshPayload12? payload) =>
        StoreCore(BuildKeyText(metadata), payload, WriteMesh);

    internal string GetCachePath(NpcMeshArchiveLookupMetadata metadata) =>
        GetCachePath(BuildKeyText(metadata));

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
