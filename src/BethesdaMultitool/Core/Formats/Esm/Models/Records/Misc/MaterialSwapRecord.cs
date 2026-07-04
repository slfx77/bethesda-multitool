using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Material Swap (MSWP) record — FO4/FO76 only. Re-skins a shared NIF by substituting whole
///     <c>.bgsm</c> materials: each BNAM (original material path) → SNAM (replacement path) pair tells
///     the engine "wherever this mesh references BNAM, load SNAM instead" (colorway skins, per-vendor
///     billboard ads). Referenced from a placement via the REFR <c>XMSP</c> FormID.
/// </summary>
public record MaterialSwapRecord
{
    /// <summary>FormID of the material swap record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     Original material path → replacement material path. Both sides are normalized via
    ///     <see cref="NormalizeMaterialPath" /> so a lookup keyed on a NIF's shader material path
    ///     (normalized the same way at decode time) hits regardless of the mixed casing / missing
    ///     <c>materials\</c> prefix the raw subrecords ship with.
    /// </summary>
    public IReadOnlyDictionary<string, string> Swaps { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Offset in the file where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>
    ///     Canonicalizes an MSWP material path into the exact form the decode-time consumer
    ///     (<c>NifGeometryExtractor</c>) produces when it normalizes a NIF's shader material path with
    ///     <see cref="NifTexturePathUtility.Normalize" />. MSWP entries ship materials-relative WITHOUT
    ///     the <c>materials\</c> prefix and in mixed case (e.g.
    ///     <c>Architecture\Buildings\HighTech\HitTechMetalPanel_01.BGSM</c>), while Normalize prefixes
    ///     any non-<c>textures\</c>/<c>materials\</c> path with <c>textures\</c> — so the prefix must be
    ///     added BEFORE normalizing or the two sides would never agree and every swap lookup would miss.
    /// </summary>
    public static string NormalizeMaterialPath(string path)
    {
        var slashed = path.Replace('/', '\\').Trim();
        if (!slashed.StartsWith("materials\\", StringComparison.OrdinalIgnoreCase))
        {
            slashed = "materials\\" + slashed;
        }

        return NifTexturePathUtility.Normalize(slashed);
    }
}
