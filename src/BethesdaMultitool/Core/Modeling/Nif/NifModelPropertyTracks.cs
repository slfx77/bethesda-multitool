using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 14 (SA4, plan sections 1.6 and 2.1): binds the property and visibility controllers of one file to
///     Shared <see cref="ScenePropertyTrack" />s, for the sequence clips and for the <c>(controllers)</c> clip.
///     <see cref="NifModelAnimationReader" /> owns one instance per read and hands it the controlled blocks and the
///     embedded controllers; the curves come from <see cref="NifModelPropertyCurveMapping" />, the kinds from
///     <see cref="NifModelPropertyController" />, the targets from <see cref="NifModelPropertyTargets" />.
/// </summary>
/// <remarks>
///     <para>A sequence controlled block binds the way the engine's NiControllerManager binds it (RE-21's full tag):</para>
///     <list type="number">
///         <item>Node Name to a placed block through slice 3 (<see cref="NifModelTargetNames" />: palette first, then node names, exact bytes).</item>
///         <item>
///             Property Type to the FIRST property of exactly that type in the target's OWN Properties list (the engine's
///             GetProperty walks no ancestors); an NiVisController carries no property type and binds the node itself.
///         </item>
///         <item>
///             Controller Type and Controller ID to the controller: the stored Controller ref when it names a block (it
///             must be of the Controller Type and target the bound object), else the first controller of that type on the
///             object's controller chain whose engine-formatted ID equals the stored ID (NULL equals NULL).
///         </item>
///         <item>
///             The kind from the Controller ID (<see cref="NifModelPropertyController" />), cross-checked against the
///             controller block's own fields when one is bound; an ID the format does not define fails closed.
///         </item>
///     </list>
///     <para>
///         Targets: a material kind drives EVERY material the property block feeds (one track each); a layer kind the
///         ordinal of the (property block, texture slot) layer in each such material, refused with 'texture slot not a
///         document layer' where no fed material carries it, and gated by <see cref="NifModelTextureTransformMember" />;
///         visibility drives every occurrence of the node. Two tracks on one exact target in one clip (the same kind and
///         target twice) both stay native (RE-21 step 7: the engine blends them). A <c>.kf</c> read binds none of these:
///         its skeleton is read as nodes only (D12), so neither the property blocks nor the controller chains the engine
///         would bind through are available, and every property or visibility block stays native, fail closed.
///     </para>
///     <para>
///         Embedded controllers follow the <c>(controllers)</c> clip rules (D6, RE-22): free-running and active, the
///         Target a property block of the type the controller drives (a placed node for NiVisController, a placed
///         geometry for NiUVController), the kind from the controller's own fields, the track clock from the controller.
///     </para>
/// </remarks>
internal sealed class NifModelPropertyTracks
{
    private const int NoRef = -1;
    private const int NoOrdinal = -1;
    private const int NoLayer = -1;
    private const string BaseSlot = "Base";

    /// <summary>The NiUVData groups in file order with the TransformMember each animates (U offset, V offset, U scale, V scale).</summary>
    private static readonly uint[] UvGroupOperations = [0, 1, 3, 4];

    private readonly NifModelAnimationSource _source;
    private readonly NifModelPropertyTargets _targets;
    private readonly bool _bindsInFile;
    private readonly CancellationToken _cancellationToken;
    private readonly List<Candidate> _candidates = [];

    /// <summary>Creates the component for one read.</summary>
    /// <param name="source">The file's views.</param>
    /// <param name="targets">The document's material targets (<see cref="NifModelPropertyTargets.None" /> without a material stage).</param>
    /// <param name="bindsInFile">True for a <c>.nif</c> read, whose property blocks and controller chains are the file's own; false for a <c>.kf</c>.</param>
    /// <param name="cancellationToken">Observed per binding.</param>
    public NifModelPropertyTracks(NifModelAnimationSource source, NifModelPropertyTargets targets, bool bindsInFile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(targets);
        _source = source;
        _targets = targets;
        _bindsInFile = bindsInFile;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    ///     Binds and maps one sequence's property controlled blocks, in controlled-block order, applying the repeated
    ///     target rule over the whole sequence, and appends the tracks, their extras entries and the decisions.
    /// </summary>
    /// <param name="sequence">The NiControllerSequence block.</param>
    /// <param name="bindings">The property controlled blocks: ordinal, view, the slice-3 match of the Node Name, and the Controller Type text.</param>
    /// <param name="palette">The manager's object palette block, when it has one (typed when a name binds through it).</param>
    /// <param name="pending">The sequence's decisions.</param>
    /// <param name="tracks">The clip's property tracks.</param>
    /// <param name="sources">The extras' property track map.</param>
    public void ReadSequenceBindings(
        int sequence,
        IReadOnlyList<(int Ordinal, NifControlledBlockView Block, NifModelTargetMatch Match, string ControllerType)> bindings,
        int? palette,
        List<NifModelAnimationDisposition> pending,
        List<ScenePropertyTrack> tracks,
        List<NifModelPropertyTrackSource> sources)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(sources);
        var emissions = new List<Emission>();
        foreach (var (ordinal, block, match, controllerType) in bindings)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var covered = BindingBlocks(sequence, block.InterpolatorRef);
            if (!match.IsResolved)
            {
                AddNative(pending, covered, match.Reason!, NifModelTargetNames.Code(match.Block), sequence, ordinal);
                continue;
            }

            if (match.Source == NifModelTargetSource.Palette && palette is { } paletteBlock)
            {
                pending.Add(Typed(paletteBlock, sequence, ordinal));
            }

            if (!_bindsInFile)
            {
                AddNative(pending, covered, NifModelAnimationReasons.SkeletonPropertyBinding,
                    NifModelAnimationReasons.SkeletonPropertyBindingCode, sequence, ordinal);
                continue;
            }

            if (!TryBindSequenceBlock(block, match, controllerType, out var binding, out var reason, out var code))
            {
                AddNative(pending, covered, reason, code, sequence, ordinal);
                continue;
            }

            var curve = MapCurve(binding.Kind, block.InterpolatorRef);
            if (!curve.IsTyped)
            {
                AddNative(pending, curve.Blocks.Count == 0 ? covered : curve.Blocks, curve.NativeReason!,
                    curve.NativeCode!, sequence, ordinal);
                continue;
            }

            if (!TryResolveTargets(binding, out var targets, out reason, out code))
            {
                AddNative(pending, curve.Blocks, reason, code, sequence, ordinal);
                continue;
            }

            var policy = NifModelSourcePolicyMapping.MapTrack(block);
            foreach (var (target, node) in targets)
            {
                emissions.Add(new Emission(sequence, ordinal, binding.Controller, curve, target, node,
                    binding.PropertyBlock, null, policy));
            }
        }

