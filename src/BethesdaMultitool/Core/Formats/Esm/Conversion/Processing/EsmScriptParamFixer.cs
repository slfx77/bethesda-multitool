using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Indexing;

namespace BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;

/// <summary>
///     Rewrites July-2010-era IsPlayerInRegion call sites in compiled SCPT bytecode.
///     <para>
///     The July-era compiler encoded the Region parameter of IsPlayerInRegion (function opcode
///     0x1260) INSIDE the compiled SCDA bytecode as an inline editor-ID string. Retail PC
///     FalloutNV.exe cannot resolve string-encoded FORM parameters (it logs
///     "Invalid Parameter used in script IsPlayerInRegion" and the region-gated logic never runs).
///     PC final proves the fix shape: Obsidian recompiled these scripts, replacing each inline
///     string with a `72 &lt;u16 index&gt;` reference into the script's SCRO table.
///     </para>
///     <para>
///     This pass runs AFTER normal subrecord conversion: the record data handed to
///     <see cref="FixScriptRegionParams" /> already has little-endian subrecord headers and
///     little-endian SCDA/SCRO payloads. Only opcode 0x1260 is touched — functions whose
///     parameter type genuinely IS a string (e.g. GetGameSetting, 0x1100) are left alone.
///     </para>
/// </summary>
public sealed class EsmScriptParamFixer(IReadOnlyDictionary<string, uint> regionFormIdsByEdid, EsmConversionStats stats)
{
    private const int SubrecordHeaderSize = 6;
    private const int MaxRegionEdidLength = 64;

    private readonly IReadOnlyDictionary<string, uint> _regionFormIdsByEdid = regionFormIdsByEdid;
    private readonly EsmConversionStats _stats = stats;

