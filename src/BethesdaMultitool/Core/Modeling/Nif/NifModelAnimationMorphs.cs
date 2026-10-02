using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 7 (plan section 1.8, RE-23, RE-21 step 7, the SA5 contract shipped at Shared <c>68335d3</c>): maps the
///     morph weights an NiGeomMorpherController drives onto Shared's per-target <see cref="SceneMorphTargetTrack" /> curves
///     (or, through <see cref="NifModelMorphTracks" />, one whole-vector <see cref="SceneMorphTrack" />). Pure over the
///     slice-1 views; <see cref="NifModelAnimationReader" /> binds the results to nodes and clips.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             Morph j (1..n-1) of the target's NiMorphData drives the cut-1a document target j - 1, exactly as
///             <see cref="NifModelMorphReader" /> numbers the targets. Morph 0 is the Base: under relative targets the
///             engine forces its weight to 1.0 and never calls its interpolator (RE-23), so slot 0 stays native with
///             <see cref="NifModelAnimationReasons.BaseWeight" /> and nothing is emitted for it.
///         </item>
///         <item>
///             Only the FIRST NiGeomMorpherController on a geometry's controller chain binds (<see cref="FirstMorpher" />):
///             the 1a reader types only its morph data. Relative Targets must be exactly 1 (RE-23 rule 1); any other value
///             keeps the controller native (RE-23 rule 7).
///         </item>
///         <item>
///             A slot's weight is its NiFloatInterpolator's NiFloatData keys through the slice-2 float mapping
///             (<see cref="NifModelCurveMapping.MapComponents" />: LINEAR, CONST, QUADRATIC as Hermite, TBC with the
///             RE-19 endpoints), a float B-spline through <see cref="NifModelBsplineMapping.MapFloatChannel" />, or a
///             Constant: the interpolator's pose Value when it is not #INV_FLT#, else the controller's stored item weight
///             (RE-23 rule 2). A valid pose Value beside keys is kept as the curve's static fallback.
///         </item>
///         <item>
///             In a sequence, a controlled block binds to the first morph whose Frame Name equals its Interpolator ID
///             (exact bytes, RE-23 rule 5, <see cref="FrameIndex" />).
///         </item>
///         <item>An NiBlend*Interpolator in a slot stays native as manager blend state; any other type is refused.</item>
///     </list>
/// </remarks>
internal static class NifModelAnimationMorphs
{
    /// <summary>The controller type that drives geometry morphs, and the Controller Type a morph controlled block names.</summary>
    public const string MorpherControllerType = NifModelMorphReader.MorpherControllerType;

    /// <summary>The code of <see cref="NifModelCoverage.NoMorphTargetsReason" /> (a morph data with no morph beyond the Base).</summary>
    public const string NoMorphTargetsCode = "noMorphTargets";

    private const string BlendInterpolatorType = "NiBlendInterpolator";
    private const string FloatInterpolatorType = "NiFloatInterpolator";
    private const string FloatDataType = "NiFloatData";
    private const int NoRef = -1;

    /// <summary>The first NiGeomMorpherController on a block's controller chain (the one the cut-1a reader types).</summary>
    /// <param name="source">The file's views.</param>
    /// <param name="targetBlock">A geometry (or any NiObjectNET) block.</param>
    /// <returns>The morpher block, or null when the chain carries none.</returns>
    public static int? FirstMorpher(NifModelAnimationSource source, int targetBlock)
    {
        ArgumentNullException.ThrowIfNull(source);
        var visited = new HashSet<int>();
        var next = source.Link(targetBlock, "Controller");
        while (next is { } controller && visited.Add(controller))
        {
            if (!source.TryReadControllerHeader(controller, out var header))
            {
                break;
            }

            if (source.Is(controller, MorpherControllerType))
            {
                return controller;
            }

            next = source.IsBlock(header.NextControllerRef) ? header.NextControllerRef : null;
        }

        return null;
    }

    /// <summary>
    ///     Reads a morpher's NiMorphData frame table and applies RE-23 rules 1 and 7: the Data ref must name an
    ///     NiMorphData that reads exactly, with Relative Targets exactly 1 and at least one morph beyond the Base.
    /// </summary>
    /// <param name="source">The file's views.</param>
    /// <param name="controller">The morpher's view.</param>
    /// <param name="data">The frame table.</param>
    /// <param name="reason">Why the controller stays native; null on success.</param>
    /// <param name="code">The code of <paramref name="reason" />; null on success.</param>
    /// <returns>True when the morph data admits typed weights.</returns>
    public static bool TryReadMorphData(
        NifModelAnimationSource source,
        NifGeomMorpherControllerView controller,
        [NotNullWhen(true)] out NifMorphDataView? data,
        [NotNullWhen(false)] out string? reason,
        [NotNullWhen(false)] out string? code)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(controller);
        data = null;
        if (!source.Is(controller.DataRef, NifModelMorphReader.MorphDataType))
        {
            (reason, code) = (NifModelAnimationReasons.MorphDataUnresolved, NifModelAnimationReasons.MorphDataUnresolvedCode);
            return false;
        }

