using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class ReferenceDecodedMeshDiskCache12Tests
{
    [Fact]
    public void DefaultCacheDirectories_AreUnderOsTempDirectory()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!Path.EndsInDirectorySeparator(tempRoot))
        {
            tempRoot += Path.DirectorySeparatorChar;
        }

        var meshCache = ReferenceDiskCachePaths.ResolveDefaultCacheDirectory(
            "ReferenceDecodedMeshCache12",
            ReferenceDecodedMeshDiskCache12.DecoderVersion);
        var textureCache = ReferenceDiskCachePaths.ResolveDefaultCacheDirectory(
            "ReferenceDecodedTextureCache12",
            ReferenceDecodedTextureDiskCache12.DecoderVersion);

        Assert.StartsWith(tempRoot, Path.GetFullPath(meshCache), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(tempRoot, Path.GetFullPath(textureCache), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BethesdaMultitool", meshCache, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BethesdaMultitool", textureCache, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StoreAndTryLoad_RoundTripsPositivePayload()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        var payload = CreatePayload();

        cache.Store(metadata, null, payload);

        Assert.True(cache.TryLoad(metadata, null, out var entry));
        Assert.False(entry.IsNegative);
        Assert.NotNull(entry.Mesh);
        var mesh = entry.Mesh;

        var loaded = Assert.Single(mesh.Submeshes);
        Assert.Equal("textures\\foo.dds", loaded.DiffuseTexturePath);
        Assert.Equal("textures\\foo_n.dds", loaded.NormalMapTexturePath);
        Assert.True(loaded.HasBump);
        Assert.True(loaded.DoubleSided);
        Assert.Equal(NifAlphaRenderMode.Blend, loaded.AlphaRenderMode);
        Assert.Equal(0.5f, loaded.AlphaTestThreshold);
        Assert.Equal(7, loaded.SrcBlendMode);
        Assert.Equal(8, loaded.DstBlendMode);
        Assert.Equal(new Vector3(1, 2, 3), loaded.LocalBoundsCenter);
        Assert.Equal(4.5f, loaded.LocalBoundsRadius);
        Assert.True(loaded.IsBillboard);
        Assert.Equal(NifBillboardMode.AlwaysFaceCenter, loaded.BillboardMode);
        Assert.True(loaded.IsLeafBillboard);
        Assert.True(loaded.IsDecal);
        Assert.Equal(new Vector3(0.478f, 0.478f, 0.478f), loaded.EffectTint);
        Assert.Equal(new Vector4(0.98481f, 0.17365f, 1f, 0f), loaded.EffectFalloffParams);
        Assert.True(loaded.HasEffectFalloff);
        Assert.Equal(321f, loaded.SoftParticleFalloffDepth);
        Assert.NotNull(loaded.MaterialAlphaController);
        Assert.Equal("sStorm03:0", loaded.MaterialAlphaController.TargetName);
        Assert.Equal(0.5f, loaded.MaterialAlphaController.Sample(2.3333f), 3);
        var sway = Assert.IsType<PhysicsLiteSwayDescriptor>(loaded.PhysicsLiteSway);
        Assert.Equal(19, sway.ConstraintBlockIndex);
        Assert.Equal(new Vector3(-55.7041f, 0.05523f, -19.1407f), sway.Pivot);
        Assert.Equal(-Vector3.UnitY, sway.Axis);
        Assert.Equal(-MathF.PI / 2f, sway.MinimumAngle);
        Assert.Equal(MathF.PI / 2f, sway.MaximumAngle);
        Assert.True(loaded.IsLighting30);
        Assert.Equal("textures\\foo_g.dds", loaded.Lighting30GlowMapTexturePath);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), loaded.Lighting30EmissionColor);
        Assert.Equal(2.5f, loaded.Lighting30EmissionMultiplier);
        Assert.True(loaded.ClampTextureU);
        Assert.False(loaded.ClampTextureV);
        Assert.Equal(new ushort[] { 0, 1, 2 }, loaded.Indices);
        Assert.Equal(new Vector3(10, 20, 30), loaded.Vertices[0].Position);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 43f / 255f), loaded.Vertices[0].VertexColor);
        Assert.True(loaded.IsTallGrass);
        Assert.Equal("textures\\effects\\chrome_e.dds", loaded.ClassicEnvironmentMapTexturePath);
        Assert.Equal("textures\\foo_m.dds", loaded.ClassicEnvironmentMaskTexturePath);
        Assert.Equal(1.25f, loaded.ClassicEnvironmentMapScale);
        Assert.True(loaded.ClassicEnvironmentMapUsesWindowReflection);
        Assert.Null(loaded.ClassicParallaxHeightMapTexturePath);
        Assert.Equal(FnvClassicBasicShaderMode.Sls1013VertexColor, loaded.ClassicBasicShaderMode);
        Assert.Equal(41, loaded.SourceBlockIndex);
        // v68: the ENGINE z-write rule bits (EngineZWriteOff + DepthTestOff) joined the payload.
        // v69: triggered-FX rest-state resolve changed particle bake output (dormant emitters).
        // v71: EmitterActive bool bindings gate baked birth rates (NVNellisArtillery idle smoke).
        // v73: Havok provenance joined the payload; a default payload remains fallback-eligible.
        // v76: Starfield (bsVersion >= 170) NIFs decode for the first time — the payload shape is
        // unchanged, but every previously-cached Starfield entry is a stale NEGATIVE and must not be
        // served, so the version had to move.
        // v77: Starfield BSGeometry shapes with an EMPTY shader material name are dropped at
        // extraction (untexturable proxy/LOD shapes that rendered bright white); warm v76 entries
        // still contain those submeshes.
        Assert.True(loaded.EngineZWriteOff);
        Assert.True(loaded.DepthTestOff);
        Assert.Equal(HavokCollisionProvenance.AbsentOrUnsupported, mesh.CollisionProvenance);
        Assert.Equal(77, ReferenceDecodedMeshDiskCache12.DecoderVersion);
    }

    [Fact]
    public void StoreAndTryLoad_RoundTripsClassicParallaxWithoutViolatingTextureIndexUnion()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        var payload = CreatePayload();
        var source = Assert.Single(payload.Submeshes);
        payload = payload with
        {
            Submeshes =
            [
                source with
                {
                    ClassicEnvironmentMapTexturePath = null,
                    ClassicEnvironmentMaskTexturePath = null,
                    ClassicEnvironmentMapScale = 0f,
                    ClassicEnvironmentMapUsesWindowReflection = false,
                    ClassicParallaxHeightMapTexturePath =
                    "textures\\landscape\\RubblePile05_p.dds"
                }
            ]
        };

        cache.Store(metadata, null, payload);

        Assert.True(cache.TryLoad(metadata, null, out var entry));
        var loaded = Assert.Single(Assert.IsType<ReferenceDecodedMeshPayload12>(entry.Mesh).Submeshes);
        Assert.Null(loaded.ClassicEnvironmentMapTexturePath);
        Assert.Null(loaded.ClassicEnvironmentMaskTexturePath);
        Assert.Equal(
            "textures\\landscape\\RubblePile05_p.dds",
            loaded.ClassicParallaxHeightMapTexturePath);
    }

    [Fact]
    public void StoreAndTryLoad_RoundTripsNegativePayload()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(null, 1000, false);

        cache.Store(metadata, null, null);

        Assert.True(cache.TryLoad(metadata, null, out var entry));
        Assert.True(entry.IsNegative);
        Assert.Null(entry.Mesh);
    }

    [Fact]
    public void StoreAndTryLoad_RoundTripsQuietParticleSourceProvenanceWithoutCloudGeometry()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        var payload = CreatePayload() with { ContainsParticleSource = true };

        cache.Store(metadata, null, payload);

        Assert.True(cache.TryLoad(metadata, null, out var entry));
        Assert.False(entry.IsNegative);
        var loaded = Assert.IsType<ReferenceDecodedMeshPayload12>(entry.Mesh);
        Assert.True(loaded.ContainsParticleSource);
        Assert.False(Assert.Single(loaded.Submeshes).IsParticleCloud);
    }

    [Fact]
    public void StoreAndTryLoad_RoundTripsAuthoredNoncollidableWithoutCollisionArrays()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        var payload = CreatePayload() with
        {
            CollisionProvenance = HavokCollisionProvenance.AuthoredNoncollidable
        };

        cache.Store(metadata, null, payload);

        Assert.True(cache.TryLoad(metadata, null, out var entry));
        var loaded = Assert.IsType<ReferenceDecodedMeshPayload12>(entry.Mesh);
        Assert.Equal(HavokCollisionProvenance.AuthoredNoncollidable, loaded.CollisionProvenance);
        Assert.Null(loaded.CollisionPositions);
        Assert.Null(loaded.CollisionTriangles);
    }

    [Fact]
    public void StoreAndTryLoad_RoundTripsAuthoredMeshProvenanceAndSoup()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        int[] triangles = [0, 1, 2];
        var payload = CreatePayload() with
        {
            CollisionProvenance = HavokCollisionProvenance.AuthoredMesh,
            CollisionPositions = positions,
            CollisionTriangles = triangles
        };

        cache.Store(metadata, null, payload);

        Assert.True(cache.TryLoad(metadata, null, out var entry));
        var loaded = Assert.IsType<ReferenceDecodedMeshPayload12>(entry.Mesh);
        Assert.Equal(HavokCollisionProvenance.AuthoredMesh, loaded.CollisionProvenance);
        Assert.Equal(positions, loaded.CollisionPositions);
        Assert.Equal(triangles, loaded.CollisionTriangles);
    }

    [Fact]
    public void StoreAndTryLoad_RoundTripsCollisionOnlyAuthoredNoncollidablePayload()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        var payload = CreatePayload() with
        {
            Submeshes = [],
            CollisionProvenance = HavokCollisionProvenance.AuthoredNoncollidable
        };

        cache.Store(metadata, null, payload);

        Assert.True(cache.TryLoad(metadata, null, out var entry));
        Assert.False(entry.IsNegative);
        var loaded = Assert.IsType<ReferenceDecodedMeshPayload12>(entry.Mesh);
        Assert.Empty(loaded.Submeshes);
        Assert.Equal(HavokCollisionProvenance.AuthoredNoncollidable, loaded.CollisionProvenance);
        Assert.Null(loaded.CollisionPositions);
        Assert.Null(loaded.CollisionTriangles);
    }

    [Fact]
    public void StoreAndTryLoad_RoundTripsCollisionOnlyAuthoredMeshPayload()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        int[] triangles = [0, 1, 2];
        var payload = CreatePayload() with
        {
            Submeshes = [],
            CollisionProvenance = HavokCollisionProvenance.AuthoredMesh,
            CollisionPositions = positions,
            CollisionTriangles = triangles
        };

        cache.Store(metadata, null, payload);

        Assert.True(cache.TryLoad(metadata, null, out var entry));
        Assert.False(entry.IsNegative);
        var loaded = Assert.IsType<ReferenceDecodedMeshPayload12>(entry.Mesh);
        Assert.Empty(loaded.Submeshes);
        Assert.Equal(HavokCollisionProvenance.AuthoredMesh, loaded.CollisionProvenance);
        Assert.Equal(positions, loaded.CollisionPositions);
        Assert.Equal(triangles, loaded.CollisionTriangles);
    }

    [Fact]
    public void Store_EmptyRenderWithAbsentCollisionDoesNotPublishPositiveFile()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);

        cache.Store(metadata, null, CreatePayload() with { Submeshes = [] });

        Assert.False(File.Exists(cache.GetCachePath(metadata)));
    }

    [Fact]
    public void Store_InvalidAuthoredCollisionPayloadsDoNotPublishFile()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        ReferenceDecodedMeshPayload12[] invalidPayloads =
        [
            CreatePayload() with
            {
                CollisionProvenance = HavokCollisionProvenance.AuthoredMesh,
                CollisionPositions = [new Vector3(float.NaN, 0f, 0f), Vector3.UnitX, Vector3.UnitY],
                CollisionTriangles = [0, 1, 2]
            },
            CreatePayload() with
            {
                CollisionProvenance = HavokCollisionProvenance.AuthoredMesh,
                CollisionPositions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                CollisionTriangles = [0, 1, 3]
            },
            CreatePayload() with
            {
                CollisionProvenance = HavokCollisionProvenance.AuthoredMesh,
                CollisionPositions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                CollisionTriangles = [0, 1, 2, 0]
            }
        ];

        foreach (var payload in invalidPayloads)
        {
            cache.Store(metadata, null, payload);
            Assert.False(File.Exists(cache.GetCachePath(metadata)));
        }
    }

    [Theory]
    [InlineData(73)] // v74 TES3 placed-water classification invalidated this predecessor.
    [InlineData(74)] // v75 FO4 refraction-shape retention invalidated this predecessor.
    public void TryLoad_PredecessorEntryReturnsMissAndDeletesFile(int staleDecoderVersion)
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        cache.Store(metadata, null, CreatePayload());
        var path = cache.GetCachePath(metadata);
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12, sizeof(int)),
            staleDecoderVersion);
        File.WriteAllBytes(path, bytes);

        Assert.False(cache.TryLoad(metadata, null, out _));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryLoad_InvalidCollisionProvenanceReturnsMissAndDeletesFile()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        cache.Store(metadata, null, CreatePayload());
        var path = cache.GetCachePath(metadata);
        var bytes = File.ReadAllBytes(path);
        var provenanceOffset = FindPayloadOffset(bytes);
        bytes[provenanceOffset] = byte.MaxValue;
        File.WriteAllBytes(path, bytes);

        Assert.False(cache.TryLoad(metadata, null, out _));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryLoad_AuthoredNoneWithCollisionArraysReturnsMissAndDeletesFile()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        var payload = CreatePayload() with
        {
            CollisionProvenance = HavokCollisionProvenance.AuthoredMesh,
            CollisionPositions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            CollisionTriangles = [0, 1, 2]
        };
        cache.Store(metadata, null, payload);
        var path = cache.GetCachePath(metadata);
        var bytes = File.ReadAllBytes(path);
        bytes[FindPayloadOffset(bytes)] = (byte)HavokCollisionProvenance.AuthoredNoncollidable;
        File.WriteAllBytes(path, bytes);

        Assert.False(cache.TryLoad(metadata, null, out _));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Store_AbsentProvenanceWithCollisionArraysDoesNotPublishFile()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        var payload = CreatePayload() with
        {
            CollisionPositions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            CollisionTriangles = [0, 1, 2]
        };

        cache.Store(metadata, null, payload);

        Assert.False(File.Exists(cache.GetCachePath(metadata)));
    }

    [Fact]
    public void TryLoad_InvalidatesWhenSourceMetadataChanges()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var original = CreateMetadata(64, 1000);
        var changed = CreateMetadata(65, 1000);

        cache.Store(original, null, CreatePayload());

        Assert.True(cache.TryLoad(original, null, out _));
        Assert.False(cache.TryLoad(changed, null, out _));
    }

    [Fact]
    public void TryLoad_CorruptedCacheReturnsMissAndDeletesFile()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(64, 1000);
        cache.Store(metadata, null, CreatePayload());
        var path = cache.GetCachePath(metadata);
        File.WriteAllBytes(path, [0x42, 0x61, 0x64]);

        Assert.False(cache.TryLoad(metadata, null, out _));
        Assert.False(File.Exists(path));
    }

    private static MeshArchiveLookupMetadata CreateMetadata(
        uint? fileRawSize,
        long archiveTicks,
        bool found = true)
    {
        return new MeshArchiveLookupMetadata(
            "meshes\\clutter\\crate.nif",
            found,
            $"archive-set:{archiveTicks}",
            found ? "C:\\Games\\FalloutNV\\Data\\Meshes.bsa" : null,
            found ? 123_456L : null,
            found ? archiveTicks : null,
            found ? 0x0123456789ABCDEFUL : null,
            fileRawSize,
            fileRawSize,
            found ? 2048U : null);
    }

    private static int FindPayloadOffset(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        _ = reader.ReadBytes(8); // magic
        _ = reader.ReadInt32(); // container version
        _ = reader.ReadInt32(); // decoder version
        var keyLength = reader.ReadInt32();
        _ = reader.ReadBytes(keyLength);
        Assert.False(reader.ReadBoolean()); // positive entry
        return checked((int)stream.Position);
    }

    private static ReferenceDecodedMeshPayload12 CreatePayload()
    {
        var vertex = new GpuMeshUploader.GpuVertex
        {
            Position = new Vector3(10, 20, 30),
            Normal = new Vector3(0, 0, 1),
            TexCoord = new Vector2(0.25f, 0.75f),
            VertexColor = new Vector4(0.1f, 0.2f, 0.3f, 43f / 255f),
            Tangent = new Vector3(1, 0, 0),
            Bitangent = new Vector3(0, 1, 0)
        };

        return new ReferenceDecodedMeshPayload12([
            new ReferenceDecodedSubmeshPayload12(
                [vertex],
                [0, 1, 2],
                "textures\\foo.dds",
                "textures\\foo_n.dds",
                true,
                NifAlphaRenderMode.Blend,
                true,
                true,
                0.5f,
                6,
                7,
                8,
                0.9f,
                true,
                false,
                new Vector3(1, 2, 3),
                4.5f,
                true,
                IsLeafBillboard: true,
                IsDecal: true,
                EffectTint: new Vector3(0.478f, 0.478f, 0.478f),
                EffectFalloffParams: new Vector4(0.98481f, 0.17365f, 1f, 0f),
                HasEffectFalloff: true,
                ClampTextureU: true,
                ClampTextureV: false,
                SoftParticleFalloffDepth: 321f,
                MaterialAlphaController: new NifMaterialAlphaController(
                    17,
                    "sStorm03:0",
                    NifKeyInterpolation.Linear,
                    [
                        new NifFloatKey(0f, 0f),
                        new NifFloatKey(2.3333f, 0.5f),
                        new NifFloatKey(45f, 0f)
                    ],
                    null,
                    new NifAlphaControllerClock(1f, 0f, 0f, 45f, NifCycleType.Loop),
                    new NifAlphaControllerClock(1f, 0f, 0f, 45f, NifCycleType.Loop)),
                PhysicsLiteSway: new PhysicsLiteSwayDescriptor(
                    19,
                    new Vector3(-55.7041f, 0.05523f, -19.1407f),
                    -Vector3.UnitY,
                    -MathF.PI / 2f,
                    MathF.PI / 2f,
                    0.35f,
                    0.18f),
                IsLighting30: true,
                Lighting30GlowMapTexturePath: "textures\\foo_g.dds",
                Lighting30EmissionColor: new Vector3(0.25f, 0.5f, 0.75f),
                Lighting30EmissionMultiplier: 2.5f,
                IsTallGrass: true,
                ClassicEnvironmentMapTexturePath: "textures\\effects\\chrome_e.dds",
                ClassicEnvironmentMaskTexturePath: "textures\\foo_m.dds",
                ClassicEnvironmentMapScale: 1.25f,
                ClassicEnvironmentMapUsesWindowReflection: true,
                ClassicBasicShaderMode: FnvClassicBasicShaderMode.Sls1013VertexColor,
                SourceBlockIndex: 41,
                BillboardMode: NifBillboardMode.AlwaysFaceCenter,
                EngineZWriteOff: true,
                DepthTestOff: true)
        ]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ReferenceDecodedMeshDiskCache12Tests_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
