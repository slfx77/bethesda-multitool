using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Phase 2a.3 — typed decode of REGN RDAT payloads into the read-only viewer projections
///     (<see cref="RegionRecord.WeatherTypes" /> / <see cref="RegionRecord.GrassFormIds" />).
///     The raw payload bytes are still round-tripped verbatim by RegnEncoder; these tests pin the
///     derived decode logic: RDWT = 12 bytes/entry (Weather FormID, Chance, Global FormID — stride
///     confirmed by the Xbox→PC converter schema), RDGS = 8 bytes/entry (Grass FormID + 4 unused —
///     fopdoc layout, decoded only when the length divides evenly so a wrong stride yields empty,
///     not garbage). Both endiannesses are covered (Xbox big-endian vs PC little-endian).
/// </summary>
public class RegionDataBlockDecodeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodeRegionWeatherTypes_TwoEntries_BothEndian(bool bigEndian)
    {
        var bytes = Concat(
            WeatherEntry(0x000AAAAA, 50, 0x000BBBBB, bigEndian),
            WeatherEntry(0x000CCCCC, 50, 0u, bigEndian));

        var into = new List<RegionWeatherType>();
        WorldRecordHandler.DecodeRegionWeatherTypes(bytes, bigEndian, into);

        Assert.Equal(2, into.Count);
        Assert.Equal(new RegionWeatherType(0x000AAAAA, 50, 0x000BBBBB), into[0]);
        Assert.Equal(new RegionWeatherType(0x000CCCCC, 50, 0u), into[1]);
    }

    [Fact]
    public void DecodeRegionWeatherTypes_TrailingPartialEntry_Ignored()
    {
        // 12 valid bytes + 5 trailing bytes (not a whole entry) — the loop stops at the last
        // complete 12-byte stride and never reads past the buffer.
        var bytes = Concat(
            WeatherEntry(0x00010203, 100, 0x00040506, bigEndian: false),
            [0xDE, 0xAD, 0xBE, 0xEF, 0x00]);

        var into = new List<RegionWeatherType>();
        WorldRecordHandler.DecodeRegionWeatherTypes(bytes, isBigEndian: false, into);

        Assert.Single(into);
        Assert.Equal(new RegionWeatherType(0x00010203, 100, 0x00040506), into[0]);
    }

    [Fact]
    public void DecodeRegionWeatherTypes_Empty_ProducesNothing()
    {
        var into = new List<RegionWeatherType>();
        WorldRecordHandler.DecodeRegionWeatherTypes([], isBigEndian: false, into);
        Assert.Empty(into);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodeRegionGrasses_EightByteStride_ReadsFormIdAtEntryStart(bool bigEndian)
    {
        // Three GRAS entries: FormID + 4 unused bytes each.
        var bytes = Concat(
            GrassEntry(0x000D1000, bigEndian),
            GrassEntry(0x000D2000, bigEndian),
            GrassEntry(0x000D3000, bigEndian));

        var into = new List<uint>();
        WorldRecordHandler.DecodeRegionGrasses(bytes, bigEndian, into);

        Assert.Equal([0x000D1000u, 0x000D2000u, 0x000D3000u], into);
    }

    [Fact]
    public void DecodeRegionGrasses_MisalignedLength_LeavesListEmpty()
    {
        // 20 bytes is not a whole number of 8-byte entries (20 % 8 == 4). The guard refuses to
        // decode rather than misinterpret a possibly-different stride as garbage FormIDs.
        var into = new List<uint>();
        WorldRecordHandler.DecodeRegionGrasses(new byte[20], isBigEndian: false, into);
        Assert.Empty(into);
    }

    [Fact]
    public void DecodeRegionGrasses_Empty_ProducesNothing()
    {
        var into = new List<uint>();
        WorldRecordHandler.DecodeRegionGrasses([], isBigEndian: false, into);
        Assert.Empty(into);
    }

    private static byte[] WeatherEntry(uint weather, uint chance, uint global, bool bigEndian)
    {
        var bytes = new byte[12];
        WriteUInt32(bytes.AsSpan(0), weather, bigEndian);
        WriteUInt32(bytes.AsSpan(4), chance, bigEndian);
        WriteUInt32(bytes.AsSpan(8), global, bigEndian);
        return bytes;
    }

    private static byte[] GrassEntry(uint grass, bool bigEndian)
    {
        var bytes = new byte[8];
        WriteUInt32(bytes.AsSpan(0), grass, bigEndian);
        // bytes[4..8] left zero — the "unused" tail.
        return bytes;
    }

    private static void WriteUInt32(Span<byte> dest, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(dest, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(dest, value);
        }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }
}
