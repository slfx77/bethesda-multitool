using System.Buffers.Binary;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using DDXConv;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTextureSourceTests;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The DDX gate (design section 5.1; plan section 4): the pure predicate check by check, BMT's own DDX header reader,
///     the fabricated-level trim, and the outcomes through the real reader and the real DDXConv on synthetic DDX files
///     (<see cref="SyntheticDdxFiles" />, byte-identical to DDXConv's own test builder): a pass is a standard payload, a
///     failure a <c>bmt.ddx-recovery</c> derivation, a relayout that throws leaves the original alone.
/// </summary>
public class NifDdxGateTests
{
    private const int Size = 64;

    /// <summary>The passing baseline passes; each single broken check fails the gate alone.</summary>
    [Theory]
    [InlineData("baseline")]
    [InlineData("lossless")]
    [InlineData("truncated")]
    [InlineData("padded")]
    [InlineData("droppedTrailing")]
    [InlineData("fullAtlas")]
    [InlineData("filledTail")]
    [InlineData("format43")]
    [InlineData("format86")]
    [InlineData("inspection")]
    [InlineData("width")]
    [InlineData("mips")]
    [InlineData("declaredMips")]
    [InlineData("missingLevel")]
    public void Gate_PassesOnlyWhenEveryCheckHolds(string broken)
    {
        var header = Header(broken switch
        {
            "format43" => (byte)0x43,
            "format86" => (byte)0x86,
            _ => SyntheticDdxFiles.Dxt1
        }, broken == "declaredMips" ? 3 : 0);
        var counters = broken switch
        {
            "lossless" => NifDdxCounters.Clean with { IsLossless = false, UnwrittenDestinationBlocks = 3 },
            "truncated" => NifDdxCounters.Clean with { TruncatedReads = 1 },
            "padded" => NifDdxCounters.Clean with { PaddedBytes = 16 },
            "droppedTrailing" => NifDdxCounters.Clean with { DroppedTrailingDataBlocks = 2 },
            "fullAtlas" => NifDdxCounters.Clean with { FullAtlasFallbacks = 1 },
            "filledTail" => NifDdxCounters.Clean with { FilledMipTailBlocks = 1 },
            _ => NifDdxCounters.Clean
        };
        var output = new NifDdxOutputFacts(broken == "width" ? 32 : Size, Size, broken == "mips" ? 2 : 1, 0, 0,
            broken != "inspection", null, broken == "missingLevel" ? 1 : 0);

        var result = NifDdxGate.Evaluate(header, counters, output);

        if (broken == "baseline")
        {
            Assert.True(result.Passed);
            Assert.Empty(result.Failures);
            Assert.Null(result.Reason);
        }
        else
        {
            Assert.False(result.Passed);
            Assert.Single(result.Failures);
        }
    }

    /// <summary>
    ///     The gate reads every counter DDXConv records, separately from IsLossless. Control: a fresh diagnostics object
    ///     passes the same header and output.
    /// </summary>
    [Fact]
    public void Counters_FromDecodeDiagnostics_FeedEveryCheck()
    {
        var diagnostics = new DecodeDiagnostics();
        diagnostics.RecordPadding(8);
        diagnostics.RecordTruncatedRead("synthetic");
        diagnostics.RecordSurfaceCoverage([0, 1, 2]);
        diagnostics.RecordDroppedTrailing([0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0], 0, 8);
        diagnostics.RecordFullAtlasFallback();
        diagnostics.RecordFilledMipTailBlock();
        var output = new NifDdxOutputFacts(Size, Size, 1, 0, 0, true, null);

        var counters = NifDdxCounters.From(diagnostics);
        var failed = NifDdxGate.Evaluate(Header(SyntheticDdxFiles.Dxt1, 0), counters, output);
        var clean = NifDdxGate.Evaluate(Header(SyntheticDdxFiles.Dxt1, 0),
            NifDdxCounters.From(new DecodeDiagnostics()), output);

        Assert.False(counters.IsLossless);
        Assert.Equal(1, counters.UnwrittenDestinationBlocks);
        Assert.Equal(1, counters.DuplicateDestinationWrites);
        Assert.Equal(8, counters.PaddedBytes);
        Assert.Equal(1, counters.TruncatedReads);
        Assert.Equal(1, counters.DroppedTrailingDataBlocks);
        Assert.Equal(6, failed.Failures.Count);
        Assert.True(clean.Passed);
    }

