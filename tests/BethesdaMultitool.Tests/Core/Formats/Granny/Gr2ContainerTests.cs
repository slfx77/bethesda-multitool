using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Granny;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Granny;

/// <summary>
///     Synthetic vectors for the ported Granny 2 reader: a hand-built container with ONE raw
///     section holding a two-member type definition, its name strings and a root object; and the
///     two codecs on streams whose output can be derived by hand.
/// </summary>
public sealed class Gr2ContainerTests
{
    private const int HeaderSize = 88 + 44; // file info block + one 44-byte section record
    private const int RootObjectOffset = 116; // inside the section: after 3 type records (96) and 20 bytes of strings

    /// <summary>
    ///     A Granny 2 file with one uncompressed section:
    ///     <c>+0</c> three 32-byte type records (String "Name", Int32 "Count", End), <c>+96</c> the
    ///     strings <c>Name\0 Count\0 hello\0</c> (20 bytes), <c>+116</c> the root object — a pointer
    ///     to "hello" then the Int32 7. Three relocations name the three pointer slots.
    /// </summary>
    private static byte[] Fixture(Action<byte[]>? mutate = null, uint version = 6, int dataGap = 0)
    {
        var section = new byte[124];
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(0), (uint)Gr2MemberType.String);
        BinaryPrimitives.WriteUInt32LittleEndian(section.AsSpan(32), (uint)Gr2MemberType.Int32);
        Encoding.ASCII.GetBytes("Name\0Count\0hello\0").CopyTo(section, 96);
        BinaryPrimitives.WriteInt32LittleEndian(section.AsSpan(RootObjectOffset + 4), 7);

        var relocations = new (int Source, int Target)[] { (4, 96), (36, 101), (RootObjectOffset, 107) };
        var relocationBytes = relocations.Length * 12;
        var dataOffset = HeaderSize + relocationBytes + dataGap;
        var file = new byte[dataOffset + section.Length];
        Gr2Container.Magic.CopyTo(file);
        Write(file, 16, HeaderSize);
        Write(file, 20, 0); // header format
        Write(file, 32, version);
        Write(file, 36, (uint)file.Length); // total size
        Write(file, 44, 56); // section array at file-info + 56 = 88
        Write(file, 48, 1); // section count
        Write(file, 52, 0); // root type section
        Write(file, 56, 0); // root type offset
        Write(file, 60, 0); // root object section
        Write(file, 64, RootObjectOffset);
        Write(file, 68, 0x8000000F); // the type tag the prototype carries

        var record = 88;
        Write(file, record, (uint)Gr2Compression.None);
        Write(file, record + 4, (uint)dataOffset);
        Write(file, record + 8, (uint)section.Length);
        Write(file, record + 12, (uint)section.Length);
        Write(file, record + 16, 4); // alignment
        Write(file, record + 20, (uint)section.Length);
        Write(file, record + 24, (uint)section.Length);
        Write(file, record + 28, HeaderSize); // relocations offset
        Write(file, record + 32, (uint)relocations.Length);
        Write(file, record + 36, (uint)(HeaderSize + relocationBytes));
        Write(file, record + 40, 0); // marshalling count
        for (var index = 0; index < relocations.Length; index++)
        {
            var at = HeaderSize + index * 12;
            Write(file, at, (uint)relocations[index].Source);
            Write(file, at + 4, 0);
            Write(file, at + 8, (uint)relocations[index].Target);
        }

