using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using BethesdaMultitool.Core.Formats.VanBuren;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Vectors for the Van Buren prototype's <c>EEN2</c> entity records, shaped after the 285 in the
///     Dec 9 2003 build measured 2026-09-06.
/// </summary>
public sealed class VanBurenEntityRecordTests
{
    private static byte[] Record(string name, string asset, params string[] tags)
    {
        var body = new List<byte>();
        body.AddRange("EEN2"u8);
        body.AddRange(new byte[4]);                       // the zero dword at +4
        body.AddRange(BitConverter.GetBytes(0x72u));      // +8: meaning NOT established
        foreach (var s in new[] { name, asset })
        {
            body.AddRange(BitConverter.GetBytes((ushort)s.Length));
            body.AddRange(Encoding.ASCII.GetBytes(s));
        }

        foreach (var tag in tags)
        {
            body.AddRange(Encoding.ASCII.GetBytes(tag));
            body.AddRange(new byte[4]);
        }

        return [.. body];
    }

    [Fact]
    public void Parse_ReadsTheTwoLengthPrefixedStrings()
    {
        // The u16 length prefix is what makes this readable without guessing: 0x16 then exactly 22
        // characters, as the retail records do.
        var record = VanBurenEntityRecord.Parse(
            Record("GD_Items_First_Aid_Kit", "IT_FirstAidKit_INV.dds"), "_ITM");

        Assert.Equal("GD_Items_First_Aid_Kit", record.Name);
        Assert.Equal("IT_FirstAidKit_INV.dds", record.Asset);
        Assert.Equal(0x72u, record.Unknown);
    }

    [Fact]
    public void Parse_AcceptsAnEmptyName()
    {
        // ⚑ Three of the 285 retail records declare a zero-length name. That is a record without
        // one, not a parse failure, and treating it as an error would reject real data.
        var record = VanBurenEntityRecord.Parse(Record(string.Empty, "x.dds"), "_ARM");

        Assert.Equal(string.Empty, record.Name);
        Assert.Equal("x.dds", record.Asset);
    }

    [Fact]
    public void Parse_ReportsTheNestedTagsItFinds()
    {
        // EEOV and GENT appear in all 285; GCRE appears 108 times, exactly the number of entries in
        // _CRT.grp, which is what makes the tags a type discriminator worth surfacing.
        var record = VanBurenEntityRecord.Parse(Record("GD_Critter", string.Empty, "EEOV", "GENT", "GCRE"), "_CRT");

        Assert.Equal(["EEOV", "GENT", "GCRE"], record.NestedTags);
    }

    [Fact]
    public void Parse_RejectsBytesWithoutTheTag()
    {
        Assert.Throws<InvalidDataException>(() => VanBurenEntityRecord.Parse("NOPE0000000000"u8.ToArray(), "bad"));
    }

    [Fact]
    public void Parse_RejectsANonZeroDwordAtFour()
    {
        var bytes = Record("a", "b");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);

        Assert.Throws<InvalidDataException>(() => VanBurenEntityRecord.Parse(bytes, "bad"));
    }

    [Fact]
    public void Parse_TreatsAMissingAssetAsEmptyRatherThanFailing()
    {
        var bytes = Record("OnlyAName", string.Empty)[..(VanBurenEntityRecord.HeaderLength + 2 + 9)];

        var record = VanBurenEntityRecord.Parse(bytes, "_USE");
        Assert.Equal("OnlyAName", record.Name);
        Assert.Equal(string.Empty, record.Asset);
    }

    [Fact]
    public void IsEntityRecord_ChecksTheTag()
    {
        Assert.True(VanBurenEntityRecord.IsEntityRecord(Record("a", "b")));
        Assert.False(VanBurenEntityRecord.IsEntityRecord("B3D "u8.ToArray()));
    }
}
