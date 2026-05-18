using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v18 tests covering the three large encoders: MGEF, WRLD, RACE.
/// </summary>
public class V18EncoderTests
{
    // ====================================================================================
    // MgefEncoder
    // ====================================================================================

    [Fact]
    public void MgefEncoder_EncodeNew_DataIs72Bytes()
    {
        var mgef = new BaseEffectRecord { FormId = 0x3100, EditorId = "FireRes" };
        var encoded = MgefEncoder.EncodeNew(mgef);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(72, data.Length);
    }

    [Fact]
    public void MgefEncoder_EncodeNew_DataLayoutMatchesPdb()
    {
        var mgef = new BaseEffectRecord
        {
            FormId = 0x3100,
            EditorId = "FireDmg",
            Flags = 0x12345678,
            BaseCost = 2.5f,
            AssociatedItem = 0xABC,
            MagicSchool = 1,
            ResistValue = 5,
            LightFormId = 0x100,
            ProjectileSpeed = 8000.0f,
            EffectShaderFormId = 0x200,
            EnchantEffectFormId = 0x300,
            CastingSoundFormId = 0x400,
            BoltSoundFormId = 0x500,
            HitSoundFormId = 0x600,
            AreaSoundFormId = 0x700,
            CEEnchantFactor = 1.5f,
            CEBarterFactor = 2.0f,
            Archetype = 5, // Absorb
            ActorValue = 10
        };

        var encoded = MgefEncoder.EncodeNew(mgef);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4, 4)));
        Assert.Equal(0xABCu, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12, 4)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16, 4)));
        Assert.Equal(0x100u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24, 4)));
        Assert.Equal(8000.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(28, 4)));
        Assert.Equal(0x700u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(52, 4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(56, 4)));
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(64, 4))); // Archetype
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(68, 4)));
    }

    [Fact]
    public void MgefEncoder_EncodeNew_CanonicalOrder()
    {
        var mgef = new BaseEffectRecord
        {
            FormId = 0x3100,
            EditorId = "T",
            FullName = "Fire",
            Description = "Burns",
            Icon = "icons/fire.dds",
            ModelPath = "fire.nif"
        };
        var encoded = MgefEncoder.EncodeNew(mgef);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "FULL", "DESC", "ICON", "MODL", "DATA"], sigs);
    }

    // ====================================================================================
    // WrldEncoder
    // ====================================================================================

    [Fact]
    public void WrldEncoder_EncodeNew_CanonicalOrder()
    {
        var wrld = new WorldspaceRecord
        {
            FormId = 0x3200,
            EditorId = "Wasteland",
            FullName = "The Mojave",
            EncounterZoneFormId = 0xAA,
            ParentWorldspaceFormId = 0xBB,
            ParentUseFlags = 0x07,
            ClimateFormId = 0xCC,
            WaterFormId = 0xDD,
            MapUsableWidth = 10,
            MapUsableHeight = 10,
            MapNWCellX = -5,
            MapNWCellY = 5,
            MapSECellX = 5,
            MapSECellY = -5,
            Flags = 0x01,
            BoundsMinX = -1000f,
            BoundsMinY = -1000f,
            BoundsMaxX = 1000f,
            BoundsMaxY = 1000f,
            MapOffsetScaleX = 1.0f,
            MapOffsetScaleY = 1.0f,
            MapOffsetZ = 0f,
            ImageSpaceFormId = 0xEE,
            MusicTypeFormId = 0xFF,
            DefaultLandHeight = -2048f,
            DefaultWaterHeight = 0f
        };

        var encoded = WrldEncoder.EncodeNew(wrld);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        // FNVEdit canonical wbWRLD order (confirmed against master WastelandNV):
        // DNAM clusters with the water subrecords (right after NAM2); ONAM/INAM go between
        // MNAM and DATA; PNAM is paired with WNAM (this fixture supplies both).
        Assert.Equal(
            ["EDID", "FULL", "XEZN", "WNAM", "PNAM", "CNAM", "NAM2", "DNAM", "MNAM",
                "ONAM", "INAM", "DATA", "NAM0", "NAM9", "ZNAM"],
            sigs);
    }

    [Fact]
    public void WrldEncoder_EncodeNew_MnamLayoutMatchesPdb()
    {
        var wrld = new WorldspaceRecord
        {
            FormId = 0x3200,
            EditorId = "W",
            MapUsableWidth = 100,
            MapUsableHeight = 50,
            MapNWCellX = -10,
            MapNWCellY = 10,
            MapSECellX = 10,
            MapSECellY = -10
        };

        var encoded = WrldEncoder.EncodeNew(wrld);
        var mnam = Assert.Single(encoded.Subrecords, s => s.Signature == "MNAM").Bytes;

        Assert.Equal(16, mnam.Length);
        Assert.Equal(100, BinaryPrimitives.ReadInt32LittleEndian(mnam.AsSpan(0, 4)));
        Assert.Equal(50, BinaryPrimitives.ReadInt32LittleEndian(mnam.AsSpan(4, 4)));
        Assert.Equal((short)-10, BinaryPrimitives.ReadInt16LittleEndian(mnam.AsSpan(8, 2)));
        Assert.Equal((short)10, BinaryPrimitives.ReadInt16LittleEndian(mnam.AsSpan(10, 2)));
        Assert.Equal((short)10, BinaryPrimitives.ReadInt16LittleEndian(mnam.AsSpan(12, 2)));
        Assert.Equal((short)-10, BinaryPrimitives.ReadInt16LittleEndian(mnam.AsSpan(14, 2)));
    }

    [Fact]
    public void WrldEncoder_EncodeNew_DnamHeightsLayout()
    {
        var wrld = new WorldspaceRecord
        {
            FormId = 0x3200,
            EditorId = "W",
            DefaultLandHeight = -1024f,
            DefaultWaterHeight = 128f
        };

        var encoded = WrldEncoder.EncodeNew(wrld);
        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM").Bytes;

        Assert.Equal(8, dnam.Length);
        Assert.Equal(-1024f, BinaryPrimitives.ReadSingleLittleEndian(dnam.AsSpan(0, 4)));
        Assert.Equal(128f, BinaryPrimitives.ReadSingleLittleEndian(dnam.AsSpan(4, 4)));
    }

    [Fact]
    public void WrldEncoder_EncodeNew_WarnsOnChildCells()
    {
        var wrld = new WorldspaceRecord
        {
            FormId = 0x3200,
            EditorId = "W",
            Cells = [new CellRecord { FormId = 0x100 }]
        };
        var encoded = WrldEncoder.EncodeNew(wrld);
        Assert.Contains(encoded.Warnings, w => w.Contains("child cell"));
    }

    [Fact]
    public void WrldEncoder_EncodeNew_OmitsAllOptionalsWhenAbsent()
    {
        var wrld = new WorldspaceRecord { FormId = 0x3200, EditorId = "Empty" };
        var encoded = WrldEncoder.EncodeNew(wrld);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID"], sigs);
    }

    // ====================================================================================
    // RaceEncoder
    // ====================================================================================

    [Fact]
    public void RaceEncoder_EncodeNew_DataIs36Bytes()
    {
        var race = new RaceRecord
        {
            FormId = 0x3300,
            EditorId = "Caucasian",
            MaleHeight = 1.0f,
            FemaleHeight = 0.95f,
            MaleWeight = 1.0f,
            FemaleWeight = 1.0f,
            DataFlags = 0x01,
            SkillBoosts =
            [
                (3, 5),
                (7, 2),
                (-1, 0)
            ]
        };

        var encoded = RaceEncoder.EncodeNew(race);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal(36, data.Length);
        Assert.Equal(3, (sbyte)data[0]);   // First skill boost index
        Assert.Equal(5, (sbyte)data[1]);   // First skill boost value
        Assert.Equal(7, (sbyte)data[2]);
        Assert.Equal(2, (sbyte)data[3]);
        Assert.Equal(-1, (sbyte)data[4]);
        Assert.Equal(0, (sbyte)data[5]);
        // Remaining skill slots (indices 3-6) should be -1 sentinel padding.
        Assert.Equal(-1, (sbyte)data[6]);
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(16, 4)));
        Assert.Equal(0.95f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(20, 4)));
        Assert.Equal(0x01u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(32, 4)));
    }

    [Fact]
    public void RaceEncoder_EncodeNew_VtckPairsMaleAndFemale()
    {
        var race = new RaceRecord
        {
            FormId = 0x3300,
            EditorId = "T",
            MaleVoiceFormId = 0x111,
            FemaleVoiceFormId = 0x222
        };

        var encoded = RaceEncoder.EncodeNew(race);
        var vtck = Assert.Single(encoded.Subrecords, s => s.Signature == "VTCK").Bytes;
        Assert.Equal(8, vtck.Length);
        Assert.Equal(0x111u, BinaryPrimitives.ReadUInt32LittleEndian(vtck.AsSpan(0, 4)));
        Assert.Equal(0x222u, BinaryPrimitives.ReadUInt32LittleEndian(vtck.AsSpan(4, 4)));
    }

    [Fact]
    public void RaceEncoder_EncodeNew_BodyPartsEmitNam0Nam1WithIndxMembers()
    {
        var race = new RaceRecord
        {
            FormId = 0x3300,
            EditorId = "T",
            MaleHeadModelPath = "characters/head_m.nif",
            MaleHeadTexturePath = "characters/head_m.dds",
            MaleMouthModelPath = "characters/mouth_m.nif",
            MaleUpperBodyPath = "characters/body_m.nif",
            MaleBodyTexturePath = "characters/body_m.dds",
            MaleLeftHandPath = "characters/lhand.nif",
            MaleRightHandPath = "characters/rhand.nif"
        };

        var encoded = RaceEncoder.EncodeNew(race);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        var nam0Idx = sigs.IndexOf("NAM0");
        var nam1Idx = sigs.IndexOf("NAM1");
        Assert.True(nam0Idx >= 0 && nam1Idx > nam0Idx);

        // Each body part block carries INDX/MODL/ICON groups; total INDX count = head parts (2: head+mouth)
        // + body parts (3: upper body + left hand + right hand) = 5.
        Assert.Equal(5, sigs.Count(s => s == "INDX"));
    }

    [Fact]
    public void RaceEncoder_EncodeNew_FaceGenMorphsEmitMnamAndFnamSections()
    {
        var race = new RaceRecord
        {
            FormId = 0x3300,
            EditorId = "T",
            MaleFaceGenGeometrySymmetric = new float[50],
            MaleFaceGenGeometryAsymmetric = new float[30],
            MaleFaceGenTextureSymmetric = new float[50],
            FemaleFaceGenGeometrySymmetric = new float[50],
            FemaleFaceGenGeometryAsymmetric = new float[30],
            FemaleFaceGenTextureSymmetric = new float[50]
        };

        var encoded = RaceEncoder.EncodeNew(race);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        // Layout: ... MNAM, FGGS(200B), FGGA(120B), FGTS(200B), FNAM, FGGS, FGGA, FGTS.
        var mnamIdx = sigs.IndexOf("MNAM");
        var fnamIdx = sigs.IndexOf("FNAM");
        Assert.True(mnamIdx >= 0 && fnamIdx > mnamIdx);

        // Three FGGS/FGGA/FGTS pairs each follow their gender marker.
        Assert.Equal(2, sigs.Count(s => s == "FGGS"));
        Assert.Equal(2, sigs.Count(s => s == "FGGA"));
        Assert.Equal(2, sigs.Count(s => s == "FGTS"));

        // FGGS = 200 bytes (50 floats), FGGA = 120 bytes (30 floats).
        var fggs = encoded.Subrecords.First(s => s.Signature == "FGGS");
        var fgga = encoded.Subrecords.First(s => s.Signature == "FGGA");
        Assert.Equal(200, fggs.Bytes.Length);
        Assert.Equal(120, fgga.Bytes.Length);
    }

    [Fact]
    public void RaceEncoder_EncodeNew_HairAndEyeFormIdLists()
    {
        var race = new RaceRecord
        {
            FormId = 0x3300,
            EditorId = "T",
            HairStyleFormIds = [0x111, 0x222, 0x333],
            EyeColorFormIds = [0x444, 0x555]
        };

        var encoded = RaceEncoder.EncodeNew(race);

        Assert.Equal(3, encoded.Subrecords.Count(s => s.Signature == "HNAM"));
        Assert.Equal(2, encoded.Subrecords.Count(s => s.Signature == "ENAM"));
    }

    [Fact]
    public void RaceEncoder_EncodeNew_AllSkillBoostSlotsFilledWithNegOneSentinel()
    {
        var race = new RaceRecord { FormId = 0x3300, EditorId = "Empty", SkillBoosts = [] };
        var encoded = RaceEncoder.EncodeNew(race);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        // All 7 skill slots default to -1 sentinel (boost 0).
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(-1, (sbyte)data[i * 2]);
            Assert.Equal(0, (sbyte)data[i * 2 + 1]);
        }
    }

    // ====================================================================================
    // Cross-encoder warning check
    // ====================================================================================

    [Fact]
    public void AllV18Encoders_EmitWarningWhenEditorIdMissing()
    {
        Assert.Contains(MgefEncoder.EncodeNew(new BaseEffectRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(WrldEncoder.EncodeNew(new WorldspaceRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(RaceEncoder.EncodeNew(new RaceRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
    }
}
