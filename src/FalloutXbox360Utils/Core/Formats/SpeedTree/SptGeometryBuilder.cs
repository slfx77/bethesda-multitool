using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;

namespace FalloutXbox360Utils.Core.Formats.SpeedTree;

/// <summary>
///     Generates renderable geometry from a parsed <see cref="SptModel" /> by reimplementing the
///     SpeedTree RT SDK's <c>CIdvBranch::Compute</c> loft, recovered from the Xbox 360 MemDebug PE
///     decompile (the MemDebug PDB carries full <c>CSpeedTreeRT</c> symbols). Unlike the earlier
///     generic-fractal generator, this is DATA-DRIVEN by the <c>.spt</c>'s nine per-branch BezierSplines
///     and scalars (see <c>speedtree-compute-algorithm</c> memory):
///     <list type="bullet">
///         <item>slot 4 = branch LENGTH, slot 5 = base RADIUS (both ×treeSize), slot 6 = radius taper.</item>
///         <item>slot 7 = declination off the parent (degrees); slot 1 = per-ring curl gain; slot 8 = roll.</item>
///         <item>slot 0 = angular noise; slots 2/3 = gnarl.</item>
///         <item>UInt6009+1 = ring count, UInt6008+1 = vertices/ring.</item>
///         <item>The four <c>.spt</c> branch records are per-LEVEL templates indexed by recursion depth;
///               the deepest level spawns leaf cards (buds) instead of child branches.</item>
///     </list>
///     The whole tree is finally rescaled to the TREE record's OBND height (the SDK works in arbitrary
///     internal units; only the ratios matter). Exact SDK rotation primitives / RNG are not reproduced
///     bit-for-bit — the result is visually faithful and tuned via <see cref="SptGeometryOptions" />.
/// </summary>
internal static class SptGeometryBuilder
{
    private const float Deg2Rad = MathF.PI / 180f;

    public static NifRenderableModel Build(SptModel model, uint seed, SptGeometryOptions? options = null)
    {
        var opt = options ?? SptGeometryOptions.FromEnvironment();
        var rng = new SptRandom(seed);
        Func<float, float, float> rand = rng.Range;

        var treeSize = ComputeTreeSize(model.General, opt, rng);

        var barkPath = SpeedTreeTexturePath.ToGamePath(model.General.BarkTexturePath);
        var barkNormalPath = DeriveNormalMap(barkPath);

        var bark = new MeshBuffer();
        var leafGroups = new Dictionary<string, MeshBuffer>(StringComparer.OrdinalIgnoreCase);
        var leafAnchors = new List<LeafAnchor>();

        var levels = Math.Min(model.Branches.Count, Math.Max(1, opt.MaxLevels));
        if (model.Branches.Count > 0)
        {
            // Root trunk: level 0, base at origin, growing +Z (FNV world-up). The root has no parent,
            // so its own declination (slot 7) is not applied — it grows up; children take their angle
            // off the parent. parentRadius starts large so the trunk's own radius is not clamped.
            GenerateBranch(model, 0, levels, 0f, Vector3.Zero, Vector3.UnitZ,
                parentLength: 0f, parentRadius: float.MaxValue, treeSize, opt, rng, rand, bark, leafAnchors);
        }

        // Leaf cards are sized relative to the BUILT skeleton's height (the SDK's absolute units are
        // arbitrary and rescaled later), so collect anchors during the loft and emit them now.
        EmitLeaves(leafAnchors, treeSize, opt, rng, leafGroups);

        var submeshes = new List<RenderableSubmesh>(4);
        if (bark.VertexCount > 0)
        {
            submeshes.Add(bark.ToSubmesh("spt:bark", barkPath, barkNormalPath, doubleSided: false, leaf: false, opt));
        }

        foreach (var (path, buffer) in leafGroups)
        {
            if (buffer.VertexCount == 0)
            {
                continue;
            }

            var leafPath = path.StartsWith("spt:", StringComparison.Ordinal) ? null : path;
            submeshes.Add(buffer.ToSubmesh("spt:leaves", leafPath, normalMap: null, doubleSided: true, leaf: true, opt));
        }

        ApplyHeightScale(submeshes, opt);

        var result = new NifRenderableModel();
        foreach (var sub in submeshes)
        {
            result.Submeshes.Add(sub);
            result.ExpandBounds(sub.Positions);
        }

        return result;
    }

