using BethesdaMultitool.Core.Formats.Esm.Conversion.Indexing;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;

internal sealed class EsmInfoMerger(byte[] input, EsmConversionStats stats)
{
    private const string Nam3Signature = "NAM3";
    private const string PnamSignature = "PNAM";

    private static readonly HashSet<string> ResponseGroupSignatures =
    [
        "TRDT",
        "NAM1",
        "NAM2",
        "NAM3"
    ];

    private static readonly HashSet<string> ChoiceSignatures =
    [
        "TCLT",
        "TCLF"
    ];

    private static readonly HashSet<string> BaseHeaderSignatures =
    [
        "DATA",
        "QSTI"
    ];

    private static readonly HashSet<string> ConditionSignatures =
    [
        "CTDA",
        "CTDT"
    ];

    private readonly byte[] _input = input;
    private readonly InfoSubrecordWriter _subrecordWriter = new(stats);
    private Dictionary<int, InfoMergeEntry>? _mergeIndex;
    private IReadOnlyDictionary<uint, int>? _toftInfoOffsetsByFormId;

    /// <summary>
    ///     Supplies the TOFT INFO index (FormID to file offset) used to locate the split INFO
    ///     fragments that get merged; invalidates the cached merge index.
    /// </summary>
    public void SetToftInfoIndex(IReadOnlyDictionary<uint, int> toftInfoOffsetsByFormId)
    {
        _toftInfoOffsetsByFormId = toftInfoOffsetsByFormId;
        _mergeIndex = null;
    }

    /// <summary>
    ///     Reorders subrecords for a non-merged INFO record to match PC expected order.
    ///     Strips orphaned NAM3 subrecords that don't follow response data.
    /// </summary>
    /// <param name="data">Already converted (little-endian) subrecord data</param>
    public static byte[]? ReorderInfoSubrecords(byte[] data)
    {
        // Parse as little-endian since data is already converted
        var subs = EsmRecordParser.ParseSubrecords(data, false);
        if (subs.Count == 0)
        {
            return null;
        }

        // Single pass to detect the response/script markers (replaces three .Any() scans).
        var hasTrdt = false;
        var hasSchr = false;
        var hasScda = false;
        foreach (var sub in subs)
        {
            switch (sub.Signature)
            {
                case "TRDT": hasTrdt = true; break;
                case "SCHR": hasSchr = true; break;
                case "SCDA": hasScda = true; break;
            }
        }

        // Has script data - keep subrecords as-is (they should already be in correct order).
        if (hasSchr || hasScda)
        {
            return null;
        }

        // No script data - rewrite, stripping orphaned NAM3 (when no TRDT) and any script subrecords.
        // Single pass replaces the two chained .Where().ToList() filters.
        var filtered = new List<AnalyzerSubrecordInfo>(subs.Count);
        foreach (var sub in subs)
        {
            if (!hasTrdt && sub.Signature == "NAM3")
            {
                continue;
            }

            if (InfoSubrecordWriter.ScriptSignatures.Contains(sub.Signature))
            {
                continue;
            }

            filtered.Add(sub);
        }

        return InfoSubrecordWriter.WriteSubrecordsToBufferLittleEndian(filtered);
    }

    /// <summary>
    ///     Tries to merge an Xbox 360 split INFO record at the given base offset into a single
    ///     PC INFO record; sets <paramref name="skip" /> when the record is a fragment to drop.
    /// </summary>
    public bool TryMergeInfoRecord(int baseOffset, uint baseFlags, out byte[]? mergedData, out uint mergedFlags,
        out bool skip)
    {
        mergedData = null;
        mergedFlags = baseFlags;
        skip = false;

        EnsureMergeIndex();

        if (_mergeIndex == null || !_mergeIndex.TryGetValue(baseOffset, out var mergeEntry))
        {
            return false;
        }

        if (mergeEntry.Skip)
        {
            skip = true;
            return true;
        }

        var responseHeader = EsmParser.ParseRecordHeader(_input.AsSpan(mergeEntry.ResponseOffset), true);
        var baseHeader = EsmParser.ParseRecordHeader(_input.AsSpan(baseOffset), true);

        if (responseHeader == null || baseHeader == null || responseHeader.Signature != "INFO")
        {
            return false;
        }

        var baseInfo = new AnalyzerRecordInfo
        {
            Signature = baseHeader.Signature,
            FormId = baseHeader.FormId,
            Flags = baseHeader.Flags,
            DataSize = baseHeader.DataSize,
            Offset = (uint)baseOffset,
            TotalSize = EsmParser.MainRecordHeaderSize + baseHeader.DataSize
        };

        var responseInfo = new AnalyzerRecordInfo
        {
            Signature = responseHeader.Signature,
            FormId = responseHeader.FormId,
            Flags = responseHeader.Flags,
            DataSize = responseHeader.DataSize,
            Offset = (uint)mergeEntry.ResponseOffset,
            TotalSize = EsmParser.MainRecordHeaderSize + responseHeader.DataSize
        };

        var baseData = EsmHelpers.GetRecordData(_input, baseInfo, true);
        var responseData = EsmHelpers.GetRecordData(_input, responseInfo, true);

        var baseSubs = EsmRecordParser.ParseSubrecords(baseData, true);
        var responseSubs = EsmRecordParser.ParseSubrecords(responseData, true);

        var mergedSubrecords = BuildMergedInfoSubrecords(baseSubs, responseSubs);

        if (mergedSubrecords == null)
        {
            return false;
        }

        mergedFlags = baseFlags;
        var isCompressed = (baseFlags & 0x00040000) != 0;
        mergedData = isCompressed
            ? EsmRecordCompression.CompressConvertedRecordData(mergedSubrecords)
            : mergedSubrecords;

        return true;
    }

