using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESCaravanDeck (CDCK, 60 bytes, FormType 0x75).
///     Reads the CARAVANDECKDATA <c>uint32</c> "joker count" from the <c>data</c>
///     substruct via PDB-resolved offsets. The runtime card count walk is skipped —
///     <c>pDeck</c> is a pointer to a heap-allocated BSSimpleList&lt;TESCaravanCard*&gt;
///     and the ESM-side CNTO counting already carries parity.
/// </summary>
internal sealed class RuntimeCaravanDeckReader(RuntimeMemoryContext context)
{
    private const byte CdckFormType = 0x75;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public CaravanDeckRecord? ReadRuntimeCaravanDeck(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != CdckFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry);
        if (view == null)
        {
            return null;
        }

        // CARAVANDECKDATA substruct holds joker count as a single uint32.
        var jokerCount = view.Offset("data", "TESCaravanDeck") is { } dataOff
            ? BinaryUtils.ReadUInt32BE(view.Buffer, dataOff)
            : 0u;

        return new CaravanDeckRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            CardCount = 0,
            JokerCount = jokerCount,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
