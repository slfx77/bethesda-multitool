using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v20 regression tests for bugs surfaced during the first in-game load of
///     v19-validation.esp:
///     1. ArmoEncoder/ArmaEncoder placed BMDT after MODL, tripping the FNV runtime's
///        post-model BMDT_ID size table (max=4) and truncating GeneralFlags.
///     2. NpcEncoder zero-filled ACBS when Stats was null, producing the "Speed
///        multiplier is zero" load-time warning on dozens of NPCs.
///     3. AlchEncoder logged a deferred warning instead of emitting EFID/EFIT pairs,
///        causing "Effect Item has no effects defined" on every food/aid item.
///     4. ScptEncoder emitted stub SCPT records with empty EditorID + no bytecode,
///        triggering "Script '' has not been compiled" at load.
///     5. InfoEncoder emitted PNAM for sentinel values (0 / 0xFFFFFFFF / self),
///        causing "Could not find previous info" and broken dialog chain walks.
/// </summary>
public class V20BugfixTests
{
    // ====================================================================================
    // ArmoEncoder / ArmaEncoder BMDT ordering
    // ====================================================================================

    [Fact]
    public void ArmoEncoder_EncodeNew_PlacesBmdtBeforeModl()
    {
        var armo = new ArmorRecord
        {
            FormId = 0x800,
            EditorId = "ArmorBmdtOrder",
            FullName = "Test Armor",
            ModelPath = "armor\\test.nif",
            BipedFlags = 0x4u,
            GeneralFlags = 0x10
        };

        var encoded = ArmoEncoder.EncodeNew(armo);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        var bmdtIndex = sigs.IndexOf("BMDT");
        var modlIndex = sigs.IndexOf("MODL");

        Assert.True(bmdtIndex >= 0, "BMDT must be emitted");
        Assert.True(modlIndex >= 0, "MODL must be emitted");
        Assert.True(bmdtIndex < modlIndex,
            $"BMDT must precede MODL (got BMDT@{bmdtIndex}, MODL@{modlIndex})");
    }

    [Fact]
    public void ArmaEncoder_EncodeNew_PlacesBmdtBeforeModl()
    {
        var arma = new ArmaRecord
        {
            FormId = 0x800,
            EditorId = "ArmaBmdtOrder",
            FullName = "Test Armor Addon",
            MaleModelPath = "armor\\test_m.nif",
            FemaleModelPath = "armor\\test_f.nif",
            BipedFlags = 0x4u,
            GeneralFlags = 0x10
        };

        var encoded = ArmaEncoder.EncodeNew(arma);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        var bmdtIndex = sigs.IndexOf("BMDT");
        var modlIndex = sigs.IndexOf("MODL");

        Assert.True(bmdtIndex >= 0, "BMDT must be emitted");
        Assert.True(modlIndex >= 0, "MODL must be emitted");
        Assert.True(bmdtIndex < modlIndex,
            $"BMDT must precede MODL (got BMDT@{bmdtIndex}, MODL@{modlIndex})");
    }

    // ====================================================================================
    // NpcEncoder ACBS defaults
    // ====================================================================================

