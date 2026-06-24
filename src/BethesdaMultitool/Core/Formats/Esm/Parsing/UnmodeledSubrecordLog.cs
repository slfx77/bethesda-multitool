using System.Collections.Concurrent;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Diagnostic collector for subrecords that a typed record handler iterated over but did not model
///     (i.e. fell through its <c>switch</c> to the <c>default:</c> case). Historically those subrecords
///     were dropped silently with no signal; this log makes them visible so the completeness tooling and
///     tests can assert that the typed record model never silently discards data.
///     <para>
///         Opt-in via <see cref="Enabled" /> so normal parsing pays no cost. Records are keyed by
///         (record type, subrecord signature, data length) with an occurrence count, mirroring
///         <c>SubrecordSchemaRegistry</c>'s fallback log.
///     </para>
/// </summary>
public static class UnmodeledSubrecordLog
{
    private static readonly ConcurrentDictionary<(string RecordType, string Signature, int DataLength), int> Entries =
        new();

    /// <summary>When false (the default) <see cref="Note" /> is a no-op, so parsing has zero overhead.</summary>
    public static bool Enabled { get; set; }

    /// <summary>True when at least one unmodeled subrecord has been recorded since the last clear.</summary>
    public static bool HasEntries => !Entries.IsEmpty;

    /// <summary>Records one occurrence of an unmodeled subrecord shape. No-op unless <see cref="Enabled" />.</summary>
    public static void Note(string recordType, string signature, int dataLength)
    {
        if (!Enabled)
        {
            return;
        }

        Entries.AddOrUpdate((recordType, signature, dataLength), 1, (_, count) => count + 1);
    }

    /// <summary>Snapshot of the distinct unmodeled subrecord shapes recorded so far, with counts.</summary>
    public static IReadOnlyList<(string RecordType, string Signature, int DataLength, int Count)> Snapshot()
    {
        return Entries
            .Select(kv => (kv.Key.RecordType, kv.Key.Signature, kv.Key.DataLength, kv.Value))
            .OrderByDescending(e => e.Value)
            .ThenBy(e => e.RecordType, StringComparer.Ordinal)
            .ThenBy(e => e.Signature, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Clears all recorded entries (call before a measured parse run).</summary>
    public static void Clear()
    {
        Entries.Clear();
    }
}
