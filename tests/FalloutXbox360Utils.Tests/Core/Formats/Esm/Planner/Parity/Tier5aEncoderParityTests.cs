using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

public sealed class Tier5aEncoderParityTests
{
    [Fact]
    public void New_Wrld_Parity()
    {
        var wrld = new WorldspaceRecord { FormId = 0x01000800, EditorId = "TestWrld" };
        var legacy = WrldEncoder.EncodeNew(wrld);
        PlannerTier1ParityHelper.AssertNewRecordParity("WRLD", wrld.FormId, wrld, legacy);
    }

    [Fact]
    public void New_Ligh_Parity()
    {
        var ligh = new LightRecord { FormId = 0x01000800, EditorId = "TestLigh" };
        var legacy = LighEncoder.EncodeNew(ligh);
        PlannerTier1ParityHelper.AssertNewRecordParity("LIGH", ligh.FormId, ligh, legacy);
    }

    [Fact]
    public void New_Furn_Parity()
    {
        var furn = new FurnitureRecord { FormId = 0x01000800, EditorId = "TestFurn" };
        var legacy = FurnEncoder.EncodeNew(furn);
        PlannerTier1ParityHelper.AssertNewRecordParity("FURN", furn.FormId, furn, legacy);
    }

    [Fact]
    public void New_Watr_Parity()
    {
        var watr = new WaterRecord { FormId = 0x01000800, EditorId = "TestWatr" };
        var legacy = WatrEncoder.EncodeNew(watr);
        PlannerTier1ParityHelper.AssertNewRecordParity("WATR", watr.FormId, watr, legacy);
    }

    [Fact]
    public void New_Wthr_Parity()
    {
        var wthr = new WeatherRecord { FormId = 0x01000800, EditorId = "TestWthr" };
        var legacy = WthrEncoder.EncodeNew(wthr);
        PlannerTier1ParityHelper.AssertNewRecordParity("WTHR", wthr.FormId, wthr, legacy);
    }

    [Fact]
    public void New_Lgtm_Parity()
    {
        var lgtm = new LightingTemplateRecord { FormId = 0x01000800, EditorId = "TestLgtm" };
        var legacy = LgtmEncoder.EncodeNew(lgtm);
        PlannerTier1ParityHelper.AssertNewRecordParity("LGTM", lgtm.FormId, lgtm, legacy);
    }

    [Fact]
    public void New_Eczn_Parity()
    {
        var eczn = new EncounterZoneRecord { FormId = 0x01000800, EditorId = "TestEczn" };
        var legacy = EczEncoder.EncodeNew(eczn);
        PlannerTier1ParityHelper.AssertNewRecordParity("ECZN", eczn.FormId, eczn, legacy);
    }

    [Fact]
    public void New_Lsct_Parity()
    {
        var lsct = new LoadScreenTypeRecord { FormId = 0x01000800, EditorId = "TestLsct" };
        var legacy = LsctEncoder.EncodeNew(lsct);
        PlannerTier1ParityHelper.AssertNewRecordParity("LSCT", lsct.FormId, lsct, legacy);
    }

    [Fact]
    public void New_Regn_Parity()
    {
        var regn = new RegionRecord { FormId = 0x01000800, EditorId = "TestRegn" };
        var legacy = RegnEncoder.EncodeNew(regn);
        PlannerTier1ParityHelper.AssertNewRecordParity("REGN", regn.FormId, regn, legacy);
    }

    [Fact]
    public void New_Scol_With_No_Parts_Parity()
    {
        var scol = new StaticCollectionRecord { FormId = 0x01000800, EditorId = "TestScol" };
        var emptySet = new HashSet<uint>();
        var legacy = ScolEncoder.EncodeNew(scol, emptySet, emptySet);
        PlannerTier1ParityHelper.AssertNewRecordParity("SCOL", scol.FormId, scol, legacy);
    }
}
