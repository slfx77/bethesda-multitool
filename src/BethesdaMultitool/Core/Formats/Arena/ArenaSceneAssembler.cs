// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/World/MapGeneration.cpp — writeVoxelInfoForFLOR / writeVoxelInfoForMAP1 /
//   writeVoxelInfoForMAP2 / writeVoxelInfoForCeiling (the FLOR/MAP1/MAP2 bit fields and which .INF
//   slot each voxel type textures with), src/World/ArenaLevelUtils.cpp getMap2VoxelHeight, and
//   src/World/ArenaMeshUtils.cpp (the raised/edge/diagonal/chasm box values). License texts are
//   collected centrally in THIRD_PARTY_LICENSES.

using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>What a MAP1 voxel id encodes, by its bit pattern.</summary>
internal enum ArenaVoxelKind
{
    Air,
    Wall,
    RaisedPlatform,
    Flat,
    TransparentWall,
    Edge,
    Door,
    Diagonal,
    Unknown
}

/// <summary>The three voxel planes of one level, in the row-major layout the files store.</summary>
/// <remarks>An absent plane is an empty array and reads as all-air.</remarks>
internal sealed record ArenaLevelPlanes(int Width, int Depth, ushort[] Floor, ushort[] Map1, ushort[] Map2)
{
    /// <summary>The planes of one <c>.MIF</c> level.</summary>
    public static ArenaLevelPlanes FromMifLevel(ArenaMifLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        return new ArenaLevelPlanes(level.Width, level.Depth, level.Floor, level.Map1, level.Map2);
    }

    /// <summary>The planes of one <c>.RMD</c> wilderness chunk.</summary>
    public static ArenaLevelPlanes FromRmd(ArenaRmdFile chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return new ArenaLevelPlanes(ArenaRmdFile.Width, ArenaRmdFile.Depth, chunk.Floor, chunk.Map1, chunk.Map2);
    }

    /// <summary>Voxel id at a coordinate of a plane, 0 outside the grid or when the plane is absent.</summary>
    public static ushort At(ushort[] plane, int width, int depth, int x, int z)
    {
        if (plane.Length == 0 || x < 0 || z < 0 || x >= width || z >= depth)
        {
            return 0;
        }

        var index = x + z * width;
        return index < plane.Length ? plane[index] : (ushort)0;
    }
}

/// <summary>What an assembly emitted and what it could not resolve.</summary>
internal sealed class ArenaSceneCensus
{
    public int Walls { get; set; }

    public int RaisedPlatforms { get; set; }

    public int TransparentWalls { get; set; }

    public int Edges { get; set; }

    public int Doors { get; set; }

    public int Diagonals { get; set; }

    /// <summary>MAP1 flats (nibble 0x8) — sprites, not geometry; counted, not built.</summary>
    public int Flats { get; set; }

    /// <summary>MAP1 nibbles 0xC/0xE/0xF, which the reference leaves unrendered.</summary>
    public int UnknownVoxels { get; set; }

    public int FloorQuads { get; set; }

    public int DryChasms { get; set; }

    public int WetChasms { get; set; }

    public int LavaChasms { get; set; }

    /// <summary>MAP2 voxels that became storeys.</summary>
    public int UpperStoreys { get; set; }

    /// <summary>Tallest MAP2 stack, in storeys.</summary>
    public int MaxStoreys { get; set; }

    /// <summary>
    ///     MAP2 voxels with a zero texture byte — 75,491 of them in the retail wilderness chunks,
    ///     all with a non-zero high byte. Their meaning is not established; they are skipped.
    /// </summary>
    public int Map2ZeroTexture { get; set; }

    /// <summary>Texture ids that wrapped modulo 64 or fell outside the .INF list.</summary>
    public int ClampedTextureIds { get; set; }

    /// <summary>Chasm floors whose .INF declares no matching <c>*…CHASM</c> texture.</summary>
    public int MissingChasmTextures { get; set; }

    /// <summary>Raised platforms whose <c>*BOXCAP</c>/<c>*BOXSIDE</c> id the .INF never declares.</summary>
    public int MissingPlatformTextures { get; set; }

    public bool CeilingEmitted { get; set; }

    public int Quads { get; set; }
}

