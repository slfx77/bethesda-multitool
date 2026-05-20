using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Pins the hardcoded NavMesh + NavMeshInfoMap struct offsets in
///     <c>RuntimeNavMeshReader</c> and <c>RuntimeNavMeshInfoMapReader</c> to the
///     PDB-derived JSON layout. If someone regenerates pdb_layouts.json from a build
///     whose NavMesh/NavMeshInfoMap struct shifts (e.g. switching the embedded PDB
///     to the July Debug 264/64-byte layout), the readers' hardcoded constants
///     silently start reading the wrong fields. This test fails loudly when that
///     happens so the constants get re-derived or the readers become build-aware.
/// </summary>
public sealed class NavMeshPdbOffsetTests
{
    private const byte NavmFormType = 0x43;
    private const byte NaviFormType = 0x38;
    private const int BSSimpleArrayCountFieldOffset = 8;

    [Fact]
    public void NavMesh_pdb_struct_layout_matches_runtime_reader_constants()
    {
        var layout = PdbStructLayouts.Get(NavmFormType);
        Assert.NotNull(layout);
        Assert.Equal("NavMesh", layout!.ClassName);
        Assert.Equal(280, layout.StructSize);

        AssertFieldAtOffset(layout, "iFormID", 12);
        AssertFieldAtOffset(layout, "pParentCell", 52);
        AssertFieldAtOffset(layout, "Vertices", 56);
        AssertFieldAtOffset(layout, "Triangles", 72);
        AssertFieldAtOffset(layout, "DoorPortals", 104);

        // BSSimpleArray<T> layout: data ptr (+0), capacity (+4), count (+8), reserved (+12).
        // RuntimeNavMeshReader.ArrayCountFieldOffset is +8.
        Assert.Equal(8, BSSimpleArrayCountFieldOffset);
    }

    [Fact]
    public void NavMeshInfoMap_pdb_struct_layout_matches_runtime_reader_constants()
    {
        var layout = PdbStructLayouts.Get(NaviFormType);
        Assert.NotNull(layout);
        Assert.Equal("NavMeshInfoMap", layout!.ClassName);
        Assert.Equal(80, layout.StructSize);

        AssertFieldAtOffset(layout, "iFormID", 12);
        AssertFieldAtOffset(layout, "bUpdateAll", 40);
        AssertFieldAtOffset(layout, "bInit", 76);
    }

    private static void AssertFieldAtOffset(PdbTypeLayout layout, string fieldName, int expectedOffset)
    {
        var field = layout.Fields.SingleOrDefault(f => f.Name == fieldName);
        Assert.NotNull(field);
        Assert.Equal(expectedOffset, field!.Offset);
    }
}
