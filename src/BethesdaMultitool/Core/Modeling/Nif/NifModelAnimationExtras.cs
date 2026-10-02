using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The clip ExtrasJson <see cref="NifModelAnimationReader" /> writes (owner ruling D11: the sequence fields are copied
///     into the clip's extras as well as kept native). The shape is stable and versioned; every value is the stored one.
/// </summary>
/// <remarks>
///     <para>Both clip kinds write one object under the key <see cref="Key" />, with <c>version</c> = <see cref="Version" />.</para>
///     <para>
///         A sequence clip (<c>"source": "sequence"</c>): <c>block</c> (the NiControllerSequence), <c>nameIndex</c>,
///         <c>weightBits</c> (the stored bits, uint) and <c>weight</c> (the float), <c>accumRootName</c> and
///         <c>accumRootNameIndex</c>, <c>managerRef</c>, <c>textKeysRef</c>, <c>animNoteRefs</c> (the BSAnimNotes refs
///         as stored: the BS above 28 array, the single BS 24 to 28 ref, or null when the stream stores neither) and
///         <c>animNotesArrayForm</c>, <c>cycleType</c> (the stored word, so a REVERSE sequence that plays as Clamp keeps
///         its 1), <c>frequencyBits</c>, <c>startTimeBits</c>, <c>stopTimeBits</c>, <c>arrayGrowBy</c>, then
///         <c>controlledBlocks</c> (every controlled block in file order: <c>ordinal</c>, <c>interpolatorRef</c>,
///         <c>controllerRef</c>, <c>priority</c> (null when the stream stores none), and the five strings
///         <c>nodeName</c>, <c>propertyType</c>, <c>controllerType</c>, <c>controllerId</c>, <c>interpolatorId</c>, each
///         beside its <c>*Index</c>), then <c>tracks</c> and <c>morphTracks</c>.
///     </para>
///     <para>
///         The <c>(controllers)</c> clip (<c>"source": "(controllers)"</c>): <c>controllers</c> (each typed controller in
///         block order: <c>block</c>, <c>kind</c> (<c>transform</c> or <c>morpher</c>), <c>interpolatorRef</c> (-1 for a
///         morpher, whose interpolators are per slot), <c>targetRef</c>, <c>flags</c> (the stored word, so the anim type
///         bit 0 stays), <c>frequencyBits</c>, <c>phaseBits</c>, <c>startTimeBits</c>, <c>stopTimeBits</c>), then
///         <c>tracks</c> and <c>morphTracks</c>.
///     </para>
///     <para>
///         <c>tracks</c> maps track i of the clip's transform tracks to its source, because Shared cannot target a single
///         track (SA10): one object per track, in track order, with <c>controlledBlock</c> (the ordinal) or
///         <c>controller</c> (the block), <c>interpolator</c>, <c>property</c> (<c>translation</c>, <c>rotation</c> or
///         <c>scale</c>) and <c>node</c>; a Squad rotation (slice 16b) adds <c>squadPolicy</c> (<c>PcFloat32</c> or
///         <c>Xbox360Estimate</c>), <c>squadKeyType</c> (2 QUADRATIC or 3 TBC) and <c>squadPolicySource</c>.
///         <c>eulerTracks</c> maps track i of the clip's Euler rotation tracks (slice 13) likewise, with <c>property</c>
///         <c>rotation</c> and <c>axisKeyTypes</c> (the X, Y and Z axes' stored key types, 0 for an empty axis).
///         <c>morphTracks</c> does the same for the morph tracks, in emission order (the
///         per-target tracks in <c>MorphTargetTracks</c> order, the whole-vector tracks in <c>MorphTracks</c> order):
///         <c>form</c> (<c>target</c> or <c>vector</c>), the same source field, <c>node</c>, and for a per-target track
///         <c>interpolator</c> (-1 for a stored weight), <c>morph</c> (the NiMorphData morph index) and <c>target</c> (the
///         document target index); a vector track lists its <c>slots</c> (each <c>morph</c> and <c>interpolator</c>) in
///         target order instead. <c>propertyTracks</c> (slice 14) maps track i of the clip's property tracks: the same
///         source field, <c>interpolator</c> (-1 for a curve read straight from an NiUVData), <c>kind</c> (the
///         ScenePropertyKind name), <c>index</c> (the material, or the node for NodeVisibility), <c>layerIndex</c> (the
///         layer ordinal, -1 for a non-layer kind), <c>node</c> (the node for visibility, null otherwise),
///         <c>propertyBlock</c> (the NiMaterialProperty or NiTexturingProperty, null for visibility),
///         <c>controllerBlock</c> (the controller the binding resolved to, null when none) and <c>keyType</c> (the
///         stored key type, 0 for a constant or a B-spline).
///     </para>
///     <para>
///         Strings are the Latin-1 text of the stored bytes (one character per byte, so the text is exact), and null for the
///         NULL string or an index outside the table; the index beside each keeps the difference.
///     </para>
///     <para>
///         A sequence of a 20.0.0.4 <c>.kf</c> (cut 2) adds <c>stringPalette</c>: <c>stringTable</c> (the note that the
///         file stores no header string table, so <c>nameIndex</c>, <c>accumRootNameIndex</c> and the controlled blocks'
///         <c>*Index</c> members index the table the reader synthesizes, <see cref="NifModelLegacyKfStrings" />, -1 for an
///         empty sentinel and -2 for an offset that does not resolve), <c>sequencePalette</c> (the sequence's own String
///         Palette ref as stored) and <c>controlledBlocks</c> (each block's <c>ordinal</c>, <c>palette</c> ref and the five
///         stored offsets <c>nodeNameOffset</c>, <c>propertyTypeOffset</c>, <c>controllerTypeOffset</c>,
///         <c>controllerIdOffset</c>, <c>interpolatorIdOffset</c>). The member is absent for every other stream, whose
///         extras are unchanged.
///     </para>
/// </remarks>
internal static class NifModelAnimationExtras
{
    /// <summary>The key the clip object is written under.</summary>
    public const string Key = "bmt.nif.animation.clip";

    /// <summary>The shape version.</summary>
    public const int Version = 1;

    /// <summary>The <c>source</c> of a sequence clip.</summary>
    public const string SequenceSource = "sequence";

    /// <summary>The <c>kind</c> of an embedded NiTransformController in the <c>(controllers)</c> clip.</summary>
    public const string TransformKind = "transform";

    /// <summary>The <c>kind</c> of an embedded NiGeomMorpherController in the <c>(controllers)</c> clip.</summary>
    public const string MorpherKind = "morpher";

    /// <summary>The <c>kind</c> of an embedded property or visibility controller in the <c>(controllers)</c> clip (slice 14).</summary>
    public const string PropertyKind = "property";

    /// <summary>The <c>stringPalette</c> member's note on the synthesized string table of a 20.0.0.4 stream (cut 2).</summary>
    public const string SynthesizedStringTableNote =
        "20.0.0.4: no header string table; the *Index members index the table the reader synthesizes from the inline " +
        "names and the NiStringPalette entries (-1 an empty sentinel, -2 an offset that does not resolve)";

    /// <summary>The ExtrasJson of a sequence clip.</summary>
    /// <param name="sequenceBlock">The NiControllerSequence block.</param>
    /// <param name="view">Its lossless view.</param>
    /// <param name="strings">The file's raw header string table.</param>
    /// <param name="tracks">One source entry per clip transform track, in track order.</param>
    /// <param name="eulerTracks">One source entry per clip Euler rotation track, in track order; null for none.</param>
    /// <param name="morphTracks">One source entry per clip morph track, in emission order; null for none.</param>
    /// <param name="propertyTracks">One source entry per clip property track, in track order; null for none.</param>
    /// <param name="legacy">The Oblivion view of a 20.0.0.4 sequence (cut 2), whose stored palette refs and offsets are recorded; null otherwise.</param>
    /// <returns>The JSON object text.</returns>
    public static string ForSequence(
        int sequenceBlock,
        NifControllerSequenceView view,
        NifHeaderStringTable strings,
        IReadOnlyList<NifModelTrackSource> tracks,
        IReadOnlyList<NifModelEulerTrackSource>? eulerTracks = null,
        IReadOnlyList<NifModelMorphTrackSource>? morphTracks = null,
        IReadOnlyList<NifModelPropertyTrackSource>? propertyTracks = null,
        NifOblivionControllerSequenceView? legacy = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(tracks);
        var controlledBlocks = new JsonArray();
        for (var ordinal = 0; ordinal < view.ControlledBlocks.Length; ordinal++)
        {
            var block = view.ControlledBlocks[ordinal];
            JsonNode? priority = block.Priority is { } stored ? JsonValue.Create((int)stored) : null;
            controlledBlocks.Add(new JsonObject
            {
                ["ordinal"] = ordinal,
                ["interpolatorRef"] = block.InterpolatorRef,
                ["controllerRef"] = block.ControllerRef,
                ["priority"] = priority,
                ["nodeName"] = Text(strings, block.NodeNameIndex),
                ["nodeNameIndex"] = block.NodeNameIndex,
                ["propertyType"] = Text(strings, block.PropertyTypeIndex),
                ["propertyTypeIndex"] = block.PropertyTypeIndex,
                ["controllerType"] = Text(strings, block.ControllerTypeIndex),
                ["controllerTypeIndex"] = block.ControllerTypeIndex,
                ["controllerId"] = Text(strings, block.ControllerIdIndex),
                ["controllerIdIndex"] = block.ControllerIdIndex,
                ["interpolatorId"] = Text(strings, block.InterpolatorIdIndex),
                ["interpolatorIdIndex"] = block.InterpolatorIdIndex
            });
        }

        var animNotes = NifModelTextKeyEvents.AnimNotes(view);
        JsonNode? animNoteRefs = null;
        if (view.AnimNoteArrayRefs is not null || view.AnimNotesRef is not null)
        {
            animNoteRefs = new JsonArray(animNotes.StoredRefs.Select(static r => (JsonNode?)JsonValue.Create(r)).ToArray());
        }

        var clip = new JsonObject
        {
            ["version"] = Version,
            ["source"] = SequenceSource,
            ["block"] = sequenceBlock,
            ["nameIndex"] = view.NameIndex,
            ["weightBits"] = view.WeightBits,
            ["weight"] = NifModelNativeValues.Float(BitConverter.UInt32BitsToSingle(view.WeightBits)),
            ["accumRootName"] = Text(strings, view.AccumRootNameIndex),
            ["accumRootNameIndex"] = view.AccumRootNameIndex,
            ["managerRef"] = view.ManagerRef,
            ["textKeysRef"] = view.TextKeysRef,
            ["animNoteRefs"] = animNoteRefs,
            ["animNotesArrayForm"] = animNotes.IsArrayForm,
            ["cycleType"] = view.RawCycle,
            ["frequencyBits"] = view.FrequencyBits,
            ["startTimeBits"] = view.StartTimeBits,
            ["stopTimeBits"] = view.StopTimeBits,
            ["arrayGrowBy"] = view.ArrayGrowBy,
            ["controlledBlocks"] = controlledBlocks,
            ["tracks"] = Tracks(tracks, "controlledBlock"),
            ["eulerTracks"] = EulerTracks(eulerTracks ?? [], "controlledBlock"),
            ["morphTracks"] = MorphTracks(morphTracks ?? [], "controlledBlock"),
            ["propertyTracks"] = PropertyTracks(propertyTracks ?? [], "controlledBlock")
        };
        if (legacy is not null)
        {
            clip["stringPalette"] = StringPalette(legacy);
        }

        return new JsonObject { [Key] = clip }.ToJsonString();
    }

    /// <summary>The <c>stringPalette</c> member of a 20.0.0.4 sequence: the stored palette refs and offsets (see the remarks).</summary>
    private static JsonObject StringPalette(NifOblivionControllerSequenceView legacy)
    {
        var blocks = new JsonArray();
        for (var ordinal = 0; ordinal < legacy.ControlledBlocks.Length; ordinal++)
        {
            var block = legacy.ControlledBlocks[ordinal];
            blocks.Add(new JsonObject
            {
                ["ordinal"] = ordinal,
                ["palette"] = block.StringPaletteRef,
                ["nodeNameOffset"] = block.NodeNameOffset,
                ["propertyTypeOffset"] = block.PropertyTypeOffset,
                ["controllerTypeOffset"] = block.ControllerTypeOffset,
                ["controllerIdOffset"] = block.ControllerIdOffset,
                ["interpolatorIdOffset"] = block.InterpolatorIdOffset
            });
        }

        return new JsonObject
        {
            ["stringTable"] = SynthesizedStringTableNote,
            ["sequencePalette"] = legacy.StringPaletteRef,
            ["controlledBlocks"] = blocks
        };
    }

    /// <summary>The ExtrasJson of the <c>(controllers)</c> clip.</summary>
    /// <param name="controllers">
    ///     Each typed controller: its block, kind (<see cref="TransformKind" />, <see cref="MorpherKind" /> or
    ///     <see cref="PropertyKind" />), NiTimeController header and Interpolator ref (-1 for a morpher or an
    ///     NiUVController), in block order.
    /// </param>
    /// <param name="tracks">One source entry per clip transform track, in track order.</param>
    /// <param name="eulerTracks">One source entry per clip Euler rotation track, in track order; null for none.</param>
    /// <param name="morphTracks">One source entry per clip morph track, in emission order; null for none.</param>
    /// <param name="propertyTracks">One source entry per clip property track, in track order; null for none.</param>
    /// <returns>The JSON object text.</returns>
    public static string ForControllers(
        IReadOnlyList<(int Block, string Kind, NifTimeControllerHeader Header, int Interpolator)> controllers,
        IReadOnlyList<NifModelTrackSource> tracks,
        IReadOnlyList<NifModelEulerTrackSource>? eulerTracks = null,
        IReadOnlyList<NifModelMorphTrackSource>? morphTracks = null,
        IReadOnlyList<NifModelPropertyTrackSource>? propertyTracks = null)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        ArgumentNullException.ThrowIfNull(tracks);
        var list = new JsonArray();
        foreach (var (block, kind, header, interpolator) in controllers)
        {
            list.Add(new JsonObject
            {
                ["block"] = block,
                ["kind"] = kind,
                ["interpolatorRef"] = interpolator,
                ["targetRef"] = header.TargetRef,
                ["flags"] = header.Flags,
                ["frequencyBits"] = header.FrequencyBits,
                ["phaseBits"] = header.PhaseBits,
                ["startTimeBits"] = header.StartTimeBits,
                ["stopTimeBits"] = header.StopTimeBits
            });
        }

        var clip = new JsonObject
        {
            ["version"] = Version,
            ["source"] = NifModelAnimationReader.ControllersClipName,
            ["controllers"] = list,
            ["tracks"] = Tracks(tracks, "controller"),
            ["eulerTracks"] = EulerTracks(eulerTracks ?? [], "controller"),
            ["morphTracks"] = MorphTracks(morphTracks ?? [], "controller"),
            ["propertyTracks"] = PropertyTracks(propertyTracks ?? [], "controller")
        };
        return new JsonObject { [Key] = clip }.ToJsonString();
    }

    /// <summary>The <c>kind</c> text of a property track: the <see cref="ScenePropertyKind" /> name.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The enum name.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind is undefined.</exception>
    public static string KindName(ScenePropertyKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown property kind.");
        }

        return kind.ToString();
    }

    /// <summary>The <c>property</c> text of a channel.</summary>
    /// <param name="property">The channel.</param>
    /// <returns><c>translation</c>, <c>rotation</c> or <c>scale</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The property is undefined.</exception>
    public static string PropertyName(SceneTransformProperty property)
    {
        return property switch
        {
            SceneTransformProperty.Translation => "translation",
            SceneTransformProperty.Rotation => "rotation",
            SceneTransformProperty.Scale => "scale",
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Unknown transform property.")
        };
    }

    private static JsonArray Tracks(IReadOnlyList<NifModelTrackSource> tracks, string sourceField)
    {
        var array = new JsonArray();
        foreach (var track in tracks)
        {
            var entry = new JsonObject
            {
                [sourceField] = track.Source,
                ["interpolator"] = track.Interpolator,
                ["property"] = PropertyName(track.Property),
                ["node"] = track.Node
            };
            if (track.SquadPolicy is { } policy)
            {
                entry["squadPolicy"] = policy.ToString();
                entry["squadKeyType"] = track.SquadKeyType;
                entry["squadPolicySource"] = track.SquadPolicySource;
            }

            array.Add(entry);
        }

        return array;
    }

    private static JsonArray EulerTracks(IReadOnlyList<NifModelEulerTrackSource> tracks, string sourceField)
    {
        var array = new JsonArray();
        foreach (var track in tracks)
        {
            array.Add(new JsonObject
            {
                [sourceField] = track.Source,
                ["interpolator"] = track.Interpolator,
                ["property"] = PropertyName(SceneTransformProperty.Rotation),
                ["node"] = track.Node,
                ["axisKeyTypes"] = new JsonArray(track.XKeyType, track.YKeyType, track.ZKeyType)
            });
        }

        return array;
    }

    private static JsonArray PropertyTracks(IReadOnlyList<NifModelPropertyTrackSource> tracks, string sourceField)
    {
        var array = new JsonArray();
        foreach (var track in tracks)
        {
            array.Add(new JsonObject
            {
                [sourceField] = track.Source,
                ["interpolator"] = track.Interpolator,
                ["kind"] = KindName(track.Kind),
                ["index"] = track.Index,
                ["layerIndex"] = track.LayerIndex,
                ["node"] = track.Node < 0 ? null : JsonValue.Create(track.Node),
                ["propertyBlock"] = track.PropertyBlock < 0 ? null : JsonValue.Create(track.PropertyBlock),
                ["controllerBlock"] = track.Controller < 0 ? null : JsonValue.Create(track.Controller),
                ["keyType"] = track.KeyType
            });
        }

        return array;
    }

    private static JsonArray MorphTracks(IReadOnlyList<NifModelMorphTrackSource> tracks, string sourceField)
    {
        var array = new JsonArray();
        foreach (var track in tracks)
        {
            var entry = new JsonObject
            {
                ["form"] = track.Form,
                [sourceField] = track.Source,
                ["node"] = track.Node
            };
            if (track.Slots is { } slots)
            {
                var list = new JsonArray();
                foreach (var (morph, interpolator) in slots)
                {
                    list.Add(new JsonObject { ["morph"] = morph, ["interpolator"] = interpolator });
                }

                entry["slots"] = list;
            }
            else
            {
                entry["interpolator"] = track.Interpolator;
                entry["morph"] = track.Morph;
                entry["target"] = track.Target;
            }

            array.Add(entry);
        }

        return array;
    }

    private static string? Text(NifHeaderStringTable strings, int index)
    {
        return NifAnimationStrings.TryGetLatin1(strings, index, out var text) ? text : null;
    }
}
