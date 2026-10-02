using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Scripts;

/// <summary>
///     Writes <c>scripts.manifest.json</c> with <see cref="Utf8JsonWriter" />, never the reflection-based
///     <see cref="JsonSerializer" />: the published CLI is trimmed with reflection-based serialization
///     disabled, which the test host does not reproduce.
///     <para>
///         Schema <c>bethesda-multitool/script-export</c>, version 1. The top level carries <c>schema</c>,
///         <c>schemaVersion</c>, <c>toolVersion</c>, <c>createdUtc</c>, <c>source</c> (input identity: path,
///         size, SHA-256, plugin or memory dump, game, container byte order, build label and where it came
///         from, then a <c>plugin</c> object with version and masters or a <c>dump</c> object with build
///         type, game module and the partial-capture note), <c>options</c>, <c>scripts</c> (one entry per
///         script) and <c>summary</c>. A script entry carries its identity, the planned stem and why it
///         differs from the EditorID, header facts, bytecode byte order and hash, how its source text is
///         classified, its variables and referenced objects (what the old per-script <c>.txt</c> wrapper
///         held), the files written with their provenance and SHA-256, and a skip reason when none was.
///     </para>
///     <para>
///         FormIDs are <c>0x%08X</c> strings. Every field is always present; absent values are explicit
///         nulls. File paths inside <c>scripts</c> are relative to the export directory, and the output
///         directory itself is not recorded, so the same input and <c>createdUtc</c> give the same bytes
///         wherever the export is written.
///     </para>
/// </summary>
internal static class ScriptExportManifestWriter
{
    /// <summary>The script manifest's <c>schema</c> identifier.</summary>
    internal const string Schema = "bethesda-multitool/script-export";

    /// <summary>The carved-fragment manifest's <c>schema</c> identifier.</summary>
    internal const string FragmentSchema = "bethesda-multitool/script-sources";

    /// <summary>The documents' <c>schemaVersion</c>. Bump only for a breaking change; additions keep it.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>The encoding every exported script file uses.</summary>
    internal const string FileEncoding = "windows-1252";

    /// <summary>Provenance of an SCTX fragment carved out of a memory dump with no owning record.</summary>
    internal const string CarvedFragmentProvenance = "carved-sctx-fragment";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>
    ///     The BethesdaMultitool assembly's informational version (else its assembly version): the value the
    ///     CLI records as <c>toolVersion</c>.
    /// </summary>
    internal static string DefaultToolVersion { get; } = ResolveToolVersion();

