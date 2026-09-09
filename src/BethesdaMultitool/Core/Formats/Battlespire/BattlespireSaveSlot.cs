using System.Text;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     A Battlespire save slot: the directory <c>SAVE0</c>..<c>SAVE9</c> under the INSTALL ROOT
///     (beside <c>GAMEDATA</c>, not inside it) holding <c>SAVETREE.DAT</c>, <c>SAVEVARS.DAT</c>,
///     <c>SAVENAME.DAT</c> and <c>IMAGE.RAW</c>. An unused slot holds none of them.
///     <para>
///         <c>SAVENAME.DAT</c> is 32 bytes: the name the player typed, NUL-terminated and NUL-padded
///         ("bd1" on the fixture, nothing after the terminator). It is the SAVE's name; the
///         character's name lives in the tree and SAVEVARS.
///     </para>
/// </summary>
internal sealed class BattlespireSaveSlot
{
    public const string TreeFileName = "SAVETREE.DAT";
    public const string VarsFileName = "SAVEVARS.DAT";
    public const string NameFileName = "SAVENAME.DAT";
    public const string ImageFileName = "IMAGE.RAW";

    /// <summary>Bytes in SAVENAME.DAT.</summary>
    public const int SaveNameLength = 32;

    private BattlespireSaveSlot(string directory, BattlespireSaveTree tree, BattlespireSaveVars? vars,
        BattlespireSaveImage? image, string? saveName)
    {
        Directory = directory;
        Tree = tree;
        Vars = vars;
        Image = image;
        SaveName = saveName;
    }

    /// <summary>The slot directory.</summary>
    public string Directory { get; }

    public BattlespireSaveTree Tree { get; }

    /// <summary>Null when SAVEVARS.DAT is absent.</summary>
    public BattlespireSaveVars? Vars { get; }

    /// <summary>Null when IMAGE.RAW is absent.</summary>
    public BattlespireSaveImage? Image { get; }

    /// <summary>The name the player gave the save; null when SAVENAME.DAT is absent.</summary>
    public string? SaveName { get; }

    /// <summary>True when the directory holds a SAVETREE.DAT.</summary>
    public static bool IsSaveSlot(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return File.Exists(Path.Combine(directory, TreeFileName));
    }

    /// <summary>
    ///     Loads the slot. SAVETREE.DAT is required (a <see cref="FileNotFoundException" /> otherwise);
    ///     the other three are optional and null when absent. Any file that does not tile throws
    ///     <see cref="InvalidDataException" />.
    /// </summary>
    public static BattlespireSaveSlot Load(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var treePath = Path.Combine(directory, TreeFileName);
        if (!File.Exists(treePath))
        {
            throw new FileNotFoundException($"{directory} holds no {TreeFileName}; it is not a Battlespire save slot.",
                treePath);
        }

        var tree = BattlespireSaveTree.Parse(File.ReadAllBytes(treePath), TreeFileName);

        var varsPath = Path.Combine(directory, VarsFileName);
        var vars = File.Exists(varsPath) ? BattlespireSaveVars.Parse(File.ReadAllBytes(varsPath), VarsFileName) : null;

        var imagePath = Path.Combine(directory, ImageFileName);
        var image = File.Exists(imagePath)
            ? BattlespireSaveImage.Parse(File.ReadAllBytes(imagePath), ImageFileName)
            : null;

        var namePath = Path.Combine(directory, NameFileName);
        var saveName = File.Exists(namePath) ? ReadSaveName(File.ReadAllBytes(namePath), NameFileName) : null;

        return new BattlespireSaveSlot(directory, tree, vars, image, saveName);
    }

    /// <summary>Reads SAVENAME.DAT: exactly 32 bytes, the name up to its first NUL.</summary>
    public static string ReadSaveName(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length != SaveNameLength)
        {
            throw new InvalidDataException(
                $"{name}: SAVENAME.DAT is {SaveNameLength} bytes, this one is {bytes.Length}.");
        }

        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }
}
