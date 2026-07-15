using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

internal sealed record DialogueInfoOverlayResult(
    byte[] RecordBytes,
    IReadOnlyList<DialogueResponse> EmittedResponses,
    IReadOnlyList<byte> SourceResponseNumbers,
    int ReplacedTextCount);

/// <summary>
///     Writes a retail INFO overlay by copying the master's ordered subrecord stream and
///     replacing only matched NAM1 payloads. This intentionally bypasses InfoEncoder: the
///     generic merge path cannot preserve result-script blocks, condition ordering, and
///     response-local TRDT/NAM2/NAM3 data byte-for-byte.
/// </summary>
internal static class DialogueInfoOverlayWriter
{
    private const uint CompressedFlag = 0x00040000u;

    public static HashSet<byte> GetMasterResponseNumbers(ParsedMainRecord masterInfo)
    {
        var responseNumbers = new HashSet<byte>();
        var ordinal = 0;
        foreach (var subrecord in masterInfo.Subrecords)
        {
            if (subrecord.Signature != "TRDT")
            {
                continue;
            }

            ordinal++;
            responseNumbers.Add(GetResponseNumber(subrecord.Data, ordinal));
        }

        return responseNumbers;
    }

    public static DialogueInfoOverlayResult Build(
        ParsedMainRecord masterInfo,
        DialogueRecord prototypeInfo,
        IReadOnlySet<string>? retainMasterSubrecords = null)
    {
        var prototypeByResponse = IndexPrototypeResponses(prototypeInfo.Responses);
        var emittedResponses = new List<DialogueResponse>();
        var sourceResponseNumbers = new List<byte>();
        var boundResponseNumbers = new HashSet<byte>();
        var replaced = 0;
        var ordinal = 0;
        byte currentResponseNumber = 0;

        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.Latin1, leaveOpen: true))
        {
            foreach (var subrecord in masterInfo.Subrecords)
            {
                if (subrecord.Signature == "TRDT")
                {
                    ordinal++;
                    currentResponseNumber = GetResponseNumber(subrecord.Data, ordinal);
                    SubrecordEncoder.WriteSubrecord(writer, subrecord.Signature, subrecord.Data);
                    continue;
                }

                if (subrecord.Signature == "NAM1"
                    && currentResponseNumber != 0
                    && prototypeByResponse.TryGetValue(currentResponseNumber, out var prototypeResponse))
                {
                    var replacementText = IsMeaningfulText(prototypeResponse.Text)
                        ? prototypeResponse.Text!
                        : subrecord.DataAsString;
                    if (IsMeaningfulText(prototypeResponse.Text)
                        && retainMasterSubrecords?.Contains("NAM1") != true)
                    {
                        SubrecordEncoder.WriteStringSubrecord(writer, "NAM1", replacementText);
                        replaced++;
                    }
                    else
                    {
                        SubrecordEncoder.WriteSubrecord(writer, subrecord.Signature, subrecord.Data);
                    }

                    if (boundResponseNumbers.Add(currentResponseNumber))
                    {
                        emittedResponses.Add(prototypeResponse with
                        {
                            Text = replacementText,
                            ResponseNumber = currentResponseNumber
                        });
                        sourceResponseNumbers.Add(currentResponseNumber);
                    }

                    continue;
                }

                SubrecordEncoder.WriteSubrecord(writer, subrecord.Signature, subrecord.Data);
            }
        }

        var uncompressedBodyBytes = body.ToArray();
        var bodyBytes = (masterInfo.Header.Flags & CompressedFlag) != 0
            ? EsmRecordCompression.CompressConvertedRecordData(uncompressedBodyBytes)
            : uncompressedBodyBytes;
        using var recordStream = new MemoryStream();
        var header = masterInfo.Header with
        {
            DataSize = (uint)bodyBytes.Length
        };
        RecordHeaderProcessor.WriteRecordHeader(recordStream, header);
        recordStream.Write(bodyBytes);

        return new DialogueInfoOverlayResult(
            recordStream.ToArray(), emittedResponses, sourceResponseNumbers, replaced);
    }

    internal static Dictionary<byte, DialogueResponse> IndexPrototypeResponses(
        IReadOnlyList<DialogueResponse> responses)
    {
        var result = new Dictionary<byte, DialogueResponse>();
        for (var index = 0; index < responses.Count; index++)
        {
            var response = responses[index];
            var responseNumber = response.ResponseNumber > 0
                ? response.ResponseNumber
                : checked((byte)(index + 1));
            if (!result.TryGetValue(responseNumber, out var existing)
                || (!IsMeaningfulText(existing.Text) && IsMeaningfulText(response.Text)))
            {
                result[responseNumber] = response with { ResponseNumber = responseNumber };
            }
        }

        return result;
    }

    internal static bool IsMeaningfulText(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && !string.Equals(text, DialogueTextBackfill.PlaceholderText, StringComparison.Ordinal);

    private static byte GetResponseNumber(byte[] trdt, int ordinal)
    {
        var stored = trdt.Length > 12 ? trdt[12] : (byte)0;
        return stored != 0 ? stored : checked((byte)ordinal);
    }
}
