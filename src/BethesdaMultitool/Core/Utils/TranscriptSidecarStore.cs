namespace BethesdaMultitool.Core.Utils;

/// <summary>
///     Where the audio transcriber keeps the sidecars it produces for one game Data directory:
///     the transcription project and the suspected-typo review file. They are never written into
///     the Data directory itself — a game install or a corpus build holds the game's files and
///     nothing else — so each Data directory maps to a directory of its own outside it:
///     <list type="number">
///         <item>
///             <description>
///                 under <see cref="RootVariable" /> when that environment variable is set;
///             </description>
///         </item>
///         <item>
///             <description>
///                 for a directory inside a corpus sample tree,
///                 <c>…/Sample/Builds/&lt;build&gt;/&lt;rest&gt;</c>, the sibling
///                 <c>…/Sample/Transcripts/&lt;build&gt;/&lt;rest&gt;</c>, so the work travels with the corpus
///                 and survives a build being regenerated;
///             </description>
///         </item>
///         <item>
///             <description>
///                 otherwise under <c>%LOCALAPPDATA%/BethesdaAudioTranscriber/Transcripts</c>.
///             </description>
///         </item>
///     </list>
///     Rules 1 and 3 mirror the Data directory's full path beneath the root (drive letter first), so
///     the location is predictable by hand and by <c>tools/scripts/transcript_typo_check.py</c>, which
///     implements the same mapping. Sidecars written into a Data directory by earlier versions are
///     still read, as a fallback, and are never modified or deleted.
/// </summary>
public static class TranscriptSidecarStore
{
    /// <summary>The transcription project sidecar.</summary>
    public const string TranscriptFileName = ".fnvtranscript.json";

    /// <summary>The suspected-typo review sidecar.</summary>
    public const string ReviewFileName = ".fnvreview.json";

    /// <summary>Environment variable naming an explicit store root.</summary>
    public const string RootVariable = "BETHESDA_TRANSCRIPT_STORE";

    private const string CorpusSampleDirectory = "Sample";
    private const string CorpusBuildsDirectory = "Builds";
    private const string CorpusTranscriptsDirectory = "Transcripts";

    /// <summary>The directory that holds the sidecars for <paramref name="dataDirectory" />.</summary>
    public static string ResolveDirectory(string dataDirectory)
    {
        return ResolveDirectory(
            dataDirectory,
            Environment.GetEnvironmentVariable(RootVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>The path a sidecar is written to. The directory is not created.</summary>
    public static string PathFor(string dataDirectory, string fileName)
    {
        return Path.Combine(ResolveDirectory(dataDirectory), fileName);
    }

    /// <summary>
    ///     The sidecar to read: the store's copy, else one an earlier version left in the Data
    ///     directory, else null.
    /// </summary>
    public static string? FindExisting(string dataDirectory, string fileName)
    {
        var stored = PathFor(dataDirectory, fileName);
        if (File.Exists(stored))
        {
            return stored;
        }

        var legacy = Path.Combine(dataDirectory, fileName);
        return File.Exists(legacy) ? legacy : null;
    }

    internal static string ResolveDirectory(string dataDirectory, string? overrideRoot, string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));

        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.Combine(Path.GetFullPath(overrideRoot), MirrorKey(full));
        }

        return TryResolveInCorpus(full) ??
               Path.Combine(localApplicationData, "BethesdaAudioTranscriber", "Transcripts", MirrorKey(full));
    }

    /// <summary>
    ///     Maps <c>…/Sample/Builds/&lt;build&gt;/&lt;rest&gt;</c> to <c>…/Sample/Transcripts/&lt;build&gt;/&lt;rest&gt;</c>,
    ///     using the nearest such ancestor. The Builds directory itself is not inside a build.
    /// </summary>
    private static string? TryResolveInCorpus(string full)
    {
        for (var builds = Path.GetDirectoryName(full); builds is not null; builds = Path.GetDirectoryName(builds))
        {
            var sample = Path.GetDirectoryName(builds);
            if (sample is null ||
                !Path.GetFileName(builds).Equals(CorpusBuildsDirectory, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(sample).Equals(CorpusSampleDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Path.Combine(sample, CorpusTranscriptsDirectory, Path.GetRelativePath(builds, full));
        }

        return null;
    }

    /// <summary>
    ///     A rooted path as a relative one: <c>E:\Games\Data</c> becomes <c>E\Games\Data</c> and
    ///     <c>\\host\share\Data</c> becomes <c>UNC\host\share\Data</c>.
    /// </summary>
    private static string MirrorKey(string full)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var rest = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var drive = root.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (drive.EndsWith(':'))
        {
            drive = drive[..^1];
        }
        else if (root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            drive = Path.Combine("UNC", drive);
        }

        return drive.Length == 0 ? rest : Path.Combine(drive, rest);
    }
}
