using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     Strict, marker-aware decoder for the bounded PNDT fields whose layouts are proven. Unknown
///     subrecords remain outside the projection, but known signatures cannot cross the BDST/BDED
///     boundary or use an unproven width without invalidating the complete record.
/// </summary>
internal static class StarfieldPlanetDataDecoder
{
    private const int SubrecordHeaderSize = 6;
    private const int MasterTupleSize = 20;
    private const int OverrideTupleSize = 21;

    private enum BodyState
    {
        BeforeBody,
        InBody,
        AfterBody
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> data,
        bool isBigEndian,
        out StarfieldPlanetDataRecord record,
        out string? error)
    {
        string? editorId = null;

        bool Fail(
            string detail,
            out StarfieldPlanetDataRecord failedRecord,
            out string? failedError)
        {
            failedError = detail;
            failedRecord = new StarfieldPlanetDataRecord
            {
                EditorId = editorId,
                DecodeFailure = detail
            };
            return false;
        }

        if (isBigEndian)
        {
            return Fail(
                "Starfield PNDT fields are supported only in little-endian records.",
                out record,
                out error);
        }

        var state = BodyState.BeforeBody;
        List<StarfieldPlanetWorldspaceEntry>? masterWorldspaces = null;
        List<StarfieldPlanetWorldspaceDelta>? worldspaceOverrides = null;
        uint? topLevelGnamRawBits = null;
        byte? bodyCnamRawValue = null;
        (uint SystemId, uint ParentPlanetId, uint PlanetId)? bodyIdentifiers = null;
        StarfieldPlanetAtmosphereData? atmosphere = null;

        var offset = 0;
        uint? pendingExtendedSize = null;
        while (offset < data.Length)
        {
            if (data.Length - offset < SubrecordHeaderSize)
            {
                return Fail(
                    $"PNDT has a truncated subrecord header at byte {offset}.",
                    out record,
                    out error);
            }

            if (!TryReadSignature(data.Slice(offset, 4), out var signature))
            {
                return Fail(
                    $"PNDT has a non-ASCII subrecord signature at byte {offset}.",
                    out record,
                    out error);
            }

            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 4, 2));
            offset += SubrecordHeaderSize;

