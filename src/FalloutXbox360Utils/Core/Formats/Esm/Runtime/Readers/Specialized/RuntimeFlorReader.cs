using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESFlora (FLOR, FormType 0x26). Harvestable plants:
///     xander roots, broc flowers, mutfruit, etc.
///
///     FLOR is a multi-inheritance class — TESProduceForm + TESScriptableForm sit before
///     TESForm in the C++ layout, so cFormType lives at +16 and iFormID at +24 (not the
///     standard +4 / +12). The PDB layout records these offsets accurately, and
///     <see cref="RuntimePdbFieldAccessor.ReadStruct" /> resolves the cFormType / iFormID
///     positions from the layout itself — so the regular OpenStructView flow now works
///     for FLOR without any reader-side offset gymnastics.
///
///     Activated by the multi-inheritance probe in
///     <see cref="FalloutXbox360Utils.Core.Formats.Esm.Records.TesFormHeaderProbe" />,
///     which populates <c>entry.FormId</c> with the FormID read from the FLOR-specific
///     iFormID offset before the reader runs.
/// </summary>
internal sealed class RuntimeFlorReader(RuntimeMemoryContext context)
{
    private const byte FlorFormType = 0x26;
    private const byte ScptFormType = 0x11;
    private const byte SounFormType = 0x0D;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

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
