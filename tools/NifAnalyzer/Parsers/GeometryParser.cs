using BethesdaMultitool.Core.Formats.Nif.Parser;
using NifAnalyzer.Models;
using static BethesdaMultitool.Core.Formats.Nif.Conversion.NifEndianUtils;
using static BethesdaMultitool.Core.Utils.BinaryUtils;

namespace NifAnalyzer.Parsers;

/// <summary>
///     Parses standalone NiTriShapeData and NiTriStripsData blocks in nif.xml field order. Geometry
///     layout is selected by the NIF binary version; the Bethesda stream version is used only for
///     fields whose schema predicates explicitly include it.
/// </summary>
internal static class GeometryParser
{
    internal static bool IsSupportedBlockType(string blockType) =>
        blockType is "NiTriShapeData" or "NiTriStripsData";

    internal static bool TryParse(
        ReadOnlySpan<byte> data,
        bool bigEndian,
        uint binaryVersion,
        int bsVersion,
        string blockType,
        out GeometryInfo info,
        out string error)
    {
        try
        {
            info = Parse(data, bigEndian, binaryVersion, bsVersion, blockType);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException
                                          or ArgumentOutOfRangeException)
        {
            info = new GeometryInfo();
            error = exception.Message;
            return false;
        }
    }

    public static GeometryInfo Parse(
        ReadOnlySpan<byte> data,
        bool bigEndian,
        uint binaryVersion,
        int bsVersion,
        string blockType)
    {
        if (!IsSupportedBlockType(blockType))
        {
            throw new InvalidDataException(
                $"Block type '{blockType}' is not standalone NiTriShapeData or NiTriStripsData geometry. " +
                "BSTriShape variants, NiTriShape nodes, and Havok triangle blocks use different layouts.");
        }

        var info = new GeometryInfo();
        var pos = 0;
        var modernGeometry = NifVersions.HasModernGeometryBase(binaryVersion);
        var legacyGeometry = NifVersions.IsLegacyNetImmerse(binaryVersion);
        if (!modernGeometry && !legacyGeometry)
        {
            throw new InvalidDataException(
                $"Unsupported NiGeometryData layout for binary version 0x{binaryVersion:X8}.");
        }

        // nif.xml's bool storage changes independently of the NiGeometryData base layout: it is a
        // uint only through 4.0.0.2, then a byte from 4.1.0.1 onward. Has UV has that same 4.0.0.2
        // upper bound, while the surrounding legacy Data Flags layout remains through 4.2.2.0.
        var usesWideGeometryBooleans = binaryVersion <= NifVersions.NetImmerse4002;
        var hasLegacyUvFlag = binaryVersion <= NifVersions.NetImmerse4002;

        // BSGeometryDataFlags exists only for Bethesda 20.2.0.7 streams. Other modern geometry uses
        // NiGeometryDataFlags, whose low six bits are a UV-set count rather than a one-bit Has UV flag.
        var usesBsDataFlags = binaryVersion == NifVersions.Gamebryo202007 && bsVersion > 0;
        info.UsesBsDataFlags = usesBsDataFlags;

        // NiGeometryData.Group ID appears at 10.1.0.114. Older Oblivion exporters and Morrowind begin
        // directly with Num Vertices even when a BS stream header is present.
        if (NifVersions.HasGeometryGroupId(binaryVersion))
        {
            EnsureAvailable(data, pos, 4, "Group ID");
            info.FieldOffsets["GroupId"] = pos;
            info.GroupId = ReadInt32(data, pos, bigEndian);
            pos += 4;
        }

        EnsureAvailable(data, pos, 2, "Num Vertices");
        info.FieldOffsets["NumVertices"] = pos;
        info.NumVertices = ReadUInt16(data, pos, bigEndian);
        pos += 2;

        // Keep/Compress Flags are keyed on NIF 10.1.0.0, not BSVersion. In particular, Oblivion's
        // 10.0.1.x geometry has the modern base without these two bytes.
        if (NifVersions.HasGeometryKeepFlags(binaryVersion))
        {
            EnsureAvailable(data, pos, 2, "Keep/Compress Flags");
            info.FieldOffsets["KeepFlags"] = pos;
            info.KeepFlags = data[pos++];
            info.FieldOffsets["CompressFlags"] = pos;
            info.CompressFlags = data[pos++];
        }

        info.HasVertices = ReadGeometryBoolean(data, ref pos, bigEndian, usesWideGeometryBooleans,
            info.FieldOffsets, "HasVertices");
        if (info.HasVertices != 0)
        {
            info.FieldOffsets["Vertices"] = pos;
            Skip(data, ref pos, info.NumVertices * 12, "Vertices");
        }

        // The modern Data/BS Data Flags precede normals. Morrowind's legacy Data Flags occur after
        // vertex colors and are handled below.
        if (modernGeometry)
        {
            EnsureAvailable(data, pos, 2, "Data Flags");
            info.FieldOffsets["DataFlags"] = pos;
            info.DataFlags = ReadUInt16(data, pos, bigEndian);
            pos += 2;

            // Material CRC: exactly Bethesda 20.2.0.7 and strictly newer than FO3/FNV BS 34.
            if (usesBsDataFlags && bsVersion > 34)
            {
                EnsureAvailable(data, pos, 4, "Material CRC");
                info.FieldOffsets["MaterialCrc"] = pos;
                info.MaterialCrc = ReadUInt32(data, pos, bigEndian);
                pos += 4;
            }
        }

        info.HasNormals = ReadGeometryBoolean(data, ref pos, bigEndian, usesWideGeometryBooleans,
            info.FieldOffsets, "HasNormals");
        if (info.HasNormals != 0)
        {
            info.FieldOffsets["Normals"] = pos;
            Skip(data, ref pos, info.NumVertices * 12, "Normals");

            // nif.xml order is Normals -> Tangents -> Bitangents -> Bounding Sphere. Keeping the
            // bounding sphere before the tangent arrays made the analyzer print tangent[0] as the
            // sphere center and shifted every following diagnostic offset by 16 bytes.
            if (binaryVersion >= NifVersions.Gamebryo10100 && (info.DataFlags & 0x1000) != 0)
            {
                info.FieldOffsets["Tangents"] = pos;
                Skip(data, ref pos, info.NumVertices * 12, "Tangents");
                info.FieldOffsets["Bitangents"] = pos;
                Skip(data, ref pos, info.NumVertices * 12, "Bitangents");
            }
        }

        ReadBoundingSphere(data, ref pos, bigEndian, info);

        info.HasVertexColors = ReadGeometryBoolean(data, ref pos, bigEndian, usesWideGeometryBooleans,
            info.FieldOffsets, "HasVertexColors");
        if (info.HasVertexColors != 0)
        {
            info.FieldOffsets["VertexColors"] = pos;
            Skip(data, ref pos, info.NumVertices * 16, "Vertex Colors");
        }

        if (modernGeometry)
        {
            info.NumUvSets = usesBsDataFlags
                ? (ushort)((info.DataFlags & 0x0001) != 0 ? 1 : 0)
                : (ushort)(info.DataFlags & 0x003F);
        }
        else
        {
            EnsureAvailable(data, pos, 2, "Legacy Data Flags");
            info.FieldOffsets["DataFlags"] = pos;
            info.DataFlags = ReadUInt16(data, pos, bigEndian);
            pos += 2;
            info.NumUvSets = (ushort)(info.DataFlags & 0x3F);

            // Has UV is a legacy compatibility field only through 4.0.0.2. The low-six-bit count is
            // authoritative for the UV array width.
            if (hasLegacyUvFlag)
            {
                info.HasUv = ReadGeometryBoolean(data, ref pos, bigEndian, usesWideGeometryBooleans,
                    info.FieldOffsets, "HasUv");
            }
        }

        if (info.NumUvSets > 0)
        {
            info.FieldOffsets["UVSets"] = pos;
            Skip(data, ref pos, info.NumVertices * info.NumUvSets * 8, "UV Sets");
        }

        if (modernGeometry)
        {
            EnsureAvailable(data, pos, 2, "Consistency Flags");
            info.FieldOffsets["ConsistencyFlags"] = pos;
            info.ConsistencyFlags = ReadUInt16(data, pos, bigEndian);
            pos += 2;
        }

        if (binaryVersion >= NifVersions.Gamebryo20004)
        {
            EnsureAvailable(data, pos, 4, "Additional Data");
            info.FieldOffsets["AdditionalData"] = pos;
            info.AdditionalData = ReadInt32(data, pos, bigEndian);
            pos += 4;
        }

        EnsureAvailable(data, pos, 2, "Num Triangles");
        info.FieldOffsets["NumTriangles"] = pos;
        info.NumTriangles = ReadUInt16(data, pos, bigEndian);
        pos += 2;

        if (blockType == "NiTriShapeData")
        {
            ParseTriShapeTail(data, ref pos, bigEndian, binaryVersion, info);
        }
        else
        {
            ParseTriStripsTail(data, ref pos, bigEndian, binaryVersion, info);
        }

        info.ParsedSize = pos;
        return info;
    }

