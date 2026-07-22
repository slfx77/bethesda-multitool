using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     How much retail association evidence is known for one placed geometry/source-shape pair.
///     Unknown is deliberately distinct from a proven empty ordered set.
/// </summary>
internal enum FnvLocalLightAssociationKnowledge
{
    Unknown = 0,
    KnownEmpty = 1,
    KnownOrdered = 2
}

/// <summary>
///     Stable CPU-side identity for one source shape on one placed geometry reference.
/// </summary>
internal readonly record struct FnvGeometryLightAssociationKey
{
    internal FnvGeometryLightAssociationKey(
        uint geometryReferenceFormId,
        int sourceShapeBlockIndex)
    {
        ArgumentOutOfRangeException.ThrowIfZero(geometryReferenceFormId);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceShapeBlockIndex);

        GeometryReferenceFormId = geometryReferenceFormId;
        SourceShapeBlockIndex = sourceShapeBlockIndex;
    }

    internal uint GeometryReferenceFormId { get; }
    internal int SourceShapeBlockIndex { get; }
}

/// <summary>
///     Telemetry-only observation of the property-associated local-light set for one geometry
///     shape. Emitter identities are placed-light REFR FormIDs, not shared LIGH base FormIDs.
///     The current frame-global viewer selection is not evidence for this contract.
/// </summary>
internal sealed record FnvLocalLightAssociationObservation
{
    // Association/order recovery alone is not sufficient for rendering: retail-prepared color and
    // light-set-aware batching are still unresolved.
    internal const bool CanDriveRendering = false;

    private FnvLocalLightAssociationObservation(
        FnvGeometryLightAssociationKey key,
        FnvLocalLightAssociationKnowledge knowledge,
        ImmutableArray<uint>? orderedEmitterReferenceFormIds,
        string evidenceSource)
    {
        Key = key;
        Knowledge = knowledge;
        OrderedEmitterReferenceFormIds = orderedEmitterReferenceFormIds;
        EvidenceSource = evidenceSource;
    }

    internal FnvGeometryLightAssociationKey Key { get; }
    internal FnvLocalLightAssociationKnowledge Knowledge { get; }
    internal ImmutableArray<uint>? OrderedEmitterReferenceFormIds { get; }
    internal string EvidenceSource { get; }
    internal bool AssociationKnown => Knowledge != FnvLocalLightAssociationKnowledge.Unknown;
    internal bool AssociationOrderKnown => Knowledge != FnvLocalLightAssociationKnowledge.Unknown;
    internal int? AssociatedLocalLightCount => OrderedEmitterReferenceFormIds?.Length;

    internal static FnvLocalLightAssociationObservation CreateUnknown(
        FnvGeometryLightAssociationKey key,
        string evidenceSource) =>
        new(
            key,
            FnvLocalLightAssociationKnowledge.Unknown,
            null,
            ValidateEvidenceSource(evidenceSource));

    internal static FnvLocalLightAssociationObservation CreateKnownEmpty(
        FnvGeometryLightAssociationKey key,
        string evidenceSource) =>
        new(
            key,
            FnvLocalLightAssociationKnowledge.KnownEmpty,
            ImmutableArray<uint>.Empty,
            ValidateEvidenceSource(evidenceSource));

    internal static FnvLocalLightAssociationObservation CreateKnownOrdered(
        FnvGeometryLightAssociationKey key,
        IEnumerable<uint> orderedEmitterReferenceFormIds,
        string evidenceSource)
    {
        ArgumentNullException.ThrowIfNull(orderedEmitterReferenceFormIds);
        var formIds = orderedEmitterReferenceFormIds.ToImmutableArray();
        if (formIds.IsEmpty)
        {
            throw new ArgumentException(
                "A known ordered association must contain at least one emitter reference.",
                nameof(orderedEmitterReferenceFormIds));
        }

        if (formIds.Any(static formId => formId == 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(orderedEmitterReferenceFormIds),
                "Emitter reference FormIDs must be non-zero.");
        }

        return new FnvLocalLightAssociationObservation(
            key,
            FnvLocalLightAssociationKnowledge.KnownOrdered,
            formIds,
            ValidateEvidenceSource(evidenceSource));
    }

    private static string ValidateEvidenceSource(string evidenceSource)
    {
        if (string.IsNullOrWhiteSpace(evidenceSource))
        {
            throw new ArgumentException(
                "An explicit evidence source or unknown-reason label is required.",
                nameof(evidenceSource));
        }

        return evidenceSource;
    }
}
