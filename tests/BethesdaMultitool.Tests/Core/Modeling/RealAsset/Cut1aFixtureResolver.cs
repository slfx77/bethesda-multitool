using System.Collections.Concurrent;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Resolves a cut-1a cover file's bytes. Candidates are tried in order and every candidate must reproduce the
///     manifest's SHA-256 or it is rejected: the manifest's own source, each <c>alsoIn</c> source, the same
///     Data-relative path through the Steam Final build's Data folder (loose files over every archive, DLC included,
///     <see cref="GameFileSystem.OpenDataFolder" />), and the installed Steam Fallout New Vegas Data folder.
/// </summary>
/// <remarks>
///     <para>
///         The manifest's first source for 92 of the 231 files is <c>Sample/Unpacked_Builds/PC_Final_Unpacked/Data/meshes</c>,
///         which no longer exists (measured 2026-09-24); 74 of them list an <c>alsoIn</c> alternative and 18 list none,
///         which is why the Data-folder fallbacks exist. The generator of the checked-in expectations
///         (<c>tools/scripts/nif_cover_expectations.py</c>) walks the same candidate order and reports 231 of 231
///         resolved (139 from the source, 74 from <c>alsoIn</c>, 18 from the Steam Final build archives).
///     </para>
///     <para>
///         The 95 big-endian files each list the other console's Meshes BSA as an <c>alsoIn</c> source under the same
///         entry, but only 27 of them are byte-identical there (measured 2026-09-24 with the probe's BSA reader: 53
///         X360-primary and 15 PS3-primary files differ on the other console, in the packed vertex-color bytes among
///         others). The digest check is what makes that harmless: the primary source reproduces the pinned bytes on
///         every one of them, and a differing copy is rejected, never read.
///     </para>
///     <para>
///         Sources spelled <c>&lt;SteamLibrary&gt;/&lt;game&gt;/&lt;path&gt;</c> resolve through
///         <see cref="RealAssetPaths.SteamGameFile" />; <c>Sample/...</c> sources through
///         <see cref="RealAssetPaths.SampleFile" /> and <see cref="RealAssetPaths.SampleDirectory" />, so the
///         <c>BETHESDA_TEST_DATA_ROOT</c> override applies. Archives and Data folders are opened once per process and
///         kept open (their tables are parsed once for the 231 theory rows); they are never disposed, which the test
///         host's exit takes care of. <see cref="DataFolder" /> and <see cref="SteamInstallDataFolder" /> expose the
///         same mounted folders to the console-twin resolution (<see cref="Cut1aConsoleTwins" />).
///     </para>
/// </remarks>
internal static class Cut1aFixtureResolver
{
    /// <summary>The Steam Final build's Data folder (Sample-relative), the first Data-folder fallback.</summary>
    public const string SteamFinalBuildData = "Builds/Fallout - New Vegas (2022-5-24, Steam - Final)/Data";

    /// <summary>The Steam game folder of the installed Fallout New Vegas, the second Data-folder fallback.</summary>
    public const string SteamGameFolder = "Fallout New Vegas";

    private const string SteamLibraryPrefix = "<SteamLibrary>/";
    private const string SamplePrefix = "Sample/";

    private static readonly ConcurrentDictionary<string, Lazy<IGameFileSystem>> Archives =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Lazy<LayeredGameFileSystem>> DataFolders =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bytes of a manifest file, or a skip naming every candidate tried when none reproduces the digest.</summary>
    public static byte[] Require(Cut1aCoverFile file)
    {
        var fixture = TryResolve(file, out var tried);
        Assert.SkipWhen(fixture is null,
            $"{file}: no candidate reproduces SHA-256 {file.Sha256}. Tried: {string.Join("; ", tried)}. " +
            RealAssetPaths.SkipMessage("the cut-1a cover corpus"));
        return fixture!.Bytes;
    }

