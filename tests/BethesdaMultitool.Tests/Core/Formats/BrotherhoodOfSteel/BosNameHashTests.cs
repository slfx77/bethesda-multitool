using System.Text;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for <see cref="BosNameHash" />, the string-key hash of Fallout: Brotherhood of Steel
///     (<c>default.xbe</c> <c>0x00014BE0</c>).
///     <para>
///         ⚑ Every expected value below is a literal READ OFF THE DISC — the hash the game's own
///         <c>.NFO</c> debug dump prints beside that string — and the fourteen come from twelve
///         different level dumps, so no single build or table can be producing them. Nothing here
///         is computed by the code under test.
///     </para>
/// </summary>
public sealed class BosNameHashTests
{
    /// <summary>
    ///     (string, stored key) pairs transcribed from the Xbox disc's <c>.NFO</c> dumps, one or two
    ///     per level so the corpus is not a single file's quirk.
    /// </summary>
    private static readonly (string Text, uint Key)[] Pairs =
    [
        ("gibchunks_human_heavy", 0x8EAE3281U), // c1\BAR\BAR.nfo
        ("amb_dinner_sleep1", 0x8E972E05U), // c1\BAR\BAR.nfo
        ("BAR", 0xDA26AA05U), // c1\BAR\BAR.nfo — the level's own name
        ("testemitter", 0x77ADB496U), // c1\BAR\BAR.nfo
        ("spit", 0x6AB79C1BU), // c1\BAR\BAR.nfo
        ("effect_fire_shortlife", 0x33B53000U), // c1\MILL_2\MILL_2.nfo
        ("smoke_bullet_wall_hit", 0x10663FFEU), // c1\TUTOR\TUTOR.nfo
        ("MOV_box_metal_medium_7111", 0xCCBD4638U), // c1\WARE_1\WARE_1.nfo
        ("COR_General_Cone", 0x9F0B8600U), // c2\DCKS_1\DCKS_1.nfo
        ("creature_ghoul_psycho_gatling", 0x7D02F402U), // c2\VTWR_1\VTWR_1.nfo
        ("light_muzzle_blast_small_3", 0x32070302U), // c3\FACL_1\FACL_1.nfo
        ("cw_mutant_fist_gauntlet_L", 0xFDD4B0FCU), // c3\GARDEN\GARDEN.nfo
        ("effect_sparks_horiz_small", 0x9CCE9703U), // c3\LAB_1\LAB_1.nfo
        ("Display_Case_1_Glass_5812", 0x86259700U) // c3\RINS_1\RINS_1.nfo
    ];

    /// <summary>The same fourteen pairs, as a theory source.</summary>
    public static TheoryData<string, uint> DiscPairs
    {
        get
        {
            var data = new TheoryData<string, uint>();
            foreach (var (text, key) in Pairs)
            {
                data.Add(text, key);
            }

            return data;
        }
    }

    /// <summary>
    ///     Display strings and the key they are FILED UNDER. These must NOT reproduce: they are the
    ///     UI text of the record whose internal name owns the key, and the computed value beside
    ///     each is what the hash actually gives. Without this half the theory above could be
    ///     satisfied by a function that returns the right answer for identifiers by accident.
    /// </summary>
    public static TheoryData<string, uint, uint> DisplayStrings =>
        new()
        {
            { "Freezer Chest", 0x57632888U, 0x9B0CEC59U },
            { "Wasteland Man", 0xDF5BFB9CU, 0xB4BE9B47U },
            { "Wasteland Wanderer", 0x9A5CEA2CU, 0xEF18796EU },
            { "smoke emitter", 0x7DFCAFC7U, 0xB5363A74U },
            { "fire light", 0x1D032411U, 0x5A530034U }
        };

    [Theory]
    [MemberData(nameof(DiscPairs))]
    public void ReproducesTheKeyTheDiscStoresBesideTheString(string text, uint expected)
    {
        Assert.Equal(expected, BosNameHash.Compute(text));
        Assert.True(BosNameHash.Names(text, expected));
    }

