namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     One described CTDA function parameter (slot 1 or 2). <see cref="Display" /> is plain text with no
///     markup; callers that render Spectre markup must escape it (it can contain <c>[</c>).
/// </summary>
/// <param name="Raw">
///     The raw u32 stored in the CTDA slot. When a CIS1/CIS2 string was observed this is placeholder storage
///     and <see cref="StringValue" /> is the value.
/// </param>
/// <param name="Kind">One of the <c>Kind*</c> constants.</param>
/// <param name="Display">Readable text for the operand, as printed inside the function call.</param>
public sealed record ConditionOperand(uint Raw, string Kind, string Display)
{
    /// <summary>A FormID, printed as <c>EDID [0xFORMID]</c>.</summary>
    public const string KindFormId = "formId";

    /// <summary>A plain number (counts, stages, enum values shown numerically).</summary>
    public const string KindNumber = "number";

    /// <summary>A FO3/FNV actor-value code, printed by its script name.</summary>
    public const string KindActorValue = "actorValue";

    /// <summary>A FO3/FNV sex value (0 Male, 1 Female).</summary>
    public const string KindSex = "sex";

    /// <summary>A script-variable index, printed as its name when known, otherwise <c>var N</c>.</summary>
    public const string KindScriptVariable = "scriptVariable";

    /// <summary>A CIS1/CIS2 string parameter.</summary>
    public const string KindString = "string";

    /// <summary>
    ///     A value whose meaning the condition table does not declare (an unknown function, an undeclared
    ///     slot, or a declared slot the table cannot classify), printed as hex and never interpreted.
    /// </summary>
    public const string KindRaw = "raw";

    /// <summary>Whether the function declares this parameter slot.</summary>
    public bool Declared { get; init; }

    /// <summary>The FormID, for <see cref="KindFormId" />.</summary>
    public uint? FormId { get; init; }

    /// <summary>The EditorID the resolver knows for <see cref="FormId" />, or null.</summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     For <see cref="KindFormId" />: whether an EditorID was found. For <see cref="KindScriptVariable" />:
    ///     whether the variable name was found. Null for kinds that are never looked up.
    /// </summary>
    public bool? Resolved { get; init; }

    /// <summary>The variable index, for <see cref="KindScriptVariable" />.</summary>
    public uint? VariableIndex { get; init; }

    /// <summary>The resolved variable name, or null when it could not be resolved (never a guess).</summary>
    public string? VariableName { get; init; }

    /// <summary>The FormID owning the variable (the quest, for <c>GetQuestVariable</c>), when known.</summary>
    public uint? VariableOwnerFormId { get; init; }

    /// <summary>The CIS1/CIS2 string value, for <see cref="KindString" />.</summary>
    public string? StringValue { get; init; }
}
