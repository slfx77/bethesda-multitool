namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     An integer read at its declared width: a basic integer (<c>byte</c>, <c>ushort</c>, <c>int</c>...), a
///     <c>bool</c> (kept as its raw byte, which is not always 0 or 1), or an enum, bitflags or bitfield (kept as its
///     storage integer; <see cref="NifValue.TypeName" /> names the enum and <see cref="StorageType" /> the storage).
/// </summary>
internal sealed class NifIntegerValue : NifValue
{
    /// <summary>Creates an integer from the bits it was stored with, already put in host order.</summary>
    /// <param name="typeName">The declared type (basic or enum-like).</param>
    /// <param name="storageType">The basic type the bits were read as.</param>
    /// <param name="byteWidth">1, 2, 4 or 8.</param>
    /// <param name="isSigned">Whether <see cref="Value" /> sign-extends the stored bits.</param>
    /// <param name="rawBits">The stored bits, zero-extended.</param>
    public NifIntegerValue(string typeName, string storageType, int byteWidth, bool isSigned, ulong rawBits)
        : base(typeName)
    {
        if (byteWidth is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(byteWidth), byteWidth, "Integer width must be 1, 2, 4 or 8.");
        }

        StorageType = storageType;
        ByteWidth = byteWidth;
        IsSigned = isSigned;
        RawBits = byteWidth == 8 ? rawBits : rawBits & ((1UL << (byteWidth * 8)) - 1);
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.Integer;

    /// <summary>The basic type the bits were read as (equal to <see cref="NifValue.TypeName" /> for basic types).</summary>
    public string StorageType { get; }

    /// <summary>The stored width in bytes.</summary>
    public int ByteWidth { get; }

    /// <summary>Whether the declared type is signed.</summary>
    public bool IsSigned { get; }

    /// <summary>The stored bits, zero-extended to 64 bits.</summary>
    public ulong RawBits { get; }

    /// <summary>True when the declared type is an enum, bitflags or bitfield rather than a basic integer.</summary>
    public bool IsEnumeration => !string.Equals(TypeName, StorageType, StringComparison.Ordinal);

    /// <summary>
    ///     The numeric value: sign-extended for signed types, zero-extended otherwise. A <c>uint64</c> above
    ///     <see cref="long.MaxValue" /> wraps; use <see cref="RawBits" /> for it.
    /// </summary>
    public long Value
    {
        get
        {
            if (!IsSigned || ByteWidth == 8)
            {
                return unchecked((long)RawBits);
            }

            var shift = 64 - ByteWidth * 8;
            return unchecked((long)(RawBits << shift)) >> shift;
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{TypeName} {Value}";
    }
}
