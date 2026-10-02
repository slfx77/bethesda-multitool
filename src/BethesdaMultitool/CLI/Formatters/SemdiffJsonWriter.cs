using System.Globalization;
using System.Text.Json;

namespace BethesdaMultitool.CLI.Formatters;

/// <summary>
///     Writes the <c>esm semdiff --format json</c> document with <see cref="Utf8JsonWriter" />, never the
///     reflection-based <see cref="JsonSerializer" />: the published CLI is trimmed and ships with
///     <c>System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false</c>, under which reflection
///     serialization throws while the test host passes it. It serializes a
///     <see cref="SemdiffTypes.SemdiffResult" /> and decides nothing: pairing and verdicts are
///     <see cref="SemdiffComparer" />'s, and every decoded value is the string the table prints, from
///     <see cref="SemdiffFieldFormatter.BuildFieldRows" />, so the two formats cannot drift.
///     <para>
///         Schema <c>bethesda-multitool/esm-semdiff</c>, version 1. One object: <c>schema</c>,
///         <c>schemaVersion</c>, <c>toolVersion</c>, <c>files</c> (a and b), <c>query</c>, <c>summary</c>
///         (counts over EVERY record the comparison saw, plus <c>listed</c>, <c>emitted</c> and
///         <c>truncated</c>), <c>warnings</c> and <c>records</c> (the listed records, at most
///         <see cref="SemdiffTypes.SemdiffQuery.Limit" />). FormIDs are <c>0x%08X</c> strings; every field is
///         always present and an absent value is an explicit null. A record whose signatures differ is
///         <c>comparison: "refused"</c>: its <c>subrecords</c> array is empty and each side carries only its
///         own header and subrecord inventory, never a field decoded with the other side's schema.
///         Decoded field values are display strings; <c>rawA</c>/<c>rawB</c> hold each differing
///         subrecord's exact bytes as hex, each in its own file's byte order. Later additions keep the
///         version; only a breaking change bumps it.
///     </para>
/// </summary>
internal static class SemdiffJsonWriter
{
    /// <summary>The document's <c>schema</c> identifier.</summary>
    internal const string Schema = "bethesda-multitool/esm-semdiff";

    /// <summary>The document's <c>schemaVersion</c>. Bump only for a breaking change; additions keep it.</summary>
    internal const int SchemaVersion = 1;

    /// <summary>How many underlying warnings a collapsed <c>duplicate-formid</c> entry lists.</summary>
    internal const int MaxDuplicateFormIdExamples = 20;

    private const string DuplicateFormIdCode = "duplicate-formid";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>
    ///     Writes one JSON object to <paramref name="output" />. Flushes its own writer but neither closes
    ///     nor disposes <paramref name="output" />, and appends no trailing newline.
    /// </summary>
    internal static void Write(Stream output, SemdiffTypes.SemdiffQuery query, SemdiffTypes.SemdiffResult result,
        SemdiffTypes.SemdiffFileInfo fileA, SemdiffTypes.SemdiffFileInfo fileB)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(fileA);
        ArgumentNullException.ThrowIfNull(fileB);

        var emitted = Math.Min(Math.Max(query.Limit, 0), result.Records.Count);

