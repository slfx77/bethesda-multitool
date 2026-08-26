namespace BethesdaMultitool.Core.Formats.Nif;

/// <summary>
///     Classifies a NIF by its endianness byte, for the "which of these files need converting?"
///     file-list columns.
///     <para>
///         This lives in <c>Core/</c> rather than beside its GUI caller on purpose. The rule is
///         pure and platform-neutral, but <c>App/**</c> is <c>Compile Remove</c>d from the
///         <c>net10.0</c> target framework, so anything defined there is unreachable from the
///         test project — which previously led to the classifier being copy-pasted into the test
///         file, leaving the real implementation with no coverage at all.
///     </para>
///     <para>
///         The classification reads the endian byte that follows the header string. A NIF opens
///         with a newline-terminated version string ("Gamebryo File Format, Version 20.2.0.7\n"),
///         then a 4-byte binary version, then a single endian byte: 0 = big-endian (Xbox 360),
///         1 = little-endian (PC). That places the endian byte at newline + 5.
///     </para>
/// </summary>
public static class NifHeaderFormat
{
    /// <summary>Bytes that must be readable before a classification is possible.</summary>
    public const int RequiredHeaderBytes = 50;

    /// <summary>Big-endian NIF — an Xbox 360 asset, and the one that needs conversion.</summary>
    public const string Xbox360 = "Xbox 360 (BE)";

    /// <summary>Little-endian NIF — already in PC form.</summary>
    public const string Pc = "PC (LE)";

    /// <summary>Too short, or no header-string terminator where one is required.</summary>
    public const string Invalid = "Invalid";

    /// <summary>A readable header carrying an endian byte that is neither 0 nor 1.</summary>
    public const string Unknown = "Unknown";

    /// <summary>Could not be read at all (I/O failure).</summary>
    public const string Error = "Error";

    /// <summary>
    ///     Describes the endianness of a NIF from its leading bytes. Returns <see cref="Invalid" />
    ///     when fewer than <see cref="RequiredHeaderBytes" /> bytes are available or the header
    ///     string's terminator is missing or too close to the end to carry an endian byte.
    /// </summary>
    public static string Describe(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < RequiredHeaderBytes)
        {
            return Invalid;
        }

        var window = headerBytes[..RequiredHeaderBytes];
        var newlinePos = window.IndexOf((byte)0x0A);

        // A terminator at index 0 means an empty version string, and one within 5 bytes of the
        // window's end leaves no room for the endian byte — neither is a usable NIF header.
        if (newlinePos <= 0 || newlinePos + 5 >= RequiredHeaderBytes)
        {
            return Invalid;
        }

        return window[newlinePos + 5] switch
        {
            0 => Xbox360,
            1 => Pc,
            _ => Unknown
        };
    }
}
