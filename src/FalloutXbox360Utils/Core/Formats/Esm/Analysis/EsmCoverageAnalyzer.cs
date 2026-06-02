using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Analysis;

public enum EsmCoverageClassification
{
    Typed,
    SpecialModeled,
    GenericModeled,
    IntentionallyOpaque,
    Unparsed
}

public sealed record EsmCoverageResult(
    string SourcePath,
    IReadOnlyList<EsmRecordCoverageRow> Records,
    IReadOnlyList<EsmSubrecordCoverageRow> Subrecords,
    IReadOnlyList<EsmScriptBytecodeCoverageRow> ScriptBytecode)
{
    public int TotalRecords => Records.Sum(r => r.Count);
    public int TotalRecordTypes => Records.Count;
    public int TotalSubrecordKinds => Subrecords.Count;
}

public sealed record EsmRecordCoverageRow(
    string RecordType,
    int Count,
    EsmCoverageClassification Classification,
    string ParserOwner,
    string EncoderOwner,
    string ExampleFormIds);

public sealed record EsmScriptBytecodeCoverageRow(
    string RecordType,
    uint FormId,
    int BlockIndex,
    int ScdaLength,
    uint? SchrCompiledSize,
    uint? SchrRefObjectCount,
    int ActualReferenceSlots,
    uint? SchrVariableCount,
    int ActualVariables,
    bool CompiledSizeMatches,
    bool RefCountMatches,
    bool VariableCountMatches,
    bool WalkedToEnd,
    int MultiByteReadCount,
    int MultiByteByteCount,
    bool HasDiagnostics,
    string Diagnostics);

public sealed record EsmSubrecordCoverageRow(
    string RecordType,
    string Subrecord,
    int DataLength,
    int Count,
    EsmCoverageClassification Classification,
    string SchemaKind,
    bool UsesRawByteArray,
    bool IsIntentionalRaw,
    string CoverageNote,
    string ParserOwner,
    string EncoderOwner,
    string ExampleFormIds);

/// <summary>
///     Reports record/subrecord modeling coverage for complete ESM/ESP files.
/// </summary>
public static class EsmCoverageAnalyzer
{
    private static readonly HashSet<string> GenericModeledRecordTypes = new(StringComparer.Ordinal)
    {
        "MSTT", "TACT", "CAMS", "ANIO", "IPDS", "EFSH", "RGDL", "LSCR",
        "ASPC", "MSET", "CHIP", "CSNO", "DOBJ", "ADDN", "TREE", "IMAD",
        "IDLM", "PWAT", "IMGS", "CLMT", "GRAS", "AMEF"
    };

    private static readonly HashSet<string> SpecialModeledRecordTypes = new(StringComparer.Ordinal)
    {
        "TES4", "REFR", "ACHR", "ACRE", "LAND"
    };

    private static readonly HashSet<string> TypedRecordTypes = new(StringComparer.Ordinal)
    {
        "NPC_", "CREA", "RACE", "FACT", "ECZN",
        "QUST", "DIAL", "INFO", "NOTE", "BOOK", "TERM", "SCPT",
        "WEAP", "ARMO", "AMMO", "ALCH", "MISC", "KEYM", "CONT",
        "PERK", "SPEL", "CELL", "WRLD", "GMST",
        "GLOB", "ENCH", "MGEF", "IMOD", "RCPE", "RCCT", "COBJ", "CHAL", "REPU",
        "PROJ", "EXPL", "MESG", "CLAS",
        "FLST", "ACTI", "LIGH", "DOOR", "STAT", "FURN", "PACK", "SCOL", "FLOR",
        "SOUN", "MUSC", "TXST", "LTEX", "ARMA", "WATR", "BPTD", "AVIF", "CSTY", "LGTM", "NAVM", "WTHR",
        "HDPT", "VTYP", "MICN", "LSCT", "IDLE", "CPTH", "IPCT", "ALOC",
        "PGRE", "REGN", "CCRD", "CMNY", "DEBR", "INGR", "NAVI", "CDCK",
        "RADS", "DEHY", "HUNG", "SLPD", "EYES", "HAIR", "LVLI", "LVLN", "LVLC"
    };