    /// <summary>Master scale = random(Float2006 − Float2007, Float2006 + Float2007), seeded; the final
    /// tree is rescaled to OBND afterwards so this only sets internal proportions.</summary>
    private static float ComputeTreeSize(SptGeneralParams general, SptGeometryOptions opt, SptRandom rng)
    {
        var mid = general.Float2006;
        var spread = MathF.Abs(general.Float2007);
        var size = mid > 1e-3f ? rng.Range(mid - spread, mid + spread) : opt.TrunkHeight;
        return MathF.Max(1f, size);
    }

    // ---- Branch loft (port of CIdvBranch::Compute) --------------------------------------

    private static void GenerateBranch(
        SptModel model, int level, int levels, float parentT, Vector3 basePos, Vector3 baseDir,
        float parentLength, float parentRadius, float treeSize, SptGeometryOptions opt, SptRandom rng,
        Func<float, float, float> rand, MeshBuffer bark, List<LeafAnchor> leafAnchors)
    {
        if (level >= levels || level >= model.Branches.Count)
        {
            return;
        }

        var branch = model.Branches[level];
        var s = branch.Splines;

        // slot 4 = length, slot 5 = radius (both normalized, ×treeSize). slot 6 = radius taper curve.
        var length = MathF.Max(0.01f, Eval(s, 4, parentT, rand) * treeSize);
        var radius = MathF.Max(0.005f, Eval(s, 5, parentT, rand) * treeSize);
        if (parentLength > 0f && length > parentLength * opt.ChildLengthClamp)
        {
            length = parentLength * opt.ChildLengthClamp;
        }

        if (radius > parentRadius * 0.85f)
        {
            radius = parentRadius * 0.85f;
        }

        var numRings = Math.Clamp((int)branch.UInt6009 + 1, 2, 48);
        var vertsPerRing = Math.Clamp((int)branch.UInt6008 + 1, 3, 24);

        // Initial direction: the trunk (level 0) grows along baseDir; children take a declination
        // (slot 7, in DEGREES) off the parent at a random azimuth.
        var segDir = baseDir;
        if (level > 0)
        {
            var declDeg = Eval(s, 7, parentT, rand);
            var (right, up) = BuildFrame(baseDir);
            segDir = RotateAwayFromAxis(baseDir, right, up, MathF.Abs(declDeg) * Deg2Rad, rng.Range(0f, MathF.Tau));
        }
        else if (opt.TrunkLeanDeg > 0f)
        {
            var (right, up) = BuildFrame(baseDir);
            segDir = RotateAwayFromAxis(baseDir, right, up, opt.TrunkLeanDeg * Deg2Rad, rng.Range(0f, MathF.Tau));
        }

        // A stable bending axis for this branch (perpendicular to the start direction), so per-ring
        // curl produces a smooth arc rather than random wander.
        var (bendRight, bendUp) = BuildFrame(segDir);

        var segLen = length / (numRings - 1);
        var pos = basePos;
        int[]? prevRing = null;
        var rings = new List<(Vector3 Pos, Vector3 Dir)>(numRings);

        for (var r = 0; r < numRings; r++)
        {
            var t = r / (float)(numRings - 1);

            if (r > 0)
            {
                // Per-ring curl: bend gain from slot 1, roll plane from slot 8, jitter from slot 0.
                var gain = Eval(s, 1, t, rand);
                var rollT = s[8] is { } r8 ? r8.Curve(t) : 0.5f;
                var rollAngle = (rollT - 0.5f) * 2f * MathF.PI;
                var noiseDeg = s[0]?.ScaledVariance(t, rand) ?? 0f;

                var bendAngle = gain * opt.CurlStrengthRad + noiseDeg * Deg2Rad * opt.NoiseScale;
                var axis = bendRight * MathF.Cos(rollAngle) + bendUp * MathF.Sin(rollAngle);
                segDir = SafeNormalize(RotateAroundAxis(segDir, axis, bendAngle), segDir);

                // Gravity sag (the SDK's per-ring declination-weight term): pull the direction toward −Z
                // a little each segment so branches arch outward-and-down into a mound rather than growing
                // straight. Scaled by depth so deeper twigs droop more. This is what turns the upright
                // form into the low wide bush the billboard shows.
                if (opt.GravityStrength > 0f)
                {
                    var pull = opt.GravityStrength * (0.4f + 0.6f * level / MathF.Max(1, levels - 1));
                    segDir = SafeNormalize(new Vector3(segDir.X, segDir.Y, segDir.Z - pull * t), segDir);
                }
            }

            // Ring radius = base radius × taper curve (slot 6), normalized so the base ring is full width.
            var taper = s[6] is { } s6 ? MathF.Max(0f, s6.Curve(t)) : 1f - t;
            var taper0 = s[6] is { } s6b ? MathF.Max(1e-3f, s6b.Curve(0f)) : 1f;
            var ringRadius = MathF.Max(radius * opt.MinRingRadiusFraction, radius * (taper / taper0));

            prevRing = EmitRing(bark, pos, segDir, ringRadius, vertsPerRing, t, prevRing);
            rings.Add((pos, segDir));

            pos += segDir * segLen;
        }

        if (level + 1 < levels && level + 1 < model.Branches.Count)
        {
            SpawnChildren(model, level, levels, branch, rings, length, radius, treeSize, opt, rng, rand, bark,
                leafAnchors);
        }
        else
        {
            PlaceLeaves(model, rings, opt, rng, leafAnchors);
        }
    }

