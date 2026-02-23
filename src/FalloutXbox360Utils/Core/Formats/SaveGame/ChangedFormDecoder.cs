namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Decodes the raw Data[] bytes of a ChangedForm into structured fields.
///     Dispatches by ChangeType to specialist decoder classes.
/// </summary>
public static class ChangedFormDecoder
{
    /// <summary>
    ///     Attempts to decode a changed form's raw data bytes.
    ///     Returns null if the change type is not supported for decoding.
    /// </summary>
    public static DecodedFormData? Decode(ChangedForm form, ReadOnlySpan<uint> formIdArray)
    {
        if (form.Data.Length == 0)
        {
            return null;
        }

        var result = new DecodedFormData { TotalBytes = form.Data.Length };
        var reader = new FormDataReader(form.Data, formIdArray);

        try
        {
            switch (form.ChangeType)
            {
                case 0: // REFR
                    RefrDecoder.DecodeRefr(ref reader, form.ChangeFlags, result, form.Initial?.DataType ?? 0);
                    break;
                case 1: // ACHR (Character)
                    ActorDecoder.DecodeActor(ref reader, form.ChangeFlags, result, true, form.Initial?.DataType ?? 0);
                    break;
                case 2: // ACRE (Creature)
                    ActorDecoder.DecodeActor(ref reader, form.ChangeFlags, result, false, form.Initial?.DataType ?? 0);
                    break;
                case >= 3 and <= 6: // PMIS, PGRE, PBEA, PFLA
                    RefrDecoder.DecodeProjectile(ref reader, form.ChangeFlags, result);
                    break;
                case 7: // CELL
                    BaseTypeDecoder.DecodeCell(ref reader, form.ChangeFlags, result);
                    break;
                case 8: // INFO
                    BaseTypeDecoder.DecodeInfo(ref reader, form.ChangeFlags, result);
                    break;
                case 9: // QUST
                    QuestNpcDecoder.DecodeQuest(ref reader, form.ChangeFlags, result);
                    break;
                case 10: // NPC_
                    QuestNpcDecoder.DecodeNpc(ref reader, form.ChangeFlags, result);
                    break;
                case 11: // CREA
                    QuestNpcDecoder.DecodeCreature(ref reader, form.ChangeFlags, result);
                    break;
                case 16: // BOOK
                    BaseTypeDecoder.DecodeBaseObject(ref reader, form.ChangeFlags, result);
                    BaseTypeDecoder.DecodeBookSpecific(ref reader, form.ChangeFlags, result);
                    break;
                case 31: // NOTE
                    BaseTypeDecoder.DecodeNoteForm(ref reader, form.ChangeFlags, result);
                    break;
                case 32: // ECZN
                    BaseTypeDecoder.DecodeEncounterZone(ref reader, form.ChangeFlags, result);
                    break;
                case 33: // CLAS
                    BaseTypeDecoder.DecodeClass(ref reader, form.ChangeFlags, result);
                    break;
                case 34: // FACT
                    BaseTypeDecoder.DecodeFaction(ref reader, form.ChangeFlags, result);
                    break;
                case 35: // PACK
                    BaseTypeDecoder.DecodePackage(ref reader, form.ChangeFlags, result);
                    break;
                case 37: // FLST
                    BaseTypeDecoder.DecodeFormList(ref reader, form.ChangeFlags, result);
                    break;
                case 38 or 39 or 40: // LVLC, LVLN, LVLI
                    BaseTypeDecoder.DecodeLeveledList(ref reader, form.ChangeFlags, result);
                    break;
                case 41: // WATR
                    BaseTypeDecoder.DecodeWater(ref reader, form.ChangeFlags, result);
                    break;
                case 43: // REPU
                    BaseTypeDecoder.DecodeReputation(ref reader, form.ChangeFlags, result);
                    break;
                case 50: // CHAL
                    BaseTypeDecoder.DecodeChallenge(ref reader, form.ChangeFlags, result);
                    break;
                default:
                    // For all other base object types (ACTI, TACT, TERM, ARMO, etc.)
                    if (form.ChangeType is >= 12 and <= 29)
                    {
                        BaseTypeDecoder.DecodeBaseObject(ref reader, form.ChangeFlags, result);
                    }
                    else
                    {
                        return null;
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Decode error at offset {reader.Position}: {ex.Message}");
        }

        result.BytesConsumed = reader.Position;
        return result;
    }
}