    private static readonly Dictionary<(string RecordType, string Subrecord), string> IntentionalRawSubrecordReasons =
        new()
        {
            [("*", "MODT")] = "Model texture hash blob; byte-preserved because PC hash contents are source-specific.",
            [("*", "MO2T")] = "Model texture hash blob; byte-preserved because PC hash contents are source-specific.",
            [("*", "MO3T")] = "Model texture hash blob; byte-preserved because PC hash contents are source-specific.",
            [("*", "MO4T")] = "Model texture hash blob; byte-preserved because PC hash contents are source-specific.",
            [("*", "DMDT")] = "Destruction model texture hash blob; byte-preserved with destruction model data.",
            [("*", "FGGS")] = "FaceGen coefficient blob; parsed for diagnostics and emitted as its canonical float payload.",
            [("*", "FGTS")] = "FaceGen coefficient blob; parsed for diagnostics and emitted as its canonical float payload.",
            [("*", "FGGA")] = "FaceGen coefficient blob; parsed for diagnostics and emitted as its canonical float payload.",
            [("LAND", "VNML")] = "LAND vertex normals; generated from modeled heightmaps for emitted LAND and byte-preserved otherwise.",
            [("PROJ", "NAM2")] = "Projectile model-info texture hash blob; byte-preserved like MODT-family hashes."
        };

    private static readonly Dictionary<(string RecordType, string Subrecord), string> CustomModeledSubrecordReasons =
        new()
        {
            [("NAVM", "NVGD")] = "Custom navmesh grid endian converter.",
            [("NAVI", "NVMI")] = "Custom NavMeshInfoMap entry converter and NAVI override builder.",
            [("NAVI", "NVCI")] = "Custom NavMeshInfoMap connection converter and NAVI override builder.",
            [("DEBR", "DATA")] = "Variable DEBR variant payload modeled as percentage + path + flags.",
            [("LAND", "VCLR")] = "LAND vertex colors parsed into structured visual data.",
            [("LAND", "VTEX")] = "LAND texture indices parsed into structured visual data.",
            [("*", "SCDA")] = "Compiled script bytecode modeled by ScriptBytecodeAnalyzer/decompiler walk."
        };

    public static EsmCoverageResult AnalyzeFile(string path)
    {
        var data = File.ReadAllBytes(path);
        var records = EsmParser.EnumerateRecordsWithGrups(data).Records;
        return AnalyzeRecords(path, records);
    }