        Emit(emissions, pending, tracks, sources, NifModelAnimationReasons.RepeatedPropertyTarget,
            NifModelAnimationReasons.RepeatedPropertyTargetCode);
    }

    /// <summary>
    ///     Reads one embedded property controller's candidacy for the <c>(controllers)</c> clip (D6, RE-22 rules 1 and
    ///     3): its decisions go to <paramref name="decisions" /> at once; a qualifying controller waits for
    ///     <see cref="EmitControllers" />, which applies the repeated target rule over the clip.
    /// </summary>
    /// <param name="controller">The controller block.</param>
    /// <param name="graph">The file's node graph.</param>
    /// <param name="referenced">Every controller a controlled block of the file references (RE-22 rule 1).</param>
    /// <param name="decisions">The reader's decisions.</param>
    public void ReadController(int controller, NifModelNodeGraph graph, HashSet<int> referenced,
        List<NifModelAnimationDisposition> decisions)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(referenced);
        ArgumentNullException.ThrowIfNull(decisions);
        var type = _source.TypeOf(controller);
        if (!_source.TryReadControllerHeader(controller, out var header))
        {
            decisions.Add(Native(controller, NifModelAnimationReasons.ControllerUnreadable,
                NifModelAnimationReasons.ControllerUnreadableCode, controller, NoOrdinal));
            return;
        }

        if (NifModelClockMapping.IsSequenceDriven(header, referenced.Contains(controller)))
        {
            decisions.Add(Native(controller, NifModelAnimationReasons.ManagerControlled,
                NifModelAnimationReasons.ManagerControlledCode, controller, NoOrdinal));
            return;
        }

        if (string.Equals(type, NifModelPropertyController.UvControllerType, StringComparison.Ordinal))
        {
            ReadUvController(controller, header, graph, decisions);
            return;
        }

        if (!_source.TryReadInterpolatorRef(controller, out var interpolator))
        {
            decisions.Add(Native(controller, NifModelAnimationReasons.ControllerUnreadable,
                NifModelAnimationReasons.ControllerUnreadableCode, controller, NoOrdinal));
            return;
        }

        var interpolatorBlocks = _source.InterpolatorBlocks(interpolator);
        if (!header.IsActive)
        {
            ControllerNative(controller, interpolatorBlocks, decisions, NifModelCurveBlock.InactiveController);
            return;
        }

        if (!_source.IsBlock(interpolator))
        {
            decisions.Add(Native(controller, NifModelAnimationReasons.NoInterpolator,
                NifModelAnimationReasons.NoInterpolatorCode, controller, NoOrdinal));
            return;
        }

        if (!TryBindController(controller, type, header.TargetRef, graph, out var binding, out var reason, out var code))
        {
            decisions.Add(Native(controller, reason, code, controller, NoOrdinal));
            AddNative(decisions, interpolatorBlocks, reason, code, controller, NoOrdinal);
            return;
        }

        var curve = MapCurve(binding.Kind, interpolator);
        if (!curve.IsTyped)
        {
            decisions.Add(Native(controller, curve.NativeReason!, curve.NativeCode!, controller, NoOrdinal));
            AddNative(decisions, curve.Blocks, curve.NativeReason!, curve.NativeCode!, controller, NoOrdinal);
            return;
        }

        if (!TryResolveTargets(binding, out var targets, out reason, out code))
        {
            decisions.Add(Native(controller, reason, code, controller, NoOrdinal));
            AddNative(decisions, curve.Blocks, reason, code, controller, NoOrdinal);
            return;
        }

        var clock = NifModelClockMapping.MapFreeRunning(header, curve.HasCurve);
        if (clock.IsBlocked || clock.Clock is null)
        {
            var (clockReason, clockCode) = clock.IsBlocked
                ? (NifModelAnimationReasons.Reason(clock.Block), NifModelAnimationReasons.Code(clock.Block))
                : (NifModelAnimationReasons.SentinelWithoutCurve, NifModelAnimationReasons.SentinelWithoutCurveCode);
            decisions.Add(Native(controller, clockReason, clockCode, controller, NoOrdinal));
            AddNative(decisions, curve.Blocks, clockReason, clockCode, controller, NoOrdinal);
            return;
        }

        var emissions = new List<Emission>();
        foreach (var (target, node) in targets)
        {
            emissions.Add(new Emission(controller, NoOrdinal, controller, curve, target, node, binding.PropertyBlock,
                clock.Clock, null));
        }

        _candidates.Add(new Candidate(controller, header, interpolator, emissions));
    }

    /// <summary>
    ///     Emits the qualifying embedded controllers' tracks (block order) after the repeated target rule over the clip,
    ///     with their extras entries, decisions and the typed-controller entries of the clip extras.
    /// </summary>
    /// <param name="decisions">The reader's decisions.</param>
    /// <param name="tracks">The clip's property tracks.</param>
    /// <param name="sources">The extras' property track map.</param>
    /// <param name="typed">The extras' typed controllers (kind <see cref="NifModelAnimationExtras.PropertyKind" />).</param>
    public void EmitControllers(
        List<NifModelAnimationDisposition> decisions,
        List<ScenePropertyTrack> tracks,
        List<NifModelPropertyTrackSource> sources,
        List<(int Block, string Kind, NifTimeControllerHeader Header, int Interpolator)> typed)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(typed);
        var counts = new Dictionary<ScenePropertyTarget, int>();
        foreach (var candidate in _candidates)
        {
            foreach (var emission in candidate.Emissions)
            {
                counts[emission.Target] = counts.GetValueOrDefault(emission.Target) + 1;
            }
        }

        foreach (var candidate in _candidates)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Emissions.Exists(emission => counts[emission.Target] > 1))
            {
                decisions.Add(Native(candidate.Controller, NifModelAnimationReasons.RepeatedEmbeddedPropertyTarget,
                    NifModelAnimationReasons.RepeatedEmbeddedPropertyTargetCode, candidate.Controller, NoOrdinal));
                AddNative(decisions, candidate.Emissions[0].Curve.Blocks,
                    NifModelAnimationReasons.RepeatedEmbeddedPropertyTarget,
                    NifModelAnimationReasons.RepeatedEmbeddedPropertyTargetCode, candidate.Controller, NoOrdinal);
                continue;
            }

            foreach (var emission in candidate.Emissions)
            {
                tracks.Add(emission.ToTrack());
                sources.Add(emission.ToSource());
            }

            foreach (var block in candidate.Emissions[0].Curve.Blocks)
            {
                decisions.Add(Typed(block, candidate.Controller, NoOrdinal));
            }

            decisions.Add(Typed(candidate.Controller, candidate.Controller, NoOrdinal));
            typed.Add((candidate.Controller, NifModelAnimationExtras.PropertyKind, candidate.Header,
                candidate.Interpolator));
        }

        _candidates.Clear();
    }

    /// <summary>
    ///     Slice 14's NiUVController (TES3 era; no 20.2.0.7 retail case): the target geometry's placed occurrences draw
    ///     with materials whose Base map layer came from the effective NiTexturingProperty; each nonempty NiUVData group
    ///     drives that layer's offset or scale member under the member gate, on every such material.
    /// </summary>
    private void ReadUvController(int controller, NifTimeControllerHeader header, NifModelNodeGraph graph,
        List<NifModelAnimationDisposition> decisions)
    {
        if (!_source.TryReadUvController(controller, out var view))
        {
            decisions.Add(Native(controller, NifModelAnimationReasons.ControllerFieldsUnreadable,
                NifModelAnimationReasons.ControllerFieldsUnreadableCode, controller, NoOrdinal));
            return;
        }

        int[] dataBlocks = _source.IsBlock(view.DataRef) ? [view.DataRef] : [];
        if (!header.IsActive)
        {
            ControllerNative(controller, dataBlocks, decisions, NifModelCurveBlock.InactiveController);
            return;
        }

        if (!_source.IsBlock(view.DataRef))
        {
            decisions.Add(Native(controller, NifModelAnimationReasons.NoInterpolator,
                NifModelAnimationReasons.NoInterpolatorCode, controller, NoOrdinal));
            return;
        }

        if (!_source.TryReadUvData(view.DataRef, out var data))
        {
            var unresolved = _source.Is(view.DataRef, "NiUVData")
                ? (NifModelAnimationReasons.DataUnreadable, NifModelAnimationReasons.DataUnreadableCode)
                : (NifModelAnimationReasons.DataUnresolved, NifModelAnimationReasons.DataUnresolvedCode);
            decisions.Add(Native(controller, unresolved.Item1, unresolved.Item2, controller, NoOrdinal));
            AddNative(decisions, dataBlocks, unresolved.Item1, unresolved.Item2, controller, NoOrdinal);
            return;
        }

        if (!data.ConsumedExactly)
        {
            decisions.Add(Native(controller, NifModelAnimationReasons.DataUnreadable,
                NifModelAnimationReasons.DataUnreadableCode, controller, NoOrdinal));
            AddNative(decisions, dataBlocks, NifModelAnimationReasons.DataUnreadable,
                NifModelAnimationReasons.DataUnreadableCode, controller, NoOrdinal);
            return;
        }

        if (view.TextureSet != 0)
        {
            decisions.Add(Native(controller, NifModelAnimationReasons.UvControllerTextureSet,
                NifModelAnimationReasons.UvControllerTextureSetCode, controller, NoOrdinal));
            AddNative(decisions, dataBlocks, NifModelAnimationReasons.UvControllerTextureSet,
                NifModelAnimationReasons.UvControllerTextureSetCode, controller, NoOrdinal);
            return;
        }

        var target = header.TargetRef;
        var materials = new List<int>();
        if (_source.IsBlock(target) && target < graph.OccurrencesByBlock.Count)
        {
            foreach (var node in graph.OccurrencesByBlock[target])
            {
                foreach (var material in _targets.MaterialsOfNode(node))
                {
                    if (!materials.Contains(material))
                    {
                        materials.Add(material);
                    }
                }
            }
        }

        if (materials.Count == 0)
        {
            decisions.Add(Native(controller, NifModelAnimationReasons.UvControllerTargetNotGeometry,
                NifModelAnimationReasons.UvControllerTargetNotGeometryCode, controller, NoOrdinal));
            AddNative(decisions, dataBlocks, NifModelAnimationReasons.UvControllerTargetNotGeometry,
                NifModelAnimationReasons.UvControllerTargetNotGeometryCode, controller, NoOrdinal);
            return;
        }

        var hasCurve = false;
        foreach (var group in data.Groups)
        {
            hasCurve |= group.Count > 0;
        }

        var clock = NifModelClockMapping.MapFreeRunning(header, hasCurve);
        if (clock.IsBlocked || clock.Clock is null)
        {
            var (clockReason, clockCode) = clock.IsBlocked
                ? (NifModelAnimationReasons.Reason(clock.Block), NifModelAnimationReasons.Code(clock.Block))
                : (NifModelAnimationReasons.SentinelWithoutCurve, NifModelAnimationReasons.SentinelWithoutCurveCode);
            decisions.Add(Native(controller, clockReason, clockCode, controller, NoOrdinal));
            AddNative(decisions, dataBlocks, clockReason, clockCode, controller, NoOrdinal);
            return;
        }

        var emissions = new List<Emission>();
        var groups = data.Groups.ToArray();
        string? failure = null;
        string? failureCode = null;
        for (var index = 0; index < groups.Length && failure is null; index++)
        {
            var result = NifModelPropertyCurveMapping.MapUvGroup(groups[index], view.DataRef);
            if (result is null)
            {
                continue;
            }

            if (!result.IsTyped)
            {
                (failure, failureCode) = (result.NativeReason!, result.NativeCode!);
                break;
            }

            foreach (var material in materials)
            {
                if (!_targets.TryFindLayer(material, BaseSlot, out var propertyBlock, out var ordinal) ||
                    !TryReadMap(propertyBlock, BaseSlot, out var map, out failure, out failureCode))
                {
                    failure ??= NifModelAnimationReasons.UvControllerNoBaseLayer;
                    failureCode ??= NifModelAnimationReasons.UvControllerNoBaseLayerCode;
                    break;
                }

                if (!NifModelTextureTransformMember.TryMap(map, UvGroupOperations[index], out var kind, out var gate))
                {
                    (failure, failureCode) = (gate!, NifModelAnimationReasons.TextureTransformMemberCode);
                    break;
                }

                emissions.Add(new Emission(controller, NoOrdinal, controller, result,
                    new ScenePropertyTarget(kind, material, ordinal), NoRef, propertyBlock, clock.Clock, null));
            }
        }

        if (failure is not null)
        {
            decisions.Add(Native(controller, failure, failureCode!, controller, NoOrdinal));
            AddNative(decisions, dataBlocks, failure, failureCode!, controller, NoOrdinal);
            return;
        }

        if (emissions.Count == 0)
        {
            decisions.Add(Native(controller, NifModelAnimationReasons.PropertyNoValue,
                NifModelAnimationReasons.PropertyNoValueCode, controller, NoOrdinal));
            AddNative(decisions, dataBlocks, NifModelAnimationReasons.PropertyNoValue,
                NifModelAnimationReasons.PropertyNoValueCode, controller, NoOrdinal);
            return;
        }

        _candidates.Add(new Candidate(controller, header, NoRef, emissions));
    }

    /// <summary>The engine's binding of one sequence controlled block (see the type remarks), after its Node Name bound.</summary>
    private bool TryBindSequenceBlock(NifControlledBlockView block, NifModelTargetMatch match, string controllerType,
        out Binding binding, out string reason, out string code)
    {
        binding = default;
        var strings = _source.Strings;
        if (!NifAnimationStrings.TryGetRaw(strings, block.PropertyTypeIndex, out var propertyType, out var noPropertyType))
        {
            (reason, code) = (NifModelAnimationReasons.PropertyTypeUnresolved,
                NifModelAnimationReasons.PropertyTypeUnresolvedCode);
            return false;
        }

        if (!NifAnimationStrings.TryGetRaw(strings, block.ControllerIdIndex, out var controllerId, out var noId))
        {
            (reason, code) = (NifModelAnimationReasons.ControllerIdUnresolved,
                NifModelAnimationReasons.ControllerIdUnresolvedCode);
            return false;
        }

        var isVisibility = string.Equals(controllerType, NifModelPropertyController.VisControllerType,
            StringComparison.Ordinal);
        int targetBlock;
        if (isVisibility)
        {
            if (!noPropertyType)
            {
                (reason, code) = (NifModelAnimationReasons.VisibilityWithPropertyType,
                    NifModelAnimationReasons.VisibilityWithPropertyTypeCode);
                return false;
            }

            targetBlock = match.TargetBlock;
        }
        else
        {
            if (noPropertyType)
            {
                (reason, code) = (NifModelAnimationReasons.PropertyTypeMissing,
                    NifModelAnimationReasons.PropertyTypeMissingCode);
                return false;
            }

            targetBlock = FindOwnProperty(match.TargetBlock, propertyType.Span);
            if (targetBlock == NoRef)
            {
                (reason, code) = (NifModelAnimationReasons.PropertyNotOnTarget,
                    NifModelAnimationReasons.PropertyNotOnTargetCode);
                return false;
            }
        }

        int controller;
        if (_source.IsBlock(block.ControllerRef))
        {
            if (!_source.Is(block.ControllerRef, controllerType) ||
                !_source.TryReadControllerHeader(block.ControllerRef, out var header) || header.TargetRef != targetBlock)
            {
                (reason, code) = (NifModelAnimationReasons.ControllerRefMismatch,
                    NifModelAnimationReasons.ControllerRefMismatchCode);
                return false;
            }

            controller = block.ControllerRef;
        }
        else
        {
            controller = FindOnChain(targetBlock, controllerType, controllerId.Span, noId);
            if (controller == NoRef)
            {
                (reason, code) = (NifModelAnimationReasons.ControllerNotOnChain,
                    NifModelAnimationReasons.ControllerNotOnChainCode);
                return false;
            }
        }

        if (!TryResolveKindFromId(controllerType, controllerId.Span, noId, controller, out var kind, out var slot,
                out var operation, out reason, out code))
        {
            return false;
        }

        binding = new Binding(kind, isVisibility ? NoRef : targetBlock, slot, operation, controller,
            isVisibility ? match.Occurrences : Array.Empty<int>());
        return true;
    }

    /// <summary>The binding of one embedded controller from its own Target and fields.</summary>
    private bool TryBindController(int controller, string type, int target, NifModelNodeGraph graph,
        out Binding binding, out string reason, out string code)
    {
        binding = default;
        reason = string.Empty;
        code = string.Empty;
        if (string.Equals(type, NifModelPropertyController.VisControllerType, StringComparison.Ordinal))
        {
            if (!_source.IsBlock(target) || target >= graph.OccurrencesByBlock.Count ||
                graph.OccurrencesByBlock[target].Count == 0)
            {
                (reason, code) = (NifModelAnimationReasons.TargetNotPlaced, NifModelAnimationReasons.TargetNotPlacedCode);
                return false;
            }

            binding = new Binding(ScenePropertyKind.NodeVisibility, NoRef, null, null, controller,
                graph.OccurrencesByBlock[target]);
            return true;
        }

        var propertyType = string.Equals(type, NifModelPropertyController.TextureTransformControllerType,
            StringComparison.Ordinal)
            ? NifModelPropertyController.TexturingPropertyType
            : NifModelPropertyController.MaterialPropertyType;
        if (!_source.Inherits(target, propertyType))
        {
            (reason, code) = (NifModelAnimationReasons.ControllerTargetNotProperty,
                NifModelAnimationReasons.ControllerTargetNotPropertyCode);
            return false;
        }

        if (!TryResolveKindFromBlock(controller, type, out var kind, out var slot, out var operation, out reason,
                out code))
        {
            return false;
        }

        binding = new Binding(kind, target, slot, operation, controller, Array.Empty<int>());
        return true;
    }

    /// <summary>The kind a sequence's Controller Type and Controller ID name, cross-checked against the bound controller's fields.</summary>
    private bool TryResolveKindFromId(string controllerType, ReadOnlySpan<byte> id, bool noId, int controller,
        out ScenePropertyKind kind, out string? slot, out uint? operation, out string reason, out string code)
    {
        kind = default;
        slot = null;
        operation = null;
        reason = string.Empty;
        code = string.Empty;
        switch (controllerType)
        {
            case NifModelPropertyController.AlphaControllerType:
            case NifModelPropertyController.EmittanceMultControllerType:
            case NifModelPropertyController.VisControllerType:
                if (!noId)
                {
                    (reason, code) = (NifModelAnimationReasons.ControllerIdUninterpretable,
                        NifModelAnimationReasons.ControllerIdUninterpretableCode);
                    return false;
                }

                kind = controllerType switch
                {
                    NifModelPropertyController.AlphaControllerType => ScenePropertyKind.MaterialAlpha,
                    NifModelPropertyController.EmittanceMultControllerType => ScenePropertyKind.MaterialEmissiveStrength,
                    _ => ScenePropertyKind.NodeVisibility
                };
                return true;
            case NifModelPropertyController.MaterialColorControllerType:
                if (noId || !NifModelPropertyController.TryParseMaterialColorId(id, out kind, out var targetColor))
                {
                    (reason, code) = (NifModelAnimationReasons.ControllerIdUninterpretable,
                        NifModelAnimationReasons.ControllerIdUninterpretableCode);
                    return false;
                }

                if (controller != NoRef)
                {
                    if (!_source.TryReadMaterialColorController(controller, out var colorView))
                    {
                        (reason, code) = (NifModelAnimationReasons.ControllerFieldsUnreadable,
                            NifModelAnimationReasons.ControllerFieldsUnreadableCode);
                        return false;
                    }

                    if (colorView.TargetColor != targetColor)
                    {
                        (reason, code) = (NifModelAnimationReasons.ControllerIdDisagrees,
                            NifModelAnimationReasons.ControllerIdDisagreesCode);
                        return false;
                    }
                }

                return true;
            case NifModelPropertyController.TextureTransformControllerType:
                if (noId || !NifModelPropertyController.TryParseTextureTransformId(id, out var shaderMap,
                        out var textureSlot, out var member))
                {
                    (reason, code) = (NifModelAnimationReasons.ControllerIdUninterpretable,
                        NifModelAnimationReasons.ControllerIdUninterpretableCode);
                    return false;
                }

                if (controller != NoRef)
                {
                    if (!_source.TryReadTextureTransformController(controller, out var transformView))
                    {
                        (reason, code) = (NifModelAnimationReasons.ControllerFieldsUnreadable,
                            NifModelAnimationReasons.ControllerFieldsUnreadableCode);
                        return false;
                    }

                    if (transformView.ShaderMapByte != shaderMap || transformView.TextureSlot != textureSlot ||
                        transformView.Operation != member)
                    {
                        (reason, code) = (NifModelAnimationReasons.ControllerIdDisagrees,
                            NifModelAnimationReasons.ControllerIdDisagreesCode);
                        return false;
                    }
                }

                return TryTextureTransformKind(shaderMap, textureSlot, member, out kind, out slot, out operation,
                    out reason, out code);
            default:
                (reason, code) = (NifModelAnimationReasons.NotTransformBlock,
                    NifModelAnimationReasons.NotTransformBlockCode);
                return false;
        }
    }

    /// <summary>The kind an embedded controller's own fields name.</summary>
    private bool TryResolveKindFromBlock(int controller, string type, out ScenePropertyKind kind, out string? slot,
        out uint? operation, out string reason, out string code)
    {
        kind = default;
        slot = null;
        operation = null;
        reason = string.Empty;
        code = string.Empty;
        switch (type)
        {
            case NifModelPropertyController.AlphaControllerType:
                kind = ScenePropertyKind.MaterialAlpha;
                return true;
            case NifModelPropertyController.EmittanceMultControllerType:
                kind = ScenePropertyKind.MaterialEmissiveStrength;
                return true;
            case NifModelPropertyController.MaterialColorControllerType:
                if (!_source.TryReadMaterialColorController(controller, out var colorView))
                {
                    (reason, code) = (NifModelAnimationReasons.ControllerFieldsUnreadable,
                        NifModelAnimationReasons.ControllerFieldsUnreadableCode);
                    return false;
                }

                if (!NifModelPropertyController.TryMaterialColorKind(colorView.TargetColor, out kind))
                {
                    (reason, code) = (NifModelAnimationReasons.MaterialColorUndefined,
                        NifModelAnimationReasons.MaterialColorUndefinedCode);
                    return false;
                }

                return true;
            case NifModelPropertyController.TextureTransformControllerType:
                if (!_source.TryReadTextureTransformController(controller, out var transformView))
                {
                    (reason, code) = (NifModelAnimationReasons.ControllerFieldsUnreadable,
                        NifModelAnimationReasons.ControllerFieldsUnreadableCode);
                    return false;
                }

                return TryTextureTransformKind(transformView.ShaderMapByte, transformView.TextureSlot,
                    transformView.Operation, out kind, out slot, out operation, out reason, out code);
            default:
                (reason, code) = (NifModelAnimationReasons.NotTransformBlock,
                    NifModelAnimationReasons.NotTransformBlockCode);
                return false;
        }
    }

    /// <summary>The layer kind and map slot of an NiTextureTransformController's fields (fail closed outside nif.xml's tables).</summary>
    private static bool TryTextureTransformKind(byte shaderMap, uint textureSlot, uint member, out ScenePropertyKind kind,
        out string? slot, out uint? operation, out string reason, out string code)
    {
        kind = default;
        slot = null;
        operation = null;
        reason = string.Empty;
        code = string.Empty;
        if (shaderMap != 0)
        {
            (reason, code) = (NifModelAnimationReasons.ShaderMapSlot, NifModelAnimationReasons.ShaderMapSlotCode);
            return false;
        }

        if (!NifModelPropertyController.TrySlotName(textureSlot, out var name))
        {
            (reason, code) = (NifModelAnimationReasons.TextureSlotUndefined,
                NifModelAnimationReasons.TextureSlotUndefinedCode);
            return false;
        }

        if (textureSlot == NifModelPropertyController.BumpMapSlot)
        {
            (reason, code) = (NifModelAnimationReasons.TextureSlotNotLayer, NifModelAnimationReasons.TextureSlotNotLayerCode);
            return false;
        }

        if (!NifModelPropertyController.TryOperationKind(member, out kind))
        {
            (reason, code) = (NifModelAnimationReasons.OperationUndefined, NifModelAnimationReasons.OperationUndefinedCode);
            return false;
        }

        slot = name;
        operation = member;
        return true;
    }

    /// <summary>The document targets of one binding: node occurrences, fed materials, or fed materials' layer ordinals under the member gate.</summary>
    private bool TryResolveTargets(in Binding binding, out List<(ScenePropertyTarget Target, int Node)> targets,
        out string reason, out string code)
    {
        targets = [];
        reason = string.Empty;
        code = string.Empty;
        if (binding.Kind == ScenePropertyKind.NodeVisibility)
        {
            foreach (var node in binding.Occurrences)
            {
                targets.Add((new ScenePropertyTarget(ScenePropertyKind.NodeVisibility, node), node));
            }

            return targets.Count > 0;
        }

        var materials = _targets.MaterialsFedBy(binding.PropertyBlock);
        if (materials.Count == 0)
        {
            (reason, code) = (NifModelAnimationReasons.PropertyNotFed, NifModelAnimationReasons.PropertyNotFedCode);
            return false;
        }

        if (!NifModelPropertyController.IsLayerKind(binding.Kind))
        {
            foreach (var material in materials)
            {
                targets.Add((new ScenePropertyTarget(binding.Kind, material), NoRef));
            }

            return true;
        }

        if (!TryReadMap(binding.PropertyBlock, binding.Slot!, out var map, out var mapReason, out var mapCode))
        {
            (reason, code) = (mapReason!, mapCode!);
            return false;
        }

        if (!NifModelTextureTransformMember.TryMap(map, binding.Operation!.Value, out var kind, out var gate))
        {
            (reason, code) = (gate!, NifModelAnimationReasons.TextureTransformMemberCode);
            return false;
        }

        foreach (var material in materials)
        {
            if (_targets.TryGetLayerOrdinal(material, binding.PropertyBlock, binding.Slot!, out var ordinal))
            {
                targets.Add((new ScenePropertyTarget(kind, material, ordinal), NoRef));
            }
        }

        if (targets.Count == 0)
        {
            (reason, code) = (NifModelAnimationReasons.TextureSlotNotLayer, NifModelAnimationReasons.TextureSlotNotLayerCode);
            return false;
        }

        return true;
    }

    /// <summary>One map of a completely decoded NiTexturingProperty, by the slot name the cut-1a view gives it.</summary>
    private bool TryReadMap(int propertyBlock, string slot, out NifTextureMapView map, out string? reason,
        out string? code)
    {
        map = null!;
        reason = null;
        code = null;
        var block = _source.State.Blocks[propertyBlock];
        if (!_source.Inherits(propertyBlock, NifModelPropertyController.TexturingPropertyType) || !block.IsComplete)
        {
            (reason, code) = (NifModelAnimationReasons.TexturingPropertyIncomplete,
                NifModelAnimationReasons.TexturingPropertyIncompleteCode);
            return false;
        }

        foreach (var candidate in NifTexturingPropertyView.Read(block).Maps)
        {
            if (string.Equals(candidate.Slot, slot, StringComparison.Ordinal))
            {
                map = candidate;
                return true;
            }
        }

        (reason, code) = (NifModelAnimationReasons.TextureSlotNotLayer, NifModelAnimationReasons.TextureSlotNotLayerCode);
        return false;
    }

    /// <summary>The curve of one binding's interpolator, at the kind's width.</summary>
    private NifModelPropertyCurveResult MapCurve(ScenePropertyKind kind, int interpolator)
    {
        if (kind == ScenePropertyKind.NodeVisibility)
        {
            return NifModelPropertyCurveMapping.MapVisibility(_source, interpolator);
        }

        return NifModelPropertyController.Width(kind) == NifModelPropertyCurveMapping.ColorWidth
            ? NifModelPropertyCurveMapping.MapColor(_source, interpolator)
            : NifModelPropertyCurveMapping.MapScalar(_source, interpolator);
    }

    /// <summary>The first property of exactly the given type in a block's own Properties list, or -1.</summary>
    private int FindOwnProperty(int block, ReadOnlySpan<byte> propertyType)
    {
        if (!_source.IsBlock(block) || !_source.State.Blocks[block].Root.TryGet("Properties", out var value) ||
            value is not NifArrayValue array)
        {
            return NoRef;
        }

        var type = System.Text.Encoding.Latin1.GetString(propertyType);
        foreach (var item in array.Items)
        {
            if (item is NifRefValue { IsNone: false } link && _source.Is(link.Index, type))
            {
                return link.Index;
            }
        }

        return NoRef;
    }

    /// <summary>
    ///     The first controller on a block's controller chain of exactly the given type whose engine-formatted ID equals
    ///     the stored ID (a NULL ID equals no ID), or -1.
    /// </summary>
    private int FindOnChain(int block, string controllerType, ReadOnlySpan<byte> id, bool noId)
    {
        var visited = new HashSet<int>();
        var next = _source.Link(block, "Controller");
        while (next is { } controller && visited.Add(controller))
        {
            if (!_source.TryReadControllerHeader(controller, out var header))
            {
                break;
            }

            if (_source.Is(controller, controllerType) && IdMatches(controller, controllerType, id, noId))
            {
                return controller;
            }

            next = _source.IsBlock(header.NextControllerRef) ? header.NextControllerRef : null;
        }

        return NoRef;
    }

    /// <summary>Whether a controller's engine-formatted ID equals a stored ID.</summary>
    private bool IdMatches(int controller, string controllerType, ReadOnlySpan<byte> id, bool noId)
    {
        switch (controllerType)
        {
            case NifModelPropertyController.MaterialColorControllerType:
                return !noId && _source.TryReadMaterialColorController(controller, out var colorView) &&
                       colorView.TargetColor < 4 &&
                       id.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(
                           NifModelPropertyController.MaterialColorId(colorView.TargetColor)));
            case NifModelPropertyController.TextureTransformControllerType:
                return !noId && _source.TryReadTextureTransformController(controller, out var transformView) &&
                       transformView.Operation < 5 &&
                       id.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(NifModelPropertyController.TextureTransformId(
                           transformView.ShaderMapByte, transformView.TextureSlot, transformView.Operation)));
            default:
                return noId;
        }
    }

    /// <summary>Applies the repeated target rule and emits the surviving tracks, sources and decisions.</summary>
    private static void Emit(List<Emission> emissions, List<NifModelAnimationDisposition> decisions,
        List<ScenePropertyTrack> tracks, List<NifModelPropertyTrackSource> sources, string repeatedReason,
        string repeatedCode)
    {
        var counts = new Dictionary<ScenePropertyTarget, int>();
        foreach (var emission in emissions)
        {
            counts[emission.Target] = counts.GetValueOrDefault(emission.Target) + 1;
        }

        var typedBlocks = new HashSet<(int Source, int Ordinal, int Block)>();
        foreach (var emission in emissions)
        {
            if (counts[emission.Target] > 1)
            {
                AddNative(decisions, emission.Curve.Blocks, repeatedReason, repeatedCode, emission.Source,
                    emission.Ordinal);
                continue;
            }

            tracks.Add(emission.ToTrack());
            sources.Add(emission.ToSource());
            foreach (var block in emission.Curve.Blocks)
            {
                if (typedBlocks.Add((emission.Source, emission.Ordinal, block)))
                {
                    decisions.Add(Typed(block, emission.Source, emission.Ordinal));
                }
            }
        }
    }

    /// <summary>The blocks a controlled block's native decision covers: its interpolator and data, or the sequence itself.</summary>
    private IReadOnlyList<int> BindingBlocks(int sequence, int interpolator)
    {
        var blocks = _source.InterpolatorBlocks(interpolator);
        return blocks.Count == 0 ? new[] { sequence } : blocks;
    }

    private void ControllerNative(int controller, IReadOnlyList<int> blocks,
        List<NifModelAnimationDisposition> decisions, NifModelCurveBlock block)
    {
        var reason = NifModelAnimationReasons.Reason(block);
        var code = NifModelAnimationReasons.Code(block);
        decisions.Add(Native(controller, reason, code, controller, NoOrdinal));
        AddNative(decisions, blocks, reason, code, controller, NoOrdinal);
    }

    private static void AddNative(List<NifModelAnimationDisposition> decisions, IEnumerable<int> blocks, string reason,
        string code, int source, int ordinal)
    {
        foreach (var block in blocks)
        {
            decisions.Add(Native(block, reason, code, source, ordinal));
        }
    }

    private static NifModelAnimationDisposition Native(int block, string reason, string code, int source, int ordinal)
    {
        return new NifModelAnimationDisposition(block, NifModelBlockDisposition.NativeOnly(reason), code, source,
            ordinal, null);
    }

    private static NifModelAnimationDisposition Typed(int block, int source, int ordinal)
    {
        return new NifModelAnimationDisposition(block, NifModelBlockDisposition.Typed,
            NifModelAnimationReasons.TypedCode, source, ordinal, null);
    }

    /// <summary>One resolved binding: the kind, the property block (or -1 for visibility), the map slot and operation of a layer kind, the controller, and the node occurrences of a visibility binding.</summary>
    private readonly record struct Binding(
        ScenePropertyKind Kind,
        int PropertyBlock,
        string? Slot,
        uint? Operation,
        int Controller,
        IReadOnlyList<int> Occurrences);

    /// <summary>One track waiting for the repeated target rule.</summary>
    private sealed record Emission(
        int Source,
        int Ordinal,
        int Controller,
        NifModelPropertyCurveResult Curve,
        ScenePropertyTarget Target,
        int Node,
        int PropertyBlock,
        SceneAnimationClock? Clock,
        SceneAnimationTrackSourcePolicy? Policy)
    {
        public ScenePropertyTrack ToTrack()
        {
            return new ScenePropertyTrack(Target, Curve.Curve!, Clock, Policy);
        }

        public NifModelPropertyTrackSource ToSource()
        {
            var entrySource = Ordinal == NoOrdinal ? Source : Ordinal;
            return new NifModelPropertyTrackSource(entrySource, Curve.InterpolatorBlock, Target.Kind, Target.Index,
                Target.LayerIndex, Node, PropertyBlock, Controller, Curve.KeyType);
        }
    }

    /// <summary>One embedded controller that qualified, waiting for the clip-level repeated target rule.</summary>
    private sealed record Candidate(int Controller, NifTimeControllerHeader Header, int Interpolator,
        List<Emission> Emissions);
}