    private static void SpawnChildren(
        SptModel model, int level, int levels, SptBranch branch, List<(Vector3 Pos, Vector3 Dir)> rings,
        float length, float radius, float treeSize, SptGeometryOptions opt, SptRandom rng,
        Func<float, float, float> rand, MeshBuffer bark, List<LeafAnchor> leafAnchors)
    {
        // Child count scales with the branch record's Float6012 (CIdvBranch::Compute derives the count
        // from this "frequency" scalar; the exact treeSize/radius coupling is uncertain, so we calibrate
        // off Float6012 directly). A floor guarantees non-terminal levels branch enough to reach the
        // leaf-bearing terminal level.
        var raw = branch.Float6012 * opt.ChildFreqScale * opt.ChildDensity;
        var childCount = Math.Clamp(
            Math.Max((int)MathF.Round(raw), branch.Float6012 > 0f ? opt.MinChildrenPerBranch : 0),
            0, opt.MaxChildrenPerBranch);
        var start = branch.Float6010;
        var end = branch.Float6011;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        for (var c = 0; c < childCount; c++)
        {
            var frac = Math.Clamp(rng.Range(start, end), 0f, 1f);
            var (cpos, cdir) = SampleAlong(rings, frac);
            GenerateBranch(model, level + 1, levels, frac, cpos, cdir, length, radius, treeSize, opt, rng, rand,
                bark, leafAnchors);
        }
    }

    // ---- Leaves (port of ComputeBud / MakeLeaf) -----------------------------------------

