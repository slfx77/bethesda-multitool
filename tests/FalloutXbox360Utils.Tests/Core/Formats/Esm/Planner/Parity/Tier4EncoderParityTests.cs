using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

public sealed class Tier4EncoderParityTests
{
    [Fact]
    public void New_Pack_With_No_Refs_Parity()
    {
        var pack = new PackageRecord { FormId = 0x01000800, EditorId = "TestPack" };
        var legacy = PackEncoder.EncodeNew(pack, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("PACK", pack.FormId, pack, legacy);
    }

    [Fact]
    public void New_Cpth_With_No_Refs_Parity()
    {
        var cpth = new CameraPathRecord { FormId = 0x01000800, EditorId = "TestCpth" };
        var legacy = CpthEncoder.EncodeNew(cpth, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("CPTH", cpth.FormId, cpth, legacy);
    }

    [Fact]
    public void New_Dial_Parity()
    {
        var dial = new DialogTopicRecord { FormId = 0x01000800, EditorId = "TestDial" };
        var legacy = DialEncoder.EncodeNew(dial);
        PlannerTier1ParityHelper.AssertNewRecordParity("DIAL", dial.FormId, dial, legacy);
    }

    [Fact]
    public void New_Mesg_Parity()
    {
        var mesg = new MessageRecord { FormId = 0x01000800, EditorId = "TestMesg" };
        var legacy = MesgEncoder.EncodeNew(mesg);
        PlannerTier1ParityHelper.AssertNewRecordParity("MESG", mesg.FormId, mesg, legacy);
    }
}
