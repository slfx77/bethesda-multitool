using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Script;
using ScriptReferenceSlot = BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics.EsmScriptBlockReader.ScriptReferenceSlot;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>
///     Stateless classification, hashing, byte-formatting, and label-resolution helpers used by the script provenance
///     analyzer.
/// </summary>
internal static class EsmScriptProvenanceClassifier
{
    public static string ClassifyReference(ScriptReferenceSlot? source, ScriptReferenceSlot? emitted)
    {
        if (source is null)
        {
            return emitted is null ? "Resolved" : "ExtraEmittedSlot";
        }

        if (emitted is null)
        {
            return "MissingSourceSlot";
        }

        if (source.Kind == "SCRV" && emitted.Kind == "SCRO")
        {
            return "SourceScrvEmittedAsScro";
        }

        if (source.Kind == "SCRO" && source.RawValue == 0)
        {
            return "SourceNull";
        }

        if (source.Kind == "SCRO" && source.RawValue != 0 &&
            emitted.Kind == "SCRO" && emitted.RawValue == 0)
        {
            return "ConverterNulledNonZeroSource";
        }

        return "Resolved";
    }

    public static string ClassifyResultScript(
        IReadOnlyList<BlockSnapshot> source,
        IReadOnlyList<BlockSnapshot> emitted)
    {
        if (source.Count == 0)
        {
            return "SourceMissing";
        }

        var sourceWithContent = source.Where(HasScriptContent).ToList();
        var emittedWithContent = emitted.Where(HasScriptContent).ToList();
        if (sourceWithContent.Count == 0)
        {
            return "SourceEmpty";
        }

        if (emittedWithContent.Count == 0)
        {
            return emitted.Count > 0 ? "EmittedPlaceholderOnly" : "SkippedOverrideScript";
        }

        return FormatHashes(sourceWithContent) == FormatHashes(emittedWithContent) &&
               string.Join(' ', sourceWithContent.Select(s => s.References.Count)) ==
               string.Join(' ', emittedWithContent.Select(s => s.References.Count))
            ? "Preserved"
            : "ContentMismatch";
    }

    public static bool HasScriptContent(BlockSnapshot block)
    {
        return block.Scda.Length > 0 ||
               !string.IsNullOrWhiteSpace(block.SourceText) ||
               block.References.Count > 0;
    }

    public static string ClassifyEndianProbe(ScriptBytecodeAnalysis littleEndian, ScriptBytecodeAnalysis bigEndian)
    {
        var leClean = littleEndian.WalkedToEnd && !littleEndian.HasDiagnostics;
        var beClean = bigEndian.WalkedToEnd && !bigEndian.HasDiagnostics;
        if (leClean)
        {
            return "WalksLe";
        }

        if (beClean)
        {
            return "WouldSwapFix";
        }

        return (littleEndian.Diagnostics + " " + bigEndian.Diagnostics)
            .Contains("Unknown opcode", StringComparison.Ordinal)
            ? "WalkerGap"
            : "CorruptOrTruncated";
    }

    public static string ResolveReferenceLabel(
        IReadOnlyDictionary<uint, LabelInfo> labels,
        ScriptReferenceSlot? reference)
    {
        if (reference is null || reference.Kind == "SCRV")
        {
            return reference is null ? string.Empty : $"SCRV[{reference.RawValue}]";
        }

        return ResolveLabel(labels, reference.RawValue);
    }

    public static string ResolveLabel(IReadOnlyDictionary<uint, LabelInfo> labels, uint formId)
    {
        if (formId == 0 || !labels.TryGetValue(formId, out var label))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(label.EditorId))
        {
            return label.EditorId;
        }

        if (!string.IsNullOrWhiteSpace(label.FullName))
        {
            return label.FullName;
        }

        return label.RecordType;
    }

    public static string FormatHashes(IReadOnlyList<BlockSnapshot> blocks)
    {
        return string.Join(' ', blocks.Select(b => b.Scda.Length == 0 ? "empty" : ShortSha256(b.Scda)));
    }

    public static string ShortSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    public static string FormatFirstBytes(byte[] bytes)
    {
        return bytes.Length == 0
            ? string.Empty
            : Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 8))).ToLowerInvariant();
    }

    public static string FormatOpcode(byte[] bytes, bool bigEndian)
    {
        if (bytes.Length < 2)
        {
            return string.Empty;
        }

        var opcode = bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2));
        return $"0x{opcode:X4}";
    }

    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    public static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }
}
