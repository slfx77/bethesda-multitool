using System.Text.Json;
using BethesdaMultitool.Tests.Helpers;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>The checked-in frozen floors, ceilings and lists of the production parity corpus gate.</summary>
/// <remarks>
///     The file starts with no strata. With no entry for a stratum, only the unconditional rules apply (zero
///     comparison failures and zero shared-validation faults). The root session freezes a stratum by copying the
///     values it wants from the receipt the gate writes under <c>TestOutput/nif-glb-parity/</c>.
/// </remarks>
internal sealed class NifGlbParityRatchet
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>The ratchet format version; currently 1.</summary>
    public int Version { get; set; }

    /// <summary>Frozen entries by stratum identifier.</summary>
    public Dictionary<string, NifGlbParityRatchetEntry> Strata { get; set; } = [];

    /// <summary>The checked-in ratchet file.</summary>
    internal static string FilePath => Path.Combine(SourceContract.RepoRoot, "tests", "BethesdaMultitool.Tests",
        "Core", "Formats", "Nif", "Rendering", "Export", "NifGlbParityRatchet.json");

    /// <summary>Reads the checked-in ratchet.</summary>
    /// <returns>The parsed ratchet.</returns>
    /// <exception cref="InvalidDataException">The file is not a version-1 ratchet.</exception>
    internal static NifGlbParityRatchet Load()
    {
        var ratchet = JsonSerializer.Deserialize<NifGlbParityRatchet>(File.ReadAllText(FilePath), Options) ??
                      throw new InvalidDataException($"{FilePath} is empty.");
        if (ratchet.Version != 1)
        {
            throw new InvalidDataException($"{FilePath} declares version {ratchet.Version}; version 1 is expected.");
        }

        return ratchet;
    }

    /// <summary>The frozen entry for a stratum, or null when that stratum is not frozen yet.</summary>
    /// <param name="stratumId">The stratum identifier.</param>
    /// <returns>The entry, or null.</returns>
    internal NifGlbParityRatchetEntry? For(string stratumId) => Strata.GetValueOrDefault(stratumId);
}
