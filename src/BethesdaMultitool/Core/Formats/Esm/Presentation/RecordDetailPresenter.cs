using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

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
    /// <param name="records">The loaded collection to search.</param>
    /// <param name="resolver">EditorID/display-name source.</param>
    /// <param name="formId">The FormID to find, or null to search by EditorID.</param>
    /// <param name="editorId">The EditorID to find, or null to search by FormID.</param>
    /// <param name="model">The built model, when a typed builder matched.</param>
    /// <param name="isMemoryDumpInput">
    ///     True when <paramref name="records" /> came from a memory dump. It decides how script text is labelled
    ///     (plugin SCTX and unattributed dump text share one origin value) and that a missing script is
    ///     described as absent from the capture rather than from the build.
    /// </param>
    internal static bool TryBuildForLookup(
        RecordCollection records,
        FormIdResolver resolver,
        uint? formId,
        string? editorId,
        out RecordDetailModel? model,
        bool isMemoryDumpInput = false)
    {
        if (!TryBuildTypedModel(records, resolver, formId, editorId, isMemoryDumpInput, out model) ||
            model == null)
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
        bool isMemoryDumpInput,
        out RecordDetailModel? model)
    {
        if (TryFind(records.ImageSpaceModifiers, formId, editorId, r => r.FormId, r => r.EditorId, out var modifier))
        {
            model = ImageSpaceModifierDetailBuilder.Build(modifier!, resolver);
            return true;
        }

        if (TryFind(records.Npcs, formId, editorId, r => r.FormId, r => r.EditorId, out var npc))
        {
            model = RecordDetailBuilders.BuildNpc(npc!, resolver, records.Game);
            return true;
        }

        if (TryFind(records.Creatures, formId, editorId, r => r.FormId, r => r.EditorId, out var creature))
        {
            model = RecordDetailBuilders.BuildCreature(creature!, resolver, records.Game);
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
            model = RecordDetailBuilders.BuildQuest(quest!, resolver, records);
            return true;
        }

        if (TryFind(records.Packages, formId, editorId, r => r.FormId, r => r.EditorId, out var package))
        {
            model = RecordDetailBuilders.BuildPackage(
                package!, resolver, ConditionDisplayContext.From(records, resolver));
            return true;
        }

        if (TryFind(records.DialogTopics, formId, editorId, r => r.FormId, r => r.EditorId, out var topic))
        {
            model = RecordDetailBuilders.BuildDialogTopic(topic!, records, resolver);
            return true;
        }

        // INFO: the CLI lookup only. The GUI's dialogue viewer has its own INFO panel, so TryBuildForRecord
        // deliberately has no INFO case.
        if (records.Game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 or BethesdaGame.Unknown &&
            TryFind(records.Dialogues, formId, editorId, r => r.FormId, r => r.EditorId, out var info))
        {
            model = DialogueInfoDetailBuilder.Build(
                info!, records, resolver, ConditionDisplayContext.From(records, resolver), isMemoryDumpInput);
            return true;
        }

        // TERM: the CLI lookup only; TryBuildForRecord (the GUI) deliberately has no TERM case.
        if (TryFind(records.Terminals, formId, editorId, r => r.FormId, r => r.EditorId, out var terminal))
        {
            model = TerminalRecordDetailBuilder.Build(
                terminal!, records, resolver, ConditionDisplayContext.From(records, resolver), isMemoryDumpInput);
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
            case MessageRecord message:
                model = MessageDetailBuilder.Build(message, resolver,
                    records is null ? ConditionDisplayContext.ForResolver(resolver, GameProfiles.DefaultGame, gameAssumed: true)
                        : ConditionDisplayContext.From(records, resolver));
                return true;
            case PerkRecord perk:
                model = PerkDetailBuilder.Build(perk, resolver);
                return true;
            case ImageSpaceModifierRecord modifier:
                model = ImageSpaceModifierDetailBuilder.Build(modifier, resolver);
                return true;
            case NpcRecord npc:
                model = RecordDetailBuilders.BuildNpc(npc, resolver, records?.Game ?? BethesdaGame.Unknown);
                return true;
            case CreatureRecord creature:
                model = RecordDetailBuilders.BuildCreature(creature, resolver, records?.Game ?? BethesdaGame.Unknown);
                return true;
            case WeaponRecord weapon:
                model = RecordDetailBuilders.BuildWeapon(weapon, resolver);
                return true;
            case ArmorRecord armor:
                model = RecordDetailBuilders.BuildArmor(armor, resolver);
                return true;
            case QuestRecord quest:
                model = RecordDetailBuilders.BuildQuest(quest, resolver, records);
                return true;
            case PackageRecord package:
                model = RecordDetailBuilders.BuildPackage(
                    package,
                    resolver,
                    records is not null
                        ? ConditionDisplayContext.From(records, resolver)
                        : ConditionDisplayContext.ForResolver(resolver, GameProfiles.DefaultGame, gameAssumed: true));
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
