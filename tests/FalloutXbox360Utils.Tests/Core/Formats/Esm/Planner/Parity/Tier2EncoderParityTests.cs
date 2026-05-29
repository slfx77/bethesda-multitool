using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Parity;

/// <summary>
///     Tier 2 byte-exact parity. Each test feeds a synthetic record (with no outgoing FormID
///     refs so legacy and planner produce identical bytes regardless of validFormIds /
///     remapTable contents) through PlanWriter, replays the legacy primitives directly,
///     and asserts byte equality.
/// </summary>
public sealed class Tier2EncoderParityTests
{
    [Fact]
    public void New_Weap_With_No_Refs_GRUP_Bytes_Match_Legacy()
    {
        // No outgoing FormID refs: ammo/projectile/etc. all null, no critical effect.
        var weap = new WeaponRecord
        {
            FormId = 0x01000800,
            EditorId = "TestWeapon",
            FullName = "Test Weapon",
            ModelPath = "weapons/test/test.nif",
            Value = 100,
            Health = 200,
            Weight = 3.0f,
            Damage = 25,
            ClipSize = 12,
        };

        var legacy = WeapEncoder.EncodeNew(weap, validFormIds: null, remapTable: null);
        PlannerTier1ParityHelper.AssertNewRecordParity("WEAP", weap.FormId, weap, legacy);
    }

    [Fact]
    public void New_Door_GRUP_Bytes_Match_Legacy()
    {
        var door = new DoorRecord
        {
            FormId = 0x01000800,
            EditorId = "TestDoor",
            FullName = "Test Door",
            ModelPath = "doors/test/test.nif",
            Flags = 0x02,
        };

        var legacy = DoorEncoder.EncodeNew(door);
        PlannerTier1ParityHelper.AssertNewRecordParity("DOOR", door.FormId, door, legacy);
    }

    [Fact]
    public void New_Misc_GRUP_Bytes_Match_Legacy()
    {
        var misc = new MiscItemRecord
        {
            FormId = 0x01000800,
            EditorId = "TestMisc",
            FullName = "Test Misc",
            ModelPath = "misc/test/test.nif",
            Value = 5,
            Weight = 0.1f,
        };

        var legacy = MiscEncoder.EncodeNew(misc);
        PlannerTier1ParityHelper.AssertNewRecordParity("MISC", misc.FormId, misc, legacy);
    }

    [Fact]
    public void New_Keym_GRUP_Bytes_Match_Legacy()
    {
        var key = new KeyRecord
        {
            FormId = 0x01000800,
            EditorId = "TestKey",
            FullName = "Test Key",
            ModelPath = "keys/test/test.nif",
            Value = 0,
            Weight = 0.0f,
        };

        var legacy = KeymEncoder.EncodeNew(key);
        PlannerTier1ParityHelper.AssertNewRecordParity("KEYM", key.FormId, key, legacy);
    }

    [Fact]
    public void New_Note_GRUP_Bytes_Match_Legacy()
    {
        var note = new NoteRecord
        {
            FormId = 0x01000800,
            EditorId = "TestNote",
            FullName = "Test Note",
            ModelPath = "notes/test/test.nif",
            NoteType = 0,
            Text = "Test contents.",
        };

        var legacy = NoteEncoder.EncodeNew(note);
        PlannerTier1ParityHelper.AssertNewRecordParity("NOTE", note.FormId, note, legacy);
    }

    [Fact]
    public void New_Rcpe_GRUP_Bytes_Match_Legacy()
    {
        var recipe = new RecipeRecord
        {
            FormId = 0x01000800,
            EditorId = "TestRecipe",
        };

        var legacy = RcpeEncoder.EncodeNew(recipe);
        PlannerTier1ParityHelper.AssertNewRecordParity("RCPE", recipe.FormId, recipe, legacy);
    }

    [Fact]
    public void New_Cobj_GRUP_Bytes_Match_Legacy()
    {
        var cobj = new ConstructibleObjectRecord
        {
            FormId = 0x01000800,
            EditorId = "TestCobj",
        };

        var legacy = CobjEncoder.EncodeNew(cobj);
        PlannerTier1ParityHelper.AssertNewRecordParity("COBJ", cobj.FormId, cobj, legacy);
    }

    [Fact]
    public void New_Arma_GRUP_Bytes_Match_Legacy()
    {
        var arma = new ArmaRecord
        {
            FormId = 0x01000800,
            EditorId = "TestArma",
        };

        var legacy = ArmaEncoder.EncodeNew(arma);
        PlannerTier1ParityHelper.AssertNewRecordParity("ARMA", arma.FormId, arma, legacy);
    }

    [Fact]
    public void New_Imod_GRUP_Bytes_Match_Legacy()
    {
        var imod = new WeaponModRecord
        {
            FormId = 0x01000800,
            EditorId = "TestImod",
            FullName = "Test Mod",
            ModelPath = "mods/test/test.nif",
            Value = 50,
            Weight = 1.0f,
        };

        var legacy = ImodEncoder.EncodeNew(imod);
        PlannerTier1ParityHelper.AssertNewRecordParity("IMOD", imod.FormId, imod, legacy);
    }

    [Fact]
    public void New_Ench_GRUP_Bytes_Match_Legacy()
    {
        var ench = new EnchantmentRecord
        {
            FormId = 0x01000800,
            EditorId = "TestEnch",
        };

        var legacy = EnchEncoder.EncodeNew(ench);
        PlannerTier1ParityHelper.AssertNewRecordParity("ENCH", ench.FormId, ench, legacy);
    }

    [Fact]
    public void New_Spel_GRUP_Bytes_Match_Legacy()
    {
        var spel = new SpellRecord
        {
            FormId = 0x01000800,
            EditorId = "TestSpel",
        };

        var legacy = SpelEncoder.EncodeNew(spel);
        PlannerTier1ParityHelper.AssertNewRecordParity("SPEL", spel.FormId, spel, legacy);
    }

    [Fact]
    public void New_Expl_GRUP_Bytes_Match_Legacy()
    {
        var expl = new ExplosionRecord
        {
            FormId = 0x01000800,
            EditorId = "TestExpl",
        };

        var legacy = ExplEncoder.EncodeNew(expl);
        PlannerTier1ParityHelper.AssertNewRecordParity("EXPL", expl.FormId, expl, legacy);
    }

    [Fact]
    public void New_Mgef_GRUP_Bytes_Match_Legacy()
    {
        var mgef = new BaseEffectRecord
        {
            FormId = 0x01000800,
            EditorId = "TestMgef",
        };

        var legacy = MgefEncoder.EncodeNew(mgef);
        PlannerTier1ParityHelper.AssertNewRecordParity("MGEF", mgef.FormId, mgef, legacy);
    }

    [Fact]
    public void New_Proj_GRUP_Bytes_Match_Legacy()
    {
        var proj = new ProjectileRecord
        {
            FormId = 0x01000800,
            EditorId = "TestProj",
        };

        var legacy = ProjEncoder.EncodeNew(proj);
        PlannerTier1ParityHelper.AssertNewRecordParity("PROJ", proj.FormId, proj, legacy);
    }
}