    private void EnsureMergeIndex()
    {
        if (_mergeIndex != null)
        {
            return;
        }

        _mergeIndex = BuildMergeIndex();
    }

    private Dictionary<int, InfoMergeEntry> BuildMergeIndex()
    {
        var index = new Dictionary<int, InfoMergeEntry>();
        var infoRecords = ScanInfoRecordsFlat();

        // Group by FormID. A FormID may have:
        // 1. Two or more primary records (old split-record logic)
        // 2. One primary record + one TOFT record with response data (streaming cache)
        foreach (var group in infoRecords.GroupBy(r => r.FormId))
        {
            // Check if there's a TOFT record for this FormID
            int? toftOffset = null;
            if (_toftInfoOffsetsByFormId != null &&
                _toftInfoOffsetsByFormId.TryGetValue(group.Key, out var offset))
            {
                toftOffset = offset;
            }

            // Skip if only one record AND no TOFT record
            if (group.Count() < 2 && toftOffset == null)
            {
                continue;
            }

            var classified = group
                .Select(record => new
                {
                    Record = record,
                    Role = ClassifyInfoRecord(record)
                })
                .OrderBy(entry => entry.Record.Offset)
                .ToList();

            // Find the base record (primary INFO with conditions/scripts but no response text)
            var baseRecord = classified
                .Where(r => r.Role == InfoRecordRole.Base)
                .Where(r => toftOffset == null || r.Record.Offset != toftOffset)
                .Select(r => (AnalyzerRecordInfo?)r.Record)
                .FirstOrDefault();

            // Find the response record - prefer TOFT record if available
            int? responseOffset = null;
            if (toftOffset != null)
            {
                // Use TOFT record for response data
                responseOffset = toftOffset;
            }
            else
            {
                // Fall back to finding a response record in primary area
                var responseRecord = classified
                    .Where(r => r.Role == InfoRecordRole.Response)
                    .Select(r => (AnalyzerRecordInfo?)r.Record)
                    .FirstOrDefault();
                if (responseRecord != null)
                {
                    responseOffset = (int)responseRecord.Offset;
                }
            }

            if (baseRecord == null || responseOffset == null || baseRecord.Offset == responseOffset)
            {
                continue;
            }

            var baseOff = (int)baseRecord.Offset;
            var respOff = responseOffset.Value;

            if (!index.ContainsKey(baseOff))
            {
                index[baseOff] = new InfoMergeEntry(baseOff, respOff, false);
            }

            if (!index.ContainsKey(respOff))
            {
                index[respOff] = new InfoMergeEntry(baseOff, respOff, true);
            }
        }

        return index;
    }

    private List<AnalyzerRecordInfo> ScanInfoRecordsFlat()
    {
        var records = new List<AnalyzerRecordInfo>();
        var header = EsmParser.ParseFileHeader(_input);
        if (header == null)
        {
            return records;
        }

        var bigEndian = header.IsBigEndian;
        var tes4Header = EsmParser.ParseRecordHeader(_input.AsSpan(), bigEndian);
        if (tes4Header == null)
        {
            return records;
        }

        var offset = EsmParser.MainRecordHeaderSize + (int)tes4Header.DataSize;
        var iterations = 0;
        const int maxIterations = 2_000_000;

        while (offset + EsmParser.MainRecordHeaderSize <= _input.Length && iterations++ < maxIterations)
        {
            var recHeader = EsmParser.ParseRecordHeader(_input.AsSpan(offset), bigEndian);
            if (recHeader == null)
            {
                break;
            }

            if (recHeader.Signature == "GRUP")
            {
                offset += EsmParser.MainRecordHeaderSize;
                continue;
            }

            var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
            if (recordEnd <= offset || recordEnd > _input.Length)
            {
                break;
            }

            if (recHeader.Signature == "INFO")
            {
                records.Add(new AnalyzerRecordInfo
                {
                    Signature = recHeader.Signature,
                    FormId = recHeader.FormId,
                    Flags = recHeader.Flags,
                    DataSize = recHeader.DataSize,
                    Offset = (uint)offset,
                    TotalSize = (uint)(recordEnd - offset)
                });
            }

            offset = recordEnd;
        }

        return records;
    }

