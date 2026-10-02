using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 8 (plan section 2.1): the coverage classification of every animation-related block, as a pure
///     classifier over the block type, the reader's decisions about the block (<see cref="NifModelAnimationDisposition" />)
///     and, for interpolators and key data, whether only particle controllers reach it. Typed wins when one data block
///     feeds typed and native consumers; otherwise the first NativeOnly reason the reader recorded; otherwise the table
///     row for the type. Since slice 10 <see cref="NifModelReader" /> merges the reader's decisions with the other
///     sub-readers' and <see cref="NifModelCoverage" /> takes the table row for an undecided animation block.
/// </summary>
/// <remarks>
///     <para>
///         The table rows, in the order they are tested (the order fixes the category of BSFrustumFOVController and the
///         light controllers, which are NiFloatInterpController and NiPoint3InterpController subclasses and would otherwise
///         fall to the property row; and of bhkBlendController and the particle controllers, which are NiTimeControllers):
///     </para>
///     <list type="number">
///         <item>Havok (<c>bhk*</c>): <see cref="NifModelCoverage.HavokReason" />.</item>
///         <item>Particle controllers and NiPSysEmitterCtlrData: <see cref="NifModelCoverage.ParticleReason" />; also interpolators and key data reached only from particle controllers.</item>
///         <item>BSFrustumFOVController and the light controllers: <see cref="NifModelCoverage.CameraLightReason" />.</item>
///         <item>The two refraction controllers: <see cref="RefractionReason" />.</item>
///         <item>NiExtraDataController and subclasses: <see cref="ExtraDataControllerReason" />.</item>
///         <item>NiBoneLODController and subclasses: <see cref="BoneLodReason" />.</item>
///         <item>BSAnimNotes and BSAnimNote: <see cref="NifModelAnimNotes.Reason" />.</item>
///         <item>NiTextKeyExtraData: <see cref="NifModelAnimationReasons.TextKeysOutsideClip" />.</item>
///         <item>NiSequence and subclasses: <see cref="SequenceUnlistedReason" /> (a sequence no manager lists is not read).</item>
///         <item>NiControllerManager: <see cref="NifModelAnimationReasons.ManagerNoClip" />.</item>
///         <item>NiAVObjectPalette and subclasses: <see cref="PaletteNoBindingReason" /> (the 1a name rule decides display names).</item>
///         <item>NiMultiTargetTransformController: <see cref="NifModelAnimationReasons.MultiTargetBinding" />.</item>
///         <item>BSTreadTransfInterpolator, BSTreadTransfController: <see cref="NifModelAnimationReasons.TreadTransform" />.</item>
///         <item>BSRotAccumTransfInterpolator: <see cref="NifModelAnimationReasons.RotationAccumulation" />.</item>
///         <item>Path and LookAt interpolators and controllers: <see cref="NifModelAnimationReasons.PathLookAt" />.</item>
///         <item>NiBlendInterpolator and subclasses: <see cref="NifModelAnimationReasons.BlendState" />.</item>
///         <item>Visibility (NiVisController, NiBoolInterpolator and NiBoolTimelineInterpolator, NiBoolData, NiVisData): <see cref="VisibilityUnreachedReason" />.</item>
///         <item>NiGeomMorpherController: <see cref="MorphUnreachedReason" />.</item>
///         <item>Transform controllers (NiKeyframeController and subclasses): <see cref="ControllerUnreachedReason" />; transform interpolators and their data: <see cref="CurveUnreachedReason" />.</item>
///         <item>Material, texture and UV controllers, the float, Point3 and color interpolators, the float and Point3 B-splines, and NiFloatData, NiPosData, NiColorData, NiUVData: <see cref="PropertyUnreachedReason" />.</item>
///         <item>
///             NiStringPalette: <see cref="StringPaletteReason" />. Cut 2: in a 20.0.0.4 <c>.kf</c> the palette is what
///             every controlled block names its target through, so the reader records it Typed when a controlled block
///             it resolved a target through joined a clip (<see cref="NifModelAnimationReader" />); the row is for a
///             palette no admitted binding used (a sequence kept native, or a palette nothing references).
///         </item>
///         <item>Any other NiTimeController, NiInterpolator or animation data type: <see cref="OtherControllerReason" />, <see cref="OtherInterpolatorReason" />, <see cref="OtherAnimationDataReason" />.</item>
///     </list>
///     <para>
///         Every row the plan wrote as pending a Shared form is settled: Euler and quaternion TBC and QUADRATIC rotations
///         map since slices 13 and 16b, and property and visibility tracks since slice 14 (Shared <c>68335d3</c>), so a
///         block carrying them is decided by the reader (Typed, or NativeOnly with the slice's own reason) and the table
///         row says only that no clip reached it. NiVisData has no 20.2.0.7 carrier (NiVisController's Data ref ends at
///         10.1.0.103) and keeps the visibility row. NiMorphData is the cut-1a geometry reader's block and is not an
///         animation block here.
///     </para>
/// </remarks>
internal static class NifModelAnimationCoverage
{
    /// <summary>The refraction controllers (plan section 2.1).</summary>
    public const string RefractionReason = "refraction: no typed vocabulary (cut 2)";

