using System.Globalization;
using System.Numerics;
using System.Text;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Resources;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Persistent on-disk cache of decoded reference meshes. Enabled by DEFAULT under the OS temp
///     directory: the cold first run writes decoded meshes, every warm run after skips
///     the entire parse→convert→extract→build chain (the bulk of cold-load CPU cost). Disable with
///     <c>FALLOUT_VIEWER_PERSISTENT_MESH_CACHE=0</c>.
///     <para>
///         Container handling (header, key echo, negatives, atomic writes, prune, stats) lives in
///         <see cref="DiskBlobCache" />; this type owns only the payload serialization and the
///         metadata-based key. The payload format is versioned by <see cref="DecoderVersion" /> — any
///         change to the serialized bytes (e.g. the appended Havok collision soup) must bump it so
///         stale warm caches are invalidated rather than misread.
///     </para>
/// </summary>
internal sealed class ReferenceDecodedMeshDiskCache12 : DiskBlobCache
{
    internal const int CacheFormatVersion = 1;
    // Bumped 1→2: the decoded float output changed when HalfToFloat moved from a Math.Pow
    // approximation to the exact hardware (BitConverter.UInt16BitsToHalf) conversion — the low bits
    // differ, so any cache written by the old decoder must be invalidated. Bump this whenever the
    // decode output bytes can change.
    // Bumped 2→3: placed-reference bake now discards the scene-root node's own authored transform
    // (treatRootsAsIdentity) so non-identity root rotations (e.g. McMarranWalls wallReg 90°,
    // monorail curves 15°) are no longer baked into the vertices — the decoded positions change.
    // Bumped 3→4: NiBillboardNode subtrees now bake with the node's rotation dropped (translation
    // kept) and the new IsBillboard flag rides the payload, so old caches lack the field and would
    // bake the smoke glow with the wrong orientation.
    // Bumped 4→5: the payload now carries the NIF's decoded Havok (bhk*) collision soup (used by
    // walk-mode ground/ceiling sampling). Old caches lack those trailing fields, so a warm read would
    // both deserialize wrong AND silently lose collision — invalidate them.
    // Bumped 5→6: the per-submesh payload now carries NiMaterialProperty specular (color + glossiness +
    // enable gate) for the GPU specular term (1A). Old caches lack those trailing fields.
    // Bumped 6→7: the per-submesh payload now carries IsLeafBillboard, the SpeedTree leaf-billboard
    // shader route bit. Old caches lack the trailing flag and would silently route as static geometry.
    // Bumped 7→8: the legacy-block measure walk no longer drops large arrays (it now bounds array length
    // by bytes-remaining instead of a magic 100000 cap), so meshes with big tangent/vertex blobs (e.g.
    // Oblivion ICAUTower01 / ICPalaceTowerBase01) extract ALL their shapes instead of desyncing mid-file.
    // The decoded submesh set changes (ICAUTower01: 10→20), so old v7 entries are stale.
    // Bumped 8→9: NIF 10.2.0.0 geometry now extracts (NiGeometryData reads keyed on the NIF version) — meshes
    // like ICPalaceTower01 that previously decoded to nothing now produce geometry, so any v8 entry is stale.
    // Bumped 9→10: the per-submesh payload now carries DepthWritingBlend (effects-folder foliage that keeps
    // alpha blend but writes depth, e.g. NVSeaPlant02). Old caches lack the trailing flag.
    // Bumped 10→11: the alpha classifier no longer demotes UNLIT decals (BSShaderNoLightingProperty) NOR flat
    // planar decals to opaque on ZBuffer_Write — neither has a closed interior to leak, so they keep their
    // authored blend instead of rendering as opaque blocks: white/black ground-blend rings (SuperMutantBedding01,
    // SewerLidExit01, HoldingTankTopOnly), the opaque green NV_BarrelPile03 radioactive disc, opaque neon signage,
    // and lit surface decals like Vault87Blood10 (which fell to OPAQUE because the cutout fallback needs a texture
    // that isn't decoded yet at decode time). The baked AlphaRenderMode changes, so old v10 entries are stale.
    // Bumped 11→12: the geometry extractor now drops shapes with the NiAVObject Hidden flag (Flags bit 0 =
    // APP_CULLED) that the engine culls — e.g. NV_FencePickCleanGate's hidden C_gatepost proxy posts. The
    // decoded submesh SET changes, so old v11 entries bake the stray hidden geometry.
    // Bumped 12→13: the alpha classifier is now engine-accurate (decompiled BSShader::SetupGeometry*): the
    // BSShaderFlags2 ZBuffer_Write "demote alpha-blend to opaque" heuristic (+ the unlit/flat-decal exemptions)
    // is gone — alpha-blend shapes keep their blend, and depth-write follows the alpha-TEST bit (blend+test ⇒
    // DepthWritingBlend). The baked AlphaRenderMode/DepthWritingBlend changes, so old v12 entries are stale.
    // Bumped 13→14: NiParticleSystem effects now bake to leaf-billboard quad clouds (NifParticleSystemExtractor)
    // and the emitter-volume meshes are dropped — the decoded submesh set changes, so old v13 entries lack the
    // particle clouds and still carry the suppressed emitter blobs.
    // Bumped 14→15: mesh emitters (e.g. NV whirlwind columns) now spawn over the emitter mesh's AABB instead of a
    // single point (NifParticleSystemExtractor.ResolveMeshEmitterBounds), so the baked particle positions change.
    // Bumped 15→16: particle bake now (a) honors NiPSysDragModifier (velocity damping) and transforms planar
    // gravity by its gravity-object (fountain jet arcs back down instead of flying to the sky), and (b) only
    // marks ADDITIVE (Dst=ONE) particles emissive — alpha-blended dust/smoke are shaded. Positions + the
    // emissive flag change, so old v15 entries are stale.
    // Bumped 16→17: particle density now follows the AUTHORED birth rate (NiPSysEmitterCtlr interpolator) instead
    // of filling to NiPSysData capacity — far fewer live particles for dust/smoke (SandDust "too opaque" fix), and
    // the volume-emitter declination axis defaults to +Z (fountain jet goes up, not sideways). Positions + counts
    // change, so old v16 entries are stale.
    // Bumped 17→18: NiPSysDragModifier is now engine-accurate (decompiled NiPSysDragModifier::Update) — anisotropic
    // (damps only the velocity component along the drag-object-transformed axis), range-gated, frame-scaled, and
    // no-op without a drag object (was a uniform -pct·v on the whole velocity). Baked positions change for any
    // system with a drag modifier, so old v17 entries are stale.
    // Bumped 18→19: NiPSysSpawnModifier now spawns child particles on death (decompiled SpawnParticles) — a dying
    // particle bursts MinToSpawn..MaxToSpawn chaos-scattered children (the fountain's splash spray), cascading up
    // to NumSpawnGenerations. Particle counts + positions change for any system with an active spawn modifier.
    // Bumped 19→20: FO4/FO76 .bgsm/.bgem materials now override the NIF's inline render state (alpha test
    // threshold/blend, two-sided, specular) and expand their texture slots to real .dds paths (diffuse AND
    // normal map), and BSTriShape decodes tangents/bitangents (enables the FO4 bump path). The serialized
    // alpha state, texture paths, and TBN payload all change, so old v19 entries are stale.
    // Bumped 20→21: (a) BSMeshLODTriShape renders only its first non-empty LOD slice (was the whole
    // buffer — full-detail + LOD copies z-fighting); (b) SLSF vertex-channel gating (Vertex_Colors /
    // Vertex_Alpha) neutralizes FO4 wind-weight vertex alpha that discarded tree trunks via the cutout
    // test; (c) absolute build-path materials now resolve (normalized), changing baked alpha/two-sided
    // state; (d) new per-submesh SpecularMapTexturePath field in the payload.
    // Bumped 21→22: FO4/FO76 grayscale-to-palette — new per-submesh GradientMapTexturePath +
    // GradientMapV payload fields (the shader replaces diffuse RGB with the palette lookup; without
    // the fields, warm v21 meshes keep rendering the lavender authoring base).
    // Bumped 22→23: BSMeshLODTriShape far-slice fallbacks (LOD0 empty) are now dropped when the model
    // has near-content siblings — they're distant imposters the engine never draws up close, and they
    // z-fight coplanar real geometry (workshop rubble's LOD2-only floor slab vs its _Foundation ref).
    // The decoded submesh SET changes, so v22 entries still carry the stray imposters.
    internal const int DecoderVersion = 23;

