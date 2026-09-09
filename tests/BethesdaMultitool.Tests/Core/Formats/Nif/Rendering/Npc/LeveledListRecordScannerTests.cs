using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class LeveledListRecordScannerTests
{
    [Fact]
    public void Process_PreservesLvldLvlfAndFullLvloEntries()
    {
        var levelOne = BuildLvlo(1, 0x00000C0C, 1, 0x08ED);
        var levelTwenty = BuildLvlo(20, 0x00035E76, 3, 0x1234);
        var (recordBytes, record) = EsmTestRecordBuilder.BuildAnalyzerRecord(
            0x0003ABC0,
            "LVLI",
            false,
            ("EDID", EsmTestRecordBuilder.NullTermString("LL0NPCWeaponLongswordLvl100")),
            ("LVLD", [17]),
            ("LVLF", [2]),
            ("LVLO", levelOne),
            ("LVLO", levelTwenty));

        var leveledList = LeveledListRecordScanner.Process(recordBytes, false, record);

        Assert.NotNull(leveledList);
        Assert.Equal("LL0NPCWeaponLongswordLvl100", leveledList.EditorId);
        Assert.Equal(17, leveledList.ChanceNone);
        Assert.Equal(0x02, leveledList.Flags);
        Assert.False(leveledList.CalculateFromAllLevelsAtOrBelowPlayer);
        Assert.True(leveledList.CalculateForEachItemInCount);
        Assert.Collection(
            leveledList.Entries,
            entry =>
            {
                Assert.Equal((ushort)1, entry.Level);
                Assert.Equal(0x00000C0Cu, entry.FormId);
                Assert.Equal((ushort)1, entry.Count);
            },
            entry =>
            {
                Assert.Equal((ushort)20, entry.Level);
                Assert.Equal(0x00035E76u, entry.FormId);
                Assert.Equal((ushort)3, entry.Count);
            });
    }

    private static byte[] BuildLvlo(
        ushort level,
        uint formId,
        ushort count,
        ushort trailingValue)
    {
        var data = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(data, level);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 0x065D);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), formId);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), count);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), trailingValue);
        return data;
    }
}