using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.DiscImage;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the original redump media for Redguard and
///     Battlespire, stored as CHD under <c>../Media/</c> and read natively. Measured 2026-09-05:
///     Redguard's Disc 1 is an InstallShield CD — its ISO9660 tree is <c>DATA1.CAB</c> + <c>DATA.TAG</c>
///     + Voodoo drivers, so the game files (including the 3dfx <c>fxart</c> the Steam build omits)
///     sit INSIDE the cabinet, which the ISO layer cannot see; Disc 2 is eleven Smacker movies plus
///     six Redbook tracks; the Battlespire disc is its data track plus seven Redbook tracks.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ClassicDiscImageRetailTests
{
    /// <summary>
    ///     The disc image for one pre-migration staging name: the stored <c>.chd</c> when there is
    ///     one, else a raw <c>.cue</c>.
    ///     <para>
    ///         ⚠ The CHD is opened DIRECTLY, never materialised: <c>chdman extractcd</c> writes one
    ///         <c>.bin</c> for the whole disc, and a single-file cue contributes ONE track region, so
    ///         a redump's Redbook tracks disappear (measured 2026-09-09: 0 audio tracks instead of 6
    ///         and 7). The CHD carries a metadata record per track, which is where they come from.
    ///     </para>
    /// </summary>
    private static string RequireDisc(string stagedDirectory)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        // The staged names are the pre-migration ones. SampleCorpus maps each onto its build
        // directory; the media itself is original media, so it now sits under the matching
        // shared ../Media path; the build holds the extracted tree.
        var directory = SampleCorpus.Candidates(Path.Combine("Full_Builds", stagedDirectory))
            .SelectMany(relative => new[]
            {
                Path.GetFullPath(Path.Combine(RepositoryRoot(), "..", MediaRelative(relative))),
                Path.Combine(RepositoryRoot(), "Sample", relative)
            })
            .FirstOrDefault(candidate => Directory.Exists(candidate) &&
                                         (Directory.GetFiles(candidate, "*.chd").Length > 0 ||
                                          Directory.GetFiles(candidate, "*.cue").Length > 0));
        var image = directory is null
            ? null
            : Directory.GetFiles(directory, "*.chd").FirstOrDefault() ??
              Directory.GetFiles(directory, "*.cue").FirstOrDefault();

        Assert.SkipWhen(image is null, RealAssetPaths.SkipMessage($"{stagedDirectory} disc image"));
        return image;
    }

    /// <summary>
    ///     The <c>../Media</c> counterpart of a <c>Sample/Builds</c> relative path. Media keeps
    ///     the build's directory name, with its root resolved by the caller.
    /// </summary>
    private static string MediaRelative(string buildRelative)
    {
        return buildRelative.StartsWith(@"Builds\", StringComparison.OrdinalIgnoreCase)
            ? @"Media\" + buildRelative[@"Builds\".Length..]
            : buildRelative;
    }

    /// <summary>The checkout root: the nearest ancestor of the test binary holding the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BethesdaMultitool.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new InvalidOperationException("BethesdaMultitool.slnx not found above the test binary.");
    }

    [Fact]
    public void RedguardDisc1IsAnInstallShieldCabinetNotALooseTree()
    {
        using var disc = ArchiveReader.Open(RequireDisc("Redguard_Disc1"));

        Assert.StartsWith("CD image (", disc.FormatName, StringComparison.Ordinal);
        var entries = disc.ListFiles();
        Assert.Equal(28, entries.Count);

        // The whole install lives inside one InstallShield cabinet; nothing of the game tree is
        // a loose ISO9660 file, so a TEXBSI/WORLD.INI search here must find nothing.
        var cabinet = Assert.Single(entries, e => e.Name.Equals("DATA1.CAB", StringComparison.OrdinalIgnoreCase));
        Assert.True(cabinet.Size > 290_000_000, $"DATA1.CAB is {cabinet.Size:N0} bytes");
        Assert.Contains(entries, e => e.Name.Equals("DATA.TAG", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Name.Contains("TEXBSI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Name.Equals("WORLD.INI", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.FolderPath == "audio");
    }

    [Fact]
    public void RedguardDisc2IsMoviesPlusSixRedbookTracks()
    {
        using var disc = ArchiveReader.Open(RequireDisc("Redguard_Disc2"));

        var entries = disc.ListFiles();
        var movies = entries.Count(e => e.Extension.Equals(".smk", StringComparison.OrdinalIgnoreCase));
        var audio = entries.Where(e => e.FolderPath == "audio").Select(e => e.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        // The redump set is one data track plus tracks 2-7 of Redbook music.
        Assert.Equal(11, movies);
        Assert.Equal(6, audio.Count);
        Assert.Equal("track02.wav", audio[0]);
        Assert.Equal("track07.wav", audio[^1]);

        // A Smacker movie opens with its "SMK2" tag when read through the raw sectors.
        var movie = entries.First(e => e.Extension.Equals(".smk", StringComparison.OrdinalIgnoreCase));
        var head = disc.ReadFile(movie.FullPath)!.AsSpan(0, 4);
        Assert.True(head.SequenceEqual("SMK2"u8) || head.SequenceEqual("SMK4"u8),
            $"{movie.Name} does not open with a Smacker tag");
    }

    [Fact]
    public void BattlespireDiscCarriesSevenRedbookTracks()
    {
        using var disc = ArchiveReader.Open(RequireDisc("Battlespire_Disc"));

        // One data track plus tracks 2-8 of Redbook music.
        var audio = disc.ListFiles().Where(e => e.FolderPath == "audio").ToList();
        Assert.Equal(7, audio.Count);

        // Every track is whole 2352-byte sectors plus the 44-byte WAV header.
        Assert.All(audio, e => Assert.Equal(0, (e.Size - 44) % 2352));
    }
}
