using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class IngrEncoderTests
{
    [Fact]
    public void PartialDiagnosticProjection_UsesFnvDataAndEtypWidths()
    {
        var encoded = IngrEncoder.EncodeNew(new IngredientRecord
        {
            FormId = 0x01001234,
            EditorId = "TestIngredient",
            Weight = 1.25f,
            EquipType = 3
        });

        var etyp = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "ETYP");
        var data = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "DATA");

        Assert.Equal(4, etyp.Bytes.Length);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(etyp.Bytes));
        Assert.Equal(4, data.Bytes.Length);
        Assert.Equal(1.25f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes));
        Assert.Contains(encoded.Warnings, warning => warning.Contains("production planning excludes INGR"));
    }
}
