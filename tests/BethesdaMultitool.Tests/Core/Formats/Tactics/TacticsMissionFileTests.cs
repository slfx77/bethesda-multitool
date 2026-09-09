using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Vectors for Fallout Tactics <c>.mis</c> missions, shaped after all <b>128</b> shipped files
///     — 103 inside the <c>mis-*.bos</c> archives and 25 loose on disk, re-counted 2026-09-07 —
///     versions "68" (98) and "69" (30), every one inflating to exactly its declared size.
/// </summary>
public sealed class TacticsMissionFileTests
{
    private static byte[] Mission(string version = "68", byte[]? world = null, int declaredBias = 0,
        int repeatedBias = 0)
    {
        world ??= [.. "<mph>"u8, 0, (byte)'8', 0, 1, 2, 3, 4];

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionMode.Compress, true))
        {
            deflate.Write(world);
        }

        var b = new List<byte>();
        b.AddRange("<world>"u8);
        b.Add(0);
        b.AddRange(Encoding.ASCII.GetBytes(version));
        b.Add(0);
        b.AddRange(BitConverter.GetBytes((uint)(world.Length + declaredBias)));
        b.AddRange(BitConverter.GetBytes((uint)(world.Length + repeatedBias)));
        b.AddRange(compressed.ToArray());
        return [.. b];
    }

    private static byte[] World(params string[] teams)
    {
        return World(false, teams);
    }

    /// <summary>
    ///     A world whose roster names are written ASCII (<paramref name="wide" /> false, 127 of the
    ///     128 shipped missions) or UTF-16LE with bit 31 set in the length prefix (true — what
    ///     <c>core/editor/ambientExample.mis</c> does).
    /// </summary>
    private static byte[] World(bool wide, params string[] teams)
    {
        var w = new List<byte>("<mph>"u8.ToArray()) { 0, (byte)'8', 0 };
        w.AddRange(BitConverter.GetBytes((uint)teams.Length));
        foreach (var team in teams)
        {
            w.AddRange(BitConverter.GetBytes(wide ? (uint)team.Length | 0x8000_0000u : (uint)team.Length));
            w.AddRange(wide ? Encoding.Unicode.GetBytes(team) : Encoding.ASCII.GetBytes(team));
        }

        // The parallel u32 array whose repeated count confirms the roster walked.
        w.AddRange(BitConverter.GetBytes((uint)teams.Length));
        for (var i = 0; i < teams.Length; i++)
        {
            w.AddRange(BitConverter.GetBytes((uint)(i + 1)));
        }

        return [.. w];
    }

    [Fact]
    public void Parse_ReadsTheTeamRosterFromTheInflatedWorld()
    {
        var mission = TacticsMissionFile.Parse(
            Mission(world: World("BOS", "Tribals (T)", "Raiders (R)")), "mission01.mis");

        Assert.Equal('8', mission.WorldVersion);
        Assert.Equal(["BOS", "Tribals (T)", "Raiders (R)"], mission.Teams);
    }

    [Fact]
    public void Parse_ReadsARosterWhoseNamesCarryTheWideFlag()
    {
        // ⚠⚠ The length prefix's BIT 31 chooses the encoding here exactly as it does in the save's
        // own strings. Retail hides this: only core/editor/ambientExample.mis writes its roster
        // wide, so an ASCII-only reader looked right on 127 of the 128 shipped missions and
        // returned an EMPTY roster — no error — on the 128th.
        var mission = TacticsMissionFile.Parse(Mission(world: World(true, "Team 1", "Team 2")), "ambientExample.mis");

        Assert.Equal(["Team 1", "Team 2"], mission.Teams);

        // ...and the same two names written ASCII still read, so the flag is what is being honoured
        // rather than one encoding being swapped for the other.
        Assert.Equal(
            ["Team 1", "Team 2"],
            TacticsMissionFile.Parse(Mission(world: World(false, "Team 1", "Team 2")), "ascii.mis").Teams);
    }

    [Fact]
    public void Parse_YieldsNoTeamsRatherThanThrowingWhenTheRosterDoesNotWalk()
    {
        // ⚠ Only the roster is decoded; the rest of the world is bulk data left untouched.
        // A future revision that changes the roster must degrade to "no teams", not fail an
        // otherwise valid mission.
        var broken = World("A", "B");
        broken[8] = 0xFF; // an impossible team count

        var mission = TacticsMissionFile.Parse(Mission(world: broken), "odd.mis");
        Assert.Empty(mission.Teams);
    }

    [Theory]
    [InlineData("68")]
    [InlineData("69")]
    public void Parse_AcceptsBothRetailVersions(string version)
    {
        // ⚠ Retail ships BOTH: 98 shipped missions at "68" and 30 at "69" (87/16 archived, 11/14
        // loose). Pinning one rejects a quarter of them — and a small sample shows only "68", which
        // is how you would come to pin it.
        var mission = TacticsMissionFile.Parse(Mission(version), "bunker01.mis");

        Assert.Equal(version, mission.Version);
        Assert.StartsWith(TacticsMissionFile.WorldTag, Encoding.ASCII.GetString(mission.World),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_InflatesTheWorldExactly()
    {
        var world = new byte[5000];
        for (var i = 0; i < world.Length; i++)
        {
            world[i] = (byte)(i * 7);
        }

        var mission = TacticsMissionFile.Parse(Mission(world: world), "big.mis");
        Assert.Equal(world, mission.World);
    }

    [Fact]
    public void Parse_RejectsAnInflatedSizeThatContradictsTheHeader()
    {
        // The declared size is the self-check: it is what makes this a verified read rather than a
        // hopeful inflate. Retail agrees on 128/128 shipped missions.
        var error = Assert.Throws<InvalidDataException>(() =>
            TacticsMissionFile.Parse(Mission(declaredBias: 1, repeatedBias: 1), "BAD.mis"));
        Assert.Contains("the header declares", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsDisagreeingSizeFields()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            TacticsMissionFile.Parse(Mission(repeatedBias: 3), "BAD.mis"));
        Assert.Contains("disagree", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsBytesLeftOverAfterTheZlibStream()
    {
        // ⚠ The inflater alone CANNOT catch this: it stops at the end of the deflate data and
        // ignores whatever follows, so an inflate that succeeds and matches the declared size says
        // nothing about the buffer's end. The trailing Adler-32 does — and 129/129 real worlds
        // (103 archived .mis, 25 loose .mis and the one archived in Snake.sav) carry it at the last
        // byte. ⛔ An earlier revision of this comment said 207/207 over "206 shipped entries": that
        // count came from walking the mis-*.bos list twice and omitted the loose files entirely.
        var trailing = new List<byte>(Mission()) { 0x00 };
        var error = Assert.Throws<InvalidDataException>(() => TacticsMissionFile.Parse([.. trailing], "PADDED.mis"));
        Assert.Contains("does not end at the last byte", error.Message, StringComparison.Ordinal);

        // ...and the same bytes without the pad still parse, so the test is about the pad alone.
        Assert.Equal("68", TacticsMissionFile.Parse(Mission(), "OK.mis").Version);
    }

    [Fact]
    public void Parse_RejectsSomethingThatIsNotAMission()
    {
        Assert.Throws<InvalidDataException>(() =>
            TacticsMissionFile.Parse("<entity>\0..........."u8.ToArray(), "x.ent"));
    }

    [Fact]
    public void IsMission_ChecksTheTagAndItsTerminator()
    {
        Assert.True(TacticsMissionFile.IsMission(Mission()));
        Assert.False(TacticsMissionFile.IsMission("<world>X...........".Select(c => (byte)c).ToArray()));
    }
}