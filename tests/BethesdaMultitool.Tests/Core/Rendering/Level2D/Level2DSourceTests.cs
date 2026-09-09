using System.Text;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Rendering.Level2D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Rendering.Level2D;

/// <summary>
///     Pins the 2D-level seam's contract on the Arena backer: only authored planes are listed, an
///     unauthored one renders null rather than throwing, and the pixels come from the same shared
///     rasterizer the PNG export writes.
/// </summary>
/// <remarks>
///     The map is built as real bytes because <c>ArenaMifLevel</c> is parse-only. The layer payload
///     reuses the hand-derived LZHUF vector from <c>ArenaMifFileTests</c> ("AAB", whose first two
///     bytes read back as voxel id 0x4141), so no encoder is needed.
/// </remarks>
public sealed class Level2DSourceTests
{
    private const int LzhufAabLength = 3;
    private static readonly byte[] LzhufAab = [0xE6, 0xE2, 0xF3, 0x80];

    private static List<byte> Chunk(string tag, IEnumerable<byte> payload)
    {
        var body = payload.ToList();
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(tag))
        {
            (byte)(body.Count & 0xFF),
            (byte)((body.Count >> 8) & 0xFF)
        };
        bytes.AddRange(body);
        return bytes;
    }

    private static List<byte> Header(int width, int depth)
    {
        var payload = new byte[ArenaMifFile.HeaderPayloadSize];
        payload[19] = 1; // one level
        payload[21] = (byte)(width & 0xFF);
        payload[22] = (byte)((width >> 8) & 0xFF);
        payload[23] = (byte)(depth & 0xFF);
        payload[24] = (byte)((depth >> 8) & 0xFF);
        return Chunk("MHDR", payload);
    }

    /// <summary>A one-voxel map whose FLOR plane is authored and whose MAP1/MAP2 are not.</summary>
    private static ArenaMifFile FloorOnlyMap()
    {
        var payload = new List<byte> { LzhufAabLength & 0xFF, (LzhufAabLength >> 8) & 0xFF };
        payload.AddRange(LzhufAab);

        var bytes = new List<byte>(Header(1, 1));
        bytes.AddRange(Chunk("LEVL", Chunk("FLOR", payload)));
        return ArenaMifFile.Parse([.. bytes], "TESTMAP.MIF");
    }

    [Fact]
    public void OnlyAuthoredPlanesAreListed()
    {
        // Most levels leave MAP2 empty, so an unauthored plane must not appear as a layer at all.
        var source = ArenaMapLevel2DSource.ForMifLevel(FloorOnlyMap(), 0);

        Assert.Equal([Level2DLayer.Floor], source.Layers);
        Assert.NotNull(source.Render(Level2DLayer.Floor));
        Assert.Null(source.Render(Level2DLayer.Walls));
        Assert.Null(source.Render(Level2DLayer.Ceiling));
    }

    [Fact]
    public void RenderMatchesTheSharedRasterizer()
    {
        var map = FloorOnlyMap();
        var floor = map.Levels[0].Floor;
        var source = ArenaMapLevel2DSource.ForMifLevel(map, 0);

        var render = Assert.NotNull(source.Render(Level2DLayer.Floor));
        var (expected, width, height, _) = VoxelLayerRasterizer.Rasterize(
            map.Width, map.Depth, 1, (x, z) => ArenaMifLevel.VoxelAt(floor, map.Width, x, z));

        Assert.Equal(width, render.Width);
        Assert.Equal(height, render.Height);
        Assert.Equal(expected, render.Rgba);
    }

    [Fact]
    public void ScaleIsAppliedToTheRenderedImage()
    {
        var source = ArenaMapLevel2DSource.ForMifLevel(FloorOnlyMap(), 0, 4);

        var render = Assert.NotNull(source.Render(Level2DLayer.Floor));

        Assert.Equal(4, render.Width);
        Assert.Equal(4, render.Height);
    }

    [Fact]
    public void DisplayNameDropsTheExtensionForASingleLevelMap()
    {
        Assert.Equal("TESTMAP", ArenaMapLevel2DSource.ForMifLevel(FloorOnlyMap(), 0).DisplayName);
    }

    [Fact]
    public void ALevelIndexOutsideTheFileIsRejected()
    {
        var map = FloorOnlyMap();

        Assert.Throws<ArgumentOutOfRangeException>(() => ArenaMapLevel2DSource.ForMifLevel(map, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArenaMapLevel2DSource.ForMifLevel(map, -1));
    }

    [Fact]
    public void ADaggerfallSourceNeedsAtLeastOneLayer()
    {
        Assert.Throws<ArgumentException>(() => new DaggerfallMapLevel2DSource(null, null));
    }

    [Theory]
    [InlineData("CITY.MIF", true)]
    [InlineData("WILD.RMD", true)]
    [InlineData("CLIMATE.PAK", true)]
    [InlineData("WOODS.WLD", true)]
    [InlineData(@"DF\DAGGER\ARENA2\woods.wld", true)]
    // Geometry, not a grid: these open in 3D and have no authored picture.
    [InlineData("ARMOR.3D", false)]
    [InlineData("L8.BS6", false)]
    [InlineData("TEXTURE.000", false)]
    // .WLD only means the heightmap when it IS the heightmap; nothing else claims the extension.
    [InlineData("OTHER.WLD", false)]
    public void SupportsAnswersWhetherAFileHasAnAuthoredPicture(string fileName, bool expected)
    {
        Assert.Equal(expected, Level2DViewPolicy.Supports(fileName));

        // A grid-authored file opens in 2D precisely because 2D is its original view.
        Assert.Equal(expected, Level2DViewPolicy.DefaultsToTwoDimensional(fileName));
    }

    [Fact]
    public void EveryLayerHasADistinctLabelAndStemSuffix()
    {
        var layers = Enum.GetValues<Level2DLayer>();

        var labels = layers.Select(Level2DViewPolicy.LayerLabel).ToList();
        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.Ordinal).Count());

        var suffixes = layers.Select(Level2DViewPolicy.LayerStemSuffix).ToList();
        Assert.Equal(suffixes.Count, suffixes.Distinct(StringComparer.Ordinal).Count());

        // The floor is the base picture, so its export keeps the bare stem.
        Assert.Equal(string.Empty, Level2DViewPolicy.LayerStemSuffix(Level2DLayer.Floor));
    }
}