namespace SampleGenerator;

/// <summary>
///     Detects whether a build has been modified from what shipped, so a corpus entry is never
///     silently presented as vanilla.
///     <para>
///         This matters because real-asset tests over a modded install must assert structure, not
///         content counts — and because a build that quietly carries a script extender will not
///         reproduce someone else's measurements.
///     </para>
/// </summary>
internal static class SampleGeneratorModAudit
{
    /// <summary>
    ///     File names that identify a modification, paired with what they mean.
    ///     <para>
    ///         ⚠⚠ These are DELIBERATELY narrow. A first pass used <c>obse*</c> and flagged
    ///         Redguard, whose data files include <c>observat.col</c> and <c>OBSERVE.ROB</c>; and
    ///         <c>*.esp</c>, which flagged Oblivion's official <c>DLCBattlehornCastle.esp</c>,
    ///         Skyrim's <c>HighResTexturePack01.esp</c>, the Xbox 360 discs' shipped
    ///         <c>update.esp</c>, and a prototype's own <c>NPC_Test.esp</c> — four false positives
    ///         out of five hits. A marker here must name a file that ONLY a modification ships.
    ///     </para>
    /// </summary>
    private static readonly (string Pattern, string Meaning, bool RootOnly)[] Markers =
    [
        ("nvse_loader.exe", "xNVSE (New Vegas Script Extender)", false),
        ("nvse_steam_loader.dll", "xNVSE (New Vegas Script Extender)", false),
        ("obse_loader.exe", "OBSE (Oblivion Script Extender)", false),
        ("obse_1_*.dll", "OBSE (Oblivion Script Extender)", false),
        ("skse_loader.exe", "SKSE (Skyrim Script Extender)", false),
        ("skse64_loader.exe", "SKSE64 (Skyrim Special Edition Script Extender)", false),
        ("f4se_loader.exe", "F4SE (Fallout 4 Script Extender)", false),
        ("sfall-readme.txt", "sfall", false),
        ("MWSE.dll", "MWSE (Morrowind Script Extender)", false),
        ("MGEXEgui.exe", "MGE (Morrowind Graphics Extender)", false),

        // ⚠⚠ ROOT ONLY, and that is the whole discriminator. A DirectDraw wrapper is a
        // ddraw.dll the game loads from its OWN directory. Searching the tree instead flagged the
        // original 1997 and 1998 Interplay CDs, which ship the bundled DirectX 3 / DirectX 5
        // redistributable at DIRECTX3\\DIRECTX\\DDRAW.DLL - a different file entirely (161,280 and
        // 270,848 bytes on the discs against 90,112 and 393,216 at the Steam game roots). The name
        // alone cannot tell a wrapper from a 1997 redistributable; the location can.
        ("ddraw.dll", "DirectDraw wrapper", true),
    ];

    /// <summary>One build's modification status.</summary>
    /// <param name="LargeAddressAware">
    ///     True when the build's main executable carries the LAA bit. ⚠ Not a mod on its own — a
    ///     64-bit or natively-LAA build sets it legitimately — but on a 2010-era 32-bit Bethesda
    ///     executable it is the 4 GB patch, which is how the New Vegas copy in this corpus was
    ///     caught (characteristics 0x0122 against the vanilla 0x0102).
    /// </param>
    /// <param name="Findings">Modifications not accounted for by the build's declared extras.</param>
    /// <param name="Shipped">
    ///     Markers the release itself bundles. A deviation from the original media, but nothing the
    ///     user did — so reported separately rather than as a modification.
    /// </param>
    internal readonly record struct ModStatus(string[] Findings, string[] Shipped, bool? LargeAddressAware)
    {
        public bool IsModified => Findings.Length > 0;
    }

    internal static ModStatus Audit(CatalogEntry entry, string buildDir)
    {
        if (!Directory.Exists(buildDir))
        {
            return new ModStatus([], [], null);
        }

        var findings = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (pattern, meaning, rootOnly) in Markers)
        {
            IEnumerable<string> hits;
            try
            {
                hits = Directory.EnumerateFiles(
                    buildDir,
                    pattern,
                    rootOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var first = hits.FirstOrDefault();
            if (first is not null)
            {
                findings.Add($"{meaning} ({Path.GetFileName(first)})");
            }
        }

        bool? laa = null;
        if (entry.MainExecutable is not null)
        {
            var executable = Directory
                .EnumerateFiles(buildDir, entry.MainExecutable, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (executable is not null)
            {
                // ⚠⚠ Recorded, never treated as a modification. The LAA bit CANNOT discriminate
                // on its own: every 64-bit executable sets it (Skyrim Special Edition), and some
                // 32-bit builds ship it (Fallout 3's 2021 repack, Skyrim's patch 1.9). Claiming it
                // as a mod flagged three vanilla builds. It was only conclusive for New Vegas
                // because that was a direct A/B against the same title's clean Steam install —
                // 0x0122 there against 0x0102 here — not an absolute test.
                laa = IsLargeAddressAware(executable);
            }
        }

        // Markers the build legitimately ships move to the Shipped list rather than disappearing:
        // a Steam release that bundles a wrapper still differs from the disc, and hiding that
        // would be as misleading as calling it a user modification.
        var shippedFound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var shipped in entry.ShippedExtras ?? [])
        {
            foreach (var hit in findings.Where(
                         f => f.StartsWith(shipped, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                findings.Remove(hit);
                shippedFound.Add(hit);
            }
        }

        return new ModStatus([.. findings], [.. shippedFound], laa);
    }

    /// <summary>
    ///     Reads the COFF characteristics word and tests <c>IMAGE_FILE_LARGE_ADDRESS_AWARE</c>
    ///     (0x0020). Null when the file is not a PE.
    /// </summary>
    private static bool? IsLargeAddressAware(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40)
            {
                return null;
            }

            stream.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 24 > stream.Length)
            {
                return null;
            }

            stream.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt16() != 0x4550)
            {
                return null;
            }

            stream.Seek(peOffset + 22, SeekOrigin.Begin);
            return (reader.ReadUInt16() & 0x0020) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return null;
        }
    }
}
