namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;

/// <summary>
///     Package location from PLDT/PLD2 subrecord (12 bytes): a one-byte type plus three pad bytes, a
///     four-byte union whose meaning the type selects, and a signed radius.
/// </summary>
public record PackageLocation
{
    /// <summary>
    ///     Location type, from xEdit's FNV PLDT enum (<c>wbDefinitionsFNV.pas</c>): 0=Near Reference,
    ///     1=In Cell, 2=Near Current Location, 3=Near Editor Location, 4=Object ID, 5=Object Type,
    ///     6=Near Linked Reference, 7=At Package Location. Fallout 3 uses the same eight; Oblivion stops
    ///     at 5. Only 0, 1 and 4 select a FormID union arm (<c>PackageReferenceIntegrity.LocationTypeIsFormId</c>).
    /// </summary>
    public byte Type { get; init; }

    /// <summary>
    ///     The union arm the type selects: a reference (0), cell (1) or base-object (4) FormID, an
    ///     object-type enum value (5), or unused (2, 3, 6 and 7 — zero throughout the retail FNV masters).
    /// </summary>
    public uint Union { get; init; }

    /// <summary>Search radius.</summary>
    public int Radius { get; init; }

    /// <summary>
    ///     Human-readable location type name: xEdit's FNV PLDT enum, title-cased like
    ///     <see cref="PackageTarget.TypeName" />.
    /// </summary>
    public string TypeName => Type switch
    {
        0 => "Near Reference",
        1 => "In Cell",
        2 => "Near Current Location",
        3 => "Near Editor Location",
        4 => "Object ID",
        5 => "Object Type",
        6 => "Near Linked Reference",
        7 => "At Package Location",
        _ => $"Unknown ({Type})"
    };
}
