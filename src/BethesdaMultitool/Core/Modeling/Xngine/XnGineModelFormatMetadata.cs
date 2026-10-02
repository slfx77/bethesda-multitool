using System.Globalization;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling.Units;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Catalog;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The static catalog description of the XnGine <c>.3D</c> reader (<c>bmt.xngine.3d</c>) for <c>mesh formats</c>:
///     the variants the probe recognizes, the default units and the unit policy from <see cref="ClassicModelUnits" />
///     (cut-1c plan section 6.3), and the admission rules of plan sections 6.1 and 6.2 (owner decisions D7 and D8).
///     Every text is built from the constants the identity chain, the content facts and the unit rows use, so a rule
///     names exactly the option keys, diagnostics and factors the reader applies.
/// </summary>
/// <remarks>
///     <para>
///         Slice 3 staged this description ahead of the reader (plan section 8); slice 5's probe and reader
///         (<see cref="XnGineModelProbe" />, <see cref="XnGineModelReader" />) keep its rules true, in particular the
///         variant strings, which are the probe evidence without its counts (<see cref="Describe" /> builds the full
///         evidence, <see cref="DescribeVariant" /> the variant). Slice 5 registers the reader, so <c>mesh formats</c>
///         lists this description beside the NIF reader's.
///     </para>
///     <para>
///         Every field stays inside <see cref="ModelStaticAdmissionRule.MaximumTextLength" /> and at most
///         <see cref="ModelSourceFormatMetadata.MaximumVariants" /> variants and
///         <see cref="ModelSourceFormatMetadata.MaximumRules" /> rules are declared; the Shared constructors enforce the
///         limits, so a violation fails the type initializer rather than truncating.
///     </para>
/// </remarks>
internal static class XnGineModelFormatMetadata
{
    /// <summary>The reader's format id.</summary>
    public const string FormatId = "bmt.xngine.3d";

    /// <summary>The catalog display name.</summary>
    public const string DisplayName =
        "XnGine .3D mesh (v2.5, v2.6, v2.7: Daggerfall ARCH3D records, Battlespire 3D.BSA and 3D.BS6 entries and " +
        "loose files, Redguard 3dart loose files and ROB segments)";

    /// <summary>
    ///     The largest record the reader loads (plan section 2), an assumed bound: the largest measured static record is
    ///     3D.BSA NONAME01.3D at 226,824 decoded bytes, the largest ROB segment MKTEST.ROB ORARY at 169,062 (slice-5
    ///     review receipt <c>measure_review_fixes.json</c>).
    /// </summary>
    public const int MaximumSourceBytes = 16 * 1024 * 1024;

    /// <summary>The probe's decline reason for a Redguard 3dfx mesh (plan section 6.1, D8).</summary>
    public const string FxartUnsupportedReason =
        "Redguard 3dfx mesh (10-byte plane header, point indices) not decoded: later cut";

    /// <summary>The immutable description the reader returns as its format metadata.</summary>
    public static ModelSourceFormatMetadata Description { get; } = Create();

    /// <summary>
    ///     The supported variants: one per mesh tag and plane-header size, spelled as the probe evidence without its
    ///     counts (<see cref="DescribeVariant" />), for example <c>XnGine v2.7 mesh, 8-byte plane headers</c>.
    /// </summary>
    public static IReadOnlyList<string> Variants()
    {
        var variants = new List<string>(XnGineContentFacts.MeshTags.Count * 2);
        foreach (var tag in XnGineContentFacts.MeshTags)
        {
            variants.Add(DescribeVariant(tag, XnGineContentFacts.DaggerfallPlaneHeaderLength));
            variants.Add(DescribeVariant(tag, XnGineContentFacts.BattlespirePlaneHeaderLength));
        }

        return variants.AsReadOnly();
    }

