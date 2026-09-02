using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     The ordered archive-format probe chain behind <c>ArchiveReader.Open</c>. Every probe must be
///     EXACT — magic bytes, or directory arithmetic that lands precisely on EOF — because several
///     classic families have weak or no magic (Fallout DAT1 especially) and a fuzzy probe placed
///     early would steal files from an exact one behind it. Strong magics first, exact arithmetic
///     after, the historical BSA fallback last (it owns the informative failure for non-archives).
///     Append new families as their backends land (XnGine name/number BSA, DAT2, DAT1, BOS zip, PCK).
/// </summary>
internal static class ArchiveProbe
{
    /// <summary>Opens the backend for <paramref name="path" /> by content probe.</summary>
    public static IArchiveBackend Open(string path)
    {
        // 1. Strong magic: BA2.
        if (Ba2Parser.IsBa2File(path))
        {
            return new Ba2Backend(new Ba2Extractor(path));
        }

        // 2. Strong leading dwords: Gamebryo BSA ("BSA\0" v103-105) or Morrowind (version 0x100).
        //    These belong to the classic BsaExtractor and must be claimed before any weak probe.
        if (HasGamebryoBsaHeader(path))
        {
            return new BsaBackend(new BsaExtractor(path));
        }

        // 3. Exact arithmetic with a weak type word: XnGine BSA (Daggerfall/Battlespire/Redguard).
        //    Ahead of Arena because its header carries a record-type field, making it the stronger
        //    of the two claims; neither can match the other's arithmetic in any case, since their
        //    payloads start at different offsets (4 vs 2) and their directory fields differ.
        if (XnGineBsaParser.TryProbe(path))
        {
            return new XnGineBsaBackend(XnGineBsaParser.Parse(path));
        }

        // 4. Exact arithmetic, no magic at all: Arena BSA (u16 count + EOF directory tiling the file).
        if (ArenaBsaParser.TryProbe(path))
        {
            return new ArenaBsaBackend(ArenaBsaParser.Parse(path));
        }

        // Fallback: the historical behavior — hand the file to the BSA extractor, whose parser
        // owns the informative "not a BSA" failure for genuinely unrecognized content.
        return new BsaBackend(new BsaExtractor(path));
    }

    private static bool HasGamebryoBsaHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[4];
            if (stream.Read(head) < 4)
            {
                return false;
            }

            // "BSA\0" (Oblivion..SkyrimSE) or the magic-less Morrowind version dword 0x00000100.
            return (head[0] == (byte)'B' && head[1] == (byte)'S' && head[2] == (byte)'A' && head[3] == 0) ||
                   BinaryPrimitives.ReadUInt32LittleEndian(head) == 0x00000100;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