    private InfoRecordRole ClassifyInfoRecord(AnalyzerRecordInfo record)
    {
        var data = EsmHelpers.GetRecordData(_input, record, true);
        var subs = EsmRecordParser.ParseSubrecords(data, true);
        return ClassifyBySubrecords(subs);
    }

    /// <summary>
    ///     Classifies an INFO record as Base/Response/Unknown from its subrecord signatures.
    ///     Single pass over the subrecord list (replaces eight independent <c>.Any()</c> scans);
    ///     a base marker dominates and short-circuits the scan.
    /// </summary>
    internal static InfoRecordRole ClassifyBySubrecords(IReadOnlyList<AnalyzerSubrecordInfo> subs)
    {
        var hasResponseMarker = false; // TRDT | NAM1 | NAM2

        foreach (var sub in subs)
        {
            switch (sub.Signature)
            {
                case "DATA":
                case "QSTI":
                case "CTDA":
                case "CTDT":
                case "TCLT":
                case "PNAM":
                    return InfoRecordRole.Base;
                case "TRDT":
                case "NAM1":
                case "NAM2":
                    hasResponseMarker = true;
                    break;
            }
        }

        return hasResponseMarker ? InfoRecordRole.Response : InfoRecordRole.Unknown;
    }

    /// <summary>
    ///     Buckets a base INFO record's subrecords into the category lists the merge writer consumes,
    ///     in a single pass. Replaces the previous ~13 separate <c>Where().ToList()</c> scans of the
    ///     same list. PNAM is dropped (it was excluded from the original "baseOther" set). All category
    ///     sets are disjoint, so the if/else-if order is equivalent to the original independent filters.
    /// </summary>
    internal static BaseSubrecordBuckets BucketBaseSubrecords(List<AnalyzerSubrecordInfo> baseSubs)
    {
        var buckets = BaseSubrecordBuckets.Create();
        foreach (var sub in baseSubs)
        {
            var sig = sub.Signature;
            if (sig == Nam3Signature)
            {
                buckets.Nam3.Add(sub);
            }
            else if (ConditionSignatures.Contains(sig))
            {
                buckets.Conditions.Add(sub);
            }
            else if (ChoiceSignatures.Contains(sig))
            {
                buckets.Choices.Add(sub);
            }
            else if (InfoSubrecordWriter.ScriptSignatures.Contains(sig))
            {
                buckets.Scripts.Add(sub);
            }
            else if (BaseHeaderSignatures.Contains(sig))
            {
                buckets.Header.Add(sub);
            }
            else if (sig != PnamSignature)
            {
                switch (sig)
                {
                    case "NAME": buckets.PreResponse.Add(sub); break;
                    case "TCFU": buckets.PreScripts.Add(sub); break;
                    case "RNAM": buckets.Rnam.Add(sub); break;
                    case "ANAM": buckets.Anam.Add(sub); break;
                    case "KNAM": buckets.Knam.Add(sub); break;
                    case "DNAM": buckets.Dnam.Add(sub); break;
                    default: buckets.OtherTail.Add(sub); break;
                }
            }
        }

        return buckets;
    }

