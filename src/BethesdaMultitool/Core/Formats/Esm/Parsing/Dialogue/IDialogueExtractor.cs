using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;

/// <summary>
///     Builds the typed dialogue models from one game's DIAL/INFO subrecord layout, feeding the
///     shared game-agnostic <see cref="Handlers.DialogueTreeBuilder" />. The context is always
///     supplied: localized games (Skyrim, FO4/76) resolve .STRINGS/.ILSTRINGS ids through it;
///     inline-text games (Oblivion) ignore it. Two dialogue pipelines deliberately do NOT implement
///     this interface: FNV/FO3 dialogue lives in the typed-handler path
///     (<c>Handlers.DialogueRecordHandler</c>), and TES3's positional file-order model with
///     deferred editor-id resolution has its own <c>Tes3DialogueExtractor</c> inside the TES3
///     parser fork — neither has a polymorphic call site here.
/// </summary>
internal interface IDialogueExtractor
{
    DialogTopicRecord BuildTopic(
        uint formId, string? editorId, IReadOnlyList<RawSubrecord> subs, RecordParserContext context);

    DialogueRecord BuildInfo(
        uint formId, string? editorId, uint? topicFormId, ushort infoIndex,
        IReadOnlyList<RawSubrecord> subs, RecordParserContext context);
}

/// <summary>
///     Selects the DIAL/INFO extractor for a schema-primary game. Mirrors the layouts: Skyrim's
///     localized TRDT framing; FO4's TRDA framing (FO76 verified identical); everything else falls
///     to Oblivion's inline-text layout. Starfield lands on the Oblivion arm today, matching the
///     historical switch — unreachable in practice, as it has no generated schema so the
///     schema-driven parser never runs for it.
/// </summary>
internal static class DialogueExtractors
{
    public static IDialogueExtractor For(BethesdaGame game) => game switch
    {
        BethesdaGame.Skyrim => SkyrimDialogueExtractor.Instance,
        BethesdaGame.Fallout4 or BethesdaGame.Fallout76 => Fallout4DialogueExtractor.Instance,
        _ => OblivionDialogueExtractor.Instance,
    };
}
