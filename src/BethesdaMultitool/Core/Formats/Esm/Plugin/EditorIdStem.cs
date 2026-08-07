using System.Text.RegularExpressions;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

/// <summary>
///     EditorID stem normalizer for the REFR base-FormID rename-remap fallback. When a
///     prototype REFR's base FormID isn't in the master ESM and isn't being freshly
///     emitted, we attempt to find a master record whose EditorID has the same stem —
///     covering the common "renamed during FNV production" case (e.g.
///     <c>SCOLParkingLotChunk03</c> → master <c>SCOLParkingLotChunk03b</c>).
///     <para>
///         Conservative by design: strip only a single trailing
///         disambiguation letter that follows a digit (<c>(?&lt;=[0-9])[a-z]$</c>), the
///         Fallout-3-to-New-Vegas rename suffix <c>nv</c>/<c>_nv</c>, and the proto-era
///         <c>Trimmed</c> suffix. Wider patterns
///         (<c>new</c>, <c>old</c>, <c>alt</c>, <c>temp</c>, <c>test</c>, <c>v\d+</c>)
///         stay out until census evidence shows misses caused by them.
///         The chunk-number itself is intentionally preserved — the empirical FNV rename
///         pattern is "append a disambiguation letter" (e.g. <c>SCOLParkingLotChunk05</c>
///         → master <c>SCOLParkingLotChunk05b</c>), not "renumber". Stripping the digits
///         collapses prototypes onto the wrong master variant (every
///         <c>SCOLParkingLotChunk0N</c> would tie with every <c>0Mb</c>) and the ambiguity
///         gate refuses the remap.
///     </para>
///     <para>
///         <c>Trimmed</c> was admitted 2026-08-05 on exactly the census evidence the doc above
///         asks for: proto-only SCOLs <c>SWDirtMidTrimmed</c> / <c>SWDirtEnd01Trimmed</c> /
///         <c>SWDirtEnd02Trimmed</c> (Feb-2010 Strip sidewalks) whose retail counterparts are
///         the un-suffixed <c>SWDirtMid</c>-class STATs — 24 placed refs dropped as
///         dangling-base solely because the stem could not shed the suffix. It is the
///         FALLBACK lane behind runtime SCOL ingestion (USER RULING: A with B fallback).
///     </para>
/// </summary>
public static partial class EditorIdStem
{
    [GeneratedRegex(@"(?:_?nv|trimmed|(?<=[0-9])[a-z])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffixRegex();

    /// <summary>
    ///     Returns the lowercase stem of <paramref name="editorId" /> with one trailing
    ///     version/rename suffix removed. Returns null for null/empty/whitespace input
    ///     and for inputs whose entire content matches the suffix (stem would be empty).
    ///     Idempotent: stripping once is intentional — calling Normalize on a previously-
    ///     normalized result is a no-op for inputs without further trailing suffixes.
    /// </summary>
    public static string? Normalize(string? editorId)
    {
        if (string.IsNullOrWhiteSpace(editorId))
        {
            return null;
        }

        var lower = editorId.ToLowerInvariant();
        var stripped = VersionSuffixRegex().Replace(lower, string.Empty);

        return string.IsNullOrEmpty(stripped) ? null : stripped;
    }
}
