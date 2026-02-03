namespace FalloutXbox360Utils.Core;

public sealed class ManagerWalkResult
{
    public string GlobalName { get; set; } = "";
    public uint PointerValue { get; set; }
    public string TargetType { get; set; } = "";
    public int ChildPointers { get; set; }
    public int WalkableEntries { get; set; }
    public List<string> ExtractedStrings { get; } = [];
    public string Summary { get; set; } = "";
}