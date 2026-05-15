using System.Text.RegularExpressions;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     EditorID stem normalizer for the REFR base-FormID rename-remap fallback. When a
///     prototype REFR's base FormID isn't in the master ESM and isn't being freshly
///     emitted, we attempt to find a master record whose EditorID has the same stem —
///     covering the common "renamed during FNV production" case (e.g.
///     <c>SCOLParkingLotChunk03</c> → master <c>SCOLParkingLotChunk03b</c>).
///     <para>
///         Conservative first cut (per user direction): strip only a single trailing
///         disambiguation letter that follows a digit (<c>(?&lt;=[0-9])[a-z]$</c>) AND the
///         Fallout-3-to-New-Vegas rename suffix <c>nv</c>/<c>_nv</c>. Wider patterns
///         (<c>new</c>, <c>old</c>, <c>alt</c>, <c>temp</c>, <c>test</c>, <c>v\d+</c>)
///         stay out until census evidence shows misses caused by them.
///         The chunk-number itself is intentionally preserved — the empirical FNV rename
///         pattern is "append a disambiguation letter" (e.g. <c>SCOLParkingLotChunk05</c>
///         → master <c>SCOLParkingLotChunk05b</c>), not "renumber". Stripping the digits
///         collapses prototypes onto the wrong master variant (every
///         <c>SCOLParkingLotChunk0N</c> would tie with every <c>0Mb</c>) and the ambiguity
///         gate refuses the remap.
///     </para>
/// </summary>
public static partial class EditorIdStem
{
    [GeneratedRegex(@"(?:_?nv|(?<=[0-9])[a-z])$",
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