/// <summary>What an assembly produced: one mesh, ready for the GLB exporter or the viewer.</summary>
internal sealed record ArenaSceneAssembly(
    XnGineTriangleMesh Mesh,
    IReadOnlyList<XnGineMeshInstance> Instances,
    ArenaSceneCensus Census,
    ArenaInfVoxelTextures Textures,
    ArenaMapKind Kind)
{
    /// <summary>The distinct texture slots the geometry references, in first-use order.</summary>
    public IEnumerable<int> UsedSlots => Mesh.SubMeshes
        .Select(s => s.TextureRecord)
        .Where(r => r != ArenaSceneAssembler.MissingTextureRecord)
        .Distinct();
}

/// <summary>
///     Turns one Arena level — its FLOR, MAP1 and MAP2 planes plus the .INF that names its
///     textures — into a textured voxel mesh.
///     <para>
///         <b>Frame.</b> One voxel is one unit. Geometry is built in the XnGine <b>Y-DOWN</b>
///         space every other classic assembler here uses, so the GLB exporter's Y flip and the
///         viewer adapter's rotation both apply unchanged. Read in the glTF Y-up frame that
///         produces: <b>+X is EAST, +Z is SOUTH, +Y is up</b>; row <c>r</c> spans
///         <c>z ∈ [r, r+1]</c> and column <c>c</c> spans <c>x ∈ [Width-1-c, Width-c]</c> — column 0
///         is the EAST edge. That is a right-handed compass frame, which is what keeps the level
///         from coming out mirrored.
///     </para>
///     <para>
///         ⚑ What is MEASURED about that frame (2026-09-08, 550 .MIF / 726 levels / 69 .RMD):
///         MHDR start points read as <c>(column, row) = (x/128, y/128)</c> land on a walkable
///         voxel 140 of 140 times and transposed only 62 of 140 (65 land inside walls); the
///         saved-game plane oracle on the board fixes the planes as row-major; a diagonal wall
///         with bit 0x100 CLEAR joins the (column-min,row-min) corner to the (column-max,row-max)
///         corner — 640 to 0 on the loose neighbour test and 322+318 to 0 on the strict one —
///         and bit SET joins the other two (652 to 2); and edge voxels with orientation 0/8 run
///         along columns while 4/C run along rows (526 of 541 single-axis cases), so their faces
///         lie on the row axis and column axis respectively.
///     </para>
///     <para>
///         ⚠ What is NOT measured here: which way is north. The reference states the original
///         game's frame as +x WEST, +z SOUTH (its <c>VoxelUtils</c>), and orientation 0 = north /
///         C = east for edges; the data above cannot separate north from south nor east from
///         west, only axis from axis. This assembler follows the reference. If it is wrong, the
///         level is mirrored end to end and nothing inside it looks wrong — exactly the failure
///         mode the frame comment exists to make visible.
///     </para>
///     <para>
///         <b>Heights.</b> A wall is <c>*CEILING/128</c> voxels tall (100/128 = 0.78 when the
///         .INF gives none). MAP2 storeys stack above that, each one wall-height, 1..4 of them by
///         the voxel's 0x80 / 0x8000 bits. ⚠ The reference's <c>getMap2VoxelHeight</c> tests 0x80
///         BEFORE the combined 0x8080, so its 4-storey branch is unreachable and a 0x8080 voxel
///         renders 2 high there; this assembler reads 0x8080 as 4, the reading the reference's own
///         comment describes and the only one under which the 1,647 retail voxels that set BOTH
///         bits mean something. Not settled by data — recorded as a choice.
///     </para>
/// </summary>
internal static class ArenaSceneAssembler
{
    /// <summary>Arena's positional unit: 128 per voxel.</summary>
    public const float ArenaUnitsPerVoxel = 128f;

    /// <summary>Texel size of every wall/floor texture the game ships.</summary>
    public const int TextureSize = 64;

    /// <summary>
    ///     Texture archive number the level's materials are keyed under. The record is the
    ///     .INF slot; the archive only keeps them apart from a real XnGine (archive, record).
    /// </summary>
    public const int TextureArchive = 0x0A;

    /// <summary>Record used for a face whose texture could not be resolved.</summary>
    public const int MissingTextureRecord = 0x7FFF;

    /// <summary>Most vertices one sub-mesh may hold: the viewer indexes with 16 bits.</summary>
    public const int MaxVerticesPerSubMesh = 65_532;

