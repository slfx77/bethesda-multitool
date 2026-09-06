using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.DiscImage;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Travels.Dawnstar;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;
using BethesdaMultitool.Core.Formats.Zip;

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

        // 3. Strong magic plus exact arithmetic: Redguard ROB (chunked "OARC"/"OARD" + "END ").
        if (RedguardRobParser.TryProbe(path))
        {
            return new RedguardRobBackend(RedguardRobParser.Parse(path));
        }

        // 3b. Strong content gate on a strong extension: CD images (.iso with a CD001 descriptor,
        //     .cue whose tracks all exist, .bin opening with the raw-sector sync pattern). Sits here,
        //     ahead of the arithmetic probes, because those never claim these extensions.
        if (DiscImageBackend.TryProbe(path))
        {
            return DiscImageBackend.Open(path);
        }

        // 3c. Strong magic: InstallShield 5 cabinet ("ISc(" — Redguard Disc 1's DATA1.CAB holds
        //     the whole install). Magic-only gate; the parser owns the informative failure.
        if (InstallShield.InstallShieldCabinet.TryProbe(path))
        {
            return new InstallShieldBackend(InstallShield.InstallShieldCabinet.Parse(path));
        }

        // 3d. Exact arithmetic, no magic, extension-gated: the two Fallout containers. DAT2 is
        //     tried first because its footer test is the cheaper and stricter of the two (it must
        //     account for the whole file), and neither can satisfy the other's arithmetic.
        if (Dat2Archive.TryProbe(path))
        {
            return new Dat2Backend(Dat2Archive.Parse(path));
        }

        if (Dat1Archive.TryProbe(path))
        {
            return new Dat1Backend(Dat1Archive.Parse(path));
        }

        // 4. Exact arithmetic with a weak type word: XnGine BSA (Daggerfall/Battlespire/Redguard).
        //    Ahead of Arena because its header carries a record-type field, making it the stronger
        //    of the two claims; neither can match the other's arithmetic in any case, since their
        //    payloads start at different offsets (4 vs 2) and their directory fields differ.
        if (XnGineBsaParser.TryProbe(path))
        {
            return new XnGineBsaBackend(XnGineBsaParser.Parse(path));
        }

        // 5. Exact arithmetic, no magic at all: Arena BSA (u16 count + EOF directory tiling the file).
        if (ArenaBsaParser.TryProbe(path))
        {
            return new ArenaBsaBackend(ArenaBsaParser.Parse(path));
        }

        // 5b. Exact arithmetic on a one-byte lead: Dawnstar's .lmp lump. The directory is a run of
        //     "-name-" + BE u32 offset + BE u16 length entries that ENDS where the first payload
        //     begins, and the payloads then tile contiguously to EOF with no gaps — a leading '-'
        //     alone would be far too weak, but the pair of conditions accepted exactly the two real
        //     lumps out of 2,184 fixture files of every other classic family.
        if (DawnstarLumpArchive.TryProbe(path))
        {
            return new LmpArchiveBackend(DawnstarLumpArchive.Parse(path));
        }

        // 5c. Exact arithmetic with an OPTIONAL magic: the cancelled PSP Oblivion's GR.ARC pack.
        //     The "A2.0" tag cannot be the gate because the earliest of the seven betas has none, so
        //     the whole claim is arithmetic: the record table ends where the header says the
        //     payloads start, the payloads tile with only 32-byte alignment padding between them,
        //     and the name table ends exactly at EOF.
        if (OblivionPspArchive.TryProbe(path))
        {
            return new OblivionPspArchiveBackend(OblivionPspArchive.Parse(path));
        }

        // 6. Strong magic plus exact arithmetic: plain PKZIP (TES Travels J2ME JARs, Fallout Tactics
        //    .bos). PK\x03\x04 at offset 0, an end record that ends exactly at EOF, and a central
        //    directory that tiles exactly between its declared offset and that record.
        if (PkZipParser.TryProbe(path))
        {
            return new ZipArchiveBackend(PkZipParser.Parse(path));
        }

        // 7. Exact arithmetic with NO magic at all: the cancelled Fallout 3 "Van Buren" .grp. Its
        //    only fixed bytes are two constant dwords (2 and 1), which is far weaker evidence than
        //    a signature, so it deliberately runs LAST of the real formats — everything with a magic
        //    gets first refusal. The claim is the tiling: the first entry begins exactly where the
        //    directory ends, each subsequent one where the previous ended, and the last at EOF.
        if (VanBuren.VanBurenGrpArchive.TryProbe(path))
        {
            return new VanBurenGrpBackend(path);
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
