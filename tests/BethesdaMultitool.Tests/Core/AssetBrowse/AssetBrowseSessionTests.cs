using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Bsa;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Pins the <see cref="AssetBrowseSession" /> lifecycle: factory-built trees over a loose
///     directory and a synthetic BSA, filesystem ownership on dispose, and double-dispose safety.
/// </summary>
public sealed class AssetBrowseSessionTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "assetbrowse-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; leaked temp dirs are harmless.
        }
    }

    [Fact]
    public void OpenFolder_BuildsExpectedTree_AndDoubleDisposeIsNoOp()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "textures"));
            File.WriteAllBytes(Path.Combine(dir, "textures", "a.dds"), new byte[16]);
            File.WriteAllBytes(Path.Combine(dir, "readme.txt"), new byte[5]);

            var session = AssetBrowseSession.OpenFolder(dir);
            try
            {
                Assert.Equal(Path.GetFullPath(dir), session.SourcePath);
                Assert.Equal(Path.GetFileName(dir), session.SourceLabel);
                Assert.Equal(session.SourceLabel, session.Root.Name);
                Assert.Equal(AssetNodeKind.Folder, session.Root.Kind);

                Assert.Equal(2, session.Root.Children.Count);
                var textures = session.Root.Children[0]; // folders sort first
                Assert.Equal("textures", textures.Name);
                Assert.Equal(AssetNodeKind.Folder, textures.Kind);
                var dds = Assert.Single(textures.Children);
                Assert.Equal("a.dds", dds.Name);
                Assert.Equal(AssetNodeKind.Texture, dds.Kind);
                Assert.Equal(16L, dds.Size);
                Assert.Equal(@"textures\a.dds", dds.VirtualPath);

                var readme = session.Root.Children[1];
                Assert.Equal("readme.txt", readme.Name);
                Assert.Equal(AssetNodeKind.Text, readme.Kind);
                Assert.Equal(5L, readme.Size);

                Assert.True(session.FileSystem.Exists("textures/a.dds"));
            }
            finally
            {
                session.Dispose();
                session.Dispose(); // double-dispose must not throw
            }
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void OpenArchive_BuildsTreeFromSyntheticBsa()
    {
        var dir = NewTempDir();
        try
        {
            var bsaPath = Path.Combine(dir, "browse.bsa");
            using (var writer = new BsaWriter(compressFiles: false))
            {
                writer.AddFile(@"meshes\clutter\bucket.nif", new byte[32]);
                writer.AddFile(@"textures\clutter\bucket.dds", new byte[48]);
                writer.Write(bsaPath);
            }

            using var session = AssetBrowseSession.OpenArchive(bsaPath);

            Assert.Equal("browse.bsa", session.SourceLabel);
            Assert.Equal(Path.GetFullPath(bsaPath), session.SourcePath);
            Assert.Equal(new[] { "meshes", "textures" },
                session.Root.Children.Select(n => n.Name).ToArray());

            var meshes = session.Root.Children[0];
            var clutter = Assert.Single(meshes.Children);
            var nif = Assert.Single(clutter.Children);
            Assert.Equal("bucket.nif", nif.Name);
            Assert.Equal(AssetNodeKind.Model, nif.Kind);
            Assert.Equal(32L, nif.Size);

            Assert.Equal(32, session.FileSystem.TryReadAllBytes(@"meshes\clutter\bucket.nif")!.Length);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void Dispose_DisposesOwnedFileSystemExactlyOnce()
    {
        var fake = new FakeGameFileSystem(("a.dds", 4));
        var root = AssetTreeBuilder.Build(fake, "fake");
        var session = new AssetBrowseSession(fake, "fake", "fake", root);

        Assert.Equal(0, fake.DisposeCount);
        session.Dispose();
        Assert.Equal(1, fake.DisposeCount);
        session.Dispose();
        Assert.Equal(1, fake.DisposeCount); // second dispose must not re-dispose the filesystem
        Assert.Same(root, session.Root);    // the tree stays readable after dispose
    }
}
