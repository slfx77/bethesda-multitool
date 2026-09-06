using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>One record type and the records of that type, in display order.</summary>
internal sealed record RecordTypeGroup(string RecordType, IReadOnlyList<GenericEsmRecord> Records)
{
    /// <summary>Label for the tree node — the signature plus how many it holds.</summary>
    public string DisplayName => $"{RecordType} ({Records.Count:N0})";
}

/// <summary>
///     Groups synthesized records for the Data Explorer's tree.
///     <para>
///         Classic games have no plugin file, so <c>ClassicGameAnalyzer</c> synthesizes records into
///         <see cref="RecordCollection.GenericRecords" /> — Arena's ATPL/AINF/ALOC/APRV, Daggerfall's
///         DREG/DLOC/DTXT/…, Redguard's RWLD/RMAP/ROBJ/RTXT. This turns that flat list into the
///         type-grouped shape a browser shows, and it lives in Core so the ordering can be tested;
///         the App layer is unreachable from the test project.
///     </para>
/// </summary>
internal static class RecordBrowserModel
{
    /// <summary>
    ///     Groups by record type, types sorted alphabetically and records within a type sorted by
    ///     the name a user would look for — editor id, then full name, then FormID.
    ///     <para>
    ///         Sorting by identity rather than by load order is deliberate: a synthesized FormID is
    ///         a <c>ClassicFormIdScheme</c> hash of the source's own identity, so its numeric order
    ///         carries no meaning a reader would recognise.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<RecordTypeGroup> Build(RecordCollection records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return [.. records.GenericRecords
            .GroupBy(r => r.RecordType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RecordTypeGroup(
                g.Key,
                [.. g.OrderBy(SortKey, StringComparer.OrdinalIgnoreCase)]))];
    }

    /// <summary>Label for one record in a list: its name if it has one, else its FormID.</summary>
    public static string DescribeRecord(GenericEsmRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!string.IsNullOrWhiteSpace(record.EditorId))
        {
            return record.EditorId;
        }

        return !string.IsNullOrWhiteSpace(record.FullName)
            ? record.FullName
            : $"0x{record.FormId:X8}";
    }

    private static string SortKey(GenericEsmRecord record) => DescribeRecord(record);
}
