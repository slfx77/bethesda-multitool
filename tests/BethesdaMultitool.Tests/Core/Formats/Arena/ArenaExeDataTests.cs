using System.Text;
using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Synthetic vectors for the province table inside Arena's unpacked <c>A.EXE</c>.
///     <para>
///         The reading that matters is that the four u16 fields are a RECTANGLE
///         (x, y, width, height) and not a bounding box (left, top, right, bottom) — both parse,
///         and the wrong one puts several provinces at absurd sizes while still producing nine
///         plausible-looking rows.
///     </para>
/// </summary>
public sealed class ArenaExeDataTests
{
    private static byte[] Record(int x, int y, int w, int h, string name)
    {
        var record = new byte[ArenaExeData.ProvinceRecordLength];

        void Put(int at, int v)
        {
            record[at] = (byte)v;
            record[at + 1] = (byte)(v >> 8);
        }

        Put(0, x);
        Put(2, y);
        Put(4, w);
        Put(6, h);
        Encoding.ASCII.GetBytes(name).CopyTo(record, ArenaExeData.ProvinceNameOffset);
        return record;
    }

    /// <summary>Builds an image whose first province is the anchor name, preceded by filler.</summary>
    private static byte[] Image(params byte[][] records)
    {
        var filler = Encoding.ASCII.GetBytes("filler text that is not a province table");
        return [.. filler, .. records.SelectMany(r => r)];
    }

    private static byte[] RetailShaped()
    {
        return Image(
            Record(37, 32, 86, 57, "High Rock"),
            Record(47, 53, 90, 62, "Hammerfell"),
            Record(113, 29, 88, 53, "Skyrim"),
            Record(190, 31, 102, 93, "Morrowind"),
            Record(31, 131, 65, 52, "Summerset Isle"),
            Record(100, 118, 61, 55, "Valenwood"),
            Record(144, 119, 50, 57, "Elsweyr"),
            Record(204, 116, 67, 67, "Black Marsh"),
            Record(103, 72, 131, 84, "Imperial Province"));
    }

    [Fact]
    public void TryReadProvinces_ReadsAllNineInOrder()
    {
        var provinces = ArenaExeData.TryReadProvinces(RetailShaped());

        Assert.NotNull(provinces);
        Assert.Equal(ArenaExeData.ProvinceCount, provinces.Count);
        Assert.Equal(
            [
                "High Rock", "Hammerfell", "Skyrim", "Morrowind", "Summerset Isle", "Valenwood", "Elsweyr",
                "Black Marsh", "Imperial Province"
            ],
            provinces.Select(p => p.Name));
    }

    [Fact]
    public void TryReadProvinces_ReadsTheFieldsAsWidthAndHeightNotRightAndBottom()
    {
        var provinces = ArenaExeData.TryReadProvinces(RetailShaped())!;
        var highRock = provinces[0];

        Assert.Equal((37, 32, 86, 57), (highRock.X, highRock.Y, highRock.Width, highRock.Height));

        // Read as (left, top, right, bottom) the third field would BE the right edge, 86. It is
        // not: the right edge is x + width.
        Assert.Equal(123, highRock.Right);
        Assert.Equal(89, highRock.Bottom);
    }

    [Fact]
    public void TryReadProvinces_EveryRectangleFitsTheWorldMap()
    {
        var provinces = ArenaExeData.TryReadProvinces(RetailShaped())!;

        Assert.All(provinces, p => Assert.True(p.Right <= ArenaExeData.WorldMapWidth));
        Assert.All(provinces, p => Assert.True(p.Bottom <= ArenaExeData.WorldMapHeight));
    }

    [Fact]
    public void TryReadProvinces_PlacesTamrielGeographically()
    {
        // The check that the offset is genuinely right rather than merely self-consistent: these
        // relationships are facts about Tamriel, not about the byte layout.
        var byName = ArenaExeData.TryReadProvinces(RetailShaped())!.ToDictionary(p => p.Name);

        Assert.True(byName["High Rock"].X < byName["Skyrim"].X, "High Rock is west of Skyrim");
        Assert.True(byName["Skyrim"].X < byName["Morrowind"].X, "Skyrim is west of Morrowind");
        Assert.True(byName["Skyrim"].Y < byName["Elsweyr"].Y, "Skyrim is north of Elsweyr");
        Assert.True(byName["Summerset Isle"].X < byName["Black Marsh"].X, "Summerset is west of Black Marsh");
    }

    [Fact]
    public void TryReadProvinces_DisagreesWithCityDataAndThatIsExpected()
    {
        // ⚠ Pinned so nobody "fixes" one table to match the other. The executable and CITYDATA
        // carry INDEPENDENT province rectangles: all nine differ on retail, and CITYDATA even
        // spells it "Summurset Isle". Which the game draws with is not established.
        var fromExe = ArenaExeData.TryReadProvinces(RetailShaped())!;

        Assert.Equal((37, 32, 86, 57), (fromExe[0].X, fromExe[0].Y, fromExe[0].Width, fromExe[0].Height));
        Assert.Equal("Summerset Isle", fromExe[4].Name);
    }

    [Fact]
    public void TryReadProvinces_WithNoAnchor_ReturnsNull()
    {
        Assert.Null(ArenaExeData.TryReadProvinces(Encoding.ASCII.GetBytes("no province table here")));
    }

    [Fact]
    public void TryReadProvinces_RejectsARectangleOutsideTheWorldMap()
    {
        // A table found at the wrong address yields rows that do not fit the map; that must be a
        // rejection rather than nine plausible provinces.
        var image = Image(
            Record(37, 32, 86, 57, "High Rock"),
            Record(600, 53, 90, 62, "Hammerfell"),
            Record(113, 29, 88, 53, "Skyrim"),
            Record(190, 31, 102, 93, "Morrowind"),
            Record(31, 131, 65, 52, "Summerset Isle"),
            Record(100, 118, 61, 55, "Valenwood"),
            Record(144, 119, 50, 57, "Elsweyr"),
            Record(204, 116, 67, 67, "Black Marsh"),
            Record(103, 72, 131, 84, "Imperial Province"));

        Assert.Null(ArenaExeData.TryReadProvinces(image));
    }

    [Fact]
    public void TryReadProvinces_RejectsAZeroSizedProvince()
    {
        var image = Image(
            Record(37, 32, 86, 57, "High Rock"),
            Record(47, 53, 0, 62, "Hammerfell"),
            Record(113, 29, 88, 53, "Skyrim"),
            Record(190, 31, 102, 93, "Morrowind"),
            Record(31, 131, 65, 52, "Summerset Isle"),
            Record(100, 118, 61, 55, "Valenwood"),
            Record(144, 119, 50, 57, "Elsweyr"),
            Record(204, 116, 67, 67, "Black Marsh"),
            Record(103, 72, 131, 84, "Imperial Province"));

        Assert.Null(ArenaExeData.TryReadProvinces(image));
    }

    [Fact]
    public void TryReadProvinces_TruncatedTable_ReturnsNull()
    {
        var full = RetailShaped();
        Assert.Null(ArenaExeData.TryReadProvinces(full.AsSpan(0, full.Length - 20)));
    }
}