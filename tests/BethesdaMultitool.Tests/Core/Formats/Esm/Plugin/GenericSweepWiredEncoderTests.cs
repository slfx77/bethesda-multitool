using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     The five FormTypes the generic runtime sweep has always read out of dumps but that nothing
///     could emit until 2026-08-26: LSCR, CHIP, IDLM, CAMS, MSET. Each arrives as a
///     <see cref="GenericEsmRecord" /> whose <c>Fields</c> are keyed by PDB identifier
///     ("Owner.Name") on the runtime path and by subrecord signature on the ESM carve path — the
///     dual-key lookup is the main regression risk these tests pin, alongside the exact emitted
///     signature order and byte values.
/// </summary>
public class GenericSweepWiredEncoderTests
{
    private static string ZString(EncodedSubrecord sub)
    {
        var bytes = sub.Bytes;
        Assert.True(bytes.Length > 0 && bytes[^1] == 0, $"{sub.Signature} is not null-terminated.");
        return Encoding.Latin1.GetString(bytes, 0, bytes.Length - 1);
    }

    private static uint FormId(EncodedSubrecord sub)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(sub.Bytes);
    }

    private static float Float(EncodedSubrecord sub)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(sub.Bytes);
    }

    // ===================================================================================
    // LSCR — xEdit wbRecord(LSCR): EDID(req), ICON(req), DESC(req), LNAM(array), WMI1.
    // ===================================================================================

    [Fact]
    public void Lscr_EmitsXEditCanonicalOrder_FromPdbKeyedFields()
    {
        var result = LscrEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000700,
            RecordType = "LSCR",
            EditorId = "ProtoLoadScreen",
            Fields = new Dictionary<string, object?>
            {
                ["TESTexture.TextureName"] = @"Textures\Interface\Loading\Goodsprings.dds",
                ["TESLoadScreen.cDescText"] = "The Mojave Wasteland is not kind to the unprepared.",
                ["TESLoadScreen.pLoadScreenType"] = 0x00088A11u
            }
        });

        Assert.Equal(["EDID", "ICON", "DESC", "WMI1"], result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Equal(@"Textures\Interface\Loading\Goodsprings.dds",
            ZString(result.Subrecords.Single(s => s.Signature == "ICON")));
        Assert.Equal("The Mojave Wasteland is not kind to the unprepared.",
            ZString(result.Subrecords.Single(s => s.Signature == "DESC")));
        Assert.Equal(0x00088A11u, FormId(result.Subrecords.Single(s => s.Signature == "WMI1")));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Lscr_ReadsIconDescAndScreenTypeFromSignatureKeyedFields()
    {
        // The ESM carve path stores ICON/DESC as decoded text and WMI1 through its registered
        // FormID schema (a single-entry field dictionary).
        var result = LscrEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000701,
            RecordType = "LSCR",
            EditorId = "CarvedLoadScreen",
            Fields = new Dictionary<string, object?>
            {
                ["ICON"] = @"Textures\Interface\Loading\Primm.dds",
                ["DESC"] = "Bison Steve.",
                ["WMI1"] = new Dictionary<string, object?> { ["Weapon Mod 1 FormID"] = 0x000A0B0Cu }
            }
        });

        Assert.Equal(@"Textures\Interface\Loading\Primm.dds",
            ZString(result.Subrecords.Single(s => s.Signature == "ICON")));
        Assert.Equal("Bison Steve.", ZString(result.Subrecords.Single(s => s.Signature == "DESC")));
        Assert.Equal(0x000A0B0Cu, FormId(result.Subrecords.Single(s => s.Signature == "WMI1")));
    }

    [Fact]
    public void Lscr_EmitsRequiredIconAndDescEmpty_AndNeverLnam()
    {
        // LNAM is a BSSimpleList<LOAD_FORM_DATA*> the reader never walks, so no location entry can
        // be recovered and none may be synthesized. ICON/DESC are .SetRequired and must be present.
        var result = LscrEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000702,
            RecordType = "LSCR",
            EditorId = "BareLoadScreen"
        });

        Assert.Equal(["EDID", "ICON", "DESC"], result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Equal([0x00], result.Subrecords.Single(s => s.Signature == "ICON").Bytes);
        Assert.Equal([0x00], result.Subrecords.Single(s => s.Signature == "DESC").Bytes);
        Assert.Contains(result.Warnings, w => w.Contains("TextureName"));
        Assert.Contains(result.Warnings, w => w.Contains("cDescText"));
    }

    [Fact]
    public void Lscr_OmitsScreenType_WhenThePointerLeakedARawVirtualAddress()
    {
        // The generic reader falls back to the raw Xbox VA when a pointer does not resolve to a
        // TESForm; encoding one as a FormID invents a 131st master.
        var result = LscrEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000703,
            RecordType = "LSCR",
            EditorId = "ProtoLoadScreen",
            Fields = new Dictionary<string, object?> { ["TESLoadScreen.pLoadScreenType"] = 0x82339658u }
        });

        Assert.DoesNotContain(result.Subrecords, s => s.Signature == "WMI1");
    }

    // ===================================================================================
    // CHIP — xEdit wbRecord(CHIP): EDID, OBND, FULL, MODL, ICON, DEST, YNAM, ZNAM.
    // ===================================================================================

    private static GenericEsmRecord Chip(Dictionary<string, object?>? fields = null, ObjectBounds? bounds = null)
    {
        return new GenericEsmRecord
        {
            FormId = 0x01000800,
            RecordType = "CHIP",
            EditorId = "ProtoCasinoChip",
            FullName = "Ultra-Luxe Chip",
            ModelPath = @"Clutter\Casino\Chip01.NIF",
            Bounds = bounds ?? new ObjectBounds { X1 = -2, Y1 = -2, Z1 = 0, X2 = 2, Y2 = 2, Z2 = 1 },
            Fields = fields ?? []
        };
    }

    [Fact]
    public void Chip_EmitsAllSixRecoverableSubrecordsInXEditOrder()
    {
        var subs = ChipEncoder.EncodeNew(Chip(new Dictionary<string, object?>
        {
            ["TESTexture.TextureName"] = @"Interface\Icons\Chip.dds",
            ["BGSPickupPutdownSounds.pPickupSound"] = 0x0003C1D2u,
            ["BGSPickupPutdownSounds.pPutdownSound"] = 0x0003C1D3u
        })).Subrecords;

        // DEST is intentionally absent — pData points at a DestructibleObjectData no reader walks.
        Assert.Equal(
            ["EDID", "OBND", "FULL", "MODL", "ICON", "YNAM", "ZNAM"],
            subs.Select(s => s.Signature).ToArray());
        Assert.Equal("Ultra-Luxe Chip", ZString(subs.Single(s => s.Signature == "FULL")));
        Assert.Equal(@"Clutter\Casino\Chip01.NIF", ZString(subs.Single(s => s.Signature == "MODL")));
        Assert.Equal(@"Interface\Icons\Chip.dds", ZString(subs.Single(s => s.Signature == "ICON")));
        Assert.Equal(0x0003C1D2u, FormId(subs.Single(s => s.Signature == "YNAM")));
        Assert.Equal(0x0003C1D3u, FormId(subs.Single(s => s.Signature == "ZNAM")));
    }

    [Fact]
    public void Chip_ObndIsSixLittleEndianInt16sInMinMaxOrder()
    {
        var obnd = ChipEncoder.EncodeNew(Chip(bounds: new ObjectBounds
        {
            X1 = -3, Y1 = -4, Z1 = -5, X2 = 6, Y2 = 7, Z2 = 8
        })).Subrecords.Single(s => s.Signature == "OBND").Bytes;

        Assert.Equal(12, obnd.Length);
        Assert.Equal(-3, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(0, 2)));
        Assert.Equal(-4, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(2, 2)));
        Assert.Equal(-5, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(4, 2)));
        Assert.Equal(6, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(6, 2)));
        Assert.Equal(7, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(8, 2)));
        Assert.Equal(8, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(10, 2)));
    }

    [Fact]
    public void Chip_OmitsObnd_WhenBoundsAreImplausible()
    {
        // Inverted extents are the misaligned-read signature (min > max).
        var result = ChipEncoder.EncodeNew(Chip(bounds: new ObjectBounds
        {
            X1 = 31, Y1 = 31, Z1 = 774, X2 = 6945, Y2 = 20487, Z2 = -8196
        }));

        Assert.DoesNotContain(result.Subrecords, s => s.Signature == "OBND");
        Assert.Contains(result.Warnings, w => w.Contains("implausible bounds"));
    }

    [Fact]
    public void Chip_ReadsPickupSoundsFromSignatureKeyedFields()
    {
        var subs = ChipEncoder.EncodeNew(Chip(new Dictionary<string, object?>
        {
            ["ICON"] = @"Interface\Icons\Chip.dds",
            ["YNAM"] = 0x00011111u,
            ["ZNAM"] = 0x00022222u
        })).Subrecords;

        Assert.Equal(0x00011111u, FormId(subs.Single(s => s.Signature == "YNAM")));
        Assert.Equal(0x00022222u, FormId(subs.Single(s => s.Signature == "ZNAM")));
    }

    [Theory]
    [InlineData(0x82339658u)] // Xbox module space
    [InlineData(0x40001000u)] // Xbox heap
    public void Chip_RejectsRawVirtualAddressesAsSoundRefs(uint rawVa)
    {
        var subs = ChipEncoder.EncodeNew(Chip(new Dictionary<string, object?>
        {
            ["BGSPickupPutdownSounds.pPickupSound"] = rawVa,
            ["BGSPickupPutdownSounds.pPutdownSound"] = rawVa
        })).Subrecords;

        Assert.DoesNotContain(subs, s => s.Signature is "YNAM" or "ZNAM");
    }

    // ===================================================================================
    // IDLM — xEdit wbRecord(IDLM) + wbIdleAnimation: EDID, OBND, IDLF, IDLC, IDLT, IDLA.
    // ===================================================================================

    [Fact]
    public void Idlm_EmitsFlagsCountAndTimer_WithCountForcedToZero()
    {
        // IDLC drives IDLA's element count in the schema. IDLA cannot be recovered (pIdleArray is
        // an out-of-struct pointer array), so writing the runtime's count would point the engine at
        // animations that are not there.
        var result = IdlmEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000900,
            RecordType = "IDLM",
            EditorId = "ProtoIdleMarker",
            Bounds = new ObjectBounds { X1 = -16, Y1 = -16, Z1 = 0, X2 = 16, Y2 = 16, Z2 = 64 },
            Fields = new Dictionary<string, object?>
            {
                ["BGSIdleCollection.cIdleFlags"] = (byte)0x05,
                ["BGSIdleCollection.cIdleCount"] = (byte)3,
                ["BGSIdleCollection.fTimerCheckForIdle"] = 12.5f
            }
        });

        Assert.Equal(
            ["EDID", "OBND", "IDLF", "IDLC", "IDLT"],
            result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Equal([0x05], result.Subrecords.Single(s => s.Signature == "IDLF").Bytes);
        Assert.Equal([0x00], result.Subrecords.Single(s => s.Signature == "IDLC").Bytes);
        Assert.Equal(12.5f, Float(result.Subrecords.Single(s => s.Signature == "IDLT")));
        Assert.DoesNotContain(result.Subrecords, s => s.Signature == "IDLA");
        Assert.Contains(result.Warnings, w => w.Contains("pIdleArray"));
    }

    [Fact]
    public void Idlm_StillEmitsZeroCount_WhenNothingWasCaptured()
    {
        var result = IdlmEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000901,
            RecordType = "IDLM",
            EditorId = "BareIdleMarker"
        });

        Assert.Equal(["EDID", "IDLC"], result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Equal([0x00], result.Subrecords.Single(s => s.Signature == "IDLC").Bytes);
        // No animations were captured, so nothing is being dropped and no warning is warranted.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("pIdleArray"));
    }

    [Fact]
    public void Idlm_OmitsFlags_WhenTheByteIsOutOfSchemaRange()
    {
        var result = IdlmEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000902,
            RecordType = "IDLM",
            EditorId = "MisalignedIdleMarker",
            // xEdit's flag list tops out at bit 4; 0x9C6D is a misaligned read, not flags.
            Fields = new Dictionary<string, object?> { ["BGSIdleCollection.cIdleFlags"] = 0x9C6Du }
        });

        Assert.DoesNotContain(result.Subrecords, s => s.Signature == "IDLF");
        Assert.Contains(result.Warnings, w => w.Contains("idle flags"));
    }

    [Fact]
    public void Idlm_ReadsCarvePathScalarsThroughTheirSingleFieldSchemaDictionaries()
    {
        // MiscRecordHandler runs IDLF/IDLT through SubrecordSchemaView, so each arrives boxed in a
        // one-field dictionary rather than as a bare number.
        var result = IdlmEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000903,
            RecordType = "IDLM",
            EditorId = "CarvedIdleMarker",
            Fields = new Dictionary<string, object?>
            {
                ["IDLF"] = new Dictionary<string, object?> { ["Flags"] = (byte)0x04 },
                ["IDLT"] = new Dictionary<string, object?> { ["Value"] = 4.25f }
            }
        });

        Assert.Equal([0x04], result.Subrecords.Single(s => s.Signature == "IDLF").Bytes);
        Assert.Equal(4.25f, Float(result.Subrecords.Single(s => s.Signature == "IDLT")));
    }

    // ===================================================================================
    // CAMS — xEdit wbRecord(CAMS): EDID, model, DATA(40 bytes, req), MNAM.
    // ===================================================================================

    /// <summary>
    ///     A big-endian CAMERA_SHOT_DATA: u32 Action/Location/Target/Flags then six floats
    ///     (Player/Target/Global time multipliers, Max Time, Min Time, Target % Between Actors).
    /// </summary>
    private static byte[] BigEndianCameraShotData()
    {
        var raw = new byte[40];
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0, 4), 2); // Action = Hit
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(4, 4), 1); // Location = Projectile
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(8, 4), 2); // Target = Target
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(12, 4), 0x21); // Flags
        BinaryPrimitives.WriteSingleBigEndian(raw.AsSpan(16, 4), 0.25f);
        BinaryPrimitives.WriteSingleBigEndian(raw.AsSpan(20, 4), 0.5f);
        BinaryPrimitives.WriteSingleBigEndian(raw.AsSpan(24, 4), 1.0f);
        BinaryPrimitives.WriteSingleBigEndian(raw.AsSpan(28, 4), 3.0f);
        BinaryPrimitives.WriteSingleBigEndian(raw.AsSpan(32, 4), 0.75f);
        BinaryPrimitives.WriteSingleBigEndian(raw.AsSpan(36, 4), 0.6f);
        return raw;
    }

    [Fact]
    public void Cams_ConvertsBigEndianCameraShotDataThroughTheSchemaOracle()
    {
        var result = CamsEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000A00,
            RecordType = "CAMS",
            EditorId = "ProtoCameraShot",
            ModelPath = @"Camera\KillCam.NIF",
            Fields = new Dictionary<string, object?>
            {
                // The raw bytes RuntimeGenericReader now hands back for a >8-byte embedded struct.
                ["BGSCameraShot.Data"] = BigEndianCameraShotData(),
                ["TESImageSpaceModifiableForm.pFormImageSpaceModifying"] = 0x000C3344u
            }
        });

        Assert.Equal(["EDID", "MODL", "DATA", "MNAM"], result.Subrecords.Select(s => s.Signature).ToArray());

        var data = result.Subrecords.Single(s => s.Signature == "DATA").Bytes;
        Assert.Equal(40, data.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)));
        Assert.Equal(0x21u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4)));
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(16, 4)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(20, 4)));
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(24, 4)));
        Assert.Equal(3.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(28, 4)));
        Assert.Equal(0.75f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(32, 4)));
        Assert.Equal(0.6f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(36, 4)));

        Assert.Equal(0x000C3344u, FormId(result.Subrecords.Single(s => s.Signature == "MNAM")));
    }

    [Fact]
    public void Cams_RebuildsDataFromTheCarvePathFieldDictionary()
    {
        // The ESM carve path decodes DATA through the same registered schema, so the encoder
        // re-serializes the dictionary rather than reinterpreting bytes.
        var result = CamsEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000A01,
            RecordType = "CAMS",
            EditorId = "CarvedCameraShot",
            Fields = new Dictionary<string, object?>
            {
                ["DATA"] = new Dictionary<string, object?>
                {
                    ["Action"] = 3u,
                    ["Location"] = 0u,
                    ["Target"] = 2u,
                    ["Flags"] = 0x10u,
                    ["PlayerTimeMult"] = 1f,
                    ["TargetTimeMult"] = 1f,
                    ["GlobalTimeMult"] = 0.5f,
                    ["MaxTime"] = 2f,
                    ["MinTime"] = 0.1f,
                    ["TargetPctBetweenActors"] = 0.5f
                }
            }
        });

        var data = result.Subrecords.Single(s => s.Signature == "DATA").Bytes;
        Assert.Equal(40, data.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(0x10u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(24, 4)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(36, 4)));
    }

    [Fact]
    public void Cams_OmitsData_WhenTheCapturedBlockIsNotAKnownSize()
    {
        var result = CamsEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000A02,
            RecordType = "CAMS",
            EditorId = "TruncatedCameraShot",
            Fields = new Dictionary<string, object?> { ["BGSCameraShot.Data"] = new byte[17] }
        });

        Assert.Equal(["EDID"], result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Contains(result.Warnings, w => w.Contains("CAMERA_SHOT_DATA"));
    }

    [Fact]
    public void Cams_EmitsNeitherObndNorFull_BecauseTheSchemaHasNeither()
    {
        var subs = CamsEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000A03,
            RecordType = "CAMS",
            EditorId = "ProtoCameraShot",
            FullName = "Should Not Appear",
            Bounds = new ObjectBounds { X1 = -1, Y1 = -1, Z1 = -1, X2 = 1, Y2 = 1, Z2 = 1 },
            Fields = new Dictionary<string, object?> { ["BGSCameraShot.Data"] = BigEndianCameraShotData() }
        }).Subrecords;

        Assert.Equal(["EDID", "DATA"], subs.Select(s => s.Signature).ToArray());
    }

    // ===================================================================================
    // MSET — xEdit wbRecord(MSET). Six positional layers × (name, dB, boundary %).
    // ===================================================================================

    [Fact]
    public void Mset_EmitsEverySlotInXEditOrderWithTheRightLayerInEachOne()
    {
        var subs = MsetEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000B00,
            RecordType = "MSET",
            EditorId = "ProtoMediaSet",
            FullName = "Vegas Strip Ambience",
            Fields = new Dictionary<string, object?>
            {
                ["NAM1"] = 1u,
                ["NAM2"] = "DayOuter", ["NAM3"] = "DayMiddle", ["NAM4"] = "DayInner",
                ["NAM5"] = "NightOuter", ["NAM6"] = "NightMiddle", ["NAM7"] = "NightInner",
                ["NAM8"] = -1f, ["NAM9"] = -2f, ["NAM0"] = -3f,
                ["ANAM"] = -4f, ["BNAM"] = -5f, ["CNAM"] = -6f,
                ["JNAM"] = 0.1f, ["KNAM"] = 0.2f, ["LNAM"] = 0.3f,
                ["MNAM"] = 0.4f, ["NNAM"] = 0.5f, ["ONAM"] = 0.6f,
                ["PNAM"] = 0x3Fu,
                ["DNAM"] = 10f, ["ENAM"] = 11f, ["FNAM"] = 12f, ["GNAM"] = 13f,
                ["HNAM"] = 0x00051234u,
                ["INAM"] = 0x00051235u
            }
        }).Subrecords;

        Assert.Equal(
        [
            "EDID", "FULL", "NAM1",
            "NAM2", "NAM3", "NAM4", "NAM5", "NAM6", "NAM7",
            "NAM8", "NAM9", "NAM0", "ANAM", "BNAM", "CNAM",
            "JNAM", "KNAM", "LNAM", "MNAM", "NNAM", "ONAM",
            "PNAM", "DNAM", "ENAM", "FNAM", "GNAM", "HNAM", "INAM"
        ], subs.Select(s => s.Signature).ToArray());

        Assert.Equal("Vegas Strip Ambience", ZString(subs.Single(s => s.Signature == "FULL")));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(subs.Single(s => s.Signature == "NAM1").Bytes));

        // Layer n's name, dB and boundary % must land in the nth slot of each group — a shift here
        // is exactly the corruption the positional rule exists to prevent.
        string[] names = ["NAM2", "NAM3", "NAM4", "NAM5", "NAM6", "NAM7"];
        string[] expectedNames = ["DayOuter", "DayMiddle", "DayInner", "NightOuter", "NightMiddle", "NightInner"];
        string[] attenuations = ["NAM8", "NAM9", "NAM0", "ANAM", "BNAM", "CNAM"];
        float[] expectedDb = [-1f, -2f, -3f, -4f, -5f, -6f];
        string[] percents = ["JNAM", "KNAM", "LNAM", "MNAM", "NNAM", "ONAM"];
        float[] expectedPercent = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f];
        for (var layer = 0; layer < 6; layer++)
        {
            Assert.Equal(expectedNames[layer], ZString(subs.Single(s => s.Signature == names[layer])));
            Assert.Equal(expectedDb[layer], Float(subs.Single(s => s.Signature == attenuations[layer])));
            Assert.Equal(expectedPercent[layer], Float(subs.Single(s => s.Signature == percents[layer])));
        }

        Assert.Equal([0x3F], subs.Single(s => s.Signature == "PNAM").Bytes);
        Assert.Equal(10f, Float(subs.Single(s => s.Signature == "DNAM")));
        Assert.Equal(13f, Float(subs.Single(s => s.Signature == "GNAM")));
        Assert.Equal(0x00051234u, FormId(subs.Single(s => s.Signature == "HNAM")));
        Assert.Equal(0x00051235u, FormId(subs.Single(s => s.Signature == "INAM")));
        // xEdit's trailing wbUnknown(DATA) has no recoverable content and is never synthesized.
        Assert.DoesNotContain(subs, s => s.Signature == "DATA");
    }

    [Fact]
    public void Mset_AbsentMiddleLayerLeavesItsSlotsEmptyRatherThanShiftingLaterLayersUp()
    {
        var subs = MsetEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000B01,
            RecordType = "MSET",
            EditorId = "SparseMediaSet",
            Fields = new Dictionary<string, object?>
            {
                // Layers 0 and 2 only — layer 1 (NAM3/NAM9/KNAM) was not captured.
                ["NAM2"] = "First", ["NAM8"] = -1f, ["JNAM"] = 0.1f,
                ["NAM4"] = "Third", ["NAM0"] = -3f, ["LNAM"] = 0.3f
            }
        }).Subrecords;

        Assert.Equal(
            ["EDID", "NAM2", "NAM4", "NAM8", "NAM0", "JNAM", "LNAM"],
            subs.Select(s => s.Signature).ToArray());
        Assert.Equal("First", ZString(subs.Single(s => s.Signature == "NAM2")));
        Assert.Equal("Third", ZString(subs.Single(s => s.Signature == "NAM4")));
        Assert.Equal(-3f, Float(subs.Single(s => s.Signature == "NAM0")));
    }

    [Fact]
    public void Mset_AcceptsCarvePathFloatSlotsBoxedAsTheirUInt32BitPattern()
    {
        // SubrecordCellAndMiscSchemas types every MSET 4-byte slot as Simple4Byte, so the carve
        // path decodes a dB float into a uint. Writing those bytes is byte-identical to writing
        // the float — this pins that equivalence rather than assuming it.
        var bits = BitConverter.SingleToUInt32Bits(-7.5f);
        var subs = MsetEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000B02,
            RecordType = "MSET",
            EditorId = "CarvedMediaSet",
            Fields = new Dictionary<string, object?>
            {
                ["NAM8"] = new Dictionary<string, object?> { ["Music Track FormID"] = bits }
            }
        }).Subrecords;

        Assert.Equal(-7.5f, Float(subs.Single(s => s.Signature == "NAM8")));
    }

    [Fact]
    public void Mset_RejectsRawVirtualAddressesAsSoundRefs()
    {
        var subs = MsetEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000B03,
            RecordType = "MSET",
            EditorId = "ProtoMediaSet",
            Fields = new Dictionary<string, object?>
            {
                ["HNAM"] = 0x82339658u,
                ["INAM"] = 0x40001000u
            }
        }).Subrecords;

        Assert.DoesNotContain(subs, s => s.Signature is "HNAM" or "INAM");
    }
}
