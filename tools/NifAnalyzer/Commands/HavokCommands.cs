using System.Buffers.Binary;
using System.Globalization;
using NifAnalyzer.Parsers;

namespace NifAnalyzer.Commands;

/// <summary>
/// Commands for analyzing Havok physics blocks in NIF files.
/// </summary>
internal static class HavokCommands
{
    public static int Havok(string path, int blockIndex)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            Console.Error.WriteLine($"Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})");
            return 1;
        }

        var offset = nif.GetBlockOffset(blockIndex);
        var typeName = nif.GetBlockTypeName(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];

        Console.WriteLine($"Block {blockIndex}: {typeName}");
        Console.WriteLine($"Offset: 0x{offset:X4}, Size: {size} bytes");
        Console.WriteLine($"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}");
        Console.WriteLine();

        return typeName switch
        {
            "hkPackedNiTriStripsData" => ParseHkPackedNiTriStripsData(data, offset, size, nif.IsBigEndian),
            "bhkPackedNiTriStripsShape" => ParseBhkPackedNiTriStripsShape(data, offset, size, nif.IsBigEndian),
            "bhkMoppBvTreeShape" => ParseBhkMoppBvTreeShape(data, offset, size, nif.IsBigEndian),
            "bhkRigidBody" or "bhkRigidBodyT" => ParseBhkRigidBody(data, offset, size, nif.IsBigEndian),
            "bhkCollisionObject" or "bhkBlendCollisionObject" or "bhkSPCollisionObject"
                => ParseBhkCollisionObject(data, offset, size, nif.IsBigEndian),
            _ => UnsupportedBlock(typeName)
        };
    }

    public static int HavokCompare(string xboxPath, string pcPath, int xboxBlock, int pcBlock)
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

        Console.WriteLine($"=== Havok Block Comparison ===");
        Console.WriteLine();
        Console.WriteLine($"{"Property",-25} {"Xbox 360",-20} {"PC",-20}");
        Console.WriteLine(new string('-', 65));
        Console.WriteLine($"{"Block Index",-25} {xboxBlock,-20} {pcBlock,-20}");
        Console.WriteLine($"{"Type",-25} {xboxTypeName,-20} {pcTypeName,-20}");
        Console.WriteLine($"{"Offset",-25} 0x{xboxOffset:X4,-17} 0x{pcOffset:X4,-17}");
        Console.WriteLine($"{"Size",-25} {xboxSize,-20} {pcSize,-20}");
        Console.WriteLine();

        if (xboxTypeName != pcTypeName)
        {
            Console.WriteLine("ERROR: Block types don't match!");
            return 1;
        }

        return xboxTypeName switch
        {
            "hkPackedNiTriStripsData" => CompareHkPackedNiTriStripsData(xboxData, xboxOffset, xboxSize,
                pcData, pcOffset, pcSize),
            "bhkMoppBvTreeShape" => CompareBhkMoppBvTreeShape(xboxData, xboxOffset, xboxSize,
                pcData, pcOffset, pcSize),
            _ => 0
        };
    }

    private static int ParseHkPackedNiTriStripsData(byte[] data, int offset, int size, bool isBE)
    {
        var pos = offset;
        var end = offset + size;

        var numTriangles = ReadUInt32(data, pos, isBE);
        pos += 4;

        Console.WriteLine($"NumTriangles: {numTriangles}");
        Console.WriteLine();

        // Show first few triangles
        Console.WriteLine("First 5 TriangleData entries (Triangle v1,v2,v3 + WeldInfo):");
        for (int i = 0; i < Math.Min(5, (int)numTriangles) && pos + 8 <= end; i++)
        {
            var v1 = ReadUInt16(data, pos, isBE);
            var v2 = ReadUInt16(data, pos + 2, isBE);
            var v3 = ReadUInt16(data, pos + 4, isBE);
            var weld = ReadUInt16(data, pos + 6, isBE);
            Console.WriteLine($"  [{i}] Triangle({v1}, {v2}, {v3}) WeldInfo=0x{weld:X4}");
            pos += 8;
        }

        // Skip remaining triangles
        pos = offset + 4 + (int)numTriangles * 8;

        if (pos + 4 > end) { Console.WriteLine("Truncated after triangles"); return 0; }

        var numVertices = ReadUInt32(data, pos, isBE);
        pos += 4;
        Console.WriteLine();
        Console.WriteLine($"NumVertices: {numVertices}");

        // Show first few vertices
        Console.WriteLine("First 5 Vertices (Vector3):");
        for (int i = 0; i < Math.Min(5, (int)numVertices) && pos + 12 <= end; i++)
        {
            var x = ReadFloat(data, pos, isBE);
            var y = ReadFloat(data, pos + 4, isBE);
            var z = ReadFloat(data, pos + 8, isBE);
            Console.WriteLine($"  [{i}] ({x:F4}, {y:F4}, {z:F4})");
            pos += 12;
        }

        return 0;
    }

    private static int ParseBhkPackedNiTriStripsShape(byte[] data, int offset, int size, bool isBE)
    {
        var pos = offset;

        var userData = ReadUInt32(data, pos, isBE);
        Console.WriteLine($"UserData: {userData}");
        pos += 4;

        Console.WriteLine($"Unused01: [{data[pos]:X2} {data[pos + 1]:X2} {data[pos + 2]:X2} {data[pos + 3]:X2}]");
        pos += 4;

        var radius = ReadFloat(data, pos, isBE);
        Console.WriteLine($"Radius: {radius:F6}");
        pos += 4;

        Console.WriteLine($"Unused02: [{data[pos]:X2} {data[pos + 1]:X2} {data[pos + 2]:X2} {data[pos + 3]:X2}]");
        pos += 4;

        Console.WriteLine($"Scale: ({ReadFloat(data, pos, isBE):F4}, {ReadFloat(data, pos + 4, isBE):F4}, {ReadFloat(data, pos + 8, isBE):F4}, {ReadFloat(data, pos + 12, isBE):F4})");
        pos += 16;

        var radiusCopy = ReadFloat(data, pos, isBE);
        Console.WriteLine($"RadiusCopy: {radiusCopy:F6}");
        pos += 4;

        Console.WriteLine($"ScaleCopy: ({ReadFloat(data, pos, isBE):F4}, {ReadFloat(data, pos + 4, isBE):F4}, {ReadFloat(data, pos + 8, isBE):F4}, {ReadFloat(data, pos + 12, isBE):F4})");
        pos += 16;

        var dataRef = ReadInt32(data, pos, isBE);
        Console.WriteLine($"Data Ref: {dataRef} (hkPackedNiTriStripsData)");

        return 0;
    }

    private static int ParseBhkMoppBvTreeShape(byte[] data, int offset, int size, bool isBE)
    {
        var pos = offset;

        var shapeRef = ReadInt32(data, pos, isBE);
        Console.WriteLine($"Shape Ref: {shapeRef}");
        pos += 4;

        Console.Write("Unused01 (12 bytes): ");
        for (int i = 0; i < 12; i++) Console.Write($"{data[pos + i]:X2} ");
        Console.WriteLine();
        pos += 12;

        var scale = ReadFloat(data, pos, isBE);
        Console.WriteLine($"Scale: {scale:F6}");
        pos += 4;

        Console.WriteLine();
        Console.WriteLine("=== hkpMoppCode ===");

        var dataSize = ReadUInt32(data, pos, isBE);
        Console.WriteLine($"DataSize: {dataSize}");
        pos += 4;

        var ox = ReadFloat(data, pos, isBE);
        var oy = ReadFloat(data, pos + 4, isBE);
        var oz = ReadFloat(data, pos + 8, isBE);
        var ow = ReadFloat(data, pos + 12, isBE);
        Console.WriteLine($"Offset: ({ox:F4}, {oy:F4}, {oz:F4}, {ow:F4})");
        pos += 16;

        var buildType = data[pos];
        Console.WriteLine($"BuildType: {buildType}");
        pos += 1;

        Console.WriteLine($"MOPP Data: {dataSize} bytes starting at 0x{pos:X4}");
        Console.Write("First 32 bytes: ");
        for (int i = 0; i < Math.Min(32, (int)dataSize); i++) Console.Write($"{data[pos + i]:X2} ");
        Console.WriteLine();

        return 0;
    }

    private static int ParseBhkRigidBody(byte[] data, int offset, int size, bool isBE)
    {
        var pos = offset;

        var shapeRef = ReadInt32(data, pos, isBE);
        Console.WriteLine($"Shape Ref: {shapeRef}");
        pos += 4;

        // 4 bytes unused
        pos += 4;

        // HavokFilter
        var filter = ReadUInt32(data, pos, isBE);
        var group = ReadUInt32(data, pos + 4, isBE);
        Console.WriteLine($"HavokFilter: 0x{filter:X8}, Group: {group}");
        pos += 8;

        // 4 bytes unused
        pos += 4;

        // CollisionResponse, unused, ProcessContactCallbackDelay
        Console.WriteLine($"CollisionResponse: {data[pos]}");
        var callbackDelay = ReadUInt16(data, pos + 2, isBE);
        Console.WriteLine($"ProcessContactCallbackDelay: {callbackDelay}");
        pos += 4;

        // 4 bytes unused
        pos += 4;

        // Translation (Vector4)
        Console.WriteLine($"Translation: ({ReadFloat(data, pos, isBE):F4}, {ReadFloat(data, pos + 4, isBE):F4}, {ReadFloat(data, pos + 8, isBE):F4}, {ReadFloat(data, pos + 12, isBE):F4})");
        pos += 16;

        // Rotation (QuaternionXYZW)
        Console.WriteLine($"Rotation: ({ReadFloat(data, pos, isBE):F4}, {ReadFloat(data, pos + 4, isBE):F4}, {ReadFloat(data, pos + 8, isBE):F4}, {ReadFloat(data, pos + 12, isBE):F4})");

        return 0;
    }

    private static int ParseBhkCollisionObject(byte[] data, int offset, int size, bool isBE)
    {
        var pos = offset;

        var target = ReadInt32(data, pos, isBE);
        Console.WriteLine($"Target: {target}");
        pos += 4;

        var flags = ReadUInt16(data, pos, isBE);
        Console.WriteLine($"Flags: 0x{flags:X4}");
        pos += 2;

        var body = ReadInt32(data, pos, isBE);
        Console.WriteLine($"Body Ref: {body}");

        return 0;
    }

    private static int CompareHkPackedNiTriStripsData(byte[] xbox, int xOff, int xSize,
        byte[] pc, int pOff, int pSize)
    {
        var xNumTri = ReadUInt32(xbox, xOff, true);
        var pNumTri = ReadUInt32(pc, pOff, false);

        Console.WriteLine($"{"NumTriangles",-25} {xNumTri,-20} {pNumTri,-20} {(xNumTri == pNumTri ? "✓" : "MISMATCH!")}");

        var xNumVert = ReadUInt32(xbox, xOff + 4 + (int)xNumTri * 8, true);
        var pNumVert = ReadUInt32(pc, pOff + 4 + (int)pNumTri * 8, false);

        Console.WriteLine($"{"NumVertices",-25} {xNumVert,-20} {pNumVert,-20} {(xNumVert == pNumVert ? "✓" : "MISMATCH!")}");

        // Compare first triangle
        if (xNumTri > 0)
        {
            Console.WriteLine();
            Console.WriteLine("First Triangle:");
            var xv1 = ReadUInt16(xbox, xOff + 4, true);
            var xv2 = ReadUInt16(xbox, xOff + 6, true);
            var xv3 = ReadUInt16(xbox, xOff + 8, true);
            var xw = ReadUInt16(xbox, xOff + 10, true);

            var pv1 = ReadUInt16(pc, pOff + 4, false);
            var pv2 = ReadUInt16(pc, pOff + 6, false);
            var pv3 = ReadUInt16(pc, pOff + 8, false);
            var pw = ReadUInt16(pc, pOff + 10, false);

            Console.WriteLine($"  Xbox: ({xv1}, {xv2}, {xv3}) Weld=0x{xw:X4}");
            Console.WriteLine($"  PC:   ({pv1}, {pv2}, {pv3}) Weld=0x{pw:X4}");
        }

        return 0;
    }

    private static int CompareBhkMoppBvTreeShape(byte[] xbox, int xOff, int xSize,
        byte[] pc, int pOff, int pSize)
    {
        var xShapeRef = ReadInt32(xbox, xOff, true);
        var pShapeRef = ReadInt32(pc, pOff, false);
        Console.WriteLine($"{"Shape Ref",-25} {xShapeRef,-20} {pShapeRef,-20}");

        var xScale = ReadFloat(xbox, xOff + 16, true);
        var pScale = ReadFloat(pc, pOff + 16, false);
        Console.WriteLine($"{"Scale",-25} {xScale:F6,-13} {pScale:F6,-13}");

        var xDataSize = ReadUInt32(xbox, xOff + 20, true);
        var pDataSize = ReadUInt32(pc, pOff + 20, false);
        Console.WriteLine($"{"MOPP DataSize",-25} {xDataSize,-20} {pDataSize,-20} {(xDataSize == pDataSize ? "✓" : "MISMATCH!")}");

        Console.WriteLine();
        Console.WriteLine("MOPP Offset Vector4:");
        Console.WriteLine($"  Xbox: ({ReadFloat(xbox, xOff + 24, true):F4}, {ReadFloat(xbox, xOff + 28, true):F4}, {ReadFloat(xbox, xOff + 32, true):F4}, {ReadFloat(xbox, xOff + 36, true):F4})");
        Console.WriteLine($"  PC:   ({ReadFloat(pc, pOff + 24, false):F4}, {ReadFloat(pc, pOff + 28, false):F4}, {ReadFloat(pc, pOff + 32, false):F4}, {ReadFloat(pc, pOff + 36, false):F4})");

        return 0;
    }

    private static int UnsupportedBlock(string typeName)
    {
        Console.WriteLine($"Havok parsing not implemented for: {typeName}");
        Console.WriteLine("Supported: hkPackedNiTriStripsData, bhkPackedNiTriStripsShape, bhkMoppBvTreeShape, bhkRigidBody, bhkCollisionObject");
        return 1;
    }

    private static uint ReadUInt32(byte[] data, int pos, bool isBE)
        => isBE ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));

    private static int ReadInt32(byte[] data, int pos, bool isBE)
        => isBE ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));

    private static ushort ReadUInt16(byte[] data, int pos, bool isBE)
        => isBE ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));

    private static float ReadFloat(byte[] data, int pos, bool isBE)
    {
        var bits = isBE ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
                        : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        return BitConverter.UInt32BitsToSingle(bits);
    }
}
