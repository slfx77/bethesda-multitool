using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a placed-grenade record (PGRE) as PC-format subrecord bytes. PGRE is a
///     cell-children record analogous to REFR/ACHR/ACRE — the engine treats it as a
///     placed projectile/grenade reference. For Phase 10 the model captures identity,
///     base object, and position; physics state is intentionally skipped. The override
///     and new-record paths therefore both emit just NAME + DATA.
/// </summary>
/// <remarks>
///     Cell-children dispatch routing is not yet wired (no PGRE→parent-cell mapping on
///     the model). This encoder is registered through <c>PlannedPgreEncoder</c> so it's
///     ready when that follow-up lands, but PGRE records currently emit zero from any
///     production pipeline. Tests exercise the encoder directly.
/// </remarks>
internal static class PgreEncoder
{
    /// <summary>
    ///     New-record path. Emits NAME (base form) + DATA (24 bytes: 3 floats position +
    ///     3 floats zero rotation). The optional <paramref name="validFormIds" /> /
    ///     <paramref name="remapTable" /> parameters mirror
    ///     <see cref="RefrEncoder.EncodeNewPlacedReference" />'s shape; they're currently
    ///     unused because PGRE only references its base form via NAME and the legacy
    ///     placed-ref pattern emits NAME directly without validity resolution.
    /// </summary>
    internal static EncodedRecord EncodeNew(
        PlacedGrenadeRecord placed,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        _ = validFormIds;
        _ = remapTable;

        var subs = new List<EncodedSubrecord>(2)
        {
            NewRecordSubrecords.EncodeFormIdSubrecord("NAME", placed.BaseFormId),
            new("DATA", BuildDataSubrecord(placed)),
        };

        return new EncodedRecord
        {
            Subrecords = subs,
            Warnings = []
        };
    }

    /// <summary>
    ///     Override path. Emits NAME when the DMP captured a base form + DATA carrying
    ///     the captured position. Matches the override shape of
    ///     <see cref="RefrEncoder.EncodePlacedReference" /> for consistency.
    /// </summary>
    internal static EncodedRecord EncodeOverride(PlacedGrenadeRecord placed)
    {
        var subs = new List<EncodedSubrecord>(2);

        if (placed.BaseFormId != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("NAME", placed.BaseFormId));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(placed)));

        return new EncodedRecord
        {
            Subrecords = subs,
            Warnings = []
        };
    }

    private static byte[] BuildDataSubrecord(PlacedGrenadeRecord placed)
    {
        var data = new byte[24];
        SubrecordEncoder.WriteFloat(data, 0, placed.PositionX);
        SubrecordEncoder.WriteFloat(data, 4, placed.PositionY);
        SubrecordEncoder.WriteFloat(data, 8, placed.PositionZ);
        // bytes 12-23: rotation X/Y/Z = 0 (physics state intentionally skipped per Phase 10 scope).
        return data;
    }
}
