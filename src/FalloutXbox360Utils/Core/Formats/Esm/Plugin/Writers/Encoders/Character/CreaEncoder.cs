using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

/// <summary>
///     Encodes a <see cref="CreatureRecord" /> (CREA) as PC-format subrecord bytes.
///     Non-human actors. Closely parallels NPC_ but with the creature-specific DATA layout
///     and physical/sound trim (OBND/RNAM/TNAM/BNAM/WNAM/NAM4/NAM5) that NPCs lack.
///     Emit order roughly follows the canonical vanilla shape:
///         EDID, OBND, FULL?, MODL?, SPLO*, EAMT?, NIFZ?, NIFT?, ACBS, SNAM*, INAM?, SCRI?,
///         VTCK?, TPLT?, CNTO*[+COED?], AIDT?, PKID*, KFFZ?, KFNM?, DATA, RNAM?, ZNAM?,
///         PNAM?, TNAM?, BNAM?, WNAM?, NAM4?, NAM5?, CSCR?, CSDT*/CSDI*/CSDC*, CNAM?, LNAM?,
///         EITM?, DEST?/DSTD?/DMDL?/DMDT?/DSTF?.
///     DATA layout (17B): uint8 CreatureType + uint8 CombatSkill + uint8 MagicSkill +
///     uint8 StealthSkill + int32 Health + int16 AttackDamage + 7 bytes unused.
///     FormID-bearing subrecords (SPLO, INAM, SCRI, VTCK, TPLT, ZNAM, CSCR, LNAM, EITM,
///     CNAM, PNAM, CNTO item) are resolved through the source→allocated alias table so they
///     reference IDs the engine will actually load.
/// </summary>
public sealed class CreaEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<CreatureRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["CreatureType"] = m => m.CreatureType,
        ["CombatSkill"] = m => m.CombatSkill,
        ["MagicSkill"] = m => m.MagicSkill,
        ["StealthSkill"] = m => m.StealthSkill,
        // Health field intentionally omitted — engine computes from Endurance + level.
        ["AttackDamage"] = m => m.AttackDamage,
        // Remaining(7) bytes left unset → zero-fill.
    };

    // ACBS bytes-builder + flag-policy lives in ActorBaseAcbsBuilder, shared with NpcEncoder.
    // Both record types use the identical extractor dict + flag-policy fixups.

    private static readonly Dictionary<string, Func<FactionMembership, object?>> SnamExtractors = new(StringComparer.Ordinal)
    {
        ["Faction"] = m => m.FactionFormId,
        ["Rank"] = m => (byte)m.Rank,
    };

    public string RecordType => "CREA";
    public Type ModelType => typeof(CreatureRecord);

    internal static EncodedRecord EncodeNew(
        CreatureRecord crea,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(crea.EditorId))
        {
            warnings.Add($"New CREA 0x{crea.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", crea.EditorId ?? string.Empty));

        if (crea.Bounds is { } bounds)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(bounds));
        }

        if (!string.IsNullOrEmpty(crea.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", crea.FullName));
        }

        if (!string.IsNullOrEmpty(crea.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", crea.ModelPath));
        }

        // SPLO — spell/ability list. One subrecord per spell. Skip dangling entries.
        foreach (var spellId in crea.Spells)
        {
            var resolved = FormIdReferenceResolver.Resolve(spellId, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SPLO", resolved.Value));
            }
        }

        if (crea.EquippedAttackAnimation.HasValue)
        {
            var eamt = new byte[2];
            SubrecordEncoder.WriteUInt16(eamt, 0, crea.EquippedAttackAnimation.Value);
            subs.Add(new EncodedSubrecord("EAMT", eamt));
        }

        // NIFZ (model file list) + NIFT (texture file hash blob). Captured opaque by the
        // parser; emit verbatim.
        if (crea.ModelFilesRaw is { Length: > 0 } nifz)
        {
            subs.Add(new EncodedSubrecord("NIFZ", nifz));
        }

        if (crea.TextureFilesRaw is { Length: > 0 } nift)
        {
            subs.Add(new EncodedSubrecord("NIFT", nift));
        }

        // ACBS — actor base stats. Routed through ActorBaseAcbsBuilder so creatures get the
        // same three flag-policy fixups NPCs do: force AutoCalcStats (0x10), set UseTemplate
        // (0x40) when TemplateFlags is nonzero, and clamp SpeedMultiplier to 100 when zero.
        // Without those fixups, templated creatures (Speedy / Sleepy / etc. captured from a
        // prototype build) would emit ACBS with cleared AutoCalc and missing UseTemplate —
        // the engine then appends a per-spawn numeric suffix to the display name (mirror of
        // the Ulysses-suffix bug previously fixed on NPC placements).
        if (crea.Stats is { } stats)
        {
            subs.Add(new EncodedSubrecord("ACBS",
                ActorBaseAcbsBuilder.Build("CREA", stats, forceAutoCalc: true)));
        }
        else
        {
            warnings.Add(
                $"New CREA 0x{crea.FormId:X8} has no ACBS — emitting default actor base stats "
                + "(Level=1, SpeedMult=100).");
            subs.Add(new EncodedSubrecord("ACBS", ActorBaseAcbsBuilder.BuildDefault("CREA")));
        }

        // SNAM faction memberships — 8 bytes each (FormID + uint8 rank + 3 padding).
        foreach (var faction in crea.Factions)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("SNAM", "CREA", 8, faction, SnamExtractors));
        }

        if (crea.DeathItem.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(crea.DeathItem.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", resolved.Value));
            }
        }

        if (crea.Script.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(crea.Script.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", resolved.Value));
            }
        }

        if (crea.VoiceType.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(crea.VoiceType.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("VTCK", resolved.Value));
            }
        }

        if (crea.Template.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(crea.Template.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("TPLT", resolved.Value));
            }
        }

        // CNTO inventory — 8 bytes each: FormID + int32 count. Optional COED ownership pair
        // (12 bytes) follows. Skip entries whose item FormID dangles (engine logs "Unable to
        // find container object on owner object" and drops the line anyway).
        var droppedItems = 0;
        foreach (var item in crea.Inventory)
        {
            var resolvedItem = FormIdReferenceResolver.Resolve(item.ItemFormId, validFormIds, remapTable);
            if (!resolvedItem.HasValue)
            {
                droppedItems++;
                continue;
            }

            var cnto = new byte[8];
            SubrecordEncoder.WriteFormId(cnto, 0, resolvedItem.Value);
            SubrecordEncoder.WriteInt32(cnto, 4, item.Count);
            subs.Add(new EncodedSubrecord("CNTO", cnto));
            if (ContEncoder.HasOwnership(item))
            {
                subs.Add(new EncodedSubrecord("COED", ContEncoder.BuildCoedSubrecord(item)));
            }
        }
        if (droppedItems > 0)
        {
            warnings.Add(
                $"New CREA 0x{crea.FormId:X8} dropped {droppedItems} CNTO inventory entry/entries " +
                "with dangling item FormID.");
        }

        if (crea.AiData is not null)
        {
            var aidt = new byte[20];
            aidt[0] = crea.AiData.Aggression;
            aidt[1] = crea.AiData.Confidence;
            aidt[2] = crea.AiData.EnergyLevel;
            aidt[3] = crea.AiData.Responsibility;
            aidt[4] = crea.AiData.Mood;
            SubrecordEncoder.WriteUInt32(aidt, 8, crea.AiData.Flags);
            aidt[14] = crea.AiData.Assistance;
            subs.Add(new EncodedSubrecord("AIDT", aidt));
        }

        foreach (var pkgId in crea.Packages)
        {
            var resolved = FormIdReferenceResolver.Resolve(pkgId, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PKID", resolved.Value));
            }
        }

        // KFFZ + KFNM. Captured opaque by the parser; emit verbatim.
        if (crea.AnimationFilesRaw is { Length: > 0 } kffz)
        {
            subs.Add(new EncodedSubrecord("KFFZ", kffz));
        }

        if (crea.AnimationNamesRaw is { Length: > 0 } kfnm)
        {
            subs.Add(new EncodedSubrecord("KFNM", kfnm));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "CREA", 17, crea, DataExtractors));

        if (crea.SoundType.HasValue)
        {
            subs.Add(new EncodedSubrecord("RNAM", [crea.SoundType.Value]));
        }

        if (crea.CombatStyleFormId.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(crea.CombatStyleFormId.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ZNAM", resolved.Value));
            }
        }

        if (crea.BodyData.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(crea.BodyData.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("PNAM", resolved.Value));
            }
        }

        if (crea.TurningSpeed.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("TNAM", crea.TurningSpeed.Value));
        }

        if (crea.BaseScale.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("BNAM", crea.BaseScale.Value));
        }

        if (crea.FootWeight.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("WNAM", crea.FootWeight.Value));
        }

        if (crea.ImpactMaterialType.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("NAM4", crea.ImpactMaterialType.Value));
        }

        if (crea.SoundLevel.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("NAM5", crea.SoundLevel.Value));
        }

        if (crea.InheritsSoundsFrom.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(crea.InheritsSoundsFrom.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("CSCR", resolved.Value));
            }
        }

        // CSDT (sound type) / CSDI (sound FormID) / CSDC (chance byte) — interleaved groups
        // captured as opaque bytes. CSDI carries FormID references but we currently round-trip
        // verbatim; if the proto held proto-allocated sound FormIDs the engine would drop them.
        // Acceptable risk for now since these are sound effects, not engine-mandatory wiring.
        if (crea.SoundDefinitionsRaw is { Count: > 0 } soundDefs)
        {
            foreach (var (sig, payload) in soundDefs)
            {
                subs.Add(new EncodedSubrecord(sig, payload));
            }
        }

        if (crea.ImpactDataSet.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(crea.ImpactDataSet.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("CNAM", resolved.Value));
            }
        }

        if (crea.DeathItemLootList.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(crea.DeathItemLootList.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("LNAM", resolved.Value));
            }
        }

        if (crea.EquippedItem.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(crea.EquippedItem.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("EITM", resolved.Value));
            }
        }

        if (crea.DestructionDataRaw is { Count: > 0 } destruction)
        {
            foreach (var (sig, payload) in destruction)
            {
                subs.Add(new EncodedSubrecord(sig, payload));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
