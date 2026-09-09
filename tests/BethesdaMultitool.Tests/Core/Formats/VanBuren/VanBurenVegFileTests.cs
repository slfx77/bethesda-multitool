using System.Text;
using BethesdaMultitool.Core.Formats.VanBuren;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Vectors for the Van Buren <c>VEG </c> effect group, shaped after the 119 shipped in the
///     prototype's <c>.grp</c> archives, all of which tile exactly (2026-09-06).
/// </summary>
public sealed class VanBurenVegFileTests
{
    private static void WriteString(List<byte> to, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        to.AddRange([(byte)(bytes.Length & 0xFF), (byte)(bytes.Length >> 8)]);
        to.AddRange(bytes);
    }

    private static void WriteProperty(List<byte> to, string name, string type, byte[] value)
    {
        WriteString(to, name);
        WriteString(to, type);
        to.AddRange(BitConverter.GetBytes(VanBurenVegFile.PropertySeparator));
        to.AddRange(BitConverter.GetBytes((uint)value.Length));
        to.AddRange(value);
    }

    private static void WriteBlock(List<byte> to, params (string Name, string Type, byte[] Value)[] properties)
    {
        to.AddRange(BitConverter.GetBytes((uint)properties.Length));
        foreach (var (name, type, value) in properties)
        {
            WriteProperty(to, name, type, value);
        }
    }

    private static byte[] Group(int blockCount, params (string Name, string Type, byte[] Value)[] blockProperties)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.Latin1.GetBytes(VanBurenVegFile.GroupTag));
        bytes.AddRange(BitConverter.GetBytes((uint)blockCount));
        bytes.AddRange(new byte[12]);
        for (var i = 0; i < blockCount; i++)
        {
            bytes.AddRange(Encoding.Latin1.GetBytes(VanBurenVegFile.BlockTag));
            bytes.AddRange(new byte[8]);
            WriteBlock(bytes, blockProperties);
        }

        // The trailing UNTAGGED group block — retail always carries exactly these three.
        WriteBlock(bytes,
            ("Loop", "bool", [1]),
            ("MinStartTime", "float", [0, 0, 0, 0]),
            ("MaxStartTime", "float", [0, 0, 0, 0]));
        return [.. bytes];
    }

    [Fact]
    public void TryParse_ReadsTheBlocksAndTheirSelfDescribingProperties()
    {
        var bytes = Group(1, ("Delay", "float", [0, 0, 0, 0]), ("EffectType", "enum VFX_EffectType", [2, 0, 65, 66]));

        Assert.True(VanBurenVegFile.TryParse(bytes, "veg", out var file, out var error), error);
        var block = Assert.Single(file.Blocks);
        Assert.Equal(2, block.Properties.Count);
        Assert.Equal("Delay", block.Properties[0].Name);
        Assert.Equal("float", block.Properties[0].Type);
        Assert.Equal("enum VFX_EffectType", block.Properties[1].Type);
    }

    [Fact]
    public void TryParse_ReadsTheTrailingUntaggedGroupBlock()
    {
        // ⚠ THE trap. Walking only the COUNTED blocks leaves exactly 91 bytes unread on every one
        // of the 119 retail files, which reads as an unexplained trailer. It is the group's own
        // property block, carrying no tag — and on retail always these three names.
        var bytes = Group(2, ("Delay", "float", [0, 0, 0, 0]));

        Assert.True(VanBurenVegFile.TryParse(bytes, "veg", out var file, out var error), error);
        Assert.Equal(2, file.Blocks.Count);
        Assert.Equal<string[]>(
            ["Loop", "MinStartTime", "MaxStartTime"],
            [.. file.GroupProperties.Select(p => p.Name)]);
    }

    [Fact]
    public void TryParse_RequiresEveryCountedBlockToCarryItsTag()
    {
        var bytes = Group(1, ("Delay", "float", [0, 0, 0, 0]));
        bytes[24] = (byte)'X';

        Assert.False(VanBurenVegFile.TryParse(bytes, "veg", out _, out var error));
        Assert.Contains("VFX V1.0", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RejectsAPropertyMissingItsSeparator()
    {
        // The 0xFFFFFFFF between a property's type and its value length is what keeps a mis-framed
        // walk from silently reading a name as a length.
        var bytes = Group(1, ("Delay", "float", [0, 0, 0, 0]));
        var at = Array.IndexOf(bytes, (byte)0xFF);
        bytes[at] = 0;

        Assert.False(VanBurenVegFile.TryParse(bytes, "veg", out _, out var error));
        Assert.Contains("separator", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RefusesBytesLeftOverAfterTheGroupBlock()
    {
        // Exact consumption is the standard of proof for this family — 119 of 119 on retail.
        var bytes = Group(1, ("Delay", "float", [0, 0, 0, 0]));
        var padded = new byte[bytes.Length + 4];
        bytes.CopyTo(padded, 0);

        Assert.False(VanBurenVegFile.TryParse(padded, "veg", out _, out var error));
        Assert.Contains("remain", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RejectsAPayloadThatIsNotAGroup()
    {
        Assert.False(VanBurenVegFile.TryParse(Encoding.Latin1.GetBytes("NOPE V1.1_______________"), "x", out _, out _));
    }
}