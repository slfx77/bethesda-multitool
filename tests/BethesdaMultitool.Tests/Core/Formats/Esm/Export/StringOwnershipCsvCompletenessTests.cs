using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.RuntimeBuffer;
using BethesdaMultitool.Core.Strings;
using Microsoft.VisualBasic.FileIO;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

public sealed class StringOwnershipCsvCompletenessTests
{
    [Theory]
    [InlineData("owned", "string_owned_editorids.csv", 12)]
    [InlineData("unknown", "string_unknown_owners.csv", 9)]
    [InlineData("unreferenced", "string_unreferenced.csv", 5)]
    public void Export_retains_rows_after_the_former_limit_and_preserves_csv_shape(string bucket, string file, int columns)
    {
        var analysis = new RuntimeStringOwnershipAnalysis();
        var hits = Enumerable.Range(0, 20003).Select(i => new RuntimeStringHit
        {
            Text = $"{i:D5}, \"quoted\"\nsecond line", Category = StringCategory.EditorId, FileOffset = i,
            OwnerResolution = new() { OwnerName = "Owner, \"name\"" }
        }).ToArray();
        var destination = bucket switch
        {
            "owned" => analysis.OwnedHits,
            "unknown" => analysis.ReferencedOwnerUnknownHits,
            _ => analysis.UnreferencedHits
        };
        destination.AddRange(hits);
        var csv = CsvSupplementalWriter.GenerateStringOwnershipCsvs(analysis)[file];
        using var reader = new TextFieldParser(new StringReader(csv))
        {
            TextFieldType = FieldType.Delimited, Delimiters = [","], HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        Assert.Equal(columns, reader.ReadFields()!.Length);
        var rows = 0;
        string? last = null;
        while (!reader.EndOfData)
        {
            var fields = reader.ReadFields()!;
            Assert.Equal(columns, fields.Length);
            last = fields[0];
            rows++;
        }
        Assert.Equal(20003, rows);
        Assert.Equal(hits[^1].Text, last);
    }
}
