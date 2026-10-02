using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The slice-1 lossless views of one file's animation blocks, read from a <see cref="NifModelReadState" /> exactly as
///     the renderer's view readers read them (<see cref="NifControllerSequenceNameTrackReader" />,
///     <see cref="NifKeyframeDataTrackReader" />, <see cref="NifBsplineTransformReader" />, <see cref="NifTextKeyReader" />,
///     <see cref="NifTimeControllerReader" />, since slice 7 <see cref="NifGeomMorpherReader" /> and
///     <see cref="NifKeyGroupReader.TryReadDataBlockView" />, and since slice 14 <see cref="NifPropertyInterpolatorReader" />,
///     <see cref="NifPropertyControllerReader" /> and <see cref="NifUvDataReader.TryReadView" />), plus the few reference
///     fields the model reader takes from the decoded blocks. Every method checks the block type first and refuses rather
///     than guessing.
/// </summary>
/// <remarks>
///     Cut 2, the 20.0.0.4 <c>.kf</c> key (<see cref="NifModelProbe.IsLegacyKfKey" />): the file stores no header string
///     table and its sequences are the Oblivion layout, so <see cref="Strings" /> is the table
///     <see cref="NifModelLegacyKfStrings" /> synthesizes from the inline names and the String Palette entries, and
///     <see cref="TryReadSequence" /> returns the Oblivion view mapped into the 20.2.0.7 shape with synthesized indices;
///     every other method is unchanged, the track data being layout-identical. <see cref="TryReadLegacySequence" /> keeps
///     the Oblivion view reachable for the extras and the palette decisions. For any other file nothing here changes.
/// </remarks>
internal sealed class NifModelAnimationSource
{
    /// <summary>The ancestor type of every controller block (nif.xml NiTimeController).</summary>
    public const string TimeControllerType = "NiTimeController";

    /// <summary>The bytes an NiSingleInterpController stores: the 26-byte NiTimeController header, then Interpolator.</summary>
    public const int SingleInterpControllerSize = NifTimeControllerHeader.HeaderSize + sizeof(int);

    private const string TransformInterpolatorType = "NiTransformInterpolator";
    private const string RotationAccumulationType = "BSRotAccumTransfInterpolator";
    private const string BsplineTransformType = "NiBSplineTransformInterpolator";
    private const string BsplineCompTransformType = "NiBSplineCompTransformInterpolator";
    private const string BsplineFloatType = "NiBSplineFloatInterpolator";
    private const string BsplineCompFloatType = "NiBSplineCompFloatInterpolator";
    private const string BsplineDataType = "NiBSplineData";
    private const string BsplineBasisType = "NiBSplineBasisData";
    private const string FloatInterpolatorType = "NiFloatInterpolator";
    private const string FloatDataType = "NiFloatData";
    private const string MorpherControllerType = "NiGeomMorpherController";
    private const string MorphDataType = "NiMorphData";
    private const string BsplinePoint3Type = "NiBSplinePoint3Interpolator";
    private const string BsplineCompPoint3Type = "NiBSplineCompPoint3Interpolator";
    private const string PosDataType = "NiPosData";
    private const string BoolDataType = "NiBoolData";
    private const string UvDataType = "NiUVData";

    private readonly byte[] _bytes;
    private readonly NifModelLegacyKfStrings? _legacy;

    /// <summary>Wraps a read state.</summary>
    /// <param name="state">The read state (the file bytes, NifParser's block table and the decoded blocks).</param>
    public NifModelAnimationSource(NifModelReadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        _bytes = MemoryMarshal.TryGetArray(state.File, out var segment) && segment.Array is { } array &&
                 segment.Offset == 0 && segment.Count == array.Length
            ? array
            : state.File.ToArray();
        _legacy = NifModelLegacyKfStrings.TryBuild(_bytes, state.Info, state.Header.Strings.Offset);
    }

    /// <summary>The read state.</summary>
    public NifModelReadState State { get; }

