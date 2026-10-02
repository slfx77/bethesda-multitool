namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>A 16-bit IEEE float (<c>hfloat</c>) kept as its stored bits.</summary>
internal sealed class NifHalfFloatValue : NifValue
{
    /// <summary>Creates a half float from its bits (already in host order).</summary>
    public NifHalfFloatValue(string typeName, ushort rawBits)
        : base(typeName)
    {
        RawBits = rawBits;
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.HalfFloat;

    /// <summary>The stored IEEE 754 binary16 bits.</summary>
    public ushort RawBits { get; }

    /// <summary>The value the bits encode.</summary>
    public Half Value => BitConverter.UInt16BitsToHalf(RawBits);

    /// <summary>The value widened to binary32, which is exact for every binary16 value.</summary>
    public float ToSingle()
    {
        return (float)Value;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{TypeName} {ToSingle():R} (0x{RawBits:X4})";
    }
}
