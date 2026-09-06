using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Png;
using BethesdaMultitool.Core.Formats.Travels.Dawnstar;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Png;

/// <summary>
///     Real-asset coverage for <see cref="PngImageDecoder" /> against every PNG the three J2ME
///     TES Travels titles ship. Measured from the staged JARs on 2026-09-05: 16 loose images in
///     Stormhold, 3 loose in Dawnstar, 26 in Oblivion mobile — every one colour type 3 (palette)
///     at bit depth 2, 4 or 8, and one Stormhold image ADAM7 INTERLACED.
///     <para>
///         These are fixed files, so the counts are exact and any drift is a regression rather
///         than content variance.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class PngImageDecoderRetailTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static TheoryData<string> Jars()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[] { "Stormhold", "Dawnstar", "OblivionMobile" })
        {
            data.Add(name);
        }

        return data;
    }

    private static string? JarPath(string game) => game switch
    {
        "Stormhold" => RealAssetPaths.Travels.StormholdJar(),
        "Dawnstar" => RealAssetPaths.Travels.DawnstarJar(),
        "OblivionMobile" => RealAssetPaths.Travels.OblivionMobileJar(),
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, "Unknown Travels JAR.")
    };

    /// <summary>
    ///     ⚠ Dawnstar keeps only 3 images loose in the JAR; its real art — 43 PNGs — lives inside
    ///     the <c>imgfiles.lmp</c> lump, and none of the 3 loose ones carries a tRNS chunk. A test
    ///     that scanned loose members alone would silently cover 3 of 46 images for that game.
    /// </summary>
    private const string DawnstarImageLump = "imgfiles.lmp";

    private static int ExpectedPngCount(string game) => game switch
    {
        "Stormhold" => 16,
        "Dawnstar" => 3 + 43,
        _ => 26
    };

    /// <summary>
    ///     Every PNG one game ships, as (entry name, bytes) — loose JAR members plus, for
    ///     Dawnstar, the contents of its image lump.
    /// </summary>
    private static List<(string Name, byte[] Bytes)> AllPngs(string game)
    {
        var jar = JarPath(game);
        Assert.SkipWhen(jar is null, $"The {game} JAR is not staged under Sample/Full_Builds.");

        using var reader = ArchiveReader.Open(jar!);
        var result = new List<(string, byte[])>();
        foreach (var path in reader.EnumerateFilePaths().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = reader.ReadFile(path);
            if (bytes is null)
            {
                continue;
            }

            if (IsPng(bytes))
            {
                result.Add((path, bytes));
                continue;
            }

            if (!Path.GetFileName(path).Equals(DawnstarImageLump, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lump = DawnstarLumpArchive.Parse(bytes, DawnstarImageLump);
            foreach (var entry in lump.Entries)
            {
                var payload = lump.Read(entry).ToArray();
                if (IsPng(payload))
                {
                    result.Add(($"{DawnstarImageLump}/{entry.Name}", payload));
                }
            }
        }

        return result;
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(PngSignature);

    /// <summary>
    ///     The bit depths the retail Travels art actually uses. PNG allows 1 as well for a
    ///     palette image; no image in either J2ME game declares it, so accepting 1 here would
    ///     widen the assertion past what was measured.
    /// </summary>
    private static readonly int[] PalettedBitDepths = [2, 4, 8];

    /// <summary>
    ///     Reads the header, failing the test by name if it does not parse. Every call site wants
    ///     a value, and an unparsed header is a fixture problem worth naming rather than a null
    ///     dereference deep inside an assertion.
    /// </summary>
    private static PngImageInfo Info(string game, string name, byte[] bytes)
    {
        var info = PngImageDecoder.ReadInfo(bytes);
        Assert.True(info.HasValue, $"{game}/{name}: IHDR did not parse.");
        return info!.Value;
    }

    /// <summary>The PNG census, pinned exactly.</summary>
    [Theory]
    [MemberData(nameof(Jars))]
    public void EveryPngIsPalettized(string game)
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var pngs = AllPngs(game);
        Assert.Equal(ExpectedPngCount(game), pngs.Count);

        foreach (var (name, bytes) in pngs)
        {
            var info = Info(game, name, bytes);
            Assert.True(info.IsPalettized,
                $"{game}/{name}: colour type {info.ColourTypeName}, expected palette.");
            Assert.Contains(info.BitDepth, PalettedBitDepths);
        }
    }

    /// <summary>
    ///     Every image decodes, and the pixel buffer matches the dimensions IHDR declared. This is
    ///     the check that would fail if Magick.NET were built without PNG support, or if the
    ///     interlaced image were mis-sized.
    /// </summary>
    [Theory]
    [MemberData(nameof(Jars))]
    public void EveryPngDecodesToItsDeclaredSize(string game)
    {
        BucketBTestGuard.SkipUnlessEnabled();

        foreach (var (name, bytes) in AllPngs(game))
        {
            var info = Info(game, name, bytes);
            var decoded = PngImageDecoder.Decode(bytes);

            Assert.Equal(info.Width, decoded.Width);
            Assert.Equal(info.Height, decoded.Height);
            Assert.Equal(info.Width * info.Height * 4, decoded.Pixels.Length);
            Assert.Equal(1, decoded.MipCount);
        }
    }

    /// <summary>
    ///     A tRNS chunk must actually reach the output. This is the discriminating check: a
    ///     decoder that ignored transparency would still produce correctly sized buffers and pass
    ///     every other assertion here, but every pixel would come back opaque.
    /// </summary>
    [Theory]
    [MemberData(nameof(Jars))]
    public void TransparencyChunksProduceTransparentPixels(string game)
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var checkedAny = false;
        foreach (var (name, bytes) in AllPngs(game))
        {
            var info = Info(game, name, bytes);
            if (!info.HasTransparencyChunk)
            {
                continue;
            }

            checkedAny = true;
            var pixels = PngImageDecoder.Decode(bytes).Pixels;
            var transparent = 0;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 255)
                {
                    transparent++;
                }
            }

            Assert.True(transparent > 0,
                $"{game}/{name}: declares tRNS but every decoded pixel is opaque.");
        }

        Assert.True(checkedAny, $"{game}: no PNG with a tRNS chunk was found to test.");
    }

    /// <summary>
    ///     Images with NO transparency chunk must come back fully opaque — the converse of the
    ///     test above, so neither can pass under a decoder that hard-codes one answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(Jars))]
    public void ImagesWithoutTransparencyAreFullyOpaque(string game)
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var checkedAny = false;
        foreach (var (name, bytes) in AllPngs(game))
        {
            var info = Info(game, name, bytes);
            if (info.HasTransparencyChunk)
            {
                continue;
            }

            checkedAny = true;
            var pixels = PngImageDecoder.Decode(bytes).Pixels;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                Assert.True(pixels[i] == 255,
                    $"{game}/{name}: no tRNS chunk, but a pixel decoded with alpha {pixels[i]}.");
            }
        }

        Assert.True(checkedAny, $"{game}: no PNG without a tRNS chunk was found to test.");
    }

    /// <summary>
    ///     Stormhold ships exactly one Adam7 image. It decodes to its declared size and is not a
    ///     single flat colour — a sequential misread of an interlaced stream still fills the
    ///     buffer, so size alone would not catch it, but a blank result would show as one colour.
    /// </summary>
    [Fact]
    public void StormholdInterlacedImageDecodes()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var interlaced = AllPngs("Stormhold")
            .Where(p => Info("Stormhold", p.Name, p.Bytes).Interlaced)
            .ToList();

        Assert.Single(interlaced);

        var (name, bytes) = interlaced[0];
        var info = Info("Stormhold", name, bytes);
        var decoded = PngImageDecoder.Decode(bytes);

        Assert.Equal(info.Width, decoded.Width);
        Assert.Equal(info.Height, decoded.Height);

        var distinct = new HashSet<uint>();
        for (var i = 0; i + 3 < decoded.Pixels.Length; i += 4)
        {
            distinct.Add(BitConverter.ToUInt32(decoded.Pixels, i));
        }

        Assert.True(distinct.Count > 1, $"Stormhold/{name}: interlaced image decoded to one colour.");
    }
}