    /// <summary>
    ///     BMT's header reader: magic, version, format byte (0x2B, else 0x24), dimensions, declared mips from mip-max.
    ///     Controls: a zero 0x2B falls back to 0x24; a foreign magic and a short buffer are refused.
    /// </summary>
    [Fact]
    public void Header_ReadsTheFieldsTheGateNeeds()
    {
        var bytes = SyntheticDdxFiles.Build("3XDO", 128, 32, SyntheticDdxFiles.Dxt5, [], 5);

        Assert.True(NifDdxHeader.TryRead(bytes, out var header, out _));
        Assert.Equal("3XDO", header.Magic);
        Assert.False(header.IsThreeXdr);
        Assert.Equal(3, header.Version);
        Assert.Equal(128, header.Width);
        Assert.Equal(32, header.Height);
        Assert.Equal(0x54, header.FormatByte);
        Assert.Equal(6, header.DeclaredMipCount);

        bytes[0x24] = 0x82;
        bytes[0x2B] = 0x86;
        Assert.True(NifDdxHeader.TryRead(bytes, out var strip, out _));
        Assert.Equal(0x86, strip.FormatByte);
        Assert.False(NifDdxGate.Evaluate(strip, NifDdxCounters.Clean,
            new NifDdxOutputFacts(128, 32, 6, 0, 0, true, null)).Passed);

        bytes[0x24] = 0x80;
        bytes[0x2B] = 0;
        Assert.True(NifDdxHeader.TryRead(bytes, out var fallback, out _));
        Assert.Equal(0x80, fallback.FormatByte);

        bytes[0] = (byte)'X';
        Assert.False(NifDdxHeader.TryRead(bytes, out _, out var magic));
        Assert.Contains("magic", magic, StringComparison.Ordinal);
        Assert.False(NifDdxHeader.TryRead(bytes.AsSpan(0, 0x40), out _, out _));
    }

    /// <summary>
    ///     The header reader on the first 0x44 bytes of two retail files (FNV Xbox 360 final, <c>Fallout - Textures.bsa</c>,
    ///     <c>textures\clutter\junk\tincan01.ddx</c> and <c>tincan01_n.ddx</c>), independent of <see cref="SyntheticDdxFiles" />,
    ///     which writes the same assumptions the reader makes. Byte 0x24 is 0x81 in both while the format is at 0x2B (DXT1
    ///     0x52, ATI2 0x71), so a reader of 0x24 fails; mip-max (dword 4 bits 6-9) gives each file's full chain (8 for
    ///     128x128, 7 for 64x64), while mip-min (bits 2-5) reads 0, so a reader of the wrong field fails.
    /// </summary>
    [Theory]
    [InlineData("3358444F040000040000000300000001000000000000000000000000FFFF0000FFFF" +
                "00008100000200000052000FE07F00000D10000001C000002A0000008000000009D0", "3XDO", 0x52, 128, 128, 8)]
    [InlineData("33584452040000040000000300000001000000000000000000000000FFFF0000FFFF" +
                "000081000002000000710007E03F00000D100000018000004A000000C00000000EF2", "3XDR", 0x71, 64, 64, 7)]
    public void Header_RetailFiles_ReadTheFetchConstant(string hex, string magic, int format, int width, int height,
        int declaredMips)
    {
        var bytes = Convert.FromHexString(hex);

        Assert.Equal(NifDdxHeader.Size, bytes.Length);
        Assert.True(NifDdxHeader.TryRead(bytes, out var header, out _));
        Assert.Equal(magic, header.Magic);
        Assert.Equal(0x81, header.DataFormatByte);
        Assert.Equal(format, header.FormatByte);
        Assert.Equal(width, header.Width);
        Assert.Equal(height, header.Height);
        Assert.Equal(0, header.MipMin);
        Assert.Equal(declaredMips, header.DeclaredMipCount);
    }