    /// <summary>The code of <see cref="RefractionReason" />.</summary>
    public const string RefractionCode = "refraction";

    /// <summary>The code of <see cref="NifModelCoverage.CameraLightReason" /> on a camera or light controller.</summary>
    public const string CameraLightCode = "cameraLight";

    /// <summary>The code of <see cref="NifModelCoverage.HavokReason" />.</summary>
    public const string HavokCode = "havok";

    /// <summary>The code of <see cref="NifModelCoverage.ParticleReason" />.</summary>
    public const string ParticlesCode = "particles";

    /// <summary>The extra-data controllers (plan section 2.1).</summary>
    public const string ExtraDataControllerReason = "drives extra data, no visual";

    /// <summary>The code of <see cref="ExtraDataControllerReason" />.</summary>
    public const string ExtraDataControllerCode = "extraDataController";

    /// <summary>The bone LOD controllers (plan section 2.1).</summary>
    public const string BoneLodReason = "runtime bone LOD";

    /// <summary>The code of <see cref="BoneLodReason" />.</summary>
    public const string BoneLodCode = "boneLod";

    /// <summary>The code of <see cref="NifModelAnimNotes.Reason" />.</summary>
    public const string AnimNotesCode = "animNotes";

    /// <summary>A <c>.nif</c> sequence no NiControllerManager lists (the reader reads sequences through their managers).</summary>
    public const string SequenceUnlistedReason = "sequence not listed by any manager: not read";

    /// <summary>The code of <see cref="SequenceUnlistedReason" />.</summary>
    public const string SequenceUnlistedCode = "sequenceUnlisted";

    /// <summary>An object palette no sequence bound through (the cut-1a name rule decides its display-name use).</summary>
    public const string PaletteNoBindingReason =
        "object palette: no animation binding used it (display names follow the cut-1a palette rule)";

    /// <summary>The code of <see cref="PaletteNoBindingReason" />.</summary>
    public const string PaletteNoBindingCode = "paletteNoBinding";

    /// <summary>A visibility block (NiVisController, the bool interpolators, NiBoolData, NiVisData) the reader made no decision about.</summary>
    public const string VisibilityUnreachedReason = "node visibility track not reached by any clip";

    /// <summary>The code of <see cref="VisibilityUnreachedReason" />.</summary>
    public const string VisibilityUnreachedCode = "visibilityUnreached";

    /// <summary>A material, texture or UV property block the reader made no decision about.</summary>
    public const string PropertyUnreachedReason = "material/texture property track not reached by any clip";

    /// <summary>The code of <see cref="PropertyUnreachedReason" />.</summary>
    public const string PropertyUnreachedCode = "propertyUnreached";

    /// <summary>An NiGeomMorpherController the reader made no decision about (no sequence or clip reached it).</summary>
    public const string MorphUnreachedReason = "morph controller not reached by any clip";

    /// <summary>The code of <see cref="MorphUnreachedReason" />.</summary>
    public const string MorphUnreachedCode = "morphUnreached";

