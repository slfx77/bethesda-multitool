using System.Globalization;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Scripts;

/// <summary>
///     Writes scripts as individual files plus one <c>scripts.manifest.json</c>.
///     <list type="bullet">
///         <item>
///             <description>
///                 Stored source (plugin SCTX, or SCTX recovered from a memory dump, per
///                 <see cref="ScriptSourceProvenance.AuthoredText" />) goes to <c>{stem}{ext}</c> VERBATIM:
///                 the Windows-1252 bytes <see cref="EsmStringUtils.EncodeGameText" /> gives back (the exact
///                 inverse of the decoder that read SCTX), no BOM, line endings as stored, nothing added or
///                 wrapped. Only one trailing NUL, if the text carries one, is dropped, and the manifest says so.
///             </description>
///         </item>
///         <item>
///             <description>
///                 A decompiled reconstruction goes to <c>{stem}.decompiled{ext}</c>, rendered from the record's
///                 own <see cref="ScriptRecord.DecompiledText" /> by
///                 <see
///                     cref="CapturedScriptEmissionContract.BuildDecompiledSource(string?, IReadOnlyList{ScriptVariableInfo}, string?, IReadOnlyList{string})" />
///                 behind an export banner (plugin or dump, FormID, build label, bytecode byte order), with CRLF
///                 line endings. It is NEVER taken from a <see cref="ScriptSourceTextOrigin.DecompiledFromBytecode" />
///                 <see cref="ScriptRecord.SourceText" />: that text is a dump-emission substitute carrying the
///                 <c>dmp to-esm</c> banner, and exporting it would present a reconstruction as captured source.
///             </description>
///         </item>
///     </list>
///     Every target path is planned first. Unless <see cref="ScriptExportOptions.Overwrite" /> is set, the
///     export fails with <see cref="ScriptExportConflictException" /> before writing anything when any of them
///     (the manifest included) already exists.
/// </summary>
internal static class ScriptExportWriter
{
    /// <summary>The manifest written beside the script files.</summary>
    internal const string ManifestFileName = "scripts.manifest.json";

    /// <summary>Skip reason: no stored source text and no bytecode.</summary>
    internal const string NoSourceOrBytecodeSkipReason = "no-source-or-bytecode";

    /// <summary>Skip reason: bytecode is present but was not decompiled (unsupported game or decoder failure).</summary>
    internal const string BytecodeNotDecompiledSkipReason = "bytecode-not-decompiled";

    /// <summary>Skip reason: a decompilation exists but <see cref="ScriptDecompiledPolicy.None" /> was requested.</summary>
    internal const string DecompiledNotRequestedSkipReason = "decompiled-not-requested";

    /// <summary>
    ///     Plans and writes the export. Returns what was written; throws
    ///     <see cref="ScriptExportConflictException" /> (nothing written) when a target exists and overwriting
    ///     is off.
    /// </summary>
    /// <param name="scripts">The scripts to export, in the order the manifest lists them.</param>
    /// <param name="source">Identity of the input file, for the manifest and the decompiled banners.</param>
    /// <param name="options">Extension, decompiled-file policy and overwrite switch.</param>
    /// <param name="outputDirectory">Directory to write into; created when missing.</param>
    /// <param name="resolveEditorId">Names referenced FormIDs in the manifest; null leaves them unnamed.</param>
    /// <param name="createdUtc">Recorded as the manifest's <c>createdUtc</c>; injected so output is reproducible.</param>
    internal static ScriptExportSummary Write(
        IReadOnlyList<ScriptRecord> scripts,
        ScriptExportSource source,
        ScriptExportOptions options,
        string outputDirectory,
        Func<uint, string?>? resolveEditorId,
        DateTimeOffset createdUtc)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var extension = ScriptExportFileNamer.NormalizeExtension(options.Extension);
        var names = ScriptExportFileNamer.PlanStems(scripts);
        var directory = Path.GetFullPath(outputDirectory);
        var manifestPath = Path.Combine(directory, ManifestFileName);

        var plans = new PlannedScript[scripts.Count];
        for (var i = 0; i < scripts.Count; i++)
        {
            plans[i] = Plan(scripts[i], names[i], source, options.Decompiled, extension);
        }

        EnsureTargetsAreFree(directory, manifestPath, plans, options.Overwrite);

