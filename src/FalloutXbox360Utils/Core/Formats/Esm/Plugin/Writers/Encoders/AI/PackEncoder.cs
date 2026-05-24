using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.AI;

/// <summary>
///     Encodes a <see cref="PackageRecord" /> (PACK) as PC-format subrecord bytes.
///     Emits the full record from scratch: EDID + PKDT + PLDT? + PLD2? + PSDT? + PTDT? +
///     PTD2? + PKPT? + PKW3? + CNAM (combat style FormID, captured from runtime
///     <c>TESPackage.pCombatStyle</c> @+88 or master ESM CNAM subrecord).
///     Override path is a no-op.
///     PKDT (12 bytes) per PDB PACKAGE_DATA:
///     uint32 iPackFlags(0) + uint8 cPackType(4) + uint8 unused(5) +
///     uint16 iFOBehaviorFlags(6) + uint16 iPackageSpecificFlags(8) + 2 unknown bytes(10,11).
///     PSDT (8 bytes): int8 Month(0) + int8 DayOfWeek(1) + int8 Date(2) + int8 Time(3) +
///     int32 Duration(4).
///     PLDT/PLD2 (12 bytes): byte Type + pad(3) + uint32 Union + int32 Radius.
///     PTDT/PTD2 (16 bytes): byte Type + pad(3) + uint32 Union + int32 CountDistance + float Unknown.
///     PKW3 (24 bytes): 6 bool bytes(0..5) + uint16 BurstCount(6) + uint16 VolleyShotsMin(8) +
///     uint16 VolleyShotsMax(10) + float VolleyWaitMin(12) + float VolleyWaitMax(16) +
///     uint32 Weapon(20).
///     PKPT (2 bytes): bool Repeatable(0) + bool StartingLocationLinkedRef(1).
/// </summary>
public sealed class PackEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<PackageData, object?>> PkdtExtractors = new(StringComparer.Ordinal)
    {
        ["iPackFlags"] = m => m.GeneralFlags,
        ["cPackType"] = m => m.Type,
        ["iFOBehaviorFlags"] = m => m.FalloutBehaviorFlags,
        ["iPackageSpecificFlags"] = m => m.TypeSpecificFlags,
        // Unused, Unknown1, Unknown2 → zero-fill.
    };

    private static readonly Dictionary<string, Func<PackageSchedule, object?>> PsdtExtractors = new(StringComparer.Ordinal)
    {
        ["Month"] = m => (byte)m.Month,
        ["DayOfWeek"] = m => (byte)m.DayOfWeek,
        ["Date"] = m => (byte)m.Date,
        ["Time"] = m => (sbyte)m.Time,
        ["Duration"] = m => m.Duration,
    };

    private static readonly Dictionary<string, Func<PackageLocation, object?>> PldtExtractors = new(StringComparer.Ordinal)
    {
        ["Type"] = m => m.Type,
        ["Union"] = m => m.Union,
        ["Radius"] = m => m.Radius,
    };

    private static readonly Dictionary<string, Func<PackageTarget, object?>> PtdtExtractors = new(StringComparer.Ordinal)
    {
        ["Type"] = m => m.Type,
        ["Union"] = m => m.FormIdOrType,
        ["CountDistance"] = m => m.CountDistance,
        ["Unknown"] = m => m.AcquireRadius,
    };

    public string RecordType => "PACK";
    public Type ModelType => typeof(PackageRecord);

    /// <summary>
    ///     Encode a new PACK record from scratch in fopdoc canonical order:
    ///     EDID, PKDT, PLDT?, PSDT?, PTDT?, PLD2?, PTD2?, PKW3?, PKPT?.
    /// </summary>
    /// <param name="validFormIds">
    ///     Optional set of FormIDs known to exist at load time (master ∪ all emitted-new).
    ///     When supplied, PLDT/PLD2/PTDT/PTD2 entries whose Type indicates a FormID-bearing
    ///     Union are validated. Dangling refs trigger the engine errors "Unable to find Package
    ///     Location Reference" and "AI: is assigned a reference location that doesnt exist for
    ///     a package" — the NPC then falls through to default idle behavior. Remap if possible,
    ///     otherwise fall back to Type 2 (NearCurrentLocation / Object Type) which is the
    ///     safest no-FormID-needed package shape (the package still runs but operates on the
    ///     actor's current state).
    /// </param>
    /// <param name="remapTable">Runtime→emitted alias table for FormID remapping.</param>
    internal static EncodedRecord EncodeNew(
        PackageRecord pack,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(pack.EditorId))
        {
            warnings.Add($"New PACK 0x{pack.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", pack.EditorId ?? string.Empty));

        if (pack.Data is not null)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PKDT", "", 12, pack.Data, PkdtExtractors));
        }
        else
        {
            warnings.Add($"New PACK 0x{pack.FormId:X8} has no PKDT data — emitting zero-filled PKDT.");
            subs.Add(new EncodedSubrecord("PKDT", new byte[12]));
        }

        var sanitizedLoc = pack.Location is not null
            ? SanitizePackageLocation(pack.Location, pack.FormId, "PLDT", validFormIds, remapTable, warnings)
            : null;
        if (sanitizedLoc is not null)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PLDT", "", 12, sanitizedLoc, PldtExtractors));
        }

        if (pack.Schedule is not null)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PSDT", "", 8, pack.Schedule, PsdtExtractors));
        }

        var sanitizedTgt = pack.Target is not null
            ? SanitizePackageTarget(pack.Target, pack.FormId, "PTDT", validFormIds, remapTable, warnings)
            : null;
        if (sanitizedTgt is not null)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PTDT", "", 16, sanitizedTgt, PtdtExtractors));
        }

        var sanitizedLoc2 = pack.Location2 is not null
            ? SanitizePackageLocation(pack.Location2, pack.FormId, "PLD2", validFormIds, remapTable, warnings)
            : null;
        if (sanitizedLoc2 is not null)
        {
            // PLD2 has its own schema registration but identical layout to PLDT.
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PLD2", "", 12, sanitizedLoc2, PldtExtractors));
        }

        var sanitizedTgt2 = pack.Target2 is not null
            ? SanitizePackageTarget(pack.Target2, pack.FormId, "PTD2", validFormIds, remapTable, warnings)
            : null;
        if (sanitizedTgt2 is not null)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PTD2", "", 16, sanitizedTgt2, PtdtExtractors));
        }

        if (pack.UseWeaponData is not null)
        {
            subs.Add(new EncodedSubrecord("PKW3", BuildPkw3Subrecord(pack.UseWeaponData)));
        }

        // PKPT is emitted only for patrol packages (TypeName "Patrol", cPackType 13). For
        // other types the model's IsRepeatable/IsStartingLocationLinkedRef are always false,
        // so guard by checking that either flag is set.
        if (pack.IsRepeatable || pack.IsStartingLocationLinkedRef)
        {
            var pkpt = new byte[2];
            pkpt[0] = pack.IsRepeatable ? (byte)1 : (byte)0;
            pkpt[1] = pack.IsStartingLocationLinkedRef ? (byte)1 : (byte)0;
            subs.Add(new EncodedSubrecord("PKPT", pkpt));
        }

        // CNAM — TESCombatStyle FormID override. Validate against (master ∪ emitted-new) so a
        // runtime-captured CSTY that didn't survive into the output ESP doesn't leave a dangling
        // ref. If dangling, skip with a warning (mirrors the REFR optional-FormID pattern); the
        // engine then falls through to the actor's base combat style which is the safe default.
        if (pack.CombatStyleFormId.HasValue && pack.CombatStyleFormId.Value != 0)
        {
            var resolved = ResolveOptionalFormId(pack.CombatStyleFormId.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                var cnam = new byte[4];
                SubrecordEncoder.WriteFormId(cnam, 0, resolved.Value);
                subs.Add(new EncodedSubrecord("CNAM", cnam));
            }
            else
            {
                warnings.Add($"New PACK 0x{pack.FormId:X8} CNAM combat style " +
                    $"0x{pack.CombatStyleFormId.Value:X8} dangles — subrecord skipped " +
                    "(engine inherits the actor's base combat style).");
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Try remap-first-then-validity for an optional FormID. Mirrors the same policy
    ///     used by RefrEncoder.ResolveOptionalFormId — returns null when the FormID is
    ///     dangling with no remap, otherwise returns the resolved (possibly remapped) FormID.
    /// </summary>
    private static uint? ResolveOptionalFormId(
        uint formId,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        if (formId == 0 || validFormIds is null)
        {
            return formId;
        }

        if (remapTable is not null
            && remapTable.TryGetValue(formId, out var remapped)
            && remapped != formId
            && validFormIds.Contains(remapped))
        {
            return remapped;
        }

        if (validFormIds.Contains(formId))
        {
            return formId;
        }

        return null;
    }

    /// <summary>
    ///     Map of PLDT Type byte → whether <c>Union</c> is a FormID. FNV recognises additional
    ///     types (NearCurrent=2, ObjectType=5) where Union is an enum/no-op and needs no
    ///     validation. Conservative: only sanitize types we know carry FormIDs.
    /// </summary>
    private static bool PlocTypeIsFormId(byte type) =>
        type is 0   // NearReference (REFR FormID)
             or 1   // InCell (CELL FormID)
             or 3   // NearEditorLocation (FormID)
             or 4   // ObjectID (base object FormID)
             or 6   // LinkedReference (keyword FormID)
             or 12; // NearLinkedRef (keyword FormID)

    /// <summary>
    ///     PLDT.Type = 2 (NearCurrentLocation) — "the actor's current location". Doesn't use
    ///     Union, no FormID needed. Safest fallback when a Type-with-FormID Union dangles:
    ///     the package still runs and the engine doesn't emit the "Unable to find Package
    ///     Location Reference" / "AI reference location doesnt exist" pair.
    /// </summary>
    private const byte PlocTypeNearCurrentLocation = 2;

    /// <summary>
    ///     Validate a PLDT/PLD2 model against the master ∪ emitted-new FormID set. Remap when
    ///     the dangling Union has an alias; otherwise rewrite Type to NearCurrentLocation (2)
    ///     and Union to 0 so the package is still loadable but no longer fails AI setup.
    /// </summary>
    private static PackageLocation SanitizePackageLocation(
        PackageLocation loc,
        uint packFormId,
        string subrecordTag,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        List<string> warnings)
    {
        if (validFormIds is null || loc.Union == 0 || !PlocTypeIsFormId(loc.Type))
        {
            return loc;
        }

        if (validFormIds.Contains(loc.Union))
        {
            return loc;
        }

        if (remapTable is not null
            && remapTable.TryGetValue(loc.Union, out var remapped)
            && remapped != loc.Union
            && validFormIds.Contains(remapped))
        {
            warnings.Add(
                $"New PACK 0x{packFormId:X8} {subrecordTag} remapped Union 0x{loc.Union:X8} → " +
                $"0x{remapped:X8} via runtime→emitted alias table.");
            return loc with { Union = remapped };
        }

        warnings.Add(
            $"New PACK 0x{packFormId:X8} {subrecordTag} fallback: Union 0x{loc.Union:X8} doesn't " +
            "resolve and no remap available — rewriting Type from " +
            $"{loc.Type} to {PlocTypeNearCurrentLocation} (NearCurrentLocation) so the package " +
            "loads without 'Unable to find Package Location Reference' / 'AI: reference " +
            "location doesnt exist' (which causes the NPC to fall through to default idle).");
        return loc with { Type = PlocTypeNearCurrentLocation, Union = 0u };
    }

    /// <summary>
    ///     PTDT/PTD2 Type byte → whether <c>FormIdOrType</c> is a FormID:
    ///     0 SpecificReference, 1 ObjectID, 3 LinkedReference.
    /// </summary>
    private static bool PtdtTypeIsFormId(byte type) => type is 0 or 1 or 3;

    /// <summary>
    ///     PTDT.Type = 2 (Object Type). FormIdOrType is then a Form-type enum (0=None) — no
    ///     FormID validation needed. Used as the fallback when a Type-with-FormID target dangles.
    /// </summary>
    private const byte PtdtTypeObjectType = 2;

    /// <summary>
    ///     Validate a PTDT/PTD2 model. Same remap-or-fallback policy as PLDT.
    /// </summary>
    private static PackageTarget SanitizePackageTarget(
        PackageTarget target,
        uint packFormId,
        string subrecordTag,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        List<string> warnings)
    {
        if (validFormIds is null || target.FormIdOrType == 0 || !PtdtTypeIsFormId(target.Type))
        {
            return target;
        }

        if (validFormIds.Contains(target.FormIdOrType))
        {
            return target;
        }

        if (remapTable is not null
            && remapTable.TryGetValue(target.FormIdOrType, out var remapped)
            && remapped != target.FormIdOrType
            && validFormIds.Contains(remapped))
        {
            warnings.Add(
                $"New PACK 0x{packFormId:X8} {subrecordTag} remapped FormIdOrType " +
                $"0x{target.FormIdOrType:X8} → 0x{remapped:X8} via alias table.");
            return target with { FormIdOrType = remapped };
        }

        warnings.Add(
            $"New PACK 0x{packFormId:X8} {subrecordTag} fallback: FormIdOrType " +
            $"0x{target.FormIdOrType:X8} doesn't resolve — rewriting Type from {target.Type} " +
            $"to {PtdtTypeObjectType} (ObjectType=None) so the package's target evaluates to a " +
            "no-op instead of triggering 'Unable to find Package Target Reference'.");
        return target with { Type = PtdtTypeObjectType, FormIdOrType = 0u };
    }

    private static byte[] BuildPkw3Subrecord(PackageUseWeaponData pkw3)
    {
        var data = new byte[24];
        data[0] = pkw3.AlwaysHit ? (byte)1 : (byte)0;
        data[1] = pkw3.DoNoDamage ? (byte)1 : (byte)0;
        data[2] = pkw3.Crouch ? (byte)1 : (byte)0;
        data[3] = pkw3.HoldFire ? (byte)1 : (byte)0;
        data[4] = pkw3.VolleyFire ? (byte)1 : (byte)0;
        data[5] = pkw3.RepeatFire ? (byte)1 : (byte)0;
        SubrecordEncoder.WriteUInt16(data, 6, pkw3.BurstCount);
        SubrecordEncoder.WriteUInt16(data, 8, pkw3.VolleyShotsMin);
        SubrecordEncoder.WriteUInt16(data, 10, pkw3.VolleyShotsMax);
        SubrecordEncoder.WriteFloat(data, 12, pkw3.VolleyWaitMin);
        SubrecordEncoder.WriteFloat(data, 16, pkw3.VolleyWaitMax);
        SubrecordEncoder.WriteUInt32(data, 20, pkw3.WeaponFormId ?? 0);
        return data;
    }
}
