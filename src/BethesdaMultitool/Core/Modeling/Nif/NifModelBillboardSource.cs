using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Games;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The source key one NIF read's billboard facing and reflected-face declarations depend on: the stream key from
///     the header, the game named by <see cref="BethesdaModelRegistration.GameOption" />, the byte order and the console
///     platform (<see cref="NifPackedPlatformSelection" />). It supplies the provenance and the verbatim evidence Shared's
///     <see cref="SceneBillboard" /> and <see cref="SceneReflectedFaces" /> carry, and the reader-established
///     <see cref="SceneBillboard.PlaneFallbackEdge" />. Pure: no state beyond its inputs.
/// </summary>
/// <remarks>
///     <para>
///         The behavior itself is RE-25 (docs/formats/nif-animation-engine-behavior-20260925.md, section RE-25 and its
///         runtime re-check of 2026-09-27); the reflected-face rule is the mirror-cull RE
///         (docs/formats/fnv-pc-framebuffer-and-mirror-cull-20260927.md, section "Mirror cull"). The key decides only how
///         well that behavior is established for the file in hand, never the encoding (<see cref="NifModelBillboards" />).
///     </para>
///     <para>
///         Facing and schedule, first match:
///         a stream other than the FNV/Fallout 3 one (NIF 20.2.0.7, user 11, a scene-graph BS version the reader accepts)
///         is Assumed; a game option naming another game is Assumed; every other read of that stream (FNV, Fallout 3 or
///         the game not established; PC and console alike) is ReverseEngineered, and its evidence says which builds were
///         examined for it: no PlayStation 3 build and no Fallout 3 console build of NiBillboardNode was examined, and a
///         big-endian read without <see cref="BethesdaModelRegistration.PlatformOption" /> may be a PS3 file.
///     </para>
///     <para>
///         <see cref="PlaneFallbackEdge" /> is declared only for FNV PC (game FNV, little-endian), the one build whose rim
///         arithmetic was bounded; its evidence then carries the camera and x87-precision premises the bound holds under.
///     </para>
///     <para>
///         Reflected faces (<see cref="SceneReflectedFaceRule.DrawnWinding" />): ReverseEngineered for FNV PC and for FNV
///         with the Xbox 360 platform established (the two builds the mirror-cull RE examined), Assumed for every other
///         read of the FNV/Fallout 3 stream whose game is FNV, Fallout 3 or not established, and not declared (null) for
///         another game or stream.
///     </para>
/// </remarks>
internal sealed class NifModelBillboardSource
{
    /// <summary>
    ///     The reader-established plane-fallback edge for FNV PC, in radians (see <see cref="PlaneFallbackEdge" />).
    /// </summary>
    /// <remarks>
    ///     acos((K* - 1.5u)/((1 + 4u)(1 + 5.5u))) + 3u with u = 2^-24 and K* = float32(0.999999) - 2^-25: the largest angle
    ///     between the opposite view axis and the direction to the camera at which the runtime's float32 dot can still
    ///     reach float32(0.999999), for a camera column norm within 4u of 1 (DESIGN-rev4 6.4.2, law_check_rev4 L6,
    ///     reproduced by VERIFY-rev4-math W1), excluding the source's position rounding. Correctly rounded from a
    ///     60-digit evaluation: 1.84339989308343604e-3.
    /// </remarks>
    public const double FnvPcPlaneFallbackEdge = 1.843399893083436e-3;

    /// <summary>The RE-25 citation every FNV/Fallout 3 facing evidence opens with.</summary>
    public const string FacingCitation =
        "RE-25 (docs/formats/nif-animation-engine-behavior-20260925.md, NiBillboardNode facing per mode; investigator " +
        "and verifier, the verifier's corrections applied): RotateToCamera was emulated per stored value on the " +
        "FNV-era GECK and classified per mode on the Fallout 3 PC runtime, and the FNV Xbox 360 MemDebug build of " +
        "2010-8-22 agrees on control flow, thresholds and the mode-5 matrix (15 of 15 disassembly facts). Its runtime " +
        "re-check (2026-09-27) found the shipped FalloutNV.exe 1.4.0.525 (Steam build 1510068) facing code " +
        "byte-identical to the GECK's modulo relocations, with output bit-identical on every emulated scene (13,913 " +
        "verifier runs at 53- and 24-bit x87 precision). The effective mode is the stored value & 7 (bit 3 is the " +
        "update-controllers flag the engine sets on load; 6 and 7 do not face). ALWAYS_FACE_CENTER and " +
        "RIGID_FACE_CENTER keep the camera plane while the float32 dot of the opposite view axis and the direction to " +
        "the camera is at least float32(0.999999), declared as the real cosine float32(0.999999) - 2^-25, and stay " +
        "unfaced while the squared distance to the camera is below float32(0.001).";

