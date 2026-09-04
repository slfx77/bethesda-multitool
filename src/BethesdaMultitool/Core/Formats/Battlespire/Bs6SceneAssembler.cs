using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>What an assembly run resolved, and what it could not.</summary>
internal sealed record Bs6SceneAssembly(
    IReadOnlyList<XnGineMeshInstance> Instances,
    int Placed,
    int Resolved,
    IReadOnlyList<string> MissingNames)
{
    /// <summary>Placements whose mesh could not be resolved.</summary>
    public int Missing => Placed - Resolved;
}

/// <summary>
///     Places a Battlespire level's meshes into one scene. Each <c>OBJS/OBJD</c> names a mesh by
///     index into the level's <c>LFIL</c> list and carries a <c>POSI</c> and an <c>ANGS</c>.
///     <para>
///         <b>Units are measured, the rotation convention is not.</b> <c>POSI</c> is in world units,
///         the same units a mesh's points reach after the 1/256 divisor: on the retail <c>L8.BS6</c>
///         the placements span x −7,025..8,578 and z −11,586..4,418 while <c>7arch</c> alone is
///         212,992 native (832 world) units wide, so <c>POSI</c> cannot be native units. Its second
///         component is the vertical one — over every retail level its median span is 960 units
///         against 2,300 and 2,176 for the other two — and mesh space agrees (CHAIR.3D is tallest
///         in local Y, BOOK.3D thinnest). <c>ANGS</c> is in the same 2,048-per-turn units as
///         Daggerfall's RDB rotations (DaggerfallConnect's <c>RotationDivisor</c> is 2048/360);
///         retail values cluster on 0/512/1024/1536 with a maximum of 1,829, which fits and nothing
///         else does.
///     </para>
///     <para>
///         The rotation convention is the plain one — component 0 about X, 1 about Y, 2 about Z,
///         unnegated, applied Y then X then Z — and each part of that was established from the
///         retail data by what the angles are USED FOR (2026-09-04), not assumed:
///     </para>
///     <list type="bullet">
///         <item>
///             Component 0 is the pitch that stands flat-authored geometry up. Of the 1,132
///             placements of meshes authored lying flat in Y, 51% carry a +/-90 degree component 0
///             (466 at 1536, 107 at 512); of the 3,334 placements of meshes already standing tall
///             in Y, 72% carry none. <c>L8.BS6</c> shows the mechanism directly: its ring of
///             <c>7volc1..14</c> wall panels are 5,120 units long in Z and as little as 0 units
///             thick in Y, each placed at ANGS (512, 0, 0), and standing them up yields exactly the
///             level's 5,410-unit vertical extent.
///         </item>
///         <item>
///             Component 1 is the yaw: it is the most-used component overall (3,118 non-zero
///             placements) and the dominant one on already-upright meshes (1,286 against component
///             2's 657) — which is what a facing angle looks like. Component 2 is the roll, the
///             rarest at 1,279.
///         </item>
///         <item>
///             The angles are NOT negated: with the raw sign all 14 of <c>L8</c>'s ring panels face
///             inward, toward the arena the player fights in; negated, only 8 do.
///         </item>
///         <item>
///             Order matters for the 32% of placements that set two or more components. Applying Y
///             then X then Z stands up 88.7% of the flat, Z-long panels among them, the best of the
///             six orders, and it is also the order DaggerfallConnect documents for the sibling
///             engine's RDB models (MIT, <c>RDBLayout.GetModelMatrix</c>).
///         </item>
///     </list>
///     <para>
///         Three statistical probes tried first — marker burial, prop uprightness, prop
///         floor-contact clustering — each picked a different winner and are recorded here as dead
///         ends so they are not repeated. Burial is confounded (rotating an object changes the AABB
///         volume the score counts) and uprightness inverts the truth, scoring the legitimate
///         stand-up pitch as a prop being tipped over.
///     </para>
/// </summary>
internal static class Bs6SceneAssembler
{
    /// <summary>Angle units in a full turn, as Daggerfall's RDB rotations also use.</summary>
    public const float AngleUnitsPerTurn = 2048f;

    /// <summary>
    ///     The placement transform of one object, in the game's own Y-down space: the components
    ///     read as rotations about X, Y and Z, applied Y first, then X, then Z, and the translation
    ///     last. Same composition order DaggerfallConnect uses for the sibling engine's RDB models
    ///     (MIT; <c>RDBLayout.GetModelMatrix</c>), but with the raw angle signs — that reference
    ///     negates all three as part of its conversion into Unity's left-handed space, which is a
    ///     step this pipeline does separately when it flips Y for glTF.
    /// </summary>
    public static Matrix4x4 Placement(Bs6Vector position, Bs6Vector angles)
    {
        const float scale = float.Tau / AngleUnitsPerTurn;
        return Matrix4x4.CreateRotationY(angles.Y * scale)
               * Matrix4x4.CreateRotationX(angles.X * scale)
               * Matrix4x4.CreateRotationZ(angles.Z * scale)
               * Matrix4x4.CreateTranslation(position.X, position.Y, position.Z);
    }

    /// <summary>
    ///     Builds the scene. <paramref name="resolve" /> maps an <c>LFIL</c> name — extensionless in
    ///     every retail level, e.g. <c>7arch</c> — to a decomposed mesh, or null when the archive
    ///     holds no such record. Missing names are reported rather than thrown on: two retail
    ///     entries are not levels at all, and a level may list a mesh that ships loose.
    /// </summary>
    public static Bs6SceneAssembly Assemble(Bs6File level, Func<string, XnGineTriangleMesh?> resolve)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(resolve);

        var instances = new List<XnGineMeshInstance>(level.Objects.Count);
        var missing = new List<string>();
        var seenMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var placed = 0;

        foreach (var placement in level.Objects)
        {
            if (placement.MeshIndex < 0 || placement.MeshIndex >= level.MeshNames.Count)
            {
                continue;
            }

            placed++;
            var name = level.MeshNames[placement.MeshIndex];
            var mesh = resolve(name);
            if (mesh is null)
            {
                if (seenMissing.Add(name))
                {
                    missing.Add(name);
                }

                continue;
            }

            instances.Add(new XnGineMeshInstance(
                mesh,
                Placement(placement.Position, placement.Angles),
                $"{name}_{placement.Id}"));
        }

        return new Bs6SceneAssembly(instances, placed, instances.Count, missing);
    }
}