    /// <summary>
    ///     The file's raw header string table; for a 20.0.0.4 <c>.kf</c> the table synthesized from its inline names and
    ///     palette entries (<see cref="NifModelLegacyKfStrings" />), which every string index this source hands out indexes.
    /// </summary>
    public NifHeaderStringTable Strings => _legacy?.Table ?? State.Header.Strings;

    /// <summary>True for the 20.0.0.4 <c>.kf</c> key (cut 2), whose sequences read through the Oblivion view.</summary>
    public bool IsLegacyKf => _legacy is not null;

    /// <summary>The number of blocks.</summary>
    public int BlockCount => State.Blocks.Count;

    /// <summary>True when <paramref name="reference" /> names a block of the file.</summary>
    /// <param name="reference">A stored ref.</param>
    /// <returns>True for 0 to <see cref="BlockCount" /> - 1.</returns>
    public bool IsBlock(int reference)
    {
        return (uint)reference < (uint)State.Blocks.Count;
    }

    /// <summary>The type name of a block.</summary>
    /// <param name="block">A block index of the file.</param>
    /// <returns>The header's type name.</returns>
    public string TypeOf(int block)
    {
        return State.Blocks[block].Type;
    }

    /// <summary>True when the reference names a block of exactly this type.</summary>
    /// <param name="reference">A stored ref.</param>
    /// <param name="type">The exact type name.</param>
    /// <returns>True for a block of that type.</returns>
    public bool Is(int reference, string type)
    {
        return IsBlock(reference) && string.Equals(State.Blocks[reference].Type, type, StringComparison.Ordinal);
    }

    /// <summary>True when the reference names a block of this type or a subtype (nif.xml inheritance).</summary>
    /// <param name="reference">A stored ref.</param>
    /// <param name="ancestor">The ancestor type name.</param>
    /// <returns>True for a block inheriting the type.</returns>
    public bool Inherits(int reference, string ancestor)
    {
        return IsBlock(reference) && State.Schema.Inherits(State.Blocks[reference].Type, ancestor);
    }

    /// <summary>The absolute offset one past a block's last byte.</summary>
    /// <param name="block">A block index of the file.</param>
    /// <returns>The block's data offset plus its size.</returns>
    public int BlockEnd(int block)
    {
        var info = State.Info.Blocks[block];
        return info.DataOffset + info.Size;
    }

    /// <summary>
    ///     Reads an NiControllerSequence through the slice-1 view reader; for a 20.0.0.4 <c>.kf</c> the Oblivion view
    ///     mapped into this shape, its string fields indexing <see cref="Strings" /> (see the type remarks).
    /// </summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not a readable sequence.</returns>
    public bool TryReadSequence(int block, [NotNullWhen(true)] out NifControllerSequenceView? view)
    {
        view = null;
        if (_legacy is not null)
        {
            view = IsBlock(block) ? _legacy.Mapped(block) : null;
            return view is not null;
        }

        return IsBlock(block) &&
               NifControllerSequenceNameTrackReader.TryReadSequenceView(_bytes, State.Info, State.Info.Blocks[block],
                   out view);
    }

    /// <summary>
    ///     The Oblivion view of a 20.0.0.4 <c>.kf</c> sequence (cut 2): the inline names, each controlled block's String
    ///     Palette ref and five stored offsets beside their resolved text, and the sequence's own palette ref.
    /// </summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False for any other file, or when the block is not a readable sequence.</returns>
    public bool TryReadLegacySequence(int block, [NotNullWhen(true)] out NifOblivionControllerSequenceView? view)
    {
        view = _legacy is not null && IsBlock(block) ? _legacy.Legacy(block) : null;
        return view is not null;
    }

