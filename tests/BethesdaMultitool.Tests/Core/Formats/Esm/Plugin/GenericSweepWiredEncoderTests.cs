using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     The FormTypes the generic runtime sweep has always read out of dumps but that nothing could
///     emit until 2026-08-26: LSCR, CHIP, IDLM, CAMS, MSET, then EFSH, RGDL and CSNO. Each arrives as a
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
    public void Idlm_EmitsResolvedAnimations_WithIdlcMatchingIdlaLength()
    {
        // The walked array is the authority for both members: RuntimeContainerFieldReader resolves
        // pIdleArray all-or-nothing, and IDLC is written as that array's length so the pair the
        // schema requires to agree cannot disagree.
        var result = IdlmEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000903,
            RecordType = "IDLM",
            EditorId = "WalkedIdleMarker",
            Fields = new Dictionary<string, object?>
            {
                ["BGSIdleCollection.cIdleCount"] = (byte)2,
                ["BGSIdleCollection.pIdleArray"] = new List<uint> { 0x00033001, 0x00033002 }
            }
        });

        Assert.Equal([0x02], result.Subrecords.Single(s => s.Signature == "IDLC").Bytes);
        var idla = result.Subrecords.Single(s => s.Signature == "IDLA").Bytes;
        Assert.Equal(8, idla.Length);
        Assert.Equal(0x00033001u, BitConverter.ToUInt32(idla, 0)); // little-endian on the PC side
        Assert.Equal(0x00033002u, BitConverter.ToUInt32(idla, 4));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("pIdleArray"));
    }

    [Fact]
    public void Idlm_ForcesCountToZero_WhenTheArrayCouldNotBeWalked()
    {
        // IDLC drives IDLA's element count in the schema, so a declared count with no array behind
        // it would point the engine at animations that are not there.
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

    // ===================================================================================
    // EFSH — xEdit wbRecord(EFSH): EDID, ICON, ICO2, NAM7, DATA(308 in the runtime form).
    // ===================================================================================

    [Fact]
    public void Efsh_EmitsThreeTexturesAndSchemaConvertedData()
    {
        var raw = new byte[308];
        raw[0] = 0x21; // Flags — a u8 the schema leaves unswapped, so it must survive verbatim.
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(4), 6); // Membrane source blend mode

        var result = EfshEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000A00,
            RecordType = "EFSH",
            EditorId = "ProtoEffectShader",
            Fields = new Dictionary<string, object?>
            {
                ["TESEffectShader.TextureShaderTexture"] = @"effects\fillfx.dds",
                ["TESEffectShader.ParticleShaderTexture"] = @"effects\particle.dds",
                ["TESEffectShader.BlockOutTexture"] = @"effects\holes.dds",
                ["TESEffectShader.Data"] = raw
            }
        });

        Assert.Equal(
            ["EDID", "ICON", "ICO2", "NAM7", "DATA"],
            result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Equal(@"effects\fillfx.dds", ZString(result.Subrecords.Single(s => s.Signature == "ICON")));
        Assert.Equal(@"effects\particle.dds", ZString(result.Subrecords.Single(s => s.Signature == "ICO2")));
        Assert.Equal(@"effects\holes.dds", ZString(result.Subrecords.Single(s => s.Signature == "NAM7")));

        var data = result.Subrecords.Single(s => s.Signature == "DATA").Bytes;
        Assert.Equal(308, data.Length);
        Assert.Equal(0x21, data[0]);
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4))); // swapped BE -> LE
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Efsh_OmitsDataAndWarns_WhenTheBlockIsTheWrongLength()
    {
        // Emitting bytes the schema cannot describe would be reinterpretation, not recovery.
        var result = EfshEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000A01,
            RecordType = "EFSH",
            EditorId = "TruncatedEffectShader",
            Fields = new Dictionary<string, object?> { ["TESEffectShader.Data"] = new byte[12] }
        });

        Assert.Equal(["EDID"], result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Contains(result.Warnings, w => w.Contains("EffectShaderData"));
    }

    // ===================================================================================
    // RGDL — xEdit wbRecord(RGDL): EDID, NVER, DATA(14), XNAM, TNAM, RAFD, RAFB, RAPS, ANAM.
    // ===================================================================================

    [Fact]
    public void Rgdl_EmitsGeneralDataAndBothRequiredReferences()
    {
        var raw = new byte[14];
        BinaryPrimitives.WriteUInt32BigEndian(raw, 7); // Dynamic Bone Count
        raw[8] = 1; // Feedback enabled

        var result = RgdlEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000B00,
            RecordType = "RGDL",
            EditorId = "ProtoRagdoll",
            Fields = new Dictionary<string, object?>
            {
                ["BGSRagdoll.SaveStruct"] = raw,
                ["BGSRagdoll.pPreviewActor"] = 0x0004A1B2u,
                ["BGSRagdoll.pBodyPartData"] = 0x0004A1B3u
            }
        });

        Assert.Equal(
            ["EDID", "DATA", "XNAM", "TNAM"],
            result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Equal(0x0004A1B2u, FormId(result.Subrecords.Single(s => s.Signature == "XNAM")));
        Assert.Equal(0x0004A1B3u, FormId(result.Subrecords.Single(s => s.Signature == "TNAM")));

        var data = result.Subrecords.Single(s => s.Signature == "DATA").Bytes;
        Assert.Equal(14, data.Length);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(1, data[8]);
    }

    [Fact]
    public void Rgdl_OmitsData_WhenTheBoneCountIsImplausible()
    {
        // Guards the deliberate divergence pinned above: the runtime block is read as a plain
        // big-endian u32, NOT through the file schema's UInt32WordSwapped field, so a misaligned
        // capture shows up as an absurd count rather than a quietly halved-and-swapped one.
        var raw = new byte[14];
        BinaryPrimitives.WriteUInt32BigEndian(raw, 0x00CAFE00);

        var result = RgdlEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000B02,
            RecordType = "RGDL",
            EditorId = "MisalignedRagdoll",
            Fields = new Dictionary<string, object?> { ["BGSRagdoll.SaveStruct"] = raw }
        });

        Assert.DoesNotContain(result.Subrecords, s => s.Signature == "DATA");
        Assert.Contains(result.Warnings, w => w.Contains("RagdollSaveStruct"));
    }

    [Fact]
    public void Rgdl_WarnsForEachRequiredMemberItCannotRecover()
    {
        var result = RgdlEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000B01,
            RecordType = "RGDL",
            EditorId = "BareRagdoll"
        });

        Assert.Equal(["EDID"], result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Contains(result.Warnings, w => w.Contains("DATA"));
        Assert.Contains(result.Warnings, w => w.Contains("XNAM"));
        Assert.Contains(result.Warnings, w => w.Contains("TNAM"));
    }

    // ===================================================================================
    // CSNO — xEdit wbRecord(CSNO): EDID(req), FULL, DATA(56), then MODL/ICON groups.
    // ===================================================================================

    [Fact]
    public void Csno_EmitsFullNameAndSchemaConvertedData()
    {
        var raw = new byte[56];
        BinaryPrimitives.WriteSingleBigEndian(raw, 0.75f); // Decks % Before Shuffle
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(36), 8); // Number of Decks

        var result = CsnoEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000C00,
            RecordType = "CSNO",
            EditorId = "ProtoCasino",
            FullName = "The Tops",
            Fields = new Dictionary<string, object?> { ["TESCasino.data"] = raw }
        });

        Assert.Equal(["EDID", "FULL", "DATA"], result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Equal("The Tops", ZString(result.Subrecords.Single(s => s.Signature == "FULL")));

        var data = result.Subrecords.Single(s => s.Signature == "DATA").Bytes;
        Assert.Equal(56, data.Length);
        Assert.Equal(0.75f, BinaryPrimitives.ReadSingleLittleEndian(data));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(36)));
    }

    [Fact]
    public void Csno_ReadsTheCarvePathKeyToo()
    {
        // The ESM carve path stores the block under the subrecord signature rather than the PDB
        // identifier; both key forms must reach the same encoder branch.
        var result = CsnoEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000C01,
            RecordType = "CSNO",
            EditorId = "CarvedCasino",
            Fields = new Dictionary<string, object?> { ["DATA"] = new byte[56] }
        });

        Assert.Contains(result.Subrecords, s => s.Signature == "DATA");
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Csno_EmitsModelAndTextureGroupsInVanillaOrder()
    {
        // Every retail CSNO carries exactly this shape after DATA: MODL x8 (chips + slot
        // machine), MOD2 duplicating the slot-machine path, MOD3/MOD4 tables, ICON x7 reels,
        // ICO2 x4 deck backs (vanilla repeats one path — preserved, not de-duplicated).
        string[] models =
        [
            @"chips\chip001.nif", @"chips\chip005.nif", @"chips\chip010.nif", @"chips\chip025.nif",
            @"chips\chip100.nif", @"chips\chip500.nif", @"chips\roulettechip.nif",
            @"casino\slotmachine.nif", @"casino\blackjacktable.nif", @"casino\roulettetable.nif"
        ];
        string[] textures =
        [
            @"reel\sym1.dds", @"reel\sym2.dds", @"reel\sym3.dds", @"reel\sym4.dds",
            @"reel\sym5.dds", @"reel\sym6.dds", @"reel\sym7.dds",
            @"cards\back.dds", @"cards\back.dds", @"cards\back.dds", @"cards\back.dds"
        ];

        var result = CsnoEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000C02,
            RecordType = "CSNO",
            EditorId = "FullCasino",
            Fields = new Dictionary<string, object?>
            {
                ["TESCasino.modelArrayList"] = models.ToList(),
                ["TESCasino.textureArrayList"] = textures.ToList()
            }
        });

        string[] expected =
        [
            "EDID",
            "MODL", "MODL", "MODL", "MODL", "MODL", "MODL", "MODL", "MODL",
            "MOD2", "MOD3", "MOD4",
            "ICON", "ICON", "ICON", "ICON", "ICON", "ICON", "ICON",
            "ICO2", "ICO2", "ICO2", "ICO2"
        ];
        Assert.Equal(expected, result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.Equal(
            ZString(result.Subrecords.Where(s => s.Signature == "MODL").Last()),
            ZString(result.Subrecords.Single(s => s.Signature == "MOD2")));
        Assert.Equal(@"casino\blackjacktable.nif", ZString(result.Subrecords.Single(s => s.Signature == "MOD3")));
        Assert.All(
            result.Subrecords.Where(s => s.Signature == "ICO2"),
            s => Assert.Equal(@"cards\back.dds", ZString(s)));
    }

    [Fact]
    public void Csno_HoleInChipSlotsDropsTheWholeModlGroupButKeepsTheTables()
    {
        // MODL is positional-by-repetition: a hole mid-group would shift every later chip onto
        // the wrong denomination, so the group is all-or-nothing. MOD3/MOD4 are independently
        // addressable signatures and survive on their own.
        var models = new List<string>
        {
            @"chips\chip001.nif", @"chips\chip005.nif", string.Empty, @"chips\chip025.nif",
            @"chips\chip100.nif", @"chips\chip500.nif", @"chips\roulettechip.nif",
            @"casino\slotmachine.nif", @"casino\blackjacktable.nif", @"casino\roulettetable.nif"
        };

        var result = CsnoEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000C03,
            RecordType = "CSNO",
            EditorId = "HoleyCasino",
            Fields = new Dictionary<string, object?> { ["TESCasino.modelArrayList"] = models }
        });

        Assert.DoesNotContain(result.Subrecords, s => s.Signature is "MODL" or "MOD2");
        Assert.Contains(result.Subrecords, s => s.Signature == "MOD3");
        Assert.Contains(result.Subrecords, s => s.Signature == "MOD4");
        Assert.Contains(result.Warnings, w => w.Contains("7/8"));
    }

    [Fact]
    public void Csno_WrongSlotCountIsRejectedAsMisaligned()
    {
        // 9 entries cannot be a TESCasino model array — a short list means the capture was
        // misaligned or predates the layout regeneration, and positional emission from it would
        // attach wrong meanings. Rejected wholesale, silently (nothing to warn about — the
        // record simply has no usable array).
        var result = CsnoEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000C04,
            RecordType = "CSNO",
            EditorId = "ShortCasino",
            Fields = new Dictionary<string, object?>
            {
                ["TESCasino.modelArrayList"] = Enumerable.Repeat(@"m.nif", 9).ToList()
            }
        });

        Assert.Equal(["EDID"], result.Subrecords.Select(s => s.Signature).ToArray());
        Assert.DoesNotContain(result.Warnings, w => w.Contains("MODL"));
    }

    // ===================================================================================
    // IPDS / DOBJ — both are a single positional FormID slot table, reachable only since
    // pdb_layouts.json gained LF_ARRAY resolution.
    // ===================================================================================

    [Fact]
    public void Ipds_EmitsAllTwelveMaterialSlotsIncludingTheEmptyOnes()
    {
        // Slot order IS the material (Stone, Dirt, Grass, …), so an unused material must stay a
        // NULL in place. Compacting the list would silently reassign every later impact.
        var slots = new List<uint> { 0x001A0001, 0, 0x001A0003, 0, 0, 0, 0, 0, 0, 0, 0, 0x001A000C };

        var result = IpdsEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000D00,
            RecordType = "IPDS",
            EditorId = "ProtoImpactSet",
            Fields = new Dictionary<string, object?> { ["BGSImpactDataSet.ppImpactData"] = slots }
        });

        Assert.Equal(["EDID", "DATA"], result.Subrecords.Select(s => s.Signature).ToArray());
        var data = result.Subrecords.Single(s => s.Signature == "DATA").Bytes;
        Assert.Equal(48, data.Length);
        Assert.Equal(0x001A0001u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        Assert.Equal(0x001A0003u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)));
        Assert.Equal(0x001A000Cu, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(44)));
    }

    [Fact]
    public void Ipds_OmitsData_WhenTheSlotCountDoesNotMatchTheSchema()
    {
        var result = IpdsEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000D01,
            RecordType = "IPDS",
            EditorId = "ShortImpactSet",
            Fields = new Dictionary<string, object?>
            {
                ["BGSImpactDataSet.ppImpactData"] = new List<uint> { 0x001A0001, 0x001A0002 }
            }
        });

        Assert.DoesNotContain(result.Subrecords, s => s.Signature == "DATA");
        Assert.Contains(result.Warnings, w => w.Contains("material"));
    }

    [Fact]
    public void Dobj_EmitsAllThirtyFourDefaultObjectSlots()
    {
        var slots = new List<uint>(new uint[34]) { [0] = 0x00015169, [33] = 0x000A1234 };

        var result = DobjEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000E00,
            RecordType = "DOBJ",
            EditorId = "DefaultObjectManager",
            Fields = new Dictionary<string, object?> { ["BGSDefaultObjectManager.pObjectArray"] = slots }
        });

        var data = result.Subrecords.Single(s => s.Signature == "DATA").Bytes;
        Assert.Equal(136, data.Length);
        Assert.Equal(0x00015169u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        Assert.Equal(0x000A1234u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(132)));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Dobj_RejectsATableWithAnImpossibleLoadOrderIndex()
    {
        // A non-zero slot whose plugin byte is out of range means the read was misaligned, which
        // invalidates the whole positional table — not just that one entry.
        var slots = new List<uint>(new uint[34]) { [5] = 0xFE000123 };

        var result = DobjEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000E01,
            RecordType = "DOBJ",
            EditorId = "MisalignedDefaultObjects",
            Fields = new Dictionary<string, object?> { ["BGSDefaultObjectManager.pObjectArray"] = slots }
        });

        Assert.DoesNotContain(result.Subrecords, s => s.Signature == "DATA");
        Assert.Contains(result.Warnings, w => w.Contains("slots"));
    }
}
