using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Merge;

/// <summary>
///     Merges DMP-encoded subrecords into a parsed ESM record's subrecord stream.
///
///     Algorithm:
///       1. Walk ESM subrecords in their original order.
///       2. For each, if an unconsumed DMP subrecord with the same signature exists AND the
///          policy doesn't force the ESM version, emit DMP bytes; otherwise emit ESM bytes.
///       3. Append any DMP-only subrecords at the end in encoder-defined canonical order.
///
///     Repeated signatures in the ESM (e.g., multiple OBND in some types) are matched
///     positionally — the Nth occurrence in ESM consumes the Nth DMP candidate of that signature.
/// </summary>
public static class RecordMergeEngine
{
    /// <summary>
    ///     Merge a DMP-encoded record onto an ESM source record. Returns the concatenated
    ///     subrecord stream (header subrecords only — no record header).
    /// </summary>
    public static MergeResult Merge(
        ParsedMainRecord esmRecord,
        EncodedRecord dmpEncoded,
        SubrecordMergePolicy policy)
    {
        var dmpSignaturesUsed = new List<string>();
        var esmSignaturesRetained = new List<string>();
        var dmpSignaturesAppended = new List<string>();
        var warnings = new List<string>(dmpEncoded.Warnings);

        // Track which DMP subrecord indices have been consumed.
        var consumed = new bool[dmpEncoded.Subrecords.Count];

        // Pre-build a per-signature queue of DMP indices for fast lookup during pass 1.
        var dmpIndexBySignature = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var i = 0; i < dmpEncoded.Subrecords.Count; i++)
        {
            var sig = dmpEncoded.Subrecords[i].Signature;
            if (!dmpIndexBySignature.TryGetValue(sig, out var queue))
            {
                queue = new Queue<int>();
                dmpIndexBySignature[sig] = queue;
            }

            queue.Enqueue(i);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Pass 1: emit ESM subrecords in order, overlaying DMP bytes when policy allows.
        foreach (var esmSub in esmRecord.Subrecords)
        {
            var sig = esmSub.Signature;
            var retainEsm = policy.RetainFromEsm.Contains(sig);

            if (!retainEsm
                && dmpIndexBySignature.TryGetValue(sig, out var queue)
                && queue.TryDequeue(out var dmpIndex))
            {
                consumed[dmpIndex] = true;
                var dmpBytes = dmpEncoded.Subrecords[dmpIndex].Bytes;
                SubrecordEncoder.WriteSubrecord(writer, sig, dmpBytes);
                dmpSignaturesUsed.Add(sig);
            }
            else
            {
                SubrecordEncoder.WriteSubrecord(writer, sig, esmSub.Data);
                esmSignaturesRetained.Add(sig);
            }
        }

        // Pass 2: any DMP-only subrecords (not consumed in pass 1) are appended in canonical order.
        for (var i = 0; i < dmpEncoded.Subrecords.Count; i++)
        {
            if (consumed[i])
            {
                continue;
            }

            var sub = dmpEncoded.Subrecords[i];
            SubrecordEncoder.WriteSubrecord(writer, sub.Signature, sub.Bytes);
            dmpSignaturesAppended.Add(sub.Signature);
        }

        return new MergeResult
        {
            SubrecordBytes = stream.ToArray(),
            DmpSignaturesUsed = dmpSignaturesUsed,
            EsmSignaturesRetained = esmSignaturesRetained,
            DmpSignaturesAppended = dmpSignaturesAppended,
            Warnings = warnings
        };
    }
}