    /// <summary>A transform controller the reader made no decision about.</summary>
    public const string ControllerUnreachedReason = "transform controller not reached by any clip";

    /// <summary>The code of <see cref="ControllerUnreachedReason" />.</summary>
    public const string ControllerUnreachedCode = "controllerUnreached";

    /// <summary>A transform interpolator or its key data the reader made no decision about.</summary>
    public const string CurveUnreachedReason = "transform curve not reached by any clip";

    /// <summary>The code of <see cref="CurveUnreachedReason" />.</summary>
    public const string CurveUnreachedCode = "curveUnreached";

    /// <summary>NiStringPalette (a pre-20.1 sequence's string store) that no admitted controlled block resolved a target through.</summary>
    public const string StringPaletteReason = "string palette: no controlled block of a clip bound a target through it";

    /// <summary>The code of <see cref="StringPaletteReason" />.</summary>
    public const string StringPaletteCode = "stringPalette";

    /// <summary>A controller type outside the plan table.</summary>
    public const string OtherControllerReason = "controller type outside the plan table: no typed vocabulary";

    /// <summary>The code of <see cref="OtherControllerReason" />.</summary>
    public const string OtherControllerCode = "otherController";

    /// <summary>An interpolator type outside the plan table.</summary>
    public const string OtherInterpolatorReason = "interpolator type outside the plan table: no typed vocabulary";

    /// <summary>The code of <see cref="OtherInterpolatorReason" />.</summary>
    public const string OtherInterpolatorCode = "otherInterpolator";

    /// <summary>An animation data type outside the plan table.</summary>
    public const string OtherAnimationDataReason = "animation data outside the plan table: no typed vocabulary";

    /// <summary>The code of <see cref="OtherAnimationDataReason" />.</summary>
    public const string OtherAnimationDataCode = "otherAnimationData";

    /// <summary>The code of <see cref="NifModelAnimationReasons.TextKeysOutsideClip" /> on an undecided NiTextKeyExtraData.</summary>
    public const string TextKeysOutsideClipCode = NifModelAnimationReasons.TextKeysOutsideClipCode;

    private const string TimeControllerType = "NiTimeController";
    private const string InterpolatorType = "NiInterpolator";
    private const string SequenceType = "NiSequence";
    private const string PaletteType = "NiAVObjectPalette";
    private const string KeyframeDataType = "NiKeyframeData";
    private const string ControllerSequenceType = "NiControllerSequence";
    private const string TreadControllerName = "BSTreadTransfController";

    /// <summary>The animation data types with no nif.xml ancestor of their own (the cut-1a list, widened by NiRotData and NiPSysEmitterCtlrData).</summary>
    private static readonly HashSet<string> AnimationDataTypes = new(StringComparer.Ordinal)
    {
        "NiFloatData", "NiPosData", "NiBoolData", "NiColorData", "NiRotData", "NiUVData", "NiVisData",
        "NiBSplineData", "NiBSplineBasisData", "NiTextKeyExtraData", "BSAnimNotes", "BSAnimNote", "NiStringPalette",
        "NiPSysEmitterCtlrData"
    };

    private static readonly HashSet<string> CameraLightControllers = new(StringComparer.Ordinal)
    {
        "BSFrustumFOVController", "NiLightColorController", "NiLightDimmerController", "NiLightIntensityController"
    };

    private static readonly HashSet<string> RefractionControllers = new(StringComparer.Ordinal)
    {
        "BSRefractionStrengthController", "BSRefractionFirePeriodController"
    };

    private static readonly HashSet<string> VisibilityTypes = new(StringComparer.Ordinal)
    {
        "NiVisController", "NiBoolData", "NiVisData"
    };

    private static readonly HashSet<string> TransformCurveTypes = new(StringComparer.Ordinal)
    {
        "NiTransformInterpolator", "NiBSplineData", "NiBSplineBasisData"
    };

    private static readonly HashSet<string> PropertyTypes = new(StringComparer.Ordinal)
    {
        "NiAlphaController", "NiMaterialColorController", "BSMaterialEmittanceMultController",
        "NiTextureTransformController", "NiUVController", "NiFlipController", "NiFloatInterpolator",
        "NiPoint3Interpolator", "NiColorInterpolator", "NiFloatData", "NiPosData", "NiColorData", "NiUVData"
    };