        Directory.CreateDirectory(directory);
        var entries = new List<ScriptExportEntry>(plans.Length);
        var storedFiles = 0;
        var capturedFiles = 0;
        var decompiledFiles = 0;
        var skipped = 0;
        foreach (var plan in plans)
        {
            var files = new List<ScriptExportFile>(plan.Files.Count);
            foreach (var file in plan.Files)
            {
                WriteBytes(Path.Combine(directory, file.Description.FileName), file.Bytes, options.Overwrite);
                files.Add(file.Description);
                if (file.Description.Content == ScriptExportContent.DecompiledFromScda)
                {
                    decompiledFiles++;
                }
                else if (file.Description.Provenance == ScriptSourceProvenance.PluginRecordToken)
                {
                    storedFiles++;
                }
                else
                {
                    capturedFiles++;
                }
            }

            if (plan.SkipReason is not null)
            {
                skipped++;
            }

            entries.Add(new ScriptExportEntry(
                plan.Script,
                plan.Name.Stem,
                plan.Name.Adjustments,
                plan.Classification,
                files,
                plan.SkipReason));
        }

        var summary = new ScriptExportSummary
        {
            OutputDirectory = directory,
            ManifestPath = manifestPath,
            Extension = extension,
            ScriptCount = scripts.Count,
            StoredSourceFiles = storedFiles,
            CapturedSourceFiles = capturedFiles,
            DecompiledFiles = decompiledFiles,
            SkippedScripts = skipped,
            Entries = entries
        };

        using (var stream = OpenForWrite(manifestPath, options.Overwrite))
        {
            ScriptExportManifestWriter.Write(stream, summary, source, options, createdUtc, resolveEditorId);
        }

