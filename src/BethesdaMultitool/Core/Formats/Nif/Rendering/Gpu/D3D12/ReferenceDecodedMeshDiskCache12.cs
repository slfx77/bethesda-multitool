using System.Globalization;
using System.Numerics;
using System.Text;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
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
    // Container layout version (magic/header/framing owned by DiskBlobCache); a mismatch
    // invalidates the file. Independent of DecoderVersion below, which versions the payload.
    internal const int CacheFormatVersion = 1;

    // DecoderVersion versions the decoded payload: ANY change to the decode output (geometry,
    // payload fields, classification, animation, collision) requires a bump so warm cache
    // entries written by the old decoder are discarded rather than misread. Most recent bumps:
    // Bumped 60→61: baked particle-cloud emissive state now follows the attached shader property.
    // Warm v60 entries can keep standard-alpha BSShaderNoLighting dust incorrectly scene-lit.
    // Bumped 61→62: the active FNV ID193 classifier now rejects shader type 29 and raw flags1
    // Skinned/SinglePass. Warm v61 entries can retain the earlier broader audit-only identity.
    // Bumped 62→63: persist each decoded submesh's stable source-shape block index. Warm v62
    // entries cannot identify the source shape for property-associated light observations.
    // v64: retain NiBillboardNode.BillboardMode. Skyrim's flame cards use ALWAYS_FACE_CENTER;
    // v63 flattened every billboard to rotate-about-up and rendered them as a horizontal glow.
    // v65: honor Skyrim BSLightingShaderProperty SLSF2 Double_Sided. Warm v64 grass cards keep
    // their incorrect backface-culling state and appear partial or as isolated floating triangles.
    // v66: NiTexturingProperty ≤ 10.0.1.2 leading-Flags fix (Oblivion GroundCover* grass diffuse) +
    // TES4-era Y-up billboard erect + texture-aware bone-attached-proxy drop. Warm v65 entries
    // cache the null-diffuse decodes (white grass) and the pre-fix drop/billboard states.
    // (Full bump history for this constant lives in git blame.)
    internal const int DecoderVersion = 66;

    private const int MaxSubmeshes = 16_384;
    private const int MaxVerticesPerSubmesh = 2_000_000;
    private const int MaxIndicesPerSubmesh = 6_000_000;
    private const int MaxCollisionVertices = 4_000_000;
    private const int MaxCollisionIndices = 12_000_000;
    private const int MaxStringBytes = 8 * 1024;
    private const int MaxAnimBones = 512;
    private const int MaxKeysPerChannel = 65_536;
    private const int MaxTextKeys = 4_096;
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

        // Keyframe animation rig (trailing, optional, v32+).
        writer.Write(mesh.Animation is not null);
        if (mesh.Animation is { } anim)
        {
            WriteAnimation(writer, anim);
        }

        // Source provenance (v46+), distinct from per-submesh IsParticleCloud.
        writer.Write(mesh.ContainsParticleSource);
    }

    private static void WriteAnimation(BinaryWriter writer, NifMeshAnimation anim)
    {
        ValidateRange(anim.Bones.Length, 1, MaxAnimBones, nameof(anim.Bones));
        ValidateRange(anim.TextKeys.Length, 0, MaxTextKeys, nameof(anim.TextKeys));

        writer.Write(anim.Bones.Length);
        for (var i = 0; i < anim.Bones.Length; i++)
        {
            var bone = anim.Bones[i];
            WriteNullableString(writer, bone.Name, MaxStringBytes);
            writer.Write(bone.ParentIndex);
            WriteVector3(writer, bone.RestTranslation);
            WriteQuaternion(writer, bone.RestRotation);
            writer.Write(bone.RestScale);

            var track = anim.Tracks[i];
            writer.Write(track is not null);
            if (track is null)
            {
                continue;
            }

            ValidateRange(track.RotationKeys.Length, 0, MaxKeysPerChannel, nameof(track.RotationKeys));
            ValidateRange(track.TranslationKeys.Length, 0, MaxKeysPerChannel, nameof(track.TranslationKeys));
            ValidateRange(track.ScaleKeys.Length, 0, MaxKeysPerChannel, nameof(track.ScaleKeys));

            writer.Write(track.Frequency);
            writer.Write(track.Phase);
            writer.Write((byte)track.RotationInterpolation);
            writer.Write(track.RotationKeys.Length);
            foreach (var key in track.RotationKeys)
            {
                writer.Write(key.Time);
                WriteQuaternion(writer, key.Value);
            }

            writer.Write((byte)track.TranslationInterpolation);
            writer.Write(track.TranslationKeys.Length);
            foreach (var key in track.TranslationKeys)
            {
                writer.Write(key.Time);
                WriteVector3(writer, key.Value);
            }

            writer.Write((byte)track.ScaleInterpolation);
            writer.Write(track.ScaleKeys.Length);
            foreach (var key in track.ScaleKeys)
            {
                writer.Write(key.Time);
                writer.Write(key.Value);
            }
        }

        writer.Write(anim.TextKeys.Length);
        foreach (var key in anim.TextKeys)
        {
            writer.Write(key.Time);
            WriteNullableString(writer, key.Label, MaxStringBytes);
        }

        writer.Write(anim.ClipStart);
        writer.Write(anim.ClipStop);
        writer.Write(anim.ClipLoops);
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

        var animation = reader.ReadBoolean() ? ReadAnimation(reader) : null;
        var containsParticleSource = reader.ReadBoolean();

        return new ReferenceDecodedMeshPayload12(
            submeshes, collisionPositions, collisionTriangles, animation, containsParticleSource);
    }

    private static NifMeshAnimation ReadAnimation(BinaryReader reader)
    {
        var boneCount = ReadInt32(reader, 1, MaxAnimBones);
        var bones = new NifAnimBone[boneCount];
        var tracks = new NifNodeTrack?[boneCount];
        for (var i = 0; i < boneCount; i++)
        {
            var name = ReadNullableString(reader, MaxStringBytes) ?? $"#{i}";
            var parentIndex = reader.ReadInt32();
            var restTranslation = ReadVector3(reader);
            var restRotation = ReadQuaternion(reader);
            var restScale = reader.ReadSingle();
            bones[i] = new NifAnimBone(name, parentIndex, restTranslation, restRotation, restScale);

            if (!reader.ReadBoolean())
            {
                continue;
            }

            var frequency = reader.ReadSingle();
            var phase = reader.ReadSingle();

            var rotInterp = (NifKeyInterpolation)reader.ReadByte();
            var rotKeys = new NifQuatKey[ReadInt32(reader, 0, MaxKeysPerChannel)];
            for (var k = 0; k < rotKeys.Length; k++)
            {
                rotKeys[k] = new NifQuatKey(reader.ReadSingle(), ReadQuaternion(reader));
            }

            var transInterp = (NifKeyInterpolation)reader.ReadByte();
            var transKeys = new NifVec3Key[ReadInt32(reader, 0, MaxKeysPerChannel)];
            for (var k = 0; k < transKeys.Length; k++)
            {
                transKeys[k] = new NifVec3Key(reader.ReadSingle(), ReadVector3(reader));
            }

            var scaleInterp = (NifKeyInterpolation)reader.ReadByte();
            var scaleKeys = new NifFloatKey[ReadInt32(reader, 0, MaxKeysPerChannel)];
            for (var k = 0; k < scaleKeys.Length; k++)
            {
                scaleKeys[k] = new NifFloatKey(reader.ReadSingle(), reader.ReadSingle());
            }

            tracks[i] = new NifNodeTrack(
                name, frequency, phase, rotInterp, rotKeys, transInterp, transKeys, scaleInterp, scaleKeys);
        }

        var textKeys = new NifAnimTextKey[ReadInt32(reader, 0, MaxTextKeys)];
        for (var i = 0; i < textKeys.Length; i++)
        {
            var time = reader.ReadSingle();
            textKeys[i] = new NifAnimTextKey(time, ReadNullableString(reader, MaxStringBytes) ?? string.Empty);
        }

        var clipStart = reader.ReadSingle();
        var clipStop = reader.ReadSingle();
        var clipLoops = reader.ReadBoolean();
        return new NifMeshAnimation(bones, tracks, textKeys, clipStart, clipStop, clipLoops);
    }

    private static void WriteSubmesh(BinaryWriter writer, ReferenceDecodedSubmeshPayload12 submesh)
    {
        ValidateRange(submesh.Vertices.Length, 0, MaxVerticesPerSubmesh, nameof(submesh.Vertices));
        ValidateRange(submesh.Indices.Length, 0, MaxIndicesPerSubmesh, nameof(submesh.Indices));
        ValidateRange(submesh.SourceBlockIndex, -1, int.MaxValue, nameof(submesh.SourceBlockIndex));

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
        writer.Write(submesh.LocalBoundsRadius);
        writer.Write(submesh.IsBillboard);
        WriteVector3(writer, submesh.SpecularColor);
        writer.Write(submesh.Glossiness);
        writer.Write(submesh.SpecularEnabled);
        writer.Write(submesh.IsLeafBillboard);
        writer.Write(submesh.DepthWritingBlend);
        WriteNullableString(writer, submesh.SpecularMapTexturePath, MaxStringBytes);
        WriteNullableString(writer, submesh.GradientMapTexturePath, MaxStringBytes);
        writer.Write(submesh.GradientMapV);
        writer.Write(submesh.IsDecal);
        WriteVector3(writer, submesh.EffectTint);
        WriteVector4(writer, submesh.EffectFalloffParams);
        writer.Write(submesh.HasEffectFalloff);
        WriteNullableString(writer, submesh.EnvironmentMapTexturePath, MaxStringBytes);
        writer.Write(submesh.EnvironmentMapScale);
        writer.Write(submesh.EnvironmentMapSmoothness);
        WriteVector2(writer, submesh.UvScrollVelocity);
        writer.Write(submesh.IsSpeedTreeBranch);
        WriteVector2(writer, submesh.SpeedTreeWindSpeeds);

        writer.Write(submesh.Skin is not null);
        if (submesh.Skin is { } skin)
        {
            ValidateRange(skin.VertexCount, 1, MaxVerticesPerSubmesh, nameof(skin.BasePositions));
            ValidateRange(skin.InverseBindPoses.Length, 1, MaxAnimBones, nameof(skin.InverseBindPoses));

            writer.Write(skin.VertexCount);
            foreach (var f in skin.BasePositions) writer.Write(f);
            writer.Write(skin.BaseNormals is not null);
            if (skin.BaseNormals is { } normals)
            {
                foreach (var f in normals) writer.Write(f);
            }

            writer.Write(skin.InverseBindPoses.Length);
            foreach (var m in skin.InverseBindPoses) WriteMatrix(writer, m);
            foreach (var idx in skin.SkinBoneToAnimBone) writer.Write(idx);
            writer.Write(skin.BoneIndices);
            foreach (var w in skin.BoneWeights) writer.Write(w);
        }

        writer.Write(submesh.ClampTextureU);
        writer.Write(submesh.ClampTextureV);
        writer.Write(submesh.IsParticleCloud);
        writer.Write(submesh.SoftParticleFalloffDepth);
        WriteMaterialAlphaController(writer, submesh.MaterialAlphaController);
        WritePhysicsLiteSway(writer, submesh.PhysicsLiteSway);
        writer.Write(submesh.IsLighting30);
        WriteNullableString(writer, submesh.Lighting30GlowMapTexturePath, MaxStringBytes);
        WriteVector3(writer, submesh.Lighting30EmissionColor);
        writer.Write(submesh.Lighting30EmissionMultiplier);
        writer.Write(submesh.IsTallGrass);
        WriteNullableString(writer, submesh.ClassicEnvironmentMapTexturePath, MaxStringBytes);
        WriteNullableString(writer, submesh.ClassicEnvironmentMaskTexturePath, MaxStringBytes);
        writer.Write(submesh.ClassicEnvironmentMapScale);
        writer.Write(submesh.ClassicEnvironmentMapUsesWindowReflection);
        WriteNullableString(writer, submesh.ClassicParallaxHeightMapTexturePath, MaxStringBytes);
        writer.Write((byte)submesh.ClassicBasicShaderMode);
        writer.Write(submesh.SourceBlockIndex);
        writer.Write((ushort)submesh.BillboardMode);
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

        var payload = new ReferenceDecodedSubmeshPayload12(
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
            reader.ReadSingle(),
            reader.ReadBoolean(),
            ReadVector3(reader),
            reader.ReadSingle(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            ReadNullableString(reader, MaxStringBytes),
            ReadNullableString(reader, MaxStringBytes),
            reader.ReadSingle(),
            reader.ReadBoolean(),
            ReadVector3(reader),
            ReadVector4(reader),
            reader.ReadBoolean(),
            ReadNullableString(reader, MaxStringBytes),
            reader.ReadSingle(),
            reader.ReadSingle(),
            ReadVector2(reader),
            reader.ReadBoolean(),
            ReadVector2(reader),
            reader.ReadBoolean() ? ReadSubmeshSkin(reader) : null,
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadSingle(),
            ReadMaterialAlphaController(reader),
            ReadPhysicsLiteSway(reader),
            reader.ReadBoolean(),
            ReadNullableString(reader, MaxStringBytes),
            ReadVector3(reader),
            reader.ReadSingle(),
            reader.ReadBoolean(),
            ReadNullableString(reader, MaxStringBytes),
            ReadNullableString(reader, MaxStringBytes),
            reader.ReadSingle(),
            reader.ReadBoolean(),
            ReadNullableString(reader, MaxStringBytes),
            (FnvClassicBasicShaderMode)reader.ReadByte(),
            reader.ReadInt32(),
            (NifBillboardMode)reader.ReadUInt16());
        if (!Enum.IsDefined(payload.ClassicBasicShaderMode))
        {
            throw new InvalidDataException("Invalid FNV classic basic shader mode in decoded mesh cache.");
        }

        if (payload.SourceBlockIndex < -1)
        {
            throw new InvalidDataException("Invalid source shape block index in decoded mesh cache.");
        }

        if (!Enum.IsDefined(payload.BillboardMode))
        {
            throw new InvalidDataException("Invalid NIF billboard mode in decoded mesh cache.");
        }

        return payload;
    }

    private static void WritePhysicsLiteSway(
        BinaryWriter writer, PhysicsLiteSwayDescriptor? descriptor)
    {
        writer.Write(descriptor.HasValue);
        if (descriptor is not { } sway)
        {
            return;
        }

        writer.Write(sway.ConstraintBlockIndex);
        WriteVector3(writer, sway.Pivot);
        WriteVector3(writer, sway.Axis);
        writer.Write(sway.MinimumAngle);
        writer.Write(sway.MaximumAngle);
        writer.Write(sway.AmplitudeFraction);
        writer.Write(sway.CyclesPerSecond);
    }

    private static PhysicsLiteSwayDescriptor? ReadPhysicsLiteSway(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }

        var descriptor = new PhysicsLiteSwayDescriptor(
            reader.ReadInt32(),
            ReadVector3(reader),
            ReadVector3(reader),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle());
        if (descriptor.ConstraintBlockIndex < 0 ||
            !IsFinite(descriptor.Pivot) ||
            !IsFinite(descriptor.Axis) || descriptor.Axis.LengthSquared() < 1e-8f ||
            !float.IsFinite(descriptor.MinimumAngle) ||
            !float.IsFinite(descriptor.MaximumAngle) ||
            descriptor.MinimumAngle > descriptor.MaximumAngle ||
            !float.IsFinite(descriptor.AmplitudeFraction) ||
            descriptor.AmplitudeFraction <= 0f || descriptor.AmplitudeFraction > 1f ||
            !float.IsFinite(descriptor.CyclesPerSecond) || descriptor.CyclesPerSecond <= 0f)
        {
            throw new InvalidDataException("Invalid FNV physics-lite descriptor in decoded mesh cache.");
        }

        return descriptor;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void WriteMaterialAlphaController(
        BinaryWriter writer, NifMaterialAlphaController? controller)
    {
        writer.Write(controller is not null);
        if (controller is null)
        {
            return;
        }

        ValidateRange(controller.Keys.Length, 0, MaxKeysPerChannel, nameof(controller.Keys));
        writer.Write(controller.MaterialPropertyRef);
        WriteNullableString(writer, controller.TargetName, MaxStringBytes);
        writer.Write((byte)controller.Interpolation);
        writer.Write(controller.Keys.Length);
        foreach (var key in controller.Keys)
        {
            writer.Write(key.Time);
            writer.Write(key.Value);
        }

        writer.Write(controller.ConstantValue.HasValue);
        if (controller.ConstantValue is { } constantValue)
        {
            writer.Write(constantValue);
        }

        WriteAlphaClock(writer, controller.SequenceClock);
        WriteAlphaClock(writer, controller.ControllerClock);
    }

    private static NifMaterialAlphaController? ReadMaterialAlphaController(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }

        var materialPropertyRef = reader.ReadInt32();
        var targetName = ReadNullableString(reader, MaxStringBytes) ?? string.Empty;
        var interpolationValue = reader.ReadByte();
        if (!Enum.IsDefined(typeof(NifKeyInterpolation), interpolationValue))
        {
            throw new InvalidDataException("Invalid material-alpha interpolation in decoded mesh cache.");
        }

        var keys = new NifFloatKey[ReadInt32(reader, 0, MaxKeysPerChannel)];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = new NifFloatKey(reader.ReadSingle(), reader.ReadSingle());
        }

        var constantValue = reader.ReadBoolean() ? reader.ReadSingle() : (float?)null;
        return new NifMaterialAlphaController(
            materialPropertyRef,
            targetName,
            (NifKeyInterpolation)interpolationValue,
            keys,
            constantValue,
            ReadAlphaClock(reader),
            ReadAlphaClock(reader));
    }

    private static void WriteAlphaClock(BinaryWriter writer, NifAlphaControllerClock clock)
    {
        writer.Write(clock.Frequency);
        writer.Write(clock.Phase);
        writer.Write(clock.StartTime);
        writer.Write(clock.StopTime);
        writer.Write((byte)clock.Cycle);
    }

    private static NifAlphaControllerClock ReadAlphaClock(BinaryReader reader)
    {
        var frequency = reader.ReadSingle();
        var phase = reader.ReadSingle();
        var startTime = reader.ReadSingle();
        var stopTime = reader.ReadSingle();
        var cycleValue = reader.ReadByte();
        if (cycleValue > (byte)NifCycleType.Clamp)
        {
            throw new InvalidDataException("Invalid material-alpha cycle type in decoded mesh cache.");
        }

        return new NifAlphaControllerClock(
            frequency, phase, startTime, stopTime, (NifCycleType)cycleValue);
    }

    private static NifSubmeshSkin ReadSubmeshSkin(BinaryReader reader)
    {
        var vertexCount = ReadInt32(reader, 1, MaxVerticesPerSubmesh);
        var basePositions = new float[vertexCount * 3];
        for (var i = 0; i < basePositions.Length; i++) basePositions[i] = reader.ReadSingle();

        float[]? baseNormals = null;
        if (reader.ReadBoolean())
        {
            baseNormals = new float[vertexCount * 3];
            for (var i = 0; i < baseNormals.Length; i++) baseNormals[i] = reader.ReadSingle();
        }

        var boneCount = ReadInt32(reader, 1, MaxAnimBones);
        var inverseBinds = new Matrix4x4[boneCount];
        for (var i = 0; i < boneCount; i++) inverseBinds[i] = ReadMatrix(reader);

        var skinBoneToAnimBone = new int[boneCount];
        for (var i = 0; i < boneCount; i++) skinBoneToAnimBone[i] = reader.ReadInt32();

        var boneIndices = reader.ReadBytes(vertexCount * 4);
        if (boneIndices.Length != vertexCount * 4)
        {
            throw new InvalidDataException("Truncated skin bone indices in decoded mesh cache.");
        }

        var boneWeights = new float[vertexCount * 4];
        for (var i = 0; i < boneWeights.Length; i++) boneWeights[i] = reader.ReadSingle();

        return new NifSubmeshSkin(
            basePositions, baseNormals, inverseBinds, skinBoneToAnimBone, boneIndices, boneWeights);
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

    private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }

    private static Quaternion ReadQuaternion(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteMatrix(BinaryWriter writer, Matrix4x4 m)
    {
        writer.Write(m.M11); writer.Write(m.M12); writer.Write(m.M13); writer.Write(m.M14);
        writer.Write(m.M21); writer.Write(m.M22); writer.Write(m.M23); writer.Write(m.M24);
        writer.Write(m.M31); writer.Write(m.M32); writer.Write(m.M33); writer.Write(m.M34);
        writer.Write(m.M41); writer.Write(m.M42); writer.Write(m.M43); writer.Write(m.M44);
    }

    private static Matrix4x4 ReadMatrix(BinaryReader reader) =>
        new(
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

/// <summary>A disk-cache entry for a decoded reference mesh: the payload, or a negative (known-empty) marker.</summary>
internal readonly record struct ReferenceDecodedMeshDiskCacheEntry12(
    ReferenceDecodedMeshPayload12? Mesh,
    bool IsNegative);

/// <summary>A decoded reference mesh ready to cache/upload: its submeshes plus optional collision geometry
/// and (v32+) the keyframe animation rig for animated statics.</summary>
internal sealed record ReferenceDecodedMeshPayload12(
    IReadOnlyList<ReferenceDecodedSubmeshPayload12> Submeshes,
    Vector3[]? CollisionPositions = null,
    int[]? CollisionTriangles = null,
    NifMeshAnimation? Animation = null,
    // Source provenance (v46+), independent of whether the static bake produced a cloud submesh.
    bool ContainsParticleSource = false);

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
    float LocalBoundsRadius,
    bool IsBillboard,
    Vector3 SpecularColor = default,
    float Glossiness = 0f,
    bool SpecularEnabled = false,
    bool IsLeafBillboard = false,
    bool DepthWritingBlend = false,
    string? SpecularMapTexturePath = null,
    string? GradientMapTexturePath = null,
    float GradientMapV = 0f,
    bool IsDecal = false,
    Vector3 EffectTint = default,
    Vector4 EffectFalloffParams = default,
    bool HasEffectFalloff = false,
    string? EnvironmentMapTexturePath = null,
    float EnvironmentMapScale = 0f,
    float EnvironmentMapSmoothness = 0f,
    // TES3 NiUVController constant scroll (v32+): UV units/second, zero = static.
    Vector2 UvScrollVelocity = default,
    // SpeedTree bark/frond route + TREE.CNAM per-tree phase multipliers (v40+).
    bool IsSpeedTreeBranch = false,
    Vector2 SpeedTreeWindSpeeds = default,
    // CPU skinning inputs for keyframe playback (v32+); null for unskinned submeshes.
    NifSubmeshSkin? Skin = null,
    // BGSM/BGEM TileU/TileV normalized to sampler clamp state (v42+).
    bool ClampTextureU = false,
    bool ClampTextureV = false,
    // Baked particle-cloud marker (v43+); drives per-quad camera sorting at draw time.
    bool IsParticleCloud = false,
    // Authored BSEffect soft-intersection depth (v48+); zero means no serialized depth.
    float SoftParticleFalloffDepth = 0f,
    // Manager-driven material opacity (v49+).
    NifMaterialAlphaController? MaterialAlphaController = null,
    // Strict FNV BS34 ambient hinge/ragdoll route (v51+), already in baked root-local coordinates.
    PhysicsLiteSwayDescriptor? PhysicsLiteSway = null,
    // Classic FO3/FNV Lighting30 material emission/glow route (v52+).
    bool IsLighting30 = false,
    string? Lighting30GlowMapTexturePath = null,
    Vector3 Lighting30EmissionColor = default,
    float Lighting30EmissionMultiplier = 1f,
    // TallGrassShaderProperty identity (v53+). VertexColor.w is its raw wind weight.
    bool IsTallGrass = false,
    // Classic FO3/FNV PP-lighting environment pass (v54+), distinct from FO4 BGSM _s semantics.
    string? ClassicEnvironmentMapTexturePath = null,
    string? ClassicEnvironmentMaskTexturePath = null,
    float ClassicEnvironmentMapScale = 0f,
    bool ClassicEnvironmentMapUsesWindowReflection = false,
    // Classic simple-parallax height map (v55+); bit-28 POM is excluded before persistence.
    string? ClassicParallaxHeightMapTexturePath = null,
    // Audit-only PC-final SLS1009/SLS1013 identity (v57+), with all-vertex validity in v58,
    // static/effective-path scope in v59, authored transformed-basis magnitudes in v60, and raw
    // type-1/non-skinned/non-single-pass scope in v62. Active retail ADT reuses it only as its
    // strict ordinary-material/vertex-color discriminator.
    FnvClassicBasicShaderMode ClassicBasicShaderMode = FnvClassicBasicShaderMode.None,
    // Stable source shape provenance (v63+); -1 means unavailable.
    int SourceBlockIndex = -1,
    // Authored NiBillboardNode mode (v64+). RotateAboutUp preserves pre-v64 behavior.
    NifBillboardMode BillboardMode = NifBillboardMode.RotateAboutUp);
