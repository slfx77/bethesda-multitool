using System.Buffers.Binary;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The bounded content probe behind <see cref="NifModelReader.Probe" /> and the scope rule
///     <see cref="NifModelReader.Read" /> shares with it (plan section 6, slice 2; cut-1b slice 10; cut 2). Recognition
///     needs the NIF header line ("Gamebryo File Format, Version " or "NetImmerse File Format, Version " with a newline in
///     the first 60 bytes, as NifParser requires); the file extension is never consulted.
/// </summary>
/// <remarks>
///     <para>
///         Supported: a scene graph at 20.2.0.7, user 11, BS 14, 21, 26, 32 or 34, in either byte order; since cut-1b
///         slice 10 a <c>.kf</c> animation stream (a root that is an NiSequence, the read requiring every root to be an
///         NiControllerSequence) at 20.2.0.7, user 11 and any of the BS versions <see cref="AnimationStreamBsVersions" />
///         lists, the scene-graph ones plus BS 24, 25, 27, 28, 30, 31 and 33, which only animation streams use; and since
///         cut 2 a <c>.kf</c> animation stream at the one pre-20.2.0.5 key the reader reads, little-endian 20.0.0.4 at
///         user 10 or 11, BS 11 (<see cref="IsLegacyKfKey" />), whose blocks all use one of the eight types measured on
///         the five FNV-shipped files (<see cref="LegacyKfBlockTypes" />, exact tiling in
///         <c>TestOutput/cut2-prep-20260928/kf2004</c>). That identity is the Oblivion-era <c>.kf</c> identity, not the
///         five files' alone: 598 of the first 600 <c>.kf</c> of Oblivion's Meshes BSA carry it (550 at user 11, 48 at
///         user 10; measured 2026-09-28 with the Python probe, <c>kf2004-carry/controls/oblivion_kf_sample.py</c>), 581
///         of them over the eight types and 17 with an NiFloatData, NiBoolData or NiBSplineCompFloatInterpolator (and
///         the NiFloatInterpolator or NiBoolInterpolator driving them), which stay <c>later-cut(2)</c> until those
///         layouts are measured (<see cref="LegacyKfBlockTypeRejection" />, the rule the Python probe's
///         <c>legacy_kf_decline</c> applies, so both gate instruments decline the same files). Unsupported with a stated
///         reason: any other 20.0.0.4 file (<c>later-cut(2)</c>: the Oblivion-era scene graphs, a stream with a block
///         type outside the eight, and a 20.0.0.4 stream at another user or BS version or in big-endian order), a
///         20.2.0.7 scene graph at an animation-only BS version (<c>animation-stream key</c>) and every other version
///         (<c>other version</c>).
///     </para>
///     <para>
///         The root is taken from the footer when the whole file is in the prefix and the header carries the Block Size
///         array (20.2.0.5 and later), and from block 0 (the Gamebryo convention) otherwise. A 20.0.0.4 header has no
///         Block Size array and no string table, so the probe never reaches its footer: block 0 decides, an NiSequence
///         there being the <c>.kf</c> key and anything else the <c>later-cut(2)</c> decline, and the read decides again
///         from the real footer (<see cref="NifModelReader.IsAnimationStream" /> and the version-aware
///         <see cref="GraphScopeRejection" />). The eight-type bound is decided on the block types the header walk
///         read (every retail header of this key fits the prefix many times over: the five files' are under 0x1D0
///         bytes); should the prefix run out inside the type indices, the types read so far decide and the read
///         decides again over NifParser's whole block table.
///     </para>
///     <para>
///         Confidence is Confirmed when the header lies wholly inside the prefix, Tentative when it does not (the prefix
///         ran out, or the content is complete but the header is truncated or implausible). A supported key stays
///         Supported in those cases, so the read reports the precise corruption instead of the probe hiding it; an
///         animation-only BS version whose footer the prefix does not reach is Supported and Tentative for the same
///         reason (a block-0 root is only a convention), the read deciding from the footer. The evidence description is
///         the header identity, for example <c>NIF 20.2.0.7, user 11, BS 34, little-endian</c>, with
///         <see cref="AnimationStreamSuffix" /> appended for a <c>.kf</c> stream; Shared's info formatter prints it beside
///         the format.
///     </para>
/// </remarks>
internal static class NifModelProbe
{
    /// <summary>The binary version the reader reads, 20.2.0.7.</summary>
    public const uint SupportedVersion = 0x14020007;

