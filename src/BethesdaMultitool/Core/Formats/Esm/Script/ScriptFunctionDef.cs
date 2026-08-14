namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     Definition shared by script-command and condition-function lookups. Most entries are extracted
///     from the game's <c>SCRIPT_FUNCTION</c>/<c>CommandInfo</c> structs (layout is game-specific), but
///     a table may also contain explicitly attributed community extension definitions.
/// </summary>
/// <param name="Name">Full function name (e.g., "GetActorValue").</param>
/// <param name="ShortName">Abbreviated name (e.g., "GetAV"), empty if none.</param>
/// <param name="IsReferenceFunction">Whether function operates on a reference (ref.FunctionName syntax).</param>
/// <param name="Params">Parameter definitions array.</param>
/// <param name="IsConditionFunction">
///     Whether the definition's declared retail or extension runtime table supports condition use,
///     or null when the legacy generator did not capture that metadata. Source provenance remains a
///     property of the owning generated table; <c>true</c> alone does not imply a retail callback.
/// </param>
/// <param name="HasUnresolvedParameters">Whether the engine declares parameters but its metadata pointer is null.</param>
public record ScriptFunctionDef(
    string Name,
    string ShortName,
    bool IsReferenceFunction,
    ScriptFunctionParamDef[] Params,
    bool? IsConditionFunction = null,
    bool HasUnresolvedParameters = false);
