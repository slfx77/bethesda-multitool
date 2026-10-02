using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>What an assembly run placed, and what it could not resolve.</summary>
internal sealed record RedguardSceneAssembly(
    IReadOnlyList<XnGineMeshInstance> Instances,
    int StaticsPlaced,
    int StaticsResolved,
    int PlacementsPlaced,
    int PlacementsResolved,
    IReadOnlyList<string> MissingNames)
{
    /// <summary>Statics and placements whose mesh could not be resolved.</summary>
    public int Missing => StaticsPlaced - StaticsResolved + PlacementsPlaced - PlacementsResolved;
}

/// <summary>What a flat assembly produced, and which texture references could not be sized.</summary>
internal sealed record RedguardFlatAssembly(
    IReadOnlyList<XnGineMeshInstance> Instances,
    int Placed,
    IReadOnlyList<string> MissingTextures);

/// <summary>
///     Places a Redguard map's geometry — the <c>MPSO</c> static meshes and the <c>MPOB</c> object
///     placements of one <c>maps\*.RGM</c> — into one scene, in the meshes' own Y-down world units.
///     <para>
///         <b>Every unit and convention here is measured, and each measurement names the control
///         that could have refuted it</b> (2026-09-08; scripts under the session scratch,
///         <c>census1..8.py</c>, all 27 retail maps):
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Position scale.</b> <c>MPOB</c> positions are world units × 256 (the low byte is
///             zero on 3,147 of 3,147); <c>MPSO</c>, <c>MPSF</c>, <c>MPSL</c>, <c>MPMK</c> and the
///             <c>WDNM</c> nav nodes are PLAIN world units — on every one of the 26 maps holding
///             both, the <c>MPSO</c> range equals the <c>MPOB/256</c> range and never the raw one
///             (ISLAND: statics x 16,403..54,400 against placements/256 2,858..57,774). The engine
///             agrees: <c>RG.EXE</c> shifts an object's stored position right by 8 wherever it needs
///             world units (<c>FUN_00024b79</c>) and inserts a static into its 512-unit grid by
///             <c>x &gt;&gt; 9</c> unshifted (<c>FUN_00093274</c>). ⚠ <see cref="RedguardRgmVector.WorldUnits" />
///             divides everything by 256 and is therefore right only for <c>MPOB</c>.
///         </item>
///         <item>
///             <b>Angle unit.</b> 2,048 per turn — not by analogy any more: the engine's own
///             placement code (<c>FUN_00082fc2</c>, disassembled from <c>RG.EXE</c>) masks each of
///             the three components with <c>0x7FF</c> before use, and its sine table at
///             <c>0x121600</c> is read for the cosine at +512 entries. Retail yaw values peak at
///             512/1024/1536/1792, and 618 stored components lie outside <c>[0, 2048)</c>
///             (negative nudges), so the mask is applied here exactly as the engine does.
///         </item>
///         <item>
///             <b>Axis assignment and order.</b> The same routine builds the object's 4.28 matrix
///             (row-major, <c>M[r][c]</c> at byte <c>4(3r + c)</c>) from an identity with three
///             helpers: about Y (<c>FUN_000b7ac0</c>) with component 1, then about X
///             (<c>FUN_000b7bd0</c>) with component 0, then about Z (<c>FUN_000b7ce0</c>) with
///             component 2, each angle NEGATED and masked first. So component 1 is the yaw (993 of
///             the 1,042 rotated retail placements use it), component 0 the pitch, component 2 the
///             roll.
///         </item>
///         <item>
///             <b>Signs, read from the helpers' instructions</b> (2026-09-28: two independent reads
///             of <c>RG.EXE</c> that agree on every instruction, and
///             <c>tools/scripts/redguard/placement_rotation.py</c>, which ports the helpers and checks
///             the port against the executable's own sine table). With <c>c</c> and <c>s</c> the
///             cosine and sine of the angle, each helper rewrites one pair of columns in every row r:
///             Y sets <c>M[r][0] = c M[r][0] - s M[r][2]</c> and <c>M[r][2] = c M[r][2] + s M[r][0]</c>;
///             X sets <c>M[r][1] = c M[r][1] + s M[r][2]</c> and <c>M[r][2] = c M[r][2] - s M[r][1]</c>;
///             Z sets <c>M[r][0] = c M[r][0] + s M[r][1]</c> and <c>M[r][1] = c M[r][1] - s M[r][0]</c>.
///             Rewriting columns is a right multiply, <c>M = M · R(a)</c>, and all three are the
///             standard right-handed rotation by +a: <c>Ry = [[c,0,s],[0,1,0],[-s,0,c]]</c>,
///             <c>Rx = [[1,0,0],[0,c,-s],[0,s,c]]</c>, <c>Rz = [[c,-s,0],[s,c,0],[0,0,1]]</c>. Y only
///             looks reversed in index order, because a positive turn about Y carries z onto x. So the
///             engine's matrix is <c>M = Ry(-y) · Rx(-x) · Rz(-z)</c>, and the object transform
///             (<c>0x34E686</c>, 52 of the 54 call sites that pass an object's matrix) applies it as
///             <c>world = M · p</c>, the same convention the statics settle below. ⚠ Until
///             2026-09-28 this class read the X and Z layouts transposed and built
///             <c>Ry(-y) · Rx(+x) · Rz(+z)</c>: the yaw was right, the pitch and roll were flipped.
///         </item>
///         <item>
///             <b>Controls on the retail placements</b>, each scored by how many transformed mesh
///             points lie within 6 units of a static face and how many mesh edges pierce one
///             (<c>tools/scripts/redguard/multiaxis_control.py</c>, float64 and float32). The 302
///             placements with a pitch or a roll score 753 near and 140 to 141 pierces under the
///             engine's matrix, 647 and 290 to 291 under the reading it replaced. The engine's is the
///             only one of the 48 axis orders and sign patterns to meet both thresholds
///             <c>RedguardSceneRetailTests</c> pins, and it pierces less than the replaced reading on 9
///             of the 16 maps holding such placements and more on none. The 713 yaw-only placements
///             on maps with statics score 3,354 near and 274 pierces, against 2,975 to 2,976 and 973
///             for the flipped yaw (on those, also the transpose, the row-vector reading); the
///             catacomb control pins the same split on one map.
///         </item>
///         <item>
///             <b>Static matrices.</b> <c>MPSO</c> stores nine 4.28 fixed-point ints, row-major,
///             det +1 on all 4,161. Control (<c>census4.py</c>) on the AFFECTED population — the
///             2,447 statics with a non-identity matrix: adjacent pieces snap to shared corners, so
///             count transformed vertices that coincide (≤ 2 units) with a vertex of a DIFFERENT
///             static. <c>world = M · p</c> scores 1,453 of 87,003; the transpose 578 — and the
///             column reading wins on every map where they separate (CATACOMB 619 vs 209, ISLAND
///             509 vs 209, OBSERVE 136 vs 30, EXTPALAC 79 vs 59, PALACE 64 vs 45) and never loses.
///             The nav-node-on-floor control (<c>census2.py</c>) agrees (CATACOMB 712 vs 659,
///             HIDEINT 227 vs 208, ties elsewhere) but is drawn from the wrong population — most
///             floors are flat — and is recorded only as concurring.
///         </item>
///     </list>
///     <para>
///         ⚠ <c>System.Numerics</c> is ROW-vector, so <c>world = M · p</c> becomes
///         <c>p · Mᵀ</c>: the 3x3 block of every matrix below is the transpose of the engine's,
///         and the placement rotation is the engine's product transposed term by term.
///     </para>
/// </summary>
internal static class RedguardSceneAssembler
{
    /// <summary>Angle units in a full turn — the engine masks every component with <see cref="AngleMask" />.</summary>
    public const float AngleUnitsPerTurn = 2048f;

    /// <summary>The mask <c>RG.EXE</c> applies to each stored rotation component.</summary>
    public const int AngleMask = 0x7FF;

    /// <summary>1.0 in the 4.28 fixed point the static matrices use.</summary>
    public const float MatrixOne = 268_435_456f;

    /// <summary>
    ///     Texture archive number reserved for the <c>MPSF</c> flats' billboards is NOT needed —
    ///     a Redguard flat names a real <c>3dart\TEXTURE.nnn</c> record, so its material is the same
    ///     (archive, record) pair a mesh plane carries and resolves through the same source.
    /// </summary>
    public const float FlatWorldUnitsPerPixel = 1f;

    /// <summary>
    ///     The rotation part of an <c>MPOB</c> placement, in the meshes' own Y-down space, built
    ///     exactly as <c>FUN_00082fc2</c> builds the object's matrix: each component masked to 11
    ///     bits and negated, applied about Y (component 1) first, then X (component 0), then Z
    ///     (component 2), and used as <c>world = M · p</c>. That is
    ///     <c>M = Ry(-y) · Rx(-x) · Rz(-z)</c> in the standard right-handed rotations the three
    ///     helpers apply (their layouts are in the class summary).
    /// </summary>
    public static Matrix4x4 PlacementRotation(RedguardRgmVector rotation)
    {
        const float scale = float.Tau / AngleUnitsPerTurn;
        var x = (rotation.X & AngleMask) * scale;
        var y = (rotation.Y & AngleMask) * scale;
        var z = (rotation.Z & AngleMask) * scale;

        // Engine (row-major, right-multiplied helpers, world = M·p): M = Ry(-y) · Rx(-x) · Rz(-z) with
        //   Ry(t) = [[c,0,s],[0,1,0],[-s,0,c]], Rx(t) = [[1,0,0],[0,c,-s],[0,s,c]], Rz(t) = [[c,-s,0],[s,c,0],[0,0,1]].
        // Row-vector form is the transpose, Rz(-z)ᵀ · Rx(-x)ᵀ · Ry(-y)ᵀ. System.Numerics lays its
        // rotations out as exactly those transposes, on every axis alike (R(t)ᵀ = CreateRotationA(t)), so
        // each angle keeps its negation. Y looks reversed against X and Z in index order, in the engine
        // and in System.Numerics both, and the two cancel.
        return Matrix4x4.CreateRotationZ(-z)
               * Matrix4x4.CreateRotationX(-x)
               * Matrix4x4.CreateRotationY(-y);
    }

    /// <summary>The full transform of an <c>MPOB</c> placement: its rotation, then its position in world units.</summary>
    public static Matrix4x4 PlacementTransform(RedguardRgmPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        const float scale = 1f / RedguardRgmFile.UnitsPerWorldUnit;
        return PlacementRotation(placement.Rotation)
               * Matrix4x4.CreateTranslation(
                   placement.Position.X * scale, placement.Position.Y * scale, placement.Position.Z * scale);
    }

    /// <summary>
    ///     The transform of an <c>MPSO</c> static: its stored 4.28 matrix applied as
    ///     <c>world = M · p</c>, then its position, which is already in world units.
    /// </summary>
    public static Matrix4x4 StaticTransform(RedguardRgmStaticMesh staticMesh)
    {
        ArgumentNullException.ThrowIfNull(staticMesh);

        var m = staticMesh.Rotation;
        if (m.Count != 9)
        {
            throw new InvalidDataException(
                $"Static '{staticMesh.MeshName}' carries {m.Count} matrix words, not 9.");
        }

        // Row-major engine matrix M[r][c] = m[3r + c]; the row-vector block is its transpose.
        return new Matrix4x4(
            m[0] / MatrixOne, m[3] / MatrixOne, m[6] / MatrixOne, 0f,
            m[1] / MatrixOne, m[4] / MatrixOne, m[7] / MatrixOne, 0f,
            m[2] / MatrixOne, m[5] / MatrixOne, m[8] / MatrixOne, 0f,
            staticMesh.Position.X, staticMesh.Position.Y, staticMesh.Position.Z, 1f);
    }

    /// <summary>
    ///     Places every static and every placement that names a mesh, resolving names through
    ///     <paramref name="resolve" />. Missing names are reported, never thrown on.
    /// </summary>
    public static RedguardSceneAssembly Assemble(RedguardRgmFile map, Func<string, XnGineTriangleMesh?> resolve)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(resolve);

        var instances = new List<XnGineMeshInstance>(map.StaticMeshes.Count + map.Placements.Count);
        var missing = new List<string>();
        var seenMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var staticsResolved = 0;
        var placementsPlaced = 0;
        var placementsResolved = 0;

        foreach (var staticMesh in map.StaticMeshes)
        {
            var mesh = resolve(staticMesh.MeshName);
            if (mesh is null)
            {
                if (seenMissing.Add(staticMesh.MeshName))
                {
                    missing.Add(staticMesh.MeshName);
                }

                continue;
            }

            staticsResolved++;
            instances.Add(new XnGineMeshInstance(
                mesh, StaticTransform(staticMesh),
                string.Create(CultureInfo.InvariantCulture, $"static_{staticMesh.Index}_{staticMesh.MeshName}")));
        }

        foreach (var placement in map.Placements)
        {
            if (!placement.HasMesh || placement.MeshStem.Length == 0)
            {
                continue;
            }

            placementsPlaced++;
            var mesh = resolve(placement.MeshStem);
            if (mesh is null)
            {
                if (seenMissing.Add(placement.MeshStem))
                {
                    missing.Add(placement.MeshStem);
                }

                continue;
            }

            placementsResolved++;
            instances.Add(new XnGineMeshInstance(
                mesh, PlacementTransform(placement),
                string.Create(CultureInfo.InvariantCulture,
                    $"object_{placement.Index}_{placement.ObjectName}_{placement.MeshStem}")));
        }

        return new RedguardSceneAssembly(
            instances, map.StaticMeshes.Count, staticsResolved, placementsPlaced, placementsResolved, missing);
    }

    /// <summary>
    ///     Places the map's <c>MPSF</c> flats — the fires, plants and splashes — as upright
    ///     billboards sized from their texture, at their world-unit position.
    ///     <para>
    ///         ⚠ One world unit per texel is a STATED ASSUMPTION carried over from the Battlespire
    ///         flats (<c>Bs6FlatBillboard</c>); nothing in the record or the texture declares the
    ///         scale, and a flat carries no size field at all.
    ///     </para>
    /// </summary>
    /// <param name="flats">The map's flats.</param>
    /// <param name="sizeOf">(archive, record) → pixel size, or null when the texture cannot be resolved.</param>
    public static RedguardFlatAssembly AssembleFlats(
        IReadOnlyList<RedguardRgmFlat> flats, Func<int, int, (int Width, int Height)?> sizeOf)
    {
        ArgumentNullException.ThrowIfNull(flats);
        ArgumentNullException.ThrowIfNull(sizeOf);

        var instances = new List<XnGineMeshInstance>(flats.Count);
        var missing = new List<string>();
        var seen = new HashSet<(int, int)>();
        var quads = new Dictionary<(int, int), XnGineTriangleMesh>();

        foreach (var flat in flats)
        {
            var key = (flat.TextureArchive, flat.TextureRecord);
            if (!quads.TryGetValue(key, out var quad))
            {
                var size = sizeOf(flat.TextureArchive, flat.TextureRecord);
                if (size is null || size.Value.Width <= 0 || size.Value.Height <= 0)
                {
                    if (seen.Add(key))
                    {
                        missing.Add(string.Create(CultureInfo.InvariantCulture,
                            $"TEXTURE.{flat.TextureArchive:D3}#{flat.TextureRecord}"));
                    }

                    continue;
                }

                quad = Billboard(flat.TextureArchive, flat.TextureRecord, size.Value.Width, size.Value.Height);
                quads[key] = quad;
            }

            instances.Add(new XnGineMeshInstance(
                quad,
                Matrix4x4.CreateTranslation(flat.Position.X, flat.Position.Y, flat.Position.Z),
                string.Create(CultureInfo.InvariantCulture,
                    $"flat_{flat.Index}_{flat.TextureArchive:D3}_{flat.TextureRecord}")));
        }

        return new RedguardFlatAssembly(instances, flats.Count, missing);
    }

    /// <summary>
    ///     An upright, double-sided quad standing on its base, textured by a real
    ///     <c>(archive, record)</c> pair. Same shape as the Battlespire billboard; the material key
    ///     differs because a Redguard flat's texture IS a mesh-style reference.
    /// </summary>
    private static XnGineTriangleMesh Billboard(int archive, int record, int pixelWidth, int pixelHeight)
    {
        var halfWidth = pixelWidth * FlatWorldUnitsPerPixel / 2f;
        var height = pixelHeight * FlatWorldUnitsPerPixel;

        var vertices = new List<XnGineVertex>(8)
        {
            new(new Vector3(-halfWidth, -height, 0), -Vector3.UnitZ, new Vector2(0, 0)),
            new(new Vector3(halfWidth, -height, 0), -Vector3.UnitZ, new Vector2(pixelWidth, 0)),
            new(new Vector3(halfWidth, 0, 0), -Vector3.UnitZ, new Vector2(pixelWidth, pixelHeight)),
            new(new Vector3(-halfWidth, 0, 0), -Vector3.UnitZ, new Vector2(0, pixelHeight))
        };
        for (var i = 0; i < 4; i++)
        {
            vertices.Add(new XnGineVertex(vertices[i].Position, Vector3.UnitZ, vertices[i].TexelUv));
        }

        int[] indices = [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6];
        var radius = MathF.Sqrt(halfWidth * halfWidth + height * height);
        return new XnGineTriangleMesh(
            0, radius, new Vector3(pixelWidth * FlatWorldUnitsPerPixel, height, 0),
            [new XnGineSubMesh(archive, record, vertices, indices)]);
    }
}