    /// <summary>The premises of the FNV PC plane-fallback edge and of every bound quoted with its evidence.</summary>
    public const string FnvPcPremises =
        "Premises of the plane-fallback edge and of every bound quoted with this evidence: the source camera's stored " +
        "float32 rotation is within 4 * 2^-24 of a rotation, in column norm (kappa = |f32| - 1) and in column " +
        "difference (epsilon_C), the size two float32 compositions of exact rotations produce (not measured in game); " +
        "the x87 precision control on the culling thread in game is unsettled (53- or 24-bit: the Direct3D 9 device is " +
        "created without D3DCREATE_FPU_PRESERVE), and every bound holds at both and with a CRT square root one float32 " +
        "ulp off. For another camera, with u = 2^-24, K = float32(0.999999), K* = K - 2^-25, q = max(|p|, |c|)/|c - p| " +
        "and theta' the angle between the opposite view axis and the direction to the camera: the source keeps the " +
        "camera plane only within acos(min(1, (K* - 1.5u)/((1 + kappa)(1 + 5.5u)))) + 3u + 2 sqrt(3) u q of the view " +
        "axis (the declared edge is this at kappa = 4u and q = 0); on the facing branch X and Y lean toward Z by at most " +
        "2 asin(min(1, eta/(2 sin((theta' + acos K)/2)))), eta = (1 + |kappa|)(1 + 5.5u) - 1 + 2u, divided by " +
        "sin(Y0, t) for ALWAYS_FACE_CENTER; and a camera epsilon_C from a rotation moves RIGID_FACE_CAMERA by at most " +
        "epsilon_C, RIGID_FACE_CENTER by 2.5 epsilon_C, ALWAYS_FACE_CAMERA by 3 epsilon_C/sin(Y0, f), and " +
        "ALWAYS_FACE_CENTER by 3 epsilon_C/sin(Y0, t) facing and 3 epsilon_C/sin(Y0, f) in its plane-fallback cone.";

    /// <summary>The RE-25 citation every FNV/Fallout 3 schedule evidence opens with.</summary>
    public const string ScheduleCitation =
        "RE-25 item 1 and verifier 3d (GECK and Fallout 3 PC), confirmed on the FNV PC runtime: the update pass never " +
        "faces (UpdateWorldData is NiAVObject's); OnVisible (vtable slot 53) calls RotateToCamera with the culler's " +
        "camera on every visible draw, which recomposes the pre-facing world from the parent's current world and the " +
        "node's current animated local transform, pivots at the node origin with translation and scale unchanged, and " +
        "re-runs every child's UpdateDownwardPass from the faced world. Where UpdateWorldData would defer to a " +
        "collision object (+0x1C) or copy the parent's world (NiAVObject flag 0x200), the facing still starts from " +
        "parent x local; neither case was checked against retail files.";

    /// <summary>The mirror-cull citation every reflected-face evidence opens with.</summary>
    public const string MirrorCullCitation =
        "Mirror cull RE (docs/formats/fnv-pc-framebuffer-and-mirror-cull-20260927.md, section Mirror cull; " +
        "investigator and verifier) on FalloutNV.exe 1.4.0.525: every D3DRS_CULLMODE value is a constant, " +
        "T[draw mode][swap] or the stencil-BOTH rule; the left/right swap flag is cleared by the constructor and set by " +
        "nothing; no cull input reads a transform, so a reflected world (NiBillboardNode mode 5, determinant -1) is " +
        "drawn with its screen winding reversed and no compensation. Retail: all 14 single-sided mode-5 triangle meshes " +
        "are wound away from the camera, the only winding visible under an uncompensated mirror (control: 813 of 816 " +
        "single-sided proper billboards are wound toward their camera axis).";

    /// <summary>The statement every Assumed reflected-face evidence carries after its read statement.</summary>
    public const string AssumedCullStatement =
        "The cull path was examined on FNV PC and the FNV Xbox 360 MemDebug build only; RE-25's verifier found the " +
        "same NiDX9RenderState cull table and swap-flag slots in the Fallout 3 PC runtime, but Fallout 3's shader cull " +
        "paths and swap-flag callers were not traced, and no PlayStation 3 build was examined.";

