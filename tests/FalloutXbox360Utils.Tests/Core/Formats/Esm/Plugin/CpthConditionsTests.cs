using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     CPTH CTDA emission tests (Phase 4.2d). Mirrors the IDLE pattern but without
///     the never-fire fallback — camera paths are visual-only so emitting an
///     unconditional path when no conditions were captured is benign.
/// </summary>
public class CpthConditionsTests
{
    [Fact]
    public void EncodeNew_emits_zero_CTDAs_when_Conditions_empty()
    {
        var cpth = MakeCpth();

        var encoded = CpthEncoder.EncodeNew(cpth);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CTDA");
    }

    [Fact]
    public void EncodeNew_emits_real_CTDAs_when_Conditions_populated()
    {
        var cpth = MakeCpth(conditions:
        [
            new DialogueCondition
            {
                Type = 0x00,
                ComparisonValue = 1.0f,
                FunctionIndex = 0x48,    // GetIsID
                Parameter1 = 0x000ED239u,
                RunOn = 0
            }
        ]);

        var encoded = CpthEncoder.EncodeNew(cpth);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal((ushort)0x0048, BinaryPrimitives.ReadUInt16LittleEndian(ctda.Bytes.AsSpan(8, 2)));
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void EncodeNew_emits_multiple_CTDAs_preserving_order()
    {
        var cpth = MakeCpth(conditions:
        [
            new DialogueCondition { FunctionIndex = 0x48, Parameter1 = 0x000ED239u },
            new DialogueCondition { FunctionIndex = 0x14, Parameter1 = 0x0001D9D5u },
            new DialogueCondition { FunctionIndex = 0x0E, Parameter1 = 5u }
        ]);

        var encoded = CpthEncoder.EncodeNew(cpth);

        var ctdas = encoded.Subrecords.Where(s => s.Signature == "CTDA").ToList();
        Assert.Equal(3, ctdas.Count);
        Assert.Equal((ushort)0x0048, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[0].Bytes.AsSpan(8, 2)));
        Assert.Equal((ushort)0x0014, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[1].Bytes.AsSpan(8, 2)));
        Assert.Equal((ushort)0x000E, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[2].Bytes.AsSpan(8, 2)));
    }

    [Fact]
    public void EncodeNew_CTDA_appears_before_ANAM_in_fopdoc_canonical_order()
    {
        var cpth = MakeCpth(conditions:
        [
            new DialogueCondition { FunctionIndex = 0x48, Parameter1 = 0x000ED239u }
        ]);

        var encoded = CpthEncoder.EncodeNew(cpth);

        var ctdaIdx = encoded.Subrecords
            .Select((s, i) => (s.Signature, i)).First(p => p.Signature == "CTDA").i;
        var anamIdx = encoded.Subrecords
            .Select((s, i) => (s.Signature, i)).First(p => p.Signature == "ANAM").i;
        Assert.True(ctdaIdx < anamIdx, $"CTDA must precede ANAM (CTDA at {ctdaIdx}, ANAM at {anamIdx}).");
    }

    [Fact]
    public void EncodeNew_sanitizes_dangling_CTDA_FormID_param_when_validFormIds_supplied()
    {
        // GetIsID 0x000DEAD1 — dangling FormID parameter. ConditionSanitizer drops the
        // whole condition. CPTH has no never-fire fallback so the CTDA simply disappears.
        var cpth = MakeCpth(conditions:
        [
            new DialogueCondition { FunctionIndex = 0x48, Parameter1 = 0x000DEAD1u }
        ]);
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = CpthEncoder.EncodeNew(cpth, validFormIds: valid);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Contains(encoded.Warnings, w => w.Contains("CTDA sanitizer"));
    }

    [Fact]
    public void EncodeNew_remaps_CTDA_FormID_param_when_remap_available()
    {
        var cpth = MakeCpth(conditions:
        [
            new DialogueCondition { FunctionIndex = 0x48, Parameter1 = 0x01999AAAu }
        ]);
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = CpthEncoder.EncodeNew(cpth, validFormIds: valid, remapTable: remap);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void EncodeNew_warns_when_ConditionCount_set_but_Conditions_empty()
    {
        // Stale model: count populated but list empty (legacy ESM scan path before 4.2d
        // wired CTDA decoding). Surface the gap as a warning so it's visible in conversion logs.
        var cpth = MakeCpth() with { ConditionCount = 3 };

        var encoded = CpthEncoder.EncodeNew(cpth);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Contains(encoded.Warnings,
            w => w.Contains("3 CTDA condition(s) captured") && w.Contains("Conditions list is empty"));
    }

    private static CameraPathRecord MakeCpth(List<DialogueCondition>? conditions = null)
    {
        return new CameraPathRecord
        {
            FormId = 0x01005C00,
            EditorId = "TestCameraPath",
            Flags = 0x01,
            ParentPathFormId = 0u,
            PreviousPathFormId = 0u,
            CameraShotFormIds = [],
            Conditions = conditions ?? []
        };
    }
}
