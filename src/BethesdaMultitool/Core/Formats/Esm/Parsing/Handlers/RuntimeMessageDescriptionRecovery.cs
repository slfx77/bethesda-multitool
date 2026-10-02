using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>Reads MESG/DESC only through independently calibrated, same-capture TESFile mappings.</summary>
internal static class RuntimeMessageDescriptionRecovery
{
    internal static MessageRecord Recover(RecordParserContext context, MessageRecord message,
        IReadOnlyList<DialogueTesFileMappingSegment> segments)
    {
        if (message.RuntimeEvidence is not { } evidence) return message;
        var offset = evidence.DescriptionFileOffset;
        MessageRecord Finish(string status, IReadOnlyList<RuntimeMessageDescriptionMapping>? mappings = null,
            string? text = null) => message with
        {
            Description = !string.IsNullOrEmpty(message.Description) ? message.Description : text,
            DescriptionSource = !string.IsNullOrEmpty(message.Description) ? message.DescriptionSource
                : text != null ? MessageFieldSource.RuntimeMappedRecord : MessageFieldSource.Unavailable,
            RuntimeEvidence = evidence with { DescriptionStatus = status, DescriptionMappings = mappings ?? [] },
            RuntimeButtons = message.RuntimeButtons is { } buttons ? buttons with
            { Evidence = evidence with { DescriptionStatus = status, DescriptionMappings = mappings ?? [] } } : null
        };
        if (!offset.HasValue) return Finish("Layout unavailable");
        if (offset == 0) return Finish("No source-file offset");
        if (context.Accessor == null || context.MinidumpInfo == null || segments.Count == 0)
            return Finish("Uncalibrated mapping");
        var candidates = segments.Where(segment => segment.Contains(offset.Value)).ToList();
        if (candidates.Count == 0) return Finish("Outside calibrated segments");
        var memory = new RuntimeMemoryContext(context.Accessor, context.FileSize, context.MinidumpInfo);
        var mappings = candidates.Select(segment => Read(context, memory, message.FormId, offset.Value, segment)).ToList();
        var recovered = mappings.Where(mapping => mapping.Status == "Recovered").ToList();
        if (recovered.Count == 0) return Finish("Unavailable", mappings);
        // Multiple matched records are retained, even if their text agrees. Only disagreement blocks selection.
        if (recovered.Select(mapping => mapping.Text).Distinct(StringComparer.Ordinal).Skip(1).Any())
            return Finish("Ambiguous mappings", mappings);
        if (!string.IsNullOrEmpty(message.Description) &&
            !string.Equals(message.Description, recovered[0].Text, StringComparison.Ordinal))
            return Finish("Conflicts with parsed description", mappings);
        return Finish("Recovered", mappings, recovered[0].Text);
    }

    private static RuntimeMessageDescriptionMapping Read(RecordParserContext context, RuntimeMemoryContext memory,
        uint formId, uint offset, DialogueTesFileMappingSegment segment)
    {
        var va = segment.BaseVirtualAddress + offset;
        var dumpOffset = context.MinidumpInfo!.VirtualAddressToFileOffset(va);
        RuntimeMessageDescriptionMapping Result(string status, long? textVa = null, string? text = null) =>
            new(segment.BaseVirtualAddress, segment.MatchCount, segment.ExampleFormId, va, dumpOffset, status,
                textVa, textVa.HasValue ? context.MinidumpInfo.VirtualAddressToFileOffset(textVa.Value) : null, text);
        if (dumpOffset == null) return Result("Mapped page not captured");
        var header = memory.ReadBytesAtVa(va, 24);
        if (header == null) return Result("Incomplete mapped header");
        // Runtime source buffers use ASCII record tags, BE integers and reversed subrecord tags.
        if (!header.AsSpan(0, 4).SequenceEqual("MESG"u8)) return Result("Signature mismatch");
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)) != formId) return Result("FormID mismatch");
        if ((BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) & 0x40000) != 0)
            return Result("Compressed payload not inspected");
        var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (size > 65536) return Result("Exceeds 64 KiB inspection limit");
        var payload = memory.ReadBytesAtVa(va + 24, (int)size);
        if (payload == null) return Result("Incomplete mapped payload");
        if (EsmSubrecordUtils.FindBoundsIssue(payload, true) != null) return Result("Invalid subrecord bounds");
        var descriptions = EsmSubrecordUtils.IterateSubrecords(payload, payload.Length, true)
            .Where(subrecord => subrecord.Signature == "DESC").ToList();
        if (descriptions.Count == 0) return Result("No DESC subrecord");
        if (descriptions.Count != 1) return Result("Multiple DESC subrecords");
        var description = descriptions[0];
        var bytes = payload.AsSpan(description.DataOffset, description.DataLength);
        // The game consumes a C string. Do not silently turn a truncated allocation into recovered text.
        if (bytes.Length == 0 || bytes[^1] != 0) return Result("Unterminated DESC");
        return Result("Recovered", va + 24 + description.DataOffset, context.ReadDescription(bytes));
    }
}
