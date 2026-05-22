using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

/// <summary>
///     Encodes a <see cref="ContainerRecord" /> (CONT) as PC-format subrecord bytes.
///     Emits the full record: EDID + OBND? + FULL? + MODL? + MODT? + SCRI? +
///     CNTO+COED?+ (per item) + DATA + SNAM? + QNAM? + RNAM?.
///     Override path is a no-op.
///     DATA layout (5 bytes, packed/unaligned):
///     byte  Flags(0)
///     float Weight(1) — little-endian
///     COED layout (12 bytes, optional per CNTO):
///     FormID Owner(0) + uint32 GlobalOrRank(4) + float ItemCondition(8)
/// </summary>
public sealed class ContEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<ContainerRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Flags"] = m => m.Flags,
        ["Weight"] = m => m.Weight,
    };

    public string RecordType => "CONT";
    public Type ModelType => typeof(ContainerRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     Encode a new CONT record from scratch in fopdoc canonical order:
    ///     EDID, OBND, FULL, MODL, MODT, SCRI, [CNTO+COED?]+, DATA, SNAM, QNAM, RNAM.
    ///     <para>
    ///     CNTO inventory entries with a dangling ItemFormId are skipped (and their
    ///     paired COED, if any). The engine logs "Unable to find container object" otherwise
    ///     and removes the inventory line, so we skip at encode time. Sounds (SNAM/QNAM/RNAM)
    ///     and SCRI are likewise validated.
    ///     </para>
    /// </summary>
    internal static EncodedRecord EncodeNew(
        ContainerRecord cont,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(cont.EditorId))
        {
            warnings.Add($"New CONT 0x{cont.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", cont.EditorId ?? string.Empty));

        if (cont.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(cont.Bounds));
        }

        if (!string.IsNullOrEmpty(cont.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", cont.FullName));
        }

        if (!string.IsNullOrEmpty(cont.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", cont.ModelPath));
        }

        if (cont.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (cont.Script.HasValue)
        {
            var resolvedScript = FormIdReferenceResolver.Resolve(
                cont.Script.Value, validFormIds, remapTable);
            if (resolvedScript.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", resolvedScript.Value));
            }
            else
            {
                warnings.Add(
                    $"New CONT 0x{cont.FormId:X8} SCRI 0x{cont.Script.Value:X8} dangles — subrecord skipped.");
            }
        }

        var droppedItems = 0;
        foreach (var item in cont.Contents)
        {
            var resolvedItem = FormIdReferenceResolver.Resolve(
                item.ItemFormId, validFormIds, remapTable);
            if (!resolvedItem.HasValue)
            {
                droppedItems++;
                continue;
            }

            subs.Add(new EncodedSubrecord("CNTO", BuildCntoSubrecord(item, resolvedItem.Value)));
            if (HasOwnership(item))
            {
                subs.Add(new EncodedSubrecord("COED", BuildCoedSubrecord(item)));
            }
        }
        if (droppedItems > 0)
        {
            warnings.Add(
                $"New CONT 0x{cont.FormId:X8} dropped {droppedItems} CNTO inventory entry/entries " +
                "with dangling item FormID (engine would log 'Unable to find container object').");
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "CONT", 5, cont, DataExtractors));

        if (cont.OpenSoundFormId.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(
                cont.OpenSoundFormId.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", resolved.Value));
            }
        }

        if (cont.OpenSoundLoopFormId.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(
                cont.OpenSoundLoopFormId.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("QNAM", resolved.Value));
            }
        }

        if (cont.CloseSoundFormId.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(
                cont.CloseSoundFormId.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("RNAM", resolved.Value));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildCntoSubrecord(InventoryItem item, uint resolvedItemFormId)
    {
        var data = new byte[8];
        SubrecordEncoder.WriteFormId(data, 0, resolvedItemFormId);
        SubrecordEncoder.WriteInt32(data, 4, item.Count);
        return data;
    }

    /// <summary>
    ///     Backward-compat overload for callers that don't have a validity context (override
    ///     paths, tests). Emits the item FormID verbatim.
    /// </summary>
    internal static byte[] BuildCntoSubrecord(InventoryItem item)
    {
        return BuildCntoSubrecord(item, item.ItemFormId);
    }

    internal static bool HasOwnership(InventoryItem item)
    {
        return item.OwnerFormId.HasValue || item.GlobalOrRank.HasValue || item.ItemCondition.HasValue;
    }

    internal static byte[] BuildCoedSubrecord(InventoryItem item)
    {
        var data = new byte[12];
        SubrecordEncoder.WriteFormId(data, 0, item.OwnerFormId ?? 0);
        SubrecordEncoder.WriteUInt32(data, 4, item.GlobalOrRank ?? 0);
        SubrecordEncoder.WriteFloat(data, 8, item.ItemCondition ?? 0f);
        return data;
    }
}
