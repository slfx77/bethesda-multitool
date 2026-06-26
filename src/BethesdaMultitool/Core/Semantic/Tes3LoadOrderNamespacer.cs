using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Tes3;

namespace BethesdaMultitool.Core.Semantic;

/// <summary>
///     Applies a load-order namespace to a Morrowind (TES3) source's synthetic FormIDs before it is
///     merged. TES3 plugins carry no real FormIDs, so the parser numbers records file-locally (1..N);
///     two plugins therefore reuse the same low IDs for unrelated records, and the FormID-keyed merge
///     would let one silently overwrite the other. Stamping each source's per-record IDs with its load
///     index (<see cref="Tes3FormIdScheme.Namespace" />) makes every plugin occupy a disjoint block while
///     leaving the shared exterior worldspace and land-texture palette intact so they still fold together.
/// </summary>
internal static class Tes3LoadOrderNamespacer
{
    /// <summary>
    ///     Returns <paramref name="records" /> with every per-plugin synthetic FormID stamped by
    ///     <paramref name="loadIndex" />. A no-op for non-TES3 collections and for load index 0 (whose
    ///     stamp is the identity), so the first/base source and all other games pass through unchanged.
    /// </summary>
    public static RecordCollection Namespaced(RecordCollection records, int loadIndex)
    {
        if (loadIndex <= 0 || !records.IsTes3)
        {
            return records;
        }

        return RecordCollectionFormIdRebaser.Rebase(records, formId => Tes3FormIdScheme.Namespace(formId, loadIndex));
    }
}
