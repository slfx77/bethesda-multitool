using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Executes a planner-owned append-only SCPT-local directive. This deliberately bypasses
///     <c>RecordMergeEngine</c>: its positional merge would consume the first master SLSD/SCVR
///     slots and append surplus locals after SCRO/SCRV, which is not a valid SCPT ordering.
/// </summary>
internal static class MasterScriptVariableAugmentationEncoder
{
    private const int SchrVariableCountOffset = 12;
    private const int SlsdSize = 24;
    private const int SlsdIntegerFlagOffset = 16;

    internal static byte[] EncodeSubrecordStream(RecordPlan record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Type != "SCPT" || record.Master is null)
        {
            throw new InvalidOperationException(
                $"Master script augmentation requires an attached SCPT master (got {record.Type}).");
        }

        return EncodeSubrecordStream(
            record.Master,
            record.FormId,
            record.ScriptVariableAugmentations);
    }

    /// <summary>
    ///     Byte encoder for planner-owned master-script augmentation. Both overloads consume the
    ///     same immutable directives produced from the single DMP currently being converted.
    /// </summary>
    internal static byte[] EncodeSubrecordStream(
        ParsedMainRecord master,
        uint formId,
        IReadOnlyList<ScriptVariableAugmentation> augmentations)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(augmentations);
        ScriptVariableAugmentationPlanner.Validate(master, formId, augmentations);

        var schr = master.Subrecords.Single(static subrecord => subrecord.Signature == "SCHR");
        var sctx = master.Subrecords.SingleOrDefault(static subrecord => subrecord.Signature == "SCTX");
        var patchedSchr = schr.Data.ToArray();
        var originalVariableCount = BinaryPrimitives.ReadUInt32LittleEndian(
            patchedSchr.AsSpan(SchrVariableCountOffset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            patchedSchr.AsSpan(SchrVariableCountOffset, 4),
            checked(originalVariableCount + (uint)augmentations.Count));
        var patchedSctx = sctx is null ? null : InsertSourceDeclarations(sctx.Data, augmentations);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, true);
        var insertedLocals = false;
        foreach (var subrecord in master.Subrecords)
        {
            if (!insertedLocals && subrecord.Signature is "SCRO" or "SCRV")
            {
                WriteVariables(writer, augmentations);
                insertedLocals = true;
            }

            // If the master had no SCTX, never synthesize or borrow one. Otherwise the
            // target's own source is patched with declarations for every fresh local.
            if (subrecord.Signature == "SCTX" && patchedSctx is null)
            {
                continue;
            }

            var payload = subrecord.Signature switch
            {
                "SCHR" => patchedSchr,
                "SCTX" => patchedSctx!,
                _ => subrecord.Data
            };
            SubrecordEncoder.WriteSubrecord(writer, subrecord.Signature, payload);
        }

        if (!insertedLocals)
        {
            WriteVariables(writer, augmentations);
        }

        return stream.ToArray();
    }

    private static void WriteVariables(
        BinaryWriter writer,
        IReadOnlyList<ScriptVariableAugmentation> augmentations)
    {
        foreach (var augmentation in augmentations)
        {
            var variable = augmentation.Variable;
            var slsd = new byte[SlsdSize];
            BinaryPrimitives.WriteUInt32LittleEndian(slsd, variable.Index);
            slsd[SlsdIntegerFlagOffset] = variable.Type;
            SubrecordEncoder.WriteSubrecord(writer, "SLSD", slsd);
            SubrecordEncoder.WriteStringSubrecord(writer, "SCVR", variable.Name);
        }
    }

    private static byte[] InsertSourceDeclarations(
        byte[] masterPayload,
        IReadOnlyList<ScriptVariableAugmentation> augmentations)
    {
        // Preserve the master's exact terminator/trailing bytes. SCTX is normally one
        // Windows-1252 string plus NUL, but retaining the tail avoids normalizing unusual yet
        // valid master payloads while adding declarations to the textual portion only.
        var nulIndex = Array.IndexOf(masterPayload, (byte)0);
        var textLength = nulIndex >= 0 ? nulIndex : masterPayload.Length;
        var source = EsmStringUtils.DecodeGameText(masterPayload.AsSpan(0, textLength));
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var declarations = string.Join(
            newline,
            augmentations.Select(static augmentation =>
                $"{DeclarationKeyword(ScriptVariableDeclarationIdentity.ForFreshLocal(augmentation.DeclarationKind))} "
                + augmentation.Variable.Name));

        var insertionIndex = FindFirstBeginLine(source);
        string augmentedSource;
        if (insertionIndex >= 0)
        {
            // Inserting immediately before the first Begin keeps SCTX valid script source;
            // appending after the final End would turn the recovered declaration into text
            // that the GECK compiler rejects if a user later inspects/recompiles the record.
            augmentedSource = source.Insert(insertionIndex, declarations + newline);
        }
        else
        {
            var separator = source.Length == 0 || source.EndsWith('\n') || source.EndsWith('\r')
                ? string.Empty
                : newline;
            augmentedSource = source + separator + declarations;
        }

        var encoded = EsmStringUtils.EncodeGameText(augmentedSource);
        if (nulIndex < 0)
        {
            return encoded;
        }

        var result = new byte[encoded.Length + masterPayload.Length - nulIndex];
        encoded.CopyTo(result, 0);
        masterPayload.AsSpan(nulIndex).CopyTo(result.AsSpan(encoded.Length));
        return result;
    }

    private static string DeclarationKeyword(ScriptVariableDeclarationKind kind)
    {
        return kind switch
        {
            ScriptVariableDeclarationKind.Short => "short",
            ScriptVariableDeclarationKind.Long => "long",
            ScriptVariableDeclarationKind.Int => "int",
            ScriptVariableDeclarationKind.Float => "float",
            ScriptVariableDeclarationKind.Reference => "ref",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static int FindFirstBeginLine(string source)
    {
        var lineStart = 0;
        while (lineStart < source.Length)
        {
            var lineEnd = source.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = source.Length;
            }

            var contentStart = lineStart;
            while (contentStart < lineEnd && char.IsWhiteSpace(source[contentStart]))
            {
                contentStart++;
            }

            const string begin = "begin";
            if (lineEnd - contentStart >= begin.Length
                && source.AsSpan(contentStart, begin.Length).Equals(begin, StringComparison.OrdinalIgnoreCase)
                && (contentStart + begin.Length == lineEnd
                    || char.IsWhiteSpace(source[contentStart + begin.Length])))
            {
                return lineStart;
            }

            if (lineEnd == source.Length)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }

        return -1;
    }
}
