namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>Result of walking one runtime manager/singleton global: its target type, child-pointer counts, and extracted strings.</summary>
public sealed class ManagerWalkResult
{
    public string GlobalName { get; set; } = "";
    public uint PointerValue { get; set; }
    public string TargetType { get; set; } = "";
    public int ChildPointers { get; set; }
    public int WalkableEntries { get; set; }
    public List<string> ExtractedStrings { get; } = [];
    internal List<RuntimeStringOwnershipClaim> OwnedStringClaims { get; } = [];
    public string Summary { get; set; } = "";
}
