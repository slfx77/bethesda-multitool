namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     A single field-level change between two versions of a record.
/// </summary>
public record FieldChange(string FieldName, string? OldValue, string? NewValue);
