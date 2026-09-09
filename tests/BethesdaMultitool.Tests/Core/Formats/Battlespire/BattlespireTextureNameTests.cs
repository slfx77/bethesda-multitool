using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Tests.Core.Formats.Xngine.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     The base-40 texture-name codec Battlespire's mesh planes use, pinned against values worked
///     out by hand from GAME.EXE's own arithmetic (place values 40^5..40^0, alphabet at 0xF4E18,
///     terminator digit 39) — NOT against the encoder, so a wrong alphabet or radix fails here.
///     <para>
///         <c>wall35</c>: w=32, a=10, l=21, l=21, 3=3, 5=5 → 32·40^5 + 10·40^4 + 21·40^3 + 21·40^2 +
///         3·40 + 5 = 3,303,777,725 = 0xC4EBA5BD. Its high word 0xC4EB is one of the two example
///         values the backlog board recorded at plane header +4 before the field was understood.
///     </para>
/// </summary>
public sealed class BattlespireTextureNameTests
{
    [Fact]
    public void TheAlphabetIsTheFortySymbolsAtF4E18()
    {
        Assert.Equal("0123456789abcdefghijklmnopqrstuvwxyz~_#%", BattlespireTextureName.Alphabet);
        Assert.Equal(40, BattlespireTextureName.Alphabet.Length);
        Assert.Equal(102_400_000u, BattlespireTextureName.FirstPlace);
        Assert.Equal(0x61A8000u, BattlespireTextureName.FirstPlace);
    }

    [Theory]
    [InlineData("wall35", 3_303_777_725u)]
    [InlineData("WALL35.BSI", 3_303_777_725u)]
    [InlineData("bok2", 1_189_124_799u)]
    [InlineData("barl01", 1_153_761_601u)]
    [InlineData("7arch", 744_147_919u)]
    [InlineData("~_#", 3_783_615_999u)]
    [InlineData("zzzzzz", 3_675_897_435u)]
    [InlineData("000000", 0u)]
    public void Encode_MatchesHandComputedKeys(string name, uint expected)
    {
        Assert.Equal(expected, BattlespireTextureName.Encode(name));
    }

    [Theory]
    [InlineData(0xC4EBA5BDu, "wall35")]
    [InlineData(0x46E09ABFu, "bok2")]
    [InlineData(0x44C50141u, "barl01")]
    [InlineData(0x2C5ACBCFu, "7arch")]
    [InlineData(0u, "000000")]
    public void Decode_ReadsTheStemBackFromTheKey(uint key, string expected)
    {
        Assert.Equal(expected, BattlespireTextureName.Decode(key));
    }

    [Fact]
    public void AKeyOfSixTerminatorsNamesNothing()
    {
        // 39 in every place = 0xF423FFFF: a legal encoding of the empty name, which the game
        // would turn into ".bsi". It resolves to no texture rather than to "".
        Assert.Equal(0xF423FFFFu, BattlespireTextureName.Encode(string.Empty));
        Assert.Null(BattlespireTextureName.Decode(0xF423FFFFu));
    }

    [Fact]
    public void AKeyWhoseLeadingDigitIsPastTheAlphabetIsNotAName()
    {
        // 40^6 = 0xF4240000 has leading digit 40, which no name produces and the alphabet cannot index.
        Assert.Null(BattlespireTextureName.Decode(0xF4240000u));
        Assert.False(BattlespireTextureName.IsSolidColor(0xF4240000u));
    }

    [Theory]
    [InlineData("a%b")]
    [InlineData("a b")]
    [InlineData("wall-1")]
    public void Encode_RefusesTheCharactersTheGameCallsIllegal(string name)
    {
        Assert.Throws<ArgumentException>(() => BattlespireTextureName.Encode(name));
    }

    [Fact]
    public void Encode_TruncatesToSixCharactersLikeTheGame()
    {
        // BSI.BSA holds KEYHOLE3/KEYHOLE4; a mesh asking for "keyhol" is what a longer name encodes to.
        Assert.Equal(BattlespireTextureName.Encode("keyhol"), BattlespireTextureName.Encode("keyhole4"));
    }