    /// <summary>The variant text for a tag and plane-header size: <c>XnGine v2.7 mesh, 8-byte plane headers</c>.</summary>
    public static string DescribeVariant(string tag, int planeHeaderLength)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return string.Create(CultureInfo.InvariantCulture, $"XnGine {tag} mesh, {planeHeaderLength}-byte plane headers");
    }

    /// <summary>
    ///     The probe evidence for a recognized record (plan section 6.1): the variant plus the header's point and plane
    ///     counts, for example <c>XnGine v2.7 mesh, 8-byte plane headers, 48 points, 34 planes</c>.
    /// </summary>
    public static string Describe(string tag, int planeHeaderLength, int pointCount, int planeCount)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{DescribeVariant(tag, planeHeaderLength)}, {pointCount} points, {planeCount} planes");
    }

    /// <summary>Builds the description once.</summary>
    private static ModelSourceFormatMetadata Create()
    {
        // The units the reader declares when no game is established: exactly the guard row the chain returns.
        var defaultUnits = ClassicModelUnits.For(BethesdaGame.Unknown);
        return new ModelSourceFormatMetadata(DisplayName, Variants(), defaultUnits, UnitPolicy(), Rules());
    }

    /// <summary>The unit policy: the identification chain and the unit rows it selects.</summary>
    private static string UnitPolicy()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"The game is established by a chain that stops at the first step that answers, with the evidence " +
            $"recorded on the document: (1) the {BethesdaModelRegistration.GameOption} option (the CLI's --game: " +
            $"{XnGineGameIdentity.GameOptionValues}), a user assertion that throws when it names no XnGine game or one " +
            $"whose plane headers the record does not walk with; (2) the container the entry came from, through the " +
            $"source's container facts (a numbered XnGine BSA is Daggerfall and names the object id, a named XnGine " +
            $"BSA with LZSS entries is Battlespire, a ROB is Redguard); (3) the {BethesdaModelRegistration.ClassicGameOption} " +
            $"option the shell sets from the install walk-up (ClassicGameLocator.DetectRootForFile) unless --game was " +
            $"given, a key separate from {BethesdaModelRegistration.GameOption} so a detected classic install never " +
            $"reaches a NIF read; (4) the content: walks only with 10-byte plane headers is Battlespire, walks only with " +
            $"8-byte headers and header +20 non-zero is Redguard, and 8-byte headers with +20 = 0 (every extracted " +
            $"ARCH3D record and 315 ROB meshes) stays Unknown with the {XnGineGameIdentity.AmbiguousDiagnostic} " +
            $"diagnostic naming --game. A container or install answer the content refutes is skipped with " +
            $"{XnGineGameIdentity.StepRefutedDiagnostic}. The game selects its unit row, every row Assumed: " +
            $"{ClassicModelUnits.DescribeRows()}. Unknown keeps factor 1 with Unknown provenance (the guard) and no " +
            $"unfold is applied. A Redguard .3DC document takes the actor row and carries the " +
            $"{ClassicModelUnits.ActorScaleDiagnostic} diagnostic.");
    }

    /// <summary>The ordered admission rules.</summary>
    private static ModelStaticAdmissionRule[] Rules()
    {
        var culture = CultureInfo.InvariantCulture;
        var tags = string.Join(", ", XnGineContentFacts.MeshTags);
        return
        [
            new ModelStaticAdmissionRule("recognition",
                "Any input",
                string.Create(culture,
                    $"Recognized by content alone: a 4-byte tag among {tags}, a 64-byte header whose point, normal and " +
                    $"plane lists fit, and a plane walk that succeeds with 8-byte or 10-byte plane headers (every corner " +
                    $"addresses one of the header's points; v2.5 stores point offsets divided by three). The file " +
                    $"extension is never consulted.")),
            new ModelStaticAdmissionRule("supported-key",
                string.Create(culture, $"Tag {tags}, lists that fit, and a plane walk under 8- or 10-byte headers"),
                "Supported. The probe is Confirmed when the record is complete inside the 64 KiB probe prefix and " +
                "Tentative otherwise (40 retail static meshes exceed the prefix: 4 ARCH3D records, 10 3D.BSA and 4 " +
                "3D.BS6 entries by their decoded size, and 22 ROB segments); the read then walks the whole record."),
            new ModelStaticAdmissionRule("3dc-shape",
                "A record satisfying the Redguard .3DC shape test: header +16 > 0, the +20 frame block inside the " +
                "prefix, a frame table at or before the plane list dividing into 3- or 4-dword records per frame",
                "NotAModel for this reader: the record belongs to the bmt.redguard.3dc reader alone, so no file is " +
                "recognized twice (147 of 147 .3DC files satisfy the test, no static mesh does, and MENU.ROB's four " +
                "+16 = 9 segments fail it and stay static)."),
            new ModelStaticAdmissionRule("fxart-tags",
                "Tag v4.0 or v5.0 (Redguard Disc 1 fxart: 3dfx meshes with 10-byte plane headers and point indices)",
                string.Create(culture, $"Unsupported with the reason \"{FxartUnsupportedReason}\".")),
            new ModelStaticAdmissionRule("not-a-model",
                "A non-mesh stray (an empty ROB segment, an executable or another tag inside a mesh archive)",
                "NotAModel: the tag is not a mesh tag or the lists do not fit."),
            new ModelStaticAdmissionRule("game-identification",
                "Every recognized record",
                string.Create(culture,
                    $"The game is established by the chain the unit policy states (--game, container, " +
                    $"{BethesdaModelRegistration.ClassicGameOption}, content), the layout follows the game (10-byte " +
                    $"plane headers for Battlespire, 8-byte for Daggerfall and Redguard), and the evidence is recorded. " +
                    $"A {BethesdaModelRegistration.GameOption} value that conflicts with the content throws; a " +
                    $"container or install answer the content refutes is skipped with " +
                    $"{XnGineGameIdentity.StepRefutedDiagnostic}; a container that disagrees with an accepted --game " +
                    $"is noted with {XnGineGameIdentity.ContainerDisagreesDiagnostic}.")),
            new ModelStaticAdmissionRule("game-unknown",
                "A record that walks only with 8-byte plane headers and has header +20 = 0, or one that walks with " +
                "both header sizes, when no option or container answers",
                string.Create(culture,
                    $"Read with 8-byte plane headers, game Unknown, units the guard row (factor 1, Unknown), no packed-UV " +
                    $"unfold, and the {XnGineGameIdentity.AmbiguousDiagnostic} diagnostic naming --game. An extracted " +
                    $"ARCH3D record is never called Redguard: that would halve its size " +
                    $"({ClassicModelUnits.RedguardMetersPerUnit:R} instead of {ClassicModelUnits.DaggerfallMetersPerUnit:R} " +
                    $"m per native unit).")),
            new ModelStaticAdmissionRule("units",
                "Every recognized record",
                string.Create(culture,
                    $"Daggerfall {ClassicModelUnits.DaggerfallMetersPerUnit:R}, Redguard " +
                    $"{ClassicModelUnits.RedguardMetersPerUnit:R} and Battlespire " +
                    $"{ClassicModelUnits.BattlespireMetersPerUnit:R} m per native unit, all Assumed with the design's " +
                    $"section 4.1 evidence (RE-2, RE-3, RE-4 pending); Unknown when the game is not established.")),
            new ModelStaticAdmissionRule("budgets",
                string.Create(culture, $"More than {MaximumSourceBytes} source bytes"),
                "Not supported: refused before the allocation it would need (the largest measured record is 3D.BSA " +
                "NONAME01.3D at 226,824 decoded bytes; an LZSS entry's decoded length is unknown until extraction, so " +
                "its declared length is null and the read applies the bound to the decoded bytes)."),
            new ModelStaticAdmissionRule("corrupt-input",
                "A header, list or plane walk inconsistent with the record",
                "The read fails as invalid data; nothing is fabricated.")
        ];
    }
}
