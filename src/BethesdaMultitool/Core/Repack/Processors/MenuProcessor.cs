using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Menus;

namespace BethesdaMultitool.Core.Repack.Processors;

/// <summary>
///     Unpacks <c>Data\final_master_xml.dat</c> into the loose <c>Data\menus\**\*.xml</c> tree the
///     PC engine reads.
///     <para>
///         Without this the converted build has no interface at all: the Xbox 360
///         <c>Fallout - Misc.bsa</c> holds 20 files and exactly one <c>menus\</c> entry
///         (<c>falloutdict.txt</c>), because the console keeps all 41 menu documents in
///         <c>final_master_xml.dat</c> instead. The PC engine never reads that container — the
///         loader survives in the retail binary but nothing calls it — so it opens
///         <c>Data\Menus\globals.xml</c>, gets nothing back, and faults during interface start-up
///         about three seconds into boot.
///     </para>
///     <para>
///         Menus are written loose rather than folded into the repacked Misc.bsa: loose files win
///         over archived ones, which keeps them inspectable and lets a user drop in a replacement
///         without repacking.
///     </para>
/// </summary>
public sealed class MenuProcessor : IRepackProcessor
{
    /// <summary>The container's fixed name in a 360 Data folder.</summary>
    public const string ContainerFileName = "final_master_xml.dat";

    public string Name => "Menus";

    public async Task<int> ProcessAsync(
        RepackerOptions options,
        IProgress<RepackerProgress> progress,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(options.SourceFolder, "Data", ContainerFileName);
        if (!File.Exists(sourcePath))
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Menus,
                Message = $"{ContainerFileName} not found, skipping menu extraction",
                IsComplete = true,
                Success = true
            });
            return 0;
        }

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Menus,
            Message = $"Reading {ContainerFileName}..."
        });

        var raw = await File.ReadAllBytesAsync(sourcePath, cancellationToken);

        IReadOnlyList<FinalMasterXmlArchive.Entry> entries;
        try
        {
            entries = FinalMasterXmlArchive.Read(raw);
        }
        catch (InvalidDataException ex)
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Menus,
                Message = $"Failed to read {ContainerFileName}: {ex.Message}",
                IsComplete = true,
                Success = false,
                Error = ex.Message
            });
            return 0;
        }

        var menusRoot = Path.Combine(options.OutputFolder, "Data");
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = FinalMasterXmlLayout.ToPcPath(entry.Name);
            await WriteMenuAsync(menusRoot, relativePath, entry.Xml, cancellationToken);
            written.Add(relativePath);

            if (written.Count % 10 == 0 || written.Count == entries.Count)
            {
                progress.Report(new RepackerProgress
                {
                    Phase = RepackPhase.Menus,
                    ItemsProcessed = written.Count,
                    TotalItems = entries.Count,
                    CurrentItem = relativePath,
                    Message = $"Menus {written.Count}/{entries.Count}"
                });
            }
        }

        var consoleCount = written.Count;
        var backfilled = BackfillFromPcDonor(options, menusRoot, written, progress);

        var stillAbsent = FinalMasterXmlLayout.MenusAbsentFromConsoleBuild
            .Where(m => !written.Contains(m))
            .ToList();
        var note = stillAbsent.Count > 0
            ? $" — WARNING: {stillAbsent.Count} PC menus have no console counterpart and no donor "
              + $"supplied them: {string.Join(", ", stillAbsent.Select(Path.GetFileName))}"
            : string.Empty;

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Menus,
            ItemsProcessed = written.Count,
            TotalItems = written.Count,
            Message = $"Extracted {consoleCount} console menus"
                      + (backfilled > 0 ? $" + backfilled {backfilled} from the PC donor" : string.Empty)
                      + note,
            IsComplete = true,
            Success = true
        });

        return written.Count;
    }

    private static async Task WriteMenuAsync(
        string menusRoot, string relativePath, byte[] data, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(menusRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, data, cancellationToken);
    }

    /// <summary>
    ///     Copies every <c>menus\</c> document from a vanilla PC <c>Fallout - Misc.bsa</c> that the
    ///     console container did not supply — the 9 menu classes the 360 build has no counterpart
    ///     for (BookMenu, LoadGameMenu, SaveGameMenu, TraitSelectMenu, …) plus the
    ///     <c>menus\prefabs\</c> tree they <c>&lt;include&gt;</c>. Console documents already written
    ///     are never overwritten, so the converted build keeps the console interface wherever one
    ///     exists and only falls back to PC layout where it must.
    /// </summary>
    /// <returns>The number of files copied from the donor.</returns>
    private static int BackfillFromPcDonor(
        RepackerOptions options,
        string menusRoot,
        HashSet<string> written,
        IProgress<RepackerProgress> progress)
    {
        var donorArchive = ResolveDonorArchive(options.PcMenuDonorPath);
        if (donorArchive is null)
        {
            return 0;
        }

        progress.Report(new RepackerProgress
        {
            Phase = RepackPhase.Menus,
            Message = $"Backfilling PC menus from {Path.GetFileName(donorArchive)}..."
        });

        var copied = 0;
        try
        {
            using var extractor = new BsaExtractor(donorArchive);
            foreach (var folder in extractor.Archive.Folders)
            {
                foreach (var file in folder.Files)
                {
                    var archivePath = file.FullPath;
                    if (!FinalMasterXmlLayout.IsBackfillCandidate(archivePath) ||
                        written.Contains(archivePath))
                    {
                        continue;
                    }

                    var data = extractor.ExtractFile(file);
                    var destination = Path.Combine(menusRoot, archivePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination, data);
                    written.Add(archivePath);
                    copied++;
                }
            }
        }
        catch (Exception ex)
        {
            progress.Report(new RepackerProgress
            {
                Phase = RepackPhase.Menus,
                Message = $"PC menu backfill failed ({ex.Message}); console menus were still written"
            });
            return copied;
        }

        return copied;
    }

    /// <summary>
    ///     Accepts either a PC <c>Data</c> folder or a direct path to a <c>Fallout - Misc.bsa</c>.
    ///     Returns null when nothing usable is configured.
    /// </summary>
    private static string? ResolveDonorArchive(string? donorPath)
    {
        if (string.IsNullOrWhiteSpace(donorPath))
        {
            return null;
        }

        if (File.Exists(donorPath))
        {
            return donorPath;
        }

        if (!Directory.Exists(donorPath))
        {
            return null;
        }

        var misc = Path.Combine(donorPath, "Fallout - Misc.bsa");
        if (File.Exists(misc))
        {
            return misc;
        }

        // Tolerate being handed the game root rather than its Data folder.
        var nested = Path.Combine(donorPath, "Data", "Fallout - Misc.bsa");
        return File.Exists(nested) ? nested : null;
    }
}
