namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     How a CTDA condition parameter's raw u32 should be interpreted for display/decoding —
///     the game-agnostic classification behind the per-game param-type enums.
/// </summary>
public enum ConditionParamKind
{
    /// <summary>Plain number (counts, stages, enum indices the display shows numerically).</summary>
    Numeric,

    /// <summary>FormID — resolve to a record name/EditorID.</summary>
    FormId
}
