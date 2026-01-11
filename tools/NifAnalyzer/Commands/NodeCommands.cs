using NifAnalyzer.Models;
using NifAnalyzer.Parsers;
using static NifAnalyzer.Utils.BinaryHelpers;

namespace NifAnalyzer.Commands;

/// <summary>
/// Commands for parsing NiNode/BSFadeNode and related blocks field-by-field.
/// </summary>
internal static class NodeCommands
{
    /// <summary>
    /// Parse and display NiNode/BSFadeNode fields according to nif.xml spec.
    /// For BS Version > 26, Flags is uint (4 bytes), not ushort (2 bytes).
    /// </summary>
    public static int ParseNode(string path, int blockIndex)
    {
        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);

        if (blockIndex < 0 || blockIndex >= nif.NumBlocks)
        {
            Console.Error.WriteLine($"Block index {blockIndex} out of range (0-{nif.NumBlocks - 1})");
            return 1;
        }

        var typeName = nif.GetBlockTypeName(blockIndex);
        var offset = nif.GetBlockOffset(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];
        var be = nif.IsBigEndian;

        Console.WriteLine($"Block {blockIndex}: {typeName}");
        Console.WriteLine($"Offset: 0x{offset:X4}, Size: {size} bytes");
        Console.WriteLine($"Endian: {(be ? "Big (Xbox 360)" : "Little (PC)")}");
        Console.WriteLine($"BS Version: {nif.BsVersion}");
        Console.WriteLine();

        int pos = offset;
        int end = offset + size;

        // === NiObjectNET ===
        Console.WriteLine("=== NiObjectNET ===");

        if (pos + 4 > end) return Error("Truncated at Name");
        var nameIdx = (int)ReadUInt32(data, pos, be);
        var nameStr = nameIdx >= 0 && nameIdx < nif.Strings.Count ? nif.Strings[nameIdx] : "(none)";
        PrintField("Name", pos, 4, $"{nameIdx} = \"{nameStr}\"");
        pos += 4;

        if (pos + 4 > end) return Error("Truncated at NumExtraData");
        var numExtraData = ReadUInt32(data, pos, be);
        PrintField("Num Extra Data", pos, 4, numExtraData.ToString());
        pos += 4;

        if (numExtraData > 0)
        {
            Console.WriteLine($"  Extra Data Refs ({numExtraData}):");
            for (int i = 0; i < numExtraData && pos + 4 <= end; i++)
            {
                var refIdx = (int)ReadUInt32(data, pos, be);
                Console.WriteLine($"    [{i}] 0x{pos:X4}: {refIdx}");
                pos += 4;
            }
        }

        if (pos + 4 > end) return Error("Truncated at Controller");
        var controllerRef = (int)ReadUInt32(data, pos, be);
        PrintField("Controller", pos, 4, controllerRef.ToString());
        pos += 4;

        // === NiAVObject ===
        Console.WriteLine();
        Console.WriteLine("=== NiAVObject ===");

        // Flags: uint for BSVER > 26, ushort for BSVER <= 26
        bool flagsIsUInt = nif.BsVersion > 26;
        if (flagsIsUInt)
        {
            if (pos + 4 > end) return Error("Truncated at Flags");
            var flags = ReadUInt32(data, pos, be);
            PrintField("Flags (uint)", pos, 4, $"0x{flags:X8}");
            pos += 4;
        }
        else
        {
            if (pos + 2 > end) return Error("Truncated at Flags");
            var flags = ReadUInt16(data, pos, be);
            PrintField("Flags (ushort)", pos, 2, $"0x{flags:X4}");
            pos += 2;
        }

        // Translation (Vector3 - 12 bytes)
        if (pos + 12 > end) return Error("Truncated at Translation");
        var tx = ReadFloat(data.AsSpan(), pos, be);
        var ty = ReadFloat(data.AsSpan(), pos + 4, be);
        var tz = ReadFloat(data.AsSpan(), pos + 8, be);
        PrintField("Translation", pos, 12, $"({tx:F4}, {ty:F4}, {tz:F4})");
        pos += 12;

