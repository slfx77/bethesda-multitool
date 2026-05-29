namespace FalloutXbox360Utils.Core.Formats.Esm.Planner;

/// <summary>
///     Describes a subrecord reshape applied by the planner when a contained FormID dangles.
///     The classic case is PACK <c>PLDT</c>: when <c>PLDT.Union</c> points at a non-emitted
///     FormID, the planner reshapes <c>PLDT</c> from <c>Type 0 (NearReference)</c> to
///     <c>Type 2 (NearCurrentLocation)</c>, dropping the dangling pointer.
/// </summary>
public sealed record ContainerDowngrade
{
    /// <summary>Signature of the containing subrecord, e.g. <c>"PLDT"</c>.</summary>
    public required string ContainerSignature { get; init; }

    /// <summary>Human-readable label of the original shape, e.g. <c>"Type 0 (NearReference)"</c>.</summary>
    public required string FromShape { get; init; }

    /// <summary>Human-readable label of the reshaped form, e.g. <c>"Type 2 (NearCurrentLocation)"</c>.</summary>
    public required string ToShape { get; init; }
}
