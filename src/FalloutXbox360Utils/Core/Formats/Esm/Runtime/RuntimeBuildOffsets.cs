using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Computes field offset shifts between early-era and final-era Xbox 360 builds.
///     Final-era builds (from ~March 30, 2010 onwards) use REFR = 120 bytes.
///     Early-era builds (before ~March 30, 2010) use REFR = 116 bytes.
///     The difference is a 4-byte field in TESChildCell (vtable-only in early builds,
///     vtable + 4B data in final builds). This shifts all OBJ_REFR and later fields by -4.
///     The TESForm base class is 40 bytes in both eras.
///     Note: The Proto Debug PDB (July 2010, TESForm=24) does NOT match these early DMPs.
/// </summary>
internal static class RuntimeBuildOffsets
{
    /// <summary>
    ///     Reverse map: ESM 4-letter signature → final-build FormType byte.
    ///     Built lazily from <see cref="GetRecordTypeCode" />.
    /// </summary>
    private static Dictionary<string, byte>? _signatureToFormType;

    private static Dictionary<string, byte> SignatureToFormType
    {
        get
        {
            if (_signatureToFormType != null) return _signatureToFormType;
            var map = new Dictionary<string, byte>();
            for (var i = 0; i <= 0x78; i++)
            {
                var code = GetRecordTypeCode((byte)i);
                if (code != null)
                {
                    map[code] = (byte)i;
                }
            }

            _signatureToFormType = map;
            return map;
        }
    }

    /// <summary>
    ///     Returns the field offset shift from Proto Debug PDB values to actual dump values.
    ///     Currently +16 for all known builds. The mechanism is retained for future extensibility
    ///     in case a Proto Debug era dump is ever encountered (would need +4 shift).
    /// </summary>
    public static int GetPdbShift(string? _buildType)
    {
        return 16;
    }

    /// <summary>
    ///     Returns the REFR field offset delta for early-era builds.
    ///     Early builds: OBJ_REFR and BSExtraList fields are 4 bytes earlier
    ///     because TESChildCell is vtable-only (4B) vs vtable+data (8B) in final.
    ///     Only applies to REFR/ACHR/ACRE forms that inherit from TESChildCell.
    /// </summary>
    public static int GetRefrFieldShift(bool isEarlyBuild)
    {
        return isEarlyBuild ? -4 : 0;
    }

    /// <summary>
    ///     Returns the field offset delta for TESWorldSpace forms in early-era builds.
    ///     RTTI analysis of the Nov 2009 module binary shows TESWorldSpace = 240 bytes
    ///     vs 244 bytes in MemDebug PDB (July 2010). The 4-byte difference shifts all
    ///     post-base-class fields by -4 in early builds.
    ///     TESObjectCELL (192 bytes) is the same size in both eras; CELL field drift
    ///     is handled separately by the layout probe.
    /// </summary>
    public static int GetWorldCellFieldShift(bool isEarlyBuild)
    {
        return isEarlyBuild ? -4 : 0;
    }

