using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

/// <summary>
///     Encodes a <see cref="ConstructibleObjectRecord" /> (COBJ) as PC-format subrecord bytes.
///     FNV crafting blueprint — ties ingredient list (CNTO*) and crafting conditions (CTDA*)
///     to a created item (CNAM).
///     New-record-only path: override emission is a no-op.
///     fopdoc canonical order: EDID, OBND?, FULL?, MODL?, MODT?, COCT, CNTO*,
///     CTDA* (with optional CIS1/CIS2), CNAM, BNAM?.
///     COCT (4 bytes): uint32 count of CNTO entries that follow.
///     CNTO (8 bytes): FormID Item(0) + int32 Count(4) — emitted via InventoryItem.
///     CNAM (4 bytes): FormID of the item produced by crafting.
///     BNAM (4 bytes, optional): FormID of the workbench keyword filter.
/// </summary>
public sealed class CobjEncoder : IRecordEncoder
{
    public string RecordType => "COBJ";
    public Type ModelType => typeof(ConstructibleObjectRecord);

    internal static EncodedRecord EncodeNew(ConstructibleObjectRecord cobj)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(cobj.EditorId))
        {
            warnings.Add($"New COBJ 0x{cobj.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", cobj.EditorId ?? string.Empty));

        if (cobj.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(cobj.Bounds));
        }

        if (!string.IsNullOrEmpty(cobj.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", cobj.FullName));
        }

        if (!string.IsNullOrEmpty(cobj.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", cobj.ModelPath));
        }

        if (cobj.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        // COCT (uint32 count of CNTOs) + CNTO entries. COCT is emitted whenever ingredients
        // exist — the parser uses it as a hint but doesn't strictly require it (CNTOs are
        // self-delimiting), so emitting only when non-zero keeps the output minimal.
        if (cobj.Ingredients.Count > 0)
        {
            subs.Add(NewRecordSubrecords.EncodeUInt32Subrecord("COCT", (uint)cobj.Ingredients.Count));
            foreach (var ingredient in cobj.Ingredients)
            {
                subs.Add(new EncodedSubrecord("CNTO", BuildCntoSubrecord(ingredient)));
            }
        }

        // Crafting conditions — CTDA* + optional CIS1/CIS2 per condition.
        foreach (var condition in cobj.Conditions)
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

        // CNAM is the created/output item — required for the recipe to do anything.
        if (cobj.CreatedItemFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("CNAM", cobj.CreatedItemFormId.Value));
        }
        else
        {
            warnings.Add(
                $"New COBJ 0x{cobj.FormId:X8} has no CreatedItemFormId — recipe will produce nothing.");
        }

        if (cobj.WorkbenchKeywordFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("BNAM", cobj.WorkbenchKeywordFormId.Value));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildCntoSubrecord(InventoryItem item)
    {
        var bytes = new byte[8];
        SubrecordEncoder.WriteFormId(bytes, 0, item.ItemFormId);
        SubrecordEncoder.WriteInt32(bytes, 4, item.Count);
        return bytes;
    }
}
