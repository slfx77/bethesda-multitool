using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using Microsoft.VisualBasic.FileIO;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

public sealed class CsvNpcColumnAlignmentTests
{
    [Fact]
    public void GenerateNpcsCsv_HairColorPreservesFollowingReferenceColumns()
    {
        var npc = new NpcRecord
        {
            FormId = 0x1000,
            EditorId = "ColorTestNpc",
            FullName = "Test, \"NPC\"",
            HairColor = 0x0000274F,
            EyesFormId = 0x1001,
            CombatStyleFormId = 0x1002,
            FaceGenGeometrySymmetric = [0f],
            IsBigEndian = true,
            Offset = 12345
        };
        var resolver = new FormIdResolver(
            new Dictionary<uint, string> { [0x1001] = "TestEyes", [0x1002] = "TestCombatStyle" },
            new Dictionary<uint, string> { [0x1001] = "Hazel, \"warm\"", [0x1002] = "Guard, ranged" });
        var csv = CsvActorWriter.GenerateNpcsCsv([npc], resolver);

        // Parse with the framework's independent CSV reader: splitting on commas would also split valid quoted fields.
        using var parser = new TextFieldParser(new StringReader(csv))
        {
            TextFieldType = FieldType.Delimited,
            Delimiters = [","],
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        var header = Assert.IsType<string[]>(parser.ReadFields());
        var row = Assert.IsType<string[]>(parser.ReadFields());
        Assert.True(parser.EndOfData);
        Assert.Equal(header.Length, row.Length);
        var fields = header.Zip(row).ToDictionary(pair => pair.First, pair => pair.Second);

        Assert.Equal(npc.FullName, fields["Name"]);
        Assert.Equal("#4F2700 (79, 39, 0)", fields["HairColor"]);
        Assert.Equal("0x00001001", fields["EyesFormID"]);
        Assert.Equal("TestEyes", fields["EyesName"]);
        Assert.Equal("Hazel, \"warm\"", fields["EyesDisplayName"]);
        Assert.Equal("0x00001002", fields["CombatStyleFormID"]);
        Assert.Equal("TestCombatStyle", fields["CombatStyleName"]);
        Assert.Equal("Guard, ranged", fields["CombatStyleDisplayName"]);
        Assert.Equal("Yes", fields["HasFaceGen"]);
        Assert.Equal("BE", fields["Endianness"]);
        Assert.Equal("12345", fields["Offset"]);
        Assert.Empty(fields["SubDetail"]);
    }
}
