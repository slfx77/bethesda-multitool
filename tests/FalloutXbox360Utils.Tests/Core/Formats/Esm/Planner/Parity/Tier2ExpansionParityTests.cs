using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 2 expansion byte-exact parity for the 22 character/misc/world/AI trivials.
///     Each test feeds a minimal synthetic record through PlanWriter and asserts the
///     bytes match the legacy primitives.
/// </summary>
public sealed class Tier2ExpansionParityTests
{
    [Fact]
    public void New_Soun_Parity()
    {
        var soun = new SoundRecord { FormId = 0x01000800, EditorId = "TestSoun" };
        PlannerTier1ParityHelper.AssertNewRecordParity("SOUN", soun.FormId, soun, SounEncoder.EncodeNew(soun));
    }

    [Fact]
    public void New_Fact_Parity()
    {
        var fact = new FactionRecord { FormId = 0x01000800, EditorId = "TestFact" };
        PlannerTier1ParityHelper.AssertNewRecordParity("FACT", fact.FormId, fact, FactEncoder.EncodeNew(fact));
    }

    [Fact]
    public void New_Hair_Parity()
    {
        var hair = new HairRecord { FormId = 0x01000800, EditorId = "TestHair" };
        PlannerTier1ParityHelper.AssertNewRecordParity("HAIR", hair.FormId, hair, HairEncoder.EncodeNew(hair));
    }

    [Fact]
    public void New_Eyes_Parity()
    {
        var eyes = new EyesRecord { FormId = 0x01000800, EditorId = "TestEyes" };
        PlannerTier1ParityHelper.AssertNewRecordParity("EYES", eyes.FormId, eyes, EyesEncoder.EncodeNew(eyes));
    }

    [Fact]
    public void New_Hdpt_Parity()
    {
        var hdpt = new HeadPartRecord { FormId = 0x01000800, EditorId = "TestHdpt" };
        PlannerTier1ParityHelper.AssertNewRecordParity("HDPT", hdpt.FormId, hdpt, HdptEncoder.EncodeNew(hdpt));
    }

    [Fact]
    public void New_Bptd_Parity()
    {
        var bptd = new BodyPartDataRecord { FormId = 0x01000800, EditorId = "TestBptd" };
        PlannerTier1ParityHelper.AssertNewRecordParity("BPTD", bptd.FormId, bptd, BptdEncoder.EncodeNew(bptd));
    }

    [Fact]
    public void New_Avif_Parity()
    {
        var avif = new ActorValueInfoRecord { FormId = 0x01000800, EditorId = "TestAvif" };
        PlannerTier1ParityHelper.AssertNewRecordParity("AVIF", avif.FormId, avif, AvifEncoder.EncodeNew(avif));
    }

    [Fact]
    public void New_Clas_Parity()
    {
        var clas = new ClassRecord { FormId = 0x01000800, EditorId = "TestClas" };
        PlannerTier1ParityHelper.AssertNewRecordParity("CLAS", clas.FormId, clas, ClasEncoder.EncodeNew(clas));
    }

    [Fact]
    public void New_Race_Parity()
    {
        var race = new RaceRecord { FormId = 0x01000800, EditorId = "TestRace" };
        PlannerTier1ParityHelper.AssertNewRecordParity("RACE", race.FormId, race, RaceEncoder.EncodeNew(race));
    }

    [Fact]
    public void New_Repu_Parity()
    {
        var repu = new ReputationRecord { FormId = 0x01000800, EditorId = "TestRepu" };
        PlannerTier1ParityHelper.AssertNewRecordParity("REPU", repu.FormId, repu, RepuEncoder.EncodeNew(repu));
    }

    [Fact]
    public void New_Vtyp_Parity()
    {
        var vtyp = new VoiceTypeRecord { FormId = 0x01000800, EditorId = "TestVtyp" };
        PlannerTier1ParityHelper.AssertNewRecordParity("VTYP", vtyp.FormId, vtyp, VtypEncoder.EncodeNew(vtyp));
    }

    [Fact]
    public void New_Chal_Parity()
    {
        var chal = new ChallengeRecord { FormId = 0x01000800, EditorId = "TestChal" };
        PlannerTier1ParityHelper.AssertNewRecordParity("CHAL", chal.FormId, chal, ChalEncoder.EncodeNew(chal));
    }

    [Fact]
    public void New_Ingr_Parity()
    {
        var ingr = new IngredientRecord { FormId = 0x01000800, EditorId = "TestIngr" };
        PlannerTier1ParityHelper.AssertNewRecordParity("INGR", ingr.FormId, ingr, IngrEncoder.EncodeNew(ingr));
    }

    [Fact]
    public void New_Ipct_Parity()
    {
        var ipct = new ImpactDataRecord { FormId = 0x01000800, EditorId = "TestIpct" };
        PlannerTier1ParityHelper.AssertNewRecordParity("IPCT", ipct.FormId, ipct, IpctEncoder.EncodeNew(ipct));
    }

    [Fact]
    public void New_Ltex_Parity()
    {
        var ltex = new LandscapeTextureRecord { FormId = 0x01000800, EditorId = "TestLtex" };
        PlannerTier1ParityHelper.AssertNewRecordParity("LTEX", ltex.FormId, ltex, LtexEncoder.EncodeNew(ltex));
    }

    [Fact]
    public void New_Micn_Parity()
    {
        var micn = new MenuIconRecord { FormId = 0x01000800, EditorId = "TestMicn" };
        PlannerTier1ParityHelper.AssertNewRecordParity("MICN", micn.FormId, micn, MicnEncoder.EncodeNew(micn));
    }

    [Fact]
    public void New_Musc_Parity()
    {
        var musc = new MusicTypeRecord { FormId = 0x01000800, EditorId = "TestMusc" };
        PlannerTier1ParityHelper.AssertNewRecordParity("MUSC", musc.FormId, musc, MuscEncoder.EncodeNew(musc));
    }

    [Fact]
    public void New_Rcct_Parity()
    {
        var rcct = new RecipeCategoryRecord { FormId = 0x01000800, EditorId = "TestRcct" };
        PlannerTier1ParityHelper.AssertNewRecordParity("RCCT", rcct.FormId, rcct, RcctEncoder.EncodeNew(rcct));
    }

    [Fact]
    public void New_Txst_Parity()
    {
        var txst = new TextureSetRecord { FormId = 0x01000800, EditorId = "TestTxst" };
        PlannerTier1ParityHelper.AssertNewRecordParity("TXST", txst.FormId, txst, TxstEncoder.EncodeNew(txst));
    }

    [Fact]
    public void New_Acti_Parity()
    {
        var acti = new ActivatorRecord { FormId = 0x01000800, EditorId = "TestActi" };
        PlannerTier1ParityHelper.AssertNewRecordParity("ACTI", acti.FormId, acti, ActiEncoder.EncodeNew(acti));
    }

    [Fact]
    public void New_Debr_Parity()
    {
        var debr = new DebrisRecord { FormId = 0x01000800, EditorId = "TestDebr" };
        PlannerTier1ParityHelper.AssertNewRecordParity("DEBR", debr.FormId, debr, DebrEncoder.EncodeNew(debr));
    }

    [Fact]
    public void New_Csty_Parity()
    {
        var csty = new CombatStyleRecord { FormId = 0x01000800, EditorId = "TestCsty" };
        PlannerTier1ParityHelper.AssertNewRecordParity("CSTY", csty.FormId, csty, CstyEncoder.EncodeNew(csty));
    }
}
