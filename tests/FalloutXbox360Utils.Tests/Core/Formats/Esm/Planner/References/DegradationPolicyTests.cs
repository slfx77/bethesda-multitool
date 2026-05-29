using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References;

public sealed class DegradationPolicyTests
{
    [Fact]
    public void Lookup_Falls_Back_To_Global_Default_When_No_Rule_Matches()
    {
        var policy = new DegradationPolicy(DanglingAction.NullRef);

        var action = policy.Lookup("PACK", "XLKR");

        Assert.Equal(DanglingActionKind.NullRef, action.Kind);
    }

    [Fact]
    public void Lookup_Uses_Type_Default_When_Field_Has_No_Specific_Rule()
    {
        var policy = new DegradationPolicy(DanglingAction.NullRef);
        policy.SetDefaultForType("REFR", DanglingAction.DropSubrecord);

        var action = policy.Lookup("REFR", "XLKR");

        Assert.Equal(DanglingActionKind.DropSubrecord, action.Kind);
    }

    [Fact]
    public void Lookup_Prefers_Exact_Rule_Over_Type_Default_And_Global_Default()
    {
        var policy = new DegradationPolicy(DanglingAction.NullRef);
        policy.SetDefaultForType("PACK", DanglingAction.DropSubrecord);

        var downgrade = new ContainerDowngrade
        {
            ContainerSignature = "PLDT",
            FromShape = "Type 0",
            ToShape = "Type 2",
        };
        policy.SetRule("PACK", "PLDT.Union", DanglingAction.DowngradeContainer(downgrade));

        var pldtAction = policy.Lookup("PACK", "PLDT.Union");
        var otherAction = policy.Lookup("PACK", "SomethingElse");

        Assert.Equal(DanglingActionKind.DowngradeContainer, pldtAction.Kind);
        Assert.Equal("PLDT", pldtAction.Downgrade!.ContainerSignature);
        Assert.Equal(DanglingActionKind.DropSubrecord, otherAction.Kind);
    }
}