    [Theory]
    [InlineData(0xFFFFFFFEu, true, 255, 255, 255)]
    [InlineData(0xFFFF632Au, true, 99, 99, 173)]
    [InlineData(0xFFF00000u, true, 0, 0, 0)]
    [InlineData(0xFFEFFFFFu, false, 0, 0, 0)]
    public void SolidColourKeys_StartAtFFF00000_AndCarryA15BitColourShiftedLeftOnce(
        uint key, bool solid, int r, int g, int b)
    {
        // 0xFFFF632A: low word 0x632A >> 1 = 0x3195 → R 12, G 12, B 21 → 99, 99, 173 after 5-to-8-bit widening.
        Assert.Equal(solid, BattlespireTextureName.IsSolidColor(key));
        if (solid)
        {
            Assert.Null(BattlespireTextureName.Decode(key));
            Assert.Equal(((byte)r, (byte)g, (byte)b), BattlespireTextureName.SolidColor(key));
        }
    }

    [Fact]
    public void RoundTrip_HoldsForEveryLegalDigitInEveryPlace()
    {
        for (var place = 0; place < BattlespireTextureName.MaxLength; place++)
        {
            for (var digit = 0; digit < BattlespireTextureName.Terminator; digit++)
            {
                var name = new string('0', place) + BattlespireTextureName.Alphabet[digit];
                Assert.Equal(name, BattlespireTextureName.Decode(BattlespireTextureName.Encode(name)));
            }
        }
    }

    [Fact]
    public void ABattlespirePlane_CarriesTheWholeDwordAsItsKey_SplitIntoHighAndLowWords()
    {
        var bytes = XnGineMeshFixture.Build("v2.7",
            [(0, 0, 0), (256, 0, 0), (256, -512, 0), (0, -512, 0)],
            [
                new XnGineMeshFixture.Plane(0, [(0, 16, 32), (1, 8, 0), (2, 0, 8)], (0, 0, -256),
                    TextureKey: 0xC4EBA5BD),
                new XnGineMeshFixture.Plane(0, [(2, 0, 0), (3, 0, 0), (0, 0, 0), (1, 0, 0)], (0, 0, 256),
                    TextureKey: 0xFFFFFFFE)
            ],
            layout: XnGineMeshLayout.Battlespire);

        var mesh = XnGineMesh.Parse(bytes, 0, XnGineMeshLayout.Battlespire);

        var wall = mesh.Planes[0];
        Assert.Equal(0xC4EBA5BDu, wall.TextureKey);
        Assert.Equal((0xC4EB, 0xA5BD), (wall.TextureArchive, wall.TextureRecord));
        // The low word is what a Daggerfall-shaped reader saw as the whole reference.
        Assert.Equal(0xA5BD, wall.TextureBits);
        Assert.Equal(6, wall.HeaderTail.Length);
        Assert.Equal("wall35", BattlespireTextureResolver.NameOf(wall.TextureArchive, wall.TextureRecord));

        var colour = mesh.Planes[1];
        Assert.Equal(0xFFFFFFFEu, colour.TextureKey);
        Assert.Equal((0xFFFF, 0xFFFE), (colour.TextureArchive, colour.TextureRecord));
        Assert.Null(BattlespireTextureResolver.NameOf(colour.TextureArchive, colour.TextureRecord));

        Assert.Equal([(0xC4EB, 0xA5BD), (0xFFFF, 0xFFFE)], mesh.UniqueTextures);

        // The decomposer keys its sub-meshes by the same pair, so a resolver can rebuild the key.
        var decomposed = XnGineMeshDecomposer.Decompose(mesh);
        Assert.Equal(2, decomposed.SubMeshes.Count);
        Assert.Equal(0xC4EBA5BDu,
            BattlespireTextureResolver.Key(decomposed.SubMeshes[0].TextureArchive, decomposed.SubMeshes[0].TextureRecord));
    }

    [Fact]
    public void ADaggerfallPlane_StillSplitsItsU16TheDaggerfallWay()
    {
        var bytes = XnGineMeshFixture.Build("v2.7",
            [(0, 0, 0), (256, 0, 0), (256, -512, 0)],
            [new XnGineMeshFixture.Plane(XnGineMeshFixture.Texture(24, 3), [(0, 0, 0), (1, 0, 0), (2, 0, 0)], (0, 0, -256))]);

        var plane = XnGineMesh.Parse(bytes, 1).Planes[0];

        Assert.Equal((24, 3), (plane.TextureArchive, plane.TextureRecord));
        Assert.Equal(plane.TextureBits, plane.TextureKey);
    }
}
