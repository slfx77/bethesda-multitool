using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSEncounterZone structs (FormType 0x61, 64 bytes).
///     Reads ENCOUNTER_ZONE_DATA at offset +40: Owner FormID + Rank + MinLevel + Flags.
/// </summary>
internal sealed class RuntimeEncounterZoneReader(RuntimeMemoryContext context)
{
    public EncounterZoneRecord? ReadRuntimeEncounterZone(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != EczFormType)
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

        // Owner is stored as a pointer to the TESForm (FACT / NPC_) in memory, not a raw FormID.
        var ownerFormId = context.FollowPointerToFormId(buffer, DataOffset) ?? 0;
        var rank = (sbyte)buffer[DataOffset + 4];
        var minLevel = (sbyte)buffer[DataOffset + 5];
        var flags = buffer[DataOffset + 6];

        return new EncounterZoneRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            OwnerFormId = ownerFormId,
            Rank = rank,
            MinimumLevel = minLevel,
            Flags = flags,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte EczFormType = 0x61;
    private const int StructSize = 64;
    private const int FormIdOffset = 12;
    private const int DataOffset = 40;

    #endregion
}
