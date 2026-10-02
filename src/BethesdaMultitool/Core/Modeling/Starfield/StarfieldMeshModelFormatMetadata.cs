using System.Globalization;
using Slfx77.Multitool.Core.Models.Catalog;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     The static catalog description of the Starfield <c>.mesh</c> reader (<c>bmt.starfield.mesh</c>) for
///     <c>mesh formats</c>: the variants the probe recognizes, the default units and unit policy
///     (<see cref="StarfieldMeshModelUnits" />), and the admission and mapping rules of the cut-2 plan
///     (<c>docs/design/cut2-starfield-mesh-reader-plan-20260928.md</c>, sections 3 and 4, decisions D1 to D12).
/// </summary>
/// <remarks>
///     Every field stays inside <see cref="ModelStaticAdmissionRule.MaximumTextLength" />; the Shared constructors enforce
///     the limits, so a violation fails the type initializer rather than truncating.
/// </remarks>
internal static class StarfieldMeshModelFormatMetadata
{
    /// <summary>The reader's format id.</summary>
    public const string FormatId = "bmt.starfield.mesh";

    /// <summary>The catalog display name.</summary>
    public const string DisplayName =
        "Starfield .mesh external geometry (geometries\\<hash>\\<hash>.mesh: versions 0, 1 and 2, with or without the " +
        "meshlet and cull tail)";

    /// <summary>
    ///     The largest stream the reader loads (Assumed bound; the largest retail file is 3,690,290 bytes, plan section
    ///     0.2).
    /// </summary>
    public const int MaximumSourceBytes = 16 * 1024 * 1024;

    /// <summary>The variant with the meshlet and cull tail (714,487 retail files).</summary>
    public const string TailVariant = "Starfield .mesh v2 with meshlet tail";

    /// <summary>The variant ending after the LOD section (6,470 retail files).</summary>
    public const string TaillessVariant = "Starfield .mesh v2 without meshlet tail";

    /// <summary>Version 1: the version 2 layout (plan decision D8); no retail file carries it.</summary>
    public const string Version1Variant = "Starfield .mesh v1 (no retail sample)";

    /// <summary>Version 0: no LOD section (plan decision D8); no retail file carries it.</summary>
    public const string Version0Variant = "Starfield .mesh v0, no LOD section (no retail sample)";

    /// <summary>
    ///     The supported variants, in the order <c>mesh formats</c> lists them. Declared before
    ///     <see cref="Description" /> because static initializers run in declaration order and the description reads it.
    /// </summary>
    public static IReadOnlyList<string> Variants { get; } = [TailVariant, TaillessVariant, Version1Variant, Version0Variant];

    /// <summary>The immutable description the reader returns as its format metadata.</summary>
    public static ModelSourceFormatMetadata Description { get; } = Create();

    /// <summary>Builds the description once.</summary>
    private static ModelSourceFormatMetadata Create()
    {
        return new ModelSourceFormatMetadata(DisplayName, Variants, StarfieldMeshModelUnits.Default, UnitPolicy(),
            Rules());
    }

    /// <summary>The unit policy (decision D12).</summary>
    private static string UnitPolicy()
    {
        var row = StarfieldMeshModelUnits.Row;
        return string.Create(CultureInfo.InvariantCulture,
            $"A .mesh belongs only to Starfield, so every document declares the Starfield unit row: " +
            $"{row.MetersPerUnit:R} m per unit, {row.Provenance} ({row.Evidence}). The " +
            $"{BethesdaModelRegistration.GameOption} option (the CLI's --game) may be absent, auto or starfield; any " +
            $"other game throws, because the user asserted a game the file cannot belong to. The source basis is the NIF " +
            $"basis (+Z up, +Y forward, right-handed, Assumed): a .mesh is one level of one BSGeometry shape, in that " +
            $"block's local frame.");
    }