    private static void ParseTriShapeTail(
        ReadOnlySpan<byte> data,
        ref int pos,
        bool bigEndian,
        uint binaryVersion,
        GeometryInfo info)
    {
        EnsureAvailable(data, pos, 4, "Num Triangle Points");
        info.FieldOffsets["NumTrianglePoints"] = pos;
        info.NumTrianglePoints = ReadUInt32(data, pos, bigEndian);
        pos += 4;

        if (NifVersions.HasShapeTriangleFlag(binaryVersion))
        {
            info.HasTriangles = ReadGeometryBoolean(data, ref pos, bigEndian, usesWideBoolean: false,
                info.FieldOffsets, "HasTriangles");
        }
        else
        {
            info.HasTriangles = 1;
        }

        info.TrianglesFieldOffset = pos;
        if (info.HasTriangles != 0)
        {
            info.FieldOffsets["Triangles"] = pos;
            Skip(data, ref pos, info.NumTriangles * 6, "Triangles");
        }

        // Num Match Groups is schema-required for every supported shape version. Historical converter
        // probes sometimes end immediately after the triangle list, so retain a partial diagnostic
        // instead of failing the whole command when only this trailing zero is absent.
        if (pos + 2 > data.Length)
        {
            info.ParseWarning =
                $"NiTriShapeData ends at 0x{pos:X} without the schema-required Num Match Groups field.";
            return;
        }

        var matchGroupsStart = pos;
        try
        {
            info.FieldOffsets["NumMatchGroups"] = pos;
            info.NumMatchGroups = ReadUInt16(data, pos, bigEndian);
            pos += 2;
            for (var group = 0; group < info.NumMatchGroups; group++)
            {
                EnsureAvailable(data, pos, 2, $"Match Group {group} vertex count");
                var count = ReadUInt16(data, pos, bigEndian);
                pos += 2;
                Skip(data, ref pos, count * 2, $"Match Group {group} indices");
            }

            // With Has Triangles clear, leftover bytes are the converter anomaly this command was
            // originally written to expose, not trustworthy match-group data. Rewind so the caller's
            // remaining-byte diagnostic includes the complete suspicious payload.
            if (info.HasTriangles == 0 && pos != data.Length)
            {
                info.FieldOffsets.Remove("NumMatchGroups");
                info.NumMatchGroups = 0;
                pos = matchGroupsStart;
                info.ParseWarning =
                    $"HasTriangles=0 leaves {data.Length - matchGroupsStart} unclassified bytes; " +
                    "they were not consumed as match groups.";
            }
        }
        catch (InvalidDataException) when (info.HasTriangles == 0)
        {
            info.FieldOffsets.Remove("NumMatchGroups");
            info.NumMatchGroups = 0;
            pos = matchGroupsStart;
            info.ParseWarning =
                $"HasTriangles=0 leaves {data.Length - matchGroupsStart} bytes that do not form valid match groups.";
        }
    }

