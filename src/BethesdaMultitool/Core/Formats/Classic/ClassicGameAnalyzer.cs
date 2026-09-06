using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     The classic-game arm of the semantic loader. Where the ESM/DMP arms scan a record stream,
///     a classic source is AN INSTALL: the profile + root resolve via <see cref="ClassicGameLocator" />,
///     the per-game record source synthesizes a <c>RecordCollection</c> of generic records
///     (<see cref="ClassicFormIdScheme" /> ids), and everything downstream — stats/list/show/diff,
///     the resolver, the GUI Records tab — consumes it exactly as it consumes the Morrowind parse.
///     <para>
///         Arena and Daggerfall synthesize today. The remaining per-game synthesizers (Fallout
///         PRO/MSG/MAP, …) plug into the same switch as their format layers land. A game whose
///         synthesizer has not landed yet still resolves and returns an empty collection stamped
///         with its profile, so the plumbing above it is exercised from the first milestone.
///     </para>
///     <para>
///         The TES Travels J2ME titles are installs packaged as ONE JAR, so the source may also be
///         that file: <see cref="ClassicGameLocator.DetectFromArchive" /> claims it and the record
///         sources read through the mounted <see cref="IGameFileSystem" /> rather than a directory,
///         which makes a JAR and the directory it was unpacked into interchangeable.
///     </para>
/// </summary>
internal static class ClassicGameAnalyzer
{
    /// <summary>
    ///     Loads the classic install owning <paramref name="filePath" /> (a declared artifact file,
    ///     or an install root directory) into a <see cref="UnifiedAnalysisResult" />.
    /// </summary>
    public static Task<UnifiedAnalysisResult> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (Core.Games.GameProfile Profile, string Root)? located;
        if (Directory.Exists(filePath))
        {
            located = ClassicGameLocator.DetectFromDirectory(filePath) is { } profile
                ? (profile, Path.GetFullPath(filePath))
                : null;
        }
        else if (ClassicGameLocator.DetectFromArchive(filePath) is { } packaged)
        {
            located = (packaged, Path.GetFullPath(filePath));
        }
        else if (ClassicSourceProbe.TryDetectDiscImage(filePath) is { } disc)
        {
            // A console disc image is an install in one file, like a J2ME JAR — the ISO mounts
            // through the format layer, so the locator sees only its entry names.
            located = (disc, Path.GetFullPath(filePath));
        }
        else
        {
            located = ClassicGameLocator.DetectRootForFile(filePath);
        }

