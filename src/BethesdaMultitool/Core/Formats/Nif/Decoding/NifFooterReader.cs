using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     Reads the footer NifParser never reads (Parser/NifParser.cs:104-111 stops at the block list). At 20.2.0.7 the
///     footer is nif.xml's <c>Footer</c> struct (nif.xml:5745-5757): <c>uint Num Roots</c> then
///     <c>Ref Roots[Num Roots]</c> (both since 3.3.0.13), in the file's byte order, immediately after the last block.
/// </summary>
internal static class NifFooterReader
{
    /// <summary>Reads and checks the footer.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="bigEndian">Whether the file's body byte order is big-endian.</param>
    /// <param name="footerOffset">One past the last block (the header's end when there are no blocks).</param>
    /// <param name="blockCount">The number of blocks, which every root must index.</param>
    /// <exception cref="InvalidDataException">
    ///     The footer is truncated, declares more roots than the bytes left can hold, does not end exactly at the end of
    ///     the file, or names a root outside [0, <paramref name="blockCount" />) (a null root, -1, is rejected too: a
    ///     root must be a block).
    /// </exception>
    public static NifFooter Read(ReadOnlySpan<byte> file, bool bigEndian, int footerOffset, int blockCount)
    {
        if (footerOffset < 0 || footerOffset > file.Length - 4)
        {
            throw new InvalidDataException(
                $"NIF footer at 0x{footerOffset:X} has no room for Num Roots in a {file.Length}-byte file.");
        }

        var rootCount = ReadUInt32(file[footerOffset..], bigEndian);
        var remaining = file.Length - footerOffset - 4;
        if (rootCount > (uint)(remaining / 4))
        {
            throw new InvalidDataException(
                $"NIF footer declares {rootCount} roots but only {remaining} bytes follow Num Roots.");
        }

        var length = 4 + (int)rootCount * 4;
        if (footerOffset + length != file.Length)
        {
            throw new InvalidDataException(
                $"NIF footer ends at 0x{footerOffset + length:X} but the file is {file.Length} bytes; " +
                $"{file.Length - footerOffset - length} trailing byte(s).");
        }

        var roots = new int[rootCount];
        for (var i = 0; i < roots.Length; i++)
        {
            var root = unchecked((int)ReadUInt32(file[(footerOffset + 4 + i * 4)..], bigEndian));
            if (root < 0 || root >= blockCount)
            {
                throw new InvalidDataException(
                    $"NIF footer root {i} is {root}, outside the {blockCount} blocks.");
            }

            roots[i] = root;
        }

        return new NifFooter(footerOffset, length, roots);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
            : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
