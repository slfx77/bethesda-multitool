using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     TES4 CELL lighting (XCLL) is 36 bytes — the FO3/FNV 40-byte layout MINUS the trailing FogPow
///     float. The parse gate used to be 40-exact, so every Oblivion cell's authored lighting was
///     silently discarded and interiors rendered engine-fallback defaults ("cave lighting seems too
///     bright", user 2026-08-11). Measured on retail masters: Oblivion.esm = 1,770 XCLL subrecords,
///     ALL 36 bytes; FalloutNV.esm = 388, ALL 40.
/// </summary>
public sealed class OblivionCellLightingTests
{
    private const int Tes4Length = 36;
    private const int Fallout3PlusLength = 40;

    private static byte[] BuildXcll(bool withFogPow)
    {
        var bytes = new byte[withFogPow ? Fallout3PlusLength : Tes4Length];
        var s = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s[..], 0x00201510u); // AmbientColor
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 0x00403020u); // DirectionalColor
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 0x00605040u); // FogColor
        BinaryPrimitives.WriteSingleLittleEndian(s[12..], 100f); // FogNear
        BinaryPrimitives.WriteSingleLittleEndian(s[16..], 4000f); // FogFar
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], 30); // DirectionalRotationXY
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], 60); // DirectionalRotationZ
        BinaryPrimitives.WriteSingleLittleEndian(s[28..], 0.5f); // DirectionalFade
        BinaryPrimitives.WriteSingleLittleEndian(s[32..], 12000f); // FogClipDistance
        if (withFogPow)
        {
            BinaryPrimitives.WriteSingleLittleEndian(s[36..], 1.5f); // FogPow (FO3/FNV only)
        }

        return bytes;
    }

    [Fact]
    public void Tes4Xcll_IsRegisteredAtThirtySixBytes_AndOmitsFogPow()
    {
        var schema = SubrecordSchemaRegistry.GetSchema("XCLL", "CELL", Tes4Length);
        Assert.NotNull(schema);

        var view = SubrecordSchemaView.TryRead("XCLL", "CELL", BuildXcll(false), false);
        Assert.NotNull(view);
        var raw = view!.Raw;
        // The nine TES4 fields must all decode…
        Assert.Equal(0x00201510u, Assert.IsType<uint>(raw["AmbientColor"]));
        Assert.Equal(0x00403020u, Assert.IsType<uint>(raw["DirectionalColor"]));
        Assert.Equal(100f, Assert.IsType<float>(raw["FogNear"]));
        Assert.Equal(4000f, Assert.IsType<float>(raw["FogFar"]));
        Assert.Equal(12000f, Assert.IsType<float>(raw["FogClipDistance"]));
        // …and FogPow must NOT be fabricated for a TES4 cell.
        Assert.False(raw.ContainsKey("FogPow"));
    }

    [Fact]
    public void Fallout3PlusXcll_StillDecodesFogPow()
    {
        // The 40-byte path must stay byte-identical — this fix widens Oblivion in, it does not
        // change FO3/FNV.
        var view = SubrecordSchemaView.TryRead("XCLL", "CELL", BuildXcll(true), false);
        Assert.NotNull(view);
        Assert.Equal(1.5f, Assert.IsType<float>(view!.Raw["FogPow"]));
        Assert.Equal(12000f, Assert.IsType<float>(view.Raw["FogClipDistance"]));
    }

    [Fact]
    public void BothLayouts_ShareFieldOrderUpToFogClipDistance()
    {
        // The only difference is the trailing float; any divergence earlier would mean one of the
        // two layouts is wrong.
        var tes4 = SubrecordSchemaView.TryRead("XCLL", "CELL", BuildXcll(false), false)!.Raw;
        var fnv = SubrecordSchemaView.TryRead("XCLL", "CELL", BuildXcll(true), false)!.Raw;

        foreach (var key in tes4.Keys)
        {
            Assert.True(fnv.ContainsKey(key), $"FO3/FNV layout is missing TES4 field '{key}'.");
            Assert.Equal(fnv[key], tes4[key]);
        }

        Assert.Equal(tes4.Count + 1, fnv.Count);
    }

    [Fact]
    public void Tes4Xcll_HandlerToEncoder_PreservesExactThirtySixBytePayload()
    {
        const uint cellFormId = 0x01001000;
        var originalXcll = BuildXcll(false);
        var recordBytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            false,
            ("EDID", "SyntheticCave\0"u8.ToArray()),
            ("DATA", new byte[] { 0x01 }),
            ("XCLL", originalXcll));
        var record = new DetectedMainRecord(
            "CELL", (uint)(recordBytes.Length - 24), 0, cellFormId, 0, false);
        var context = new RecordParserContext(
            new EsmRecordScanResult
            {
                Game = BethesdaGame.Oblivion,
                MainRecords = [record]
            },
            null,
            new ByteArrayMemoryAccessor(recordBytes),
            recordBytes.Length,
            null);

        var parsedCell = Assert.Single(new CellRecordHandler(context).ParseCells());
        var lighting = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(parsedCell.LightingData);
        Assert.Equal(0x00201510u, Assert.IsType<uint>(lighting["AmbientColor"]));
        Assert.Equal(30, Assert.IsType<int>(lighting["DirectionalRotationXY"]));
        Assert.Equal(12000f, Assert.IsType<float>(lighting["FogClipDistance"]));
        Assert.False(lighting.ContainsKey("FogPow"));

        var encoded = new CellEncoder().Encode(parsedCell);
        var encodedXcll = Assert.Single(encoded.Subrecords, sub => sub.Signature == "XCLL");

        Assert.Equal(Tes4Length, encodedXcll.Bytes.Length);
        Assert.Equal(originalXcll, encodedXcll.Bytes);
    }
}