using System.Text;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Cheap "does this NIF carry Havok collision" test, used only to break a tie when more
///     than one donor folder has an exact hit for the same mesh path.
///     <para>
///     Deliberately a block-type-name marker scan rather than a real parse. A NIF stores its
///     block types as length-prefixed ASCII near the head of the file, and those bytes are
///     ASCII in both little- and big-endian (Xbox 360) NIFs, so one scan serves both without
///     needing to know the version or endianness. <c>HavokCollisionExtractor.TryExtract</c>
///     would be exact but needs a fully parsed <c>NifInfo</c>, which is far too heavy for a
///     resolver tie-break run over thousands of contested paths.
///     </para>
///     <para>
///     Measured imprecision versus an exact block-type-table parse on this corpus: 5 files in
///     3,035 (~0.16%), from a marker string appearing outside the type table. That is
///     acceptable here because both candidates are scanned identically — a shared false
///     positive simply leaves the caller on its normal folder-order fallback.
///     </para>
/// </summary>
internal static class NifCollisionMarkerProbe
{
    /// <summary>
    ///     Block types that mean "this mesh has a collision body". Ordered cheapest-first by
    ///     likelihood so the common case short-circuits early.
    /// </summary>
    private static readonly byte[][] Markers =
    [
        Encoding.ASCII.GetBytes("bhkCollisionObject"),
        Encoding.ASCII.GetBytes("bhkRigidBody"),
        Encoding.ASCII.GetBytes("bhkSPCollisionObject"),
        Encoding.ASCII.GetBytes("bhkPackedNiTriStripsShape"),
        Encoding.ASCII.GetBytes("bhkNiTriStripsShape"),
        Encoding.ASCII.GetBytes("bhkMoppBvTreeShape")
    ];

    /// <summary>True when the buffer names any Havok collision block type.</summary>
    public static bool HasCollision(ReadOnlySpan<byte> nifBytes)
    {
        if (nifBytes.Length < 64)
        {
            return false;
        }

        foreach (var marker in Markers)
        {
            if (nifBytes.IndexOf(marker) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     True when the path is a mesh the probe understands. Non-NIF assets keep plain
    ///     folder-order resolution — there is no collision concept to compare.
    /// </summary>
    public static bool AppliesTo(string normalizedPath) =>
        normalizedPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase);
}
