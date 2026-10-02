using System.Security.Cryptography;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Scripts;

/// <summary>What kind of input a script export was read from.</summary>
public enum ScriptExportSourceKind
{
    /// <summary>A plugin file (ESM/ESP): its SCTX is the text the GECK saved.</summary>
    Plugin,

    /// <summary>
    ///     A memory dump: a partial capture, so anything missing from it proves nothing about the build.
    /// </summary>
    MemoryDump
}

/// <summary>
///     Identity of the file a script export was read from, recorded in the manifest's <c>source</c> object
///     and summarized in each decompiled file's banner. Built by <see cref="Describe" />, or directly by a
///     caller (tests) that already knows the values.
/// </summary>
public sealed record ScriptExportSource
{
    /// <summary>The manifest's <c>capture</c> value for a memory dump.</summary>
    public const string PartialMemoryDumpCapture = "partial-memory-dump";

    /// <summary>The manifest's absence note for a memory dump.</summary>
    public const string PartialCaptureNote =
        "Scripts, source text and bytecode missing from this export are " +
        ScriptSourceProvenance.PartialDumpAbsenceWording + ".";

    /// <summary><see cref="BuildLabelSource" /> when the label came from the caller (the CLI's --build-label).</summary>
    public const string UserBuildLabelSource = "user";

    /// <summary>
    ///     <see cref="BuildLabelSource" /> when the label is the name of the nearest
    ///     <c>Sample/Builds/&lt;build&gt;</c> directory above the file.
    /// </summary>
    public const string CorpusPathBuildLabelSource = "corpus-path";

    /// <summary>How much of a plugin's start is read to parse its TES4 header (masters, version, byte order).</summary>
    private const int PluginHeaderPrefixBytes = 1024 * 1024;

    /// <summary>Full path of the input file.</summary>
    public required string Path { get; init; }

    /// <summary>The input's file name.</summary>
    public required string FileName { get; init; }

    /// <summary>Plugin or memory dump.</summary>
    public required ScriptExportSourceKind Kind { get; init; }

    /// <summary>The game the records were parsed as.</summary>
    public BethesdaGame Game { get; init; }

    /// <summary>Size of the input in bytes, or null when it was not measured.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Lower-case hex SHA-256 of the whole input, or null when it was not computed.</summary>
    public string? Sha256 { get; init; }

    /// <summary>
    ///     Byte order of the record container: <c>big</c> for an Xbox 360 plugin or dump, <c>little</c>
    ///     otherwise, null when unknown. This is not the bytecode's order; each script records that itself.
    /// </summary>
    public string? ContainerEndianness { get; init; }

    /// <summary>A human build label, or null when none is known.</summary>
    public string? BuildLabel { get; init; }

    /// <summary>
    ///     Where <see cref="BuildLabel" /> came from: <see cref="UserBuildLabelSource" /> or
    ///     <see cref="CorpusPathBuildLabelSource" />; null when there is no label.
    /// </summary>
    public string? BuildLabelSource { get; init; }

    /// <summary>A plugin's TES4 HEDR version, or null (dumps, unreadable headers).</summary>
    public float? PluginVersion { get; init; }

    /// <summary>A plugin's MAST list in header order; empty for dumps.</summary>
    public IReadOnlyList<string> PluginMasters { get; init; } = [];

    /// <summary>A memory dump's build type as the analyzer reported it (e.g. Release Beta), or null.</summary>
    public string? DumpBuildType { get; init; }

    /// <summary>The game executable module named in a memory dump, or null.</summary>
    public string? DumpGameModule { get; init; }

    /// <summary>That module's PE TimeDateStamp as UTC, or null when absent or zero.</summary>
    public DateTimeOffset? DumpGameModuleTimeDateStampUtc { get; init; }

    /// <summary>Whether the input is a memory dump.</summary>
    public bool IsMemoryDump => Kind == ScriptExportSourceKind.MemoryDump;

