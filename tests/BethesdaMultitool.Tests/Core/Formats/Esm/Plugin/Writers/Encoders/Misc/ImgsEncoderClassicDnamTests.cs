using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Parity;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Pins the FO3/FNV packed IMGS contract independently from the legacy split encoder path.
///     Every semantic float has a unique sentinel so an offset swap cannot pass as a round trip.
/// </summary>
public sealed class ImgsEncoderClassicDnamTests
{
    private static readonly uint[] Retail132PostBodyWords = [0x07u];
    private static readonly uint[] RcDefault148PostBodyWords = [0x0Fu, 0u, 0u, 0u, 0x04u];

    [Theory]
    [InlineData(132, false)]
    [InlineData(132, true)]
    [InlineData(148, false)]
    [InlineData(148, true)]
    [InlineData(152, false)]
    [InlineData(152, true)]
    public void ClassicDnam_ParseAndCanonicalEncode_RoundTripsEverySemanticField(
        int sourceLength, bool sourceBigEndian)
    {
        var source = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(
            BuildClassicDnam(sourceLength, sourceBigEndian), sourceBigEndian);

        Assert.Equal(LayoutFor(sourceLength), source.SourceLayout);
        Assert.Equal(ExpectedHdr(sourceLength == 152), source.Hdr);
        Assert.Equal(new ImageSpaceClassicBloom(101f, 102f, 103f), source.Bloom);
        Assert.Equal(new ImageSpaceClassicGetHit(104f, 105f, 106f), source.GetHit);
        Assert.Equal(new ImageSpaceClassicNightEye(107f, 108f, 109f, 110f), source.NightEye);
        Assert.Equal(ExpectedCinematic(sourceLength > 132), source.Cinematic);
        Assert.Equal(ExpectedTint(), source.Tint);
        Assert.Equal(ExpectedPostBodyWords(sourceLength), source.PostBodyWords);
        Assert.Equal(0x02u, source.PostBodyWords[0] & 0x0F);
        if (sourceLength > 132) Assert.Equal(0x0Cu, source.PostBodyWords[4] & 0x0F);

        var encoded = ImgsEncoder.EncodeClassicDnam(MakeClassicRecord(source));

        Assert.Equal(152, encoded.Length);
        Assert.Equal(source.Hdr.EyeAdaptSpeed,
            BinaryPrimitives.ReadSingleLittleEndian(encoded.AsSpan(0, sizeof(float))));
        Assert.Equal(source.Hdr.SunlightDimmer,
            BinaryPrimitives.ReadSingleLittleEndian(encoded.AsSpan(44, sizeof(float))));
        Assert.Equal(source.Bloom.BlurRadius,
            BinaryPrimitives.ReadSingleLittleEndian(encoded.AsSpan(60, sizeof(float))));
        Assert.Equal(source.NightEye.Brightness,
            BinaryPrimitives.ReadSingleLittleEndian(encoded.AsSpan(96, sizeof(float))));
        Assert.Equal(source.Tint.Amount,
            BinaryPrimitives.ReadSingleLittleEndian(encoded.AsSpan(128, sizeof(float))));
        var expectedCanonicalTail = sourceLength == 132
            ? [.. ExpectedPostBodyTail(sourceLength, false), .. new byte[16]]
            : ExpectedPostBodyTail(sourceLength, false);
        Assert.Equal(expectedCanonicalTail, encoded.AsSpan(132, 20).ToArray());

        var roundTrip = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(encoded, false);
        Assert.Equal(ImageSpaceClassicDnamLayout.Dnam152, roundTrip.SourceLayout);
        Assert.Equal(source.Hdr, roundTrip.Hdr);
        Assert.Equal(source.Bloom, roundTrip.Bloom);
        Assert.Equal(source.GetHit, roundTrip.GetHit);
        Assert.Equal(source.NightEye, roundTrip.NightEye);
        Assert.Equal(
            source.Cinematic with
            {
                HasExplicitFlags = true,
                Flags = sourceLength == 132 ? ImageSpaceCinematicFlags.None : source.Cinematic.Flags
            },
            roundTrip.Cinematic);
        Assert.Equal(source.Tint, roundTrip.Tint);
        var expectedCanonicalWords = sourceLength == 132
            ? [.. ExpectedPostBodyWords(sourceLength), 0, 0, 0, 0]
            : ExpectedPostBodyWords(sourceLength);
        Assert.Equal(expectedCanonicalWords, roundTrip.PostBodyWords);
    }

