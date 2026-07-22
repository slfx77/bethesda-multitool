using System.Buffers.Binary;
using System.CommandLine;
using NifAnalyzer.Parsers;
using Spectre.Console;
using NifVersions = BethesdaMultitool.Core.Formats.Nif.Parser.NifVersions;

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
                ParseHkPackedNiTriStripsData(data, offset, size, nif.IsBigEndian, nif.Version);
                break;
            case "bhkPackedNiTriStripsShape":
                ParseBhkPackedNiTriStripsShape(data, offset, size, nif.IsBigEndian, nif.Version);
                break;
            case "bhkMoppBvTreeShape":
                ParseBhkMoppBvTreeShape(data, offset, size, nif.IsBigEndian);
                break;
            case "bhkRigidBody":
            case "bhkRigidBodyT":
                ParseBhkRigidBody(data, offset, size, nif.IsBigEndian, nif.Version);
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

    /// <summary>
    ///     Decodes a NIF's Havok (bhk*) collision via the production
    ///     <see cref="BethesdaMultitool.Core.Formats.Nif.Collision.HavokCollisionExtractor" /> and dumps
    ///     the merged triangle soup's vertex/triangle counts + AABB, plus a per-collision-object listing.
    ///     Compare the AABB to the visual mesh's AABB on a known bridge to confirm scale (a 7× mismatch =
    ///     wrong Havok scale) and frame (offset/rotation = wrong node transform).
    /// </summary>
    private static void HavokDump(string path)
    {
        var data = File.ReadAllBytes(path);
        var nif = BethesdaMultitool.Core.Formats.Nif.Parser.NifParser.Parse(data);
        if (nif is null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] failed to parse NIF.");
            return;
        }

        AnsiConsole.WriteLine($"File: {path}");
        AnsiConsole.WriteLine(
            $"Endian: {(nif.IsBigEndian ? "Big (Xbox 360)" : "Little (PC)")}, Blocks: {nif.Blocks.Count}");
        AnsiConsole.WriteLine();

        var collisionObjects = 0;
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var type = nif.Blocks[i].TypeName;
            if (type is not ("bhkCollisionObject" or "bhkBlendCollisionObject" or "bhkSPCollisionObject"))
            {
                continue;
            }

            collisionObjects++;
            var off = nif.Blocks[i].DataOffset;
            var target = ReadInt32(data, off, nif.IsBigEndian);
            var body = ReadInt32(data, off + 6, nif.IsBigEndian);
            var bodyType = body >= 0 && body < nif.Blocks.Count ? nif.Blocks[body].TypeName : "(none)";
            AnsiConsole.WriteLine($"  bhkCollisionObject #{i}: Target=#{target}, Body=#{body} ({bodyType})");
        }

        AnsiConsole.WriteLine($"Collision objects: {collisionObjects}");
        AnsiConsole.WriteLine();

        var soup = BethesdaMultitool.Core.Formats.Nif.Collision.HavokCollisionExtractor.TryExtract(
            data, nif, nif.IsBigEndian);
        if (soup is not { } s)
        {
            AnsiConsole.WriteLine("No decodable Havok collision (packed tri-strips) found → visual-mesh fallback.");
            return;
        }

        var min = new System.Numerics.Vector3(float.PositiveInfinity);
        var max = new System.Numerics.Vector3(float.NegativeInfinity);
        foreach (var p in s.Positions)
        {
            min = System.Numerics.Vector3.Min(min, p);
            max = System.Numerics.Vector3.Max(max, p);
        }

        AnsiConsole.WriteLine($"Decoded soup: {s.Positions.Length} verts, {s.Triangles.Length / 3} triangles");
        AnsiConsole.WriteLine($"  AABB min:  ({min.X:F2}, {min.Y:F2}, {min.Z:F2})");
        AnsiConsole.WriteLine($"  AABB max:  ({max.X:F2}, {max.Y:F2}, {max.Z:F2})");
        AnsiConsole.WriteLine($"  AABB size: ({max.X - min.X:F2}, {max.Y - min.Y:F2}, {max.Z - min.Z:F2})");
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

    private static void ParseHkPackedNiTriStripsData(byte[] data, int offset, int size, bool isBE,
        uint version)
    {
        var pos = offset;
        var end = offset + size;

        // TES4-era (≤20.0.0.5): TriangleData carries a trailing Vector3 normal (stride 20, not 8),
        // there is NO Compressed flag (since 20.2.0.7), and NO trailing sub-shape array (that array
        // lives on bhkPackedNiTriStripsShape instead at that era).
        var tes4Era = NifVersions.IsTes4Era(version);
        var triStride = tes4Era ? 20 : 8;

        var numTriangles = ReadUInt32(data, pos, isBE);
        pos += 4;

        AnsiConsole.WriteLine($"NumTriangles: {numTriangles} (TriangleData stride {triStride})");
        AnsiConsole.WriteLine();

        // Show first few triangles
        AnsiConsole.WriteLine("First 5 TriangleData entries (Triangle v1,v2,v3 + WeldInfo):");
        for (var i = 0; i < Math.Min(5, (int)numTriangles) && pos + triStride <= end; i++)
        {
            var v1 = ReadUInt16(data, pos, isBE);
            var v2 = ReadUInt16(data, pos + 2, isBE);
            var v3 = ReadUInt16(data, pos + 4, isBE);
            var weld = ReadUInt16(data, pos + 6, isBE);
            if (tes4Era)
            {
                var nx = ReadFloat(data, pos + 8, isBE);
                var ny = ReadFloat(data, pos + 12, isBE);
                var nz = ReadFloat(data, pos + 16, isBE);
                AnsiConsole.WriteLine(
                    $"  [{i}] Triangle({v1}, {v2}, {v3}) WeldInfo=0x{weld:X4} Normal({nx:F3}, {ny:F3}, {nz:F3})");
            }
            else
            {
                AnsiConsole.WriteLine($"  [{i}] Triangle({v1}, {v2}, {v3}) WeldInfo=0x{weld:X4}");
            }

            pos += triStride;
        }

        // Skip remaining triangles
        pos = offset + 4 + (int)numTriangles * triStride;

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
        byte compressed = 0;
        if (!tes4Era)
        {
            compressed = data[pos];
            pos += 1;
        }

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

            pos = offset + 4 + (int)numTriangles * triStride + 4 + 1 + (int)numVertices * 6;
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

            pos = offset + 4 + (int)numTriangles * triStride + (tes4Era ? 4 : 5) + (int)numVertices * 12;
        }

        // NumSubShapes (moved into this data block at 20.2.0.7; TES4-era files carry it on the shape)
        if (tes4Era)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine("SubShapes: (none in TES4-era data blocks — see bhkPackedNiTriStripsShape)");
            return;
        }

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

    private static void ParseBhkPackedNiTriStripsShape(byte[] data, int offset, int size, bool isBE,
        uint version)
    {
        var pos = offset;

        // TES4-era (≤20.0.0.5) shapes are prefixed by Num Sub Shapes (ushort) + hkSubPartData[]
        // (12 bytes each); 20.2.0.7 moved the array into hkPackedNiTriStripsData.
        if (NifVersions.IsTes4Era(version))
        {
            var numSubShapes = ReadUInt16(data, pos, isBE);
            pos += 2;
            AnsiConsole.WriteLine($"NumSubShapes: {numSubShapes}");
            for (var i = 0; i < numSubShapes && pos + 12 <= offset + size; i++)
            {
                var filter = ReadUInt32(data, pos, isBE);
                var subVerts = ReadUInt32(data, pos + 4, isBE);
                var material = ReadUInt32(data, pos + 8, isBE);
                Console.WriteLine(
                    $"  [{i}] Filter=0x{filter:X8}, NumVerts={subVerts}, Material=0x{material:X8}");
                pos += 12;
            }

            AnsiConsole.WriteLine();
        }

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

    // Layout: nif.xml bhkRigidBodyCInfo550_660 (FO3/FNV), byte-verified against retail rockcave07.nif
    // (Translation @52 is a small Havok-unit vector, Rotation @68 a unit quaternion; the old 28/44 read
    // landed on CollisionResponse/ProcessContactCallbackDelay=0xFFFF, a guaranteed-NaN float).
    // The CInfo's five leading header fields (16 bytes) are since="10.1.0.0" — Oblivion's oldest
    // 10.0.1.x meshes go straight from Unused04 to Translation (@36).
    private static void ParseBhkRigidBody(byte[] data, int offset, int size, bool isBE, uint version)
    {
        var pos = offset;

        var shapeRef = ReadInt32(data, pos, isBE);
        AnsiConsole.WriteLine($"Shape Ref: {shapeRef}");
        pos += 4;

        // bhkWorldObject.HavokFilter: layer byte, flags/part byte, group ushort
        var filter = ReadUInt32(data, pos, isBE);
        AnsiConsole.WriteLine($"HavokFilter: 0x{filter:X8} (Layer: {data[pos]})");
        pos += 4;

        // bhkWorldObjCInfo: Unused01[4] + BroadPhaseType(1) + Unused02[3] + Property(12)
        pos += 20;

        // bhkEntityCInfo: CollisionResponse(1), Unused(1), ProcessContactCallbackDelay(2)
        AnsiConsole.WriteLine($"CollisionResponse: {data[pos]}");
        var callbackDelay = ReadUInt16(data, pos + 2, isBE);
        AnsiConsole.WriteLine($"ProcessContactCallbackDelay: 0x{callbackDelay:X4}");
        pos += 4;

        // bhkRigidBodyCInfo preamble: [Unused01[4] + HavokFilter copy(4) + Unused02[4]
        //   + CollisionResponse/CallbackDelay copy(4), since 10.1.0.0] + Unused04[4]
        pos += version >= NifVersions.Gamebryo10100 ? 20 : 4;

        // Translation (Vector4, Havok units) @52
        Console.WriteLine(
            $"Translation: ({ReadFloat(data, pos, isBE):F4}, {ReadFloat(data, pos + 4, isBE):F4}, {ReadFloat(data, pos + 8, isBE):F4}, {ReadFloat(data, pos + 12, isBE):F4})");
        pos += 16;

        // Rotation (hkQuaternion XYZW) @68
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

    public static Command CreateHavokDumpCommand()
    {
        var command = new Command("havokdump",
            "Decode a NIF's Havok collision (production extractor) and dump its triangle-soup counts + AABB");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => HavokDump(parseResult.GetValue(fileArg)!));
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