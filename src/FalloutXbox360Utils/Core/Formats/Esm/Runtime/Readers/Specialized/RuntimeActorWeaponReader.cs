using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

internal sealed class RuntimeActorWeaponReader(RuntimeMemoryContext context, int bipedPtrShift = 0)
{
    private const int CharacterStructSize = 472;
    private const int FormIdOffset = 12;
    private const int CurrentProcessPtrOffset = 120;
    private const int BipedPtrOffset = 452;
    private const int BipedWeaponOffset = 0x7C;
    private const int ProcessWeaponDrawnOffset = 0x135;

    // MemDebug PDB: BipedAnim (UDT 0xfda9, size 692) has `BIPOBJECT object[20]` at +44;
    // BIPOBJECT (UDT 0x18b98, size 16) has `TESForm* pParent` at +0. Slot 5 is the weapon:
    // 44 + 5*16 = 0x7C = BipedWeaponOffset above. The worn-armor read deliberately scans
    // the whole struct instead of indexing slots — see ReadRuntimeActorWornArmor.
    private const byte ArmoFormType = 0x18;
    private readonly RuntimeMemoryContext _context = context;

    // Proto builds shift Character.pBiped away from the PDB's +452; the shift is
    // discovered per dump by RuntimeBipedOffsetProbe and applied to both readers.
    private readonly int _bipedPtrOffset = BipedPtrOffset + bipedPtrShift;
    private readonly int _characterReadSize = Math.Max(CharacterStructSize, BipedPtrOffset + bipedPtrShift + 4);

    public RuntimeActorWeaponState? ReadRuntimeActorWeaponState(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x3B)
        {
            return null;
        }

        var actorBuffer = _context.ReadBytes(entry.TesFormOffset.Value, _characterReadSize);
        if (actorBuffer == null)
        {
            return null;
        }

        var actorFormId = BinaryUtils.ReadUInt32BE(actorBuffer, FormIdOffset);
        if (actorFormId != entry.FormId)
        {
            return null;
        }

        uint? weaponFormId = null;
        var bipedPtr = BinaryUtils.ReadUInt32BE(actorBuffer, _bipedPtrOffset);
        if (bipedPtr != 0)
        {
            var bipedBuffer = _context.ReadBytesAtVa(
                Xbox360MemoryUtils.VaToLong(bipedPtr),
                BipedWeaponOffset + 4);
            if (bipedBuffer != null)
            {
                var weaponPtr = BinaryUtils.ReadUInt32BE(bipedBuffer, BipedWeaponOffset);
                weaponFormId = ReadExpectedFormId(weaponPtr, 0x28);
            }
        }

        var isWeaponDrawn = false;
        var currentProcessPtr = BinaryUtils.ReadUInt32BE(actorBuffer, CurrentProcessPtrOffset);
        if (currentProcessPtr != 0)
        {
            var processBuffer = _context.ReadBytesAtVa(
                Xbox360MemoryUtils.VaToLong(currentProcessPtr),
                ProcessWeaponDrawnOffset + 1);
            if (processBuffer != null)
            {
                isWeaponDrawn = processBuffer[ProcessWeaponDrawnOffset] != 0;
            }
        }

        return new RuntimeActorWeaponState(
            entry.FormId,
            weaponFormId,
            isWeaponDrawn);
    }

    /// <summary>
    ///     Reads the actor's worn armor FormIDs from the BipedAnim slot array
    ///     (the engine's per-biped-slot equipped-3D registry). Non-ARMO slots
    ///     (weapon, torch) are filtered; armor spanning multiple slots is deduped.
    ///     Returns an empty list (not null) when the actor has no biped data, so
    ///     callers can distinguish "actor unreadable" from "nothing worn".
    /// </summary>
    public RuntimeActorWornArmorState? ReadRuntimeActorWornArmor(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x3B)
        {
            return null;
        }

        var actorBuffer = _context.ReadBytes(entry.TesFormOffset.Value, _characterReadSize);
        if (actorBuffer == null)
        {
            return null;
        }

        var actorFormId = BinaryUtils.ReadUInt32BE(actorBuffer, FormIdOffset);
        if (actorFormId != entry.FormId)
        {
            return null;
        }

        var wornArmorFormIds = new List<uint>();
        var bipedPtr = BinaryUtils.ReadUInt32BE(actorBuffer, _bipedPtrOffset);
        if (bipedPtr == 0)
        {
            return new RuntimeActorWornArmorState(entry.FormId, wornArmorFormIds);
        }

        // Scan the BipedAnim for ARMO pointers instead of indexing exact slots: proto
        // builds reshuffle BipedAnim internals, but worn armor is referenced by
        // 4-byte-aligned heap pointers in the struct on every observed layout. On the
        // final layout this covers both `object[20]` (+44) and `bufferedObjects[20]`
        // (+364), which dedupe to the same forms. ARMO-only filtering keeps the weapon
        // (slot 5, FormType 0x28) and hair (0x0C) out.
        var bipedBuffer = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(bipedPtr),
            RuntimeBipedOffsetProbe.PointeeScanBytes);
        if (bipedBuffer == null)
        {
            return new RuntimeActorWornArmorState(entry.FormId, wornArmorFormIds);
        }

        var seen = new HashSet<uint>();
        for (var pos = 0; pos + 4 <= bipedBuffer.Length; pos += 4)
        {
            var itemPtr = BinaryUtils.ReadUInt32BE(bipedBuffer, pos);
            if (!RuntimeBipedOffsetProbe.IsDataPointer(_context, itemPtr))
            {
                continue;
            }

            var armorFormId = ReadExpectedFormId(itemPtr, ArmoFormType);
            if (armorFormId.HasValue && seen.Add(armorFormId.Value))
            {
                wornArmorFormIds.Add(armorFormId.Value);
            }
        }

        if (wornArmorFormIds.Count > 0)
        {
            Logger.Instance.Debug("[WornArmor] 0x{0:X8} ({1}): biped 0x{2:X8} -> [{3}]",
                entry.FormId, entry.EditorId, bipedPtr,
                string.Join(", ", wornArmorFormIds.Select(id => $"0x{id:X8}")));
        }

        return new RuntimeActorWornArmorState(entry.FormId, wornArmorFormIds);
    }

    private uint? ReadExpectedFormId(uint pointer, byte expectedFormType)
    {
        if (pointer == 0)
        {
            return null;
        }

        var formHeader = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(pointer), 16);
        if (formHeader == null || formHeader[4] != expectedFormType)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(formHeader, FormIdOffset);
        return formId is 0 or 0xFFFFFFFF ? null : formId;
    }

    internal readonly record struct RuntimeActorWeaponState(
        uint ActorFormId,
        uint? WeaponFormId,
        bool IsWeaponDrawn);

    internal readonly record struct RuntimeActorWornArmorState(
        uint ActorFormId,
        IReadOnlyList<uint> WornArmorFormIds);
}