    /// <summary>The camera drop the reference applies to every swimmable chasm before the per-world delta.</summary>
    private const int ChasmSurfaceBase = -83;

    /// <summary>FLOR texture ids that are chasms rather than floors.</summary>
    private const int DryChasmId = 0xC;

    private const int WetChasmId = 0xD;
    private const int LavaChasmId = 0xE;

    /// <summary>Classifies a MAP1 voxel id.</summary>
    public static ArenaVoxelKind Classify(ushort map1)
    {
        if (map1 == 0)
        {
            return ArenaVoxelKind.Air;
        }

        if ((map1 & 0x8000) == 0)
        {
            var msb = (map1 & 0x7F00) >> 8;
            var lsb = map1 & 0x7F;
            return msb == lsb ? ArenaVoxelKind.Wall : ArenaVoxelKind.RaisedPlatform;
        }

        return (map1 >> 12) switch
        {
            0x8 => ArenaVoxelKind.Flat,
            0x9 => ArenaVoxelKind.TransparentWall,
            0xA => ArenaVoxelKind.Edge,
            0xB => ArenaVoxelKind.Door,
            0xD => ArenaVoxelKind.Diagonal,
            _ => ArenaVoxelKind.Unknown
        };
    }

    /// <summary>Storeys a MAP2 voxel stacks: 0x80 → 2, 0x8000 → 3, both → 4, neither → 1.</summary>
    public static int Map2Height(ushort map2)
    {
        var low = (map2 & 0x80) != 0;
        var high = (map2 & 0x8000) != 0;
        if (low && high)
        {
            return 4;
        }

        if (high)
        {
            return 3;
        }

        return low ? 2 : 1;
    }

    /// <summary>True when a MAP1 voxel blocks movement across the voxel, for door orientation.</summary>
    public static bool Blocks(ArenaVoxelKind kind)
    {
        return kind is ArenaVoxelKind.Wall or ArenaVoxelKind.RaisedPlatform or ArenaVoxelKind.Diagonal
            or ArenaVoxelKind.TransparentWall or ArenaVoxelKind.Door or ArenaVoxelKind.Edge;
    }

    /// <summary>Assembles one level.</summary>
    /// <param name="planes">The level's voxel planes.</param>
    /// <param name="textures">The .INF texture index the ids resolve through.</param>
    /// <param name="kind">Which world the level belongs to; picks platform tables and chasm depths.</param>
    /// <param name="name">Instance label.</param>
    /// <param name="platforms">Raised-platform tables; the retail executable's when null.</param>
    public static ArenaSceneAssembly Assemble(
        ArenaLevelPlanes planes,
        ArenaInfVoxelTextures textures,
        ArenaMapKind kind,
        string name,
        ArenaRaisedPlatformTable? platforms = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(name);

        var context = new Context(planes, textures, kind, platforms ?? ArenaRaisedPlatformTable.Retail);
        var census = context.Census;
        var w = planes.Width;
        var d = planes.Depth;

        var hasMap2 = planes.Map2.Any(v => v != 0);
        var ceiling = kind == ArenaMapKind.Interior && !textures.Ceiling.OutdoorDungeon && !hasMap2;

        for (var r = 0; r < d; r++)
        {
            for (var c = 0; c < w; c++)
            {
                var map1 = ArenaLevelPlanes.At(planes.Map1, w, d, c, r);
                var map1Kind = Classify(map1);

                EmitFloor(context, c, r, map1Kind);
                EmitMap1(context, c, r, map1, map1Kind);
                EmitMap2(context, c, r);

                if (ceiling && map1Kind != ArenaVoxelKind.Wall)
                {
                    var slot = textures.Ceiling.TextureIndex ?? 0;
                    context.Quad(slot, Face.Bottom, c, r, context.WallHeight, context.WallHeight);
                }
            }
        }

        census.CeilingEmitted = ceiling;

        var mesh = context.Build(w, d);
        var instances = new List<XnGineMeshInstance>();
        if (mesh.SubMeshes.Count > 0)
        {
            instances.Add(new XnGineMeshInstance(mesh, Matrix4x4.Identity, name));
        }

        return new ArenaSceneAssembly(mesh, instances, census, textures, kind);
    }

