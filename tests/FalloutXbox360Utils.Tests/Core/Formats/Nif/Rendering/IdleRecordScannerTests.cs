using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class IdleRecordScannerTests(SampleFileFixture samples)
{
    private const uint NpcWeaponIdlesFormId = 0x0005C364;
    private const uint VatsPowerFistLowFormId = 0x000C1105;
    private const uint VatsPowerFistRootFormId = 0x000C1103;
    private const uint NpcOnlyIdlesFormId = 0x0001E60F;
    private const uint Npc2hrIdlesFormId = 0x00025572;
    private const uint Npc2haIdlesFormId = 0x000289B9;

    [Fact]
    public void IdleDataSchema_ReadFields_ParsesXboxAndPcLayouts()
    {
        var xboxFields = SubrecordDataReader.ReadFields(
            "DATA",
            "IDLE",
            [0x07, 0x01, 0x02, 0x00, 0x12, 0x34, 0x56, 0x00],
            true);

        Assert.Equal((byte)0x07, SubrecordDataReader.GetByte(xboxFields, "AnimData"));
        Assert.Equal((byte)0x01, SubrecordDataReader.GetByte(xboxFields, "LoopMin"));
        Assert.Equal((byte)0x02, SubrecordDataReader.GetByte(xboxFields, "LoopMax"));
        Assert.Equal((ushort)0x1234, SubrecordDataReader.GetUInt16(xboxFields, "ReplayDelay"));
        Assert.Equal((byte)0x56, SubrecordDataReader.GetByte(xboxFields, "FlagsEx"));

        var pcFields = SubrecordDataReader.ReadFields(
            "DATA",
            "IDLE",
            [0x07, 0x01, 0x02, 0x00, 0x34, 0x12],
            false);

        Assert.Equal((byte)0x07, SubrecordDataReader.GetByte(pcFields, "AnimData"));
        Assert.Equal((byte)0x01, SubrecordDataReader.GetByte(pcFields, "LoopMin"));
        Assert.Equal((byte)0x02, SubrecordDataReader.GetByte(pcFields, "LoopMax"));
        Assert.Equal((ushort)0x1234, SubrecordDataReader.GetUInt16(pcFields, "ReplayDelay"));
        Assert.Equal((byte)0x00, SubrecordDataReader.GetByte(pcFields, "FlagsEx"));
    }

    [Fact]
    public void IdleIndexBuilder_FinalXboxEsm_IndexesWeaponIdleRootsAndPowerFistLeaf()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var esm = EsmFileLoader.Load(samples.Xbox360FinalEsm!, false);
        Assert.NotNull(esm);

        var index = NpcAppearanceIndexBuilder.Build(esm!.Data, esm.IsBigEndian);

        var npcWeaponIdles = Assert.Contains(NpcWeaponIdlesFormId, index.Idles);
        Assert.Equal("NPCWeaponIdles", npcWeaponIdles.EditorId);
        Assert.Equal(@"Characters\_Male\IdleAnims", npcWeaponIdles.ModelPath);
        Assert.Equal(NpcOnlyIdlesFormId, npcWeaponIdles.ParentIdleFormId);
        Assert.Null(npcWeaponIdles.PreviousIdleFormId);
        Assert.Equal((byte)0x07, npcWeaponIdles.AnimData);

        var vatsPowerFistLow = Assert.Contains(VatsPowerFistLowFormId, index.Idles);
        Assert.Equal("VATSPowerFistLow", vatsPowerFistLow.EditorId);
        Assert.Equal(
            @"Characters\_Male\IdleAnims\VATSH2HAttackPower_PowerFistLow.kf",
            vatsPowerFistLow.ModelPath);
        Assert.Equal(VatsPowerFistRootFormId, vatsPowerFistLow.ParentIdleFormId);
        Assert.Equal((byte)0x04, vatsPowerFistLow.AnimData);

        var weaponChildren = Assert.Contains(NpcWeaponIdlesFormId, index.IdleChildrenByParent);
        Assert.Contains(Npc2hrIdlesFormId, weaponChildren);
        Assert.Contains(Npc2haIdlesFormId, weaponChildren);
    }

    [Fact]
    public void IdleIndexBuilder_FinalXboxEsm_PowerFistIdlesAreVatsOnly()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var esm = EsmFileLoader.Load(samples.Xbox360FinalEsm!, false);
        Assert.NotNull(esm);

        var index = NpcAppearanceIndexBuilder.Build(esm!.Data, esm.IsBigEndian);

        var powerFistIdles = index.Idles.Values
            .Where(idle => !string.IsNullOrWhiteSpace(idle.EditorId) &&
                           idle.EditorId.Contains("PowerFist", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(powerFistIdles);
        Assert.All(
            powerFistIdles,
            idle => Assert.StartsWith("VATS", idle.EditorId!, StringComparison.OrdinalIgnoreCase));
    }
}