        // Rotation (Matrix33 - 36 bytes)
        if (pos + 36 > end) return Error("Truncated at Rotation");
        Console.WriteLine($"  0x{pos:X4} [36] Rotation (Matrix33):");
        for (int row = 0; row < 3; row++)
        {
            var m0 = ReadFloat(data.AsSpan(), pos + row * 12, be);
            var m1 = ReadFloat(data.AsSpan(), pos + row * 12 + 4, be);
            var m2 = ReadFloat(data.AsSpan(), pos + row * 12 + 8, be);
            Console.WriteLine($"           [{m0,10:F4} {m1,10:F4} {m2,10:F4}]");
        }
        pos += 36;

        // Scale (float - 4 bytes)
        if (pos + 4 > end) return Error("Truncated at Scale");
        var scale = ReadFloat(data.AsSpan(), pos, be);
        PrintField("Scale", pos, 4, $"{scale:F4}");
        pos += 4;

        // For BS <= FO3 (BS Version <= 34): Num Properties + Properties array
        bool hasProperties = nif.BsVersion <= 34;
        if (hasProperties)
        {
            if (pos + 4 > end) return Error("Truncated at Num Properties");
            var numProperties = ReadUInt32(data, pos, be);
            PrintField("Num Properties", pos, 4, numProperties.ToString());
            pos += 4;

            if (numProperties > 0)
            {
                Console.WriteLine($"  Property Refs ({numProperties}):");
                for (int i = 0; i < numProperties && pos + 4 <= end; i++)
                {
                    var refIdx = (int)ReadUInt32(data, pos, be);
                    Console.WriteLine($"    [{i}] 0x{pos:X4}: {refIdx}");
                    pos += 4;
                }
            }
        }

        // Collision Object
        if (pos + 4 > end) return Error("Truncated at Collision Object");
        var collisionRef = (int)ReadUInt32(data, pos, be);
        PrintField("Collision Object", pos, 4, collisionRef.ToString());
        pos += 4;

        // === NiNode ===
        Console.WriteLine();
        Console.WriteLine("=== NiNode ===");

        // Children array
        if (pos + 4 > end) return Error("Truncated at Num Children");
        var numChildren = ReadUInt32(data, pos, be);
        PrintField("Num Children", pos, 4, numChildren.ToString());
        pos += 4;

        if (numChildren > 0 && numChildren < 1000)
        {
            Console.WriteLine($"  Children Refs ({numChildren}):");
            for (int i = 0; i < numChildren && pos + 4 <= end; i++)
            {
                var refIdx = (int)ReadUInt32(data, pos, be);
                Console.WriteLine($"    [{i}] 0x{pos:X4}: {refIdx}");
                pos += 4;
            }
        }

        // Effects array
        if (pos + 4 > end) return Error("Truncated at Num Effects");
        var numEffects = ReadUInt32(data, pos, be);
        PrintField("Num Effects", pos, 4, numEffects.ToString());
        pos += 4;

