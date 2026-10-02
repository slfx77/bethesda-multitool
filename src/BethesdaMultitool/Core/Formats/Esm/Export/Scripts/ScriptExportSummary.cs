using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Scripts;

/// <summary>What one exported file holds.</summary>
public enum ScriptExportContent
{
    /// <summary>
    ///     Text stored in a plugin SCTX, byte for byte; this alone cannot prove authorship
    ///     (the file's provenance token says which).
    /// </summary>
    StoredSource,

    /// <summary>A BethesdaMultitool reconstruction rendered from SCDA; never original source.</summary>
    DecompiledFromScda,

    /// <summary>SCTX recovered from a partial memory capture.</summary>
    CapturedSource
}

/// <summary>The result of one script export.</summary>
public sealed record ScriptExportSummary
{
    /// <summary>The directory the files were written to.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Full path of the <c>scripts.manifest.json</c> written beside them.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>The normalized extension the files use.</summary>
    public required string Extension { get; init; }

    /// <summary>Scripts considered (one manifest entry each, whether written or skipped).</summary>
    public int ScriptCount { get; init; }

    /// <summary>Source files holding SCTX stored in the plugin itself.</summary>
    public int StoredSourceFiles { get; init; }

    /// <summary>Source files holding SCTX recovered from a memory dump.</summary>
    public int CapturedSourceFiles { get; init; }

    /// <summary><c>.decompiled</c> files rendered from SCDA.</summary>
    public int DecompiledFiles { get; init; }

    /// <summary>Scripts for which no file was written.</summary>
    public int SkippedScripts { get; init; }

    /// <summary>Script files written (the manifest is not counted).</summary>
    public int FilesWritten => StoredSourceFiles + CapturedSourceFiles + DecompiledFiles;

    /// <summary>One entry per script, in input order.</summary>
    public IReadOnlyList<ScriptExportEntry> Entries { get; init; } = [];
}

/// <summary>One script's export: its planned name, how its source text is classified, and the files written.</summary>
/// <param name="Script">The script record.</param>
/// <param name="Stem">The file stem (see <see cref="ScriptExportFileNamer" />).</param>
/// <param name="NameAdjustments">Why the stem differs from the EditorID; empty when it does not.</param>
/// <param name="SourceText">How the record's <see cref="ScriptRecord.SourceText" /> is classified.</param>
/// <param name="Files">The files written for this script (zero, one or two).</param>
/// <param name="SkipReason">Why no file was written, or null when at least one was.</param>
public sealed record ScriptExportEntry(
    ScriptRecord Script,
    string Stem,
    IReadOnlyList<string> NameAdjustments,
    ScriptSourceClassification SourceText,
    IReadOnlyList<ScriptExportFile> Files,
    string? SkipReason);

/// <summary>One written file and the facts the manifest records about it.</summary>
/// <param name="FileName">The file name, relative to the export directory.</param>
/// <param name="Content">Stored source, captured source or a decompiled reconstruction.</param>
/// <param name="Provenance">
///     A <see cref="ScriptSourceProvenance" /> token: plugin-record, dmp-fragment, runtime-same-object,
///     unattributed-same-dump or decompiled-from-bytecode.
/// </param>
/// <param name="Correspondence">The same-dump SCTX/SCDA correspondence token (see <see cref="ScriptSourceClassification" />).</param>
/// <param name="LineEndings"><c>crlf</c>, <c>lf</c>, <c>cr</c>, <c>mixed</c> or <c>none</c>.</param>
/// <param name="ByteLength">Bytes written.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of exactly those bytes.</param>
/// <param name="UnmappedCharacters">
///     Characters Windows-1252 cannot hold, written as <c>?</c>. Always 0 for stored source, which was
///     decoded from Windows-1252 in the first place.
/// </param>
/// <param name="TrailingNulRemoved">
///     For stored source, whether one trailing NUL was dropped from the text; null for decompiled files.
/// </param>
public sealed record ScriptExportFile(
    string FileName,
    ScriptExportContent Content,
    string Provenance,
    string Correspondence,
    string LineEndings,
    int ByteLength,
    string Sha256,
    int UnmappedCharacters,
    bool? TrailingNulRemoved);