    private static void PlaceLeaves(
        SptModel model, List<(Vector3 Pos, Vector3 Dir)> rings, SptGeometryOptions opt,
        SptRandom rng, List<LeafAnchor> leafAnchors)
    {
        if (model.Leaves.Count == 0 || opt.LeavesPerRing <= 0 || rings.Count == 0)
        {
            return;
        }

        for (var i = 0; i < rings.Count; i++)
        {
            // Bias leaves toward the branch tip (more foliage at the ends).
            var t = i / (float)Math.Max(1, rings.Count - 1);
            if (t < opt.LeafStartFraction)
            {
                continue;
            }

            var (pos, dir) = rings[i];
            for (var k = 0; k < opt.LeavesPerRing; k++)
            {
                var template = model.Leaves[rng.NextInt(model.Leaves.Count)];
                leafAnchors.Add(new LeafAnchor(pos, dir, template));
            }
        }
    }

    /// <summary>
    ///     Emit the collected leaf-card anchors, sized as a fraction of the built tree's height (so leaf
    ///     scale tracks the tree regardless of the SDK's arbitrary internal units), preserving each
    ///     template's atlas-UV rect (Corner0 ± Corner1) and c2 aspect ratio.
    /// </summary>
    private static void EmitLeaves(
        List<LeafAnchor> anchors, float treeSize, SptGeometryOptions opt, SptRandom rng,
        Dictionary<string, MeshBuffer> leafGroups)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        // Leaf card size tracks the tree's master scale (≈ constant built units), so a short shrub gets
        // proportionally large foliage and a tall pine gets small needle clusters — both natural.
        var half = MathF.Max(0.25f, treeSize * opt.LeafSizeFraction * opt.LeafSizeScale);

