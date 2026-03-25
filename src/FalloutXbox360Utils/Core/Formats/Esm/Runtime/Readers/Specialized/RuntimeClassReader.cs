using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESClass structs (FormType 0x07, 112 bytes).
///     Reads CLASS_DATA (tag skills, flags, barter, training), TESAttributes, full name, icon.
/// </summary>
internal sealed class RuntimeClassReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeClassReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public ClassRecord? ReadRuntimeClass(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != ClasFormType)
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
        var icon = _context.ReadBSStringT(offset, IconOffset);

        // TESAttributes: vtable(4) + UCHAR[7] at +76. Attribute bytes start at +80.
        var attributeWeights = new byte[7];
        Array.Copy(buffer, AttributeDataOffset, attributeWeights, 0, 7);

        // CLASS_DATA (28 bytes at +84)
        var tagSkills = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var skill = unchecked((int)BinaryUtils.ReadUInt32BE(buffer, ClassDataOffset + i * 4));
            if (skill >= 0)
            {
                tagSkills.Add(skill);
            }
        }

        var classFlags = BinaryUtils.ReadUInt32BE(buffer, ClassDataOffset + 16);
        var barterFlags = BinaryUtils.ReadUInt32BE(buffer, ClassDataOffset + 20);
        var trainingSkill = buffer[ClassDataOffset + 24];
        var trainingLevel = buffer[ClassDataOffset + 25];

        return new ClassRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Icon = icon,
            TagSkills = tagSkills.ToArray(),
            Flags = classFlags,
            BarterFlags = barterFlags,
            TrainingSkill = trainingSkill,
            TrainingLevel = trainingLevel,
            AttributeWeights = attributeWeights,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte ClasFormType = 0x07;
    private const int StructSize = 112;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44;
    private const int IconOffset = 64;
    private const int AttributeDataOffset = 80; // TESAttributes vtable(4) at +76, data starts at +80
    private const int ClassDataOffset = 84; // CLASS_DATA, 28 bytes

    #endregion
}
