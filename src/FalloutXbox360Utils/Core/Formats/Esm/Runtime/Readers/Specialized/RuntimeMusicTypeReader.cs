using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSMusicType (MUSC, FormType 0x66).
///     Reads music file path and attenuation via the PDB layout.
/// </summary>
internal sealed class RuntimeMusicTypeReader(RuntimeMemoryContext context)
{
    private const byte MuscFormType = 0x66;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public MusicTypeRecord? ReadRuntimeMusicType(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != MuscFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, MuscFormType);
        if (view == null)
        {
            return null;
        }

        return new MusicTypeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FileName = view.BsString("cSoundFile", "TESSoundFile"),
            Attenuation = view.Float("fAttenuation", "BGSMusicType"),
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
