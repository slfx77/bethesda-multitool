namespace BethesdaMultitool.Core.Formats.Nif.Parser;

/// <summary>
///     Information about a NIF file.
/// </summary>
public sealed class NifInfo
{
    public string HeaderString { get; set; } = "";
    public uint BinaryVersion { get; set; }
    public bool IsBigEndian { get; set; }
    public uint UserVersion { get; set; }
    public uint BsVersion { get; set; }
    public int BlockCount { get; set; }
    public List<BlockInfo> Blocks { get; } = [];
    public List<string> BlockTypeNames { get; } = [];
    public List<string> Strings { get; } = [];

    /// <summary>
    ///     True for NIFs older than 20.1.0.1 (Oblivion 20.0.0.x, Morrowind 4.0.0.2), whose header has
    ///     no global String table — object names and texture file names are stored inline as
    ///     <c>SizedString</c>s (uint32 length + ASCII) within each block, not as 4-byte indices into
    ///     <see cref="Strings" />. Render-path field-walkers must skip the inline name accordingly.
    /// </summary>
    public bool HasInlineStrings { get; set; }

    /// <summary>
    ///     Block index → captured NiObjectNET.Name, populated by the legacy measure pass for
    ///     <see cref="HasInlineStrings" /> NIFs (where names aren't in <see cref="Strings" />).
    /// </summary>
    public Dictionary<int, string> BlockNames { get; } = [];

    /// <summary>
    ///     True for Morrowind-era NetImmerse NIFs (4.0.0.2). These predate several NiObjectNET /
    ///     NiAVObject / NiGeometryData layout changes that the render field-walkers assume: a single
    ///     Extra Data ref (vs the count+list added at 10.0.1.0), an NiAVObject Velocity vector and
    ///     Has-Bounding-Volume flag (vs the Collision Object ref), 32-bit booleans, and no
    ///     NiGeometryData Group ID. Render readers branch on this to take the legacy paths.
    /// </summary>
    public bool IsMorrowind { get; set; }

    /// <summary>
    ///     Get the type name for a block by index.
    /// </summary>
    public string GetBlockTypeName(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= Blocks.Count)
        {
            return "Invalid";
        }

        return Blocks[blockIndex].TypeName;
    }
}