    private static void EmitFloor(Context ctx, int c, int r, ArenaVoxelKind map1Kind)
    {
        var floor = ArenaLevelPlanes.At(ctx.Planes.Floor, ctx.Planes.Width, ctx.Planes.Depth, c, r);
        var textureId = (floor & 0xFF00) >> 8;

        if (textureId is DryChasmId or WetChasmId or LavaChasmId)
        {
            EmitChasm(ctx, c, r, textureId);
            return;
        }

        if (map1Kind == ArenaVoxelKind.Wall)
        {
            // Hidden under a solid wall; the reference removes these internal faces too.
            return;
        }

        var slot = ctx.Slot(textureId);
        ctx.Quad(slot, Face.Top, c, r, 0f, 0f);
        ctx.Census.FloorQuads++;
    }

    private static void EmitChasm(Context ctx, int c, int r, int textureId)
    {
        int? slotIndex;
        float surface;
        switch (textureId)
        {
            case DryChasmId:
                slotIndex = ctx.Textures.DryChasmIndex;
                surface = -ctx.WallHeight;
                ctx.Census.DryChasms++;
                break;
            case WetChasmId:
                slotIndex = ctx.Textures.WetChasmIndex;
                surface = ChasmSurface(ctx.Kind);
                ctx.Census.WetChasms++;
                break;
            default:
                slotIndex = ctx.Textures.LavaChasmIndex;
                surface = ChasmSurface(ctx.Kind);
                ctx.Census.LavaChasms++;
                break;
        }

        if (slotIndex is null)
        {
            ctx.Census.MissingChasmTextures++;
        }

        var slot = slotIndex ?? MissingTextureRecord;
        ctx.Quad(slot, Face.Top, c, r, surface, surface);

        // The pit's walls stand where the neighbour is not itself a chasm, from the surface up to
        // the floor the player walks on.
        foreach (var face in Context.SideFaces)
        {
            var (nc, nr) = Neighbour(c, r, face);
            if (nc < 0 || nr < 0 || nc >= ctx.Planes.Width || nr >= ctx.Planes.Depth)
            {
                continue;
            }

            var neighbourFloor = ArenaLevelPlanes.At(ctx.Planes.Floor, ctx.Planes.Width, ctx.Planes.Depth, nc, nr);
            var neighbourId = (neighbourFloor & 0xFF00) >> 8;
            if (neighbourId is DryChasmId or WetChasmId or LavaChasmId)
            {
                continue;
            }

            // The wall faces INTO the pit: the pit voxel's own face on that side, normal flipped.
            ctx.Quad(slot, face, c, r, surface, 0f, inward: true);
        }
    }

    /// <summary>Water/lava surface below the floor, in voxels: the reference's -83 plus the per-world delta, over 128.</summary>
    private static float ChasmSurface(ArenaMapKind kind)
    {
        var delta = kind switch
        {
            ArenaMapKind.Interior => -25,
            ArenaMapKind.City => -50,
            _ => -10
        };
        return (ChasmSurfaceBase + delta) / ArenaUnitsPerVoxel;
    }

    private static void EmitMap1(Context ctx, int c, int r, ushort map1, ArenaVoxelKind kind)
    {
        var h = ctx.WallHeight;
        switch (kind)
        {
            case ArenaVoxelKind.Air:
                return;
            case ArenaVoxelKind.Flat:
                ctx.Census.Flats++;
                return;
            case ArenaVoxelKind.Unknown:
                ctx.Census.UnknownVoxels++;
                return;
            case ArenaVoxelKind.Wall:
            {
                ctx.Census.Walls++;
                var slot = ctx.Slot(((map1 & 0x7F00) >> 8) - 1);
                foreach (var face in Context.SideFaces)
                {
                    var (nc, nr) = Neighbour(c, r, face);
                    var neighbour = Classify(ArenaLevelPlanes.At(ctx.Planes.Map1, ctx.Planes.Width, ctx.Planes.Depth, nc, nr));
                    if (neighbour != ArenaVoxelKind.Wall)
                    {
                        ctx.Quad(slot, face, c, r, 0f, h);
                    }
                }

                var above = ArenaLevelPlanes.At(ctx.Planes.Map2, ctx.Planes.Width, ctx.Planes.Depth, c, r);
                if (above == 0 && !(ctx.Kind == ArenaMapKind.Interior && !ctx.Textures.Ceiling.OutdoorDungeon))
                {
                    ctx.Quad(slot, Face.Top, c, r, h, h);
                }

                return;
            }
            case ArenaVoxelKind.RaisedPlatform:
                EmitRaised(ctx, c, r, map1);
                return;
            case ArenaVoxelKind.TransparentWall:
            {
                ctx.Census.TransparentWalls++;
                var slot = ctx.Slot((map1 & 0xFF) - 1);
                foreach (var face in Context.SideFaces)
                {
                    ctx.Quad(slot, face, c, r, 0f, h);
                }

                return;
            }
            case ArenaVoxelKind.Edge:
                EmitEdge(ctx, c, r, map1);
                return;
            case ArenaVoxelKind.Door:
                EmitDoor(ctx, c, r, map1);
                return;
            case ArenaVoxelKind.Diagonal:
                EmitDiagonal(ctx, c, r, map1);
                return;
            default:
                return;
        }
    }

