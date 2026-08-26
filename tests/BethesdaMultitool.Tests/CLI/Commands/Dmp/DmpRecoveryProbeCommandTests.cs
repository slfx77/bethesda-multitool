using System.IO.Compression;
using System.Text;
using BethesdaMultitool.CLI.Commands.Dmp;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Dmp;

/// <summary>
///     Synthetic-only coverage for the `dmp recovery-probe` internals: the RFC-1950 candidate
///     filter, trial inflation + content sniffing of a known zlib stream, VA-stitched parsing of a
///     minimal in-memory BSA, and the full <c>ProbeDump</c> gap walk over a synthetic minidump
///     layout (region table + coverage gaps + sparse accessor). No real dumps are touched.
/// </summary>
public sealed class DmpRecoveryProbeCommandTests
{
    // ===== RFC-1950 candidate filter =====

    [Theory]
    [InlineData(0x01, true)] // 0x7801 = 30721 = 31*991
    [InlineData(0x9C, true)] // 0x789C = 30876 = 31*996
    [InlineData(0xDA, true)] // 0x78DA = 30938 = 31*998
    [InlineData(0x00, false)] // fails the %31 FCHECK constraint
    [InlineData(0x5E, false)] // %31 passes but FLG is outside the common {01,9C,DA} set
    [InlineData(0xFF, false)]
    public void IsZlibCandidate_FiltersSecondByte(byte second, bool expected)
    {
        Assert.Equal(expected, DmpRecoveryProbeCommand.IsZlibCandidate(0x78, second));
    }

    [Fact]
    public void IsZlibCandidate_RejectsNonDeflateCmfByte()
    {
        Assert.False(DmpRecoveryProbeCommand.IsZlibCandidate(0x79, 0x9C));
        Assert.False(DmpRecoveryProbeCommand.IsZlibCandidate(0x00, 0x9C));
    }

    // ===== content sniff =====

    [Fact]
    public void SniffContent_RecognizesKnownMagics()
    {
        Assert.Equal("esm-tes4", DmpRecoveryProbeCommand.SniffContent("TES4\0\0\0\0"u8));
        Assert.Equal("esm-grup", DmpRecoveryProbeCommand.SniffContent("GRUP\0\0\0\0"u8));
        Assert.Equal("nif", DmpRecoveryProbeCommand.SniffContent("Gamebryo File Format, Version 20.0.0.4"u8));
        Assert.Equal("nif", DmpRecoveryProbeCommand.SniffContent("NetImmerse File Format, Version 4.0.0.2"u8));
        Assert.Equal("dds", DmpRecoveryProbeCommand.SniffContent("DDS |\0\0\0"u8));
        Assert.Equal("bsa", DmpRecoveryProbeCommand.SniffContent("BSA\0h\0\0\0"u8));
        Assert.Equal("png", DmpRecoveryProbeCommand.SniffContent([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A]));
        Assert.Equal("riff-wave-xma", DmpRecoveryProbeCommand.SniffContent("RIFF\x01\0\0\0WAVEfmt "u8));
        Assert.Equal("empty", DmpRecoveryProbeCommand.SniffContent([]));
    }

    [Fact]
    public void SniffContent_ClassifiesAsciiAndBinary()
    {
        var ascii = Encoding.ASCII.GetBytes("scn SomeScriptName\r\nbegin GameMode\r\nend\r\n");
        Assert.Equal("ascii-text", DmpRecoveryProbeCommand.SniffContent(ascii));

        var binary = new byte[64];
        for (var i = 0; i < binary.Length; i++)
        {
            binary[i] = (byte)(0x80 + (i * 7 % 0x7F));
        }

        Assert.Equal("other", DmpRecoveryProbeCommand.SniffContent(binary));
    }

    // ===== trial inflation =====

