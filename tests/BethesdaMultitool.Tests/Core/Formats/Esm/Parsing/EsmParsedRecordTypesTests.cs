using System.Reflection;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Guards <see cref="EsmParsedRecordTypes" /> — the single source of truth for which record types the
///     semantic parser counts as "parsed" (everything else surfaces as "Other (not parsed)" in the UI).
///     The completeness test is the anti-drift teeth: a new typed collection on <see cref="RecordCollection" />
///     that nobody registers fails CI instead of silently mislabeling its record type as unparsed.
/// </summary>
public class EsmParsedRecordTypesTests
{
    [Fact]
    public void Mappings_DoNotOverlapGloballyOrWithinAGame()
    {
        var ambiguous = EsmParsedRecordTypes.All
            .GroupBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Where(group => group.Any(entry => entry.Game is null) ||
                            group.GroupBy(entry => entry.Game).Any(gameGroup => gameGroup.Count() > 1))
            .Select(group => group.Key)
            .ToList();

        Assert.True(ambiguous.Count == 0,
            $"Overlapping parsed-type mappings: {string.Join(", ", ambiguous)}");
    }

    [Fact]
    public void Codes_AreFourCharSignatures()
    {
        var bad = EsmParsedRecordTypes.All.Where(e => e.Code.Length != 4).Select(e => e.Code).ToList();
        Assert.True(bad.Count == 0, $"Non-4-char codes: {string.Join(", ", bad)}");
    }

    [Fact]
    public void Codes_AreKnownRecordTypes()
    {
        var unknown = EsmParsedRecordTypes.All
            .Select(e => e.Code)
            .Where(code => !EsmRecordTypes.MainRecordTypes.ContainsKey(code))
            .ToList();

        Assert.True(unknown.Count == 0,
            $"Codes not present in EsmRecordTypes.MainRecordTypes (typo?): {string.Join(", ", unknown)}");
    }

    [Fact]
    public void Codes_IncludeFormerlyMislabeledTypes()
    {
        // Regression: these all have working parse handlers + RecordCollection lists, but were missing from
        // the old hand-maintained whitelist, so the GUI summary showed them under "Other (not parsed)".
        foreach (var code in new[] { "LVLI", "LVLN", "LVLC", "HAIR", "EYES", "RCCT", "COBJ" })
        {
            Assert.True(EsmParsedRecordTypes.Codes.Contains(code), $"{code} should be registered as parsed");
        }
    }

    [Fact]
    public void EntriesForGame_SeparatesGameScopedCe2EnvironmentRecords()
    {
        var starfield = EsmParsedRecordTypes.CodesForGame(BethesdaGame.Starfield);
        Assert.Contains("WTHS", starfield);
        Assert.Contains("VOLI", starfield);
        Assert.Contains("CLDF", starfield);
        Assert.Contains("ATMO", starfield);
        Assert.Contains("PNDT", starfield);
        Assert.Contains("STDT", starfield);
        Assert.Contains("SUNP", starfield);
        Assert.Contains("CUR3", starfield);

        var fallout76 = EsmParsedRecordTypes.CodesForGame(BethesdaGame.Fallout76);
        Assert.DoesNotContain("WTHS", fallout76);
        Assert.Contains("VOLI", fallout76);
        Assert.DoesNotContain("CLDF", fallout76);
        Assert.DoesNotContain("ATMO", fallout76);
        Assert.DoesNotContain("PNDT", fallout76);
        Assert.DoesNotContain("STDT", fallout76);
        Assert.DoesNotContain("SUNP", fallout76);
        Assert.DoesNotContain("CUR3", fallout76);
        Assert.Contains("WTHR", fallout76);
        var fallout4 = EsmParsedRecordTypes.CodesForGame(BethesdaGame.Fallout4);
        Assert.DoesNotContain("ATMO", fallout4);
        Assert.DoesNotContain("PNDT", fallout4);
        Assert.DoesNotContain("STDT", fallout4);
        Assert.DoesNotContain("SUNP", fallout4);
        Assert.DoesNotContain("CUR3", fallout4);

        Assert.Equal(nameof(RecordCollection.VolumetricLightingSettings),
            Assert.Single(EsmParsedRecordTypes.EntriesForGame(BethesdaGame.Starfield)
                .Where(entry => entry.Code == "VOLI")).Collection);
        Assert.Equal(nameof(RecordCollection.Fallout76VolumetricLightingSettings),
            Assert.Single(EsmParsedRecordTypes.EntriesForGame(BethesdaGame.Fallout76)
                .Where(entry => entry.Code == "VOLI")).Collection);
        Assert.Equal(nameof(RecordCollection.Atmospheres),
            Assert.Single(EsmParsedRecordTypes.EntriesForGame(BethesdaGame.Starfield)
                .Where(entry => entry.Code == "ATMO")).Collection);
        Assert.Equal(nameof(RecordCollection.PlanetData),
            Assert.Single(EsmParsedRecordTypes.EntriesForGame(BethesdaGame.Starfield)
                .Where(entry => entry.Code == "PNDT")).Collection);
        Assert.Equal(nameof(RecordCollection.StarData),
            Assert.Single(EsmParsedRecordTypes.EntriesForGame(BethesdaGame.Starfield)
                .Where(entry => entry.Code == "STDT")).Collection);
        Assert.Equal(nameof(RecordCollection.SunPresets),
            Assert.Single(EsmParsedRecordTypes.EntriesForGame(BethesdaGame.Starfield)
                .Where(entry => entry.Code == "SUNP")).Collection);
        Assert.Equal(nameof(RecordCollection.Curves3D),
            Assert.Single(EsmParsedRecordTypes.EntriesForGame(BethesdaGame.Starfield)
                .Where(entry => entry.Code == "CUR3")).Collection);
    }

    [Fact]
    public void EveryTypedCollection_IsRegistered()
    {
        var registered = EsmParsedRecordTypes.All
            .Where(e => e.Collection != null)
            .Select(e => e.Collection!)
            .ToHashSet(StringComparer.Ordinal);

        // Every public List<TRecord> on RecordCollection is a typed parse output and must be claimed by a
        // registry entry. Element types not ending in "Record" (e.g. List<PlacedReference> MapMarkers, which
        // is derived from REFR placed refs) are intentionally excluded.
        var recordListProperties = typeof(RecordCollection)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>)
                        && p.PropertyType.GetGenericArguments()[0].Name
                            .EndsWith("Record", StringComparison.Ordinal))
            .Select(p => p.Name)
            .ToList();

        var unregistered = recordListProperties.Where(name => !registered.Contains(name)).ToList();

        Assert.True(unregistered.Count == 0,
            "RecordCollection list(s) with no EsmParsedRecordTypes entry (their record type would be " +
            $"mislabeled as 'not parsed'): {string.Join(", ", unregistered)}");
    }
}
