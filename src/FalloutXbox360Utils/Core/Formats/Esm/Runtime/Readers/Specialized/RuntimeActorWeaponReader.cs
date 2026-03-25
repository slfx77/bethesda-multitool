using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

internal sealed class RuntimeActorWeaponReader(RuntimeMemoryContext context)
{
    private const int CharacterStructSize = 472;
    private const int FormIdOffset = 12;
    private const int CurrentProcessPtrOffset = 120;
    private const int BipedPtrOffset = 452;
    private const int BipedWeaponOffset = 0x7C;
    private const int ProcessWeaponDrawnOffset = 0x135;
    private readonly RuntimeMemoryContext _context = context;

    public RuntimeActorWeaponState? ReadRuntimeActorWeaponState(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x3B)
        {
            return null;
        }

        var actorBuffer = _context.ReadBytes(entry.TesFormOffset.Value, CharacterStructSize);
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
        var bipedPtr = BinaryUtils.ReadUInt32BE(actorBuffer, BipedPtrOffset);
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
}