    private static void EmitRaised(Context ctx, int c, int r, ushort map1)
    {
        ctx.Census.RaisedPlatforms++;
        var msb = (map1 & 0x7F00) >> 8;
        var heightIndex = msb & 0x07;
        var thicknessIndex = (msb & 0x78) >> 3;
        var boxScale = ctx.Textures.Ceiling.BoxScale;

        var yOffset = ctx.Platforms.Height(ctx.Kind, heightIndex) / ArenaUnitsPerVoxel;
        var ySize = ctx.Platforms.Thickness(ctx.Kind, thicknessIndex, boxScale) / ArenaUnitsPerVoxel;
        var top = yOffset + ySize;

        var sideId = ctx.Textures.BoxSide(map1 & 0x000F);
        var capId = ctx.Textures.BoxCap((map1 & 0x00F0) >> 4);
        var underId = ctx.Textures.Ceiling.TextureIndex;
        if (sideId is null || capId is null)
        {
            ctx.Census.MissingPlatformTextures++;
        }

        var side = sideId ?? MissingTextureRecord;
        var cap = capId ?? MissingTextureRecord;
        var under = underId ?? 0;

        // The side texture shows the band of its 64 rows the platform's height covers, as the
        // reference computes it (normalised by the wall height).
        var scale = ctx.WallHeight;
        var vTop = MathF.Max(0f, 1f - yOffset / scale - ySize / scale);
        var vBottom = MathF.Min(vTop + ySize / scale, 1f);

        foreach (var face in Context.SideFaces)
        {
            var (nc, nr) = Neighbour(c, r, face);
            var neighbour = ArenaLevelPlanes.At(ctx.Planes.Map1, ctx.Planes.Width, ctx.Planes.Depth, nc, nr);
            if (neighbour == map1 || Classify(neighbour) == ArenaVoxelKind.Wall)
            {
                // An identical platform beside it hides this side; so does a solid wall.
                continue;
            }

            ctx.Quad(side, face, c, r, yOffset, top, vTop * TextureSize, vBottom * TextureSize);
        }

        ctx.Quad(cap, Face.Top, c, r, top, top);
        if (yOffset > 0f)
        {
            ctx.Quad(under, Face.Bottom, c, r, yOffset, yOffset);
        }
    }

    private static void EmitEdge(Context ctx, int c, int r, ushort map1)
    {
        ctx.Census.Edges++;
        var textureIndex = (map1 & 0x003F) - 1;
        if (textureIndex < 0)
        {
            textureIndex = 0;
        }

        var slot = ctx.Slot(textureIndex);
        var baseOffset = (map1 & 0x0E00) >> 9;
        var fullOffset = ctx.Kind == ArenaMapKind.Interior ? baseOffset * 8 : baseOffset * 32 - 8;
        var yOffset = fullOffset / ArenaUnitsPerVoxel;

        // Orientation (two bits above the texture index): 0 north, 4 west, 8 south, C east — the
        // reference's table; retail data fixes the axis (0/8 on the row axis, 4/C on the column
        // axis) but not which end of it.
        var face = ((map1 & 0x00C0) >> 4) switch
        {
            0x0 => Face.North,
            0x4 => Face.West,
            0x8 => Face.South,
            _ => Face.East
        };

        ctx.Quad(slot, face, c, r, yOffset, yOffset + ctx.WallHeight);
        ctx.Quad(slot, face, c, r, yOffset, yOffset + ctx.WallHeight, inward: true);
    }

