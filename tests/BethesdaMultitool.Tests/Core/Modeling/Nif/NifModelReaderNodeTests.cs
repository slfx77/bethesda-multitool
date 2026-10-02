using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The reader skeleton's node hierarchy: document identity and provenance, one node per occurrence from the footer
///     roots (instancing), cycles rejected, null children dropped with their ordinals kept, Latin-1 names with raw
///     bytes, the TRS rule applied per node, the root-transform diagnostic and the native hidden flag.
/// </summary>
public class NifModelReaderNodeTests
{
    private static readonly float[] QuarterTurnAboutZ = [0f, -1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f];

    /// <summary>Root "Scene Root" [A, B]; A has no children; B [Leaf].</summary>
    private static byte[] SmallTree(bool bigEndian, uint bs)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        AddNode(builder, builder.AddString("Scene Root"), [1, 2]);
        AddNode(builder, builder.AddString("Child A"), []);
        AddNode(builder, builder.AddString("Child B"), [3]);
        AddNode(builder, builder.AddString("Leaf"), []);
        return builder.Build();
    }

    [Theory]
    [InlineData(false, 14u)]
    [InlineData(false, 21u)]
    [InlineData(false, 26u)]
    [InlineData(false, 32u)]
    [InlineData(false, 34u)]
    [InlineData(true, 14u)]
    [InlineData(true, 21u)]
    [InlineData(true, 26u)]
    [InlineData(true, 32u)]
    [InlineData(true, 34u)]
    public void Read_BuildsTheHierarchyFromTheFooterRoots(bool bigEndian, uint bs)
    {
        var bytes = SmallTree(bigEndian, bs);

        var document = Read(bytes).Document;

        Assert.Equal("bmt.nif", document.SourceFormat);
        Assert.Equal("model", document.Name);
        Assert.Equal(new AssetReference(SourceId, DefaultPath).ToString(), document.SourceIdentity);
        var provenance = Assert.IsType<SceneSourceProvenance>(document.SourceProvenance);
        Assert.Equal(DefaultPath, provenance.RelativePath);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), provenance.Sha256);

        var scene = Assert.Single(document.Scenes);
        Assert.Equal([0], scene.RootNodeIndices);
        Assert.Equal(["Scene Root", "Child A", "Child B", "Leaf"], document.Nodes.Select(n => n.Name));
        Assert.Equal([1, 2], document.Nodes[0].Children);
        Assert.Empty(document.Nodes[1].Children);
        Assert.Equal([3], document.Nodes[2].Children);
        Assert.All(document.Nodes, node => Assert.Equal(SceneNodeRole.Transform, node.Role));
        Assert.All(document.Nodes, node => Assert.Equal(Matrix4x4.Identity, node.LocalTransform));
        Assert.All(document.Nodes, node => Assert.NotNull(node.LocalTrs));
        Assert.Empty(document.Meshes);

        SceneValidation.ValidateStructure(document);
    }

    /// <summary>
    ///     A block reached through two parent edges becomes two nodes. Control: a document that instead points both
    ///     parents at one node index fails Shared's structure validation, so one node per occurrence is required.
    /// </summary>
    [Fact]
    public void Read_BlockWithTwoParents_BecomesOneNodePerOccurrence()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1, 2]);
        AddNode(builder, builder.AddString("A"), [3]);
        AddNode(builder, builder.AddString("B"), [3]);
        AddNode(builder, builder.AddString("Shared"), [], (1f, 2f, 3f));

        var document = Read(builder.Build()).Document;

        Assert.Equal(["Root", "A", "Shared", "B", "Shared"], document.Nodes.Select(n => n.Name));
        Assert.Equal([1, 3], document.Nodes[0].Children);
        Assert.Equal([2], document.Nodes[1].Children);
        Assert.Equal([4], document.Nodes[3].Children);
        Assert.Equal(document.Nodes[2].LocalTransform, document.Nodes[4].LocalTransform);
        Assert.Equal(new Vector3(1f, 2f, 3f), document.Nodes[4].LocalTransform.Translation);
        SceneValidation.ValidateStructure(document);

        var occurrences = BlockPayload(document, 3)["node"]!["occurrences"]!.AsArray().Select(v => (int)v!);
        Assert.Equal([2, 4], occurrences);

        var aliased = new ModelDocument("test", "aliased", [new SceneDefinition("s", [0])],
        [
            new SceneNode("Root", Matrix4x4.Identity, [1, 2]),
            new SceneNode("A", Matrix4x4.Identity, [3]),
            new SceneNode("B", Matrix4x4.Identity, [3]),
            new SceneNode("Shared", Matrix4x4.Identity)
        ], []);
        Assert.Throws<InvalidDataException>(() => SceneValidation.ValidateStructure(aliased));
    }

    public static TheoryData<int[][]> Cycles => new()
    {
        new[] { new[] { 1 }, new[] { 0 } },
        new[] { new[] { 0 } },
        new[] { new[] { 1 }, new[] { 2 }, new[] { 1 } }
    };

    [Theory]
    [MemberData(nameof(Cycles))]
    public void Read_CycleThroughChildren_Throws(int[][] childrenByBlock)
    {
        var builder = new NifTestFileBuilder(false, 34);
        foreach (var children in childrenByBlock)
        {
            AddNode(builder, -1, children);
        }

        var error = Assert.Throws<InvalidDataException>(() => Read(builder.Build()));
        Assert.Contains("cycle", error.Message);

        // Control: the same chain without its back edge reads.
        var acyclic = new NifTestFileBuilder(false, 34);
        for (var i = 0; i < childrenByBlock.Length; i++)
        {
            AddNode(acyclic, -1, i + 1 < childrenByBlock.Length ? [i + 1] : []);
        }

        Assert.Equal(childrenByBlock.Length, Read(acyclic.Build()).Document.Nodes.Count);
    }

    [Fact]
    public void Read_NullChildren_AreDropped_AndTheirOrdinalsKept()
    {
        var builder = new NifTestFileBuilder(true, 34);
        AddNode(builder, builder.AddString("Root"), [-1, 1, -1, 2]);
        AddNode(builder, builder.AddString("First"), []);
        AddNode(builder, builder.AddString("Second"), []);

        var document = Read(builder.Build()).Document;

        Assert.Equal([1, 2], document.Nodes[0].Children);
        Assert.Equal(["Root", "First", "Second"], document.Nodes.Select(n => n.Name));
        var node = BlockPayload(document, 0)["node"]!;
        Assert.Equal([0, 2], node["nullChildOrdinals"]!.AsArray().Select(v => (int)v!));
        Assert.Equal(4, (int)node["childSlots"]!);
        Assert.Empty(BlockPayload(document, 1)["node"]!["nullChildOrdinals"]!.AsArray());
    }

    /// <summary>
    ///     Names are the Latin-1 text of the raw string-table bytes and the raw bytes are kept when any byte is at or
    ///     above 0x80. Control: NifParser's ASCII reading would give "Caf?", and an ASCII name carries no raw bytes.
    /// </summary>
    [Fact]
    public void Read_NameWithHighBytes_IsLatin1_AndKeepsTheRawBytes()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddRawString([0x43, 0x61, 0x66, 0xE9]), [1]);
        AddNode(builder, builder.AddString("Plain"), []);

        var document = Read(builder.Build()).Document;

        Assert.Equal("Caf\u00e9", document.Nodes[0].Name);
        Assert.NotEqual("Caf?", document.Nodes[0].Name);
        Assert.Equal("436166e9", (string)BlockPayload(document, 0)["node"]!["nameRawHex"]!);
        Assert.Equal("stringTable", (string)BlockPayload(document, 0)["node"]!["nameSource"]!);
        Assert.Null(BlockPayload(document, 1)["node"]!["nameRawHex"]);
    }

    [Fact]
    public void Read_NullName_IsEmpty_WithNameSourceNone()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, -1, []);

        var document = Read(builder.Build()).Document;

        Assert.Equal("", document.Nodes[0].Name);
        Assert.Equal("none", (string)BlockPayload(document, 0)["node"]!["nameSource"]!);
    }

    /// <summary>
    ///     The TRS rule per node, end to end: a scaled quarter turn is TRS and maps +X to +Y (the transpose; the
    ///     untransposed reading would give -Y), a shear is a matrix node with exact element copies, and both analyses
    ///     are recorded in native state.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_AppliesTheTrsRulePerNode(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        AddNode(builder, builder.AddString("Root"), [1, 2]);
        AddNode(builder, builder.AddString("Turned"), [], (1f, 2f, 3f), QuarterTurnAboutZ, 2f);
        AddNode(builder, builder.AddString("Sheared"), [], rotation: [1f, 0.5f, 0f, 0f, 1f, 0f, 0f, 0f, 1f]);

        var document = Read(builder.Build()).Document;

        var turned = document.Nodes[1];
        var trs = Assert.NotNull(turned.LocalTrs);
        Assert.Equal(new Vector3(2f, 2f, 2f), trs.Scale);
        var mapped = Vector3.Transform(Vector3.UnitX, turned.LocalTransform);
        Assert.Equal(1f, mapped.X, 1e-5f);
        Assert.Equal(4f, mapped.Y, 1e-5f);
        Assert.Equal(3f, mapped.Z, 1e-5f);

        var sheared = document.Nodes[2];
        Assert.Null(sheared.LocalTrs);
        Assert.Equal(0.5f, sheared.LocalTransform.M21);
        Assert.Equal(0f, sheared.LocalTransform.M12);

        Assert.Equal("Trs", (string)BlockPayload(document, 1)["node"]!["transform"]!["kind"]!);
        var shearAnalysis = BlockPayload(document, 2)["node"]!["transform"]!;
        Assert.Equal("Matrix", (string)shearAnalysis["kind"]!);
        Assert.Equal(0.5, (double)shearAnalysis["orthonormalityError"]!);
        Assert.Equal(1.0, (double)shearAnalysis["determinant"]!);
        Assert.Null(shearAnalysis["quaternionReconstructionError"]);
        SceneValidation.ValidateStructure(document);
    }

    [Fact]
    public void Read_NonIdentityRoot_ReportsADiagnostic()
    {
        var moved = new NifTestFileBuilder(false, 34);
        AddNode(moved, -1, [], (0f, 0f, 5f));
        var still = new NifTestFileBuilder(false, 34);
        AddNode(still, -1, []);

        var movedDocument = Read(moved.Build()).Document;
        var stillDocument = Read(still.Build()).Document;

        Assert.Contains(movedDocument.Diagnostics, d => d.Code == NifModelNodeReader.RootTransformDiagnostic);
        Assert.DoesNotContain(stillDocument.Diagnostics, d => d.Code == NifModelNodeReader.RootTransformDiagnostic);
        Assert.Equal(new Vector3(0f, 0f, 5f), movedDocument.Nodes[0].LocalTransform.Translation);
    }

    /// <summary>
    ///     The hidden flag is kept in native state with every other flag bit, and (slice 8) becomes a default-off layer
    ///     set; <see cref="NifModelHiddenLayerTests" /> covers the set. Control: a visible node is neither flagged nor in
    ///     any set.
    /// </summary>
    [Fact]
    public void Read_HiddenFlag_IsKeptNatively_AndBecomesADefaultOffLayerSet()
    {
        var hidden = new NifTestFileBuilder(false, 34);
        AddNode(hidden, -1, [1]);
        AddNode(hidden, -1, [], flags: 0x0F);
        var shown = new NifTestFileBuilder(false, 34);
        AddNode(shown, -1, [1]);
        AddNode(shown, -1, []);

        var hiddenDocument = Read(hidden.Build()).Document;
        var shownDocument = Read(shown.Build()).Document;

        Assert.True((bool)BlockPayload(hiddenDocument, 1)["node"]!["hidden"]!);
        Assert.Equal(0x0Fu, (uint)BlockPayload(hiddenDocument, 1)["node"]!["flags"]!);
        var set = Assert.Single(hiddenDocument.LayerSets);
        Assert.False(set.DefaultOn);
        Assert.Equal([1], set.Members);
        Assert.False((bool)BlockPayload(shownDocument, 1)["node"]!["hidden"]!);
        Assert.Empty(shownDocument.LayerSets);
    }

    /// <summary>
    ///     Cut-1b slice 10: a .kf root is read as an animation stream, which needs a skeleton; with none in the read's
    ///     source (and no bmt.skeleton) it is not supported with D4's reason, never the retired 'later-cut(1b)' one.
    /// </summary>
    [Fact]
    public void Read_KfRootWithoutASkeleton_IsNotSupported()
    {
        var builder = new NifTestFileBuilder(false, 34);
        builder.AddBlock("NiControllerSequence", w => w.U32(0));

        var error = Assert.Throws<NotSupportedException>(() => Read(builder.Build()));

        Assert.StartsWith(NifModelSkeletonResolver.NoSkeletonReason, error.Message);
        Assert.DoesNotContain("later-cut(1b)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RootThatIsNotAnAvObject_IsInvalid()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, -1, []);
        AddAlphaProperty(builder);

        var error = Assert.Throws<InvalidDataException>(() => Read(builder.WithRoot(1).Build()));
        Assert.Contains("not an NiAVObject", error.Message);

        // Control: the same blocks with the node as the root read.
        var control = new NifTestFileBuilder(false, 34);
        AddNode(control, -1, []);
        AddAlphaProperty(control);
        Assert.Single(Read(control.WithRoot(0).Build()).Document.Nodes);
    }

    [Theory]
    [InlineData(83u, "other version:")]
    [InlineData(24u, NifModelProbe.AnimationStreamKeyCategory)]
    public void Read_KeyOutsideCutOneA_IsNotSupported(uint bs, string reasonPrefix)
    {
        var builder = new NifTestFileBuilder(false, bs);
        AddNode(builder, -1, []);

        var error = Assert.Throws<NotSupportedException>(() => Read(builder.Build()));

        Assert.StartsWith(reasonPrefix, error.Message);
    }

    [Fact]
    public void Read_TrailingByteAfterTheFooter_IsCorrupt()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, -1, []);

        Assert.Throws<InvalidDataException>(() => Read(builder.WithTrailingBytes(0).Build()));

        var control = new NifTestFileBuilder(false, 34);
        AddNode(control, -1, []);
        Assert.Single(Read(control.Build()).Document.Nodes);
    }

    [Fact]
    public void Read_RequiresTheContextsOccurrence_AndAFreshNifCache()
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, -1, []);
        var bytes = builder.Build();
        var reader = new NifModelReader();
        var (item, input) = Open(bytes);
        var (otherItem, otherInput) = Open(bytes, "meshes/test/other.nif");
        using var ownedInput = input;
        using var ownedOther = otherInput;

        var mismatched = new ModelReadContext(otherItem, otherInput, new NifModelReadCache());
        Assert.Throws<ArgumentException>(() => reader.Read(item, mismatched, CancellationToken.None));

        var foreignCache = new ModelReadContext(item, new MemoryStream(bytes, false), new ForeignCache());
        Assert.Throws<ArgumentException>(() => reader.Read(item, foreignCache, CancellationToken.None));

        var reused = new NifModelReadCache();
        reader.Read(otherItem, new ModelReadContext(otherItem, new MemoryStream(bytes, false), reused),
            CancellationToken.None);
        Assert.Throws<ArgumentException>(() =>
            reader.Read(item, new ModelReadContext(item, new MemoryStream(bytes, false), reused), CancellationToken.None));

        // Control: the matching occurrence with a fresh cache reads.
        var result = reader.Read(item, new ModelReadContext(item, input, new NifModelReadCache()), CancellationToken.None);
        Assert.Equal(item.Reference, result.Coverage.Source);
    }

    /// <summary>A cache scope that is not the NIF reader's.</summary>
    private sealed class ForeignCache : IModelReadCacheScope
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
