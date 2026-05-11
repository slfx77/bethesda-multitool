namespace FalloutXbox360Utils.Core.Formats.Esm.Merge;

/// <summary>
///     Classifies a DMP-derived record as either an "override" (existing in the base ESM) or
///     "new" (not present in the base ESM and would need a fresh plugin-index FormID).
/// </summary>
public sealed class NewVsOverrideClassifier
{
    private readonly HashSet<uint> _esmFormIds;

    public NewVsOverrideClassifier(IEnumerable<uint> esmFormIds)
    {
        _esmFormIds = new HashSet<uint>(esmFormIds);
    }

    /// <summary>True if a record with this FormID exists in the base ESM.</summary>
    public bool IsOverride(uint formId)
    {
        return _esmFormIds.Contains(formId);
    }

    /// <summary>True if a record with this FormID would be a new plugin record (not in the base ESM).</summary>
    public bool IsNew(uint formId)
    {
        return !_esmFormIds.Contains(formId);
    }

    /// <summary>Total count of FormIDs from the base ESM.</summary>
    public int EsmFormIdCount => _esmFormIds.Count;
}
