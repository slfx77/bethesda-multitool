using static FalloutXbox360Utils.Core.Formats.Esm.Conversion.EsmEndianHelpers;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

/// <summary>
///     Processes subrecord data using schema definitions.
/// </summary>
public static class SubrecordSchemaProcessor
{
    /// <summary>
    ///     Converts subrecord data based on schema.
    ///     Returns null if no schema is found.
    /// </summary>
    public static byte[]? ConvertWithSchema(string signature, ReadOnlySpan<byte> data, string recordType)
    {
        // PKDT has Xbox-specific byte ordering for the first 4 bytes
        if (signature == "PKDT" && data.Length == 12)
        {
            var pkdt = data.ToArray();
            ConvertPkdt(pkdt);
            return pkdt;
        }

        // PERK DATA is a small fixed-size struct, but Xbox 360 files frequently store it as 5 bytes
        // where the last byte is always 0x00 (observed). PC FalloutNV.esm uses 4 bytes.
        // Emit PC-compatible size by dropping the trailing 0x00.
        if (recordType == "PERK" && signature == "DATA" && data.Length == 5 && data[4] == 0x00)
        {
            return data[..4].ToArray();
        }

        // PERK PRKE entries are followed by a DATA(8) payload where the first dword is big-endian
        // on Xbox 360 and must be swapped to match PC ordering. The trailing 4 bytes should be
        // preserved as-is.
        if (recordType == "PERK" && signature == "DATA" && data.Length == 8)
        {
            var perkData = data.ToArray();
            Swap4Bytes(perkData, 0);
            return perkData;
        }

        // IDLE DATA differs between Xbox 360 and PC:
        // - Xbox: DATA(8) = cAnimData(1) + cLoopMin(1) + cLoopMax(1) + pad(1) + sReplayDelay(2 BE) + cFlagsEx(1) + pad(1)
        // - PC:   DATA(6) = same first 6 bytes with sReplayDelay in LE
        // PDB: IDLE_DATA — Endian() swaps uint16 at offset 4 (sReplayDelay)
        // Emit PC-compatible size by swapping the uint16 and dropping trailing 2 bytes.
        if (recordType == "IDLE" && signature == "DATA" && data.Length == 8)
        {
            var idle = data[..6].ToArray();
            Swap2Bytes(idle, 4); // sReplayDelay: BE → LE
            return idle;
        }

        // IMAD DNAM (244 bytes) structure:
        // - Bytes 0-3: uint32 'animatable' flag - ALREADY little-endian on Xbox!
        // - Bytes 4-7: float 'duration' (needs BE->LE swap)
        // - Bytes 8+: uint32 counts (need BE->LE swap)
        // Xbox 360 stores the first uint32 in LE for some reason (hardware optimization?).
        // The default FloatArray would swap ALL bytes, corrupting the already-LE first field.
        if (recordType == "IMAD" && signature == "DNAM" && data.Length == 244)
        {
            var dnam = data.ToArray();
            // Skip bytes 0-3 (already little-endian on Xbox!)
            // Swap bytes 4+ as 4-byte values
            for (var i = 4; i < 244; i += 4)
            {
                Swap4Bytes(dnam, i);
            }

            return dnam;
        }

        // WTHR INAM (Image Space Modifiers) has MIXED endianness on Xbox 360!
        // Floats 0-20, 25-31, 38, 52 are big-endian and need swapping.
        // Floats 21-24 are already little-endian (Xbox optimization) - DO NOT SWAP.
        // All other floats are zero (no swap needed).
        if (recordType == "WTHR" && signature == "INAM" && data.Length == 304)
        {
            var inam = data.ToArray();
            // Swap floats 0-20 (bytes 0-83)
            for (var i = 0; i < 84; i += 4)
            {
                Swap4Bytes(inam, i);
            }

            // Skip floats 21-24 (bytes 84-99) - already little-endian on Xbox!
            // Swap floats 25-31 (bytes 100-127)
            for (var i = 100; i < 128; i += 4)
            {
                Swap4Bytes(inam, i);
            }

            // Floats 32-37 are zero - no swap needed
            // Swap float 38 (bytes 152-155)
            Swap4Bytes(inam, 152);
            // Floats 39-51 are zero - no swap needed
            // Swap float 52 (bytes 208-211)
            Swap4Bytes(inam, 208);
            // Floats 53-75 are zero - no swap needed
            return inam;
        }

        // NOTE TNAM is overloaded:
        // - String when used as text
        // - FormID (4 bytes) when DATA==3 (topics)
        // We can safely disambiguate by size: a 4-byte TNAM is a FormID.
        if (recordType == "NOTE" && signature == "TNAM" && data.Length == 4)
        {
            var tnam = data.ToArray();
            Swap4Bytes(tnam, 0);
            return tnam;
        }

        // Check for string subrecords first
        if (SubrecordSchemaRegistry.IsStringSubrecord(signature, recordType))
        {
            return data.ToArray();
        }

        // NVTR (NavMesh Triangles) has a field ordering difference between Xbox 360 and PC:
        // Each 16-byte entry: Vertex0(2), Vertex1(2), Vertex2(2), Edge01(2), Edge12(2), Edge20(2), CoverFlags(2), Flags(2)
        // Xbox 360 stores: [vertices + edges as BE] + CoverFlags(BE) + Flags(BE)
        // PC stores:       [vertices + edges as LE] + Flags(LE) + CoverFlags(LE)
        // So we must: 1) swap each uint16 for endianness, 2) swap the positions of Flags and CoverFlags
        if (signature == "NVTR" && recordType == "NAVM" && data.Length % 16 == 0)
        {
            var nvtr = data.ToArray();
            var triangleCount = nvtr.Length / 16;
            for (var i = 0; i < triangleCount; i++)
            {
                var baseOffset = i * 16;
                // Swap all 8 uint16 values for endianness
                for (var j = 0; j < 8; j++)
                {
                    Swap2Bytes(nvtr, baseOffset + j * 2);
                }

                // Now swap positions of bytes 12-13 (CoverFlags) and 14-15 (Flags)
                // After endian swap, these need to be swapped in position
                var temp0 = nvtr[baseOffset + 12];
                var temp1 = nvtr[baseOffset + 13];
                nvtr[baseOffset + 12] = nvtr[baseOffset + 14];
                nvtr[baseOffset + 13] = nvtr[baseOffset + 15];
                nvtr[baseOffset + 14] = temp0;
                nvtr[baseOffset + 15] = temp1;
            }

            return nvtr;
        }

        // NVDP (NavMesh Door Links) - Each 8-byte entry: FormID(4), Triangle(2), Padding(2)
        // PDB: NavMeshTriangleDoorPortal is { pDoorForm(uint32, +0), iOwningTriangleIndex(uint16, +4) }
        // Disassembly confirms Endian() only swaps +0 (swap32) and +4 (swap16). Bytes +6-7 are struct padding.
        if (signature == "NVDP" && recordType == "NAVM" && data.Length % 8 == 0)
        {
            var nvdp = data.ToArray();
            var entryCount = nvdp.Length / 8;
            for (var i = 0; i < entryCount; i++)
            {
                var baseOffset = i * 8;
                // Swap FormID (4 bytes)
                Swap4Bytes(nvdp, baseOffset);
                // Swap Triangle index (2 bytes)
                Swap2Bytes(nvdp, baseOffset + 4);
                // Bytes +6-7 are struct padding — do NOT swap
            }

            return nvdp;
        }

        // Get schema for this subrecord
        var schema = SubrecordSchemaRegistry.GetSchema(signature, recordType, data.Length);

        if (schema == null)
        {
            // Navmesh subrecords require custom parsing logic - NOT fallbacks, these are properly handled
            if (signature == "NVMI" && recordType == "NAVI")
            {
                var navmi = data.ToArray();
                EsmSubrecordConverter.ConvertNvmi(navmi);
                return navmi;
            }

            if (signature == "NVCI" && recordType == "NAVI")
            {
                var nvci = data.ToArray();
                EsmSubrecordConverter.ConvertNvci(nvci);
                return nvci;
            }

            if (signature == "NVGD" && recordType == "NAVM")
            {
                var nvgd = data.ToArray();
                EsmSubrecordConverter.ConvertNvgd(nvgd);
                return nvgd;
            }

            return null; // No schema - caller should handle
        }

        var result = data.ToArray();

        // Handle special schema types
        if (ReferenceEquals(schema, SubrecordSchema.String) ||
            ReferenceEquals(schema, SubrecordSchema.ByteArray))
        {
            return result; // No conversion needed
        }

        if (ReferenceEquals(schema, SubrecordSchema.FormIdArray))
        {
            ConvertFormIdArray(result);
            return result;
        }

        if (ReferenceEquals(schema, SubrecordSchema.FloatArray))
        {
            ConvertFloatArray(result);
            return result;
        }

        if (ReferenceEquals(schema, SubrecordSchema.TextureHashes))
        {
            ConvertTextureHashes(result);
            return result;
        }

        // ATXT/BTXT: FormID swap at offset 0, Layer (uint16) swap at offset 6 are handled by the schema.
        // Byte 5 (PlatformFlag) must be set to 0x88 for PC — Xbox 360 stores 0x00, PC expects 0x88.
        if ((signature is "ATXT" or "BTXT") && result.Length >= 6)
        {
            result[5] = 0x88;
        }

        // NOTE: AIDT was previously zeroing bytes 5-7 thinking they were Xbox-specific,
        // but Xbox and PC have the same values there (e.g., 64 D8 23). The schema handles
        // AIDT correctly with Padding(3) preserving those bytes and proper byte-swapping
        // for ServiceFlags, TrainSkill, and TrainLevel.

        // Handle repeating arrays (ExpectedSize < 0 means repeat fields)
        if (schema.ExpectedSize < 0 && schema.Fields.Length > 0)
        {
            // Repeating structure - repeat fields until data is consumed
            var elementSize = schema.Fields.Sum(f => f.EffectiveSize);
            if (elementSize > 0 && data.Length % elementSize == 0)
            {
                var elementCount = data.Length / elementSize;
                for (var i = 0; i < elementCount; i++)
                {
                    ConvertFieldsAtOffset(result, i * elementSize, schema.Fields);
                }
            }
            else if (schema.Fields.Length == 1)
            {
                // Single repeating field
                ConvertSingleRepeatingField(result, schema.Fields[0]);
            }

            return result;
        }

        // Variable length (ExpectedSize == 0) or fixed size (ExpectedSize > 0)
        // Process fields once, leave any remaining bytes untouched
        ConvertFieldsAtOffset(result, 0, schema.Fields);
        return result;
    }

