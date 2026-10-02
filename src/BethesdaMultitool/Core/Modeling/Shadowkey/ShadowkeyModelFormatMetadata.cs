using System.Globalization;
using Slfx77.Multitool.Core.Models.Catalog;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The static catalog descriptions of the two Shadowkey readers, <c>bmt.shadowkey.mesh</c> and
///     <c>bmt.shadowkey.zone</c>, for <c>mesh formats</c>: the variants the probes recognize, the unit row and policy
///     (<see cref="ShadowkeyModelUnits" />), and the admission and mapping rules of the cut-2 plan
///     (<c>docs/design/cut2-shadowkey-reader-plan-20260928.md</c>, sections 3 to 5, decisions D1 to D12).
/// </summary>
/// <remarks>
///     Every field stays inside <see cref="ModelStaticAdmissionRule.MaximumTextLength" />; the Shared constructors enforce
///     the limits, so a violation fails the type initializer rather than truncating.
/// </remarks>
internal static class ShadowkeyModelFormatMetadata
{
    /// <summary>The mesh reader's format id.</summary>
    public const string MeshFormatId = "bmt.shadowkey.mesh";

    /// <summary>The zone reader's format id.</summary>
    public const string ZoneFormatId = "bmt.shadowkey.zone";

    /// <summary>The largest record the mesh reader loads (Assumed; the largest retail record is 257,784 bytes).</summary>
    public const int MaximumMeshBytes = 1024 * 1024;

    /// <summary>The largest <c>.zmp</c> the zone reader loads (Assumed; the largest retail file is 48,524 bytes).</summary>
    public const int MaximumZoneBytes = 1024 * 1024;

    /// <summary>The mesh catalog display name.</summary>
    public const string MeshDisplayName =
        "Shadowkey mesh record (N-Gage, little-endian: one slot of models.huge, entry NNN_name.bin, or a loose " +
        "extracted record)";

    /// <summary>The zone catalog display name.</summary>
    public const string ZoneDisplayName =
        "Shadowkey zone (N-Gage, little-endian: the .zmp grid with its .zcp, .zsk, .ztx, .zlu, .zfg, .ent, .sur, .zon, " +
        ".pth, .stn, .pal and .sta companions, the zone's model list and the models.huge pack)";

    /// <summary>A one-frame record (193 retail).</summary>
    public const string StaticVariant = "Shadowkey mesh record, one frame";

    /// <summary>A keyframe-animated record (33 retail, 2 to 200 frames).</summary>
    public const string AnimatedVariant = "Shadowkey mesh record, keyframe-animated (2 or more frames)";

    /// <summary>A record with several whole-mesh skins (19 retail, 2 to 19 skins).</summary>
    public const string MultiSkinVariant = "Shadowkey mesh record with several skins";

    /// <summary>A zone whose sky payload carries the counted texture header (12 outdoor retail zones).</summary>
    public const string CountedSkyVariant = "Shadowkey zone, outdoor sky (counted texture header)";

    /// <summary>A zone whose sky payload carries the uncounted texture header (9 interior retail zones).</summary>
    public const string UncountedSkyVariant = "Shadowkey zone, interior sky (uncounted texture header)";

    /// <summary>The mesh variants, in the order <c>mesh formats</c> lists them.</summary>
    public static IReadOnlyList<string> MeshVariants { get; } = [StaticVariant, AnimatedVariant, MultiSkinVariant];

    /// <summary>The zone variants, in the order <c>mesh formats</c> lists them.</summary>
    public static IReadOnlyList<string> ZoneVariants { get; } = [CountedSkyVariant, UncountedSkyVariant];

    /// <summary>The mesh reader's description.</summary>
    public static ModelSourceFormatMetadata Mesh { get; } = new(MeshDisplayName, MeshVariants, ShadowkeyModelUnits.Units,
        UnitPolicy(), MeshRules());

    /// <summary>The zone reader's description.</summary>
    public static ModelSourceFormatMetadata Zone { get; } = new(ZoneDisplayName, ZoneVariants, ShadowkeyModelUnits.Units,
        UnitPolicy(), ZoneRules());

