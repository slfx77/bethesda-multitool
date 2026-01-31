namespace FalloutXbox360Utils.Core.Formats.Bsa;

/// <summary>
///     BSA file content type flags.
/// </summary>
[Flags]
public enum BsaFileFlags : ushort
{
    None = 0,
    Meshes = 0x0001,
    Textures = 0x0002,
    Menus = 0x0004,
    Sounds = 0x0008,
    Voices = 0x0010,
    Shaders = 0x0020,
    Trees = 0x0040,
    Fonts = 0x0080,
    Misc = 0x0100
}
