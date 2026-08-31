using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation;

/// <summary>
///     Builds <see cref="RecordDetailModel" /> instances for display in the GUI and CLI.
///     Per-record-type building logic is in <see cref="RecordDetailBuilders" />;
///     shared helper methods are in <see cref="RecordDetailHelpers" />.
/// </summary>
internal static class RecordDetailPresenter
{
    /// <summary>
    ///     Build the detail model for a record, then append whatever nested payloads the collection
    ///     holds for it.
    ///     <para>
    ///         The append happens here, once, rather than inside each <c>RecordDetailBuilders.BuildX</c>
    ///         because the payloads hang off engine base classes shared by dozens of record types —
    ///         adding them per builder would mean the same block in twenty places, and every builder
    ///         added later would silently omit them.
    ///     </para>
    /// </summary>
    internal static bool TryBuildForLookup(
        RecordCollection records,
        FormIdResolver resolver,
        uint? formId,
        string? editorId,
        out RecordDetailModel? model)
    {
        if (!TryBuildTypedModel(records, resolver, formId, editorId, out model) || model == null)
        {
            return false;
        }

        model = RecordDetailNestedPayloads.Append(model, records, resolver);
        return true;
    }

    private static bool TryBuildTypedModel(
        RecordCollection records,
        FormIdResolver resolver,
        uint? formId,
        string? editorId,
        out RecordDetailModel? model)
    {
        if (TryFind(records.Npcs, formId, editorId, r => r.FormId, r => r.EditorId, out var npc))
        {
            model = RecordDetailBuilders.BuildNpc(npc!, resolver);
            return true;
        }

        if (TryFind(records.Creatures, formId, editorId, r => r.FormId, r => r.EditorId, out var creature))
        {
            model = RecordDetailBuilders.BuildCreature(creature!, resolver);
            return true;
        }

        if (TryFind(records.Weapons, formId, editorId, r => r.FormId, r => r.EditorId, out var weapon))
        {
            model = RecordDetailBuilders.BuildWeapon(weapon!, resolver);
            return true;
        }

        if (TryFind(records.Armor, formId, editorId, r => r.FormId, r => r.EditorId, out var armor))
        {
            model = RecordDetailBuilders.BuildArmor(armor!, resolver);
            return true;
        }

        if (TryFind(records.Quests, formId, editorId, r => r.FormId, r => r.EditorId, out var quest))
        {
            model = RecordDetailBuilders.BuildQuest(quest!, resolver);
            return true;
        }

        if (TryFind(records.Packages, formId, editorId, r => r.FormId, r => r.EditorId, out var package))
        {
            model = RecordDetailBuilders.BuildPackage(package!, resolver);
            return true;
        }

        if (TryFind(records.DialogTopics, formId, editorId, r => r.FormId, r => r.EditorId, out var topic))
        {
            model = RecordDetailBuilders.BuildDialogTopic(topic!, records, resolver);
            return true;
        }

        if (TryFind(records.Cells, formId, editorId, r => r.FormId, r => r.EditorId, out var cell))
        {
            model = RecordDetailBuilders.BuildCell(cell!, resolver);
            return true;
        }

        if (TryFind(records.Worldspaces, formId, editorId, r => r.FormId, r => r.EditorId,
                out var worldspace))
        {
            model = RecordDetailBuilders.BuildWorldspace(worldspace!, resolver);
            return true;
        }

        model = null;
        return false;
    }

    internal static bool TryBuildForRecord(
        object record,
        RecordCollection? records,
        FormIdResolver resolver,
        out RecordDetailModel? model)
    {
        switch (record)
        {
            case NpcRecord npc:
                model = RecordDetailBuilders.BuildNpc(npc, resolver);
                return true;
            case CreatureRecord creature:
                model = RecordDetailBuilders.BuildCreature(creature, resolver);
                return true;
            case WeaponRecord weapon:
                model = RecordDetailBuilders.BuildWeapon(weapon, resolver);
                return true;
            case ArmorRecord armor:
                model = RecordDetailBuilders.BuildArmor(armor, resolver);
                return true;
            case QuestRecord quest:
                model = RecordDetailBuilders.BuildQuest(quest, resolver);
                return true;
            case PackageRecord package:
                model = RecordDetailBuilders.BuildPackage(package, resolver);
                return true;
            case DialogTopicRecord topic:
                model = RecordDetailBuilders.BuildDialogTopic(topic, records, resolver);
                return true;
            case CellRecord cell:
                model = RecordDetailBuilders.BuildCell(cell, resolver);
                return true;
            case WorldspaceRecord worldspace:
                model = RecordDetailBuilders.BuildWorldspace(worldspace, resolver);
                return true;
            default:
                model = null;
                return false;
        }
    }

    private static bool TryFind<T>(
        IEnumerable<T> records,
        uint? formId,
        string? editorId,
        Func<T, uint> formIdSelector,
        Func<T, string?> editorIdSelector,
        out T? match)
        where T : class
    {
        match = records.FirstOrDefault(record =>
            (formId.HasValue && formIdSelector(record) == formId.Value) ||
            (!string.IsNullOrEmpty(editorId) &&
             string.Equals(editorIdSelector(record), editorId, StringComparison.OrdinalIgnoreCase)));
        return match != null;
    }
}