            if (signature == "XXXX")
            {
                if (pendingExtendedSize.HasValue)
                {
                    return Fail("PNDT has consecutive XXXX size markers.", out record, out error);
                }

                if (shortSize != sizeof(uint) || data.Length - offset < sizeof(uint))
                {
                    return Fail("PNDT has a malformed XXXX size marker.", out record, out error);
                }

                pendingExtendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    data.Slice(offset, sizeof(uint)));
                offset += sizeof(uint);
                continue;
            }

            if (pendingExtendedSize.HasValue && shortSize != 0)
            {
                return Fail(
                    $"PNDT XXXX target '{signature}' has a nonzero short length.",
                    out record,
                    out error);
            }

            var payloadSize = pendingExtendedSize ?? shortSize;
            pendingExtendedSize = null;
            if (payloadSize > int.MaxValue || payloadSize > (uint)(data.Length - offset))
            {
                return Fail(
                    $"PNDT subrecord '{signature}' overruns the record body.",
                    out record,
                    out error);
            }

            var payload = data.Slice(offset, (int)payloadSize);
            offset += (int)payloadSize;

            switch (signature)
            {
                case "EDID":
                    if (state != BodyState.BeforeBody)
                    {
                        return Fail(
                            "PNDT EDID is valid only before BDST.",
                            out record,
                            out error);
                    }

                    if (editorId is not null)
                    {
                        return Fail("PNDT has a duplicate EDID.", out record, out error);
                    }

                    if (!TryReadEditorId(payload, out editorId))
                    {
                        return Fail(
                            "PNDT EDID must be a non-empty NUL-terminated ASCII identifier.",
                            out record,
                            out error);
                    }

                    break;

                case "BDST":
                    if (payload.Length != 0)
                    {
                        return Fail("PNDT BDST marker must have zero length.", out record, out error);
                    }

                    if (state != BodyState.BeforeBody)
                    {
                        return Fail("PNDT has a nested or duplicate BDST marker.", out record, out error);
                    }

                    state = BodyState.InBody;
                    break;

                case "BDED":
                    if (payload.Length != 0)
                    {
                        return Fail("PNDT BDED marker must have zero length.", out record, out error);
                    }

                    if (state != BodyState.InBody)
                    {
                        return Fail("PNDT has an unmatched or duplicate BDED marker.", out record, out error);
                    }

                    state = BodyState.AfterBody;
                    break;

                case "CNAM":
                    if (state == BodyState.InBody)
                    {
                        if (payload.Length != 1 || bodyCnamRawValue.HasValue)
                        {
                            return Fail(
                                "PNDT body CNAM must occur once with length 1.",
                                out record,
                                out error);
                        }

                        bodyCnamRawValue = payload[0];
                        break;
                    }

                    if (state != BodyState.BeforeBody)
                    {
                        return Fail(
                            "PNDT top-level CNAM is valid only before BDST.",
                            out record,
                            out error);
                    }

                    if (masterWorldspaces is not null || worldspaceOverrides is not null)
                    {
                        return Fail(
                            "PNDT has duplicate or mixed top-level CNAM/EOVR payloads.",
                            out record,
                            out error);
                    }

                    if (!TryReadMasterWorldspaces(payload, out masterWorldspaces, out var masterError))
                    {
                        return Fail(
                            masterError ?? "PNDT top-level CNAM decoding failed.",
                            out record,
                            out error);
                    }

                    break;

                case "EOVR":
                    if (state == BodyState.InBody)
                    {
                        return Fail("PNDT EOVR is not valid inside BDST/BDED.", out record, out error);
                    }

                    if (state != BodyState.BeforeBody)
                    {
                        return Fail(
                            "PNDT top-level EOVR is valid only before BDST.",
                            out record,
                            out error);
                    }

                    if (masterWorldspaces is not null || worldspaceOverrides is not null)
                    {
                        return Fail(
                            "PNDT has duplicate or mixed top-level CNAM/EOVR payloads.",
                            out record,
                            out error);
                    }

                    if (!TryReadWorldspaceOverrides(payload, out worldspaceOverrides, out var overrideError))
                    {
                        return Fail(
                            overrideError ?? "PNDT top-level EOVR decoding failed.",
                            out record,
                            out error);
                    }

                    break;

                case "GNAM":
                    if (state == BodyState.InBody)
                    {
                        if (payload.Length != 3 * sizeof(uint) || bodyIdentifiers.HasValue)
                        {
                            return Fail(
                                "PNDT body GNAM must occur once with length 12.",
                                out record,
                                out error);
                        }

                        bodyIdentifiers = (
                            BinaryPrimitives.ReadUInt32LittleEndian(payload),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]));
                        break;
                    }

                    if (state != BodyState.BeforeBody)
                    {
                        return Fail(
                            "PNDT top-level GNAM is valid only before BDST.",
                            out record,
                            out error);
                    }

                    if (payload.Length != sizeof(uint) || topLevelGnamRawBits.HasValue)
                    {
                        return Fail(
                            "PNDT top-level GNAM must occur at most once with length 4.",
                            out record,
                            out error);
                    }

                    topLevelGnamRawBits = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                    break;

                case "INAM":
                    if (state != BodyState.InBody)
                    {
                        return Fail("PNDT INAM is valid only inside BDST/BDED.", out record, out error);
                    }

                    if (payload.Length != sizeof(uint) + (3 * sizeof(float)) || atmosphere is not null)
                    {
                        return Fail(
                            "PNDT body INAM must occur once with length 16.",
                            out record,
                            out error);
                    }

                    var unknownFloat0 = BinaryPrimitives.ReadSingleLittleEndian(payload[4..]);
                    var unknownFloat1 = BinaryPrimitives.ReadSingleLittleEndian(payload[8..]);
                    var unknownFloat2 = BinaryPrimitives.ReadSingleLittleEndian(payload[12..]);
                    if (!float.IsFinite(unknownFloat0) ||
                        !float.IsFinite(unknownFloat1) ||
                        !float.IsFinite(unknownFloat2))
                    {
                        return Fail(
                            "PNDT body INAM contains a non-finite float.",
                            out record,
                            out error);
                    }

                    atmosphere = new StarfieldPlanetAtmosphereData(
                        BinaryPrimitives.ReadUInt32LittleEndian(payload),
                        unknownFloat0,
                        unknownFloat1,
                        unknownFloat2);
                    break;
            }
        }

        if (pendingExtendedSize.HasValue)
        {
            return Fail("PNDT ends with an unresolved XXXX size marker.", out record, out error);
        }

        if (state == BodyState.BeforeBody)
        {
            return Fail("PNDT is missing its BDST/BDED body.", out record, out error);
        }

        if (state == BodyState.InBody)
        {
            return Fail("PNDT body is missing its BDED marker.", out record, out error);
        }

        if (masterWorldspaces is null && worldspaceOverrides is null)
        {
            return Fail(
                "PNDT is missing its top-level CNAM or EOVR payload.",
                out record,
                out error);
        }

        if (!bodyCnamRawValue.HasValue || !bodyIdentifiers.HasValue || atmosphere is null)
        {
            return Fail(
                "PNDT body requires one CNAM(1), GNAM(12), and INAM(16).",
                out record,
                out error);
        }

        var identifiers = bodyIdentifiers.Value;
        record = new StarfieldPlanetDataRecord
        {
            EditorId = editorId,
            PayloadKind = masterWorldspaces is not null
                ? StarfieldPlanetDataPayloadKind.Master
                : StarfieldPlanetDataPayloadKind.Override,
            MasterWorldspaces = Array.AsReadOnly(
                masterWorldspaces?.ToArray() ?? Array.Empty<StarfieldPlanetWorldspaceEntry>()),
            WorldspaceOverrides = Array.AsReadOnly(
                worldspaceOverrides?.ToArray() ?? Array.Empty<StarfieldPlanetWorldspaceDelta>()),
            TopLevelGnamRawBits = topLevelGnamRawBits,
            Body = new StarfieldPlanetBodyData(
                bodyCnamRawValue.Value,
                identifiers.SystemId,
                identifiers.ParentPlanetId,
                identifiers.PlanetId,
                atmosphere)
        };
        error = null;
        return true;
    }

    private static bool TryReadMasterWorldspaces(
        ReadOnlySpan<byte> payload,
        out List<StarfieldPlanetWorldspaceEntry>? entries,
        out string? error)
    {
        entries = null;
        error = null;
        if (payload.Length % MasterTupleSize != 0)
        {
            error = "PNDT top-level CNAM length is not a multiple of 20.";
            return false;
        }

        entries = new List<StarfieldPlanetWorldspaceEntry>(payload.Length / MasterTupleSize);
        for (var offset = 0; offset < payload.Length; offset += MasterTupleSize)
        {
            entries.Add(ReadWorldspaceEntry(payload.Slice(offset, MasterTupleSize)));
        }

        return true;
    }

    private static bool TryReadWorldspaceOverrides(
        ReadOnlySpan<byte> payload,
        out List<StarfieldPlanetWorldspaceDelta>? deltas,
        out string? error)
    {
        deltas = null;
        error = null;
        if (payload.Length % OverrideTupleSize != 0)
        {
            error = "PNDT top-level EOVR length is not a multiple of 21.";
            return false;
        }

        deltas = new List<StarfieldPlanetWorldspaceDelta>(payload.Length / OverrideTupleSize);
        for (var offset = 0; offset < payload.Length; offset += OverrideTupleSize)
        {
            var operationByte = payload[offset + MasterTupleSize];
            if (operationByte != (byte)StarfieldPlanetWorldspaceOperation.Removed &&
                operationByte != (byte)StarfieldPlanetWorldspaceOperation.Added)
            {
                error =
                    $"PNDT EOVR entry {offset / OverrideTupleSize} has unknown operation {operationByte}.";
                deltas = null;
                return false;
            }

            deltas.Add(new StarfieldPlanetWorldspaceDelta(
                ReadWorldspaceEntry(payload.Slice(offset, MasterTupleSize)),
                (StarfieldPlanetWorldspaceOperation)operationByte));
        }

        return true;
    }

    private static StarfieldPlanetWorldspaceEntry ReadWorldspaceEntry(ReadOnlySpan<byte> payload)
    {
        return new StarfieldPlanetWorldspaceEntry(
            BinaryPrimitives.ReadInt64LittleEndian(payload),
            BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[16..]));
    }

    private static bool TryReadSignature(ReadOnlySpan<byte> data, out string signature)
    {
        foreach (var value in data)
        {
            var isUppercaseLetter = value >= (byte)'A' && value <= (byte)'Z';
            var isDigit = value >= (byte)'0' && value <= (byte)'9';
            if (!isUppercaseLetter && !isDigit && value != (byte)'_')
            {
                signature = string.Empty;
                return false;
            }
        }

        signature = Encoding.ASCII.GetString(data);
        return true;
    }

    private static bool TryReadEditorId(ReadOnlySpan<byte> payload, out string? editorId)
    {
        editorId = null;
        if (payload.Length < 2 || payload[^1] != 0)
        {
            return false;
        }

        var text = payload[..^1];
        foreach (var value in text)
        {
            if (value is 0 or > 0x7F)
            {
                return false;
            }
        }

        editorId = Encoding.ASCII.GetString(text);
        return !string.IsNullOrWhiteSpace(editorId);
    }

}
