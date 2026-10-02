using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 3, target binding (<see cref="NifModelTargetNames" />; plan section 1.7, owner rulings D3 and D12):
///     exact, case-sensitive name bytes; a repeated name is 'ambiguous target'; a name the skeleton lacks is 'target not in
///     the resolved skeleton (attachment node)'; a <c>.nif</c> binds through its manager's palette first, then node names,
///     with one track per occurrence. The file-level tests read synthetic NIFs through the real decoder and node reader.
/// </summary>
public sealed class NifModelTargetNamesTests
{
    /// <summary>
    ///     'Bip01 L Hand' does not bind to a skeleton node named 'bip01 l hand' and reports the attachment reason; the exact
    ///     spelling binds. Control: a case-insensitive map of the same names binds it, which is the match D12 rejects.
    /// </summary>
    [Fact]
    public void SkeletonNames_MatchExactBytes_CaseSensitive()
    {
        string[] skeleton = ["Bip01", "bip01 l hand", "Bip01 R Hand"];
        var targets = NifModelTargetNames.Build(NifModelTargetScope.Skeleton, [], Names(skeleton),
            OneOccurrenceEach(skeleton.Length));

        var wrongCase = targets.Match(Latin1("Bip01 L Hand"));
        Assert.False(wrongCase.IsResolved);
        Assert.Equal(NifModelTargetBlock.TargetNotInSkeleton, wrongCase.Block);
        Assert.Equal(-1, wrongCase.TargetBlock);
        Assert.Empty(wrongCase.Occurrences);
        var exact = targets.Match(Latin1("bip01 l hand"));
        Assert.True(exact.IsResolved);
        Assert.Equal(1, exact.TargetBlock);
        Assert.Equal(new[] { 1 }, exact.Occurrences);
        Assert.Equal(NifModelTargetSource.ObjectName, exact.Source);

        var caseInsensitive = skeleton.Select(static (name, block) => (name, block))
            .ToDictionary(static pair => pair.name, static pair => pair.block, StringComparer.OrdinalIgnoreCase);
        Assert.True(caseInsensitive.TryGetValue("Bip01 L Hand", out var wronglyBound));
        Assert.Equal(1, wronglyBound);
    }

    /// <summary>
    ///     A '##' attachment name the skeleton lacks reports the typed attachment reason, with its text and code. Control: a
    ///     skeleton that holds the name binds it, so the reason comes from the name's absence.
    /// </summary>
    [Fact]
    public void MissingTarget_ReportsTheAttachmentReason()
    {
        var targets = NifModelTargetNames.Build(NifModelTargetScope.Skeleton, [], Names("Bip01", "Bip01 R Hand"),
            OneOccurrenceEach(2));

        var missing = targets.Match(Latin1("##NifRound"));

        Assert.Equal(NifModelTargetBlock.TargetNotInSkeleton, missing.Block);
        Assert.Equal(NifModelTargetSource.None, missing.Source);
        Assert.Equal("target not in the resolved skeleton (attachment node)", missing.Reason);
        Assert.Equal("targetNotInSkeleton", NifModelTargetNames.Code(missing.Block));
        Assert.Equal("##NifRound", missing.Name);
        Assert.Empty(missing.CandidateBlocks);

        var withAttachment = NifModelTargetNames.Build(NifModelTargetScope.Skeleton, [],
            Names("Bip01", "Bip01 R Hand", "##NifRound"), OneOccurrenceEach(3));
        Assert.Equal(2, withAttachment.Match(Latin1("##NifRound")).TargetBlock);
    }

    /// <summary>
    ///     A name two skeleton blocks share is 'ambiguous target' with both blocks listed, and neither is bound. Control: one
    ///     block placed twice (instancing) is not ambiguous; it binds with one occurrence per placement.
    /// </summary>
    [Fact]
    public void DuplicatedName_IsAmbiguous_ButInstancingIsNot()
    {
        var targets = NifModelTargetNames.Build(NifModelTargetScope.Skeleton, [],
            Names("Bip01", "Bip01 Spine", "Bip01 Spine"), OneOccurrenceEach(3));

        var ambiguous = targets.Match(Latin1("Bip01 Spine"));

        Assert.Equal(NifModelTargetBlock.AmbiguousTarget, ambiguous.Block);
        Assert.Equal("ambiguous target", ambiguous.Reason);
        Assert.Equal(new[] { 1, 2 }, ambiguous.CandidateBlocks);
        Assert.Equal(-1, ambiguous.TargetBlock);
        Assert.Empty(ambiguous.Occurrences);

        IReadOnlyList<IReadOnlyList<int>> instanced = [new[] { 0 }, new[] { 1, 3 }, new[] { 2 }];
        var once = NifModelTargetNames.Build(NifModelTargetScope.Skeleton, [], Names("Bip01", "Bip01 Spine", "Tail"),
            instanced);
        var bound = once.Match(Latin1("Bip01 Spine"));
        Assert.True(bound.IsResolved);
        Assert.Equal(new[] { 1, 3 }, bound.Occurrences);
    }

