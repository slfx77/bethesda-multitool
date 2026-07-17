using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.AI;

/// <summary>
///     Encodes a <see cref="PackageRecord" /> (PACK) as PC-format subrecord bytes.
///     Emits the full record from scratch: EDID + PKDT + PLDT? + PLD2? + PSDT? + PTDT? +
///     PTD2? + CTDA* + IDLF/IDLC/IDLT/IDLA? + PKDD? + PKPT? + PKW3? + CNAM
///     (combat style FormID, captured from runtime
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
///     PKDD (24 bytes) per PDB PACK_DIALOGUE_DATA.
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
        ["Time"] = m => m.Time,
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
    ///     EDID, PKDT, PLDT?, PSDT?, PTDT?, PLD2?, PTD2?, CTDA*, IDLE?, PKDD?,
    ///     PKW3?, PKPT?, CNAM?, POBA?, POEA?, POCA?.
    /// </summary>
    /// <param name="validFormIds">
    ///     Optional set of FormIDs known to exist at load time (master ∪ all emitted-new).
    ///     When supplied, PLDT/PLD2/PTDT/PTD2 entries whose Type indicates a FormID-bearing
    ///     Union are validated. Dangling refs trigger the engine errors "Unable to find Package
    ///     Location Reference" and "AI: is assigned a reference location that doesnt exist for
    ///     a package". Remap if possible; otherwise suppress the whole package. Rewriting the
    ///     union arm to NearCurrentLocation / ObjectType=None silently changes AI behavior.
    /// </param>
    /// <param name="remapTable">Runtime→emitted alias table for FormID remapping.</param>
    internal static EncodedRecord EncodeNew(
        PackageRecord pack,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();
        var emittedScriptPaths = new List<string>();
        var droppedCtdas = 0;
        var remappedCtdaParams = 0;

        var inlineScriptIssue = InlineScriptReferenceValidator.FindFirstIssue(
            pack, validFormIds, remapTable);
        if (inlineScriptIssue is not null)
        {
            warnings.Add(
                $"New PACK 0x{pack.FormId:X8} suppressed: {inlineScriptIssue.Message} " +
                "Inline SCDA/SLSD/SCRO/SCRV is atomic; no slot was dropped or zero-filled.");
            return new EncodedRecord { Subrecords = [], Warnings = warnings };
        }

        warnings.AddRange(
            InlineScriptReferenceValidator.FindSourceContractIssues(pack, validFormIds, remapTable)
                .Select(static issue => issue.Message));

        var referenceSanitation = PackageReferenceIntegrity.Sanitize(pack, validFormIds, remapTable);
        if (!referenceSanitation.IsValid)
        {
            var issue = referenceSanitation.Issue!;
            warnings.Add(
                $"New PACK 0x{pack.FormId:X8} suppressed: {issue.FieldPath} Type " +
                $"{issue.UnionType} FormID 0x{issue.FormId:X8} is invalid ({issue.Reason}). " +
                "The package is removed instead of changing its target/location semantics.");
            return new EncodedRecord { Subrecords = [], Warnings = warnings };
        }

        pack = referenceSanitation.Package;
        foreach (var remap in referenceSanitation.Remaps)
        {
            warnings.Add(
                $"New PACK 0x{pack.FormId:X8} {remap.FieldPath} remapped " +
                $"0x{remap.SourceFormId:X8} → 0x{remap.EmittedFormId:X8} via source→emitted alias table.");
        }

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

        if (pack.Location is not null)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PLDT", "", 12, pack.Location, PldtExtractors));
        }

        if (pack.Schedule is not null)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PSDT", "", 8, pack.Schedule, PsdtExtractors));
        }

        if (pack.Target is not null)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PTDT", "", 16, pack.Target, PtdtExtractors));
        }

        if (pack.Location2 is not null)
        {
            // PLD2 has its own schema registration but identical layout to PLDT.
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PLD2", "", 12, pack.Location2, PldtExtractors));
        }

        if (pack.Target2 is not null)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("PTD2", "", 16, pack.Target2, PtdtExtractors));
        }

        // PACK CTDAs are the activation gate for AI packages. Dropping them turns
        // situational packages into always-on packages when an actor's PKID list points at them.
        EmitConditions(subs, pack.Conditions, validFormIds, remapTable,
            ref droppedCtdas, ref remappedCtdaParams);

        EmitIdleCollection(subs, pack, validFormIds, remapTable, warnings);

        if (pack.DialogueData is not null)
        {
            var dialogueData = SanitizePackageDialogueData(
                pack.DialogueData, pack.FormId, validFormIds, remapTable, warnings);
            subs.Add(new EncodedSubrecord("PKDD", BuildPkddSubrecord(dialogueData)));
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

        if (pack.HasEatMarker)
        {
            subs.Add(new EncodedSubrecord("PKED", []));
        }

        if (pack.HasUseItemMarker)
        {
            subs.Add(new EncodedSubrecord("PUID", []));
        }

        if (pack.HasAmbushMarker)
        {
            subs.Add(new EncodedSubrecord("PKAM", []));
        }

        var ownerLabel = pack.EditorId ?? $"PACK 0x{pack.FormId:X8}";
        EmitEventAction(subs, pack.OnBegin, "POBA", pack.FormId, ownerLabel,
            validFormIds, remapTable, warnings, emittedScriptPaths);
        EmitEventAction(subs, pack.OnEnd, "POEA", pack.FormId, ownerLabel,
            validFormIds, remapTable, warnings, emittedScriptPaths);
        EmitEventAction(subs, pack.OnChange, "POCA", pack.FormId, ownerLabel,
            validFormIds, remapTable, warnings, emittedScriptPaths);

        if (droppedCtdas > 0 || remappedCtdaParams > 0)
        {
            warnings.Add(
                $"New PACK 0x{pack.FormId:X8} CTDA sanitizer: dropped {droppedCtdas} condition(s) " +
                $"with dangling FormID params, remapped {remappedCtdaParams} param FormID(s) via " +
                "the runtime→emitted alias table.");
        }

        return new EncodedRecord
        {
            Subrecords = subs,
            Warnings = warnings,
            EmittedScriptPaths = emittedScriptPaths
        };
    }

    private static void EmitIdleCollection(
        List<EncodedSubrecord> subs,
        PackageRecord pack,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        List<string> warnings)
    {
        if (pack.IdleCollection is null)
        {
            return;
        }

        var idles = new List<uint>(pack.IdleCollection.IdleAnimationFormIds.Count);
        var dropped = 0;
        var remapped = 0;
        foreach (var idleFormId in pack.IdleCollection.IdleAnimationFormIds)
        {
            var resolved = ResolveOptionalFormId(idleFormId, validFormIds, remapTable);
            if (!resolved.HasValue)
            {
                dropped++;
                continue;
            }

            if (resolved.Value != idleFormId)
            {
                remapped++;
            }

            idles.Add(resolved.Value);
        }

        if (dropped > 0 || remapped > 0)
        {
            warnings.Add(
                $"New PACK 0x{pack.FormId:X8} IDLA sanitizer: dropped {dropped} dangling idle " +
                $"animation FormID(s), remapped {remapped} via the runtime→emitted alias table.");
        }

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("IDLF", pack.IdleCollection.Flags));
        var count = idles.Count > 0
            ? (byte)Math.Min(byte.MaxValue, idles.Count)
            : pack.IdleCollection.Count;
        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("IDLC", count));
        subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("IDLT", pack.IdleCollection.TimerCheckForIdle));

        if (idles.Count > 0)
        {
            var idla = new byte[idles.Count * 4];
            for (var i = 0; i < idles.Count; i++)
            {
                SubrecordEncoder.WriteFormId(idla, i * 4, idles[i]);
            }

            subs.Add(new EncodedSubrecord("IDLA", idla));
        }
    }

    private static void EmitConditions(
        List<EncodedSubrecord> subs,
        List<DialogueCondition> conditions,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        ref int droppedCtdas,
        ref int remappedCtdaParams)
    {
        IReadOnlyList<DialogueCondition> emitConds;
        if (validFormIds is null)
        {
            emitConds = conditions;
        }
        else
        {
            emitConds = ConditionSanitizer.Filter(
                conditions,
                validFormIds as HashSet<uint> ?? new HashSet<uint>(validFormIds),
                remapTable,
                ref remappedCtdaParams,
                ref droppedCtdas);
        }

        foreach (var condition in emitConds)
        {
            subs.Add(new EncodedSubrecord("CTDA", InfoEncoder.BuildCtdaSubrecord(condition)));
            if (!string.IsNullOrEmpty(condition.Parameter1String))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("CIS1", condition.Parameter1String));
            }

            if (!string.IsNullOrEmpty(condition.Parameter2String))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("CIS2", condition.Parameter2String));
            }
        }
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

    private static PackageDialogueData SanitizePackageDialogueData(
        PackageDialogueData data,
        uint packFormId,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        List<string> warnings)
    {
        if (data.TopicFormId == 0)
        {
            return data;
        }

        var resolved = ResolveOptionalFormId(data.TopicFormId, validFormIds, remapTable);
        if (resolved.HasValue)
        {
            if (resolved.Value != data.TopicFormId)
            {
                warnings.Add(
                    $"New PACK 0x{packFormId:X8} PKDD TopicID remapped 0x{data.TopicFormId:X8} → " +
                    $"0x{resolved.Value:X8} via runtime→emitted alias table.");
            }

            return data with { TopicFormId = resolved.Value };
        }

        warnings.Add(
            $"New PACK 0x{packFormId:X8} PKDD TopicID 0x{data.TopicFormId:X8} dangles — " +
            "rewriting to null so the package does not reference a missing topic.");
        return data with { TopicFormId = 0 };
    }

    private static void EmitEventAction(
        List<EncodedSubrecord> subs,
        PackageEventAction? action,
        string marker,
        uint packFormId,
        string ownerLabel,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        List<string> warnings,
        List<string> emittedScriptPaths)
    {
        if (action is null)
        {
            return;
        }

        subs.Add(new EncodedSubrecord(marker, []));
        subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM",
            ResolvePackageEventFormId(action.IdleFormId, packFormId, marker, "INAM",
                validFormIds, remapTable, warnings)));

        if (action.Scripts.Count == 0)
        {
            InfoEncoder.EmitResultScriptBlock(subs, null, validFormIds, remapTable);
        }
        else
        {
            for (var i = 0; i < action.Scripts.Count; i++)
            {
                var script = action.Scripts[i];
                if (InfoEncoder.EmitResultScriptBlock(subs, script, validFormIds, remapTable))
                {
                    emittedScriptPaths.Add($"{ownerLabel}/{action.Kind}/script[{i}]");
                }

                if (i < action.Scripts.Count - 1)
                {
                    subs.Add(new EncodedSubrecord("NEXT", []));
                }
            }
        }

        subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TNAM",
            ResolvePackageEventFormId(action.TopicFormId, packFormId, marker, "TNAM",
                validFormIds, remapTable, warnings)));
    }

    private static uint ResolvePackageEventFormId(
        uint formId,
        uint packFormId,
        string marker,
        string subrecord,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        List<string> warnings)
    {
        if (formId == 0)
        {
            return 0;
        }

        var resolved = ResolveOptionalFormId(formId, validFormIds, remapTable);
        if (resolved.HasValue)
        {
            if (resolved.Value != formId)
            {
                warnings.Add(
                    $"New PACK 0x{packFormId:X8} {marker}/{subrecord} remapped 0x{formId:X8} → " +
                    $"0x{resolved.Value:X8} via runtime→emitted alias table.");
            }

            return resolved.Value;
        }

        warnings.Add(
            $"New PACK 0x{packFormId:X8} {marker}/{subrecord} 0x{formId:X8} dangles — " +
            "rewriting to null inside the package event action.");
        return 0;
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

    private static byte[] BuildPkddSubrecord(PackageDialogueData pkdd)
    {
        var data = new byte[24];
        SubrecordEncoder.WriteFloat(data, 0, pkdd.Fov);
        SubrecordEncoder.WriteFormId(data, 4, pkdd.TopicFormId);
        data[8] = pkdd.NoHeadtracking ? (byte)1 : (byte)0;
        data[9] = pkdd.DoNotControlTarget ? (byte)1 : (byte)0;
        data[10] = pkdd.SpeakerMoveTalk ? (byte)1 : (byte)0;
        SubrecordEncoder.WriteFloat(data, 12, pkdd.DistanceStartTalking);
        data[16] = pkdd.SayTo ? (byte)1 : (byte)0;
        SubrecordEncoder.WriteUInt32(data, 20, pkdd.TriggerType);
        return data;
    }
}
