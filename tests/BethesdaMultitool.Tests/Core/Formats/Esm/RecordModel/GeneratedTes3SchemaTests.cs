using BethesdaMultitool.Core.Formats.Esm.RecordModel.Generated;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     Verifies the generated TES3 schema (emitted from xEdit's <c>wbDefinitionsTES3.pas</c> by
///     <c>tools/EsmSchemaGen</c>) is present and structurally sound in the main library — the
///     productionized end of the Pascal → IR → C# → Core pipeline.
/// </summary>
public class GeneratedTes3SchemaTests
{
    [Fact]
    public void Has_All_Records_With_A_Decodable_Header()
    {
        var records = Tes3Schema.Records;

        Assert.Equal(44, records.Count);
        Assert.Contains(records, r => r.Signature == "GMST");
        Assert.Contains(records, r => r.Signature == "NPC_");
        Assert.Contains(records, r => r.Signature == "CELL");

        // TES3 file header → HEDR struct → first field is the float Version (matches real Morrowind.esm: 1.2).
        var tes3 = Assert.Single(records, r => r.Signature == "TES3");
        var hedr = Assert.IsType<StructDef>(Assert.Single(tes3.Members, m => m.Signature == "HEDR"));
        var version = Assert.IsType<FieldDef>(hedr.Members[0]);
        Assert.Equal("Version", version.Name);
        Assert.Equal(PrimType.Float, version.Type);
    }
}