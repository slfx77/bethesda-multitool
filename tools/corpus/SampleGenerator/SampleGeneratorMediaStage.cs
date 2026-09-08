using System.Diagnostics;
using static SampleGenerator.SampleGeneratorConfig;
using static SampleGenerator.SampleGeneratorFileOperations;

namespace SampleGenerator;

/// <summary>
///     Expands a build's original media into its tree, then relocates the media itself to
///     <c>Sample/Media/&lt;build&gt;/</c> so a build directory holds game files and nothing else.
///     <para>
///         Only files a catalog entry <em>declares</em> as media are ever touched. Scanning by
///         extension instead would have moved real game content out of three builds — see
///         <see cref="MediaItem" />.
///     </para>
///     <para>
///         Idempotent: once a build's declared media has moved out, there is nothing left to find
///         and the stage does nothing. Extraction always happens before the move, so a failed
///         extraction leaves the media where it is rather than losing it.
///     </para>
/// </summary>
internal static class SampleGeneratorMediaStage
{
    /// <summary>Outcome of processing one build's media.</summary>
    internal readonly record struct MediaResult(int Extracted, int Relocated, int Failed);

    internal static MediaResult Apply(CatalogEntry entry, string buildDir, string buildName, bool dryRun)
    {
        if (entry.Media is not { Length: > 0 } items)
        {
            return default;
        }

        var extracted = 0;
        var relocated = 0;
        var failed = 0;

        foreach (var item in items)
        {
            // Only an item that declares itself recursive moves whole; see MediaItem.Recursive
            // for why inferring that from "it is a directory" is wrong.
            if (item.Recursive && item.PathInBuild != ".")
            {
                var directory = Path.Combine(buildDir, item.PathInBuild);
                if (Directory.Exists(directory))
                {
                    relocated += RelocateTree(directory, item.PathInBuild, buildName, dryRun);
                    continue;
                }
            }

            var (files, container) = CollectMedia(buildDir, item);
            if (files.Count == 0 || AlreadyRelocated(buildName, container))
            {
                // ⚠⚠ Once a disc has been expanded, a PathInBuild of "." collects the EXTRACTED
                // GAME FILES, not the media — Arena's tree is full of .IMG and .DAT that were never
                // media. Deciding "done" by what is already in Sample/Media, rather than by
                // sniffing what is left in the build, is what keeps that from ever misfiring.
                continue;
            }

            if (dryRun)
            {
                Console.WriteLine($"    would {item.Extraction} {files.Count} media file(s) " +
                                  $"from '{item.PathInBuild}' then move them to Media/{buildName}");
                relocated += files.Count;
                continue;
            }

            if (item.Extraction != MediaExtraction.KeepPacked)
            {
                var target = ResolveExtractTarget(buildDir, item);
                if (item.Extraction == MediaExtraction.ExpandInPlace &&
                    Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                {
                    continue;
                }

                if (!Extract(item, files, target))
                {
                    // Leave the media in place: a build with unreadable media is recoverable, a
                    // build whose media was moved away after a failed extraction is not.
                    Console.Error.WriteLine($"    extraction FAILED for '{item.PathInBuild}' — media left in place");
                    failed++;
                    continue;
                }

                extracted++;

                // The container stays in the build: it is part of the tree as shipped.
                if (item.Extraction == MediaExtraction.ExpandInPlace)
                {
                    continue;
                }
            }

            if (item.Extraction == MediaExtraction.RawDisc &&
                !files.Any(f => f.EndsWith(".cue", StringComparison.OrdinalIgnoreCase)))
            {
                // Same case as above: these are extracted game files, not the disc's tracks.
                continue;
            }

            relocated += Relocate(files, buildDir, buildName, container);
        }

        return new MediaResult(extracted, relocated, failed);
    }

    /// <summary>
    ///     The declared media's actual files, plus the build-relative directory they sit in.
    ///     A path of <c>.</c> or a directory means every file directly inside it; anything else is
    ///     a single named file.
    /// </summary>
    private static (List<string> Files, string Container) CollectMedia(string buildDir, MediaItem item)
    {
        var files = new List<string>();

        if (item.PathInBuild == ".")
        {
            // ⚠ The generator's own manifest sits at the build root while the older layout is
            // being cleaned up, and it is not media — collecting it here would file build.json
            // under Sample/Media.
            files.AddRange(Directory
                .EnumerateFiles(buildDir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).Equals("build.json", StringComparison.OrdinalIgnoreCase)));
            return (files, string.Empty);
        }

        var path = SampleGeneratorPathSafety.ResolveDestinationPath(buildDir, item.PathInBuild);
        if (Directory.Exists(path))
        {
            files.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly));
            return (files, item.PathInBuild);
        }

        if (File.Exists(path))
        {
            files.Add(path);
            return (files, Path.GetDirectoryName(item.PathInBuild) ?? string.Empty);
        }

        return (files, string.Empty);
    }

    private static string ResolveExtractTarget(string buildDir, MediaItem item)
    {
        var relative = item.ExtractTo ?? item.PathInBuild;
        return relative == "."
            ? buildDir
            : SampleGeneratorPathSafety.ResolveDestinationPath(buildDir, relative);
    }

    private static bool Extract(MediaItem item, IReadOnlyList<string> files, string target)
    {
        Directory.CreateDirectory(target);

        return item.Extraction switch
        {
            MediaExtraction.RawDisc => ExtractRawDisc(files, target),
            MediaExtraction.Package => ExtractPackages(files, target),
            // Installer cabinets go through this repository's own CLI, whose InstallShield backend
            // reads the "ISc(" format that 7z does not handle reliably.
            MediaExtraction.ExpandInPlace => ExtractWithRepositoryCli(files, target),
            _ => true,
        };
    }

    /// <summary>
    ///     Raw 2352-byte-sector CDs. 7z cannot read these at all, so extraction goes through this
    ///     repository's own CLI and its <c>DiscImageBackend</c>. The <c>.cue</c> is the entry point;
    ///     the <c>.bin</c> tracks beside it are pulled in by the sheet.
    /// </summary>
    private static bool ExtractRawDisc(IReadOnlyList<string> files, string target)
    {
        var cue = files.FirstOrDefault(f => f.EndsWith(".cue", StringComparison.OrdinalIgnoreCase));
        if (cue is null)
        {
            // Not a failure: a raw-disc item with no sheet means the disc was already expanded and
            // its tracks relocated, so what CollectMedia found is the EXTRACTED tree, not media.
            // Reporting this as an extraction failure made a healthy corpus look broken on every
            // run and, worse, would have invited "fixing" it by moving game files into Media.
            return true;
        }

        var cli = FindRepositoryCli();
        if (cli is null)
        {
            Console.Error.WriteLine(
                "      the BethesdaMultitool CLI is not built; run a Release build so raw CDs can be extracted");
            return false;
        }

        return Run(cli, ["archive", "extract", cue, "-o", target]);
    }

    /// <summary>Expands each declared file through this repository's own archive reader.</summary>
    private static bool ExtractWithRepositoryCli(IReadOnlyList<string> files, string target)
    {
        var cli = FindRepositoryCli();
        if (cli is null)
        {
            Console.Error.WriteLine(
                "      the BethesdaMultitool CLI is not built; run a Release build so containers can be expanded");
            return false;
        }

        var ok = true;
        foreach (var file in files)
        {
            if (!Run(cli, ["archive", "extract", file, "-o", target]))
            {
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>ISO 9660, UDF, FAT floppy images and ZIP/JAR packages, each via 7z.</summary>
    private static bool ExtractPackages(IReadOnlyList<string> files, string target)
    {
        // A media directory can hold read-me files and checksums beside the image itself. Those
        // relocate with the rest but are not archives, and handing one to 7z is a guaranteed
        // failure that would abort the whole item.
        var expandable = files.Where(IsExpandable).ToArray();
        if (expandable.Length == 0)
        {
            return true;
        }

        var ok = true;
        foreach (var file in expandable)
        {
            var destination = expandable.Length > 1
                ? SampleGeneratorPathSafety.ResolveDestinationPath(
                    target, SampleGeneratorPathSafety.SanitizePathSegment(Path.GetFileNameWithoutExtension(file)))
                : target;
            Directory.CreateDirectory(destination);

            if (!Expand7z(file, destination))
            {
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>
    ///     Expands one archive with 7z, judging success by what actually landed rather than by the
    ///     exit code.
    ///     <para>
    ///         ⚠⚠ 7z exits 2 ("Headers Error", "There are data after the end of archive") on the
    ///         PS3 UDF 2.50 Blu-ray while still extracting every file — the sibling Neversoft
    ///         generator records the same behaviour. Treating exit 2 as failure left that image
    ///         unexpanded. So on a non-zero exit the archive is listed and the extracted file count
    ///         compared against it; the extraction is accepted only if everything the listing
    ///         promised is on disk.
    ///     </para>
    /// </summary>
    private static bool Expand7z(string archive, string destination)
    {
        if (Run("7z", ["x", archive, $"-o{destination}", "-y"]))
        {
            return true;
        }

        var expected = CountEntries(archive);
        if (expected <= 0)
        {
            return false;
        }

        var actual = Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count();
        if (actual >= expected)
        {
            Console.WriteLine(
                $"      {Path.GetFileName(archive)}: 7z reported an error but all {actual} of " +
                $"{expected} listed files extracted — accepted");
            return true;
        }

        Console.Error.WriteLine(
            $"      {Path.GetFileName(archive)}: only {actual} of {expected} listed files extracted");
        return false;
    }

    /// <summary>The number of files <c>7z l</c> reports for an archive, or -1 when it cannot say.</summary>
    private static int CountEntries(string archive)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "7z",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("l");
        startInfo.ArgumentList.Add(archive);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return -1;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();

            // The summary line reads e.g. "... 187 files, 46 folders".
            var match = System.Text.RegularExpressions.Regex.Match(
                output, @"(\d+)\s+files", System.Text.RegularExpressions.RegexOptions.RightToLeft);
            return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException)
        {
            return -1;
        }
    }

    /// <summary>
    ///     Extensions 7z can expand here: disc images, floppy images and ZIP-family packages. A
    ///     <c>.cue</c> is excluded because it is a text sheet describing its <c>.bin</c> tracks,
    ///     not an archive — raw CDs go through <see cref="ExtractRawDisc" /> instead.
    /// </summary>
    private static bool IsExpandable(string path) =>
        Path.GetExtension(path).ToLowerInvariant()
            is ".iso" or ".ima" or ".img" or ".zip" or ".jar" or ".7z" or ".rar" or ".cab";

    /// <summary>
    ///     Whether this item's media is already sitting in <c>Sample/Media</c>, which makes the
    ///     item complete regardless of what remains in the build directory.
    /// </summary>
    private static bool AlreadyRelocated(string buildName, string container)
    {
        var mediaRoot = Path.Combine(SampleMedia, buildName);
        var target = string.IsNullOrEmpty(container) ? mediaRoot : Path.Combine(mediaRoot, container);
        if (!Directory.Exists(target))
        {
            return false;
        }

        return string.IsNullOrEmpty(container)
            ? Directory.EnumerateFiles(target, "*", SearchOption.TopDirectoryOnly).Any()
            : Directory.EnumerateFileSystemEntries(target).Any();
    }

    /// <summary>Moves a whole media directory to <c>Sample/Media/&lt;build&gt;/</c>.</summary>
    private static int RelocateTree(string sourceDir, string relative, string buildName, bool dryRun)
    {
        var (files, _) = MeasureTree(sourceDir);
        if (files == 0)
        {
            return 0;
        }

        var mediaRoot = SampleGeneratorPathSafety.ResolveDestinationPath(SampleMedia, buildName);
        var destination = SampleGeneratorPathSafety.ResolveDestinationPath(mediaRoot, relative);

        if (dryRun)
        {
            Console.WriteLine($"    would move media directory '{relative}' ({files} file(s)) to Media/{buildName}");
            return files;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(destination))!);
        if (Directory.Exists(destination))
        {
            // ⛔ Do NOT delete the source here. This branch used to treat "the destination exists"
            // as "the build copy is a duplicate" and wipe it — which destroyed Redguard's iso/ and
            // extracted/ trees, because an earlier top-level relocation had already created the
            // destination for the disc image alone. Refuse and report; a human can compare.
            Console.Error.WriteLine(
                $"      media directory '{relative}' already exists under Media/{buildName}; " +
                "leaving the build copy alone rather than assuming it is a duplicate");
            return 0;
        }

        MoveTree(sourceDir, destination);
        return files;
    }

    private static int Relocate(IReadOnlyList<string> files, string buildDir, string buildName, string container)
    {
        var mediaRoot = SampleGeneratorPathSafety.ResolveDestinationPath(SampleMedia, buildName);
        var destinationDir = string.IsNullOrEmpty(container)
            ? mediaRoot
            : SampleGeneratorPathSafety.ResolveDestinationPath(mediaRoot, container);
        Directory.CreateDirectory(destinationDir);

        var moved = 0;
        foreach (var file in files)
        {
            var target = SampleGeneratorPathSafety.ResolveDestinationPath(
                destinationDir, Path.GetFileName(file));
            if (File.Exists(target))
            {
                // Already relocated by an earlier run; drop the duplicate left in the build.
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                moved++;
                continue;
            }

            File.SetAttributes(file, FileAttributes.Normal);
            File.Move(file, target);
            moved++;
        }

        // A directory that held nothing but media is not part of the build tree.
        if (!string.IsNullOrEmpty(container))
        {
            var sourceDir = SampleGeneratorPathSafety.ResolveDestinationPath(buildDir, container);
            if (Directory.Exists(sourceDir) && !Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                Directory.Delete(sourceDir);
            }
        }

        return moved;
    }

    /// <summary>The Release CLI build, walked up from this tool's own output directory.</summary>
    private static string? FindRepositoryCli()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "BethesdaMultitool", "bin", "Release", "net10.0",
                "BethesdaMultitool.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool Run(string fileName, string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine($"      could not start {fileName}");
            return false;
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        // 7z warns with exit code 2 on some images while still extracting everything it can.
        if (process.ExitCode is 0 or 1)
        {
            return true;
        }

        Console.Error.WriteLine($"      {Path.GetFileName(fileName)} exited {process.ExitCode}: " +
                                $"{Truncate(error.GetAwaiter().GetResult())}{Truncate(output.GetAwaiter().GetResult())}");
        return false;
    }

    private static string Truncate(string text) =>
        text.Length <= 400 ? text.Trim() : text[..400].Trim();
}
