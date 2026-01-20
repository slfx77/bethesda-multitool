namespace EsmAnalyzer.Conversion;

/// <summary>
///     Low-level byte swapping utilities for endian conversion.
/// </summary>
internal static class EsmEndianHelpers
{
    /// <summary>
    ///     Swaps 2 bytes at the given offset (big-endian ↔ little-endian).
    /// </summary>
    public static void Swap2Bytes(byte[] data, int offset)
    {
        if (offset + 2 > data.Length) return;

        (data[offset], data[offset + 1]) = (data[offset + 1], data[offset]);
    }

    /// <summary>
    ///     Swaps 4 bytes at the given offset (big-endian ↔ little-endian).
    /// </summary>
    public static void Swap4Bytes(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return;

        (data[offset], data[offset + 3]) = (data[offset + 3], data[offset]);
        (data[offset + 1], data[offset + 2]) = (data[offset + 2], data[offset + 1]);
    }

    /// <summary>
    ///     Swaps 8 bytes at the given offset (big-endian ↔ little-endian uint64).
    /// </summary>
    public static void Swap8Bytes(byte[] data, int offset)
    {
        if (offset + 8 > data.Length) return;

        // Reverse all 8 bytes
        (data[offset], data[offset + 7]) = (data[offset + 7], data[offset]);
        (data[offset + 1], data[offset + 6]) = (data[offset + 6], data[offset + 1]);
        (data[offset + 2], data[offset + 5]) = (data[offset + 5], data[offset + 2]);
        (data[offset + 3], data[offset + 4]) = (data[offset + 4], data[offset + 3]);
    }

    /// <summary>
    ///     Checks if a string looks like a valid ESM subrecord signature.
    ///     Valid signatures contain only uppercase letters A-Z, digits 0-9, or underscore.
    ///     Special case: IMAD records use keyed *IAD subrecords where the first character
    ///     can be a control character or symbol (0x00-0x7F), followed by "IAD".
    ///     Examples: \x00IAD, @IAD, AIAD, BIAD, etc.
    /// </summary>
    public static bool IsValidSubrecordSignature(string signature)
    {
        if (signature.Length != 4) return false;

        // Special case: IMAD keyed *IAD subrecords
        // The last 3 characters are "IAD" and the first can be any byte (0x00-0x7F typically)
        if (signature[1] == 'I' && signature[2] == 'A' && signature[3] == 'D')
        {
            // First char can be: 0x00-0x09 (control chars), 0x0A-0x0F (\n, \r, etc.), 
            // 0x40 (@), 0x41-0x54 (A-T keys)
            var firstChar = signature[0];
            if (firstChar <= 0x7F) // Any ASCII character is valid for the key byte
                return true;
        }

        foreach (var c in signature)
            if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
                return false;

        return true;
    }

