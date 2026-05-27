using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     PLDT/PLD2 + PTDT/PTD2 dangling-FormID sanitizer tests for PackEncoder.EncodeNew.
///     Dangling Package Location FormIDs trigger the engine errors "Unable to find Package
///     Location Reference" and "AI: is assigned a reference location that doesnt exist for
///     a package" — the NPC's AI then falls through to default idle. Remap when possible,
///     otherwise rewrite the Type byte to a no-FormID-needed variant (NearCurrentLocation
///     for PLDT, Object Type for PTDT) so the package still loads cleanly.
/// </summary>
public class PackRefSanitizerTests
{
    private const byte PlocNearReference = 0;
    private const byte PlocNearCurrent = 2;
    private const byte PtdtSpecificReference = 0;
    private const byte PtdtObjectType = 2;

    [Fact]
    public void EncodeNew_keeps_PLDT_when_no_validFormIds_supplied()
    {
        var pack = MakePack(loc: new PackageLocation
        {
            Type = PlocNearReference, Union = 0x000CDA76u, Radius = 100
        });

        var encoded = PackEncoder.EncodeNew(pack);

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(PlocNearReference, pldt.Bytes[0]);
        Assert.Equal(0x000CDA76u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_keeps_PLDT_when_Union_FormID_is_valid()
    {
        var pack = MakePack(loc: new PackageLocation
        {
            Type = PlocNearReference, Union = 0x000ED239u, Radius = 100
        });
        var valid = new HashSet<uint> { 0x000ED239u };

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(PlocNearReference, pldt.Bytes[0]);
        Assert.Equal(0x000ED239u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_falls_back_to_NearCurrent_when_PLDT_Union_dangles_no_remap()
    {
        // 0x00122985 is one of the actual dangling refs from the live error log.
        var pack = MakePack(loc: new PackageLocation
        {
            Type = PlocNearReference, Union = 0x00122985u, Radius = 100
        });
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(PlocNearCurrent, pldt.Bytes[0]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("PLDT") && w.Contains("fallback"));
    }

    [Fact]
    public void EncodeNew_remaps_PLDT_Union_when_dangling_ref_is_in_remap_table()
    {
        var pack = MakePack(loc: new PackageLocation
        {
            Type = PlocNearReference, Union = 0x01999AAAu, Radius = 100
        });
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid, remapTable: remap);

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(PlocNearReference, pldt.Bytes[0]);   // Type preserved
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("PLDT") && w.Contains("remapped"));
    }

    [Fact]
    public void EncodeNew_does_not_touch_PLDT_when_Type_is_ObjectType_enum()
    {
        // Type 5 (ObjectType) has Union = form-type enum, NOT a FormID. Don't validate.
        var pack = MakePack(loc: new PackageLocation { Type = 5, Union = 42u, Radius = 0 });
        var valid = new HashSet<uint>();    // empty — Union is not a FormID anyway

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var pldt = Assert.Single(encoded.Subrecords, s => s.Signature == "PLDT");
        Assert.Equal(5, pldt.Bytes[0]);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(pldt.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_falls_back_PTDT_to_ObjectType_when_FormIdOrType_dangles()
    {
        var pack = MakePack(target: new PackageTarget
        {
            Type = PtdtSpecificReference, FormIdOrType = 0x000DEAD1u
        });
        var valid = new HashSet<uint> { 0x00000001u };

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var ptdt = Assert.Single(encoded.Subrecords, s => s.Signature == "PTDT");
        Assert.Equal(PtdtObjectType, ptdt.Bytes[0]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(ptdt.Bytes.AsSpan(4, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("PTDT") && w.Contains("fallback"));
    }

    [Fact]
    public void EncodeNew_keeps_PTDT_when_Type_is_ObjectType_enum()
    {
        // PTDT Type 2 = ObjectType enum, FormIdOrType is a Form-type code, not a FormID.
        var pack = MakePack(target: new PackageTarget { Type = PtdtObjectType, FormIdOrType = 41u });
        var valid = new HashSet<uint>();

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var ptdt = Assert.Single(encoded.Subrecords, s => s.Signature == "PTDT");
        Assert.Equal(PtdtObjectType, ptdt.Bytes[0]);
        Assert.Equal(41u, BinaryPrimitives.ReadUInt32LittleEndian(ptdt.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void EncodeNew_PLD2_and_PTD2_share_the_same_sanitization_path()
    {
        var pack = new PackageRecord
        {
            FormId = 0x01000A00,
            EditorId = "TestPack",
            Data = new PackageData(),
            Location2 = new PackageLocation { Type = PlocNearReference, Union = 0xBADBADu, Radius = 0 },
            Target2 = new PackageTarget { Type = PtdtSpecificReference, FormIdOrType = 0xBADBADu }
        };
        var valid = new HashSet<uint>();

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid);

        var pld2 = Assert.Single(encoded.Subrecords, s => s.Signature == "PLD2");
        Assert.Equal(PlocNearCurrent, pld2.Bytes[0]);
        var ptd2 = Assert.Single(encoded.Subrecords, s => s.Signature == "PTD2");
        Assert.Equal(PtdtObjectType, ptd2.Bytes[0]);
    }

    [Fact]
    public void EncodeNew_emits_PACK_conditions_with_optional_CIS_strings()
    {
        var pack = MakePack() with
        {
            Conditions =
            [
                new DialogueCondition
                {
                    Type = 0,
                    ComparisonValue = 1.0f,
                    FunctionIndex = 0x48, // GetIsID
                    Parameter1 = 0x000E5958u,
                    Parameter1String = "QJChompsLewis"
                }
            ]
        };

        var encoded = PackEncoder.EncodeNew(pack);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "PKDT" or "CTDA" or "CIS1")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["PKDT", "CTDA", "CIS1"], sigs);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(0x48, BinaryPrimitives.ReadUInt16LittleEndian(ctda.Bytes.AsSpan(8, 2)));
        Assert.Equal(0x000E5958u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
        Assert.Single(encoded.Subrecords, s => s.Signature == "CIS1");
    }

    [Fact]
    public void EncodeNew_remaps_and_drops_PACK_condition_form_parameters()
    {
        var pack = MakePack() with
        {
            Conditions =
            [
                new DialogueCondition
                {
                    Type = 0,
                    ComparisonValue = 1.0f,
                    FunctionIndex = 0x48, // GetIsID param1 is a FormID
                    Parameter1 = 0x01999AAAu
                },
                new DialogueCondition
                {
                    Type = 0,
                    ComparisonValue = 1.0f,
                    FunctionIndex = 0x48,
                    Parameter1 = 0x000DEAD1u
                }
            ]
        };
        var valid = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };

        var encoded = PackEncoder.EncodeNew(pack, validFormIds: valid, remapTable: remap);

        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.Equal(0x01000123u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("CTDA sanitizer") &&
                                              w.Contains("dropped 1") &&
                                              w.Contains("remapped 1"));
    }

    [Fact]
    public void EncodeNew_emits_typed_PACK_dialogue_idle_and_event_blocks()
    {
        var pack = MakePack() with
        {
            Data = new PackageData { Type = 15 },
            DialogueData = new PackageDialogueData
            {
                Fov = 75.0f,
                TopicFormId = 0x00001001u,
                NoHeadtracking = true,
                SpeakerMoveTalk = true,
                DistanceStartTalking = 128.0f,
                SayTo = true,
                TriggerType = 2
            },
            IdleCollection = new PackageIdleCollection
            {
                Flags = 0x03,
                Count = 2,
                TimerCheckForIdle = 5.5f,
                IdleAnimationFormIds = [0x00002001u, 0x00002002u]
            },
            OnBegin = new PackageEventAction
            {
                Kind = PackageEventActionKind.OnBegin,
                IdleFormId = 0x00003001u,
                TopicFormId = 0x00004001u,
                Scripts =
                [
                    new DialogueResultScript
                    {
                        SourceText = "SetStage TestQuest 10",
                        CompiledData = [0x1D, 0x00],
                        ReferencedObjects = [0x00005001u]
                    }
                ]
            }
        };

        var encoded = PackEncoder.EncodeNew(pack);

        Assert.Equal(
            ["EDID", "PKDT", "IDLF", "IDLC", "IDLT", "IDLA", "PKDD", "POBA", "INAM", "SCHR", "SCDA", "SCTX", "SCRO", "TNAM"],
            encoded.Subrecords.Select(s => s.Signature).ToList());

        var pkdd = Assert.Single(encoded.Subrecords, s => s.Signature == "PKDD").Bytes;
        Assert.Equal(75.0f, BinaryPrimitives.ReadSingleLittleEndian(pkdd.AsSpan(0, 4)));
        Assert.Equal(0x00001001u, BinaryPrimitives.ReadUInt32LittleEndian(pkdd.AsSpan(4, 4)));
        Assert.Equal(1, pkdd[8]);
        Assert.Equal(0, pkdd[9]);
        Assert.Equal(1, pkdd[10]);
        Assert.Equal(128.0f, BinaryPrimitives.ReadSingleLittleEndian(pkdd.AsSpan(12, 4)));
        Assert.Equal(1, pkdd[16]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(pkdd.AsSpan(20, 4)));

        Assert.Equal(0x03, Assert.Single(encoded.Subrecords, s => s.Signature == "IDLF").Bytes[0]);
        Assert.Equal(2, Assert.Single(encoded.Subrecords, s => s.Signature == "IDLC").Bytes[0]);
        Assert.Equal(5.5f, BinaryPrimitives.ReadSingleLittleEndian(
            Assert.Single(encoded.Subrecords, s => s.Signature == "IDLT").Bytes));
        var idla = Assert.Single(encoded.Subrecords, s => s.Signature == "IDLA").Bytes;
        Assert.Equal(0x00002001u, BinaryPrimitives.ReadUInt32LittleEndian(idla.AsSpan(0, 4)));
        Assert.Equal(0x00002002u, BinaryPrimitives.ReadUInt32LittleEndian(idla.AsSpan(4, 4)));

        Assert.Equal(0x00003001u, BinaryPrimitives.ReadUInt32LittleEndian(
            Assert.Single(encoded.Subrecords, s => s.Signature == "INAM").Bytes));
        Assert.Equal(0x00005001u, BinaryPrimitives.ReadUInt32LittleEndian(
            Assert.Single(encoded.Subrecords, s => s.Signature == "SCRO").Bytes));
        Assert.Equal(0x00004001u, BinaryPrimitives.ReadUInt32LittleEndian(
            Assert.Single(encoded.Subrecords, s => s.Signature == "TNAM").Bytes));
    }

    private static PackageRecord MakePack(PackageLocation? loc = null, PackageTarget? target = null)
    {
        return new PackageRecord
        {
            FormId = 0x01000A00,
            EditorId = "TestPack",
            Data = new PackageData(),    // empty PKDT
            Location = loc,
            Target = target
        };
    }
}
