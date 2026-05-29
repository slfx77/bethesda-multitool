namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;

/// <summary>
///     Provenance of a <see cref="CatalogEntry" /> — which input produced this record.
///     <c>DispositionEngine</c> branches on this when deciding KeepMaster / Override / New / Skip.
/// </summary>
public enum SourceKind
{
    /// <summary>Came from the master ESM and is not overridden by any DMP capture.</summary>
    MasterOnly,

    /// <summary>
    ///     Came from a DMP capture and a master record with the same FormID exists.
    ///     The disposition engine decides whether to actually emit an override.
    /// </summary>
    DmpOverride,

    /// <summary>
    ///     Came from a DMP capture and no master record with this FormID exists.
    ///     A new plugin-range FormID will be allocated for it in phase C.
    /// </summary>
    DmpNew,

    /// <summary>
    ///     Reserved for future FO3 ESM full-body conversion (Plan C from the prior plan).
    ///     Not produced by the Tier 0 catalog; here so policies don't have to bend later.
    /// </summary>
    Fo3Source,
}

/// <summary>
///     One entry in the planner's input catalog. Uniform view over master ESM + DMP +
///     (future) FO3 inputs. Phase A produces these; phase B (Disposition) consumes them.
/// </summary>
public sealed record CatalogEntry
{
    /// <summary>Record signature, e.g. <c>"REFR"</c>, <c>"SCPT"</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Where this entry came from.</summary>
    public required SourceKind Source { get; init; }

    /// <summary>
    ///     The master FormID when one exists for this record (<see cref="SourceKind.MasterOnly" />
    ///     or <see cref="SourceKind.DmpOverride" />). Null for <see cref="SourceKind.DmpNew" />.
    /// </summary>
    public uint? MasterFormId { get; init; }

    /// <summary>
    ///     The FormID the DMP capture had for this record. Null for
    ///     <see cref="SourceKind.MasterOnly" />. May equal <see cref="MasterFormId" /> on overrides.
    /// </summary>
    public uint? DmpFormId { get; init; }

    /// <summary>
    ///     The typed DMP model when applicable. Null for <see cref="SourceKind.MasterOnly" />.
    /// </summary>
    public object? Model { get; init; }

    /// <summary>
    ///     The parsed master record when applicable. Null for <see cref="SourceKind.DmpNew" />.
    /// </summary>
    public ParsedMainRecord? Master { get; init; }
}