    [Fact]
    public void ProbeZlibStream_InflatesSyntheticStreamAndSniffs()
    {
        var payload = BuildEsmShapedPayload(96 * 1024);
        var compressed = ZlibWrap(payload);

        // Trailing junk after the stream must not break inflation of the stream itself.
        var withTail = compressed.Concat(new byte[4096]).ToArray();
        using var input = new MemoryStream(withTail);

        var outcome = DmpRecoveryProbeCommand.ProbeZlibStream(input, 8L * 1024 * 1024);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(payload.Length, outcome.InflatedBytes);
        Assert.False(outcome.HitCap);
        Assert.Equal("esm-tes4", outcome.ContentSniff);
        // Consumed is an upper bound: at least the real stream, overshooting by at most the
        // decompressor's read-ahead into the tail junk.
        Assert.InRange(outcome.ConsumedUpperBound, compressed.Length, withTail.Length);
    }

    [Fact]
    public void ProbeZlibStream_CapsInflationAndReportsHitCap()
    {
        var payload = BuildEsmShapedPayload(256 * 1024);
        using var input = new MemoryStream(ZlibWrap(payload));

        var outcome = DmpRecoveryProbeCommand.ProbeZlibStream(input, 64 * 1024);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(64 * 1024, outcome.InflatedBytes);
        Assert.True(outcome.HitCap);
    }

    [Fact]
    public void ProbeZlibStream_GarbageAfterValidHeaderFailsCleanly()
    {
        // 0x78 0x9C passes the header check; 0xFF as the first deflate byte declares an invalid
        // block type (BTYPE=11), so the decompressor must throw and the probe must report failure.
        var garbage = new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        using var input = new MemoryStream(garbage);

        var outcome = DmpRecoveryProbeCommand.ProbeZlibStream(input, 8L * 1024 * 1024);

        Assert.False(outcome.Success);
        Assert.Equal(0, outcome.InflatedBytes);
        Assert.NotNull(outcome.Error);
    }

    // ===== full ProbeDump over a synthetic dump layout =====

    [Fact]
    public void ProbeDump_ParsesBsaTablesAndInflatesZlibInSyntheticGap()
    {
        // One captured region: VA 0x40000000, file offset 0x1000. The gap starts 0x100 into the
        // region and holds a BSA fixture at a 4-byte-aligned offset plus a zlib stream at an
        // unaligned offset further in.
        const long regionVa = 0x40000000;
        const long regionFileOffset = 0x1000;
        const long gapOffsetInRegion = 0x100;
        const int bsaOffsetInGap = 8; // 4-byte aligned for the magic stride scan

        var bsaPayload = Encoding.ASCII.GetBytes("bsa-probe-entry-payload-bytes");
        var bsaBytes = BuildMinimalV104Bsa(bsaPayload);

        var esmPayload = BuildEsmShapedPayload(32 * 1024);
        var zlibBytes = ZlibWrap(esmPayload);
        var zlibOffsetInGap = bsaOffsetInGap + bsaBytes.Length + 13; // deliberately unaligned

        // Generous slack after the stream: ConsumedUpperBound includes decompressor read-ahead,
        // and ExtendsBeyondGap must stay false for a stream that ends well inside the gap.
        var gapSize = zlibOffsetInGap + zlibBytes.Length + 128 * 1024;
        var regionSize = gapOffsetInRegion + gapSize + 0x100;

        var regionBytes = new byte[regionSize];
        Array.Copy(bsaBytes, 0, regionBytes, gapOffsetInRegion + bsaOffsetInGap, bsaBytes.Length);
        Array.Copy(zlibBytes, 0, regionBytes, gapOffsetInRegion + zlibOffsetInGap, zlibBytes.Length);

        var minidump = new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions =
            {
                new MinidumpMemoryRegion
                {
                    VirtualAddress = regionVa,
                    Size = regionSize,
                    FileOffset = regionFileOffset
                }
            }
        };

        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(regionFileOffset, regionBytes);

        var gapFileOffset = regionFileOffset + gapOffsetInRegion;
        var coverage = new CoverageResult
        {
            FileSize = regionFileOffset + regionSize,
            Gaps =
            {
                new CoverageGap
                {
                    FileOffset = gapFileOffset,
                    Size = gapSize,
                    VirtualAddress = regionVa + gapOffsetInRegion,
                    Classification = GapClassification.BinaryData
                }
            }
        };

        var result = DmpRecoveryProbeCommand.ProbeDump(
            "synthetic.dmp", minidump, coverage, accessor,
            new DmpRecoveryProbeCommand.RecoveryProbeOptions());

