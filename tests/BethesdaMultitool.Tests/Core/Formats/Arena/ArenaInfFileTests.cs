using System;
using System.Linq;
using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Vectors for <see cref="ArenaInfFile" />. The decryption vector is retail ciphertext read
///     out of GLOBAL.BSA with a hex dump, paired with its known plaintext — independent of the
///     decoder under test. The grammar vectors are hand-built and mirror the shapes the retail
///     files actually use (verified across all 93 archived .INF files plus the 5 loose ones).
/// </summary>
public class ArenaInfFileTests
{
    /// <summary>
    ///     The first 16 bytes of AGTEMPL.INF as they sit inside GLOBAL.BSA, and the plaintext they
    ///     must produce. Captured by hex dump, not by this code.
    /// </summary>
    private static readonly byte[] RetailAgtemplPrefix =
    [
        0xAA, 0x3A, 0x1C, 0x8F, 0x52, 0x9C, 0x6D, 0xAD,
        0xF8, 0xAE, 0x1B, 0x8D, 0x6C, 0x9A, 0x0F, 0xE6
    ];

    private const string RetailAgtemplPlaintext = "@FLOORS\r\n*CEILIN";

    [Fact]
    public void Decrypt_RetailPrefix_ProducesTheKnownPlaintext()
    {
        var plain = ArenaInfFile.Decrypt(RetailAgtemplPrefix);

        Assert.Equal(RetailAgtemplPlaintext, System.Text.Encoding.Latin1.GetString(plain));
    }

    [Fact]
    public void Decrypt_IsItsOwnInverse()
    {
        var original = new byte[512];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = (byte)((i * 7) + 3);
        }