    private static string UnitPolicy()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"Every Shadowkey document declares one row: {ShadowkeyModelUnits.MetersPerUnit:R} m per mesh unit (0.5 m per " +
            $"tile, 256 units per tile), Assumed, {ShadowkeyModelUnits.ReverseEngineeringItem} pending; the factor is exact " +
            $"in binary and every coordinate stays an exact integer. The {BethesdaModelRegistration.GameOption} option " +
            $"(the CLI's --game) may be absent, auto or shadowkey; any other game throws. Mesh and sky basis: +Y up, +Z " +
            $"forward, right-handed; zone basis: +Z up, -Y forward, right-handed; all Assumed.");
    }

    private static ModelStaticAdmissionRule[] MeshRules()
    {
        return
        [
            new ModelStaticAdmissionRule("recognition",
                "Any input",
                "Recognized by content alone, by a bounded structural walk over the probe prefix (there is no magic): " +
                "format tag 7, trailer 1, a coordinate count of 3 x vertices, non-zero frame, vertex, UV and face counts, " +
                "sections that fit the declared length, every face index inside the prefix in range, a texture header of " +
                "at least one skin with sides 1 to 1,024, a positive whole number of sequence rows, every sequence " +
                "start < end <= frames with a non-zero rate, and an end exactly at the last byte. The name is never " +
                "consulted. A complete prefix that runs out is NotAModel; an incomplete one is Tentative."),
            new ModelStaticAdmissionRule("vertex-domain",
                "Every record",
                "The UV list is the primitive's vertex domain (decision D2): vertex i is UV index i, at frame 0 of the " +
                "one record vertex every face corner using it names; triangles are the stored UV index triples; point " +
                "indices name the owners (unused record vertices kept in the point count, and their indices and " +
                "per-frame positions in native rows, NativeOnly in the census). A UV owned by two vertices or used by " +
                "no face is invalid data (0 retail)."),
            new ModelStaticAdmissionRule("texture-coordinates",
                "Every record",
                "fl32(U) / fl32(256 x width) and fl32(V) / fl32(256 x height), one correctly rounded division each, exact " +
                "on every retail texture; values past 1 are kept (up to 7.875 x) and the sampler repeats."),
            new ModelStaticAdmissionRule("skins",
                "Every record",
                "One image, material and mesh per skin. The image keeps the stored 0x0RGB block and a lossless 8-bit " +
                "indexed PNG (first-appearance palette, each channel x17); more than 256 colors (none retail) gives an " +
                "RGB PNG. Unlit, opaque, single-sided (Assumed: no field states culling; a closed record wound " +
                "clockwise seen from outside, 4 retail, carries a diagnostic); magenta 0x0F0F stays opaque with a " +
                "diagnostic (decision D3)."),
            new ModelStaticAdmissionRule("skin-alternatives",
                "A record with several skins",
                $"One node and mesh per skin, each a scene root, in the exclusive layer group " +
                $"{ShadowkeyMeshModelLayers.ExclusiveGroup}, skin00 on by default; the per-skin meshes share the morph " +
                $"target objects, and the clips drive skin00 only, so the other skins hold frame 0 (decision D4, the " +
                $"owner's ruling pending). GLB draws the default or, with every alternative, each skin as a scene; an " +
                $"explicit selection of another skin is refused, because its layer selection would leave the animated " +
                $"skin00 undrawn."),
            new ModelStaticAdmissionRule("animation",
                "A record with 2 or more frames",
                "Frames 1 to N-1 are absolute morph targets over the vertex domain (exact integers). One clip per " +
                "sequence (seqNN), driving the default skin: keys at fl32(i) / fl32(rate), one-hot Step weights, a " +
                "derived final key at " +
                "length / rate holding the last pose, no authored duration; the rate read as frames per second, Assumed " +
                "(RE-6), with the raw rate and unit recorded (decision D5). A one-frame record's sequence is native state."),
            new ModelStaticAdmissionRule("budgets",
                string.Create(CultureInfo.InvariantCulture, $"More than {MaximumMeshBytes} source bytes"),
                "Not supported: refused before the read (the largest retail record is 257,784 bytes)."),
            new ModelStaticAdmissionRule("corrupt-input",
                "A header constant that differs, a section that runs past the record, a face or sequence out of range, " +
                "bytes after the sequence table, a UV owned by two vertices, no skin, a texture side of 0 or a sequence " +
                "rate of 0",
                "The read fails as invalid data naming the field and offset; nothing is fabricated.")
        ];
    }

    private static ModelStaticAdmissionRule[] ZoneRules()
    {
        return
        [
            new ModelStaticAdmissionRule("recognition",
                "Any input",
                "Recognized from a .zmp's content: the compressed envelope (u32 inflated length, zlib at byte 4), a " +
                "NUL-terminated printable zone name, a grid of 1 to 4,096 cells a side and 132 + 6 x cells equal to the " +
                "declared length; a complete file must inflate exactly with the stream ending at its last byte " +
                "(Confirmed), an incomplete prefix is Tentative. Only the 21 retail .zmp files pass."),
            new ModelStaticAdmissionRule("companions",
                "Every zone",
                "The companions resolve by stem beside the .zmp, with the zone's model list and the pack globals; a " +
                "required one that is missing or ambiguous, a compressed file that does not inflate exactly, or a " +
                "table that does not tile is invalid data."),
            new ModelStaticAdmissionRule("terrain",
                "Every zone",
                "The one face rule (ShadowkeyTileQuads): floors, ceilings, walls, risers and downstands as one primitive " +
                "each, four vertices per quad in mesh units (exact integers), SceneFaceList quads triangulated (0, 1, 2) " +
                "and (0, 2, 3), the diagonal Assumed; neutral unlit materials per face kind; tiles untextured " +
                "(decision D9, diagnostic)."),
            new ModelStaticAdmissionRule("palette-and-textures",
                "Every zone",
                "A typed ScenePalette per zone (Rgb8, the 768 bytes, alpha 255) with the .zlu as its auxiliary table and " +
                "the magenta entry as its transparent index where the light table pins it to 0x0F0F (13 retail zones, " +
                "Assumed); every .ztx texture as its stored index bytes with an indexed PNG top row first bound to the " +
                "palette; one material per .sur row bound to its texture, used by no primitive (decision D10)."),
            new ModelStaticAdmissionRule("sky",
                "Every zone",
                "The .zsk payload is a mesh record (decision D8): its texture-header variant chosen by length, a 256 x " +
                "256 0x0RGB skin, placed through the mesh-to-zone basis map alone at unit scale with the sky " +
                "presentation."),
            new ModelStaticAdmissionRule("placements",
                "Every .ent row that resolves through entities.txt, the zone's model list and the pack",
                "One placement of its slot's frame-0, skin-0 document through scale(RawScale / 256), (x, y, z) to " +
                "(x, -z, y), yaw -2 pi Angle2 / 65536 and the raw translation, exact on quarter turns; Angle0 and Angle1 " +
                "are native state; animated slots are shown static (decision D6, D7). An unresolved row is native state " +
                "only, with a diagnostic."),
            new ModelStaticAdmissionRule("native-state",
                "Every zone",
                "The header, cells (with each cell's prototype index), every prototype field (the corner heights " +
                "included: the terrain draws only the prototypes an open cell uses), surfaces, fog, triggers, paths, " +
                "locks, .sta records and every placement's raw fields with its entities.txt kind and name are native " +
                "rows; raw bytes only with full native detail."),
            new ModelStaticAdmissionRule("budgets",
                string.Create(CultureInfo.InvariantCulture,
                    $"More than {MaximumZoneBytes} .zmp bytes, a companion over 64 MiB, or more placements than the composer admits"),
                "Not supported: refused before the read or before composing (the worst retail zone places 1,072 meshes).")
        ];
    }
}
