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
    // 44 + 5*16 = 0x7C = BipedWeaponOffset above, anchoring this layout to the proven read.
    private const int BipedSlotArrayOffset = 44;
    private const int BipedSlotCount = 20;
    private const int BipedSlotSize = 16;
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
            Logger.Instance.Debug("[WornArmor] 0x{0:X8}: pBiped is null", entry.FormId);
            return new RuntimeActorWornArmorState(entry.FormId, wornArmorFormIds);
        }

        var slotBuffer = _context.ReadBytesAtVa(
            Xbox360MemoryUtils.VaToLong(bipedPtr) + BipedSlotArrayOffset,
            BipedSlotCount * BipedSlotSize);
        if (slotBuffer == null)
        {
            Logger.Instance.Debug("[WornArmor] 0x{0:X8}: slot array unreadable at pBiped 0x{1:X8}",
                entry.FormId, bipedPtr);
            return new RuntimeActorWornArmorState(entry.FormId, wornArmorFormIds);
        }

        var seen = new HashSet<uint>();
        for (var slot = 0; slot < BipedSlotCount; slot++)
        {
            var itemPtr = BinaryUtils.ReadUInt32BE(slotBuffer, slot * BipedSlotSize);
            if (itemPtr != 0)
            {
                Logger.Instance.Debug("[WornArmor] 0x{0:X8}: slot {1} ptr 0x{2:X8} formType 0x{3:X2}",
                    entry.FormId, slot, itemPtr, DescribePointeeFormType(itemPtr));
            }

            var armorFormId = ReadExpectedFormId(itemPtr, ArmoFormType);
            if (armorFormId.HasValue && seen.Add(armorFormId.Value))
            {
                wornArmorFormIds.Add(armorFormId.Value);
            }
        }

        return new RuntimeActorWornArmorState(entry.FormId, wornArmorFormIds);
    }

    private int DescribePointeeFormType(uint pointer)
    {
        var header = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(pointer), 16);
        return header?[4] ?? -1;
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
