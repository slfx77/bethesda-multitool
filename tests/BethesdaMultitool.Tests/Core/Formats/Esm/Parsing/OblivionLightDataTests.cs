using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the LIGH DATA struct decode across its two shipping sizes. TES4 authors both a 24-byte
///     variant (Time/Radius/Color/Flags/Falloff/FOV — xEdit wbDefinitionsTES4 marks Value/Weight
///     optional via wbStruct(DATA, …, 6)) and the full 32-byte layout FO3+ always use. The old hard
///     32-byte guard parsed every 24-byte TES4 light as radius 0, so no Oblivion placed light ever
///     reached the viewer's point-light pass.
/// </summary>
public sealed class OblivionLightDataTests
{
    private static byte[] BuildData(int length, bool bigEndian)
    {
        var d = new byte[length];
        WriteInt(d, 0, -1, bigEndian); // Time (infinite)
        WriteUInt(d, 4, 512, bigEndian); // Radius
        WriteUInt(d, 8, 0x00B19C78, bigEndian); // Color RGBA
        WriteUInt(d, 12, 0x00000009, bigEndian); // Flags: Dynamic + Flicker
        WriteFloat(d, 16, 0.6f, bigEndian); // Falloff Exponent
        WriteFloat(d, 20, 100f, bigEndian); // FOV
        if (length >= 32)
        {
            WriteInt(d, 24, 35, bigEndian); // Value
            WriteFloat(d, 28, 1.5f, bigEndian); // Weight
        }

        return d;
    }

    private static void WriteInt(byte[] data, int offset, int value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, 4), value);
        else BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);
    }

    private static void WriteUInt(byte[] data, int offset, uint value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
        else BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    }

    private static void WriteFloat(byte[] data, int offset, float value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset, 4), value);
        else BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadLightData_TruncatedTes4Variant_ReadsSixFieldsAndZeroesOptionalTail(bool bigEndian)
    {
        var fields = MiscWorldObjectHandler.ReadLightData(BuildData(24, bigEndian), bigEndian);

        Assert.Equal(-1, fields.Duration);
        Assert.Equal(512u, fields.Radius);
        Assert.Equal(0x00B19C78u, fields.Color);
        Assert.Equal(9u, fields.Flags);
        Assert.Equal(0.6f, fields.FalloffExponent, 4);
        Assert.Equal(100f, fields.Fov, 4);
        Assert.Equal(0, fields.Value);
        Assert.Equal(0f, fields.Weight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadLightData_FullLayout_ReadsAllEightFields(bool bigEndian)
    {
        var fields = MiscWorldObjectHandler.ReadLightData(BuildData(32, bigEndian), bigEndian);

        Assert.Equal(512u, fields.Radius);
        Assert.Equal(0.6f, fields.FalloffExponent, 4);
        Assert.Equal(100f, fields.Fov, 4);
        Assert.Equal(35, fields.Value);
        Assert.Equal(1.5f, fields.Weight, 4);
    }

    [Fact]
    public void PlacedLight_BuildsFromTruncatedVariantRadius()
    {
        var fields = MiscWorldObjectHandler.ReadLightData(BuildData(24, false), false);
        var light = new LightRecord
        {
            FormId = 0x200,
            Radius = fields.Radius,
            Color = fields.Color,
            Flags = fields.Flags,
            FalloffExponent = fields.FalloffExponent,
            Fov = fields.Fov
        };
        var placement = new PlacedReference
        {
            FormId = 0x100,
            BaseFormId = 0x200,
            RecordType = "REFR",
            Scale = 1f
        };

        var built = PlacedLight.TryBuild(placement, light);

        Assert.NotNull(built);
        Assert.Equal(512f, built!.Value.Radius);
    }
}