namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

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

        // Check if this record has response data (TRDT)
        var hasTrdt = subs.Any(s => s.Signature == "TRDT");
        var hasSchr = subs.Any(s => s.Signature == "SCHR");
        var hasScda = subs.Any(s => s.Signature == "SCDA");

        var filtered = subs;

        // If no response data, strip NAM3 subrecords (they're orphaned)
        if (!hasTrdt)
        {
            filtered = filtered.Where(s => s.Signature != "NAM3").ToList();
        }

        if (!hasSchr && !hasScda)
        {
            filtered = filtered.Where(s => !InfoSubrecordWriter.ScriptSignatures.Contains(s.Signature)).ToList();
            return InfoSubrecordWriter.WriteSubrecordsToBufferLittleEndian(filtered);
        }

        // Has response data - keep subrecords as-is (they should already be in correct order)
        return null;
    }

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

        var hasData = subs.Any(s => s.Signature == "DATA");
        var hasQsti = subs.Any(s => s.Signature == "QSTI");
        var hasCtda = subs.Any(s => s.Signature is "CTDA" or "CTDT");
        var hasTclt = subs.Any(s => s.Signature == "TCLT");
        var hasPnam = subs.Any(s => s.Signature == "PNAM");
        var hasTrdt = subs.Any(s => s.Signature == "TRDT");
        var hasNam1 = subs.Any(s => s.Signature == "NAM1");
        var hasNam2 = subs.Any(s => s.Signature == "NAM2");

        if (hasData || hasQsti || hasCtda || hasTclt || hasPnam)
        {
            return InfoRecordRole.Base;
        }

        return hasTrdt || hasNam1 || hasNam2
            ? InfoRecordRole.Response
            : InfoRecordRole.Unknown;
    }

    private byte[]? BuildMergedInfoSubrecords(List<AnalyzerSubrecordInfo> baseSubs,
        List<AnalyzerSubrecordInfo> responseSubs)
    {
        var baseNam3 = baseSubs.Where(s => s.Signature == Nam3Signature).ToList();
        var baseConditions = baseSubs.Where(s => ConditionSignatures.Contains(s.Signature)).ToList();
        var baseChoices = baseSubs.Where(s => ChoiceSignatures.Contains(s.Signature)).ToList();
        var baseScripts = baseSubs.Where(s => InfoSubrecordWriter.ScriptSignatures.Contains(s.Signature)).ToList();
        var baseHeader = baseSubs.Where(s => BaseHeaderSignatures.Contains(s.Signature)).ToList();
        var baseOther = baseSubs.Where(s =>
                !BaseHeaderSignatures.Contains(s.Signature) &&
                s.Signature != Nam3Signature &&
                !ConditionSignatures.Contains(s.Signature) &&
                !ChoiceSignatures.Contains(s.Signature) &&
                !InfoSubrecordWriter.ScriptSignatures.Contains(s.Signature) &&
                s.Signature != PnamSignature)
            .ToList();

        var basePreResponse = baseOther.Where(s => s.Signature == "NAME").ToList();
        var basePreScripts = baseOther.Where(s => s.Signature == "TCFU").ToList();
        var baseRnam = baseOther.Where(s => s.Signature == "RNAM").ToList();
        var baseAnam = baseOther.Where(s => s.Signature == "ANAM").ToList();
        var baseKnam = baseOther.Where(s => s.Signature == "KNAM").ToList();
        var baseDnam = baseOther.Where(s => s.Signature == "DNAM").ToList();
        var baseOtherTail = baseOther
            .Where(s => s.Signature is not "NAME" and not "TCFU" and not "RNAM" and not "ANAM" and not "KNAM"
                and not "DNAM")
            .ToList();

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

    private enum InfoRecordRole
    {
        Unknown,
        Base,
        Response
    }
}