    /// <summary>
    ///     Converts fields at a given offset.
    /// </summary>
    private static void ConvertFieldsAtOffset(byte[] data, int baseOffset, SubrecordField[] fields)
    {
        var offset = baseOffset;

        foreach (var field in fields)
        {
            if (offset >= data.Length)
            {
                break;
            }

            var size = field.EffectiveSize;
            if (offset + size > data.Length)
            {
                break;
            }

            ConvertField(data, offset, field.Type);
            offset += size;
        }
    }

    /// <summary>
    ///     Converts a single field at the given offset.
    /// </summary>
    private static void ConvertField(byte[] data, int offset, SubrecordFieldType type)
    {
        switch (type)
        {
            case SubrecordFieldType.UInt16:
            case SubrecordFieldType.Int16:
                Swap2Bytes(data, offset);
                break;

            case SubrecordFieldType.UInt32:
            case SubrecordFieldType.Int32:
            case SubrecordFieldType.Float:
            case SubrecordFieldType.FormId:
                Swap4Bytes(data, offset);
                break;

            // FormIdLittleEndian: Already little-endian on Xbox 360, no swap needed
            case SubrecordFieldType.FormIdLittleEndian:
                break;

            // UInt16LittleEndian: Already little-endian on Xbox 360, no swap needed
            case SubrecordFieldType.UInt16LittleEndian:
                break;

            // UInt32WordSwapped: Xbox stores as two BE uint16 words in LE order
            // Xbox [A B C D] where AB=high word (BE), CD=low word (BE)
            // Swap each word: [B A D C] to get proper little-endian uint32
            case SubrecordFieldType.UInt32WordSwapped:
                if (offset + 4 <= data.Length)
                {
                    // Swap high word bytes (offset 0-1)
                    (data[offset], data[offset + 1]) = (data[offset + 1], data[offset]);
                    // Swap low word bytes (offset 2-3)
                    (data[offset + 2], data[offset + 3]) = (data[offset + 3], data[offset + 2]);
                }

                break;

            case SubrecordFieldType.UInt64:
            case SubrecordFieldType.Int64:
            case SubrecordFieldType.Double:
                Swap8Bytes(data, offset);
                break;

            case SubrecordFieldType.Vec3:
                Swap4Bytes(data, offset);
                Swap4Bytes(data, offset + 4);
                Swap4Bytes(data, offset + 8);
                break;

            case SubrecordFieldType.Quaternion:
                Swap4Bytes(data, offset);
                Swap4Bytes(data, offset + 4);
                Swap4Bytes(data, offset + 8);
                Swap4Bytes(data, offset + 12);
                break;

            case SubrecordFieldType.PosRot:
                // 6 floats
                for (var i = 0; i < 6; i++)
                {
                    Swap4Bytes(data, offset + i * 4);
                }

                break;

            case SubrecordFieldType.ColorArgb:
                // Convert Xbox 360 ARGB to PC RGBA
                // Xbox: [A][R][G][B] -> PC: [R][G][B][A]
                if (offset + 4 <= data.Length)
                {
                    var a = data[offset];
                    var r = data[offset + 1];
                    var g = data[offset + 2];
                    var b = data[offset + 3];
                    data[offset] = r;
                    data[offset + 1] = g;
                    data[offset + 2] = b;
                    data[offset + 3] = a;
                }

                break;

            case SubrecordFieldType.UInt8:
            case SubrecordFieldType.Int8:
            case SubrecordFieldType.ByteArray:
            case SubrecordFieldType.String:
            case SubrecordFieldType.ColorRgba:
            case SubrecordFieldType.Padding:
                // No conversion needed
                break;
        }
    }

