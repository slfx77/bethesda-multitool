using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Records;

/// <summary>Endian-aware decoders shared by both ESM record paths.</summary>
internal static class BendableSplineDataReader
{
    private const int DefinitionDataSize = 32;
    private const int RequiredPlacementDataSize = 20;

    internal static BendableSplineDefinitionData? ReadDefinition(
        ReadOnlySpan<byte> data,
        bool bigEndian)
    {
        // BNDS.DNAM is fixed-size in both the FO4 and FO76 definitions. Rejecting other sizes
        // prevents a same-signature future layout from being silently interpreted as this one.
        if (data.Length != DefinitionDataSize)
        {
            return null;
        }

        return new BendableSplineDefinitionData
        {
            DefaultTileCount = ReadFloat(data, 0, bigEndian),
            DefaultSliceCount = ReadUInt16(data, 4, bigEndian),
            TilesRelativeToLengthRaw = ReadUInt16(data, 6, bigEndian),
            DefaultColor = new Vector4(
                ReadFloat(data, 8, bigEndian),
                ReadFloat(data, 12, bigEndian),
                ReadFloat(data, 16, bigEndian),
                ReadFloat(data, 20, bigEndian)),
            WindSensibility = ReadFloat(data, 24, bigEndian),
            WindFlexibility = ReadFloat(data, 28, bigEndian)
        };
    }

    internal static BendableSplinePlacementData? ReadPlacement(
        ReadOnlySpan<byte> data,
        bool bigEndian)
    {
        // XBSD's first five floats are the common required prefix. FO4 may end there; FO76 requires
        // the wind byte + three padding bytes, and pre-form-version-131 records can carry two more
        // floats. Preserve everything after the common prefix because this shared reader does not
        // receive the game/form version needed to label those bytes safely.
        if (data.Length < RequiredPlacementDataSize)
        {
            return null;
        }

        return new BendableSplinePlacementData
        {
            Slack = ReadFloat(data, 0, bigEndian),
            Thickness = ReadFloat(data, 4, bigEndian),
            HalfExtents = new Vector3(
                ReadFloat(data, 8, bigEndian),
                ReadFloat(data, 12, bigEndian),
                ReadFloat(data, 16, bigEndian)),
            WindDetachedEndRaw = data.Length >= 21 ? data[20] : null,
            TrailingData = data.Length > 21 ? data[21..].ToArray() : []
        };
    }

    private static float ReadFloat(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data[offset..])
            : BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
        bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
}