        if (numEffects > 0 && numEffects < 1000)
        {
            Console.WriteLine($"  Effect Refs ({numEffects}):");
            for (int i = 0; i < numEffects && pos + 4 <= end; i++)
            {
                var refIdx = (int)ReadUInt32(data, pos, be);
                Console.WriteLine($"    [{i}] 0x{pos:X4}: {refIdx}");
                pos += 4;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Bytes consumed: {pos - offset} / {size}");
        if (pos - offset != size)
        {
            Console.WriteLine($"WARNING: Size mismatch! Expected {size}, consumed {pos - offset}");
        }

        return 0;
    }

    /// <summary>
    /// Compare NiNode/BSFadeNode fields between Xbox and PC/Converted files.
    /// </summary>
    public static int CompareNode(string xboxPath, string otherPath, int xboxBlock, int otherBlock)
    {
        var xboxData = File.ReadAllBytes(xboxPath);
        var otherData = File.ReadAllBytes(otherPath);

        var xbox = NifParser.Parse(xboxData);
        var other = NifParser.Parse(otherData);

        var xTypeName = xbox.GetBlockTypeName(xboxBlock);
        var oTypeName = other.GetBlockTypeName(otherBlock);

        Console.WriteLine($"=== Node Comparison ===");
        Console.WriteLine($"Xbox: Block {xboxBlock} ({xTypeName}), Offset 0x{xbox.GetBlockOffset(xboxBlock):X4}");
        Console.WriteLine($"Other: Block {otherBlock} ({oTypeName}), Offset 0x{other.GetBlockOffset(otherBlock):X4}");
        Console.WriteLine();

        // Parse both and compare key fields
        var xFields = ParseNodeFields(xboxData, xbox, xboxBlock);
        var oFields = ParseNodeFields(otherData, other, otherBlock);

        Console.WriteLine($"{"Field",-20} {"Xbox",-25} {"Other",-25} {"Match"}");
        Console.WriteLine(new string('-', 80));

        foreach (var key in xFields.Keys)
        {
            var xVal = xFields.GetValueOrDefault(key, "N/A");
            var oVal = oFields.GetValueOrDefault(key, "N/A");
            var match = xVal == oVal ? "✓" : "✗ MISMATCH";
            Console.WriteLine($"{key,-20} {xVal,-25} {oVal,-25} {match}");
        }

        return 0;
    }

    private static Dictionary<string, string> ParseNodeFields(byte[] data, NifInfo nif, int blockIndex)
    {
        var fields = new Dictionary<string, string>();
        var offset = nif.GetBlockOffset(blockIndex);
        var size = (int)nif.BlockSizes[blockIndex];
        var be = nif.IsBigEndian;
        int pos = offset;
        int end = offset + size;

        // NiObjectNET
        if (pos + 4 <= end)
        {
            var nameIdx = (int)ReadUInt32(data, pos, be);
            var nameStr = nameIdx >= 0 && nameIdx < nif.Strings.Count ? nif.Strings[nameIdx] : "";
            fields["Name"] = $"{nameIdx} ({nameStr})";
            pos += 4;
        }

        if (pos + 4 <= end)
        {
            var numExtraData = ReadUInt32(data, pos, be);
            fields["NumExtraData"] = numExtraData.ToString();
            pos += 4;
            pos += (int)numExtraData * 4; // Skip extra data refs
        }

        if (pos + 4 <= end)
        {
            fields["Controller"] = ((int)ReadUInt32(data, pos, be)).ToString();
            pos += 4;
        }

        // NiAVObject - Flags
        bool flagsIsUInt = nif.BsVersion > 26;
        if (flagsIsUInt && pos + 4 <= end)
        {
            var flags = ReadUInt32(data, pos, be);
            fields["Flags"] = $"0x{flags:X8}";
            pos += 4;
        }
        else if (!flagsIsUInt && pos + 2 <= end)
        {
            var flags = ReadUInt16(data, pos, be);
            fields["Flags"] = $"0x{flags:X4}";
            pos += 2;
        }

        // Translation
        if (pos + 12 <= end)
        {
            var tx = ReadFloat(data.AsSpan(), pos, be);
            var ty = ReadFloat(data.AsSpan(), pos + 4, be);
            var tz = ReadFloat(data.AsSpan(), pos + 8, be);
            fields["Translation"] = $"({tx:F2},{ty:F2},{tz:F2})";
            pos += 12;
        }

        // Skip Rotation (36 bytes) and Scale (4 bytes)
        pos += 40;

        // Properties (if BS <= 34)
        if (nif.BsVersion <= 34 && pos + 4 <= end)
        {
            var numProperties = ReadUInt32(data, pos, be);
            fields["NumProperties"] = numProperties.ToString();
            pos += 4;
            pos += (int)numProperties * 4;
        }

        // Collision
        if (pos + 4 <= end)
        {
            fields["Collision"] = ((int)ReadUInt32(data, pos, be)).ToString();
            pos += 4;
        }

        // NiNode
        if (pos + 4 <= end)
        {
            var numChildren = ReadUInt32(data, pos, be);
            fields["NumChildren"] = numChildren.ToString();
            pos += 4;
            pos += (int)Math.Min(numChildren, 100) * 4;
        }

        if (pos + 4 <= end)
        {
            fields["NumEffects"] = ReadUInt32(data, pos, be).ToString();
        }

        return fields;
    }

    private static void PrintField(string name, int offset, int size, string value)
    {
        Console.WriteLine($"  0x{offset:X4} [{size,2}] {name}: {value}");
    }

    private static int Error(string msg)
    {
        Console.Error.WriteLine($"Error: {msg}");
        return 1;
    }
}
