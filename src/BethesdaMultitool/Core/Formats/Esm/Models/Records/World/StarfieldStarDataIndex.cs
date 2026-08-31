using System.Collections.ObjectModel;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Immutable lookup snapshot for already load-order-merged STDT records. Both keys retain all
///     candidates: a scalar system ID is not globally guaranteed to be unique, and a caller that
///     accidentally supplies unmerged duplicate FormIDs must not receive a silent last-wins route.
/// </summary>
internal sealed class StarfieldStarDataIndex
{
    private StarfieldStarDataIndex(
        IReadOnlyList<StarfieldStarDataRecord> records,
        IReadOnlyDictionary<uint, IReadOnlyList<StarfieldStarDataRecord>> recordsByFormId,
        IReadOnlyDictionary<uint, IReadOnlyList<StarfieldStarDataRecord>> recordsBySystemId,
        IReadOnlyList<StarfieldStarDataRecord> recordsWithoutSystemId)
    {
        Records = records;
        RecordsByFormId = recordsByFormId;
        RecordsBySystemId = recordsBySystemId;
        RecordsWithoutSystemId = recordsWithoutSystemId;
    }

    internal IReadOnlyList<StarfieldStarDataRecord> Records { get; }

    internal IReadOnlyDictionary<uint, IReadOnlyList<StarfieldStarDataRecord>> RecordsByFormId { get; }

    /// <summary>Includes scalar key zero; it is the authored Sol system ID, not absence.</summary>
    internal IReadOnlyDictionary<uint, IReadOnlyList<StarfieldStarDataRecord>> RecordsBySystemId { get; }

    /// <summary>
    ///     Records whose typed projection is absent or whose DNAM was omitted. They remain visible
    ///     for diagnostics but cannot be reached from a PNDT scalar system ID.
    /// </summary>
    internal IReadOnlyList<StarfieldStarDataRecord> RecordsWithoutSystemId { get; }

    internal static StarfieldStarDataIndex Build(IEnumerable<StarfieldStarDataRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var snapshot = records.ToArray();
        var byFormId = new Dictionary<uint, List<StarfieldStarDataRecord>>();
        var bySystemId = new Dictionary<uint, List<StarfieldStarDataRecord>>();
        var withoutSystemId = new List<StarfieldStarDataRecord>();

        foreach (var record in snapshot)
        {
            if (record is null)
            {
                throw new ArgumentException("The STDT index cannot contain a null record.", nameof(records));
            }

            Add(byFormId, record.FormId, record);
            if (record.Routing?.SystemId is { } systemId)
            {
                Add(bySystemId, systemId, record);
            }
            else
            {
                withoutSystemId.Add(record);
            }
        }

        return new StarfieldStarDataIndex(
            Array.AsReadOnly(snapshot),
            Freeze(byFormId),
            Freeze(bySystemId),
            Array.AsReadOnly(withoutSystemId.ToArray()));
    }

    private static void Add(
        IDictionary<uint, List<StarfieldStarDataRecord>> index,
        uint key,
        StarfieldStarDataRecord record)
    {
        if (!index.TryGetValue(key, out var bucket))
        {
            bucket = [];
            index.Add(key, bucket);
        }

        bucket.Add(record);
    }

    private static IReadOnlyDictionary<uint, IReadOnlyList<StarfieldStarDataRecord>> Freeze(
        IReadOnlyDictionary<uint, List<StarfieldStarDataRecord>> source)
    {
        var snapshot = new Dictionary<uint, IReadOnlyList<StarfieldStarDataRecord>>(source.Count);
        foreach (var (key, records) in source)
        {
            snapshot.Add(key, Array.AsReadOnly(records.ToArray()));
        }

        return new ReadOnlyDictionary<uint, IReadOnlyList<StarfieldStarDataRecord>>(snapshot);
    }
}
