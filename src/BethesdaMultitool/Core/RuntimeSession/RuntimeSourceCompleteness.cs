using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.RuntimeSession;

internal static class RuntimeSourceCompleteness
{
    // Require exact physical coverage from the canonical walker; its recovery output alone
    // cannot exclude an unseen later override. No record/subrecord decoding is duplicated here.
    internal static bool CompleteFraming(byte[] bytes, PluginFormat format, IReadOnlyList<ParsedMainRecord> records,
        IReadOnlyList<GrupHeaderInfo> groups, CancellationToken token)
    {
        var header = EsmParser.ParseRecordHeader(bytes, EsmParser.IsBigEndian(bytes), format);
        if (header is null || header.Signature != "TES4") return false;
        var spans = new List<(long Start, long End, long? GroupEnd)>
            { (0, format.RecordHeaderSize + (long)header.DataSize, null) };
        foreach (var record in records)
            spans.Add((record.Offset, record.Offset + format.RecordHeaderSize + record.Header.DataSize, null));
        foreach (var group in groups)
            spans.Add((group.Offset, group.Offset + format.GroupHeaderSize, group.Offset + group.GroupSize));
        var ends = new Stack<long>();
        long offset = 0;
        foreach (var span in spans.OrderBy(span => span.Start))
        {
            token.ThrowIfCancellationRequested();
            while (ends.TryPeek(out var closed) && closed == offset) ends.Pop();
            if (span.Start != offset || span.End <= offset || span.End > bytes.LongLength ||
                ends.TryPeek(out var parentEnd) && span.End > parentEnd) return false;
            if (span.GroupEnd is { } groupEnd)
            {
                if (groupEnd < span.End || groupEnd > bytes.LongLength ||
                    ends.TryPeek(out parentEnd) && groupEnd > parentEnd) return false;
                ends.Push(groupEnd);
            }
            offset = span.End;
        }
        return offset == bytes.LongLength && ends.All(end => end == offset);
    }

    internal static bool CompletePayload(byte[] bytes, ParsedMainRecord record, int headerSize, bool bigEndian)
    {
        var start = record.Offset + headerSize;
        if (start < 0 || start > bytes.LongLength || record.Header.DataSize > bytes.LongLength - start) return false;
        var payload = bytes.AsSpan((int)start, (int)record.Header.DataSize);
        var decoded = record.Header.IsCompressed ? EsmParser.DecompressRecordData(payload, bigEndian) : payload.ToArray();
        if (decoded is null) return false;
        var diagnostics = new List<SubrecordReadDiagnostic>();
        var checkedRows = EsmParser.ParseSubrecords(decoded, bigEndian, diagnostics.Add);
        return diagnostics.Count == 0 && checkedRows.Count == record.Subrecords.Count &&
            checkedRows.Zip(record.Subrecords).All(pair => pair.First.Signature == pair.Second.Signature &&
                pair.First.Data.SequenceEqual(pair.Second.Data));
    }
}