    /// <summary>Reads the NiTimeController header of any controller block.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="header">The header, lossless.</param>
    /// <returns>False when the block is not an NiTimeController or is too short for the header.</returns>
    public bool TryReadControllerHeader(int block, out NifTimeControllerHeader header)
    {
        header = default;
        return Inherits(block, TimeControllerType) &&
               NifTimeControllerReader.TryRead(_bytes, State.Info.Blocks[block], State.Info.IsBigEndian, out header);
    }

    /// <summary>
    ///     Reads an NiSingleInterpController's Interpolator ref (nif.xml, since 10.1.0.104: it follows the 26-byte
    ///     NiTimeController header; NiInterpController's Manager Controlled bool is stored only up to 10.1.0.108).
    /// </summary>
    /// <param name="block">The controller block index.</param>
    /// <param name="interpolator">The ref as stored.</param>
    /// <returns>False when the block is too short to hold the field.</returns>
    public bool TryReadInterpolatorRef(int block, out int interpolator)
    {
        interpolator = -1;
        if (!IsBlock(block))
        {
            return false;
        }

        var info = State.Info.Blocks[block];
        if (info.Size < SingleInterpControllerSize || (long)info.DataOffset + SingleInterpControllerSize > _bytes.Length)
        {
            return false;
        }

        interpolator = BinaryUtils.ReadInt32(_bytes, info.DataOffset + NifTimeControllerHeader.HeaderSize,
            State.Info.IsBigEndian);
        return true;
    }

    /// <summary>Reads an NiTransformInterpolator (or BSRotAccumTransfInterpolator) through the slice-1 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block does not read.</returns>
    public bool TryReadTransformInterpolator(int block, out NifTransformInterpolatorView view)
    {
        view = default;
        return (Is(block, TransformInterpolatorType) || Is(block, RotationAccumulationType)) &&
               NifControllerSequenceNameTrackReader.TryReadTransformInterpolatorView(_bytes, State.Info,
                   State.Info.Blocks[block], out view);
    }

    /// <summary>Reads an NiTransformData or NiKeyframeData block through the slice-1 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not keyframe data or does not read.</returns>
    public bool TryReadKeyframeData(int block, out NifKeyframeDataView view)
    {
        view = default;
        return IsBlock(block) && NifKeyframeDataTrackReader.IsTrackDataBlock(TypeOf(block)) &&
               NifKeyframeDataTrackReader.TryReadView(_bytes, State.Info, State.Info.Blocks[block], out view);
    }

    /// <summary>True when the block is one of the two transform B-spline interpolators.</summary>
    /// <param name="block">A stored ref.</param>
    /// <returns>True for NiBSplineTransformInterpolator and NiBSplineCompTransformInterpolator.</returns>
    public bool IsBsplineTransform(int block)
    {
        return Is(block, BsplineTransformType) || Is(block, BsplineCompTransformType);
    }

    /// <summary>True when the block is one of the two float B-spline interpolators (slice 7, morph weights).</summary>
    /// <param name="block">A stored ref.</param>
    /// <returns>True for NiBSplineFloatInterpolator and NiBSplineCompFloatInterpolator.</returns>
    public bool IsBsplineFloat(int block)
    {
        return Is(block, BsplineFloatType) || Is(block, BsplineCompFloatType);
    }

    /// <summary>Reads a transform B-spline interpolator through the slice-1 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not a readable transform B-spline interpolator.</returns>
    public bool TryReadBsplineTransform(int block, [NotNullWhen(true)] out NifBsplineInterpolatorView? view)
    {
        view = null;
        return IsBsplineTransform(block) &&
               NifBsplineTransformReader.TryReadInterpolatorView(_bytes, State.Info, State.Info.Blocks[block],
                   out view) &&
               view.Kind == NifBsplineInterpolatorKind.Transform;
    }

    /// <summary>Reads a float B-spline interpolator through the slice-1 view reader (slice 7, morph weights).</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view (one static word, one handle, and for the Comp form one offset and half range).</param>
    /// <returns>False when the block is not a readable float B-spline interpolator.</returns>
    public bool TryReadBsplineFloat(int block, [NotNullWhen(true)] out NifBsplineInterpolatorView? view)
    {
        view = null;
        return IsBsplineFloat(block) &&
               NifBsplineTransformReader.TryReadInterpolatorView(_bytes, State.Info, State.Info.Blocks[block],
                   out view) &&
               view.Kind == NifBsplineInterpolatorKind.Float;
    }