    /// <summary>The user version the reader reads.</summary>
    public const uint SupportedUserVersion = 11;

    /// <summary>
    ///     The one pre-20.2.0.5 binary version the reader reads (cut 2), and only as a little-endian <c>.kf</c> animation
    ///     stream at <see cref="LegacyKfUserVersions" /> and <see cref="LegacyKfBsVersion" />.
    /// </summary>
    public const uint LegacyKfVersion = NifVersions.Gamebryo20004;

    /// <summary>The BS stream version of the 20.0.0.4 <c>.kf</c> key.</summary>
    public const uint LegacyKfBsVersion = 11;

    /// <summary>The suffix the evidence description carries for a <c>.kf</c> animation stream.</summary>
    public const string AnimationStreamSuffix = ", .kf animation stream";

    /// <summary>The reason category of a scene graph at a BS version only animation streams use.</summary>
    public const string AnimationStreamKeyCategory = "animation-stream key";

    /// <summary>The reason category of every 20.0.0.4 file outside the <c>.kf</c> key (the Oblivion-era scene graphs).</summary>
    public const string LaterCutTwoCategory = "later-cut(2)";

    private const uint FirstVersionWithEndianByte = 0x14000003;
    private const uint FirstVersionWithBlockSizes = 0x14020005;
    private const uint FirstVersionWithStringTable = 0x14010001;
    private const int MaximumHeaderLine = 60;
    private const string SequenceType = "NiSequence";
    private const string ControllerSequenceType = "NiControllerSequence";

    private static readonly byte[] GamebryoSignature = "Gamebryo File Format, Version "u8.ToArray();
    private static readonly byte[] NetImmerseSignature = "NetImmerse File Format, Version "u8.ToArray();

    /// <summary>The BS versions a scene graph (<c>.nif</c>) is read at.</summary>
    public static IReadOnlyList<uint> SupportedBsVersions { get; } = [14, 21, 26, 32, 34];

    /// <summary>The 20.2.0.7 user-11 BS versions only <c>.kf</c> animation streams use; read as <c>.kf</c> streams only.</summary>
    public static IReadOnlyList<uint> AnimationStreamOnlyBsVersions { get; } = [24, 25, 27, 28, 30, 31, 33];

    /// <summary>Every BS version a 20.2.0.7 <c>.kf</c> animation stream is read at, ascending (the two lists above together).</summary>
    public static IReadOnlyList<uint> AnimationStreamBsVersions { get; } = [14, 21, 24, 25, 26, 27, 28, 30, 31, 32, 33, 34];

    /// <summary>
    ///     The user versions of the 20.0.0.4 <c>.kf</c> key (of the five FNV-shipped files three store 10 and two 11; of
    ///     the 598 sampled Oblivion files 48 and 550).
    /// </summary>
    public static IReadOnlyList<uint> LegacyKfUserVersions { get; } = [10, 11];

    /// <summary>
    ///     The eight block types the 20.0.0.4 <c>.kf</c> read is bounded to: the types the five FNV-shipped files use,
    ///     whose 20.0.0.4 layouts were checked against nif.xml by exact tiling (<c>TestOutput/cut2-prep-20260928/kf2004</c>),
    ///     spelled as the Python probe's <c>LEGACY_KF_TYPES</c>. A stream of the key identity whose blocks use any other
    ///     type is declined by <see cref="LegacyKfBlockTypeRejection" /> until that layout is measured.
    /// </summary>
    public static IReadOnlyList<string> LegacyKfBlockTypes { get; } =
    [
        "NiControllerSequence", "NiTextKeyExtraData", "NiStringPalette", "NiTransformInterpolator", "NiTransformData",
        "NiBSplineCompTransformInterpolator", "NiBSplineData", "NiBSplineBasisData"
    ];

    /// <summary>Probes one bounded candidate.</summary>
    public static ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var content = candidate.Content;
        if (!HasHeaderLine(content, out var newline))
        {
            return ModelProbeResult.NotAModel;
        }

        var identity = NifParser.ProbeVersionInfo(content.ToArray());
        if (identity is not { } id || IdentityEnd(newline, id.BinaryVersion, id.UserVersion) > content.Length)
        {
            return new ModelProbeResult(ModelProbeKind.Unsupported, ModelProbeConfidence.Tentative,
                new ModelProbeEvidence(0, content.Length, "NIF header line without complete version fields"),
                string.Create(CultureInfo.InvariantCulture,
                    $"truncated: the {content.Length}-byte content ends before the NIF version fields are complete."));
        }

