using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Enums;

/// <summary>
///     Pins <see cref="RecordHeaderFlagRegistry" /> against xEdit's per-signature record flag lists
///     (<c>wbDefinitionsFNV.pas</c> / <c>wbDefinitionsFO3.pas</c>): at least one bit of every New Vegas
///     list, plus the controls that a signature-less or game-less table would fail — QUST bit 11 has no
///     meaning (it is Initially Disabled only on placed references and NAVM), INFO bit 13 is xEdit's
///     "Unknown 13", and Fallout 3 has no TREE list while New Vegas names TREE bit 6.
/// </summary>
public sealed class RecordHeaderFlagRegistryTests
{
    [Theory]
    [InlineData("ACHR", 10, "Persistent")]
    [InlineData("ACHR", 11, "Initially Disabled")]
    [InlineData("ACHR", 25, "No AI Acquire")]
    [InlineData("ACRE", 15, "Visible When Distant")]
    [InlineData("ACTI", 17, "Dangerous")]
    [InlineData("ALCH", 10, "Quest Item")]
    [InlineData("ARMO", 19, "Has Platform Specific Textures")]
    [InlineData("BOOK", 10, "Quest Item")]
    [InlineData("CELL", 10, "Persistent")]
    [InlineData("CELL", 17, "Off Limits")]
    [InlineData("CELL", 19, "Can't Wait")]
    [InlineData("CONT", 30, "Navmesh - Ground")]
    [InlineData("CREA", 10, "Quest Item")]
    [InlineData("DOOR", 16, "Random Anim Start")]
    [InlineData("FURN", 29, "Child Can Use")]
    [InlineData("GLOB", 6, "Constant")]
    [InlineData("IDLM", 29, "Child Can Use")]
    [InlineData("KEYM", 10, "Quest Item")]
    [InlineData("LAND", 18, "Compressed")]
    [InlineData("LIGH", 25, "Obstacle")]
    [InlineData("LSCR", 10, "Displays In Main Menu")]
    [InlineData("MISC", 10, "Quest Item")]
    [InlineData("MSTT", 9, "On Local Map")]
    [InlineData("NAVM", 11, "Initially Disabled")]
    [InlineData("NAVM", 26, "AutoGen")]
    [InlineData("NPC_", 10, "Quest Item")]
    [InlineData("PGRE", 11, "Initially Disabled")]
    [InlineData("REFR", 6, "Hidden From Local Map")]
    [InlineData("REFR", 15, "Visible When Distant")]
    [InlineData("REFR", 16, "High Priority LOD")]
    [InlineData("REFR", 29, "Refracted by Auto Water")]
    [InlineData("REFR", 31, "Multibound")]
    [InlineData("REGN", 6, "Border Region")]
    [InlineData("SCOL", 27, "Navmesh - Bounding Box")]
    [InlineData("STAT", 26, "Navmesh - Filter")]
    [InlineData("TACT", 13, "No Voice Filter")]
    [InlineData("TACT", 28, "Non-Pipboy")]
    [InlineData("TERM", 16, "Random Anim Start")]
    [InlineData("TES4", 0, "ESM")]
    [InlineData("TES4", 4, "Optimized")]
    [InlineData("TREE", 6, "Has Tree LOD")]
    [InlineData("WEAP", 10, "Quest Item")]
    [InlineData("WRLD", 19, "Can't Wait")]
    public void RecordHeaderFlagRegistry_FalloutNewVegas_NamesBitsPerSignature(string signature, int bit,
        string expected)
    {
        Assert.Equal(expected, RecordHeaderFlagRegistry.GetName(BethesdaGame.FalloutNewVegas, signature, bit));
    }

    [Theory]
    [InlineData("QUST", 5, "Deleted")]
    [InlineData("QUST", 12, "Ignored")]
    [InlineData("QUST", 18, "Compressed")]
    [InlineData("INFO", 12, "Ignored")]
    [InlineData("REFR", 5, "Deleted")]
    [InlineData("REFR", 18, "Compressed")]
    [InlineData("TES4", 12, "Ignored")]
    public void GetName_FalloutNewVegas_CommonBitsAreNamedOnEverySignature(string signature, int bit,
        string expected)
    {
        Assert.Equal(expected, RecordHeaderFlagRegistry.GetName(BethesdaGame.FalloutNewVegas, signature, bit));
    }

