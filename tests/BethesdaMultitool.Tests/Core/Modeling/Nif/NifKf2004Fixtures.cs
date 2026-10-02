using System.Text;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Hand-written little-endian 20.0.0.4 (user 11, BS 11) fixtures for the legacy header-layout, decoder and reader
///     tests (cut 2, TestOutput/cut2-prep-20260928/kf2004 and kf2004-carry), written field by field from nif.xml's
///     20.0.0.4 conditions (never through the reader's own decoder). <see cref="Build" /> is the five-block <c>.kf</c>
///     shape the FNV-shipped files use: 0 NiControllerSequence 'Idle2004' with one controlled block whose palette
///     offsets name 'Bip01', 1 NiTransformInterpolator (identity transform, Data at 3), 2 NiStringPalette ('Bip01' +
///     NUL, and the Controller Type entry when one is asked for), 3 NiTransformData (one LINEAR rotation key and one
///     LINEAR translation key), 4 NiTextKeyExtraData ('start' at 0, 'end' at 1), and a one-root footer.
///     <see cref="BuildSceneGraph" /> is the same header over one NiNode: a 20.0.0.4 SCENE GRAPH, the reader's
///     <c>later-cut(2)</c> control. <c>NifTestFileBuilder</c> cannot write either: it builds 20.2.0.7 headers (Block
///     Size array, string table, indexed strings).
/// </summary>
/// <remarks>
///     The parameters exist for the controls: a changed BS or user version must keep the decoder's
///     <see cref="NotSupportedException" /> refusal, an over-declared controlled-block count corrupts the legacy
///     measure walk, which must refuse the whole block list rather than emit wrong offsets, and the default
///     <paramref name="controllerType" /> (none) writes the empty Controller Type sentinel, so the reader admits the
///     controlled block as a transform track only when the retail form ('NiTransformController' in the palette) is
///     asked for, and <paramref name="extraBlockType" /> adds a sixth block of a type outside the eight the reader
///     measured, which the probe and the read must decline (the Oblivion <c>.kf</c> shape the review measured). The
///     defaults reproduce <c>fixtures/synthetic_kf2004.bin</c> byte for byte (the foundation's
///     <c>build_synthetic_kf.py</c> pins that). Strings here are inline SizedStrings (nif.xml <c>string</c> until
///     20.0.0.5); the header has no Block Size array and no string table.
/// </remarks>
internal static class NifKf2004Fixtures
{
    /// <summary>The sequence block's name.</summary>
    public const string SequenceName = "Idle2004";

    /// <summary>The palette's first entry, the controlled block's target and the accum root.</summary>
    public const string TargetName = "Bip01";

    /// <summary>The Controller Type the retail files name in every controlled block (the binder's transform test).</summary>
    public const string TransformControllerType = "NiTransformController";

    /// <summary>The scene-graph fixture's one node.</summary>
    public const string SceneGraphNodeName = "Scene Root";

    /// <summary>The five header block types, in block order (each block uses its own type); all among the reader's eight.</summary>
    public static readonly string[] BlockTypes =
    [
        "NiControllerSequence", "NiTransformInterpolator", "NiStringPalette", "NiTransformData", "NiTextKeyExtraData"
    ];

    /// <summary>
    ///     Builds the <c>.kf</c> fixture. The defaults build the valid file; every parameter change is a named control.
    /// </summary>
    /// <param name="version">The binary version field (the header line always names 20.0.0.4).</param>
    /// <param name="userVersion">The header User Version.</param>
    /// <param name="bsVersion">The BSStreamHeader BS Version.</param>
    /// <param name="declaredControlledBlocks">
    ///     The Num Controlled Blocks the sequence declares; one block is always written, so any other value corrupts
    ///     the sequence body and must abort the legacy measure walk.
    /// </param>
    /// <param name="controllerType">
    ///     The controlled block's Controller Type: null writes the empty sentinel (0xFFFFFFFF) and a one-entry palette;
    ///     a name is written as the palette's second entry and named by its offset (the retail form).
    /// </param>
    /// <param name="extraBlockType">
    ///     A sixth block type outside the eight the reader measured, NiFloatData or NiBoolData (each is one
    ///     <c>KeyGroup</c> at 20.0.0.4): the type table gains it, block 5 is written as an empty KeyGroup (Num Keys 0,
    ///     four bytes, no Interpolation field) that nothing references, and the footer still names block 0 alone. The
    ///     block-type control: a probe or read deciding from the identity and the root alone would admit it.
    /// </param>
    public static byte[] Build(
        uint version = 0x14000004,
        uint userVersion = 11,
        uint bsVersion = 11,
        uint declaredControlledBlocks = 1,
        string? controllerType = null,
        string? extraBlockType = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);

        string[] blockTypes = extraBlockType is null ? BlockTypes : [.. BlockTypes, extraBlockType];
        WriteHeader(writer, version, userVersion, bsVersion, blockTypes,
            Enumerable.Range(0, blockTypes.Length).Select(static i => (ushort)i));

        var paletteText = TargetName + "\0" + (controllerType is null ? "" : controllerType + "\0");
        var controllerTypeOffset = controllerType is null ? 0xFFFFFFFFu : (uint)(TargetName.Length + 1);

