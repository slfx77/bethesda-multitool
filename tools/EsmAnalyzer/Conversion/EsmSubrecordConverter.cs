using static EsmAnalyzer.Conversion.EsmEndianHelpers;

namespace EsmAnalyzer.Conversion;

/// <summary>
///     Converts subrecord data based on type and parent record.
///     Handles endian conversion for all known subrecord formats.
/// </summary>
internal static partial class EsmSubrecordConverter
{
    /// <summary>
    ///     Converts subrecord data based on type.
    /// </summary>
    public static byte[] ConvertSubrecordData(string signature, ReadOnlySpan<byte> data, string recordType)
    {
        var result = data.ToArray();

        // String subrecords - no conversion needed
        if (IsStringSubrecord(signature, recordType)) return result;

        // IMAD (Image Space Adapter) records have subrecords containing float arrays
        // Handle these specially to avoid incorrect FormID conversion
        if (recordType == "IMAD") return ConvertImadSubrecord(signature, result);

        // Apply conversion based on subrecord type
        ConvertBySignature(signature, result, recordType);

        return result;
    }

    /// <summary>
    ///     Converts subrecord data based on signature.
    /// </summary>
    private static void ConvertBySignature(string signature, byte[] result, string recordType)
    {
        var dataLength = result.Length;

        switch (signature)
        {
            // Simple 4-byte FormID/uint32 swaps
            case "NAME":
            case "RNAM" when recordType is not "INFO" and not "CHAL" and not "CREA" and not "REGN":
            case "TPLT":
            case "VTCK":
            case "LNAM":
            case "LTMP":
            case "INAM":
            case "REPL":
            case "ZNAM":
            case "XOWN":
            case "XEZN":
            case "XCAS":
            case "XCIM":
            case "XCMO":
            case "XCWT":
            case "PKID":
            case "NAM6":
            case "NAM7":
            case "NAM8":
            case "NAM9" when recordType != "WRLD":
            case "HCLR":
            case "ETYP":
            case "WMI1":
            case "WMI2":
            case "WMI3":
            case "WMS1":
            case "WMS2":
            case "EFID":
            case "SCRI":
            case "CSCR":
            case "BIPL":
            case "EITM":
            case "TCLT":
            case "QSTI":
            case "SPLO":
            case "XCLW":
                Swap4Bytes(result, 0);
                break;

            case "HNAM" when dataLength == 4:
            case "ENAM" when dataLength == 4:
            case "UNAM" when recordType != "IMAD":
            case "WNAM" when recordType != "IMAD":
            case "YNAM" when recordType != "IMAD":
            case "PNAM" when dataLength == 4:
            case "NAM0" when dataLength == 4:
            case "VNAM" when dataLength == 4 && recordType != "IMAD":
            case "SNAM" when recordType == "WEAP" && dataLength == 4:
            case "TNAM" when dataLength == 4 && recordType != "IMAD":
            case "XNAM" when dataLength == 4 && recordType != "IMAD":
            case "BNAM" when dataLength == 4:
            case "EPFD" when dataLength == 4:
                Swap4Bytes(result, 0);
                break;

            // Simple 2-byte swaps
            case "NAM5" when dataLength == 2:
            case "EAMT":
            case "DNAM" when recordType == "TXST" && dataLength == 2:
                Swap2Bytes(result, 0);
                break;

            case "SNAM" when recordType == "RACE" && dataLength == 2:
                Swap2Bytes(result, 0);
                break;

            // Byte arrays - no conversion needed (array of uint8)
            case "ATTR" when dataLength == 7: // CLAS: 7 attributes (S.P.E.C.I.A.L)
                break;

            // LAND vertex data - byte arrays, no conversion needed
            // VNML: 33×33×3 vertex normals = 3267 bytes
            // VCLR: 33×33×3 vertex colors = 3267 bytes  
            // VHGT: 4-byte float offset + 33×33 signed bytes = 1093 bytes (only float needs swap)
            case "VNML":
            case "VCLR":
                // Pure byte arrays - no swap needed
                break;

            // SCDA - Compiled Script Data (bytecode) - no endian conversion needed
            // Script bytecode is already stored in little-endian format
            case "SCDA":
                break;

            // SLSD - Script Local Variable Data (24 bytes)
            // Structure: uint32 Index + unused(12) + uint8 Type + unused(7)
            case "SLSD" when dataLength == 24:
                Swap4Bytes(result, 0); // Index (uint32)
                // Rest is unused or single bytes - no swap needed
                break;

            // XNAM in FACT - Relation (12 bytes): FormID + int32 Modifier + uint32 Combat Reaction
            case "XNAM" when recordType == "FACT" && dataLength == 12:
                Swap4Bytes(result, 0);  // Faction FormID
                Swap4Bytes(result, 4);  // Modifier (int32)
                Swap4Bytes(result, 8);  // Group Combat Reaction (uint32)
                break;

            // OBND - 6 × int16
            case "OBND" when dataLength == 12:
                for (var i = 0; i < 6; i++) Swap2Bytes(result, i * 2);
                break;

            // DATA - varies by record type
            case "DATA":
                ConvertDataSubrecord(result, recordType);
                break;

            // ACBS - Actor Base Stats (24 bytes)
            case "ACBS" when dataLength == 24:
                ConvertAcbs(result);
                break;

            // AIDT - AI Data (20 bytes) - special handling
            case "AIDT" when dataLength == 20:
                ConvertAidt(result);
                break;

            // SNAM - varies by record type
            case "SNAM":
                ConvertSnam(result, recordType);
                break;

            // CNTO - Container item (8 bytes)
            case "CNTO" when dataLength == 8:
                Swap4Bytes(result, 0); // FormID
                Swap4Bytes(result, 4); // Count
                break;

            // CTDA - Condition (28 bytes)
            case "CTDA" when dataLength == 28:
                ConvertCtda(result);
                break;

            // DNAM - varies by record type
            case "DNAM":
                ConvertDnam(result, recordType);
                break;

            // XCLL - Cell lighting (40 bytes)
            case "XCLL" when dataLength == 40:
                for (var i = 0; i < 10; i++) Swap4Bytes(result, i * 4);
                break;

            // XNDP - Navigation Door Portal (8 bytes)
            case "XNDP" when dataLength == 8:
                Swap4Bytes(result, 0); // FormID (NAVM)
                Swap2Bytes(result, 4); // Navmesh triangle index
                break;

            // XESP - Enable Parent (8 bytes)
            case "XESP" when dataLength == 8:
                Swap4Bytes(result, 0); // Parent reference FormID
                Swap4Bytes(result, 4); // Flags
                break;

            // XTEL - Door Teleport (32 bytes)
            case "XTEL" when dataLength == 32:
                for (var i = 0; i < 8; i++) Swap4Bytes(result, i * 4);
                break;

            // XLKR - Linked Ref (8 bytes)
            case "XLKR" when dataLength == 8:
                Swap4Bytes(result, 0); // KYWD FormID
                Swap4Bytes(result, 4); // REFR FormID
                break;

            // XAPR - Activation Parent (8 bytes)
            case "XAPR" when dataLength == 8:
                Swap4Bytes(result, 0); // REFR FormID
                Swap4Bytes(result, 4); // Delay float
                break;

            // XLOC - Lock Information (20 bytes for FNV)
            case "XLOC" when dataLength == 20:
                Swap4Bytes(result, 4); // KEYM FormID at offset 4
                break;

            // XSCL - Scale (4 bytes float)
            case "XSCL" when dataLength == 4:
                Swap4Bytes(result, 0);
                break;

            // XCNT - Item Count (4 bytes uint32)
            case "XCNT" when dataLength == 4:
                Swap4Bytes(result, 0);
                break;

            // XRDS - Radius (4 bytes float)
            case "XRDS" when dataLength == 4:
                Swap4Bytes(result, 0);
                break;

            // EFIT - Effect item (20 bytes) - 5 × uint32
            case "EFIT" when dataLength == 20:
                for (var i = 0; i < 5; i++) Swap4Bytes(result, i * 4);
                break;

            // PKDT - Package Data (12 bytes)
            // Structure: uint32 flags + uint8 type + uint8 unused + uint16 behaviorFlags + uint16 typeFlags + uint16 unknown
            case "PKDT" when dataLength == 12:
                Swap4Bytes(result, 0);  // General Flags (uint32)
                // Bytes 4-5: Type + Unused (single bytes, no swap)
                Swap2Bytes(result, 6);  // Fallout Behavior Flags (uint16)
                Swap2Bytes(result, 8);  // Type Specific Flags (uint16)
                Swap2Bytes(result, 10); // Unknown (uint16)
                break;

            // PSDT - Package Schedule Data (8 bytes)
            // Structure: uint8 month + uint8 dayOfWeek + uint8 date + int8 time + int32 duration
            case "PSDT" when dataLength == 8:
                // Bytes 0-3: single bytes (no swap)
                Swap4Bytes(result, 4);  // Duration (int32)
                break;

            // ENIT - varies by record type
            case "ENIT":
                ConvertEnit(result, recordType);
                break;

            // CRDT - Critical data (16 bytes)
            case "CRDT" when dataLength == 16:
                Swap2Bytes(result, 0); // Damage (ushort)
                Swap2Bytes(result, 2); // Unknown (ushort)
                Swap4Bytes(result, 4); // Multiplier (float)
                break;

            // SNDD - Sound Data (36 bytes) in SOUN
            // Structure: uint8 + uint8 + int8 + unused(1) + uint32 flags + int16 + uint8 + uint8
            //            + int16[5] attenuation + int16 reverb + int32 priority + int32 loopBegin + int32 loopEnd
            case "SNDD" when recordType == "SOUN" && dataLength == 36:
                // Bytes 0-3: uint8, uint8, int8, unused - no swap
                Swap4Bytes(result, 4);  // Flags (uint32)
                Swap2Bytes(result, 8);  // Static attenuation (int16)
                // Bytes 10-11: uint8, uint8 - no swap
                for (var i = 0; i < 5; i++) Swap2Bytes(result, 12 + i * 2);  // Attenuation curve (5 × int16)
                Swap2Bytes(result, 22); // Reverb attenuation control (int16)
                Swap4Bytes(result, 24); // Priority (int32)
                Swap4Bytes(result, 28); // Loop begin (int32)
                Swap4Bytes(result, 32); // Loop end (int32)
                break;

            // DODT - Decal Data (36 bytes)
            // Structure: 7× float (MinWidth, MaxWidth, MinHeight, MaxHeight, Depth, Shininess, ParallaxScale)
            //            + uint8 (Passes) + uint8 (Flags) + 2 unused + 4-byte color
            case "DODT" when dataLength == 36:
                for (var i = 0; i < 7; i++) Swap4Bytes(result, i * 4); // 7 floats (28 bytes)
                // Bytes 28-31: uint8 Passes, uint8 Flags, 2 unused - no swap
                // Bytes 32-35: ByteColors (4 bytes RGBA) - no swap
                break;

            // DAT2 - AMMO secondary data (20 bytes)
            case "DAT2" when dataLength == 20:
                Swap4Bytes(result, 8);
                Swap4Bytes(result, 16);
                break;

            // VATS data (20 bytes) - 5 × uint32
            case "VATS" when dataLength == 20:
                for (var i = 0; i < 5; i++) Swap4Bytes(result, i * 4);
                break;

            // ATXT/BTXT - Texture alpha (8 bytes)
            case "ATXT":
            case "BTXT":
                if (dataLength == 8)
                {
                    Swap4Bytes(result, 0); // FormID
                    result[5] = 0x88; // Platform flag - set to PC value
                    Swap2Bytes(result, 6); // Layer
                }

                break;

            // VTXT - Vertex texture (8 bytes per entry)
            case "VTXT":
                ConvertVtxt(result);
                break;

            // VHGT - Height data (1096 bytes)
            case "VHGT" when dataLength == 1096:
                Swap4Bytes(result, 0); // Height offset (float)
                break;

            // HNAM - LTEX Havok Data (3 bytes) - uint8 values
            case "HNAM" when recordType == "LTEX" && dataLength == 3:
                // All bytes are single-byte values - no swap needed
                break;

            // HNAM - Race hair (24 bytes when in RACE) - array of 6 FormIDs
            case "HNAM" when recordType == "RACE" && dataLength == 24:
                for (var i = 0; i < 6; i++) Swap4Bytes(result, i * 4);
                break;

            // HNAM - Race hairs (array of FormIDs, variable length)
            case "HNAM" when recordType == "RACE" && dataLength % 4 == 0:
                for (var i = 0; i < dataLength / 4; i++) Swap4Bytes(result, i * 4);
                break;

            // ENAM - Race eyes (16 bytes when in RACE)
            case "ENAM" when recordType == "RACE" && dataLength == 16:
                for (var i = 0; i < 4; i++) Swap4Bytes(result, i * 4);
                break;

            // ENAM - Race eyes (array of FormIDs, variable length)
            case "ENAM" when recordType == "RACE" && dataLength % 4 == 0:
                for (var i = 0; i < dataLength / 4; i++) Swap4Bytes(result, i * 4);
                break;

            // FGGS/FGTS - Face geometry (200 bytes) - floats
            case "FGGS" when dataLength == 200:
            case "FGTS" when dataLength == 200:
                for (var i = 0; i < 50; i++) Swap4Bytes(result, i * 4);
                break;

            // FGGA - Facegen Asymmetric Geometry (120 bytes) - array of 30 floats
            case "FGGA" when dataLength == 120:
                for (var i = 0; i < 30; i++) Swap4Bytes(result, i * 4);
                break;

            // BMDT - Biped model data (8 bytes)
            case "BMDT" when dataLength == 8:
                Swap4Bytes(result, 0); // Biped flags
                break;

            // MODT/MO2T/MO3T/MO4T/DMDT - Model texture hashes (24 bytes per texture)
            // Structure per texture: uint64 PC Hash + uint64 Console Hash + uint64 Folder Hash
            case "MODT":
            case "MO2T":
            case "MO3T":
            case "MO4T":
            case "DMDT":
                for (var i = 0; i < dataLength / 8; i++) Swap8Bytes(result, i * 8);
                break;

            // NAM2 - Model info (texture hashes) in PROJ (24 bytes per texture)
            case "NAM2" when recordType == "PROJ" && dataLength % 8 == 0:
                for (var i = 0; i < dataLength / 8; i++) Swap8Bytes(result, i * 8);
                break;

            // MO*S - Model shader data (variable)
            case "MO2S":
            case "MO3S":
            case "MO4S":
            case "MODS":
                if (dataLength >= 4) Swap4Bytes(result, 0); // Count
                break;

            // NIFT - NIF data (variable)
            case "NIFT":
                if (dataLength >= 4) Swap4Bytes(result, 0); // Count/size
                break;

            // INDX - Index (4 bytes)
            case "INDX" when dataLength == 4:
                Swap4Bytes(result, 0);
                break;

            // HEDR - File header (12 bytes)
            case "HEDR" when dataLength == 12:
                Swap4Bytes(result, 0); // Version float
                Swap4Bytes(result, 4); // NumRecords
                Swap4Bytes(result, 8); // NextObjectId
                break;

            // SPIT - Spell Data (16 bytes)
            // Structure: uint32 Type + uint32 Cost + uint32 Level + uint8 Flags + unused(3)
            case "SPIT" when recordType == "SPEL" && dataLength == 16:
                Swap4Bytes(result, 0);  // Type (uint32)
                Swap4Bytes(result, 4);  // Cost (uint32)
                Swap4Bytes(result, 8);  // Level (uint32)
                // Byte 12: Flags (uint8) + 3 unused - no swap
                break;

            // DEST - Destructible Header (8 bytes)
            // Structure: int32 Health + uint8 Count + uint8 Flags + unused(2)
            case "DEST" when dataLength == 8:
                Swap4Bytes(result, 0); // Health (int32)
                // Remaining bytes are single-byte/unused - no swap
                break;

            // DSTD - Destruction Stage Data (20 bytes)
            // Structure: uint8 Health% + uint8 Index + uint8 DamageStage + uint8 Flags +
            //            int32 SelfDamagePerSecond + FormID Explosion + FormID Debris + int32 DebrisCount
            case "DSTD" when dataLength == 20:
                // Bytes 0-3: single bytes - no swap
                Swap4Bytes(result, 4);  // Self Damage per Second (int32)
                Swap4Bytes(result, 8);  // Explosion FormID (uint32)
                Swap4Bytes(result, 12); // Debris FormID (uint32)
                Swap4Bytes(result, 16); // Debris Count (int32)
                break;

            // COED - Extra Data (12 bytes)
            // Structure: FormID Owner + (FormID Global or int32 Required Rank) + float Item Condition
            case "COED" when dataLength == 12:
                Swap4Bytes(result, 0); // Owner FormID
                Swap4Bytes(result, 4); // Global FormID or Required Rank (int32)
                Swap4Bytes(result, 8); // Item Condition (float)
                break;

            // CNAM - Tree Data (32 bytes) in TREE
            // Structure: 7 floats + int32 Shadow Radius
            case "CNAM" when recordType == "TREE" && dataLength == 32:
                for (var i = 0; i < 8; i++) Swap4Bytes(result, i * 4);
                break;

            // BNAM - Billboard Dimensions (8 bytes) in TREE
            // Structure: float Width + float Height
            case "BNAM" when recordType == "TREE" && dataLength == 8:
                Swap4Bytes(result, 0);
                Swap4Bytes(result, 4);
                break;

            // LVLO - Leveled List Entry (12 bytes)
            // Structure: uint16 Level + unused(2) + FormID + uint16 Count + unused(2)
            case "LVLO" when dataLength == 12:
                Swap2Bytes(result, 0); // Level (uint16)
                Swap4Bytes(result, 4); // Entry FormID
                Swap2Bytes(result, 8); // Count (uint16)
                break;

            // IDLA - Idle Marker Animations (array of FormIDs)
            case "IDLA" when dataLength % 4 == 0:
                for (var i = 0; i < dataLength / 4; i++) Swap4Bytes(result, i * 4);
                break;

            // PNAM - Weather Cloud Colors (byte colors, no endian swap)
            case "PNAM" when recordType == "WTHR":
                break;

            // NAM0 - Weather Colors (byte colors, no endian swap)
            case "NAM0" when recordType == "WTHR":
                break;

            // FNAM - Weather Fog Distance (24 bytes) in WTHR
            // Structure: 6 floats (Day/Night Near/Far + Day/Night Power)
            case "FNAM" when recordType == "WTHR" && dataLength == 24:
                for (var i = 0; i < 6; i++) Swap4Bytes(result, i * 4);
                break;

            // WLST - Weather Types array in CLMT (12 bytes per entry)
            // Structure per entry: FormID (Weather) + int32 (Chance) + FormID (Global)
            case "WLST":
                for (var i = 0; i < dataLength; i += 12)
                {
                    Swap4Bytes(result, i);     // Weather FormID
                    Swap4Bytes(result, i + 4); // Chance (int32)
                    Swap4Bytes(result, i + 8); // Global FormID
                }
                break;

            // TNAM - Climate Timing (6 bytes) in CLMT
            // Structure: sunrise begin/end (2 bytes), sunset begin/end (2 bytes), volatility (1), phase length (1)
            // All single bytes, no endian swap needed
            case "TNAM" when recordType == "CLMT" && dataLength == 6:
                break;

            // RPLI - Region Point List Index (4 bytes) - edge fall-off uint32
            case "RPLI":
                Swap4Bytes(result, 0);
                break;

            // RPLD - Region Point List Data (array of X,Y float pairs)
            case "RPLD":
                for (var i = 0; i < dataLength; i += 4)
                    Swap4Bytes(result, i);
                break;

            // RDAT - Region Data Header (8 bytes)
            // Structure: uint32 Type + byte Override + byte Priority + 2 bytes unused
            case "RDAT" when dataLength == 8:
                Swap4Bytes(result, 0); // Type
                // bytes 4-7: single bytes, no swap needed
                break;

            // RDSD - Region Sounds array (12 bytes per entry)
            // Structure: FormID Sound + uint32 Flags + uint32 Chance
            case "RDSD":
                for (var i = 0; i < dataLength; i += 12)
                {
                    Swap4Bytes(result, i);     // Sound FormID
                    Swap4Bytes(result, i + 4); // Flags
                    Swap4Bytes(result, i + 8); // Chance
                }
                break;

            // RDID - Region Imposters (array of FormIDs)
            case "RDID":
                for (var i = 0; i < dataLength; i += 4)
                    Swap4Bytes(result, i);
                break;

            // RDWT - Region Weather Types (12 bytes per entry)
            // Structure: FormID Weather + uint32 Chance + FormID Global
            case "RDWT":
                for (var i = 0; i < dataLength; i += 12)
                {
                    Swap4Bytes(result, i);     // Weather FormID
                    Swap4Bytes(result, i + 4); // Chance
                    Swap4Bytes(result, i + 8); // Global FormID
                }
                break;

            // RDOT - Region Objects (52 bytes per entry)
            // Complex structure with FormID, floats, uint16s, and bytes
            case "RDOT":
                for (var i = 0; i < dataLength; i += 52)
                {
                    Swap4Bytes(result, i);      // Object FormID
                    Swap2Bytes(result, i + 4);  // Parent Index (uint16)
                    // i + 6: unused 2 bytes
                    Swap4Bytes(result, i + 8);  // Density (float)
                    // i + 12: 4 single bytes (Clustering, Min Slope, Max Slope, Flags)
                    Swap2Bytes(result, i + 16); // Radius wrt Parent (uint16)
                    Swap2Bytes(result, i + 18); // Radius (uint16)
                    Swap4Bytes(result, i + 20); // Min Height (float)
                    Swap4Bytes(result, i + 24); // Max Height (float)
                    Swap4Bytes(result, i + 28); // Sink (float)
                    Swap4Bytes(result, i + 32); // Sink Variance (float)
                    Swap4Bytes(result, i + 36); // Size Variance (float)
                    Swap2Bytes(result, i + 40); // Angle Variance X (uint16)
                    Swap2Bytes(result, i + 42); // Angle Variance Y (uint16)
                    Swap2Bytes(result, i + 44); // Angle Variance Z (uint16)
                    // i + 46: unused 2 bytes + unknown 4 bytes
                }
                break;

            // NVMI - Navmesh Info (variable size) in NAVI
            // Complex structure with FormIDs, floats, and optional island data
            case "NVMI":
                ConvertNvmi(result);
                break;

            // NVCI - Navmesh Connection Info (variable size) in NAVI
            // Structure: FormID Navmesh + 3 variable-length arrays of FormIDs
            case "NVCI":
                ConvertNvci(result);
                break;

            // NVVX - Navmesh Vertices (array of Vec3, 12 bytes each)
            case "NVVX":
                for (var i = 0; i < dataLength; i += 4)
                    Swap4Bytes(result, i);
                break;

            // NVTR - Navmesh Triangles (16 bytes each)
            // Structure: 3 × uint16 vertices + 3 × int16 edges + uint16 flags + uint16 cover flags
            case "NVTR":
                for (var i = 0; i < dataLength; i += 16)
                {
                    Swap2Bytes(result, i);      // Vertex 0
                    Swap2Bytes(result, i + 2);  // Vertex 1
                    Swap2Bytes(result, i + 4);  // Vertex 2
                    Swap2Bytes(result, i + 6);  // Edge 0-1
                    Swap2Bytes(result, i + 8);  // Edge 1-2
                    Swap2Bytes(result, i + 10); // Edge 2-0
                    Swap2Bytes(result, i + 12); // Flags
                    Swap2Bytes(result, i + 14); // Cover Flags
                }
                break;

            // NVCA - Cover Triangles (array of uint16)
            case "NVCA":
                for (var i = 0; i < dataLength; i += 2)
                    Swap2Bytes(result, i);
                break;

            // NVDP - Navmesh Door Links (8 bytes each)
            // Structure: FormID Door Ref + uint16 Triangle + 2 unused
            case "NVDP":
                for (var i = 0; i < dataLength; i += 8)
                {
                    Swap4Bytes(result, i);     // Door Ref FormID
                    Swap2Bytes(result, i + 4); // Triangle
                    // i + 6: unused 2 bytes
                }
                break;

            // NVGD - Navmesh Grid (variable size)
            // Structure: uint32 Divisor + 2 floats + 2 Vec3 (bounds) + variable cells array
            case "NVGD":
                ConvertNvgd(result);
                break;

            // NVEX - Navmesh Edge Links (10 bytes each)
            // Structure: uint32 Type + FormID Navmesh + uint16 Triangle
            case "NVEX":
                for (var i = 0; i < dataLength; i += 10)
                {
                    Swap4Bytes(result, i);     // Type
                    Swap4Bytes(result, i + 4); // Navmesh FormID
                    Swap2Bytes(result, i + 8); // Triangle
                }
                break;

            // XRGD - Ragdoll Bone Data (28 bytes per bone)
            // Structure: byte BoneId + 3 unused + Vec3 Position + Vec3 Rotation
            case "XRGD":
                for (var i = 0; i < dataLength; i += 28)
                {
                    // i: BoneId (1 byte) + unused (3 bytes) - no swap
                    // Position Vec3 (12 bytes)
                    Swap4Bytes(result, i + 4);
                    Swap4Bytes(result, i + 8);
                    Swap4Bytes(result, i + 12);
                    // Rotation Vec3 (12 bytes)
                    Swap4Bytes(result, i + 16);
                    Swap4Bytes(result, i + 20);
                    Swap4Bytes(result, i + 24);
                }
                break;

            // XPWR - Water Reflection/Refraction (8 bytes)
            // Structure: FormID Reference + uint32 Type
            case "XPWR":
                Swap4Bytes(result, 0); // Reference FormID
                Swap4Bytes(result, 4); // Type
                break;

            // XMBO - Bound Half Extents (Vec3, 12 bytes)
            case "XMBO":
                Swap4Bytes(result, 0);
                Swap4Bytes(result, 4);
                Swap4Bytes(result, 8);
                break;

            // XPRM - Primitive (32 bytes)
            // Structure: Vec3 Bounds + Vec3 Colors + Unknown (4) + uint32 Type
            case "XPRM" when dataLength == 32:
                // Bounds (12 bytes)
                Swap4Bytes(result, 0);
                Swap4Bytes(result, 4);
                Swap4Bytes(result, 8);
                // Colors (12 bytes)
                Swap4Bytes(result, 12);
                Swap4Bytes(result, 16);
                Swap4Bytes(result, 20);
                // Unknown (4 bytes) - swap just in case
                Swap4Bytes(result, 24);
                // Type (4 bytes)
                Swap4Bytes(result, 28);
                break;

            // XPOD - Portal Data (8 bytes = 2 FormIDs)
            case "XPOD" when dataLength == 8:
                Swap4Bytes(result, 0);
                Swap4Bytes(result, 4);
                break;

            // XRGB - Biped Rotation (Vec3, 12 bytes)
            case "XRGB":
                Swap4Bytes(result, 0);
                Swap4Bytes(result, 4);
                Swap4Bytes(result, 8);
                break;

            // XRDO - Radio Data (16 bytes)
            // Structure: float Range + uint32 Type + float StaticPct + FormID PositionRef
            case "XRDO" when dataLength == 16:
                Swap4Bytes(result, 0);  // Range
                Swap4Bytes(result, 4);  // Type
                Swap4Bytes(result, 8);  // Static Percentage
                Swap4Bytes(result, 12); // Position Reference FormID
                break;

            // XOCP - Occlusion Plane Data (36 bytes)
            // Structure: Size(2 floats) + Position(Vec3) + Rotation Quaternion(4 floats)
            case "XOCP" when dataLength == 36:
                for (int i = 0; i < 36; i += 4)
                    Swap4Bytes(result, i);
                break;

            // XORD - Linked Occlusion Planes (16 bytes = 4 FormIDs)
            case "XORD" when dataLength == 16:
                Swap4Bytes(result, 0);  // Right
                Swap4Bytes(result, 4);  // Left
                Swap4Bytes(result, 8);  // Bottom
                Swap4Bytes(result, 12); // Top
                break;

            // MNAM - World Map Data in WRLD (16 bytes)
            // Structure: 2×int32 (Usable X,Y) + 4×int16 (NW Cell X,Y, SE Cell X,Y)
            case "MNAM" when recordType == "WRLD" && dataLength == 16:
                Swap4Bytes(result, 0);  // Usable X
                Swap4Bytes(result, 4);  // Usable Y
                Swap2Bytes(result, 8);  // NW Cell X
                Swap2Bytes(result, 10); // NW Cell Y
                Swap2Bytes(result, 12); // SE Cell X
                Swap2Bytes(result, 14); // SE Cell Y
                break;

            // NAM0 - Worldspace Bounds Min (8 bytes = 2 floats)
            case "NAM0" when recordType == "WRLD" && dataLength == 8:
                Swap4Bytes(result, 0);  // X
                Swap4Bytes(result, 4);  // Y
                break;

            // NAM9 - Worldspace Bounds Max (8 bytes = 2 floats)
            case "NAM9" when recordType == "WRLD" && dataLength == 8:
                Swap4Bytes(result, 0);  // X
                Swap4Bytes(result, 4);  // Y
                break;

            // XCLC - CELL Grid (12 bytes)
            // Structure: int32 X + int32 Y + uint8 LandFlags + 3 unused
            case "XCLC" when dataLength == 12:
                Swap4Bytes(result, 0);  // X
                Swap4Bytes(result, 4);  // Y
                // byte 8 is LandFlags (uint8, no swap)
                // bytes 9-11 are unused
                break;

            // XCLR - CELL Regions (array of FormIDs)
            case "XCLR":
                for (int i = 0; i < dataLength; i += 4)
                    Swap4Bytes(result, i);
                break;

            // TRDT - INFO Response Data (24 bytes)
            // Structure: uint32 EmotionType + int32 EmotionValue + 4 unused + uint8 + 3 unused + FormID Sound + uint8 + 3 unused
            case "TRDT" when dataLength == 24:
                Swap4Bytes(result, 0);  // Emotion Type
                Swap4Bytes(result, 4);  // Emotion Value
                // 8-11 unused
                // 12 ResponseNumber (uint8)
                // 13-15 unused
                Swap4Bytes(result, 16); // Sound FormID
                // 20 UseEmotionAnim (uint8)
                // 21-23 unused
                break;

            // QSTA - QUST Target (8 bytes)
            // Structure: FormID Target + uint8 Flags + 3 unused
            case "QSTA" when dataLength == 8:
                Swap4Bytes(result, 0);  // Target FormID
                // 4 is Flags (uint8), 5-7 unused
                break;

            // ANAM - IDLE Animations / CPTH Camera Paths (8 bytes = 2 FormIDs)
            case "ANAM" when recordType is "IDLE" or "CPTH" && dataLength == 8:
                Swap4Bytes(result, 0);  // Parent FormID
                Swap4Bytes(result, 4);  // Previous FormID
                break;

            // PTDT - PACK Target (16 bytes)
            // Structure: int32 Type + 4-byte union (FormID/uint32) + int32 Count + float Unknown
            case "PTDT" when dataLength == 16:
            case "PTD2" when dataLength == 16:
                Swap4Bytes(result, 0);  // Type
                Swap4Bytes(result, 4);  // Union (FormID or uint32)
                Swap4Bytes(result, 8);  // Count/Distance
                Swap4Bytes(result, 12); // Unknown float
                break;

            // PKDD - PACK Dialogue Data (24 bytes)
            // Structure: float FOV + FormID Topic + uint32 Flags + 4 unused + uint32 DialogueType + 4 unknown
            case "PKDD" when dataLength == 24:
                Swap4Bytes(result, 0);  // FOV
                Swap4Bytes(result, 4);  // Topic FormID
                Swap4Bytes(result, 8);  // Flags
                // 12-15 unused
                Swap4Bytes(result, 16); // Dialogue Type
                // 20-23 unknown (byte array or could be uint32)
                Swap4Bytes(result, 20); // Unknown
                break;

            // PLDT - PACK Location 1 (12 bytes)
            // Structure: int32 Type + 4-byte union (FormID/uint32) + int32 Radius
            case "PLDT" when dataLength == 12:
            case "PLD2" when dataLength == 12:
                Swap4Bytes(result, 0);  // Type
                Swap4Bytes(result, 4);  // Union (FormID/uint32/unused)
                Swap4Bytes(result, 8);  // Radius
                break;

            // PKW3 - PACK Use Weapon Data (24 bytes)
            // Structure: uint32 Flags + uint8×2 + uint16×3 + float×2 + 4 unused
            case "PKW3" when dataLength == 24:
                Swap4Bytes(result, 0);  // Flags
                // 4,5 are uint8 (no swap)
                Swap2Bytes(result, 6);  // Number of Bursts
                Swap2Bytes(result, 8);  // Min Shoots
                Swap2Bytes(result, 10); // Max Shoots
                Swap4Bytes(result, 12); // Min Pause
                Swap4Bytes(result, 16); // Max Pause
                // 20-23 unused
                break;

            // CSTD - Combat Style Advanced Standard (92 bytes)
            // Mixed uint8, uint16, floats - see wbDefinitionsFNV.pas for layout
            case "CSTD" when dataLength == 92:
                // uint8 at 0,1, unused at 2-3
                Swap4Bytes(result, 4);   // float
                Swap4Bytes(result, 8);   // float
                Swap4Bytes(result, 12);  // float
                Swap4Bytes(result, 16);  // float
                Swap4Bytes(result, 20);  // float
                Swap4Bytes(result, 24);  // float
                Swap4Bytes(result, 28);  // float
                Swap4Bytes(result, 32);  // float
                // uint8 at 36,37, unused at 38-39
                Swap4Bytes(result, 40);  // float
                Swap4Bytes(result, 44);  // float
                Swap4Bytes(result, 48);  // float
                // uint8 at 52, unused at 53-55
                Swap4Bytes(result, 56);  // float
                Swap4Bytes(result, 60);  // float
                // uint8 at 64,65,66,67,68, unused at 69-71
                Swap4Bytes(result, 72);  // float
                Swap4Bytes(result, 76);  // float
                Swap2Bytes(result, 80);  // uint16 Flags
                // unused 82-83, uint8 at 84,85, unused 86-87
                Swap4Bytes(result, 88);  // float
                break;

            // CSAD - Combat Style Advanced (84 bytes = 21 floats)
            case "CSAD" when dataLength == 84:
                for (int i = 0; i < 84; i += 4)
                    Swap4Bytes(result, i);
                break;

            // CSSD - Combat Style Simple (64 bytes)
            // All floats except uint32 at offset 40
            case "CSSD" when dataLength == 64:
                for (int i = 0; i < 64; i += 4)
                    Swap4Bytes(result, i);
                break;

            // GNAM - WATR Related Waters (12 bytes = 3 FormIDs)
            case "GNAM" when recordType == "WATR" && dataLength == 12:
                Swap4Bytes(result, 0);  // Daytime
                Swap4Bytes(result, 4);  // Nighttime
                Swap4Bytes(result, 8);  // Underwater
                break;

            // PRKE - PERK Effect Header (3 bytes = 3 uint8s - no swap)
            case "PRKE" when dataLength == 3:
                // All uint8, no conversion needed
                break;

            // BPND - Body Part Node Data (84 bytes)
            // Complex mix: float, uint8s, uint16, FormIDs, Vec3PosRot
            case "BPND" when dataLength == 84:
                Swap4Bytes(result, 0);   // Damage Mult (float)
                // 4-9: uint8s (no swap)
                Swap2Bytes(result, 10);  // Debris Count (uint16)
                Swap4Bytes(result, 12);  // Debris FormID
                Swap4Bytes(result, 16);  // Explosion FormID
                Swap4Bytes(result, 20);  // Tracking Max Angle (float)
                Swap4Bytes(result, 24);  // Debris Scale (float)
                Swap4Bytes(result, 28);  // Severable Debris Count (int32)
                Swap4Bytes(result, 32);  // Severable Debris FormID
                Swap4Bytes(result, 36);  // Severable Explosion FormID
                Swap4Bytes(result, 40);  // Severable Debris Scale (float)
                // 44-67: Vec3PosRot (6 floats)
                for (int i = 44; i < 68; i += 4)
                    Swap4Bytes(result, i);
                Swap4Bytes(result, 68);  // Severable Impact FormID
                Swap4Bytes(result, 72);  // Explodable Impact FormID
                // 76-79: uint8s + unused
                Swap4Bytes(result, 80);  // Limb Replacement Scale (float)
                break;

            // NAM5 - Model Info in BPTD (texture paths/counts - variable structure based on FormVersion)
            // In FNV, typically texture file path arrays - byte data, no swap needed
            case "NAM5" when recordType == "BPTD":
                // Model info contains texture paths and counts - mostly string/byte data
                // The first 4 bytes are a count if FormVersion >= 40
                // For safety, just swap the count (if present) and leave rest as-is
                if (dataLength >= 4)
                    Swap4Bytes(result, 0);  // Texture count
                break;

            // RAFD - Ragdoll Feedback Data (60 bytes = 15 floats/int32s)
            case "RAFD" when dataLength == 60:
                for (var i = 0; i < 60; i += 4)
                    Swap4Bytes(result, i);
                break;

            // RAPS - Ragdoll Pose Matching Data (24 bytes)
            // Structure: 3 uint16s (bones) + uint8 flags + unused + 4 floats
            case "RAPS" when dataLength == 24:
                Swap2Bytes(result, 0);  // Bone 0
                Swap2Bytes(result, 2);  // Bone 1
                Swap2Bytes(result, 4);  // Bone 2
                // 6: uint8 flags, 7: unused
                Swap4Bytes(result, 8);   // Motors Strength
                Swap4Bytes(result, 12);  // Pose Activation Delay Time
                Swap4Bytes(result, 16);  // Match Error Allowance
                Swap4Bytes(result, 20);  // Displacement To Disable
                break;

            // RAFB - Ragdoll Feedback Dynamic Bones (array of uint16)
            case "RAFB":
                for (var i = 0; i < dataLength; i += 2)
                    Swap2Bytes(result, i);
                break;

            // SCHR - Script Header (20 bytes)
            // Structure: unused(4) + uint32 RefCount + uint32 CompiledSize + uint32 VariableCount + uint16 Type + uint16 Flags
            case "SCHR" when dataLength == 20:
                // Bytes 0-3: unused - no swap
                Swap4Bytes(result, 4);  // RefCount (uint32)
                Swap4Bytes(result, 8);  // CompiledSize (uint32)
                Swap4Bytes(result, 12); // VariableCount (uint32)
                Swap2Bytes(result, 16); // Type (uint16)
                Swap2Bytes(result, 18); // Flags (uint16)
                break;

            // Default: throw error for unknown subrecords so we can add explicit handlers
            default:
                ConvertUnknownSubrecord(result, signature, recordType);
                break;
        }
    }
}