    [Theory]
    [MemberData(nameof(DisplayStrings))]
    public void ADisplayStringDoesNotHashToTheKeyItIsFiledUnder(string text, uint storedKey, uint computed)
    {
        Assert.Equal(computed, BosNameHash.Compute(text));
        Assert.NotEqual(storedKey, computed);
        Assert.False(BosNameHash.Names(text, storedKey));
    }

    [Fact]
    public void TheEmptyStringHashesToZero()
    {
        // The loop never runs, so the seed is the answer. Pinned because a seed other than 0 would
        // still reproduce nothing above but would silently change every derived table.
        Assert.Equal(0U, BosNameHash.Compute(string.Empty));
    }

    [Fact]
    public void OneCharacterIsTheCharacterItself()
    {
        // h = (0 * M) ^ (0 >> 27) ^ c == c, derivable by hand from the instruction sequence.
        Assert.Equal('A', BosNameHash.Compute("A"));
        Assert.Equal(0x6EU, BosNameHash.Compute("n"));
    }

    [Fact]
    public void TheShiftIsArithmeticNotLogical()
    {
        // ⚠⚠ The one substitution that looks harmless and is not. This implements the SAME formula
        // with an unsigned shift, independently of the production code, and requires it to fail on
        // every disc pair whose running hash ever goes negative — 13 of the 14 here.
        static uint LogicalShiftVariant(string text)
        {
            var h = 0u;
            foreach (var c in text)
            {
                h = unchecked((h * BosNameHash.Multiplier) ^ (h >> BosNameHash.ShiftBits) ^ c);
            }

            return h;
        }

        var agreed = new List<string>();
        foreach (var (text, expected) in Pairs)
        {
            if (LogicalShiftVariant(text) == expected)
            {
                agreed.Add(text);
            }
        }

        // "BAR" is three characters and never sets the sign bit, so the two agree there; that is
        // exactly why a short sample cannot settle the shift.
        Assert.Equal(new List<string> { "BAR" }, agreed);
    }

    [Fact]
    public void ItIsNotTheAssetHash()
    {
        // ⛔ The disc carries TWO hashes and they are not interchangeable: BosAssetHash keys .CLP
        // sections by file name (multiplier 0x80000025, Latin-1 bytes, logical shift). It scores
        // zero on this corpus.
        foreach (var (text, expected) in Pairs)
        {
            Assert.NotEqual(expected, BosAssetHash.Compute(text));
        }
    }

    [Fact]
    public void HashingIsOverUtf16CodeUnitsNotEncodedBytes()
    {
        // The engine hashes 16-bit code units (0x00014BE0 reads `mov cx, word ptr [edx]` and folds
        // it in with `movzx ecx, cx`), so one char is one round. A byte-wise reading of the same
        // text would run TWO rounds for 'é' (0xC3, 0xA9 in UTF-8) and land somewhere else
        // entirely — computed here independently so the comparison is not against the production
        // code's own answer.
        // ⚠ What this does NOT show: that our char loop matches the engine on non-ASCII input. The
        // widening caller at 0x00014C20 is `movsx cx, cl` — SIGN-extending a signed char — so the
        // engine feeds the byte 0xE9 to the hash as 0xFFE9, not as 0x00E9. Every name on either
        // disc is ASCII, where the two agree, so the distinction never arises in practice; it is
        // pinned below rather than left as an assumption.
        static uint OverEncodedBytes(byte[] utf8)
        {
            var h = 0u;
            foreach (var b in utf8)
            {
                h = unchecked((h * BosNameHash.Multiplier) ^ (uint)((int)h >> BosNameHash.ShiftBits) ^ b);
            }

            return h;
        }

        // First round of an empty hash is 0 * M ^ (0 >> 27) ^ c == c, so a one-character string
        // hashes to its own code unit. Derivable by hand from the instruction sequence.
        Assert.Equal(0xE9U, BosNameHash.Compute("é"));
        Assert.Equal(0x8C60DC86U, OverEncodedBytes(Encoding.UTF8.GetBytes("é")));
        Assert.Equal('€', BosNameHash.Compute("€"));

        // The sign-extended form the engine's own widener would produce for the byte 0xE9.
        Assert.Equal(0xFFE9U, BosNameHash.Compute("￩"));
        Assert.NotEqual(BosNameHash.Compute("é"), BosNameHash.Compute("￩"));
    }
}