using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESSound (SOUN, FormType 0x0D).
///     Reads sound file path, SOUND_DATA fields, and random percent chance via the
///     PDB layout. The SOUND_DATA struct at +76 is opaque in the PDB, so we resolve
///     its offset by name then parse the 12-byte prefix manually.
/// </summary>
internal sealed class RuntimeSoundReader(RuntimeMemoryContext context)
{
    private const byte SounFormType = 0x0D;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public SoundRecord? ReadRuntimeSound(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != SounFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, SounFormType);
        if (view == null)
        {
            return null;
        }

        var dataOff = view.Offset("data", "TESSound");
        if (dataOff is not { } o || o + 12 > view.Buffer.Length)
        {
            return null;
        }

        // SOUND_DATA layout: minAtten(byte), maxAtten(byte), pad(2), flags(uint32),
        // staticAtten(int16), endTime(byte), startTime(byte).
        var minAtten = view.Buffer[o];
        var maxAtten = view.Buffer[o + 1];
        var flags = BinaryUtils.ReadUInt32BE(view.Buffer, o + 4);
        var staticAtten = BinaryUtils.ReadInt16BE(view.Buffer, o + 8);
        var endTime = view.Buffer[o + 10];
        var startTime = view.Buffer[o + 11];

        return new SoundRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FileName = view.BsString("cSoundFile", "TESSoundFile"),
            MinAttenuationDistance = minAtten,
            MaxAttenuationDistance = maxAtten,
            StaticAttenuation = staticAtten,
            Flags = flags,
            StartTime = startTime,
            EndTime = endTime,
            RandomPercentChance = (sbyte)view.Byte("cRandomPercentChance", "TESSound"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