    /// <summary>
    ///     Converts a single repeating field across the entire data.
    /// </summary>
    private static void ConvertSingleRepeatingField(byte[] data, SubrecordField field)
    {
        var size = field.EffectiveSize;
        if (size <= 0 || data.Length % size != 0)
        {
            return;
        }

        var count = data.Length / size;
        for (var i = 0; i < count; i++)
        {
            ConvertField(data, i * size, field.Type);
        }
    }

    /// <summary>
    ///     Converts an array of FormIDs (4 bytes each).
    /// </summary>
    private static void ConvertFormIdArray(byte[] data)
    {
        for (var i = 0; i < data.Length; i += 4)
        {
            Swap4Bytes(data, i);
        }
    }

    /// <summary>
    ///     Converts an array of floats (4 bytes each).
    /// </summary>
    private static void ConvertFloatArray(byte[] data)
    {
        for (var i = 0; i < data.Length; i += 4)
        {
            Swap4Bytes(data, i);
        }
    }

    /// <summary>
    ///     Converts texture hashes (8 bytes each).
    /// </summary>
    private static void ConvertTextureHashes(byte[] data)
    {
        for (var i = 0; i < data.Length; i += 8)
        {
            Swap8Bytes(data, i);
        }
    }

    /// <summary>
    ///     Converts PKDT (12 bytes) — Xbox 360 to PC byte order.
    ///     Layout (from PDB PACKAGE_DATA):
    ///     [0-3] iPackFlags (uint32) — endian swap (swap [0]↔[3] + swap [1]↔[2])
    ///     [4]   cPackType (byte) — no swap needed
    ///     [5]   Unused (byte)
    ///     [6-7] iFOBehaviorFlags (uint16) — endian swap
    ///     [8-9] iPackageSpecificFlags (uint16) — endian swap
    ///     [10-11] Unknown (bytes)
    /// </summary>
    private static void ConvertPkdt(byte[] data)
    {
        // Swap byte 0 and 3 (Flags1 <-> Type)
        (data[0], data[3]) = (data[3], data[0]);

        // Swap BE uint16 fields
        Swap2Bytes(data, 1); // Flags2
        Swap2Bytes(data, 6); // FalloutBehaviorFlags
        Swap2Bytes(data, 8); // TypeSpecificFlags
    }

    /// <summary>
    ///     Checks if a subrecord is a string (no conversion needed).
    /// </summary>
    public static bool IsStringSubrecord(string signature, string recordType)
    {
        return SubrecordSchemaRegistry.IsStringSubrecord(signature, recordType);
    }
}
