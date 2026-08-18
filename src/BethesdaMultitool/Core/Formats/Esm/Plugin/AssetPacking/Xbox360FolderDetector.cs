using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Sniffs a data folder to guess whether it holds Xbox 360 assets. Tries any
///     <c>.esm</c>/<c>.esp</c> first (cheap big-endian magic check via
///     <see cref="EsmParser.IsBigEndian" />), then falls back to any <c>.bsa</c> header
///     flag (<see cref="BsaHeader.IsXbox360" />). Default false (PC) when neither is
///     present or none are readable.
///     The probe descends a few levels because a donor is often handed to us as the
///     extracted disc/title root rather than the <c>Data</c> folder itself (e.g.
///     <c>…\Fallout New Vegas (July 21, 2010)\FalloutNV\Data\</c>). Looking only at the
///     top level classified such a donor as PC, and every LOOSE asset under it then packed
///     without conversion — <see cref="DataFolderIndex" /> falls back to this folder-level
///     hint for loose files, while BSA entries carry their own per-archive flag.
/// </summary>
public static class Xbox360FolderDetector
{
    /// <summary>
    ///     How many directory levels below the supplied folder to probe. A title root nests
    ///     its Data folder one or two levels down; beyond that we would start walking the
    ///     asset tree itself, which is large and never holds an ESM or BSA.
    /// </summary>
    private const int MaxProbeDepth = 3;

    /// <summary>
    ///     Upper bound on directories examined, so a pathological tree cannot turn a hint
    ///     into a long walk. Real donors resolve within the first handful.
    /// </summary>
    private const int MaxProbedDirectories = 256;

    /// <summary>
    ///     Return true when the folder appears to contain Xbox 360 format assets.
    ///     The result is a best-effort hint; callers should expose it to the user with
    ///     the ability to override.
    /// </summary>
    public static bool DetectIsXbox360Format(string folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
        {
            return false;
        }

        foreach (var candidate in EnumerateProbeDirectories(folderPath))
        {
            if (HasBigEndianEsm(candidate) || HasXbox360Bsa(candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Breadth-first directory walk from <paramref name="root" />, shallowest first, so
    ///     the common case (the caller already passed the Data folder) costs one probe.
    /// </summary>
    private static IEnumerable<string> EnumerateProbeDirectories(string root)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var probed = 0;

        while (queue.Count > 0 && probed < MaxProbedDirectories)
        {
            var (path, depth) = queue.Dequeue();
            probed++;
            yield return path;

            if (depth >= MaxProbeDepth)
            {
                continue;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(path);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var child in children)
            {
                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static bool HasBigEndianEsm(string folderPath)
    {
        Span<byte> head = stackalloc byte[4];

        foreach (var pattern in new[] { "*.esm", "*.esp" })
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folderPath, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    if (stream.Read(head) < 4)
                    {
                        continue;
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (EsmParser.IsBigEndian(head))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasXbox360Bsa(string folderPath)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folderPath, "*.bsa", SearchOption.TopDirectoryOnly);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }

        foreach (var file in files)
        {
            // Header-only probe: the Xbox 360 bit lives in the archive flags, so a full
            // folder/file-table parse per BSA is unnecessary. (TryReadHeader
            // still full-parses Morrowind-format BSAs — their counts live in the body — but
            // those are never Xbox 360.) Invalid/locked files return null and are skipped.
            if (BsaParser.TryReadHeader(file) is { IsXbox360: true })
            {
                return true;
            }
        }

        return false;
    }
}

