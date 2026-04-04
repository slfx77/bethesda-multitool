using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Pdb;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

/// <summary>
///     String extraction and classification from runtime memory buffers.
/// </summary>
internal sealed class RuntimeBufferStringExtractor
{
    internal const int MinStringLength = 4;
    internal const int MaxStringLength = 512;

    private static readonly HashSet<string> KnownFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nif", ".dds", ".ddx", ".kf", ".wav", ".lip", ".psc", ".txt",
        ".esm", ".esp", ".bsa", ".xml", ".ini", ".fuz", ".xwm", ".bik",
        ".mp3", ".ogg", ".xur", ".xui", ".scda"
    };

    private static readonly HashSet<string> GameAssetPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "meshes", "textures", "sound", "interface", "menus", "scripts",
        "shaders", "music", "video", "strings", "grass", "trees",
        "landscape", "actors", "characters", "creatures", "effects",
        "clutter", "architecture", "weapons", "armor", "lodsettings",
        "data", "bsa", "esm", "esp"
    };

    private readonly BufferAnalysisContext _ctx;

    public RuntimeBufferStringExtractor(BufferAnalysisContext ctx)
    {
        _ctx = ctx;
    }

    #region String Extraction

    /// <summary>
    ///     Extract strings from a buffer and categorize them.
    /// </summary>
    internal static void ExtractStringsFromBuffer(
        byte[] buffer,
        long baseFileOffset,
        long? baseVirtualAddress,
        GapClassification gapClassification,
        List<RuntimeStringHit> hits,
        HashSet<string> uniqueStrings,
        HashSet<string> filePaths,
        HashSet<string> editorIds,
        HashSet<string> dialogue,
        HashSet<string> settings,
        StringPoolSummary summary)
    {
        var start = -1;

        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] >= 0x20 && buffer[i] <= 0x7E)
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else
            {
                if (start >= 0 && buffer[i] == 0)
                {
                    var len = i - start;
                    if (len >= MinStringLength && len <= MaxStringLength)
                    {
                        summary.TotalStrings++;
                        var str = Encoding.ASCII.GetString(buffer, start, len);
                        var category = CategorizeString(str);
                        hits.Add(new RuntimeStringHit
                        {
                            Text = str,
                            Category = category,
                            GapClassification = gapClassification,
                            FileOffset = baseFileOffset + start,
                            VirtualAddress = baseVirtualAddress.HasValue ? baseVirtualAddress.Value + start : null,
                            Length = len
                        });

                        if (uniqueStrings.Add(str))
                        {
                            switch (category)
                            {
                                case StringCategory.FilePath:
                                    filePaths.Add(str);
                                    break;
                                case StringCategory.EditorId:
                                    editorIds.Add(str);
                                    break;
                                case StringCategory.DialogueLine:
                                    dialogue.Add(str);
                                    break;
                                case StringCategory.GameSetting:
                                    settings.Add(str);
                                    break;
                            }
                        }
                    }
                }

                start = -1;
            }
        }
    }

    /// <summary>
    ///     Try to read a null-terminated C string at the given virtual address.
    /// </summary>
    internal string? TryReadCString(uint va, int maxLen = 256)
    {
        return TryReadCStringInfo(va, maxLen)?.Text;
    }

    /// <summary>
    ///     Try to read a null-terminated C string at the given virtual address and retain its location.
    /// </summary>
    internal RuntimeDecodedString? TryReadCStringInfo(uint va, int maxLen = 256)
    {
        if (va == 0)
        {
            return null;
        }

        var fileOffset = _ctx.VaToFileOffset(va);
        if (fileOffset == null)
        {
            return null;
        }

        var readLen = (int)Math.Min(maxLen, _ctx.FileSize - fileOffset.Value);
        if (readLen < MinStringLength)
        {
            return null;
        }

        var buf = new byte[readLen];
        _ctx.Accessor.ReadArray(fileOffset.Value, buf, 0, readLen);

        var end = 0;
        while (end < readLen && buf[end] != 0)
        {
            end++;
        }

        if (end < MinStringLength || end >= readLen)
        {
            return null;
        }

        for (var i = 0; i < end; i++)
        {
            if (buf[i] < 0x20 || buf[i] > 0x7E)
            {
                return null;
            }
        }

        var text = Encoding.ASCII.GetString(buf, 0, end);
        return new RuntimeDecodedString(text, fileOffset.Value, va, end, CategorizeString(text));
    }

    /// <summary>
    ///     Try to read either a direct C string pointer or a BSStringT wrapper.
    /// </summary>
    internal RuntimeDecodedString? TryReadStringAtPointerInfo(uint va)
    {
        var str = TryReadCStringInfo(va);
        if (str != null)
        {
            return str;
        }

        var fileOffset = _ctx.VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + 8 > _ctx.FileSize)
        {
            return null;
        }

        var buf = new byte[4];
        _ctx.Accessor.ReadArray(fileOffset.Value, buf, 0, 4);
        var innerPtr = BinaryPrimitives.ReadUInt32BigEndian(buf);
        return innerPtr != 0 && _ctx.IsValidPointer(innerPtr)
            ? TryReadCStringInfo(innerPtr)
            : null;
    }

    /// <summary>
    ///     Walk a global pointer that might point to a string or BSStringT.
    /// </summary>
    internal void WalkStringAtPointer(BufferExplorationResult result, ResolvedGlobal global)
    {
        var str = TryReadStringAtPointerInfo(global.PointerValue);
        if (str == null)
        {
            return;
        }

        var walkResult = new ManagerWalkResult
        {
            GlobalName = global.Global.Name,
            PointerValue = global.PointerValue,
            TargetType = "BSStringT",
            WalkableEntries = 1,
            Summary = $"\"{str.Text}\""
        };
        walkResult.ExtractedStrings.Add(str.Text);
        walkResult.OwnedStringClaims.Add(new RuntimeStringOwnershipClaim(
            str.FileOffset,
            str.VirtualAddress,
            "ManagerGlobal",
            global.Global.Name,
            null,
            global.FileOffset));
        result.ManagerResults.Add(walkResult);
    }

    #endregion

    #region String Classification

    /// <summary>
    ///     Categorize a string based on its content patterns.
    /// </summary>
    private static StringCategory CategorizeString(string s)
    {
        // File path detection - require strong evidence
        if (IsLikelyFilePath(s))
        {
            return StringCategory.FilePath;
        }

        // Analyze character composition once for remaining checks
        var hasUnderscore = false;
        var hasSpace = false;
        var allAlphaNumOrUnderscore = true;
        var upperCount = 0;
        var lowerCount = 0;

        foreach (var c in s)
        {
            if (c == '_')
            {
                hasUnderscore = true;
            }
            else if (c == ' ')
            {
                hasSpace = true;
                allAlphaNumOrUnderscore = false;
            }
            else if (char.IsUpper(c))
            {
                upperCount++;
            }
            else if (char.IsLower(c))
            {
                lowerCount++;
            }
            else if (!char.IsDigit(c))
            {
                allAlphaNumOrUnderscore = false;
            }
        }

        // Game setting: fXxx/iXxx/bXxx/sXxx/uXxx, all alphanumeric, CamelCase, 8+ chars
        if (s.Length >= 8 && !hasUnderscore && !hasSpace && allAlphaNumOrUnderscore &&
            s[0] is 'f' or 'i' or 'b' or 'u' && char.IsUpper(s[1]) && char.IsLower(s[2]) &&
            upperCount >= 2 && lowerCount >= 4)
        {
            return StringCategory.GameSetting;
        }

        // EditorID: alphanumeric + underscore, starts with uppercase, CamelCase-like, 6+ chars
        // Require character diversity to filter repeating binary noise (e.g. "katSkatSkatS")
        if (s.Length >= 6 && !hasSpace && allAlphaNumOrUnderscore &&
            char.IsUpper(s[0]) && lowerCount >= 2 && upperCount >= 1)
        {
            var distinctChars = CountDistinctChars(s);
            var minDistinct = Math.Max(4, s.Length / 5);
            if (distinctChars >= minDistinct)
            {
                return StringCategory.EditorId;
            }
        }

        // Dialogue: natural language - has spaces, 25+ chars, starts with letter,
        // mostly lowercase, no technical patterns
        if (hasSpace && s.Length >= 25 && char.IsLetter(s[0]) &&
            lowerCount > upperCount * 2 && !IsTechnicalString(s))
        {
            return StringCategory.DialogueLine;
        }

        return StringCategory.Other;
    }

    /// <summary>
    ///     Check if a string looks like a file path.
    /// </summary>
    private static bool IsLikelyFilePath(string s)
    {
        // First character must be alphanumeric or underscore (filter stray binary bytes)
        if (s.Length < 6 || (!char.IsLetterOrDigit(s[0]) && s[0] != '_'))
        {
            return false;
        }

        // Check for known file extensions (strong signal)
        var dotIndex = s.LastIndexOf('.');
        if (dotIndex >= 1 && dotIndex < s.Length - 1)
        {
            var ext = s[dotIndex..];
            if (KnownFileExtensions.Contains(ext))
            {
                return true;
            }
        }

        // Path separators - require additional evidence to avoid false positives
        var separatorCount = s.Count(c => c is '\\' or '/');

        if (separatorCount == 0)
        {
            return false;
        }

        // Require 2+ separators for strings without known directory prefix
        if (separatorCount >= 2 && s.Length >= 10)
        {
            return true;
        }

        // Single separator: check if first component is a known game directory
        var sepIndex = s.IndexOfAny(['\\', '/']);
        if (sepIndex >= 3)
        {
            var firstDir = s[..sepIndex];
            if (GameAssetPrefixes.Contains(firstDir))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Check if a string contains technical/debug patterns.
    /// </summary>
    private static bool IsTechnicalString(string s)
    {
        // Filter out debug/technical strings that aren't player-visible dialogue
        return s.Contains("LOD", StringComparison.Ordinal) ||
               (s.Contains("Level ", StringComparison.Ordinal) && s.Contains("Cells", StringComparison.Ordinal)) ||
               s.Contains("MULTIBOUND", StringComparison.Ordinal) ||
               s.Contains("0x", StringComparison.Ordinal) ||
               s.Contains("NULL", StringComparison.Ordinal) ||
               s.Contains("WARNING", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("ASSERT", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("DEBUG", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Count distinct characters in a string (for noise filtering).
    /// </summary>
    private static int CountDistinctChars(string s)
    {
        Span<bool> seen = stackalloc bool[128];
        var count = 0;
        foreach (var c in s)
        {
            if (c < 128 && !seen[c])
            {
                seen[c] = true;
                count++;
            }
        }

        return count;
    }

    #endregion
}
