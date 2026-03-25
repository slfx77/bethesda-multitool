using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESSound (SOUN, 116 bytes, FormType 0x0D).
///     Reads sound file path, SOUND_DATA fields, and random percent chance.
/// </summary>
internal sealed class RuntimeSoundReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeSoundReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public SoundRecord? ReadRuntimeSound(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != SounFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[StructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, StructSize);
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

        var fileName = _context.ReadBSStringT(offset, SoundFileOffset);

        // SOUND_DATA struct at +76 (36 bytes) — same layout as SNDD subrecord
        var minAtten = buffer[SoundDataOffset + 0];
        var maxAtten = buffer[SoundDataOffset + 1];
        var flags = BinaryUtils.ReadUInt32BE(buffer, SoundDataOffset + 4);
        var staticAtten = BinaryUtils.ReadInt16BE(buffer, SoundDataOffset + 8);
        var endTime = buffer[SoundDataOffset + 10];
        var startTime = buffer[SoundDataOffset + 11];
        var randomChance = (sbyte)buffer[RandomChanceOffset];

        return new SoundRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FileName = fileName,
            MinAttenuationDistance = minAtten,
            MaxAttenuationDistance = maxAtten,
            StaticAttenuation = staticAtten,
            Flags = flags,
            StartTime = startTime,
            EndTime = endTime,
            RandomPercentChance = randomChance,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte SounFormType = 0x0D;
    private const int StructSize = 116;
    private const int FormIdOffset = 12;
    private const int SoundFileOffset = 68; // TESSoundFile.cSoundFile BSStringT
    private const int SoundDataOffset = 76; // SOUND_DATA struct (36 bytes)
    private const int RandomChanceOffset = 112; // cRandomPercentChance int8

    #endregion
}
