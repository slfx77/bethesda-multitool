using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTargetTestSupport;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 5, text keys to events (<see cref="NifModelTextKeyEvents" />; plan section 1.5): every stored key
///     becomes one Shared event in file order, its text the Latin-1 decoding of the stored bytes, with no sort, trim,
///     dedupe or drop, and the stored bytes returned beside it. The keys are written as bytes and read back through the
///     slice-1 view reader, so every stored word is found where the file puts it. BSAnimNotes stay NativeOnly.
/// </summary>
public sealed class NifModelTextKeyEventsTests
{
    /// <summary>The indexed labels of <see cref="IndexedFixture" />, string-table entries 0 to 3.</summary>
    private static readonly string[] IndexedLabels = ["Sound: FootLeft\r\n", "Hit\r\nSound: Swing\r\n", "", "End"];

    /// <summary>The event texts <see cref="IndexedFixture" /> must produce, in file order.</summary>
    private static readonly string[] ExpectedTexts =
        ["Sound: FootLeft\r\n", "Hit\r\nSound: Swing\r\n", "", "", "End"];

    /// <summary>
    ///     A CR LF label, a label holding two events, an empty label and a NULL label are kept byte for byte in file order,
    ///     an equal-time pair and a time lower than its predecessor included. Controls: trimming, dropping empty events,
    ///     sorting by time and sorting the equal-time pair by text each give a different result, and so does the
    ///     renderer's reader (ASCII, a line split, a trim, dropped empties, a sort).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Events_KeepCrlfTwoEventAndEmptyLabels_ByteExact_InFileOrder(bool bigEndian)
    {
        var (data, file, block) = IndexedFixture(bigEndian);
        Assert.True(NifTextKeyReader.TryReadView(data, file.Nif, block, out var view));

        var result = NifModelTextKeyEvents.Map(view, Table(IndexedLabels));

        Assert.False(result.IsBlocked);
        Assert.False(result.InlineStrings);
        var texts = result.Events.Select(static e => e.Text).ToArray();
        Assert.Equal(ExpectedTexts, texts);
        Assert.Equal(new[] { Bits(1f), Bits(1f), Bits(0.25f), Bits(2f), Bits(2f) },
            result.Events.Select(static e => Bits(e.TimeSeconds)));
        Assert.Equal(new[] { 0, 1, 2, -1, 3 }, result.Keys.Select(static k => k.LabelIndex));
        Assert.Equal(new[] { false, false, false, true, false }, result.Keys.Select(static k => k.IsNullLabel));
        Assert.Equal(result.Events.Count, result.Keys.Count);
        for (var i = 0; i < ExpectedTexts.Length; i++)
        {
            Assert.Equal(Latin1(ExpectedTexts[i]), result.Keys[i].RawLabel.ToArray());
            Assert.Equal(Bits(result.Events[i].TimeSeconds), result.Keys[i].TimeBits);
        }

        Assert.NotEqual(texts, texts.Select(static t => t.Trim()).ToArray());
        Assert.NotEqual(texts.Length, texts.Count(static t => t.Length > 0));
        Assert.NotEqual(texts, result.Events.OrderBy(static e => e.TimeSeconds).Select(static e => e.Text).ToArray());
        var equalTimePair = texts[..2];
        Assert.NotEqual(equalTimePair, equalTimePair.Order(StringComparer.Ordinal).ToArray());
        var renderer = NifTextKeyReader.Read(data, file.Nif, block).Select(static k => k.Label).ToArray();
        Assert.NotEqual(texts, renderer);
    }