    /// <summary>
    ///     Returns the C++ struct size for a given runtime FormType byte.
    ///     Sizes from MemDebug PDB (Fallout_Release_MemDebug.pdb, TESForm=40 bytes).
    ///     Used for memory map region sizing in DMP analysis.
    /// </summary>
    public static int GetStructSize(byte formType)
    {
        return formType switch
        {
            // Source: MemDebug PDB ENUM_FORM_ID cross-referenced with LF_CLASS struct sizes
            0x01 => 1068, // TESFile (TES4)
            0x03 => 12, // SettingT<GameSettingCollection> (GMST)
            0x04 => 192, // BGSTextureSet (TXST)
            0x05 => 52, // BGSMenuIcon (MICN)
            0x06 => 48, // TESGlobal (GLOB)
            0x07 => 112, // TESClass (CLAS)
            0x08 => 92, // TESFaction (FACT)
            0x09 => 96, // BGSHeadPart (HDPT)
            0x0A => 92, // TESHair (HAIR)
            0x0B => 68, // TESEyes (EYES)
            0x0C => 1260, // TESRace (RACE)
            0x0D => 116, // TESSound (SOUN)
            0x0E => 100, // BGSAcousticSpace (ASPC)
            0x0F => 112, // TESSkill (SKIL)
            0x10 => 192, // EffectSetting (MGEF)
            0x11 => 100, // Script (SCPT)
            0x12 => 56, // TESLandTexture (LTEX)
            0x13 => 84, // EnchantmentItem (ENCH)
            0x14 => 84, // SpellItem (SPEL)
            0x15 => 160, // TESObjectACTI (ACTI)
            0x16 => 168, // BGSTalkingActivator (TACT)
            0x17 => 184, // BGSTerminal (TERM)
            0x18 => 416, // TESObjectARMO (ARMO)
            0x19 => 212, // TESObjectBOOK (BOOK)
            0x1A => 356, // TESObjectCLOT (CLOT)
            0x1B => 172, // TESObjectCONT (CONT)
            0x1C => 160, // TESObjectDOOR (DOOR)
            0x1D => 180, // IngredientItem (INGR)
            0x1E => 216, // TESObjectLIGH (LIGH)
            0x1F => 188, // TESObjectMISC (MISC)
            0x20 => 104, // TESObjectSTAT (STAT)
            0x21 => 96, // BGSStaticCollection (SCOL)
            0x22 => 132, // BGSMovableStatic (MSTT)
            0x23 => 96, // BGSPlaceableWater (PWAT)
            0x24 => 120, // TESGrass (GRAS)
            0x25 => 164, // TESObjectTREE (TREE)
            0x26 => 172, // TESFlora (FLOR)
            0x27 => 164, // TESFurniture (FURN)
            0x28 => 924, // TESObjectWEAP (WEAP)
            0x29 => 236, // TESAmmo (AMMO)
            0x2A => 508, // TESNPC (NPC_)
            0x2B => 368, // TESCreature (CREA)
            0x2C => 128, // TESLevCharacter (LVLC)
            0x2D => 128, // TESLevCreature (LVLN)
            0x2E => 188, // TESKey (KEYM)
            0x2F => 232, // AlchemyItem (ALCH)
            0x30 => 80, // BGSIdleMarker (IDLM)
            0x31 => 144, // BGSNote (NOTE)
            0x32 => 196, // BGSConstructibleObject (COBJ)
            0x33 => 224, // BGSProjectile (PROJ)
            0x34 => 92, // TESLevItem (LVLI)
            0x35 => 900, // TESWeather (WTHR)
            0x36 => 112, // TESClimate (CLMT)
            0x37 => 72, // TESRegion (REGN)
            0x38 => 80, // NavMeshInfoMap (NAVI)
            0x39 => 192, // TESObjectCELL (CELL)
            0x3A => 120, // TESObjectREFR (REFR)
            0x3B => 472, // Character (ACHR)
            0x3C => 464, // Creature (ACRE)
            0x3D => 400, // MissileProjectile (PMIS)
            0x3E => 400, // GrenadeProjectile (PGRE)
            0x3F => 400, // BeamProjectile (PBEA)
            0x40 => 400, // FlameProjectile (PFLA)
            0x41 => 244, // TESWorldSpace (WRLD)
            0x43 => 280, // NavMesh (NAVM)
            0x44 => 60, // TESObjectLAND (TLOD)
            0x45 => 80, // TESTopic (DIAL)
            0x46 => 96, // TESTopicInfo (INFO)
            0x47 => 116, // TESQuest (QUST)
            0x48 => 92, // TESIdleForm (IDLE)
            0x49 => 144, // TESPackage (PACK)
            0x4A => 280, // TESCombatStyle (CSTY)
            0x4B => 80, // TESLoadScreen (LSCR)
            0x4C => 92, // TESLevSpell (LVSP)
            0x4D => 76, // TESObjectANIO (ANIO)
            0x4E => 420, // TESWaterForm (WATR)
            0x4F => 384, // TESEffectShader (EFSH)
            0x51 => 184, // BGSExplosion (EXPL)
            0x52 => 52, // BGSDebris (DEBR)
            0x53 => 200, // TESImageSpace (IMGS)
            0x54 => 1864, // TESImageSpaceModifier (IMAD)
            0x55 => 52, // BGSListForm (FLST)
            0x56 => 96, // BGSPerk (PERK)
            0x57 => 132, // BGSBodyPartData (BPTD)
            0x58 => 112, // BGSAddonNode (ADDN)
            0x59 => 212, // ActorValueInfo (AVIF)
            0x5A => 48, // BGSRadiationStage (RADS)
            0x5B => 136, // BGSCameraShot (CAMS)
            0x5C => 72, // BGSCameraPath (CPTH)
            0x5D => 44, // BGSVoiceType (VTYP)
            0x5E => 136, // BGSImpactData (IPCT)
            0x5F => 92, // BGSImpactDataSet (IPDS)
            0x60 => 416, // TESObjectARMA (ARMA)
            0x61 => 64, // BGSEncounterZone (ECZN)
            0x62 => 80, // BGSMessage (MESG)
            0x63 => 344, // BGSRagdoll (RGDL)
            0x64 => 176, // BGSDefaultObjectManager (DOBJ)
            0x65 => 84, // BGSLightingTemplate (LGTM)
            0x66 => 68, // BGSMusicType (MUSC)
            0x67 => 192, // TESObjectIMOD (IMOD)
            0x68 => 96, // TESReputation (REPU)
            0x6A => 108, // TESRecipe (RCPE)
            0x6B => 56, // TESRecipeCategory (RCCT)
            0x6C => 172, // TESCasinoChips (CHIP)
            0x6D => 560, // TESCasino (CSNO)
            0x6E => 128, // TESLoadScreenType (LSCT)
            0x6F => 212, // MediaSet (MSET)
            0x70 => 200, // MediaLocationController (ALOC)
            0x71 => 140, // TESChallenge (CHAL)
            0x72 => 64, // TESAmmoEffect (AMEF)
            0x73 => 204, // TESCaravanCard (CCRD)
            0x74 => 220, // TESCaravanMoney (CMNY)
            0x75 => 60, // TESCaravanDeck (CDCK)
            0x76 => 48, // BGSDehydrationStage (DEHY)
            0x77 => 48, // BGSHungerStage (HUNG)
            0x78 => 48, // BGSSleepDeprevationStage (SLPD)
            _ => 40 // TESForm base size
        };
    }

