using System.Buffers.Binary;
using EsmAnalyzer.Helpers;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Conversion;

internal sealed class EsmInfoMerger
{
    private const string ChoiceSignature = "TCLT";
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

    private static readonly HashSet<string> ScriptSignatures =
    [
        "SCHR",
        "NEXT",
        "SCTX",
        "SCDA",
        "SCRO",
        "SLSD",
        "SCVR",
        "SCRV"
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

    private readonly byte[] _input;
    private readonly EsmConversionStats _stats;
    private Dictionary<int, InfoMergeEntry>? _mergeIndex;

    public EsmInfoMerger(byte[] input, EsmConversionStats stats)
    {
        _input = input;
        _stats = stats;
    }

    /// <summary>
    ///     Reorders subrecords for a non-merged INFO record to match PC expected order.
    ///     Strips orphaned NAM3 subrecords that don't follow response data.
    /// </summary>
    /// <param name="data">Already converted (little-endian) subrecord data</param>
    public byte[]? ReorderInfoSubrecords(byte[] data)
    {
        // Parse as little-endian since data is already converted
        var subs = EsmHelpers.ParseSubrecords(data, bigEndian: false);
        if (subs.Count == 0) return null;

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
            filtered = filtered.Where(s => !ScriptSignatures.Contains(s.Signature)).ToList();
            return WriteSubrecordsToBufferLittleEndian(filtered);
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

        if (_mergeIndex == null || !_mergeIndex.TryGetValue(baseOffset, out var mergeEntry)) return false;

        if (mergeEntry.Skip)
        {
            skip = true;
            return true;
        }

        var responseHeader = EsmParser.ParseRecordHeader(_input.AsSpan(mergeEntry.ResponseOffset), true);
        var baseHeader = EsmParser.ParseRecordHeader(_input.AsSpan(baseOffset), true);

        if (responseHeader == null || baseHeader == null || responseHeader.Signature != "INFO") return false;

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

        var baseSubs = EsmHelpers.ParseSubrecords(baseData, true);
        var responseSubs = EsmHelpers.ParseSubrecords(responseData, true);

        var mergedSubrecords = BuildMergedInfoSubrecords(baseSubs, responseSubs);

        if (mergedSubrecords == null) return false;

        mergedFlags = baseFlags;
        var isCompressed = (baseFlags & 0x00040000) != 0;
        mergedData = isCompressed
            ? EsmRecordCompression.CompressConvertedRecordData(mergedSubrecords)
            : mergedSubrecords;

        return true;
    }

    private void EnsureMergeIndex()
    {
        if (_mergeIndex != null) return;

        _mergeIndex = BuildMergeIndex();
    }

    private Dictionary<int, InfoMergeEntry> BuildMergeIndex()
    {
        var index = new Dictionary<int, InfoMergeEntry>();
        var infoRecords = ScanInfoRecordsFlat();

        foreach (var group in infoRecords.GroupBy(r => r.FormId))
        {
            if (group.Count() < 2) continue;

            var classified = group
                .Select(record => new
                {
                    Record = record,
                    Role = ClassifyInfoRecord(record)
                })
                .OrderBy(entry => entry.Record.Offset)
                .ToList();

            var baseRecord = classified.FirstOrDefault(r => r.Role == InfoRecordRole.Base)?.Record;
            var responseRecord = classified.FirstOrDefault(r => r.Role == InfoRecordRole.Response)?.Record;

            if (baseRecord == null || responseRecord == null || baseRecord.Offset == responseRecord.Offset) continue;

            var baseOffset = (int)baseRecord.Offset;
            var responseOffset = (int)responseRecord.Offset;

            if (!index.ContainsKey(baseOffset))
                index[baseOffset] = new InfoMergeEntry(baseOffset, responseOffset, false);

            if (!index.ContainsKey(responseOffset))
                index[responseOffset] = new InfoMergeEntry(baseOffset, responseOffset, true);
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
        var subs = EsmHelpers.ParseSubrecords(data, true);

        var hasData = subs.Any(s => s.Signature == "DATA");
        var hasQsti = subs.Any(s => s.Signature == "QSTI");
        var hasCtda = subs.Any(s => s.Signature is "CTDA" or "CTDT");
        var hasTclt = subs.Any(s => s.Signature == "TCLT");
        var hasPnam = subs.Any(s => s.Signature == "PNAM");
        var hasTrdt = subs.Any(s => s.Signature == "TRDT");
        var hasNam1 = subs.Any(s => s.Signature == "NAM1");
        var hasNam2 = subs.Any(s => s.Signature == "NAM2");

        if (hasData || hasQsti || hasCtda || hasTclt || hasPnam) return InfoRecordRole.Base;

        if (hasTrdt || hasNam1 || hasNam2) return InfoRecordRole.Response;

        return InfoRecordRole.Unknown;
    }

    private byte[]? BuildMergedInfoSubrecords(List<AnalyzerSubrecordInfo> baseSubs,
        List<AnalyzerSubrecordInfo> responseSubs)
    {
        var baseNam3 = baseSubs.Where(s => s.Signature == Nam3Signature).ToList();
        var baseConditions = baseSubs.Where(s => ConditionSignatures.Contains(s.Signature)).ToList();
        var baseChoices = baseSubs.Where(s => ChoiceSignatures.Contains(s.Signature)).ToList();
        var baseScripts = baseSubs.Where(s => ScriptSignatures.Contains(s.Signature)).ToList();
        var baseHeader = baseSubs.Where(s => BaseHeaderSignatures.Contains(s.Signature)).ToList();
        var baseOther = baseSubs.Where(s =>
                !BaseHeaderSignatures.Contains(s.Signature) &&
                s.Signature != Nam3Signature &&
                !ConditionSignatures.Contains(s.Signature) &&
                !ChoiceSignatures.Contains(s.Signature) &&
                !ScriptSignatures.Contains(s.Signature) &&
                s.Signature != PnamSignature)
            .ToList();

        var basePreResponse = baseOther.Where(s => s.Signature == "NAME").ToList();
        var basePreScripts = baseOther.Where(s => s.Signature == "TCFU").ToList();
        var baseRnam = baseOther.Where(s => s.Signature == "RNAM").ToList();
        var baseAnam = baseOther.Where(s => s.Signature == "ANAM").ToList();
        var baseKnam = baseOther.Where(s => s.Signature == "KNAM").ToList();
        var baseDnam = baseOther.Where(s => s.Signature == "DNAM").ToList();
        var baseOtherTail = baseOther
            .Where(s => s.Signature is not "NAME" and not "TCFU" and not "RNAM" and not "ANAM" and not "KNAM" and not "DNAM")
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

            if (ScriptSignatures.Contains(sub.Signature))
            {
                responseScripts.Add(sub);
                continue;
            }

            if (sub.Signature == PnamSignature) continue;

            responseItems.Add(ResponseItem.FromSubrecord(sub));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteSubrecords(writer, baseHeader);
        WriteSubrecords(writer, basePreResponse);

        var nam3Index = 0;
        foreach (var item in responseItems)
        {
            if (item.IsGroup)
            {
                var group = responseGroups[item.GroupIndex];
                WriteSubrecords(writer, group);
                if (nam3Index < baseNam3.Count)
                {
                    WriteSubrecord(writer, baseNam3[nam3Index]);
                    nam3Index++;
                }
                continue;
            }

            WriteSubrecord(writer, item.Subrecord);
        }

        for (; nam3Index < baseNam3.Count; nam3Index++)
        {
            WriteSubrecord(writer, baseNam3[nam3Index]);
        }

        WriteSubrecords(writer, baseConditions);
        WriteSubrecords(writer, baseChoices);
        WriteSubrecords(writer, basePreScripts);

        // Merge script subrecords in correct order: SCHR, SCDA, SCTX, SCRO, SLSD, SCVR, SCRV, NEXT
        // Xbox splits: SCTX in base, SCHR+SCDA+SCRO+NEXT in response
        // PC expects: SCHR → SCDA → SCTX → SCRO → (variables) → NEXT
        WriteScriptSubrecordsInOrder(writer, responseScripts, baseScripts);

        WriteSubrecords(writer, baseOtherTail);
        WriteSubrecords(writer, baseRnam);
        WriteSubrecords(writer, baseAnam);
        WriteSubrecords(writer, baseKnam);
        WriteSubrecords(writer, baseDnam);

        return stream.ToArray();
    }

    private void WriteScriptSubrecordsInOrder(BinaryWriter writer, List<AnalyzerSubrecordInfo> responseScripts,
        List<AnalyzerSubrecordInfo> baseScripts)
    {
        // PC INFO script format - each block has its own SCDA, SCTX, SCRO:
        //   SCHR (Begin) → SCDA → SCTX → SCRO* → NEXT → SCHR (End) → SCDA → SCTX → SCRO*
        //
        // Xbox response record already groups correctly:
        //   SCHR → SCDA → SCRO* → NEXT → SCHR → SCDA → SCRO*
        //
        // Xbox base record has SCTX in order:
        //   SCTX (Begin source) → SCTX (End source)
        //
        // Strategy: Parse Xbox response into blocks, insert matching SCTX from base after each SCDA

        var baseSctx = baseScripts.Where(s => s.Signature == "SCTX").ToList();
        var baseSctxIndex = 0;

        // Parse response scripts into blocks
        // A block starts with SCHR and contains SCDA + SCRO* until NEXT or end
        var blocks = new List<ScriptBlock>();
        ScriptBlock? currentBlock = null;
        var hasNextBeforeFirstSchr = false;
        var hasAnyNext = false;
        var seenSchr = false;

        foreach (var sub in responseScripts)
        {
            switch (sub.Signature)
            {
                case "SCHR":
                    seenSchr = true;
                    currentBlock = new ScriptBlock { Header = sub };
                    blocks.Add(currentBlock);
                    break;
                case "NEXT":
                    if (!seenSchr) hasNextBeforeFirstSchr = true;
                    hasAnyNext = true;
                    // NEXT ends current block and marks separation
                    if (currentBlock != null)
                    {
                        currentBlock.HasNextAfter = true;
                        currentBlock = null;
                    }
                    else if (blocks.Count == 0)
                    {
                        // NEXT before any SCHR - we'll handle this below
                    }
                    break;
                case "SCDA":
                    currentBlock?.Bytecode.Add(sub);
                    break;
                case "SCRO":
                    currentBlock?.References.Add(sub);
                    break;
                default:
                    currentBlock?.OtherSubrecords.Add(sub);
                    break;
            }
        }

        // Handle edge case: NEXT before first SCHR means we need a synthetic Begin block
        if (hasNextBeforeFirstSchr && blocks.Count > 0)
        {
            // Insert empty Begin block before existing blocks
            var beginBlock = new ScriptBlock
            {
                Header = CreateSyntheticSchr(),
                HasNextAfter = true
            };
            blocks.Insert(0, beginBlock);
        }

        // Handle edge case: trailing NEXT without a following SCHR
        if (blocks.Count > 0 && blocks[^1].HasNextAfter)
        {
            blocks.Add(new ScriptBlock { Header = CreateSyntheticSchr() });
        }

        // Handle edge case: No blocks but we have SCTX or response has NEXT
        if (blocks.Count == 0 && (baseSctx.Count > 0 || hasAnyNext))
        {
            if (hasAnyNext)
            {
                // Response had NEXT but no SCHR: synthesize Begin/End blocks
                var beginBlock = new ScriptBlock { Header = CreateSyntheticSchr(), HasNextAfter = true };
                var endBlock = new ScriptBlock { Header = CreateSyntheticSchr() };
                blocks.Add(beginBlock);
                blocks.Add(endBlock);
            }
            else
            {
                var singleBlock = new ScriptBlock { Header = CreateSyntheticSchr() };
                blocks.Add(singleBlock);
            }
        }

        // Decide which block gets which SCTX. Prefer blocks that have SCDA.
        var sctxForBlock = new AnalyzerSubrecordInfo?[blocks.Count];
        var assignedCount = 0;
        if (baseSctx.Count >= blocks.Count)
        {
            for (var i = 0; i < blocks.Count; i++)
            {
                sctxForBlock[i] = baseSctx[baseSctxIndex++];
                assignedCount++;
            }
        }
        else
        {
            var sctxQueue = new Queue<AnalyzerSubrecordInfo>(baseSctx);

            // First, assign to blocks with bytecode
            for (var i = 0; i < blocks.Count && sctxQueue.Count > 0; i++)
            {
                if (blocks[i].Bytecode.Count > 0)
                {
                    sctxForBlock[i] = sctxQueue.Dequeue();
                    assignedCount++;
                }
            }

            // Then assign remaining to other blocks in order
            for (var i = 0; i < blocks.Count && sctxQueue.Count > 0; i++)
            {
                if (sctxForBlock[i] == null)
                {
                    sctxForBlock[i] = sctxQueue.Dequeue();
                    assignedCount++;
                }
            }
            baseSctxIndex = assignedCount;
        }

        // Write blocks in PC format: SCHR → SCDA → SCTX → (SLSD/SCVR/SCRV) → SCRO* [→ NEXT → ...]
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];

            // Write SCHR
            WriteSubrecord(writer, block.Header);

            // Write SCDA (bytecode)
            foreach (var scda in block.Bytecode)
                WriteSubrecord(writer, scda);

            // Write SCTX (source from base record)
            if (sctxForBlock[i] != null)
                WriteSubrecord(writer, sctxForBlock[i]!);

            // Write other subrecords (SLSD, SCVR, SCRV)
            foreach (var other in block.OtherSubrecords)
                WriteSubrecord(writer, other);

            // Write SCRO (references)
            foreach (var scro in block.References)
                WriteSubrecord(writer, scro);

            // Write NEXT separator (if this block has one and there's a next block)
            if (block.HasNextAfter && i < blocks.Count - 1)
                WriteSubrecord(writer, CreateNextSubrecord());
        }

        // Write any remaining SCTX that didn't get matched to a block
        while (baseSctxIndex < baseSctx.Count)
            WriteSubrecord(writer, baseSctx[baseSctxIndex++]);
    }

