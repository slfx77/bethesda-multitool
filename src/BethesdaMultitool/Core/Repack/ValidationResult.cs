namespace BethesdaMultitool.Core.Repack;

/// <summary>
///     Result of source folder validation.
/// </summary>
public sealed record ValidationResult(bool IsValid, string Message);