        return summary;
    }

    /// <summary>Line-ending token for a text: <c>crlf</c>, <c>lf</c>, <c>cr</c>, <c>mixed</c> or <c>none</c>.</summary>
    internal static string DetectLineEndings(string text)
    {
        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        return kinds switch
        {
            0 => "none",
            > 1 => "mixed",
            _ => crlf > 0 ? "crlf" : lf > 0 ? "lf" : "cr"
        };
    }

    /// <summary>The banner a decompiled file opens with. Every line is a <c>;</c> comment.</summary>
    internal static IReadOnlyList<string> BuildDecompiledBanner(ScriptRecord script, ScriptExportSource source)
    {
        var origin = source.IsMemoryDump
            ? $"Memory dump: {BannerValue(source.FileName)} (partial capture)"
            : $"Plugin: {BannerValue(source.FileName)}";
        var build = source.BuildLabel is { } label ? BannerValue(label) : "(no build label)";
        var byteOrder = script.IsBigEndianBytecode ? "big-endian" : "little-endian";
        var evidence = ScriptExportManifestWriter.FormatByteOrderEvidence(script.BytecodeByteOrderEvidence);
        return
        [
            CapturedScriptEmissionContract.DecompiledEmissionBannerFirstLine,
            "; Declarations: SLSD/SCVR and SCRV.",
            $"; {origin}",
            string.Create(CultureInfo.InvariantCulture, $"; FormID: 0x{script.FormId:X8}"),
            $"; Build: {build}",
            $"; Bytecode byte order: {byteOrder} ({evidence})",
            $"; Provenance and hashes: {ManifestFileName}"
        ];
    }

    private static PlannedScript Plan(
        ScriptRecord script,
        ScriptExportName name,
        ScriptExportSource source,
        ScriptDecompiledPolicy policy,
        string extension)
    {
        var classification = ScriptSourceProvenance.Classify(script, source.IsMemoryDump);
        var authored = ScriptSourceProvenance.AuthoredText(script);
        var files = new List<PlannedFile>(2);

        if (authored is not null)
        {
            var trailingNul = authored.EndsWith('\0');
            var text = trailingNul ? authored[..^1] : authored;
            files.Add(CreateFile(
                name.Stem + extension,
                classification.IsRecoveredFromDump ? ScriptExportContent.CapturedSource : ScriptExportContent.StoredSource,
                classification.Token,
                classification.CorrespondenceToken,
                text,
                trailingNul));
        }

        var hasDecompilation = !string.IsNullOrWhiteSpace(script.DecompiledText);
        var wantsDecompiled = policy == ScriptDecompiledPolicy.All
                              || (policy == ScriptDecompiledPolicy.Missing && authored is null);
        if (wantsDecompiled && hasDecompilation)
        {
            // Rendered from DecompiledText, never from SourceText: a DecompiledFromBytecode SourceText is the
            // dmp to-esm substitute and carries that pipeline's banner.
            var scriptName = ScriptRecordEmissionPolicy.ResolveEditorId(script.EditorId, authored);
            var rendered = CapturedScriptEmissionContract.BuildDecompiledSource(
                script.DecompiledText,
                script.Variables,
                scriptName,
                BuildDecompiledBanner(script, source), script.ReferencedObjects);
            if (rendered is not null)
            {
                files.Add(CreateFile(
                    name.Stem + ScriptExportFileNamer.DecompiledInfix + extension,
                    ScriptExportContent.DecompiledFromScda,
                    ScriptSourceProvenance.DecompiledFromBytecodeToken,
                    ScriptSourceProvenance.CorrespondenceNotApplicable,
                    NormalizeToCrLf(rendered),
                    null));
            }
        }

        string? skipReason = null;
        if (files.Count == 0)
        {
            skipReason = policy == ScriptDecompiledPolicy.None && hasDecompilation
                ? DecompiledNotRequestedSkipReason
                : script.CompiledData is { Length: > 0 }
                    ? BytecodeNotDecompiledSkipReason
                    : NoSourceOrBytecodeSkipReason;
        }

        return new PlannedScript(script, name, classification, files, skipReason);
    }

    private static PlannedFile CreateFile(
        string fileName,
        ScriptExportContent content,
        string provenance,
        string correspondence,
        string text,
        bool? trailingNulRemoved)
    {
        var bytes = EsmStringUtils.EncodeGameText(text);
        var unmapped = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (bytes[i] == (byte)'?' && text[i] != '?')
            {
                unmapped++;
            }
        }

        var description = new ScriptExportFile(
            fileName,
            content,
            provenance,
            correspondence,
            DetectLineEndings(text),
            bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            unmapped,
            trailingNulRemoved);
        return new PlannedFile(description, bytes);
    }

    private static void EnsureTargetsAreFree(
        string directory,
        string manifestPath,
        IReadOnlyList<PlannedScript> plans,
        bool overwrite)
    {
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestFileName };
        var targets = new List<string> { manifestPath };
        foreach (var plan in plans)
        {
            foreach (var file in plan.Files)
            {
                if (!fileNames.Add(file.Description.FileName))
                {
                    throw new InvalidOperationException(
                        $"Two exported files were planned under the same name: '{file.Description.FileName}'.");
                }

                targets.Add(Path.Combine(directory, file.Description.FileName));
            }
        }

        if (!Directory.Exists(directory))
        {
            return;
        }

        // A directory in the way can never be replaced by a file, even when overwriting.
        var conflicts = targets
            .Where(target => Directory.Exists(target) || (!overwrite && File.Exists(target)))
            .ToList();
        if (conflicts.Count > 0)
        {
            throw new ScriptExportConflictException(conflicts);
        }
    }

    private static void WriteBytes(string path, byte[] bytes, bool overwrite)
    {
        using var stream = OpenForWrite(path, overwrite);
        stream.Write(bytes);
    }

    private static FileStream OpenForWrite(string path, bool overwrite)
    {
        return new FileStream(
            path,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
    }

    private static string NormalizeToCrLf(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary>A banner value on one line: control characters (line breaks included) become spaces.</summary>
    private static string BannerValue(string value)
    {
        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
    }

    private sealed record PlannedFile(ScriptExportFile Description, byte[] Bytes);

    private sealed record PlannedScript(
        ScriptRecord Script,
        ScriptExportName Name,
        ScriptSourceClassification Classification,
        IReadOnlyList<PlannedFile> Files,
        string? SkipReason);
}

/// <summary>
///     A script export refused to start because files it would write already exist (and overwriting was
///     off) or a directory stands where a file must go. Nothing was written.
/// </summary>
public sealed class ScriptExportConflictException : IOException
{
    /// <summary>Creates the exception with a generic message and no paths.</summary>
    public ScriptExportConflictException()
        : base("Script export targets already exist.")
    {
        ConflictingPaths = [];
    }

    /// <summary>Creates the exception with <paramref name="message" /> and no paths.</summary>
    public ScriptExportConflictException(string message)
        : base(message)
    {
        ConflictingPaths = [];
    }

    /// <summary>Creates the exception with <paramref name="message" />, an inner exception and no paths.</summary>
    public ScriptExportConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
        ConflictingPaths = [];
    }

    /// <summary>Creates the exception for the paths that are in the way.</summary>
    public ScriptExportConflictException(IReadOnlyList<string> conflictingPaths)
        : base(BuildMessage(conflictingPaths))
    {
        ConflictingPaths = conflictingPaths;
    }

    /// <summary>Every target that is in the way (full paths).</summary>
    public IReadOnlyList<string> ConflictingPaths { get; }

    private static string BuildMessage(IReadOnlyList<string> conflictingPaths)
    {
        ArgumentNullException.ThrowIfNull(conflictingPaths);
        const int shown = 5;
        var listed = string.Join(", ", conflictingPaths.Take(shown).Select(path => Path.GetFileName(path)));
        var more = conflictingPaths.Count > shown
            ? string.Create(CultureInfo.InvariantCulture, $" and {conflictingPaths.Count - shown} more")
            : string.Empty;
        var count = conflictingPaths.Count.ToString(CultureInfo.InvariantCulture);
        return count + " export target(s) already exist (" + listed + more + "); nothing was written. " +
               "Choose an empty directory or allow overwriting.";
    }
}
