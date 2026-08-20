namespace BethesdaMultitool.Core.Formats.Nif.Parser;

/// <summary>
///     Authored <c>NiBillboardNode.BillboardMode</c>. Values are the on-disk NetImmerse/Gamebryo
///     constants; Bethesda's value 9 is a second rotate-about-up spelling used by older assets.
/// </summary>
public enum NifBillboardMode : ushort
{
    AlwaysFaceCamera = 0,
    RotateAboutUp = 1,
    RigidFaceCamera = 2,
    AlwaysFaceCenter = 3,
    RigidFaceCenter = 4,
    BsRotateAboutUp = 5,
    RotateAboutUp2 = 9
}
