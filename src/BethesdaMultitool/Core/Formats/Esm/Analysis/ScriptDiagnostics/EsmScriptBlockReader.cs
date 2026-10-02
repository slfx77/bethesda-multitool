using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;

/// <summary>
///     Shared low-level helpers for walking compiled-script (SCHR/SCDA) blocks inside an ESM/ESP record's subrecord
///     list. Used by both the script diagnostics and script provenance analyzers.
/// </summary>
internal static class EsmScriptBlockReader
{
    /// <summary>
    ///     Finds the index of the subrecord that terminates the current script block (the next block/event boundary),
    ///     or the subrecord count if no boundary follows.
    ///     <para>
    ///         <c>ITXT</c> opens the next TERM menu item, so a menu item's embedded script ends there instead of
    ///         absorbing the next item's ITXT/RNAM/ANAM/INAM/TNAM. ITXT never occurs inside an INFO, QUST, SCPT,
    ///         PACK or PERK script bundle, so those blocks are unchanged.
    ///     </para>
    /// </summary>
    public static int FindScriptBlockEnd(List<ParsedSubrecord> subrecords, int start, bool questBoundaries = false)
    {
        for (var i = start; i < subrecords.Count; i++)
        {
            if (subrecords[i].Signature is "NEXT" or "SCHR" or "POBA" or "POEA" or "POCA" or "ITXT"
                || (questBoundaries && subrecords[i].Signature is "INDX" or "QSDT" or "QOBJ" or "QSTA"))
            {
                return i;
            }
        }

        return subrecords.Count;
    }

    /// <summary>
    ///     Locates every compiled-script block of a record and numbers it the way the diagnostics rows do: each
    ///     SCHR opens a block (1, 2, ... in record order), then every SCDA not already claimed by a SCHR block
    ///     opens an implicit block numbered after them. Every consumer that reports a <c>block_index</c> must
    ///     number blocks through this method so the indexes join across reports.
    /// </summary>
    public static List<ScriptBlockSpan> LocateScriptBlocks(List<ParsedSubrecord> subrecords, bool questBoundaries = false)
    {
        var blocks = new List<ScriptBlockSpan>();
        var claimedScda = new HashSet<int>();
        var blockIndex = 0;

        for (var i = 0; i < subrecords.Count; i++)
        {
            if (subrecords[i].Signature != "SCHR")
            {
                continue;
            }

            var end = FindScriptBlockEnd(subrecords, i + 1, questBoundaries);
            var scdaIndex = FindFirstSubrecord(subrecords, "SCDA", i + 1, end);
            blockIndex++;
            if (scdaIndex >= 0)
            {
                claimedScda.Add(scdaIndex);
            }

            blocks.Add(new ScriptBlockSpan(blockIndex, i, scdaIndex, end));
        }

        for (var i = 0; i < subrecords.Count; i++)
        {
            if (subrecords[i].Signature != "SCDA" || claimedScda.Contains(i))
            {
                continue;
            }

            blockIndex++;
            blocks.Add(new ScriptBlockSpan(blockIndex, -1, i, FindScriptBlockEnd(subrecords, i + 1, questBoundaries)));
        }

        return blocks;
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

    /// <summary>
    ///     Reads SLSD+SCVR variable definitions from the subrecords in [start, end). The SLSD index
    ///     is read in its subrecord's container order (big-endian inside an Xbox 360 record).
    /// </summary>
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
                pendingIndex = ReadContainerUInt32(sub);
                pendingType = ScriptLocalVariableLayout.ReadType(sub.Data);
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

    /// <summary>
    ///     Reads SCRO/SCRV reference slots from the subrecords in [start, end), each in its
    ///     subrecord's container order (big-endian inside an Xbox 360 record).
    /// </summary>
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
                references.Add(new ScriptReferenceSlot("SCRO", ReadContainerUInt32(sub)));
            }
            else if (sub.Signature == "SCRV")
            {
                references.Add(new ScriptReferenceSlot("SCRV", ReadContainerUInt32(sub)));
            }
        }

        return references;
    }

    /// <summary>
    ///     Reads the leading u32 of a script-table subrecord (SLSD index, SCRO FormID, SCRV local
    ///     ID) in the byte order of the record it was parsed from. These integers follow the
    ///     container; only the SCDA bytecode beside them is little-endian on every platform.
    /// </summary>
    private static uint ReadContainerUInt32(ParsedSubrecord sub)
    {
        return sub.BigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data.AsSpan(0, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(0, 4));
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

    /// <summary>A single SCRO/SCRV reference slot inside a compiled-script block.</summary>
    public sealed record ScriptReferenceSlot(string Kind, uint RawValue);

    /// <summary>
    ///     One compiled-script block located by <see cref="LocateScriptBlocks" />: its 1-based
    ///     <c>BlockIndex</c>, the subrecord index of its SCHR (-1 for an implicit SCDA-only block), of its
    ///     SCDA (-1 when the block has none) and of the boundary that ends it (<c>End</c>, exclusive).
    /// </summary>
    public readonly record struct ScriptBlockSpan(int BlockIndex, int SchrIndex, int ScdaIndex, int End)
    {
        /// <summary>The subrecord that opens the block: its SCHR, or its SCDA when there is no SCHR.</summary>
        public int StartIndex => SchrIndex >= 0 ? SchrIndex : ScdaIndex;
    }
}
