using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

internal static class StarfieldPlanetDataTestData
{
    internal static byte[] ValidMasterData(
        IReadOnlyList<StarfieldPlanetWorldspaceEntry> entries,
        uint topLevelGnamRawBits = 0x3FC00000,
        byte bodyCnamRawValue = 7,
        uint systemId = 10,
        uint parentPlanetId = 20,
        uint planetId = 30,
        uint atmosphereFormId = 0x00123456,
        float unknownFloat0 = 1.25f,
        float unknownFloat1 = -2.5f,
        float unknownFloat2 = 0f)
    {
        return Concat(
            Subrecord("CNAM", MasterPayload(entries)),
            Subrecord("GNAM", U32(topLevelGnamRawBits)),
            ValidBody(
                bodyCnamRawValue,
                systemId,
                parentPlanetId,
                planetId,
                atmosphereFormId,
                unknownFloat0,
                unknownFloat1,
                unknownFloat2));
    }

    internal static byte[] ValidOverrideData(
        IReadOnlyList<StarfieldPlanetWorldspaceDelta> deltas,
        byte bodyCnamRawValue = 7,
        uint systemId = 10,
        uint parentPlanetId = 20,
        uint planetId = 30,
        uint atmosphereFormId = 0x00123456,
        float unknownFloat0 = 1.25f,
        float unknownFloat1 = -2.5f,
        float unknownFloat2 = 0f)
    {
        return Concat(
            Subrecord("EOVR", OverridePayload(deltas)),
            ValidBody(
                bodyCnamRawValue,
                systemId,
                parentPlanetId,
                planetId,
                atmosphereFormId,
                unknownFloat0,
                unknownFloat1,
                unknownFloat2));
    }

    internal static byte[] ValidBody(
        byte bodyCnamRawValue = 7,
        uint systemId = 10,
        uint parentPlanetId = 20,
        uint planetId = 30,
        uint atmosphereFormId = 0x00123456,
        float unknownFloat0 = 1.25f,
        float unknownFloat1 = -2.5f,
        float unknownFloat2 = 0f)
    {
        return MarkerBody(
            ("CNAM", [bodyCnamRawValue]),
            ("GNAM", Identifiers(systemId, parentPlanetId, planetId)),
            ("INAM", Atmosphere(
                atmosphereFormId,
                unknownFloat0,
                unknownFloat1,
                unknownFloat2)));
    }

    internal static byte[] MarkerBody(params (string Signature, byte[] Payload)[] fields)
    {
        var parts = new List<byte[]> { Subrecord("BDST", []) };
        parts.AddRange(fields.Select(field => Subrecord(field.Signature, field.Payload)));
        parts.Add(Subrecord("BDED", []));
        return Concat([.. parts]);
    }

    internal static byte[] MasterPayload(IReadOnlyList<StarfieldPlanetWorldspaceEntry> entries)
    {
        var payload = new byte[checked(entries.Count * 20)];
        for (var index = 0; index < entries.Count; index++)
        {
            var offset = index * 20;
            BinaryPrimitives.WriteInt64LittleEndian(
                payload.AsSpan(offset), entries[index].LatitudeRawBits);
            BinaryPrimitives.WriteInt64LittleEndian(
                payload.AsSpan(offset + 8), entries[index].LongitudeRawBits);
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(offset + 16), entries[index].WorldspaceFormId);
        }

        return payload;
    }

    internal static byte[] OverridePayload(IReadOnlyList<StarfieldPlanetWorldspaceDelta> deltas)
    {
        var payload = new byte[checked(deltas.Count * 21)];
        for (var index = 0; index < deltas.Count; index++)
        {
            var offset = index * 21;
            var entry = deltas[index].Entry;
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), entry.LatitudeRawBits);
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset + 8), entry.LongitudeRawBits);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 16), entry.WorldspaceFormId);
            payload[offset + 20] = (byte)deltas[index].Operation;
        }

        return payload;
    }

    internal static byte[] Identifiers(uint systemId, uint parentPlanetId, uint planetId) =>
        Concat(U32(systemId), U32(parentPlanetId), U32(planetId));

    internal static byte[] Atmosphere(
        uint atmosphereFormId,
        float unknownFloat0,
        float unknownFloat1,
        float unknownFloat2) =>
        Concat(
            U32(atmosphereFormId),
            F32(unknownFloat0),
            F32(unknownFloat1),
            F32(unknownFloat2));

    internal static byte[] Subrecord(string signature, byte[] payload)
    {
        if (signature.Length != 4) throw new ArgumentException("Signature must be four characters.", nameof(signature));
        if (payload.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payload));

        var bytes = new byte[6 + payload.Length];
        Encoding.ASCII.GetBytes(signature, bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)payload.Length));
        payload.CopyTo(bytes, 6);
        return bytes;
    }

    internal static byte[] ExtendedSubrecord(string signature, byte[] payload)
    {
        var targetHeader = new byte[6];
        Encoding.ASCII.GetBytes(signature, targetHeader);
        return Concat(
            Subrecord("XXXX", U32(checked((uint)payload.Length))),
            targetHeader,
            payload);
    }

    internal static byte[] Header(string signature, ushort declaredLength)
    {
        var bytes = new byte[6];
        Encoding.ASCII.GetBytes(signature, bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), declaredLength);
        return bytes;
    }

    internal static byte[] U32(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    internal static byte[] F32(float value)
    {
        var bytes = new byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }

    internal static byte[] Concat(params byte[][] parts)
    {
        var size = parts.Sum(part => part.Length);
        var result = new byte[size];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
