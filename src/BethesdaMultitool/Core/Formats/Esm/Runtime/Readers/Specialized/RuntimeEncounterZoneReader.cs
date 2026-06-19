using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSEncounterZone structs (FormType 0x61).
///     Reads ENCOUNTER_ZONE_DATA at the PDB-resolved Data offset: Owner pointer +
///     Rank + MinLevel + Flags.
/// </summary>
internal sealed class RuntimeEncounterZoneReader(RuntimeMemoryContext context)
{
    private const byte EczFormType = 0x61;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    /// <summary>Reads the runtime encounter-zone record for the given DMP entry, or null if it can't be read.</summary>
    public EncounterZoneRecord? ReadRuntimeEncounterZone(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != EczFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, EczFormType);
        if (view == null)
        {
            return null;
        }

        var dataOff = view.Offset("Data", "BGSEncounterZone");
        if (dataOff is not { } o || o + 7 > view.Buffer.Length)
        {
            return null;
        }

        // Owner is stored as a pointer to the TESForm (FACT / NPC_), not a raw FormID.
        var ownerFormId = context.FollowPointerToFormId(view.Buffer, o) ?? 0;
        var rank = (sbyte)view.Buffer[o + 4];
        var minLevel = (sbyte)view.Buffer[o + 5];
        var flags = view.Buffer[o + 6];

        return new EncounterZoneRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            OwnerFormId = ownerFormId,
            Rank = rank,
            MinimumLevel = minLevel,
            Flags = flags,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