    private static void EmitDoor(Context ctx, int c, int r, ushort map1)
    {
        ctx.Census.Doors++;
        var slot = ctx.Slot((map1 & 0x003F) - 1);

        // The plate lies in the plane of the wall run it closes: walls north and south mean the
        // run is along the row axis, so the plate spans z at mid-x. Retail: 2,172 of 2,495 doors
        // sit in such a run; the rest default to the row-axis plate.
        var n = Blocks(Classify(ArenaLevelPlanes.At(ctx.Planes.Map1, ctx.Planes.Width, ctx.Planes.Depth, c, r - 1)));
        var s = Blocks(Classify(ArenaLevelPlanes.At(ctx.Planes.Map1, ctx.Planes.Width, ctx.Planes.Depth, c, r + 1)));
        var e = Blocks(Classify(ArenaLevelPlanes.At(ctx.Planes.Map1, ctx.Planes.Width, ctx.Planes.Depth, c + 1, r)));
        var wst = Blocks(Classify(ArenaLevelPlanes.At(ctx.Planes.Map1, ctx.Planes.Width, ctx.Planes.Depth, c - 1, r)));
        var alongRows = !(e && wst && !(n && s));

        var h = ctx.WallHeight;
        if (alongRows)
        {
            ctx.DiagonalOrPlate(slot, c, r, 0.5f, 0f, 0.5f, 1f, 0f, h);
        }
        else
        {
            ctx.DiagonalOrPlate(slot, c, r, 0f, 0.5f, 1f, 0.5f, 0f, h);
        }
    }

    private static void EmitDiagonal(Context ctx, int c, int r, ushort map1)
    {
        ctx.Census.Diagonals++;
        var slot = ctx.Slot((map1 & 0x00FF) - 1);
        var h = ctx.WallHeight;

        // Bit 0x100 clear: from the (column-min, row-min) corner to the (column-max, row-max) one.
        // Measured 640:0 / 322+318:0 on retail neighbour walls (see the type remarks).
        if ((map1 & 0x0100) == 0)
        {
            ctx.DiagonalOrPlate(slot, c, r, 0f, 0f, 1f, 1f, 0f, h);
        }
        else
        {
            ctx.DiagonalOrPlate(slot, c, r, 1f, 0f, 0f, 1f, 0f, h);
        }
    }

    private static void EmitMap2(Context ctx, int c, int r)
    {
        var map2 = ArenaLevelPlanes.At(ctx.Planes.Map2, ctx.Planes.Width, ctx.Planes.Depth, c, r);
        if (map2 == 0)
        {
            return;
        }

        var textureIndex = (map2 & 0x007F) - 1;
        if (textureIndex < 0)
        {
            ctx.Census.Map2ZeroTexture++;
            return;
        }

        var slot = ctx.Slot(textureIndex);
        var storeys = Map2Height(map2);
        ctx.Census.UpperStoreys++;
        ctx.Census.MaxStoreys = Math.Max(ctx.Census.MaxStoreys, storeys);

        var h = ctx.WallHeight;
        for (var k = 0; k < storeys; k++)
        {
            var y0 = h * (k + 1);
            var y1 = h * (k + 2);
            foreach (var face in Context.SideFaces)
            {
                var (nc, nr) = Neighbour(c, r, face);
                var neighbour = ArenaLevelPlanes.At(ctx.Planes.Map2, ctx.Planes.Width, ctx.Planes.Depth, nc, nr);
                var neighbourStoreys = neighbour == 0 || (neighbour & 0x7F) == 0 ? 0 : Map2Height(neighbour);
                if (neighbourStoreys <= k)
                {
                    ctx.Quad(slot, face, c, r, y0, y1);
                }
            }
        }

        ctx.Quad(slot, Face.Top, c, r, h * (storeys + 1), h * (storeys + 1));
    }

    /// <summary>The neighbouring voxel across a side face, in index space.</summary>
    private static (int Column, int Row) Neighbour(int c, int r, Face face)
    {
        return face switch
        {
            Face.North => (c, r - 1),
            Face.South => (c, r + 1),
            // Columns increase WESTWARD (the reference's original-frame convention), so the west
            // neighbour is column + 1.
            Face.West => (c + 1, r),
            Face.East => (c - 1, r),
            _ => (c, r)
        };
    }

    /// <summary>Faces of a voxel box, named by compass direction in the glTF frame.</summary>
    private enum Face
    {
        North,
        South,
        East,
        West,
        Top,
        Bottom
    }

