namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Definition of a script function (opcode handler).
///     Extracted from SCRIPT_FUNCTION structs (40 bytes each) in the game executable.
/// </summary>
/// <param name="Name">Full function name (e.g., "GetActorValue").</param>
/// <param name="ShortName">Abbreviated name (e.g., "GetAV"), empty if none.</param>
/// <param name="IsReferenceFunction">Whether function operates on a reference (ref.FunctionName syntax).</param>
/// <param name="Params">Parameter definitions array.</param>
public record ScriptFunctionDef(
    string Name,
    string ShortName,
    bool IsReferenceFunction,
    ScriptFunctionParamDef[] Params);
