using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSMusicType (MUSC, 68 bytes, FormType 0x66).
///     Reads music file path and attenuation.
/// </summary>
internal sealed class RuntimeMusicTypeReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeMusicTypeReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public MusicTypeRecord? ReadRuntimeMusicType(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != MuscFormType)
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
        var attenuation = BinaryUtils.ReadFloatBE(buffer, AttenuationOffset);

        return new MusicTypeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FileName = fileName,
            Attenuation = attenuation,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte MuscFormType = 0x66;
    private const int StructSize = 68;
    private const int FormIdOffset = 12;
    private const int SoundFileOffset = 44; // TESSoundFile.cSoundFile BSStringT
    private const int AttenuationOffset = 52; // BGSMusicType.fAttenuation float32

    #endregion
}
