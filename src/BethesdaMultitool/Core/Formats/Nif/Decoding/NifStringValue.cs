using System.Text;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     A header string-table reference (<c>NiFixedString</c>, and the <c>string</c> / <c>FilePath</c> structs that
///     wrap it from 20.1.0.3 on): the stored index plus the raw bytes of the entry it resolves to. Index -1 means no
///     string. The text is decoded as Latin-1 so no byte is lost (NifParser's ASCII decoding turns every byte at or
///     above 0x80 into '?').
/// </summary>
internal sealed class NifStringValue : NifValue
{
    private readonly byte[]? _rawBytes;

    /// <summary>Creates a string reference.</summary>
    /// <param name="typeName">The declared type.</param>
    /// <param name="index">The stored index; -1 for none.</param>
    /// <param name="rawBytes">The resolved entry's bytes, or null when the index is -1 or does not resolve.</param>
    public NifStringValue(string typeName, int index, byte[]? rawBytes)
        : base(typeName)
    {
        Index = index;
        _rawBytes = rawBytes;
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.String;

    /// <summary>The stored string-table index (-1 for none).</summary>
    public int Index { get; }

    /// <summary>True when the index is -1.</summary>
    public bool IsNone => Index == -1;

    /// <summary>True when the index resolved to a header string.</summary>
    public bool IsResolved => _rawBytes is not null;

    /// <summary>The resolved entry's bytes (empty when unresolved).</summary>
    public ReadOnlyMemory<byte> RawBytes => _rawBytes ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>The resolved entry as Latin-1 text, or null when unresolved.</summary>
    public string? Text => _rawBytes is null ? null : Encoding.Latin1.GetString(_rawBytes);

    /// <summary>True when the resolved entry contains a byte at or above 0x80.</summary>
    public bool HasNonAsciiBytes => _rawBytes is not null && Array.Exists(_rawBytes, b => b >= 0x80);

    /// <inheritdoc />
    public override string ToString()
    {
        return IsResolved ? $"{TypeName} #{Index} \"{Text}\"" : $"{TypeName} #{Index}";
    }
}