        section.CopyTo(file, dataOffset);
        mutate?.Invoke(file);
        return file;
    }

    private static void Write(byte[] file, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(offset), value);
    }

    [Fact]
    public void Parse_ReadsTheSectionTableAndRootAddresses()
    {
        var container = Gr2Container.Parse(Fixture(), "synthetic");

        Assert.Equal(HeaderSize, container.HeaderSize);
        Assert.Equal(6u, container.Version);
        Assert.Equal(0x8000000Fu, container.TypeTag);
        var section = Assert.Single(container.Sections);
        Assert.Equal(Gr2Compression.None, section.Compression);
        Assert.Equal(124, section.ExpandedDataSize);
        Assert.Equal(3, section.Relocations.Count);
        Assert.Equal(new Gr2Address(0, RootObjectOffset), container.RootObject);
    }

    [Fact]
    public void TypeTree_WalksTheSchemaAndReadsTheRootObject()
    {
        var tree = Gr2TypeTree.Parse(Gr2Container.Parse(Fixture(), "synthetic"));

        Assert.Equal(["Name", "Count"], tree.RootType.Members.Select(member => member.Name));
        Assert.Equal(Gr2MemberType.String, tree.RootType.Members[0].Type);
        Assert.Equal(Gr2MemberType.Int32, tree.RootType.Members[1].Type);
        Assert.Equal(8, tree.RootType.Size);
        Assert.Equal("hello", tree.RootObject["Name"].ReadString());
        Assert.Equal(7, tree.RootObject["Count"].ReadInt32());
        Assert.Null(tree.RootObject.FindMember("Missing"));
    }

    [Fact]
    public void Parse_RejectsAGapBetweenTheRelocationTableAndTheData()
    {
        var error = Assert.Throws<InvalidDataException>(() => Gr2Container.Parse(Fixture(dataGap: 4), "gap"));

        Assert.Contains("gap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAnotherFileFormatVersion()
    {
        Assert.Throws<InvalidDataException>(() => Gr2Container.Parse(Fixture(version: 7), "v7"));
    }

    [Fact]
    public void Parse_RejectsARelocationOutsideItsSection()
    {
        // The third relocation's target offset lives at file offset HeaderSize + 2*12 + 8.
        var bytes = Fixture(file => Write(file, HeaderSize + 24 + 8, 5000));

        Assert.Throws<InvalidDataException>(() => Gr2Container.Parse(bytes, "bad reloc"));
    }

    [Fact]
    public void ReadPointer_RefusesAnUnrelocatedNonZeroSlot()
    {
        // Drop the root object's relocation by pointing it at an unused dword, leaving the raw
        // slot value 0x6B (which is what a native pointer left in a file would look like).
        var bytes = Fixture(file =>
        {
            Write(file, HeaderSize + 24, 120);
            Write(file, HeaderSize + 36 + RootObjectOffset, 0x6B);
        });
        var expanded = Gr2ExpandedFile.Expand(Gr2Container.Parse(bytes, "stale"));

        Assert.Throws<InvalidDataException>(() => expanded.ReadPointer(new Gr2Address(0, RootObjectOffset)));
        Assert.Null(expanded.ReadPointer(new Gr2Address(0, 68))); // the End record's name slot: zero, unrelocated
    }

    [Fact]
    public void IsGranny2_RequiresTheFullSixteenByteMagic()
    {
        var bytes = Fixture();
        Assert.True(Gr2Container.IsGranny2(bytes));
        bytes[15] ^= 1;
        Assert.False(Gr2Container.IsGranny2(bytes));
    }

    /// <summary>
    ///     An all-zero Oodle0 stream decodes to zeros, and the derivation is by hand: the arithmetic
    ///     code word starts at 0 and every refill bit is 0, so every <c>GetCount</c> is 0 and every
    ///     model returns its first slot — the escape — whose escaped value <c>GetValue(scale)</c> is
    ///     again 0. Length symbol 0 means "literal"; the literal model's escape yields byte 0. On the
    ///     second byte both models have promoted symbol 0 to a real slot and return it directly.
    ///     Header: block 0 declares max byte value 1 (bits 0-8 of word 0), 1 unique byte value
    ///     (word 1) and one unique length per group (0x01010101); the other two blocks are unused
    ///     because both marshalling stops sit at the end.
    /// </summary>
    [Fact]
    public void Oodle0_AnAllZeroStreamExpandsToZeros()
    {
        var compressed = new byte[36 + 8];
        Write(compressed, 0, 1);
        Write(compressed, 4, 1);
        Write(compressed, 8, 0x01010101);

        var expanded = Gr2Oodle0Decoder.Decompress(compressed, 2, 2, 2);

        Assert.Equal(new byte[] { 0, 0 }, expanded);
    }

    [Fact]
    public void Oodle0_RejectsASectionTaggedWithAnotherCodec()
    {
        var section = Gr2Container.Parse(Fixture(), "raw").Sections[0];

        Assert.Throws<ArgumentException>(() => Gr2Oodle0Decoder.Decode(section));
    }

    /// <summary>
    ///     The Oodle1 counterpart: a zero numerator makes every <c>ReadCumulative</c> return 0, so
    ///     each window's first symbol (the escape) fires, its escaped value is 0, the size symbol 0
    ///     selects a literal and the literal is 0. Parameters: decoded value max 1, back-reference
    ///     max 0 (word 0 = 1), decoded count 1 (word 1 = 1), one size symbol per group.
    /// </summary>
    [Fact]
    public void Oodle1_AnAllZeroStreamExpandsToZeros()
    {
        var compressed = new byte[36 + 16];
        Write(compressed, 0, 1);
        Write(compressed, 4, 1);
        compressed[8] = compressed[9] = compressed[10] = compressed[11] = 1;

        var expanded = Gr2Oodle1Decoder.Decompress(compressed, 2, 2, 2);

        Assert.Equal(new byte[] { 0, 0 }, expanded);
    }

    /// <summary>
    ///     The debug-heap fill test that keeps the six unwritten Van Buren door-box topologies from
    ///     being read as indices: every word must be 0xBAADF00D (0xF00D as a 16-bit array); one
    ///     differing word, an empty buffer or another element size is not a fill.
    /// </summary>
    [Fact]
    public void IsDebugHeapFill_RequiresEveryWordToBeTheFill()
    {
        var wide = new byte[12];
        for (var offset = 0; offset < wide.Length; offset += 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(wide.AsSpan(offset), 0xBAADF00D);
        }

        Assert.True(Gr2File.IsDebugHeapFill(wide, 4));
        Assert.False(Gr2File.IsDebugHeapFill(wide, 2)); // 0xBAAD halves break a 16-bit reading
        wide[5] = 0;
        Assert.False(Gr2File.IsDebugHeapFill(wide, 4));

        var narrow = new byte[] { 0x0D, 0xF0, 0x0D, 0xF0, 0x0D, 0xF0 };
        Assert.True(Gr2File.IsDebugHeapFill(narrow, 2));
        Assert.False(Gr2File.IsDebugHeapFill(narrow, 4)); // 0xF00DF00D is not the fill
        Assert.False(Gr2File.IsDebugHeapFill(narrow.AsSpan(0, 4), 3));
        Assert.False(Gr2File.IsDebugHeapFill([], 4));
        Assert.False(Gr2File.IsDebugHeapFill(new byte[] { 0x0D, 0xF0, 0x0D }, 2));
    }

    [Fact]
    public void Oodle1_RejectsUnorderedStopsAndTruncatedStreams()
    {
        Assert.Throws<InvalidDataException>(() => Gr2Oodle1Decoder.Decompress(new byte[64], 8, 6, 4));
        Assert.Throws<InvalidDataException>(() => Gr2Oodle1Decoder.Decompress(new byte[36], 8, 8, 8));
        Assert.Empty(Gr2Oodle1Decoder.Decompress(new byte[36], 0, 0, 0));
    }
}
