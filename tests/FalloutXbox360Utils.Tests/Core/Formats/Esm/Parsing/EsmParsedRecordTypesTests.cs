using System.Reflection;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Guards <see cref="EsmParsedRecordTypes" /> — the single source of truth for which record types the
///     semantic parser counts as "parsed" (everything else surfaces as "Other (not parsed)" in the UI).
///     The completeness test is the anti-drift teeth: a new typed collection on <see cref="RecordCollection" />
///     that nobody registers fails CI instead of silently mislabeling its record type as unparsed.
/// </summary>
public class EsmParsedRecordTypesTests
{
    [Fact]
    public void Codes_AreUnique()
    {
        var dupes = EsmParsedRecordTypes.All
            .GroupBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(dupes.Count == 0, $"Duplicate parsed-type codes: {string.Join(", ", dupes)}");
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