    /// <summary>
    ///     A block with no node occurrence, and a palette entry naming no object, bind to nothing ('target names no placed
    ///     node'). A palette that cannot be read blocks every lookup. Controls: the placed block binds, and the same map
    ///     with a readable palette binds.
    /// </summary>
    [Fact]
    public void UnplacedTargets_AndAnUnreadablePalette_AreBlocked()
    {
        IReadOnlyList<IReadOnlyList<int>> occurrences = [new[] { 0 }, Array.Empty<int>()];
        NifModelTargetName[] palette = [new(Latin1("Ghost"), -1)];
        var targets = NifModelTargetNames.Build(NifModelTargetScope.File, palette, Names("Root", "Unplaced"),
            occurrences);

        var ghost = targets.Match(Latin1("Ghost"));
        Assert.Equal(NifModelTargetBlock.TargetNotPlaced, ghost.Block);
        Assert.Equal(NifModelTargetSource.Palette, ghost.Source);
        Assert.Equal(new[] { -1 }, ghost.CandidateBlocks);
        var unplaced = targets.Match(Latin1("Unplaced"));
        Assert.Equal(NifModelTargetBlock.TargetNotPlaced, unplaced.Block);
        Assert.Equal(new[] { 1 }, unplaced.CandidateBlocks);
        Assert.Equal(NifModelTargetBlock.TargetNotInFile, targets.Match(Latin1("Missing")).Block);
        Assert.True(targets.Match(Latin1("Root")).IsResolved);

        var unreadable = NifModelTargetNames.Build(NifModelTargetScope.File, [], Names("Root"), occurrences, true);
        Assert.True(unreadable.PaletteUnreadable);
        Assert.Equal(NifModelTargetBlock.PaletteUnreadable, unreadable.Match(Latin1("Root")).Block);
        Assert.True(NifModelTargetNames.Build(NifModelTargetScope.File, [], Names("Root"), occurrences)
            .Match(Latin1("Root")).IsResolved);
    }

    /// <summary>
    ///     A skeleton read through the real decoder and node reader: exact case, a Latin-1 byte above 0x7F, a repeated name,
    ///     an instanced node, a NULL-named node, and a controlled block's NULL or out-of-range name index. Controls: the
    ///     case-folded name and the ASCII rendering of the 0xE9 name (what NifInfo.Strings holds) do not bind.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkeletonFromARealNif_BindsByExactBytes(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        var root = builder.AddString("Bip01");
        var spine = builder.AddString("Bip01 Spine");
        AddNode(builder, root, [1, 2, 3, 4, 5, 6]);
        AddNode(builder, builder.AddString("bip01 l hand"), []);
        AddNode(builder, builder.AddRawString(Latin1("Bip01 R\u00E9f")), []);
        AddNode(builder, spine, []);
        AddNode(builder, spine, [6]);
        AddNode(builder, -1, []);
        AddNode(builder, builder.AddString("Bip01 Head"), []);
        var (state, graph) = ReadGraph(builder.Build());

        var targets = NifModelTargetNames.ForSkeleton(graph);

        Assert.Equal(NifModelTargetScope.Skeleton, targets.Scope);
        Assert.Equal(0, targets.PaletteNameCount);
        Assert.Equal(5, targets.ObjectNameCount);
        Assert.Equal(0, targets.Match(Latin1("Bip01")).TargetBlock);
        Assert.Equal(1, targets.Match(Latin1("bip01 l hand")).TargetBlock);
        Assert.Equal(NifModelTargetBlock.TargetNotInSkeleton, targets.Match(Latin1("Bip01 L Hand")).Block);
        Assert.Equal(2, targets.Match(Latin1("Bip01 R\u00E9f")).TargetBlock);
        Assert.Contains("Bip01 R?f", state.Info.Strings);
        Assert.Equal(NifModelTargetBlock.TargetNotInSkeleton, targets.Match(Latin1("Bip01 R?f")).Block);
        var repeated = targets.Match(Latin1("Bip01 Spine"));
        Assert.Equal(NifModelTargetBlock.AmbiguousTarget, repeated.Block);
        Assert.Equal(new[] { 3, 4 }, repeated.CandidateBlocks);
        var head = targets.Match(Latin1("Bip01 Head"));
        Assert.True(head.IsResolved);
        Assert.Equal(2, head.Occurrences.Count);
        Assert.Equal(graph.OccurrencesByBlock[6], head.Occurrences);

