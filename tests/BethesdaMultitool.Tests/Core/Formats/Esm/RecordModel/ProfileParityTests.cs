using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     The FNV parity gate for the schema-driven presentation profiles. For every FNV record of a
///     given type, the profile (reading the DecodedTree) must build the EXACT same
///     <see cref="RecordDetailModel" /> the hand-written typed <see cref="RecordDetailBuilders" />
///     produces (reading the typed record) — every section, label, value and link.
///     <para>
///         This replaces seven near-identical files (Armor/Creature/DialogTopic/Npc/Package/Quest/
///         Weapon, ~758 lines) that were the same test written out once per record type: identical
///         master resolution, identical cache load, identical compare loop and identical
///         <see cref="Serialize" />. Only the profile, the collection accessor, the typed-builder
///         call and the minimum-compared floor ever differed, so those are the table below.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public class ProfileParityTests
{
    /// <summary>Mismatches quoted in the failure message before it is truncated.</summary>
    private const int MaxReportedMismatches = 5;

    /// <summary>
    ///     The record signatures under test. The theory is keyed on the signature rather than on
    ///     the case object because <c>[MemberData]</c> requires a <em>public</em> member, and
    ///     <see cref="ProfileParityCase" /> exposes the internal <see cref="IRecordProfile" />.
    ///     Signatures also give each case a readable display name.
    /// </summary>
    public static TheoryData<string> Signatures =>
        [.. Cases.Select(c => c.Signature)];

    private static IReadOnlyList<ProfileParityCase> Cases =>
    [
        Case("WEAP", "weapons", minimumCompared: 50, new WeaponProfile(),
            records => records.Weapons,
            weapon => weapon.FormId,
            weapon => weapon.EditorId,
            weapon => weapon.FullName,
            (weapon, _, resolver) => RecordDetailBuilders.BuildWeapon(weapon, resolver)),

        Case("ARMO", "armor records", minimumCompared: 200, new ArmorProfile(),
            records => records.Armor,
            armor => armor.FormId,
            armor => armor.EditorId,
            armor => armor.FullName,
            (armor, _, resolver) => RecordDetailBuilders.BuildArmor(armor, resolver)),

        Case("CREA", "creatures", minimumCompared: 50, new CreatureProfile(),
            records => records.Creatures,
            creature => creature.FormId,
            creature => creature.EditorId,
            creature => creature.FullName,
            (creature, _, resolver) => RecordDetailBuilders.BuildCreature(creature, resolver)),

        Case("NPC_", "NPCs", minimumCompared: 1000, new NpcProfile(),
            records => records.Npcs,
            npc => npc.FormId,
            npc => npc.EditorId,
            npc => npc.FullName,
            (npc, _, resolver) => RecordDetailBuilders.BuildNpc(npc, resolver)),

        // QUST: Variables and RelatedNpcFormIds are cross-record enrichment the profile cannot
        // (and should not) reproduce from the record's own tree.
        Case("QUST", "quests", minimumCompared: 50, new QuestProfile(),
            records => records.Quests,
            quest => quest.FormId,
            quest => quest.EditorId,
            quest => quest.FullName,
            (quest, _, resolver) => RecordDetailBuilders.BuildQuest(
                quest with { Variables = [], RelatedNpcFormIds = [] }, resolver)),

        // DIAL: DummyPrompt is enrichment-derived and the tree cannot reproduce it, so it is
        // stripped from the typed reference (the profile omits it; FNV keeps BuildDialogTopic for
        // the full display). DIAL is also the only profile that needs the whole collection, for
        // its child-INFO list.
        Case("DIAL", "dialog topics", minimumCompared: 50, new DialogTopicProfile(),
            records => records.DialogTopics,
            topic => topic.FormId,
            topic => topic.EditorId,
            topic => topic.FullName,
            (topic, records, resolver) =>
                RecordDetailBuilders.BuildDialogTopic(topic with { DummyPrompt = null }, records, resolver)),

        // PACK: the profile is given a null display name, and two fields decoded from
        // schema-"Unused" bytes are not recoverable from the tree, so they are stripped from the
        // typed reference rather than asserted.
        Case("PACK", "packages", minimumCompared: 50, new PackageProfile(),
            records => records.Packages,
            package => package.FormId,
            package => package.EditorId,
            _ => null,
            (package, _, resolver) => RecordDetailBuilders.BuildPackage(
                package with
                {
                    IsStartingLocationLinkedRef = false,
                    UseWeaponData = package.UseWeaponData is { } u ? u with { WeaponFormId = null } : null
                },
                resolver))
    ];

    [Theory]
    [MemberData(nameof(Signatures))]
    public async Task SchemaProfile_ReproducesTheTypedBuilderExactly_ForFnv(string signature)
    {
        var parityCase = Cases.Single(c => c.Signature == signature);

        var esm = RealAssetPaths.Masters.FalloutNv();
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(esm is not null, RealAssetPaths.SkipMessage("FalloutNV.esm"));

        // Never disposed: RealAssetEsmCache owns the result and shares it across this collection.
        var result = await RealAssetEsmCache.LoadAsync(esm!, TestContext.Current.CancellationToken);
        var records = result.Records;
        var resolver = new FormIdResolver(records.FormIdToEditorId, records.FormIdToDisplayName);

        var byFormId = parityCase.IndexByFormId(records);

        var compared = 0;
        var mismatches = new List<string>();
        foreach (var (formId, tree) in records.DecodedTreesByFormId)
        {
            if (!byFormId.TryGetValue(formId, out var record))
            {
                continue;
            }

            var typed = Serialize(parityCase.BuildTyped(record, records, resolver));
            var profiled = Serialize(parityCase.Profile.Build(
                formId, parityCase.EditorId(record), parityCase.DisplayName(record), tree,
                BethesdaGame.FalloutNewVegas, resolver, records));

            compared++;
            if (typed != profiled && mismatches.Count < MaxReportedMismatches)
            {
                mismatches.Add(
                    $"{parityCase.Signature} 0x{formId:X8} ({parityCase.EditorId(record)}):\n"
                    + $"--- typed ---\n{typed}\n--- profile ---\n{profiled}");
            }
        }

        Assert.True(compared > parityCase.MinimumCompared,
            $"Expected to compare more than {parityCase.MinimumCompared} FNV "
            + $"{parityCase.Noun}; got {compared}. The master may have failed to decode.");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {compared} {parityCase.Noun} diverged from the typed builder:\n\n"
            + string.Join("\n\n", mismatches));
    }

    /// <summary>
    ///     Builds a case from strongly-typed accessors and erases the record type, so the theory
    ///     body stays type-agnostic while every call site above is still compiler-checked.
    /// </summary>
    private static ProfileParityCase Case<TRecord>(
        string signature,
        string noun,
        int minimumCompared,
        IRecordProfile profile,
        Func<RecordCollection, IEnumerable<TRecord>> select,
        Func<TRecord, uint> formId,
        Func<TRecord, string?> editorId,
        Func<TRecord, string?> displayName,
        Func<TRecord, RecordCollection, FormIdResolver, RecordDetailModel> buildTyped)
    {
        return new ProfileParityCase(
            signature,
            noun,
            minimumCompared,
            profile,
            records => select(records).ToDictionary(formId, r => (object)r!),
            record => editorId((TRecord)record),
            record => displayName((TRecord)record),
            (record, records, resolver) => buildTyped((TRecord)record, records, resolver));
    }

    /// <summary>Deterministic, order-preserving serialization of the detail model for equality.</summary>
    private static string Serialize(RecordDetailModel model)
    {
        var sb = new StringBuilder();
        foreach (var section in model.Sections)
        {
            sb.Append("§ ").Append(section.Title).Append('\n');
            foreach (var entry in section.Entries)
            {
                sb.Append("  ").Append(entry.Kind).Append('|').Append(entry.Label).Append('|')
                    .Append(entry.Value ?? "").Append('|').Append(entry.LinkedFormId?.ToString("X8") ?? "")
                    .Append('|').Append(entry.ExpandByDefault).Append('\n');
                if (entry.Items is null)
                {
                    continue;
                }

                foreach (var item in entry.Items)
                {
                    sb.Append("    - ").Append(item.Label).Append('|').Append(item.Value)
                        .Append('|').Append(item.LinkedFormId?.ToString("X8") ?? "").Append('\n');
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>One record type's parity contract. <see cref="ToString" /> names the theory case.</summary>
    internal sealed record ProfileParityCase(
        string Signature,
        string Noun,
        int MinimumCompared,
        IRecordProfile Profile,
        Func<RecordCollection, Dictionary<uint, object>> IndexByFormId,
        Func<object, string?> EditorId,
        Func<object, string?> DisplayName,
        Func<object, RecordCollection, FormIdResolver, RecordDetailModel> BuildTyped)
    {
        public override string ToString()
        {
            return Signature;
        }
    }
}
