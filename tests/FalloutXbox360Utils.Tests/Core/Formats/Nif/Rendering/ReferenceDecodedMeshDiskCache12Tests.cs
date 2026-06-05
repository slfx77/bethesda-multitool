using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class ReferenceDecodedMeshDiskCache12Tests
{
    [Fact]
    public void StoreAndTryLoad_RoundTripsPositivePayload()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(fileRawSize: 64, archiveTicks: 1000);
        var payload = CreatePayload();

        cache.Store(metadata, payload);

        Assert.True(cache.TryLoad(metadata, out var entry));
        Assert.False(entry.IsNegative);
        Assert.NotNull(entry.Mesh);
        var mesh = entry.Mesh;

        var loaded = Assert.Single(mesh.Submeshes);
        Assert.Equal("textures\\foo.dds", loaded.DiffuseTexturePath);
        Assert.Equal("textures\\foo_n.dds", loaded.NormalMapTexturePath);
        Assert.True(loaded.HasBump);
        Assert.Equal(NifAlphaRenderMode.Blend, loaded.AlphaRenderMode);
        Assert.Equal(0.5f, loaded.AlphaTestThreshold);
        Assert.Equal(7, loaded.SrcBlendMode);
        Assert.Equal(8, loaded.DstBlendMode);
        Assert.Equal(new Vector3(1, 2, 3), loaded.LocalBoundsCenter);
        Assert.Equal(new ushort[] { 0, 1, 2 }, loaded.Indices);
        Assert.Equal(new Vector3(10, 20, 30), loaded.Vertices[0].Position);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), loaded.Vertices[0].VertexColor);
    }

    [Fact]
    public void StoreAndTryLoad_RoundTripsNegativePayload()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(fileRawSize: null, archiveTicks: 1000, found: false);

        cache.Store(metadata, payload: null);

        Assert.True(cache.TryLoad(metadata, out var entry));
        Assert.True(entry.IsNegative);
        Assert.Null(entry.Mesh);
    }

    [Fact]
    public void TryLoad_InvalidatesWhenSourceMetadataChanges()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var original = CreateMetadata(fileRawSize: 64, archiveTicks: 1000);
        var changed = CreateMetadata(fileRawSize: 65, archiveTicks: 1000);

        cache.Store(original, CreatePayload());

        Assert.True(cache.TryLoad(original, out _));
        Assert.False(cache.TryLoad(changed, out _));
    }

    [Fact]
    public void TryLoad_CorruptedCacheReturnsMissAndDeletesFile()
    {
        using var tempDir = new TempDirectory();
        var cache = new ReferenceDecodedMeshDiskCache12(tempDir.Path);
        var metadata = CreateMetadata(fileRawSize: 64, archiveTicks: 1000);
        cache.Store(metadata, CreatePayload());
        var path = cache.GetCachePath(metadata);
        File.WriteAllBytes(path, [0x42, 0x61, 0x64]);

        Assert.False(cache.TryLoad(metadata, out _));
        Assert.False(File.Exists(path));
    }

    private static NpcMeshArchiveLookupMetadata CreateMetadata(
        uint? fileRawSize,
        long archiveTicks,
        bool found = true) =>
        new(
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

    private static ReferenceDecodedMeshPayload12 CreatePayload()
    {
        var vertex = new GpuMeshUploader.GpuVertex
        {
            Position = new Vector3(10, 20, 30),
            Normal = new Vector3(0, 0, 1),
            TexCoord = new Vector2(0.25f, 0.75f),
            VertexColor = new Vector4(0.1f, 0.2f, 0.3f, 0.4f),
            Tangent = new Vector3(1, 0, 0),
            Bitangent = new Vector3(0, 1, 0)
        };

        return new ReferenceDecodedMeshPayload12([
            new ReferenceDecodedSubmeshPayload12(
                [vertex],
                [0, 1, 2],
                "textures\\foo.dds",
                "textures\\foo_n.dds",
                HasBump: true,
                NifAlphaRenderMode.Blend,
                AlphaBlend: true,
                AlphaTest: true,
                AlphaTestThreshold: 0.5f,
                AlphaTestFunction: 6,
                SrcBlendMode: 7,
                DstBlendMode: 8,
                MaterialAlpha: 0.9f,
                DoubleSided: true,
                IsEmissive: false,
                LocalBoundsCenter: new Vector3(1, 2, 3))
        ]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ReferenceDecodedMeshDiskCache12Tests_" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
