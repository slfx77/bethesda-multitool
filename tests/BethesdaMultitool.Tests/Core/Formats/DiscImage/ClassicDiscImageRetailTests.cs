using System;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.DiscImage;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the original redump media for Redguard and
///     Battlespire, staged as CUE/BIN under <c>Sample/Full_Builds/</c>. Measured 2026-09-05:
///     Redguard's Disc 1 is an InstallShield CD — its ISO9660 tree is <c>DATA1.CAB</c> + <c>DATA.TAG</c>
///     + Voodoo drivers, so the game files (including the 3dfx <c>fxart</c> the Steam build omits)
///     sit INSIDE the cabinet, which the ISO layer cannot see; Disc 2 is eleven Smacker movies plus
///     six Redbook tracks; the Battlespire disc is its data track plus seven Redbook tracks.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ClassicDiscImageRetailTests
{
    private static string RequireCue(string stagedDirectory)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var directory = Path.Combine(RepositoryRoot(), "Sample", "Full_Builds", stagedDirectory);
        var cue = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.cue").FirstOrDefault() : null;
        Assert.SkipWhen(cue is null, RealAssetPaths.SkipMessage($"{stagedDirectory} CUE/BIN"));
        return cue!;
    }

    /// <summary>The checkout root: the nearest ancestor of the test binary holding the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BethesdaMultitool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("BethesdaMultitool.slnx not found above the test binary.");
    }

    [Fact]
    public void RedguardDisc1IsAnInstallShieldCabinetNotALooseTree()
    {
        using var disc = ArchiveReader.Open(RequireCue("Redguard_Disc1"));

        Assert.Equal("CD image (CUE/BIN)", disc.FormatName);
        var entries = disc.ListFiles();
        Assert.Equal(28, entries.Count);

        // The whole install lives inside one InstallShield cabinet; nothing of the game tree is
        // a loose ISO9660 file, so a TEXBSI/WORLD.INI search here must find nothing.
        var cabinet = Assert.Single(entries, e => e.Name.Equals("DATA1.CAB", StringComparison.OrdinalIgnoreCase));
        Assert.True(cabinet.Size > 290_000_000, $"DATA1.CAB is {cabinet.Size:N0} bytes");
        Assert.Contains(entries, e => e.Name.Equals("DATA.TAG", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Name.Contains("TEXBSI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Name.Equals("WORLD.INI", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(entries.Where(e => e.FolderPath == "audio"));
    }

    [Fact]
    public void RedguardDisc2IsMoviesPlusSixRedbookTracks()
    {
        using var disc = ArchiveReader.Open(RequireCue("Redguard_Disc2"));

        var entries = disc.ListFiles();
        var movies = entries.Count(e => e.Extension.Equals(".smk", StringComparison.OrdinalIgnoreCase));
        var audio = entries.Where(e => e.FolderPath == "audio").Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        // The redump set is one data track plus tracks 2-7 of Redbook music.
        Assert.Equal(11, movies);
        Assert.Equal(6, audio.Count);
        Assert.Equal("track02.wav", audio[0]);
        Assert.Equal("track07.wav", audio[^1]);

        // A Smacker movie opens with its "SMK2" tag when read through the raw sectors.
        var movie = entries.First(e => e.Extension.Equals(".smk", StringComparison.OrdinalIgnoreCase));
        var head = disc.ReadFile(movie.FullPath)!.AsSpan(0, 4);
        Assert.True(head.SequenceEqual("SMK2"u8) || head.SequenceEqual("SMK4"u8), $"{movie.Name} does not open with a Smacker tag");
    }

    [Fact]
    public void BattlespireDiscCarriesSevenRedbookTracks()
    {
        using var disc = ArchiveReader.Open(RequireCue("Battlespire_Disc"));

        // One data track plus tracks 2-8 of Redbook music.
        var audio = disc.ListFiles().Where(e => e.FolderPath == "audio").ToList();
        Assert.Equal(7, audio.Count);

        // Every track is whole 2352-byte sectors plus the 44-byte WAV header.
        Assert.All(audio, e => Assert.Equal(0, (e.Size - 44) % 2352));
    }
}
