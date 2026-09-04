namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     What the dump's runtime objects are, and how much of the still-unowned string population
///     they could account for.
///     <para>
///         This is a measurement, not a claim: nothing here names an owner. It exists to answer, in
///         numbers rather than argument, whether extending ownership resolution to arbitrary C++
///         classes is worth building. The reference point is that containment over known TESForms
///         reached 1.2% of the target population.
///     </para>
/// </summary>
public sealed class RuntimeObjectCensus
{
    // --- RTTI index health --------------------------------------------------------------------

    public int TypeDescriptorCount { get; init; }

    /// <summary>Type descriptors no object locator referenced; a large share means lost coverage.</summary>
    public int TypeDescriptorsWithNoColCount { get; init; }

    public int VtableCount { get; init; }

    public int SecondaryVtableCount { get; init; }

    public int ClassCount { get; init; }

    public long ModuleBytesCaptured { get; init; }

    public long ModuleBytesDeclared { get; init; }

    public uint MinVtableVa { get; init; }

    public uint MaxVtableVa { get; init; }

    // --- Object inventory ---------------------------------------------------------------------

    /// <summary>Vtable words found, before collapsing to distinct object bases.</summary>
    public int RawVtableWordHits { get; init; }

    public int ObjectCount { get; init; }

    /// <summary>Objects whose class has a PDB-declared size, so their extent is exact.</summary>
    public int ObjectsWithDeclaredSize { get; init; }

    public int AmbiguousBaseCount { get; init; }

    /// <summary>Distinct classes with at least one instance found, versus the whole build's classes.</summary>
    public int LiveClassCount { get; init; }

    // --- The gate: can these objects account for the unowned strings? -------------------------

    public int UnknownHitsExamined { get; set; }

    /// <summary>Unowned strings with at least one referrer inside a located object.</summary>
    public int UnknownHitsWithReferrerInObject { get; set; }

    /// <summary>Of those, the ones whose container has an exact declared size rather than an inferred one.</summary>
    public int UnknownHitsWithDeclaredSizeContainer { get; set; }

    /// <summary>
    ///     Unowned strings whose every referrer lives in module space. Those referrer addresses are
    ///     stored sign-extended and therefore negative, and every ownership strategy skips them, so
    ///     these strings are unnameable by construction today however good the object inventory gets.
    /// </summary>
    public int ModuleOnlyReferrerHits { get; set; }

    public int MixedReferrerHits { get; set; }

    public int HeapOnlyReferrerHits { get; set; }

    // --- Diagnostics --------------------------------------------------------------------------

    /// <summary>Which classes hold the unowned strings. Turns the gate number into a decision.</summary>
    public Dictionary<string, int> ContainingClassCounts { get; } = [];

    /// <summary>Object bases per 16 MB address band, keyed by the leading byte pair (e.g. "0x41").</summary>
    public Dictionary<string, int> ObjectsByVaBand { get; } = [];

    /// <summary>
    ///     Object base address modulo 16. Measured on xex44 as 73.7% / 11.5% / 7.7% / 7.1% across
    ///     0 / 8 / 4 / 12 — which is why alignment is reported here and never used as a filter.
    /// </summary>
    public Dictionary<int, int> BaseAlignmentHistogram { get; } = [];
}
