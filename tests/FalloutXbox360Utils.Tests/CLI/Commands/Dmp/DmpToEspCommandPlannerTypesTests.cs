using FalloutXbox360Utils.CLI.Commands.Dmp;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter;
using Xunit;

namespace FalloutXbox360Utils.Tests.CLI.Commands.Dmp;

/// <summary>
///     Direct coverage of <c>DmpToEspCommand.ResolvePlannerTypes</c>. The CLI option is
///     wired in <c>DmpToEspCommand.Create</c>, but the validation logic lives in the
///     resolver helper — these tests pin its shape without involving the full
///     <c>System.CommandLine</c> parse path.
/// </summary>
public sealed class DmpToEspCommandPlannerTypesTests
{
    [Fact]
    public void Empty_Args_Yield_Every_Known_Type()
    {
        // Tier 7b flipped the default: no explicit --planner-types means planner-on for
        // every signature. Resolver mirrors the PluginBuildOptions record default.
        var result = DmpToEspCommand.ResolvePlannerTypes([]);
        var expected = PlannedEncoders.KnownRecordTypes().ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Whitespace_Only_Args_Yield_Every_Known_Type()
    {
        // Whitespace-only tokens contain no real input — equivalent to passing no
        // tokens. Falls through to the implicit "all known" default.
        var result = DmpToEspCommand.ResolvePlannerTypes(["", "   ", "\t"]);
        var expected = PlannedEncoders.KnownRecordTypes().ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Single_Valid_Type_Survives()
    {
        var result = DmpToEspCommand.ResolvePlannerTypes(["STAT"]);
        Assert.Single(result);
        Assert.Contains("STAT", result);
    }

    [Fact]
    public void Multiple_Valid_Types_All_Resolve()
    {
        var result = DmpToEspCommand.ResolvePlannerTypes(["STAT", "WEAP", "GMST"]);
        Assert.Equal(3, result.Count);
        Assert.Contains("STAT", result);
        Assert.Contains("WEAP", result);
        Assert.Contains("GMST", result);
    }

    [Fact]
    public void All_Token_Resolves_To_Every_Known_Type()
    {
        var result = DmpToEspCommand.ResolvePlannerTypes(["all"]);
        var expected = PlannedEncoders.KnownRecordTypes().ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void All_Token_Is_Case_Insensitive()
    {
        var lower = DmpToEspCommand.ResolvePlannerTypes(["all"]);
        var mixed = DmpToEspCommand.ResolvePlannerTypes(["ALL"]);
        var titled = DmpToEspCommand.ResolvePlannerTypes(["All"]);

        Assert.Equal(lower, mixed);
        Assert.Equal(lower, titled);
    }

    [Fact]
    public void Args_Are_Deduplicated()
    {
        var result = DmpToEspCommand.ResolvePlannerTypes(["STAT", "STAT", "WEAP"]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void All_Token_Plus_Explicit_Type_Stays_Idempotent()
    {
        var result = DmpToEspCommand.ResolvePlannerTypes(["all", "STAT"]);
        var expected = PlannedEncoders.KnownRecordTypes().ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, result);
    }
}
