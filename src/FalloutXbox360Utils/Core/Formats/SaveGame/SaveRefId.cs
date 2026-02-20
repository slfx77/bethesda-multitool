namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     A 3-byte RefID used in Gamebryo save files.
///     Top 2 bits encode the type, bottom 22 bits encode the value.
/// </summary>
public readonly record struct SaveRefId(uint Raw)
{
    /// <summary>
    ///     RefID type (top 2 bits).
    /// </summary>
    public SaveRefIdType Type => (SaveRefIdType)(Raw >> 22);

    /// <summary>
    ///     RefID value (bottom 22 bits).
    /// </summary>
    public uint Value => Raw & 0x3FFFFF;

    /// <summary>
    ///     Reads a 3-byte RefID from a span (big-endian byte order within the 3 bytes).
    /// </summary>
    public static SaveRefId Read(ReadOnlySpan<byte> data, int offset)
    {
        uint raw = ((uint)data[offset] << 16) | ((uint)data[offset + 1] << 8) | data[offset + 2];
        return new SaveRefId(raw);
    }

    /// <summary>
    ///     Resolves this RefID to a full 32-bit FormID using the save's FormID array.
    /// </summary>
    public uint ResolveFormId(ReadOnlySpan<uint> formIdArray)
    {
        return Type switch
        {
            SaveRefIdType.FormIdArray => Value == 0 || (int)(Value - 1) >= formIdArray.Length
                ? 0
                : formIdArray[(int)(Value - 1)],
            SaveRefIdType.Default => Value,
            SaveRefIdType.Created => 0xFF000000 | Value,
            _ => Raw
        };
    }

    public bool IsNull => Raw == 0;

    public override string ToString()
    {
        return Type switch
        {
            SaveRefIdType.FormIdArray => $"[{Value}]",
            SaveRefIdType.Default => $"0x{Value:X6}",
            SaveRefIdType.Created => $"FF{Value:X6}",
            _ => $"?{Raw:X6}"
        };
    }
}

/// <summary>
///     RefID type encoded in the top 2 bits.
/// </summary>
public enum SaveRefIdType : byte
{
    /// <summary>Look up in the FormID array at the end of the save.</summary>
    FormIdArray = 0,

    /// <summary>Default/base game FormID (load order index 0x00).</summary>
    Default = 1,

    /// <summary>Runtime-created form (0xFF prefix).</summary>
    Created = 2,

    /// <summary>Unknown/undefined.</summary>
    Unknown = 3
}
