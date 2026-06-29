using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     Guards the localized base-name fix: on a localized plugin (Skyrim/FO4/FO76) a FULL is a 4-byte
///     .STRINGS index, so the resolver's display names must come from the table — not from raw bytes. A raw
///     pre-scan used to seed FormIdToFullName with the index bytes as text ("1\xAD") and win the first-write
///     TryAdd race, leaving ~half of all base names as mojibake. This asserts the resolved display names are
///     overwhelmingly clean text. Skipped when the real game plugin is absent (env-gated, like the parity
///     tests).
/// </summary>
public class LocalizedBaseNameResolutionTests
{
    [Theory]
    [InlineData(@"E:\SteamLibrary\SteamApps\common\Skyrim\Data\Skyrim.esm")]
    [InlineData(@"E:\SteamLibrary\SteamApps\common\Fallout 4\Data\Fallout4.esm")]
    public async Task LocalizedBaseNames_ResolveCleanly(string esm)
    {
        Assert.SkipUnless(File.Exists(esm), $"Localized plugin not found: {esm}");

        using var result = await SemanticFileLoader.LoadAsync(
            esm, cancellationToken: TestContext.Current.CancellationToken);

        // DIAL topic names flow through a separate dialogue-prompt path; this fix targets base RECORD names,
        // so exclude DIAL from the gate.
        var dialFormIds = new HashSet<uint>(result.Records.DialogTopics.Select(t => t.FormId));

        var clean = 0;
        var mojibake = 0;
        foreach (var (formId, name) in result.Records.FormIdToDisplayName)
        {
            if (dialFormIds.Contains(formId))
            {
                continue;
            }

            // A table-resolved name is printable text; a raw .STRINGS index rendered as bytes carries control
            // or high-bit characters.
            if (string.IsNullOrEmpty(name) || name.Any(c => c < 0x20 || c >= 0x7F))
            {
                mojibake++;
            }
            else
            {
                clean++;
            }
        }

        var total = clean + mojibake;
        Assert.True(total > 1000, $"Expected many base names; got {total}.");
        var cleanFraction = (double)clean / total;
        Assert.True(cleanFraction > 0.95,
            $"{Path.GetFileName(esm)}: only {cleanFraction:P1} of {total} base names resolved cleanly " +
            $"({mojibake} mojibake) — the localized .STRINGS resolution regressed.");
    }
}
