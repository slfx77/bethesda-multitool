using System.Globalization;
using BethesdaMultitool.Core.Games;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Catalog;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The static catalog description of <see cref="NifModelReader" /> for <c>mesh formats</c> (plan section 5, "Debug
///     commands"): the header identities the probe supports, the default units and the unit policy from
///     <see cref="GameProfiles" />, and the admission rules that say what the reader refuses, keeps as native state or
///     types. It declares implemented conditions only; an actual input still needs recognition and a read.
/// </summary>
/// <remarks>
///     <para>
///         Every text is built from the constants the probe, the reader, the units resolver and the coverage table use,
///         so a variant string is exactly the probe's evidence description and a native-only reason is exactly the
///         coverage reason. The interim skin, billboard and switch reasons (slices 7 and 8) are stated in words here,
///         because those constants are retired when their slices land; update the two rules with them. Cut-1b slice 10
///         adds the <c>.kf</c> variants (the probe's evidence with <see cref="NifModelProbe.AnimationStreamSuffix" />), the
///         <c>.kf</c> admission rule and the typed animation rule, and retires the 'later-cut(1b)' rules.
///     </para>
///     <para>
///         Every field stays inside <see cref="ModelStaticAdmissionRule.MaximumTextLength" /> and at most
///         <see cref="ModelSourceFormatMetadata.MaximumVariants" /> variants and
///         <see cref="ModelSourceFormatMetadata.MaximumRules" /> rules are declared; the Shared constructors enforce the
///         limits, so a violation fails the type initializer rather than truncating.
///     </para>
/// </remarks>
internal static class NifModelFormatMetadata
{
    /// <summary>The catalog display name.</summary>
    public const string DisplayName =
        "Gamebryo NIF (20.2.0.7, user 11: scene graphs at BS 14, 21, 26, 32 and 34; .kf animation streams at BS 14, " +
        "21, 24 to 28 and 30 to 34; 20.0.0.4, user 10 or 11, BS 11, little-endian: .kf animation streams)";

    /// <summary>The immutable description the reader returns as its format metadata.</summary>
    public static ModelSourceFormatMetadata Description { get; } = Create();

    /// <summary>
    ///     The supported variants, spelled exactly as the probe's evidence: one per scene-graph BS version and byte order
    ///     (<see cref="NifModelProbe.Describe" />, for example <c>NIF 20.2.0.7, user 11, BS 34, little-endian</c>), then one
    ///     per <c>.kf</c> BS version and byte order (<see cref="NifModelProbe.DescribeAnimationStream" />, for example
    ///     <c>NIF 20.2.0.7, user 11, BS 24, little-endian, .kf animation stream</c>), then (cut 2) one per user version
    ///     of the little-endian 20.0.0.4 <c>.kf</c> key (<c>NIF 20.0.0.4, user 10, BS 11, little-endian, .kf animation stream</c>).
    /// </summary>
    public static IReadOnlyList<string> Variants()
    {
        var variants = new List<string>(
            (NifModelProbe.SupportedBsVersions.Count + NifModelProbe.AnimationStreamBsVersions.Count) * 2 +
            NifModelProbe.LegacyKfUserVersions.Count);
        foreach (var bs in NifModelProbe.SupportedBsVersions)
        {
            variants.Add(NifModelProbe.Describe(NifModelProbe.SupportedVersion, NifModelProbe.SupportedUserVersion, bs,
                false));
            variants.Add(NifModelProbe.Describe(NifModelProbe.SupportedVersion, NifModelProbe.SupportedUserVersion, bs,
                true));
        }

        foreach (var bs in NifModelProbe.AnimationStreamBsVersions)
        {
            variants.Add(NifModelProbe.DescribeAnimationStream(NifModelProbe.SupportedVersion,
                NifModelProbe.SupportedUserVersion, bs, false));
            variants.Add(NifModelProbe.DescribeAnimationStream(NifModelProbe.SupportedVersion,
                NifModelProbe.SupportedUserVersion, bs, true));
        }

        foreach (var userVersion in NifModelProbe.LegacyKfUserVersions)
        {
            variants.Add(NifModelProbe.DescribeAnimationStream(NifModelProbe.LegacyKfVersion, userVersion,
                NifModelProbe.LegacyKfBsVersion, false));
        }

        return variants.AsReadOnly();
    }

    /// <summary>Builds the description once.</summary>
    private static ModelSourceFormatMetadata Create()
    {
        // The units the reader declares when no game is established: exactly what the resolver returns for no option.
        var defaultUnits = NifModelUnits.Resolve(new Dictionary<string, string>(StringComparer.Ordinal));
        return new ModelSourceFormatMetadata(DisplayName, Variants(), defaultUnits, UnitPolicy(), Rules());
    }

    /// <summary>The unit policy: how <c>--game</c> selects a profile's units, with the FNV and FO3 values and evidence.</summary>
    private static string UnitPolicy()
    {
        var fnv = GameProfiles.For(BethesdaGame.FalloutNewVegas).Units;
        var fo3 = GameProfiles.For(BethesdaGame.Fallout3).Units;
        return string.Create(CultureInfo.InvariantCulture,
            $"The {BethesdaModelRegistration.GameOption} option (the CLI's --game) selects the game whose GameProfiles " +
            $"units apply, with that profile's provenance and evidence. " +
            $"fnv (FalloutNewVegas): {fnv.MetersPerUnit:R} m per unit, " +
            $"{NifModelUnits.ToSceneProvenance(fnv.Provenance)}: {fnv.Evidence}. " +
            $"fo3 (Fallout3): {fo3.MetersPerUnit:R} m per unit, " +
            $"{NifModelUnits.ToSceneProvenance(fo3.Provenance)}: {fo3.Evidence}. " +
            $"The other Gamebryo 20.x game names (Oblivion, Skyrim, Fallout4, Fallout76, Starfield) select their own " +
            $"profiles. Omitted or auto, the game is not established and FNV's value is used as Assumed " +
            $"({NifModelUnits.AssumedGameEvidence}). Any other value is refused, never replaced by a default. The " +
            $"basis is +Z up, +Y forward, right-handed (Assumed) and is not normalized by the reader. The " +
            $"{BethesdaModelRegistration.PlatformOption} option (the CLI's --platform: {NifPackedPlatformOption.X360Value} " +
            $"or {NifPackedPlatformOption.Ps3Value}) names the console a big-endian file was shipped for; it selects only " +
            $"the byte order of packed vertex colors ({NifPackedPlatformOption.X360ColorByteOrder} on X360, " +
            $"{NifPackedPlatformOption.Ps3ColorByteOrder} on PS3, measured 2026-09-24 on L1 and L4; L6's order is " +
            $"inferred from them, every retail L6 vertex being white, and its colors carry Assumed provenance with a " +
            $"{NifPackedGeometryReader.ColorOrderInferredDiagnostic} diagnostic under either platform) and the Squad " +
            $"policy of a big-endian file's TBC and QUADRATIC quaternion rotations. Omitted, X360 is assumed and every " +
            $"packed color stream typed into a primitive gets a {NifPackedGeometryReader.PlatformAssumedDiagnostic} " +
            $"diagnostic. A .kf animation stream's clips are in the units of the skeleton they bind to.");
    }

    /// <summary>The ordered admission rules.</summary>
    private static ModelStaticAdmissionRule[] Rules()
    {
        var culture = CultureInfo.InvariantCulture;
        var supportedBs = string.Join(", ", NifModelProbe.SupportedBsVersions);
        var animationOnlyBs = string.Join(", ", NifModelProbe.AnimationStreamOnlyBsVersions);
        var streamBs = string.Join(", ", NifModelProbe.AnimationStreamBsVersions);
        var supportedVersion = NifModelProbe.FormatVersion(NifModelProbe.SupportedVersion);
        var ddxFormats = string.Join(", ", NifDdxGate.AdmittedFormats.Select(format => "0x" + format.ToString("X2", culture)));
        return
        [
            new ModelStaticAdmissionRule("recognition",
                "Any input",
                "Recognized by content alone: a \"Gamebryo File Format, Version \" or \"NetImmerse File Format, " +
                "Version \" header line ending in a newline within the first 60 bytes. The file extension is never " +
                "consulted."),
            new ModelStaticAdmissionRule("supported-key",
                string.Create(culture,
                    $"NIF {supportedVersion}, user version {NifModelProbe.SupportedUserVersion}, BS {supportedBs}, " +
                    $"little- or big-endian, whose roots are not animation sequences (a scene graph)"),
                "Supported. The probe is Confirmed when the whole header lies inside the 64 KiB probe prefix and " +
                "Tentative otherwise; a truncated or malformed header stays Supported so that the read reports the " +
                "exact corruption."),
            new ModelStaticAdmissionRule("kf-stream",
                string.Create(culture,
                    $"NIF {supportedVersion}, user version {NifModelProbe.SupportedUserVersion}, BS {streamBs}, little- " +
                    $"or big-endian, whose every footer root is an NiControllerSequence (a .kf animation stream; block 0 " +
                    $"stands in when the footer lies beyond the probe prefix)"),
                string.Create(culture,
                    $"Supported; the probe evidence ends in \"{NifModelProbe.AnimationStreamSuffix}\". The skeleton is the " +
                    $"file the {BethesdaModelRegistration.SkeletonOption} option (the CLI's --skeleton) names, else the " +
                    $"nearest ancestor {NifModelSkeletonResolver.SkeletonFileName} of the stream's virtual path, both " +
                    $"through the companion resolver: exact names, nearest wins, no compatibility gate. Its nodes are read " +
                    $"by the scene-graph rules, without its geometry, collision, materials or skins. The clips bind " +
                    $"within it by exact name bytes; a name it lacks keeps that track native " +
                    $"(\"{NifModelTargetNames.TargetNotInSkeletonReason}\"). The document is the skeleton's nodes and the " +
                    $"clips, with a {NifModelSkeletonProvenance.Kind} Document row (path, SHA-256, rule, candidates, bound " +
                    $"and unbound names). Not supported: no skeleton (\"{NifModelSkeletonResolver.NoSkeletonReason}\"), an " +
                    $"explicit skeleton that names nothing, an ambiguous, unreadable or out-of-scope skeleton, and a " +
                    $"stream whose clips drive nothing (\"{NifModelAnimationStreamReader.NoClipExpressibleReason}: \" and " +
                    $"the first blocking reasons).")),
            new ModelStaticAdmissionRule("animation-stream-key",
                string.Create(culture,
                    $"NIF {supportedVersion}, user version {NifModelProbe.SupportedUserVersion}, BS {animationOnlyBs}, " +
                    $"whose roots are not animation sequences"),
                string.Create(culture,
                    $"Unsupported with the reason {NifModelProbe.AnimationStreamKeyCategory}: these BS versions are read " +
                    $"only as .kf animation streams. When the footer lies beyond the probe prefix the probe is Supported " +
                    $"and Tentative (block 0 is only a convention) and the read refuses such a file.")),
            new ModelStaticAdmissionRule("mixed-roots",
                "Footer roots that mix an NiSequence with other blocks, or an NiSequence that is not an NiControllerSequence",
                string.Create(culture,
                    $"Unsupported with the reason {NifModelProbe.AnimationStreamKeyCategory}: a .kf stream is read only " +
                    $"when every root is an NiControllerSequence.")),
            new ModelStaticAdmissionRule("kf-stream-20004",
                string.Create(culture,
                    $"NIF {NifModelProbe.FormatVersion(NifModelProbe.LegacyKfVersion)}, user version " +
                    $"{string.Join(" or ", NifModelProbe.LegacyKfUserVersions)}, BS {NifModelProbe.LegacyKfBsVersion}, " +
                    $"little-endian, whose block 0 (the probe: its header has no Block Size array, so the footer is out of " +
                    $"reach) and every footer root (the read) is an NiControllerSequence, and whose every block uses one " +
                    $"of the eight types measured on the five FNV-shipped 20.0.0.4 .kf " +
                    $"({string.Join(", ", NifModelProbe.LegacyKfBlockTypes)}): the Oblivion-era .kf identity, bounded " +
                    $"to the measured layouts (cut 2)"),
                string.Create(culture,
                    $"Supported as a .kf animation stream by the kf-stream rule, its blocks sized by NifParser's legacy " +
                    $"measure walk. The sequence Name and Accum Root Name are inline strings and each controlled block " +
                    $"names its five strings through an NiStringPalette; the reader builds the file's string table from " +
                    $"them and binds, maps and records the clip exactly as for a 20.2.0.7 stream, the clip extras and " +
                    $"native row keeping the stored palette offsets. An NiStringPalette a bound controlled block " +
                    $"resolved through is Typed; one no binding used keeps the reason " +
                    $"\"{NifModelAnimationCoverage.StringPaletteReason}\".")),
            new ModelStaticAdmissionRule("later-cut-2-version",
                "NIF 20.0.0.4 outside that key (a scene graph, another user or BS version, big-endian order, or a .kf " +
                "stream with a block type outside the eight measured ones, such as the NiFloatData, NiBoolData and " +
                "NiBSplineCompFloatInterpolator of 17 in a 600-file Oblivion sample)",
                string.Create(culture,
                    $"Unsupported with the reason {NifModelProbe.LaterCutTwoCategory}: the Oblivion-era meshes, and the " +
                    $"Oblivion .kf block types not yet measured at this version, are read from cut 2 as they are " +
                    $"measured; only the little-endian .kf animation stream at user 10 or 11, BS 11 over the eight " +
                    $"measured types is read.")),
            new ModelStaticAdmissionRule("other-version",
                "Any other NIF version, user version or BS version",
                string.Create(culture,
                    $"Unsupported with the reason other version: outside the reader's keys ({supportedVersion}, user " +
                    $"{NifModelProbe.SupportedUserVersion}: BS {supportedBs} for a scene graph, BS {streamBs} for a .kf " +
                    $"animation stream; and the little-endian 20.0.0.4 .kf animation stream at user 10 or 11, BS 11).")),
            new ModelStaticAdmissionRule("root-type",
                "A footer root that is not an NiAVObject, when no root is an animation sequence",
                "The read fails as invalid data."),
            new ModelStaticAdmissionRule("budgets",
                string.Create(culture,
                    $"More than {NifModelReader.MaximumSourceBytes} source bytes, more than " +
                    $"{NifModelReader.MaximumBlocks} blocks, or more native-state rows (header, blocks, primitives, " +
                    $"materials, images, skins, one per clip and a .kf's skeleton row) than the " +
                    $"{ModelDocument.MaximumNativeStates} a document allows"),
                "Not supported: refused before the allocation it would need, or as soon as the row count is known."),
            new ModelStaticAdmissionRule("corrupt-input",
                "A header, block layout or footer inconsistent with the file, or a strictly decoded block (NiNode " +
                "subclasses, the placed geometry types, their geometry data, NiMorphData and " +
                "BSPackedAdditionalGeometryData) that does not decode exactly",
                "The read fails as invalid data; nothing is fabricated."),
            new ModelStaticAdmissionRule("partial-decode",
                "Any other block that decodes only partially",
                string.Create(culture,
                    $"Kept as native state with a {NifModelReader.DecodeIncompleteDiagnostic} diagnostic. A property, " +
                    $"texture or skin block that the reader types must still decode exactly.")),
            new ModelStaticAdmissionRule("geometry",
                "NiTriShape, BSSegmentedTriShape and NiTriStrips placed in the hierarchy",
                "Typed: stored positions, normals, colors, texture coordinates, triangles (strips triangulated with " +
                "parity) and morph targets. Repeated-index triangles are dropped and counted. The stored tangent " +
                "frame becomes glTF-defined tangents: xyz is the stored Bitangents array, raw (it runs along +dP/du; " +
                "nif.xml's Tangents runs along +dP/dv), and w = sign(dot(cross(N, Bitangents), -Tangents)) per vertex; " +
                "both arrays stay native state under their nif.xml names. A geometry without a " +
                string.Create(culture,
                    $"vertex or triangle is native only (\"{NifModelCoverage.EmptyGeometryReason}\") and its node stays " +
                    $"placed.")),
            new ModelStaticAdmissionRule("packed-geometry",
                string.Create(culture,
                    $"Geometry whose data names a {NifModelGeometryReader.PackedAdditionalDataType} (the X360 and PS3 " +
                    $"packed streams)"),
                string.Create(culture,
                    $"Typed when the block's ordered (Type, Unit Size, Block Offset) list and stride match one of the " +
                    $"six measured layouts (L1 static with colors, L2 static, L3 skinned, L4 skinned with colors, L5 " +
                    $"and L6 interface with float normals): positions, normals, UV set 0 and the tangent frame as " +
                    $"exactly widened big-endian halves or copied floats (the fourth half of each half4 channel is a " +
                    $"constant 1.0, recorded, never the bitangent sign; the lower-offset Bitangent channel is the tangent " +
                    $"xyz and the Tangent channel enters only w, exactly as the PC arrays), colors as bytes / 255 in " +
                    $"the platform's " +
                    $"byte order, triangles from the data block (static: packed vertex i is shape vertex i) or from the " +
                    $"NiSkinPartition offset per partition (skinned: the vertex domain is the concatenated vertex maps, " +
                    $"stated by point indices, never welded). L6's color byte order is inferred from L1 and L4 (every " +
                    $"retail L6 vertex is white), so its colors carry Assumed provenance and a " +
                    $"{NifPackedGeometryReader.ColorOrderInferredDiagnostic} diagnostic. Native only, with the " +
                    $"reason: an unknown stream table (\"{NifModelCoverage.PackedLayoutUnknownReason}\"; nothing is " +
                    $"guessed from a vector length), an empty, absent or count-inconsistent payload, or a shape " +
                    $"vertex repeated across partitions with differing positions " +
                    $"(\"{NifModelCoverage.PackedPayloadReason}\"), a skinned shape on a weightless layout " +
                    $"(\"{NifModelCoverage.PackedSkinnedStaticLayoutReason}\"), or zero declared vertices " +
                    $"(\"{NifModelCoverage.EmptyGeometryReason}\", as inline). The layout id, stride, platform, " +
                    $"fourth-half census, sentinel and slot-3 counts and the color-order-sensitive vertex count are " +
                    $"native state.")),
            new ModelStaticAdmissionRule("additional-geometry",
                "Geometry data naming an NiAdditionalGeometryData (PC landscape LOD)",
                string.Create(culture,
                    $"The inline streams are typed; the additional block is native only " +
                    $"(\"{NifModelCoverage.AdditionalGeometryReason}\").")),
            new ModelStaticAdmissionRule("skin",
                "NiSkinInstance, BSDismemberSkinInstance, NiSkinData and NiSkinPartition on placed geometry",
                string.Create(culture,
                    $"Typed: one skin per skinned shape, its bones as joints (Joint role) under the skeleton root, " +
                    $"inverse binds exactly S*R^T*T per bone (never orthonormalized) and influences as authored in " +
                    $"NiSkinData (padded to the widest vertex, never normalized), else from the partitions. The overall " +
                    $"skin transform stays native state with a diagnostic when it is not the identity. Dismember body " +
                    $"parts become face streams when the partitions reproduce the triangles exactly. Not typed, with a " +
                    $"reason: no vertex weights anywhere (\"{NifModelCoverage.SkinNoInfluencesReason}\"), a null or " +
                    $"unplaced bone (\"{NifModelCoverage.SkinUnplacedBoneReason}\"), too many influences per vertex " +
                    $"(\"{NifModelCoverage.SkinStrideReason}\"), non-finite " +
                    $"values (\"{NifModelCoverage.SkinNonFiniteReason}\"), joints under several roots " +
                    $"(\"{NifModelCoverage.SkinRootsReason}\"), and the skin blocks of packed geometry that was not " +
                    $"typed (its packed reason). Packed console skins (layouts L3 and L4) take their influences from " +
                    $"the packed weight and bone-index channels: stride 4 in packed order, slot k = the owning " +
                    $"partition's bone list at byte 3 - k, a slot-3 weight of exactly 1.0 beside three weights summing " +
                    $"to 1 read as the sentinel 0, never renormalized; the console NiSkinData stores no weights and is " +
                    $"not used. A repeated or ambiguous bone fails the read.")),
            new ModelStaticAdmissionRule("billboard-switch-lod",
                "NiBillboardNode, NiSwitchNode, NiLODNode and hidden nodes",
                string.Create(culture,
                    $"Typed: billboard modes 0-5 and 9 become an aim with Front, Up and Roll left unknown (the NIF " +
                    $"convention is not established); any other mode keeps its raw value with no aim and a diagnostic. " +
                    $"A switch is one exclusive group of layer sets, one per child ordinal, with the stored child on " +
                    $"(null children keep their ordinals); " +
                    $"an LOD node puts its finest level on and keeps its ranges native " +
                    $"(\"{NifModelCoverage.LodDataReason}\"); a hidden node is in a default-off set.")),
            new ModelStaticAdmissionRule("other-node",
                "Any other NiNode subclass",
                string.Create(culture, $"Native only (\"{NifModelCoverage.OtherNodeReason}\").")),
            new ModelStaticAdmissionRule("havok",
                "bhk* blocks and hkPackedNiTriStripsData",
                string.Create(culture, $"Native only (\"{NifModelCoverage.HavokReason}\").")),
            new ModelStaticAdmissionRule("animation",
                "Controllers, interpolators, sequences, object palettes, keyframe data and other animation data inside " +
                "a mesh",
                string.Create(culture,
                    $"Typed into clips: one per NiControllerSequence in its manager's list order, then one " +
                    $"\"{NifModelAnimationReader.ControllersClipName}\" clip for the active, free-running transform, morph, " +
                    $"property and visibility controllers, each with its clock, events and source policy and a " +
                    $"{NifModelAnimationNativeState.Kind} native row. Transform channels (quaternion, Euler, and TBC or " +
                    $"QUADRATIC rotations as Gamebryo Squad under the platform's policy), morph weights where the mesh " +
                    $"carries the typed targets (\"{NifModelAnimationReasons.MorphTargetsNotTyped}\" otherwise), and " +
                    $"material, texture-transform, UV and visibility tracks where a typed material or node is the target. " +
                    $"A block or channel outside the Shared vocabulary stays native with its plan 2.1 reason (for example " +
                    $"\"{NifModelAnimationCoverage.RefractionReason}\" or " +
                    $"\"{NifModelAnimationReasons.BlendState}\"), and a {NifModelAnimationDiagnostics.NativeBlocksDiagnostic} " +
                    $"diagnostic summarizes them. An object palette that names an otherwise unnamed node, or binds a " +
                    $"target, is Typed.")),
            new ModelStaticAdmissionRule("particles-cameras-lights",
                "Particle systems and their modifiers, cameras and lights",
                string.Create(culture,
                    $"Native only (\"{NifModelCoverage.ParticleNodeReason}\", \"{NifModelCoverage.ParticleReason}\", " +
                    $"\"{NifModelCoverage.CameraLightReason}\").")),
            new ModelStaticAdmissionRule("other-native",
                "Water shading, NiFogProperty, NiPixelData, multibound data, furniture markers, extra data and any " +
                "other block type",
                string.Create(culture,
                    $"Native only with a named reason (\"{NifModelCoverage.WaterReason}\", " +
                    $"\"{NifModelCoverage.NoVocabularyReason}\", \"{NifModelCoverage.PixelDataReason}\", " +
                    $"\"{NifModelCoverage.MultiBoundReason}\", \"{NifModelCoverage.MarkerReason}\", " +
                    $"\"{NifModelCoverage.ExtraDataReason}\", \"{NifModelCoverage.FallbackReason}\").")),
            new ModelStaticAdmissionRule("unreachable",
                "A block that no footer root reaches, or an NiAVObject reached only outside the Children hierarchy",
                string.Create(culture,
                    $"Native only (\"{NifModelCoverage.UnreachableReason}\" or " +
                    $"\"{NifModelCoverage.OutsideHierarchyReason}\").")),
            new ModelStaticAdmissionRule("materials",
                "Properties inherited by drawable placed geometry: alpha, stencil, z-buffer, vertex color, material, " +
                "the Bethesda shader properties and NiTexturingProperty",
                "Typed into one material per placement, with the render state, the texture layers and the source " +
                string.Create(culture,
                    $"scalars; a property no drawable placement inherits is native only " +
                    $"(\"{NifModelCoverage.UninheritedPropertyReason}\"). With a typed Bethesda shader and no " +
                    $"NiVertexColorProperty, vertex colors modulate ambient and diffuse and vertex alpha is opacity " +
                    $"(TallGrass: a wind weight), whatever SF2 bit 5 says (Assumed; BMT's renderer policy); with " +
                    $"neither, the material declares no vertex-color use.")),
            new ModelStaticAdmissionRule("textures",
                "A texture-set slot or NiSourceTexture path bound by a typed material",
                "Resolved through the data roots (--data-root, else the nearest ancestor of the input holding a " +
                "textures folder; loose files over archives; a missing .dds falls back to .ddx), else by basename " +
                "within the input's own source. Each authored string is one image named by it, deduplicated by " +
                "SHA-256. A missing or ambiguous path is a missing image with its reason and a " +
                string.Create(culture,
                    $"{NifModelTextureSource.MissingDiagnostic} diagnostic; an unbound texture block is native only " +
                    $"(\"{NifModelCoverage.UnboundTextureReason}\").")),
            new ModelStaticAdmissionRule("dds",
                "A DDS companion",
                "The original bytes are kept. The descriptor comes from Shared's DDS inspection (header, extent and " +
                "the BC1 selector scan, never a pixel decode), or from BMT's minimal header when the inspection " +
                string.Create(culture,
                    $"refuses the surface ({NifModelTextureSource.DdsHeaderDiagnostic}). BC5 declares an Assumed " +
                    $"positive-hemisphere Z reconstruction.")),
            new ModelStaticAdmissionRule("ddx-gate",
                "An Xbox 360 DDX companion",
                string.Create(culture,
                    $"The original bytes are kept. Unreadable header: no descriptor " +
                    $"({NifModelTextureSource.DdxHeaderDiagnostic}). The DDXConv relayout throws or exceeds the " +
                    $"companion budget: the original only ({NifModelTextureSource.DdxRelayoutDiagnostic}). The gate " +
                    $"passes (the untile is a permutation; no truncated reads, padded bytes, dropped trailing blocks, " +
                    $"atlas fallbacks or inferred mip-tail blocks; format byte {ddxFormats}; Shared's DDS inspection " +
                    $"admits the output; its extent and trimmed mip count equal the header's): a DDS standard payload " +
                    $"(\"{NifModelTextureSource.RelayoutNote}\"). The gate fails: a " +
                    $"{NifModelTextureSource.DdxRecoveryRecipe} derivation naming every failed check " +
                    $"({NifModelTextureSource.DdxGateDiagnostic}).")),
            new ModelStaticAdmissionRule("xbox-specular",
                "A normal-map slot whose file resolved to a DDX and whose path follows the _n convention",
                "The _s specular companion is resolved as its own image (origin PlatformCompanion) when present; BC5 " +
                "and BC4 are never merged."),
            new ModelStaticAdmissionRule("pixel-free-inspection",
                "Inspection (mesh info, mesh validate)",
                "Reads exactly what conversion reads and decodes no pixel: image work is header inspection, the BC1 " +
                "selector scan and the DDX block relayout. Writer fidelity that needs pixels is resolved only by mesh " +
                "fidelity, through the writer's own preparation.")
        ];
    }
}