    /// <summary>The ordered admission and mapping rules.</summary>
    private static ModelStaticAdmissionRule[] Rules()
    {
        return
        [
            new ModelStaticAdmissionRule("recognition",
                "Any input",
                "Recognized by content alone, by a bounded structural walk over the probe prefix (there is no magic): " +
                "version 0 to 2; an index count that is a multiple of 3 and leaves room within the declared length for " +
                "the smallest tail-less layout after it (46 bytes, 42 for version 0); a finite positive " +
                "scale; at most 8 weights per vertex; 1 to 65,536 vertices that fit the declared length; every index " +
                "inside the prefix below the vertex count; UV, color, normal and tangent counts of 0 or the vertex " +
                "count; a weight count of vertices x weightsPerVertex; for version 1 and 2 at most 8 LOD lists, each a " +
                "multiple of 3 with every index below the vertex count; a cull count equal to the meshlet count; and " +
                "an end exactly at the last byte. Any violated check is NotAModel. The extension is never consulted."),
            new ModelStaticAdmissionRule("supported",
                "A walk that ends exactly at the last byte, with the meshlet and cull tail or without it",
                "Supported. Confirmed when the stream is complete inside the 64 KiB prefix; Tentative when it extends " +
                "past it (51,604 retail files), and the read then walks the whole stream."),
            new ModelStaticAdmissionRule("tail-rule",
                "A complete stream that ends exactly where the meshlet tail would start while its first normal's 2-bit W " +
                "is 1",
                "Unsupported (truncated) in the probe and invalid data in the read: on every retail file normal W is 1 " +
                "exactly when the tail follows and 0 exactly when it does not (6,470 and 714,487 files, a two-way " +
                "equality), so this is a stream cut at the tail boundary. W 0 with a tail is read, with the " +
                $"{StarfieldMeshModelDiagnostics.TailWithoutNormalW} diagnostic (no retail file)."),
            new ModelStaticAdmissionRule("truncated",
                "A complete stream whose walk runs out after the vertex count validated",
                "Unsupported (truncated); before the vertex count validated it is NotAModel."),
            new ModelStaticAdmissionRule("versions-0-1",
                "Version 0 or 1",
                "Read from the reference layout (NifSkope's BSD-3 MeshFile.cpp as StarfieldMeshFile records it): version " +
                "0 has no LOD section, version 1 is the version 2 layout. No retail file carries either (720,957 of " +
                "720,957 say 2), so only synthetic streams exercise them."),
            new ModelStaticAdmissionRule("geometry",
                "Every recognized stream",
                "One primitive: positions fl32(q x scale / 32767) evaluated in binary64 (one rounding), normals and " +
                "tangents fl32((2v - 1023) / 1023) per 10-bit channel, unnormalized, tangent w glTF's handedness: -1 " +
                "for code 3 and +1 for code 0, the stored bitangent sign negated, because the stored sign puts " +
                "cross(N, T) x w along +dP/dv of the DirectX UVs and glTF's bitangent runs along -dP/dv (codes 1 and 2 " +
                "are invalid data; the stored codes stay in bmt.starfield.mesh.header), UV0 and UV1 the half values " +
                "exactly, triangles the main " +
                "index list as stored (unused vertices kept). A non-finite UV component reads 0 and the set's exact " +
                "half bits are kept in starfield.uv{n}.raw (UInt16 x2)."),
            new ModelStaticAdmissionRule("dec4-sentinel",
                "A normal or tangent stored as (511, 511, 511)",
                "Kept as decoded: length 0.00169 along (-1, -1, -1), what a shader decoding the code sees (inferred); " +
                "counted in bmt.starfield.mesh.dec4-sentinels with the " + StarfieldMeshModelDiagnostics.Dec4Sentinel +
                " diagnostic (plan decision D6)."),
            new ModelStaticAdmissionRule("colors",
                "A stream with vertex colors",
                "The non-primary stream starfield.color (UInt8 x4, normalized, RGBA after the stored BGRA is relaid, " +
                "color space Unknown): the referencing NIF's material decides whether vertex color tints, so the " +
                "portable vertex color stays white (plan decision D4)."),
            new ModelStaticAdmissionRule("weights",
                "A stream with skin weights",
                "Per slot k, starfield.bone.{k} and starfield.weight.{k} (UInt16 x1 each, the weight normalized), values " +
                "exactly as stored, zero slots and repeated bones kept: the joint palette the bones index lives in the " +
                "referencing NIF's BSSkin::Instance, so no SceneSkinInfluences are typed (plan decision D3)."),
            new ModelStaticAdmissionRule("lods",
                "A version 1 or 2 stream with LOD index lists",
                "LOD 0 (the main list) on the first node; each LOD list k on its own node, as a mesh whose primitive " +
                "shares the main primitive's vertex and attribute buffers; one exclusive layer-set group " +
                $"{StarfieldMeshModelLayers.ExclusiveGroup} with lod0 on by default (plan decision D1). An empty list " +
                $"gets a node without a mesh and the {StarfieldMeshModelDiagnostics.EmptyLod} diagnostic."),
            new ModelStaticAdmissionRule("meshlets-cull",
                "A stream with the meshlet and cull tail",
                "NativeOnly: the meshlet records (vertex count, vertex offset, triangle count, triangle offset) and the " +
                "cull records (center + extent) are kept in bmt.starfield.mesh.meshlets and bmt.starfield.mesh.cull, raw " +
                "section bytes with full native detail; neither writer has a carrier."),
            new ModelStaticAdmissionRule("material",
                "Every recognized stream",
                $"One placeholder material {StarfieldMeshModelMaterials.PlaceholderName} (white, opaque, single-sided, " +
                "lit metallic-roughness at metallic 0 and roughness 1) and the " +
                $"{StarfieldMeshModelDiagnostics.MaterialInNif} diagnostic: materials are named by the referencing NIF " +
                "and resolved through Starfield's material database (plan decision D10)."),
            new ModelStaticAdmissionRule("budgets",
                string.Create(CultureInfo.InvariantCulture, $"More than {MaximumSourceBytes} source bytes"),
                "Not supported: refused before the allocation it would need (the largest retail file is 3,690,290 " +
                "bytes)."),
            new ModelStaticAdmissionRule("corrupt-input",
                "A count that runs past the stream, an index count or LOD list that is not a multiple of 3, an index at " +
                "or beyond the vertex count, a stream count other than 0 or the vertex count, a weight count other than " +
                "vertices x weightsPerVertex, a tangent W code of 1 or 2, no indices, or bytes after the walk",
                "The read fails as invalid data naming the field and offset; nothing is fabricated.")
        ];
    }
}
