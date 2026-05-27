using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESCaravanMoney (CMNY, FormType 0x74).
/// </summary>
internal sealed class RuntimeCaravanMoneyReader(RuntimeMemoryContext context)
{
    private const byte CmnyFormType = 0x74;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public CaravanMoneyRecord? ReadRuntimeCaravanMoney(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != CmnyFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, CmnyFormType);
        if (view == null)
        {
            return null;
        }

        return new CaravanMoneyRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            Value = view.UInt32("iValue", "TESValueForm"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
