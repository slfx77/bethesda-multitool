using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Encoders for record types that the DMP pipeline decodes but previously could not emit.
///     MSTT/ANIO arrive as <see cref="GenericEsmRecord" /> from <c>RuntimeGenericReader</c>, whose
///     <c>Fields</c> keys are PDB identifiers ("Owner.Name") rather than subrecord signatures —
///     the main regression risk these tests pin.
/// </summary>
public class GenericOnlyEncoderTests
{
    private static GenericEsmRecord Mstt(Dictionary<string, object?>? fields = null)
    {
        return new GenericEsmRecord
        {
            FormId = 0x01000123,
            RecordType = "MSTT",
            EditorId = "ProtoMovableCrate",
            FullName = "Crate",
            ModelPath = @"Clutter\Crate01.NIF",
            Bounds = new ObjectBounds { X1 = -16, Y1 = -16, Z1 = 0, X2 = 16, Y2 = 16, Z2 = 32 },
            Fields = fields ?? []
        };
    }

    // ===================================================================================
    // MSTT
    // ===================================================================================

    [Fact]
    public void Mstt_EmitsXEditCanonicalSubrecordOrder()
    {
        var subs = MsttEncoder.EncodeNew(Mstt(new Dictionary<string, object?>
        {
            ["BGSMovableStatic.data"] = "01",
            ["BGSMovableStatic.pSoundLoop"] = 0x0004A1B2u
        })).Subrecords;

        // xEdit wbRecord(MSTT): EDID, OBND, FULL, MODL, DATA, SNAM.
        Assert.Equal(
            ["EDID", "OBND", "FULL", "MODL", "DATA", "SNAM"],
            subs.Select(s => s.Signature).ToArray());
    }

    [Fact]
    public void Mstt_ReadsSoundFromPdbKeyedField()
    {
        var subs = MsttEncoder.EncodeNew(Mstt(new Dictionary<string, object?>
        {
            ["BGSMovableStatic.pSoundLoop"] = 0x0004A1B2u
        })).Subrecords;

        var snam = Assert.Single(subs, s => s.Signature == "SNAM");
        Assert.Equal(0x0004A1B2u, BinaryPrimitives.ReadUInt32LittleEndian(snam.Bytes));
    }

    [Fact]
    public void Mstt_ReadsSoundFromSignatureKeyedField()
    {
        // The ESM carve path keys by subrecord signature instead of PDB identifier.
        var subs = MsttEncoder.EncodeNew(Mstt(new Dictionary<string, object?>
        {
            ["SNAM"] = 0x000512EFu
        })).Subrecords;

        var snam = Assert.Single(subs, s => s.Signature == "SNAM");
        Assert.Equal(0x000512EFu, BinaryPrimitives.ReadUInt32LittleEndian(snam.Bytes));
    }

    [Fact]
    public void Mstt_DecodesHexStringDataByte()
    {
        // RuntimeGenericReader renders structs of <=8 bytes as an uppercase hex string.
        var subs = MsttEncoder.EncodeNew(Mstt(new Dictionary<string, object?>
        {
            ["BGSMovableStatic.data"] = "01"
        })).Subrecords;

        var data = Assert.Single(subs, s => s.Signature == "DATA");
        Assert.Equal([0x01], data.Bytes);
    }

    [Fact]
    public void Mstt_EmitsRequiredDataWithWarning_WhenNotCaptured()
    {
        var result = MsttEncoder.EncodeNew(Mstt());

        // DATA is .SetRequired in the schema, so it must still be present.
        var data = Assert.Single(result.Subrecords, s => s.Signature == "DATA");
        Assert.Equal([0x00], data.Bytes);
        Assert.Contains(result.Warnings, w => w.Contains("MOVABLE_STATIC_DATA"));
    }