    /// <summary>
    ///     Pre-scans the big-endian Xbox 360 input for REGN records and builds a
    ///     case-insensitive EDID → FormID map for inline-string resolution.
    /// </summary>
    public static Dictionary<string, uint> BuildRegionEdidIndex(byte[] input)
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in EsmRecordParser.ScanForRecordType(input, true, "REGN"))
        {
            byte[] recordData;
            try
            {
                recordData = EsmHelpers.GetRecordData(input, record, true);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            var subrecords = EsmRecordParser.ParseSubrecords(recordData, true);
            var edid = EsmRecordParser.FindSubrecord(subrecords, "EDID");
            if (edid == null)
            {
                continue;
            }

            var name = EsmRecordParser.GetSubrecordString(edid);
            if (!string.IsNullOrEmpty(name))
            {
                _ = map.TryAdd(name, record.FormId);
            }
        }

        return map;
    }

    /// <summary>
    ///     Scans the CONVERTED (little-endian) SCPT record data for inline-string
    ///     IsPlayerInRegion parameters and rewrites them as SCRO references.
    ///     Returns the fixed record data, or null when nothing was changed.
    ///     <para>
    ///     Index semantics (verified against PC final and the RefCount invariant across all
    ///     2,487 July scripts): the script's runtime reference array is the COMBINED SCRO+SCRV
    ///     subrecord sequence in on-disk order (SCRV entries are interleaved among SCROs and
    ///     occupy array slots), the `72` operand's u16 is a 1-based index into that combined
    ///     array, and SCHR.RefCount equals its total length. An appended SCRO therefore gets
    ///     index (SCRO count + SCRV count + 1).
    ///     </para>
    /// </summary>
    public byte[]? FixScriptRegionParams(byte[] recordData)
    {
        if (!TryParseScriptLayout(recordData, out var layout))
        {
            return null;
        }

        var sites = FindRewritableSites(recordData, layout);
        if (sites.Count == 0)
        {
            return null;
        }

        // 1-based combined-array index of the first SCRO carrying each FormID.
        var indexByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < layout.RefEntries.Count; i++)
        {
            var entry = layout.RefEntries[i];
            if (entry.IsScro)
            {
                var formId = BinaryPrimitives.ReadUInt32LittleEndian(recordData.AsSpan(entry.PayloadOffset, 4));
                _ = indexByFormId.TryAdd(formId, i + 1);
            }
        }

        var combinedCount = layout.RefEntries.Count;
        var appendedFormIds = new List<uint>();
        var buffer = (byte[])recordData.Clone();

        foreach (var site in sites)
        {
            if (!indexByFormId.TryGetValue(site.RegionFormId, out var index))
            {
                combinedCount++;
                index = combinedCount;
                indexByFormId[site.RegionFormId] = index;
                appendedFormIds.Add(site.RegionFormId);
            }

            // Rewrite the param in place: `72 <u16 1-based index>` then zero-fill the rest of the
            // old inline-string bytes. paramBytesLen / paramCount / SCDA size are NOT changed — the
            // interpreter advances by the recorded lengths, so trailing zero padding is skipped.
            var p = site.ParamOffset;
            buffer[p] = 0x72;
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(p + 1, 2), (ushort)index);
            for (var i = p + 3; i < p + 2 + site.StringLength; i++)
            {
                buffer[i] = 0;
            }
        }

        // SCHR.RefCount (u32 at payload offset 4) = final combined SCRO+SCRV count.
        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(layout.SchrPayloadOffset + 4, 4), (uint)combinedCount);

        if (appendedFormIds.Count == 0)
        {
            UpdateStats(sites.Count, 0);
            return buffer;
        }

        // Append new SCRO subrecords at the end of the record data. SCRO* is the last subrecord
        // group in SCPT records, so end-of-record placement keeps the canonical order. The caller
        // (EsmRecordWriter) recomputes the record's DataSize from the returned buffer length, and
        // GRUP sizes are back-patched from output stream positions, so growth is accounted for.
        using var stream = new MemoryStream(buffer.Length + appendedFormIds.Count * (SubrecordHeaderSize + 4));
        stream.Write(buffer);
        Span<byte> scro = stackalloc byte[SubrecordHeaderSize + 4];
        scro[0] = (byte)'S';
        scro[1] = (byte)'C';
        scro[2] = (byte)'R';
        scro[3] = (byte)'O';
        foreach (var formId in appendedFormIds)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(scro[4..6], 4);
            BinaryPrimitives.WriteUInt32LittleEndian(scro[6..10], formId);
            stream.Write(scro);
        }

        UpdateStats(sites.Count, appendedFormIds.Count);
        return stream.ToArray();
    }

    private void UpdateStats(int sitesRewritten, int scrosAppended)
    {
        _stats.ScriptRegionSitesRewritten += sitesRewritten;
        _stats.ScriptRegionScrosAppended += scrosAppended;
        _stats.ScriptRegionScriptsTouched++;
    }

    /// <summary>
    ///     Walks the little-endian subrecords of a converted SCPT record and locates SCHR,
    ///     SCDA, and the combined SCRO/SCRV reference sequence. Returns false when the record
    ///     is malformed or lacks the subrecords this pass needs.
    /// </summary>
    private static bool TryParseScriptLayout(byte[] recordData, out ScriptLayout layout)
    {
        var schrPayloadOffset = -1;
        var scdaPayloadOffset = -1;
        var scdaSize = 0;
        var refEntries = new List<RefEntry>();

        var offset = 0;
        while (offset + SubrecordHeaderSize <= recordData.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(recordData.AsSpan(offset + 4, 2));
            var payloadOffset = offset + SubrecordHeaderSize;
            if (payloadOffset + size > recordData.Length)
            {
                break;
            }

            var sig = recordData.AsSpan(offset, 4);
            if (sig.SequenceEqual("SCHR"u8) && size >= 20)
            {
                schrPayloadOffset = payloadOffset;
            }
            else if (sig.SequenceEqual("SCDA"u8))
            {
                scdaPayloadOffset = payloadOffset;
                scdaSize = size;
            }
            else if (sig.SequenceEqual("SCRO"u8) && size == 4)
            {
                refEntries.Add(new RefEntry(true, payloadOffset));
            }
            else if (sig.SequenceEqual("SCRV"u8) && size == 4)
            {
                // Local-variable references occupy slots in the same runtime reference
                // array as SCROs — they must be counted for index/RefCount arithmetic.
                refEntries.Add(new RefEntry(false, payloadOffset));
            }

            offset = payloadOffset + size;
        }

        layout = new ScriptLayout(schrPayloadOffset, scdaPayloadOffset, scdaSize, refEntries);
        return schrPayloadOffset >= 0 && scdaPayloadOffset >= 0 && scdaSize > 0 && offset == recordData.Length;
    }

    /// <summary>
    ///     Finds validated inline-string IsPlayerInRegion call sites inside the SCDA payload.
    ///     Every check must pass before a site is eligible — a raw data byte run could
    ///     coincidentally match the `58 60 12` pattern.
    /// </summary>
    private List<CallSite> FindRewritableSites(byte[] recordData, ScriptLayout layout)
    {
        var sites = new List<CallSite>();
        var scdaStart = layout.ScdaPayloadOffset;
        var scdaEnd = scdaStart + layout.ScdaSize;

        // Minimum site: 58 op op len len count count strlen strlen char
        var i = scdaStart;
        while (i + 10 <= scdaEnd)
        {
            // `58` = function-call-in-expression marker; opcode 0x1260 (IsPlayerInRegion) LE.
            if (recordData[i] != 0x58 || recordData[i + 1] != 0x60 || recordData[i + 2] != 0x12)
            {
                i++;
                continue;
            }

            var paramBytesLen = BinaryPrimitives.ReadUInt16LittleEndian(recordData.AsSpan(i + 3, 2));
            var paramsStart = i + 5;
            if (paramsStart + paramBytesLen > scdaEnd || paramBytesLen < 4)
            {
                i++;
                continue;
            }

            var paramCount = BinaryPrimitives.ReadUInt16LittleEndian(recordData.AsSpan(paramsStart, 2));
            if (paramCount != 1)
            {
                i++;
                continue;
            }

            var paramOffset = paramsStart + 2;
            if (recordData[paramOffset] == 0x72)
            {
                // Already SCRO-ref-encoded — nothing to do; skip the whole call site.
                i = paramsStart + paramBytesLen;
                continue;
            }

            var strLen = BinaryPrimitives.ReadUInt16LittleEndian(recordData.AsSpan(paramOffset, 2));
            if (strLen < 1 || strLen > MaxRegionEdidLength || strLen + 4 > paramBytesLen)
            {
                i++;
                continue;
            }

            if (!IsPrintableAscii(recordData.AsSpan(paramOffset + 2, strLen)))
            {
                i++;
                continue;
            }

            var edid = System.Text.Encoding.ASCII.GetString(recordData, paramOffset + 2, strLen);
            if (!_regionFormIdsByEdid.TryGetValue(edid, out var regionFormId))
            {
                i++;
                continue;
            }

            sites.Add(new CallSite(paramOffset, strLen, regionFormId));
            i = paramsStart + paramBytesLen;
        }

        return sites;
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b < 0x20 || b > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Subrecord layout of one converted SCPT record.</summary>
    private readonly record struct ScriptLayout(
        int SchrPayloadOffset,
        int ScdaPayloadOffset,
        int ScdaSize,
        List<RefEntry> RefEntries);

    /// <summary>One slot of the script's runtime reference array (a SCRO or SCRV subrecord).</summary>
    private readonly record struct RefEntry(bool IsScro, int PayloadOffset);

    /// <summary>One validated inline-string call site (offsets are into the record data buffer).</summary>
    private readonly record struct CallSite(int ParamOffset, int StringLength, uint RegionFormId);
}
