using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESClass structs (FormType 0x07).
///     Reads CLASS_DATA (tag skills, flags, barter, training), TESAttributes, full name,
///     icon via the PDB layout. TESAttributes and CLASS_DATA are opaque in the PDB —
///     we resolve their offsets by name and parse the inner layouts manually.
/// </summary>
internal sealed class RuntimeClassReader(RuntimeMemoryContext context)
{
    private const byte ClasFormType = 0x07;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public ClassRecord? ReadRuntimeClass(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != ClasFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, ClasFormType);
        if (view == null)
        {
            return null;
        }

        // TESAttributes is declared with size 0 in the PDB (opaque); the field is at
        // attrOff but the 4-byte vtable precedes the actual UCHAR[7] data, so attribute
        // bytes start at attrOff + 4.
        var attrOff = view.Offset("cAttribute", "TESAttributes");
        byte[] attributeWeights = new byte[7];
        if (attrOff is { } ao && ao + 4 + 7 <= view.Buffer.Length)
        {
            Array.Copy(view.Buffer, ao + 4, attributeWeights, 0, 7);
        }

        // CLASS_DATA (28 bytes): 4 tag-skill int32s, classFlags uint32, barterFlags uint32,
        // trainingSkill byte, trainingLevel byte.
        var dataOff = view.Offset("data", "TESClass");
        if (dataOff is not { } o || o + 26 > view.Buffer.Length)
        {
            return null;
        }

        var tagSkills = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var skill = unchecked((int)BinaryUtils.ReadUInt32BE(view.Buffer, o + i * 4));
            if (skill >= 0)
            {
                tagSkills.Add(skill);
            }
        }

        return new ClassRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName"),
            Icon = view.BsString("TextureName", "TESTexture"),
            TagSkills = tagSkills.ToArray(),
            Flags = BinaryUtils.ReadUInt32BE(view.Buffer, o + 16),
            BarterFlags = BinaryUtils.ReadUInt32BE(view.Buffer, o + 20),
            TrainingSkill = view.Buffer[o + 24],
            TrainingLevel = view.Buffer[o + 25],
            AttributeWeights = attributeWeights,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