    [Theory]
    [InlineData(148, false)]
    [InlineData(148, true)]
    [InlineData(152, false)]
    [InlineData(152, true)]
    public void ClassicDnam_EncoderUpdatesOnlyExplicitFlagLowNibble(
        int sourceLength, bool sourceBigEndian)
    {
        var source = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(
            BuildClassicDnam(sourceLength, sourceBigEndian), sourceBigEndian);
        var changedFlags = ImageSpaceCinematicFlags.Saturation | ImageSpaceCinematicFlags.Tint;
        var changed = MakeClassicRecord(source) with
        {
            Cinematic = source.Cinematic with { Flags = changedFlags }
        };
        var expectedWords = ExpectedPostBodyWords(sourceLength);
        var originalFlagsLane = expectedWords[4];
        var expectedFlagsLane = (originalFlagsLane & 0xFFFF_FFF0u) | (uint)changedFlags;
        expectedWords[4] = expectedFlagsLane;
        var expectedTail = EncodePostBodyWords(expectedWords, false);

        var encoded = ImgsEncoder.EncodeClassicDnam(changed);

        Assert.Equal(expectedTail, encoded.AsSpan(132, 20).ToArray());
        var encodedFlagsLane = BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(148, sizeof(uint)));
        Assert.Equal(0x02u,
            BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(132, sizeof(uint))) & 0x0F);
        Assert.Equal(originalFlagsLane & 0xFFFF_FFF0u, encodedFlagsLane & 0xFFFF_FFF0u);
        Assert.Equal((uint)changedFlags, encodedFlagsLane & 0x0F);
    }

    [Fact]
    public void ClassicDnam_TopsPairedPcAndXboxTail_NormalizesToSameWordsAndOutput()
    {
        var pcBytes = BuildClassicDnam(152, false);
        var xboxBytes = BuildClassicDnam(152, true);
        Convert.FromHexString("0F000000E80D000052000000E0A4E31176F24B00").CopyTo(pcBytes, 132);
        Convert.FromHexString("0000000F00000DE80000005211E3A4E0004BF276").CopyTo(xboxBytes, 132);
        uint[] expectedWords = [0x0F, 0xDE8, 0x52, 0x11E3_A4E0, 0x004B_F276];

        var pc = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(pcBytes, false);
        var xbox = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(xboxBytes, true);

        Assert.Equal(expectedWords, pc.PostBodyWords);
        Assert.Equal(expectedWords, xbox.PostBodyWords);
        Assert.Equal(ImageSpaceCinematicFlags.Contrast | ImageSpaceCinematicFlags.Tint, pc.Cinematic.Flags);
        Assert.Equal(pc.Cinematic.Flags, xbox.Cinematic.Flags);
        Assert.Equal(
            ImgsEncoder.EncodeClassicDnam(MakeClassicRecord(pc)),
            ImgsEncoder.EncodeClassicDnam(MakeClassicRecord(xbox)));
    }

    [Fact]
    public void ClassicDnam_Retail132Word_CanonicalizesMissingWordsToZero()
    {
        var pcBytes = BuildClassicDnam(132, false);
        Convert.FromHexString("07000000").CopyTo(pcBytes, 128);
        var source = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(pcBytes, false);

        var encoded = ImgsEncoder.EncodeClassicDnam(MakeClassicRecord(source));

        Assert.Equal(Retail132PostBodyWords, source.PostBodyWords);
        Assert.False(source.Cinematic.HasExplicitFlags);
        Assert.Equal(0x07u, BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(132, sizeof(uint))));
        Assert.All(encoded.AsSpan(136, 16).ToArray(), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void ClassicDnam_RcDefaultRetail148Tail_ShiftsTerminalFlagsToCanonicalOffset()
    {
        var pcBytes = BuildClassicDnam(148, false);
        Convert.FromHexString("0F00000000000000000000000000000004000000").CopyTo(pcBytes, 128);

        var source = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(pcBytes, false);
        var encoded = ImgsEncoder.EncodeClassicDnam(MakeClassicRecord(source));

        Assert.Equal(RcDefault148PostBodyWords, source.PostBodyWords);
        Assert.True(source.Cinematic.HasExplicitFlags);
        Assert.Equal(ImageSpaceCinematicFlags.Tint, source.Cinematic.Flags);
        Assert.Equal(0x0Fu,
            BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(132, sizeof(uint))));
        Assert.Equal(0x04u,
            BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(148, sizeof(uint))));
    }

    [Fact]
    public void ClassicDnam_EncoderUsesTopLevelSemanticEditsOverNestedParseSnapshot()
    {
        var classic = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(BuildClassicDnam(152, false), false);
        var editedHdr = classic.Hdr with { SunlightDimmer = 901f };
        var editedCinematic = classic.Cinematic with
        {
            Saturation = 902f,
            Flags = ImageSpaceCinematicFlags.Saturation
        };
        var editedTint = classic.Tint with { Amount = 903f };
        var record = MakeClassicRecord(classic) with
        {
            Hdr = editedHdr,
            Cinematic = editedCinematic,
            Tint = editedTint
        };

        var encoded = ImgsEncoder.EncodeClassicDnam(record);

        Assert.NotEqual(editedHdr, classic.Hdr);
        Assert.NotEqual(editedCinematic, classic.Cinematic);
        Assert.NotEqual(editedTint, classic.Tint);
        Assert.Equal(901f, BinaryPrimitives.ReadSingleLittleEndian(encoded.AsSpan(44, sizeof(float))));
        Assert.Equal(902f, BinaryPrimitives.ReadSingleLittleEndian(encoded.AsSpan(100, sizeof(float))));
        Assert.Equal(903f, BinaryPrimitives.ReadSingleLittleEndian(encoded.AsSpan(128, sizeof(float))));
        Assert.Equal(0xF3E2_D1B1u,
            BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(148, sizeof(uint))));
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3, false)]
    [InlineData(BethesdaGame.Fallout3, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, false)]
    [InlineData(BethesdaGame.FalloutNewVegas, true)]
    public void ParseImageSpaces_ProjectsPackedDnamWithExplicitProvenance(
        BethesdaGame game, bool bigEndian)
    {
        var record = ParseImageSpace(game, bigEndian, 152);

        Assert.NotNull(record.ClassicDnam);
        Assert.Equal(ImageSpaceClassicDnamLayout.Dnam152, record.ClassicDnam.SourceLayout);
        Assert.Same(record.ClassicDnam.Hdr, record.Hdr);
        Assert.Same(record.ClassicDnam.Cinematic, record.Cinematic);
        Assert.Same(record.ClassicDnam.Tint, record.Tint);
        Assert.Equal(new ImageSpaceClassicBloom(101f, 102f, 103f), record.ClassicDnam.Bloom);
        Assert.Equal(new ImageSpaceClassicNightEye(107f, 108f, 109f, 110f), record.ClassicDnam.NightEye);
    }

    [Fact]
    public void EncodeNew_ClassicProvenance_EmitsOneCanonicalDnamAndNoSplitSubrecords()
    {
        var classic = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(
            BuildClassicDnam(132, true), true);
        var record = MakeClassicRecord(classic) with
        {
            // A classic record must not append a second, modern-style DNAM even if compatibility
            // properties were populated by a synthetic caller.
            DepthOfField = [900f, 901f]
        };

        var encoded = ImgsEncoder.EncodeNew(record);

        Assert.Equal(["EDID", "DNAM"], encoded.Subrecords.Select(subrecord => subrecord.Signature));
        var dnam = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "DNAM");
        Assert.Equal(152, dnam.Bytes.Length);
        Assert.DoesNotContain(encoded.Subrecords,
            subrecord => subrecord.Signature is "HNAM" or "CNAM" or "TNAM");
        Assert.Equal(1f,
            BinaryPrimitives.ReadSingleLittleEndian(dnam.Bytes.AsSpan(56, sizeof(float))));
    }

    [Fact]
    public void EncodeNew_LegacySplitWithoutClassicProvenance_PreservesExistingShape()
    {
        var record = new ImageSpaceRecord
        {
            FormId = 0x0100_1234,
            EditorId = "LegacySplit",
            Hdr = ExpectedHdr(false),
            Cinematic = ExpectedCinematic(true),
            Tint = ExpectedTint(),
            DepthOfField = [301f, 302f]
        };

        var encoded = ImgsEncoder.EncodeNew(record);

        Assert.Null(record.ClassicDnam);
        Assert.Equal(["EDID", "HNAM", "CNAM", "TNAM", "DNAM"],
            encoded.Subrecords.Select(subrecord => subrecord.Signature));
        Assert.Equal(8, encoded.Subrecords.Single(subrecord => subrecord.Signature == "DNAM").Bytes.Length);
    }

    [Fact]
    public void DispatcherAndPlannedWriter_KeepClassicDnamCompatibleWithFormVersion15()
    {
        const uint formId = 0x0100_1234;
        var classic = MiscEnvironmentHandler.ReadClassicImageSpaceDnam(
            BuildClassicDnam(148, false), false);
        var record = MakeClassicRecord(classic) with { FormId = formId };

        var dispatched = NewTopLevelRecordEncoderDispatcher.TryEncode(
            "IMGS",
            record,
            new NewTopLevelRecordEncodingContext(
                new HashSet<uint>(),
                new HashSet<uint>(),
                new Dictionary<uint, uint>()));

        Assert.NotNull(dispatched);
        Assert.Equal(["EDID", "DNAM"], dispatched.Subrecords.Select(subrecord => subrecord.Signature));
        Assert.Equal(152, dispatched.Subrecords.Single(subrecord => subrecord.Signature == "DNAM").Bytes.Length);

        var recordBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
            "IMGS", formId, 0, dispatched.Subrecords);
        Assert.Equal(Tes4HeaderBuilder.RecordVersion,
            BinaryPrimitives.ReadUInt16LittleEndian(recordBytes.AsSpan(20, sizeof(ushort))));

        PlannerTier1ParityHelper.AssertNewRecordParity("IMGS", formId, record, dispatched);
    }

    private static ImageSpaceRecord MakeClassicRecord(ImageSpaceClassicData classic)
    {
        return new ImageSpaceRecord
        {
            FormId = 0x0100_1234,
            EditorId = "ClassicPacked",
            ClassicDnam = classic,
            Hdr = classic.Hdr,
            Cinematic = classic.Cinematic,
            Tint = classic.Tint
        };
    }

    private static ImageSpaceRecord ParseImageSpace(BethesdaGame game, bool bigEndian, int dnamLength)
    {
        var subrecords = BuildSubrecords(bigEndian,
            ("EDID", Encoding.ASCII.GetBytes("ClassicPacked\0")),
            ("DNAM", BuildClassicDnam(dnamLength, bigEndian)));
        const int headerSize = 24;
        var file = new byte[headerSize + subrecords.Length];
        var formVersion = dnamLength switch
        {
            132 => (ushort)9,
            148 => (ushort)13,
            152 => (ushort)15,
            _ => throw new ArgumentOutOfRangeException(nameof(dnamLength))
        };
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(20, sizeof(ushort)), formVersion);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(20, sizeof(ushort)), formVersion);
        subrecords.CopyTo(file, headerSize);

        var detected = new DetectedMainRecord(
            "IMGS", (uint)subrecords.Length, 0, 0x0100_1234, 0, bigEndian)
        {
            HeaderSize = headerSize
        };
        var scan = new EsmRecordScanResult
        {
            Game = game,
            MainRecords = [detected]
        };
        var context = new RecordParserContext(
            scan,
            null,
            new ByteArrayMemoryAccessor(file),
            file.Length,
            null);

        return Assert.Single(new MiscEnvironmentHandler(context).ParseImageSpaces());
    }

    private static byte[] BuildClassicDnam(int length, bool bigEndian)
    {
        if (length is not (132 or 148 or 152))
            throw new ArgumentOutOfRangeException(nameof(length));

        var bytes = new byte[length];
        var hasSkinDimmer = length == 152;

        for (var index = 0; index < 14; index++)
            WriteSingle(bytes, index * sizeof(float), index + 1f, bigEndian);
        if (hasSkinDimmer) WriteSingle(bytes, 56, 15f, bigEndian);

        var auxiliaryBase = hasSkinDimmer ? 60 : 56;
        for (var index = 0; index < 10; index++)
            WriteSingle(bytes, auxiliaryBase + index * sizeof(float), 101f + index, bigEndian);

        var cinematicBase = auxiliaryBase + 40;
        for (var index = 0; index < 8; index++)
            WriteSingle(bytes, cinematicBase + index * sizeof(float), 201f + index, bigEndian);

        EncodePostBodyWords(ExpectedPostBodyWords(length), bigEndian).CopyTo(bytes, cinematicBase + 32);
        return bytes;
    }

    private static uint[] ExpectedPostBodyWords(int length)
    {
        return length == 132
            ? [0xEFBE_ADE2]
            :
            [
                0xEFBE_ADE2, // Immediate lane low nibble deliberately differs from flags.
                0x4433_2211,
                0x8877_6655,
                0xCCBB_AA99,
                0xF3E2_D1BC // Terminal flags lane low nibble C; upper 28 bits remain opaque.
            ];
    }

    private static byte[] ExpectedPostBodyTail(int length, bool bigEndian)
    {
        return EncodePostBodyWords(ExpectedPostBodyWords(length), bigEndian);
    }

    private static byte[] EncodePostBodyWords(uint[] words, bool bigEndian)
    {
        var tail = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            WriteUInt32(tail, index * sizeof(uint), words[index], bigEndian);
        return tail;
    }

    private static byte[] BuildSubrecords(
        bool bigEndian,
        params (string Signature, byte[] Data)[] subrecords)
    {
        var result = new List<byte>();
        foreach (var (signature, data) in subrecords)
        {
            var signatureBytes = Encoding.ASCII.GetBytes(signature);
            if (bigEndian) Array.Reverse(signatureBytes);
            result.AddRange(signatureBytes);

            var length = new byte[sizeof(ushort)];
            if (bigEndian)
                BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)data.Length));
            else
                BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)data.Length));
            result.AddRange(length);
            result.AddRange(data);
        }

        return [.. result];
    }

    private static ImageSpaceClassicDnamLayout LayoutFor(int length)
    {
        return length switch
        {
            132 => ImageSpaceClassicDnamLayout.Dnam132,
            148 => ImageSpaceClassicDnamLayout.Dnam148,
            152 => ImageSpaceClassicDnamLayout.Dnam152,
            _ => throw new ArgumentOutOfRangeException(nameof(length))
        };
    }

    private static ImageSpaceHdr ExpectedHdr(bool hasSkinDimmer)
    {
        return new ImageSpaceHdr
        {
            EyeAdaptSpeed = 1f,
            BlurRadius = 2f,
            BlurPasses = 3f,
            EmissiveMult = 4f,
            TargetLum = 5f,
            UpperLumClamp = 6f,
            BrightScale = 7f,
            BrightClamp = 8f,
            LumRampNoTex = 9f,
            LumRampMin = 10f,
            LumRampMax = 11f,
            SunlightDimmer = 12f,
            GrassDimmer = 13f,
            TreeDimmer = 14f,
            SkinDimmer = hasSkinDimmer ? 15f : 1f
        };
    }

    private static ImageSpaceCinematic ExpectedCinematic(bool hasExplicitFlags)
    {
        return new ImageSpaceCinematic
        {
            HasExplicitFlags = hasExplicitFlags,
            Flags = hasExplicitFlags
                ? ImageSpaceCinematicFlags.Tint | ImageSpaceCinematicFlags.Brightness
                : ImageSpaceCinematicFlags.None,
            Saturation = 201f,
            ContrastAvgLum = 202f,
            Contrast = 203f,
            Brightness = 204f
        };
    }

    private static ImageSpaceTint ExpectedTint()
    {
        return new ImageSpaceTint
        {
            Red = 205f,
            Green = 206f,
            Blue = 207f,
            Amount = 208f
        };
    }

    private static void WriteSingle(byte[] bytes, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteSingleBigEndian(bytes.AsSpan(offset, sizeof(float)), value);
        else
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, sizeof(float)), value);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, sizeof(uint)), value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)), value);
    }
}