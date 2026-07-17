using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References;

/// <summary>
///     Applies the fixed-slot inline-script invariant to INFO, PACK, and TERM owners after
///     allocation. Unsafe new owners are suppressed; unsafe overrides retain their master
///     record verbatim. The pass iterates because suppressing one new owner can invalidate
///     an SCRO slot in another inline script.
/// </summary>
internal static class InlineScriptReferenceSafetyPlanner
{
    private static readonly ImmutableHashSet<string> OwnerTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal, "INFO", "PACK", "TERM");

    internal sealed record Result(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> Decisions,
        ImmutableDictionary<uint, uint> SourceToEmitted,
        ImmutableHashSet<uint> EmittedFormIds,
        ImmutableDictionary<int, ImmutableArray<ResolvedRef>> References,
        ImmutableArray<PlanDiagnostic> Diagnostics);

    public static Result Apply(
        IReadOnlyList<(CatalogEntry Entry, DispositionDecision Decision)> decisions,
        ImmutableDictionary<uint, uint> sourceToEmitted,
        ImmutableHashSet<uint> emittedFormIds,
        ImmutableDictionary<int, ImmutableArray<ResolvedRef>> references,
        ReferenceResolver resolver)
    {
        var rewritten = decisions.ToArray();
        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();

        while (true)
        {
            var unsafeOwners = new List<(int Index, InlineScriptReferenceValidator.Issue Issue)>();
            for (var index = 0; index < rewritten.Length; index++)
            {
                var (entry, decision) = rewritten[index];
                if (!OwnerTypes.Contains(entry.Type)
                    || decision.Disposition is not (RecordDisposition.New or RecordDisposition.Override)
                    || entry.Model is null)
                {
                    continue;
                }

                var issue = InlineScriptReferenceValidator.FindFirstIssue(
                    entry.Model,
                    emittedFormIds,
                    sourceToEmitted);
                if (issue is not null)
                {
                    unsafeOwners.Add((index, issue));
                }
            }

            if (unsafeOwners.Count == 0)
            {
                break;
            }

            var suppressedNewFormIds = new HashSet<uint>();
            foreach (var (index, issue) in unsafeOwners)
            {
                var (entry, decision) = rewritten[index];
                var emitted = ResolveEmittedFormId(entry, sourceToEmitted);
                var keepMaster = decision.Disposition == RecordDisposition.Override
                                 && entry.MasterFormId.HasValue;
                var disposition = keepMaster
                    ? RecordDisposition.KeepMaster
                    : RecordDisposition.Skip;
                var action = keepMaster ? "Retained master" : "Suppressed new";

                rewritten[index] = (entry, decision with
                {
                    Disposition = disposition,
                    Provenance = new PlanProvenance
                    {
                        PolicyId = "InlineScriptReferenceSafetyPlanner.UnsafeBundle",
                        Reason = $"{issue.Message} Inline SCDA/SLSD/SCRO/SCRV is atomic.",
                    },
                });

                if (!keepMaster && emitted is > 0)
                {
                    suppressedNewFormIds.Add(emitted.Value);
                }

                diagnostics.Add(new PlanDiagnostic
                {
                    Kind = PlanDiagnosticKind.Warning,
                    Phase = "References",
                    Code = "inline-script.suppress-unsafe-owner",
                    RecordType = entry.Type,
                    FormId = entry.DmpFormId ?? entry.MasterFormId,
                    Message = $"{action} {entry.Type} 0x{(entry.DmpFormId ?? entry.MasterFormId ?? 0):X8}: "
                              + issue.Message,
                    Metadata = BuildMetadata(entry, emitted, disposition, issue),
                });
            }

            if (suppressedNewFormIds.Count > 0)
            {
                sourceToEmitted = sourceToEmitted
                    .Where(pair => !suppressedNewFormIds.Contains(pair.Value))
                    .ToImmutableDictionary();
                emittedFormIds = emittedFormIds.Except(suppressedNewFormIds);
            }

            references = resolver.ResolveAll(rewritten, emittedFormIds, sourceToEmitted);
        }

        return new Result(
            rewritten,
            sourceToEmitted,
            emittedFormIds,
            references,
            diagnostics.ToImmutable());
    }

    private static uint? ResolveEmittedFormId(
        CatalogEntry entry,
        IReadOnlyDictionary<uint, uint> sourceToEmitted)
    {
        if (entry.MasterFormId.HasValue)
        {
            return entry.MasterFormId;
        }

        return entry.DmpFormId is { } source
               && sourceToEmitted.TryGetValue(source, out var emitted)
            ? emitted
            : entry.DmpFormId;
    }

    private static IReadOnlyDictionary<string, string?> BuildMetadata(
        CatalogEntry entry,
        uint? emitted,
        RecordDisposition disposition,
        InlineScriptReferenceValidator.Issue issue)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["owner-source-form-id"] = FormatFormId(entry.DmpFormId),
            ["owner-emitted-form-id"] = FormatFormId(emitted),
            ["owner-disposition"] = disposition.ToString(),
            ["script-path"] = issue.ScriptPath,
            ["reference-field"] = issue.FieldPath,
            ["target-source-form-id"] = FormatFormId(issue.SourceFormId),
            ["target-emitted-form-id"] = FormatFormId(issue.ResolvedFormId),
            ["local-variable-id"] = issue.LocalVariableId?.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static string? FormatFormId(uint? formId) =>
        formId.HasValue ? $"0x{formId.Value:X8}" : null;
}
