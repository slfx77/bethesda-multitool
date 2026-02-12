namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Definition of a single parameter in a script function.
///     Extracted from SCRIPT_PARAMETER structs (12 bytes each) in the game executable.
/// </summary>
/// <param name="Name">Parameter name (e.g., "ObjectReferenceID", "Count").</param>
/// <param name="Type">Parameter type determining compiled data encoding.</param>
/// <param name="Optional">Whether this parameter is optional in script source.</param>
public record ScriptFunctionParamDef(string Name, ScriptParamType Type, bool Optional);
