using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the XBOX release's asset encodings — the textures,
///     the sounds and the name hash — against the extracted disc tree, plus the cross-console
///     oracle that decodes the same texture on both discs and compares the pixels.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BosXboxAssetRetailTests
{
    private static string RequireXboxResx()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var extracted = RealAssetPaths.Consoles.BrotherhoodOfSteelXboxExtracted();
        Assert.SkipWhen(extracted is null, RealAssetPaths.SkipMessage("Fallout: Brotherhood of Steel Xbox tree"));
        var resx = Path.Combine(extracted, "resx");
        Assert.SkipWhen(!Directory.Exists(resx), RealAssetPaths.SkipMessage("the Xbox disc's resx tree"));
        return resx;
    }

    private static string RequirePs2Iso()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var iso = RealAssetPaths.Consoles.BrotherhoodOfSteelIso();
        Assert.SkipWhen(iso is null, RealAssetPaths.SkipMessage("Fallout: Brotherhood of Steel PS2 disc image"));
        return iso;
    }

    /// <summary>Every <c>.clp</c> under the Xbox tree that the container reader accepts.</summary>
    private static IEnumerable<(string Path, byte[] Bytes, BosClumpFile Clump)> XboxClumps(string resx)
    {
        foreach (var path in Directory.EnumerateFiles(resx, "*.clp", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(path);
            if (BosClumpFile.TryParse(bytes, Path.GetFileName(path), out var clump, out _))
            {
                yield return (path, bytes, clump);
            }
        }
    }

    [Fact]
    public void EveryXboxTextureSectionTilesItsPaletteAndPixelsExactly()
    {
        // ⚑ THE GATE IS THE PROOF: dataOffset + 1024 + width × height == the section length.
        // ⚠ The flag bit ALONE proves nothing — 5,682 of the 21,905 sections carry 0x4000 in their
        // +4 dword and only 3,686 are textures. The other 1,996 are refused by the DIMENSION gate
        // (1,948) or by the LENGTH gate (48) — which reads `length <= 0x38 + 1024`, so it catches
        // 47 sections strictly shorter than a header plus a palette and one that is EXACTLY 1,080
        // bytes; not one of them ever
        // reaches the size arithmetic, and 1,725 of them are RIFF WAVE sound sections whose
        // riffSize happens to carry the bit ('RIFF' reads as 18,770 × 17,990). ⛔ An earlier
        // comment here said "223 further sections … every one of them fails the arithmetic": 223
        // is only the non-RIFF share of the dimension-gate refusals, and the mechanism was wrong.
        var resx = RequireXboxResx();
        var textures = 0;
        var powerOfTwo = 0;
        var clumps = 0;
        var globalXtextures = 0;
        var sections = 0;
        var flagged = 0;
        var refusedByDimensions = 0;
        var refusedAsTooShort = 0;
        var refusedRiff = 0;
        var refusedByArithmetic = 0;
        var boosted = 0;
        var offsets = new HashSet<int>();
        // ⚑ THE DISCRIMINATING CONTROL, run over EVERY section rather than only the accepted ones:
        // how many of the 21,905 would a reader with a different palette length — or one that
        // ignored +8 for a fixed header length — accept? If "0x38 header + 1,024-byte palette +
        // width x height" were merely CONSISTENT with the data rather than forced by it, a
        // neighbouring size would fit some sections too.
        int[] rivalPaletteLengths = [0, 256, 512, 768, 2048];
        var rivalHits = new int[rivalPaletteLengths.Length];
        int[] rivalHeaderLengths = [16, 24, 32, 48, 64, 128];
        var rivalHeaderHits = new int[rivalHeaderLengths.Length];

        foreach (var (path, bytes, clump) in XboxClumps(resx))
        {
            clumps++;
            var here = 0;
            foreach (var section in clump.Sections)
            {
                var span = bytes.AsSpan(section.Offset, section.Size);
                sections++;

                // The rival lengths and the flag census are measured on EVERY section, so a rival
                // reading gets the whole corpus to find a hit in rather than only the 3,686 the
                // shipped reading already accepts.
                var width = span.Length >= 12 ? BinaryPrimitives.ReadUInt16LittleEndian(span) : 0;
                var height = span.Length >= 12 ? BinaryPrimitives.ReadUInt16LittleEndian(span[2..]) : 0;
                var flags = span.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(span[4..]) : 0;
                var declared = span.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(span[8..]) : 0;
                var plausible = width is > 0 and <= 4096 && height is > 0 and <= 4096;
                if (plausible)
                {
                    for (var rival = 0; rival < rivalPaletteLengths.Length; rival++)
                    {
                        if (declared >= BosXboxTexture.HeaderLength && declared <= (uint)span.Length &&
                            declared + rivalPaletteLengths[rival] + width * height == span.Length)
                        {
                            rivalHits[rival]++;
                        }
                    }

                    for (var rival = 0; rival < rivalHeaderLengths.Length; rival++)
                    {
                        if (rivalHeaderLengths[rival] + BosXboxTexture.PaletteLength +
                            width * height == span.Length)
                        {
                            rivalHeaderHits[rival]++;
                        }
                    }
                }

                if ((flags & BosXboxTexture.PalettizedFlag) != 0)
                {
                    flagged++;
                }

                if (!BosXboxTexture.IsTexture(span))
                {
                    if ((flags & BosXboxTexture.PalettizedFlag) != 0)
                    {
                        if (span.Length <= BosXboxTexture.StandardDataOffset + BosXboxTexture.PaletteLength)
                        {
                            refusedAsTooShort++;
                        }
                        else if (!plausible)
                        {
                            refusedByDimensions++;
                            if (span.Length >= 4 && span[..4].SequenceEqual("RIFF"u8))
                            {
                                refusedRiff++;
                            }
                        }
                        else
                        {
                            // Refused by the OFFSET gate or the size arithmetic. Zero on retail —
                            // the counter is here so the claim about the mechanism can fail.
                            refusedByArithmetic++;
                        }
                    }

                    continue;
                }

                var texture = BosXboxTexture.Parse(span, $"{Path.GetFileName(path)}#{section.Tag:X8}");
                textures++;
                here++;
                offsets.Add(texture.DataOffset);
                if (texture.IsPowerOfTwo)
                {
                    powerOfTwo++;
                }

                if (texture.IsSaturationBoosted)
                {
                    boosted++;
                }

                Assert.Equal(texture.Width * texture.Height, texture.Indices.Length);
                Assert.Equal(texture.Width * texture.Height * 4, texture.Rgba.Length);
            }

            if (Path.GetFileName(path).Equals("global_x.clp", StringComparison.OrdinalIgnoreCase))
            {
                globalXtextures = here;
                // ⚑ global_x.clp is the Xbox-only split of GLOBAL.CLP and holds NOTHING BUT
                // textures: 140 of its 141 sections decode, and the odd one out is a 0 x 0
                // PLACEHOLDER — tag B3D41265, 1,080 bytes, w = h = 0, flags 0x4014, dataOffset
                // 0x38: the 0x38 header plus a palette and no pixels at all.
                // ⛔ CORRECTION (2026-09-08): an earlier comment here said it "satisfies the size
                // relation and is refused ONLY BY THE DIMENSION CHECK". Both halves are wrong, and
                // this test's own counters say so. TryReadHeader's FIRST gate is
                // `bytes.Length <= StandardDataOffset + PaletteLength`, and 1,080 <= 0x38 + 1024 =
                // 1,080 is TRUE — so the section is refused THERE and the dimension gate is never
                // reached. It is counted in refusedAsTooShort (which is why that figure is 48 and
                // not 47), never in refusedByDimensions. The size arithmetic is degenerate rather
                // than satisfied: 0x38 + 1024 + 0*0 equals the length only because there are no
                // pixels to disagree, and the reader never evaluates it.
                Assert.Equal(141, clump.Sections.Count);
                Assert.Equal(clump.Sections.Count - 1, here);

                // The mechanism claim above is pinned rather than merely asserted in prose: these
                // literals were measured off the disc, and they would break if the placeholder
                // moved, changed size, or stopped being caught by the LENGTH gate.
                var placeholder = clump.Sections.Single(s => !BosXboxTexture.IsTexture(bytes.AsSpan(s.Offset, s.Size)));
                Assert.Equal(0xB3D41265u, placeholder.Tag);
                Assert.Equal(1080, placeholder.Size);
                Assert.Equal(BosXboxTexture.StandardDataOffset + BosXboxTexture.PaletteLength, placeholder.Size);
                var placeholderSpan = bytes.AsSpan(placeholder.Offset, placeholder.Size);
                Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(placeholderSpan));
                Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(placeholderSpan[2..]));
                Assert.Equal(0x4014u, BinaryPrimitives.ReadUInt32LittleEndian(placeholderSpan[4..]));
                Assert.Equal(
                    (uint)BosXboxTexture.StandardDataOffset,
                    BinaryPrimitives.ReadUInt32LittleEndian(placeholderSpan[8..]));
            }
        }

        Assert.Equal(230, clumps);
        Assert.Equal(21_905, sections);
        Assert.Equal(3_686, textures);
        Assert.Equal(2_782, powerOfTwo);
        Assert.Equal(140, globalXtextures);
        // Every shipped texture puts its palette at 0x38; the reader accepts other values, so this
        // records the fact rather than assuming it.
        Assert.Equal(BosXboxTexture.StandardDataOffset, Assert.Single(offsets));
        // ⚑ Zero of 21,905 for every rival palette length AND every rival header length — 1,024
        // and 0x38 are the only pair that fits any section at all, so the layout is FORCED by the
        // data rather than merely compatible with it.
        Assert.All(rivalHits, hits => Assert.Equal(0, hits));
        Assert.All(rivalHeaderHits, hits => Assert.Equal(0, hits));
        // ⚠ What the flag bit alone does, and which gate actually does the refusing.
        Assert.Equal(5_682, flagged);
        Assert.Equal(1_948, refusedByDimensions);
        Assert.Equal(48, refusedAsTooShort);
        Assert.Equal(1_725, refusedRiff);
        Assert.Equal(0, refusedByArithmetic);
        // ⚑ The loader's saturation boost is suppressed by flags bit 0x08, which is CLEAR on every
        // shipped texture — so the console draws all 3,686 more saturated than the stored bytes.
        Assert.Equal(textures, boosted);
    }

    [Fact]
    public void EveryXboxSoundSectionIsARiffWaveWhoseChunkWalkConsumesItExactly()
    {
        var resx = RequireXboxResx();
        var sounds = 0;
        var canonical = 0;
        var seconds = 0.0;
        var rates = new SortedDictionary<int, int>();
        var proToolsRawRates = new List<(string File, uint Tag, uint RawRate, uint RawLength)>();

        foreach (var (path, bytes, clump) in XboxClumps(resx))
        {
            foreach (var section in clump.Sections)
            {
                var span = bytes.AsSpan(section.Offset, section.Size);
                if (!BosXboxSound.IsSound(span))
                {
                    continue;
                }

                var sound = BosXboxSound.Parse(span, $"{Path.GetFileName(path)}#{section.Tag:X8}");
                sounds++;
                if (sound.IsCanonical)
                {
                    canonical++;
                }

                seconds += sound.Duration;
                rates[sound.SampleRate] = rates.GetValueOrDefault(sound.SampleRate) + 1;

                if (!sound.IsCanonical)
                {
                    // The dword the ENGINE would read as the rate, at its fixed +0x18 — which for
                    // these lands inside the bext Description rather than in a fmt chunk.
                    proToolsRawRates.Add(
                        (Path.GetFileName(path), section.Tag,
                            BinaryPrimitives.ReadUInt32LittleEndian(span[0x18..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(span[0x28..])));
                }

                // The engine only ever plays 16-bit mono PCM, and so does every shipped section.
                Assert.Equal(BosXboxSound.PcmFormatTag, sound.FormatTag);
                Assert.Equal(1, sound.Channels);
                Assert.Equal(16, sound.BitsPerSample);
                Assert.Equal(2, sound.BlockAlign);
            }
        }

        Assert.Equal(3_521, sounds);
        // ⚠⚠ Three are Pro Tools leftovers whose bext chunk defeats the engine's fixed +0x28 read,
        // so retail plays them as silence; the chunk walk recovers them.
        Assert.Equal(3_518, canonical);
        Assert.Equal(927, rates[16_000]);
        Assert.Equal(903, rates[18_000]);
        Assert.Equal(829, rates[24_000]);
        Assert.Equal(790, rates[22_050]);
        Assert.Equal(16, rates.Count);
        Assert.InRange(seconds, 3_012.4, 3_012.5);

        // ⛔ CORRECTION (2026-09-08): the class doc used to state ONE garbage rate for all three
        // Pro Tools sections. It is not one value — +0x18 lands in the bext Description text, so
        // the number is whatever four ASCII bytes sit there. Pinned here so a single-literal claim
        // cannot come back: "Torc" for the two CRB_2_S sections, "LtHa" for MILL_4_S. All three do
        // agree on the LENGTH the engine reads (0 — which is why retail plays them as silence).
        Assert.Equal(3, proToolsRawRates.Count);
        Assert.All(proToolsRawRates, r => Assert.Equal(0u, r.RawLength));
        Assert.Equal(
            new List<(string, uint, uint, uint)>
            {
                ("CRB_2_S.clp", 0x0C5EF9E7u, 1_668_443_988u, 0u),
                ("CRB_2_S.clp", 0xBE44BB3Cu, 1_668_443_988u, 0u),
                ("MILL_4_S.clp", 0x1EB0CF0Du, 1_632_138_316u, 0u)
            },
            proToolsRawRates.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Tag).ToList());
        // The two literals ARE text, which is what shows the mechanism rather than just recording
        // the numbers: they are bytes 4-7 of the bext chunk's Description field read little-endian,
        // and that field holds the CLIP name (RaidTorchHam_Bck / RaidTorchHam_Scr / RaidLtHand_Grena;
        // the chunk's Originator field is the literal "Pro Tools") — measured 2026-09-08, see the
        // BosXboxSound class doc. Nothing on the disc is a "session name".
        Assert.Equal("Torc", Encoding.ASCII.GetString(BitConverter.GetBytes(1_668_443_988u)));
        Assert.Equal("LtHa", Encoding.ASCII.GetString(BitConverter.GetBytes(1_632_138_316u)));
    }

    [Fact]
    public void TheLevelSoundCountsMatchThePs2Release()
    {
        // ⚑ Same content, different encoding: BAR_S carries 34 sections on each disc under the
        // SAME BosAssetHash tags, so nothing was cut in the port even though every byte differs.
        var resx = RequireXboxResx();
        var iso = RequirePs2Iso();

        var xboxBytes = File.ReadAllBytes(Path.Combine(resx, "c1", "BAR", "BAR_S.clp"));
        var xbox = BosClumpFile.Parse(xboxBytes, "BAR_S.clp");

        using var disc = ArchiveReader.Open(iso);
        var ps2Bytes = disc.ReadFile(@"DATA\C1\BAR\BAR_S.CLP");
        Assert.NotNull(ps2Bytes);
        var ps2 = BosClumpFile.Parse(ps2Bytes, "BAR_S.CLP");

        Assert.Equal(34, xbox.Sections.Count);
        Assert.Equal(34, ps2.Sections.Count);
        Assert.Equal(
            xbox.Sections.Select(s => s.Tag).Order().ToList(),
            ps2.Sections.Select(s => s.Tag).Order().ToList());
    }

    [Fact]
    public void TheSameTextureDecodesToTheSamePixelsOnBothConsoles()
    {
        // ⚑⚑ THE CROSS-CONSOLE ORACLE. The two discs key their clump sections with the same
        // BosAssetHash, so a texture can be decoded through two completely unrelated readers — an
        // Xbox linear P8 section and a PS2 GS packet stream with a CSM1 CLUT and a block swizzle —
        // and the RGB compared byte for byte. A wrong reading of either scores zero.
        var resx = RequireXboxResx();
        var iso = RequirePs2Iso();

        var xbox = new Dictionary<uint, BosXboxTexture>();
        foreach (var (path, bytes, clump) in XboxClumps(resx))
        {
            foreach (var section in clump.Sections)
            {
                var span = bytes.AsSpan(section.Offset, section.Size);
                if (BosXboxTexture.IsTexture(span) && !xbox.ContainsKey(section.Tag))
                {
                    xbox[section.Tag] = BosXboxTexture.Parse(span, $"{Path.GetFileName(path)}#{section.Tag:X8}");
                }
            }
        }

        var shared = 0;
        var seen = new HashSet<uint>();
        var sameDimensions = 0;
        var identicalRgb = 0;
        var ps2TallerByOne = 0;

        using var disc = ArchiveReader.Open(iso);
        foreach (var entry in disc.ListFiles()
                     .Where(e => e.Name.EndsWith(".CLP", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = disc.ReadFile(entry.FullPath);
            if (bytes is null || !BosClumpFile.TryParse(bytes, entry.Name, out var clump, out _))
            {
                continue;
            }

            foreach (var section in clump.Sections)
            {
                if (!xbox.TryGetValue(section.Tag, out var ours))
                {
                    continue;
                }

                var span = bytes.AsSpan(section.Offset, section.Size);
                if (!BosTexture.IsTexture(span) ||
                    !BosTexture.TryParse(span, $"{entry.Name}#{section.Tag:X8}", out var theirs, out _))
                {
                    continue;
                }

                // ⚠ A tag repeats across the PS2's level clumps; count each texture once.
                if (!seen.Add(section.Tag))
                {
                    continue;
                }

                shared++;
                if (theirs.Width == ours.Width && theirs.Height == ours.Height + 1)
                {
                    ps2TallerByOne++;
                }

                if (theirs.Width != ours.Width || theirs.Height != ours.Height)
                {
                    continue;
                }

                sameDimensions++;
                var same = true;
                for (var texel = 0; texel < ours.Width * ours.Height && same; texel++)
                {
                    for (var channel = 0; channel < 3; channel++)
                    {
                        if (ours.Rgba[texel * 4 + channel] != theirs.Rgba[texel * 4 + channel])
                        {
                            same = false;
                            break;
                        }
                    }
                }

                if (same)
                {
                    identicalRgb++;
                }
            }
        }

        // Asserted together so one run reports all four: shared tags, pairs whose dimensions
        // agree, pairs whose RGB is BYTE-IDENTICAL across the two consoles, and the pairs the PS2
        // stores exactly one row taller.
        // ⚑ Every dimension mismatch is that one row: the PS2 uploads a PSMT8 image as PSMCT32 at
        // half height, so an odd height was padded by one for that disc.
        Assert.Equal(
            "858 shared, 838 same size, 590 identical, 20 padded",
            $"{shared} shared, {sameDimensions} same size, {identicalRgb} identical, {ps2TallerByOne} padded");
        Assert.Equal(shared - sameDimensions, ps2TallerByOne);
    }

    [Fact]
    public void TheDiscsOwnDebugDumpsReproduceTheirStoredKeysThroughTheNameHash()
    {
        // ⚑ The .NFO dumps print the stored key beside the string, which makes them an oracle the
        // hash cannot be fitted to after the fact. 5,215 of 5,589 reproduce; every miss is a
        // display string filed under an internal name's key, and the control that separates the
        // two kinds is the UNDERSCORE — see the assertions at the end of this method.
        var resx = RequireXboxResx();
        var files = 0;
        var pairs = 0;
        var names = 0;
        var missesWithAnUnderscore = 0;
        var provenLookingLikeALabel = 0;

        foreach (var path in Directory.EnumerateFiles(resx, "*.nfo", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var nfo = BosNfoFile.Parse(File.ReadAllText(path), Path.GetFileName(path));
            files++;
            pairs += nfo.Entries.Count;
            names += nfo.NameCount;
            foreach (var entry in nfo.Entries)
            {
                var looksLikeALabel = entry.Value.Length > 0 && char.IsUpper(entry.Value[0]) &&
                                      !entry.Value.Contains('_', StringComparison.Ordinal);
                if (!entry.IsName && entry.Value.Contains('_', StringComparison.Ordinal))
                {
                    missesWithAnUnderscore++;
                }

                if (entry.IsName && looksLikeALabel)
                {
                    provenLookingLikeALabel++;
                }
            }

            // The dump's own numstrings is the parse's self-check; BosNfoFile.Parse enforces it.
            Assert.Equal(nfo.DeclaredCount, nfo.Entries.Count);
        }

        Assert.Equal(53, files);
        Assert.Equal(5_589, pairs);
        Assert.Equal(5_215, names);
        // ⚑ WHAT THE 374 MISSES ARE, and the control that makes it mean something: not one of
        // them contains an UNDERSCORE, while this game's identifiers are underscore-separated —
        // only 13 of the 5,215 proven names are Title-Case without one. The misses are 69 distinct
        // display labels ("Footlocker", "Vault Door", "Save Game Console"), repeated across levels.
        // ⚠ An earlier reading, "every miss contains a space", is REFUTED: 81 of the 374 are
        // one-word labels (Footlocker 34, Locker 13, Switch 11, …).
        Assert.Equal(0, missesWithAnUnderscore);
        Assert.Equal(13, provenLookingLikeALabel);
    }

    [Fact]
    public void TheMasterDefinitionsAreNamedByStringsWhoseHashProvesThem()
    {
        // ⚑ Before this upgrade the Xbox tree named 1,920 of its 2,243 master definitions by taking
        // whatever string sat under the key. Now 1,775 carry a name the hash PROVES and 396 more a
        // display string, leaving 72 unnamed. The counts are what changed; the correctness is that
        // an EditorID is now the engine's own identifier rather than a synthetic index.
        var resx = RequireXboxResx();
        var extracted = Directory.GetParent(resx)!.FullName;

        using var install = new LooseFileSystem(extracted);
        var records = new RecordCollection();
        BosRecordSource.Populate(install, records, CancellationToken.None);

        var definitions = records.GenericRecords
            .Where(r => r.RecordType == BosRecordSource.DefinitionRecordType)
            .ToList();

        Assert.Equal(2_243, definitions.Count);
        Assert.Equal(1_775, definitions.Count(r => r.Fields.ContainsKey("Name")));
        Assert.Equal(2_171, definitions.Count(r => r.FullName is not null));
        Assert.Equal(72, definitions.Count(r => r.FullName is null));

        // ⚑ PROSE IS THE ORACLE. These four pairs are the two halves of one record — an internal
        // identifier that hashes to the key and the UI text filed under it — and no arithmetic on
        // the container could invent either, let alone pair them.
        var byName = definitions
            .Where(r => r.Fields.TryGetValue("Name", out var n) && n is string)
            .ToLookup(r => (string)r.Fields["Name"]!, r => r.Fields.GetValueOrDefault("DisplayName") as string,
                StringComparer.Ordinal);

        Assert.Contains("Glowing Radscorpion", byName["creature_radscorpion_glowing"]);
        Assert.Contains("Giant Radscorpion", byName["creature_radscorpion_giant"]);
        Assert.Contains("Power Armor", byName["cain_armor_power_torso"]);
        Assert.Contains("Mutant Nightkin", byName["creature_mutant_nightkin_rocket"]);

        // The proven name becomes the EditorID; the fallback survives only where nothing names it.
        var editorIds = definitions.Select(r => r.EditorId ?? string.Empty).ToList();
        Assert.Equal(2_243 - 1_775, editorIds.Count(e => e.StartsWith("BOS_", StringComparison.Ordinal)));
    }
}