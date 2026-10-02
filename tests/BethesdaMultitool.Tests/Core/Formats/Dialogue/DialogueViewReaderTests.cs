using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Dialogue.CreationKit;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Dialogue;

/// <summary>Checks sidecar identity, preservation, malformed-input rejection, and archive selection.</summary>
public sealed class DialogueViewReaderTests
{
    private const string Diagram = """
        <Diagram Version="12"><Nodes>
          <Node Id="a" Class="std:TableNode"><Bounds>1, 2, 300, 120</Bounds>
            <ToolTip>0x01031459 extra 0000ABCD</ToolTip><Text>Authored text</Text>
            <Cells><Cell><Text>Cell text</Text></Cell></Cells><Unknown><Value>retained</Value></Unknown>
          </Node>
        </Nodes><Links><Link Id="edge"><Origin Id="a"/><Destination Id="absent"/>
          <Points><Point>1, 2</Point><Point>5, 6</Point></Points></Link></Links></Diagram>
        """;

    /// <summary>Retains diagram IDs and stale edges independently of parsed potential FormIDs.</summary>
    [Fact]
    public void ReadsLayoutCellTextAndUnresolvedLinksWithoutInventingRecords()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Diagram));
        var document = DialogueViewReader.Read(stream, "test.xml");
        var node = Assert.Single(document.Nodes);
        Assert.Equal("a", node.Id);
        Assert.Equal(new DialogueViewBounds(1, 2, 300, 120), node.Bounds);
        Assert.Contains("Cell text", node.Text, StringComparison.Ordinal);
        Assert.Equal(new uint[] { 0x01031459, 0x0000ABCD }, node.FormIds);
        Assert.Equal("absent", Assert.Single(document.Links).DestinationId);
        Assert.Contains("<Unknown><Value>retained</Value></Unknown>", document.OriginalXml, StringComparison.Ordinal);
        Assert.True(stream.CanRead);
    }

    /// <summary>Rejects invalid structure and dangerous XML declarations.</summary>
    [Theory]
    [InlineData("<root/>")]
    [InlineData("<Diagram><Nodes><Node Id='x'/><Node Id='x'/></Nodes></Diagram>")]
    [InlineData("<Diagram><Nodes><Node Id='x'><Bounds>NaN,0,1,2</Bounds></Node></Nodes></Diagram>")]
    [InlineData("<Diagram><Nodes><Node Id='x'><Bounds>0,0,-1,2</Bounds></Node></Nodes></Diagram>")]
    public void RejectsMalformedDiagrams(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        Assert.Throws<InvalidDataException>(() => DialogueViewReader.Read(stream, "invalid.xml"));
    }

    /// <summary>External entities are disabled even when a diagram otherwise has the correct root.</summary>
    [Fact]
    public void RejectsDtdDeclarations()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE Diagram [<!ENTITY x SYSTEM 'file:///private'>]><Diagram>&x;</Diagram>"));
        Assert.Throws<System.Xml.XmlException>(() => DialogueViewReader.Read(stream, "external.xml"));
    }

    /// <summary>Reads DialogueViews folders in Scripts.zip and ignores unrelated XML configuration.</summary>
    [Fact]
    public void ReadsSidecarsFromZipWithoutExtractingFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "dialogue-views-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("Data/DialogueViews/01000001.xml").Open())) { writer.Write(Diagram); }
                using var ignored = new StreamWriter(zip.CreateEntry("Data/Scripts/config.xml").Open());
                ignored.Write("<unrelated/>");
            }
            var document = Assert.Single(DialogueViewReader.ReadPath(path, TestContext.Current.CancellationToken));
            Assert.EndsWith("!/Data/DialogueViews/01000001.xml", document.Source, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }
}
