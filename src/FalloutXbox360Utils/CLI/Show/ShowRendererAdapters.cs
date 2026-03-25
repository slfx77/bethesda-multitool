using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.CLI.Show;

// Actor domain adapters

internal sealed class NpcShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => ActorShowRenderer.TryShowNpc(records, resolver, formId, editorId);
}

internal sealed class RaceShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => ActorShowRenderer.TryShowRace(records, resolver, formId, editorId);
}

internal sealed class FactionShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => ActorShowRenderer.TryShowFaction(records, resolver, formId, editorId);
}

internal sealed class ScriptShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => ActorShowRenderer.TryShowScript(records, resolver, formId, editorId);
}

// Quest domain adapters

internal sealed class QuestShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => QuestShowRenderer.TryShowQuest(records, resolver, formId, editorId);
}

internal sealed class DialogTopicShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => QuestShowRenderer.TryShowDialogTopic(records, resolver, formId, editorId);
}

// Item domain adapters

internal sealed class WeaponShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => ItemShowRenderer.TryShowWeapon(records, resolver, formId, editorId);
}

internal sealed class ArmorShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => ItemShowRenderer.TryShowArmor(records, resolver, formId, editorId);
}

internal sealed class RecipeShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => ItemShowRenderer.TryShowRecipe(records, resolver, formId, editorId);
}

internal sealed class BookShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => ItemShowRenderer.TryShowBook(records, resolver, formId, editorId);
}

// Misc domain adapters

internal sealed class SoundShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => MiscShowRenderer.TryShowSound(records, resolver, formId, editorId);
}

internal sealed class ExplosionShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => MiscShowRenderer.TryShowExplosion(records, resolver, formId, editorId);
}

internal sealed class MessageShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => MiscShowRenderer.TryShowMessage(records, resolver, formId, editorId);
}

internal sealed class ChallengeShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => MiscShowRenderer.TryShowChallenge(records, resolver, formId, editorId);
}

internal sealed class GenericShowAdapter : IRecordDisplayRenderer
{
    public bool TryShow(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId)
        => MiscShowRenderer.TryShowGeneric(records, resolver, formId, editorId);
}