        // --- BSA branch ---
        Assert.Equal(1, result.BsaMagicHits);
        var bsaRow = Assert.Single(result.BsaRows);
        Assert.True(bsaRow.ParseSuccess, bsaRow.Error);
        Assert.Equal(gapFileOffset + bsaOffsetInGap, bsaRow.FileOffset);
        Assert.Equal(regionVa + gapOffsetInRegion + bsaOffsetInGap, bsaRow.VirtualAddress);
        Assert.Equal(104u, bsaRow.Version);
        Assert.Equal(1u, bsaRow.FolderCount);
        Assert.Equal(1u, bsaRow.FileCount);
        Assert.Equal(1, bsaRow.ResolvableNames);
        Assert.Equal(bsaPayload.Length, bsaRow.DeclaredDataBytes);
        // The single entry's declared data range sits entirely inside the captured region.
        Assert.Equal(bsaPayload.Length, bsaRow.EntryDataSampledBytes);
        Assert.Equal(bsaPayload.Length, bsaRow.EntryDataResidentBytes);
        Assert.Equal(100.0, bsaRow.EntryDataResidentPercent, 3);
        Assert.Equal(bsaRow.TableBytes, bsaRow.TablePresentBytes);

        // --- zlib branch ---
        var expectedCandidateOffset = gapFileOffset + zlibOffsetInGap;
        var zlibRow = Assert.Single(result.ZlibRows, r => r.CandidateOffset == expectedCandidateOffset);
        Assert.True(zlibRow.Success, zlibRow.Error);
        Assert.Equal(esmPayload.Length, zlibRow.InflatedBytes);
        Assert.Equal("esm-tes4", zlibRow.ContentSniff);
        Assert.False(zlibRow.ExtendsBeyondGap);
        Assert.Equal(regionVa + gapOffsetInRegion + zlibOffsetInGap, zlibRow.CandidateVa);
        Assert.True(result.ZlibCandidatesFound >= 1);
        Assert.True(result.TotalInflatedBytes >= esmPayload.Length);
    }

    [Fact]
    public void ProbeDump_LyingBsaHeaderIsRecordedAsFailureWithoutHugeAllocation()
    {
        const long regionVa = 0x40000000;
        const long regionFileOffset = 0x1000;

        // "BSA\0" + v104 header claiming 50 million files: must be recorded as a failed probe
        // (implausible header), never sized into an allocation.
        var bytes = new byte[256];
        "BSA\0"u8.CopyTo(bytes);
        BitConverter.GetBytes(104u).CopyTo(bytes, 4); // version
        BitConverter.GetBytes(36u).CopyTo(bytes, 8); // folder record offset
        BitConverter.GetBytes(0x3u).CopyTo(bytes, 12); // dir + file names
        BitConverter.GetBytes(1u).CopyTo(bytes, 16); // folder count
        BitConverter.GetBytes(50_000_000u).CopyTo(bytes, 20); // file count (lie)
        BitConverter.GetBytes(8u).CopyTo(bytes, 24);
        BitConverter.GetBytes(16u).CopyTo(bytes, 28);

        var minidump = new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions =
            {
                new MinidumpMemoryRegion
                {
                    VirtualAddress = regionVa, Size = bytes.Length, FileOffset = regionFileOffset
                }
            }
        };
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(regionFileOffset, bytes);

        var coverage = new CoverageResult
        {
            FileSize = regionFileOffset + bytes.Length,
            Gaps =
            {
                new CoverageGap
                {
                    FileOffset = regionFileOffset,
                    Size = bytes.Length,
                    Classification = GapClassification.BinaryData
                }
            }
        };

        var result = DmpRecoveryProbeCommand.ProbeDump(
            "synthetic.dmp", minidump, coverage, accessor,
            new DmpRecoveryProbeCommand.RecoveryProbeOptions());

        var row = Assert.Single(result.BsaRows);
        Assert.False(row.ParseSuccess);
        Assert.Contains("implausible", row.Error);
    }

    // ===== VA stitch =====

    [Fact]
    public void ReadVaOrFlat_StitchesAcrossRegionsAndZeroFillsUnmappedVa()
    {
        // Two regions contiguous in VA but not in file order, with a 16-byte unmapped VA hole
        // between them: [VA 0x1000, 32B] hole [VA 0x1030, 32B].
        var first = Enumerable.Repeat((byte)0xAA, 32).ToArray();
        var second = Enumerable.Repeat((byte)0xBB, 32).ToArray();

        var minidump = new MinidumpInfo
        {
            IsValid = true,
            MemoryRegions =
            {
                new MinidumpMemoryRegion { VirtualAddress = 0x1000, Size = 32, FileOffset = 0x500 },
                new MinidumpMemoryRegion { VirtualAddress = 0x1030, Size = 32, FileOffset = 0x100 }
            }
        };
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(0x500, first);
        accessor.AddRange(0x100, second);

        var (data, present) = DmpRecoveryProbeCommand.ReadVaOrFlat(
            minidump, accessor, 0x1000, 0x500, 0x1000, 80);

        Assert.Equal(64, present);
        Assert.All(data[..32], b => Assert.Equal(0xAA, b));
        Assert.All(data[32..48], b => Assert.Equal(0, b)); // unmapped hole zero-filled
        Assert.All(data[48..80], b => Assert.Equal(0xBB, b));
    }

    // ===== fixtures =====

    /// <summary>"TES4" magic followed by deterministic patterned bytes, so the inflated prefix sniffs as ESM.</summary>
    private static byte[] BuildEsmShapedPayload(int size)
    {
        var payload = new byte[size];
        "TES4"u8.CopyTo(payload);
        for (var i = 4; i < size; i++)
        {
            payload[i] = (byte)(i * 31 % 251);
        }

        return payload;
    }

    /// <summary>
    ///     Deterministic zlib wrap: fixed 0x78 0x9C header (accepted by the candidate filter
    ///     regardless of the actual compression level), raw deflate body, big-endian Adler-32
    ///     trailer. Avoids depending on which FLG byte the runtime's ZLibStream chooses to emit.
    /// </summary>
    private static byte[] ZlibWrap(byte[] payload)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
        {
            deflate.Write(payload);
        }

        var adler = Adler32(payload);
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static uint Adler32(byte[] data)
    {
        const uint modAdler = 65521;
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % modAdler;
            b = (b + a) % modAdler;
        }

        return (b << 16) | a;
    }

    /// <summary>
    ///     Minimal valid v104 BSA (cribbed from BsaMalformedTests.BuildV104Bsa): one folder
    ///     ("meshes"), one uncompressed file ("test.nif"), directory + file names present.
    /// </summary>
    private static byte[] BuildMinimalV104Bsa(byte[] payload)
    {
        const string folderName = "meshes";
        const string fileName = "test.nif";
        // 36 header + 16 folder record + (1 + 7) folder name + 16 file record + 9 file name buffer
        const uint dataOffset = 85;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII);
        bw.Write("BSA\0"u8.ToArray());
        bw.Write(104u); // version
        bw.Write(36u); // folder record offset
        bw.Write(0x3u); // IncludeDirectoryNames | IncludeFileNames
        bw.Write(1u); // folder count
        bw.Write(1u); // file count
        bw.Write((uint)(folderName.Length + 1)); // total folder name length
        bw.Write((uint)(fileName.Length + 1)); // total file name length
        bw.Write((ushort)0x1); // file flags: meshes
        bw.Write((ushort)0); // padding

        bw.Write(0x1122334455667788ul); // folder name hash
        bw.Write(1u); // folder file count
        bw.Write(0u); // folder offset (informational)

        bw.Write((byte)(folderName.Length + 1)); // folder name length incl. null
        bw.Write(Encoding.ASCII.GetBytes(folderName));
        bw.Write((byte)0);

        bw.Write(0x99AABBCCDDEEFF00ul); // file name hash
        bw.Write((uint)payload.Length); // raw size
        bw.Write(dataOffset); // data offset from archive start

        bw.Write(Encoding.ASCII.GetBytes(fileName));
        bw.Write((byte)0);

        bw.Write(payload);
        return ms.ToArray();
    }
}
