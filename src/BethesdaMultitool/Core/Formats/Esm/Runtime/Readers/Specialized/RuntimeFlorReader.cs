using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESFlora (FLOR, FormType 0x26). Harvestable plants:
///     xander roots, broc flowers, mutfruit, etc.
///
///     FLOR is a multiple-inheritance class whose TESForm subobject begins at +12 in
///     the complete object. Its identity therefore sits at complete-object offsets
///     +16/+24, while the TESForm pointer stored in runtime maps reads the same fields at
///     canonical subobject offsets +4/+12. <c>RuntimePdbFieldAccessor.ReadStruct</c>
///     rebases that retained subobject pointer by the PDB-derived +12 before applying
///     complete-object-relative fields.
/// </summary>
internal sealed class RuntimeFlorReader(RuntimeMemoryContext context)
{
    private const byte FlorFormType = 0x26;
    private const byte ScptFormType = 0x11;
    private const byte SounFormType = 0x0D;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads a runtime FLOR (flora) base form for the given DMP entry as a generic record.</summary>
    public GenericEsmRecord? ReadRuntimeFlor(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != FlorFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, FlorFormType);
        if (view == null)
        {
            return null;
        }

        var fullName = view.BsString("cFullName", "TESFullName");
        var modelPath = view.BsString("cModel", "TESModel");

        // Ingredient — ALCH/INGR don't share a single FormType, so resolve without
        // an expected-type constraint.
        var ingredientFormId = view.FormIdPointer("pFormIngredient", "TESProduceForm");
        var scriptFormId = view.FormIdPointer("pFormScript", "TESScriptableForm", ScptFormType);
        var soundFormId = view.FormIdPointer("pSoundLoop", "TESObjectACTI", SounFormType);

        var fields = new Dictionary<string, object?>();
        if (ingredientFormId.HasValue)
        {
            fields["pFormIngredient"] = ingredientFormId.Value;
            fields["PFIG"] = ingredientFormId.Value;
        }

        if (scriptFormId.HasValue)
        {
            fields["pFormScript"] = scriptFormId.Value;
            fields["SCRI"] = scriptFormId.Value;
        }

        if (soundFormId.HasValue)
        {
            fields["pSoundLoop"] = soundFormId.Value;
            fields["SNAM"] = soundFormId.Value;
        }

        return new GenericEsmRecord
        {
            FormId = entry.FormId,
            RecordType = "FLOR",
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            Fields = fields,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