        Assert.Equal(original, ArenaInfFile.Decrypt(ArenaInfFile.Decrypt(original)));
    }

    [Fact]
    public void Decrypt_KeystreamRepeatsEvery256Bytes_NotEvery8()
    {
        // The key repeats every 8 bytes but the added counter wraps every 256, and 8 divides 256,
        // so the combined keystream period is 256. Decrypting zeros exposes the keystream itself:
        // blocks 8 apart must differ, blocks 256 apart must match.
        var stream = ArenaInfFile.Decrypt(new byte[256 + 8]);

        Assert.NotEqual(stream.Take(8).ToArray(), stream.Skip(8).Take(8).ToArray());
        Assert.Equal(stream.Take(8).ToArray(), stream.Skip(256).Take(8).ToArray());
    }

    [Fact]
    public void IsProbablyEncrypted_SeparatesCiphertextFromPlaintext()
    {
        Assert.True(ArenaInfFile.IsProbablyEncrypted(RetailAgtemplPrefix));
        Assert.False(ArenaInfFile.IsProbablyEncrypted(
            System.Text.Encoding.Latin1.GetBytes("@FLOORS\r\n*BOXCAP 0\r\nfloora.set  #4\r\n")));
    }

    [Fact]
    public void Parse_Encrypted_RoundTripsThroughDecryption()
    {
        const string text = "@FLOORS\n*BOXCAP 3\nfloord.set #2\n";
        var cipher = ArenaInfFile.Decrypt(System.Text.Encoding.Latin1.GetBytes(text));

        var inf = ArenaInfFile.Parse(cipher, "TEST.INF", encrypted: true);

        var floor = Assert.Single(inf.Floors);
        Assert.Equal("floord.set", floor.FileName);
        Assert.Equal(2, floor.SetSize);
        Assert.Equal([3], floor.BoxCapIds);
    }

    [Fact]
    public void ParseText_CeilingDirective_ReadsHeightScaleAndOutdoorFlag()
    {
        var inf = ArenaInfFile.ParseText("@FLOORS\n*CEILING 135 346 1\nfloora.set #4\n", "T.INF");

        var floor = Assert.Single(inf.Floors);
        Assert.Equal("floora.set", floor.FileName);
        Assert.Equal(4, floor.SetSize);
        var ceiling = floor.Ceiling;
        Assert.NotNull(ceiling);
        Assert.Equal(135, ceiling.Height);
        Assert.Equal(346, ceiling.BoxScale);
        Assert.True(ceiling.OutdoorDungeon);
        Assert.Same(ceiling, inf.Ceiling);
    }

    [Fact]
    public void ParseText_MissingFloorsHeader_StillReadsTheOpeningSection()
    {
        // DAGOTH1.INF and DAGOTH2.INF (the final staff-piece dungeon) open straight into *BOXCAP.
        var inf = ArenaInfFile.ParseText("*BOXCAP 0\n*CEILING 100\nwall.img\n", "DAGOTH1.INF");

        var floor = Assert.Single(inf.Floors);
        Assert.Equal("wall.img", floor.FileName);
        Assert.Equal([0], floor.BoxCapIds);
    }

    [Fact]
    public void ParseText_WallDirectivesStack_UntilATextureLineConsumesThem()
    {
        var inf = ArenaInfFile.ParseText(
            "@WALLS\n*BOXSIDE 0\n*DRYCHASM\ncaspit.img\n*BOXSIDE 1\n*WETCHASM\nwcas.img\n",
            "T.INF");

        Assert.Equal(2, inf.Walls.Count);
        Assert.Equal("caspit.img", inf.Walls[0].FileName);
        Assert.Equal([0], inf.Walls[0].BoxSideIds);
        Assert.Equal(ArenaInfVoxelFlags.DryChasm, inf.Walls[0].Flags);

        Assert.Equal("wcas.img", inf.Walls[1].FileName);
        Assert.Equal([1], inf.Walls[1].BoxSideIds);
        Assert.Equal(ArenaInfVoxelFlags.WetChasm, inf.Walls[1].Flags);
    }

    [Fact]
    public void ParseText_BlankLine_DiscardsAPendingDirective()
    {
        var inf = ArenaInfFile.ParseText("@WALLS\n*LEVELUP\n\ncasu.img\n", "T.INF");

        var wall = Assert.Single(inf.Walls);
        Assert.Equal(ArenaInfVoxelFlags.None, wall.Flags);
    }

    [Fact]
    public void ParseText_DoorDirective_IsKept()
    {
        // The reference discards *DOOR because its renderer reads doors from voxel data. The
        // retail files author 1,367 of these lines, so a data browser keeps them.
        var inf = ArenaInfFile.ParseText("@WALLS\n*DOOR 0\ndcas2.img\n*DOOR 3\ndcas3.img\n", "T.INF");

        Assert.Equal([0], inf.Walls[0].DoorIds);
        Assert.Equal([3], inf.Walls[1].DoorIds);
    }

    [Fact]
    public void ParseText_MenuDirective_CarriesItsId()
    {
        var inf = ArenaInfFile.ParseText("@WALLS\n*MENU 7\ndoor.img\n", "T.INF");

        Assert.Equal(7, Assert.Single(inf.Walls).MenuId);
    }

    [Fact]
    public void ParseText_FlatModifiers_DecodeIntoNamedProperties()
    {
        var inf = ArenaInfFile.ParseText(
            "@FLATS NOSHOW\nbagage1.img\tF:1\nbrazier.dfa\tS:3\ncandle.dfa\tY:12\n",
            "T.INF");

        Assert.True(inf.FlatsNoShow);
        Assert.Equal(3, inf.Flats.Count);

        Assert.Equal("BAGAGE1.IMG", inf.Flats[0].TextureName);
        Assert.Equal(1, inf.Flats[0].Properties);
        Assert.True(inf.Flats[0].Collider);
        Assert.False(inf.Flats[0].Puddle);

        Assert.Equal(3, inf.Flats[1].LightIntensity);
        Assert.Equal(12, inf.Flats[2].YOffset);
    }

    [Fact]
    public void ParseText_FlatPropertyBits_MapToTheDocumentedFlags()
    {
        var inf = ArenaInfFile.ParseText("@FLATS\nx.img F:127\n", "T.INF");

        var flat = Assert.Single(inf.Flats);
        Assert.True(flat.Collider);
        Assert.True(flat.Puddle);
        Assert.True(flat.LargeScale);
        Assert.True(flat.Dark);
        Assert.True(flat.Transparent);
        Assert.True(flat.Ceiling);
        Assert.True(flat.MediumScale);
    }

    [Fact]
    public void ParseText_FlatAfterItem_CarriesTheItemId()
    {
        var inf = ArenaInfFile.ParseText("@FLATS\nking.img\n*ITEM 1\nkey.img\nncolumn1.img F:1\n", "T.INF");

        Assert.Null(inf.Flats[0].ItemId);
        Assert.Equal(1, inf.Flats[1].ItemId);

        // The *ITEM applies to the next flat only.
        Assert.Null(inf.Flats[2].ItemId);
    }

    [Fact]
    public void ParseText_FlatNameWithSpacesAndNoModifiers_IsNotSplit()
    {
        // The *ITEM 55 case in CRYSTAL3.INF: no ':' on the line, so whitespace is not a separator.
        var inf = ArenaInfFile.ParseText("@FLATS\n*ITEM 55\nnight sky.img\n", "T.INF");

        Assert.Equal("NIGHT SKY.IMG", Assert.Single(inf.Flats).TextureName);
    }

    [Fact]
    public void ParseText_FlatNameWithLeadingDash_IsRecordedAndStripped()
    {
        var inf = ArenaInfFile.ParseText("@FLATS\n-ghost.dfa F:16\n", "T.INF");

        var flat = Assert.Single(inf.Flats);
        Assert.Equal("GHOST.DFA", flat.TextureName);
        Assert.True(flat.LeadingDash);
    }

    [Fact]
    public void ParseText_Sounds_PairFileNamesWithIds()
    {
        var inf = ArenaInfFile.ParseText("@SOUND\ndoor.voc 12\nchest.voc 4\n", "T.INF");

        Assert.Equal(new ArenaInfSound(12, "DOOR.VOC"), inf.Sounds[0]);
        Assert.Equal(new ArenaInfSound(4, "CHEST.VOC"), inf.Sounds[1]);
    }

    [Fact]
    public void ParseText_LoreText_IsGatheredUnderItsId()
    {
        var inf = ArenaInfFile.ParseText(
            "@TEXT\n*TEXT 0\nA nearby sign reads:\nAGA  NU\n*TEXT 2\nThe stench of decaying food.\n",
            "T.INF");

        Assert.Equal(2, inf.Texts.Count);
        Assert.Equal(0, inf.Texts[0].Id);
        Assert.Equal("A nearby sign reads:\nAGA  NU", inf.Texts[0].Text);
        Assert.Equal(2, inf.Texts[1].Id);
        Assert.False(inf.Texts[1].DisplayedOnce);
    }

    [Fact]
    public void ParseText_TildePrefix_MarksTextDisplayedOnce()
    {
        var inf = ArenaInfFile.ParseText("@TEXT\n*TEXT 5\n~You feel a chill.\n", "T.INF");

        var text = Assert.Single(inf.Texts);
        Assert.True(text.DisplayedOnce);
        Assert.Equal("You feel a chill.", text.Text);
    }

    [Fact]
    public void ParseText_KeyFollowedByText_KeepsBothUnderOneId()
    {
        // The AGTEMPL.INF shape: a '+key' line, then plain lore text under the same *TEXT id.
        var inf = ArenaInfFile.ParseText("@TEXT\n*TEXT 9\n+123\nThe door is locked.\n", "T.INF");

        var text = Assert.Single(inf.Texts);
        Assert.Equal(123, text.KeyId);
        Assert.Equal("The door is locked.", text.Text);
    }

    [Fact]
    public void ParseText_Riddle_SplitsQuestionAnswersAndResponses()
    {
        var inf = ArenaInfFile.ParseText(
            """
            @TEXT
            *TEXT 0
            ^3 12
            What walks on four legs?

            And then on two?
            :man
            :human
            `CORRECT
            The door swings open.
            `WRONG
            Nothing happens.

            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            "LABRNTH2.INF");

        var riddle = Assert.Single(inf.Texts).Riddle;
        Assert.NotNull(riddle);
        Assert.Equal(3, riddle.FirstNumber);
        Assert.Equal(12, riddle.SecondNumber);

        // Blank lines inside a riddle body are content, not separators.
        Assert.Equal("What walks on four legs?\n\nAnd then on two?", riddle.Riddle);
        Assert.Equal(["man", "human"], riddle.Answers);
        Assert.Equal("The door swings open.", riddle.Correct);
        Assert.Equal("Nothing happens.", riddle.Wrong);
    }

    [Fact]
    public void ParseText_UnknownSection_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ArenaInfFile.ParseText("@NOPE\nx.img\n", "T.INF"));
    }

    [Fact]
    public void ParseText_UnknownWallDirective_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => ArenaInfFile.ParseText("@WALLS\n*NOSUCH 1\nx.img\n", "BAD.INF"));

        Assert.Contains("BAD.INF", ex.Message, StringComparison.Ordinal);
    }
}
