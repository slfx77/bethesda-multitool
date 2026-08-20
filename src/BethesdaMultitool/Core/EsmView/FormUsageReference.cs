namespace BethesdaMultitool.Core.EsmView;

/// <summary>A single record that references a form: the referrer's FormID, its kind, and where the reference occurs.</summary>
internal sealed record FormUsageReference(
    uint SourceFormId,
    string SourceKind,
    string Context);
