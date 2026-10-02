using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Export.Scripts;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

internal static class EsmQuestScriptExportWriter
{
    internal static void Write(EsmScriptDiagnosticsResult result, string outputDirectory)
    {
        var directory = Directory.CreateDirectory(Path.Combine(outputDirectory, "quest-scripts")).FullName;
        using var stream = File.Create(Path.Combine(directory, "manifest.json"));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", 1);
        writer.WriteString("game", result.Game.ToString());
        writer.WriteStartObject("source");
        writer.WriteString("path", result.SourcePath);
        writer.WriteString("sha256", result.SourceSha256);
        WriteNumber(writer, "length", result.SourceLength);
        writer.WriteEndObject();
        writer.WriteStartArray("fragments");
        var extension = ScriptExportFileNamer.DefaultExtension(result.Game);
        foreach (var fragment in result.QuestScripts)
        {
            var stem = string.Create(CultureInfo.InvariantCulture,
                $"QUST_{fragment.FormId:X8}_offset{fragment.RecordOffset:X}_occ{fragment.RecordOccurrence:D4}_block{fragment.BlockIndex:D2}");
            writer.WriteStartObject();
            writer.WriteString("form_id", $"{fragment.FormId:X8}");
            writer.WriteString("editor_id", fragment.EditorId);
            writer.WriteNumber("record_offset", fragment.RecordOffset);
            writer.WriteNumber("record_occurrence", fragment.RecordOccurrence);
            writer.WriteNumber("record_flags", fragment.RecordFlags);
            writer.WriteNumber("block_index", fragment.BlockIndex);
            WriteNumber(writer, "stage_ordinal", fragment.StageOrdinal);
            WriteNumber(writer, "stage_index", fragment.StageIndex);
            WriteNumber(writer, "entry_ordinal", fragment.EntryOrdinal);
            writer.WriteNumber("start_subrecord_index", fragment.StartSubrecordIndex);
            WriteNumber(writer, "scda_subrecord_index", fragment.ScdaSubrecordIndex);
            WriteNumber(writer, "scda_file_offset", fragment.ScdaFileOffset);
            writer.WriteString("offset_status", fragment.OffsetStatus);
            writer.WriteString("header_state", fragment.HeaderState);
            WriteNumber(writer, "declared_compiled_size", fragment.DeclaredCompiledSize);
            WriteNumber(writer, "declared_reference_count", fragment.DeclaredReferenceCount);
            WriteNumber(writer, "declared_variable_count", fragment.DeclaredVariableCount);
            writer.WriteString("bytecode_state", fragment.BytecodeState);
            writer.WriteString("source_state", fragment.SourceState);
            writer.WriteString("status", fragment.Status);
            writer.WriteString("byte_order", fragment.ByteOrder);
            writer.WriteString("byte_order_evidence", fragment.ByteOrderEvidence);
            writer.WriteString("byte_order_detail", fragment.ByteOrderDetail);
            WritePayload(writer, "bytecode", fragment.Bytecode, directory, stem + ".scda.bin");
            var stored = fragment.StoredSource;
            var sourceFile = stored is { Length: > 0 } && stored[^1] == 0 ? stored[..^1] : stored;
            WritePayload(writer, "stored_source", stored, directory, stem + extension, sourceFile);
            WritePayload(writer, "reconstruction", fragment.Reconstruction is { } text
                ? Encoding.UTF8.GetBytes(text) : null, directory, stem + ".decompiled" + extension);
            WriteTables(writer, fragment);
            writer.WriteStartArray("diagnostics");
            foreach (var diagnostic in fragment.Diagnostics) writer.WriteStringValue(diagnostic);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteTables(Utf8JsonWriter writer, EsmQuestScriptFragment fragment)
    {
        var refs = fragment.References.Select(reference => reference.DecoderValue).ToArray();
        writer.WriteStartArray("variables");
        foreach (var variable in fragment.Variables)
        {
            var type = ScriptVariableTypeResolver.Resolve(variable, refs);
            writer.WriteStartObject();
            writer.WriteNumber("index", variable.Index);
            writer.WriteString("name", variable.Name);
            writer.WriteNumber("storage_type", variable.Type);
            writer.WriteString("declaration_type", type.Name);
            writer.WriteString("type_evidence", type.Evidence);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("references");
        foreach (var reference in fragment.References)
        {
            writer.WriteStartObject();
            writer.WriteNumber("slot_index", reference.SlotIndex);
            writer.WriteString("kind", reference.Kind);
            WriteNumber(writer, "raw_value", reference.RawValue);
            writer.WriteNumber("subrecord_index", reference.SubrecordIndex);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("external_variables");
        foreach (var binding in fragment.ExternalVariables)
        {
            writer.WriteStartObject();
            writer.WriteNumber("owner_form_id", binding.OwnerFormId);
            writer.WriteNumber("variable_index", binding.VariableIndex);
            writer.WriteString("status", binding.Status);
            WriteNumber(writer, "script_form_id", binding.ScriptFormId);
            writer.WriteString("name", binding.Name);
            WriteNumbers(writer, "candidate_scripts", binding.CandidateScripts);
            WriteNumbers(writer, "owner_chain", binding.OwnerChain);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("metadata");
        foreach (var sub in fragment.Metadata)
        {
            writer.WriteStartObject();
            writer.WriteString("signature", sub.Signature);
            writer.WriteNumber("subrecord_index", sub.SubrecordIndex);
            writer.WriteNumber("length", sub.Data.Length);
            writer.WriteString("hex", Convert.ToHexString(sub.Data));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WritePayload(Utf8JsonWriter writer, string property, byte[]? bytes,
        string directory, string fileName, byte[]? exported = null)
    {
        if (bytes is null) { writer.WriteNull(property); return; }
        exported ??= bytes;
        File.WriteAllBytes(Path.Combine(directory, fileName), exported);
        writer.WriteStartObject(property);
        writer.WriteNumber("length", bytes.Length);
        writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        writer.WriteString("file", fileName);
        writer.WriteNumber("file_length", exported.Length);
        writer.WriteString("file_sha256", Convert.ToHexStringLower(SHA256.HashData(exported)));
        writer.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    private static void WriteNumbers(Utf8JsonWriter writer, string name, IEnumerable<uint> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }
}
