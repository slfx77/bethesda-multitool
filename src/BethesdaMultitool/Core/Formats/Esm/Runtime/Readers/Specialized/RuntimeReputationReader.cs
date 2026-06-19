using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESReputation (REPU, FormType 0x68).
///     Reads full name, positive/negative threshold values via the PDB layout.
/// </summary>
internal sealed class RuntimeReputationReader(RuntimeMemoryContext context)
{
    private const byte RepuFormType = 0x68;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads the runtime reputation record for the given DMP entry, or null if it can't be read.</summary>
    public ReputationRecord? ReadRuntimeReputation(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != RepuFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, RepuFormType);
        if (view == null)
        {
            return null;
        }

        var positiveValue = view.Float("fPositiveValue", "TESReputation");
        if (!RuntimeMemoryContext.IsNormalFloat(positiveValue))
        {
            positiveValue = 0f;
        }

        var negativeValue = view.Float("fNegativeValue", "TESReputation");
        if (!RuntimeMemoryContext.IsNormalFloat(negativeValue))
        {
            negativeValue = 0f;
        }

        return new ReputationRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName"),
            PositiveValue = positiveValue,
            NegativeValue = negativeValue,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
