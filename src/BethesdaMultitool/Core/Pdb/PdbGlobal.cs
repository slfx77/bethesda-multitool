namespace BethesdaMultitool.Core.Pdb;

/// <summary>A global symbol parsed from a PDB: its kind, PE section/offset location, and name.</summary>
public sealed class PdbGlobal
{
    public required string Kind { get; init; }
    public required int Section { get; init; }
    public required uint Offset { get; init; }
    public required string Name { get; init; }
}