    private const int MaxSubmeshes = 16_384;
    private const int MaxVerticesPerSubmesh = 2_000_000;
    private const int MaxIndicesPerSubmesh = 6_000_000;
    private const int MaxCollisionVertices = 4_000_000;
    private const int MaxCollisionIndices = 12_000_000;
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
            cacheDirectory = ReferenceDiskCachePaths.ResolveDefaultCacheDirectory(
                "ReferenceDecodedMeshCache12",
                DecoderVersion);
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
        MeshArchiveLookupMetadata metadata,
        string? variantKey,
        out ReferenceDecodedMeshDiskCacheEntry12 entry)
    {
        if (!TryLoadCore(BuildKeyText(metadata, variantKey), ReadMesh, out var mesh, out var isNegative))
        {
            entry = default;
            return false;
        }

        entry = new ReferenceDecodedMeshDiskCacheEntry12(mesh, isNegative);
        return true;
    }

    internal void Store(
        MeshArchiveLookupMetadata metadata,
        string? variantKey,
        ReferenceDecodedMeshPayload12? payload) =>
        StoreCore(BuildKeyText(metadata, variantKey), payload, WriteMesh);

    internal string GetCachePath(MeshArchiveLookupMetadata metadata, string? variantKey = null) =>
        GetCachePath(BuildKeyText(metadata, variantKey));

    private static string BuildKeyText(MeshArchiveLookupMetadata metadata, string? variantKey)
    {
        var builder = new StringBuilder(512);
        Append("format", CacheFormatVersion);
        Append("decoder", DecoderVersion);
        Append("path", metadata.NormalizedPath);
        // Re-skin variant discriminator (AlternateTextureSet.VariantKey — a content hash of the
        // override + material-swap pairs). Appended only when present so default-variant keys are
        // byte-identical to the pre-variant-persistence format (no wholesale cache invalidation).
        if (!string.IsNullOrEmpty(variantKey))
        {
            Append("variant", variantKey);
        }
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

        // Havok collision soup (trailing, optional). Counts of 0 mean "no Havok collision" → null.
        var collisionPositions = mesh.CollisionPositions ?? [];
        var collisionTriangles = mesh.CollisionTriangles ?? [];
        ValidateRange(collisionPositions.Length, 0, MaxCollisionVertices, nameof(mesh.CollisionPositions));
        ValidateRange(collisionTriangles.Length, 0, MaxCollisionIndices, nameof(mesh.CollisionTriangles));
        writer.Write(collisionPositions.Length);
        foreach (var p in collisionPositions) WriteVector3(writer, p);
        writer.Write(collisionTriangles.Length);
        foreach (var t in collisionTriangles) writer.Write(t);
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

        var collisionVertexCount = ReadInt32(reader, 0, MaxCollisionVertices);
        Vector3[]? collisionPositions = null;
        if (collisionVertexCount > 0)
        {
            collisionPositions = new Vector3[collisionVertexCount];
            for (var i = 0; i < collisionVertexCount; i++) collisionPositions[i] = ReadVector3(reader);
        }

        var collisionIndexCount = ReadInt32(reader, 0, MaxCollisionIndices);
        int[]? collisionTriangles = null;
        if (collisionIndexCount > 0)
        {
            collisionTriangles = new int[collisionIndexCount];
            for (var i = 0; i < collisionIndexCount; i++) collisionTriangles[i] = reader.ReadInt32();
        }

        return new ReferenceDecodedMeshPayload12(submeshes, collisionPositions, collisionTriangles);
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
        writer.Write(submesh.IsBillboard);
        WriteVector3(writer, submesh.SpecularColor);
        writer.Write(submesh.Glossiness);
        writer.Write(submesh.SpecularEnabled);
        writer.Write(submesh.IsLeafBillboard);
        writer.Write(submesh.DepthWritingBlend);
        WriteNullableString(writer, submesh.SpecularMapTexturePath, MaxStringBytes);
        WriteNullableString(writer, submesh.GradientMapTexturePath, MaxStringBytes);
        writer.Write(submesh.GradientMapV);
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
            ReadVector3(reader),
            reader.ReadBoolean(),
            ReadVector3(reader),
            reader.ReadSingle(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            ReadNullableString(reader, MaxStringBytes),
            ReadNullableString(reader, MaxStringBytes),
            reader.ReadSingle());
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

/// <summary>A disk-cache entry for a decoded reference mesh: the payload, or a negative (known-empty) marker.</summary>
internal readonly record struct ReferenceDecodedMeshDiskCacheEntry12(
    ReferenceDecodedMeshPayload12? Mesh,
    bool IsNegative);

/// <summary>A decoded reference mesh ready to cache/upload: its submeshes plus optional collision geometry.</summary>
internal sealed record ReferenceDecodedMeshPayload12(
    IReadOnlyList<ReferenceDecodedSubmeshPayload12> Submeshes,
    Vector3[]? CollisionPositions = null,
    int[]? CollisionTriangles = null);

/// <summary>One decoded submesh: its vertices/indices, texture paths, and resolved alpha/specular/billboard render state.</summary>
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
    Vector3 LocalBoundsCenter,
    bool IsBillboard,
    Vector3 SpecularColor = default,
    float Glossiness = 0f,
    bool SpecularEnabled = false,
    bool IsLeafBillboard = false,
    bool DepthWritingBlend = false,
    string? SpecularMapTexturePath = null,
    string? GradientMapTexturePath = null,
    float GradientMapV = 0f);