    [Fact]
    public void Mstt_StillRejectsTheRetiredStructDescriptorPlaceholder()
    {
        // RuntimeGenericReader no longer produces "[TypeName, NB]" for a >8-byte struct — it hands
        // back the raw bytes. The guard stays so a stale capture or a hand-built record cannot
        // smuggle a descriptor string in and have its characters parsed as hex.
        var result = MsttEncoder.EncodeNew(Mstt(new Dictionary<string, object?>
        {
            ["BGSMovableStatic.data"] = "[MOVABLE_STATIC_DATA, 16B]"
        }));

        var data = Assert.Single(result.Subrecords, s => s.Signature == "DATA");
        Assert.Equal([0x00], data.Bytes);
        Assert.Contains(result.Warnings, w => w.Contains("MOVABLE_STATIC_DATA"));
    }

    [Fact]
    public void LargeEmbeddedStructBytes_ReachTryBytes_InsteadOfBeingDropped()
    {
        // The contract this replaces: >8-byte structs used to arrive as a data-free descriptor
        // string, which made every large embedded block (CAMS's 40-byte CAMERA_SHOT_DATA among
        // them) structurally unemittable. They now arrive as raw bytes, and TryBytes accepts them.
        var payload = new byte[16];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(0xA0 + i);
        }

        var record = Mstt(new Dictionary<string, object?> { ["BGSMovableStatic.data"] = payload });

