namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;

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

        var masterByFormId = new Dictionary<uint, CatalogEntry>();
        foreach (var entry in master.Enumerate(enabledTypes))
        {
            entries.Add(entry);
            if (entry.MasterFormId is { } formId)
            {
                masterByFormId[formId] = entry;
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
            if (masterByFormId.TryGetValue(formId, out var masterEntry)
                && string.Equals(masterEntry.Type, type, StringComparison.Ordinal))
            {
                var idx = entries.IndexOf(masterEntry);
                entries[idx] = masterEntry with
                {
                    Source = SourceKind.DmpOverride,
                    DmpFormId = formId,
                    Model = model,
                };
                dmpOverrideIndices.Add(idx);
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
