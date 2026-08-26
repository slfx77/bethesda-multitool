namespace BethesdaMultitool.Core.Formats.Ddx;

/// <summary>
///     Classifies a DDX texture by its 4-byte magic, for the file-list columns.
///     <para>
///         Lives in <c>Core/</c> for the same reason as
///         <see cref="Nif.NifHeaderFormat" />: the rule is platform-neutral, but its GUI caller
///         sits under <c>App/**</c>, which is <c>Compile Remove</c>d from the <c>net10.0</c>
///         target framework and therefore unreachable from the test project.
///     </para>
/// </summary>
public static class DdxHeaderFormat
{
    /// <summary>Bytes that must be readable before a classification is possible.</summary>
    public const int RequiredHeaderBytes = 4;

    /// <summary>Xbox 360 DDX, linear layout.</summary>
    public const string Xdo = "3XDO";

    /// <summary>Xbox 360 DDX, engine-tiled layout.</summary>
    public const string Xdr = "3XDR";

    /// <summary>Too short, or not a DDX magic at all.</summary>
    public const string Invalid = "Invalid";

    /// <summary>Could not be read at all (I/O failure).</summary>
    public const string Error = "Error";

    /// <summary>
    ///     Describes a DDX variant from its leading bytes. Returns <see cref="Invalid" /> when
    ///     fewer than <see cref="RequiredHeaderBytes" /> bytes are available, when the "3XD"
    ///     prefix is absent, or when the variant byte is neither 'O' nor 'R'.
    /// </summary>
    public static string Describe(ReadOnlySpan<byte> header)
    {
        if (header.Length < RequiredHeaderBytes)
        {
            return Invalid;
        }

        if (header[0] != (byte)'3' || header[1] != (byte)'X' || header[2] != (byte)'D')
        {
            return Invalid;
        }

        return header[3] switch
        {
            (byte)'O' => Xdo,
            (byte)'R' => Xdr,
            _ => Invalid
        };
    }
}
