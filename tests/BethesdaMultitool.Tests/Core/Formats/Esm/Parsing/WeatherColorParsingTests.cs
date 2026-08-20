using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the WTHR NAM0/PNAM weather-color layout across the FO3↔FNV format gap. FNV stores TEN color
///     categories of SIX RGBA bands each (24 bytes — Sunrise/Day/Sunset/Night/HighNoon/Midnight); FO3 (the
///     format the earliest crash dumps carry) stores only FOUR bands (16 bytes), since FNV added High Noon
///     + Midnight. The band count is detected structurally from NAM0's length (10 categories) rather than
///     assumed, so FO3-era records don't drift 8 bytes per category (horizon black at noon / white at
///     night). These tests guard both the stride and the structural detection.
/// </summary>
public class WeatherColorParsingTests
{
    // A subrecord = 4-byte signature + 2-byte LE length + payload, matching EsmSubrecordUtils.
    private static byte[] BuildSubrecord(string sig, int payloadLength)
    {
        var buffer = new byte[6 + payloadLength];
        Encoding.ASCII.GetBytes(sig).CopyTo(buffer, 0);
        buffer[4] = (byte)(payloadLength & 0xFF);
        buffer[5] = (byte)((payloadLength >> 8) & 0xFF);
        return buffer;
    }

