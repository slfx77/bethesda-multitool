using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Vfs;

/// <summary>
///     <see cref="PrefixedFileSystem" /> mounts a loose directory under a name — what lets the
///     Battlespire CD's <c>videos\</c> (beside <c>GAME.EXE</c>, outside the <c>GAMEDATA</c> loose
///     root) surface as <c>videos\ANCHORS.SMK</c> in a game-root mount.
/// </summary>
public sealed class PrefixedFileSystemTests
{
    [Fact]
    public void EntriesSurfaceUnderThePrefixAndResolveThroughIt()
    {
        var root = Directory.CreateTempSubdirectory("prefixed-vfs-");
        try
        {
            File.WriteAllBytes(Path.Combine(root.FullName, "ANCHORS.SMK"), [1, 2, 3]);
            Directory.CreateDirectory(Path.Combine(root.FullName, "sub"));
            File.WriteAllBytes(Path.Combine(root.FullName, "sub", "x.bin"), [9]);

            using var fs = new PrefixedFileSystem(new LooseFileSystem(root.FullName), "videos");

            var paths = fs.EnumerateFiles().Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal([@"videos\ANCHORS.SMK", @"videos\sub\x.bin"], paths);

            Assert.True(fs.Exists(@"videos\ANCHORS.SMK"));
            Assert.True(fs.Exists("videos/anchors.smk"));
            Assert.False(fs.Exists("ANCHORS.SMK"));
            Assert.Null(fs.TryReadAllBytes("ANCHORS.SMK"));
            Assert.Equal([1, 2, 3], fs.TryReadAllBytes(@"videos\ANCHORS.SMK"));
            Assert.Equal(3, fs.TryStat(@"videos\ANCHORS.SMK")!.Size);
            Assert.Equal(@"videos\ANCHORS.SMK", fs.TryStat(@"videos\ANCHORS.SMK")!.Path);

            var bounded = fs.TryReadAllBytesBounded(@"videos\sub\x.bin", 16);
            Assert.NotNull(bounded);
            Assert.Equal(@"videos\sub\x.bin", bounded.Entry.Path);

            Assert.Single(fs.EnumerateFiles(@"videos\sub"));
            Assert.Equal(2, fs.EnumerateFiles("vid").Count());
            Assert.Empty(fs.EnumerateFiles("other"));

            var page = fs.EnumerateFilesBounded(null, 1);
            Assert.Single(page.Entries);
            Assert.True(page.IsTruncated);
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void GameRootMountAddsExtraLooseDirectoriesUnderTheirOwnNames()
    {
        var root = Directory.CreateTempSubdirectory("classic-root-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "GAMEDATA"));
            File.WriteAllBytes(Path.Combine(root.FullName, "GAMEDATA", "A.TXT"), [1]);
            Directory.CreateDirectory(Path.Combine(root.FullName, "videos"));
            File.WriteAllBytes(Path.Combine(root.FullName, "videos", "JUMP.SMK"), [2]);

            var profile = new BethesdaMultitool.Core.Games.GameProfile
            {
                Game = BethesdaMultitool.Core.Games.BethesdaGame.Battlespire,
                Engine = BethesdaMultitool.Core.Games.EngineFamily.None,
                RecordHeaderSize = 0,
                GroupHeaderSize = 0,
                HasRecordVersionTrailer = false,
                ClassicLooseRoot = "GAMEDATA",
                ClassicExtraLooseDirectories = ["videos", "missing"],
                // Every profile states its unit (required); this test is about mounts, so it borrows the
                // registry's Battlespire value rather than inventing one.
                Units = BethesdaMultitool.Core.Games.GameProfiles.For(BethesdaMultitool.Core.Games.BethesdaGame.Battlespire).Units
            };

            using var fs = GameFileSystem.OpenGameRoot(profile, root.FullName);
            var paths = fs.EnumerateFiles().Select(e => e.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

            Assert.Equal(["A.TXT", @"videos\JUMP.SMK"], paths);
            Assert.Equal([2], fs.TryReadAllBytes(@"videos\JUMP.SMK"));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
