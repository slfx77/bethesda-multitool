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
///     <para>
///         The byte-order argument belongs to the individual DIAL/INFO record rather than the selected
///         game. This keeps recovered or synthetic records honest and, for Skyrim, preserves unconverted
///         Xbox 360 numeric fields without making platform claims about FO4/FO76 inputs.
///     </para>
/// </summary>
internal interface IDialogueExtractor
{
    DialogTopicRecord BuildTopic(
        uint formId, string? editorId, IReadOnlyList<RawSubrecord> subs, bool isBigEndian,
        RecordParserContext context);

    DialogueRecord BuildInfo(
        uint formId, string? editorId, uint? topicFormId, ushort infoIndex,
        IReadOnlyList<RawSubrecord> subs, bool isBigEndian, RecordParserContext context);
}

/// <summary>
///     Selects the DIAL/INFO extractor for a schema-primary game. Mirrors the layouts: Oblivion's
///     inline text, Skyrim's localized TRDT framing, and FO4's TRDA framing (FO76 verified
///     identical). Games without a registered schema-primary dialogue layout are rejected instead
///     of being interpreted as Oblivion records.
/// </summary>
internal static class DialogueExtractors
{
    public static IDialogueExtractor For(BethesdaGame game) => game switch
    {
        BethesdaGame.Oblivion => OblivionDialogueExtractor.Instance,
        BethesdaGame.Skyrim => SkyrimDialogueExtractor.Instance,
        BethesdaGame.Fallout4 or BethesdaGame.Fallout76 => Fallout4DialogueExtractor.Instance,
        BethesdaGame.Unknown or
            BethesdaGame.Morrowind or
            BethesdaGame.Fallout3 or
            BethesdaGame.FalloutNewVegas or
            BethesdaGame.Starfield => throw new NotSupportedException(
                $"No schema-primary dialogue extractor is registered for {game}."),
        _ => throw new ArgumentOutOfRangeException(nameof(game), game, null),
    };
}
