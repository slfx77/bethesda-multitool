using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Localization;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Localization;

/// <summary>
///     Deterministic coverage for the Bethesda string-table parser used to resolve localized
///     plugin text (Skyrim/FO4/Starfield). Builds the wire format by hand so the test needs no
///     game install. Regression: localized FULL names previously decoded to a single garbage
///     character because the 4-byte string ID was read as an inline zstring.
/// </summary>
public class LocalizedStringTablesTests
{
    [Fact]
    public void ParseTable_NullTerminatedStrings_ResolvesById()
    {
        var table = LocalizedStringTables.ParseTable(
            BuildTable(false, (1u, "Skyrim"), (2u, "Whiterun")),
            false);

        Assert.Equal("Skyrim", table[1]);
        Assert.Equal("Whiterun", table[2]);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void ParseTable_LengthPrefixedStrings_ResolvesById()
    {
        var table = LocalizedStringTables.ParseTable(
            BuildTable(true, (10u, "A short description."), (11u, "Another one.")),
            true);

        Assert.Equal("A short description.", table[10]);
        Assert.Equal("Another one.", table[11]);
    }

    [Fact]
    public void ParseTable_TruncatedBuffer_ReturnsEmptyWithoutThrowing()
    {
        Assert.Empty(LocalizedStringTables.ParseTable([0x01, 0x02], false));
    }

    private static byte[] BuildTable(bool lengthPrefixed, params (uint Id, string Text)[] entries)
    {
        var data = new List<byte>();
        var directory = new List<(uint Id, uint Offset)>();
        foreach (var (id, text) in entries)
        {
            directory.Add((id, (uint)data.Count));
            var bytes = Encoding.ASCII.GetBytes(text);
            if (lengthPrefixed)
            {
                data.AddRange(BitConverter.GetBytes((uint)(bytes.Length + 1))); // length includes null
            }

            data.AddRange(bytes);
            data.Add(0);
        }

        var result = new List<byte>();
        result.AddRange(BitConverter.GetBytes((uint)entries.Length)); // count
        result.AddRange(BitConverter.GetBytes((uint)data.Count)); // dataSize
        foreach (var (id, offset) in directory)
        {
            result.AddRange(BitConverter.GetBytes(id));
            result.AddRange(BitConverter.GetBytes(offset));
        }

        result.AddRange(data);
        return result.ToArray();
    }
}