    /// <summary>Reads an NiBSplineData block through the slice-1 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not a readable NiBSplineData.</returns>
    public bool TryReadBsplineData(int block, out NifBsplineDataView view)
    {
        view = default;
        return Is(block, BsplineDataType) &&
               NifBsplineTransformReader.TryReadDataView(_bytes, State.Info, State.Info.Blocks[block], out view);
    }

    /// <summary>Reads an NiBSplineBasisData block's Num Control Points through the slice-1 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="count">The stored count.</param>
    /// <returns>False when the block is not a readable NiBSplineBasisData.</returns>
    public bool TryReadBsplineBasis(int block, out uint count)
    {
        count = 0;
        return Is(block, BsplineBasisType) &&
               NifBsplineTransformReader.TryReadBasisView(_bytes, State.Info, State.Info.Blocks[block], out count);
    }

    /// <summary>Reads an NiTextKeyExtraData block through the slice-1 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block does not read.</returns>
    public bool TryReadTextKeys(int block, [NotNullWhen(true)] out NifTextKeyExtraDataView? view)
    {
        view = null;
        return IsBlock(block) &&
               NifTextKeyReader.TryReadView(_bytes, State.Info, State.Info.Blocks[block], out view);
    }

    /// <summary>Reads an NiGeomMorpherController through the slice-7 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not a readable 20.2.0.7 NiGeomMorpherController.</returns>
    public bool TryReadMorpherController(int block, [NotNullWhen(true)] out NifGeomMorpherControllerView? view)
    {
        view = null;
        return Is(block, MorpherControllerType) &&
               NifGeomMorpherReader.TryReadControllerView(_bytes, State.Info, State.Info.Blocks[block], out view);
    }

    /// <summary>Reads the frame table of an NiMorphData block through the slice-7 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not a readable 20.2.0.7 NiMorphData.</returns>
    public bool TryReadMorphData(int block, [NotNullWhen(true)] out NifMorphDataView? view)
    {
        view = null;
        return Is(block, MorphDataType) &&
               NifGeomMorpherReader.TryReadMorphDataView(_bytes, State.Info, State.Info.Blocks[block], out view);
    }

    /// <summary>Reads an NiFloatInterpolator through the slice-7 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiFloatInterpolator of exactly 8 bytes.</returns>
    public bool TryReadFloatInterpolator(int block, out NifFloatInterpolatorView view)
    {
        view = default;
        return Is(block, FloatInterpolatorType) &&
               NifGeomMorpherReader.TryReadFloatInterpolatorView(_bytes, State.Info, State.Info.Blocks[block],
                   out view);
    }

    /// <summary>
    ///     Reads an NiFloatData block's one key group through the slice-1 key-group reader. Whether the group consumes
    ///     the block exactly is the caller's check (<see cref="NifKeyGroupView.EndOffset" /> against <see cref="BlockEnd" />).
    /// </summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiFloatData or its group does not read.</returns>
    public bool TryReadFloatData(int block, out NifKeyGroupView view)
    {
        view = default;
        return Is(block, FloatDataType) &&
               NifKeyGroupReader.TryReadDataBlockView(_bytes, State.Info, State.Info.Blocks[block], out view);
    }

    /// <summary>Reads an NiPoint3Interpolator through the slice-14 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiPoint3Interpolator of exactly 16 bytes.</returns>
    public bool TryReadPoint3Interpolator(int block, out NifPoint3InterpolatorView view)
    {
        view = default;
        return IsBlock(block) &&
               NifPropertyInterpolatorReader.TryReadPoint3InterpolatorView(_bytes, State.Info, State.Info.Blocks[block],
                   out view);
    }