    /// <summary>
    ///     Maps a runtime FormType byte (ENUM_FORM_ID) to its 4-letter ESM record signature.
    ///     Values derived from PDB ENUM_FORM_ID (LF_ENUM, 122 members, sequential 0-120).
    ///     Source: MemDebug PDB (Fallout_Release_MemDebug.pdb).
    ///     Returns null for unrecognized types.
    /// </summary>
    public static string? GetRecordTypeCode(byte formType)
    {
        return formType switch
        {
            0x01 => "TES4",
            0x02 => "GRUP",
            0x03 => "GMST",
            0x04 => "TXST",
            0x05 => "MICN",
            0x06 => "GLOB",
            0x07 => "CLAS",
            0x08 => "FACT",
            0x09 => "HDPT",
            0x0A => "HAIR",
            0x0B => "EYES",
            0x0C => "RACE",
            0x0D => "SOUN",
            0x0E => "ASPC",
            0x0F => "SKIL",
            0x10 => "MGEF",
            0x11 => "SCPT",
            0x12 => "LTEX",
            0x13 => "ENCH",
            0x14 => "SPEL",
            0x15 => "ACTI",
            0x16 => "TACT",
            0x17 => "TERM",
            0x18 => "ARMO",
            0x19 => "BOOK",
            0x1A => "CLOT",
            0x1B => "CONT",
            0x1C => "DOOR",
            0x1D => "INGR",
            0x1E => "LIGH",
            0x1F => "MISC",
            0x20 => "STAT",
            0x21 => "SCOL",
            0x22 => "MSTT",
            0x23 => "PWAT",
            0x24 => "GRAS",
            0x25 => "TREE",
            0x26 => "FLOR",
            0x27 => "FURN",
            0x28 => "WEAP",
            0x29 => "AMMO",
            0x2A => "NPC_",
            0x2B => "CREA",
            0x2C => "LVLC",
            0x2D => "LVLN",
            0x2E => "KEYM",
            0x2F => "ALCH",
            0x30 => "IDLM",
            0x31 => "NOTE",
            0x32 => "COBJ",
            0x33 => "PROJ",
            0x34 => "LVLI",
            0x35 => "WTHR",
            0x36 => "CLMT",
            0x37 => "REGN",
            0x38 => "NAVI",
            0x39 => "CELL",
            0x3A => "REFR",
            0x3B => "ACHR",
            0x3C => "ACRE",
            0x3D => "PMIS",
            0x3E => "PGRE",
            0x3F => "PBEA",
            0x40 => "PFLA",
            0x41 => "WRLD",
            0x42 => "LAND",
            0x43 => "NAVM",
            0x44 => "TLOD",
            0x45 => "DIAL",
            0x46 => "INFO",
            0x47 => "QUST",
            0x48 => "IDLE",
            0x49 => "PACK",
            0x4A => "CSTY",
            0x4B => "LSCR",
            0x4C => "LVSP",
            0x4D => "ANIO",
            0x4E => "WATR",
            0x4F => "EFSH",
            0x50 => "TOFT",
            0x51 => "EXPL",
            0x52 => "DEBR",
            0x53 => "IMGS",
            0x54 => "IMAD",
            0x55 => "FLST",
            0x56 => "PERK",
            0x57 => "BPTD",
            0x58 => "ADDN",
            0x59 => "AVIF",
            0x5A => "RADS",
            0x5B => "CAMS",
            0x5C => "CPTH",
            0x5D => "VTYP",
            0x5E => "IPCT",
            0x5F => "IPDS",
            0x60 => "ARMA",
            0x61 => "ECZN",
            0x62 => "MESG",
            0x63 => "RGDL",
            0x64 => "DOBJ",
            0x65 => "LGTM",
            0x66 => "MUSC",
            0x67 => "IMOD",
            0x68 => "REPU",
            0x69 => "PCBE",
            0x6A => "RCPE",
            0x6B => "RCCT",
            0x6C => "CHIP",
            0x6D => "CSNO",
            0x6E => "LSCT",
            0x6F => "MSET",
            0x70 => "ALOC",
            0x71 => "CHAL",
            0x72 => "AMEF",
            0x73 => "CCRD",
            0x74 => "CMNY",
            0x75 => "CDCK",
            0x76 => "DEHY",
            0x77 => "HUNG",
            0x78 => "SLPD",
            _ => null
        };
    }

