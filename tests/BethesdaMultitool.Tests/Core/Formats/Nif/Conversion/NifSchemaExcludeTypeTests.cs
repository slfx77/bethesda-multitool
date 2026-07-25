using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Conversion;

/// <summary>
///     Regression for nif.xml's <c>excludeT</c> field selector. Oblivion 20.0.0.4 NIFs do not carry a
///     block-size table, so consuming even one excluded field shifts every following block boundary.
///     Retail waterfall NIFs exercise this through <c>NiGeometryData.Num Vertices</c>: the ordinary
///     field is excluded from <c>NiPSysData</c>, which has its own particle-specific replacement.
/// </summary>
public sealed class NifSchemaExcludeTypeTests
{
    [Fact]
    public void MeasureBlock_NiPSysData_SkipsExcludedInheritedNumVertices()
    {
        const string xml = """
                           <niftoolsxml>
                             <basic name="ushort" size="2" integral="true" />
                             <niobject name="NiGeometryData">
                               <field name="Num Vertices" type="ushort" excludeT="NiPSysData" />
                               <field name="Num Vertices" type="ushort" onlyT="NiPSysData" />
                               <field name="Geometry Tail" type="ushort" />
                             </niobject>
                             <niobject name="NiParticlesData" inherit="NiGeometryData" />
                             <niobject name="NiPSysData" inherit="NiParticlesData" />
                           </niftoolsxml>
                           """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var schema = NifSchema.LoadFromStream(stream);
        var converter = new NifSchemaConverter(schema, measure: true);

        // Leave one extra ushort after the valid block. Before excludeT was implemented, the walk
        // consumed it as a second Num Vertices and reported six bytes instead of the correct four.
        byte[] blockAndNextBlockPrefix = [1, 0, 2, 0, 0xCD, 0xAB];
        var (size, _) = converter.MeasureBlock(
            blockAndNextBlockPrefix, 0, blockAndNextBlockPrefix.Length, "NiPSysData");

        Assert.Equal(4, size);
        Assert.Equal("NiPSysData", schema.Objects["NiGeometryData"].Fields[0].ExcludeT);
    }
}