        using var writer = new Utf8JsonWriter(output, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("schema", Schema);
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("toolVersion", CliConsoles.ToolVersion);

        writer.WriteStartObject("files");
        WriteFile(writer, "a", fileA);
        WriteFile(writer, "b", fileB);
        writer.WriteEndObject();

        WriteQuery(writer, query);
        WriteSummary(writer, result.Summary, result.Records.Count, emitted);

        writer.WriteStartArray("warnings");
        foreach (var entry in SummarizeWarnings(result.Warnings, fileA.Label, fileB.Label))
        {
            WriteWarning(writer, entry);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("records");
        for (var i = 0; i < emitted; i++)
        {
            WriteRecord(writer, result.Records[i]);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>
    ///     The warnings as the document and the stderr lines report them, in the order they were raised:
    ///     one entry per warning, except that every <c>duplicate-formid</c> warning of one file becomes a
    ///     single entry (at the position of the first) with the number of repeated FormIDs and the first
    ///     <see cref="MaxDuplicateFormIdExamples" /> warnings as examples.
    /// </summary>
    internal static List<SemdiffTypes.SemdiffWarningEntry> SummarizeWarnings(
        IReadOnlyList<SemdiffTypes.SemdiffWarning> warnings, string labelA, string labelB)
    {
        var entries = new List<SemdiffTypes.SemdiffWarningEntry>();
        var collapsedSides = new HashSet<SemdiffTypes.SemdiffSide?>();

        foreach (var warning in warnings)
        {
            if (!string.Equals(warning.Code, DuplicateFormIdCode, StringComparison.Ordinal))
            {
                entries.Add(new SemdiffTypes.SemdiffWarningEntry(warning.Code, warning.Side, warning.FormId, 1,
                    warning.Message, []));
                continue;
            }

            if (!collapsedSides.Add(warning.Side))
            {
                continue;
            }

            var side = warning.Side;
            var group = warnings
                .Where(w => string.Equals(w.Code, DuplicateFormIdCode, StringComparison.Ordinal) && w.Side == side)
                .ToList();
            entries.Add(CollapseDuplicates(group, SideLabel(side, labelA, labelB)));
        }

        return entries;
    }

    private static SemdiffTypes.SemdiffWarningEntry CollapseDuplicates(List<SemdiffTypes.SemdiffWarning> group,
        string label)
    {
        var first = group[0];
        var examples = group.Take(MaxDuplicateFormIdExamples).ToList();
        if (group.Count == 1)
        {
            return new SemdiffTypes.SemdiffWarningEntry(first.Code, first.Side, first.FormId, 1, first.Message,
                examples);
        }

        var message =
            $"{group.Count.ToString(CultureInfo.InvariantCulture)} FormIDs occur more than once in {label}; the " +
            "occurrences of each were paired by file order (the first " +
            $"{examples.Count.ToString(CultureInfo.InvariantCulture)} are listed as examples)";
        return new SemdiffTypes.SemdiffWarningEntry(first.Code, first.Side, null, group.Count, message, examples);
    }

    private static string SideLabel(SemdiffTypes.SemdiffSide? side, string labelA, string labelB)
    {
        return side switch
        {
            SemdiffTypes.SemdiffSide.A => labelA,
            SemdiffTypes.SemdiffSide.B => labelB,
            _ => "a file"
        };
    }

    private static void WriteFile(Utf8JsonWriter writer, string propertyName, SemdiffTypes.SemdiffFileInfo file)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("label", file.Label);
        writer.WriteString("path", file.Path);
        writer.WriteString("fileName", file.FileName);
        writer.WriteNumber("sizeBytes", file.SizeBytes);
        writer.WriteString("endianness", file.BigEndian ? "big" : "little");
        writer.WriteString("game", file.Game.ToString());
        writer.WriteStartArray("masters");
        foreach (var master in file.Masters)
        {
            writer.WriteStringValue(master);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteQuery(Utf8JsonWriter writer, SemdiffTypes.SemdiffQuery query)
    {
        writer.WriteStartObject("query");
        WriteFormIdOrNull(writer, "formId", query.FormId);
        WriteStringOrNull(writer, "type", string.IsNullOrWhiteSpace(query.RecordType) ? null : query.RecordType);
        writer.WriteString("match", query.Match switch
        {
            SemdiffTypes.MatchMode.EditorId => "editorid",
            SemdiffTypes.MatchMode.ExplicitMap => "map",
            _ => "formid"
        });
        writer.WriteStartArray("maps");
        foreach (var mapping in query.Maps)
        {
            writer.WriteStartObject();
            writer.WriteString("a", FormatFormId(mapping.FormIdA));
            writer.WriteString("b", FormatFormId(mapping.FormIdB));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("limit", query.Limit);
        writer.WriteBoolean("all", query.ShowAll);
        writer.WriteEndObject();
    }

    private static void WriteSummary(Utf8JsonWriter writer, SemdiffTypes.SemdiffSummary summary, int listed,
        int emitted)
    {
        writer.WriteStartObject("summary");
        writer.WriteNumber("compared", summary.Compared);
        writer.WriteNumber("withDifferences", summary.WithDifferences);
        writer.WriteNumber("different", summary.Different);
        writer.WriteNumber("signatureMismatch", summary.SignatureMismatch);
        writer.WriteNumber("onlyInA", summary.OnlyInA);
        writer.WriteNumber("onlyInB", summary.OnlyInB);
        writer.WriteNumber("ambiguous", summary.Ambiguous);
        writer.WriteNumber("formVersionOnly", summary.FormVersionOnly);
        writer.WriteNumber("nonSemanticHeaderOnly", summary.NonSemanticHeaderOnly);
        writer.WriteNumber("identical", summary.Identical);
        writer.WriteNumber("listed", listed);
        writer.WriteNumber("emitted", emitted);
        writer.WriteBoolean("truncated", emitted < listed);
        writer.WriteEndObject();
    }

    private static void WriteWarning(Utf8JsonWriter writer, SemdiffTypes.SemdiffWarningEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("code", entry.Code);
        WriteSideOrNull(writer, "side", entry.Side);
        WriteFormIdOrNull(writer, "formId", entry.FormId);
        writer.WriteNumber("count", entry.Count);
        writer.WriteString("message", entry.Message);
        writer.WriteStartArray("examples");
        foreach (var example in entry.Examples)
        {
            writer.WriteStartObject();
            WriteFormIdOrNull(writer, "formId", example.FormId);
            writer.WriteString("message", example.Message);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRecord(Utf8JsonWriter writer, SemdiffTypes.RecordDiff diff)
    {
        var refused = diff.DiffType == SemdiffTypes.DiffType.SignatureMismatch;

        writer.WriteStartObject();
        writer.WriteString("status", StatusName(diff.DiffType));
        writer.WriteString("signature", diff.RecordType);
        writer.WriteString("formId", FormatFormId(diff.FormId));
        writer.WriteString("formIdB", FormatFormId(diff.FormIdB));
        writer.WriteString("matchedBy", diff.MatchedBy switch
        {
            SemdiffTypes.MatchedBy.EditorId => "editorId",
            SemdiffTypes.MatchedBy.FormIdFallback => "formIdFallback",
            SemdiffTypes.MatchedBy.ExplicitMap => "map",
            _ => "formId"
        });
        writer.WriteString("comparison", ComparisonName(diff.DiffType));
        WriteStringOrNull(writer, "refusalReason", refused ? "signature-mismatch" : null);
        WriteStringOrNull(writer, "editorIdA", diff.EditorIdA ?? diff.RecordA?.EditorId);
        WriteStringOrNull(writer, "editorIdB", diff.EditorIdB ?? diff.RecordB?.EditorId);

        WriteSide(writer, "a", diff.RecordA, diff.OccurrenceA, diff.FlagNamesA, diff.InventoryA);
        WriteSide(writer, "b", diff.RecordB, diff.OccurrenceB, diff.FlagNamesB, diff.InventoryB);

        WriteStringOrNull(writer, "editorIdLookup", diff.EditorIdHint);
        WriteStringOrNull(writer, "subrecordOrderNote", diff.SubrecordOrderNote);
        WriteHeaderComparison(writer, diff.Header);
        WriteAmbiguous(writer, diff);

        writer.WriteStartArray("subrecords");
        if (!refused && diff.FieldDiffs != null)
        {
            foreach (var fieldDiff in diff.FieldDiffs)
            {
                WriteSubrecord(writer, fieldDiff, diff);
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSide(Utf8JsonWriter writer, string propertyName, SemdiffTypes.ParsedRecord? record,
        int occurrence, IReadOnlyList<string> flagNames,
        IReadOnlyList<SemdiffTypes.SubrecordInventoryEntry>? inventory)
    {
        if (record == null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartObject(propertyName);
        writer.WriteString("signature", record.Type);
        writer.WriteString("formId", FormatFormId(record.FormId));
        WriteStringOrNull(writer, "editorId", record.EditorId);
        writer.WriteNumber("offset", record.Offset);
        writer.WriteNumber("occurrence", occurrence);

        writer.WriteStartObject("header");
        writer.WriteNumber("dataSize", record.DataSize);
        writer.WriteStartObject("flags");
        writer.WriteNumber("value", record.Flags);
        writer.WriteString("hex", FormatHex32(record.Flags));
        writer.WriteStartArray("names");
        foreach (var name in flagNames)
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteNumber("formVersion", record.FormVersion);
        writer.WriteString("versionControl1", SemdiffRecordParser.FormatVersionControl1(record.VersionControl1));
        writer.WriteNumber("versionControl2", record.VersionControl2);
        writer.WriteEndObject();

        writer.WriteStartArray("subrecordInventory");
        foreach (var entry in inventory ?? SemdiffRecordParser.BuildSubrecordInventory(record))
        {
            writer.WriteStartObject();
            writer.WriteString("signature", entry.Signature);
            writer.WriteNumber("count", entry.Count);
            writer.WriteStartArray("sizes");
            foreach (var size in entry.Sizes)
            {
                writer.WriteNumberValue(size);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteHeaderComparison(Utf8JsonWriter writer, SemdiffTypes.HeaderComparison? header)
    {
        if (header == null)
        {
            writer.WriteNull("header");
            return;
        }

        writer.WriteStartObject("header");
        writer.WriteString("flagsA", FormatHex32(header.FlagsA));
        writer.WriteString("flagsB", FormatHex32(header.FlagsB));
        WriteBitDeltas(writer, "flagsAdded", header.Added);
        WriteBitDeltas(writer, "flagsRemoved", header.Removed);

        writer.WriteStartArray("fields");
        foreach (var field in header.Fields)
        {
            writer.WriteStartObject();
            writer.WriteString("name", field.Field);
            writer.WriteString("a", field.ValueA);
            writer.WriteString("b", field.ValueB);
            writer.WriteString("class", ClassName(field.Class));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("countedDelta", header.HasCountedDelta);
        writer.WriteBoolean("formVersionDelta", header.HasFormVersionDelta);
        writer.WriteBoolean("bookkeepingDelta", header.HasBookkeepingDelta);
        writer.WriteEndObject();
    }

    private static void WriteBitDeltas(Utf8JsonWriter writer, string propertyName,
        IReadOnlyList<SemdiffTypes.FlagBitDelta> deltas)
    {
        writer.WriteStartArray(propertyName);
        foreach (var delta in deltas)
        {
            writer.WriteStartObject();
            writer.WriteNumber("bit", delta.Bit);
            writer.WriteString("mask", FormatHex32(delta.Mask));
            WriteStringOrNull(writer, "name", delta.Name);
            writer.WriteString("class", ClassName(delta.Class));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteAmbiguous(Utf8JsonWriter writer, SemdiffTypes.RecordDiff diff)
    {
        if (diff.DiffType != SemdiffTypes.DiffType.Ambiguous)
        {
            writer.WriteNull("ambiguousFormIds");
            return;
        }

        writer.WriteStartObject("ambiguousFormIds");
        WriteFormIdArray(writer, "a", diff.AmbiguousFormIdsA);
        WriteFormIdArray(writer, "b", diff.AmbiguousFormIdsB);
        writer.WriteEndObject();
    }

    private static void WriteFormIdArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<uint> formIds)
    {
        writer.WriteStartArray(propertyName);
        foreach (var formId in formIds)
        {
            writer.WriteStringValue(FormatFormId(formId));
        }

        writer.WriteEndArray();
    }

    /// <summary>
    ///     One subrecord difference: where it sits on each side, its decoded rows (the table's rows, from
    ///     <see cref="SemdiffFieldFormatter.BuildFieldRows" />) and its exact bytes.
    /// </summary>
    private static void WriteSubrecord(Utf8JsonWriter writer, SemdiffTypes.FieldDiff fieldDiff,
        SemdiffTypes.RecordDiff diff)
    {
        var (indexA, positionA) = Locate(diff.RecordA, fieldDiff.Signature, fieldDiff.DataA);
        var (indexB, positionB) = Locate(diff.RecordB, fieldDiff.Signature, fieldDiff.DataB);

        writer.WriteStartObject();
        writer.WriteString("signature", fieldDiff.Signature);
        writer.WriteString("status", SubrecordStatus(fieldDiff));
        WriteStringOrNull(writer, "message", fieldDiff.Message);
        WriteNumberOrNull(writer, "indexA", indexA);
        WriteNumberOrNull(writer, "indexB", indexB);
        WriteNumberOrNull(writer, "positionA", positionA);
        WriteNumberOrNull(writer, "positionB", positionB);
        WriteNumberOrNull(writer, "sizeA", fieldDiff.DataA?.Length);
        WriteNumberOrNull(writer, "sizeB", fieldDiff.DataB?.Length);
        writer.WriteBoolean("byteOrderUnresolved", fieldDiff.ByteOrderUnresolved);
        WriteNumberOrNull(writer, "firstDifferingOffset", fieldDiff.FirstDifferingOffset);

        writer.WriteStartArray("fields");
        foreach (var row in SemdiffFieldFormatter.BuildFieldRows(fieldDiff))
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Field);
            WriteStringOrNull(writer, "a", row.ValueA);
            WriteStringOrNull(writer, "b", row.ValueB);
            writer.WriteBoolean("equal", row.Equal);
            WriteStringOrNull(writer, "note", row.Message);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteStringOrNull(writer, "rawA", fieldDiff.DataA != null ? Convert.ToHexString(fieldDiff.DataA) : null);
        WriteStringOrNull(writer, "rawB", fieldDiff.DataB != null ? Convert.ToHexString(fieldDiff.DataB) : null);
        writer.WriteEndObject();
    }

    /// <summary>
    ///     The 0-based instance index of the subrecord among the record's subrecords of its signature, and
    ///     its 0-based position in the record's whole subrecord sequence. A difference carries the parsed
    ///     payload array itself, so it is found by reference; null when the side has no such subrecord.
    /// </summary>
    private static (int? Index, int? Position) Locate(SemdiffTypes.ParsedRecord? record, string signature,
        byte[]? data)
    {
        if (record == null || data == null)
        {
            return (null, null);
        }

        var index = 0;
        for (var position = 0; position < record.Subrecords.Count; position++)
        {
            var subrecord = record.Subrecords[position];
            if (!string.Equals(subrecord.Signature, signature, StringComparison.Ordinal))
            {
                continue;
            }

            if (ReferenceEquals(subrecord.Data, data))
            {
                return (index, position);
            }

            index++;
        }

        return (null, null);
    }

    private static string SubrecordStatus(SemdiffTypes.FieldDiff fieldDiff)
    {
        if (fieldDiff.DataB == null)
        {
            return "onlyInA";
        }

        if (fieldDiff.DataA == null)
        {
            return "onlyInB";
        }

        return fieldDiff.ByteOrderUnresolved ? "byteOrderUnresolved" : "different";
    }

    private static string StatusName(SemdiffTypes.DiffType diffType)
    {
        return diffType switch
        {
            SemdiffTypes.DiffType.OnlyInA => "onlyInA",
            SemdiffTypes.DiffType.OnlyInB => "onlyInB",
            SemdiffTypes.DiffType.Different => "different",
            SemdiffTypes.DiffType.Identical => "identical",
            SemdiffTypes.DiffType.NonSemanticHeaderOnly => "nonSemanticHeaderOnly",
            SemdiffTypes.DiffType.SignatureMismatch => "signatureMismatch",
            SemdiffTypes.DiffType.Ambiguous => "ambiguous",
            SemdiffTypes.DiffType.FormVersionOnly => "formVersionOnly",
            _ => diffType.ToString()
        };
    }

    /// <summary>
    ///     <c>typed</c> for a same-signature pair (compared with one schema), <c>refused</c> for a pair of
    ///     different signatures, <c>none</c> where no pair was formed (a record on one side only, or an
    ///     ambiguous EditorID).
    /// </summary>
    private static string ComparisonName(SemdiffTypes.DiffType diffType)
    {
        return diffType switch
        {
            SemdiffTypes.DiffType.SignatureMismatch => "refused",
            SemdiffTypes.DiffType.OnlyInA or SemdiffTypes.DiffType.OnlyInB or SemdiffTypes.DiffType.Ambiguous =>
                "none",
            _ => "typed"
        };
    }

    private static string ClassName(SemdiffTypes.HeaderDeltaClass deltaClass)
    {
        return deltaClass switch
        {
            SemdiffTypes.HeaderDeltaClass.Semantic => "semantic",
            SemdiffTypes.HeaderDeltaClass.Storage => "storage",
            SemdiffTypes.HeaderDeltaClass.Format => "format",
            SemdiffTypes.HeaderDeltaClass.Bookkeeping => "bookkeeping",
            _ => deltaClass.ToString()
        };
    }

    private static void WriteSideOrNull(Utf8JsonWriter writer, string propertyName, SemdiffTypes.SemdiffSide? side)
    {
        switch (side)
        {
            case SemdiffTypes.SemdiffSide.A:
                writer.WriteString(propertyName, "A");
                break;
            case SemdiffTypes.SemdiffSide.B:
                writer.WriteString(propertyName, "B");
                break;
            default:
                writer.WriteNull(propertyName);
                break;
        }
    }

    private static void WriteFormIdOrNull(Utf8JsonWriter writer, string propertyName, uint? formId)
    {
        if (formId is { } value)
        {
            writer.WriteString(propertyName, FormatFormId(value));
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value == null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string propertyName, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(propertyName, number);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static string FormatFormId(uint formId)
    {
        return FormatHex32(formId);
    }

    private static string FormatHex32(uint value)
    {
        return "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
    }
}