    /// <summary>
    ///     Detect FormType code drift by cross-referencing runtime EditorID entries
    ///     (which carry the FormType byte read from memory) against ESM-scanned records
    ///     (which carry the 4-letter record signature). If a type was inserted or removed
    ///     in the ENUM_FORM_ID during development, all types after the insertion point
    ///     shift by ±1. Returns a remap dictionary (dmpFormType → finalFormType) or null
    ///     if no drift is detected.
    /// </summary>
    public static Dictionary<byte, byte>? DetectFormTypeDrift(
        IReadOnlyList<RuntimeEditorIdEntry> runtimeEntries,
        IReadOnlyList<DetectedMainRecord> esmRecords)
    {
        // Build ESM lookup: FormId → ESM signature
        var esmFormIdToSignature = new Dictionary<uint, string>();
        foreach (var rec in esmRecords)
        {
            esmFormIdToSignature.TryAdd(rec.FormId, rec.RecordType);
        }

        // For each runtime entry that has a matching ESM record, compare FormType vs expected
        // Collect (dmpFormType, expectedFormType) pairs
        var mismatches = new Dictionary<byte, Dictionary<byte, int>>(); // dmpType → { expectedType → count }
        var matches = 0;

        foreach (var entry in runtimeEntries)
        {
            if (!esmFormIdToSignature.TryGetValue(entry.FormId, out var esmSig))
            {
                continue;
            }

            if (!SignatureToFormType.TryGetValue(esmSig, out var expectedFormType))
            {
                continue;
            }

            if (entry.FormType == expectedFormType)
            {
                matches++;
                continue;
            }

            if (!mismatches.TryGetValue(entry.FormType, out var targets))
            {
                targets = new Dictionary<byte, int>();
                mismatches[entry.FormType] = targets;
            }

            targets.TryGetValue(expectedFormType, out var count);
            targets[expectedFormType] = count + 1;
        }

        var totalMismatched = mismatches.Values.Sum(d => d.Values.Sum());
        Logger.Instance.Info(
            $"[FormType Drift] Cross-ref: {runtimeEntries.Count} runtime, " +
            $"{esmFormIdToSignature.Count} ESM, {matches} match, {totalMismatched} mismatch");

        if (mismatches.Count == 0 && matches > 0)
        {
            // No ESM cross-reference mismatches. Try distribution-based heuristic:
            // If all ESM records are for types below a certain threshold, a shift above
            // that threshold would be invisible to cross-reference. Detect by finding
            // "displaced" high-entry-count types in the runtime EditorID distribution.
            var heuristicRemap = DetectDriftFromDistribution(runtimeEntries, matches);
            if (heuristicRemap != null)
            {
                return heuristicRemap;
            }

            return null;
        }

        if (mismatches.Count == 0)
        {
            return null;
        }

        // Build confirmed remaps: for each mismatched dmpType, pick the most common expected type.
        // A mismatch with ≥2 agreeing records is considered a candidate.
        var candidates = new Dictionary<byte, (byte FinalType, int Count)>();
        foreach (var (dmpType, targets) in mismatches)
        {
            var best = targets.MaxBy(kv => kv.Value);
            if (best.Value >= 2)
            {
                candidates[dmpType] = (best.Key, best.Value);
            }
        }

        // Detect systematic shift pattern: if all candidates share a small delta (±1 or ±2),
        // and all matches are for types BELOW the drift point (consistent with partial shift),
        // extrapolate to all types >= the lowest candidate code.
        var remap = new Dictionary<byte, byte>();
        if (candidates.Count > 0)
        {
            var deltas = candidates.Select(kv => kv.Value.FinalType - kv.Key).Distinct().ToList();
            if (deltas.Count == 1 && Math.Abs(deltas[0]) <= 2)
            {
                var delta = deltas[0];
                var lowestDmpCode = candidates.Keys.Min();
                var totalCandidateRecords = candidates.Values.Sum(c => c.Count);

                // Verify matches are consistent: all matches should be for types below the drift point.
                // Types below the insertion point have the same codes in both builds.
                var matchesAboveDriftPoint = CountMatchesAtOrAbove(
                    runtimeEntries, esmFormIdToSignature, lowestDmpCode);

                if (totalCandidateRecords >= 2 && matchesAboveDriftPoint == 0)
                {
                    // Extend the shift range downward using distribution analysis.
                    // The ESM cross-reference may not cover types like WRLD, LAND, NAVM
                    // that rarely appear as embedded ESM records in DMP files.
                    // Check for displaced anchor types below the confirmed range.
                    var extendedStart = lowestDmpCode;
                    if (delta == -1)
                    {
                        // Build per-FormType entry counts
                        var entryCounts = new Dictionary<byte, int>();
                        foreach (var entry in runtimeEntries)
                        {
                            entryCounts.TryGetValue(entry.FormType, out var c);
                            entryCounts[entry.FormType] = c + 1;
                        }

                        // Check anchor types below the confirmed range:
                        // If code (X+1) has entries but code X has 0, and X is a known type,
                        // the data at (X+1) is likely X shifted by +1.
                        var anchors = new byte[] { 0x41, 0x39 }; // WRLD, CELL
                        foreach (var expected in anchors)
                        {
                            if (expected >= lowestDmpCode) continue;
                            var displaced = (byte)(expected + 1); // where it would be with +1 shift
                            entryCounts.TryGetValue(expected, out var expectedCount);
                            entryCounts.TryGetValue(displaced, out var displacedCount);
                            if (expectedCount == 0 && displacedCount >= 5 && displaced < extendedStart)
                            {
                                extendedStart = displaced;
                            }
                        }
                    }

                    Logger.Instance.Info(
                        $"[FormType Drift] Systematic shift detected: delta={delta:+0;-0;0} " +
                        $"starting at DMP code 0x{extendedStart:X2} " +
                        $"(confirmed at 0x{lowestDmpCode:X2}, {candidates.Count} types, " +
                        $"{totalCandidateRecords} records, {matches} unaffected matches below)");

                    for (var code = extendedStart; code <= 0x78; code++)
                    {
                        var target = (byte)(code + delta);
                        if (target <= 0x78 && GetRecordTypeCode(target) != null)
                        {
                            remap[code] = target;
                        }
                    }
                }
            }

            // Fallback: if systematic detection didn't fire, use individually confirmed remaps
            // with a stronger threshold (≥5 records) and reasonable delta (±2)
            if (remap.Count == 0)
            {
                foreach (var (dmpType, (finalType, count)) in candidates)
                {
                    if (count >= 5 && Math.Abs(finalType - dmpType) <= 2)
                    {
                        remap[dmpType] = finalType;
                    }
                }
            }
        }

        if (remap.Count > 0)
        {
            var candidateStr = string.Join(", ",
                candidates.Select(kv =>
                    $"0x{kv.Key:X2}→0x{kv.Value.FinalType:X2} ({GetRecordTypeCode(kv.Value.FinalType)})"));
            Logger.Instance.Info(
                $"[FormType Drift] Applying {remap.Count} remapped codes. " +
                $"Evidence: [{candidateStr}]. " +
                $"Range: 0x{remap.Keys.Min():X2}–0x{remap.Keys.Max():X2}");
        }

        return remap.Count > 0 ? remap : null;
    }