    /// <summary>Reads an NiBoolInterpolator or NiBoolTimelineInterpolator through the slice-14 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is neither bool interpolator type of exactly 5 bytes.</returns>
    public bool TryReadBoolInterpolator(int block, out NifBoolInterpolatorView view)
    {
        view = default;
        return IsBlock(block) &&
               NifPropertyInterpolatorReader.TryReadBoolInterpolatorView(_bytes, State.Info, State.Info.Blocks[block],
                   out view);
    }

    /// <summary>
    ///     Reads an NiPosData block's one Vector3 key group through the slice-1 key-group reader. Whether the group
    ///     consumes the block exactly is the caller's check (<see cref="NifKeyGroupView.EndOffset" /> against <see cref="BlockEnd" />).
    /// </summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiPosData or its group does not read.</returns>
    public bool TryReadPosData(int block, out NifKeyGroupView view)
    {
        view = default;
        return Is(block, PosDataType) &&
               NifKeyGroupReader.TryReadDataBlockView(_bytes, State.Info, State.Info.Blocks[block], out view);
    }

    /// <summary>
    ///     Reads an NiBoolData block's one byte key group through the slice-1 key-group reader. Whether the group
    ///     consumes the block exactly is the caller's check.
    /// </summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiBoolData or its group does not read.</returns>
    public bool TryReadBoolData(int block, out NifKeyGroupView view)
    {
        view = default;
        return Is(block, BoolDataType) &&
               NifKeyGroupReader.TryReadDataBlockView(_bytes, State.Info, State.Info.Blocks[block], out view);
    }

    /// <summary>True when the block is one of the two Point3 B-spline interpolators (slice 14, color tracks).</summary>
    /// <param name="block">A stored ref.</param>
    /// <returns>True for NiBSplinePoint3Interpolator and NiBSplineCompPoint3Interpolator.</returns>
    public bool IsBsplinePoint3(int block)
    {
        return Is(block, BsplinePoint3Type) || Is(block, BsplineCompPoint3Type);
    }

    /// <summary>Reads a Point3 B-spline interpolator through the slice-1 view reader (slice 14, color tracks).</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view (three static words, one handle, and for the Comp form one offset and half range).</param>
    /// <returns>False when the block is not a readable Point3 B-spline interpolator.</returns>
    public bool TryReadBsplinePoint3(int block, [NotNullWhen(true)] out NifBsplineInterpolatorView? view)
    {
        view = null;
        return IsBsplinePoint3(block) &&
               NifBsplineTransformReader.TryReadInterpolatorView(_bytes, State.Info, State.Info.Blocks[block],
                   out view) &&
               view.Kind == NifBsplineInterpolatorKind.Point3;
    }

    /// <summary>Reads an NiUVData block's four float key groups through the slice-14 view reader.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiUVData or a group does not read.</returns>
    public bool TryReadUvData(int block, out NifUvDataView view)
    {
        view = default;
        return Is(block, UvDataType) &&
               NifUvDataReader.TryReadView(_bytes, State.Info, State.Info.Blocks[block], out view);
    }

    /// <summary>Reads an NiTextureTransformController's own fields through the slice-14 view reader.</summary>
    /// <param name="block">The controller block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiTextureTransformController of exactly its size.</returns>
    public bool TryReadTextureTransformController(int block, out NifTextureTransformControllerView view)
    {
        view = default;
        return IsBlock(block) &&
               NifPropertyControllerReader.TryReadTextureTransformView(_bytes, State.Info, State.Info.Blocks[block],
                   out view);
    }

    /// <summary>Reads an NiMaterialColorController's Target Color through the slice-14 view reader.</summary>
    /// <param name="block">The controller block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiMaterialColorController of exactly its size.</returns>
    public bool TryReadMaterialColorController(int block, out NifMaterialColorControllerView view)
    {
        view = default;
        return IsBlock(block) &&
               NifPropertyControllerReader.TryReadMaterialColorView(_bytes, State.Info, State.Info.Blocks[block],
                   out view);
    }

