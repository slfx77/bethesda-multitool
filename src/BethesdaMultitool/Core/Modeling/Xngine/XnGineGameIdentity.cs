using System.Globalization;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling.Units;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The game an XnGine <c>.3D</c> record belongs to, established by the chain of cut-1c plan section 6.2 (owner
///     decision D7) with its evidence recorded: (1) the <c>bmt.game</c> option, a user assertion; (2) the container the
///     entry came from, through <see cref="ClassicContainerFacts" />; (3) the <c>bmt.classic-game</c> option the shell
///     set from the install walk-up; (4) the record's own bytes. The chain touches no file system: everything it needs
///     arrives through the app options, the item's source and the record bytes. The answer carries the plane-list
///     layout the game implies, the unit row (<see cref="ClassicModelUnits" />), the object id when a numbered archive
///     supplied one, and every diagnostic the chain raised.
/// </summary>
/// <remarks>
///     <para>
///         A step answers only when the content agrees with it (the game's plane-header size walks). The user's
///         <c>--game</c> is the one step that may not be refuted quietly: a conflict throws, because the user asserted a
///         game the bytes cannot belong to. A refuted container or install answer is recorded
///         (<see cref="StepRefutedDiagnostic" />) and the chain continues; the container also gets a diagnostic when it
///         disagrees with an accepted <c>--game</c> (<see cref="ContainerDisagreesDiagnostic" />), so an override never
///         hides what the archive said.
///     </para>
///     <para>
///         The content step is one-way on purpose. A mesh that walks only with 10-byte headers is Battlespire (0 of
///         4,755 Battlespire meshes walk with 8-byte headers, 0 of 4,719 Redguard static meshes walk with 10-byte
///         headers). A mesh that walks only with 8-byte headers and has header +20 non-zero is Redguard (+20 is 0 on
///         10,251 of 10,251 ARCH3D records and non-zero on 52 of 52 loose Redguard files and 4,352 of 4,667 ROB
///         meshes). A mesh that walks only with 8-byte headers and has +20 = 0 is ambiguous between an extracted ARCH3D
///         record (BMT's own <c>archive extract</c> writes them loose, named by id) and one of 315 Redguard ROB meshes,
///         and stays Unknown with a diagnostic naming <c>--game</c>: calling it Redguard would give an extracted
///         Daggerfall record half its size (1/20480 instead of 1/10240) and skip the unfold. A mesh that walks both ways
///         (20 ARCH3D records) is likewise Unknown when nothing else answers.
///     </para>
/// </remarks>
internal sealed class XnGineGameIdentity
{
    /// <summary>The <c>bmt.game</c> values the XnGine readers accept, for messages.</summary>
    public const string GameOptionValues = "daggerfall, battlespire or redguard";

    /// <summary>Diagnostic: the content alone cannot decide between Daggerfall and Redguard (or both layouts walk).</summary>
    public const string AmbiguousDiagnostic = "bmt.xngine.game-ambiguous";

    /// <summary>Diagnostic: a container or install answer was refuted by the content and skipped.</summary>
    public const string StepRefutedDiagnostic = "bmt.xngine.game-step-refuted";

    /// <summary>Diagnostic: the container says another game than the accepted <c>bmt.game</c> option.</summary>
    public const string ContainerDisagreesDiagnostic = "bmt.xngine.game-container-disagrees";

    /// <summary>Diagnostic: the detected install is a classic game that ships no XnGine meshes, so it answers nothing.</summary>
    public const string InstallNotXnGineDiagnostic = "bmt.xngine.game-install-not-xngine";

    /// <summary>
    ///     Diagnostic: the container is a named XnGine BSA without LZSS entries, which answers nothing. Raised only when
    ///     the chain reaches the container step; under an accepted <c>bmt.game</c> the container is never consulted, so
    ///     nothing is said about it.
    /// </summary>
    public const string ContainerSilentDiagnostic = "bmt.xngine.game-container-silent";

    /// <summary>The evidence prefix of an Unknown answer.</summary>
    public const string NotEstablishedEvidence = "game not established";

    private static readonly BethesdaGame[] XnGineGames =
    [
        BethesdaGame.Daggerfall, BethesdaGame.Battlespire, BethesdaGame.Redguard
    ];

    /// <summary>Creates an answer; see <see cref="Resolve" />.</summary>
    private XnGineGameIdentity(BethesdaGame game, XnGineGameIdentityStep step, XnGineMeshLayout layout, string evidence,
        uint? objectId, IReadOnlyList<XnGineIdentityDiagnostic> diagnostics)
    {
        Game = game;
        Step = step;
        Layout = layout;
        Evidence = evidence;
        ObjectId = objectId;
        Diagnostics = diagnostics;
    }

    /// <summary>The game: Daggerfall, Battlespire, Redguard, or <see cref="BethesdaGame.Unknown" /> when no step answered.</summary>
    public BethesdaGame Game { get; }

    /// <summary>The step that answered, or <see cref="XnGineGameIdentityStep.None" />.</summary>
    public XnGineGameIdentityStep Step { get; }

    /// <summary>
    ///     The plane-list layout to read the record with: the game's when established, else the one layout the content
    ///     walks with, else (both walk) the 8-byte layout, stated in a diagnostic.
    /// </summary>
    public XnGineMeshLayout Layout { get; }

    /// <summary>The evidence recorded for the answer (the step, the option or container that supplied it, and the measurement it rests on).</summary>
    public string Evidence { get; }

    /// <summary>The object id a numbered XnGine BSA supplied (the record id), or null when no container names one.</summary>
    public uint? ObjectId { get; }

    /// <summary>Every diagnostic the chain raised, in the order it raised them.</summary>
    public IReadOnlyList<XnGineIdentityDiagnostic> Diagnostics { get; }

    /// <summary>True when a step answered.</summary>
    public bool IsEstablished => Game != BethesdaGame.Unknown;

    /// <summary>The static-mesh unit row for <see cref="Game" /> (the guard row when Unknown); a <c>.3DC</c> reader takes the actor row instead.</summary>
    public SceneUnits Units => ClassicModelUnits.For(Game);

    /// <summary>
    ///     Runs the chain (see the type remarks). <paramref name="container" /> is null for a loose file or a source that
    ///     answers no facts; <paramref name="content" /> must come from the complete record.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     The <c>bmt.game</c> option names no XnGine game, or names one whose plane headers the record does not walk with.
    /// </exception>
    /// <exception cref="InvalidDataException">The record walks with neither plane-header size, so it is no XnGine <c>.3D</c> mesh.</exception>
    public static XnGineGameIdentity Resolve(IReadOnlyDictionary<string, string> appOptions,
        ClassicContainerFacts? container, XnGineContentFacts content)
    {
        ArgumentNullException.ThrowIfNull(appOptions);
        ArgumentNullException.ThrowIfNull(content);
        if (content.WalksWithNeitherLayout)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"The record (tag '{content.Tag}') walks with neither 8-byte nor 10-byte plane headers" +
                $"{(content.IsInconclusive ? " within the bytes measured" : string.Empty)}; it is no XnGine .3D mesh."));
        }

        var diagnostics = new List<XnGineIdentityDiagnostic>();
        var objectId = container?.EntryId;
        var containerAnswer = AnswerFromContainer(container);

        // Step 1: the user's assertion.
        if (appOptions.TryGetValue(BethesdaModelRegistration.GameOption, out var value) &&
            !string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var game = ParseGame(value);
            var layout = LayoutOf(game);
            if (!content.WalksWith(layout))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"The {BethesdaModelRegistration.GameOption} option '{value}' asserts {game}, whose plane headers " +
                    $"are {XnGineContentFacts.PlaneHeaderLengthOf(layout)} bytes, but the record walks only with " +
                    $"{XnGineContentFacts.PlaneHeaderLengthOf(OtherLayout(layout))}-byte plane headers."),
                    nameof(appOptions));
            }

            var source = appOptions.TryGetValue(BethesdaModelRegistration.GameEvidenceOption, out var evidence) &&
                         !string.IsNullOrWhiteSpace(evidence)
                ? $"{BethesdaModelRegistration.GameOption}={value} ({evidence})"
                : $"{BethesdaModelRegistration.GameOption}={value}";
            if (containerAnswer is { } disagreeing && disagreeing.Game != game)
            {
                diagnostics.Add(new XnGineIdentityDiagnostic(ContainerDisagreesDiagnostic,
                    $"the container says {disagreeing.Game} ({disagreeing.Evidence}); {source} overrides it"));
            }

            return new XnGineGameIdentity(game, XnGineGameIdentityStep.GameOption, layout,
                string.Create(CultureInfo.InvariantCulture, $"{game} per {source}"), objectId, diagnostics);
        }

        // Step 2: the container.
        if (containerAnswer is { } fromContainer)
        {
            if (content.WalksWith(LayoutOf(fromContainer.Game)))
            {
                return new XnGineGameIdentity(fromContainer.Game, XnGineGameIdentityStep.Container,
                    LayoutOf(fromContainer.Game), $"{fromContainer.Game} per container: {fromContainer.Evidence}",
                    objectId, diagnostics);
            }

            diagnostics.Add(Refuted("container", fromContainer.Game, fromContainer.Evidence, content));
        }
        else if (container is { Kind: ClassicContainerKind.NamedXnGineBsa })
        {
            diagnostics.Add(new XnGineIdentityDiagnostic(ContainerSilentDiagnostic, string.Create(CultureInfo.InvariantCulture,
                $"named XnGine BSA {container.ContainerName} has no LZSS entry, so the container answers nothing")));
        }

        // Step 3: the install the shell detected.
        if (appOptions.TryGetValue(BethesdaModelRegistration.ClassicGameOption, out var install) &&
            !string.IsNullOrWhiteSpace(install))
        {
            var installEvidence =
                appOptions.TryGetValue(BethesdaModelRegistration.ClassicGameEvidenceOption, out var how) &&
                !string.IsNullOrWhiteSpace(how)
                    ? $"{BethesdaModelRegistration.ClassicGameOption}={install} ({how})"
                    : $"{BethesdaModelRegistration.ClassicGameOption}={install}";
            if (TryParseXnGineGame(install, out var installGame))
            {
                if (content.WalksWith(LayoutOf(installGame)))
                {
                    return new XnGineGameIdentity(installGame, XnGineGameIdentityStep.Install, LayoutOf(installGame),
                        $"{installGame} per install: {installEvidence}", objectId, diagnostics);
                }

                diagnostics.Add(Refuted("install", installGame, installEvidence, content));
            }
            else
            {
                diagnostics.Add(new XnGineIdentityDiagnostic(InstallNotXnGineDiagnostic,
                    $"{installEvidence} names no XnGine game; the install answers nothing"));
            }
        }

        // Step 4: the content.
        if (content.WalksWithBattlespireLayout && !content.WalksWithDaggerfallLayout)
        {
            return new XnGineGameIdentity(BethesdaGame.Battlespire, XnGineGameIdentityStep.Content,
                XnGineMeshLayout.Battlespire,
                "Battlespire per content: walks only with 10-byte plane headers (0 of 4,755 Battlespire meshes walk " +
                "with 8-byte headers; 0 of 4,719 Redguard static meshes walk with 10-byte headers)", objectId,
                diagnostics);
        }

        if (content.WalksWithDaggerfallLayout && !content.WalksWithBattlespireLayout)
        {
            if (content.HeaderPlus20 != 0)
            {
                return new XnGineGameIdentity(BethesdaGame.Redguard, XnGineGameIdentityStep.Content,
                    XnGineMeshLayout.Daggerfall, string.Create(CultureInfo.InvariantCulture,
                        $"Redguard per content: walks only with 8-byte plane headers and header +20 = " +
                        $"{content.HeaderPlus20} (non-zero on 0 of 10,251 ARCH3D records, 52 of 52 loose Redguard " +
                        $"files and 4,352 of 4,667 ROB meshes)"), objectId, diagnostics);
            }

            diagnostics.Add(new XnGineIdentityDiagnostic(AmbiguousDiagnostic,
                "the record walks only with 8-byte plane headers and has header +20 = 0, which is every extracted " +
                "ARCH3D record (10,251 of 10,251 have +20 = 0) and 315 of 4,667 Redguard ROB meshes; pass --game " +
                "daggerfall or --game redguard"));
            return new XnGineGameIdentity(BethesdaGame.Unknown, XnGineGameIdentityStep.None, XnGineMeshLayout.Daggerfall,
                NotEstablishedEvidence + ": 8-byte plane headers with header +20 = 0 (Daggerfall or Redguard)",
                objectId, diagnostics);
        }

        diagnostics.Add(new XnGineIdentityDiagnostic(AmbiguousDiagnostic,
            "the record walks with both plane-header sizes (20 of 10,251 ARCH3D records do); read with 8-byte plane " +
            "headers; pass --game " + GameOptionValues + " to settle it"));
        return new XnGineGameIdentity(BethesdaGame.Unknown, XnGineGameIdentityStep.None, XnGineMeshLayout.Daggerfall,
            NotEstablishedEvidence + ": walks with both plane-header sizes", objectId, diagnostics);
    }

    /// <summary>Parses a <c>bmt.game</c> value: <c>daggerfall</c>, <c>battlespire</c> or <c>redguard</c>, without regard to case.</summary>
    /// <exception cref="ArgumentException">The value names no XnGine game (a Gamebryo game name applies to NIF files only).</exception>
    public static BethesdaGame ParseGame(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (TryParseXnGineGame(value, out var game))
        {
            return game;
        }

        throw new ArgumentException(
            $"The {BethesdaModelRegistration.GameOption} option '{value}' names no XnGine game (use {GameOptionValues}; " +
            "a Gamebryo game name such as fnv applies to NIF files only).", nameof(value));
    }

    /// <summary>True when the value names Daggerfall, Battlespire or Redguard (trimmed, without regard to case).</summary>
    public static bool TryParseXnGineGame(string value, out BethesdaGame game)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        foreach (var candidate in XnGineGames)
        {
            if (string.Equals(trimmed, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                game = candidate;
                return true;
            }
        }

        game = BethesdaGame.Unknown;
        return false;
    }

    /// <summary>The plane-list layout a game's meshes use: 10-byte headers for Battlespire, 8-byte for Daggerfall and Redguard.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The game is not one the XnGine readers serve.</exception>
    public static XnGineMeshLayout LayoutOf(BethesdaGame game)
    {
        return game switch
        {
            BethesdaGame.Battlespire => XnGineMeshLayout.Battlespire,
            BethesdaGame.Daggerfall or BethesdaGame.Redguard => XnGineMeshLayout.Daggerfall,
            _ => throw new ArgumentOutOfRangeException(nameof(game), game, "Not an XnGine game.")
        };
    }

    /// <summary>
    ///     The container step's answer and evidence, or null when there is no container or it answers nothing (a named
    ///     XnGine BSA without LZSS entries; the step itself records that silence, so an accepted <c>bmt.game</c> does not).
    /// </summary>
    private static (BethesdaGame Game, string Evidence)? AnswerFromContainer(ClassicContainerFacts? container)
    {
        if (container is null)
        {
            return null;
        }

        var culture = CultureInfo.InvariantCulture;
        switch (container.Kind)
        {
            case ClassicContainerKind.NumberedXnGineBsa:
                return (BethesdaGame.Daggerfall, string.Create(culture,
                    $"numbered XnGine BSA {container.ContainerName}, record {container.EntryName} at index " +
                    $"{container.EntryIndex} of {container.EntryCount}"));
            case ClassicContainerKind.NamedXnGineBsa when container.ArchiveHasCompressedEntries:
                return (BethesdaGame.Battlespire, string.Create(culture,
                    $"named XnGine BSA {container.ContainerName} with {container.CompressedEntryCount} of " +
                    $"{container.EntryCount} LZSS entries, {container.EntryName} at index {container.EntryIndex}"));
            case ClassicContainerKind.NamedXnGineBsa:
                return null;
            case ClassicContainerKind.RedguardRob:
                return (BethesdaGame.Redguard, string.Create(culture,
                    $"Redguard ROB {container.ContainerName}, segment {container.EntryName} at index " +
                    $"{container.EntryIndex} of {container.EntryCount} (type {container.SegmentType})"));
            default:
                throw new ArgumentOutOfRangeException(nameof(container), container.Kind, "Unknown container kind.");
        }
    }

    /// <summary>The diagnostic for a step whose game the content refutes.</summary>
    private static XnGineIdentityDiagnostic Refuted(string step, BethesdaGame game, string evidence,
        XnGineContentFacts content)
    {
        var layout = LayoutOf(game);
        return new XnGineIdentityDiagnostic(StepRefutedDiagnostic, string.Create(CultureInfo.InvariantCulture,
            $"the {step} says {game} ({evidence}), but the record does not walk with its " +
            $"{XnGineContentFacts.PlaneHeaderLengthOf(layout)}-byte plane headers (it walks with " +
            $"{XnGineContentFacts.PlaneHeaderLengthOf(OtherLayout(layout))}-byte headers); the step is skipped"));
    }

    /// <summary>The layout that is not <paramref name="layout" />.</summary>
    private static XnGineMeshLayout OtherLayout(XnGineMeshLayout layout)
    {
        return layout == XnGineMeshLayout.Battlespire ? XnGineMeshLayout.Daggerfall : XnGineMeshLayout.Battlespire;
    }
}