    /// <summary>
    ///     The trim removes only all-zero levels beyond the declared count and rewrites the mip count. Control: a
    ///     non-zero undeclared level is never trimmed and is counted.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MipTrim_RemovesOnlyAllZeroUndeclaredLevels(bool dataInLevelTwo)
    {
        // 8x8 DXT1, four levels: 32 + 8 + 8 + 8 bytes after the 128-byte header.
        var levels = new byte[56];
        levels.AsSpan(0, 32).Fill(0x5A);
        if (dataInLevelTwo)
        {
            levels[40] = 1;
        }

        var dds = SyntheticDds.FourCc("DXT1", 8, 8, levels, 4);

        var trim = NifDdsMipTrim.Trim(dds, 1);

        if (dataInLevelTwo)
        {
            Assert.Same(dds, trim.Bytes);
            Assert.Equal(4, trim.Levels);
            Assert.Equal(1, trim.UndeclaredNonZeroLevels);
        }
        else
        {
            Assert.Equal(1, trim.Levels);
            Assert.Equal(3, trim.TrimmedLevels);
            Assert.Equal(24, trim.TrimmedBytes);
            Assert.Equal(128 + 32, trim.Bytes.Length);
            Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(trim.Bytes.AsSpan(28)));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(trim.Bytes.AsSpan(8)) & 0x20000u);
            Assert.Equal(dds.AsSpan(128, 32).ToArray(), trim.Bytes.AsSpan(128).ToArray());
        }
    }

    /// <summary>
    ///     A complete 64x64 DXT1 3XDR declaring one level relayouts cleanly: the original DDX stays, the DDS is its
    ///     standard payload with the relayout note and counter evidence, and there is no derivation.
    /// </summary>
    [Fact]
    public void PassingDdx_BecomesAStandardPayload()
    {
        var ddx = Ddx(SyntheticDdxFiles.Dxt1, SyntheticDdxFiles.IndexStampedBlocks(256, 8));
        var document = ReadTextures(PerPixel(0, @"textures\t\a.dds"), (@"textures\t\a.ddx", ddx)).Document;
        var source = Assert.Single(document.Images).Source!;

        Assert.Equal("ddx", source.Container);
        Assert.Equal(ddx, source.Original!.CopyContent());
        var standard = Assert.IsType<SceneStandardImagePayload>(source.StandardPayload);
        Assert.Equal("dds", standard.Payload.Container);
        Assert.Equal(NifModelTextureSource.RelayoutNote, standard.Note);
        Assert.Contains("DDXConv", standard.Evidence, StringComparison.Ordinal);
        Assert.Equal(Size, BinaryPrimitives.ReadInt32LittleEndian(standard.Payload.Content[16..]));
        Assert.Equal(Size, standard.Descriptor.Width);
        Assert.Null(source.Derivation);
        Assert.Equal("Xenos", source.Descriptor.PixelFormatNamespace);
        Assert.Equal("0x52", source.Descriptor.PixelFormatCode);
        Assert.Equal("xenos-tiled-3xdr", source.Descriptor.Layout);
        Assert.Equal(SceneTextureMipPresence.Authored, source.Descriptor.MipLevels[0].Presence);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     Gate failures become a recovery derivation (original kept, no standard payload): a 3XDR declaring a mip chain,
    ///     one whose dropped tail holds data, a format DDXConv does not map exactly (0x43), and a truncated 3XDO. Control:
    ///     <see cref="PassingDdx_BecomesAStandardPayload" />, identical but for the one broken property.
    /// </summary>
    [Theory]
    [InlineData("declaredMips", "declares 7")]
    [InlineData("storedTail", "dropped after mip 0")]
    [InlineData("format43", "0x43")]
    [InlineData("truncated", "truncated read")]
    public void FailingDdx_BecomesARecoveryDerivation(string broken, string reason)
    {
        var mip0 = SyntheticDdxFiles.IndexStampedBlocks(256, 8);
        var ddx = broken switch
        {
            "declaredMips" => Ddx(SyntheticDdxFiles.Dxt1, mip0, 6),
            "storedTail" => Ddx(SyntheticDdxFiles.Dxt1, [.. mip0, .. StampedTail()]),
            "format43" => Ddx(0x43, SyntheticDdxFiles.IndexStampedBlocks(256, 16)),
            _ => [.. SyntheticDdxFiles.Build("3XDO", 128, 128, SyntheticDdxFiles.Dxt1,
                SyntheticDdxFiles.IndexStampedBlocks(65536 / 8, 8)), 0xAB]
        };
        var document = ReadTextures(PerPixel(0, @"textures\t\a.dds"), (@"textures\t\a.ddx", ddx)).Document;
        var source = Assert.Single(document.Images).Source!;

        Assert.Equal(ddx, source.Original!.CopyContent());
        Assert.Null(source.StandardPayload);
        var derivation = Assert.IsType<SceneImageDerivation>(source.Derivation);
        Assert.Equal(NifModelTextureSource.DdxRecoveryRecipe, derivation.Recipe);
        Assert.Empty(derivation.InputImageIndices);
        Assert.Equal("dds", derivation.Payload!.Container);
        Assert.NotNull(derivation.Descriptor);
        Assert.Contains(reason, derivation.Reason, StringComparison.Ordinal);
        var details = JsonNode.Parse(derivation.DetailsJson!)!;
        Assert.NotNull(details["counters"]);
        if (broken == "storedTail")
        {
            Assert.Equal(32, (int)details["counters"]!["droppedTrailingDataBlocks"]!);
        }

        Assert.Contains(document.Diagnostics, d => d.Code == NifModelTextureSource.DdxGateDiagnostic);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     DDXConv throwing (version 2 is refused) leaves the original only, with a diagnostic; no standard payload and no
    ///     derivation is invented.
    /// </summary>
    [Fact]
    public void RelayoutThatThrows_KeepsTheOriginalOnly()
    {
        var ddx = SyntheticDdxFiles.Build("3XDR", Size, Size, SyntheticDdxFiles.Dxt1,
            SyntheticDdxFiles.IndexStampedBlocks(256, 8), version: 2);
        var document = ReadTextures(PerPixel(0, @"textures\t\a.dds"), (@"textures\t\a.ddx", ddx)).Document;
        var source = Assert.Single(document.Images).Source!;

        Assert.NotNull(source.Original);
        Assert.Null(source.StandardPayload);
        Assert.Null(source.Derivation);
        Assert.Contains(document.Diagnostics, d => d.Code == NifModelTextureSource.DdxRelayoutDiagnostic);
        SceneValidation.ValidateStructure(document);
    }

    /// <summary>A 64x64 3XDR with the given format, payload and mip-max.</summary>
    private static byte[] Ddx(byte format, byte[] payload, int mipMax = 0)
    {
        return SyntheticDdxFiles.Build("3XDR", Size, Size, format, payload, mipMax);
    }

    /// <summary>768 bytes after mip 0 whose first 32 eight-byte blocks are non-zero (DDXConv's own tail case).</summary>
    private static byte[] StampedTail()
    {
        var tail = new byte[768];
        for (var block = 0; block < 32; block++)
        {
            tail[block * 8] = (byte)(block + 1);
        }

        return tail;
    }

    private static NifDdxHeader Header(byte format, int mipMax)
    {
        Assert.True(NifDdxHeader.TryRead(SyntheticDdxFiles.Build("3XDR", Size, Size, format, [], mipMax),
            out var header, out _));
        return header;
    }
}
