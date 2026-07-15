using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class InfoResponseTextExtractorTests
{
    [Fact]
    public void Extract_UsesStoredNumbersAndOrdinalFallback()
    {
        var record = Info(
            Trdt(0), Text("First"),
            Trdt(7), Text("Sparse seventh"),
            Trdt(0), Text("Ordinal third"));

        var responses = InfoResponseTextExtractor.Extract(record);

        Assert.Equal("First", responses[1]);
        Assert.Equal("Sparse seventh", responses[7]);
        Assert.Equal("Ordinal third", responses[3]);
        Assert.Equal(3, responses.Count);
    }

    [Fact]
    public void Extract_ResponseOnlyXboxHalfUsesNam1Order()
    {
        var responses = InfoResponseTextExtractor.Extract(Info(Text("First"), Text("Second")));

        Assert.Equal("First", responses[1]);
        Assert.Equal("Second", responses[2]);
    }

    [Fact]
    public void Extract_SupportsRecoveredNam1BeforeTrdtOrder()
    {
        var responses = InfoResponseTextExtractor.Extract(Info(
            Text("Sparse seventh"), Trdt(7),
            Text("Second"), Trdt(2)));

        Assert.Equal("Sparse seventh", responses[7]);
        Assert.Equal("Second", responses[2]);
    }

    private static ParsedMainRecord Info(params ParsedSubrecord[] subrecords) => new()
    {
        Header = new MainRecordHeader { Signature = "INFO", FormId = 0x0010656E },
        Subrecords = [..subrecords]
    };

    private static ParsedSubrecord Trdt(byte responseNumber)
    {
        var data = new byte[24];
        data[12] = responseNumber;
        return new ParsedSubrecord { Signature = "TRDT", Data = data };
    }

    private static ParsedSubrecord Text(string value) => new()
    {
        Signature = "NAM1",
        Data = System.Text.Encoding.Latin1.GetBytes(value + "\0")
    };
}