    /// <summary>
    ///     Checks if a subrecord contains string data (no endian conversion needed).
    /// </summary>
    public static bool IsStringSubrecord(string signature, string recordType)
    {
        // TES4-specific strings (author, description, master file names)
        if (recordType == "TES4")
            if (signature is "CNAM" or "SNAM" or "MAST")
                return true;

        // INFO/CHAL RNAM is a prompt/result string, not a FormID
        if (signature == "RNAM" && recordType is "INFO" or "CHAL")
            return true;

        // NOTE TNAM is text (when DATA != 3), XNAM is texture path
        if (recordType == "NOTE" && signature is "TNAM" or "XNAM")
            return true;

        // CELL XNAM is water noise texture path
        if (recordType == "CELL" && signature == "XNAM")
            return true;

        // WRLD XNAM is water noise texture path, NNAM is canopy shadow
        if (recordType == "WRLD" && signature is "XNAM" or "NNAM")
            return true;

        // INFO NAM1 is Response Text, NAM2 is Script Notes, NAM3 is Edits - all strings
        if (recordType == "INFO" && signature is "NAM1" or "NAM2" or "NAM3")
            return true;

        // DIAL TDUM is Dumb Response string
        if (recordType == "DIAL" && signature == "TDUM")
            return true;

        // QUST CNAM is Log Entry string, NNAM is objective Description
        if (recordType == "QUST" && signature is "CNAM" or "NNAM")
            return true;

        // PERK EPF2 is Button Label string
        if (recordType == "PERK" && signature == "EPF2")
            return true;

        // BPTD body part strings
        if (recordType == "BPTD" && signature is "BPTN" or "BPNN" or "BPNT" or "BPNI" or "NAM1" or "NAM4")
            return true;

        // AMMO QNAM is abbreviation string
        if (recordType == "AMMO" && signature == "QNAM")
            return true;

        // WTHR cloud texture layers are strings
        if (recordType == "WTHR" && signature is "DNAM" or "CNAM" or "ANAM" or "BNAM")
            return true;

        // CLMT FNAM/GNAM are sun texture path strings
        if (recordType == "CLMT" && signature is "FNAM" or "GNAM")
            return true;

        // FACT MNAM/FNAM/INAM are rank title strings
        if (recordType == "FACT" && signature is "MNAM" or "FNAM" or "INAM")
            return true;

        // SOUN FNAM is sound filename (string)
        if (recordType == "SOUN" && signature == "FNAM")
            return true;

        // AVIF ANAM is Short Name (string)
        if (recordType == "AVIF" && signature == "ANAM")
            return true;

        // RGDL ANAM is Death Pose string
        if (recordType == "RGDL" && signature == "ANAM")
            return true;

        // MUSC FNAM is music FileName string
        if (recordType == "MUSC" && signature == "FNAM")
            return true;

        // MSET NAM2-NAM7 are audio path strings
        if (recordType == "MSET" && signature is "NAM2" or "NAM3" or "NAM4" or "NAM5" or "NAM6" or "NAM7")
            return true;

        return signature switch
        {
            "EDID" or "FULL" or "MODL" or "DMDL" or "ICON" or "MICO" or "ICO2" or "MIC2" or "DESC" or "BMCT" or "NNAM" or "KFFZ" or
                "TX00" or "TX01" or "TX02" or "TX03" or "TX04" or "TX05" or "TX06" or "TX07" or
                "MWD1" or "MWD2" or "MWD3" or "MWD4" or "MWD5" or "MWD6" or "MWD7" or
                "VANM" or "MOD2" or "MOD3" or "MOD4" or "NIFZ" or "SCVR" or "XATO" or "ITXT" or
                "ONAM" or "SCTX" or "NAM1" or "RDMP" => true,
            _ => false
        };
    }

    /// <summary>
    ///     Gets the human-readable name for a GRUP type.
    /// </summary>
    public static string GetGrupTypeName(int grupType)
    {
        return grupType switch
        {
            0 => "Top (Record Type)",
            1 => "World Children",
            2 => "Interior Cell Block",
            3 => "Interior Cell Sub-Block",
            4 => "Exterior Cell Block",
            5 => "Exterior Cell Sub-Block",
            6 => "Cell Children",
            7 => "Topic Children",
            8 => "Cell Persistent",
            9 => "Cell Temporary",
            10 => "Cell VWD",
            _ => $"Unknown ({grupType})"
        };
    }

    /// <summary>
    ///     Checks if a GRUP type is invalid at top-level (depth 0).
    ///     Most GRUP types must be nested under their parent in PC ESM format.
    /// </summary>
    public static bool IsNestedOnlyGrupType(int grupType)
    {
        // Type 0 (record type) is valid at top level
        // Types 1-10 must be nested under their parent GRUP
        return grupType is >= 1 and <= 10;
    }

    /// <summary>
    ///     Performs floored integer division (rounds toward negative infinity).
    /// </summary>
    public static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        var remainder = value % divisor;
        if (remainder != 0 && remainder > 0 != divisor > 0) result -= 1;

        return result;
    }

    /// <summary>
    ///     Composes a grid label from X and Y coordinates.
    ///     Used for exterior cell block/sub-block labels.
    /// </summary>
    public static uint ComposeGridLabel(int x, int y)
    {
        unchecked
        {
            return (ushort)x | ((uint)(ushort)y << 16);
        }
    }
}