    [Theory]
    [InlineData("QUST", 11)] // Initially Disabled only on placed references and NAVM: a flat table fails here
    [InlineData("QUST", 10)] // Persistent / Quest Item are per signature too
    [InlineData("INFO", 13)] // xEdit "Unknown 13"
    [InlineData("WEAP", 27)] // xEdit "Unknown 27"
    [InlineData("NPC_", 19)] // xEdit "Unknown 19"
    [InlineData("CREA", 29)] // xEdit "Unknown 29"
    [InlineData("PACK", 27)] // xEdit "Unknown 27"
    [InlineData("PROJ", 27)] // xEdit "Unknown 27"
    [InlineData("ALCH", 29)] // xEdit "Unknown 29"
    [InlineData("ACHR", 6)] // REFR's Hidden From Local Map; ACHR has no bit 6
    [InlineData("TES4", 1)]
    public void GetName_FalloutNewVegas_BitWithoutMeaningOnThatSignature_IsNull(string signature, int bit)
    {
        Assert.Null(RecordHeaderFlagRegistry.GetName(BethesdaGame.FalloutNewVegas, signature, bit));
    }

    [Fact]
    public void GetName_Fallout3_DiffersFromNewVegasExactlyWhereXEditDoes()
    {
        // Fallout 3 declares no TREE flag list; New Vegas names TREE bit 6.
        Assert.Null(RecordHeaderFlagRegistry.GetName(BethesdaGame.Fallout3, "TREE", 6));
        Assert.Equal("Has Tree LOD", RecordHeaderFlagRegistry.GetName(BethesdaGame.FalloutNewVegas, "TREE", 6));

        // NAVM bit 26 is spelled differently by the two definitions.
        Assert.Equal("Autogen", RecordHeaderFlagRegistry.GetName(BethesdaGame.Fallout3, "NAVM", 26));
        Assert.Equal("AutoGen", RecordHeaderFlagRegistry.GetName(BethesdaGame.FalloutNewVegas, "NAVM", 26));

        // Everything else is shared.
        Assert.Equal("Initially Disabled", RecordHeaderFlagRegistry.GetName(BethesdaGame.Fallout3, "ACHR", 11));
        Assert.Equal("Multibound", RecordHeaderFlagRegistry.GetName(BethesdaGame.Fallout3, "REFR", 31));
        Assert.Equal("Can't Wait", RecordHeaderFlagRegistry.GetName(BethesdaGame.Fallout3, "WRLD", 19));
        Assert.Null(RecordHeaderFlagRegistry.GetName(BethesdaGame.Fallout3, "QUST", 11));
    }

    [Fact]
    public void GetName_OtherTes4Games_NameTheCommonBitsOnly()
    {
        Assert.Equal("Deleted", RecordHeaderFlagRegistry.GetName(BethesdaGame.Skyrim, "ACHR", 5));
        Assert.Equal("Compressed", RecordHeaderFlagRegistry.GetName(BethesdaGame.Skyrim, "NPC_", 18));
        Assert.Equal("Ignored", RecordHeaderFlagRegistry.GetName(BethesdaGame.Oblivion, "REFR", 12));

        // No per-signature table is transcribed for these games, and New Vegas's is never borrowed.
        Assert.Null(RecordHeaderFlagRegistry.GetName(BethesdaGame.Skyrim, "TREE", 6));
        Assert.Null(RecordHeaderFlagRegistry.GetName(BethesdaGame.Oblivion, "ACHR", 11));

        // The Unknown profile is TES4-framed (24-byte headers), so it keeps the common bits.
        Assert.Equal("Compressed", RecordHeaderFlagRegistry.GetName(BethesdaGame.Unknown, "REFR", 18));
    }

