using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.RuntimeBuffer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeBuffer;

/// <summary>
///     The field indices decide which strings the vtable reverse lookup can name. They are built
///     from the shipped PDB layout database, so these assert against real data rather than a
///     fixture — the point is precisely that the database describes more than the code was reading.
/// </summary>
public sealed class OwnershipFieldIndexBuilderTests
{
    /// <summary>
    ///     The 449 auxiliary struct layouts must be indexed, not just the 116 FormType records.
    /// </summary>
    [Fact]
    public void BuildFieldIndices_IndexesAuxiliaryStructClasses()
    {
        var (_, classIndex, _) = OwnershipFieldIndexBuilder.BuildFieldIndices();

        Assert.True(classIndex.ContainsKey("NiObjectNET"),
            "NiObjectNET is an auxStructs entry with a string field and must be indexed.");

        var (formType, fields) = classIndex["NiObjectNET"];
        Assert.Equal(0, formType); // not a record class
        Assert.Contains(fields, f => f.Offset == 8 && f.Label.Contains("m_kName", StringComparison.Ordinal));
    }

    /// <summary>
    ///     <c>NiFixedString</c> is a 4-byte struct whose sole member is a <c>char*</c> at +0, so the
    ///     word at the field's own offset is the string pointer. The index used to accept only
    ///     <c>BSStringT</c>, which made the entire Gamebryo naming family invisible.
    /// </summary>
    [Fact]
    public void BuildFieldIndices_TreatsNiFixedStringAsAStringField()
    {
        var (_, classIndex, _) = OwnershipFieldIndexBuilder.BuildFieldIndices();

        var niFixedStringClasses = PdbStructLayouts.AuxStructs
            .Where(entry => entry.Value.Fields.Any(f => f.TypeDetail == "NiFixedString"))
            .Select(entry => entry.Key)
            .ToList();

        Assert.NotEmpty(niFixedStringClasses);
        foreach (var className in niFixedStringClasses)
        {
            Assert.True(classIndex.ContainsKey(className),
                $"{className} declares a NiFixedString member but is missing from the index.");
        }
    }

    /// <summary>
    ///     A string can live inside an embedded struct member — a shader's texture paths sit in
    ///     nested layouts, not at a top-level offset — so the index must compose
    ///     <c>member.Offset + inner.Offset</c>. Without that step <c>TESEffectShader</c> has no
    ///     string offsets at all and is absent from every index.
    /// </summary>
    [Fact]
    public void BuildFieldIndices_ComposesStringOffsetsInsideEmbeddedStructs()
    {
        var (_, classIndex, _) = OwnershipFieldIndexBuilder.BuildFieldIndices();

        Assert.True(classIndex.ContainsKey("TESEffectShader"),
            "TESEffectShader's texture paths are only reachable by composing an embedded struct's "
            + "offsets; its absence means the composition step is not running.");

        var (_, fields) = classIndex["TESEffectShader"];
        Assert.NotEmpty(fields);
        Assert.All(fields, f => Assert.True(f.Offset >= 0));
    }

    /// <summary>
    ///     These offsets all contradict <c>pdb_layouts.json</c>, and all resolve real strings on
    ///     older dumps. The PDBs postdate the corpus, so the tempting cleanup — delete what
    ///     disagrees with them — silently discards working recovery.
    /// </summary>
    [Theory]
    [InlineData("Script", 144)]
    [InlineData("Script", 152)]
    [InlineData("BGSTerminal", 208)]
    [InlineData("BGSTerminal", 216)]
    [InlineData("TESModelTextureSwap", 44)]
    [InlineData("TESLoadScreen", 68)]
    [InlineData("TESCreature", 216)]
    [InlineData("TESCreature", 296)]
    [InlineData("BGSBodyPart", 12)]
    public void BuildNiObjectFieldIndex_KeepsOffsetsTheNewerPdbContradicts(string className, int offset)
    {
        var handWritten = OwnershipFieldIndexBuilder.BuildNiObjectFieldIndex();

        Assert.True(handWritten.TryGetValue(className, out var fields),
            $"{className} was removed from the fallback index. The PDB postdates the dumps; "
            + "disagreement with it is not proof the offset is wrong.");
        Assert.Contains(fields!, f => f.Offset == offset);
    }

    /// <summary>
    ///     The two sources are layered, not exclusive: the PDB index is consulted first and the
    ///     hand-written table only fires where it has no matching offset. Overlapping class names
    ///     are therefore expected and correct — asserting disjointness would be asserting that one
    ///     build's layout is the only one that can ever be right.
    /// </summary>
    [Fact]
    public void HandWrittenAndPdbIndices_AreAllowedToDescribeTheSameClass()
    {
        var (_, classIndex, _) = OwnershipFieldIndexBuilder.BuildFieldIndices();
        var handWritten = OwnershipFieldIndexBuilder.BuildNiObjectFieldIndex();

        Assert.Contains(handWritten.Keys, className => classIndex.ContainsKey(className));
    }

    /// <summary>
    ///     The FormType-keyed BSStringT index feeds the TESForm reverse lookup, whose BSStringT
    ///     length validation only makes sense for genuine BSStringT members. Sweeping the auxiliary
    ///     structs must not have leaked non-record classes into it.
    /// </summary>
    [Fact]
    public void BuildFieldIndices_BsStringTIndexStaysRecordOnly()
    {
        var (bsIndex, _, _) = OwnershipFieldIndexBuilder.BuildFieldIndices();

        Assert.NotEmpty(bsIndex);
        Assert.All(bsIndex.Keys, key => Assert.True(
            PdbStructLayouts.Layouts.ContainsKey(key.FormType),
            $"FormType 0x{key.FormType:X2} is not a record layout."));
    }
}
