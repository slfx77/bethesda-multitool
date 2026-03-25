using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for BGSMessage (MESG, 80 bytes, FormType 0x62).
///     Reads full name, flags, and display time.
///     Description and button list are ESM-only.
/// </summary>
internal sealed class RuntimeMessageReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeMessageReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public MessageRecord? ReadRuntimeMessage(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != MesgFormType)
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, FullNameOffset);
        var flags = BinaryUtils.ReadUInt32BE(buffer, FlagsOffset);
        var displayTime = BinaryUtils.ReadUInt32BE(buffer, DisplayTimeOffset);

        return new MessageRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Flags = flags,
            DisplayTime = displayTime,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte MesgFormType = 0x62;
    private const int StructSize = 80;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44; // TESFullName.cFullName BSStringT
    private const int FlagsOffset = 72; // iFlags uint32
    private const int DisplayTimeOffset = 76; // iDisplayTime uint32

    #endregion
}