    /// <summary>
    ///     An inline label byte above 0x7F decodes to the one Latin-1 character and encodes back to the stored byte, and an
    ///     empty inline label is kept. Control: the renderer's ASCII decoding turns the byte into '?' and does not round-trip.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Latin1ByteAbove0x7F_RoundTrips(bool bigEndian)
    {
        byte[] stored = [0x45, 0x6E, 0xE9, 0x0D, 0x0A];
        var file = new NifAnimationTestFile(bigEndian, NifVersions.Gamebryo20004, 11);
        var block = file.AddBlock("NiTextKeyExtraData", writer => writer
            .U32(0)
            .U32(2)
            .F32(0f).U32(0)
            .F32(0.5f).U32((uint)stored.Length).Raw(stored));
        var data = file.ToArray();
        Assert.True(NifTextKeyReader.TryReadView(data, file.Nif, block, out var view));

        var result = NifModelTextKeyEvents.Map(view, Table());

        Assert.True(result.InlineStrings);
        Assert.Equal(new[] { "", "En\u00E9\r\n" }, result.Events.Select(static e => e.Text));
        Assert.Equal(stored, Encoding.Latin1.GetBytes(result.Events[1].Text));
        Assert.Equal(stored, result.Keys[1].RawLabel.ToArray());
        Assert.Equal(0, result.Keys[0].RawLabel.Length);
        Assert.False(result.Keys[0].IsNullLabel);

        var ascii = Encoding.ASCII.GetString(stored);
        Assert.Equal("En?\r\n", ascii);
        Assert.NotEqual(stored, Encoding.Latin1.GetBytes(ascii));
    }

