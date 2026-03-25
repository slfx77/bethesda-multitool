using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal static class DialogueTesFileScriptRecovery
{
    private const int HeaderSize = 24;
    private const int MaxRecordDataSize = 65536;

    internal static List<DialogueTesFileMappingSegment> CalibrateSegments(
        IEnumerable<DialogueRecord> dialogues,
        MinidumpInfo? minidumpInfo)
    {
        if (minidumpInfo == null)
        {
            return [];
        }

        var groups = dialogues
            .Where(dialogue => dialogue.RawRecordOffset > 0 && dialogue.TesFileOffset > 0)
            .Select(dialogue =>
            {
                var rawVa = minidumpInfo.FileOffsetToVirtualAddress(dialogue.RawRecordOffset);
                return rawVa.HasValue
                    ? new
                    {
                        Dialogue = dialogue,
                        BaseVirtualAddress = rawVa.Value - dialogue.TesFileOffset
                    }
                    : null;
            })
            .Where(item => item != null)
            .GroupBy(item => item!.BaseVirtualAddress)
            .Select(group => new DialogueTesFileMappingSegment
            {
                BaseVirtualAddress = group.Key,
                MinTesFileOffset = group.Min(item => item!.Dialogue.TesFileOffset),
                MaxTesFileOffset = group.Max(item => item!.Dialogue.TesFileOffset),
                MatchCount = group.Count(),
                ExampleFormId = group.First()!.Dialogue.FormId,
                ExampleRawRecordOffset = group.First()!.Dialogue.RawRecordOffset
            })
            .OrderByDescending(segment => segment.MatchCount)
            .ThenBy(segment => segment.MinTesFileOffset)
            .ToList();

        return groups;
    }

    internal static DialogueTesFileScriptRecoveryResult TryRecover(
        RecordParserContext context,
        IReadOnlyList<DialogueTesFileMappingSegment> segments,
        uint tesFileOffset,
        uint expectedFormId,
        string? editorId,
        bool captureRecordBytes = false)
    {
        if (tesFileOffset == 0)
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.NoTesFileOffset,
                TesFileOffset = tesFileOffset
            };
        }

        if (context.Accessor == null || context.MinidumpInfo == null)
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.UncalibratedBase,
                TesFileOffset = tesFileOffset
            };
        }

        if (segments.Count == 0)
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.UncalibratedBase,
                TesFileOffset = tesFileOffset
            };
        }

        var candidateSegments = segments
            .Where(segment => segment.Contains(tesFileOffset))
            .ToList();

        if (candidateSegments.Count == 0)
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.MappedPageMissing,
                TesFileOffset = tesFileOffset
            };
        }

        DialogueTesFileScriptRecoveryResult? firstMapped = null;
        DialogueTesFileScriptRecoveryResult? firstInfoMismatch = null;
        var sawMappedCandidate = false;

        foreach (var segment in candidateSegments)
        {
            var targetVa = segment.BaseVirtualAddress + tesFileOffset;
            var mappedDumpOffset = context.MinidumpInfo.VirtualAddressToFileOffset(targetVa);
            if (!mappedDumpOffset.HasValue)
            {
                continue;
            }

            sawMappedCandidate = true;
            var candidate = TryRecoverFromMappedLocation(
                context,
                mappedDumpOffset.Value,
                targetVa,
                segment.BaseVirtualAddress,
                tesFileOffset,
                expectedFormId,
                editorId,
                captureRecordBytes);

            if (candidate.Status is DialogueTesFileScriptRecoveryStatus.Recovered or
                DialogueTesFileScriptRecoveryStatus.NoScriptSubrecords or
                DialogueTesFileScriptRecoveryStatus.CompressedRecord)
            {
                return candidate;
            }

            firstMapped ??= candidate;

            if (candidate.Status == DialogueTesFileScriptRecoveryStatus.FormIdMismatch)
            {
                firstInfoMismatch ??= candidate;
            }
        }

        if (!sawMappedCandidate)
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.MappedPageMissing,
                TesFileOffset = tesFileOffset
            };
        }

        return firstInfoMismatch ?? firstMapped ?? new DialogueTesFileScriptRecoveryResult
        {
            Status = DialogueTesFileScriptRecoveryStatus.MappedPageMissing,
            TesFileOffset = tesFileOffset
        };
    }

    private static DialogueTesFileScriptRecoveryResult TryRecoverFromMappedLocation(
        RecordParserContext context,
        long mappedDumpOffset,
        long targetVa,
        long segmentBaseVirtualAddress,
        uint tesFileOffset,
        uint expectedFormId,
        string? editorId,
        bool captureRecordBytes)
    {
        if (mappedDumpOffset + HeaderSize > context.FileSize)
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.HeaderReadFailed,
                TesFileOffset = tesFileOffset,
                SegmentBaseVirtualAddress = segmentBaseVirtualAddress,
                TargetVirtualAddress = targetVa,
                MappedDumpOffset = mappedDumpOffset
            };
        }

        var header = new byte[HeaderSize];
        try
        {
            context.Accessor!.ReadArray(mappedDumpOffset, header, 0, HeaderSize);
        }
        catch
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.HeaderReadFailed,
                TesFileOffset = tesFileOffset,
                SegmentBaseVirtualAddress = segmentBaseVirtualAddress,
                TargetVirtualAddress = targetVa,
                MappedDumpOffset = mappedDumpOffset
            };
        }

        var signature = Encoding.ASCII.GetString(header, 0, 4);
        var dataSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        var flags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
        var recordFormId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));

        if (signature != "INFO")
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.SignatureMismatch,
                TesFileOffset = tesFileOffset,
                SegmentBaseVirtualAddress = segmentBaseVirtualAddress,
                TargetVirtualAddress = targetVa,
                MappedDumpOffset = mappedDumpOffset,
                Signature = signature,
                RecordFormId = recordFormId,
                RecordFlags = flags,
                RecordDataSize = dataSize,
                HeaderBytes = header
            };
        }

        if (recordFormId != expectedFormId)
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.FormIdMismatch,
                TesFileOffset = tesFileOffset,
                SegmentBaseVirtualAddress = segmentBaseVirtualAddress,
                TargetVirtualAddress = targetVa,
                MappedDumpOffset = mappedDumpOffset,
                Signature = signature,
                RecordFormId = recordFormId,
                RecordFlags = flags,
                RecordDataSize = dataSize,
                HeaderBytes = header
            };
        }

        if ((flags & 0x00040000) != 0)
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.CompressedRecord,
                TesFileOffset = tesFileOffset,
                SegmentBaseVirtualAddress = segmentBaseVirtualAddress,
                TargetVirtualAddress = targetVa,
                MappedDumpOffset = mappedDumpOffset,
                Signature = signature,
                RecordFormId = recordFormId,
                RecordFlags = flags,
                RecordDataSize = dataSize,
                HeaderBytes = header
            };
        }

        if (dataSize > MaxRecordDataSize || mappedDumpOffset + HeaderSize + dataSize > context.FileSize)
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.HeaderReadFailed,
                TesFileOffset = tesFileOffset,
                SegmentBaseVirtualAddress = segmentBaseVirtualAddress,
                TargetVirtualAddress = targetVa,
                MappedDumpOffset = mappedDumpOffset,
                Signature = signature,
                RecordFormId = recordFormId,
                RecordFlags = flags,
                RecordDataSize = dataSize,
                HeaderBytes = header
            };
        }

        var recordData = new byte[dataSize];
        try
        {
            context.Accessor!.ReadArray(mappedDumpOffset + HeaderSize, recordData, 0, (int)dataSize);
        }
        catch
        {
            return new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.HeaderReadFailed,
                TesFileOffset = tesFileOffset,
                SegmentBaseVirtualAddress = segmentBaseVirtualAddress,
                TargetVirtualAddress = targetVa,
                MappedDumpOffset = mappedDumpOffset,
                Signature = signature,
                RecordFormId = recordFormId,
                RecordFlags = flags,
                RecordDataSize = dataSize,
                HeaderBytes = header
            };
        }

        var scripts = DialogueConditionParser.ParseResultScriptsFromSubrecords(
            recordData,
            (int)dataSize,
            true,
            editorId,
            expectedFormId,
            context.ResolveFormName);

        return new DialogueTesFileScriptRecoveryResult
        {
            Status = scripts.Count > 0
                ? DialogueTesFileScriptRecoveryStatus.Recovered
                : DialogueTesFileScriptRecoveryStatus.NoScriptSubrecords,
            TesFileOffset = tesFileOffset,
            SegmentBaseVirtualAddress = segmentBaseVirtualAddress,
            TargetVirtualAddress = targetVa,
            MappedDumpOffset = mappedDumpOffset,
            Signature = signature,
            RecordFormId = recordFormId,
            RecordFlags = flags,
            RecordDataSize = dataSize,
            HeaderBytes = header,
            RecordDataBytes = captureRecordBytes ? recordData : null,
            Scripts = scripts
        };
    }
}