    private static readonly HashSet<string> PathLookAtTypes = new(StringComparer.Ordinal)
    {
        "NiPathInterpolator", "NiLookAtInterpolator", "NiPathController", "NiLookAtController"
    };

    /// <summary>True when the type belongs to the animation families the classifier covers.</summary>
    /// <param name="schema">The nif.xml definitions.</param>
    /// <param name="type">The block type name.</param>
    /// <returns>True for controllers, interpolators, sequences, palettes, keyframe data and the animation data types.</returns>
    public static bool IsAnimationType(NifSchema schema, string type)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(type);
        return string.Equals(type, TreadControllerName, StringComparison.Ordinal) ||
               AnimationDataTypes.Contains(type) ||
               schema.Inherits(type, TimeControllerType) || schema.Inherits(type, InterpolatorType) ||
               schema.Inherits(type, SequenceType) || schema.Inherits(type, PaletteType) ||
               schema.Inherits(type, KeyframeDataType);
    }

    /// <summary>
    ///     The plan 2.1 row of a type the reader made no decision about: its NativeOnly reason and code, or null when the
    ///     type is not an animation type.
    /// </summary>
    /// <param name="schema">The nif.xml definitions.</param>
    /// <param name="type">The block type name.</param>
    /// <param name="reachedOnlyFromParticles">True when only particle controllers reach the block (interpolators and key data).</param>
    /// <returns>The row, or null.</returns>
    public static (string Reason, string Code)? TableRow(NifSchema schema, string type,
        bool reachedOnlyFromParticles = false)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(type);
        if (!IsAnimationType(schema, type))
        {
            return null;
        }

        if (type.StartsWith("bhk", StringComparison.Ordinal))
        {
            return (NifModelCoverage.HavokReason, HavokCode);
        }

        var isInterpolator = schema.Inherits(type, InterpolatorType);
        var isController = schema.Inherits(type, TimeControllerType);
        if (IsParticleController(schema, type) || string.Equals(type, "NiPSysEmitterCtlrData", StringComparison.Ordinal) ||
            (reachedOnlyFromParticles && (isInterpolator || (!isController && AnimationDataTypes.Contains(type)))))
        {
            return (NifModelCoverage.ParticleReason, ParticlesCode);
        }

        if (CameraLightControllers.Contains(type))
        {
            return (NifModelCoverage.CameraLightReason, CameraLightCode);
        }

        if (RefractionControllers.Contains(type))
        {
            return (RefractionReason, RefractionCode);
        }

        if (schema.Inherits(type, "NiExtraDataController"))
        {
            return (ExtraDataControllerReason, ExtraDataControllerCode);
        }

        if (schema.Inherits(type, "NiBoneLODController"))
        {
            return (BoneLodReason, BoneLodCode);
        }

        if (type is "BSAnimNotes" or "BSAnimNote")
        {
            return (NifModelAnimNotes.Reason, AnimNotesCode);
        }

        if (string.Equals(type, "NiTextKeyExtraData", StringComparison.Ordinal))
        {
            return (NifModelAnimationReasons.TextKeysOutsideClip, TextKeysOutsideClipCode);
        }

        if (schema.Inherits(type, SequenceType))
        {
            return (SequenceUnlistedReason, SequenceUnlistedCode);
        }

        if (schema.Inherits(type, "NiControllerManager"))
        {
            return (NifModelAnimationReasons.ManagerNoClip, NifModelAnimationReasons.ManagerNoClipCode);
        }

        if (schema.Inherits(type, PaletteType))
        {
            return (PaletteNoBindingReason, PaletteNoBindingCode);
        }

        if (string.Equals(type, "NiMultiTargetTransformController", StringComparison.Ordinal))
        {
            return (NifModelAnimationReasons.MultiTargetBinding, NifModelAnimationReasons.MultiTargetBindingCode);
        }

        if (type is "BSTreadTransfInterpolator" or TreadControllerName)
        {
            return (NifModelAnimationReasons.TreadTransform, NifModelAnimationReasons.TreadTransformCode);
        }

        if (string.Equals(type, "BSRotAccumTransfInterpolator", StringComparison.Ordinal))
        {
            return (NifModelAnimationReasons.RotationAccumulation, NifModelAnimationReasons.RotationAccumulationCode);
        }

        if (PathLookAtTypes.Contains(type))
        {
            return (NifModelAnimationReasons.PathLookAt, NifModelAnimationReasons.PathLookAtCode);
        }

        if (schema.Inherits(type, "NiBlendInterpolator"))
        {
            return (NifModelAnimationReasons.BlendState, NifModelAnimationReasons.BlendStateCode);
        }

        if (VisibilityTypes.Contains(type) || schema.Inherits(type, "NiBoolInterpolator"))
        {
            return (VisibilityUnreachedReason, VisibilityUnreachedCode);
        }

        if (string.Equals(type, NifModelAnimationMorphs.MorpherControllerType, StringComparison.Ordinal))
        {
            return (MorphUnreachedReason, MorphUnreachedCode);
        }

        if (schema.Inherits(type, "NiKeyframeController"))
        {
            return (ControllerUnreachedReason, ControllerUnreachedCode);
        }

        if (TransformCurveTypes.Contains(type) || schema.Inherits(type, KeyframeDataType) ||
            schema.Inherits(type, "NiBSplineTransformInterpolator"))
        {
            return (CurveUnreachedReason, CurveUnreachedCode);
        }

        if (PropertyTypes.Contains(type) || schema.Inherits(type, "NiFloatInterpController") ||
            schema.Inherits(type, "NiPoint3InterpController") || schema.Inherits(type, "NiBSplineFloatInterpolator") ||
            schema.Inherits(type, "NiBSplinePoint3Interpolator"))
        {
            return (PropertyUnreachedReason, PropertyUnreachedCode);
        }

        if (string.Equals(type, "NiStringPalette", StringComparison.Ordinal))
        {
            return (StringPaletteReason, StringPaletteCode);
        }

        if (isController)
        {
            return (OtherControllerReason, OtherControllerCode);
        }

        return isInterpolator
            ? (OtherInterpolatorReason, OtherInterpolatorCode)
            : (OtherAnimationDataReason, OtherAnimationDataCode);
    }

    /// <summary>
    ///     Classifies one block: Typed when any decision about it is Typed (whatever the order), else the first NativeOnly
    ///     decision, else the table row; null when the type is not an animation type.
    /// </summary>
    /// <param name="schema">The nif.xml definitions.</param>
    /// <param name="block">The block.</param>
    /// <param name="type">Its type name.</param>
    /// <param name="decisions">Every reader decision about the block, in the order the reader made them.</param>
    /// <param name="reachedOnlyFromParticles">True when only particle controllers reach the block.</param>
    /// <returns>The classification, or null.</returns>
    public static NifModelAnimationClassification? Classify(NifSchema schema, int block, string type,
        IReadOnlyList<NifModelAnimationDisposition> decisions, bool reachedOnlyFromParticles = false)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentOutOfRangeException.ThrowIfNegative(block);
        if (!IsAnimationType(schema, type))
        {
            return null;
        }

        NifModelAnimationDisposition? native = null;
        foreach (var decision in decisions)
        {
            if (decision.Block != block)
            {
                throw new ArgumentException($"A decision about block {decision.Block} was given for block {block}.",
                    nameof(decisions));
            }

            if (decision.IsTyped)
            {
                return new NifModelAnimationClassification(block, ModelSourceCoverageKind.Typed, null,
                    NifModelAnimationReasons.TypedCode);
            }

            native ??= decision;
        }

        if (native is { } first)
        {
            return new NifModelAnimationClassification(block, ModelSourceCoverageKind.NativeOnly, first.Reason,
                first.Code);
        }

        var (reason, code) = TableRow(schema, type, reachedOnlyFromParticles)!.Value;
        return new NifModelAnimationClassification(block, ModelSourceCoverageKind.NativeOnly, reason, code);
    }

    /// <summary>Classifies every animation block of a file from the reader's result, in block order.</summary>
    /// <param name="state">The read state.</param>
    /// <param name="result">The reader's result for the file.</param>
    /// <param name="cancellationToken">Observed per block.</param>
    /// <returns>One classification per animation block.</returns>
    public static IReadOnlyList<NifModelAnimationClassification> ClassifyFile(NifModelReadState state,
        NifModelAnimationResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(result);
        var byBlock = new Dictionary<int, List<NifModelAnimationDisposition>>();
        foreach (var decision in result.Decisions)
        {
            if (!byBlock.TryGetValue(decision.Block, out var list))
            {
                list = [];
                byBlock.Add(decision.Block, list);
            }

            list.Add(decision);
        }

        var particleOnly = ParticleReach(state, cancellationToken);
        var classifications = new List<NifModelAnimationClassification>();
        for (var block = 0; block < state.Blocks.Count; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decisions = byBlock.TryGetValue(block, out var list)
                ? list
                : new List<NifModelAnimationDisposition>();
            if (Classify(state.Schema, block, state.Blocks[block].Type, decisions, particleOnly.Contains(block))
                is { } classification)
            {
                classifications.Add(classification);
            }
        }

        return classifications;
    }

    /// <summary>
    ///     The interpolator and key-data blocks reached only from particle controllers (their Interpolator and Visibility
    ///     Interpolator links and those interpolators' Data, Spline Data and Basis Data links), minus every block any other
    ///     controller or any sequence controlled block reaches the same way.
    /// </summary>
    /// <param name="state">The read state (decoded blocks).</param>
    /// <param name="cancellationToken">Observed per block.</param>
    /// <returns>The block indices.</returns>
    public static HashSet<int> ParticleReach(NifModelReadState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var particles = new HashSet<int>();
        var others = new HashSet<int>();
        for (var block = 0; block < state.Blocks.Count; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = state.Blocks[block].Type;
            if (state.Schema.Inherits(type, TimeControllerType))
            {
                var reached = IsParticleController(state.Schema, type) ? particles : others;
                foreach (var field in new[] { "Interpolator", "Visibility Interpolator" })
                {
                    if (Link(state, block, field) is { } interpolator)
                    {
                        AddInterpolator(state, interpolator, reached);
                    }
                }
            }
            else if (string.Equals(type, ControllerSequenceType, StringComparison.Ordinal) &&
                     state.Blocks[block].Root.TryGet("Controlled Blocks", out var value) &&
                     value is NifArrayValue controlled)
            {
                foreach (var item in controlled.Items)
                {
                    if (item is NifStructValue entry && entry.TryGet("Interpolator", out var reference) &&
                        reference is NifRefValue { IsNone: false } link && (uint)link.Index < (uint)state.Blocks.Count)
                    {
                        AddInterpolator(state, link.Index, others);
                    }
                }
            }
        }

        particles.ExceptWith(others);
        return particles;
    }

    /// <summary>True for the particle controller families (NiPSys*, BSPSys*, NiParticleSystemController and subclasses).</summary>
    private static bool IsParticleController(NifSchema schema, string type)
    {
        return schema.Inherits(type, TimeControllerType) &&
               (type.StartsWith("NiPSys", StringComparison.Ordinal) ||
                type.StartsWith("BSPSys", StringComparison.Ordinal) ||
                schema.Inherits(type, "NiParticleSystemController"));
    }

    private static void AddInterpolator(NifModelReadState state, int interpolator, HashSet<int> reached)
    {
        reached.Add(interpolator);
        foreach (var field in new[] { "Data", "Spline Data", "Basis Data" })
        {
            if (Link(state, interpolator, field) is { } data)
            {
                reached.Add(data);
            }
        }
    }

    private static int? Link(NifModelReadState state, int block, string field)
    {
        return (uint)block < (uint)state.Blocks.Count && state.Blocks[block].Root.TryGet(field, out var value) &&
               value is NifRefValue { IsNone: false } link && (uint)link.Index < (uint)state.Blocks.Count
            ? link.Index
            : null;
    }
}
