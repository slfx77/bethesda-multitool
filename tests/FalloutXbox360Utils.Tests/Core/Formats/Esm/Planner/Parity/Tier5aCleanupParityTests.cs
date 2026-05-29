using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

public sealed class Tier5aCleanupParityTests
{
    [Fact]
    public void New_Aloc_Parity()
    {
        var aloc = new AudioLocationControllerRecord { FormId = 0x01000800, EditorId = "TestAloc" };
        var legacy = AlocEncoder.EncodeNew(aloc);
        PlannerTier1ParityHelper.AssertNewRecordParity("ALOC", aloc.FormId, aloc, legacy);
    }

    [Fact]
    public void New_Ccrd_Parity()
    {
        var ccrd = new CaravanCardRecord { FormId = 0x01000800, EditorId = "TestCcrd" };
        var legacy = CcrdEncoder.EncodeNew(ccrd);
        PlannerTier1ParityHelper.AssertNewRecordParity("CCRD", ccrd.FormId, ccrd, legacy);
    }

    [Fact]
    public void New_Cmny_Parity()
    {
        var cmny = new CaravanMoneyRecord { FormId = 0x01000800, EditorId = "TestCmny" };
        var legacy = CmnyEncoder.EncodeNew(cmny);
        PlannerTier1ParityHelper.AssertNewRecordParity("CMNY", cmny.FormId, cmny, legacy);
    }

    [Fact]
    public void New_Cdck_Parity()
    {
        var cdck = new CaravanDeckRecord { FormId = 0x01000800, EditorId = "TestCdck" };
        var legacy = CdckEncoder.EncodeNew(cdck);
        PlannerTier1ParityHelper.AssertNewRecordParity("CDCK", cdck.FormId, cdck, legacy);
    }

    [Fact]
    public void New_Flst_Parity()
    {
        var flst = new FormListRecord { FormId = 0x01000800, EditorId = "TestFlst" };
        var legacy = FlstEncoder.EncodeNew(flst);
        PlannerTier1ParityHelper.AssertNewRecordParity("FLST", flst.FormId, flst, legacy);
    }
}
