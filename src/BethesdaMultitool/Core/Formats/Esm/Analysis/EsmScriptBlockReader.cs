using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis;

/// <summary>
///     Shared low-level helpers for walking compiled-script (SCHR/SCDA) blocks inside an ESM/ESP record's subrecord
///     list. Used by both the script diagnostics and script provenance analyzers.
/// </summary>
internal static class EsmScriptBlockReader
{
    /// <summary>A single SCRO/SCRV reference slot inside a compiled-script block.</summary>
    public sealed record ScriptReferenceSlot(string Kind, uint RawValue);

    /// <summary>
    ///     Finds the index of the subrecord that terminates the current script block (the next block/event boundary),
    ///     or the subrecord count if no boundary follows.
    /// </summary>
    public static int FindScriptBlockEnd(List<ParsedSubrecord> subrecords, int start)
    {
        for (var i = start; i < subrecords.Count; i++)
        {
            if (subrecords[i].Signature is "NEXT" or "SCHR" or "POBA" or "POEA" or "POCA")
            {
                return i;
            }
        }

        return subrecords.Count;
    }

    /// <summary>Finds the index of the first subrecord with the given signature in [start, end), or -1.</summary>
    public static int FindFirstSubrecord(
        IReadOnlyList<ParsedSubrecord> subrecords,
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

    /// <summary>Reads SLSD+SCVR variable definitions from the subrecords in [start, end).</summary>
    public static List<ScriptVariableInfo> ReadScriptVariables(
        List<ParsedSubrecord> subrecords,
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
                    sub.DataAsString,
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

    /// <summary>Reads SCRO/SCRV reference slots from the subrecords in [start, end).</summary>
    public static List<ScriptReferenceSlot> ReadScriptReferences(
        List<ParsedSubrecord> subrecords,
        int start,
        int end)
    {
        var references = new List<ScriptReferenceSlot>();
        for (var i = start; i < end; i++)
        {
            var sub = subrecords[i];
            if (sub.Data.Length < 4)
            {
                continue;
            }

            if (sub.Signature == "SCRO")
            {
                references.Add(new ScriptReferenceSlot("SCRO", BinaryPrimitives.ReadUInt32LittleEndian(sub.Data)));
            }
            else if (sub.Signature == "SCRV")
            {
                references.Add(new ScriptReferenceSlot("SCRV", BinaryPrimitives.ReadUInt32LittleEndian(sub.Data)));
            }
        }

        return references;
    }

    /// <summary>Returns the string payload of the first subrecord with the given signature in [start, end), or "".</summary>
    public static string ReadFirstStringSubrecord(
        List<ParsedSubrecord> subrecords,
        string signature,
        int start,
        int end)
    {
        for (var i = start; i < end; i++)
        {
            if (subrecords[i].Signature == signature)
            {
                return subrecords[i].DataAsString;
            }
        }

        return string.Empty;
    }
}