    public static EsmCoverageResult AnalyzeRecords(
        string sourcePath,
        IReadOnlyList<ParsedMainRecord> records)
    {
        var encoderTypes = BuildEncoderTypeSet();

        var recordRows = records
            .GroupBy(r => r.Header.Signature, StringComparer.Ordinal)
            .Select(g =>
            {
                var type = g.Key;
                return new EsmRecordCoverageRow(
                    type,
                    g.Count(),
                    ClassifyRecord(type),
                    GetParserOwner(type),
                    encoderTypes.Contains(type) ? "RecordEncoderRegistry" : "",
                    FormatExamples(g.Select(r => r.Header.FormId)));
            })
            .OrderBy(r => r.Classification)
            .ThenByDescending(r => r.Count)
            .ThenBy(r => r.RecordType, StringComparer.Ordinal)
            .ToList();

        var subrecordRows = records
            .SelectMany(record => record.Subrecords.Select(sub => new
            {
                RecordType = record.Header.Signature,
                Subrecord = sub.Signature,
                DataLength = sub.Data.Length,
                record.Header.FormId
            }))
            .GroupBy(x => (x.RecordType, x.Subrecord, x.DataLength))
            .Select(g =>
            {
                var recordType = g.Key.RecordType;
                var subrecord = g.Key.Subrecord;
                var dataLength = g.Key.DataLength;
                var schema = GetSchema(recordType, subrecord, dataLength);
                var isCustomModeled = TryGetCustomModeledReason(recordType, subrecord, dataLength, out var customReason);
                var rawReason = string.Empty;
                var isIntentionalRaw = !isCustomModeled &&
                    TryGetIntentionalRawReason(recordType, subrecord, out rawReason);
                var schemaKind = isCustomModeled
                    ? "CustomProcessor"
                    : GetSchemaKind(schema, isIntentionalRaw);
                var usesRaw = !isCustomModeled && (schema == null || ReferenceEquals(schema, SubrecordSchema.ByteArray));
                var classification = isIntentionalRaw
                    ? EsmCoverageClassification.IntentionallyOpaque
                    : ClassifyRecord(recordType);

                return new EsmSubrecordCoverageRow(
                    recordType,
                    subrecord,
                    g.Key.DataLength,
                    g.Count(),
                    classification,
                    schemaKind,
                    usesRaw,
                    isIntentionalRaw,
                    isCustomModeled ? customReason : rawReason,
                    GetParserOwner(recordType),
                    encoderTypes.Contains(recordType) ? "RecordEncoderRegistry" : "",
                    FormatExamples(g.Select(x => x.FormId)));
            })
            .OrderBy(r => r.Classification)
            .ThenBy(r => r.RecordType, StringComparer.Ordinal)
            .ThenBy(r => r.Subrecord, StringComparer.Ordinal)
            .ThenBy(r => r.DataLength)
            .ToList();

        var scriptRows = AnalyzeScriptBytecode(records);

        return new EsmCoverageResult(sourcePath, recordRows, subrecordRows, scriptRows);
    }

    private static List<EsmScriptBytecodeCoverageRow> AnalyzeScriptBytecode(IReadOnlyList<ParsedMainRecord> records)
    {
        var rows = new List<EsmScriptBytecodeCoverageRow>();
        foreach (var record in records)
        {
            if (!record.Subrecords.Any(s => s.Signature == "SCDA"))
            {
                continue;
            }

            var blockIndex = 0;
            var scdaSeenInBlocks = new HashSet<int>();
            var subs = record.Subrecords;
            for (var i = 0; i < subs.Count; i++)
            {
                if (subs[i].Signature != "SCHR")
                {
                    continue;
                }

                var end = FindScriptBlockEnd(subs, i + 1);
                var scdaIndex = FindFirstSubrecord(subs, "SCDA", i + 1, end);
                if (scdaIndex < 0)
                {
                    continue;
                }

                blockIndex++;
                scdaSeenInBlocks.Add(scdaIndex);
                rows.Add(BuildScriptBytecodeRow(record, blockIndex, subs[i], subs[scdaIndex], subs, i + 1, end));
            }

            // Defensive fallback: malformed records can contain SCDA without a preceding SCHR.
            foreach (var (sub, index) in subs.Select((sub, index) => (sub, index)))
            {
                if (sub.Signature != "SCDA" || scdaSeenInBlocks.Contains(index))
                {
                    continue;
                }

                blockIndex++;
                rows.Add(BuildScriptBytecodeRow(record, blockIndex, schr: null, sub, subs, index + 1,
                    FindScriptBlockEnd(subs, index + 1)));
            }
        }

        return rows
            .OrderBy(r => r.RecordType, StringComparer.Ordinal)
            .ThenBy(r => r.FormId)
            .ThenBy(r => r.BlockIndex)
            .ToList();
    }

