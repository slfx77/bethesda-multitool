using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     The classic-game arm of the semantic loader. Where the ESM/DMP arms scan a record stream,
///     a classic source is AN INSTALL: the profile + root resolve via <see cref="ClassicGameLocator" />,
///     the per-game record source synthesizes a <c>RecordCollection</c> of generic records
///     (<see cref="ClassicFormIdScheme" /> ids), and everything downstream — stats/list/show/diff,
///     the resolver, the GUI Records tab — consumes it exactly as it consumes the Morrowind parse.
///     <para>
///         Arena synthesizes today. The remaining per-game synthesizers (Fallout PRO/MSG/MAP,
///         Daggerfall MAPS, …) plug into the same switch as their format layers land. A game whose
///         synthesizer has not landed yet still resolves and returns an empty collection stamped
///         with its profile, so the plumbing above it is exercised from the first milestone.
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

        var located = Directory.Exists(filePath)
            ? ClassicGameLocator.DetectFromDirectory(filePath) is { } profile ? (profile, Path.GetFullPath(filePath)) : null
            : ClassicGameLocator.DetectRootForFile(filePath);

        if (located is not var (resolvedProfile, root))
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(filePath)}' is not inside a recognizable classic game install " +
                "(no profile's install markers matched the directory or its ancestors).");
        }

        var records = new RecordCollection { Game = resolvedProfile.Game };
        switch (resolvedProfile.Game)
        {
            case BethesdaGame.Arena:
                ArenaRecordSource.Populate(root, records, cancellationToken);
                break;
            default:
                // No synthesizer for this game yet — the empty collection is the honest answer.
                break;
        }

        var rawResult = new AnalysisResult
        {
            FileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0
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
}
