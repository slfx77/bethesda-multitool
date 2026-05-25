using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Typed runtime reader for TESChallenge (CHAL, FormType 0x71).
///     Reads full name, CHALLENGE_DATA, and the linked script. The CHALLENGE_DATA
///     struct at +100 is opaque in the PDB — we resolve its offset by name then parse
///     the 20-byte prefix (Type, Threshold, Flags, Interval, Value1..3) manually.
/// </summary>
internal sealed class RuntimeChallengeReader(RuntimeMemoryContext context)
{
    private const byte ChalFormType = 0x71;

    private readonly RuntimePdbFieldAccessor _fields = new(context);

    public ChallengeRecord? ReadRuntimeChallenge(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != ChalFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, ChalFormType);
        if (view == null)
        {
            return null;
        }

        var dataOff = view.Offset("data", "TESChallenge");
        if (dataOff is not { } o || o + 20 > view.Buffer.Length)
        {
            return null;
        }

        return new ChallengeRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName ?? view.BsString("cFullName", "TESFullName"),
            ChallengeType = BinaryUtils.ReadUInt32BE(view.Buffer, o),
            Threshold = BinaryUtils.ReadUInt32BE(view.Buffer, o + 4),
            Flags = BinaryUtils.ReadUInt16BE(view.Buffer, o + 8),
            Interval = BinaryUtils.ReadUInt16BE(view.Buffer, o + 10),
            Value1 = BinaryUtils.ReadUInt32BE(view.Buffer, o + 12),
            Value2 = BinaryUtils.ReadUInt16BE(view.Buffer, o + 16),
            Value3 = BinaryUtils.ReadUInt16BE(view.Buffer, o + 18),
            Script = view.FormIdPointer("pFormScript", "TESScriptableForm") ?? 0u,
            Offset = view.FileOffset,
            IsBigEndian = true
        };
    }
}