    private static EsmScriptBytecodeCoverageRow BuildScriptBytecodeRow(
        ParsedMainRecord record,
        int blockIndex,
        ParsedSubrecord? schr,
        ParsedSubrecord scda,
        IReadOnlyList<ParsedSubrecord> allSubrecords,
        int blockStart,
        int blockEnd)
    {
        var header = TryReadScriptHeader(schr?.Data);
        var variables = ReadScriptVariables(allSubrecords, blockStart, blockEnd);
        var references = ReadScriptReferences(allSubrecords, blockStart, blockEnd);
        var analysis = ScriptBytecodeAnalyzer.Analyze(scda.Data, false, variables, references);

        var compiledSizeMatches = !header.CompiledSize.HasValue || header.CompiledSize.Value == scda.Data.Length;
        var refCountMatches = !header.RefObjectCount.HasValue || header.RefObjectCount.Value == references.Count;
        var variableCountMatches = !header.VariableCount.HasValue || header.VariableCount.Value == variables.Count;

        return new EsmScriptBytecodeCoverageRow(
            record.Header.Signature,
            record.Header.FormId,
            blockIndex,
            scda.Data.Length,
            header.CompiledSize,
            header.RefObjectCount,
            references.Count,
            header.VariableCount,
            variables.Count,
            compiledSizeMatches,
            refCountMatches,
            variableCountMatches,
            analysis.WalkedToEnd,
            analysis.MultiByteReadCount,
            analysis.MultiByteByteCount,
            analysis.HasDiagnostics,
            analysis.Diagnostics);
    }

    private static int FindScriptBlockEnd(List<ParsedSubrecord> subrecords, int start)
    {
        for (var i = start; i < subrecords.Count; i++)
        {
            if (subrecords[i].Signature is "NEXT" or "SCHR")
            {
                return i;
            }
        }

        return subrecords.Count;
    }

    private static int FindFirstSubrecord(
        List<ParsedSubrecord> subrecords,
        string signature,
        int start,
        int end)
    {
        for (var i = start; i < end; i++)
        {
            if (subrecords[i].Signature == signature)
            {
                return i;
            }
        }

        return -1;
    }