    /// <summary>Everything the per-voxel emitters share, plus the chunked mesh being built.</summary>
    private sealed class Context
    {
        public static readonly Face[] SideFaces = [Face.North, Face.South, Face.East, Face.West];

        private readonly Dictionary<int, ChunkList> _chunks = [];

        public Context(ArenaLevelPlanes planes, ArenaInfVoxelTextures textures, ArenaMapKind kind, ArenaRaisedPlatformTable platforms)
        {
            Planes = planes;
            Textures = textures;
            Kind = kind;
            Platforms = platforms;
            WallHeight = textures.Ceiling.Scale;
        }

        public ArenaLevelPlanes Planes { get; }

        public ArenaInfVoxelTextures Textures { get; }

        public ArenaMapKind Kind { get; }

        public ArenaRaisedPlatformTable Platforms { get; }

        public float WallHeight { get; }

        public ArenaSceneCensus Census { get; } = new();

        /// <summary>Resolves a texture id to a slot index, counting clamps.</summary>
        public int Slot(int textureId)
        {
            var slot = Textures.ResolveIndex(textureId, out var clamped);
            if (clamped)
            {
                Census.ClampedTextureIds++;
            }

            return slot;
        }

        /// <summary>
        ///     Emits one axis-aligned face of the voxel at (c, r). Side faces span
        ///     <paramref name="y0" />..<paramref name="y1" />; a top or bottom face sits at
        ///     <paramref name="y0" />. Texel v runs <paramref name="v0" />..<paramref name="v1" />
        ///     top to bottom on side faces. <paramref name="inward" /> flips the face to look into
        ///     the voxel instead of out of it.
        /// </summary>
        public void Quad(int slot, Face face, int c, int r, float y0, float y1, float v0 = 0f, float v1 = TextureSize, bool inward = false)
        {
            var x0 = Planes.Width - 1 - c;
            var x1 = Planes.Width - c;
            var z0 = (float)r;
            var z1 = r + 1f;
            const float t = TextureSize;

            // Vertices in the glTF Y-up frame, counter-clockwise seen from OUTSIDE, bottom edge
            // first and running to the viewer's right, so u reads left to right.
            Vector3 a, b, cc, dd, n;
            Vector2 ua, ub, uc, ud;
            switch (face)
            {
                case Face.East:
                    (a, b, cc, dd) = (new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1));
                    n = Vector3.UnitX;
                    (ua, ub, uc, ud) = (new Vector2(0, v1), new Vector2(t, v1), new Vector2(t, v0), new Vector2(0, v0));
                    break;
                case Face.West:
                    (a, b, cc, dd) = (new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0));
                    n = -Vector3.UnitX;
                    (ua, ub, uc, ud) = (new Vector2(0, v1), new Vector2(t, v1), new Vector2(t, v0), new Vector2(0, v0));
                    break;
                case Face.South:
                    (a, b, cc, dd) = (new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1));
                    n = Vector3.UnitZ;
                    (ua, ub, uc, ud) = (new Vector2(0, v1), new Vector2(t, v1), new Vector2(t, v0), new Vector2(0, v0));
                    break;
                case Face.North:
                    (a, b, cc, dd) = (new Vector3(x1, y0, z0), new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0));
                    n = -Vector3.UnitZ;
                    (ua, ub, uc, ud) = (new Vector2(0, v1), new Vector2(t, v1), new Vector2(t, v0), new Vector2(0, v0));
                    break;
                case Face.Top:
                    (a, b, cc, dd) = (new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), new Vector3(x0, y0, z0));
                    n = Vector3.UnitY;
                    (ua, ub, uc, ud) = (new Vector2(0, t), new Vector2(t, t), new Vector2(t, 0), new Vector2(0, 0));
                    break;
                default:
                    (a, b, cc, dd) = (new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1));
                    n = -Vector3.UnitY;
                    (ua, ub, uc, ud) = (new Vector2(0, 0), new Vector2(t, 0), new Vector2(t, t), new Vector2(0, t));
                    break;
            }

            if (inward)
            {
                // Reverse the winding and the normal; keep u running the same way on the texture.
                (a, b, cc, dd) = (b, a, dd, cc);
                (ua, ub, uc, ud) = (ub, ua, ud, uc);
                n = -n;
            }

            Emit(slot, a, b, cc, dd, n, ua, ub, uc, ud);
        }

        /// <summary>
        ///     Emits a vertical, double-sided plate inside the voxel at (c, r) from fractional
        ///     index-space point (<paramref name="ca" />, <paramref name="ra" />) to
        ///     (<paramref name="cb" />, <paramref name="rb" />), spanning y0..y1. Used for doors
        ///     and diagonal walls.
        /// </summary>
        public void DiagonalOrPlate(int slot, int c, int r, float ca, float ra, float cb, float rb, float y0, float y1)
        {
            // Index-space column fraction f maps to x = Width - c - f (columns run westward).
            var xa = Planes.Width - c - ca;
            var xb = Planes.Width - c - cb;
            var za = r + ra;
            var zb = r + rb;

            var p0 = new Vector3(xa, y0, za);
            var p1 = new Vector3(xb, y0, zb);
            var p2 = new Vector3(xb, y1, zb);
            var p3 = new Vector3(xa, y1, za);
            var along = Vector3.Normalize(p1 - p0);
            var normal = Vector3.Normalize(Vector3.Cross(along, Vector3.UnitY));
            const float t = TextureSize;

            Emit(slot, p0, p1, p2, p3, normal, new Vector2(0, t), new Vector2(t, t), new Vector2(t, 0), new Vector2(0, 0));
            Emit(slot, p1, p0, p3, p2, -normal, new Vector2(t, t), new Vector2(0, t), new Vector2(0, 0), new Vector2(t, 0));
        }

        /// <summary>
        ///     Appends a quad given in the glTF Y-up frame. Converted into XnGine Y-down space by
        ///     negating Y with the vertex order kept: the mirror flips the right-hand normal, and
        ///     the GLB exporter's own Y flip plus winding reversal undoes exactly that — the same
        ///     convention <c>Bs6FlatBillboard</c> follows.
        /// </summary>
        private void Emit(int slot, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            if (!_chunks.TryGetValue(slot, out var chunks))
            {
                chunks = new ChunkList();
                _chunks[slot] = chunks;
            }

            var flipped = new Vector3(n.X, -n.Y, n.Z);
            chunks.Add(
                new XnGineVertex(new Vector3(a.X, -a.Y, a.Z), flipped, ua),
                new XnGineVertex(new Vector3(b.X, -b.Y, b.Z), flipped, ub),
                new XnGineVertex(new Vector3(c.X, -c.Y, c.Z), flipped, uc),
                new XnGineVertex(new Vector3(d.X, -d.Y, d.Z), flipped, ud));
            Census.Quads++;
        }

        public XnGineTriangleMesh Build(int width, int depth)
        {
            var subMeshes = new List<XnGineSubMesh>();
            foreach (var (slot, chunks) in _chunks.OrderBy(kv => kv.Key))
            {
                var record = slot == MissingTextureRecord ? MissingTextureRecord : slot;
                subMeshes.AddRange(chunks.Finish(record));
            }

            var size = new Vector3(width, WallHeight * 5, depth);
            return new XnGineTriangleMesh(0, MathF.Sqrt(width * width + depth * depth) / 2f, size, subMeshes);
        }
    }

    /// <summary>Quads of one texture slot, rolled over into a new sub-mesh at the 16-bit index limit.</summary>
    private sealed class ChunkList
    {
        private readonly List<(List<XnGineVertex> Vertices, List<int> Indices)> _closed = [];
        private List<XnGineVertex> _vertices = new(4096);
        private List<int> _indices = new(6144);

        public void Add(XnGineVertex a, XnGineVertex b, XnGineVertex c, XnGineVertex d)
        {
            if (_vertices.Count + 4 > MaxVerticesPerSubMesh)
            {
                _closed.Add((_vertices, _indices));
                _vertices = new List<XnGineVertex>(4096);
                _indices = new List<int>(6144);
            }

            var baseIndex = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            _vertices.Add(d);
            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 1);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex + 3);
        }

        public IEnumerable<XnGineSubMesh> Finish(int record)
        {
            foreach (var (vertices, indices) in _closed)
            {
                yield return new XnGineSubMesh(TextureArchive, record, vertices, indices);
            }

            if (_vertices.Count > 0)
            {
                yield return new XnGineSubMesh(TextureArchive, record, _vertices, _indices);
            }
        }
    }
}
