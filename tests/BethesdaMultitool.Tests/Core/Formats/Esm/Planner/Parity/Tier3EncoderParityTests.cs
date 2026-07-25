using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 3 byte-exact parity. Bare FormId/EditorId fixtures for this tier are covered by
///     <see cref="AggregatePlannerParityTests" /> sweeping every registered encoder; only the
///     LVLI fixture remains here because it sets <see cref="LeveledListRecord.ListType" />,
///     which the aggregate sweep's synthetic model does not.
/// </summary>
public sealed class Tier3EncoderParityTests
{
    [Fact]
    public void New_Lvli_With_No_Refs_Parity()
    {
        var lvli = new LeveledListRecord
        {
            FormId = 0x01000800,
            EditorId = "TestLvli",
            ListType = "LVLI"
        };

        var legacy = LvliEncoder.EncodeNew(lvli);
        PlannerTier1ParityHelper.AssertNewRecordParity("LVLI", lvli.FormId, lvli, legacy);
    }
}