    /// <summary>
    ///     Writes the manifest for one export. Flushes its own writer but neither closes nor disposes
    ///     <paramref name="output" />.
    /// </summary>
    internal static void Write(
        Stream output,
        ScriptExportSummary summary,
        ScriptExportSource source,
        ScriptExportOptions options,
        DateTimeOffset createdUtc,
        Func<uint, string?>? resolveEditorId)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        using var writer = new Utf8JsonWriter(output, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("schema", Schema);
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("toolVersion", options.ToolVersion ?? DefaultToolVersion);
        writer.WriteString("createdUtc", FormatUtc(createdUtc));
        WriteSource(writer, source);

        writer.WriteStartObject("options");
        writer.WriteString("extension", summary.Extension);
        writer.WriteString("decompiled", ScriptExportOptions.FormatDecompiledPolicy(options.Decompiled));
        writer.WriteBoolean("overwrite", options.Overwrite);
        writer.WriteEndObject();

        writer.WriteStartArray("scripts");
        foreach (var entry in summary.Entries)
        {
            WriteScript(writer, entry, resolveEditorId);
        }

        writer.WriteEndArray();

        writer.WriteStartObject("summary");
        writer.WriteNumber("scripts", summary.ScriptCount);
        writer.WriteNumber("storedSourceFiles", summary.StoredSourceFiles);
        writer.WriteNumber("capturedSourceFiles", summary.CapturedSourceFiles);
        writer.WriteNumber("decompiledFiles", summary.DecompiledFiles);
        writer.WriteNumber("skippedScripts", summary.SkippedScripts);
        writer.WriteNumber("filesWritten", summary.FilesWritten);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    ///     Writes <c>script_sources/manifest.json</c> for SCTX fragments carved from a memory dump. The fragments
    ///     have no owning record: they come from a keyword-filtered scan for SCTX subrecord headers.
    /// </summary>
    internal static void WriteCarvedFragments(
        Stream output,
        ScriptExportSource? source,
        IReadOnlyList<CarvedScriptFragment> fragments,
        DateTimeOffset createdUtc,
        string? toolVersion = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(fragments);

        using var writer = new Utf8JsonWriter(output, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("schema", FragmentSchema);
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("toolVersion", toolVersion ?? DefaultToolVersion);
        writer.WriteString("createdUtc", FormatUtc(createdUtc));
        if (source is null)
        {
            writer.WriteNull("source");
        }
        else
        {
            WriteSource(writer, source);
        }

        writer.WriteString("provenance", CarvedFragmentProvenance);
        writer.WriteString(
            "method",
            "Keyword-filtered scan of the dump for SCTX subrecord headers. A fragment has no owning record, " +
            "and its text ends at its first NUL.");
        writer.WriteString("note", ScriptExportSource.PartialCaptureNote);

        writer.WriteStartArray("fragments");
        foreach (var fragment in fragments)
        {
            writer.WriteStartObject();
            writer.WriteString("path", fragment.FileName);
            writer.WriteNumber("offset", fragment.Offset);
            writer.WriteNumber("subrecordLength", fragment.SubrecordLength);
            writer.WriteString("encoding", FileEncoding);
            writer.WriteString("lineEndings", fragment.LineEndings);
            writer.WriteNumber("byteLength", fragment.ByteLength);
            writer.WriteString("sha256", fragment.Sha256);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>The kebab-case token for a bytecode byte-order decision.</summary>
    internal static string FormatByteOrderEvidence(ScriptBytecodeByteOrderEvidence evidence)
    {
        return evidence switch
        {
            ScriptBytecodeByteOrderEvidence.NotApplicable => "not-applicable",
            ScriptBytecodeByteOrderEvidence.ScriptNameAnchor => "script-name-anchor",
            ScriptBytecodeByteOrderEvidence.SingleCleanWalk => "single-clean-walk",
            ScriptBytecodeByteOrderEvidence.FewerUnknownOpcodes => "fewer-unknown-opcodes",
            ScriptBytecodeByteOrderEvidence.WalkedToEndInOneOrder => "walked-to-end-in-one-order",
            ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault => "ambiguous-serialized-default",
            ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback => "ambiguous-container-fallback",
            ScriptBytecodeByteOrderEvidence.RuntimeScriptObject => "runtime-script-object",
            _ => string.Create(CultureInfo.InvariantCulture, $"unknown-{(int)evidence}")
        };
    }

    private static void WriteSource(Utf8JsonWriter writer, ScriptExportSource source)
    {
        writer.WriteStartObject("source");
        writer.WriteString("path", source.Path);
        writer.WriteString("fileName", source.FileName);
        WriteInt64OrNull(writer, "sizeBytes", source.SizeBytes);
        WriteStringOrNull(writer, "sha256", source.Sha256);
        writer.WriteString("kind", source.IsMemoryDump ? "memory-dump" : "plugin");
        writer.WriteString("game", source.Game.ToString());
        WriteStringOrNull(writer, "containerEndianness", source.ContainerEndianness);
        WriteStringOrNull(writer, "buildLabel", source.BuildLabel);
        WriteStringOrNull(writer, "buildLabelSource", source.BuildLabelSource);

        if (source.IsMemoryDump)
        {
            writer.WriteNull("plugin");
            writer.WriteStartObject("dump");
            WriteStringOrNull(writer, "buildType", source.DumpBuildType);
            WriteStringOrNull(writer, "gameModule", source.DumpGameModule);
            WriteStringOrNull(
                writer,
                "gameModuleTimeDateStampUtc",
                source.DumpGameModuleTimeDateStampUtc is { } stamp ? FormatUtc(stamp) : null);
            writer.WriteString("capture", ScriptExportSource.PartialMemoryDumpCapture);
            writer.WriteString("note", ScriptExportSource.PartialCaptureNote);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteStartObject("plugin");
            if (source.PluginVersion is { } version && float.IsFinite(version))
            {
                writer.WriteNumber("version", version);
                writer.WriteNull("versionRawBits");
            }
            else if (source.PluginVersion is { } nonFinite)
            {
                writer.WriteNull("version");
                writer.WriteString(
                    "versionRawBits",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"0x{BitConverter.SingleToUInt32Bits(nonFinite):X8}"));
            }
            else
            {
                writer.WriteNull("version");
                writer.WriteNull("versionRawBits");
            }

            writer.WriteStartArray("masters");
            foreach (var master in source.PluginMasters)
            {
                writer.WriteStringValue(master);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteNull("dump");
        }

        writer.WriteEndObject();
    }

    private static void WriteScript(
        Utf8JsonWriter writer,
        ScriptExportEntry entry,
        Func<uint, string?>? resolveEditorId)
    {
        var script = entry.Script;
        writer.WriteStartObject();
        writer.WriteString("formId", FormatFormId(script.FormId));
        WriteStringOrNull(writer, "editorId", script.EditorId);
        writer.WriteString("stem", entry.Stem);
        writer.WriteStartArray("nameAdjustments");
        foreach (var adjustment in entry.NameAdjustments)
        {
            writer.WriteStringValue(adjustment);
        }

        writer.WriteEndArray();
        writer.WriteString("scriptType", script.ScriptType);
        writer.WriteNumber("recordOffset", script.Offset);
        writer.WriteBoolean("fromRuntime", script.FromRuntime);
        writer.WriteBoolean("isCompiled", script.IsCompiled);
        writer.WriteNumber("compiledSize", script.CompiledSize);
        writer.WriteNumber("variableCount", script.VariableCount);
        writer.WriteNumber("refObjectCount", script.RefObjectCount);
        writer.WriteString("containerEndianness", script.IsBigEndian ? "big" : "little");

        if (script.CompiledData is { Length: > 0 } bytecode)
        {
            writer.WriteStartObject("bytecode");
            writer.WriteNumber("byteLength", bytecode.Length);
            writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(bytecode)));
            writer.WriteString("endianness", script.IsBigEndianBytecode ? "big" : "little");
            writer.WriteString("byteOrderEvidence", FormatByteOrderEvidence(script.BytecodeByteOrderEvidence));
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("bytecode");
        }

        writer.WriteStartObject("sourceText");
        writer.WriteString("kind", entry.SourceText.Token);
        writer.WriteString("correspondence", entry.SourceText.CorrespondenceToken);
        writer.WriteString("label", entry.SourceText.Label);
        writer.WriteEndObject();

        writer.WriteStartArray("variables");
        foreach (var variable in script.Variables)
        {
            var type = ScriptVariableTypeResolver.Resolve(variable, script.ReferencedObjects);
            writer.WriteStartObject();
            writer.WriteNumber("index", variable.Index);
            WriteStringOrNull(writer, "name", variable.Name);
            writer.WriteString("type", type.Name);
            writer.WriteString("storageType", variable.TypeName);
            writer.WriteString("typeEvidence", type.Evidence);
            writer.WriteNumber("typeByte", (int)variable.Type);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("referencedObjects");
        for (var i = 0; i < script.ReferencedObjects.Count; i++)
        {
            WriteReference(writer, i + 1, script.ReferencedObjects[i], resolveEditorId);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("externalVariables");
        foreach (var binding in script.ExternalVariableBindings)
        {
            writer.WriteStartObject();
            writer.WriteString("ownerFormId", FormatFormId(binding.OwnerFormId));
            writer.WriteNumber("variableIndex", binding.VariableIndex);
            writer.WriteString("status", binding.Status);
            WriteStringOrNull(writer, "name", binding.Name);
            WriteStringOrNull(writer, "scriptFormId", binding.ScriptFormId is { } id ? FormatFormId(id) : null);
            writer.WriteStartArray("candidateScripts");
            foreach (var candidate in binding.CandidateScripts) writer.WriteStringValue(FormatFormId(candidate));
            writer.WriteEndArray();
            writer.WriteStartArray("ownerChain");
            foreach (var owner in binding.OwnerChain) writer.WriteStringValue(FormatFormId(owner));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("files");
        foreach (var file in entry.Files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.FileName);
            writer.WriteString(
                "content",
                file.Content switch
                {
                    ScriptExportContent.DecompiledFromScda => "decompiled-from-scda",
                    ScriptExportContent.CapturedSource => "captured-source",
                    _ => "stored-source"
                });
            writer.WriteString("provenance", file.Provenance);
            writer.WriteString("correspondence", file.Correspondence);
            writer.WriteString("encoding", FileEncoding);
            writer.WriteString("lineEndings", file.LineEndings);
            writer.WriteNumber("byteLength", file.ByteLength);
            writer.WriteString("sha256", file.Sha256);
            writer.WriteNumber("unmappedCharacters", file.UnmappedCharacters);
            if (file.TrailingNulRemoved is { } removed)
            {
                writer.WriteBoolean("trailingNulRemoved", removed);
            }
            else
            {
                writer.WriteNull("trailingNulRemoved");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteStringOrNull(writer, "skipped", entry.SkipReason);
        writer.WriteEndObject();
    }

    /// <summary>
    ///     One slot of the combined SCRO/SCRV list (the bytecode's 1-based reference index): a FormID, or for
    ///     SCRV (stored with the high bit set) the index of a local variable.
    /// </summary>
    private static void WriteReference(
        Utf8JsonWriter writer,
        int slot,
        uint value,
        Func<uint, string?>? resolveEditorId)
    {
        writer.WriteStartObject();
        writer.WriteNumber("slot", slot);
        if ((value & 0x80000000u) != 0)
        {
            writer.WriteNull("formId");
            writer.WriteNull("editorId");
            writer.WriteNumber("scrvVariableIndex", value & 0x7FFFFFFFu);
        }
        else
        {
            writer.WriteString("formId", FormatFormId(value));
            WriteStringOrNull(writer, "editorId", resolveEditorId?.Invoke(value));
            writer.WriteNull("scrvVariableIndex");
        }

        writer.WriteEndObject();
    }

    private static string FormatFormId(uint formId)
    {
        return string.Create(CultureInfo.InvariantCulture, $"0x{formId:X8}");
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteInt64OrNull(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static string ResolveToolVersion()
    {
        var assembly = typeof(ScriptExportManifestWriter).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}

/// <summary>One SCTX fragment written by the carved-fragment export.</summary>
/// <param name="FileName">The file name, relative to the <c>script_sources</c> directory.</param>
/// <param name="Offset">Offset of the SCTX subrecord header in the dump.</param>
/// <param name="SubrecordLength">The subrecord's declared data length.</param>
/// <param name="LineEndings">Line-ending token of the written text.</param>
/// <param name="ByteLength">Bytes written.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of those bytes.</param>
internal sealed record CarvedScriptFragment(
    string FileName,
    long Offset,
    int SubrecordLength,
    string LineEndings,
    int ByteLength,
    string Sha256);