    internal byte[]? BuildMergedInfoSubrecords(List<AnalyzerSubrecordInfo> baseSubs,
        List<AnalyzerSubrecordInfo> responseSubs)
    {
        var buckets = BucketBaseSubrecords(baseSubs);
        var baseNam3 = buckets.Nam3;
        var baseConditions = buckets.Conditions;
        var baseChoices = buckets.Choices;
        var baseScripts = buckets.Scripts;
        var baseHeader = buckets.Header;
        var basePreResponse = buckets.PreResponse;
        var basePreScripts = buckets.PreScripts;
        var baseRnam = buckets.Rnam;
        var baseAnam = buckets.Anam;
        var baseKnam = buckets.Knam;
        var baseDnam = buckets.Dnam;
        var baseOtherTail = buckets.OtherTail;

        var responseGroups = new List<List<AnalyzerSubrecordInfo>>();
        var responseScripts = new List<AnalyzerSubrecordInfo>();
        var responseItems = new List<ResponseItem>();
        List<AnalyzerSubrecordInfo>? currentGroup = null;

        foreach (var sub in responseSubs)
        {
            if (sub.Signature == "TRDT")
            {
                currentGroup = [];
                responseGroups.Add(currentGroup);
                currentGroup.Add(sub);
                responseItems.Add(ResponseItem.Group(responseGroups.Count - 1));
                continue;
            }

            if (currentGroup != null && ResponseGroupSignatures.Contains(sub.Signature))
            {
                currentGroup.Add(sub);
                continue;
            }

            if (InfoSubrecordWriter.ScriptSignatures.Contains(sub.Signature))
            {
                responseScripts.Add(sub);
                continue;
            }

            if (sub.Signature == PnamSignature)
            {
                continue;
            }

            responseItems.Add(ResponseItem.FromSubrecord(sub));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        _subrecordWriter.WriteSubrecords(writer, baseHeader);
        _subrecordWriter.WriteSubrecords(writer, basePreResponse);

        var nam3Index = 0;
        foreach (var item in responseItems)
        {
            if (item.IsGroup)
            {
                var group = responseGroups[item.GroupIndex];
                _subrecordWriter.WriteSubrecords(writer, group);
                if (nam3Index < baseNam3.Count)
                {
                    _subrecordWriter.WriteSubrecord(writer, baseNam3[nam3Index]);
                    nam3Index++;
                }

                continue;
            }

            _subrecordWriter.WriteSubrecord(writer, item.Subrecord!);
        }

        for (; nam3Index < baseNam3.Count; nam3Index++)
        {
            _subrecordWriter.WriteSubrecord(writer, baseNam3[nam3Index]);
        }

        _subrecordWriter.WriteSubrecords(writer, baseConditions);
        _subrecordWriter.WriteSubrecords(writer, baseChoices);
        _subrecordWriter.WriteSubrecords(writer, basePreScripts);

        // Merge script subrecords in correct order: SCHR, SCDA, SCTX, SCRO, SLSD, SCVR, SCRV, NEXT
        // Xbox splits: SCTX in base, SCHR+SCDA+SCRO+NEXT in response
        // PC expects: SCHR -> SCDA -> SCTX -> SCRO -> (variables) -> NEXT
        _subrecordWriter.WriteScriptSubrecordsInOrder(writer, responseScripts, baseScripts);

        _subrecordWriter.WriteSubrecords(writer, baseOtherTail);
        _subrecordWriter.WriteSubrecords(writer, baseRnam);
        _subrecordWriter.WriteSubrecords(writer, baseAnam);
        _subrecordWriter.WriteSubrecords(writer, baseKnam);
        _subrecordWriter.WriteSubrecords(writer, baseDnam);

        return stream.ToArray();
    }

    /// <summary>Category lists produced by <see cref="BucketBaseSubrecords" />, in writer-consumption order.</summary>
    internal readonly record struct BaseSubrecordBuckets(
        List<AnalyzerSubrecordInfo> Nam3,
        List<AnalyzerSubrecordInfo> Conditions,
        List<AnalyzerSubrecordInfo> Choices,
        List<AnalyzerSubrecordInfo> Scripts,
        List<AnalyzerSubrecordInfo> Header,
        List<AnalyzerSubrecordInfo> PreResponse,
        List<AnalyzerSubrecordInfo> PreScripts,
        List<AnalyzerSubrecordInfo> Rnam,
        List<AnalyzerSubrecordInfo> Anam,
        List<AnalyzerSubrecordInfo> Knam,
        List<AnalyzerSubrecordInfo> Dnam,
        List<AnalyzerSubrecordInfo> OtherTail)
    {
        public static BaseSubrecordBuckets Create()
        {
            return new BaseSubrecordBuckets([], [], [], [], [], [], [], [], [], [], [], []);
        }
    }

    private readonly record struct InfoMergeEntry(int BaseOffset, int ResponseOffset, bool Skip);

    private readonly record struct ResponseItem(bool IsGroup, int GroupIndex, AnalyzerSubrecordInfo? Subrecord)
    {
        public static ResponseItem Group(int groupIndex)
        {
            return new ResponseItem(true, groupIndex, null);
        }

        public static ResponseItem FromSubrecord(AnalyzerSubrecordInfo subrecord)
        {
            return new ResponseItem(false, -1, subrecord);
        }
    }

    internal enum InfoRecordRole
    {
        Unknown,
        Base,
        Response
    }
}