    [Fact]
    public void NpcEncoder_EncodeNew_StatsNull_EmitsAcbsWithDefaults()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "NpcNoStats",
            Stats = null
        };

        var encoded = NpcEncoder.EncodeNew(npc);
        var acbs = Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS");

        Assert.Equal(24, acbs.Bytes.Length);
        // Level (int16 at offset 8) should be 1 (engine default for spawnable NPC).
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(acbs.Bytes.AsSpan(8, 2)));
        // SpeedMult (uint16 at offset 14) should be 100 (normal walk/run speed).
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2)));
    }

    [Fact]
    public void NpcEncoder_EncodeNew_StatsWithZeroSpeedMult_BumpsTo100()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "NpcZeroSpeed",
            Stats = new ActorBaseSubrecord(
                Flags: 0,
                FatigueBase: 50,
                BarterGold: 200,
                Level: 5,
                CalcMin: 1,
                CalcMax: 10,
                SpeedMultiplier: 0,
                KarmaAlignment: 0f,
                DispositionBase: 50,
                TemplateFlags: 0,
                Offset: 0,
                IsBigEndian: false)
        };

        var encoded = NpcEncoder.EncodeNew(npc);
        var acbs = Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS");

        // Source said 0; encoder must rewrite to 100 so the engine doesn't refuse to move the NPC.
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2)));
        // Other fields preserved verbatim.
        Assert.Equal((short)5, BinaryPrimitives.ReadInt16LittleEndian(acbs.Bytes.AsSpan(8, 2)));
        Assert.Equal((ushort)200, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(6, 2)));
    }

    [Fact]
    public void NpcEncoder_EncodeNew_StatsWithNonZeroSpeedMult_PreservesValue()
    {
        var npc = new NpcRecord
        {
            FormId = 0x800,
            EditorId = "NpcFastSpeed",
            Stats = new ActorBaseSubrecord(
                Flags: 0,
                FatigueBase: 0,
                BarterGold: 0,
                Level: 1,
                CalcMin: 1,
                CalcMax: 1,
                SpeedMultiplier: 150,
                KarmaAlignment: 0f,
                DispositionBase: 0,
                TemplateFlags: 0,
                Offset: 0,
                IsBigEndian: false)
        };

        var encoded = NpcEncoder.EncodeNew(npc);
        var acbs = Assert.Single(encoded.Subrecords, s => s.Signature == "ACBS");
        Assert.Equal((ushort)150, BinaryPrimitives.ReadUInt16LittleEndian(acbs.Bytes.AsSpan(14, 2)));
    }

    // ====================================================================================
    // AlchEncoder EFID/EFIT effect emission
    // ====================================================================================

    [Fact]
    public void AlchEncoder_EncodeNew_WithEffects_EmitsEfidEfitPairs()
    {
        var alch = new ConsumableRecord
        {
            FormId = 0x800,
            EditorId = "FoodWithEffects",
            Weight = 0.5f,
            Effects =
            [
                new EnchantmentEffect
                {
                    EffectFormId = 0x1234,
                    Magnitude = 10f,
                    Area = 0,
                    Duration = 60,
                    Type = 0,
                    ActorValue = 24
                },
                new EnchantmentEffect
                {
                    EffectFormId = 0x5678,
                    Magnitude = 5f,
                    Area = 0,
                    Duration = 30,
                    Type = 0,
                    ActorValue = 25
                }
            ]
        };

        var encoded = AlchEncoder.EncodeNew(alch);

        var efids = encoded.Subrecords.Where(s => s.Signature == "EFID").ToList();
        var efits = encoded.Subrecords.Where(s => s.Signature == "EFIT").ToList();

        Assert.Equal(2, efids.Count);
        Assert.Equal(2, efits.Count);

        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32LittleEndian(efids[0].Bytes));
        Assert.Equal(0x5678u, BinaryPrimitives.ReadUInt32LittleEndian(efids[1].Bytes));

        // EFIT is 20 bytes (Magnitude/Area/Duration/Type/ActorValue).
        Assert.Equal(20, efits[0].Bytes.Length);
        Assert.Equal(10f, BinaryPrimitives.ReadSingleLittleEndian(efits[0].Bytes.AsSpan(0, 4)));
        Assert.Equal(60u, BinaryPrimitives.ReadUInt32LittleEndian(efits[0].Bytes.AsSpan(8, 4)));
        Assert.Equal(24, BinaryPrimitives.ReadInt32LittleEndian(efits[0].Bytes.AsSpan(16, 4)));
    }

    [Fact]
    public void AlchEncoder_EncodeNew_WithEffects_DoesNotWarnAboutDeferredEffects()
    {
        var alch = new ConsumableRecord
        {
            FormId = 0x800,
            EditorId = "Food",
            Effects = [new EnchantmentEffect { EffectFormId = 0x1234 }]
        };

        var encoded = AlchEncoder.EncodeNew(alch);
        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("deferred"));
    }

    [Fact]
    public void AlchEncoder_EncodeNew_NoEffects_OmitsEfidEfit()
    {
        var alch = new ConsumableRecord { FormId = 0x800, EditorId = "Food", Weight = 0.5f };
        var encoded = AlchEncoder.EncodeNew(alch);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "EFID");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "EFIT");
    }

    // ====================================================================================
    // ScptEncoder stub-script filtering
    // ====================================================================================

    [Fact]
    public void ScptEncoder_EncodeNew_EmptyStub_ReturnsEmptySubrecords()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = null,
            CompiledData = null,
            Variables = [],
            ReferencedObjects = []
        };

        var encoded = ScptEncoder.EncodeNew(script);

        // No subrecords → PluginBuilder will skip the record entirely.
        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, w => w.Contains("stub"));
    }

    [Fact]
    public void ScptEncoder_EncodeNew_HasEditorId_EmitsRecord()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = "TestScript"
        };

        var encoded = ScptEncoder.EncodeNew(script);
        Assert.Contains(encoded.Subrecords, s => s.Signature == "EDID");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "SCHR");
    }

    [Fact]
    public void ScptEncoder_EncodeNew_HasCompiledDataButNoEditorId_EmitsRecord()
    {
        var script = new ScriptRecord
        {
            FormId = 0x800,
            EditorId = null,
            CompiledData = new byte[] { 0x01, 0x02, 0x03 },
            IsCompiled = true,
            CompiledSize = 3
        };

        var encoded = ScptEncoder.EncodeNew(script);
        Assert.Contains(encoded.Subrecords, s => s.Signature == "SCDA");
    }

    // ====================================================================================
    // InfoEncoder defensive PNAM filtering
    // ====================================================================================

    [Fact]
    public void InfoEncoder_EncodeNew_PreviousInfoZero_OmitsPnam()
    {
        var info = new DialogueRecord { FormId = 0x800, PreviousInfo = 0u };
        var encoded = InfoEncoder.EncodeNew(info);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "PNAM");
    }

    [Fact]
    public void InfoEncoder_EncodeNew_PreviousInfoSentinelFFFFFFFF_OmitsPnam()
    {
        var info = new DialogueRecord { FormId = 0x800, PreviousInfo = 0xFFFFFFFFu };
        var encoded = InfoEncoder.EncodeNew(info);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "PNAM");
    }

    [Fact]
    public void InfoEncoder_EncodeNew_PreviousInfoSelfReference_OmitsPnam()
    {
        var info = new DialogueRecord { FormId = 0x800, PreviousInfo = 0x800u };
        var encoded = InfoEncoder.EncodeNew(info);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "PNAM");
    }

    [Fact]
    public void InfoEncoder_EncodeNew_PreviousInfoValid_EmitsPnam()
    {
        var info = new DialogueRecord { FormId = 0x800, PreviousInfo = 0x12345u };
        var encoded = InfoEncoder.EncodeNew(info);
        var pnam = Assert.Single(encoded.Subrecords, s => s.Signature == "PNAM");
        Assert.Equal(0x12345u, BinaryPrimitives.ReadUInt32LittleEndian(pnam.Bytes));
    }
}
