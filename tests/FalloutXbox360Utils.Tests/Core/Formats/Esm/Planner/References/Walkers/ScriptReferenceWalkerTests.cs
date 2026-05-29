using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class ScriptReferenceWalkerTests
{
    [Fact]
    public void Empty_References_Yields_No_References()
    {
        var script = new ScriptRecord { FormId = 0x0014DA58 };
        var walker = new ScriptReferenceWalker();

        var refs = walker.Walk(script).ToList();

        Assert.Empty(refs);
    }

    [Fact]
    public void Each_ScrO_Becomes_Indexed_Field_Path()
    {
        var script = new ScriptRecord
        {
            FormId = 0x0014DA58,
            ReferencedObjects = [0x000ABCDEu, 0x000ABCDFu, 0x000ABCE0u],
        };
        var walker = new ScriptReferenceWalker();

        var refs = walker.Walk(script).ToList();

        Assert.Equal(3, refs.Count);
        Assert.Equal("SCRO[0]", refs[0].FieldPath);
        Assert.Equal(0x000ABCDEu, refs[0].FormId);
        Assert.Equal("SCRO[1]", refs[1].FieldPath);
        Assert.Equal(0x000ABCDFu, refs[1].FormId);
        Assert.Equal("SCRO[2]", refs[2].FieldPath);
        Assert.Equal(0x000ABCE0u, refs[2].FormId);
    }

    [Fact]
    public void Non_Script_Model_Yields_Nothing()
    {
        var walker = new ScriptReferenceWalker();

        var refs = walker.Walk(new object()).ToList();

        Assert.Empty(refs);
    }
}
