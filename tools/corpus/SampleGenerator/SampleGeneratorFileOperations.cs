using System.Diagnostics;
using static SampleGenerator.SampleGeneratorConfig;

namespace SampleGenerator;

/// <summary>
///     Filesystem primitives: mirroring, safe recursive deletion, moving a staged tree into the
///     corpus, expanding a source archive, and re-measuring an executable's COFF timestamp.
/// </summary>
internal static class SampleGeneratorFileOperations
{
    /// <summary>Recursive file copy. Returns the number of files written and their total size.</summary>
    internal static (int Files, long Bytes) MirrorTree(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        var files = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var targetPath = SampleGeneratorPathSafety.ResolveDestinationPath(destinationDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);

            // File.Copy preserves the read-only attribute (disc extractions and installer payloads
            // ship them), which would break the NEXT rebuild's wipe — normalize it away.
            File.SetAttributes(targetPath, FileAttributes.Normal);
            files++;
            bytes += new FileInfo(targetPath).Length;
        }

        return (files, bytes);
    }

    /// <summary>Copies one file into <paramref name="destinationDir" />, keeping its name.</summary>
    internal static (int Files, long Bytes) MirrorFile(string sourceFile, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        var targetPath = SampleGeneratorPathSafety.ResolveDestinationPath(
            destinationDir, Path.GetFileName(sourceFile));
        File.Copy(sourceFile, targetPath, overwrite: true);
        File.SetAttributes(targetPath, FileAttributes.Normal);
        return (1, new FileInfo(targetPath).Length);
    }

    /// <summary>
    ///     Moves a tree already inside <c>Sample/</c> to its catalog location. A move, not a copy:
    ///     the migration relocates ~52 GB that is already in the repository, and on one volume a
    ///     rename is instant. Falls back to copy-then-delete across volumes.
    /// </summary>
    internal static (int Files, long Bytes) MoveTree(string sourceDir, string destinationDir)
    {
        if (Directory.Exists(destinationDir) &&
            Directory.EnumerateFileSystemEntries(destinationDir).Any())
        {
            throw new InvalidOperationException(
                $"Refusing to move onto a non-empty destination: {destinationDir}");
        }

        var measured = MeasureTree(sourceDir);
        Directory.CreateDirectory(Path.GetDirectoryName(
            Path.TrimEndingDirectorySeparator(destinationDir))!);

        if (Directory.Exists(destinationDir))
        {
            Directory.Delete(destinationDir);
        }

        try
        {
            Directory.Move(sourceDir, destinationDir);
            return measured;
        }
        catch (IOException)
        {
            // Cross-volume, or a handle held open somewhere: fall back to copy-then-delete.
            var copied = MirrorTree(sourceDir, destinationDir);
            DeleteTreeUnder(sourceDir, SampleRoot);
            return copied;
        }
    }

    /// <summary>Moves one staged file into its catalog location.</summary>
    internal static (int Files, long Bytes) MoveFile(string sourceFile, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        var targetPath = SampleGeneratorPathSafety.ResolveDestinationPath(
            destinationDir, Path.GetFileName(sourceFile));
        var length = new FileInfo(sourceFile).Length;

        if (File.Exists(targetPath))
        {
            throw new InvalidOperationException($"Refusing to move onto an existing file: {targetPath}");
        }

        File.Move(sourceFile, targetPath);
        File.SetAttributes(targetPath, FileAttributes.Normal);
        return (1, length);
    }

    internal static (int Files, long Bytes) MeasureTree(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return (0, 0);
        }

        var files = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            files++;
            bytes += new FileInfo(file).Length;
        }

        return (files, bytes);
    }

    /// <summary>
    ///     Deletes a tree after proving it is a strict descendant of <paramref name="allowedRoot" />.
    ///     Reparse points anywhere in the path or the tree are rejected so normalization cannot
    ///     escape through a junction into a live game install.
    /// </summary>
    internal static void DeleteTreeUnder(string dir, string allowedRoot)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        if (!SampleGeneratorPathSafety.IsStrictDescendant(target, allowedRoot))
        {
            throw new InvalidOperationException(
                $"Refusing recursive deletion outside {allowedRoot}: {target}");
        }

        SampleGeneratorPathSafety.RejectReparseTraversal(allowedRoot, target);
        if (!Directory.Exists(target))
        {
            return;
        }

        NormalizeTreeForDeletion(target);
        Directory.Delete(target, recursive: true);
    }

    /// <summary>
    ///     Expands a <c>.7z</c>/<c>.zip</c> source into <paramref name="destinationDir" /> with 7z,
    ///     caching the result so a rebuild does not repeat a multi-gigabyte extraction.
    /// </summary>
    internal static bool ExpandArchive(string archivePath, string destinationDir, bool force)
    {
        if (!force && Directory.Exists(destinationDir) &&
            Directory.EnumerateFileSystemEntries(destinationDir).Any())
        {
            return true;
        }

        if (Directory.Exists(destinationDir))
        {
            DeleteTreeUnder(destinationDir, ResearchRoot);
        }

        Directory.CreateDirectory(destinationDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = "7z",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("x");
        startInfo.ArgumentList.Add(archivePath);
        startInfo.ArgumentList.Add($"-o{destinationDir}");
        startInfo.ArgumentList.Add("-y");

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start 7z. Is it on PATH?");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        // 7z warns with exit code 2 on some images while still extracting everything, so judge
        // success by whether anything landed rather than by the exit code alone.
        if (process.ExitCode is 0 or 1)
        {
            return true;
        }

        var extracted = Directory.Exists(destinationDir) &&
                        Directory.EnumerateFiles(destinationDir, "*", SearchOption.AllDirectories).Any();
        if (extracted)
        {
            return true;
        }

        Console.Error.WriteLine(
            $"    7z exited {process.ExitCode} for {Path.GetFileName(archivePath)}: " +
            $"{error.GetAwaiter().GetResult()}{output.GetAwaiter().GetResult()}");
        return false;
    }

    /// <summary>
    ///     Reads a PE file's COFF header timestamp. Returns null for a DOS-only executable (no PE
    ///     signature) or an unreadable file.
    ///     <para>
    ///         ⚠ The value is not always a date. Morrowind.exe stamps 2030-10-02 and the corpus
    ///         therefore names that build from its release date instead; deterministic builds put a
    ///         content hash in this field. Callers must sanity-check before believing it.
    ///     </para>
    /// </summary>
    internal static DateTime? ReadCoffTimestamp(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40)
            {
                return null;
            }

            stream.Seek(0x3C, SeekOrigin.Begin);
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 8 > stream.Length)
            {
                return null;
            }

            stream.Seek(peOffset, SeekOrigin.Begin);
            if (reader.ReadUInt16() != 0x4550)
            {
                return null;
            }

            stream.Seek(peOffset + 8, SeekOrigin.Begin);
            return DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt32()).UtcDateTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentOutOfRangeException or EndOfStreamException)
        {
            return null;
        }
    }

    private static void NormalizeTreeForDeletion(string target)
    {
        var pending = new Stack<string>();
        pending.Push(target);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"Refusing to delete through a reparse point: {directory}");
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException(
                            $"Refusing to delete a tree containing a reparse point: {entry}");
                    }

                    pending.Push(entry);
                }
                else
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
            }
        }
    }
}
