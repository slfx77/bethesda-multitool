using System.Globalization;
using BethesdaMultitool.Core.Modeling.Units;
using BethesdaMultitool.Core.Modeling.Xngine;
using Slfx77.Multitool.Core.Models.Catalog;

namespace BethesdaMultitool.Core.Modeling.Redguard;

/// <summary>
///     The static catalog description of the Redguard <c>.3DC</c> reader (<c>bmt.redguard.3dc</c>) for
///     <c>mesh formats</c>: the variants the probe recognizes, the actor unit row (cut-1c plan section 6.3) and the
///     admission rules of plan sections 4 and 6.1. Every text is built from the constants the probe, the reader and
///     the unit rows use, so a rule names exactly what the reader applies.
/// </summary>
/// <remarks>
///     Every field stays inside <see cref="ModelStaticAdmissionRule.MaximumTextLength" />; the Shared constructors
///     enforce the limits, so a violation fails the type initializer rather than truncating.
/// </remarks>
internal static class Redguard3DcModelFormatMetadata
{
    /// <summary>The reader's format id.</summary>
    public const string FormatId = "bmt.redguard.3dc";

    /// <summary>The catalog display name.</summary>
    public const string DisplayName =
        "Redguard .3DC animated mesh (v2.6, v2.7 frame stacks: an int32 keyframe and later poses as int16 deltas from " +
        "it or as int32 poses; the 3dart actors)";

    /// <summary>
    ///     The largest file the reader loads (plan section 2), an assumed bound: the largest measured <c>.3DC</c> is
    ///     7,081,150 bytes (plan section 0.2).
    /// </summary>
    public const int MaximumSourceBytes = 16 * 1024 * 1024;

    /// <summary>
    ///     The reason a <c>v2.5</c>-tagged frame stack is declined: no such file is known, and the frame-stack parser
    ///     addresses corners by the untripled offsets of v2.6 and v2.7.
    /// </summary>
    public const string V25UnsupportedReason =
        "A v2.5 .3DC frame stack is not known (147 of 147 retail .3DC files are v2.6 or v2.7) and the frame-stack " +
        "parser reads plane corner offsets untripled, as v2.6 and v2.7 store them: not read";

    /// <summary>The tags a <c>.3DC</c> carries: 120 retail files are v2.6 and 27 are v2.7 (plan section 0.2).</summary>
    public static IReadOnlyList<string> Tags { get; } = ["v2.6", "v2.7"];

    /// <summary>The immutable description the reader returns as its format metadata.</summary>
    public static ModelSourceFormatMetadata Description { get; } = Create();

    /// <summary>
    ///     The supported variants: one per tag and frame-record width, spelled as the probe evidence without its counts
    ///     (<see cref="DescribeVariant" />), for example <c>Redguard .3DC v2.6 frame stack, 3-dword frame records</c>.
    /// </summary>
    public static IReadOnlyList<string> Variants()
    {
        var variants = new List<string>(Tags.Count * 2);
        foreach (var tag in Tags)
        {
            variants.Add(DescribeVariant(tag, 3));
            variants.Add(DescribeVariant(tag, 4));
        }

        return variants.AsReadOnly();
    }

