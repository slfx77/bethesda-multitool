using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     Guards localized display-name resolution: on a localized plugin (Skyrim/FO4/FO76) a FULL/RNAM holds a
///     4-byte string-table index, so display names must come from the table — never from rendering the index
///     bytes as Windows-1252 text. Two regressions produced that mojibake: a raw FULL pre-scan that won the
///     first-write TryAdd race (base records), and INFO RNAM (dialogue prompt) read raw then surfaced as the
///     topic display name (DIAL). This asserts almost no display name is a raw index. Skipped when the real
///     game plugin is absent (env-gated, like the parity tests).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class LocalizedBaseNameResolutionTests
{
    [Theory]
    [InlineData(@"E:\SteamLibrary\SteamApps\common\Skyrim\Data\Skyrim.esm")]
    [InlineData(@"E:\SteamLibrary\SteamApps\common\Fallout 4\Data\Fallout4.esm")]
    public async Task LocalizedDisplayNames_ResolveCleanly(string esm)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(esm), $"Localized plugin not found: {esm}");

        var result = await RealAssetEsmCache.LoadAsync(
            esm, TestContext.Current.CancellationToken);

        var total = 0;
        var rawIndex = 0;
        foreach (var (_, name) in result.Records.FormIdToDisplayName)
        {
            total++;
            if (LooksLikeRawStringIndex(name))
            {
                rawIndex++;
            }
        }

        Assert.True(total > 1000, $"Expected many display names; got {total}.");
        var rawFraction = (double)rawIndex / total;
        Assert.True(rawFraction < 0.005,
            $"{Path.GetFileName(esm)}: {rawFraction:P2} of {total} display names ({rawIndex}) look like raw " +
            $".STRINGS indices rendered as bytes — localized resolution regressed.");
    }

    // A resolved string is real prose (accented chars like é/å and spaces are fine). A raw 4-byte index
    // rendered as text is short, carries a high-bit/control byte, and has no spaces — that's the signature we
    // reject without flagging legitimate non-ASCII names.
    private static bool LooksLikeRawStringIndex(string? s)
    {
        return !string.IsNullOrEmpty(s) && s.Length <= 4 && !s.Contains(' ') && s.Any(c => c < 0x20 || c >= 0x7F);
    }
}