    private static void ParseTriStripsTail(
        ReadOnlySpan<byte> data,
        ref int pos,
        bool bigEndian,
        uint binaryVersion,
        GeometryInfo info)
    {
        EnsureAvailable(data, pos, 2, "Num Strips");
        info.FieldOffsets["NumStrips"] = pos;
        info.NumStrips = ReadUInt16(data, pos, bigEndian);
        pos += 2;

        info.StripLengths = new ushort[info.NumStrips];
        info.FieldOffsets["StripLengths"] = pos;
        for (var i = 0; i < info.NumStrips; i++)
        {
            EnsureAvailable(data, pos, 2, $"Strip Length {i}");
            info.StripLengths[i] = ReadUInt16(data, pos, bigEndian);
            pos += 2;
        }

        if (NifVersions.HasStripPointsFlag(binaryVersion))
        {
            info.HasPoints = ReadGeometryBoolean(data, ref pos, bigEndian, usesWideBoolean: false,
                info.FieldOffsets, "HasPoints");
        }
        else
        {
            info.HasPoints = 1;
        }

        info.TrianglesFieldOffset = pos;
        if (info.HasPoints != 0)
        {
            info.FieldOffsets["Points"] = pos;
            long pointCount = 0;
            foreach (var length in info.StripLengths)
            {
                pointCount += length;
            }

            var pointBytes = pointCount * 2;
            if (pointBytes > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"NiTriStripsData declares {pointCount} strip points ({pointBytes} bytes), " +
                    "which exceeds the supported block size.");
            }

            Skip(data, ref pos, (int)pointBytes, "Strip Points");
        }
    }

    private static void ReadBoundingSphere(
        ReadOnlySpan<byte> data,
        ref int pos,
        bool bigEndian,
        GeometryInfo info)
    {
        EnsureAvailable(data, pos, 16, "Bounding Sphere");
        info.FieldOffsets["BoundingSphere"] = pos;
        info.BoundingCenterX = ReadFloat(data, pos, bigEndian);
        info.BoundingCenterY = ReadFloat(data, pos + 4, bigEndian);
        info.BoundingCenterZ = ReadFloat(data, pos + 8, bigEndian);
        info.FieldOffsets["BoundingRadius"] = pos + 12;
        info.BoundingRadius = ReadFloat(data, pos + 12, bigEndian);
        pos += 16;
    }

    private static uint ReadGeometryBoolean(
        ReadOnlySpan<byte> data,
        ref int pos,
        bool bigEndian,
        bool usesWideBoolean,
        Dictionary<string, int> offsets,
        string fieldName)
    {
        offsets[fieldName] = pos;
        if (usesWideBoolean)
        {
            EnsureAvailable(data, pos, 4, fieldName);
            var value = ReadUInt32(data, pos, bigEndian);
            pos += 4;
            return value;
        }

        EnsureAvailable(data, pos, 1, fieldName);
        return data[pos++];
    }

    private static void Skip(ReadOnlySpan<byte> data, ref int pos, int count, string fieldName)
    {
        EnsureAvailable(data, pos, count, fieldName);
        pos += count;
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int pos, int count, string fieldName)
    {
        if (pos < 0 || count < 0 || pos > data.Length - count)
        {
            throw new InvalidDataException(
                $"NiGeometryData field '{fieldName}' exceeds block bounds at 0x{pos:X} " +
                $"(need {count} bytes, block has {data.Length}).");
        }
    }
}
