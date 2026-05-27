using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Scans a NIF binary for embedded texture-path references and returns the set of
///     canonical asset paths (e.g. <c>textures\armor\ulysses\uhair_d.dds</c>) found.
///     NIF stores texture paths inside <c>BSShaderTextureSet</c> / <c>NiSourceTexture</c>
///     blocks as <c>SizedString</c> (uint32 length + ASCII bytes, no null terminator). The
///     DMP raw-byte scanner requires null termination to recognize a candidate, so these
///     embedded paths slip past it — the engine then fails to find the textures at runtime
///     and renders whatever happens to be in the texture slot ("cycling memory garbage" /
///     visible gore caps lit by stale texture data).
///     This collector closes that gap. Endian-agnostic: only the ASCII payload matters,
///     so the same scanner works on Xbox 360 BE-NIF and PC LE-NIF inputs alike.
/// </summary>
internal static class NifEmbeddedAssetCollector
{
    private const int MinPathLength = 6;
    private const int MaxPathLength = 260;

    /// <summary>
    ///     Walk the bytes of one NIF looking for printable-ASCII runs and emit, for each
    ///     asset-extension boundary inside a run, a candidate path ending at that boundary.
    ///     Splitting on every <c>.dds</c>/<c>.ddx</c>/<c>.nif</c>/etc. inside a run is what
    ///     keeps adjacent SizedStrings from fusing into a single fake path when the LE
    ///     length-prefix between them happens to be a printable byte (lengths in
    ///     <c>[97..122]</c> or <c>[65..90]</c> all hit ASCII letters).
    /// </summary>
    public static HashSet<string> ScanBytes(ReadOnlySpan<byte> nifBytes)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < nifBytes.Length)
        {
            while (i < nifBytes.Length && !IsPrintablePathByte(nifBytes[i]))
            {
                i++;
            }

            if (i >= nifBytes.Length)
            {
                break;
            }

            var runStart = i;
            while (i < nifBytes.Length && IsPrintablePathByte(nifBytes[i]))
            {
                i++;
            }

            var runEnd = i;
            EmitCandidatesInRun(nifBytes, runStart, runEnd, paths);
        }

        return paths;
    }

    /// <summary>
    ///     Walks a single printable-ASCII run and emits one candidate per asset-extension
    ///     occurrence. Each candidate is the byte slice from <c>segmentStart</c> (initially
    ///     the run start; advanced past each accepted extension) up to and including the
    ///     extension. Splitting on every extension is what prevents a length-prefix byte
    ///     that happens to be printable from gluing two adjacent paths together.
    /// </summary>
    private static void EmitCandidatesInRun(
        ReadOnlySpan<byte> bytes,
        int runStart,
        int runEnd,
        HashSet<string> paths)
    {
        var segmentStart = runStart;
        var p = runStart;
        while (p < runEnd)
        {
            if (TryMatchExtensionAt(bytes, p, runEnd, out var extEnd))
            {
                EmitCandidate(bytes, segmentStart, extEnd, paths);
                segmentStart = extEnd;
                p = extEnd;
            }
            else
            {
                p++;
            }
        }
    }

    /// <summary>
    ///     If position <paramref name="p" /> starts a known asset extension (a literal
    ///     <c>.</c> followed by 2–3 letters from the asset-extension whitelist), return
    ///     true and the exclusive end position (just past the last letter). Keep this
    ///     whitelist in sync with the texture/mesh extensions a NIF can legitimately
    ///     reference inside a SizedString.
    /// </summary>
    private static bool TryMatchExtensionAt(
        ReadOnlySpan<byte> bytes,
        int p,
        int runEnd,
        out int extEnd)
    {
        extEnd = 0;
        if (p >= runEnd || bytes[p] != (byte)'.')
        {
            return false;
        }

        if (p + 4 <= runEnd)
        {
            var c1 = ToAsciiLower(bytes[p + 1]);
            var c2 = ToAsciiLower(bytes[p + 2]);
            var c3 = ToAsciiLower(bytes[p + 3]);
            if ((c1 == 'd' && c2 == 'd' && (c3 == 's' || c3 == 'x'))
                || (c1 == 'n' && c2 == 'i' && c3 == 'f')
                || (c1 == 't' && c2 == 'r' && c3 == 'i')
                || (c1 == 'e' && c2 == 'g' && (c3 == 'm' || c3 == 't')))
            {
                extEnd = p + 4;
                return true;
            }
        }

        if (p + 3 <= runEnd)
        {
            var c1 = ToAsciiLower(bytes[p + 1]);
            var c2 = ToAsciiLower(bytes[p + 2]);
            if (c1 == 'k' && c2 == 'f')
            {
                extEnd = p + 3;
                return true;
            }
        }

        return false;
    }

    private static void EmitCandidate(
        ReadOnlySpan<byte> bytes,
        int segmentStart,
        int segmentEnd,
        HashSet<string> paths)
    {
        var length = segmentEnd - segmentStart;
        if (length is < MinPathLength or > MaxPathLength)
        {
            return;
        }

        var slice = bytes.Slice(segmentStart, length);
        if (!SegmentLooksLikeAssetPath(slice))
        {
            return;
        }

        var candidate = Encoding.ASCII.GetString(slice);
        var normalized = AssetPathRules.TryNormalizeRequestPath(candidate);
        if (normalized is null)
        {
            return;
        }

        paths.Add(normalized);
    }

    /// <summary>
    ///     Cheap pre-filter applied before paying for UTF-8 decoding + normalization.
    ///     Requires the segment to contain a path separator (so we don't pick up bare
    ///     <c>foo.dds</c> filenames that the normalizer would otherwise infer into
    ///     <c>textures\foo.dds</c>) AND to contain a known Data\ subdirectory prefix
    ///     somewhere (so noise that happens to be a printable letter run plus a
    ///     <c>.dds</c> extension still gets rejected).
    /// </summary>
    private static bool SegmentLooksLikeAssetPath(ReadOnlySpan<byte> segment)
    {
        var hasSeparator = false;
        foreach (var b in segment)
        {
            if (b == (byte)'\\' || b == (byte)'/')
            {
                hasSeparator = true;
                break;
            }
        }

        if (!hasSeparator)
        {
            return false;
        }

        foreach (var prefix in AssetPathRules.DmpScanStrictPrefixes)
        {
            if (ContainsAsciiIgnoreCase(segment, prefix))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Case-insensitive ASCII substring search that folds <c>/</c> to <c>\</c> so the
    ///     prefix list (which uses <c>\</c>) still matches paths written with forward
    ///     slashes — some prototype meshes do this.
    /// </summary>
    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> haystack, string needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                var hb = NormalizePathByte(haystack[i + j]);
                var nb = NormalizePathByte((byte)needle[j]);
                if (hb != nb)
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static byte NormalizePathByte(byte b)
    {
        if (b == (byte)'/')
        {
            return (byte)'\\';
        }

        return ToAsciiLower(b);
    }

    private static byte ToAsciiLower(byte b)
    {
        return b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + ('a' - 'A')) : b;
    }

    /// <summary>
    ///     Conservative path-byte predicate. Allows letters, digits, separators (<c>\</c>
    ///     and <c>/</c>), and the punctuation NIFs use inside SizedStrings (<c>_</c>,
    ///     <c>-</c>, <c>.</c>). Excludes spaces and other punctuation that would let random
    ///     binary noise stitch into a longer fake run.
    /// </summary>
    private static bool IsPrintablePathByte(byte b)
    {
        if (b is >= (byte)'a' and <= (byte)'z')
        {
            return true;
        }

        if (b is >= (byte)'A' and <= (byte)'Z')
        {
            return true;
        }

        if (b is >= (byte)'0' and <= (byte)'9')
        {
            return true;
        }

        return b is (byte)'\\' or (byte)'/' or (byte)'.' or (byte)'_' or (byte)'-';
    }
}
