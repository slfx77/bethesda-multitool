namespace FalloutXbox360Utils.Core.Formats.Esm.Merge;

/// <summary>
///     Per-record-type rules controlling which subrecords come from the DMP and which are
///     retained from the source ESM. The default is "DMP wins for any subrecord present in
///     the encoded output". Specific signatures can be flagged as "always retain ESM" — for
///     example, MODT/MODS texture-set hashes are PC-format-specific and not reproducible
///     from a DMP that loaded Xbox-format textures.
/// </summary>
public sealed record SubrecordMergePolicy
{
    public static readonly SubrecordMergePolicy Default = new()
    {
        RetainFromEsm = new HashSet<string>(StringComparer.Ordinal),
        AlwaysFromDmp = new HashSet<string>(StringComparer.Ordinal)
    };

    /// <summary>
    ///     Subrecord signatures that MUST be retained from the ESM, even when the DMP
    ///     encoder produces a value for them.
    /// </summary>
    public required IReadOnlySet<string> RetainFromEsm { get; init; }

    /// <summary>
    ///     Subrecord signatures that are always taken from the DMP encoder, even when they
    ///     contradict ESM data. Reserved for fields like DATA/DNAM where runtime values
    ///     are authoritative.
    /// </summary>
    public required IReadOnlySet<string> AlwaysFromDmp { get; init; }

    /// <summary>
    ///     Builds the v1 default policy mapping per-record-type ESM-retain rules.
    ///     For texture-mod-related records, MODT/MODS/MO2T/MO3T/MO4T/MO2S/MO3S/MO4S are retained
    ///     from the source ESM because the DMP doesn't carry PC-format texture hashes.
    /// </summary>
    public static SubrecordMergePolicy ForRecordType(string recordType)
    {
        return recordType switch
        {
            "WEAP" or "ARMO" or "AMMO" or "MISC" or "KEYM" or "ALCH" or "BOOK"
                or "CONT" or "NPC_" => new SubrecordMergePolicy
                {
                    RetainFromEsm = new HashSet<string>(StringComparer.Ordinal)
                    {
                        // Texture-set hashes are PC-format-specific.
                        "MODT", "MODS",
                        "MO2T", "MO2S",
                        "MO3T", "MO3S",
                        "MO4T", "MO4S",
                        // Damage modifier table is parsed from PC ESM only on this version.
                        "DMDT"
                    },
                    AlwaysFromDmp = new HashSet<string>(StringComparer.Ordinal)
                },
            _ => Default
        };
    }
}
