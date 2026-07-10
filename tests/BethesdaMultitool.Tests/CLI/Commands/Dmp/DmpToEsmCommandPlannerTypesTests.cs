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