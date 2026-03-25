using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESEyes (EYES, 68B) and TESHair (HAIR, 92B).
/// </summary>
internal sealed class RuntimeCharacterAppearanceReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeCharacterAppearanceReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    #region EYES — TESEyes (68 bytes, FormType 0x0B)

    public EyesRecord? ReadRuntimeEyes(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != EyesFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + EyesStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[EyesStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, EyesStructSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, EyesFullNameOffset);
        var texturePath = _context.ReadBSStringT(offset, EyesTextureOffset);
        var flags = buffer[EyesFlagsOffset];

        return new EyesRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            TexturePath = texturePath,
            Flags = flags,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region HAIR — TESHair (92 bytes, FormType 0x0A)

    public HairRecord? ReadRuntimeHair(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != HairFormType)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + HairStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[HairStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, HairStructSize);
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

        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, HairFullNameOffset);
        var modelPath = _context.ReadBSStringT(offset, HairModelOffset);
        var texturePath = _context.ReadBSStringT(offset, HairTextureOffset);
        var flags = buffer[HairFlagsOffset];

        return new HairRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            TexturePath = texturePath,
            Flags = flags,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #endregion

    #region Constants

    private const int FormIdOffset = 12;

    // EYES — TESEyes (68 bytes)
    private const byte EyesFormType = 0x0B;
    private const int EyesStructSize = 68;
    private const int EyesFullNameOffset = 44;
    private const int EyesTextureOffset = 56;
    private const int EyesFlagsOffset = 64;

    // HAIR — TESHair (92 bytes)
    private const byte HairFormType = 0x0A;
    private const int HairStructSize = 92;
    private const int HairFullNameOffset = 44;
    private const int HairModelOffset = 56;
    private const int HairTextureOffset = 80;
    private const int HairFlagsOffset = 88;

    #endregion
}
