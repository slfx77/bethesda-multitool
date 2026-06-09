using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     FLOR (Flora) new-record encoder. FLOR has no typed model — it is captured/parsed as a
///     <see cref="GenericEsmRecord" /> — so the encoder reads identity + FormID refs straight from
///     the generic shape and emits the canonical fopdoc <c>wbFLOR</c> subrecord order. Also covers
///     FormID remapping of FLOR's PFIG/SCRI/SNAM references.
/// </summary>
public class FlorEncoderTests
{
    private const uint IngredientFormId = 0x000F2000;
    private const uint SoundFormId = 0x000F3000;
    private const uint ScriptFormId = 0x000F4000;

    [Fact]
    public void EncodeNew_EmitsCanonicalOrderAndDecodableFormIds()
    {
        var flor = new GenericEsmRecord
        {
            FormId = 0x01000800,
            RecordType = "FLOR",
            EditorId = "TestFlora",
            FullName = "Test Flora",
            ModelPath = "Plants\\TestFlora.NIF",
            // Runtime-reader shape: FormIDs stored as boxed uint under the subrecord-signature key.
            Fields = new Dictionary<string, object?>
            {
                ["PFIG"] = IngredientFormId,
                ["SCRI"] = ScriptFormId,
                ["SNAM"] = SoundFormId
            }
        };

        var encoded = FlorEncoder.EncodeNew(flor);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "FULL", "MODL", "SCRI", "PFIG", "SNAM"], sigs);

        Assert.Equal(ScriptFormId, ReadFormId(encoded, "SCRI"));
        Assert.Equal(IngredientFormId, ReadFormId(encoded, "PFIG"));
        Assert.Equal(SoundFormId, ReadFormId(encoded, "SNAM"));
        Assert.Empty(encoded.Warnings);
    }

    [Fact]
    public void EncodeNew_EmitsObndAndPfpcWhenPresent()
    {
        var flor = new GenericEsmRecord
        {
            FormId = 0x01000801,
            RecordType = "FLOR",
            EditorId = "BoundedFlora",
            Bounds = new ObjectBounds { X1 = -10, Y1 = -11, Z1 = -12, X2 = 20, Y2 = 21, Z2 = 22 },
            Fields = new Dictionary<string, object?>
            {
                ["PFPC"] = new byte[] { 64, 100, 64, 0 } // per-season harvest chance
            }
        };

        var encoded = FlorEncoder.EncodeNew(flor);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "OBND", "PFPC"], sigs);

        var obnd = encoded.Subrecords.Single(s => s.Signature == "OBND");
        Assert.Equal(12, obnd.Bytes.Length);
        Assert.Equal((short)-10, BinaryPrimitives.ReadInt16LittleEndian(obnd.Bytes));

        var pfpc = encoded.Subrecords.Single(s => s.Signature == "PFPC");
        Assert.Equal(new byte[] { 64, 100, 64, 0 }, pfpc.Bytes);
    }

    [Fact]
    public void EncodeNew_MissingEditorId_EmitsEmptyEdidWithWarning()
    {
        var flor = new GenericEsmRecord { FormId = 0x01000802, RecordType = "FLOR" };

        var encoded = FlorEncoder.EncodeNew(flor);

        Assert.Equal(["EDID"], encoded.Subrecords.Select(s => s.Signature));
        Assert.Single(encoded.Subrecords[0].Bytes); // just the null terminator
        Assert.Single(encoded.Warnings);
    }

    [Fact]
    public void EncodeNew_UnwrapsEsmParseNestedFieldDictionary()
    {
        // ESM-parse shape: schema-decoded subrecords are stored as a nested field dictionary.
        var flor = new GenericEsmRecord
        {
            FormId = 0x01000803,
            RecordType = "FLOR",
            EditorId = "NestedFlora",
            Fields = new Dictionary<string, object?>
            {
                ["PFIG"] = new Dictionary<string, object?> { ["Ingredient FormID"] = IngredientFormId }
            }
        };

        var encoded = FlorEncoder.EncodeNew(flor);

        Assert.Equal(IngredientFormId, ReadFormId(encoded, "PFIG"));
    }

    [Fact]
    public void Remapper_RewritesPfigScriSnamToAliases()
    {
        var aliases = new Dictionary<uint, uint>
        {
            [IngredientFormId] = 0x01005000,
            [ScriptFormId] = 0x01005001,
            [SoundFormId] = 0x01005002
        };

        var subs = new List<EncodedSubrecord>
        {
            NewRecordSubrecords.EncodeFormIdSubrecord("PFIG", IngredientFormId),
            NewRecordSubrecords.EncodeFormIdSubrecord("SCRI", ScriptFormId),
            NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", SoundFormId)
        };

        var remapped = EncodedSubrecordFormIdRemapper.Remap("FLOR", subs, aliases);

        Assert.Equal(0x01005000u, ReadFormId(remapped, "PFIG"));
        Assert.Equal(0x01005001u, ReadFormId(remapped, "SCRI"));
        Assert.Equal(0x01005002u, ReadFormId(remapped, "SNAM"));
    }

    private static uint ReadFormId(EncodedRecord encoded, string signature)
    {
        return ReadFormId(encoded.Subrecords, signature);
    }

    private static uint ReadFormId(IReadOnlyList<EncodedSubrecord> subrecords, string signature)
    {
        var sub = subrecords.Single(s => s.Signature == signature);
        return BinaryPrimitives.ReadUInt32LittleEndian(sub.Bytes);
    }
}