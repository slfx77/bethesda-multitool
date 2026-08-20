using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;

/// <summary>
///     Prevents a partial DMP IMAD capture from receiving a plugin FormID. An allocated but
///     non-serializable IMAD would look live during SCPT reference resolution, allowing a
///     fixed-slot SCRO table to target a record the writer cannot emit.
/// </summary>
public sealed class ImageSpaceModifierDispositionPolicy : IDispositionPolicy
{
    public IReadOnlySet<string> RecordTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "IMAD"
    };

    public DispositionDecision? Decide(CatalogEntry entry)
    {
        if (entry.Source != SourceKind.DmpNew
            || entry.Model is not ImageSpaceModifierRecord modifier)
        {
            // Master IMADs stay master-pure. A complete DMP model is used only when the
            // record is genuinely new; overrides deliberately fall through to the default
            // disposition and the planned encoder emits an empty merge delta.
            return null;
        }

        if (ImageSpaceModifierCaptureValidator.IsCompleteNewCapture(modifier, out var reason))
        {
            return null;
        }

        return new DispositionDecision
        {
            Disposition = RecordDisposition.Skip,
            Provenance = new PlanProvenance
            {
                PolicyId = "ImageSpaceModifierDispositionPolicy.IncompleteCapture",
                Reason = $"New IMAD capture is incomplete: {reason}; keep it out of the emit set."
            }
        };
    }
}