    /// <summary>
    ///     The first candidate whose bytes reproduce the manifest SHA-256, or null with the list of candidates tried and
    ///     why each was rejected.
    /// </summary>
    public static Cut1aFixture? TryResolve(Cut1aCoverFile file, out IReadOnlyList<string> tried)
    {
        var attempts = new List<string>();
        tried = attempts;
        var step = "source";
        foreach (var candidate in file.Candidates)
        {
            var (bytes, label) = ReadCandidate(candidate);
            if (bytes is null)
            {
                attempts.Add(label);
            }
            else if (!Matches(bytes, file))
            {
                attempts.Add("SHA-256 mismatch: " + label);
            }
            else
            {
                return new Cut1aFixture(bytes, label, step);
            }

            step = "alsoIn";
        }

        foreach (var (folderStep, folder) in DataFolderFallbacks())
        {
            if (folder is null)
            {
                attempts.Add($"{folderStep}: the Data folder is not present");
                continue;
            }

            var read = folder.TryReadAllBytesBounded(file.DataRelativePath, NifModelReader.MaximumSourceBytes);
            if (read is null)
            {
                attempts.Add($"{folderStep}: {file.DataRelativePath} is in no layer of {folder.Label}");
            }
            else if (!Matches(read.Data, file))
            {
                attempts.Add($"SHA-256 mismatch: {folderStep} {file.DataRelativePath} ({read.Entry.Source})");
            }
            else
            {
                return new Cut1aFixture(read.Data, $"{folderStep} :: {file.DataRelativePath} ({read.Entry.Source})",
                    folderStep);
            }
        }

        return null;
    }

    /// <summary>
    ///     A Data folder (Sample-relative path) mounted once per process: loose files over every archive in
    ///     case-insensitive file-name order. Null when the folder is not present.
    /// </summary>
    public static LayeredGameFileSystem? DataFolder(string sampleRelativePath)
    {
        return RealAssetPaths.SampleDirectory(sampleRelativePath) is { } directory ? OpenDataFolder(directory) : null;
    }

    /// <summary>The installed Steam Fallout New Vegas Data folder, mounted once per process, or null when not installed.</summary>
    public static LayeredGameFileSystem? SteamInstallDataFolder()
    {
        return RealAssetPaths.SteamGameDirectory(SteamGameFolder, "Data") is { } data ? OpenDataFolder(data) : null;
    }

    private static IEnumerable<(string Step, LayeredGameFileSystem? Folder)> DataFolderFallbacks()
    {
        yield return ("steamFinalBuild", DataFolder(SteamFinalBuildData));
        yield return ("steamInstall", SteamInstallDataFolder());
    }

    private static LayeredGameFileSystem OpenDataFolder(string directory)
    {
        return DataFolders.GetOrAdd(Path.GetFullPath(directory),
            path => new Lazy<LayeredGameFileSystem>(() => GameFileSystem.OpenDataFolder(path))).Value;
    }

    private static (byte[]? Bytes, string Label) ReadCandidate(Cut1aCoverSource candidate)
    {
        var label = $"{candidate.Source} :: {candidate.Entry}";
        var location = Locate(candidate.Source);
        if (location is null)
        {
            return (null, "absent: " + label);
        }

        if (Directory.Exists(location))
        {
            var path = Path.Combine(location, candidate.Entry.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? (File.ReadAllBytes(path), label) : (null, "no such loose file: " + label);
        }

        if (!location.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "unsupported source kind: " + label);
        }

        var archive = Archives.GetOrAdd(Path.GetFullPath(location),
            path => new Lazy<IGameFileSystem>(() => GameFileSystem.OpenArchive(path))).Value;
        var bytes = archive.TryReadAllBytes(candidate.Entry);
        return bytes is null ? (null, "entry not found: " + label) : (bytes, label);
    }

    /// <summary>A manifest source string as a file or directory on this machine, or null.</summary>
    private static string? Locate(string source)
    {
        if (source.StartsWith(SteamLibraryPrefix, StringComparison.Ordinal))
        {
            var rest = source[SteamLibraryPrefix.Length..];
            var separator = rest.IndexOf('/', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return null;
            }

            var game = rest[..separator];
            var relative = rest[(separator + 1)..];
            return RealAssetPaths.SteamGameFile(game, relative) ?? RealAssetPaths.SteamGameDirectory(game, relative);
        }

        if (source.StartsWith(SamplePrefix, StringComparison.Ordinal))
        {
            var relative = source[SamplePrefix.Length..];
            return RealAssetPaths.SampleFile(relative) ?? RealAssetPaths.SampleDirectory(relative);
        }

        return File.Exists(source) || Directory.Exists(source) ? source : null;
    }

    private static bool Matches(byte[] bytes, Cut1aCoverFile file)
    {
        return bytes.LongLength == file.Size &&
               string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.Ordinal);
    }

    /// <summary>The digest of arbitrary bytes, lowercase, as the manifest and expectations spell it.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