    private sealed class ScriptBlock
    {
        public required AnalyzerSubrecordInfo Header { get; set; }
        public List<AnalyzerSubrecordInfo> Bytecode { get; } = [];
        public List<AnalyzerSubrecordInfo> References { get; } = [];
        public List<AnalyzerSubrecordInfo> OtherSubrecords { get; } = [];
        public bool HasNextAfter { get; set; }
    }

    private void WriteSubrecords(BinaryWriter writer, List<AnalyzerSubrecordInfo> subrecords)
    {
        foreach (var sub in subrecords) WriteSubrecord(writer, sub);
    }

    private byte[] WriteSubrecordsToBuffer(List<AnalyzerSubrecordInfo> subrecords)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var sub in subrecords)
        {
            var convertedData = EsmSubrecordConverter.ConvertSubrecordData(sub.Signature, sub.Data, "INFO");
            writer.Write((byte)sub.Signature[0]);
            writer.Write((byte)sub.Signature[1]);
            writer.Write((byte)sub.Signature[2]);
            writer.Write((byte)sub.Signature[3]);
            writer.Write((ushort)convertedData.Length);
            writer.Write(convertedData);
            _stats.SubrecordsConverted++;
            _stats.IncrementSubrecordType("INFO", sub.Signature);
        }
        return stream.ToArray();
    }

    /// <summary>
    ///     Writes already-converted (little-endian) subrecords without further conversion.
    /// </summary>
    private byte[] WriteSubrecordsToBufferLittleEndian(List<AnalyzerSubrecordInfo> subrecords)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var sub in subrecords)
        {
            // Data is already in little-endian, just write as-is
            writer.Write((byte)sub.Signature[0]);
            writer.Write((byte)sub.Signature[1]);
            writer.Write((byte)sub.Signature[2]);
            writer.Write((byte)sub.Signature[3]);
            writer.Write((ushort)sub.Data.Length);
            writer.Write(sub.Data);
        }
        return stream.ToArray();
    }

    private void WriteSubrecord(BinaryWriter writer, AnalyzerSubrecordInfo subrecord)
    {
        _stats.IncrementSubrecordType("INFO", subrecord.Signature);

        var convertedData = EsmSubrecordConverter.ConvertSubrecordData(subrecord.Signature, subrecord.Data, "INFO");
        writer.Write((byte)subrecord.Signature[0]);
        writer.Write((byte)subrecord.Signature[1]);
        writer.Write((byte)subrecord.Signature[2]);
        writer.Write((byte)subrecord.Signature[3]);
        writer.Write((ushort)convertedData.Length);
        writer.Write(convertedData);
        _stats.SubrecordsConverted++;
    }

    private readonly record struct InfoMergeEntry(int BaseOffset, int ResponseOffset, bool Skip);

    private readonly record struct ResponseItem(bool IsGroup, int GroupIndex, AnalyzerSubrecordInfo Subrecord)
    {
        public static ResponseItem Group(int groupIndex) => new(true, groupIndex, default);
        public static ResponseItem FromSubrecord(AnalyzerSubrecordInfo subrecord) => new(false, -1, subrecord);
    }

    private static AnalyzerSubrecordInfo CreateSyntheticSchr()
    {
        var data = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), 1);
        return new AnalyzerSubrecordInfo
        {
            Signature = "SCHR",
            Data = data,
            Offset = 0
        };
    }

    private static AnalyzerSubrecordInfo CreateNextSubrecord()
    {
        return new AnalyzerSubrecordInfo
        {
            Signature = "NEXT",
            Data = [],
            Offset = 0
        };
    }

    private enum InfoRecordRole
    {
        Unknown,
        Base,
        Response
    }
}