        if (!source.TryReadMorphData(controller.DataRef, out var view) || !view.ConsumedExactly)
        {
            (reason, code) = (NifModelAnimationReasons.MorphDataUnreadable, NifModelAnimationReasons.MorphDataUnreadableCode);
            return false;
        }

        if (!view.IsRelative)
        {
            (reason, code) = (NifModelAnimationReasons.AbsoluteMorphTargets, NifModelAnimationReasons.AbsoluteMorphTargetsCode);
            return false;
        }

        if (view.MorphCount <= 1)
        {
            (reason, code) = (NifModelCoverage.NoMorphTargetsReason, NoMorphTargetsCode);
            return false;
        }

        data = view;
        (reason, code) = (null, null);
        return true;
    }

    /// <summary>The first morph whose Frame Name is exactly these bytes (RE-23 rule 5), or -1.</summary>
    /// <param name="data">The frame table.</param>
    /// <param name="strings">The file's raw header string table.</param>
    /// <param name="frameName">The Interpolator ID bytes.</param>
    /// <returns>The morph index (0 is the Base), or -1 when no frame matches.</returns>
    public static int FrameIndex(NifMorphDataView data, NifHeaderStringTable strings, ReadOnlySpan<byte> frameName)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(strings);
        for (var morph = 0; morph < data.MorphCount; morph++)
        {
            if (NifAnimationStrings.TryGetRaw(strings, data.FrameNameIndices[morph], out var bytes, out var isNone) &&
                !isNone && bytes.Span.SequenceEqual(frameName))
            {
                return morph;
            }
        }

        return -1;
    }

    /// <summary>Maps one non-Base morph slot to its weight curve, or the reason it stays native.</summary>
    /// <param name="source">The file's views.</param>
    /// <param name="morphIndex">The morph index (1 or more).</param>
    /// <param name="interpolatorRef">The slot's Interpolator ref as stored (-1 for none).</param>
    /// <param name="storedWeightBits">
    ///     The controller's stored item weight bits for the slot (an embedded morpher), which the engine uses when the
    ///     slot has no interpolator value; null for a sequence controlled block, which stores none.
    /// </param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The morph index is the Base or negative.</exception>
    public static NifModelMorphTargetResult MapSlot(
        NifModelAnimationSource source, int morphIndex, int interpolatorRef, uint? storedWeightBits)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(morphIndex, 1);
        var storedWeight = storedWeightBits is { } bits ? BitConverter.UInt32BitsToSingle(bits) : (float?)null;
        if (!source.IsBlock(interpolatorRef))
        {
            return interpolatorRef == NoRef && storedWeight is not null
                ? Constant(morphIndex, NoRef, [], storedWeight, false)
                : NifModelMorphTargetResult.Native(morphIndex, NoRef, [], NifModelAnimationReasons.NoInterpolator,
                    NifModelAnimationReasons.NoInterpolatorCode);
        }

        if (source.TryReadFloatInterpolator(interpolatorRef, out var view))
        {
            return MapFloatInterpolator(source, morphIndex, interpolatorRef, view, storedWeight);
        }

        if (source.IsBsplineFloat(interpolatorRef))
        {
            return MapFloatBspline(source, morphIndex, interpolatorRef, storedWeight);
        }

        var blocks = new[] { interpolatorRef };
        if (source.Inherits(interpolatorRef, BlendInterpolatorType))
        {
            return NifModelMorphTargetResult.Native(morphIndex, interpolatorRef, blocks,
                NifModelAnimationReasons.BlendState, NifModelAnimationReasons.BlendStateCode);
        }

        return source.Is(interpolatorRef, FloatInterpolatorType)
            ? NifModelMorphTargetResult.Native(morphIndex, interpolatorRef, blocks,
                NifModelAnimationReasons.InterpolatorUnreadable, NifModelAnimationReasons.InterpolatorUnreadableCode)
            : NifModelMorphTargetResult.Native(morphIndex, interpolatorRef, blocks,
                NifModelAnimationReasons.NotFloatInterpolator, NifModelAnimationReasons.NotFloatInterpolatorCode);
    }

    /// <summary>An NiFloatInterpolator: its NiFloatData keys, or its pose Value, or the stored item weight.</summary>
    private static NifModelMorphTargetResult MapFloatInterpolator(
        NifModelAnimationSource source, int morphIndex, int interpolator, NifFloatInterpolatorView view,
        float? storedWeight)
    {
        List<int> blocks = [interpolator];
        var fallback = view.HasNoValue ? null : (float?)BitConverter.UInt32BitsToSingle(view.ValueBits);
        if (view.DataRef == NoRef)
        {
            return Constant(morphIndex, interpolator, blocks, fallback ?? storedWeight, false);
        }

        if (!source.Is(view.DataRef, FloatDataType))
        {
            if (source.IsBlock(view.DataRef))
            {
                blocks.Add(view.DataRef);
            }

            return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                NifModelAnimationReasons.DataUnresolved, NifModelAnimationReasons.DataUnresolvedCode);
        }

        blocks.Add(view.DataRef);
        if (!source.TryReadFloatData(view.DataRef, out var group) || group.EndOffset != source.BlockEnd(view.DataRef))
        {
            return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                NifModelAnimationReasons.DataUnreadable, NifModelAnimationReasons.DataUnreadableCode);
        }

        if (group.Count == 0)
        {
            return Constant(morphIndex, interpolator, blocks, fallback ?? storedWeight, false);
        }

        var mapped = NifModelCurveMapping.MapComponents(group, 1);
        if (mapped.Curve is not { } curve)
        {
            return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                NifModelAnimationReasons.Reason(mapped.Block), NifModelAnimationReasons.Code(mapped.Block), true);
        }

        var scene = new SceneCurve(1, curve.Times, curve.Values, curve.Interpolation,
            SceneAnimationChannelState.Keyed, FiniteFallback(fallback), null, curve.TbcParameters, curve.TbcEndpoints);
        return NifModelMorphTargetResult.Typed(morphIndex, interpolator, blocks, scene, curve, true);
    }

    /// <summary>A float B-spline interpolator: its controls through the slice-2 B-spline mapping, or its static value.</summary>
    private static NifModelMorphTargetResult MapFloatBspline(
        NifModelAnimationSource source, int morphIndex, int interpolator, float? storedWeight)
    {
        List<int> blocks = [interpolator];
        if (!source.TryReadBsplineFloat(interpolator, out var view))
        {
            return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                NifModelAnimationReasons.InterpolatorUnreadable, NifModelAnimationReasons.InterpolatorUnreadableCode);
        }

        NifBsplineDataView? data = null;
        if (view.SplineDataRef != NoRef)
        {
            if (!source.IsBlock(view.SplineDataRef))
            {
                return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                    NifModelAnimationReasons.DataUnresolved, NifModelAnimationReasons.DataUnresolvedCode);
            }

            blocks.Add(view.SplineDataRef);
            if (!source.TryReadBsplineData(view.SplineDataRef, out var spline) || !spline.ConsumedExactly)
            {
                return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                    NifModelAnimationReasons.DataUnreadable, NifModelAnimationReasons.DataUnreadableCode);
            }

            data = spline;
        }

        uint? count = null;
        if (view.BasisDataRef != NoRef)
        {
            if (!source.IsBlock(view.BasisDataRef))
            {
                return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                    NifModelAnimationReasons.DataUnresolved, NifModelAnimationReasons.DataUnresolvedCode);
            }

            if (!blocks.Contains(view.BasisDataRef))
            {
                blocks.Add(view.BasisDataRef);
            }

            if (!source.TryReadBsplineBasis(view.BasisDataRef, out var basis))
            {
                return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                    NifModelAnimationReasons.DataUnreadable, NifModelAnimationReasons.DataUnreadableCode);
            }

            count = basis;
        }

        var staticBits = view.StaticValueBits[0];
        var fallback = staticBits == NifFloatInterpolatorView.InvalidValueBits
            ? null
            : (float?)BitConverter.UInt32BitsToSingle(staticBits);
        var result = NifModelBsplineMapping.MapFloatChannel(view, data, count);
        if (result.IsEmpty)
        {
            return Constant(morphIndex, interpolator, blocks, fallback ?? storedWeight, false);
        }

        if (result.Spline is not { } curve)
        {
            return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                NifModelAnimationReasons.Reason(result.Block), NifModelAnimationReasons.Code(result.Block), true);
        }

        var scene = new SceneCurve(1, [], [], SceneInterpolation.BSpline, SceneAnimationChannelState.Keyed,
            FiniteFallback(fallback), curve);
        return NifModelMorphTargetResult.Typed(morphIndex, interpolator, blocks, scene, null, true);
    }

    /// <summary>A Constant width-1 curve, or the native reason when there is no finite value to hold.</summary>
    private static NifModelMorphTargetResult Constant(int morphIndex, int interpolator, IReadOnlyList<int> blocks,
        float? value, bool hasCurve)
    {
        if (value is not { } weight)
        {
            return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                NifModelAnimationReasons.MorphNoValue, NifModelAnimationReasons.MorphNoValueCode, hasCurve);
        }

        if (!float.IsFinite(weight))
        {
            var block = NifModelCurveBlock.NonFiniteValue;
            return NifModelMorphTargetResult.Native(morphIndex, interpolator, blocks,
                NifModelAnimationReasons.Reason(block), NifModelAnimationReasons.Code(block), hasCurve);
        }

        var scene = new SceneCurve(1, [], [], SceneInterpolation.Linear, SceneAnimationChannelState.Constant,
            [weight]);
        return NifModelMorphTargetResult.Typed(morphIndex, interpolator, blocks, scene, null, hasCurve);
    }

    /// <summary>A keyed curve's static fallback: the pose Value when it is finite (Shared holds no other), else none.</summary>
    private static float[]? FiniteFallback(float? fallback)
    {
        return fallback is { } value && float.IsFinite(value) ? [value] : null;
    }
}
