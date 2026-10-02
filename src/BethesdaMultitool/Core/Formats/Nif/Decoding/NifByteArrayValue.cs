namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>A bulk array of single bytes (<c>byte</c>, <c>char</c>), copied exactly as stored.</summary>
internal sealed class NifByteArrayValue : NifValue
{
    private readonly byte[] _bytes;

    /// <summary>Creates a byte array value over a private copy of the stored bytes.</summary>
    public NifByteArrayValue(string typeName, byte[] bytes)
        : base(typeName)
    {
        _bytes = bytes;
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.ByteArray;

    /// <summary>The number of bytes.</summary>
    public int Count => _bytes.Length;

    /// <summary>The bytes exactly as stored.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{TypeName}[{Count}]";
    }
}
