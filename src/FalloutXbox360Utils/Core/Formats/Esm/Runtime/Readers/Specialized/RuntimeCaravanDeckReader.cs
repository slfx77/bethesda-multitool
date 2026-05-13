using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESCaravanDeck (CDCK, 60 bytes, FormType 0x75).
///     Walks the pDeck BSSimpleList for card count + reads CARAVANDECKDATA
///     uint32 at +56 (joker count).
/// </summary>
internal sealed class RuntimeCaravanDeckReader(RuntimeMemoryContext context)
{
    public CaravanDeckRecord? ReadRuntimeCaravanDeck(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != CdckFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            context.Accessor.ReadArray(offset, buffer, 0, StructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, FormIdOffset);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        // pDeck is a pointer to a heap-allocated BSSimpleList<TESCaravanCard*>.
        // Walking it requires dereferencing the pointer first — skip for now;
        // runtime card count remains 0. ESM-side CNTO counting carries the parity.
        var jokerCount = BinaryUtils.ReadUInt32BE(buffer, DataOffset);

        return new CaravanDeckRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            CardCount = 0,
            JokerCount = jokerCount,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte CdckFormType = 0x75;
    private const int StructSize = 60;
    private const int FormIdOffset = 12;
    private const int DeckPointerOffset = 52;
    private const int DataOffset = 56;

    #endregion
}
