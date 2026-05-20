using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     IDLE.ANAM (parent + previous idle FormIDs) sanitizer tests. Dangling parent-idle
///     refs trigger the engine's "Could not find parent idle" error and corrupt the idle
///     resolution tree, causing animations resolved through that subtree to fall back to
///     a default idle (in vanilla FNV content the crucified pose). Policy: remap if
///     possible, else zero the field (IDLE ANAM=0 is a legal "no link" value).
/// </summary>
public class IdleAnamSanitizerTests
{
    [Fact]
    public void EncodeNew_emits_ANAM_verbatim_when_no_validFormIds_supplied()
    {
        var idle = MakeIdle(parent: 0x0012A25Bu, previous: 0x0012A257u);

        var encoded = IdleEncoder.EncodeNew(idle);

        var anam = Assert.Single(encoded.Subrecords, s => s.Signature == "ANAM");
        Assert.Equal(0x0012A25Bu, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(0, 4)));
        Assert.Equal(0x0012A257u, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_keeps_ANAM_when_FormIDs_are_valid()
    {
        var idle = MakeIdle(parent: 0x0012A25Bu, previous: 0u);
        var valid = new HashSet<uint> { 0x0012A25Bu };

        var encoded = IdleEncoder.EncodeNew(idle, validFormIds: valid);

        var anam = Assert.Single(encoded.Subrecords, s => s.Signature == "ANAM");
        Assert.Equal(0x0012A25Bu, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(0, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_zeros_dangling_parent_idle_when_no_remap_available()
    {
        // 0x0012A25B is the actual dangling parent-idle FormID from the live error log.
        var idle = MakeIdle(parent: 0x0012A25Bu, previous: 0u);
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = IdleEncoder.EncodeNew(idle, validFormIds: valid);

        var anam = Assert.Single(encoded.Subrecords, s => s.Signature == "ANAM");
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(0, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(4, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("parent idle") && w.Contains("zeroed"));
    }

    [Fact]
    public void EncodeNew_zeros_dangling_previous_idle_when_no_remap_available()
    {
        var idle = MakeIdle(parent: 0u, previous: 0x0012A257u);
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = IdleEncoder.EncodeNew(idle, validFormIds: valid);

        var anam = Assert.Single(encoded.Subrecords, s => s.Signature == "ANAM");
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(0, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(4, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("previous idle") && w.Contains("zeroed"));
    }

    [Fact]
    public void EncodeNew_remaps_dangling_parent_idle_via_remapTable()
    {
        var idle = MakeIdle(parent: 0x01999AAAu, previous: 0u);
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = IdleEncoder.EncodeNew(idle, validFormIds: valid, remapTable: remap);

        var anam = Assert.Single(encoded.Subrecords, s => s.Signature == "ANAM");
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(0, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("parent idle") && w.Contains("remapped"));
    }

    [Fact]
    public void EncodeNew_does_not_warn_when_ANAM_fields_are_zero()
    {
        // 0 is the canonical "no link" value — must never trigger a sanitizer warning.
        var idle = MakeIdle(parent: 0u, previous: 0u);
        var valid = new HashSet<uint>();

        var encoded = IdleEncoder.EncodeNew(idle, validFormIds: valid);

        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("parent idle"));
        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("previous idle"));
    }

    [Fact]
    public void EncodeNew_zeroes_one_anam_field_and_keeps_the_other()
    {
        // ParentIdleFormId valid, PreviousIdleFormId dangling → only previous gets zeroed.
        var idle = MakeIdle(parent: 0x0012A25Bu, previous: 0x000DEAD1u);
        var valid = new HashSet<uint> { 0x0012A25Bu };

        var encoded = IdleEncoder.EncodeNew(idle, validFormIds: valid);

        var anam = Assert.Single(encoded.Subrecords, s => s.Signature == "ANAM");
        Assert.Equal(0x0012A25Bu, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(0, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(anam.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_emits_never_fire_CTDA_so_idle_is_inert()
    {
        // Proto-only idles (CrucifixIdle / NVCrucifixHang*) had CTDA conditions in master
        // FNV that restricted them to specific actors. Our runtime reader only captures the
        // CTDA count, not the conditions — so we emit a synthetic CTDA that always evaluates
        // false ("GetIsID 0 == 1") to make the idle inert until we can model the real CTDAs.
        var idle = MakeIdle(parent: 0u, previous: 0u);

        var encoded = IdleEncoder.EncodeNew(idle);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(28, ctda.Bytes.Length);
        // Type byte = 0x00 (equality operator)
        Assert.Equal(0x00, ctda.Bytes[0]);
        // ComparisonValue at offset 4 = 1.0f LE
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(ctda.Bytes.AsSpan(4, 4)));
        // FunctionIndex at offset 8 = 0x0048 (GetIsID)
        Assert.Equal((ushort)0x0048, BinaryPrimitives.ReadUInt16LittleEndian(ctda.Bytes.AsSpan(8, 2)));
        // Parameter1 at offset 12 = 0 (FormID 0)
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void EncodeNew_emits_real_CTDAs_when_runtime_reader_captured_them()
    {
        // GetIsID 0x000ED239 == 1 — a typical IDLE CTDA restricting to a specific NPC base.
        // When the runtime reader has captured the proto's actual conditions, we emit them
        // instead of the synthetic never-fire CTDA.
        var idle = MakeIdle(parent: 0u, previous: 0u, conditions: new()
        {
            new DialogueCondition
            {
                Type = 0x00,
                ComparisonValue = 1.0f,
                FunctionIndex = 0x48,    // GetIsID
                Parameter1 = 0x000ED239u,
                RunOn = 0
            }
        });

        var encoded = IdleEncoder.EncodeNew(idle);

        var ctdas = encoded.Subrecords.Where(s => s.Signature == "CTDA").ToList();
        Assert.Single(ctdas);
        // The emitted CTDA should be the REAL one (FunctionIndex 0x48 GetIsID, Param1 0x000ED239),
        // NOT the synthetic never-fire CTDA (which would have Param1 = 0).
        Assert.Equal((ushort)0x0048, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[0].Bytes.AsSpan(8, 2)));
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(ctdas[0].Bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void EncodeNew_emits_multiple_real_CTDAs_preserving_order()
    {
        var idle = MakeIdle(parent: 0u, previous: 0u, conditions: new()
        {
            new DialogueCondition { FunctionIndex = 0x48, Parameter1 = 0x000ED239u },
            new DialogueCondition { FunctionIndex = 0x14, Parameter1 = 0x0001D9D5u },   // GetInFaction
            new DialogueCondition { FunctionIndex = 0x0E, Parameter1 = 5u }              // GetActorValue (Endurance)
        });

        var encoded = IdleEncoder.EncodeNew(idle);

        var ctdas = encoded.Subrecords.Where(s => s.Signature == "CTDA").ToList();
        Assert.Equal(3, ctdas.Count);
        Assert.Equal((ushort)0x0048, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[0].Bytes.AsSpan(8, 2)));
        Assert.Equal((ushort)0x0014, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[1].Bytes.AsSpan(8, 2)));
        Assert.Equal((ushort)0x000E, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[2].Bytes.AsSpan(8, 2)));
    }

    [Fact]
    public void EncodeNew_does_NOT_emit_never_fire_CTDA_when_real_conditions_present()
    {
        var idle = MakeIdle(parent: 0u, previous: 0u, conditions: new()
        {
            new DialogueCondition { FunctionIndex = 0x48, Parameter1 = 0x000ED239u }
        });

        var encoded = IdleEncoder.EncodeNew(idle);

        var ctdas = encoded.Subrecords.Where(s => s.Signature == "CTDA").ToList();
        Assert.Single(ctdas);
        // Never-fire CTDA would have FunctionIndex=0x48 + Parameter1=0. Real CTDA has Param1!=0.
        Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(ctdas[0].Bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void EncodeNew_sanitizes_dangling_CTDA_FormID_param_when_validFormIds_supplied()
    {
        // GetIsID 0xDEADBEEF — dangling FormID parameter. ConditionSanitizer should drop
        // the whole condition. With no remaining conditions, fallback to never-fire.
        var idle = MakeIdle(parent: 0u, previous: 0u, conditions: new()
        {
            new DialogueCondition { FunctionIndex = 0x48, Parameter1 = 0x000DEAD1u }
        });
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = IdleEncoder.EncodeNew(idle, validFormIds: valid);

        var ctdas = encoded.Subrecords.Where(s => s.Signature == "CTDA").ToList();
        Assert.Single(ctdas);
        // Sanitizer dropped the dangling CTDA → fallback to never-fire (Param1=0).
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(ctdas[0].Bytes.AsSpan(12, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("CTDA sanitizer"));
    }

    [Fact]
    public void EncodeNew_remaps_CTDA_FormID_param_when_remap_available()
    {
        var idle = MakeIdle(parent: 0u, previous: 0u, conditions: new()
        {
            new DialogueCondition { FunctionIndex = 0x48, Parameter1 = 0x01999AAAu }
        });
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = IdleEncoder.EncodeNew(idle, validFormIds: valid, remapTable: remap);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
    }

    [Fact]
    public void EncodeNew_CTDA_appears_before_ANAM_in_fopdoc_canonical_order()
    {
        var idle = MakeIdle(parent: 0u, previous: 0u);

        var encoded = IdleEncoder.EncodeNew(idle);

        var ctdaIdx = encoded.Subrecords
            .Select((s, i) => (s.Signature, i)).First(p => p.Signature == "CTDA").i;
        var anamIdx = encoded.Subrecords
            .Select((s, i) => (s.Signature, i)).First(p => p.Signature == "ANAM").i;
        Assert.True(ctdaIdx < anamIdx, $"CTDA must precede ANAM (CTDA at {ctdaIdx}, ANAM at {anamIdx}).");
    }

    private static IdleAnimationRecord MakeIdle(uint parent, uint previous,
        List<DialogueCondition>? conditions = null)
    {
        return new IdleAnimationRecord
        {
            FormId = 0x01002FA6,
            EditorId = "TestIdle",
            ModelPath = "Idle\\Test.kf",
            ParentIdleFormId = parent,
            PreviousIdleFormId = previous,
            AnimData = 0,
            LoopMin = 1,
            LoopMax = 1,
            ReplayDelay = 0,
            FlagsEx = 0,
            Conditions = conditions ?? []
        };
    }
}
