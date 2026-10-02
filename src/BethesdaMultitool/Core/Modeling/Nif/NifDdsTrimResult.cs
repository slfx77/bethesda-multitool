using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>The outcome of <see cref="NifDdsMipTrim.Trim" />.</summary>
/// <param name="Bytes">The DDS after trimming (the input itself when nothing was trimmed).</param>
/// <param name="StoredLevels">The level count the relayout wrote.</param>
/// <param name="Levels">The level count after trimming.</param>
/// <param name="TrimmedLevels">Fabricated all-zero levels removed.</param>
/// <param name="TrimmedBytes">Bytes removed.</param>
/// <param name="UndeclaredNonZeroLevels">Levels (or trailing data) beyond the declaration that hold non-zero bytes.</param>
/// <param name="Note">Why the extra levels could not be examined, or null.</param>
internal sealed record NifDdsTrimResult(
    byte[] Bytes,
    int StoredLevels,
    int Levels,
    int TrimmedLevels,
    long TrimmedBytes,
    int UndeclaredNonZeroLevels,
    string? Note)
{
    /// <summary>The trim facts for native state and derivation details.</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["storedLevels"] = StoredLevels,
            ["levels"] = Levels,
            ["trimmedLevels"] = TrimmedLevels,
            ["trimmedBytes"] = TrimmedBytes,
            ["undeclaredNonZeroLevels"] = UndeclaredNonZeroLevels,
            ["note"] = Note
        };
    }
}
