namespace BethesdaMultitool.Core.Formats.Esm.Models.World;

/// <summary>
///     Radio broadcast configuration for a placed reference — the on-disk <c>XRDO</c> subrecord
///     and the runtime <c>RADIO_DATA</c> carried by an <c>ExtraRadioData</c> node, which are the
///     same four fields in the same order and widths (16 bytes total).
/// </summary>
public sealed record RadioData
{
    /// <summary>Broadcast radius in world units. Only meaningful when <see cref="RangeType" /> is Radius.</summary>
    public float Radius { get; init; }

    /// <summary>
    ///     <c>RADIO_RANGE_TYPE</c>: 0 Radius, 1 Everywhere, 2 Worldspace and linked interiors,
    ///     3 Linked interiors, 4 Current cell only.
    /// </summary>
    public uint RangeType { get; init; }

    /// <summary>Proportion of static mixed into the broadcast.</summary>
    public float StaticPercentage { get; init; }

    /// <summary>
    ///     Reference that anchors the broadcast position, or null. Types 1 and 3 need no anchor;
    ///     type 0 (Radius) needs one in an exterior cell, either this reference or the owner itself.
    /// </summary>
    public uint? PositionRefFormId { get; init; }

    /// <summary>Radius is the only range type that requires an exterior anchor.</summary>
    public bool RequiresExteriorAnchor => RangeType == 0;
}
