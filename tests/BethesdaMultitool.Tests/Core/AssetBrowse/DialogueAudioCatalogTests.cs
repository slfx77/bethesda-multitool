using BethesdaMultitool.CLI.Commands.Dialogue;
using BethesdaMultitool.Core.Media.Audio.Dialogue;
using BethesdaMultitool.Core.Vfs;
using Microsoft.VisualBasic.FileIO;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

public sealed class DialogueAudioCatalogTests
{
    [Theory]
    [InlineData("topic_00001234_1.wav", "matched")]
    [InlineData("topic_00001234_2.wav", "unmatched-response")]
    [InlineData("topic_00005678_1.wav", "unmatched-info")]
    [InlineData("unknown.wav", "unparsed-name")]
    public void CatalogRetainsMatchedAndOrphanEntries(string filename, string state)
    {
        var entry = new GameFileEntry($"sound/voice/FalloutNV.esm/Male/{filename}", 12, "voices.bsa");
        var rows = DialogueAudioCatalog.Build([entry], [new("FalloutNV.esm", 0x1234, "FalloutNV.esm", 42,
            [new(1, "A quoted, \"line\".\nNext line.")])], ["FalloutNV.esm"]);
        Assert.Equal(state, Assert.Single(rows).State);
        using var parser = new TextFieldParser(new StringReader(DialogueAudioCatalogCommand.ToCsv(rows)))
            { HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var header = parser.ReadFields()!;
        var data = parser.ReadFields()!;
        Assert.Equal(header.Length, data.Length);
        Assert.Equal("Pending", data[^1]);
        if (state == "matched") Assert.Equal("A quoted, \"line\".\nNext line.", data[10]);
        Assert.True(parser.EndOfData);
    }

    [Fact]
    public void CompetingRecordsAndAudioLayersRemainVisible()
    {
        const string path = "sound/voice/FalloutNV.esm/Male/topic_00001234_1.wav";
        var rows = DialogueAudioCatalog.Build([new(path, 12, "a.bsa"), new(path, 12, "b.bsa")],
            [new("FalloutNV.esm", 0x1234, "one.esm", 42, [new(1, "One")]),
             new("FalloutNV.esm", 0x1234, "two.esm", 84, [new(1, "Two")])], ["FalloutNV.esm"]);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => { Assert.Equal("ambiguous", row.State); Assert.True(row.AmbiguousAudio); Assert.Equal(2, row.Matches.Count); });
        var unavailable = DialogueAudioCatalog.Build([new(path, 12, "a.bsa")], [], []);
        Assert.Equal("unavailable-plugin", Assert.Single(unavailable).State);
    }
}
