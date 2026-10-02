using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

/// <summary>
///     One test per <see cref="ScriptBytecodeByteOrderSelector" /> rule. Every decided case passes the
///     OPPOSITE caller default, so a rule that silently fell through to the default would fail; the
///     undecidable payloads pass both defaults to prove the fallback is honoured, not hard-coded.
///     Opcodes are pinned from the FNV command table: 0x117A ShowRepairMenu, 0x1223 ForceTerminalBack;
///     0x1D00, 0x7A11, 0x2312, 0x1270 and 0x7012 are not in it.
/// </summary>
public class ScriptBytecodeByteOrderSelectorTests
{
    // July 2010 X360 prototype, SCPT 0x00132163 NVCCBunkerLogBookSCRIPT, SCDA verbatim:
    // ScriptName / Begin OnAdd / End, little-endian inside a big-endian container.
    private const string JulyLogBookScdaLittleEndian = "1D00000010000800030004000000000011000000";

    // The same three statements hand-encoded big-endian.
    private const string LogBookScdaBigEndian = "001D0000001000080003000000040000" + "00110000";

    [Theory]
    [InlineData("", true)]
    [InlineData("", false)]
    [InlineData("1D0000", true)]
    [InlineData("1D0000", false)]
    public void Select_FewerThanFourBytes_ReturnsCallerDefaultAsNotApplicable(
        string scdaHex,
        bool fallbackBigEndian)
    {
        var decision = Select(scdaHex, fallbackBigEndian, ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault);

        Assert.Equal(fallbackBigEndian, decision.IsBigEndian);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.NotApplicable, decision.Evidence);
    }

    [Theory]
    [InlineData(JulyLogBookScdaLittleEndian, false)]
    [InlineData(LogBookScdaBigEndian, true)]
    [InlineData("1D000000", false)]
    [InlineData("001D0000", true)]
    public void Select_ScriptNameAnchor_DecidesAgainstTheCallerDefault(
        string scdaHex,
        bool expectedBigEndian)
    {
        var decision = Select(scdaHex, !expectedBigEndian, ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback);

        Assert.Equal(expectedBigEndian, decision.IsBigEndian);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.ScriptNameAnchor, decision.Evidence);
    }

    [Theory]
    // Set f0 to 1: 15 00 | 0B 00 | 66 00 00 | 06 00 | 20 6E 01 00 00 00. Read the other way the
    // first statement is opcode 0x1500 declaring 2,816 payload bytes, so that walk is truncated.
    [InlineData("15000B006600000600206E01000000", false)]
    [InlineData("0015000B6600000006206E00000001", true)]
    public void Select_OnlyOneOrderWalksCleanly_IsSingleCleanWalk(string scdaHex, bool expectedBigEndian)
    {
        var decision = Select(scdaHex, !expectedBigEndian, ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback);

        Assert.Equal(expectedBigEndian, decision.IsBigEndian);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.SingleCleanWalk, decision.Evidence);
    }

    [Theory]
    // A 4-byte call walks cleanly both ways; only one reading names its opcode.
    [InlineData("7A110000", false)] // ShowRepairMenu LE, UnknownFunc_0x7A11 BE
    [InlineData("117A0000", true)] // UnknownFunc_0x7A11 LE, ShowRepairMenu BE
    [InlineData("23120000", false)] // ForceTerminalBack LE (July TERM 0x001645E0), UnknownFunc_0x2312 BE
    [InlineData("12230000", true)]
    // Relative, not absolute: a genuinely unlisted opcode (0x1270, as retail has) in the correct
    // reading still leaves it with fewer unknowns (1) than the misread one (2).
    [InlineData("7A11000070120000", false)]
    public void Select_BothOrdersWalkCleanly_FewerUnknownOpcodesWins(string scdaHex, bool expectedBigEndian)
    {
        var decision = Select(scdaHex, !expectedBigEndian, ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback);

        Assert.Equal(expectedBigEndian, decision.IsBigEndian);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.FewerUnknownOpcodes, decision.Evidence);
    }

    [Theory]
    // LE: unknown flow opcode 0x0001 with a 2-byte payload, walked to the end with a diagnostic.
    // BE: opcode 0x0100 declaring 512 payload bytes, truncated after the first word pair.
    [InlineData("01000200AABB", false)]
    [InlineData("00010002AABB", true)]
    public void Select_NeitherOrderClean_OnlyOneWalksToEnd_IsWalkedToEndInOneOrder(
        string scdaHex,
        bool expectedBigEndian)
    {
        var decision = Select(scdaHex, !expectedBigEndian, ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback);

        Assert.Equal(expectedBigEndian, decision.IsBigEndian);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.WalkedToEndInOneOrder, decision.Evidence);
    }

    [Theory]
    // All zero: opcode 0x0000 is unknown in both orders and both walks reach the end.
    [InlineData("00000000", true, ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback)]
    [InlineData("00000000", false, ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault)]
    [InlineData("00000000", false, ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback)]
    // ScriptDone (0xFFFF) with an empty payload is a palindrome: both walks clean, no unknowns.
    [InlineData("FFFF0000", true, ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback)]
    [InlineData("FFFF0000", false, ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault)]
    public void Select_UndecidablePayload_HonoursTheCallerDefaultAndLabel(
        string scdaHex,
        bool fallbackBigEndian,
        ScriptBytecodeByteOrderEvidence fallbackEvidence)
    {
        var decision = Select(scdaHex, fallbackBigEndian, fallbackEvidence);

        Assert.Equal(fallbackBigEndian, decision.IsBigEndian);
        Assert.Equal(fallbackEvidence, decision.Evidence);
    }

    [Fact]
    public void Select_UsesTheGamesCommandTable()
    {
        // Oblivion's table stops short of 0x1223, so neither reading of ForceTerminalBack names an
        // opcode and the payload is undecidable there; FNV and FO3 name it little-endian.
        var bytecode = Convert.FromHexString("23120000");

        var oblivion = ScriptBytecodeByteOrderSelector.Select(
            bytecode, [], [], true, ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback,
            BethesdaGame.Oblivion);
        var fallout3 = ScriptBytecodeByteOrderSelector.Select(
            bytecode, [], [], true, ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback,
            BethesdaGame.Fallout3);

        Assert.True(oblivion.IsBigEndian);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback, oblivion.Evidence);
        Assert.False(fallout3.IsBigEndian);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.FewerUnknownOpcodes, fallout3.Evidence);
    }

    [Fact]
    public void Select_UnidentifiedGame_WalksWithTheFalloutCommandTable()
    {
        // An empty table would make every function opcode unknown in both orders; Unknown walks
        // with the decompiler's own FNV/FO3 default instead, so the short call is still decided.
        var decision = ScriptBytecodeByteOrderSelector.Select(
            Convert.FromHexString("23120000"), [], [], true,
            ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback, BethesdaGame.Unknown);

        Assert.False(decision.IsBigEndian);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.FewerUnknownOpcodes, decision.Evidence);
    }

    [Fact]
    public void Select_ReportsBothWalksInTheDetail()
    {
        var decision = Select("23120000", true, ScriptBytecodeByteOrderEvidence.AmbiguousContainerFallback);

        Assert.Contains("LE: walked to end, 0 unknown opcode(s)", decision.Detail, StringComparison.Ordinal);
        Assert.Contains("BE: walked to end, 1 unknown opcode(s)", decision.Detail, StringComparison.Ordinal);
    }

    private static ScriptBytecodeByteOrderDecision Select(
        string scdaHex,
        bool fallbackBigEndian,
        ScriptBytecodeByteOrderEvidence fallbackEvidence)
    {
        return ScriptBytecodeByteOrderSelector.Select(
            Convert.FromHexString(scdaHex),
            [],
            [],
            fallbackBigEndian,
            fallbackEvidence,
            BethesdaGame.FalloutNewVegas);
    }
}
