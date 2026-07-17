using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Planner;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     Exact source-to-emitted identity mapping for one recovered quest-script local.
///     Conditions and every emitted bytecode operand that addresses this state channel must
///     consume the same mapping; changing only CTDA would leave producer scripts writing the
///     prototype index while dialogue reads an unrelated or permanently-zero retail local.
/// </summary>
internal sealed record QuestVariableRecoveryMapping(
    uint SourceQuestFormId,
    uint TargetQuestFormId,
    uint TargetScriptFormId,
    ScriptVariableInfo SourceVariable,
    ScriptVariableInfo TargetVariable,
    ScriptVariableDeclarationKind DeclarationKind);