        var identityEnd = IdentityEnd(newline, id.BinaryVersion, id.UserVersion);
        var description = Describe(id.BinaryVersion, id.UserVersion, id.BsVersion, id.IsBigEndian);
        if (ScopeRejection(id.BinaryVersion, id.UserVersion, id.BsVersion, id.IsBigEndian) is { } rejection)
        {
            return new ModelProbeResult(ModelProbeKind.Unsupported, ModelProbeConfidence.Confirmed,
                new ModelProbeEvidence(0, identityEnd, description), rejection);
        }

        // The key has a fixed identity layout: version, endian byte, user version, Num Blocks, BS version.
        var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(content[(identityEnd - 8)..]);
        var header = NifModelProbeHeader.Read(content, identityEnd, blockCount, id.IsBigEndian,
            id.BinaryVersion >= FirstVersionWithBlockSizes, id.BinaryVersion >= FirstVersionWithStringTable);
        var complete = header.Status == NifModelProbeHeaderStatus.Complete;
        var examined = header.ExaminedBytes;
        var rootTypes = RootTypes(content, candidate.IsComplete, header, blockCount, id.IsBigEndian, ref examined,
            out var fromFooter);
        var isAnimationStream = rootTypes.Any(IsAnimationRoot);
        var confidence = complete ? ModelProbeConfidence.Confirmed : ModelProbeConfidence.Tentative;
        var length = Math.Clamp(examined, 1, content.Length);
        if (isAnimationStream)
        {
            if (LegacyKfBlockTypeRejection(id.BinaryVersion, id.UserVersion, id.BsVersion, id.IsBigEndian,
                    BlockTypes(header)) is { } typeRejection)
            {
                // The key identity over a block type outside the eight measured ones (an Oblivion .kf driving a float
                // or a bool): later-cut(2), as the Python probe declines it; the evidence is the plain identity.
                return new ModelProbeResult(ModelProbeKind.Unsupported, confidence,
                    new ModelProbeEvidence(0, length, description), typeRejection);
            }

            var streamEvidence = new ModelProbeEvidence(0, length,
                DescribeAnimationStream(id.BinaryVersion, id.UserVersion, id.BsVersion, id.IsBigEndian));
            return ByHeaderStatus(header, candidate.IsComplete, content.Length, confidence, streamEvidence);
        }

        var evidence = new ModelProbeEvidence(0, length, description);
        if (IsLegacyKfKey(id.BinaryVersion, id.UserVersion, id.BsVersion, id.IsBigEndian))
        {
            // Block 0 is not a sequence: the 20.0.0.4 key is read only as a .kf, so this is the later-cut(2) decline
            // (the read refuses such a file from its real footer with the same reason).
            return new ModelProbeResult(ModelProbeKind.Unsupported, confidence, evidence,
                LegacySceneGraphReason(id.UserVersion, id.BsVersion));
        }

