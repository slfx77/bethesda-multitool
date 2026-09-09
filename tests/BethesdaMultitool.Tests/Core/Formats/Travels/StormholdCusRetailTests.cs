using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Travels.Stormhold;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Opt-in checks of the Stormhold MIDlet JAR (<c>RUN_BUCKET_B=1</c>). The <c>.cus</c> format
///     has NO magic — the header arithmetic tiling the file exactly is the entire identification —
///     so the numbers pinned here are the format's only real evidence. They are exact because the
///     JAR is a fixed fixture: 37 resources, 581 palette entries, and the transparent-slot census
///     that proves slot 0 must not be assumed.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class StormholdCusRetailTests
{
    /// <summary>
    ///     Every <c>.cus</c> in the JAR, paired with the raw bytes it came from so the tiling
    ///     assertion can compare the decoded fields against a length it did not itself compute.
    /// </summary>
    private static List<(byte[] Bytes, StormholdCusImage Image)> LoadEveryCus()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var jar = RealAssetPaths.Travels.StormholdJar();
        Assert.SkipWhen(jar is null, RealAssetPaths.SkipMessage("the Stormhold JAR"));

        using var reader = ArchiveReader.Open(jar);
        var loaded = new List<(byte[] Bytes, StormholdCusImage Image)>();
        foreach (var entry in reader.ListFiles()
                     .Where(e => e.FullPath.EndsWith(".cus", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var bytes = reader.Extract(entry);
            loaded.Add((bytes, StormholdCusImage.Parse(bytes, entry.Name)));
        }

        return loaded;
    }

    /// <summary>
    ///     Every resource parses, and the exact tiling holds on all 37 — which is the same
    ///     statement as "the layout is right", since a wrong field width would leave a remainder.
    /// </summary>
    [Fact]
    public void EveryCusResource_TilesItsFileExactly()
    {
        var loaded = LoadEveryCus();

        Assert.Equal(37, loaded.Count);
        foreach (var (bytes, image) in loaded)
        {
            var expected = StormholdCusImage.HeaderLength
                           + StormholdCusImage.PaletteEntryLength * image.Palette444.Length
                           + image.Width * image.Height;
            Assert.Equal(bytes.Length, expected);
            Assert.Equal(image.Width * image.Height, image.Bitmap.Indices.Length);
            Assert.Equal(image.Width, image.Bitmap.Width);
            Assert.Equal(image.Height, image.Bitmap.Height);
        }

        // The parse already rejects an index at or above N; assert it independently so this test
        // still fails if that check is ever loosened.
        Assert.All(loaded, item => Assert.All(
            item.Image.Bitmap.Indices, index => Assert.True(index < item.Image.Palette444.Length)));
    }

    /// <summary>
    ///     581 palette entries across the 37 files, every one with bits 15-12 clear. That is what
    ///     licenses reading the u16 as 0RGB 4:4:4 rather than a 5:5:5 or 5:6:5 packing.
    /// </summary>
    [Fact]
    public void EveryPaletteEntry_HasAClearTopNibble()
    {
        var images = LoadEveryCus().Select(l => l.Image).ToList();

        var entries = images.Sum(i => i.Palette444.Length);
        Assert.Equal(581, entries);
        Assert.All(images, image => Assert.All(
            image.Palette444, entry => Assert.Equal(0, entry >> 12)));
    }

    /// <summary>
    ///     Palette sizes are tightly clustered — a 16-colour art budget with three files trimmed
    ///     below it. A parse that mis-read the count byte would not land on this shape.
    /// </summary>
    [Fact]
    public void PaletteCountCensus_MatchesTheRetailShape()
    {
        var images = LoadEveryCus().Select(l => l.Image).ToList();

        var census = images.GroupBy(i => i.Palette444.Length)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(new Dictionary<int, int> { [12] = 1, [13] = 1, [15] = 4, [16] = 31 }, census);
    }

    /// <summary>
    ///     The transparency contract: the flag is set on all 37, the key colour is present in the
    ///     palette on all 37, and the slot it lands on is NOT always 0. Three files key on a later
    ///     slot, which is the whole reason the reader searches instead of assuming index 0.
    /// </summary>
    [Fact]
    public void TransparentSlotCensus_ShowsThreeFilesThatDoNotKeyOnSlotZero()
    {
        var images = LoadEveryCus().Select(l => l.Image).ToList();

        Assert.All(images, image => Assert.True(image.HasKeyColour));
        Assert.All(images, image => Assert.Contains(image.KeyColour444, image.Palette444));

        var census = images.GroupBy(i => i.TransparentIndex).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<int, int> { [0] = 34, [2] = 1, [4] = 2 }, census);

        Assert.Equal(
            new[] { "trainerfembelt1.cus", "trainerfemneck.cus" },
            images.Where(i => i.TransparentIndex == 4)
                .Select(i => i.Name.ToLowerInvariant())
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            "wardenbody2f2half.cus",
            images.Single(i => i.TransparentIndex == 2).Name.ToLowerInvariant());
    }

    /// <summary>Dimensions stay inside the phone's 176x208 screen, as sprite art must.</summary>
    [Fact]
    public void DimensionsStayInsideTheMeasuredRange()
    {
        var images = LoadEveryCus().Select(l => l.Image).ToList();

        Assert.Equal(12, images.Min(i => i.Width));
        Assert.Equal(212, images.Max(i => i.Width));
        Assert.Equal(10, images.Min(i => i.Height));
        Assert.Equal(158, images.Max(i => i.Height));
    }

    /// <summary>
    ///     One named worked example, read the way a consumer would: the smallest resource, decoded
    ///     end to end.
    /// </summary>
    [Fact]
    public void BagSmall_DecodesToItsMeasuredHeader()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var jar = RealAssetPaths.Travels.StormholdJar();
        Assert.SkipWhen(jar is null, RealAssetPaths.SkipMessage("the Stormhold JAR"));

        using var reader = ArchiveReader.Open(jar);
        var bytes = reader.ReadFile("bagsmall.cus");
        Assert.NotNull(bytes);
        Assert.Equal(156, bytes.Length);

        var image = StormholdCusImage.Parse(bytes, "bagsmall.cus");

        Assert.Equal(12, image.Width);
        Assert.Equal(10, image.Height);
        Assert.True(image.HasKeyColour);
        Assert.Equal(0x0FB2, image.KeyColour444);
        Assert.Equal(12, image.Palette444.Length);
        Assert.Equal(0, image.TransparentIndex);
        Assert.Equal(120, image.Bitmap.Indices.Length);
        Assert.True(StormholdCusImage.TryProbe(bytes));
    }

    /// <summary>
    ///     The probe is the archive chain's only handle on this format, so it must accept every
    ///     <c>.cus</c> and reject every other resource in the same JAR (classes, PNGs, the
    ///     <c>readUTF</c> <c>.dat</c> tables and the manifest).
    /// </summary>
    [Fact]
    public void TryProbe_AcceptsEveryCusAndNothingElseInTheJar()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var jar = RealAssetPaths.Travels.StormholdJar();
        Assert.SkipWhen(jar is null, RealAssetPaths.SkipMessage("the Stormhold JAR"));

        using var reader = ArchiveReader.Open(jar);
        var accepted = 0;
        var falsePositives = new List<string>();
        foreach (var entry in reader.ListFiles())
        {
            var isCus = entry.FullPath.EndsWith(".cus", StringComparison.OrdinalIgnoreCase);
            var probed = StormholdCusImage.TryProbe(reader.Extract(entry));
            if (isCus && probed)
            {
                accepted++;
            }
            else if (!isCus && probed)
            {
                falsePositives.Add(entry.FullPath);
            }
        }

        Assert.Equal(37, accepted);
        Assert.Empty(falsePositives);
    }
}