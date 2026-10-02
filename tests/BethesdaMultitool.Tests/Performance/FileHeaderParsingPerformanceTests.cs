using BethesdaMultitool.Core.Formats.Ddx;
using BethesdaMultitool.Core.Formats.Nif;
using Xunit;

namespace BethesdaMultitool.Tests.Performance;

/// <summary>
///     Covers the production NIF/DDX header classifiers used by the converter file lists.
///     Byte fixtures distinguish format and invalid-header cases without filesystem setup.
/// </summary>
public sealed class FileHeaderParsingPerformanceTests
{
    public static TheoryData<byte[], string, string> NifHeaderCases => new()
    {
        { CreateNifHeader(true), NifHeaderFormat.Xbox360, "endian byte 0 = big-endian" },
        { CreateNifHeader(false), NifHeaderFormat.Pc, "endian byte 1 = little-endian" },
        { CreateNifHeader(7), NifHeaderFormat.Unknown, "endian byte is neither 0 nor 1" },
        { new byte[NifHeaderFormat.RequiredHeaderBytes - 1], NifHeaderFormat.Invalid, "one byte short of a header" },
        { [], NifHeaderFormat.Invalid, "empty input" },
        { new byte[NifHeaderFormat.RequiredHeaderBytes], NifHeaderFormat.Invalid, "all zeroes: no newline terminator" },
        { CreateNifHeaderWithNewlineAt(0), NifHeaderFormat.Invalid, "newline at index 0 = empty version string" },
        {
            CreateNifHeaderWithNewlineAt(NifHeaderFormat.RequiredHeaderBytes - 3),
            NifHeaderFormat.Invalid, "newline too close to the end to carry an endian byte"
        }
    };

    public static TheoryData<byte[], string, string> DdxHeaderCases => new()
    {
        { "3XDO"u8.ToArray(), DdxHeaderFormat.Xdo, "linear Xbox 360 DDX" },
        { "3XDR"u8.ToArray(), DdxHeaderFormat.Xdr, "engine-tiled Xbox 360 DDX" },
        { "3XDZ"u8.ToArray(), DdxHeaderFormat.Invalid, "known prefix, unknown variant byte" },
        { "XXXX"u8.ToArray(), DdxHeaderFormat.Invalid, "not a DDX magic at all" },
        { "DDS "u8.ToArray(), DdxHeaderFormat.Invalid, "a PC DDS, not a DDX" },
        { "3XD"u8.ToArray(), DdxHeaderFormat.Invalid, "one byte short of the magic" },
        { [], DdxHeaderFormat.Invalid, "empty input" }
    };

    [Theory]
    [MemberData(nameof(NifHeaderCases))]
    public void Describe_NifHeader_ClassifiesEndianness(byte[] header, string expected, string because)
    {
        _ = because; // Surfaces the equivalence class in the test-case display name.

        Assert.Equal(expected, NifHeaderFormat.Describe(header));
    }

    [Theory]
    [MemberData(nameof(DdxHeaderCases))]
    public void Describe_DdxHeader_ClassifiesVariant(byte[] header, string expected, string because)
    {
        _ = because;

        Assert.Equal(expected, DdxHeaderFormat.Describe(header));
    }

    #region Synthetic NIF headers

    private static byte[] CreateNifHeader(bool isXbox360)
    {
        return CreateNifHeader((byte)(isXbox360 ? 0 : 1));
    }

    /// <summary>
    ///     A minimal 50-byte NIF header: a newline-terminated version string, then the endian
    ///     byte five bytes past the terminator (the 4-byte binary version sits between them).
    /// </summary>
    private static byte[] CreateNifHeader(byte endianByte)
    {
        var header = new byte[NifHeaderFormat.RequiredHeaderBytes];
        "Gamebryo File Format, Version 20.2.0.7\n"u8.ToArray().CopyTo(header, 0);

        var newlinePos = Array.IndexOf(header, (byte)0x0A);
        header[newlinePos + 5] = endianByte;
        return header;
    }

    /// <summary>A 50-byte buffer whose only newline sits at <paramref name="index" />.</summary>
    private static byte[] CreateNifHeaderWithNewlineAt(int index)
    {
        var header = new byte[NifHeaderFormat.RequiredHeaderBytes];
        Array.Fill(header, (byte)'A');
        header[index] = 0x0A;
        return header;
    }

    #endregion
}
