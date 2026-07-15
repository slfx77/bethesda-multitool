using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter;

/// <summary>
///     Drops SCRI bindings that no longer name a live SCPT after the planner suppresses an
///     unsafe new script. This is deliberately post-remap so both source and allocated IDs
///     are checked in the exact form that would be serialized.
/// </summary>
internal static class EncodedScriptBindingSanitizer
{
    public static IReadOnlyList<EncodedSubrecord> DropInvalidScri(
        IReadOnlyList<EncodedSubrecord> subrecords,
        IReadOnlySet<uint> validScriptFormIds)
    {
        List<EncodedSubrecord>? filtered = null;
        for (var i = 0; i < subrecords.Count; i++)
        {
            var subrecord = subrecords[i];
            var invalid = subrecord.Signature == "SCRI"
                          && (subrecord.Bytes.Length != sizeof(uint)
                              || !validScriptFormIds.Contains(
                                  BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Bytes)));
            if (!invalid)
            {
                filtered?.Add(subrecord);
                continue;
            }

            filtered ??= CopyPrefix(subrecords, i);
        }

        return filtered ?? subrecords;
    }

    private static List<EncodedSubrecord> CopyPrefix(
        IReadOnlyList<EncodedSubrecord> subrecords,
        int count)
    {
        var copy = new List<EncodedSubrecord>(subrecords.Count - 1);
        for (var i = 0; i < count; i++)
        {
            copy.Add(subrecords[i]);
        }

        return copy;
    }
}
