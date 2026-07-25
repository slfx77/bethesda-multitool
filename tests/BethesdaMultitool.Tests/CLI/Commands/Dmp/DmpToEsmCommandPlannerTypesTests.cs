using BethesdaMultitool.CLI.Commands.Dmp;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Commands.Dmp;

/// <summary>
///     Direct coverage of <c>DmpToEsmCommand.ResolvePlannerTypes</c>. The CLI option is
///     wired in <c>DmpToEsmCommand.Create</c>, but the validation logic lives in the
///     resolver helper — these tests pin its shape without involving the full
///     <c>System.CommandLine</c> parse path.
/// </summary>
public sealed class DmpToEsmCommandPlannerTypesTests
{
    [Fact]
    public void DiagnosticOptions_AreRepeatableAtTheCommandLine()
    {
        var parse = DmpToEsmCommand.Create().Parse(
        [
            "sample.dmp",
            "--pc-esm", "FalloutNV.esm",
            "--output", "diagnostic.esm",
            "--event-log-jsonl", "diagnostic.events.jsonl",
            "--planner-types", "all",
            "--diag-keep-master-formid", "0x0012795D",
            "--diag-keep-master-formid", "0x000000C8",
            "--diag-retain-master-subrecords", "0x00127956:AIDT,SNAM",
            "--diag-retain-master-subrecords", "0x00127956:DATA,DNAM"
        ]);

        Assert.Empty(parse.Errors);
    }

    [Fact]
    public void DiagnosticKeepMasterFormIds_AreStrictHexAndDeduplicated()
    {
        var result = DmpToEsmCommand.ParseDiagnosticFormIdSet(
            ["0x0012795D", "0012795d", "000000C8"]);

        Assert.Equal(2, result.Count);
        Assert.Contains(0x0012795Du, result);
        Assert.Contains(0x000000C8u, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("0")]
    [InlineData("not-hex")]
    [InlineData("0x100000000")]
    public void DiagnosticKeepMasterFormIds_RejectInvalidInput(string value)
    {
        Assert.Throws<FormatException>(() => DmpToEsmCommand.ParseDiagnosticFormIdSet([value]));
    }

    [Fact]
    public void DiagnosticRetentions_MergeRepeatedFormIdsAndNormalizeSignatures()
    {
        var result = DmpToEsmCommand.ParseDiagnosticSubrecordRetentions(
            ["0x0012795D:AIDT,SNAM", "0012795d:data,dnam"]);

        var signatures = Assert.Single(result).Value;
        Assert.Equal(4, signatures.Count);
        Assert.Contains("AIDT", signatures);
        Assert.Contains("SNAM", signatures);
        Assert.Contains("DATA", signatures);
        Assert.Contains("DNAM", signatures);
    }

    [Theory]
    [InlineData("0012795D")]
    [InlineData("0012795D:")]
    [InlineData(":DNAM")]
    [InlineData("0012795D:DNA")]
    [InlineData("0012795D:DNAM,")]
    [InlineData("0012795D:DNAM:DATA")]
    [InlineData("0012795D:A[DT")]
    [InlineData("0012795D:A-DT")]
    public void DiagnosticRetentions_RejectInvalidSyntax(string value)
    {
        Assert.Throws<FormatException>(() => DmpToEsmCommand.ParseDiagnosticSubrecordRetentions([value]));
    }

    [Fact]
    public void Empty_Args_Yields_Empty_Set()
    {
        var result = DmpToEsmCommand.ResolvePlannerTypes([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Whitespace_Args_Are_Filtered()
    {
        var result = DmpToEsmCommand.ResolvePlannerTypes(["", "   ", "\t"]);
        Assert.Empty(result);
    }

    [Fact]
    public void Single_Valid_Type_Survives()
    {
        var result = DmpToEsmCommand.ResolvePlannerTypes(["STAT"]);
        Assert.Single(result);
        Assert.Contains("STAT", result);
    }

    [Fact]
    public void Multiple_Valid_Types_All_Resolve()
    {
        var result = DmpToEsmCommand.ResolvePlannerTypes(["STAT", "WEAP", "GMST"]);
        Assert.Equal(3, result.Count);
        Assert.Contains("STAT", result);
        Assert.Contains("WEAP", result);
        Assert.Contains("GMST", result);
    }

    [Fact]
    public void All_Token_Resolves_To_Every_Known_Type()
    {
        var result = DmpToEsmCommand.ResolvePlannerTypes(["all"]);
        var expected = PlannedEncoders.KnownRecordTypes().ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void All_Token_Is_Case_Insensitive()
    {
        var lower = DmpToEsmCommand.ResolvePlannerTypes(["all"]);
        var mixed = DmpToEsmCommand.ResolvePlannerTypes(["ALL"]);
        var titled = DmpToEsmCommand.ResolvePlannerTypes(["All"]);

        Assert.Equal(lower, mixed);
        Assert.Equal(lower, titled);
    }

    [Fact]
    public void Args_Are_Deduplicated()
    {
        var result = DmpToEsmCommand.ResolvePlannerTypes(["STAT", "STAT", "WEAP"]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void All_Token_Plus_Explicit_Type_Stays_Idempotent()
    {
        var result = DmpToEsmCommand.ResolvePlannerTypes(["all", "STAT"]);
        var expected = PlannedEncoders.KnownRecordTypes().ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, result);
    }
}