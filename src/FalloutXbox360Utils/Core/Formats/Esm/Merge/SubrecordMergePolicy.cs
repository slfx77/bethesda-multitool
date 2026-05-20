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
        AlwaysFromDmp = new HashSet<string>(StringComparer.Ordinal),
        DoNotAppendFromDmp = new HashSet<string>(StringComparer.Ordinal)
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
    ///     DMP subrecord signatures that should not be appended when the source ESM record has
    ///     no matching slot or when that matching slot was intentionally retained from ESM.
    /// </summary>
    public required IReadOnlySet<string> DoNotAppendFromDmp { get; init; }

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
                or "CONT" => new SubrecordMergePolicy
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
                    AlwaysFromDmp = new HashSet<string>(StringComparer.Ordinal),
                    DoNotAppendFromDmp = new HashSet<string>(StringComparer.Ordinal)
                    {
                        // COED (inventory item ownership/condition) is positionally paired with
                        // its preceding CNTO. The merge engine appends unconsumed DMP subrecords
                        // at the END of the stream, which produces an orphan COED far away from
                        // any CNTO — FNVEdit flags this as out-of-order and the engine ignores
                        // it (so the COED metadata wouldn't apply anyway). Drop it instead.
                        "COED"
                    }
                },
            "NPC_" or "CREA" => CreateActorMergePolicy(),
            "CELL" => new SubrecordMergePolicy
                {
                    RetainFromEsm = new HashSet<string>(StringComparer.Ordinal)
                    {
                        // Preserve master cell structure; runtime captures can misclassify interiors.
                        "DATA", "XCLC"
                    },
                    AlwaysFromDmp = new HashSet<string>(StringComparer.Ordinal),
                    DoNotAppendFromDmp = new HashSet<string>(StringComparer.Ordinal)
                    {
                        "DATA", "XCLC"
                    }
                },
            _ => Default
        };
    }

    /// <summary>
    ///     Actor (NPC_/CREA) override policy. We retain ONLY FormID-bearing identity references
    ///     (race, script, class, eyes, voice, hair, head parts, combat style). These FormIDs
    ///     may point at prototype-only records that don't exist in master; letting them through
    ///     causes the engine's NPC-init bind to fail partially, which manifests as gore caps
    ///     on living NPCs (race mismatch → wrong body part data) and partial dismemberment.
    ///
    ///     We DO let through raw-data fields: FGGS/FGGA/FGTS (FaceGen coefficient blobs),
    ///     HCLR/LNAM/NAM4/NAM5/NAM6/NAM7 (hair color, length, skeleton scale). These aren't
    ///     FormIDs and can't dangle — retaining them blocks prototype FaceGen changes from
    ///     reaching the rendered actor (Sunny Smiles' face stayed master-default).
    ///
    ///     Each retained signature must also be in <see cref="DoNotAppendFromDmp" />, because
    ///     <see cref="RetainFromEsm" /> only controls Pass 1 (ESM-positional merge) and leaves
    ///     the DMP copy unconsumed — Pass 2 then appends it at the end of the record, producing
    ///     a duplicate subrecord that crashes plugin load.
    /// </summary>
    private static SubrecordMergePolicy CreateActorMergePolicy()
    {
        var identityFields = new HashSet<string>(StringComparer.Ordinal)
        {
            // Texture-set hashes are PC-format-specific (not reproducible from Xbox textures).
            "MODT", "MODS",
            "MO2T", "MO2S",
            "MO3T", "MO3S",
            "MO4T", "MO4S",
            // Damage modifier table is parsed from PC ESM only on this version.
            "DMDT",
            // FormID-bearing identity references. Prototype FormIDs that aren't in master
            // break NPC-init and cause visual body-part failure on the rendered actor.
            "RNAM", // Race FormID — wrong race = wrong body part data = gore caps on living actors
            "SCRI", // Script FormID
            "ZNAM", // Combat Style FormID
            "CNAM", // Class FormID
            "ENAM", // Eyes FormID
            "VTCK", // Voice Type FormID
            "HNAM", // Hair FormID
            "PNAM" // Head Part FormID list (multi-occurrence)
        };

        // DoNotAppendFromDmp must include every identity field + COED (the inventory-pair
        // orphan from CNTO/COED merging seen in xex21 NPC_:0011A509).
        var doNotAppend = new HashSet<string>(identityFields, StringComparer.Ordinal)
        {
            "COED"
        };

        return new SubrecordMergePolicy
        {
            RetainFromEsm = identityFields,
            AlwaysFromDmp = new HashSet<string>(StringComparer.Ordinal),
            DoNotAppendFromDmp = doNotAppend
        };
    }
}
