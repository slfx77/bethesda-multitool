using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Synthetic containers for the Daggerfall CD's <c>ARENA2\PACKED.DAT</c> reader. The payloads
///     are built by a literal-only PKWARE DCL ENCODER written here — the inverse of the decoder
///     under test, not a call into it — so a round trip proves both the container arithmetic and
///     that the reader hands the codec the right byte range. Every rejection case corrupts exactly
///     one field of an otherwise-valid container, so a test that stopped discriminating (because
///     the reader stopped checking) would fail rather than pass quietly.
/// </summary>
public class DaggerfallPackedArchiveTests
{
    private const uint HeaderConstant0 = 0x00491038;
    private const uint HeaderConstant1 = 0x004D2038;
    private const uint HeaderConstant6 = 0x00080000;
    private const uint HeaderConstant8 = 0x00549754;

    // The container's shape, as LITERALS measured on the retail disc — never the production
    // constants. A fixture built from the code under test moves with it, so a wrong BlockSize (say)
    // would still split "correctly" and the test could not fail.
    private const int FirstBlockOffset = 8;
    private const int BlockHeaderLength = 36;
    private const int EntryLength = 25;
    private const int DirectoryNameLength = 60;
    private const int BlockSize = 262_144;

    [Fact]
    public void Parse_TwoEntries_TilesAndRoundTrips()
    {
        var first = Payload(1_000, 7);
        var second = Payload(2_500, 11);
        var bytes = BuildContainer("ARENA2", [("ARCH3D.BSA", first), ("DAGGER.SND", second)]);
        var path = WriteTemp(bytes);

        try
        {
            Assert.True(DaggerfallPackedArchive.TryProbe(path));
            var archive = DaggerfallPackedArchive.Parse(path);

            Assert.Equal("ARENA2", archive.TargetDirectory);
            Assert.Equal(2, archive.Entries.Count);
            Assert.Equal("ARCH3D.BSA", archive.Entries[0].Name);
            Assert.Equal(1_000, archive.Entries[0].UncompressedSize);
            Assert.Equal(8, archive.Entries[0].FirstBlockOffset);
            Assert.Equal("DAGGER.SND", archive.Entries[1].Name);
            Assert.Equal(2_500, archive.Entries[1].UncompressedSize);

            var read = ReadFrom(bytes);
            Assert.Equal(first, DaggerfallPackedArchive.Extract(archive.Entries[0], read));
            Assert.Equal(second, DaggerfallPackedArchive.Extract(archive.Entries[1], read));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_PayloadLongerThanABlock_SplitsAtTheBlockSize()
    {
        var payload = Payload(BlockSize + 5_000, 3);
        var bytes = BuildContainer("ARENA2", [("BIG.BSA", payload)]);
        var path = WriteTemp(bytes);

        try
        {
            var archive = DaggerfallPackedArchive.Parse(path);
            var entry = Assert.Single(archive.Entries);

            Assert.Equal(2, entry.Blocks.Count);
            Assert.Equal(BlockSize, entry.Blocks[0].UncompressedSize);
            Assert.Equal(5_000, entry.Blocks[1].UncompressedSize);
            Assert.Equal(payload, DaggerfallPackedArchive.Extract(entry, ReadFrom(bytes)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_HeaderConstantCorrupted_Rejected()
    {
        var bytes = BuildContainer("ARENA2", [("ARCH3D.BSA", Payload(64, 1))]);
        bytes[8] ^= 0xFF; // First dword of the first block header.
        var path = WriteTemp(bytes);

        try
        {
            Assert.False(DaggerfallPackedArchive.TryProbe(path));
            var error = Assert.Throws<InvalidDataException>(() => DaggerfallPackedArchive.Parse(path));
            Assert.Contains("block header", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_DirectoryNameNotEndingAtEof_Rejected()
    {
        var bytes = BuildContainer("ARENA2", [("ARCH3D.BSA", Payload(64, 1))]);
        var path = WriteTemp([.. bytes, 0]); // One byte past the directory name.

        try
        {
            Assert.False(DaggerfallPackedArchive.TryProbe(path));
            var error = Assert.Throws<InvalidDataException>(() => DaggerfallPackedArchive.Parse(path));
            Assert.Contains("EOF", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_EntryTableNotAWholeNumberOfRecords_Rejected()
    {
        var bytes = BuildContainer("ARENA2", [("ARCH3D.BSA", Payload(64, 1))]);
        var tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, tableOffset - 1);
        var path = WriteTemp(bytes);

        try
        {
            Assert.False(DaggerfallPackedArchive.TryProbe(path));
            var error = Assert.Throws<InvalidDataException>(() => DaggerfallPackedArchive.Parse(path));
            Assert.Contains("whole number", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     Moving the second entry's start four bytes on breaks the chain from both ends: the first
    ///     entry's blocks no longer end where the second begins. The reader catches it there, which
    ///     is why the message is about the FIRST entry's blocks rather than the second's start.
    /// </summary>
    [Fact]
    public void Parse_SecondEntryStartingElsewhere_Rejected()
    {
        var bytes = BuildContainer(
            "ARENA2", [("ARCH3D.BSA", Payload(500, 2)), ("DAGGER.SND", Payload(500, 5))]);
        var tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var secondStartField = (int)tableOffset + EntryLength + 21;
        var start = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(secondStartField));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(secondStartField), start + 4);
        var path = WriteTemp(bytes);

        try
        {
            Assert.False(DaggerfallPackedArchive.TryProbe(path));
            var error = Assert.Throws<InvalidDataException>(() => DaggerfallPackedArchive.Parse(path));
            Assert.Contains("its blocks end at", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_FirstEntryNotStartingAtTheFirstBlock_Rejected()
    {
        var bytes = BuildContainer("ARENA2", [("ARCH3D.BSA", Payload(500, 2))]);
        var tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var startField = bytes.AsSpan((int)tableOffset + 21);
        BinaryPrimitives.WriteUInt32LittleEndian(startField, FirstBlockOffset + 4);
        var path = WriteTemp(bytes);

        try
        {
            Assert.False(DaggerfallPackedArchive.TryProbe(path));
            var error = Assert.Throws<InvalidDataException>(() => DaggerfallPackedArchive.Parse(path));
            Assert.Contains("the previous blocks end at 8", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     The container's arithmetic CANNOT see a compressed size that is too large — the blocks
    ///     still tile and the entry still ends on the table — so the check has to come from the
    ///     payload: a block must decode from every byte its header declares, ending on the codec's
    ///     end-of-stream code. This is the same class of error as the "8-byte trailer" framing that
    ///     tiled identically to the right one on the retail disc for weeks.
    /// </summary>
    [Fact]
    public void Extract_BlockPaddedBeyondItsEndCode_Rejected()
    {
        var payload = Payload(700, 17);
        var bytes = BuildContainer("ARENA2", [("ARCH3D.BSA", payload)], 3);
        var path = WriteTemp(bytes);

        try
        {
            // The container itself is still well-formed: only the decode can catch this.
            Assert.True(DaggerfallPackedArchive.TryProbe(path));
            var archive = DaggerfallPackedArchive.Parse(path);

            var error = Assert.Throws<InvalidDataException>(() =>
                DaggerfallPackedArchive.Extract(archive.Entries[0], ReadFrom(bytes)));

            Assert.Contains("compressed bytes its header declares", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     The fixture above is built from literals measured on the retail disc, so it would keep
    ///     passing if the reader's own constants drifted. This is the pin that catches that.
    /// </summary>
    [Fact]
    public void Constants_MatchTheShapeMeasuredOnTheRetailDisc()
    {
        Assert.Equal(8, DaggerfallPackedArchive.FirstBlockOffset);
        Assert.Equal(36, DaggerfallPackedArchive.BlockHeaderLength);
        Assert.Equal(25, DaggerfallPackedArchive.EntryLength);
        Assert.Equal(60, DaggerfallPackedArchive.DirectoryNameLength);
        Assert.Equal(262_144, DaggerfallPackedArchive.BlockSize);
    }

    [Fact]
    public void TryProbe_OrdinaryFile_NotClaimed()
    {
        var path = WriteTemp(Payload(4_096, 13));

        try
        {
            Assert.False(DaggerfallPackedArchive.TryProbe(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Reader over the in-memory container, standing in for the backend's mapped view.</summary>
    private static Func<long, int, byte[]> ReadFrom(byte[] container)
    {
        return (offset, count) => container.AsSpan((int)offset, count).ToArray();
    }

    /// <summary>Deterministic pseudo-random bytes, so a failure is reproducible.</summary>
    private static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"packed_{Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    ///     Assembles a container around the given files, block by block.
    ///     <paramref name="blockPadding" /> appends that many bytes to each block's DCL stream and
    ///     counts them in its declared compressed size, so the container still tiles exactly while
    ///     the payload no longer accounts for every byte the header claims.
    /// </summary>
    private static byte[] BuildContainer(
        string targetDirectory, (string Name, byte[] Payload)[] files, int blockPadding = 0)
    {
        var body = new List<byte>();
        body.AddRange(new byte[FirstBlockOffset]);

        var starts = new List<int>();
        foreach (var (_, payload) in files)
        {
            starts.Add(body.Count);
            for (var offset = 0; offset < payload.Length; offset += BlockSize)
            {
                var plain = payload.AsSpan(
                    offset, Math.Min(BlockSize, payload.Length - offset));
                var stream = EncodeRawLiterals(plain);
                if (blockPadding > 0)
                {
                    stream = [.. stream, .. new byte[blockPadding]];
                }

                var header = new byte[BlockHeaderLength];
                BinaryPrimitives.WriteUInt32LittleEndian(header, HeaderConstant0);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), HeaderConstant1);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)plain.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)stream.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)stream.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)plain.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), HeaderConstant6);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 0xDEADBEEF);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), HeaderConstant8);
                body.AddRange(header);
                body.AddRange(stream);
            }
        }

        var tableOffset = body.Count;
        for (var i = 0; i < files.Length; i++)
        {
            var record = new byte[EntryLength];
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)files[i].Payload.Length);
            var name = Encoding.Latin1.GetBytes(files[i].Name);
            name.CopyTo(record, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(21), (uint)starts[i]);
            body.AddRange(record);
        }

        var directoryOffset = body.Count;
        var directoryField = new byte[DirectoryNameLength];
        Encoding.Latin1.GetBytes(targetDirectory).CopyTo(directoryField, 0);
        body.AddRange(directoryField);

        var bytes = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)tableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)directoryOffset);
        return bytes;
    }

    /// <summary>
    ///     Encodes <paramref name="plain" /> as a DCL stream of raw literals: header 0x00 (uncoded
    ///     literals) and 0x06 (4 KiB dictionary, the exponent the retail container uses), then, for
    ///     each byte, a 0 flag bit and its eight bits low bit first, packed LSB-first, and finally
    ///     the END-OF-STREAM CODE — a 1 flag bit, seven 0 bits (length symbol 15, base 264) and
    ///     eight 1 bits of extra, making 519. ⚠ This fixture used to close with two zero bytes
    ///     instead, on the belief that the retail blocks carried no end code and left two bytes of
    ///     encoder flush; decoding one symbol past the declared size refutes that on 134 of 134
    ///     retail blocks, so the synthetic container now ends its blocks the way the real one does.
    /// </summary>
    private static byte[] EncodeRawLiterals(ReadOnlySpan<byte> plain)
    {
        var output = new List<byte> { 0x00, 0x06 };
        var accumulator = 0;
        var bits = 0;

        void Push(int bit)
        {
            accumulator |= bit << bits;
            if (++bits == 8)
            {
                output.Add((byte)accumulator);
                accumulator = 0;
                bits = 0;
            }
        }

        foreach (var value in plain)
        {
            Push(0);
            for (var i = 0; i < 8; i++)
            {
                Push((value >> i) & 1);
            }
        }

        Push(1);
        for (var i = 0; i < 7; i++)
        {
            Push(0);
        }

        for (var i = 0; i < 8; i++)
        {
            Push(1);
        }

        if (bits > 0)
        {
            output.Add((byte)accumulator);
        }

        return [.. output];
    }
}