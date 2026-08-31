using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Strict decoder for the bounded STDT routing subset. Unknown, correctly framed subrecords
///     remain opaque so the large retail star-presentation payload is not misrepresented as typed
///     data. Every established field is single-valued and must use its proven width.
/// </summary>
internal static class StarfieldStarDataDecoder
{
    private const int SubrecordHeaderSize = 6;

    internal static bool TryDecode(
        ReadOnlySpan<byte> data,
        bool isBigEndian,
        out StarfieldStarDataRecord record,
        out string? error)
    {
        if (isBigEndian)
        {
            return Fail(
                "Starfield STDT routing fields are supported only in little-endian records.",
                isBigEndian,
                out record,
                out error);
        }

        string? editorId = null;
        var hasEditorId = false;
        uint? systemId = null;
        uint? binaryStarFormId = null;
        uint? sunPresetFormId = null;
        uint? timeOfDayDataFormId = null;

        var offset = 0;
        uint? pendingExtendedSize = null;
        while (offset < data.Length)
        {
            if (data.Length - offset < SubrecordHeaderSize)
            {
                return Fail(
                    $"STDT has a truncated subrecord header at byte {offset}.",
                    isBigEndian,
                    out record,
                    out error);
            }

            if (!TryReadSignature(data.Slice(offset, sizeof(uint)), out var signature))
            {
                return Fail(
                    $"STDT has a non-ASCII subrecord signature at byte {offset}.",
                    isBigEndian,
                    out record,
                    out error);
            }

            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                data.Slice(offset + sizeof(uint), sizeof(ushort)));
            offset += SubrecordHeaderSize;

            if (signature == "XXXX")
            {
                if (pendingExtendedSize.HasValue)
                {
                    return Fail(
                        "STDT has consecutive XXXX size markers.",
                        isBigEndian,
                        out record,
                        out error);
                }

                if (shortSize != sizeof(uint) || data.Length - offset < sizeof(uint))
                {
                    return Fail(
                        "STDT has a malformed XXXX size marker.",
                        isBigEndian,
                        out record,
                        out error);
                }

                pendingExtendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    data.Slice(offset, sizeof(uint)));
                offset += sizeof(uint);
                continue;
            }

            if (pendingExtendedSize.HasValue && shortSize != 0)
            {
                return Fail(
                    $"STDT XXXX target '{signature}' has a nonzero short length.",
                    isBigEndian,
                    out record,
                    out error);
            }

            var payloadSize = pendingExtendedSize ?? shortSize;
            pendingExtendedSize = null;
            if (payloadSize > int.MaxValue || payloadSize > (uint)(data.Length - offset))
            {
                return Fail(
                    $"STDT subrecord '{signature}' overruns the record body.",
                    isBigEndian,
                    out record,
                    out error);
            }

            var payload = data.Slice(offset, (int)payloadSize);
            offset += (int)payloadSize;

            switch (signature)
            {
                case "EDID":
                    if (hasEditorId)
                    {
                        return Fail(
                            "STDT has a duplicate EDID field.",
                            isBigEndian,
                            out record,
                            out error);
                    }

                    if (payload.Length < 2 || payload[^1] != 0 || payload[..^1].IndexOf((byte)0) >= 0)
                    {
                        return Fail(
                            "STDT EDID must be one non-empty null-terminated string.",
                            isBigEndian,
                            out record,
                            out error);
                    }

                    editorId = EsmStringUtils.DecodeGameText(payload[..^1]);
                    if (string.IsNullOrWhiteSpace(editorId))
                    {
                        return Fail(
                            "STDT EDID must not be empty or whitespace.",
                            isBigEndian,
                            out record,
                            out error);
                    }

                    hasEditorId = true;
                    break;

                case "DNAM":
                    if (!TryReadSingleUInt32(signature, payload, ref systemId, out var dnamError))
                    {
                        return Fail(dnamError!, isBigEndian, out record, out error);
                    }

                    break;

                case "SNAM":
                    if (!TryReadSingleUInt32(
                            signature, payload, ref binaryStarFormId, out var snamError))
                    {
                        return Fail(snamError!, isBigEndian, out record, out error);
                    }

                    break;

                case "PNAM":
                    if (!TryReadSingleUInt32(
                            signature, payload, ref sunPresetFormId, out var pnamError))
                    {
                        return Fail(pnamError!, isBigEndian, out record, out error);
                    }

                    break;

                case "HNAM":
                    if (!TryReadSingleUInt32(
                            signature, payload, ref timeOfDayDataFormId, out var hnamError))
                    {
                        return Fail(hnamError!, isBigEndian, out record, out error);
                    }

                    break;
            }
        }

        if (pendingExtendedSize.HasValue)
        {
            return Fail(
                "STDT ends with an unresolved XXXX size marker.",
                isBigEndian,
                out record,
                out error);
        }

        record = new StarfieldStarDataRecord
        {
            EditorId = editorId,
            Routing = new StarfieldStarDataRouting
            {
                SystemId = systemId,
                BinaryStarFormId = binaryStarFormId,
                SunPresetFormId = sunPresetFormId,
                TimeOfDayDataFormId = timeOfDayDataFormId
            },
            IsBigEndian = false
        };
        error = null;
        return true;
    }

    private static bool TryReadSingleUInt32(
        string signature,
        ReadOnlySpan<byte> payload,
        ref uint? destination,
        out string? error)
    {
        if (destination.HasValue)
        {
            error = $"STDT has a duplicate {signature} field.";
            return false;
        }

        if (payload.Length != sizeof(uint))
        {
            error = $"STDT {signature} must be exactly four bytes; found {payload.Length}.";
            return false;
        }

        destination = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        error = null;
        return true;
    }

    private static bool TryReadSignature(ReadOnlySpan<byte> data, out string signature)
    {
        foreach (var value in data)
        {
            var isUppercaseLetter = value is >= (byte)'A' and <= (byte)'Z';
            var isDigit = value is >= (byte)'0' and <= (byte)'9';
            if (!isUppercaseLetter && !isDigit && value != (byte)'_')
            {
                signature = string.Empty;
                return false;
            }
        }

        signature = Encoding.ASCII.GetString(data);
        return true;
    }

    private static bool Fail(
        string detail,
        bool isBigEndian,
        out StarfieldStarDataRecord record,
        out string? error)
    {
        error = detail;
        record = new StarfieldStarDataRecord
        {
            DecodeFailure = detail,
            IsBigEndian = isBigEndian
        };
        return false;
    }
}
