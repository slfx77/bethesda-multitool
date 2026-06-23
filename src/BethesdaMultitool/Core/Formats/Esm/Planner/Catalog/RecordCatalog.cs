using BethesdaMultitool.Core.Formats.Esm.Parsing;
namespace BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;

/// <summary>
///     Phase A — combines master + DMP sources into a uniform <see cref="CatalogEntry" />
///     list. The disposition engine consumes the output and never reaches back to the
///     <c>ParsedMainRecord</c> / <c>RecordCollection</c> inputs directly.
/// </summary>
public static class RecordCatalog
{
    /// <summary>
    ///     Combine master + DMP records into a catalog covering only
    ///     <paramref name="enabledTypes" />. Pairs DMP records with master records sharing
    ///     the same FormID; unmatched DMP records become <see cref="SourceKind.DmpNew" />.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Build(
        MasterRecordSource master,
        DmpRecordSource dmp,
        IReadOnlySet<string> enabledTypes)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(dmp);

        if (enabledTypes.Count == 0)
        {
            return [];
        }

        var entries = new List<CatalogEntry>();

        // Track the master entry's *index* in `entries` alongside its value so duplicate
        // DMP-record FormIDs don't fall into List.IndexOf, which compares records by
        // value — once we mutate entries[idx] for the first DMP duplicate, subsequent
        // IndexOf(masterEntry) calls miss and return -1, throwing on `entries[-1] = …`.
        var masterByFormId = new Dictionary<uint, CatalogEntry>();
        var masterEntryIndexByFormId = new Dictionary<uint, int>();
        foreach (var entry in master.Enumerate(enabledTypes))
        {
            entries.Add(entry);
            if (entry.MasterFormId is { } formId)
            {
                masterByFormId[formId] = entry;
                masterEntryIndexByFormId[formId] = entries.Count - 1;
            }
        }

        var dmpOverrideIndices = new HashSet<int>();
        foreach (var (type, formId, model) in dmp.Enumerate(enabledTypes))
        {
            // Only pair with master when record TYPES also match. FormIDs are unique
            // across types in vanilla ESMs, but DMP captures occasionally surface the same
            // FormID under a different signature (runtime aliasing, parser misclassification).
            // Cross-type pairing would produce a CatalogEntry with the master's Type and
            // the DMP's wrong-typed Model, which the planner encoder dispatch rejects as
            // "Model is not of type X: actual Y". Type-mismatched DMP records fall through
            // to DmpNew so they emit through their own signature's encoder.
            // First DMP record with this FormID wins the override slot (dmpOverrideIndices.Add).
            // Later duplicates fall through to DmpNew so the downstream emission warning surfaces
            // the conflict (rather than silently dropping) and the planner doesn't crash on the
            // second IndexOf.
            if (masterByFormId.TryGetValue(formId, out var masterEntry)
                && string.Equals(masterEntry.Type, type, StringComparison.Ordinal)
                && masterEntryIndexByFormId.TryGetValue(formId, out var idx)
                && dmpOverrideIndices.Add(idx))
            {
                entries[idx] = masterEntry with
                {
                    Source = SourceKind.DmpOverride,
                    DmpFormId = formId,
                    Model = model,
                };
                continue;
            }

            entries.Add(new CatalogEntry
            {
                Type = type,
                Source = SourceKind.DmpNew,
                DmpFormId = formId,
                Model = model,
            });
        }

        return entries;
    }
}