    // Each band is one endian-aware RGBA dword. N full categories at the given stride.
    private static byte[] BuildCategoryBuffer(int categories, int bandsPerEntry, bool bigEndian = false)
    {
        var data = new byte[categories * bandsPerEntry * 4];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i + 1); // byte value encodes its offset so bands are trivially identifiable.
        }

        if (bigEndian)
        {
            for (var offset = 0; offset < data.Length; offset += 4)
            {
                Array.Reverse(data, offset, 4);
            }
        }

        return data;
    }

    private static void WriteRgba(byte[] target, int offset, WeatherRgba value, bool bigEndian)
    {
        var raw = value.R |
                  ((uint)value.G << 8) |
                  ((uint)value.B << 16) |
                  ((uint)value.A << 24);
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset, 4), raw);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), raw);
    }

    private static void WriteSingle(byte[] target, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteSingleBigEndian(target.AsSpan(offset, 4), value);
        else
            BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(offset, 4), value);
    }

    [Theory]
    // Flags occupy the terminal source-endian dword in the two longer layouts. The 132-byte layout ends at its
    // immediate unknown dword and does not explicitly author cinematic-enable flags.
    [InlineData(132, -1, false)]
    [InlineData(132, -1, true)]
    [InlineData(148, 144, false)]
    [InlineData(148, 144, true)]
    [InlineData(152, 148, false)]
    [InlineData(152, 148, true)]
    public void ReadImageSpaceCinematicFlags_UsesTerminalSourceEndianDword(
        int length, int flagsOffset, bool bigEndian)
    {
        var data = new byte[length];
        var immediateUnknownOffset = length == 152 ? 132 : 128;
        const uint immediateUnknown = 0xA1B2_C302;
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(immediateUnknownOffset, sizeof(uint)), immediateUnknown);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(immediateUnknownOffset, sizeof(uint)),
                immediateUnknown);
        const uint terminalFlagsLane = 0xA5F0_0007;
        if (flagsOffset >= 0)
        {
            if (bigEndian)
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(flagsOffset, sizeof(uint)), terminalFlagsLane);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(flagsOffset, sizeof(uint)), terminalFlagsLane);
        }

        var expected = flagsOffset < 0
            ? ImageSpaceCinematicFlags.None
            : ImageSpaceCinematicFlags.Saturation |
              ImageSpaceCinematicFlags.Contrast |
              ImageSpaceCinematicFlags.Tint;
        Assert.Equal(expected,
            MiscEnvironmentHandler.ReadImageSpaceCinematicFlags(data, bigEndian));
        var decodedImmediateUnknown = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(immediateUnknownOffset, sizeof(uint)))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(immediateUnknownOffset, sizeof(uint)));
        Assert.Equal(0x02u, decodedImmediateUnknown & 0x0F);
    }

    [Fact]
    public void ImageSpaceCinematic_PreservesWhetherFlagsWereActuallyAuthored()
    {
        var classic132Bytes = new byte[132];
        classic132Bytes[128] = 0x02;
        var classic148Bytes = new byte[148];
        classic148Bytes[128] = 0x02;
        classic148Bytes[144] = 0xA7;

        var classic132 = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(classic132Bytes, false);
        var classic148 = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(classic148Bytes, false);
        var modernCnam = new ImageSpaceCinematic { HasExplicitFlags = false };

        Assert.False(classic132.Cinematic.HasExplicitFlags);
        Assert.Equal(ImageSpaceCinematicFlags.None, classic132.Cinematic.Flags);
        Assert.True(classic148.Cinematic.HasExplicitFlags);
        Assert.Equal(
            ImageSpaceCinematicFlags.Saturation | ImageSpaceCinematicFlags.Contrast | ImageSpaceCinematicFlags.Tint,
            classic148.Cinematic.Flags);
        Assert.False(modernCnam.HasExplicitFlags);
    }

    /// <summary>
    ///     FO3/FNV IMGS DNAM: Skin Dimmer exists only in the 152-byte (form version ≥ 14) layout and
    ///     shifts everything after +56; 148 and 132 omit it and differ only in trailing padding.
    ///     <para>
    ///         No reliable authored on-disk fade block is established after Tint.Amount. The first
    ///         dword remains opaque; the 148/152 layouts additionally carry three opaque lanes and
    ///         terminal cinematic flags at source offset 144/148. The 132-byte layout ends earlier.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(132, false)]
    [InlineData(132, true)]
    [InlineData(148, false)]
    [InlineData(148, true)]
    [InlineData(152, false)]
    [InlineData(152, true)]
    public void ReadClassicImageSpaceDnam_NormalizesSkinDimmerWithoutLosingAuthoredData(
        int length, bool bigEndian)
    {
        var data = new byte[length];

        void WriteFloat(int offset, float value)
        {
            if (bigEndian)
                BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset, 4), value);
            else
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);
        }

        for (var index = 0; index < 14; index++) WriteFloat(index * 4, index + 1f);
        var hasSkin = length >= 152;
        if (hasSkin) WriteFloat(56, 15f);
        var cinematicBase = hasSkin ? 100 : 96;
        WriteFloat(cinematicBase, 0.7f);
        WriteFloat(cinematicBase + 4, 0.2f);
        WriteFloat(cinematicBase + 8, 1.3f);
        WriteFloat(cinematicBase + 12, 0.9f);
        WriteFloat(cinematicBase + 16, 0.1f);
        WriteFloat(cinematicBase + 20, 0.2f);
        WriteFloat(cinematicBase + 24, 0.3f);
        WriteFloat(cinematicBase + 28, 0.4f);

        var postBodyOffset = hasSkin ? 132 : 128;
        const uint immediateUnknown = 0xA1B2_C302;
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(postBodyOffset, sizeof(uint)), immediateUnknown);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(postBodyOffset, sizeof(uint)), immediateUnknown);
        var hasExplicitFlags = length > 132;
        const uint terminalFlags = 0xA5F0_000F;
        if (hasExplicitFlags)
        {
            if (bigEndian)
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(length - 4, sizeof(uint)), terminalFlags);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(length - 4, sizeof(uint)), terminalFlags);
        }

        var decoded = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(data, bigEndian);

        Assert.Equal(10f, decoded.Hdr.LumRampMin);
        Assert.Equal(11f, decoded.Hdr.LumRampMax);
        Assert.Equal(12f, decoded.Hdr.SunlightDimmer);
        Assert.Equal(13f, decoded.Hdr.GrassDimmer);
        Assert.Equal(14f, decoded.Hdr.TreeDimmer);
        Assert.Equal(hasSkin ? 15f : 1f, decoded.Hdr.SkinDimmer);
        Assert.Equal(0.7f, decoded.Cinematic.Saturation);
        Assert.Equal(0.2f, decoded.Cinematic.ContrastAvgLum);
        Assert.Equal(1.3f, decoded.Cinematic.Contrast);
        Assert.Equal(0.9f, decoded.Cinematic.Brightness);
        Assert.Equal(hasExplicitFlags, decoded.Cinematic.HasExplicitFlags);
        Assert.Equal(hasExplicitFlags ? ImageSpaceCinematicFlags.All : ImageSpaceCinematicFlags.None,
            decoded.Cinematic.Flags);
        Assert.Equal(0.1f, decoded.Tint.Red);
        Assert.Equal(0.4f, decoded.Tint.Amount);
        Assert.Equal(length == 132 ? 1 : 5, decoded.PostBodyWords.Length);
        Assert.Equal(immediateUnknown, decoded.PostBodyWords[0]);
        if (hasExplicitFlags) Assert.Equal(terminalFlags, decoded.PostBodyWords[4]);
    }

    /// <summary>
    ///     Retail-shaped guard against mistaking the plausible immediate unknown dword for the
    ///     terminal flags dword. The two fields deliberately carry different low nibbles.
    /// </summary>
    [Theory]
    [InlineData(132)]
    [InlineData(148)]
    [InlineData(152)]
    public void ReadImageSpaceCinematicFlags_IgnoresImmediateUnknownDword(int length)
    {
        var data = new byte[length];
        var immediateUnknownOffset = length == 152 ? 132 : 128;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(immediateUnknownOffset, sizeof(uint)), 0x0F);
        if (length > 132)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(length - 4, sizeof(uint)), 0x02);

        Assert.Equal(
            length == 132 ? ImageSpaceCinematicFlags.None : ImageSpaceCinematicFlags.Contrast,
            MiscEnvironmentHandler.ReadImageSpaceCinematicFlags(data, false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadWeatherRgbRow_PreservesFourBandsIncludingRawPadding(bool bigEndian)
    {
        var data = new byte[16];
        for (var band = 0; band < 4; band++)
        {
            var rgba = new[]
            {
                (byte)(1 + band * 3), (byte)(2 + band * 3), (byte)(3 + band * 3),
                (byte)(0x40 + band)
            };
            if (bigEndian) Array.Reverse(rgba);
            rgba.CopyTo(data, band * 4);
        }

        var row = MiscEnvironmentHandler.ReadWeatherRgbRow(data, bigEndian);

        Assert.Equal(new WeatherRgba(1, 2, 3, 0x40), row.Sunrise);
        Assert.Equal(new WeatherRgba(4, 5, 6, 0x41), row.Day);
        Assert.Equal(new WeatherRgba(7, 8, 9, 0x42), row.Sunset);
        Assert.Equal(new WeatherRgba(10, 11, 12, 0x43), row.Night);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadDirectionalAmbientMean_AveragesTheSixDirectionsOnly(bool bigEndian)
    {
        // One DALC band: six direction colors (X+/X−/Y+/Y−/Z+/Z−) + specular RGBA + fresnel float
        // (32 bytes, the FO4 layout). The mean must cover ONLY the six directions — folding the
        // trailing specular in would skew every band bright.
        var data = new byte[32];
        for (var d = 0; d < 6; d++)
        {
            WriteRgba(data, d * 4, new WeatherRgba(
                (byte)((d + 1) * 6), // R: 6..36  → mean 21
                (byte)((d + 1) * 12), // G: 12..72 → mean 42
                (byte)((d + 1) * 18), // B: 18..108 → mean 63
                0xFF), bigEndian);
        }

        WriteRgba(data, 24, new WeatherRgba(255, 255, 255, 255), bigEndian);

        var mean = MiscEnvironmentHandler.ReadDirectionalAmbientMean(data, bigEndian);

        Assert.Equal(new WeatherRgba(21, 42, 63, 255), mean);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadDirectionalAmbientCube_PreservesEveryFaceSpecularAndFresnel(bool bigEndian)
    {
        var expectedFaces = new[]
        {
            new WeatherRgba(1, 2, 3, 4),
            new WeatherRgba(5, 6, 7, 8),
            new WeatherRgba(9, 10, 11, 12),
            new WeatherRgba(13, 14, 15, 16),
            new WeatherRgba(17, 18, 19, 20),
            new WeatherRgba(21, 22, 23, 24)
        };
        var expectedSpecular = new WeatherRgba(101, 102, 103, 104);
        const float expectedFresnel = 3.25f;
        var data = new byte[32];

        for (var face = 0; face < expectedFaces.Length; face++)
            WriteRgba(data, face * 4, expectedFaces[face], bigEndian);
        WriteRgba(data, 24, expectedSpecular, bigEndian);
        WriteSingle(data, 28, expectedFresnel, bigEndian);

        var cube = MiscEnvironmentHandler.ReadDirectionalAmbientCube(data, bigEndian);

        Assert.Equal(expectedFaces[0], cube.PositiveX);
        Assert.Equal(expectedFaces[1], cube.NegativeX);
        Assert.Equal(expectedFaces[2], cube.PositiveY);
        Assert.Equal(expectedFaces[3], cube.NegativeY);
        Assert.Equal(expectedFaces[4], cube.PositiveZ);
        Assert.Equal(expectedFaces[5], cube.NegativeZ);
        Assert.Equal(expectedSpecular, cube.Specular);
        Assert.Equal(expectedFresnel, cube.FresnelPower);
    }

    [Fact]
    public void ReadWeatherColors_SixBands_Uses24ByteStride()
    {
        var colors = MiscEnvironmentHandler.ReadWeatherColors(BuildCategoryBuffer(2, 6), false, 6);

        Assert.Equal(2, colors.Count);
        Assert.Equal(new WeatherRgba(1, 2, 3, 4), colors[0].Sunrise);
        Assert.Equal(new WeatherRgba(17, 18, 19, 20), colors[0].HighNoon);
        Assert.Equal(new WeatherRgba(21, 22, 23, 24), colors[0].Midnight);

        // Category 1 must START at byte 24 (24-byte stride). A regressed 16-byte stride would instead
        // read category 0's HighNoon bytes (17,18,19,20) here — this assertion is the stride guard.
        Assert.Equal(new WeatherRgba(25, 26, 27, 28), colors[1].Sunrise);
        Assert.NotEqual(colors[0].HighNoon, colors[1].Sunrise);
    }

    [Fact]
    public void ReadWeatherColors_FourBands_Uses16ByteStride_AndBackfillsNoonMidnight()
    {
        // FO3: 4 bands, 16-byte stride. HighNoon/Midnight don't exist → fall back to Day/Night.
        var colors = MiscEnvironmentHandler.ReadWeatherColors(BuildCategoryBuffer(2, 4), false, 4);

        Assert.Equal(2, colors.Count);
        Assert.Equal(new WeatherRgba(1, 2, 3, 4), colors[0].Sunrise);
        Assert.Equal(new WeatherRgba(5, 6, 7, 8), colors[0].Day);
        Assert.Equal(new WeatherRgba(13, 14, 15, 16), colors[0].Night);
        Assert.Equal(colors[0].Day, colors[0].HighNoon);
        Assert.Equal(colors[0].Night, colors[0].Midnight);

        // Category 1 starts at byte 16 (16-byte stride), not 24.
        Assert.Equal(new WeatherRgba(17, 18, 19, 20), colors[1].Sunrise);
    }

    [Fact]
    public void ReadWeatherColors_FullFnvNam0_DecodesTenCategories()
    {
        var colors = MiscEnvironmentHandler.ReadWeatherColors(new byte[240], false, 6);
        Assert.Equal(10, colors.Count);
    }

    [Fact]
    public void ReadWeatherColors_FullFo3Nam0_DecodesTenCategories()
    {
        // FO3 NAM0 is 160 bytes = 10 categories × 16 bytes (4 bands).
        var colors = MiscEnvironmentHandler.ReadWeatherColors(new byte[160], false, 4);
        Assert.Equal(10, colors.Count);
    }

    [Theory]
    [InlineData(240, 6)] // FNV NAM0: 10 × 24
    [InlineData(160, 4)] // FO3 NAM0: 10 × 16
    public void DetectWeatherBands_ReadsBandCountFromNam0Length(int nam0Payload, int expectedBands)
    {
        var record = BuildSubrecord("NAM0", nam0Payload);
        Assert.Equal(expectedBands, MiscEnvironmentHandler.DetectWeatherBands(record, record.Length, false));
    }

    [Fact]
    public void DetectWeatherBands_NoNam0_DefaultsToFnvSixBands()
    {
        var record = BuildSubrecord("FNAM", 24);
        Assert.Equal(6, MiscEnvironmentHandler.DetectWeatherBands(record, record.Length, false));
    }

    // --- Modern (Skyrim/FO4/FO76/SF1) NAM0 layout -------------------------------------------------------
    // These games use a form-versioned wbWeatherTimeOfDay per category: 4 RGBA bands (16B) base, widening
    // to 8 (32B) for FO4/FO76/SF1 at form version 111. Category COUNT is form-version dependent (10→19),
    // so the stride is taken as given rather than derived from NAM0's length.

    [Theory]
    [InlineData(BethesdaGame.Fallout4, 131, 32)] // FO4 retail: 8 bands
    [InlineData(BethesdaGame.Fallout4, 110, 16)] // FO4 pre-111: 4 bands
    [InlineData(BethesdaGame.Fallout76, 120, 32)]
    [InlineData(BethesdaGame.Starfield, 150, 32)]
    [InlineData(BethesdaGame.Skyrim, 43, 16)] // Skyrim never widens — 4 bands regardless of version
    [InlineData(BethesdaGame.Skyrim, 200, 16)]
    public void ModernWeatherStride_WidensOnlyForFo4PlusAtVersion111(BethesdaGame game, int formVersion,
        int expectedStride)
    {
        Assert.Equal(expectedStride, MiscEnvironmentHandler.ModernWeatherStride(game, formVersion));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadWeatherColorsModern_EightBandStride_PreservesAllTransitionBands(bool bigEndian)
    {
        // FO4 v111+: 32-byte stride in semantic order: four base bands, then Early/Late
        // Sunrise and Early/Late Sunset.
        var colors = MiscEnvironmentHandler.ReadWeatherColorsModern(
            BuildCategoryBuffer(2, 8, bigEndian), bigEndian, 32);

        Assert.Equal(2, colors.Count);
        Assert.Equal(new WeatherRgba(1, 2, 3, 4), colors[0].Sunrise);
        Assert.Equal(new WeatherRgba(5, 6, 7, 8), colors[0].Day);
        Assert.Equal(new WeatherRgba(9, 10, 11, 12), colors[0].Sunset);
        Assert.Equal(new WeatherRgba(13, 14, 15, 16), colors[0].Night);
        Assert.Equal(new WeatherRgba(17, 18, 19, 20), colors[0].EarlySunrise!.Value);
        Assert.Equal(new WeatherRgba(21, 22, 23, 24), colors[0].LateSunrise!.Value);
        Assert.Equal(new WeatherRgba(25, 26, 27, 28), colors[0].EarlySunset!.Value);
        Assert.Equal(new WeatherRgba(29, 30, 31, 32), colors[0].LateSunset!.Value);
        Assert.Equal(colors[0].Day, colors[0].HighNoon);
        Assert.Equal(colors[0].Night, colors[0].Midnight);

        // Category 1 must START at byte 32 (32-byte stride), not 16 — this is the stride guard that a
        // regressed 16-byte read (the FO4 "red sky" bug) would fail by landing on category 0's Early
        // Sunrise band (17,18,19,20).
        Assert.Equal(new WeatherRgba(33, 34, 35, 36), colors[1].Sunrise);
        Assert.NotEqual(new WeatherRgba(17, 18, 19, 20), colors[1].Sunrise);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadWeatherColorsModern_FourBandStride_Uses16ByteStride(bool bigEndian)
    {
        // FO4 pre-111 / Skyrim: 16-byte stride (4 bands).
        var colors = MiscEnvironmentHandler.ReadWeatherColorsModern(
            BuildCategoryBuffer(2, 4, bigEndian), bigEndian, 16);

        Assert.Equal(2, colors.Count);
        Assert.Equal(new WeatherRgba(1, 2, 3, 4), colors[0].Sunrise);
        Assert.Equal(new WeatherRgba(13, 14, 15, 16), colors[0].Night);
        Assert.Equal(colors[0].Day, colors[0].HighNoon);
        Assert.Equal(new WeatherRgba(17, 18, 19, 20), colors[1].Sunrise);
    }

    [Fact]
    public void ReadWeatherColorsModern_Fo4RetailNam0_Decodes19Categories()
    {
        // FO4 retail NAM0 is 608 bytes = 19 categories × 32 bytes (verified against Fallout4.esm
        // 0x0024A3C1). The widened tail includes the authored Sun Glare and Moon Glare rows.
        var colors = MiscEnvironmentHandler.ReadWeatherColorsModern(new byte[608], false, 32);
        Assert.Equal(19, colors.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadWeatherColorsModern_PreservesFo4SunAndMoonGlareRows(bool bigEndian)
    {
        var data = new byte[17 * 32];

        var sunGlareDay = new WeatherRgba(11, 22, 33, 44);
        var moonGlareNight = new WeatherRgba(55, 66, 77, 88);
        WriteRgba(data, (int)WeatherColorType.SunGlare * 32 + 4, sunGlareDay, bigEndian);
        WriteRgba(data, (int)WeatherColorType.MoonGlare * 32 + 12, moonGlareNight, bigEndian);

        var colors = MiscEnvironmentHandler.ReadWeatherColorsModern(data, bigEndian, 32);

        Assert.Equal(sunGlareDay, colors[(int)WeatherColorType.SunGlare].Day);
        Assert.Equal(moonGlareNight, colors[(int)WeatherColorType.MoonGlare].Night);
    }

    // --- JNAM "Cloud Alphas" (Skyrim/FO4/FO76/SF1 per-layer cloud opacity) -------------------------------
    // The engine's real per-layer cloud OPACITY lives in JNAM, NOT the PNAM color's alpha byte (which these
    // records author as 0). Each layer is a wbWeatherCloudAlphas struct of FOUR opacity floats (Sunrise/Day/
    // Sunset/Night), widening to 8 floats for FO4/FO76/SF1 at form version 111 like the colors. Verified
    // against FalloutNV (no JNAM → flat opacity), SkyrimCloudy (all 1.0 → heavy overcast) and FO4
    // CommonwealthClear (layers 0.0/0.2/0.4/0.75 → mostly-sky clear).

    // Builds N layers of cloud-alpha floats at the given stride; layer L band B float = L*10 + B + 1 so
    // every band is trivially identifiable and the stride is testable.
    private static byte[] BuildCloudAlphaBuffer(int layers, int floatsPerLayer, bool bigEndian = false)
    {
        var data = new byte[layers * floatsPerLayer * 4];
        for (var L = 0; L < layers; L++)
        {
            for (var b = 0; b < floatsPerLayer; b++)
            {
                WriteSingle(data, (L * floatsPerLayer + b) * 4, L * 10 + b + 1, bigEndian);
            }
        }

        return data;
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout4, 131, 32)] // FO4 retail: 8 floats
    [InlineData(BethesdaGame.Fallout4, 110, 16)] // FO4 pre-111: 4 floats
    [InlineData(BethesdaGame.Fallout76, 120, 32)]
    [InlineData(BethesdaGame.Starfield, 150, 32)]
    [InlineData(BethesdaGame.Skyrim, 43, 16)] // Skyrim never widens
    public void ModernCloudAlphaStride_WidensOnlyForFo4PlusAtVersion111(BethesdaGame game, int formVersion,
        int expectedStride)
    {
        Assert.Equal(expectedStride, MiscEnvironmentHandler.ModernCloudAlphaStride(game, formVersion));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCloudAlphas_FourFloatStride_ReadsAllBands(bool bigEndian)
    {
        // Skyrim / FO4 pre-111: 16-byte stride (4 floats).
        var alphas = MiscEnvironmentHandler.ReadCloudAlphas(
            BuildCloudAlphaBuffer(2, 4, bigEndian), bigEndian, 16);

        Assert.Equal(2, alphas.Count);
        Assert.Equal(1f, alphas[0].Sunrise);
        Assert.Equal(2f, alphas[0].Day);
        Assert.Equal(3f, alphas[0].Sunset);
        Assert.Equal(4f, alphas[0].Night);
        // Layer 1 starts at byte 16 (L*10 + b + 1 → 11,12,13,14).
        Assert.Equal(11f, alphas[1].Sunrise);
        Assert.Equal(12f, alphas[1].Day);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCloudAlphas_EightFloatStride_PreservesAllTransitionBands(bool bigEndian)
    {
        // FO4 v111+: 32-byte stride (8 floats), retaining each transition. This is also the stride guard — a regressed
        // 16-byte read would land layer 1 on layer 0's Early-Sunrise float (5) instead of byte 32 (11).
        var alphas = MiscEnvironmentHandler.ReadCloudAlphas(
            BuildCloudAlphaBuffer(2, 8, bigEndian), bigEndian, 32);

        Assert.Equal(2, alphas.Count);
        Assert.Equal(1f, alphas[0].Sunrise);
        Assert.Equal(2f, alphas[0].Day);
        Assert.Equal(3f, alphas[0].Sunset);
        Assert.Equal(4f, alphas[0].Night);
        Assert.Equal(5f, alphas[0].EarlySunrise!.Value);
        Assert.Equal(6f, alphas[0].LateSunrise!.Value);
        Assert.Equal(7f, alphas[0].EarlySunset!.Value);
        Assert.Equal(8f, alphas[0].LateSunset!.Value);
        Assert.Equal(11f, alphas[1].Sunrise);
        Assert.NotEqual(5f, alphas[1].Sunrise);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadWeatherHdr_PreservesAllFourteenFloats(bool bigEndian)
    {
        var data = new byte[56];
        for (var i = 0; i < 14; i++)
        {
            var bytes = BitConverter.GetBytes(i + 0.25f);
            if (bigEndian) Array.Reverse(bytes);
            bytes.CopyTo(data, i * 4);
        }

        var hdr = MiscEnvironmentHandler.ReadWeatherHdr(data, bigEndian);
        Assert.Equal(0.25f, hdr.EyeAdaptSpeed);
        Assert.Equal(2.25f, hdr.BlurPasses);
        Assert.Equal(7.25f, hdr.BrightClamp);
        Assert.Equal(13.25f, hdr.TreeDimmer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadWeatherImageSpaces_PreservesEightSemanticFormIds(bool bigEndian)
    {
        var data = new byte[32];
        for (var i = 0; i < 8; i++)
        {
            var bytes = BitConverter.GetBytes((uint)(0x01020300 + i));
            if (bigEndian) Array.Reverse(bytes);
            bytes.CopyTo(data, i * 4);
        }

        var bands = MiscEnvironmentHandler.ReadWeatherImageSpaces(data, bigEndian, 8);
        Assert.Equal(0x01020300u, bands.Sunrise);
        Assert.Equal(0x01020304u, bands.EarlySunrise!.Value);
        Assert.Equal(0x01020305u, bands.LateSunrise!.Value);
        Assert.Equal(0x01020306u, bands.EarlySunset!.Value);
        Assert.Equal(0x01020307u, bands.LateSunset!.Value);
    }

    [Fact]
    public void ReadCloudAlphas_BigEndian_ReadsSwappedFloats()
    {
        // Xbox 360 records are big-endian; the float read must honor the record endianness so Xbox and PC
        // produce the same opacities. Encode one layer's Day = 1.0 in big-endian and read it back.
        var data = new byte[16];
        var dayBe = BitConverter.GetBytes(1.0f);
        Array.Reverse(dayBe);
        dayBe.CopyTo(data, 4); // Day is the second float (offset 4)

        var alphas = MiscEnvironmentHandler.ReadCloudAlphas(data, true, 16);
        Assert.Single(alphas);
        Assert.Equal(1.0f, alphas[0].Day);
    }
}