    /// <summary>
    ///     Describes an input file. Reads only what it needs: the file is hashed as a stream, and for a plugin
    ///     only the start of the file is read to parse its TES4 header. Never loads the whole file.
    /// </summary>
    /// <param name="path">The input file.</param>
    /// <param name="raw">
    ///     The format analysis of that file, when available: a memory dump's build type and game module come
    ///     from it. Unused for plugins.
    /// </param>
    /// <param name="game">The game the records were parsed as.</param>
    /// <param name="isMemoryDump">True for a memory dump, false for a plugin.</param>
    /// <param name="userBuildLabel">
    ///     A label the user supplied. When blank, the nearest <c>Sample/Builds/&lt;build&gt;</c> directory above
    ///     the file names the build, if there is one.
    /// </param>
    /// <param name="computeSha256">False skips hashing (the manifest then records a null hash).</param>
    public static ScriptExportSource Describe(
        string path,
        AnalysisResult? raw,
        BethesdaGame game,
        bool isMemoryDump,
        string? userBuildLabel,
        bool computeSha256 = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(fullPath);

        var (buildLabel, buildLabelSource) = ResolveBuildLabel(fullPath, userBuildLabel);
        var source = new ScriptExportSource
        {
            Path = fullPath,
            FileName = info.Name,
            Kind = isMemoryDump ? ScriptExportSourceKind.MemoryDump : ScriptExportSourceKind.Plugin,
            Game = game,
            SizeBytes = info.Exists ? info.Length : null,
            Sha256 = computeSha256 && info.Exists ? ComputeSha256(fullPath) : null,
            BuildLabel = buildLabel,
            BuildLabelSource = buildLabelSource
        };

        return isMemoryDump ? WithDumpIdentity(source, raw) : WithPluginHeader(source, info);
    }

    /// <summary>
    ///     The name of the nearest <c>…/Sample/Builds/&lt;build&gt;</c> directory above
    ///     <paramref name="fullPath" />, or null. The same corpus rule the transcript sidecar store uses.
    /// </summary>
    internal static string? TryGetCorpusBuildName(string fullPath)
    {
        for (var directory = System.IO.Path.GetDirectoryName(fullPath);
             directory is not null;
             directory = System.IO.Path.GetDirectoryName(directory))
        {
            var builds = System.IO.Path.GetDirectoryName(directory);
            var sample = builds is null ? null : System.IO.Path.GetDirectoryName(builds);
            if (builds is null || sample is null)
            {
                return null;
            }

            if (System.IO.Path.GetFileName(builds).Equals("Builds", StringComparison.OrdinalIgnoreCase)
                && System.IO.Path.GetFileName(sample).Equals("Sample", StringComparison.OrdinalIgnoreCase))
            {
                return System.IO.Path.GetFileName(directory);
            }
        }

        return null;
    }

    private static (string? Label, string? Source) ResolveBuildLabel(string fullPath, string? userBuildLabel)
    {
        if (!string.IsNullOrWhiteSpace(userBuildLabel))
        {
            return (userBuildLabel.Trim(), UserBuildLabelSource);
        }

        var corpusBuild = TryGetCorpusBuildName(fullPath);
        return corpusBuild is null ? (null, null) : (corpusBuild, CorpusPathBuildLabelSource);
    }

    private static string ComputeSha256(string fullPath)
    {
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static ScriptExportSource WithPluginHeader(ScriptExportSource source, FileInfo info)
    {
        if (!info.Exists)
        {
            return source;
        }

        var prefixLength = (int)Math.Min(info.Length, PluginHeaderPrefixBytes);
        var prefix = new byte[prefixLength];
        using (var stream = new FileStream(
                   info.FullName,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            stream.ReadExactly(prefix);
        }

        var header = EsmParser.ParseFileHeader(prefix);
        if (header is null)
        {
            return source;
        }

        return source with
        {
            ContainerEndianness = header.IsBigEndian ? "big" : "little",
            PluginVersion = header.Version,
            PluginMasters = [.. header.Masters]
        };
    }

    private static ScriptExportSource WithDumpIdentity(ScriptExportSource source, AnalysisResult? raw)
    {
        var minidump = raw?.MinidumpInfo;
        var gameModule = minidump?.FindGameModule();
        return source with
        {
            ContainerEndianness = minidump is null ? null : minidump.IsXbox360 ? "big" : "little",
            DumpBuildType = raw?.BuildType,
            DumpGameModule = gameModule?.Name,
            DumpGameModuleTimeDateStampUtc = gameModule is { TimeDateStamp: > 0 } module
                ? DateTimeOffset.FromUnixTimeSeconds(module.TimeDateStamp)
                : null
        };
    }
}
