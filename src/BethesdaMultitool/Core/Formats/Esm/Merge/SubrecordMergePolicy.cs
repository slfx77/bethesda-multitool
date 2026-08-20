namespace BethesdaMultitool.Core.Formats.Esm.Merge;

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
    ///     Per-signature byte reconcilers applied when BOTH the ESM and the DMP carry the
    ///     signature: <c>(esmBytes, dmpBytes) → emittedBytes</c>. For fields where the runtime
    ///     write-back is only PARTIALLY trustworthy — e.g. NPC_ AIDT, where the engine discards
    ///     the aggro radius at load and the capture writes back 0 — a reconciler can splice the
    ///     authoritative master lanes into the captured payload instead of choosing a whole side.
    /// </summary>
    public IReadOnlyDictionary<string, Func<byte[], byte[], byte[]>>? FieldReconcilers { get; init; }

    /// <summary>
    ///     Value-aware append gates for DMP-only subrecords (pass 2). Unlike
    ///     <see cref="DoNotAppendFromDmp" /> — a blanket ban — a filter sees the encoded bytes
    ///     and returns false to skip the append. Used to keep runtime-materialized defaults
    ///     (NPC_ NAM6 Height=1.0 where the master deliberately omits the subrecord) from being
    ///     grafted onto records whose master never carried them.
    /// </summary>
    public IReadOnlyDictionary<string, Func<byte[], bool>>? AppendFilters { get; init; }

    /// <summary>
    ///     Return an immutable-by-construction extension for an exact-record diagnostic.
    ///     Every retained signature is also blocked from the DMP append pass; retaining it
    ///     in only the positional pass would create a duplicate subrecord at record end.
    /// </summary>
    public SubrecordMergePolicy WithAdditionalMasterRetention(IEnumerable<string> signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);

        var additional = signatures.ToHashSet(StringComparer.Ordinal);
        var retained = new HashSet<string>(RetainFromEsm, StringComparer.Ordinal);
        retained.UnionWith(additional);
        var doNotAppend = new HashSet<string>(DoNotAppendFromDmp, StringComparer.Ordinal);
        doNotAppend.UnionWith(additional);

        return this with
        {
            RetainFromEsm = retained,
            DoNotAppendFromDmp = doNotAppend
        };
    }

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
    ///     We DO let through raw-data fields: FGGS/FGGA/FGTS (FaceGen coefficient blobs),
    ///     HCLR/LNAM/NAM4/NAM5/NAM6/NAM7 (hair color, length, skeleton scale). These aren't
    ///     FormIDs and can't dangle — retaining them blocks prototype FaceGen changes from
    ///     reaching the rendered actor (Sunny Smiles' face stayed master-default).
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
            "PNAM", // Head Part FormID list (multi-occurrence)
            // PKID — AI package list (multi-occurrence). The DMP-reconstructed proto packages —
            // especially Patrol packages — carry route markers / XLKR chains we don't fully
            // reconstruct, so building PatrolActorPackageData from them AVs at load
            // (RSFoxtrotLenk 0x00116B40: master's AtRadio+Sandbox packages replaced by the broken
            // proto FoxtrotCommPatrol 0x0100160D → eip 0x0040FE9F). Retain master's package list so
            // overridden actors keep working AI; orphaned proto PACKs are then harmless. Reconstructing
            // proto patrol routes end-to-end is the deferred deep fix.
            "PKID"
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
            DoNotAppendFromDmp = doNotAppend,
            FieldReconcilers = new Dictionary<string, Func<byte[], byte[], byte[]>>(StringComparer.Ordinal)
            {
                ["AIDT"] = ReconcileActorAiData,
                ["NAM6"] = ReconcileActorHeight
            },
            AppendFilters = new Dictionary<string, Func<byte[], bool>>(StringComparer.Ordinal)
            {
                // Master deliberately omits NAM6 on these NPCs; grafting the runtime-
                // materialized default onto them fabricates a subrecord retail never shipped
                // (xex44: baseline AND proto-360 carry no NAM6 while our override appended 1.0).
                ["NAM6"] = bytes => bytes.Length == 4 && !IsMaterializedHeight(BitConverter.ToSingle(bytes, 0))
            }
        };
    }

    /// <summary>
    ///     USER POLICY 2026-08-03: AIDT should match what's in the proto FILE, not the runtime
    ///     write-back. Two lanes of the captured 20-byte AIDT are load-time artifacts:
    ///     the 3 unused pad bytes @5-7 (runtime zero-fills; the file carries uninitialized GECK
    ///     noise — PROTO-360 agrees with the master, our zeros are capture-side), and
    ///     AggroRadius @16-19 when AggroRadiusBehavior @15 is 0 (the engine discards the radius
    ///     at load and the capture writes back 0; master AND proto both store 500). Splice the
    ///     master's bytes back into those lanes; every other captured lane wins as usual.
    /// </summary>
    private static byte[] ReconcileActorAiData(byte[] esmBytes, byte[] dmpBytes)
    {
        if (esmBytes.Length != 20 || dmpBytes.Length != 20)
        {
            return dmpBytes;
        }

        var merged = (byte[])dmpBytes.Clone();
        merged[5] = esmBytes[5];
        merged[6] = esmBytes[6];
        merged[7] = esmBytes[7];

        var capturedBehavior = dmpBytes[15];
        var capturedRadius = BitConverter.ToUInt32(dmpBytes, 16);
        if (capturedBehavior == 0 && capturedRadius == 0)
        {
            merged[15] = esmBytes[15];
            Array.Copy(esmBytes, 16, merged, 16, 4);
        }

        return merged;
    }

    /// <summary>
    ///     USER POLICY 2026-08-03: NPC_ NAM6 Height 1.0 (and the child-race 0.8) is the engine's
    ///     runtime-materialized value, not authored data — masters store 0.0 or omit the
    ///     subrecord entirely, and the capture bakes the materialized default back in (1,698×
    ///     1.0-vs-0 and 29× 0.8-vs-0 on xex21 overrides, against 4 genuine nonzero diffs).
    ///     Keep the master's bytes when the capture holds a materialized default.
    /// </summary>
    private static byte[] ReconcileActorHeight(byte[] esmBytes, byte[] dmpBytes)
    {
        if (dmpBytes.Length != 4)
        {
            return dmpBytes;
        }

        return IsMaterializedHeight(BitConverter.ToSingle(dmpBytes, 0)) ? esmBytes : dmpBytes;
    }

    /// <summary>The two runtime-materialized NPC height defaults: adult 1.0, child-race 0.8.</summary>
    private static bool IsMaterializedHeight(float height)
    {
        return MathF.Abs(height - 1.0f) < 1e-5f || MathF.Abs(height - 0.8f) < 1e-5f;
    }
}
