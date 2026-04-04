using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESChallenge (CHAL, 140 bytes, FormType 0x71).
///     Reads full name, CHALLENGE_DATA, and form pointer targets.
/// </summary>
internal sealed class RuntimeChallengeReader
{
    private readonly RuntimeMemoryContext _context;

    public RuntimeChallengeReader(RuntimeMemoryContext context)
    {
        _context = context;
    }

    public ChallengeRecord? ReadRuntimeChallenge(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != ChalFormType)
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

        // CHALLENGE_DATA (24 bytes at +100)
        var challengeType = BinaryUtils.ReadUInt32BE(buffer, DataOffset);
        var threshold = BinaryUtils.ReadUInt32BE(buffer, DataOffset + 4);
        var flags = BinaryUtils.ReadUInt16BE(buffer, DataOffset + 8);
        var interval = BinaryUtils.ReadUInt16BE(buffer, DataOffset + 10);
        var value1 = BinaryUtils.ReadUInt32BE(buffer, DataOffset + 12);
        var value2 = BinaryUtils.ReadUInt16BE(buffer, DataOffset + 16);
        var value3 = BinaryUtils.ReadUInt16BE(buffer, DataOffset + 18);

        // Follow script pointer
        var scriptFormId = _context.FollowPointerToFormId(buffer, ScriptPtrOffset) ?? 0u;

        return new ChallengeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ChallengeType = challengeType,
            Threshold = threshold,
            Flags = flags,
            Interval = interval,
            Value1 = value1,
            Value2 = value2,
            Value3 = value3,
            Script = scriptFormId,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Constants

    private const byte ChalFormType = 0x71;
    private const int StructSize = 140;
    private const int FormIdOffset = 12;
    private const int FullNameOffset = 44; // TESFullName.cFullName BSStringT
    private const int ScriptPtrOffset = 64; // TESScriptableForm.pFormScript pointer
    private const int DataOffset = 100; // CHALLENGE_DATA (24 bytes)

    #endregion
}
