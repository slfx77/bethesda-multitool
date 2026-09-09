using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Fallout;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Fallout;

/// <summary>
///     Vectors for the located sections of a Fallout <c>SAVE.DAT</c> body, shaped after the retail
///     Fallout 1 SLOT01 measured 2026-09-07 — see <see cref="FalloutSaveBody" /> for the oracles.
/// </summary>
public sealed class FalloutSaveBodyTests
{
    /// <summary>The retail Fallout 1 player's 35 stat slots, transcribed from the save.</summary>
    private static readonly int[] RetailStats =
    [
        7, 5, 6, 3, 8, 5, 6,
        34, 7, 5, 0, 2, 200, 10, 2, 6,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        100, 0, 12, 30, 29, 0
    ];

    private static void WriteInts(byte[] buffer, int at, ReadOnlySpan<int> values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(at + i * 4), values[i]);
        }
    }

    private static byte[] Body(int[] globals, string[] maps, int[]? stats = null, int automapLength = 2218)
    {
        var buffer = new byte[FalloutSaveBody.GameVariablesOffset + globals.Length * 8 + 1024];
        WriteInts(buffer, FalloutSaveBody.GameVariablesOffset, globals);

        var at = FalloutSaveBody.GameVariablesOffset + globals.Length * 4;
        buffer[at] = 0;
        buffer[at + 1] = (byte)maps.Length;
        at += 2;
        foreach (var map in maps)
        {
            Encoding.ASCII.GetBytes(map).CopyTo(buffer, at);
            at += map.Length + 1;
        }

        // The name run is followed by the uncompressed AUTOMAP.SAV length, then the second copy.
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(at), automapLength);
        at += 4;

        WriteInts(buffer, at, globals);
        at += globals.Length * 4;

        if (stats is not null)
        {
            WriteInts(buffer, at + 8, stats);
        }

        return buffer;
    }

    [Fact]
    public void TryReadGameVariables_ReadsBigEndianIntsAndFindsTheSecondCopy()
    {
        // ⚑ The array is stored TWICE, byte-identical — proved on the MID-GAME Fallout 2 save,
        // where the values are not the .GAM initials and the block still occurs twice.
        int[] globals = [0, 0, 9, 0, 0, 0, 0, 0, 0, 180, 150, -1];
        var body = Body(globals, ["V13ENT.SAV"]);

        Assert.True(
            FalloutSaveBody.TryReadGameVariables(body, globals.Length, out var values, out var copy, out var error),
            error);

        Assert.Equal(globals, values);
        Assert.Equal(9, values[2]);
        Assert.Equal(180, values[9]);
        Assert.Equal(-1, values[11]);
        Assert.True(copy > FalloutSaveBody.GameVariablesOffset + globals.Length * 4);
    }

    [Fact]
    public void TryReadGameVariables_RefusesACountTheFileCannotHold()
    {
        Assert.False(FalloutSaveBody.TryReadGameVariables(new byte[64], 618, out _, out _, out var error));
        Assert.Contains("618", error, StringComparison.Ordinal);

        Assert.False(FalloutSaveBody.TryReadGameVariables(new byte[65536], 0, out _, out _, out _));
    }

    [Fact]
    public void TryReadVisitedMaps_ReadsTheCountedNameRun()
    {
        var body = Body([1, 2, 3], ["ARCAVES.SAV", "ARTEMPLE.SAV", "ARVILLAG.SAV"]);

        Assert.True(FalloutSaveBody.TryReadVisitedMaps(body, out var maps, out var offset));
        Assert.Equal(new[] { "ARCAVES.SAV", "ARTEMPLE.SAV", "ARVILLAG.SAV" }, maps);
        Assert.True(offset > FalloutSaveBody.GameVariablesOffset);
    }

    [Fact]
    public void TryReadVisitedMaps_IgnoresNamesThatAreNotSidecars()
    {
        // The run has to be entirely .SAV names, which is what keeps the scan from latching on to
        // arbitrary text elsewhere in the body.
        var body = Body([1, 2, 3], ["README.TXT"]);

        Assert.False(FalloutSaveBody.TryReadVisitedMaps(body, out var maps, out _));
        Assert.Empty(maps);
    }

    [Fact]
    public void TryReadAutomapLength_ReadsTheDwordThatFollowsTheNameRun()
    {
        // The field sits immediately after the visited-map names, so a reader that mis-walked the
        // names by even one byte would read a shifted dword rather than this value.
        var body = Body([1, 2, 3], ["ARCAVES.SAV", "ARTEMPLE.SAV"], automapLength: 10449);

        Assert.True(FalloutSaveBody.TryReadAutomapLength(body, out var length, out var offset));
        Assert.Equal(10449, length);
        Assert.True(FalloutSaveBody.TryReadVisitedMaps(body, out var maps, out var listOffset));
        Assert.Equal(listOffset + "ARCAVES.SAV".Length + 1 + "ARTEMPLE.SAV".Length + 1, offset);
        Assert.Equal(2, maps.Length);
    }

    [Fact]
    public void TryReadAutomapLength_SaysNoWhenThereIsNoMapList()
    {
        Assert.False(FalloutSaveBody.TryReadAutomapLength(new byte[4096], out var length, out var offset));
        Assert.Equal(0, length);
        Assert.Equal(-1, offset);
    }

    [Fact]
    public void TryReadCharacter_FindsTheBlockWhereEveryDerivationHolds()
    {
        // Every value here is a LITERAL from the retail save, hand-checked against Fallout's
        // published formulas: HP 15+7+2x6 = 34, AP 5+5/2 = 7, AC = AG = 5, melee 7-5 = 2,
        // carry 25x7+25 = 200, sequence 2x5 = 10, healing 6/3 = 2, critical = LK = 6,
        // EMP 100, radiation 2x6 = 12, poison 5x6 = 30.
        var body = Body([1, 2, 3], ["V13ENT.SAV"], RetailStats);

        Assert.True(FalloutSaveBody.TryReadCharacter(body, out var player));
        Assert.Equal(40, player.PrimaryTotal);
        Assert.Equal(7, player.Strength);
        Assert.Equal(34, player.MaxHitPoints);
        Assert.Equal(200, player.CarryWeight);
        Assert.Equal(100, player.EmpDamageResistance);
        Assert.Equal(29, player.Age);
        Assert.Equal(0, player.Gender);
        Assert.Equal(35, player.Stats.Count);
    }

    [Fact]
    public void TryReadCharacter_RefusesABlockWhoseDerivationsDoNotHold()
    {
        // The probe must be able to say no: one wrong derived stat and the block is not a stat
        // block. Without this the locator could be latching on to any 35 plausible-looking ints.
        var broken = (int[])RetailStats.Clone();
        broken[7] = 33;
        var body = Body([1, 2, 3], ["V13ENT.SAV"], broken);

        Assert.False(FalloutSaveBody.TryReadCharacter(body, out _));
    }
}