        var strings = state.Header.Strings;
        Assert.True(targets.Match(root, strings).IsResolved);
        Assert.Equal(NifModelTargetBlock.NoTargetName, targets.Match(-1, strings).Block);
        Assert.Equal(NifModelTargetBlock.UnresolvedTargetName, targets.Match(strings.Count, strings).Block);
    }

    /// <summary>
    ///     A <c>.nif</c> binds through its manager's NiDefaultAVObjectPalette first: the NULL-named node (the X360 case)
    ///     binds only there, the palette's "Own" wins over another node's own name "Own", and two palette entries sharing a
    ///     name are ambiguous; names the palette lacks fall back to node names; the bound block drives one track per
    ///     occurrence. Control: the node names alone (<see cref="NifModelTargetNames.ForSkeleton" /> on the same graph) miss
    ///     the NULL-named node and bind "Own" to the other block, so the palette's precedence is observable.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FileTargets_PaletteFirst_ThenNodeNames_OneTrackPerOccurrence(bool bigEndian)
    {
        var builder = new NifTestFileBuilder(bigEndian, 34);
        var rootName = builder.AddString("Root");
        builder.AddBlock("NiNode", w => NifTestBlockLayouts.NodeWithTail(w, 34, rootName, [3, 4, 5, 6], _ => { },
            controller: 1));
        builder.AddBlock("NiControllerManager", w => NifTestBlockLayouts.ControllerManager(w, 0, 2));
        builder.AddBlock("NiDefaultAVObjectPalette", w => NifTestBlockLayouts.DefaultAvObjectPalette(w, 0,
            [("Bip01 Head", 3), ("Own", 5), ("Dup", 4), ("Dup", 6)]));
        AddNode(builder, -1, []);
        AddNode(builder, builder.AddString("Own"), [3]);
        var twin = builder.AddString("Twin");
        AddNode(builder, twin, []);
        AddNode(builder, twin, []);
        var (state, graph) = ReadGraph(builder.Build());

        var targets = NifModelTargetNames.ForFile(state, 1, graph);

        Assert.Equal(NifModelTargetScope.File, targets.Scope);
        Assert.False(targets.PaletteUnreadable);
        Assert.Equal(3, targets.PaletteNameCount);
        var head = targets.Match(Latin1("Bip01 Head"));
        Assert.True(head.IsResolved);
        Assert.Equal(NifModelTargetSource.Palette, head.Source);
        Assert.Equal(3, head.TargetBlock);
        Assert.Equal(2, head.Occurrences.Count);
        Assert.Equal(graph.OccurrencesByBlock[3], head.Occurrences);
        var own = targets.Match(Latin1("Own"));
        Assert.Equal(NifModelTargetSource.Palette, own.Source);
        Assert.Equal(5, own.TargetBlock);
        var dup = targets.Match(Latin1("Dup"));
        Assert.Equal(NifModelTargetBlock.AmbiguousTarget, dup.Block);
        Assert.Equal(new[] { 4, 6 }, dup.CandidateBlocks);
        var twins = targets.Match(Latin1("Twin"));
        Assert.Equal(NifModelTargetBlock.AmbiguousTarget, twins.Block);
        Assert.Equal(NifModelTargetSource.ObjectName, twins.Source);
        Assert.Equal(new[] { 5, 6 }, twins.CandidateBlocks);
        var rootMatch = targets.Match(Latin1("Root"));
        Assert.Equal(NifModelTargetSource.ObjectName, rootMatch.Source);
        Assert.Equal(0, rootMatch.TargetBlock);
        Assert.Equal(NifModelTargetBlock.TargetNotInFile, targets.Match(Latin1("own")).Block);
        Assert.Equal(NifModelTargetNames.TargetNotInFileReason, targets.Match(Latin1("own")).Reason);
        Assert.Throws<ArgumentException>(() => NifModelTargetNames.ForFile(state, 0, graph));

        var nodeNamesOnly = NifModelTargetNames.ForSkeleton(graph);
        Assert.Equal(4, nodeNamesOnly.Match(Latin1("Own")).TargetBlock);
        Assert.Equal(NifModelTargetBlock.TargetNotInSkeleton, nodeNamesOnly.Match(Latin1("Bip01 Head")).Block);
    }

    /// <summary>
    ///     A controlled block's Node Name index resolves against the raw string table. Control: an index one past the table
    ///     is reported, not read.
    /// </summary>
    [Fact]
    public void ControlledBlockNameIndex_ResolvesThroughTheRawTable()
    {
        var targets = NifModelTargetNames.Build(NifModelTargetScope.Skeleton, [], Names("Bip01 R\u00E9f"),
            OneOccurrenceEach(1));
        var strings = new NifHeaderStringTable(0, 16, [Latin1("Bip01 R\u00E9f")]);

        Assert.True(targets.Match(0, strings).IsResolved);
        Assert.Equal(NifModelTargetBlock.NoTargetName, targets.Match(-1, strings).Block);
        Assert.Equal(NifModelTargetNames.NoTargetNameReason, targets.Match(-1, strings).Reason);

        Assert.Equal(NifModelTargetBlock.UnresolvedTargetName, targets.Match(1, strings).Block);
    }
}
