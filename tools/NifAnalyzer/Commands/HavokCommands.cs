using System.Buffers.Binary;
using System.CommandLine;
using NifAnalyzer.Parsers;
using Spectre.Console;

namespace NifAnalyzer.Commands;

/// <summary>
///     Commands for analyzing Havok physics blocks in NIF files.
/// </summary>
internal static class HavokCommands
{
    private static void Havok(string path, int blockIndex)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})");
            return;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var typeName = nif.GetBlockTypeName(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];

        AnsiConsole.WriteLine($"Block {blockIndex}: {typeName}");
        AnsiConsole.WriteLine($"Offset: 0x{offset:X4}, Size: {size} bytes");
        AnsiConsole.WriteLine($"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}");
        AnsiConsole.WriteLine();

        switch (typeName)
        {
            case "hkPackedNiTriStripsData":
                ParseHkPackedNiTriStripsData(data, offset, size, nif.IsBigEndian);
                break;
            case "bhkPackedNiTriStripsShape":
                ParseBhkPackedNiTriStripsShape(data, offset, size, nif.IsBigEndian);
                break;
            case "bhkMoppBvTreeShape":
                ParseBhkMoppBvTreeShape(data, offset, size, nif.IsBigEndian);
                break;
            case "bhkRigidBody":
            case "bhkRigidBodyT":
                ParseBhkRigidBody(data, offset, size, nif.IsBigEndian);
                break;
            case "bhkCollisionObject":
            case "bhkBlendCollisionObject":
            case "bhkSPCollisionObject":
                ParseBhkCollisionObject(data, offset, size, nif.IsBigEndian);
                break;
            default:
                UnsupportedBlock(typeName);
                break;
        }
    }

    private static void HavokCompare(string xboxPath, string pcPath, int xboxBlock, int pcBlock)
    {
        var xboxData = File.ReadAllBytes(xboxPath);
        var pcData = File.ReadAllBytes(pcPath);

        var xbox = NifParser.Parse(xboxData);
        var pc = NifParser.Parse(pcData);

        var xboxOffset = xbox.GetBlockOffset(xboxBlock);
        var pcOffset = pc.GetBlockOffset(pcBlock);

        var xboxTypeName = xbox.GetBlockTypeName(xboxBlock);
        var pcTypeName = pc.GetBlockTypeName(pcBlock);

        var xboxSize = (int)xbox.BlockSizes[xboxBlock];
        var pcSize = (int)pc.BlockSizes[pcBlock];

        AnsiConsole.WriteLine("=== Havok Block Comparison ===");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine($"{"Property",-25} {"Xbox 360",-20} {"PC",-20}");
        AnsiConsole.WriteLine(new string('-', 65));
        AnsiConsole.WriteLine($"{"Block Index",-25} {xboxBlock,-20} {pcBlock,-20}");
        AnsiConsole.WriteLine($"{"Type",-25} {xboxTypeName,-20} {pcTypeName,-20}");
        AnsiConsole.WriteLine($"{"Offset",-25} 0x{xboxOffset:X4,-17} 0x{pcOffset:X4,-17}");
        AnsiConsole.WriteLine($"{"Size",-25} {xboxSize,-20} {pcSize,-20}");
        AnsiConsole.WriteLine();

        if (xboxTypeName != pcTypeName)
        {
            AnsiConsole.WriteLine("ERROR: Block types don't match!");
            return;
        }

        switch (xboxTypeName)
        {
            case "hkPackedNiTriStripsData":
                CompareHkPackedNiTriStripsData(xboxData, xboxOffset, xboxSize, pcData, pcOffset, pcSize);
                break;
            case "bhkMoppBvTreeShape":
                CompareBhkMoppBvTreeShape(xboxData, xboxOffset, xboxSize, pcData, pcOffset, pcSize);
                break;
        }
    }

    private static void ParseHkPackedNiTriStripsData(byte[] data, int offset, int size, bool isBE)
    {
        var pos = offset;
        var end = offset + size;

        var numTriangles = ReadUInt32(data, pos, isBE);
        pos += 4;

        AnsiConsole.WriteLine($"NumTriangles: {numTriangles}");
        AnsiConsole.WriteLine();

        // Show first few triangles
        AnsiConsole.WriteLine("First 5 TriangleData entries (Triangle v1,v2,v3 + WeldInfo):");
        for (var i = 0; i < Math.Min(5, (int)numTriangles) && pos + 8 <= end; i++)
        {
            var v1 = ReadUInt16(data, pos, isBE);
            var v2 = ReadUInt16(data, pos + 2, isBE);
            var v3 = ReadUInt16(data, pos + 4, isBE);
            var weld = ReadUInt16(data, pos + 6, isBE);
            AnsiConsole.WriteLine($"  [{i}] Triangle({v1}, {v2}, {v3}) WeldInfo=0x{weld:X4}");
            pos += 8;
        }

        // Skip remaining triangles
        pos = offset + 4 + (int)numTriangles * 8;

        if (pos + 4 > end)
        {
            AnsiConsole.WriteLine("Truncated after triangles");
            return;
        }

        var numVertices = ReadUInt32(data, pos, isBE);
        pos += 4;
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine($"NumVertices: {numVertices}");

        // Compressed flag (since NIF 20.2.0.7)
        var compressed = data[pos];
        pos += 1;
        Console.WriteLine(
            $"Compressed: {compressed} ({(compressed == 1 ? "HalfVector3 - 6 bytes/vertex" : "Vector3 - 12 bytes/vertex")})");
        AnsiConsole.WriteLine();

        // Show first few vertices based on compression
        if (compressed == 1)
        {
            AnsiConsole.WriteLine("First 5 Vertices (HalfVector3):");
            for (var i = 0; i < Math.Min(5, (int)numVertices) && pos + 6 <= end; i++)
            {
                var hx = ReadUInt16(data, pos, isBE);
                var hy = ReadUInt16(data, pos + 2, isBE);
                var hz = ReadUInt16(data, pos + 4, isBE);
                var x = HalfToFloat(hx);
                var y = HalfToFloat(hy);
                var z = HalfToFloat(hz);
                AnsiConsole.WriteLine($"  [{i}] Half(0x{hx:X4}, 0x{hy:X4}, 0x{hz:X4}) -> ({x:F4}, {y:F4}, {z:F4})");
                pos += 6;
            }

            pos = offset + 4 + (int)numTriangles * 8 + 4 + 1 + (int)numVertices * 6;
        }
        else
        {
            AnsiConsole.WriteLine("First 5 Vertices (Vector3):");
            for (var i = 0; i < Math.Min(5, (int)numVertices) && pos + 12 <= end; i++)
            {
                var x = ReadFloat(data, pos, isBE);
                var y = ReadFloat(data, pos + 4, isBE);
                var z = ReadFloat(data, pos + 8, isBE);
                AnsiConsole.WriteLine($"  [{i}] ({x:F4}, {y:F4}, {z:F4})");
                pos += 12;
            }

            pos = offset + 4 + (int)numTriangles * 8 + 4 + 1 + (int)numVertices * 12;
        }

        // NumSubShapes
        if (pos + 2 > end)
        {
            AnsiConsole.WriteLine("\nTruncated before SubShapes");
            return;
        }

        var numSubShapes = ReadUInt16(data, pos, isBE);
        pos += 2;
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine($"NumSubShapes: {numSubShapes}");

        // SubShapes
        AnsiConsole.WriteLine("SubShapes (hkSubPartData):");
        for (var i = 0; i < numSubShapes && pos + 12 <= end; i++)
        {
            var havokFilter = ReadUInt32(data, pos, isBE);
            var subNumVerts = ReadUInt32(data, pos + 4, isBE);
            var havokMaterial = ReadUInt32(data, pos + 8, isBE);
            Console.WriteLine(
                $"  [{i}] Filter=0x{havokFilter:X8}, NumVerts={subNumVerts}, Material=0x{havokMaterial:X8}");
            pos += 12;
        }
    }

    /// <summary>
    ///     Convert half-precision float (IEEE 754 binary16) to single precision float.
    /// </summary>
    private static float HalfToFloat(ushort h)
    {
        var sign = (h >> 15) & 0x0001;
        var exp = (h >> 10) & 0x001F;
        var mant = h & 0x03FF;

        if (exp == 0)
        {
            if (mant == 0) return sign != 0 ? -0.0f : 0.0f;
            while ((mant & 0x0400) == 0)
            {
                mant <<= 1;
                exp--;
            }

            exp++;
            mant &= ~0x0400;
        }
        else if (exp == 31)
        {
            return mant != 0 ? float.NaN : sign != 0 ? float.NegativeInfinity : float.PositiveInfinity;
        }

        exp += 127 - 15;
        mant <<= 13;
        var bits = (sign << 31) | (exp << 23) | mant;
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static void ParseBhkPackedNiTriStripsShape(byte[] data, int offset, int size, bool isBE)
    {
        var pos = offset;

        var userData = ReadUInt32(data, pos, isBE);
        AnsiConsole.WriteLine($"UserData: {userData}");
        pos += 4;

        AnsiConsole.WriteLine($"Unused01: [{data[pos]:X2} {data[pos + 1]:X2} {data[pos + 2]:X2} {data[pos + 3]:X2}]");
        pos += 4;

        var radius = ReadFloat(data, pos, isBE);
        AnsiConsole.WriteLine($"Radius: {radius:F6}");
        pos += 4;

        AnsiConsole.WriteLine($"Unused02: [{data[pos]:X2} {data[pos + 1]:X2} {data[pos + 2]:X2} {data[pos + 3]:X2}]");
        pos += 4;

        Console.WriteLine(
            $"Scale: ({ReadFloat(data, pos, isBE):F4}, {ReadFloat(data, pos + 4, isBE):F4}, {ReadFloat(data, pos + 8, isBE):F4}, {ReadFloat(data, pos + 12, isBE):F4})");
        pos += 16;

        var radiusCopy = ReadFloat(data, pos, isBE);
        AnsiConsole.WriteLine($"RadiusCopy: {radiusCopy:F6}");
        pos += 4;

        Console.WriteLine(
            $"ScaleCopy: ({ReadFloat(data, pos, isBE):F4}, {ReadFloat(data, pos + 4, isBE):F4}, {ReadFloat(data, pos + 8, isBE):F4}, {ReadFloat(data, pos + 12, isBE):F4})");
        pos += 16;

        var dataRef = ReadInt32(data, pos, isBE);
        AnsiConsole.WriteLine($"Data Ref: {dataRef} (hkPackedNiTriStripsData)");
    }

    private static void ParseBhkMoppBvTreeShape(byte[] data, int offset, int size, bool isBE)
    {
        var pos = offset;

        var shapeRef = ReadInt32(data, pos, isBE);
        AnsiConsole.WriteLine($"Shape Ref: {shapeRef}");
        pos += 4;

        Console.Write("Unused01 (12 bytes): ");
        for (var i = 0; i < 12; i++) Console.Write($"{data[pos + i]:X2} ");
        AnsiConsole.WriteLine();
        pos += 12;

        var scale = ReadFloat(data, pos, isBE);
        AnsiConsole.WriteLine($"Scale: {scale:F6}");
        pos += 4;

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("=== hkpMoppCode ===");

        var dataSize = ReadUInt32(data, pos, isBE);
        AnsiConsole.WriteLine($"DataSize: {dataSize}");
        pos += 4;

        var ox = ReadFloat(data, pos, isBE);
        var oy = ReadFloat(data, pos + 4, isBE);
        var oz = ReadFloat(data, pos + 8, isBE);
        var ow = ReadFloat(data, pos + 12, isBE);
        AnsiConsole.WriteLine($"Offset: ({ox:F4}, {oy:F4}, {oz:F4}, {ow:F4})");
        pos += 16;

        var buildType = data[pos];
        AnsiConsole.WriteLine($"BuildType: {buildType}");
        pos += 1;

        AnsiConsole.WriteLine($"MOPP Data: {dataSize} bytes starting at 0x{pos:X4}");
        Console.Write("First 32 bytes: ");
        for (var i = 0; i < Math.Min(32, (int)dataSize); i++) Console.Write($"{data[pos + i]:X2} ");
        AnsiConsole.WriteLine();
    }

    private static void ParseBhkRigidBody(byte[] data, int offset, int size, bool isBE)
    {
        var pos = offset;

        var shapeRef = ReadInt32(data, pos, isBE);
        AnsiConsole.WriteLine($"Shape Ref: {shapeRef}");
        pos += 4;

        // 4 bytes unused
        pos += 4;

        // HavokFilter
        var filter = ReadUInt32(data, pos, isBE);
        var group = ReadUInt32(data, pos + 4, isBE);
        AnsiConsole.WriteLine($"HavokFilter: 0x{filter:X8}, Group: {group}");
        pos += 8;

        // 4 bytes unused
        pos += 4;

        // CollisionResponse, unused, ProcessContactCallbackDelay
        AnsiConsole.WriteLine($"CollisionResponse: {data[pos]}");
        var callbackDelay = ReadUInt16(data, pos + 2, isBE);
        AnsiConsole.WriteLine($"ProcessContactCallbackDelay: {callbackDelay}");
        pos += 4;

        // 4 bytes unused
        pos += 4;

        // Translation (Vector4)
        Console.WriteLine(
            $"Translation: ({ReadFloat(data, pos, isBE):F4}, {ReadFloat(data, pos + 4, isBE):F4}, {ReadFloat(data, pos + 8, isBE):F4}, {ReadFloat(data, pos + 12, isBE):F4})");
        pos += 16;

        // Rotation (QuaternionXYZW)
        Console.WriteLine(
            $"Rotation: ({ReadFloat(data, pos, isBE):F4}, {ReadFloat(data, pos + 4, isBE):F4}, {ReadFloat(data, pos + 8, isBE):F4}, {ReadFloat(data, pos + 12, isBE):F4})");
    }

    private static void ParseBhkCollisionObject(byte[] data, int offset, int size, bool isBE)
    {
        var pos = offset;

        var target = ReadInt32(data, pos, isBE);
        AnsiConsole.WriteLine($"Target: {target}");
        pos += 4;

        var flags = ReadUInt16(data, pos, isBE);
        AnsiConsole.WriteLine($"Flags: 0x{flags:X4}");
        pos += 2;

        var body = ReadInt32(data, pos, isBE);
        AnsiConsole.WriteLine($"Body Ref: {body}");
    }

    private static void CompareHkPackedNiTriStripsData(byte[] xbox, int xOff, int xSize,
        byte[] pc, int pOff, int pSize)
    {
        var xNumTri = ReadUInt32(xbox, xOff, true);
        var pNumTri = ReadUInt32(pc, pOff, false);

        AnsiConsole.WriteLine(
            $"{"NumTriangles",-25} {xNumTri,-20} {pNumTri,-20} {(xNumTri == pNumTri ? "✓" : "MISMATCH!")}");

        var xNumVert = ReadUInt32(xbox, xOff + 4 + (int)xNumTri * 8, true);
        var pNumVert = ReadUInt32(pc, pOff + 4 + (int)pNumTri * 8, false);

        AnsiConsole.WriteLine(
            $"{"NumVertices",-25} {xNumVert,-20} {pNumVert,-20} {(xNumVert == pNumVert ? "✓" : "MISMATCH!")}");

        // Compare first triangle
        if (xNumTri > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine("First Triangle:");
            var xv1 = ReadUInt16(xbox, xOff + 4, true);
            var xv2 = ReadUInt16(xbox, xOff + 6, true);
            var xv3 = ReadUInt16(xbox, xOff + 8, true);
            var xw = ReadUInt16(xbox, xOff + 10, true);

            var pv1 = ReadUInt16(pc, pOff + 4, false);
            var pv2 = ReadUInt16(pc, pOff + 6, false);
            var pv3 = ReadUInt16(pc, pOff + 8, false);
            var pw = ReadUInt16(pc, pOff + 10, false);

            AnsiConsole.WriteLine($"  Xbox: ({xv1}, {xv2}, {xv3}) Weld=0x{xw:X4}");
            AnsiConsole.WriteLine($"  PC:   ({pv1}, {pv2}, {pv3}) Weld=0x{pw:X4}");
        }
    }

    private static void CompareBhkMoppBvTreeShape(byte[] xbox, int xOff, int xSize,
        byte[] pc, int pOff, int pSize)
    {
        var xShapeRef = ReadInt32(xbox, xOff, true);
        var pShapeRef = ReadInt32(pc, pOff, false);
        AnsiConsole.WriteLine($"{"Shape Ref",-25} {xShapeRef,-20} {pShapeRef,-20}");

        var xScale = ReadFloat(xbox, xOff + 16, true);
        var pScale = ReadFloat(pc, pOff + 16, false);
        AnsiConsole.WriteLine($"{"Scale",-25} {xScale:F6,-13} {pScale:F6,-13}");

        var xDataSize = ReadUInt32(xbox, xOff + 20, true);
        var pDataSize = ReadUInt32(pc, pOff + 20, false);
        AnsiConsole.WriteLine(
            $"{"MOPP DataSize",-25} {xDataSize,-20} {pDataSize,-20} {(xDataSize == pDataSize ? "✓" : "MISMATCH!")}");

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("MOPP Offset Vector4:");
        AnsiConsole.WriteLine(
            $"  Xbox: ({ReadFloat(xbox, xOff + 24, true):F4}, {ReadFloat(xbox, xOff + 28, true):F4}, {ReadFloat(xbox, xOff + 32, true):F4}, {ReadFloat(xbox, xOff + 36, true):F4})");
        AnsiConsole.WriteLine(
            $"  PC:   ({ReadFloat(pc, pOff + 24, false):F4}, {ReadFloat(pc, pOff + 28, false):F4}, {ReadFloat(pc, pOff + 32, false):F4}, {ReadFloat(pc, pOff + 36, false):F4})");
    }

    private static void UnsupportedBlock(string typeName)
    {
        AnsiConsole.WriteLine($"Havok parsing not implemented for: {typeName}");
        Console.WriteLine(
            "Supported: hkPackedNiTriStripsData, bhkPackedNiTriStripsShape, bhkMoppBvTreeShape, bhkRigidBody, bhkCollisionObject");
    }

    private static uint ReadUInt32(byte[] data, int pos, bool isBE)
    {
        return isBE
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
    }

    private static int ReadInt32(byte[] data, int pos, bool isBE)
    {
        return isBE
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
    }

    private static ushort ReadUInt16(byte[] data, int pos, bool isBE)
    {
        return isBE
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
    }

    private static float ReadFloat(byte[] data, int pos, bool isBE)
    {
        var bits = isBE
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        return BitConverter.UInt32BitsToSingle(bits);
    }

    #region Command Registration

    public static Command CreateHavokCommand()
    {
        var command = new Command("havok",
            "Parse Havok physics blocks (hkPackedNiTriStripsData, bhkMoppBvTreeShape, etc.)");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        var blockArg = new Argument<int>("block") { Description = "Block index" };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(blockArg);
        command.SetAction(parseResult => Havok(parseResult.GetValue(fileArg), parseResult.GetValue(blockArg)));
        return command;
    }

    public static Command CreateHavokCompareCommand()
    {
        var command = new Command("havokcompare", "Compare Havok blocks between Xbox 360 and PC files");
        var xboxArg = new Argument<string>("xbox") { Description = "Xbox NIF file path" };
        var pcArg = new Argument<string>("pc") { Description = "PC NIF file path" };
        var xboxBlockArg = new Argument<int>("xbox-block") { Description = "Xbox block index" };
        var pcBlockArg = new Argument<int>("pc-block") { Description = "PC block index" };
        command.Arguments.Add(xboxArg);
        command.Arguments.Add(pcArg);
        command.Arguments.Add(xboxBlockArg);
        command.Arguments.Add(pcBlockArg);
        command.SetAction(parseResult => HavokCompare(
            parseResult.GetValue(xboxArg), parseResult.GetValue(pcArg),
            parseResult.GetValue(xboxBlockArg), parseResult.GetValue(pcBlockArg)));
        return command;
    }

    #endregion
}