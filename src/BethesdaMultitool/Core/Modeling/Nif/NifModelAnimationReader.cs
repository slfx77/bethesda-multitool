using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slices 4, 6, 7 and 14 (plan sections 1.6 and 1.8, owner rulings D6, D7 and D11, RE-21, RE-22 and RE-23,
///     SA4): assembles the slice 1, 2, 3, 5, 7 and 14 components into Shared clips. One clip per NiControllerSequence,
///     and for a <c>.nif</c> one <c>(controllers)</c> clip for the free-running NiTransformControllers,
///     NiGeomMorpherControllers and (slice 14) property and visibility controllers. Since the switch-over (slice 10)
///     <see cref="NifModelReader" /> calls it on every read: <see cref="ReadNif(NifModelReadState, NifModelNodeGraph, NifModelPropertyTargets, NifPackedPlatformSelection, CancellationToken)" />
///     on a <c>.nif</c>'s placed graph with the cut-1a targets, and
///     <see cref="ReadKf(NifModelReadState, NifModelNodeGraph, NifPackedPlatformSelection, CancellationToken)" /> on a
///     <c>.kf</c> bound to its resolved skeleton (<see cref="NifModelAnimationStreamReader" />). Component tests still call
///     it directly.
/// </summary>
/// <remarks>
///     <para>Sequence clips (slice 4):</para>
///     <list type="bullet">
///         <item>
///             Order: each NiControllerManager's Controller Sequences list (managers in block order) for a <c>.nif</c>,
///             the footer root order for a <c>.kf</c>. A sequence listed twice is read once, at its first listing; a
///             <c>.nif</c> sequence no manager lists is not read (slice 8 classifies it).
///         </item>
///         <item>Name: the Latin-1 text of the stored Name bytes (empty for the NULL string).</item>
///         <item>
///             Tracks: every controlled block whose Controller Type is exactly the bytes <c>NiTransformController</c>
///             (the engine's own test) maps through slice 2 (<see cref="NifModelInterpolatorChannels" />). Its target binds
///             through slice 3 (<see cref="NifModelTargetNames" />: palette first, then node names, exact bytes), and a
///             target block with several occurrences gives one track per occurrence at that occurrence's node index,
///             occurrence-major, then translation, rotation, scale. A refused channel stays native with its reason and
///             the clip keeps the other tracks; a target that does not bind keeps its interpolator native with the slice-3
///             reason. Every mapped channel becomes a track, NotDriven and Constant included.
///         </item>
///         <item>
///             Clock: slice 2's sequence clock (RE-22 rule 2), on the clip; tracks carry no clock and are not gated on
///             their controller's active bit. An inactive NiControllerManager, or an inactive
///             NiMultiTargetTransformController on its target's controller chain, keeps every sequence it drives native.
///         </item>
///         <item>Events: slice 5 on the sequence's NiTextKeyExtraData, in file order.</item>
///         <item>
///             Rotation forms (slices 13 and 16b): an XYZ-Euler rotation becomes a <see cref="SceneEulerRotationTrack" />
///             in the clip's EulerRotationTracks, the node's only rotation driver (no quaternion Rotation track is
///             emitted for it), and is refused when the effective clock (the clip clock here, the track clock in the
///             <c>(controllers)</c> clip) could sample below the first key of a multi-key axis (RE-20 rule 7). A TBC or
///             QUADRATIC quaternion rotation becomes a Squad track under the platform policy the read resolved the way
///             the cut-1a packed-geometry path does (<see cref="NifPackedPlatformSelection" />,
///             <see cref="NifModelSquadPolicy" />): a little-endian file is PC (PcFloat32), a big-endian one X360
///             (Xbox360Estimate, which Shared samples at the unit-normalization centre under a certified hardware-estimate term) or PS3 (refused).
///         </item>
///         <item>
///             Source policy: slice 2 (<see cref="NifModelSourcePolicyMapping" />): the controlled block's Priority per
///             track, the Weight and Accum Root Name per clip, provenance Authored. The accumulation root is bound to a
///             node only when its name binds to exactly one occurrence.
///         </item>
///         <item><c>DurationSeconds</c> stays null; the timing says the times are float seconds and invents no rate.</item>
///         <item>
///             D11: the sequence fields (weight, accumulation root, manager ref, text-key ref, each controlled block's
///             priority and five strings) are copied into the clip's extras (<see cref="NifModelAnimationExtras" />) and
///             recorded as a NativeOnly decision on the sequence block (<see cref="NifModelAnimationReasons.SequenceNativeFields" />).
///         </item>
///         <item>
///             RE-21 (settled; it supersedes D7's 'native until RE-21'): NiTransformController controlled blocks that
///             bind one target block collapse to the lowest controlled-block index when their content is identical (the
///             same interpolator block, or interpolators whose mapped channels are bit-identical with none blocked). Each
///             dropped block stays native with <see cref="NifModelAnimationReasons.Collapsed" />, which records the kept
///             index and the multiplicity. Differing content or differing priorities keep the WHOLE sequence native
///             (<see cref="NifModelAnimationReasons.RepeatedDifferingContent" />,
///             <see cref="NifModelAnimationReasons.RepeatedDifferingPriority" />). Bindings whose name does not resolve
///             produce no track and are not grouped. A final guard still refuses tracks that would repeat a (node,
///             property) (<see cref="NifModelAnimationReasons.RepeatedTarget" />), so no clip reaches Shared validation
///             with a repeat and no duplicate is dropped without a recorded decision.
///         </item>
///         <item>
///             Morph weights (slice 7): a controlled block whose Controller Type is exactly <c>NiGeomMorpherController</c>
///             binds its Node Name like a transform block, then its Interpolator ID to the first Frame Name of the target's
///             first morpher's NiMorphData (exact bytes, RE-23 rule 5). The Base (morph 0) stays native with the RE-23
///             reason; two blocks binding one morph both stay native (RE-21 step 7); the others map through
///             <see cref="NifModelAnimationMorphs" /> and <see cref="NifModelMorphTracks" /> into one whole-vector track or
///             per-target tracks per occurrence, with the controlled block's Priority as each track's source policy.
///             Slice 10: when the targets know the document's meshes (<see cref="NifModelPropertyTargets.KnowsMeshes" />),
///             a morph binding whose target occurrences do not all draw a mesh carrying exactly the morph data's targets
///             stays native (<see cref="NifModelAnimationReasons.MorphTargetsNotTyped" />): the cut-1a readers did not type
///             those targets, and Shared would refuse the channel. A <c>.kf</c> document has no mesh, so none binds there.
///         </item>
///         <item>
///             A sequence that is admitted becomes a clip even when every one of its channels stays native: the clip then
///             carries its clock, events and policies, and each missing channel is a recorded decision.
///         </item>
///     </list>
///     <para>
///         Property and visibility tracks (slice 14, SA4): a controlled block whose Controller Type is NiAlphaController,
///         NiMaterialColorController, BSMaterialEmittanceMultController, NiTextureTransformController or
///         NiVisController binds through <see cref="NifModelPropertyTracks" /> (Node Name, then Property Type on the
///         target's own property list, then Controller Type and Controller ID on its controller chain) to Shared
///         <see cref="ScenePropertyTrack" />s: one per material the property block feeds (or per layer ordinal of the
///         texture slot, or per node occurrence for visibility), with no track clock. The targets come from the cut-1a
///         material result through <see cref="NifModelPropertyTargets" />; a read without one keeps every material track
///         native. Two blocks on one exact target both stay native (RE-21 step 7). A <c>.kf</c> binds none (D12).
///     </para>
///     <para>The <c>(controllers)</c> clip (slice 6, D6, RE-22 rules 1 and 3; slice 14 adds the property controllers):</para>
///     <list type="bullet">
///         <item>
///             The NiTransformControllers and NiGeomMorpherControllers in block order that are free-running (flags 0x20
///             clear and no controlled block references them) and active (flags 0x08 set), after the sequences, named
///             exactly <see cref="ControllersClipName" />, bound to their Target block's occurrences. A morpher binds only
///             when it is the first NiGeomMorpherController on its target's chain (the one the cut-1a reader types).
///         </item>
///         <item>
///             No clip clock; each track carries slice 2's free-running controller clock. A refused clock (inactive,
///             sentinel with a curve, one-sided sentinel, stop before start, cycle 3) keeps the controller native with its
///             reason; the double sentinel without a curve gives no clock and no track (RE-22 rule 3a).
///         </item>
///         <item>
///             Manager-controlled controllers stay native (<see cref="NifModelAnimationReasons.ManagerControlled" />):
///             they bind only through their sequences. Two free-running controllers that would repeat a (node, property)
///             both stay native.
///         </item>
///         <item>No clip at all when no track qualifies.</item>
///     </list>
///     <para>
///         Manager-side state stays native: every NiBlend*Interpolator (<see cref="NifModelAnimationReasons.BlendState" />)
///         and NiMultiTargetTransformController (<see cref="NifModelAnimationReasons.MultiTargetBinding" />).
///     </para>
///     <para>
///         Cut 2, the 20.0.0.4 <c>.kf</c> key: the source hands the same sequence view, its strings indexing the table it
///         synthesizes from the inline names and the String Palette entries (<see cref="NifModelAnimationSource" />), so
///         every rule above applies unchanged. Two additions: a controlled block that bound its target resolved that
///         target through its NiStringPalette, which is therefore Typed (the 20.2.0.7 object-palette rule, applied to the
///         palette the block names); and the clip extras record the stored palette refs and offsets
///         (<see cref="NifModelAnimationExtras" />), the synthesized indices being the reader's, not the file's.
///     </para>
/// </remarks>
internal sealed class NifModelAnimationReader
{
    /// <summary>The name of the clip that holds the free-running controllers.</summary>
    public const string ControllersClipName = "(controllers)";

    /// <summary>The evidence of every clip's timing (plan section 1.1).</summary>
    public const string TimingEvidence = "NIF key and clock times are float seconds";

    /// <summary>The Controller Type a transform controlled block names, and the embedded controller type slice 6 reads.</summary>
    public const string TransformControllerType = "NiTransformController";

    private const string SequenceType = "NiControllerSequence";
    private const string ManagerType = "NiControllerManager";
    private const string MultiTargetType = "NiMultiTargetTransformController";
    private const string BlendInterpolatorType = "NiBlendInterpolator";
    private const string TextKeysType = "NiTextKeyExtraData";
    private const int NoRef = -1;
    private const int NoOrdinal = -1;

    private static readonly byte[] TransformControllerBytes = Encoding.ASCII.GetBytes(TransformControllerType);
    private static readonly byte[] MorpherControllerBytes = Encoding.ASCII.GetBytes(NifModelAnimationMorphs.MorpherControllerType);

    private static readonly IReadOnlyDictionary<string, string> NoAppOptions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly NifModelAnimationSource _source;
    private readonly NifModelSquadPolicy _squad;
    private readonly NifModelPropertyTargets _targets;
    private readonly NifModelPropertyTracks _properties;
    private readonly CancellationToken _cancellationToken;
    private readonly List<SceneAnimation> _clips = [];
    private readonly List<NifModelAnimationDisposition> _decisions = [];
    private readonly HashSet<int> _readSequences = [];

    private NifModelAnimationReader(NifModelReadState state, NifPackedPlatformSelection platform,
        NifModelPropertyTargets targets, bool bindsPropertiesInFile, CancellationToken cancellationToken)
    {
        _source = new NifModelAnimationSource(state);
        _squad = NifModelSquadPolicy.Resolve(state.Info.IsBigEndian, platform);
        _targets = targets;
        _properties = new NifModelPropertyTracks(_source, targets, bindsPropertiesInFile, cancellationToken);
        _cancellationToken = cancellationToken;
    }

    /// <summary>The timing every clip carries: authored float seconds, no frame rate invented.</summary>
    public static SceneAnimationTiming Timing { get; } =
        new(null, null, null, SceneValueProvenance.Authored, TimingEvidence);

    /// <summary>
    ///     Reads a <c>.nif</c>'s clips under the platform a read with no <see cref="BethesdaModelRegistration.PlatformOption" />
    ///     resolves (X360 assumed for a big-endian file, which matters only to a Squad rotation; a little-endian file is
    ///     PC regardless).
    /// </summary>
    /// <param name="state">The read state, as NifModelReader builds it.</param>
    /// <param name="graph">The file's node graph (<see cref="NifModelNodeReader.Read" />).</param>
    /// <param name="cancellationToken">Observed per block, sequence and controlled block.</param>
    /// <returns>The clips and every decision.</returns>
    /// <exception cref="InvalidDataException">A transform static value is malformed (see <see cref="NifModelInterpolatorChannels.Map" />).</exception>
    public static NifModelAnimationResult ReadNif(NifModelReadState state, NifModelNodeGraph graph,
        CancellationToken cancellationToken)
    {
        return ReadNif(state, graph, NifModelPropertyTargets.None, DefaultPlatform(), cancellationToken);
    }

    /// <summary>
    ///     Reads a <c>.nif</c>'s clips without a material stage (see
    ///     <see cref="ReadNif(NifModelReadState, NifModelNodeGraph, NifModelPropertyTargets, NifPackedPlatformSelection, CancellationToken)" />):
    ///     material and layer property tracks then find no document material and stay native; visibility tracks bind.
    /// </summary>
    /// <param name="state">The read state, as NifModelReader builds it.</param>
    /// <param name="graph">The file's node graph (<see cref="NifModelNodeReader.Read" />).</param>
    /// <param name="platform">The platform the read resolved (<see cref="NifPackedPlatformOption.Resolve" />), for Squad rotations of a big-endian file.</param>
    /// <param name="cancellationToken">Observed per block, sequence and controlled block.</param>
    /// <returns>The clips and every decision.</returns>
    /// <exception cref="InvalidDataException">A transform static value is malformed (see <see cref="NifModelInterpolatorChannels.Map" />).</exception>
    public static NifModelAnimationResult ReadNif(NifModelReadState state, NifModelNodeGraph graph,
        NifPackedPlatformSelection platform, CancellationToken cancellationToken)
    {
        return ReadNif(state, graph, NifModelPropertyTargets.None, platform, cancellationToken);
    }

    /// <summary>
    ///     Reads a <c>.nif</c>'s clips: the manager sequences, then the <c>(controllers)</c> clip, with the property and
    ///     visibility tracks of slice 14 bound to the cut-1a materials the targets describe.
    /// </summary>
    /// <param name="state">The read state, as NifModelReader builds it.</param>
    /// <param name="graph">The file's node graph (<see cref="NifModelNodeReader.Read" />).</param>
    /// <param name="targets">The document's material targets (<see cref="NifModelPropertyTargets.FromResults" />).</param>
    /// <param name="platform">The platform the read resolved (<see cref="NifPackedPlatformOption.Resolve" />), for Squad rotations of a big-endian file.</param>
    /// <param name="cancellationToken">Observed per block, sequence and controlled block.</param>
    /// <returns>The clips and every decision.</returns>
    /// <exception cref="InvalidDataException">A transform static value is malformed (see <see cref="NifModelInterpolatorChannels.Map" />).</exception>
    public static NifModelAnimationResult ReadNif(NifModelReadState state, NifModelNodeGraph graph,
        NifModelPropertyTargets targets, NifPackedPlatformSelection platform, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(platform);
        var reader = new NifModelAnimationReader(state, platform, targets, true, cancellationToken);
        for (var block = 0; block < reader._source.BlockCount; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader._source.Inherits(block, ManagerType))
            {
                reader.ReadManager(block, graph);
            }
        }

        reader.ReadControllers(graph);
        reader.ClassifyManagerSideState();
        return new NifModelAnimationResult(reader._clips.AsReadOnly(), reader._decisions.AsReadOnly());
    }

    /// <summary>
    ///     Reads a <c>.kf</c>'s clips under the platform a read with no <see cref="BethesdaModelRegistration.PlatformOption" />
    ///     resolves (see <see cref="ReadNif(NifModelReadState, NifModelNodeGraph, CancellationToken)" />).
    /// </summary>
    /// <param name="state">The <c>.kf</c>'s read state (parsed and decoded; a <c>.kf</c> has no scene graph of its own).</param>
    /// <param name="skeleton">The resolved skeleton's node graph.</param>
    /// <param name="cancellationToken">Observed per sequence and controlled block.</param>
    /// <returns>The clips and every decision.</returns>
    /// <exception cref="InvalidDataException">A transform static value is malformed (see <see cref="NifModelInterpolatorChannels.Map" />).</exception>
    public static NifModelAnimationResult ReadKf(NifModelReadState state, NifModelNodeGraph skeleton,
        CancellationToken cancellationToken)
    {
        return ReadKf(state, skeleton, DefaultPlatform(), cancellationToken);
    }

    /// <summary>
    ///     Reads a <c>.kf</c>'s clips: its footer-root sequences in footer order, bound within the resolved skeleton
    ///     (slice 3's resolver output). Track node indices are the skeleton graph's. The targets are
    ///     <see cref="NifModelPropertyTargets.SkeletonOnly" /> (D12: the document holds the skeleton's nodes and no mesh or
    ///     material), so no property or morph channel binds.
    /// </summary>
    /// <param name="state">The <c>.kf</c>'s read state (parsed and decoded; a <c>.kf</c> has no scene graph of its own).</param>
    /// <param name="skeleton">The resolved skeleton's node graph.</param>
    /// <param name="platform">The platform the read resolved (<see cref="NifPackedPlatformOption.Resolve" />), for Squad rotations of a big-endian file.</param>
    /// <param name="cancellationToken">Observed per sequence and controlled block.</param>
    /// <returns>The clips and every decision.</returns>
    /// <exception cref="InvalidDataException">A transform static value is malformed (see <see cref="NifModelInterpolatorChannels.Map" />).</exception>
    public static NifModelAnimationResult ReadKf(NifModelReadState state, NifModelNodeGraph skeleton,
        NifPackedPlatformSelection platform, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(platform);
        var reader = new NifModelAnimationReader(state, platform, NifModelPropertyTargets.SkeletonOnly, false,
            cancellationToken);
        var targets = NifModelTargetNames.ForSkeleton(skeleton);
        foreach (var root in state.Footer.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader._source.Is(root, SequenceType) && reader._readSequences.Add(root))
            {
                reader.ReadSequence(root, targets, true, null);
            }
        }

        return new NifModelAnimationResult(reader._clips.AsReadOnly(), reader._decisions.AsReadOnly());
    }

    /// <summary>The platform a read with no <see cref="BethesdaModelRegistration.PlatformOption" /> resolves (X360 assumed).</summary>
    private static NifPackedPlatformSelection DefaultPlatform()
    {
        return NifPackedPlatformOption.Resolve(NoAppOptions);
    }

    /// <summary>Reads one NiControllerManager's sequences in list order.</summary>
    private void ReadManager(int manager, NifModelNodeGraph graph)
    {
        if (!_source.TryReadControllerHeader(manager, out var header))
        {
            Decide(manager, NifModelAnimationReasons.ControllerUnreadable,
                NifModelAnimationReasons.ControllerUnreadableCode, manager);
            return;
        }

        if (!_source.TryReadRefs(manager, "Controller Sequences", out var sequences))
        {
            Decide(manager, NifModelAnimationReasons.SequenceListUnreadable,
                NifModelAnimationReasons.SequenceListUnreadableCode, manager);
            return;
        }

        var targets = NifModelTargetNames.ForFile(_source.State, manager, graph);
        var driversActive = header.IsActive && MultiTargetControllersActive(header.TargetRef);
        var palette = _source.Link(manager, "Object Palette");
        var typed = false;
        foreach (var sequence in sequences)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_source.Is(sequence, SequenceType))
            {
                Decide(manager, NifModelAnimationReasons.SequenceListEntry,
                    NifModelAnimationReasons.SequenceListEntryCode, manager);
                continue;
            }

            if (_readSequences.Add(sequence))
            {
                typed |= ReadSequence(sequence, targets, driversActive, palette);
            }
        }

        if (typed)
        {
            _decisions.Add(Typed(manager, manager, NoOrdinal, null));
        }
        else
        {
            Decide(manager, NifModelAnimationReasons.ManagerNoClip, NifModelAnimationReasons.ManagerNoClipCode,
                manager);
        }
    }

    /// <summary>
    ///     RE-22 rule 1's exception: false when an NiMultiTargetTransformController on the manager target's controller
    ///     chain has its active bit clear.
    /// </summary>
    private bool MultiTargetControllersActive(int target)
    {
        var visited = new HashSet<int>();
        var next = _source.Link(target, "Controller");
        while (next is { } controller && visited.Add(controller))
        {
            if (!_source.TryReadControllerHeader(controller, out var header))
            {
                break;
            }

            if (_source.Is(controller, MultiTargetType) && !header.IsActive)
            {
                return false;
            }

            next = _source.IsBlock(header.NextControllerRef) ? header.NextControllerRef : null;
        }

        return true;
    }

    /// <summary>Reads one sequence; true when it became a clip.</summary>
    private bool ReadSequence(int sequence, NifModelTargetNames targets, bool driversActive, int? palette)
    {
        if (!_source.TryReadSequence(sequence, out var view))
        {
            SequenceNative(sequence, null, NifModelAnimationReasons.SequenceUnreadable,
                NifModelAnimationReasons.SequenceUnreadableCode);
            return false;
        }

        if (!view.TailExact)
        {
            SequenceNative(sequence, view, NifModelAnimationReasons.SequenceNotExact,
                NifModelAnimationReasons.SequenceNotExactCode);
            return false;
        }

        var strings = _source.Strings;
        _source.TryReadLegacySequence(sequence, out var legacy);
        if (!NifAnimationStrings.TryGetRaw(strings, view.NameIndex, out var nameBytes, out _))
        {
            SequenceNative(sequence, view, NifModelAnimationReasons.SequenceNameUnresolved,
                NifModelAnimationReasons.SequenceNameUnresolvedCode);
            return false;
        }

        var clock = NifModelClockMapping.MapSequence(view, driversActive);
        if (clock.IsBlocked)
        {
            SequenceNative(sequence, view, NifModelAnimationReasons.Reason(clock.Block),
                NifModelAnimationReasons.Code(clock.Block));
            return false;
        }

        if (!float.IsFinite(BitConverter.UInt32BitsToSingle(view.WeightBits)) ||
            !NifAnimationStrings.TryGetRaw(strings, view.AccumRootNameIndex, out var accumulationRoot,
                out var noAccumulationRoot))
        {
            SequenceNative(sequence, view, NifModelAnimationReasons.SequencePolicyUnreadable,
                NifModelAnimationReasons.SequencePolicyUnreadableCode);
            return false;
        }

        var pending = new List<NifModelAnimationDisposition>();
        var bindings = Bind(sequence, view, targets, pending);
        var transforms = bindings.Where(static binding => !binding.IsMorph && binding.PropertyControllerType is null)
            .ToList();
        if (!CollapseRepeats(sequence, transforms, pending, out var repeatReason, out var repeatCode))
        {
            SequenceNative(sequence, view, repeatReason, repeatCode);
            return false;
        }

        var tracks = new List<SceneTransformTrack>();
        var eulerTracks = new List<SceneEulerRotationTrack>();
        var sources = new List<NifModelTrackSource>();
        var eulerSources = new List<NifModelEulerTrackSource>();
        foreach (var (ordinal, block, match, _, _) in transforms)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!match.IsResolved)
            {
                AddNative(pending, BindingBlocks(sequence, block.InterpolatorRef), match.Reason!,
                    NifModelTargetNames.Code(match.Block), sequence, ordinal, null);
                continue;
            }

            if (!_source.IsBlock(block.InterpolatorRef))
            {
                AddNative(pending, [sequence], NifModelAnimationReasons.NoInterpolator,
                    NifModelAnimationReasons.NoInterpolatorCode, sequence, ordinal, null);
                continue;
            }

            if (match.Source == NifModelTargetSource.Palette && palette is { } paletteBlock)
            {
                pending.Add(Typed(paletteBlock, sequence, ordinal, null));
            }

            AddStringPaletteTyped(legacy, ordinal, sequence, pending);
            var channels = NifModelInterpolatorChannels.Map(_source, block.InterpolatorRef, _squad);
            AddTracks(channels, match.Occurrences, null, clock.Clock, NifModelSourcePolicyMapping.MapTrack(block),
                sequence, ordinal, tracks, eulerTracks, sources, eulerSources, pending);
        }

        if (RepeatsComponent(tracks, eulerTracks))
        {
            SequenceNative(sequence, view, NifModelAnimationReasons.RepeatedTarget,
                NifModelAnimationReasons.RepeatedTargetCode);
            return false;
        }

        var morphVectors = new List<SceneMorphTrack>();
        var morphTargets = new List<SceneMorphTargetTrack>();
        var morphSources = new List<NifModelMorphTrackSource>();
        ReadSequenceMorphs(sequence, bindings.Where(static binding => binding.IsMorph).ToList(), palette, pending,
            morphVectors, morphTargets, morphSources);

        var propertyTracks = new List<ScenePropertyTrack>();
        var propertySources = new List<NifModelPropertyTrackSource>();
        _properties.ReadSequenceBindings(sequence,
            bindings.Where(static binding => binding.PropertyControllerType is not null)
                .Select(static binding => (binding.Ordinal, binding.Block, binding.Match, binding.PropertyControllerType!))
                .ToList(),
            palette, pending, propertyTracks, propertySources);

        var events = ReadEvents(sequence, view, pending);
        int? accumulationNode = null;
        if (!noAccumulationRoot && targets.Match(accumulationRoot.Span) is { IsResolved: true } root &&
            root.Occurrences.Count == 1)
        {
            accumulationNode = root.Occurrences[0];
        }

        var clip = new SceneAnimation(
            Encoding.Latin1.GetString(nameBytes.Span),
            morphVectors,
            NifModelAnimationExtras.ForSequence(sequence, view, strings, sources, eulerSources, morphSources,
                propertySources, legacy),
            tracks,
            clock: clock.Clock,
            timing: Timing,
            events: events,
            sourcePolicy: NifModelSourcePolicyMapping.MapSequence(view, strings, accumulationNode),
            eulerRotationTracks: eulerTracks,
            propertyTracks: propertyTracks,
            morphTargetTracks: morphTargets);
        _clips.Add(clip);
        _decisions.Add(Typed(sequence, sequence, NoOrdinal, null));
        _decisions.Add(Native(sequence, NifModelAnimationReasons.SequenceNativeFields,
            NifModelAnimationReasons.SequenceNativeFieldsCode, sequence, NoOrdinal, null));
        _decisions.AddRange(pending);
        AddAnimNotes(sequence, view);
        return true;
    }

    /// <summary>
    ///     Selects the transform, morph and (slice 14) property controlled blocks and binds their targets; the other
    ///     controlled blocks get their pending native decision here. A property block carries its Controller Type text.
    /// </summary>
    private List<(int Ordinal, NifControlledBlockView Block, NifModelTargetMatch Match, bool IsMorph, string? PropertyControllerType)> Bind(
        int sequence, NifControllerSequenceView view, NifModelTargetNames targets,
        List<NifModelAnimationDisposition> pending)
    {
        var strings = _source.Strings;
        var bindings = new List<(int Ordinal, NifControlledBlockView Block, NifModelTargetMatch Match, bool IsMorph,
            string? PropertyControllerType)>();
        for (var ordinal = 0; ordinal < view.ControlledBlocks.Length; ordinal++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var block = view.ControlledBlocks[ordinal];
            if (!NifAnimationStrings.TryGetRaw(strings, block.ControllerTypeIndex, out var controllerType, out _))
            {
                AddNative(pending, BindingBlocks(sequence, block.InterpolatorRef),
                    NifModelAnimationReasons.ControllerTypeUnresolved,
                    NifModelAnimationReasons.ControllerTypeUnresolvedCode, sequence, ordinal, null);
                continue;
            }

            var isTransform = controllerType.Span.SequenceEqual(TransformControllerBytes);
            var isMorph = !isTransform && controllerType.Span.SequenceEqual(MorpherControllerBytes);
            string? propertyType = null;
            if (!isTransform && !isMorph)
            {
                var text = Encoding.Latin1.GetString(controllerType.Span);
                if (NifModelPropertyController.IsSequenceControllerType(text))
                {
                    propertyType = text;
                }
            }

            if (!isTransform && !isMorph && propertyType is null)
            {
                var covered = _source.IsBlock(block.InterpolatorRef)
                    ? new[] { block.InterpolatorRef }
                    : new[] { sequence };
                AddNative(pending, covered, NifModelAnimationReasons.NotTransformBlock,
                    NifModelAnimationReasons.NotTransformBlockCode, sequence, ordinal, null);
                continue;
            }

            bindings.Add((ordinal, block, targets.Match(block.NodeNameIndex, strings), isMorph, propertyType));
        }

        return bindings;
    }

    /// <summary>
    ///     The blocks a controlled block's native decision covers: its interpolator and the key data it resolves, or the
    ///     sequence itself when the Interpolator ref names no block.
    /// </summary>
    private IReadOnlyList<int> BindingBlocks(int sequence, int interpolator)
    {
        var blocks = _source.InterpolatorBlocks(interpolator);
        return blocks.Count == 0 ? new[] { sequence } : blocks;
    }

    /// <summary>
    ///     RE-21: groups the resolved bindings by target block. A group whose members share one priority and identical
    ///     content keeps its lowest controlled-block index; the others are removed from <paramref name="bindings" /> and
    ///     recorded native with the collapse reason. Returns false, with the reason, when a group differs in priority or
    ///     content, so the caller keeps the whole sequence native.
    /// </summary>
    private bool CollapseRepeats(int sequence,
        List<(int Ordinal, NifControlledBlockView Block, NifModelTargetMatch Match, bool IsMorph, string? PropertyControllerType)> bindings,
        List<NifModelAnimationDisposition> pending, out string reason, out string code)
    {
        reason = string.Empty;
        code = string.Empty;
        var groups = new Dictionary<int, List<int>>();
        for (var position = 0; position < bindings.Count; position++)
        {
            if (!bindings[position].Match.IsResolved)
            {
                continue;
            }

            var target = bindings[position].Match.TargetBlock;
            if (!groups.TryGetValue(target, out var positions))
            {
                positions = [];
                groups.Add(target, positions);
            }

            positions.Add(position);
        }

        var dropped = new List<int>();
        foreach (var positions in groups.Values)
        {
            if (positions.Count < 2)
            {
                continue;
            }

            // Bindings are in controlled-block order, so the first position is the lowest index.
            var kept = bindings[positions[0]];
            for (var member = 1; member < positions.Count; member++)
            {
                var other = bindings[positions[member]];
                if (other.Block.Priority != kept.Block.Priority)
                {
                    reason = NifModelAnimationReasons.RepeatedDifferingPriority;
                    code = NifModelAnimationReasons.RepeatedDifferingPriorityCode;
                    return false;
                }

                if (!SameContent(kept.Block.InterpolatorRef, other.Block.InterpolatorRef))
                {
                    reason = NifModelAnimationReasons.RepeatedDifferingContent;
                    code = NifModelAnimationReasons.RepeatedDifferingContentCode;
                    return false;
                }
            }

            for (var member = 1; member < positions.Count; member++)
            {
                var other = bindings[positions[member]];
                // A shared interpolator is typed through the kept block, so the collapse is recorded on the sequence.
                var covered = other.Block.InterpolatorRef == kept.Block.InterpolatorRef
                    ? new[] { sequence }
                    : BindingBlocks(sequence, other.Block.InterpolatorRef);
                AddNative(pending, covered, NifModelAnimationReasons.Collapsed(kept.Ordinal, positions.Count),
                    NifModelAnimationReasons.RepeatedCollapsedCode, sequence, other.Ordinal, null);
                dropped.Add(positions[member]);
            }
        }

        dropped.Sort();
        for (var index = dropped.Count - 1; index >= 0; index--)
        {
            bindings.RemoveAt(dropped[index]);
        }

        return true;
    }

    /// <summary>
    ///     RE-21 step 3: two interpolators are identical when they are one block, or when both map every channel (none
    ///     blocked, neither native) to bit-identical tracks (<see cref="NifModelTrackContent" />). Anything that cannot be
    ///     proven identical counts as different.
    /// </summary>
    private bool SameContent(int first, int second)
    {
        if (first == second)
        {
            return true;
        }

        if (!_source.IsBlock(first) || !_source.IsBlock(second))
        {
            return false;
        }

        var left = NifModelInterpolatorChannels.Map(_source, first, _squad);
        var right = NifModelInterpolatorChannels.Map(_source, second, _squad);
        if (left.IsNative || right.IsNative || left.Channels.Count != right.Channels.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Channels.Count; index++)
        {
            var (a, b) = (left.Channels[index], right.Channels[index]);
            if (a.IsBlocked || b.IsBlocked || a.IsEuler != b.IsEuler)
            {
                return false;
            }

            if (a.Euler is { } leftEuler && b.Euler is { } rightEuler)
            {
                if (!NifModelTrackContent.Equal(leftEuler.ToTrack(0), rightEuler.ToTrack(0)))
                {
                    return false;
                }

                continue;
            }

            if (a.Channel is null || b.Channel is null ||
                !NifModelTrackContent.Equal(a.Channel.ToTrack(0), b.Channel.ToTrack(0)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     The guard behind D7: true when two tracks drive one (node, property), which Shared rejects; an Euler track
    ///     drives its node's rotation (Shared counts it as the rotation driver).
    /// </summary>
    private static bool RepeatsComponent(IEnumerable<SceneTransformTrack> tracks,
        IEnumerable<SceneEulerRotationTrack> eulerTracks)
    {
        var seen = new HashSet<(int Node, SceneTransformProperty Property)>();
        foreach (var track in tracks)
        {
            if (!seen.Add((track.NodeIndex, track.Property)))
            {
                return true;
            }
        }

        foreach (var track in eulerTracks)
        {
            if (!seen.Add((track.NodeIndex, SceneTransformProperty.Rotation)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Adds one track per occurrence and mapped channel (occurrence-major, then translation, rotation, scale), an
    ///     Euler rotation as an Euler track instead of a quaternion one (slice 13), and the channel decisions for every
    ///     block the interpolator resolved. An Euler channel whose effective clock could sample below the first key of a
    ///     multi-key axis stays native (RE-20 rule 7); the other channels of the interpolator keep their tracks.
    /// </summary>
    /// <param name="channels">The interpolator's mapped channels.</param>
    /// <param name="occurrences">The target's node occurrences.</param>
    /// <param name="trackClock">The clock each track carries (a free-running controller's), or null.</param>
    /// <param name="effectiveClock">The clock that maps the tracks' time: the clip clock of a sequence, the track clock of a free-running controller.</param>
    /// <param name="policy">The controlled block's priority, or null.</param>
    /// <param name="source">The sequence or controller block.</param>
    /// <param name="ordinal">The controlled-block ordinal, or -1.</param>
    /// <param name="tracks">The clip's transform tracks.</param>
    /// <param name="eulerTracks">The clip's Euler rotation tracks.</param>
    /// <param name="sources">The extras' track map.</param>
    /// <param name="eulerSources">The extras' Euler track map.</param>
    /// <param name="pending">The decisions.</param>
    private void AddTracks(
        NifModelInterpolatorChannels channels,
        IReadOnlyList<int> occurrences,
        SceneAnimationClock? trackClock,
        SceneAnimationClock? effectiveClock,
        SceneAnimationTrackSourcePolicy? policy,
        int source,
        int ordinal,
        List<SceneTransformTrack> tracks,
        List<SceneEulerRotationTrack> eulerTracks,
        List<NifModelTrackSource> sources,
        List<NifModelEulerTrackSource> eulerSources,
        List<NifModelAnimationDisposition> pending)
    {
        var entrySource = ordinal == NoOrdinal ? source : ordinal;
        if (channels.IsNative)
        {
            AddNative(pending, channels.Blocks, channels.NativeReason!, channels.NativeCode!, source, ordinal, null);
            return;
        }

        var refusals = new NifModelCurveBlock[channels.Channels.Count];
        for (var i = 0; i < channels.Channels.Count; i++)
        {
            var property = NifModelInterpolatorChannels.Properties[i];
            var result = channels.Channels[i];
            refusals[i] = result.Block;
            if (!result.IsBlocked && result.Euler is { } euler && euler.SamplesBeforeFirstKey(effectiveClock))
            {
                refusals[i] = NifModelCurveBlock.EulerSampledBeforeFirstKey;
            }

            foreach (var block in channels.Blocks)
            {
                pending.Add(refusals[i] != NifModelCurveBlock.None
                    ? Native(block, NifModelAnimationReasons.Reason(refusals[i]),
                        NifModelAnimationReasons.Code(refusals[i]), source, ordinal, property)
                    : Typed(block, source, ordinal, property));
            }
        }

        foreach (var node in occurrences)
        {
            for (var i = 0; i < channels.Channels.Count; i++)
            {
                if (refusals[i] != NifModelCurveBlock.None)
                {
                    continue;
                }

                var result = channels.Channels[i];
                if (result.Euler is { } euler)
                {
                    eulerTracks.Add(euler.ToTrack(node, trackClock, policy));
                    eulerSources.Add(new NifModelEulerTrackSource(entrySource, channels.Interpolator, node,
                        euler.X.KeyType, euler.Y.KeyType, euler.Z.KeyType));
                    continue;
                }

                if (result.Channel is not { } channel)
                {
                    continue;
                }

                tracks.Add(channel.ToTrack(node, trackClock, policy));
                sources.Add(TrackSource(entrySource, channels.Interpolator, channel, node));
            }
        }
    }

    /// <summary>The extras entry of one transform track, naming the Squad policy when the curve is a Squad rotation.</summary>
    private NifModelTrackSource TrackSource(int source, int interpolator, NifModelTransformChannel channel, int node)
    {
        if (channel.Curve is { Interpolation: SceneInterpolation.GamebryoSquad, SquadPolicy: { } policy } curve)
        {
            return new NifModelTrackSource(source, interpolator, channel.Property, node, policy, curve.KeyType,
                _squad.Source);
        }

        return new NifModelTrackSource(source, interpolator, channel.Property, node);
    }

    /// <summary>
    ///     The first refusal among an interpolator's channels: a blocked channel's reason, else the RE-20 rule 7 guard of
    ///     an Euler channel the effective clock could sample below its first key.
    /// </summary>
    private static NifModelCurveBlock FirstRefusal(NifModelInterpolatorChannels channels,
        SceneAnimationClock? effectiveClock)
    {
        foreach (var result in channels.Channels)
        {
            if (result.IsBlocked)
            {
                return result.Block;
            }

            if (result.Euler is { } euler && euler.SamplesBeforeFirstKey(effectiveClock))
            {
                return NifModelCurveBlock.EulerSampledBeforeFirstKey;
            }
        }

        throw new InvalidOperationException("A mapped channel on a placed target always gives a track.");
    }

    /// <summary>
    ///     Slice 7, the sequence side: binds each morph controlled block to a morph of its target's first morpher by Frame
    ///     Name, keeps the Base and any repeated morph native, maps the rest, and emits the assembled tracks per
    ///     occurrence.
    /// </summary>
    private void ReadSequenceMorphs(
        int sequence,
        List<(int Ordinal, NifControlledBlockView Block, NifModelTargetMatch Match, bool IsMorph, string? PropertyControllerType)> bindings,
        int? palette,
        List<NifModelAnimationDisposition> pending,
        List<SceneMorphTrack> vectors,
        List<SceneMorphTargetTrack> targetTracks,
        List<NifModelMorphTrackSource> sources)
    {
        var strings = _source.Strings;
        var byTarget = new Dictionary<int, List<(int Ordinal, NifControlledBlockView Block, NifModelTargetMatch Match)>>();
        var order = new List<int>();
        foreach (var (ordinal, block, match, _, _) in bindings)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!match.IsResolved)
            {
                AddNative(pending, BindingBlocks(sequence, block.InterpolatorRef), match.Reason!,
                    NifModelTargetNames.Code(match.Block), sequence, ordinal, null);
                continue;
            }

            if (match.Source == NifModelTargetSource.Palette && palette is { } paletteBlock)
            {
                pending.Add(Typed(paletteBlock, sequence, ordinal, null));
            }

            _source.TryReadLegacySequence(sequence, out var legacyView);
            AddStringPaletteTyped(legacyView, ordinal, sequence, pending);
            if (!byTarget.TryGetValue(match.TargetBlock, out var group))
            {
                group = [];
                byTarget.Add(match.TargetBlock, group);
                order.Add(match.TargetBlock);
            }

            group.Add((ordinal, block, match));
        }

        foreach (var targetBlock in order)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var group = byTarget[targetBlock];
            if (NifModelAnimationMorphs.FirstMorpher(_source, targetBlock) is not { } morpher)
            {
                GroupNative(sequence, group, pending, NifModelAnimationReasons.MorphTargetNoMorpher,
                    NifModelAnimationReasons.MorphTargetNoMorpherCode);
                continue;
            }

            if (!_source.TryReadMorpherController(morpher, out var controllerView))
            {
                GroupNative(sequence, group, pending, NifModelAnimationReasons.MorpherUnreadable,
                    NifModelAnimationReasons.MorpherUnreadableCode);
                continue;
            }

            if (!NifModelAnimationMorphs.TryReadMorphData(_source, controllerView, out var data, out var reason,
                    out var code))
            {
                GroupNative(sequence, group, pending, reason, code);
                continue;
            }

            if (!_targets.AdmitsMorphChannels(group[0].Match.Occurrences, data.MorphCount - 1))
            {
                GroupNative(sequence, group, pending, NifModelAnimationReasons.MorphTargetsNotTyped,
                    NifModelAnimationReasons.MorphTargetsNotTypedCode);
                continue;
            }

            var slots = new List<(int Ordinal, NifControlledBlockView Block, int Morph)>();
            foreach (var (ordinal, block, _) in group)
            {
                if (!NifAnimationStrings.TryGetRaw(strings, block.InterpolatorIdIndex, out var frameName,
                        out var noFrameName) || noFrameName)
                {
                    AddNative(pending, BindingBlocks(sequence, block.InterpolatorRef),
                        NifModelAnimationReasons.MorphFrameUnresolved,
                        NifModelAnimationReasons.MorphFrameUnresolvedCode, sequence, ordinal, null);
                    continue;
                }

                var morph = NifModelAnimationMorphs.FrameIndex(data, strings, frameName.Span);
                if (morph < 0)
                {
                    AddNative(pending, BindingBlocks(sequence, block.InterpolatorRef),
                        NifModelAnimationReasons.MorphFrameNotFound, NifModelAnimationReasons.MorphFrameNotFoundCode,
                        sequence, ordinal, null);
                    continue;
                }

                slots.Add((ordinal, block, morph));
            }

            var counts = new Dictionary<int, int>();
            foreach (var slot in slots)
            {
                counts[slot.Morph] = counts.GetValueOrDefault(slot.Morph) + 1;
            }

            var results = new List<NifModelMorphTargetResult>();
            var policies = new List<SceneAnimationTrackSourcePolicy?>();
            var slotSources = new List<(int Ordinal, int Interpolator)>();
            foreach (var (ordinal, block, morph) in slots)
            {
                var blocks = BindingBlocks(sequence, block.InterpolatorRef);
                if (counts[morph] > 1)
                {
                    AddNative(pending, blocks, NifModelAnimationReasons.RepeatedMorphTarget,
                        NifModelAnimationReasons.RepeatedMorphTargetCode, sequence, ordinal, null);
                    continue;
                }

                if (morph == 0)
                {
                    AddNative(pending, blocks, NifModelAnimationReasons.BaseWeight,
                        NifModelAnimationReasons.BaseWeightCode, sequence, ordinal, null);
                    continue;
                }

                var result = NifModelAnimationMorphs.MapSlot(_source, morph, block.InterpolatorRef, null);
                RecordSlot(result, sequence, ordinal, pending);
                results.Add(result);
                policies.Add(NifModelSourcePolicyMapping.MapTrack(block));
                slotSources.Add((ordinal, result.InterpolatorBlock));
            }

            if (results.Count == 0)
            {
                continue;
            }

            var assembled = NifModelMorphTracks.Assemble(data.MorphCount, results, policies);
            EmitMorphTracks(assembled, results, slotSources, group[0].Match.Occurrences, null, vectors, targetTracks,
                sources);
        }
    }

    /// <summary>Keeps every binding of one target group native with one reason.</summary>
    private void GroupNative(int sequence,
        List<(int Ordinal, NifControlledBlockView Block, NifModelTargetMatch Match)> group,
        List<NifModelAnimationDisposition> pending, string reason, string code)
    {
        foreach (var (ordinal, block, _) in group)
        {
            AddNative(pending, BindingBlocks(sequence, block.InterpolatorRef), reason, code, sequence, ordinal, null);
        }
    }

    /// <summary>Records one mapped slot's decisions on the blocks it covers (or on the source when it covers none).</summary>
    private static void RecordSlot(NifModelMorphTargetResult result, int source, int ordinal,
        List<NifModelAnimationDisposition> pending)
    {
        var blocks = result.Blocks.Count == 0 ? new[] { source } : result.Blocks;
        if (result.IsTyped)
        {
            foreach (var block in blocks)
            {
                pending.Add(Typed(block, source, ordinal, null));
            }
        }
        else
        {
            AddNative(pending, blocks, result.NativeReason!, result.NativeCode!, source, ordinal, null);
        }
    }

    /// <summary>
    ///     Emits the assembled morph tracks on every occurrence of the target: the whole-vector track, or one per-target
    ///     track per typed slot, with their extras source entries.
    /// </summary>
    private static void EmitMorphTracks(
        NifModelMorphTracks assembled,
        IReadOnlyList<NifModelMorphTargetResult> results,
        IReadOnlyList<(int Source, int Interpolator)> slotSources,
        IReadOnlyList<int> occurrences,
        SceneAnimationClock? clock,
        List<SceneMorphTrack> vectors,
        List<SceneMorphTargetTrack> targetTracks,
        List<NifModelMorphTrackSource> sources)
    {
        if (!assembled.HasTracks)
        {
            return;
        }

        var sourceByMorph = new Dictionary<int, (int Source, int Interpolator)>();
        for (var index = 0; index < results.Count; index++)
        {
            sourceByMorph[results[index].MorphIndex] = slotSources[index];
        }

        foreach (var node in occurrences)
        {
            if (assembled.IsVector)
            {
                vectors.Add(assembled.VectorTrack(node, clock));
                var slots = assembled.Typed
                    .Select(slot => (Morph: slot.MorphIndex, sourceByMorph[slot.MorphIndex].Interpolator)).ToArray();
                sources.Add(new NifModelMorphTrackSource(sourceByMorph[assembled.Typed[0].MorphIndex].Source, node,
                    NifModelMorphTrackSource.VectorForm, NoRef, NoRef, NoRef, slots));
                continue;
            }

            var tracks = assembled.TargetTracks(node, clock);
            for (var index = 0; index < tracks.Count; index++)
            {
                var slot = assembled.Typed[index];
                var (source, interpolator) = sourceByMorph[slot.MorphIndex];
                targetTracks.Add(tracks[index]);
                sources.Add(new NifModelMorphTrackSource(source, node, NifModelMorphTrackSource.TargetForm,
                    interpolator, slot.MorphIndex, slot.TargetIndex, null));
            }
        }
    }

    /// <summary>Slice 5: the sequence's text keys as events, in file order; the block's decision goes to pending.</summary>
    private IReadOnlyList<SceneAnimationEvent> ReadEvents(int sequence, NifControllerSequenceView view,
        List<NifModelAnimationDisposition> pending)
    {
        var textKeys = view.TextKeysRef;
        if (textKeys == NoRef)
        {
            return [];
        }

        if (!_source.Is(textKeys, TextKeysType) || !_source.TryReadTextKeys(textKeys, out var keys))
        {
            if (_source.IsBlock(textKeys))
            {
                pending.Add(Native(textKeys, NifModelAnimationReasons.TextKeysUnreadable,
                    NifModelAnimationReasons.TextKeysUnreadableCode, sequence, NoOrdinal, null));
            }

            return [];
        }

        var mapped = NifModelTextKeyEvents.Map(keys, _source.Strings);
        if (mapped.IsBlocked)
        {
            pending.Add(Native(textKeys, NifModelAnimationReasons.Reason(mapped.Block),
                NifModelAnimationReasons.Code(mapped.Block), sequence, NoOrdinal, null));
            return [];
        }

        pending.Add(Typed(textKeys, sequence, NoOrdinal, null));
        return mapped.Events;
    }

    /// <summary>
    ///     Cut 2: a 20.0.0.4 controlled block that bound its target resolved that target's name through the NiStringPalette
    ///     it names, so that palette is Typed (recorded per binding, as the object palette is on a 20.2.0.7 stream).
    /// </summary>
    private void AddStringPaletteTyped(NifOblivionControllerSequenceView? legacy, int ordinal, int sequence,
        List<NifModelAnimationDisposition> pending)
    {
        if (legacy is null || ordinal >= legacy.ControlledBlocks.Length)
        {
            return;
        }

        var palette = legacy.ControlledBlocks[ordinal].StringPaletteRef;
        if (_source.Is(palette, "NiStringPalette"))
        {
            pending.Add(Typed(palette, sequence, ordinal, null));
        }
    }

    /// <summary>Keeps the whole sequence native: the sequence, every controlled block's interpolator and data, its text keys.</summary>
    private void SequenceNative(int sequence, NifControllerSequenceView? view, string reason, string code)
    {
        _decisions.Add(Native(sequence, reason, code, sequence, NoOrdinal, null));
        if (view is null)
        {
            return;
        }

        for (var ordinal = 0; ordinal < view.ControlledBlocks.Length; ordinal++)
        {
            AddNative(_decisions, _source.InterpolatorBlocks(view.ControlledBlocks[ordinal].InterpolatorRef), reason,
                code, sequence, ordinal, null);
        }

        if (_source.IsBlock(view.TextKeysRef))
        {
            _decisions.Add(Native(view.TextKeysRef, NifModelAnimationReasons.TextKeysOutsideClip,
                NifModelAnimationReasons.TextKeysOutsideClipCode, sequence, NoOrdinal, null));
        }

        AddAnimNotes(sequence, view);
    }

    /// <summary>The sequence's BSAnimNotes stay NativeOnly (slice 5, <see cref="NifModelAnimNotes.Reason" />).</summary>
    private void AddAnimNotes(int sequence, NifControllerSequenceView view)
    {
        foreach (var (block, disposition) in NifModelTextKeyEvents.AnimNotes(view).Dispositions)
        {
            if (_source.IsBlock(block))
            {
                _decisions.Add(new NifModelAnimationDisposition(block, disposition, "animNotes", sequence, NoOrdinal,
                    null));
            }
        }
    }

    /// <summary>
    ///     Slices 6 and 7: the free-running, active NiTransformControllers and NiGeomMorpherControllers become one
    ///     <c>(controllers)</c> clip.
    /// </summary>
    private void ReadControllers(NifModelNodeGraph graph)
    {
        var referenced = ReferencedControllers();
        var candidates = new List<(int Controller, NifTimeControllerHeader Header, NifModelInterpolatorChannels Channels,
            IReadOnlyList<int> Occurrences, SceneAnimationClock Clock)>();
        var morphVectors = new List<SceneMorphTrack>();
        var morphTargets = new List<SceneMorphTargetTrack>();
        var morphSources = new List<NifModelMorphTrackSource>();
        var typed = new List<(int Block, string Kind, NifTimeControllerHeader Header, int Interpolator)>();
        for (var controller = 0; controller < _source.BlockCount; controller++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_source.Is(controller, TransformControllerType))
            {
                if (ReadCandidate(controller, graph, referenced) is { } candidate)
                {
                    candidates.Add(candidate);
                }
            }
            else if (_source.Is(controller, NifModelAnimationMorphs.MorpherControllerType))
            {
                ReadMorphController(controller, graph, referenced, morphVectors, morphTargets, morphSources, typed);
            }
            else if (NifModelPropertyController.IsControllerType(_source.TypeOf(controller)))
            {
                _properties.ReadController(controller, graph, referenced, _decisions);
            }
        }

        var propertyTracks = new List<ScenePropertyTrack>();
        var propertySources = new List<NifModelPropertyTrackSource>();
        _properties.EmitControllers(_decisions, propertyTracks, propertySources, typed);

        var counts = new Dictionary<(int Node, SceneTransformProperty Property), int>();
        foreach (var candidate in candidates)
        {
            foreach (var key in Components(candidate.Channels, candidate.Occurrences, candidate.Clock))
            {
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        var tracks = new List<SceneTransformTrack>();
        var eulerTracks = new List<SceneEulerRotationTrack>();
        var sources = new List<NifModelTrackSource>();
        var eulerSources = new List<NifModelEulerTrackSource>();
        foreach (var (controller, header, channels, occurrences, clock) in candidates)
        {
            if (Components(channels, occurrences, clock).Any(key => counts[key] > 1))
            {
                _decisions.Add(Native(controller, NifModelAnimationReasons.RepeatedEmbeddedTarget,
                    NifModelAnimationReasons.RepeatedEmbeddedTargetCode, controller, NoOrdinal, null));
                AddNative(_decisions, channels.Blocks, NifModelAnimationReasons.RepeatedEmbeddedTarget,
                    NifModelAnimationReasons.RepeatedEmbeddedTargetCode, controller, NoOrdinal, null);
                continue;
            }

            var before = tracks.Count;
            var eulerBefore = eulerTracks.Count;
            AddTracks(channels, occurrences, clock, clock, null, controller, NoOrdinal, tracks, eulerTracks, sources,
                eulerSources, _decisions);
            if (tracks.Count > before || eulerTracks.Count > eulerBefore)
            {
                _decisions.Add(Typed(controller, controller, NoOrdinal, null));
                typed.Add((controller, NifModelAnimationExtras.TransformKind, header, channels.Interpolator));
            }
            else
            {
                // Every channel was refused (a mapped channel on a placed target always gives a track).
                var refused = FirstRefusal(channels, clock);
                _decisions.Add(Native(controller, NifModelAnimationReasons.Reason(refused),
                    NifModelAnimationReasons.Code(refused), controller, NoOrdinal, null));
            }
        }

        if (tracks.Count == 0 && eulerTracks.Count == 0 && morphVectors.Count == 0 && morphTargets.Count == 0 &&
            propertyTracks.Count == 0)
        {
            return;
        }

        typed.Sort(static (a, b) => a.Block.CompareTo(b.Block));
        _clips.Add(new SceneAnimation(
            ControllersClipName,
            morphVectors,
            NifModelAnimationExtras.ForControllers(typed, sources, eulerSources, morphSources, propertySources),
            tracks,
            timing: Timing,
            eulerRotationTracks: eulerTracks,
            propertyTracks: propertyTracks,
            morphTargetTracks: morphTargets));
    }

    /// <summary>
    ///     One NiTransformController's candidacy for the <c>(controllers)</c> clip: null (with its native decision) when it
    ///     is manager-controlled, inactive, curve-less, untargeted, native as a whole, or its clock is refused.
    /// </summary>
    private (int Controller, NifTimeControllerHeader Header, NifModelInterpolatorChannels Channels,
        IReadOnlyList<int> Occurrences, SceneAnimationClock Clock)? ReadCandidate(
            int controller, NifModelNodeGraph graph, HashSet<int> referenced)
    {
        if (!_source.TryReadControllerHeader(controller, out var header) ||
            !_source.TryReadInterpolatorRef(controller, out var interpolator))
        {
            Decide(controller, NifModelAnimationReasons.ControllerUnreadable,
                NifModelAnimationReasons.ControllerUnreadableCode, controller);
            return null;
        }

        if (NifModelClockMapping.IsSequenceDriven(header, referenced.Contains(controller)))
        {
            Decide(controller, NifModelAnimationReasons.ManagerControlled,
                NifModelAnimationReasons.ManagerControlledCode, controller);
            return null;
        }

        if (!header.IsActive)
        {
            var inactive = NifModelCurveBlock.InactiveController;
            _decisions.Add(Native(controller, NifModelAnimationReasons.Reason(inactive),
                NifModelAnimationReasons.Code(inactive), controller, NoOrdinal, null));
            AddNative(_decisions, _source.InterpolatorBlocks(interpolator), NifModelAnimationReasons.Reason(inactive),
                NifModelAnimationReasons.Code(inactive), controller, NoOrdinal, null);
            return null;
        }

        if (!_source.IsBlock(interpolator))
        {
            Decide(controller, NifModelAnimationReasons.NoInterpolator, NifModelAnimationReasons.NoInterpolatorCode,
                controller);
            return null;
        }

        var target = header.TargetRef;
        if (!_source.IsBlock(target) || target >= graph.OccurrencesByBlock.Count ||
            graph.OccurrencesByBlock[target].Count == 0)
        {
            Decide(controller, NifModelAnimationReasons.TargetNotPlaced, NifModelAnimationReasons.TargetNotPlacedCode,
                controller);
            AddNative(_decisions, _source.InterpolatorBlocks(interpolator), NifModelAnimationReasons.TargetNotPlaced,
                NifModelAnimationReasons.TargetNotPlacedCode, controller, NoOrdinal, null);
            return null;
        }

        var channels = NifModelInterpolatorChannels.Map(_source, interpolator, _squad);
        if (channels.IsNative)
        {
            Decide(controller, channels.NativeReason!, channels.NativeCode!, controller);
            AddNative(_decisions, channels.Blocks, channels.NativeReason!, channels.NativeCode!, controller, NoOrdinal,
                null);
            return null;
        }

        var clock = NifModelClockMapping.MapFreeRunning(header, channels.HasCurve);
        if (clock.IsBlocked || clock.Clock is null)
        {
            var (reason, code) = clock.IsBlocked
                ? (NifModelAnimationReasons.Reason(clock.Block), NifModelAnimationReasons.Code(clock.Block))
                : (NifModelAnimationReasons.SentinelWithoutCurve, NifModelAnimationReasons.SentinelWithoutCurveCode);
            Decide(controller, reason, code, controller);
            AddNative(_decisions, channels.Blocks, reason, code, controller, NoOrdinal, null);
            return null;
        }

        return (controller, header, channels, graph.OccurrencesByBlock[target], clock.Clock);
    }

    /// <summary>
    ///     Slice 7, the embedded side (D6, RE-22 rule 3, RE-23): one free-running, active NiGeomMorpherController that is
    ///     the first morpher on its placed target's chain maps every non-Base slot and emits the assembled tracks on each
    ///     occurrence with the controller's clock; anything else keeps the controller and its slot blocks native.
    /// </summary>
    private void ReadMorphController(
        int controller,
        NifModelNodeGraph graph,
        HashSet<int> referenced,
        List<SceneMorphTrack> vectors,
        List<SceneMorphTargetTrack> targetTracks,
        List<NifModelMorphTrackSource> sources,
        List<(int Block, string Kind, NifTimeControllerHeader Header, int Interpolator)> typed)
    {
        if (!_source.TryReadMorpherController(controller, out var view) || !view.ConsumedExactly)
        {
            Decide(controller, NifModelAnimationReasons.MorpherUnreadable,
                NifModelAnimationReasons.MorpherUnreadableCode, controller);
            return;
        }

        var header = view.Header;
        var slotBlocks = view.Items.SelectMany(item => _source.InterpolatorBlocks(item.InterpolatorRef)).Distinct()
            .ToArray();
        if (NifModelClockMapping.IsSequenceDriven(header, referenced.Contains(controller)))
        {
            // As for a transform controller: the slots' NiBlend*Interpolators are classified as manager-side state.
            Decide(controller, NifModelAnimationReasons.ManagerControlled,
                NifModelAnimationReasons.ManagerControlledCode, controller);
            return;
        }

        if (!header.IsActive)
        {
            var inactive = NifModelCurveBlock.InactiveController;
            MorpherNative(controller, slotBlocks, NifModelAnimationReasons.Reason(inactive),
                NifModelAnimationReasons.Code(inactive));
            return;
        }

        var target = header.TargetRef;
        if (!_source.IsBlock(target) || target >= graph.OccurrencesByBlock.Count ||
            graph.OccurrencesByBlock[target].Count == 0)
        {
            MorpherNative(controller, slotBlocks, NifModelAnimationReasons.TargetNotPlaced,
                NifModelAnimationReasons.TargetNotPlacedCode);
            return;
        }

        if (NifModelAnimationMorphs.FirstMorpher(_source, target) != controller)
        {
            MorpherNative(controller, slotBlocks, NifModelAnimationReasons.ExtraMorpher,
                NifModelAnimationReasons.ExtraMorpherCode);
            return;
        }

        if (!NifModelAnimationMorphs.TryReadMorphData(_source, view, out var data, out var dataReason,
                out var dataCode))
        {
            MorpherNative(controller, slotBlocks, dataReason, dataCode);
            return;
        }

        if (!_targets.AdmitsMorphChannels(graph.OccurrencesByBlock[target], data.MorphCount - 1))
        {
            MorpherNative(controller, slotBlocks, NifModelAnimationReasons.MorphTargetsNotTyped,
                NifModelAnimationReasons.MorphTargetsNotTypedCode);
            return;
        }

        if (view.Items.Length > 0)
        {
            AddNative(_decisions, _source.InterpolatorBlocks(view.Items[0].InterpolatorRef),
                NifModelAnimationReasons.BaseWeight, NifModelAnimationReasons.BaseWeightCode, controller, NoOrdinal,
                null);
        }

        var results = new List<NifModelMorphTargetResult>();
        var policies = new List<SceneAnimationTrackSourcePolicy?>();
        var hasCurve = false;
        for (var morph = 1; morph < data.MorphCount; morph++)
        {
            var result = morph < view.Items.Length
                ? NifModelAnimationMorphs.MapSlot(_source, morph, view.Items[morph].InterpolatorRef,
                    view.Items[morph].WeightBits)
                : NifModelMorphTargetResult.Native(morph, NoRef, [], NifModelAnimationReasons.MorphSlotNoItem,
                    NifModelAnimationReasons.MorphSlotNoItemCode);
            hasCurve |= result.HasCurve;
            results.Add(result);
            policies.Add(null);
        }

        var clock = NifModelClockMapping.MapFreeRunning(header, hasCurve);
        if (clock.IsBlocked || clock.Clock is null)
        {
            var (reason, code) = clock.IsBlocked
                ? (NifModelAnimationReasons.Reason(clock.Block), NifModelAnimationReasons.Code(clock.Block))
                : (NifModelAnimationReasons.SentinelWithoutCurve, NifModelAnimationReasons.SentinelWithoutCurveCode);
            MorpherNative(controller, results.SelectMany(static r => r.Blocks).Distinct().ToArray(), reason, code);
            return;
        }

        var slotSources = new List<(int Source, int Interpolator)>();
        foreach (var result in results)
        {
            RecordSlot(result, controller, NoOrdinal, _decisions);
            slotSources.Add((controller, result.InterpolatorBlock));
        }

        var assembled = NifModelMorphTracks.Assemble(data.MorphCount, results, policies);
        if (!assembled.HasTracks)
        {
            var first = results.First(static r => !r.IsTyped);
            _decisions.Add(Native(controller, first.NativeReason!, first.NativeCode!, controller, NoOrdinal, null));
            return;
        }

        EmitMorphTracks(assembled, results, slotSources, graph.OccurrencesByBlock[target], clock.Clock, vectors,
            targetTracks, sources);
        _decisions.Add(Typed(controller, controller, NoOrdinal, null));
        typed.Add((controller, NifModelAnimationExtras.MorpherKind, header, NoRef));
    }

    /// <summary>Keeps a morpher and its slot blocks native with one reason.</summary>
    private void MorpherNative(int controller, IReadOnlyList<int> slotBlocks, string reason, string code)
    {
        Decide(controller, reason, code, controller);
        AddNative(_decisions, slotBlocks, reason, code, controller, NoOrdinal, null);
    }

    /// <summary>Every controller a controlled block of any NiControllerSequence in the file references (RE-22 rule 1).</summary>
    private HashSet<int> ReferencedControllers()
    {
        var referenced = new HashSet<int>();
        for (var block = 0; block < _source.BlockCount; block++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!_source.Is(block, SequenceType) || !_source.TryReadSequence(block, out var view))
            {
                continue;
            }

            foreach (var controlled in view.ControlledBlocks)
            {
                if (_source.IsBlock(controlled.ControllerRef))
                {
                    referenced.Add(controlled.ControllerRef);
                }
            }
        }

        return referenced;
    }

    /// <summary>
    ///     The (node, property) pairs a candidate's mapped channels would drive; an Euler rotation drives the rotation
    ///     unless the RE-20 rule 7 guard keeps it native under the candidate's clock.
    /// </summary>
    private static IEnumerable<(int Node, SceneTransformProperty Property)> Components(
        NifModelInterpolatorChannels channels, IReadOnlyList<int> occurrences, SceneAnimationClock? effectiveClock)
    {
        foreach (var node in occurrences)
        {
            foreach (var result in channels.Channels)
            {
                if (result.Channel is { } channel)
                {
                    yield return (node, channel.Property);
                }
                else if (result.Euler is { } euler && !euler.SamplesBeforeFirstKey(effectiveClock))
                {
                    yield return (node, SceneTransformProperty.Rotation);
                }
            }
        }
    }

    /// <summary>Plan sections 1.6 and 2.1: manager-side blend interpolators and multi-target controllers stay native.</summary>
    private void ClassifyManagerSideState()
    {
        for (var block = 0; block < _source.BlockCount; block++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_source.Is(block, MultiTargetType))
            {
                Decide(block, NifModelAnimationReasons.MultiTargetBinding,
                    NifModelAnimationReasons.MultiTargetBindingCode, NoRef);
            }
            else if (_source.Inherits(block, BlendInterpolatorType))
            {
                Decide(block, NifModelAnimationReasons.BlendState, NifModelAnimationReasons.BlendStateCode, NoRef);
            }
        }
    }

    private void Decide(int block, string reason, string code, int source)
    {
        _decisions.Add(Native(block, reason, code, source, NoOrdinal, null));
    }

    private static void AddNative(List<NifModelAnimationDisposition> decisions, IEnumerable<int> blocks, string reason,
        string code, int source, int ordinal, SceneTransformProperty? property)
    {
        foreach (var block in blocks)
        {
            decisions.Add(Native(block, reason, code, source, ordinal, property));
        }
    }

    private static NifModelAnimationDisposition Native(int block, string reason, string code, int source, int ordinal,
        SceneTransformProperty? property)
    {
        return new NifModelAnimationDisposition(block, NifModelBlockDisposition.NativeOnly(reason), code, source,
            ordinal, property);
    }

    private static NifModelAnimationDisposition Typed(int block, int source, int ordinal,
        SceneTransformProperty? property)
    {
        return new NifModelAnimationDisposition(block, NifModelBlockDisposition.Typed,
            NifModelAnimationReasons.TypedCode, source, ordinal, property);
    }
}
