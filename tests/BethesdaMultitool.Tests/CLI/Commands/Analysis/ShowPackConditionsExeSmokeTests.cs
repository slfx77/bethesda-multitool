using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Analysis;

/// <summary>
///     PACK conditions end to end through the SHIPPED exe (see <see cref="CliExeRunner" />) on a synthetic
///     FalloutNV.esm: the CTDA parser, the quest enricher (QUST SCRI -> SCPT SLSD/SCVR variable names), the
///     shared condition describer, and every output that carries it — the <c>show</c> panel, the
///     <c>esm packages</c> text block and <c>esm packages -f json</c> (which must also survive the trimmed
///     exe's disabled reflection-based JSON).
///     <para>
///         Fixture: quest 0x000F2429 <c>VDialogueVegasNorth</c> whose script 0x000F3DD1 declares the retail
///         variables 10 NumFiendsDead, 11 GhoulDealtWith and 12 HelpMrsHostetler, and three packages. PACK
///         0x000FE923 <c>AliceHostetlerRunAway</c> carries the CTDA bytes copied verbatim from the 2022 Steam
///         FalloutNV.esm (GetQuestVariable on variable 11, == 2, Subject). Every expected string is an
///         independent literal; the neighboring variables 10 and 12 must never be printed for index 11.
///     </para>
/// </summary>
public sealed class ShowPackConditionsExeSmokeTests
{
    private const uint QuestFormId = 0x000F2429;
    private const uint QuestScriptFormId = 0x000F3DD1;
    private const uint AlicePackageFormId = 0x000FE923;
    private const uint AndyPackageFormId = 0x000FE91C;
    private const uint BracketPackageFormId = 0x000FE924;

    // PACK 0x000FE923 AliceHostetlerRunAway's CTDA, verbatim: type 0x00 (==, AND), comparison 2.0f,
    // function 0x004F GetQuestVariable, quest 0x000F2429, variable 11, Run On 0 (Subject), reference 0.
    private const string AliceHostetlerCtdaHex =
        "00000000" + "00000040" + "4F000000" + "29240F00" + "0B000000" + "00000000" + "00000000";

    // Shaped like its retail sibling 0x000FE91C AndyLeavePackage: type 0x60 (>=) and comparison 1.0f.
    private const string AndyLeavePackageCtdaHex =
        "60000000" + "0000803F" + "4F000000" + "29240F00" + "0B000000" + "00000000" + "00000000";

    // Synthetic OR pair on the same variable: type 0x01 (==, OR) with 1.0f, then type 0x00 (==) with 2.0f.
    private const string OrFlaggedCtdaHex =
        "01000000" + "0000803F" + "4F000000" + "29240F00" + "0B000000" + "00000000" + "00000000";

    private const string AliceLine =
        "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 2 [Run On: Subject]";

    private const string AndyLine =
        "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) >= 1 [Run On: Subject]";

    private const string OrFirstLine =
        "GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 1 [Run On: Subject] OR";

    /// <summary>
    ///     <c>show</c> on the package prints its Conditions section with the quest named by EditorID and the
    ///     variable named from the quest script. The panel wraps at the console width, so this asserts whole
    ///     tokens; the unwrapped line is pinned by the <c>esm packages</c> tests below.
    /// </summary>
    [Fact]
    public async Task Show_SyntheticPackage_PrintsConditions()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var plugin = CliExeRunner.WriteFalloutNvEsm(directory, BuildPlugin());

