using System.Text;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     An inline length-prefixed string (<c>SizedString</c>: uint length; <c>SizedString16</c>: ushort length), whose
///     length is stored in the file's byte order. Kept as raw bytes; <see cref="Text" /> is the Latin-1 reading.
/// </summary>
internal sealed class NifSizedStringValue : NifValue
{
    private readonly byte[] _rawBytes;

    /// <summary>Creates a sized string from its bytes (the length prefix is not included).</summary>
    public NifSizedStringValue(string typeName, int lengthPrefixBytes, byte[] rawBytes)
        : base(typeName)
    {
        LengthPrefixBytes = lengthPrefixBytes;
        _rawBytes = rawBytes;
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.SizedString;

    /// <summary>The width of the stored length prefix (4 or 2).</summary>
    public int LengthPrefixBytes { get; }

    /// <summary>The string bytes exactly as stored (no terminator is added or removed).</summary>
    public ReadOnlyMemory<byte> RawBytes => _rawBytes;

    /// <summary>The bytes as Latin-1 text.</summary>
    public string Text => Encoding.Latin1.GetString(_rawBytes);

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{TypeName} \"{Text}\"";
    }
}
