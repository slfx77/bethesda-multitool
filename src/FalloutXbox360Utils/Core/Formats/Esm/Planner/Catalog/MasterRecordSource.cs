using FalloutXbox360Utils.Core.Formats.Esm;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;

/// <summary>
///     ESM-side input to <see cref="RecordCatalog" />. Wraps the parsed master records and
///     yields one <see cref="CatalogEntry" /> per record whose type is in the planner's
///     enabled set. The DMP side produces overrides and matches them against this output
///     via FormID — entries here are the baseline.
/// </summary>
public sealed class MasterRecordSource
{
    private readonly IReadOnlyList<ParsedMainRecord> _records;

    public MasterRecordSource(IReadOnlyList<ParsedMainRecord> records)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
    }

    /// <summary>
    ///     Enumerate master records whose signature is in <paramref name="enabledTypes" />.
    ///     Each becomes a <see cref="CatalogEntry" /> with <see cref="SourceKind.MasterOnly" /> —
    ///     the disposition engine may later flip this to <c>Override</c> if a DMP capture matches.
    /// </summary>
    public IEnumerable<CatalogEntry> Enumerate(IReadOnlySet<string> enabledTypes)
    {
        if (enabledTypes.Count == 0)
        {
            yield break;
        }

        foreach (var record in _records)
        {
            var type = record.Header.Signature;
            if (type == "TES4")
            {
                continue;
            }

            if (!enabledTypes.Contains(type))
            {
                continue;
            }

            yield return new CatalogEntry
            {
                Type = type,
                Source = SourceKind.MasterOnly,
                MasterFormId = record.Header.FormId,
                Master = record,
            };
        }
    }
}