    /// <summary>Resolves the key from the parsed header, the read's app options and its resolved platform.</summary>
    /// <param name="info">NifParser's header.</param>
    /// <param name="appOptions">The read's app options (the game option parsed as <see cref="NifModelUnits" /> does).</param>
    /// <param name="platform">The read's console platform selection.</param>
    /// <exception cref="ArgumentException">The game option names no NIF-era game.</exception>
    public static NifModelBillboardSource Resolve(NifInfo info, IReadOnlyDictionary<string, string> appOptions,
        NifPackedPlatformSelection platform)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(appOptions);
        ArgumentNullException.ThrowIfNull(platform);
        BethesdaGame? game = null;
        if (appOptions.TryGetValue(BethesdaModelRegistration.GameOption, out var value) &&
            !string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            game = NifModelUnits.ParseGame(value);
        }

        return new NifModelBillboardSource(info.BinaryVersion, info.UserVersion, info.BsVersion, info.IsBigEndian,
            game, platform);
    }

    /// <summary>Creates the key and derives every declaration from it.</summary>
    /// <param name="binaryVersion">The header's NIF version.</param>
    /// <param name="userVersion">The header's user version.</param>
    /// <param name="bsVersion">The header's BS version.</param>
    /// <param name="isBigEndian">Whether the stream is big-endian (a console file).</param>
    /// <param name="game">The established game, or null when the game option was absent or <c>auto</c>.</param>
    /// <param name="platform">The console platform selection (ignored for a little-endian stream).</param>
    public NifModelBillboardSource(uint binaryVersion, uint userVersion, uint bsVersion, bool isBigEndian,
        BethesdaGame? game, NifPackedPlatformSelection platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        BinaryVersion = binaryVersion;
        UserVersion = userVersion;
        BsVersion = bsVersion;
        IsBigEndian = isBigEndian;
        Game = game;
        Platform = platform;
        IsFalloutStream = binaryVersion == NifModelProbe.SupportedVersion &&
                          userVersion == NifModelProbe.SupportedUserVersion &&
                          NifModelProbe.SupportedBsVersions.Contains(bsVersion);
        var isFalloutGame = game is null or BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3;
        Description = Describe();
        if (!IsFalloutStream)
        {
            var stream = string.Create(CultureInfo.InvariantCulture,
                $"NIF {FormatVersion(binaryVersion)}, user {userVersion}, BS {bsVersion} is not the FNV/Fallout 3 stream " +
                $"RE-25 examined (20.2.0.7, user 11, a scene graph at BS 14, 21, 26, 32 or 34); the FNV/Fallout 3 ");
            AssumedReason = "the stream is not the FNV/Fallout 3 stream RE-25 examined";
            FacingProvenance = SceneValueProvenance.Assumed;
            FacingEvidence = stream + "encoding is assumed.";
            ScheduleProvenance = SceneValueProvenance.Assumed;
            ScheduleEvidence = stream + "schedule is assumed.";
            return;
        }

        if (!isFalloutGame)
        {
            AssumedReason = string.Create(CultureInfo.InvariantCulture,
                $"{BethesdaModelRegistration.GameOption} names {game}, whose reading of this stream was not examined");
            FacingProvenance = SceneValueProvenance.Assumed;
            FacingEvidence = string.Create(CultureInfo.InvariantCulture,
                $"{BethesdaModelRegistration.GameOption} names {game}, not Fallout: New Vegas or Fallout 3, while the " +
                $"stream is the FNV/Fallout 3 one (NIF 20.2.0.7, user 11, BS {bsVersion}). RE-25 examined " +
                $"NiBillboardNode on the FNV GECK and runtime, the Fallout 3 PC runtime and the FNV Xbox 360 MemDebug " +
                $"build, and found (investigator only) the Skyrim and Fallout 4 facing mathematics identical; how {game} " +
                $"faces this stream was not examined, so the FNV/Fallout 3 encoding is assumed.");
            ScheduleProvenance = SceneValueProvenance.Assumed;
            ScheduleEvidence = string.Create(CultureInfo.InvariantCulture,
                $"{BethesdaModelRegistration.GameOption} names {game}: RE-25 established the schedule (OnVisible faces " +
                $"from the recomposed pre-facing world and re-updates the children) for FNV and Fallout 3 only, and " +
                $"found (investigator only) that Skyrim and Fallout 4 schedule differently; the FNV/Fallout 3 schedule " +
                $"is assumed.");
            return;
        }

        var isFnvPc = !isBigEndian && game == BethesdaGame.FalloutNewVegas;
        FacingProvenance = SceneValueProvenance.ReverseEngineered;
        FacingEvidence = FacingCitation + " " + FacingRead() + (isFnvPc ? " " + FnvPcPremises : "");
        ScheduleProvenance = SceneValueProvenance.ReverseEngineered;
        ScheduleEvidence = ScheduleCitation + " " + ScheduleRead();
        PlaneFallbackEdge = isFnvPc ? FnvPcPlaneFallbackEdge : null;
        var cullExamined = game == BethesdaGame.FalloutNewVegas &&
                           (!isBigEndian || platform is { Platform: NifPackedPlatform.X360, IsAssumed: false });
        ReflectedFaces = cullExamined
            ? new SceneReflectedFaces(SceneReflectedFaceRule.DrawnWinding, SceneValueProvenance.ReverseEngineered,
                MirrorCullCitation + " " + CullRead())
            : new SceneReflectedFaces(SceneReflectedFaceRule.DrawnWinding, SceneValueProvenance.Assumed,
                MirrorCullCitation + " " + CullRead() + " " + AssumedCullStatement);
    }

    /// <summary>The header's NIF version.</summary>
    public uint BinaryVersion { get; }

    /// <summary>The header's user version.</summary>
    public uint UserVersion { get; }

    /// <summary>The header's BS version.</summary>
    public uint BsVersion { get; }

    /// <summary>Whether the stream is big-endian (a console file).</summary>
    public bool IsBigEndian { get; }

    /// <summary>The established game, or null when it is not established.</summary>
    public BethesdaGame? Game { get; }

    /// <summary>The console platform selection; it matters only for a big-endian stream.</summary>
    public NifPackedPlatformSelection Platform { get; }

    /// <summary>True for the FNV/Fallout 3 stream RE-25 examined (20.2.0.7, user 11, a reader scene-graph BS).</summary>
    public bool IsFalloutStream { get; }

    /// <summary>A short statement of the key for native state (game, byte order, platform).</summary>
    public string Description { get; }

    /// <summary>Why the facing is Assumed for this key, or null when it is ReverseEngineered.</summary>
    public string? AssumedReason { get; }

    /// <summary>The provenance of the facing rules, axes, lock, reflection and thresholds.</summary>
    public SceneValueProvenance FacingProvenance { get; }

    /// <summary>The verbatim facing evidence for this key.</summary>
    public string FacingEvidence { get; }

    /// <summary>The provenance of the facing schedule (after animated locals, before descendants, on each draw).</summary>
    public SceneValueProvenance ScheduleProvenance { get; }

    /// <summary>The verbatim schedule evidence for this key.</summary>
    public string ScheduleEvidence { get; }

    /// <summary>
    ///     The reader-established outer plane-fallback angle in radians (<see cref="FnvPcPlaneFallbackEdge" />) for FNV PC,
    ///     else null: no other build's rim arithmetic was bounded.
    /// </summary>
    public double? PlaneFallbackEdge { get; }

    /// <summary>
    ///     The document's reflected-face rule for this key, or null when it is not established (another game or stream).
    /// </summary>
    public SceneReflectedFaces? ReflectedFaces { get; }

    /// <summary>The per-key facing sentence that follows <see cref="FacingCitation" />.</summary>
    private string FacingRead()
    {
        if (!IsBigEndian)
        {
            return Game switch
            {
                BethesdaGame.FalloutNewVegas =>
                    "This read is FNV PC (the game option names Fallout: New Vegas; little-endian), the build the " +
                    "re-check examined.",
                BethesdaGame.Fallout3 =>
                    "This read is Fallout 3 PC (little-endian): its runtime was classified per mode within 1.1e-6 of the " +
                    "GECK (SSE float32 arithmetic, RE-25 item 5); its plane-fallback rim and near-axis tilt arithmetic " +
                    "were not bounded, so no plane-fallback edge is declared.",
                _ =>
                    "This read is little-endian (PC) with the game not established: the FNV and Fallout 3 PC builds were " +
                    "both examined and agree per mode; Fallout 3's plane-fallback rim arithmetic was not bounded, so no " +
                    "plane-fallback edge is declared."
            };
        }

        var platform = Platform.IsAssumed
            ? "This read is big-endian with bmt.platform not set, so Xbox 360 is assumed, but the file may be a " +
              "PlayStation 3 file and no PS3 build of NiBillboardNode was examined; the FNV PC runtime, the FNV Xbox 360 " +
              "MemDebug build and the Fallout 3 PC runtime agree, and no plane-fallback edge is declared."
            : Platform.Platform == NifPackedPlatform.Ps3
                ? "This read is a PlayStation 3 stream (bmt.platform=ps3): no PS3 build of NiBillboardNode was " +
                  "examined; the reading rests on the FNV PC runtime, the FNV Xbox 360 MemDebug build and the Fallout 3 " +
                  "PC runtime agreeing, and no plane-fallback edge is declared."
                : "This read is an Xbox 360 stream (bmt.platform=x360): the FNV Xbox 360 MemDebug build agrees with the " +
                  "GECK structurally; its PowerPC float32 arithmetic was not emulated, so no plane-fallback edge is " +
                  "declared.";
        return platform + ConsoleGameSuffix();
    }

    /// <summary>The per-key schedule sentence that follows <see cref="ScheduleCitation" />.</summary>
    private string ScheduleRead()
    {
        if (!IsBigEndian)
        {
            return Game switch
            {
                BethesdaGame.FalloutNewVegas =>
                    "This read is FNV PC, whose OnVisible, RotateToCamera and update-pass overrides are " +
                    "byte-identical to the GECK's.",
                BethesdaGame.Fallout3 =>
                    "This read is Fallout 3 PC, whose update and culling passes were emulated with the real update code.",
                _ => "This read is PC with the game not established; the GECK and the Fallout 3 PC runtime agree."
            };
        }

        var platform = Platform.IsAssumed
            ? "This read is big-endian with bmt.platform not set (Xbox 360 assumed); the file may be a PlayStation 3 " +
              "file, and no PS3 build was examined."
            : Platform.Platform == NifPackedPlatform.Ps3
                ? "This read is a PlayStation 3 stream: no PS3 build was examined; the PC runtimes and the Xbox 360 " +
                  "build agree."
                : "This read is an Xbox 360 stream: the MemDebug build's OnVisible calls RotateToCamera with the " +
                  "culler's camera, agreeing with the GECK's control flow.";
        return platform + ConsoleGameSuffix();
    }

    /// <summary>The per-key sentence that follows <see cref="MirrorCullCitation" />.</summary>
    private string CullRead()
    {
        if (!IsBigEndian)
        {
            return Game switch
            {
                BethesdaGame.FalloutNewVegas => "This read is FNV PC, the build examined.",
                BethesdaGame.Fallout3 => "This read is Fallout 3 PC, whose cull path was not examined.",
                _ => "This read is PC with the game not established, so it may be a Fallout 3 file."
            };
        }

        if (Platform.IsAssumed)
        {
            return "This read is big-endian with bmt.platform not set (Xbox 360 assumed), so it may be a PlayStation 3 " +
                   "file." + ConsoleGameSuffix();
        }

        if (Platform.Platform == NifPackedPlatform.Ps3)
        {
            return "This read is a PlayStation 3 stream (bmt.platform=ps3)." + ConsoleGameSuffix();
        }

        return Game == BethesdaGame.FalloutNewVegas
            ? "This read is an FNV Xbox 360 stream: the PDB-named 2010-8-22 MemDebug build has no caller of " +
              "SetLeftRightSwap and the same batch-group and state-list cull rules; its batch-level cull lock differs " +
              "from PC only for stencil-CW geometry."
            : "This read is an Xbox 360 stream (bmt.platform=x360)." + ConsoleGameSuffix();
    }

    /// <summary>The game sentence of a console read: empty for FNV, else what was not examined.</summary>
    private string ConsoleGameSuffix()
    {
        return Game switch
        {
            BethesdaGame.FalloutNewVegas => "",
            BethesdaGame.Fallout3 => " No Fallout 3 console build was examined.",
            _ => " The game is not established, and no Fallout 3 console build was examined."
        };
    }

    /// <summary>The key as one short statement.</summary>
    private string Describe()
    {
        var game = Game is { } named ? "game " + named : "game not established";
        var order = !IsBigEndian
            ? "little-endian (PC)"
            : Platform.IsAssumed
                ? "big-endian, platform not established (Xbox 360 assumed)"
                : "big-endian, platform " + Platform.OptionValue;
        return string.Create(CultureInfo.InvariantCulture,
            $"NIF {FormatVersion(BinaryVersion)}, user {UserVersion}, BS {BsVersion}; {game}; {order}");
    }

    /// <summary>Formats a packed NIF version as its dotted form (0x14020007 is 20.2.0.7).</summary>
    private static string FormatVersion(uint version)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{version >> 24}.{(version >> 16) & 0xFF}.{(version >> 8) & 0xFF}.{version & 0xFF}");
    }
}
