using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     Everything <see cref="ConditionDescriber" /> needs beyond the CTDA bytes: the game whose condition
///     table numbers the functions, a <see cref="FormIdResolver" /> for EditorIDs, and (when built from a
///     <see cref="RecordCollection" />) quest-variable names for <c>GetQuestVariable</c>.
///     <para>
///         Quest-variable names come from <see cref="QuestRecord.Variables" />, which the parser fills from
///         the quest script's SLSD/SCVR table. The index is built lazily, once per context, and a quest
///         FormID that appears more than once (an overlay or a merged load order) never throws: every copy's
///         variables are merged and the first name seen for an index wins.
///     </para>
/// </summary>
public sealed class ConditionDisplayContext
{
    private static readonly IReadOnlyList<QuestRecord> NoQuests = [];

    private readonly Lazy<Dictionary<uint, Dictionary<uint, string?>>> _questVariables;
    private readonly Lazy<ExternalScriptVariableResolver>? _scriptVariables;

    private ConditionDisplayContext(
        FormIdResolver resolver,
        BethesdaGame game,
        bool gameAssumed,
        IReadOnlyList<QuestRecord> quests,
        bool hasQuestSource,
        RecordCollection? records = null)
    {
        Resolver = resolver;
        GameAssumed = gameAssumed || game == BethesdaGame.Unknown;
        Game = game == BethesdaGame.Unknown ? GameProfiles.DefaultGame : game;
        HasQuestVariableSource = hasQuestSource;
        _questVariables = new Lazy<Dictionary<uint, Dictionary<uint, string?>>>(
            () => BuildQuestVariableIndex(quests),
            LazyThreadSafetyMode.ExecutionAndPublication);
        if (records is not null)
            _scriptVariables = new Lazy<ExternalScriptVariableResolver>(
                () => ExternalScriptVariableResolver.FromRecords(records),
                LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    ///     The game whose condition table numbers the functions. <see cref="BethesdaGame.Unknown" /> is mapped
    ///     to <see cref="GameProfiles.DefaultGame" /> (as the dialogue viewer does) and flagged by
    ///     <see cref="GameAssumed" />.
    /// </summary>
    public BethesdaGame Game { get; }

    /// <summary>
    ///     True when <see cref="Game" /> was not detected from the input but assumed. Callers should say so
    ///     beside the conditions, because function names come from the assumed game's table.
    /// </summary>
    public bool GameAssumed { get; }

    /// <summary>EditorID source for FormID operands, GLOB comparisons and the Reference slot.</summary>
    public FormIdResolver Resolver { get; }

    /// <summary>
    ///     True when this context was built from a record collection, so an unresolved quest variable means
    ///     the loaded quest carries no name for that index (not that no quests were available).
    /// </summary>
    public bool HasQuestVariableSource { get; }

    /// <summary>Uses this collection's game, variable tables and explicit script-owner links.</summary>
    public static ConditionDisplayContext From(RecordCollection records, FormIdResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(resolver);
        return new ConditionDisplayContext(resolver, records.Game, false, records.Quests, true, records);
    }

    /// <summary>
    ///     Builds a context with no quest-variable names (every <c>GetQuestVariable</c> variable prints as
    ///     <c>var N</c>).
    /// </summary>
    /// <param name="resolver">EditorID source.</param>
    /// <param name="game">The game; <see cref="BethesdaGame.Unknown" /> maps to the default and sets <see cref="GameAssumed" />.</param>
    /// <param name="gameAssumed">Pass true when the caller itself substituted a default game.</param>
    public static ConditionDisplayContext ForResolver(
        FormIdResolver resolver,
        BethesdaGame game,
        bool gameAssumed = false)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return new ConditionDisplayContext(resolver, game, gameAssumed, NoQuests, false);
    }

    /// <summary>
    ///     Looks up the name of variable <paramref name="index" /> of the quest <paramref name="questFormId" />.
    ///     Returns null when the quest is unknown, carries no variable table, or has no (or an empty) name for
    ///     that index. Never guesses.
    /// </summary>
    /// <param name="questFormId">The quest's FormID (the condition's first parameter).</param>
    /// <param name="index">The SLSD variable index (the condition's second parameter).</param>
    /// <param name="questHasVariables">True when the quest was found with at least one variable.</param>
    public string? TryGetQuestVariableName(uint questFormId, uint index, out bool questHasVariables)
    {
        if (!_questVariables.Value.TryGetValue(questFormId, out var variables) || variables.Count == 0)
        {
            questHasVariables = false;
            return null;
        }

        questHasVariables = true;
        return variables.TryGetValue(index, out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;
    }

    /// <summary>
    /// Resolves a variable through explicit placed-reference, base-object and attached-script links.
    /// Missing links, conflicting copies and incomplete tables retain the numeric operand.
    /// </summary>
    public string? TryGetScriptVariableName(uint ownerFormId, uint index) => index <= ushort.MaxValue
        ? _scriptVariables?.Value.Resolve(ownerFormId, (ushort)index).Name
        : null;

    private static Dictionary<uint, Dictionary<uint, string?>> BuildQuestVariableIndex(
        IReadOnlyList<QuestRecord> quests)
    {
        var index = new Dictionary<uint, Dictionary<uint, string?>>();
        foreach (var quest in quests)
        {
            if (!index.TryGetValue(quest.FormId, out var variables))
            {
                variables = [];
                index.Add(quest.FormId, variables);
            }

            foreach (var variable in quest.Variables)
            {
                // A later copy may still fill an index whose first-seen name was missing.
                if (!variables.TryAdd(variable.Index, variable.Name) &&
                    string.IsNullOrWhiteSpace(variables[variable.Index]))
                {
                    variables[variable.Index] = variable.Name;
                }
            }
        }

        return index;
    }
}