    /// <summary>
    ///     Detect FormType drift using runtime EditorID distribution when ESM cross-references
    ///     are insufficient (all ESM records below drift point). Looks for a contiguous block of
    ///     displaced types: codes that have entries but where the PREVIOUS code (delta -1) is the
    ///     expected final type, suggesting a +1 shift from an inserted enum value.
    ///     Requires: (1) a well-known high-count anchor type (DIAL/INFO) is displaced,
    ///     (2) shift is consistent across all displaced codes, (3) gap at each expected code.
    /// </summary>
    private static Dictionary<byte, byte>? DetectDriftFromDistribution(
        IReadOnlyList<RuntimeEditorIdEntry> runtimeEntries,
        int confirmedMatchesBelow)
    {
        // Build per-FormType entry counts from the runtime EditorID table
        var counts = new Dictionary<byte, int>();
        foreach (var entry in runtimeEntries)
        {
            counts.TryGetValue(entry.FormType, out var c);
            counts[entry.FormType] = c + 1;
        }

        // Anchor types: high-count types whose expected code should have many entries.
        // DIAL (0x45) typically has 5K-15K entries in FNV. If code 0x46 has that many
        // but 0x45 has 0, that's strong evidence of +1 shift starting at or before 0x46.
        // Similarly check INFO (0x46→0x47) and QUST (0x47→0x48).
        var anchorChecks = new (byte ExpectedCode, byte DisplacedCode, string Name)[]
        {
            (0x45, 0x46, "DIAL"), // DIAL normally at 0x45; if shifted, at 0x46
            (0x46, 0x47, "INFO"), // INFO normally at 0x46; if shifted, at 0x47
            (0x41, 0x42, "WRLD") // WRLD normally at 0x41; if shifted, at 0x42
        };

        var displacedAnchors = 0;
        byte lowestDisplaced = 0xFF;
        foreach (var (expected, displaced, _) in anchorChecks)
        {
            counts.TryGetValue(expected, out var expectedCount);
            counts.TryGetValue(displaced, out var displacedCount);

            // The expected code should be empty (or near-empty) and the displaced code
            // should have significant entries (≥10)
            if (expectedCount == 0 && displacedCount >= 10)
            {
                displacedAnchors++;
                if (displaced < lowestDisplaced)
                {
                    lowestDisplaced = displaced;
                }
            }
        }

        if (displacedAnchors < 2)
        {
            return null; // Need at least 2 displaced anchors for confidence
        }

        // Determine shift start: walk downward from lowestDisplaced to find where shift begins.
        // A code C is shifted if: C has entries, and (C-1) is a valid final type with 0 entries at code (C-1).
        var shiftStart = lowestDisplaced;
        for (var code = (byte)(lowestDisplaced - 1); code >= 0x03; code--)
        {
            counts.TryGetValue(code, out var codeCount);
            counts.TryGetValue((byte)(code - 1), out var prevCount);

            // If this code has no entries, stop — the shift doesn't extend here
            if (codeCount == 0)
            {
                break;
            }

            // If the code below also has entries and is a valid type that matches,
            // we've found the boundary where the shift stops
            var codeMinusOneType = GetRecordTypeCode((byte)(code - 1));
            if (codeMinusOneType == null)
            {
                break;
            }

            // Check: does code C look displaced? Code (C-1) should be empty if shifted.
            if (prevCount == 0)
            {
                shiftStart = code;
            }
            else
            {
                break; // Code (C-1) has entries — shift doesn't extend this far down
            }
        }

        // Build remap: delta = -1 (runtime code is +1 from final)
        var remap = new Dictionary<byte, byte>();
        for (var code = shiftStart; code <= 0x78; code++)
        {
            var target = (byte)(code - 1);
            if (GetRecordTypeCode(target) != null)
            {
                remap[code] = target;
            }
        }

        if (remap.Count > 0)
        {
            Logger.Instance.Info(
                $"[FormType Drift] Distribution-based shift detected: delta=-1 " +
                $"starting at DMP code 0x{shiftStart:X2} " +
                $"({displacedAnchors} anchor types displaced, " +
                $"{confirmedMatchesBelow} unaffected matches below drift point). " +
                $"Range: 0x{remap.Keys.Min():X2}–0x{remap.Keys.Max():X2}");
        }

        return remap.Count > 0 ? remap : null;
    }

    /// <summary>
    ///     Count how many exact matches (runtime FormType == final expected FormType) exist
    ///     for runtime entries with FormType >= threshold. If this is non-zero, it contradicts
    ///     a systematic shift starting at that threshold.
    /// </summary>
    private static int CountMatchesAtOrAbove(
        IReadOnlyList<RuntimeEditorIdEntry> runtimeEntries,
        Dictionary<uint, string> esmFormIdToSignature,
        byte threshold)
    {
        var count = 0;
        foreach (var entry in runtimeEntries)
        {
            if (entry.FormType < threshold)
            {
                continue;
            }

            if (!esmFormIdToSignature.TryGetValue(entry.FormId, out var esmSig))
            {
                continue;
            }

            if (!SignatureToFormType.TryGetValue(esmSig, out var expectedFormType))
            {
                continue;
            }

            if (entry.FormType == expectedFormType)
            {
                count++;
            }
        }

        return count;
    }
}