        foreach (var anchor in anchors)
        {
            var template = anchor.Template;
            var atlasPath = SpeedTreeTexturePath.ToGamePath(template.Material) ?? "spt:leaf";
            if (!leafGroups.TryGetValue(atlasPath, out var buffer))
            {
                buffer = new MeshBuffer();
                leafGroups[atlasPath] = buffer;
            }

            var quads = opt.CrossedLeafCards ? 2 : 1;
            if (!buffer.CanAdd(4 * quads))
            {
                continue;
            }

            var aspect = ComputeAspect(template.Corner2);
            var hx = half * aspect.X;
            var hy = half * aspect.Y;
            var uvMin = new Vector2(template.Corner0.X - template.Corner1.X, template.Corner0.Y - template.Corner1.Y);
            var uvMax = new Vector2(template.Corner0.X + template.Corner1.X, template.Corner0.Y + template.Corner1.Y);

            // Bud: step out from the branch tilted by BudAngle at a random roll (ComputeBud); card center
            // at the bud tip.
            var (bRight, bUp) = BuildFrame(anchor.Dir);
            var budDir = SafeNormalize(
                RotateAwayFromAxis(anchor.Dir, bRight, bUp, opt.BudAngleDeg * Deg2Rad, rng.Range(0f, MathF.Tau)),
                anchor.Dir);
            var center = anchor.Pos + budDir * (half * opt.BudReach);

            if (opt.LeafFaceDirection is { } face)
            {
                // Camera-facing billboard stand-in: one quad whose normal faces the given direction, so a
                // still shows the leaf cluster full-on (what the engine's per-card leaf billboards produce).
                var faceDir = SafeNormalize(face, Vector3.UnitZ);
                var (fRight, fUp) = BuildFrame(faceDir);
                AddLeafCard(buffer, center, fRight * hx, fUp * hy, faceDir, uvMin, uvMax);
                continue;
            }

            // Static fallback: crossed pair gives volume from any angle (the engine re-faces leaves to
            // camera in-shader; a static mesh can't, so two perpendicular cards substitute).
            var (right, up) = BuildFrame(budDir);
            AddLeafCard(buffer, center, right * hx, up * hy, budDir, uvMin, uvMax);
            if (opt.CrossedLeafCards)
            {
                AddLeafCard(buffer, center, budDir * hx, up * hy, right, uvMin, uvMax);
            }
        }
    }


    // ---- Geometry emission --------------------------------------------------------------

    private static int[]? EmitRing(
        MeshBuffer bark, Vector3 center, Vector3 dir, float radius, int radial, float v, int[]? prevRing)
    {
        if (!bark.CanAdd(radial + 1))
        {
            return prevRing;
        }

        var (right, up) = BuildFrame(dir);
        var ring = new int[radial + 1];
        for (var k = 0; k <= radial; k++)
        {
            var a = MathF.Tau * (k % radial) / radial;
            var normal = SafeNormalize(right * MathF.Cos(a) + up * MathF.Sin(a), right);
            var vertex = center + normal * radius;
            ring[k] = bark.Add(vertex, normal, new Vector2(k / (float)radial, v * 2f));
        }

        if (prevRing is not null)
        {
            for (var k = 0; k < radial; k++)
            {
                bark.Quad(prevRing[k], prevRing[k + 1], ring[k + 1], ring[k]);
            }
        }

        return ring;
    }

    private static (Vector3 Pos, Vector3 Dir) SampleAlong(List<(Vector3 Pos, Vector3 Dir)> rings, float frac)
    {
        if (rings.Count == 0)
        {
            return (Vector3.Zero, Vector3.UnitZ);
        }

        var f = Math.Clamp(frac, 0f, 1f) * (rings.Count - 1);
        var i = Math.Clamp((int)f, 0, rings.Count - 1);
        return rings[i];
    }

    private static void AddLeafCard(
        MeshBuffer buf, Vector3 center, Vector3 halfRight, Vector3 halfUp, Vector3 normal, Vector2 uvMin, Vector2 uvMax)
    {
        var v00 = buf.Add(center - halfRight - halfUp, normal, new Vector2(uvMin.X, uvMax.Y));
        var v10 = buf.Add(center + halfRight - halfUp, normal, new Vector2(uvMax.X, uvMax.Y));
        var v11 = buf.Add(center + halfRight + halfUp, normal, new Vector2(uvMax.X, uvMin.Y));
        var v01 = buf.Add(center - halfRight + halfUp, normal, new Vector2(uvMin.X, uvMin.Y));
        buf.Triangle(v00, v10, v11);
        buf.Triangle(v00, v11, v01);
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static float Eval(IReadOnlyList<SptBezierSpline?> splines, int slot, float param,
        Func<float, float, float> rand) =>
        slot < splines.Count && splines[slot] is { } spline ? spline.Evaluate(param, rand) : 0f;

    private static void ApplyHeightScale(List<RenderableSubmesh> submeshes, SptGeometryOptions opt)
    {
        var minZ = float.MaxValue;
        var maxZ = float.MinValue;
        foreach (var sub in submeshes)
        {
            for (var i = 2; i < sub.Positions.Length; i += 3)
            {
                var z = sub.Positions[i];
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }
        }

        var height = maxZ - minZ;
        if (height <= 1e-3f)
        {
            return;
        }

        var desired = (opt.TargetHeight is { } target && target > 0f ? target : height) * opt.HeightScale;
        var scale = desired / height;
        if (MathF.Abs(scale - 1f) < 1e-3f)
        {
            return;
        }

        foreach (var sub in submeshes)
        {
            var p = sub.Positions;
            for (var i = 0; i < p.Length; i++)
            {
                p[i] *= scale;
            }
        }
    }

    private static Vector2 ComputeAspect(Vector3 c2)
    {
        var x = MathF.Abs(c2.X);
        var y = MathF.Abs(c2.Y);
        if (x < 1e-3f || y < 1e-3f)
        {
            return Vector2.One;
        }

        var m = MathF.Max(x, y);
        return new Vector2(x / m, y / m);
    }

    private static (Vector3 Right, Vector3 Up) BuildFrame(Vector3 dir)
    {
        dir = SafeNormalize(dir, Vector3.UnitZ);
        var reference = MathF.Abs(dir.Z) > 0.99f ? Vector3.UnitX : Vector3.UnitZ;
        var right = SafeNormalize(Vector3.Cross(reference, dir), Vector3.UnitX);
        var up = Vector3.Cross(dir, right);
        return (right, up);
    }

    private static Vector3 RotateAwayFromAxis(Vector3 axis, Vector3 right, Vector3 up, float declination, float azimuth)
    {
        axis = SafeNormalize(axis, Vector3.UnitZ);
        var radial = right * MathF.Cos(azimuth) + up * MathF.Sin(azimuth);
        var dir = axis * MathF.Cos(declination) + radial * MathF.Sin(declination);
        return SafeNormalize(dir, axis);
    }

    private static Vector3 RotateAroundAxis(Vector3 v, Vector3 axis, float angle)
    {
        axis = SafeNormalize(axis, Vector3.UnitZ);
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        // Rodrigues' rotation.
        return v * cos + Vector3.Cross(axis, v) * sin + axis * Vector3.Dot(axis, v) * (1f - cos);
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        var lenSq = v.LengthSquared();
        return lenSq > 1e-10f ? v / MathF.Sqrt(lenSq) : fallback;
    }

    private static string? DeriveNormalMap(string? barkPath)
    {
        if (string.IsNullOrEmpty(barkPath))
        {
            return null;
        }

        var dot = barkPath.LastIndexOf('.');
        return dot < 0 ? null : barkPath[..dot] + "_n" + barkPath[dot..];
    }

    /// <summary>A pending leaf card: where on a terminal branch it attaches and which template it uses.</summary>
    private readonly record struct LeafAnchor(Vector3 Pos, Vector3 Dir, SptLeaf Template);

    /// <summary>Deterministic xorshift32 RNG with a uniform <see cref="Range" />, seeded by the tree's
    /// SNAM/Token2005 so a given tree is stable across builds.</summary>
    private sealed class SptRandom(uint seed)
    {
        private uint _state = seed == 0 ? 0x9E3779B9u : seed;

        public float NextFloat()
        {
            var x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return (x & 0xFFFFFF) / (float)0x1000000;
        }

        public float Range(float min, float max) => min + NextFloat() * (max - min);

        public int NextInt(int exclusiveMax) => exclusiveMax <= 0 ? 0 : (int)(NextFloat() * exclusiveMax) % exclusiveMax;
    }

    /// <summary>Growing vertex/index accumulator with a ushort index guard.</summary>
    private sealed class MeshBuffer
    {
        private const int MaxVertices = 60000;
        private readonly List<float> _positions = [];
        private readonly List<float> _normals = [];
        private readonly List<float> _uvs = [];
        private readonly List<ushort> _indices = [];

        public int VertexCount => _positions.Count / 3;

        public bool CanAdd(int verts) => VertexCount + verts <= MaxVertices;

        public int Add(Vector3 position, Vector3 normal, Vector2 uv)
        {
            var index = VertexCount;
            _positions.Add(position.X);
            _positions.Add(position.Y);
            _positions.Add(position.Z);
            _normals.Add(normal.X);
            _normals.Add(normal.Y);
            _normals.Add(normal.Z);
            _uvs.Add(uv.X);
            _uvs.Add(uv.Y);
            return index;
        }

        public void Triangle(int a, int b, int c)
        {
            _indices.Add((ushort)a);
            _indices.Add((ushort)b);
            _indices.Add((ushort)c);
        }

        public void Quad(int a, int b, int c, int d)
        {
            Triangle(a, b, c);
            Triangle(a, c, d);
        }

        public RenderableSubmesh ToSubmesh(
            string name, string? diffuse, string? normalMap, bool doubleSided, bool leaf, SptGeometryOptions opt)
        {
            return new RenderableSubmesh
            {
                ShapeName = name,
                Positions = [.. _positions],
                Triangles = [.. _indices],
                Normals = [.. _normals],
                UVs = [.. _uvs],
                DiffuseTexturePath = diffuse,
                NormalMapTexturePath = normalMap,
                IsDoubleSided = doubleSided,
                HasAlphaTest = leaf,
                AlphaTestThreshold = leaf ? opt.LeafAlphaThreshold : (byte)128,
            };
        }
    }
}