        var result = await CliExeRunner.RunAsync(
            ["--plain", "show", plugin, "0x000FE923"],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        var stdout = result.StandardOutput;
        Assert.Contains("AliceHostetlerRunAway", stdout, StringComparison.Ordinal);
        Assert.True(stdout.Contains("Conditions:", StringComparison.Ordinal), result.Describe());
        Assert.True(stdout.Contains("GetQuestVariable(VDialogueVegasNorth", StringComparison.Ordinal),
            result.Describe());
        Assert.True(stdout.Contains("GhoulDealtWith", StringComparison.Ordinal), result.Describe());
        Assert.DoesNotContain("NumFiendsDead", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("HelpMrsHostetler", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>esm packages -f json</c> writes each package's described <c>conditions</c> beside the unchanged
    ///     <c>conditionsRaw</c>, under the shipped runtimeconfig that disables reflection-based JSON.
    /// </summary>
    [Fact]
    public async Task PackagesJson_DescribesEachConditionBesideItsRawWords()
    {
        CliExeRunner.AssertShippedJsonReflectionIsDisabled();
        using var directory = CliExeRunner.CreateTempDirectory();
        var plugin = CliExeRunner.WriteFalloutNvEsm(directory, BuildPlugin());

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", "packages", plugin, "-f", "json"],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.DoesNotContain("Reflection-based serialization", result.StandardError, StringComparison.Ordinal);

        // JsonDocument.Parse rejects leading text and a second top-level value: stdout is ONE document.
        using var json = JsonDocument.Parse(result.StandardOutput);
        var root = json.RootElement;
        Assert.Equal(3, root.GetProperty("totalPackages").GetInt32());

        var context = root.GetProperty("conditionContext");
        Assert.Equal("FalloutNewVegas", context.GetProperty("game").GetString());
        Assert.False(context.GetProperty("gameAssumed").GetBoolean());
        Assert.True(context.GetProperty("questVariableSource").GetBoolean());
        Assert.False(context.GetProperty("memoryDumpInput").GetBoolean());

        var packages = root.GetProperty("packages").EnumerateArray()
            .ToDictionary(package => package.GetProperty("formId").GetString()!);
        Assert.Equal(3, packages.Count);

        var alicePackage = packages["0x000FE923"];
        Assert.Equal("AliceHostetlerRunAway", alicePackage.GetProperty("editorId").GetString());
        var alice = Assert.Single(alicePackage.GetProperty("conditions").EnumerateArray());
        Assert.Equal("GetQuestVariable", alice.GetProperty("function").GetString());
        Assert.Equal("==", alice.GetProperty("operator").GetString());
        Assert.Equal(2f, alice.GetProperty("comparison").GetProperty("value").GetSingle());
        Assert.Equal("VDialogueVegasNorth", alice.GetProperty("parameter1").GetProperty("editorId").GetString());
        Assert.Equal(11, alice.GetProperty("parameter2").GetProperty("variableIndex").GetInt32());
        Assert.Equal("GhoulDealtWith", alice.GetProperty("parameter2").GetProperty("variableName").GetString());
        Assert.Equal("Subject", alice.GetProperty("runOn").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, alice.GetProperty("connectorToNext").ValueKind);
        Assert.Equal(AliceLine, alice.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, alicePackage.GetProperty("conditionLogic").ValueKind);

        // Additive: the raw words are still there, exactly as stored.
        var aliceRaw = Assert.Single(alicePackage.GetProperty("conditionsRaw").EnumerateArray());
        Assert.Equal(0x4F, aliceRaw.GetProperty("functionIndex").GetInt32());
        Assert.Equal(0, aliceRaw.GetProperty("typeRaw").GetInt32());
        Assert.Equal("0x40000000", aliceRaw.GetProperty("comparisonRawBits").GetString());
        Assert.Equal("0x000F2429", aliceRaw.GetProperty("parameter1").GetString());
        Assert.Equal("0x0000000B", aliceRaw.GetProperty("parameter2").GetString());

        var andy = Assert.Single(packages["0x000FE91C"].GetProperty("conditions").EnumerateArray());
        Assert.Equal(">=", andy.GetProperty("operator").GetString());
        Assert.Equal(AndyLine, andy.GetProperty("text").GetString());

        var bracketPackage = packages["0x000FE924"];
        Assert.Equal("[Q]", bracketPackage.GetProperty("editorId").GetString());
        var pair = bracketPackage.GetProperty("conditions").EnumerateArray().ToArray();
        Assert.Equal(2, pair.Length);
        Assert.Equal("OR", pair[0].GetProperty("connectorToNext").GetString());
        Assert.Equal(OrFirstLine, pair[0].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, pair[1].GetProperty("connectorToNext").ValueKind);
        Assert.StartsWith("1 OR 2 (GECK convention", bracketPackage.GetProperty("conditionLogic").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The text output ends with a <c>Package conditions</c> block: per conditioned package a header, then
    ///     one UNWRAPPED line per condition in stored order (so it greps like the JSON <c>text</c>), then the
    ///     grouping summary when there is an OR. A bracketed EditorID prints literally, never double-escaped.
    /// </summary>
    [Fact]
    public async Task PackagesText_ConditionsBlock_PrintsOneLinePerConditionInStoredOrder()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var plugin = CliExeRunner.WriteFalloutNvEsm(directory, BuildPlugin());

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", "packages", plugin],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        Assert.Contains("Total packages: 3", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("[[", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("NumFiendsDead", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("HelpMrsHostetler", result.StandardOutput, StringComparison.Ordinal);

        var lines = SplitLines(result.StandardOutput);
        var blockStart = lines.FindIndex(line => line.Contains("Package conditions", StringComparison.Ordinal));
        Assert.True(blockStart >= 0, result.Describe());

        AssertLinesInOrder(lines, blockStart + 1, result,
            ("AliceHostetlerRunAway [0x000FE923] (1 condition)", false),
            ("  1: " + AliceLine, false),
            ("AndyLeavePackage [0x000FE91C] (1 condition)", false),
            ("  1: " + AndyLine, false),
            ("[Q] [0x000FE924] (2 conditions)", false),
            ("  1: " + OrFirstLine, false),
            ("  2: " + AliceLine, false),
            ("  Logic: 1 OR 2 (GECK convention", true));
    }

    /// <summary>
    ///     The table gains a <c>Cond</c> count column appended LAST, so every existing column keeps its place.
    ///     Filtered to the short-named Find package so the eight columns fit the redirected 80-column console
    ///     without wrapping; a left-aligned cell starts in the same column as its header.
    /// </summary>
    [Fact]
    public async Task PackagesText_CondColumn_IsAppendedLastAndCountsConditions()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var plugin = CliExeRunner.WriteFalloutNvEsm(directory, BuildPlugin());

        var result = await CliExeRunner.RunAsync(
            ["--plain", "esm", "packages", plugin, "-t", "Find"],
            TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);

        Assert.True(result.ExitCode == 0, result.Describe());
        var lines = SplitLines(result.StandardOutput);
        var headerIndex = lines.FindIndex(line =>
            line.Contains("FormID", StringComparison.Ordinal) && line.Contains("Cond", StringComparison.Ordinal));
        Assert.True(headerIndex >= 0, result.Describe());
        var header = lines[headerIndex];

        string[] columns = ["FormID", "EditorID", "Type", "Schedule", "Location", "Target", "Flags", "Cond"];
        var positions = columns.Select(column => header.IndexOf(column, StringComparison.Ordinal)).ToArray();
        Assert.True(positions.All(position => position >= 0), header);
        for (var i = 1; i < positions.Length; i++)
        {
            Assert.True(positions[i] > positions[i - 1], $"'{columns[i]}' must follow '{columns[i - 1]}': {header}");
        }

        var rowIndex = lines.FindIndex(headerIndex + 1,
            line => line.Contains("0x000FE924", StringComparison.Ordinal));
        Assert.True(rowIndex > headerIndex, result.Describe());
        var row = lines[rowIndex];
        var condColumn = positions[^1];
        Assert.True(row.Length > condColumn, row);
        Assert.Equal('2', row[condColumn]);
    }

    /// <summary>
    ///     A little-endian FalloutNV plugin: SCPT 0x000F3DD1 (a quest script declaring 10 NumFiendsDead,
    ///     11 GhoulDealtWith, 12 HelpMrsHostetler), QUST 0x000F2429 <c>VDialogueVegasNorth</c> whose SCRI names
    ///     it, and three PACKs — Travel <c>AliceHostetlerRunAway</c> (the retail CTDA), Travel
    ///     <c>AndyLeavePackage</c> (&gt;= 1) and Find <c>[Q]</c> (an OR pair, and a markup-hostile EditorID).
    /// </summary>
    private static EsmTestFileBuilder BuildPlugin()
    {
        var schr = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(12), 3); // variable count
        BinaryPrimitives.WriteUInt16LittleEndian(schr.AsSpan(16), 1); // type: quest script

        var scri = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(scri, QuestScriptFormId);

        var travel = new byte[12];
        travel[4] = 6; // Travel
        var find = new byte[12];
        find[4] = 0; // Find

        return new EsmTestFileBuilder()
            .AddTopLevelGrup("SCPT",
                EsmTestFileBuilder.BuildRecord("SCPT", QuestScriptFormId, 0,
                    ("EDID", NullTerminated("vDialogueVegasNorthScript")),
                    ("SCHR", schr),
                    ("SLSD", IntegerLocal(10)),
                    ("SCVR", NullTerminated("NumFiendsDead")),
                    ("SLSD", IntegerLocal(11)),
                    ("SCVR", NullTerminated("GhoulDealtWith")),
                    ("SLSD", IntegerLocal(12)),
                    ("SCVR", NullTerminated("HelpMrsHostetler"))))
            .AddTopLevelGrup("QUST",
                EsmTestFileBuilder.BuildRecord("QUST", QuestFormId, 0,
                    ("EDID", NullTerminated("VDialogueVegasNorth")),
                    ("SCRI", scri)))
            .AddTopLevelGrup("PACK",
                EsmTestFileBuilder.BuildRecord("PACK", AlicePackageFormId, 0,
                    ("EDID", NullTerminated("AliceHostetlerRunAway")),
                    ("PKDT", travel),
                    ("CTDA", Convert.FromHexString(AliceHostetlerCtdaHex))),
                EsmTestFileBuilder.BuildRecord("PACK", AndyPackageFormId, 0,
                    ("EDID", NullTerminated("AndyLeavePackage")),
                    ("PKDT", travel),
                    ("CTDA", Convert.FromHexString(AndyLeavePackageCtdaHex))),
                EsmTestFileBuilder.BuildRecord("PACK", BracketPackageFormId, 0,
                    ("EDID", NullTerminated("[Q]")),
                    ("PKDT", find),
                    ("CTDA", Convert.FromHexString(OrFlaggedCtdaHex)),
                    ("CTDA", Convert.FromHexString(AliceHostetlerCtdaHex))));
    }

    /// <summary>A 24-byte SLSD: the variable index (u32 LE) and the integer flag at offset 16.</summary>
    private static byte[] IntegerLocal(uint index)
    {
        var slsd = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(slsd, index);
        slsd[16] = 1;
        return slsd;
    }

    private static byte[] NullTerminated(string value)
    {
        return Encoding.ASCII.GetBytes(value + "\0");
    }

    private static List<string> SplitLines(string text)
    {
        return text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
    }

    /// <summary>
    ///     Asserts each expected line occurs, in order, at or after <paramref name="start" />: exact equality,
    ///     or a prefix match when its flag is set.
    /// </summary>
    private static void AssertLinesInOrder(
        List<string> lines,
        int start,
        CliExeRunner.Result result,
        params (string Text, bool Prefix)[] expected)
    {
        var from = start;
        foreach (var (text, prefix) in expected)
        {
            var index = lines.FindIndex(from, line => prefix
                ? line.StartsWith(text, StringComparison.Ordinal)
                : string.Equals(line, text, StringComparison.Ordinal));
            Assert.True(index >= 0,
                $"Expected a line {(prefix ? "starting with" : "equal to")} \"{text}\" at or after line {from}.\n" +
                result.Describe());
            from = index + 1;
        }
    }
}
