using FalloutXbox360Utils.CLI.Rendering.Nif;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifExportPathResolverTests
{
    [Fact]
    public void ResolveOutputPath_UsesDirectoryOutputWhenNoGlbExtensionIsProvided()
    {
        var root = Path.Combine(Path.GetTempPath(), "NifExportPathResolverTests");
        var inputPath = Path.Combine(root, "Data", "meshes", "foo.nif");
        var outputPath = Path.Combine(root, "exports", "glb");

        var resolved = NifExportPathResolver.ResolveOutputPath(inputPath, outputPath);

        Assert.Equal(Path.Combine(Path.GetFullPath(outputPath), "foo.glb"), resolved);
    }

    [Fact]
    public void ResolveOutputPath_PreservesExplicitGlbFilePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "NifExportPathResolverTests");
        var inputPath = Path.Combine(root, "Data", "meshes", "foo.nif");
        var outputPath = Path.Combine(root, "exports", "bar.glb");

        var resolved = NifExportPathResolver.ResolveOutputPath(inputPath, outputPath);

        Assert.Equal(Path.GetFullPath(outputPath), resolved);
    }

    [Fact]
    public void TryDetectDataRoot_FindsAncestorContainingTexturesDirectory()
    {
        using var tempDir = new TemporaryDirectory();
        var dataRoot = Path.Combine(tempDir.Path, "FalloutNV", "Data");
        var nifPath = Path.Combine(dataRoot, "meshes", "architecture", "testmesh.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(nifPath)!);
        Directory.CreateDirectory(Path.Combine(dataRoot, "textures"));
        File.WriteAllBytes(nifPath, []);

        var found = NifExportPathResolver.TryDetectDataRoot(nifPath, out var detectedDataRoot);

        Assert.True(found);
        Assert.Equal(dataRoot, detectedDataRoot);
    }

    [Fact]
    public void ResolveTextureSourcePaths_AutoDetectsDataRootWhenExplicitSourcesAreOmitted()
    {
        using var tempDir = new TemporaryDirectory();
        var dataRoot = Path.Combine(tempDir.Path, "FalloutNV", "Data");
        var nifPath = Path.Combine(dataRoot, "meshes", "architecture", "testmesh.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(nifPath)!);
        Directory.CreateDirectory(Path.Combine(dataRoot, "textures"));
        File.WriteAllBytes(nifPath, []);

        var sources = NifExportPathResolver.ResolveTextureSourcePaths(
            nifPath,
            null,
            null,
            out var error);

        Assert.Null(error);
        Assert.NotNull(sources);
        Assert.Equal([dataRoot], sources);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"NifExportPathResolverTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}