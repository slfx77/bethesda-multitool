using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.Policies;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Disposition;

public sealed class RuntimeStatePolicyTests
{
    [Fact]
    public void Player_Form_Id_Is_Skipped()
    {
        var policy = new RuntimeStatePolicy();
        var entry = new CatalogEntry
        {
            Type = "NPC_",
            Source = SourceKind.DmpOverride,
            MasterFormId = 0x00000007u, // Player
            DmpFormId = 0x00000007u,
        };

        var decision = policy.Decide(entry);

        Assert.NotNull(decision);
        Assert.Equal(RecordDisposition.Skip, decision.Disposition);
    }

    [Fact]
    public void Non_Runtime_State_Returns_Null()
    {
        var policy = new RuntimeStatePolicy();
        var entry = new CatalogEntry
        {
            Type = "WEAP",
            Source = SourceKind.DmpOverride,
            MasterFormId = 0x000ABCDEu,
        };

        Assert.Null(policy.Decide(entry));
    }

    [Fact]
    public void Dmp_New_Entry_With_Runtime_State_Form_Id_Is_Still_Skipped()
    {
        var policy = new RuntimeStatePolicy();
        var entry = new CatalogEntry
        {
            Type = "GLOB",
            Source = SourceKind.DmpNew,
            DmpFormId = 0x00000038u, // GameHour
        };

        var decision = policy.Decide(entry);

        Assert.NotNull(decision);
        Assert.Equal(RecordDisposition.Skip, decision.Disposition);
    }
}
