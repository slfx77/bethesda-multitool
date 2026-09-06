using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Vectors for Fallout Tactics <c>.mis</c> missions, shaped after all 103 retail files measured
///     2026-09-06 — versions "68" (87) and "69" (16), every one inflating to exactly its declared
///     size.
/// </summary>
public sealed class TacticsMissionFileTests
{
    private static byte[] Mission(string version = "68", byte[]? world = null, int declaredBias = 0, int repeatedBias = 0)
    {
        world ??= [.. "<mph>"u8, 0, (byte)'8', 0, 1, 2, 3, 4];

        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionMode.Compress, leaveOpen: true))
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
        var w = new List<byte>("<mph>"u8.ToArray()) { 0, (byte)'8', 0 };
        w.AddRange(BitConverter.GetBytes((uint)teams.Length));
        foreach (var team in teams)
        {
            w.AddRange(BitConverter.GetBytes((uint)team.Length));
            w.AddRange(Encoding.ASCII.GetBytes(team));
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
    public void Parse_YieldsNoTeamsRatherThanThrowingWhenTheRosterDoesNotWalk()
    {
        // ⚠ Only the roster is decoded; the rest of the world is bulk data left untouched.
        // A future revision that changes the roster must degrade to "no teams", not fail an
        // otherwise valid mission.
        var broken = World("A", "B");
        broken[8] = 0xFF;   // an impossible team count

        var mission = TacticsMissionFile.Parse(Mission(world: broken), "odd.mis");
        Assert.Empty(mission.Teams);
    }

    [Theory]
    [InlineData("68")]
    [InlineData("69")]
    public void Parse_AcceptsBothRetailVersions(string version)
    {
        // ⚠ Retail ships BOTH: 87 missions at "68" and 16 at "69". Pinning one rejects a sixth of
        // the campaign — and a small sample shows only "68", which is how you would come to pin it.
        var mission = TacticsMissionFile.Parse(Mission(version), "bunker01.mis");

        Assert.Equal(version, mission.Version);
        Assert.StartsWith(TacticsMissionFile.WorldTag, Encoding.ASCII.GetString(mission.World), StringComparison.Ordinal);
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
        // hopeful inflate. Retail agrees on 103/103.
        var error = Assert.Throws<InvalidDataException>(
            () => TacticsMissionFile.Parse(Mission(declaredBias: 1, repeatedBias: 1), "BAD.mis"));
        Assert.Contains("the header declares", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsDisagreeingSizeFields()
    {
        var error = Assert.Throws<InvalidDataException>(() => TacticsMissionFile.Parse(Mission(repeatedBias: 3), "BAD.mis"));
        Assert.Contains("disagree", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsSomethingThatIsNotAMission()
    {
        Assert.Throws<InvalidDataException>(() => TacticsMissionFile.Parse("<entity>\0..........."u8.ToArray(), "x.ent"));
    }

    [Fact]
    public void IsMission_ChecksTheTagAndItsTerminator()
    {
        Assert.True(TacticsMissionFile.IsMission(Mission()));
        Assert.False(TacticsMissionFile.IsMission("<world>X...........".Select(c => (byte)c).ToArray()));
    }
}