        if (!SupportedBsVersions.Contains(id.BsVersion))
        {
            if (fromFooter)
            {
                return new ModelProbeResult(ModelProbeKind.Unsupported, confidence, evidence,
                    AnimationStreamKeyReason(id.BsVersion));
            }

            // Only an animation stream uses this BS version, but the footer is beyond the prefix (block 0 is only the
            // Gamebryo convention for the root): the read decides from the footer, as it reports any corruption the
            // probe cannot see.
            return new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Tentative, evidence,
                string.Create(CultureInfo.InvariantCulture,
                    $"The footer roots are not within the {content.Length}-byte probe content; NIF 20.2.0.7, user 11, " +
                    $"BS {id.BsVersion} is read only as a .kf animation stream, which the read checks."));
        }

        return ByHeaderStatus(header, candidate.IsComplete, content.Length, confidence, evidence);
    }

    /// <summary>
    ///     True for the one pre-20.2.0.5 identity the reader reads (cut 2): little-endian 20.0.0.4 at user 10 or 11,
    ///     BS 11, and only as a <c>.kf</c> animation stream over <see cref="LegacyKfBlockTypes" />
    ///     (<see cref="LegacyKfBlockTypeRejection" />). It is the Oblivion-era <c>.kf</c> identity (598 of 600 sampled
    ///     Oblivion files), of which FNV ships five files; the decoder's legacy header branch (<c>NifHeaderLayout</c>)
    ///     reads exactly this identity.
    /// </summary>
    /// <param name="version">The binary version.</param>
    /// <param name="userVersion">The user version.</param>
    /// <param name="bsVersion">The BS stream version.</param>
    /// <param name="bigEndian">Whether the body byte order is big-endian (endian byte 0).</param>
    /// <returns>True for the 20.0.0.4 <c>.kf</c> key.</returns>
    public static bool IsLegacyKfKey(uint version, uint userVersion, uint bsVersion, bool bigEndian)
    {
        return version == LegacyKfVersion && !bigEndian && LegacyKfUserVersions.Contains(userVersion) &&
               bsVersion == LegacyKfBsVersion;
    }

    /// <summary>
    ///     The reason a <c>.kf</c> of the 20.0.0.4 key uses a block type outside <see cref="LegacyKfBlockTypes" />: the
    ///     <c>later-cut(2)</c> decline naming the first such type in ordinal order, as the Python probe's
    ///     <c>legacy_kf_decline</c> names it. Null when the identity is not the key, or every block's type is one of
    ///     the eight. The probe applies it to the types its header walk read, the read to NifParser's block table, so
    ///     the two gate instruments and the read decline the same files (the Oblivion sample: 17 of 598).
    /// </summary>
    /// <param name="version">The binary version.</param>
    /// <param name="userVersion">The user version.</param>
    /// <param name="bsVersion">The BS stream version.</param>
    /// <param name="bigEndian">Whether the body byte order is big-endian.</param>
    /// <param name="blockTypes">The type of every block, in block order (repeats allowed).</param>
    /// <returns>The reason, or null.</returns>
    public static string? LegacyKfBlockTypeRejection(uint version, uint userVersion, uint bsVersion, bool bigEndian,
        IEnumerable<string> blockTypes)
    {
        ArgumentNullException.ThrowIfNull(blockTypes);
        if (!IsLegacyKfKey(version, userVersion, bsVersion, bigEndian))
        {
            return null;
        }

        var other = blockTypes.Where(static type => !LegacyKfBlockTypes.Contains(type, StringComparer.Ordinal))
            .Min(StringComparer.Ordinal);
        if (other is null)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{LaterCutTwoCategory}: NIF 20.0.0.4 (user {userVersion}, BS {bsVersion}) is read only as a .kf animation " +
            $"stream over the eight block types measured at this version ({string.Join(", ", LegacyKfBlockTypes)}); " +
            $"{other} is not among them, and the other Oblivion-era layouts are read from cut 2 as they are measured.");
    }

    /// <summary>
    ///     The reason a header identity is outside every key the reader reads, or null when it is 20.2.0.7, user 11 at
    ///     one of <see cref="AnimationStreamBsVersions" />, or the 20.0.0.4 <c>.kf</c> key (whether a scene graph at
    ///     either is read depends on its roots: <see cref="GraphScopeRejection" />). Used by the probe and, as a
    ///     <see cref="NotSupportedException" /> message, by the read.
    /// </summary>
    /// <param name="version">The binary version.</param>
    /// <param name="userVersion">The user version.</param>
    /// <param name="bsVersion">The BS stream version.</param>
    /// <param name="bigEndian">Whether the body byte order is big-endian.</param>
    /// <returns>The reason, or null for a key the reader reads.</returns>
    public static string? ScopeRejection(uint version, uint userVersion, uint bsVersion, bool bigEndian)
    {
        if (version == SupportedVersion && userVersion == SupportedUserVersion &&
            AnimationStreamBsVersions.Contains(bsVersion))
        {
            return null;
        }

        if (IsLegacyKfKey(version, userVersion, bsVersion, bigEndian))
        {
            return null;
        }

        if (version == LegacyKfVersion)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{LaterCutTwoCategory}: NIF 20.0.0.4 (user {userVersion}, BS {bsVersion}, " +
                $"{(bigEndian ? "big-endian" : "little-endian")}) is outside the one 20.0.0.4 key read, the " +
                $"little-endian .kf animation stream at user 10 or 11, BS 11 (the Oblivion-era .kf identity, bounded " +
                $"to the eight block types measured on the five FNV-shipped files); the Oblivion-era meshes are read " +
                $"from cut 2.");
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"other version: NIF {FormatVersion(version)}, user {userVersion}, BS {bsVersion} is outside the reader's " +
            $"keys (20.2.0.7, user 11: a scene graph at BS 14, 21, 26, 32 or 34; a .kf animation stream also at BS 24, " +
            $"25, 27, 28, 30, 31 or 33; and a little-endian 20.0.0.4 .kf animation stream at user 10 or 11, BS 11).");
    }

    /// <summary>
    ///     The reason a scene graph (no animation-stream root) at a key read only as a <c>.kf</c> stream is not read: the
    ///     <c>later-cut(2)</c> decline for the 20.0.0.4 key, the <c>animation-stream key</c> reason for a 20.2.0.7 BS
    ///     version only streams use; null when the BS version is one a scene graph is read at.
    /// </summary>
    /// <param name="version">The binary version.</param>
    /// <param name="userVersion">The user version.</param>
    /// <param name="bsVersion">The BS stream version.</param>
    /// <param name="bigEndian">Whether the body byte order is big-endian.</param>
    /// <returns>The reason, or null.</returns>
    public static string? GraphScopeRejection(uint version, uint userVersion, uint bsVersion, bool bigEndian)
    {
        if (IsLegacyKfKey(version, userVersion, bsVersion, bigEndian))
        {
            return LegacySceneGraphReason(userVersion, bsVersion);
        }

        return SupportedBsVersions.Contains(bsVersion) ? null : AnimationStreamKeyReason(bsVersion);
    }

    /// <summary>The reason text of <see cref="GraphScopeRejection" /> for an animation-only BS version.</summary>
    public static string AnimationStreamKeyReason(uint bsVersion)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{AnimationStreamKeyCategory}: NIF 20.2.0.7, user 11, BS {bsVersion} is read only as a .kf animation " +
            $"stream (its roots NiControllerSequence blocks); a scene graph is read at BS 14, 21, 26, 32 and 34.");
    }

    /// <summary>
    ///     The reason text of <see cref="GraphScopeRejection" /> for a 20.0.0.4 file whose roots are not animation
    ///     sequences: the Oblivion-era scene graphs stay <c>later-cut(2)</c>.
    /// </summary>
    public static string LegacySceneGraphReason(uint userVersion, uint bsVersion)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{LaterCutTwoCategory}: NIF 20.0.0.4 (user {userVersion}, BS {bsVersion}) is read only as a .kf animation " +
            $"stream (its roots NiControllerSequence blocks, over the eight block types measured on the five " +
            $"FNV-shipped files); the Oblivion-era scene graphs are read from cut 2.");
    }

    /// <summary>
    ///     The reason a file whose roots mix an animation sequence with other blocks, or name an NiSequence that is not an
    ///     NiControllerSequence, is not read.
    /// </summary>
    public static string MixedRootsReason(string rootType)
    {
        return $"{AnimationStreamKeyCategory}: the footer names an {rootType} root beside roots that are not all " +
               "NiControllerSequence blocks; a .kf animation stream is read only when every root is an " +
               "NiControllerSequence.";
    }

    /// <summary>True for NiSequence and its subclasses (NiControllerSequence), the root type of a .kf stream.</summary>
    public static bool IsAnimationRoot(string typeName)
    {
        return NifSchema.LoadEmbedded().Inherits(typeName, SequenceType);
    }

    /// <summary>True for NiControllerSequence and its subclasses, the root type a <c>.kf</c> stream is read from.</summary>
    public static bool IsAnimationStreamRoot(string typeName)
    {
        return NifSchema.LoadEmbedded().Inherits(typeName, ControllerSequenceType);
    }

    /// <summary>The header identity, for example <c>NIF 20.2.0.7, user 11, BS 34, little-endian</c>.</summary>
    public static string Describe(uint version, uint userVersion, uint bsVersion, bool bigEndian)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"NIF {FormatVersion(version)}, user {userVersion}, BS {bsVersion}, " +
            $"{(bigEndian ? "big-endian" : "little-endian")}");
    }

    /// <summary>
    ///     The header identity of a <c>.kf</c> animation stream, for example
    ///     <c>NIF 20.2.0.7, user 11, BS 24, little-endian, .kf animation stream</c>.
    /// </summary>
    public static string DescribeAnimationStream(uint version, uint userVersion, uint bsVersion, bool bigEndian)
    {
        return Describe(version, userVersion, bsVersion, bigEndian) + AnimationStreamSuffix;
    }

    /// <summary>A binary version in dotted form, for example 0x14020007 as <c>20.2.0.7</c>.</summary>
    public static string FormatVersion(uint version)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{version >> 24}.{(version >> 16) & 0xFF}.{(version >> 8) & 0xFF}.{version & 0xFF}");
    }

    /// <summary>The Supported result for a header status: Confirmed and silent when complete, else Tentative with the problem.</summary>
    private static ModelProbeResult ByHeaderStatus(NifModelProbeHeader header, bool isComplete, int contentLength,
        ModelProbeConfidence confidence, ModelProbeEvidence evidence)
    {
        return header.Status switch
        {
            NifModelProbeHeaderStatus.Complete => new ModelProbeResult(ModelProbeKind.Supported, confidence, evidence),
            NifModelProbeHeaderStatus.Exhausted when !isComplete => new ModelProbeResult(
                ModelProbeKind.Supported, confidence, evidence, string.Create(CultureInfo.InvariantCulture,
                    $"The header extends past the {contentLength}-byte probe prefix (the prefix ends inside " +
                    $"{header.Problem}); the rest of the header was not checked.")),
            NifModelProbeHeaderStatus.Exhausted => new ModelProbeResult(ModelProbeKind.Supported, confidence,
                evidence, string.Create(CultureInfo.InvariantCulture,
                    $"truncated: the complete {contentLength}-byte content ends inside {header.Problem}; " +
                    $"reading reports the error.")),
            _ => new ModelProbeResult(ModelProbeKind.Supported, confidence, evidence,
                $"malformed header: {header.Problem}; reading reports the error.")
        };
    }

    private static bool HasHeaderLine(ReadOnlySpan<byte> content, out int newline)
    {
        newline = content[..Math.Min(MaximumHeaderLine, content.Length)].IndexOf((byte)0x0A);
        return newline > 0 &&
               ((content.StartsWith(GamebryoSignature) && newline > GamebryoSignature.Length) ||
                (content.StartsWith(NetImmerseSignature) && newline > NetImmerseSignature.Length));
    }

    /// <summary>
    ///     One past the identity fields NifParser.ProbeVersionInfo reads: version, the endian byte (since 20.0.0.3), the
    ///     user version (since 10.0.1.8), Num Blocks and, when the BSStreamHeader gate holds, the BS version.
    /// </summary>
    private static int IdentityEnd(int newline, uint version, uint userVersion)
    {
        var position = newline + 1 + 4;
        if (version >= FirstVersionWithEndianByte)
        {
            position += 1;
        }

        if (NifVersions.HasUserVersion(version))
        {
            position += 4;
        }

        position += 4;
        if (NifVersions.HasBsStreamHeader(version, userVersion))
        {
            position += 4;
        }

        return position;
    }

    /// <summary>
    ///     The root block types: every footer root when the whole file is in the prefix, the header carries the Block
    ///     Size array and its footer reads cleanly (<paramref name="fromFooter" /> true), otherwise block 0 when its type
    ///     index was read, otherwise none.
    /// </summary>
    private static List<string> RootTypes(ReadOnlySpan<byte> content, bool isComplete, NifModelProbeHeader header,
        uint blockCount, bool bigEndian, ref int examined, out bool fromFooter)
    {
        fromFooter = false;
        var types = header.BlockTypeNames;
        var indices = header.BlockTypeIndices;
        if (header.Status == NifModelProbeHeaderStatus.Complete && isComplete && header.HasBlockSizes)
        {
            var footerOffset = (long)header.HeaderEnd + header.BlockSizes.Sum(size => (long)size);
            if (footerOffset <= content.Length - 4)
            {
                try
                {
                    var footer = NifFooterReader.Read(content, bigEndian, (int)footerOffset, (int)blockCount);
                    examined = content.Length;
                    fromFooter = true;
                    return footer.Roots.Select(root => types[indices[root]]).ToList();
                }
                catch (InvalidDataException)
                {
                    // A broken footer is the read's to report; fall back to the block-0 convention here.
                }
            }
        }

        return indices.Count > 0 ? [types[indices[0]]] : [];
    }

    /// <summary>The type of every block whose type index the header walk read, in block order.</summary>
    private static IEnumerable<string> BlockTypes(NifModelProbeHeader header)
    {
        return header.BlockTypeIndices.Select(index => header.BlockTypeNames[index]);
    }
}
