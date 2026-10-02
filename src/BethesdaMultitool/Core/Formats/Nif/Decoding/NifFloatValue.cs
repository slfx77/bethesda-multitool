namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>A 32-bit IEEE float kept as its stored bits, so NaN payloads and signed zeros survive.</summary>
internal sealed class NifFloatValue : NifValue
{
    /// <summary>Creates a float from its bits (already in host order).</summary>
    public NifFloatValue(string typeName, uint rawBits)
        : base(typeName)
    {
        RawBits = rawBits;
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.Float;

    /// <summary>The stored IEEE 754 binary32 bits.</summary>
    public uint RawBits { get; }

    /// <summary>The value the bits encode.</summary>
    public float Value => BitConverter.UInt32BitsToSingle(RawBits);

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{TypeName} {Value:R} (0x{RawBits:X8})";
    }
}
