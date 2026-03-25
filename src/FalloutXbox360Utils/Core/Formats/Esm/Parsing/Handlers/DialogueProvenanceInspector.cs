using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class DialogueProvenanceInspector : RecordHandlerBase
{
    internal DialogueProvenanceInspector(
        RecordParserContext context,
        IEnumerable<DialogueRecord> dialogues)
        : base(context)
    {
        TesFileSegments = DialogueTesFileScriptRecovery.CalibrateSegments(
            dialogues,
            context.MinidumpInfo);
    }

    internal IReadOnlyList<DialogueTesFileMappingSegment> TesFileSegments { get; }

    internal DialogueInfoProvenanceReport InspectInfo(DialogueRecord dialogue, bool includeHex = false)
    {
        var runtimeStructBytes = ReadBytes(
            dialogue.RuntimeStructOffset > 0 ? dialogue.RuntimeStructOffset : null,
            RuntimeDialogueLayouts.InfoLayout.StructSize);

        uint? conversationDataPointer = null;
        long? conversationDataOffset = null;
        byte[]? conversationDataBytes = null;

        if (runtimeStructBytes is { Length: >= 76 })
        {
            var pointer =
                BinaryUtils.ReadUInt32BE(runtimeStructBytes, RuntimeDialogueLayouts.InfoConversationDataPtrOffset);
            if (pointer != 0)
            {
                conversationDataPointer = pointer;
                conversationDataOffset = Context.MinidumpInfo?.VirtualAddressToFileOffset(pointer);
                conversationDataBytes = includeHex ? ReadBytes(conversationDataOffset, 24) : null;
            }
        }

        return new DialogueInfoProvenanceReport
        {
            Dialogue = dialogue,
            TesFileSegments = TesFileSegments,
            ResultScriptRecovery = DialogueTesFileScriptRecovery.TryRecover(
                Context,
                TesFileSegments,
                dialogue.TesFileOffset,
                dialogue.FormId,
                dialogue.EditorId,
                includeHex),
            RuntimeStructBytes = runtimeStructBytes,
            ConversationDataPointer = conversationDataPointer,
            ConversationDataOffset = conversationDataOffset,
            ConversationDataBytes = conversationDataBytes
        };
    }

    internal DialogTopicProvenanceReport InspectTopic(DialogTopicRecord topic, bool includeHex = false)
    {
        var runtimeStructBytes = ReadBytes(
            topic.RuntimeStructOffset > 0 ? topic.RuntimeStructOffset : null,
            RuntimeDialogueLayouts.DialStructSize);
        uint? stringPointer = null;
        ushort? stringLength = null;
        long? stringOffset = null;
        byte[]? stringBytes = null;
        var decodedText = topic.FullName;

        if (runtimeStructBytes is { Length: >= 50 })
        {
            stringPointer = BinaryUtils.ReadUInt32BE(runtimeStructBytes, RuntimeDialogueLayouts.DialFullNameOffset);
            stringLength = BinaryUtils.ReadUInt16BE(runtimeStructBytes, RuntimeDialogueLayouts.DialFullNameOffset + 4);

            if (stringPointer is > 0 && stringLength is > 0)
            {
                stringOffset = Context.MinidumpInfo?.VirtualAddressToFileOffset(stringPointer.Value);
                stringBytes = ReadBytes(stringOffset, stringLength.Value);
                if (stringBytes is { Length: > 0 })
                {
                    decodedText = EsmStringUtils.ValidateAndDecodeAscii(stringBytes, stringBytes.Length) ?? decodedText;
                }
            }
        }

        return new DialogTopicProvenanceReport
        {
            Topic = topic,
            RuntimeStructBytes = runtimeStructBytes,
            StringPointer = stringPointer,
            StringLength = stringLength,
            StringOffset = stringOffset,
            StringBytes = stringBytes,
            DecodedText = decodedText
        };
    }

    private byte[]? ReadBytes(long? fileOffset, int count)
    {
        if (fileOffset is not >= 0 || Context.Accessor == null || fileOffset.Value + count > Context.FileSize)
        {
            return null;
        }

        var bytes = new byte[count];
        try
        {
            Context.Accessor.ReadArray(fileOffset.Value, bytes, 0, count);
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