        Assert.Equal(payload, GenericRecordFields.TryBytes(record, 16, "BGSMovableStatic.data"));
        // Length is still checked: a caller asking for a different size gets nothing.
        Assert.Null(GenericRecordFields.TryBytes(record, 12, "BGSMovableStatic.data"));
    }

    [Fact]
    public void Mstt_OmitsSnam_WhenSoundPointerIsNull()
    {
        var subs = MsttEncoder.EncodeNew(Mstt(new Dictionary<string, object?>
        {
            ["BGSMovableStatic.pSoundLoop"] = 0u
        })).Subrecords;

        Assert.DoesNotContain(subs, s => s.Signature == "SNAM");
    }

    // ===================================================================================
    // ANIO
    // ===================================================================================

    [Fact]
    public void Anio_EmitsEdidModlData_AndNoObnd()
    {
        var subs = AnioEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000200,
            RecordType = "ANIO",
            EditorId = "ProtoAnimObject",
            ModelPath = @"Clutter\Anim01.NIF",
            Bounds = new ObjectBounds { X1 = -8, Y1 = -8, Z1 = 0, X2 = 8, Y2 = 8, Z2 = 8 },
            Fields = new Dictionary<string, object?> { ["TESObjectANIO.pIdleAnim"] = 0x00033445u }
        }).Subrecords;

        // ANIO has neither OBND nor FULL in the FNV schema even when bounds were captured.
        Assert.Equal(["EDID", "MODL", "DATA"], subs.Select(s => s.Signature).ToArray());
        Assert.Equal(0x00033445u,
            BinaryPrimitives.ReadUInt32LittleEndian(subs.Single(s => s.Signature == "DATA").Bytes));
    }

    [Fact]
    public void Anio_OmitsData_RatherThanEmittingNullIdleReference()
    {
        var result = AnioEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000201,
            RecordType = "ANIO",
            EditorId = "ProtoAnimObject",
            ModelPath = @"Clutter\Anim01.NIF",
            Fields = new Dictionary<string, object?>()
        });

        Assert.DoesNotContain(result.Subrecords, s => s.Signature == "DATA");
        Assert.Contains(result.Warnings, w => w.Contains("pIdleAnim"));
    }

    // ===================================================================================
    // CLMT
    // ===================================================================================

    [Fact]
    public void Clmt_EmitsXEditCanonicalSubrecordOrder()
    {
        var subs = ClmtEncoder.EncodeNew(new ClimateRecord
        {
            FormId = 0x01000300,
            EditorId = "ProtoClimate",
            SunTexture = @"Textures\Sky\Sun.dds",
            SunGlareTexture = @"Textures\Sky\SunGlare.dds",
            ModelPath = @"Sky\Sky.NIF",
            WeatherTypes = [new ClimateWeatherEntry(0x00015AA1, 40, 0)],
            Timing = new ClimateTimingData(30, 42, 90, 102, 20, 24)
        }).Subrecords;

        // xEdit wbRecord(CLMT): EDID, WLST, FNAM, GNAM, MODL, TNAM.
        Assert.Equal(
            ["EDID", "WLST", "FNAM", "GNAM", "MODL", "TNAM"],
            subs.Select(s => s.Signature).ToArray());
    }

    [Fact]
    public void Clmt_WlstEntriesAre12ByteLittleEndianTriples()
    {
        var wlst = ClmtEncoder.EncodeWlst([
            new ClimateWeatherEntry(0x00015AA1, 40, 0x00000ABC),
            new ClimateWeatherEntry(0x00015AA2, 60, 0)
        ]);

        Assert.Equal(24, wlst.Length);
        Assert.Equal(0x00015AA1u, BinaryPrimitives.ReadUInt32LittleEndian(wlst.AsSpan(0, 4)));
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(wlst.AsSpan(4, 4)));
        Assert.Equal(0x00000ABCu, BinaryPrimitives.ReadUInt32LittleEndian(wlst.AsSpan(8, 4)));
        Assert.Equal(0x00015AA2u, BinaryPrimitives.ReadUInt32LittleEndian(wlst.AsSpan(12, 4)));
        Assert.Equal(60, BinaryPrimitives.ReadInt32LittleEndian(wlst.AsSpan(16, 4)));
    }

    [Fact]
    public void Clmt_TnamIsSixRawBytesInEngineOrder()
    {
        // Raw 10-minute units — the engine multiplies by 1/6 for hours, so no scaling here.
        var tnam = ClmtEncoder.EncodeTnam(new ClimateTimingData(30, 42, 90, 102, 20, 24));

        Assert.Equal([30, 42, 90, 102, 20, 24], tnam);
    }

    [Fact]
    public void Clmt_OmitsOptionalSubrecords_WhenAbsent()
    {
        var subs = ClmtEncoder.EncodeNew(new ClimateRecord
        {
            FormId = 0x01000301,
            EditorId = "BareClimate"
        }).Subrecords;

        Assert.Equal(["EDID"], subs.Select(s => s.Signature).ToArray());
    }

    // ===================================================================================
    // TACT / ASPC / ADDN
    // ===================================================================================

    [Fact]
    public void Tact_EmitsXEditCanonicalSubrecordOrder()
    {
        var subs = TactEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000400,
            RecordType = "TACT",
            EditorId = "ProtoRadio",
            FullName = "Radio",
            ModelPath = @"Clutter\Radio01.NIF",
            Bounds = new ObjectBounds { X1 = -8, Y1 = -8, Z1 = 0, X2 = 8, Y2 = 8, Z2 = 8 },
            Fields = new Dictionary<string, object?>
            {
                ["TESScriptableForm.pFormScript"] = 0x00011111u,
                ["TESObjectACTI.pSoundLoop"] = 0x00022222u,
                ["BGSTalkingActivator.pVoiceType"] = 0x00033333u,
                ["TESObjectACTI.pRadioTemplate"] = 0x00044444u
            }
        }).Subrecords;

        Assert.Equal(
            ["EDID", "OBND", "FULL", "MODL", "SCRI", "SNAM", "VNAM", "INAM"],
            subs.Select(s => s.Signature).ToArray());
    }

    [Fact]
    public void Tact_DoesNotEmitActivateSound_WhichHasNoTactSubrecord()
    {
        // TESObjectACTI.pSoundActivate is inherited but has no TACT subrecord in the FNV schema.
        var subs = TactEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000401,
            RecordType = "TACT",
            EditorId = "ProtoRadio",
            Fields = new Dictionary<string, object?>
            {
                ["TESObjectACTI.pSoundActivate"] = 0x00099999u
            }
        }).Subrecords;

        Assert.Equal(["EDID"], subs.Select(s => s.Signature).ToArray());
    }

    [Fact]
    public void Aspc_AlwaysEmitsFivePositionalSnams_EvenWhenSlotsAreNull()
    {
        // Only Dusk is captured. All five slots must still be emitted, in order, or every later
        // sound would shift into the wrong time-of-day slot.
        var subs = AspcEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000500,
            RecordType = "ASPC",
            EditorId = "ProtoAcoustic",
            Fields = new Dictionary<string, object?>
            {
                ["BGSAcousticSpace.pDuskSound"] = 0x00055555u
            }
        }).Subrecords;

        var snams = subs.Where(s => s.Signature == "SNAM").ToArray();
        Assert.Equal(5, snams.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(snams[0].Bytes));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(snams[1].Bytes));
        Assert.Equal(0x00055555u, BinaryPrimitives.ReadUInt32LittleEndian(snams[2].Bytes));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(snams[3].Bytes));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(snams[4].Bytes));
    }

    [Fact]
    public void Aspc_EmitsRequiredScalarsAndOmitsOptionalRdat()
    {
        var subs = AspcEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000501,
            RecordType = "ASPC",
            EditorId = "ProtoAcoustic",
            Fields = new Dictionary<string, object?>
            {
                ["BGSAcousticSpace.iWallaPop"] = 7u,
                ["BGSAcousticSpace.eEnvType"] = 4u,
                // A real System.Boolean, which is what the PDB's kind:"bool" actually boxes as.
                // Seeding 1u here previously hid that TryUInt had no bool arm, so INAM came out
                // structurally 0 on every record regardless of what the capture held.
                ["BGSAcousticSpace.bIsInterior"] = true
            }
        }).Subrecords;

        // RDAT is the only optional member: EDID, 5×SNAM, WNAM, ANAM, INAM.
        Assert.Equal(
            ["EDID", "SNAM", "SNAM", "SNAM", "SNAM", "SNAM", "WNAM", "ANAM", "INAM"],
            subs.Select(s => s.Signature).ToArray());
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(subs.Single(s => s.Signature == "WNAM").Bytes));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(subs.Single(s => s.Signature == "ANAM").Bytes));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(subs.Single(s => s.Signature == "INAM").Bytes));
    }

    [Fact]
    public void Addn_PrefersCapturedAddonDataBytesForDnam()
    {
        var subs = AddnEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000600,
            RecordType = "ADDN",
            EditorId = "ProtoAddon",
            ModelPath = @"Effects\Node01.NIF",
            Fields = new Dictionary<string, object?>
            {
                ["BGSAddonNode.iIndex"] = 12u,
                ["BGSAddonNode.Data"] = "0A000000"
            }
        }).Subrecords;

        Assert.Equal(["EDID", "MODL", "DATA", "DNAM"], subs.Select(s => s.Signature).ToArray());
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(subs.Single(s => s.Signature == "DATA").Bytes));
        Assert.Equal([0x0A, 0x00, 0x00, 0x00], subs.Single(s => s.Signature == "DNAM").Bytes);
    }

    [Fact]
    public void Addn_SynthesizesDnamFromParticleSystemIndex_WhenAddonDataMissing()
    {
        var subs = AddnEncoder.EncodeNew(new GenericEsmRecord
        {
            FormId = 0x01000601,
            RecordType = "ADDN",
            EditorId = "ProtoAddon",
            Fields = new Dictionary<string, object?>
            {
                ["BGSAddonNode.iIndex"] = 3u,
                ["BGSAddonNode.iMasterParticleSystemIndex"] = 5u
            }
        }).Subrecords;

        var dnam = subs.Single(s => s.Signature == "DNAM").Bytes;
        Assert.Equal(4, dnam.Length);
        Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(dnam.AsSpan(0, 2)));
    }

    // ===================================================================================
    // v139 garbage guards — the generic reader leaks raw Xbox VAs and misaligned structs
    // for types whose runtime layout diverges from the PDB (observed: 100% of v138 MSTTs
    // carried SNAM=0x82339658-style module-space VAs and inverted OBND extents).
    // ===================================================================================

    [Theory]
    [InlineData(0x82339658u)] // Xbox module space — the observed v138 leak
    [InlineData(0x40001000u)] // Xbox heap
    [InlineData(0x02000001u)] // load-order byte above any legal master/plugin index here
    public void Mstt_RejectsRawVirtualAddressesAsSoundRefs(uint rawVa)
    {
        var subs = MsttEncoder.EncodeNew(Mstt(new Dictionary<string, object?>
        {
            ["BGSMovableStatic.pSoundLoop"] = rawVa
        })).Subrecords;

        Assert.DoesNotContain(subs, s => s.Signature == "SNAM");
    }

    [Fact]
    public void Mstt_OmitsObnd_WhenBoundsAreImplausible()
    {
        var record = Mstt() with
        {
            // Inverted extents — the misaligned-read signature (min > max).
            Bounds = new ObjectBounds { X1 = 31, Y1 = 31, Z1 = 774, X2 = 6945, Y2 = 20487, Z2 = -8196 }
        };

        var result = MsttEncoder.EncodeNew(record);

        Assert.DoesNotContain(result.Subrecords, s => s.Signature == "OBND");
        Assert.Contains(result.Warnings, w => w.Contains("implausible bounds"));
    }

    [Fact]
    public void Remapper_RewritesNewTypeRefs_MsttSnamAnioDataClmtWlst()
    {
        var aliases = new Dictionary<uint, uint>
        {
            [0x000A1111] = 0x01002001,
            [0x000B2222] = 0x01002002,
            [0x000C3333] = 0x01002003
        };

        var mstt = EncodedSubrecordFormIdRemapper.Remap("MSTT",
            [new EncodedSubrecord("SNAM", BitConverter.GetBytes(0x000A1111u))], aliases);
        Assert.Equal(0x01002001u, BinaryPrimitives.ReadUInt32LittleEndian(mstt[0].Bytes));

        var anio = EncodedSubrecordFormIdRemapper.Remap("ANIO",
            [new EncodedSubrecord("DATA", BitConverter.GetBytes(0x000B2222u))], aliases);
        Assert.Equal(0x01002002u, BinaryPrimitives.ReadUInt32LittleEndian(anio[0].Bytes));

        // WLST entry: WTHR @0 (remapped), chance @4 (untouched), GLOB @8 (remapped).
        var wlst = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(wlst.AsSpan(0, 4), 0x000A1111);
        BinaryPrimitives.WriteInt32LittleEndian(wlst.AsSpan(4, 4), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(wlst.AsSpan(8, 4), 0x000C3333);
        var clmt = EncodedSubrecordFormIdRemapper.Remap("CLMT",
            [new EncodedSubrecord("WLST", wlst)], aliases);
        Assert.Equal(0x01002001u, BinaryPrimitives.ReadUInt32LittleEndian(clmt[0].Bytes.AsSpan(0, 4)));
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(clmt[0].Bytes.AsSpan(4, 4)));
        Assert.Equal(0x01002003u, BinaryPrimitives.ReadUInt32LittleEndian(clmt[0].Bytes.AsSpan(8, 4)));
    }

    // ===================================================================================
    // Wiring — an encoder that nothing routes to is dead code (the GRAS/IMGS/TREE/PWAT bug)
    // ===================================================================================

    [Theory]
    [InlineData("MSTT")]
    [InlineData("ANIO")]
    [InlineData("TACT")]
    [InlineData("ASPC")]
    [InlineData("ADDN")]
    [InlineData("CLMT")]
    [InlineData("GRAS")]
    [InlineData("IMGS")]
    [InlineData("PWAT")]
    [InlineData("TREE")]
    [InlineData("LSCR")]
    [InlineData("CHIP")]
    [InlineData("IDLM")]
    [InlineData("CAMS")]
    [InlineData("MSET")]
    public void NewlyWiredTypes_AreRegisteredAndDispatchable(string recordType)
    {
        Assert.True(
            RecordEncoderRegistry.CreateDefault().TryGet(recordType, out _),
            $"{recordType} is missing from RecordEncoderRegistry.CreateDefault().");
        Assert.Contains(recordType, NewTopLevelRecordEncoderDispatcher.GetSupportedRecordTypes());
    }
}