    [Fact]
    public void GetName_GamesWithoutTes4RecordHeaders_NameNothing()
    {
        // Morrowind's 16-byte TES3 header has no compression bit; the pre-plugin games have no records.
        Assert.Null(RecordHeaderFlagRegistry.GetName(BethesdaGame.Morrowind, "CELL", 18));
        Assert.Null(RecordHeaderFlagRegistry.GetName(BethesdaGame.Arena, "CELL", 5));
        Assert.Equal(
            new[] { "bit 18 (0x00040000)" },
            RecordHeaderFlagRegistry.DescribeSetBits(BethesdaGame.Morrowind, "CELL", 0x00040000u));
    }

    [Fact]
    public void DescribeSetBits_UnnamedBit_RendersBitNumberAndMask()
    {
        Assert.Equal(
            new[] { "bit 13 (0x00002000)" },
            RecordHeaderFlagRegistry.DescribeSetBits(BethesdaGame.FalloutNewVegas, "INFO", 0x00002000u));
        Assert.Equal(
            new[] { "bit 11 (0x00000800)" },
            RecordHeaderFlagRegistry.DescribeSetBits(BethesdaGame.FalloutNewVegas, "QUST", 0x00000800u));
    }

    [Fact]
    public void DescribeSetBits_ListsEverySetBitLowestFirst()
    {
        // ACHR 0x000E739E: 0x00000400 in the 2010-9-16 Steam Disc master, 0x00000C00 in the 2022 master
        // (header bytes read directly; the 40-byte payloads are identical).
        Assert.Equal(
            new[] { "Persistent" },
            RecordHeaderFlagRegistry.DescribeSetBits(BethesdaGame.FalloutNewVegas, "ACHR", 0x00000400u));
        Assert.Equal(
            new[] { "Persistent", "Initially Disabled" },
            RecordHeaderFlagRegistry.DescribeSetBits(BethesdaGame.FalloutNewVegas, "ACHR", 0x00000C00u));

        Assert.Equal(
            new[] { "Deleted", "Persistent", "Initially Disabled", "bit 13 (0x00002000)", "Compressed", "Multibound" },
            RecordHeaderFlagRegistry.DescribeSetBits(BethesdaGame.FalloutNewVegas, "REFR", 0x80042C20u));

        Assert.Empty(RecordHeaderFlagRegistry.DescribeSetBits(BethesdaGame.FalloutNewVegas, "REFR", 0u));
    }

    [Theory]
    [InlineData(5, "Deleted")]
    [InlineData(10, "Persistent")]
    [InlineData(11, "Initially Disabled")]
    [InlineData(18, "Compressed")]
    public void GetName_AgreesWithDetectedMainRecordPredicates(int bit, string expected)
    {
        var record = new DetectedMainRecord("ACHR", 40, 1u << bit, 0x000E739E, 0, false);
        var predicate = expected switch
        {
            "Deleted" => record.IsDeleted,
            "Persistent" => record.IsPersistent,
            "Initially Disabled" => record.IsInitiallyDisabled,
            "Compressed" => record.IsCompressed,
            _ => false
        };

        Assert.True(predicate);
        Assert.Equal(expected, RecordHeaderFlagRegistry.GetName(BethesdaGame.FalloutNewVegas, "ACHR", bit));
        Assert.Equal(expected, RecordHeaderFlagRegistry.GetName(BethesdaGame.Fallout3, "ACHR", bit));
    }

    [Fact]
    public void GetName_IgnoredAgreesWithMainRecordHeaderPredicate()
    {
        var header = new MainRecordHeader { Signature = "QUST", Flags = 0x00001000 };

        Assert.True(header.IsIgnored);
        Assert.Equal("Ignored", RecordHeaderFlagRegistry.GetName(BethesdaGame.FalloutNewVegas, "QUST", 12));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    public void GetName_BitOutsideTheU32_Throws(int bit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecordHeaderFlagRegistry.GetName(BethesdaGame.FalloutNewVegas, "REFR", bit));
    }
}