    /// <summary>The variant text for a tag and frame-record width: <c>Redguard .3DC v2.6 frame stack, 3-dword frame records</c>.</summary>
    public static string DescribeVariant(string tag, int recordDwords)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return string.Create(CultureInfo.InvariantCulture,
            $"Redguard .3DC {tag} frame stack, {recordDwords}-dword frame records");
    }

    /// <summary>
    ///     The probe evidence for a recognized stack: the variant plus the header's point, plane and frame counts, for
    ///     example <c>Redguard .3DC v2.6 frame stack, 3-dword frame records, 223 points, 422 planes, 38 frames</c>.
    /// </summary>
    public static string Describe(string tag, int recordDwords, int pointCount, int planeCount, int frameCount)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{DescribeVariant(tag, recordDwords)}, {pointCount} points, {planeCount} planes, {frameCount} frames");
    }

    /// <summary>Builds the description once.</summary>
    private static ModelSourceFormatMetadata Create()
    {
        return new ModelSourceFormatMetadata(DisplayName, Variants(), ClassicModelUnits.ForRedguardActor(),
            UnitPolicy(), Rules());
    }

    /// <summary>The unit policy: every <c>.3DC</c> takes the Redguard actor row.</summary>
    private static string UnitPolicy()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"A .3DC frame stack is a Redguard format (147 of 147 retail files are 3dart actors), so every document " +
            $"takes the Redguard actor row: {ClassicModelUnits.RedguardMetersPerUnit:R} m per native unit, Assumed for " +
            $"human actors only (RE-3), with the {ClassicModelUnits.ActorScaleDiagnostic} diagnostic, because the " +
            $"keyframes are normalized to the int16 range. The {BethesdaModelRegistration.GameOption} option may name " +
            $"redguard (or auto); another XnGine game, or a game name that is not an XnGine game, throws. A container " +
            $"or install that names another game is recorded with {XnGineGameIdentity.StepRefutedDiagnostic} and skipped.");
    }

    /// <summary>The ordered admission rules.</summary>
    private static ModelStaticAdmissionRule[] Rules()
    {
        var culture = CultureInfo.InvariantCulture;
        var tags = string.Join(", ", Tags);
        return
        [
            new ModelStaticAdmissionRule("recognition",
                "Any input",
                string.Create(culture,
                    $"Recognized by content alone: a mesh tag, the .3DC shape test (header +16 frames > 0, the +20 frame " +
                    $"block inside the prefix, a frame table at or before the +60 plane list dividing into 3- or 4-dword " +
                    $"records per frame; 147 of 147 .3DC files and no static mesh satisfy it) and a plane walk with 8-byte " +
                    $"plane headers whose corners address the header's points. The bmt.xngine.3d reader answers NotAModel " +
                    $"for the same bytes, so exactly one reader recognizes each file. The file extension is never consulted.")),
            new ModelStaticAdmissionRule("supported-key",
                string.Create(culture, $"Tag {tags}, the shape test, and the plane walk"),
                "Supported. Confirmed when the whole file lies inside the 64 KiB probe prefix and its blocks tile it " +
                "exactly (26 retail files); Tentative otherwise (121 retail files exceed the prefix; every retail plane " +
                "list ends by byte 32,848, inside it), and Tentative with the reason when a complete file declares a " +
                "block outside itself or does not tile, so the read reports why it refuses it."),
            new ModelStaticAdmissionRule("tiling",
                "Every read",
                "Acceptance is exact tiling (Redguard3DcFile): the header, the frame block and table, the plane list and " +
                "every frame's point, normal and plane-data block cover the file with no overlap and at most the one " +
                "region the frame block declares. The frame width (int16 deltas or int32 poses) is the reading that " +
                "tiles; a stack that tiles neither way is invalid data. The header's +24/+48/+52 offsets are never used " +
                "(they are frame 1's on wide files only)."),
            new ModelStaticAdmissionRule("v2.5",
                "Tag v2.5 with the .3DC shape",
                string.Create(culture,
                    $"Unsupported, decided before the plane walk (a v2.5 record stores its corners as point index x 4): " +
                    $"{V25UnsupportedReason}.")),
            new ModelStaticAdmissionRule("fxart-tags",
                "Tag v4.0 or v5.0 (Redguard Disc 1 fxart, 204 .3DC files among them)",
                "NotAModel for this reader: the bmt.xngine.3d reader declines every 3dfx tag as Unsupported (a later cut), " +
                "so the file is recognized once."),
            new ModelStaticAdmissionRule("not-a-model",
                "A static mesh, a non-mesh tag, or a record failing the shape test or the plane walk",
                "NotAModel."),
            new ModelStaticAdmissionRule("poses",
                "Every read",
                "The keyframe (frame 0, int32 points) is the base geometry, mapped as a static .3D is (Y negated, faces " +
                "reversed, split vertices with explicit source points, xngine.uv16 and xngine.plane). Frames 1 to N-1 " +
                "become morph targets named 'pose NNN' on every primitive: a narrow file's int16 deltas exactly as " +
                "PositionDeltas (added to the keyframe, never accumulated frame to frame), a wide file's int32 poses " +
                "exactly as AbsolutePositions. A corner is kept when the reference corner test keeps it in any pose."),
            new ModelStaticAdmissionRule("normals",
                "Every read",
                "Flat on both widths (plan decision D4): vertex normals (0, 0, 0) with Flat provenance and no normal " +
                "deltas. The wide files' authored per-pose normal blocks and the narrow files' undecoded 4-byte blocks " +
                "are native state."),
            new ModelStaticAdmissionRule("clip",
                "A stack of two or more frames",
                string.Create(culture,
                    $"One clip '{Redguard3DcModelAnimation.ClipName}' with one morph-weight track: Step, one-hot weights at " +
                    $"i/{Redguard3DcModelAnimation.FramesPerSecond} s for frame i (all zero at frame 0), plus a derived " +
                    $"final key at N/{Redguard3DcModelAnimation.FramesPerSecond} s repeating frame N-1 so the clip spans N " +
                    $"frame periods; no authored duration (the GLB writer refuses one that differs from the last key); " +
                    $"{Redguard3DcModelAnimation.FramesPerSecond} frames per second and Step Assumed (RE-3). A single-frame " +
                    $"stack has no clip.")),
            new ModelStaticAdmissionRule("uvs",
                "Every read",
                "The .3DC UV encoding is not decoded (plan decision D3): xngine.uv16 keeps the stored values, the portable " +
                "UVs apply the .3D reference rule without the packed-UV unfold over a 64-texel fallback, and every " +
                "primitive carries a placeholder material named TEXTURE.aaa#r, stated by a diagnostic."),
            new ModelStaticAdmissionRule("units",
                "Every read",
                string.Create(culture,
                    $"Redguard actor row: {ClassicModelUnits.RedguardMetersPerUnit:R} m per native unit, Assumed for human " +
                    $"actors only, with the {ClassicModelUnits.ActorScaleDiagnostic} diagnostic.")),
            new ModelStaticAdmissionRule("budgets",
                string.Create(culture,
                    $"More than {MaximumSourceBytes} source bytes, a census over 65,536 elements, a pose clip over " +
                    $"{Redguard3DcModelAnimation.MaximumWeights} one-hot weights ((N + 1) x (N - 1), so more than " +
                    $"{Redguard3DcModelAnimation.MaximumFrames} frames), or morph targets over " +
                    $"{Redguard3DcModelPoses.MaximumPositions} positions ((N - 1) x the plane corners)"),
                "Not supported: refused before the work it would need, the census and the clip from the header's counts " +
                "before the parse, the morph targets before the geometry (the largest measured .3DC is 7,081,150 bytes; " +
                "the longest has 228 frames, a 51,983-weight clip; the largest morph payload is 518,336 positions, " +
                "GOLMA001)."),
            new ModelStaticAdmissionRule("corrupt-input",
                "A header, frame table, plane walk or tiling inconsistent with the file",
                "The read fails as invalid data; nothing is fabricated. The declared sizes are checked before the parse: " +
                "the keyframe (point count x 12 bytes) and the plane headers (plane count x 8 bytes) must fit the file " +
                "and every frame-table offset must lie inside it, so no block end the parser computes can overflow.")
        ];
    }
}
