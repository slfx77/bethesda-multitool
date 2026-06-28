namespace BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;

/// <summary>
///     Registry of per-record-type <see cref="IRecordProfile" />s. The unified display surfaces (GUI
///     <c>EsmBrowserTreeBuilder.BuildProperties</c>, CLI <c>ProfileShowRenderer</c>) look a record's type up
///     here; a hit means the curated, profile-driven model is used instead of the generic flat field tree.
///     Grows one entry at a time alongside <c>SchemaTreeEnricher.ProfiledTypes</c> (the enricher decides
///     which typed-primary records get a tree; this decides which get curated display) as the rollout
///     proceeds. Today: NPC_.
/// </summary>
internal static class RecordProfiles
{
    private static readonly Dictionary<string, IRecordProfile> ByType =
        new IRecordProfile[]
            {
                new NpcProfile(), new ArmorProfile(), new WeaponProfile(), new CreatureProfile(),
                new QuestProfile(), new PackageProfile()
            }
            .ToDictionary(p => p.RecordType, p => p, StringComparer.Ordinal);

    /// <summary>The profile for <paramref name="recordType" />, or null when none is registered.</summary>
    public static IRecordProfile? Get(string recordType) =>
        ByType.GetValueOrDefault(recordType);

    /// <summary>Whether a profile exists for the record type.</summary>
    public static bool Has(string recordType) => ByType.ContainsKey(recordType);
}