    /// <summary>Reads an NiUVController's own fields through the slice-14 view reader.</summary>
    /// <param name="block">The controller block index.</param>
    /// <param name="view">The view.</param>
    /// <returns>False when the block is not an NiUVController of exactly its size.</returns>
    public bool TryReadUvController(int block, out NifUvControllerView view)
    {
        view = default;
        return IsBlock(block) &&
               NifPropertyControllerReader.TryReadUvControllerView(_bytes, State.Info, State.Info.Blocks[block],
                   out view);
    }

    /// <summary>
    ///     The blocks an interpolator references for its keys, without mapping anything: the interpolator itself, then its
    ///     Data ref (a transform, float, Point3 or bool interpolator) or its Spline Data and Basis Data refs (a transform,
    ///     float or Point3 B-spline), where those name blocks of the file. Used to mark a whole binding native.
    /// </summary>
    /// <param name="interpolator">A stored Interpolator ref.</param>
    /// <returns>The blocks; empty when the ref names no block.</returns>
    public IReadOnlyList<int> InterpolatorBlocks(int interpolator)
    {
        if (!IsBlock(interpolator))
        {
            return [];
        }

        var blocks = new List<int> { interpolator };
        if (TryReadTransformInterpolator(interpolator, out var transform))
        {
            AddIfBlock(blocks, transform.DataRef);
        }
        else if (TryReadFloatInterpolator(interpolator, out var scalar))
        {
            AddIfBlock(blocks, scalar.DataRef);
        }
        else if (TryReadPoint3Interpolator(interpolator, out var point))
        {
            AddIfBlock(blocks, point.DataRef);
        }
        else if (TryReadBoolInterpolator(interpolator, out var flag))
        {
            AddIfBlock(blocks, flag.DataRef);
        }
        else if ((IsBsplineTransform(interpolator) || IsBsplineFloat(interpolator) || IsBsplinePoint3(interpolator)) &&
                 NifBsplineTransformReader.TryReadInterpolatorView(_bytes, State.Info, State.Info.Blocks[interpolator],
                     out var spline))
        {
            AddIfBlock(blocks, spline.SplineDataRef);
            AddIfBlock(blocks, spline.BasisDataRef);
        }

        return blocks;
    }

    /// <summary>A decoded ref array (for example an NiControllerManager's Controller Sequences), entries as stored.</summary>
    /// <param name="block">The block index.</param>
    /// <param name="field">The nif.xml field name.</param>
    /// <param name="refs">The refs, -1 entries kept.</param>
    /// <returns>False when the field did not decode as an array of refs.</returns>
    public bool TryReadRefs(int block, string field, out IReadOnlyList<int> refs)
    {
        refs = [];
        if (!IsBlock(block) || !State.Blocks[block].Root.TryGet(field, out var value) || value is not NifArrayValue array)
        {
            return false;
        }

        var list = new int[array.Count];
        for (var i = 0; i < list.Length; i++)
        {
            if (array.Items[i] is not NifRefValue reference)
            {
                return false;
            }

            list[i] = reference.Index;
        }

        refs = list;
        return true;
    }

    /// <summary>A decoded single ref that names a block of the file (a tolerant decode may keep an out-of-range index).</summary>
    /// <param name="block">The block index.</param>
    /// <param name="field">The nif.xml field name.</param>
    /// <returns>The named block, or null.</returns>
    public int? Link(int block, string field)
    {
        return IsBlock(block) && State.Blocks[block].Root.TryGet(field, out var value) &&
               value is NifRefValue { IsNone: false } link && IsBlock(link.Index)
            ? link.Index
            : null;
    }

    private void AddIfBlock(List<int> blocks, int reference)
    {
        if (IsBlock(reference) && !blocks.Contains(reference))
        {
            blocks.Add(reference);
        }
    }
}