    private static (uint? VariableCount, uint? RefObjectCount, uint? CompiledSize) TryReadScriptHeader(byte[]? data)
    {
        if (data is not { Length: >= 20 })
        {
            return (null, null, null);
        }

        return (
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)));
    }

    private static List<ScriptVariableInfo> ReadScriptVariables(
        IReadOnlyList<ParsedSubrecord> subrecords,
        int start,
        int end)
    {
        var variables = new List<ScriptVariableInfo>();
        uint? pendingIndex = null;
        byte pendingType = 0;
        for (var i = start; i < end; i++)
        {
            var sub = subrecords[i];
            if (sub.Signature == "SLSD" && sub.Data.Length >= 4)
            {
                pendingIndex = BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(0, 4));
                pendingType = sub.Data.Length > 16 && sub.Data[16] != 0 ? (byte)1 : (byte)0;
            }
            else if (sub.Signature == "SCVR" && pendingIndex.HasValue)
            {
                variables.Add(new ScriptVariableInfo(
                    pendingIndex.Value,
                    ReadNullTerminatedLatin1(sub.Data),
                    pendingType));
                pendingIndex = null;
                pendingType = 0;
            }
        }

        if (pendingIndex.HasValue)
        {
            variables.Add(new ScriptVariableInfo(pendingIndex.Value, null, pendingType));
        }

        return variables;
    }

    private static List<uint> ReadScriptReferences(
        IReadOnlyList<ParsedSubrecord> subrecords,
        int start,
        int end)
    {
        var references = new List<uint>();
        for (var i = start; i < end; i++)
        {
            var sub = subrecords[i];
            if (sub.Data.Length < 4)
            {
                continue;
            }

            if (sub.Signature == "SCRO")
            {
                references.Add(BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(0, 4)));
            }
            else if (sub.Signature == "SCRV")
            {
                references.Add(0x80000000u | BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(0, 4)));
            }
        }

        return references;
    }

    private static string ReadNullTerminatedLatin1(byte[] data)
    {
        var length = Array.IndexOf(data, (byte)0);
        if (length < 0)
        {
            length = data.Length;
        }

        return Encoding.Latin1.GetString(data, 0, length);
    }

    public static void WriteReport(EsmCoverageResult result, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "record_coverage.csv"), BuildRecordCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "subrecord_coverage.csv"), BuildSubrecordCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "script_bytecode_coverage.csv"),
            BuildScriptBytecodeCsv(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "summary.md"), BuildSummary(result), Encoding.UTF8);
    }

    private static HashSet<string> BuildEncoderTypeSet()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in RecordEncoderRegistry.CreateDefault().SupportedRecordTypes)
        {
            result.Add(type);
        }

        foreach (var type in NewTopLevelRecordEncoderDispatcher.GetSupportedRecordTypes())
        {
            result.Add(type);
        }

        return result;
    }

    private static EsmCoverageClassification ClassifyRecord(string recordType)
    {
        if (SpecialModeledRecordTypes.Contains(recordType))
        {
            return EsmCoverageClassification.SpecialModeled;
        }

        if (GenericModeledRecordTypes.Contains(recordType))
        {
            return EsmCoverageClassification.GenericModeled;
        }

        return TypedRecordTypes.Contains(recordType)
            ? EsmCoverageClassification.Typed
            : EsmCoverageClassification.Unparsed;
    }

    private static string GetParserOwner(string recordType)
    {
        if (SpecialModeledRecordTypes.Contains(recordType))
        {
            return recordType switch
            {
                "REFR" or "ACHR" or "ACRE" => "Cell/placed-ref enrichment",
                "LAND" => "Terrain enrichment",
                "TES4" => "File header",
                _ => "Special"
            };
        }

        if (GenericModeledRecordTypes.Contains(recordType))
        {
            return "GenericEsmRecord";
        }

        return TypedRecordTypes.Contains(recordType) ? "RecordParser" : "";
    }

    private static SubrecordSchema? GetSchema(string recordType, string subrecord, int dataLength)
    {
        return SubrecordSchemaRegistry.IsStringSubrecord(subrecord, recordType)
            ? SubrecordSchema.String
            : SubrecordSchemaRegistry.GetSchema(subrecord, recordType, dataLength);
    }

    private static string GetSchemaKind(SubrecordSchema? schema, bool isIntentionalRaw)
    {
        if (schema == null)
        {
            return "Unregistered";
        }

        if (ReferenceEquals(schema, SubrecordSchema.String))
        {
            return "String";
        }

        if (ReferenceEquals(schema, SubrecordSchema.Empty))
        {
            return "Empty";
        }

        if (ReferenceEquals(schema, SubrecordSchema.ByteArray))
        {
            return isIntentionalRaw ? "IntentionalByteArray" : "ByteArray";
        }

        if (ReferenceEquals(schema, SubrecordSchema.FloatArray))
        {
            return "FloatArray";
        }

        if (ReferenceEquals(schema, SubrecordSchema.FormIdArray))
        {
            return "FormIdArray";
        }

        if (ReferenceEquals(schema, SubrecordSchema.TextureHashes))
        {
            return "TextureHashes";
        }

        return schema.Fields.Length == 0 ? "Marker" : $"TypedFields:{schema.Fields.Length}";
    }

    private static bool TryGetIntentionalRawReason(
        string recordType,
        string subrecord,
        out string reason)
    {
        if (IntentionalRawSubrecordReasons.TryGetValue((recordType, subrecord), out var specificReason))
        {
            reason = specificReason;
            return true;
        }

        if (IntentionalRawSubrecordReasons.TryGetValue(("*", subrecord), out var wildcardReason))
        {
            reason = wildcardReason;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool TryGetCustomModeledReason(
        string recordType,
        string subrecord,
        int dataLength,
        out string reason)
    {
        if (recordType == "GMST" && subrecord == "DATA" && dataLength != 4)
        {
            reason = "Contextual GMST string DATA modeled through EDID type-prefix parsing.";
            return true;
        }

        if (CustomModeledSubrecordReasons.TryGetValue((recordType, subrecord), out var specificReason))
        {
            reason = specificReason;
            return true;
        }

        if (CustomModeledSubrecordReasons.TryGetValue(("*", subrecord), out var wildcardReason))
        {
            reason = wildcardReason;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string FormatExamples(IEnumerable<uint> formIds)
    {
        return string.Join(" ", formIds
            .Where(id => id != 0)
            .Distinct()
            .Take(3)
            .Select(id => $"0x{id:X8}"));
    }

    private static string BuildRecordCsv(EsmCoverageResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("record_type,count,classification,parser_owner,encoder_owner,example_form_ids");
        foreach (var row in result.Records)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.RecordType),
                row.Count,
                row.Classification,
                Csv(row.ParserOwner),
                Csv(row.EncoderOwner),
                Csv(row.ExampleFormIds)));
        }

        return sb.ToString();
    }

    private static string BuildSubrecordCsv(EsmCoverageResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "record_type,subrecord,data_length,count,classification,schema_kind,uses_raw_byte_array,is_intentional_raw,coverage_note,parser_owner,encoder_owner,example_form_ids");
        foreach (var row in result.Subrecords)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.RecordType),
                Csv(row.Subrecord),
                row.DataLength,
                row.Count,
                row.Classification,
                Csv(row.SchemaKind),
                row.UsesRawByteArray,
                row.IsIntentionalRaw,
                Csv(row.CoverageNote),
                Csv(row.ParserOwner),
                Csv(row.EncoderOwner),
                Csv(row.ExampleFormIds)));
        }

        return sb.ToString();
    }

    private static string BuildScriptBytecodeCsv(EsmCoverageResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "record_type,form_id,block_index,scda_length,schr_compiled_size,schr_ref_object_count,actual_reference_slots,schr_variable_count,actual_variables,compiled_size_matches,ref_count_matches,variable_count_matches,walked_to_end,multi_byte_read_count,multi_byte_byte_count,has_diagnostics,diagnostics");
        foreach (var row in result.ScriptBytecode)
        {
            sb.AppendLine(string.Join(',',
                Csv(row.RecordType),
                Csv($"0x{row.FormId:X8}"),
                row.BlockIndex,
                row.ScdaLength,
                row.SchrCompiledSize?.ToString() ?? string.Empty,
                row.SchrRefObjectCount?.ToString() ?? string.Empty,
                row.ActualReferenceSlots,
                row.SchrVariableCount?.ToString() ?? string.Empty,
                row.ActualVariables,
                row.CompiledSizeMatches,
                row.RefCountMatches,
                row.VariableCountMatches,
                row.WalkedToEnd,
                row.MultiByteReadCount,
                row.MultiByteByteCount,
                row.HasDiagnostics,
                Csv(row.Diagnostics)));
        }

        return sb.ToString();
    }

    private static string BuildSummary(EsmCoverageResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# ESM Coverage: {Path.GetFileName(result.SourcePath)}");
        sb.AppendLine();
        sb.AppendLine($"- Record types: {result.TotalRecordTypes:N0}");
        sb.AppendLine($"- Records: {result.TotalRecords:N0}");
        sb.AppendLine($"- Subrecord shapes: {result.TotalSubrecordKinds:N0}");
        sb.AppendLine();
        sb.AppendLine("## Record Classification");
        sb.AppendLine();
        foreach (var group in result.Records.GroupBy(r => r.Classification).OrderBy(g => g.Key))
        {
            sb.AppendLine($"- {group.Key}: {group.Count():N0} type(s), {group.Sum(r => r.Count):N0} record(s)");
        }

        AppendScriptBytecodeSummary(sb, result.ScriptBytecode);

        var rawGaps = result.Subrecords
            .Where(r => r.UsesRawByteArray && !r.IsIntentionalRaw)
            .OrderByDescending(r => r.Count)
            .Take(25)
            .ToList();
        var intentionalRaw = result.Subrecords
            .Where(r => r.IsIntentionalRaw)
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.RecordType, StringComparer.Ordinal)
            .ThenBy(r => r.Subrecord, StringComparer.Ordinal)
            .Take(25)
            .ToList();

        sb.AppendLine();
        sb.AppendLine("## Largest Non-Intentional Raw Subrecords");
        sb.AppendLine();
        if (rawGaps.Count == 0)
        {
            sb.AppendLine("No non-intentional raw byte-array subrecords found.");
        }
        else
        {
            sb.AppendLine("| Record | Subrecord | Size | Count | Classification | Examples |");
            sb.AppendLine("|---|---|---:|---:|---|---|");
            foreach (var row in rawGaps)
            {
                sb.AppendLine(
                    $"| {row.RecordType} | {row.Subrecord} | {row.DataLength} | {row.Count:N0} | {row.Classification} | {row.ExampleFormIds} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Largest Intentional Opaque Subrecords");
        sb.AppendLine();
        if (intentionalRaw.Count == 0)
        {
            sb.AppendLine("No intentional opaque subrecords found.");
        }
        else
        {
            sb.AppendLine("| Record | Subrecord | Size | Count | Schema | Reason | Examples |");
            sb.AppendLine("|---|---|---:|---:|---|---|---|");
            foreach (var row in intentionalRaw)
            {
                sb.AppendLine(
                    $"| {row.RecordType} | {row.Subrecord} | {row.DataLength} | {row.Count:N0} | {row.SchemaKind} | {row.CoverageNote} | {row.ExampleFormIds} |");
            }
        }

        return sb.ToString();
    }

    private static void AppendScriptBytecodeSummary(StringBuilder sb, IReadOnlyList<EsmScriptBytecodeCoverageRow> rows)
    {
        sb.AppendLine();
        sb.AppendLine("## Script Bytecode Coverage");
        sb.AppendLine();

        if (rows.Count == 0)
        {
            sb.AppendLine("No SCDA bytecode blocks found.");
            return;
        }

        var compiledSizeMismatches = rows.Count(r => !r.CompiledSizeMatches);
        var refCountMismatches = rows.Count(r => !r.RefCountMatches);
        var walkFailures = rows.Count(r => !r.WalkedToEnd);
        var diagnostics = rows.Count(r => r.HasDiagnostics);

        sb.AppendLine($"- SCDA blocks: {rows.Count:N0}");
        sb.AppendLine($"- Walked to end: {rows.Count - walkFailures:N0}/{rows.Count:N0}");
        sb.AppendLine($"- SCHR compiled-size mismatches: {compiledSizeMismatches:N0}");
        sb.AppendLine($"- SCHR reference-count mismatches: {refCountMismatches:N0}");
        sb.AppendLine("- SLSD variables: counted in CSV only; SCHR's first dword is not a reliable vanilla variable count.");
        sb.AppendLine($"- Blocks with decoder diagnostics: {diagnostics:N0}");

        var notable = rows
            .Where(r => !r.CompiledSizeMatches || !r.RefCountMatches || !r.WalkedToEnd || r.HasDiagnostics)
            .OrderByDescending(r => r.ScdaLength)
            .Take(15)
            .ToList();
        if (notable.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("| Record | FormID | Block | Size | Issue | Diagnostics |");
        sb.AppendLine("|---|---|---:|---:|---|---|");
        foreach (var row in notable)
        {
            var issues = new List<string>();
            if (!row.CompiledSizeMatches)
            {
                issues.Add("compiled-size");
            }

            if (!row.RefCountMatches)
            {
                issues.Add("ref-count");
            }

            if (!row.WalkedToEnd)
            {
                issues.Add("walk");
            }

            if (row.HasDiagnostics)
            {
                issues.Add("diagnostics");
            }

            sb.AppendLine(
                $"| {row.RecordType} | 0x{row.FormId:X8} | {row.BlockIndex} | {row.ScdaLength} | {string.Join(' ', issues)} | {row.Diagnostics} |");
        }
    }

    private static string Csv(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        return text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}
