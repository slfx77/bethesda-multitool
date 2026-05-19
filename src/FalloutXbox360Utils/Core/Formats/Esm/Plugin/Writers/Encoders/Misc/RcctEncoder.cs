using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a <see cref="RecipeCategoryRecord" /> (RCCT) as PC-format subrecord bytes.
///     New-record-only path: override emission is a no-op.
///     fopdoc canonical order: EDID, FULL?, DATA(1 byte flags).
///     PDB struct: TESRecipeCategory (56 bytes, RECIPE_CATEGORY_DATA at +52 is 1 byte).
/// </summary>
public sealed class RcctEncoder : IRecordEncoder
{
    public string RecordType => "RCCT";
    public Type ModelType => typeof(RecipeCategoryRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(RecipeCategoryRecord rcct)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(rcct.EditorId))
        {
            warnings.Add($"New RCCT 0x{rcct.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", rcct.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(rcct.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", rcct.FullName));
        }

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("DATA", rcct.Flags));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