        // Block 0: NiControllerSequence (NiSequence fields, then the 20.0.0.4 tail).
        WriteSizedString(writer, SequenceName);
        writer.Write(declaredControlledBlocks);
        writer.Write(1u); // Array Grow By
        writer.Write(1); // ControlledBlock.Interpolator -> block 1
        writer.Write(-1); // Controller
        writer.Write((byte)7); // Priority (vercond #BSSTREAM#)
        writer.Write(2); // String Palette -> block 2
        writer.Write(0u); // Node Name Offset -> 'Bip01'
        writer.Write(0xFFFFFFFFu); // Property Type Offset (empty sentinel)
        writer.Write(controllerTypeOffset); // Controller Type Offset
        writer.Write(0xFFFFFFFFu); // Controller ID Offset
        writer.Write(0xFFFFFFFFu); // Interpolator ID Offset
        writer.Write(1f); // Weight
        writer.Write(4); // Text Keys -> block 4
        writer.Write(2u); // Cycle Type CYCLE_CLAMP
        writer.Write(1f); // Frequency
        writer.Write(0f); // Start Time
        writer.Write(1f); // Stop Time
        writer.Write(-1); // Manager
        WriteSizedString(writer, TargetName); // Accum Root Name (inline at 20.0.0.4)
        writer.Write(2); // String Palette (since 10.1.0.113 until 20.1.0.0) -> block 2

        // Block 1: NiTransformInterpolator (NiQuatTransform without TRS Valid, then Data).
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f); // Translation
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f); // Rotation (w, x, y, z)
        writer.Write(1f); // Scale
        writer.Write(3); // Data -> block 3

        // Block 2: NiStringPalette (the NUL-terminated entries, then the repeated length).
        var palette = Encoding.ASCII.GetBytes(paletteText);
        writer.Write((uint)palette.Length);
        writer.Write(palette);
        writer.Write((uint)palette.Length);

        // Block 3: NiTransformData (one LINEAR rotation key, one LINEAR translation key, no scales).
        writer.Write(1u); // Num Rotation Keys
        writer.Write(1u); // Rotation Type LINEAR_KEY
        writer.Write(0f); // QuatKey.Time
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f); // QuatKey.Value (w, x, y, z)
        writer.Write(1u); // Translations.Num Keys
        writer.Write(1u); // Translations.Interpolation LINEAR_KEY
        writer.Write(0f); // Key.Time
        writer.Write(2f);
        writer.Write(3f);
        writer.Write(4f); // Key.Value
        writer.Write(0u); // Scales.Num Keys

        // Block 4: NiTextKeyExtraData (empty NiExtraData.Name, two inline-string keys).
        WriteSizedString(writer, "");
        writer.Write(2u); // Num Text Keys
        writer.Write(0f);
        WriteSizedString(writer, "start");
        writer.Write(1f);
        WriteSizedString(writer, "end");

        // Block 5 (the block-type control only): one empty KeyGroup, the whole body of NiFloatData and NiBoolData.
        if (extraBlockType is not null)
        {
            writer.Write(0u); // KeyGroup.Num Keys (Interpolation is present only when Num Keys != 0)
        }

        // Footer.
        writer.Write(1u); // Num Roots
        writer.Write(0); // Roots[0] -> the sequence

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    ///     A 20.0.0.4 (user 11, BS 11) scene graph: one NiNode (nif.xml NiObjectNET at 20.0.0.4: inline Name, Num Extra
    ///     Data List, Controller; NiAVObject: ushort Flags at BS 11, Translation, Rotation, Scale, Num Properties,
    ///     Collision Object since 10.0.1.0; NiNode: Num Children, Num Effects) and a one-root footer. The reader's
    ///     <c>later-cut(2)</c> control: the same identity as the <c>.kf</c> key, but its root is not a sequence.
    /// </summary>
    public static byte[] BuildSceneGraph()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        WriteHeader(writer, 0x14000004, 11, 11, ["NiNode"], [0]);

        // Block 0: NiNode.
        WriteSizedString(writer, SceneGraphNodeName); // NiObjectNET.Name (inline)
        writer.Write(0u); // Num Extra Data List
        writer.Write(-1); // Controller
        writer.Write((ushort)0x000E); // NiAVObject.Flags (ushort up to BS 26)
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f); // Translation
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(1f); // Rotation (identity)
        writer.Write(1f); // Scale
        writer.Write(0u); // Num Properties
        writer.Write(-1); // Collision Object
        writer.Write(0u); // NiNode.Num Children
        writer.Write(0u); // Num Effects

        // Footer.
        writer.Write(1u);
        writer.Write(0);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>The 20.0.0.4 header (no Block Size array, no string table) over the given block-type table.</summary>
    private static void WriteHeader(BinaryWriter writer, uint version, uint userVersion, uint bsVersion,
        IReadOnlyList<string> blockTypes, IEnumerable<ushort> blockTypeIndices)
    {
        var indices = blockTypeIndices.ToArray();
        writer.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.0.0.4\n"));
        writer.Write(version);
        writer.Write((byte)1); // little-endian
        writer.Write(userVersion);
        writer.Write((uint)indices.Length); // Num Blocks
        writer.Write(bsVersion); // BSStreamHeader (user version 3..11 at 10.1.0.0..20.0.0.4)
        WriteExportString(writer, "bmt");
        WriteExportString(writer, ""); // Process Script (BS < 131)
        WriteExportString(writer, ""); // Export Script
        writer.Write((ushort)blockTypes.Count);
        foreach (var type in blockTypes)
        {
            WriteSizedString(writer, type);
        }

        foreach (var index in indices)
        {
            writer.Write(index); // Block Type Index
        }

        writer.Write(0u); // Num Groups
    }

    private static void WriteSizedString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteExportString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value + "\0");
        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
    }
}
