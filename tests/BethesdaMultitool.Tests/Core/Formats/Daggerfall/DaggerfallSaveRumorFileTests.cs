using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Synthetic vectors for <see cref="DaggerfallSaveRumorFile" />: a 34-byte header per rumour
///     and a NUL-terminated text whose terminator is INSIDE the declared length.
/// </summary>
public class DaggerfallSaveRumorFileTests
{
    /// <summary>One rumour record. <paramref name="text" /> gets its 0xFD breaks and NUL added here.</summary>
    internal static byte[] Rumor(
        ushort faction1,
        uint type,
        byte regionId,
        byte flags,
        string text,
        byte questId = 0,
        string questName = "",
        ushort messageId = 0,
        uint timeLimit = 566_670)
    {
        var body = new List<byte>(Encoding.ASCII.GetBytes(text));
        body.Add(DaggerfallSaveRumor.LineBreak);
        body.Add(0);

        var header = new byte[DaggerfallSaveRumor.HeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(header, faction1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), type);
        header[8] = regionId;
        header[9] = flags;
        header[10] = questId;
        Encoding.ASCII.GetBytes(questName).CopyTo(header.AsSpan(11));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), messageId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(26), (uint)body.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(30), timeLimit);

        return [.. header, .. body];
    }

    private static byte[] TwoRumorFile()
    {
        return
        [
            .. Rumor(208, 12, 32, 8, "Rlerki is the new Count of Shalgora."),
            .. Rumor(0, 0, 0, 4, "Lady Brisienna awaits someone at The Restless Castle in Mermont", 1, "_BRISIEN",
                1_005, 566_730)
        ];
    }

    [Fact]
    public void Parse_TilesTheFileExactly()
    {
        var file = DaggerfallSaveRumorFile.Parse(TwoRumorFile(), "RUMOR.DAT");

        Assert.Equal(2, file.Rumors.Count);
        Assert.Equal(file.Length,
            file.Rumors[1].Offset + DaggerfallSaveRumor.HeaderLength + (int)file.Rumors[1].TextLength);
        Assert.Equal(0, file.Rumors[0].Offset);
        Assert.Equal(DaggerfallSaveRumor.HeaderLength + 38, file.Rumors[1].Offset);
    }

    [Fact]
    public void Parse_ReadsARegionRumor()
    {
        var rumor = DaggerfallSaveRumorFile.Parse(TwoRumorFile(), "RUMOR.DAT").Rumors[0];

        Assert.Equal(0, rumor.Index);
        Assert.Equal(208, rumor.Faction1);
        Assert.Equal(0, rumor.Faction2);
        // 12 is the "new ruler" rumour the retail save carries nine of.
        Assert.Equal(12u, rumor.RumorType);
        Assert.Equal(32, rumor.RegionId);
        Assert.Equal(8, rumor.Flags);
        Assert.Equal(string.Empty, rumor.QuestName);
        Assert.Equal(0, rumor.QuestMessageId);
        Assert.Equal(566_670u, rumor.TimeLimit);
        // 36 characters, the 0xFD break and the NUL.
        Assert.Equal(38u, rumor.TextLength);
        Assert.Equal("Rlerki is the new Count of Shalgora.\n", rumor.Text);
    }

    [Fact]
    public void Parse_ReadsAQuestRumor()
    {
        var rumor = DaggerfallSaveRumorFile.Parse(TwoRumorFile(), "RUMOR.DAT").Rumors[1];

        Assert.Equal(1, rumor.QuestId);
        Assert.Equal("_BRISIEN", rumor.QuestName);
        Assert.Equal(1_005, rumor.QuestMessageId);
        Assert.Equal(0u, rumor.RumorType);
        Assert.Equal(4, rumor.Flags);
        Assert.Equal(566_730u, rumor.TimeLimit);
        Assert.StartsWith("Lady Brisienna awaits someone", rumor.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsAnEmptyFile()
    {
        var file = DaggerfallSaveRumorFile.Parse([], "RUMOR.DAT");

        Assert.Empty(file.Rumors);
        Assert.Equal(0, file.Length);
    }

    /// <summary>A text length that runs past EOF is a fault, not a truncated read.</summary>
    [Fact]
    public void Parse_RefusesATextLengthThatOverruns()
    {
        var bytes = TwoRumorFile();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(26), 10_000);

        var error = Assert.Throws<InvalidDataException>(() => DaggerfallSaveRumorFile.Parse(bytes, "RUMOR.DAT"));
        Assert.Contains("overruns", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Bytes left over that cannot hold another header mean the framing is wrong.</summary>
    [Fact]
    public void Parse_RefusesADanglingPartialHeader()
    {
        var bytes = TwoRumorFile();
        Array.Resize(ref bytes, bytes.Length + 10);

        var error = Assert.Throws<InvalidDataException>(() => DaggerfallSaveRumorFile.Parse(bytes, "RUMOR.DAT"));
        Assert.Contains("34-byte rumour header", error.Message, StringComparison.Ordinal);
    }
}