    /// <summary>
    ///     The events travel on a Shared clip that passes Shared's structural validation, unchanged and in order. Controls: a
    ///     non-finite time cannot become a Shared event, which is why such a key keeps its block native; and the same clip
    ///     without its events is refused, so the events are what carry it.
    /// </summary>
    [Fact]
    public void Events_AttachToASharedClipUnchanged()
    {
        var (data, file, block) = IndexedFixture(false);
        Assert.True(NifTextKeyReader.TryReadView(data, file.Nif, block, out var view));
        var result = NifModelTextKeyEvents.Map(view, Table(IndexedLabels));
        var baseDocument = Document(IdentityRest,
            new SceneTransformTrack(0, SceneTransformProperty.Translation, [0f], [1f, 2f, 3f]));
        var clip = new SceneAnimation("clip", [], events: result.Events);
        var document = new ModelDocument(baseDocument.SourceFormat, baseDocument.Name, baseDocument.Scenes,
            baseDocument.Nodes, baseDocument.Meshes, animations: [clip]);

        SceneValidation.ValidateStructure(document, TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedTexts, document.Animations[0].Events.Select(static e => e.Text));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneAnimationEvent(float.NaN, "Start"));
        var empty = new ModelDocument(baseDocument.SourceFormat, baseDocument.Name, baseDocument.Scenes,
            baseDocument.Nodes, baseDocument.Meshes, animations: [new SceneAnimation("clip", [])]);
        Assert.Throws<InvalidDataException>(() =>
            SceneValidation.ValidateStructure(empty, TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     A label index outside the table, a non-finite time, and keys that do not end where the block ends each keep the
    ///     whole block native with a typed reason and no partial event list. Controls: the same keys with a large enough
    ///     table, a finite time, and no trailing byte map.
    /// </summary>
    [Fact]
    public void UnmappableKeys_KeepTheWholeBlockNative()
    {
        var outOfRange = Read(static writer => writer.I32(-1).U32(2).F32(0f).I32(0).F32(1f).I32(7));
        var blocked = NifModelTextKeyEvents.Map(outOfRange, Table("Start"));
        Assert.Equal(NifModelTextKeyBlock.LabelOutOfRange, blocked.Block);
        Assert.Equal(1, blocked.BlockedKeyIndex);
        Assert.Empty(blocked.Events);
        Assert.Empty(blocked.Keys);
        Assert.Equal(2, NifModelTextKeyEvents.Map(outOfRange, Table("Start", "", "", "", "", "", "", "Stop")).Events.Count);

        var nan = Read(static writer => writer.I32(-1).U32(1).U32(0x7FC00000).I32(0));
        var nanResult = NifModelTextKeyEvents.Map(nan, Table("Start"));
        Assert.Equal(NifModelTextKeyBlock.NonFiniteTime, nanResult.Block);
        Assert.Equal(0, nanResult.BlockedKeyIndex);
        var finite = Read(static writer => writer.I32(-1).U32(1).U32(0xBF800000).I32(0));
        Assert.Equal(-1f, Assert.Single(NifModelTextKeyEvents.Map(finite, Table("Start")).Events).TimeSeconds);

        var padded = Read(static writer => writer.I32(-1).U32(1).F32(0f).I32(0).U8(0));
        Assert.False(padded.ConsumedExactly);
        var paddedResult = NifModelTextKeyEvents.Map(padded, Table("Start"));
        Assert.Equal(NifModelTextKeyBlock.NotConsumedExactly, paddedResult.Block);
        Assert.Null(paddedResult.BlockedKeyIndex);
        var exact = Read(static writer => writer.I32(-1).U32(1).F32(0f).I32(0));
        Assert.False(NifModelTextKeyEvents.Map(exact, Table("Start")).IsBlocked);
    }

    /// <summary>
    ///     A sequence's BSAnimNotes stay NativeOnly with the anim-notes reason, in the single-ref (BS 24 to 28) and the array
    ///     (BS above 28) forms, refs kept as stored. Control: a NULL ref is kept as stored but marks no block, and a stream
    ///     without the field has no anim notes at all.
    /// </summary>
    [Fact]
    public void AnimNotes_StayNativeOnly()
    {
        var single = NifModelTextKeyEvents.AnimNotes(Sequence(11, null));
        Assert.False(single.IsArrayForm);
        Assert.Equal(new[] { 11 }, single.StoredRefs);
        Assert.True(single.HasNotes);
        var disposition = Assert.Single(single.Dispositions);
        Assert.Equal(11, disposition.Key);
        Assert.Equal(ModelSourceCoverageKind.NativeOnly, disposition.Value.Kind);
        Assert.Equal("anim notes: no Shared vocabulary", disposition.Value.Reason);

        var array = NifModelTextKeyEvents.AnimNotes(Sequence(null, [12, -1, 13]));
        Assert.True(array.IsArrayForm);
        Assert.Equal(new[] { 12, -1, 13 }, array.StoredRefs);
        Assert.Equal(new[] { 12, 13 }, array.Dispositions.Keys.Order());

        var nullRef = NifModelTextKeyEvents.AnimNotes(Sequence(-1, null));
        Assert.Equal(new[] { -1 }, nullRef.StoredRefs);
        Assert.False(nullRef.HasNotes);
        Assert.Same(NifModelAnimNotes.None, NifModelTextKeyEvents.AnimNotes(Sequence(null, null)));
    }

    /// <summary>
    ///     A 20.2.0.7 NiTextKeyExtraData with five indexed keys: (1.0, 0) CR LF, (1.0, 1) two events, (0.25, 2) empty,
    ///     (2.0, -1) NULL, (2.0, 3) "End". The renderer's ASCII table (NifInfo.Strings) is filled too, for its control.
    /// </summary>
    private static (byte[] Data, NifAnimationTestFile File, BlockInfo Block) IndexedFixture(bool bigEndian)
    {
        var file = new NifAnimationTestFile(bigEndian);
        var block = file.AddBlock("NiTextKeyExtraData", static writer => writer
            .I32(-1).U32(5)
            .F32(1f).I32(0)
            .F32(1f).I32(1)
            .F32(0.25f).I32(2)
            .F32(2f).I32(-1)
            .F32(2f).I32(3));
        file.Nif.Strings.AddRange(IndexedLabels);
        return (file.ToArray(), file, block);
    }

    /// <summary>Reads one 20.2.0.7 little-endian NiTextKeyExtraData body through the slice-1 view reader.</summary>
    private static NifTextKeyExtraDataView Read(Action<NifAnimationByteWriter> write)
    {
        var file = new NifAnimationTestFile(false);
        var block = file.AddBlock("NiTextKeyExtraData", write);
        Assert.True(NifTextKeyReader.TryReadView(file.ToArray(), file.Nif, block, out var view));
        return view;
    }

    /// <summary>A raw header string table whose entries are the Latin-1 bytes of the given texts.</summary>
    private static NifHeaderStringTable Table(params string[] texts)
    {
        return new NifHeaderStringTable(0, 64, texts.Select(Latin1).ToArray());
    }

    /// <summary>A sequence view with the given anim-note fields and nothing else of interest.</summary>
    private static NifControllerSequenceView Sequence(int? animNotesRef, int[]? animNoteArrayRefs)
    {
        return new NifControllerSequenceView(0, 0, [], Bits(1f), -1, 2, Bits(1f), Bits(0f), Bits(1f), -1, -1,
            animNotesRef, animNoteArrayRefs, true);
    }
}
