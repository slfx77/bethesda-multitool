using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

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
        IReadOnlySet<string> enabledTypes,
        IReadOnlyDictionary<uint, uint>? masterFormIdAliases = null)
    {
        return Build(
            master,
            dmp,
            enabledTypes,
            masterFormIdAliases,
            out _,
            out _);
    }

    /// <summary>
    ///     Builds the catalog and returns every source-to-master alias that was proven by
    ///     an enabled, same-type DMP/master pair. The mapping is intentionally independent
    ///     of which captured model wins a shared master slot: losing aliases still identify
    ///     references that must resolve to the retained master record.
    ///     <para>
    ///         <paramref name="diagnostics" /> reports the captures the catalog discarded —
    ///         duplicate (type, FormID) DMP records and aliases shadowed by an earlier
    ///         pairing — so first-wins selection is no longer a silent drop. Winners are
    ///         unchanged; the caller routes these into <c>EmitPlan.Diagnostics</c>.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<CatalogEntry> Build(
        MasterRecordSource master,
        DmpRecordSource dmp,
        IReadOnlySet<string> enabledTypes,
        IReadOnlyDictionary<uint, uint>? masterFormIdAliases,
        out IReadOnlyDictionary<uint, uint> validatedMasterFormIdAliases,
        out IReadOnlyList<PlanDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(dmp);

        var diagnosticList = new List<PlanDiagnostic>();
        diagnostics = diagnosticList;
        if (enabledTypes.Count == 0)
        {
            validatedMasterFormIdAliases = new Dictionary<uint, uint>();
            return [];
        }

        var entries = new List<CatalogEntry>();
        var validatedAliases = new Dictionary<uint, uint>();

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

        // Runtime dumps can contain repeated snapshots of the same top-level GRUP. Match
        // the legacy emitter's per-type, per-FormID first-wins behavior before catalog
        // pairing so later copies cannot masquerade as newly-authored records.
        var keptDmpModels = new Dictionary<(string Type, uint FormId), object>();
        var pairedMasterFormIds = new HashSet<uint>();
        foreach (var (type, formId, model) in dmp.Enumerate(enabledTypes))
        {
            if (!keptDmpModels.TryAdd((type, formId), model))
            {
                var kept = keptDmpModels[(type, formId)];
                var differs = DescribeDiscardedModelDelta(kept, model);

                // Two snapshots of one record fail in different places, so first-wins by
                // enumeration order threw away recoverable content — a capture whose EditorID never
                // resolved could beat one that named the record, purely because it came first. The
                // richer capture leads, the other fills whatever it left unset, and the entry the
                // pipeline sees is the union.
                var (primary, secondary) = RecordModelUnion.Score(model) > RecordModelUnion.Score(kept)
                    ? (model, kept)
                    : (kept, model);
                var merged = RecordModelUnion.Fill(primary, secondary);
                keptDmpModels[(type, formId)] = merged;
                ReplaceKeptModel(entries, type, formId, kept, merged);

                var mergedSomething = !ReferenceEquals(merged, kept);
                diagnosticList.Add(new PlanDiagnostic
                {
                    Kind = PlanDiagnosticKind.Warning,
                    Phase = "Catalog",
                    Code = "catalog.duplicate-dmp-record",
                    RecordType = type,
                    FormId = formId,
                    Message = mergedSomething
                        ? $"Duplicate DMP capture of {type} 0x{formId:X8} merged into the richer " +
                          "capture (repeated runtime GRUP snapshot)."
                        : $"Duplicate DMP capture of {type} 0x{formId:X8} added nothing to the kept " +
                          "capture (repeated runtime GRUP snapshot).",
                    Metadata = new Dictionary<string, string?>
                    {
                        ["type"] = type,
                        ["formId"] = $"0x{formId:X8}",
                        ["differs"] = differs,
                        ["merged"] = mergedSomething ? "true" : "false"
                    }
                });
                continue;
            }

            var masterLookupFormId = formId;
            var aliasedMasterFormId = formId;
            var usesAlias = masterFormIdAliases != null
                            && masterFormIdAliases.TryGetValue(formId, out aliasedMasterFormId)
                            && aliasedMasterFormId != formId;
            if (usesAlias)
            {
                masterLookupFormId = aliasedMasterFormId;
                if (!masterByFormId.TryGetValue(masterLookupFormId, out var aliasedMaster))
                {
                    throw new InvalidOperationException(
                        $"DMP FormID alias 0x{formId:X8} targets missing master 0x{masterLookupFormId:X8}.");
                }

                if (!string.Equals(aliasedMaster.Type, type, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"DMP {type} 0x{formId:X8} aliases master {aliasedMaster.Type} " +
                        $"0x{masterLookupFormId:X8}; aliases must preserve record type.");
                }

                validatedAliases.Add(formId, masterLookupFormId);
            }

            // Only pair with master when record TYPES also match. FormIDs are unique
            // across types in vanilla ESMs, but DMP captures occasionally surface the same
            // FormID under a different signature (runtime aliasing, parser misclassification).
            // Cross-type pairing would produce a CatalogEntry with the master's Type and
            // the DMP's wrong-typed Model, which the planner encoder dispatch rejects as
            // "Model is not of type X: actual Y". Type-mismatched DMP records fall through
            // to DmpNew so they emit through their own signature's encoder.
            if (masterByFormId.TryGetValue(masterLookupFormId, out var masterEntry)
                && string.Equals(masterEntry.Type, type, StringComparison.Ordinal)
                && masterEntryIndexByFormId.TryGetValue(masterLookupFormId, out var idx))
            {
                // An exact captured FormID is stronger evidence than an EditorID-derived
                // alias. Exact captures always replace an earlier alias; a later alias can
                // never shadow an exact capture. When several prototype IDs alias the same
                // master, retain the first capture just like duplicate raw FormIDs.
                if (usesAlias && pairedMasterFormIds.Contains(masterLookupFormId))
                {
                    diagnosticList.Add(new PlanDiagnostic
                    {
                        Kind = PlanDiagnosticKind.Warning,
                        Phase = "Catalog",
                        Code = "catalog.alias-shadowed-by-exact",
                        RecordType = type,
                        FormId = formId,
                        Message = $"Aliased DMP capture {type} 0x{formId:X8} discarded: master " +
                                  $"0x{masterLookupFormId:X8} is already paired with an earlier " +
                                  "capture (exact captures and first aliases win).",
                        Metadata = new Dictionary<string, string?>
                        {
                            ["type"] = type,
                            ["formId"] = $"0x{formId:X8}",
                            ["masterFormId"] = $"0x{masterLookupFormId:X8}"
                        }
                    });
                    continue;
                }

                entries[idx] = masterEntry with
                {
                    Source = SourceKind.DmpOverride,
                    DmpFormId = formId,
                    Model = model
                };
                pairedMasterFormIds.Add(masterLookupFormId);
                continue;
            }

            entries.Add(new CatalogEntry
            {
                Type = type,
                Source = SourceKind.DmpNew,
                DmpFormId = formId,
                Model = model
            });
        }

        validatedMasterFormIdAliases = validatedAliases;
        return entries;
    }

    /// <summary>
    ///     Point the already-created catalog entry at the merged model. The first capture was
    ///     entered before the duplicate arrived, so without this the entry would keep holding the
    ///     un-merged instance and the union would be invisible downstream.
    /// </summary>
    private static void ReplaceKeptModel(
        List<CatalogEntry> entries, string type, uint formId, object kept, object merged)
    {
        if (ReferenceEquals(kept, merged))
        {
            return;
        }

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (ReferenceEquals(entry.Model, kept) &&
                string.Equals(entry.Type, type, StringComparison.Ordinal) &&
                entry.DmpFormId == formId)
            {
                entries[i] = entry with { Model = merged };
                return;
            }
        }
    }

    /// <summary>
    ///     "Does the discarded duplicate differ from the kept capture" classifier for the
    ///     <c>catalog.duplicate-dmp-record</c> diagnostic: <c>"false"</c> (provably the
    ///     same), <c>"true"</c> (provably different), or <c>"unknown"</c> (no comparison
    ///     surface for this model type). Deliberately ignores the capture <c>Offset</c> —
    ///     two snapshots of one record at different heap addresses are not a content
    ///     difference.
    ///     <para>
    ///         Model types that are flat scalar <c>record</c>s get an exact deep compare via
    ///         compiler-synthesized value equality; anything holding collections cannot (see
    ///         the <see cref="GenericEsmRecord" /> arm below).
    ///     </para>
    /// </summary>
    private static string DescribeDiscardedModelDelta(object kept, object discarded)
    {
        if (ReferenceEquals(kept, discarded))
        {
            return "false";
        }

        if (kept.GetType() != discarded.GetType())
        {
            return "true";
        }

        // Flat scalar records: every member is a scalar or string, so the synthesized record
        // equality IS the deep compare — no hand-written comparer to drift out of sync.
        //
        // IsBigEndian is deliberately NOT normalized away: two captures of one record inside a
        // single dump must agree on endianness, so a divergence is a genuine red flag, not noise.
        //
        // Float members compare through EqualityComparer<float?>.Default -> Single.Equals, which
        // treats NaN as equal to NaN (unlike operator ==). That is what we want here: a corrupt
        // capture that read NaN twice is "the same capture twice", not an endless difference.
        if (kept is GameSettingRecord keptSetting && discarded is GameSettingRecord discardedSetting)
        {
            return (keptSetting with { Offset = 0 }) == (discardedSetting with { Offset = 0 })
                ? "false"
                : "true";
        }

        if (kept is GlobalRecord keptGlobal && discarded is GlobalRecord discardedGlobal)
        {
            return (keptGlobal with { Offset = 0 }) == (discardedGlobal with { Offset = 0 })
                ? "false"
                : "true";
        }

        // GenericEsmRecord cannot use record equality: Fields is an IReadOnlyDictionary and
        // DecodedTree an IReadOnlyList, neither of which overrides Equals, so the synthesized
        // comparison degrades to reference equality and would report "true" for every duplicate.
        // Hence the cheap surface probe, which can only ever prove difference, never sameness.
        if (kept is GenericEsmRecord keptGeneric && discarded is GenericEsmRecord discardedGeneric)
        {
            var cheaplyDiffers =
                !string.Equals(keptGeneric.EditorId, discardedGeneric.EditorId, StringComparison.Ordinal)
                || !string.Equals(keptGeneric.FullName, discardedGeneric.FullName, StringComparison.Ordinal)
                || !string.Equals(keptGeneric.ModelPath, discardedGeneric.ModelPath, StringComparison.Ordinal)
                || keptGeneric.Fields.Count != discardedGeneric.Fields.Count
                || (keptGeneric.DecodedTree?.Count ?? -1) != (discardedGeneric.DecodedTree?.Count ?? -1);
            return cheaplyDiffers ? "true" : "unknown";
        }

        return "unknown";
    }
}
