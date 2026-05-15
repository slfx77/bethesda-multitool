using FalloutXbox360Utils.Core.Formats.Bsa;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Sniffs a data folder to guess whether it holds Xbox 360 assets. Tries any
///     <c>.esm</c>/<c>.esp</c> first (cheap big-endian magic check via
///     <see cref="EsmParser.IsBigEndian"/>), then falls back to any <c>.bsa</c> header
///     flag (<see cref="BsaHeader.IsXbox360"/>). Default false (PC) when neither is
///     present or none are readable.
/// </summary>
public static class Xbox360FolderDetector
{
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

        if (HasBigEndianEsm(folderPath))
        {
            return true;
        }

        if (HasXbox360Bsa(folderPath))
        {
            return true;
        }

        return false;
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
            try
            {
                var archive = BsaParser.Parse(file);
                if (archive.Header.IsXbox360)
                {
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                // Not a valid BSA; skip and try the next one.
            }
            catch (IOException)
            {
                // File locked / unreadable; skip.
            }
        }

        return false;
    }
}