        if (located is not var (resolvedProfile, root))
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(filePath)}' is not inside a recognizable classic game install " +
                "(no profile's install markers matched the directory or its ancestors).");
        }

        // The install root is where the markers matched; the data lives under the profile's loose
        // root (Daggerfall's ARENA2, empty for Arena whose root IS the data directory).
        var dataRoot = Path.Combine(root, resolvedProfile.ClassicLooseRoot);
        var records = new RecordCollection { Game = resolvedProfile.Game };
        switch (resolvedProfile.Game)
        {
            case BethesdaGame.Arena:
                ArenaRecordSource.Populate(dataRoot, records, cancellationToken);
                break;
            case BethesdaGame.Daggerfall:
                DaggerfallRecordSource.Populate(dataRoot, records, cancellationToken);
                break;
            case BethesdaGame.Battlespire:
                BattlespireRecordSource.Populate(dataRoot, records, cancellationToken);
                break;
            case BethesdaGame.Redguard:
                RedguardRecordSource.Populate(dataRoot, records, cancellationToken);
                break;
            case BethesdaGame.Fallout1:
            case BethesdaGame.Fallout2:
                // Fallout's assets live in the DATs, not loose, so this one takes the INSTALL root
                // and mounts the layered file system itself rather than reading a data directory.
                FalloutRecordSource.Populate(root, records, resolvedProfile.Game, cancellationToken);
                break;
            case BethesdaGame.Stormhold:
            case BethesdaGame.Dawnstar:
            case BethesdaGame.Shadowkey:
            case BethesdaGame.OblivionMobile:
            case BethesdaGame.OblivionPsp:
                PopulateTravels(resolvedProfile, root, records, cancellationToken);
                break;
            case BethesdaGame.FalloutBrotherhoodOfSteel:
            {
                // Like a J2ME JAR, this install is ONE FILE — a PS2 disc image — so it mounts as an
                // archive; a directory is the extracted disc and mounts through the profile.
                using var disc = File.Exists(root)
                    ? GameFileSystem.OpenArchive(root)
                    : GameFileSystem.OpenGameRoot(resolvedProfile, root);
                BosRecordSource.Populate(disc, records, cancellationToken);
                break;
            }
            default:
                // No synthesizer for this game yet — the empty collection is the honest answer.
                break;
        }

        // Stormhold and Dawnstar carry NPC dialogue as grouped strings, and the Dialogue viewer
        // reads RecordCollection.DialogueTree — so attaching one here lights up the existing tab
        // with no viewer change at all. The other three Travels games have no speaker-grouped
        // string table, and an empty tree would advertise a feature they do not have.
        var dialogueSignature = resolvedProfile.Game switch
        {
            BethesdaGame.Stormhold => StormholdRecordSource.SignaturePrefix + TravelsRecordSynthesizer.StringCode,
            BethesdaGame.Dawnstar => DawnstarRecordSource.SignaturePrefix + TravelsRecordSynthesizer.StringCode,
            _ => null
        };

        if (dialogueSignature is not null)
        {
            var tree = TravelsDialogueTreeBuilder.Build(records.GenericRecords, dialogueSignature);
            if (tree.OrphanTopics.Count > 0)
            {
                records = records with { DialogueTree = tree };
            }
        }

        var rawResult = new AnalysisResult
        {
            // The source is either an install DIRECTORY (nothing to size or map) or the single
            // JAR that IS a J2ME install. Downstream display and session code reads FilePath, so
            // stamp the resolved root here rather than leaving it at its empty default.
            FilePath = root,
            FileSize = AnalysisSourcePath.SizeOf(filePath)
        };

        var result = new UnifiedAnalysisResult
        {
            FileType = AnalysisFileType.ClassicGameData,
            Records = records,
            Resolver = records.CreateResolver(),
            RawResult = rawResult,
            FilePath = root
        };
        return Task.FromResult(result);
    }

    /// <summary>
    ///     Mounts a Travels install — the JAR itself when <paramref name="root" /> is a file, else
    ///     the directory through its profile (loose root + declared archive globs) — and hands the
    ///     filesystem to the game's record source.
    /// </summary>
    private static void PopulateTravels(
        Core.Games.GameProfile profile, string root, RecordCollection records, CancellationToken cancellationToken)
    {
        using var install = File.Exists(root)
            ? GameFileSystem.OpenArchive(root)
            : GameFileSystem.OpenGameRoot(profile, root);

        switch (profile.Game)
        {
            case BethesdaGame.Stormhold:
                StormholdRecordSource.Populate(install, records, cancellationToken);
                break;
            case BethesdaGame.Dawnstar:
                DawnstarRecordSource.Populate(install, records, cancellationToken);
                break;
            case BethesdaGame.Shadowkey:
                ShadowkeyRecordSource.Populate(install, records, cancellationToken);
                break;
            case BethesdaGame.OblivionMobile:
                OblivionMobileRecordSource.Populate(install, records, cancellationToken);
                break;
            case BethesdaGame.OblivionPsp:
                // The odd one out: a GR.ARC pack reaches 216 MB and this source needs only its
                // header, record table and name table, so it reads from the path rather than
                // pulling the whole pack through the mounted filesystem.
                OblivionPspRecordSource.Populate(root, records, cancellationToken);
                break;
        }
    }
}
