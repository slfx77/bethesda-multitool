using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

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
///     The whole tree may be finally rescaled to the TREE record's OBND height by the host. Some rotation
///     and RNG details are still approximations unless explicitly backed by nearby decompile comments.
/// </summary>
internal static class SptGeometryBuilder
{
    private const float Deg2Rad = MathF.PI / 180f;

    /// <summary>Bud declination off the branch direction before a leaf card is placed — a hard 60°
    /// constant in the engine (CIdvBranch::ComputeBud, fStack_234 = 60.0).</summary>
    private const float LeafBudDeclinationDeg = 60f;

    /// <summary>Default <c>RoomForLeaf</c> spacing factor when the <c>.spt</c> leaf table omits token 3007
    /// — the engine's <c>SIdvLeafInfo</c> ctor default (<c>+0x20 = 0.5</c>). The actual per-tree value is
    /// token 3007 (<see cref="SptLeafTable.Float3007" />), e.g. pine 0.35 / whiteoak 0.45 / shrub 0.31.</summary>
    private const float DefaultLeafSpacingFactor = 0.5f;

    /// <summary>Default blossom threshold from <c>SIdvLeafInfo</c> ctor (<c>+0x24 = 0.75</c>), overridden
    /// by token 3000. <c>CIdvBranch::IsBlossom</c> only considers blossoms when branch-param exceeds it.</summary>
    private const float DefaultBlossomThreshold = 0.75f;

    /// <summary>Default blossom probability from <c>SIdvLeafInfo</c> ctor (<c>+0x28 = 0.8</c>), overridden
    /// by token 3002.</summary>
    private const float DefaultBlossomProbability = 0.8f;

    /// <summary>Per-ring slot-0 angular-noise damping applied to the TRUNK (recursion level 0) only. The
    /// engine accumulates the slot-0 ScaledVariance per ring (verified vs CIdvBranch::Compute L2285-2291),
    /// which is fine for branches but, on a tree whose slot-0 curve is non-zero at the base (e.g. Oblivion
    /// JapaneseMaple's constant ~0.5 curve → ±5°/ring from the ground up), random-walks the main stem into
    /// a strange lean. Since our RNG is not the engine's exact sequence (visual fidelity, not bit-exact), we
    /// damp the trunk's noise so the stem stays upright; branches keep full character, and trees whose trunk
    /// slot-0 curve already ramps from 0 (e.g. whiteoak) are ~unaffected at the base.</summary>
    private const float TrunkNoiseDamping = 0.3f;

    public static NifRenderableModel Build(SptModel model, uint seed, SptGeometryOptions? options = null)
    {
        var opt = options ?? SptGeometryOptions.FromEnvironment();
        var rng = new SptRandom(seed);

        var treeSize = ComputeTreeSize(model.General, opt, rng);
        // CTreeEngine::Compute reseeds after the randomized tree-size draw and before recursive branch
        // generation, so branch/leaf layout depends on SNAM but not on the size-variance sample.
        rng.Reseed(seed);
        Func<float, float, float> rand = rng.Range;

        var barkPath = SpeedTreeTexturePath.ToGamePath(model.General.BarkTexturePath, SpeedTreeTextureKind.Bark);
        var barkNormalPath = DeriveNormalMap(barkPath);

        var bark = new MeshBuffer();
        var leafGroups = new Dictionary<string, MeshBuffer>(StringComparer.OrdinalIgnoreCase);
        var leafAnchors = new List<LeafAnchor>();
        var collectedBranches = new List<CollectedBranch>();

        var levels = Math.Min(model.Branches.Count, Math.Max(1, opt.MaxLevels));
        if (model.Branches.Count > 0)
        {
            // Root trunk: the binary starts from an identity 3x3 frame and still applies the root's
            // slot-7 two-angle rotation; shipped trees use slot7=-90 so the local +X growth vector maps
            // to FNV world-up (+Z). parentRadius starts large so the trunk's own radius is not clamped.
            GenerateBranch(model, 0, levels, 0f, Vector3.Zero, BranchFrame.Identity,
                parentRadius: float.MaxValue, treeSize, opt, rng, rand, collectedBranches, leafAnchors, seed);
        }

        // The engine never renders the raw skeleton: CTreeEngine::Compute lofts every branch, then
        // BuildBranchLods decimates them into the rendered LOD mesh. Reproduce LOD0 here — keep the
        // highest-"volume" branches until their cumulative volume reaches the .spt's near fraction.
        EmitDecimatedBranches(collectedBranches, model.Lod, bark);

        // Leaf cards are sized relative to the BUILT skeleton's height (the SDK's absolute units are
        // arbitrary and rescaled later), so collect anchors during the loft and emit them now. The
        // RoomForLeaf spacing factor and placement mode are data-driven from the .spt leaf table
        // (token 3007 / 3008, SIdvLeafInfo::Parse), defaulting to the engine ctor's 0.5 / mode 2.
        var leafSpacing = model.LeafTable?.Float3007 ?? DefaultLeafSpacingFactor;
        var placementMode = model.LeafTable?.UInt3008 ?? 2u;
        // Leaf-card size uses the tree-size MID (Float2006), NOT the per-instance random draw `treeSize`.
        // CTreeEngine::Compute sets each leaf template's card size = CTreeEngine[+0x4c] (the non-random size
        // mid, token 2006) * leafTemplate[+0x3c] (=Corner1), while branches use random(mid +/- spread). The
        // runtime RoomForLeaf trace proved this: card = mid*Corner1 EXACTLY (treeSize-independent), and using
        // the random draw made the spacing ~mid/draw too small -> the canopy over-accepted (62 vs the engine's
        // 39 for WastelandShrub). The absolute scale washes out in the later OBND rescale, so only the
        // mid-vs-draw distinction matters for leaf density.
        var treeSizeMid = model.General.Float2006 > 1e-3f ? model.General.Float2006 : opt.TrunkHeight;
        EmitLeaves(leafAnchors, treeSizeMid, leafSpacing, placementMode, opt, rng, leafGroups);

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
            submeshes.Add(buffer.ToSubmesh("spt:leaves", leafPath, normalMap: null, doubleSided: true, leaf: true, opt,
                leafBillboard: opt.LeafBillboard));
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
        SptModel model, int level, int levels, float parentT, Vector3 basePos, BranchFrame parentFrame,
        float parentRadius, float treeSize, SptGeometryOptions opt, SptRandom rng,
        Func<float, float, float> rand, List<CollectedBranch> collected, List<LeafAnchor> leafAnchors,
        uint branchSeed)
    {
        if (level >= levels || level >= model.Branches.Count)
        {
            return;
        }

        var branch = model.Branches[level];
        var s = branch.Splines;

        // slot 4 = length, slot 5 = radius (both ×treeSize). slot 6 = radius taper curve. The engine has
        // NO child-length clamp — limbs are MEANT to be far longer than the (stubby) trunk (a cottonwood
        // trunk ≈ 0.04·treeSize but its level-1 limbs ≈ 0.45·treeSize). Clamping them is what collapsed the
        // canopy into a ball, so only the radius is clamped. Xbox CIdvBranch::Compute passes the sampled
        // parent ring radius at the child attachment, then clamps children to ≤ 0.85× that radius.
        var length = MathF.Max(0.01f, Eval(s, 4, parentT, rand) * treeSize);
        var radius = MathF.Max(0.005f, Eval(s, 5, parentT, rand) * treeSize);
        if (radius > parentRadius * 0.85f)
        {
            radius = parentRadius * 0.85f;
        }

        var numRings = Math.Clamp((int)branch.UInt6009 + 1, 2, 48);
        var vertsPerRing = Math.Clamp((int)branch.UInt6008 + 1, 3, 24);

        // Initial frame: CIdvBranch::Compute copies the parent ring matrix, applies a random spin through
        // SetVecFromParent, then applies RotateTwoAngles(slot7 + slot0Variance, slot0Variance). The local
        // +X column is the branch direction (NormalizeExtract(..., m_vBranchVector, matrix)).
        var frame = parentFrame;
        var spinDeg = rng.Range(-180f, 180f); // consumed by the binary before the initial two-angle rotate
        if (level > 0)
        {
            frame = frame.MultiplyLocal(BranchFrame.FromAxisAngle(Vector3.UnitX, spinDeg * Deg2Rad));
        }

        if (level == 0)
        {
            // The engine's root trunk grows straight +Z: apply ONLY slot-7's MEAN (the −90° that maps the
            // local +X growth vector to world-up), with NO declination variance and NO base slot-0 noise.
            // Otherwise a tree whose slot-7 carries variance (e.g. JapaneseMaple slot7 VAR=7) and/or whose
            // slot-0 noise curve is non-zero at the base (JapaneseMaple slot0 is a constant 1 → ±10° even at
            // param 0) tilts the WHOLE trunk ~15-20° ("angled strangely"). Per-ring slot-0 noise (in the
            // ring loop below) still adds natural wiggle up the trunk; only the base tilt is removed.
            var rootAngle = s.Count > 7 && s[7] is { } s7 ? s7.Evaluate(parentT, null) : 0f;
            frame = frame.MultiplyLocal(BranchFrame.RotateTwoAngles(rootAngle, 0f));
        }
        else
        {
            var initialNoiseB = ScaledVariance(s, 0, parentT, rand);
            var initialNoiseA = ScaledVariance(s, 0, parentT, rand);
            frame = frame.MultiplyLocal(BranchFrame.RotateTwoAngles(Eval(s, 7, parentT, rand) + initialNoiseA,
                initialNoiseB));
        }

        // slot 1 = the branch's bend gain, evaluated ONCE at its parent-t (CIdvBranch::Compute L964).
        // No scale factor: the engine applies this verbatim (decompile L1105/L1204).
        var slot1Gain = Eval(s, 1, parentT, rand);

        // Damp the trunk's per-ring angular noise so the main stem stays upright (see TrunkNoiseDamping);
        // branches (level > 0) keep full character.
        var noiseScale = level == 0 ? TrunkNoiseDamping : 1f;

        var pos = basePos;
        var rings = new List<BranchRing>(numRings);
        var previousDistance = 0f;
        var previousFrame = frame;

        for (var r = 0; r < numRings; r++)
        {
            var t = r / (float)(numRings - 1);
            var pathT = r == 0 ? 0f : MathF.Pow(t, branch.Float6014);
            var pathDistance = pathT * length;
            if (r == 0)
            {
                frame = ApplyGravity(frame, s, 0f, slot1Gain, rand);
            }
            else
            {
                var delta = pathDistance - previousDistance;
                pos += previousFrame.Direction * delta;
                frame = ApplyGravity(previousFrame, s, pathT, slot1Gain, rand);
                frame = frame.MultiplyLocal(BranchFrame.RotateTwoAngles(
                    ScaledVariance(s, 0, pathT, rand) * noiseScale,
                    ScaledVariance(s, 0, pathT, rand) * noiseScale));
            }

            previousDistance = pathDistance;

            // Ring radius = base radius × slot-6 taper, exactly as the engine does (ring field [6] =
            // slot5·treeSize·Eval(slot6,t), decompile L1175). No fraction floor — the engine lets the
            // taper reach 0 at the tip; only a tiny absolute epsilon guards against degenerate rings.
            var taper = s[6] is { } s6 ? MathF.Max(0f, s6.Evaluate(pathT, rand)) : 1f - pathT;
            var ringRadius = MathF.Max(0.01f, radius * taper);

            // Defer emission: the branch is collected as a unit so BuildBranchLods-style decimation can
            // drop it whole before any vertices exist (the engine lofts THEN decimates).
            rings.Add(new BranchRing(pos, frame, pathDistance, ringRadius, pathT));
            previousFrame = frame;
        }

        // Branch "volume" weight = CIdvBranch::ComputeVolume (360 0x82975E80): Σ segLen·(rᵢ+rᵢ₊₁) over the
        // ring centers — the importance metric BuildBranchLods ranks branches by when decimating.
        var importance = 0f;
        for (var i = 0; i < rings.Count - 1; i++)
        {
            importance += Vector3.Distance(rings[i].Pos, rings[i + 1].Pos) * (rings[i].Radius + rings[i + 1].Radius);
        }

        collected.Add(new CollectedBranch(rings, vertsPerRing, importance));

        // A branch at level L spawns children at L+1. The LAST .spt branch record is the leaf-stub
        // TEMPLATE — never lofted as a tube; when a branch's child level would be that terminal record,
        // its "children" are leaves (buds), not branches (CIdvBranch::Compute L1308 terminal check uses
        // the CHILD level, L1397 ComputeBud). So leaves attach to the second-deepest level (e.g. the
        // whiteoak's twig level, Float6012=600), NOT the deepest, and record 3 is purely the leaf source.
        var terminalRecord = model.Branches.Count - 1;
        if (level + 1 < terminalRecord && level + 1 < levels)
        {
            SpawnChildren(model, level, levels, branch, rings, length, treeSize, opt, rng, rand, collected,
                leafAnchors, branchSeed);
        }
        else
        {
            SpawnLeaves(branch, rings, length, treeSize, model, rng, leafAnchors);
        }
    }

    private static void SpawnChildren(
        SptModel model, int level, int levels, SptBranch branch, List<BranchRing> rings,
        float length, float treeSize, SptGeometryOptions opt, SptRandom rng,
        Func<float, float, float> rand, List<CollectedBranch> collected, List<LeafAnchor> leafAnchors,
        uint branchSeed)
    {
        // Child count = (Float6012 / treeSize) · length  (CIdvBranch::Compute L1305; = Float6012·Eval(slot4)).
        // No density multiplier — the engine has none. The cap only bounds the vertex budget (the engine's
        // true count can reach ~45 on dense canopy levels); it is a perf safety, not a shape parameter.
        var raw = treeSize > 0f ? branch.Float6012 * length / treeSize : 0f;
        var childCount = TruncateSpawnCount(raw);
        var start = branch.Float6010;
        var end = branch.Float6011;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        var childSeed = branchSeed;
        for (var c = 0; c < childCount; c++)
        {
            // CIdvBranch::Compute does not run one global RNG stream through all descendants. For each
            // non-terminal child it increments the branch-local seed by 3, reseeds to pick the attachment
            // point, then reseeds to the same child seed and burns one uniform before recursive Compute.
            childSeed = unchecked(childSeed + 3u);
            rng.Reseed(childSeed);

            var min = start;
            var max = end;
            if (c == 0)
            {
                min = start + (end - start) * 0.85f;
                max = start + (end - start) * 0.95f;
            }

            var frac = Math.Clamp(rng.Range(min, max), 0f, 1f);
            var spawnT = SpawnTemplateParam(frac, start, end);
            var (cpos, cframe, parentAttachRadius) = SampleAlong(rings, frac);
            rng.Reseed(childSeed);
            _ = rng.Range(0f, 100f);

            GenerateBranch(model, level + 1, levels, spawnT, cpos, cframe, parentAttachRadius, treeSize, opt, rng, rand,
                collected, leafAnchors, childSeed);
        }
    }

    // ---- Leaves (port of ComputeBud / MakeLeaf) -----------------------------------------

    private static void SpawnLeaves(
        SptBranch branch, List<BranchRing> rings, float length, float treeSize,
        SptModel model, SptRandom rng, List<LeafAnchor> leafAnchors)
    {
        if (model.Leaves.Count == 0 || rings.Count == 0)
        {
            return;
        }

        // Leaf count = (Float6012/treeSize)·length — the SAME spawn formula as child branches
        // (CIdvBranch::Compute L1305); at the terminal child level those spawns are buds (one leaf each,
        // ComputeBud) instead of branches. The cap is a vertex-budget safety, not a shape parameter.
        var raw = treeSize > 0f ? branch.Float6012 * length / treeSize : 0f;
        var count = TruncateSpawnCount(raw);
        var start = branch.Float6010;
        var end = branch.Float6011;
        if (end < start)
        {
            (start, end) = (end, start);
        }

        // The bud's own length comes from the terminal leaf-stub record's length spline (slot 4 = the
        // ComputeBud spline at struct +0x60), evaluated at the bud's position — a SMALL twig step, not the
        // leaf-card size. Offsetting leaves by the card size (≈20 units) instead sprayed them into balls
        // far off the branch ("clustered, no tree visible"); the real reach hugs the bough.
        var terminalBranch = model.Branches[^1];
        var budSplines = terminalBranch.Splines;
        var budReachDivisor = MathF.Max(1f, terminalBranch.UInt6009);

        // Partition templates into leaves (Type 0) and blossoms (Type 1). MakeLeaf keeps the pools separate:
        // IsBlossom first gates the current bud by token 3000/3002, then the accepted pool selects a template.
        // Blossoms replace that bud; they are not seeded as an extra second population.
        List<int>? leafTemplates = null;
        List<int>? blossomTemplates = null;
        for (var t = 0; t < model.Leaves.Count; t++)
        {
            if (model.Leaves[t].Type == 1)
            {
                (blossomTemplates ??= []).Add(t);
            }
            else
            {
                (leafTemplates ??= []).Add(t);
            }
        }

        // A tree with only blossom records (no Type-0) falls back to treating them as leaves so it still
        // renders rather than vanishing.
        if (leafTemplates is null)
        {
            leafTemplates = new List<int>(model.Leaves.Count);
            for (var t = 0; t < model.Leaves.Count; t++)
            {
                leafTemplates.Add(t);
            }
        }

        void PlaceBud(int templateIndex, float frac, float spawnT)
        {
            var (pos, frame, _) = SampleAlong(rings, frac);
            var template = model.Leaves[templateIndex];
            var textureCoords = templateIndex < model.LeafTextureCoords.Count
                ? model.LeafTextureCoords[templateIndex]
                : SptLeafTextureCoords.FullAtlas;
            var budReach = MathF.Max(0.01f, Eval(budSplines, 4, spawnT, rng.Range) * treeSize / budReachDivisor);
            leafAnchors.Add(new LeafAnchor(pos, frame, template, textureCoords, budReach));
        }

        for (var i = 0; i < count; i++)
        {
            var frac = Math.Clamp(rng.Range(start, end), 0f, 1f);
            var spawnT = SpawnTemplateParam(frac, start, end);
            var useBlossom = blossomTemplates is { Count: > 0 } && ShouldUseBlossom(spawnT, model, rng);
            var pool = useBlossom ? blossomTemplates! : leafTemplates;
            PlaceBud(pool[rng.NextInt(pool.Count)], frac, spawnT);
        }
    }

    private static bool ShouldUseBlossom(float branchParam, SptModel model, SptRandom rng)
    {
        var threshold = model.LeafSize > 0f ? model.LeafSize : DefaultBlossomThreshold;
        if (branchParam <= threshold)
        {
            return false;
        }

        var probability = model.LeafTable?.Float3002 ?? DefaultBlossomProbability;
        probability = Math.Clamp(probability, 0f, 1f);
        return probability >= 1f || rng.Range(0f, 1f) <= probability;
    }

    /// <summary>
    ///     Emit the collected leaf-card anchors with the engine's <c>RoomForLeaf</c> overlap rejection: a
    ///     candidate is skipped if any already-placed leaf lies within an axis-aligned cube around it, so
    ///     the canopy fills to "as many leaves as fit" and reveals the branch structure instead of burying
    ///     it under every <c>Float6012·length/treeSize</c> candidate. Card size = cardScale · Corner1, split
    ///     around the leaf's Corner0 pivot, where cardScale is the tree-size MID (token 2006), not the random
    ///     per-instance draw — see the call site / CTreeEngine::Compute leaf-template size overwrite.
    /// </summary>
    private static void EmitLeaves(
        List<LeafAnchor> anchors, float cardScale, float spacingFactor, uint placementMode,
        SptGeometryOptions opt, SptRandom rng, Dictionary<string, MeshBuffer> leafGroups)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        // RoomForLeaf rejects against the engine's SINGLE leaf-manager array (CTreeEngine+0x84+0x10 =
        // pcRam832ae8b0 in CIdvBranch::MakeLeaf/RoomForLeaf) — one GLOBAL list across the whole tree, NOT a
        // per-branch list. The earlier per-branch (PlacementScope) bucketing let leaves from different
        // branches cluster freely and over-spawned the canopy ~8x (404 cards vs the engine's 53 for
        // WastelandShrub, whose runtime cards sit ~12u apart in a 90u canopy = exactly this spacing
        // applied globally). Any non-zero placement mode rejects against this one global list; mode 0
        // disables rejection (engine SIdvLeafInfo+0xc == 0).
        var globalPlaced = placementMode != 0 ? new List<Vector3>(anchors.Count) : null;

        // Per-leaf wind weight (the SFVFLeafVertex blend factor, recovered from STLEAF*.vso / STB*.vso:
        // windedPos = lerp(pos, WindMatrix·pos, windWeight)). The SDK derives it from leaf exposure; we
        // approximate with normalized height up the canopy (low leaves barely move, the crown sways most)
        // plus a floor so the whole crown stirs. A fraction, so it survives ApplyHeightScale; carried in
        // the leaf-billboard card's bitangent.z and consumed by reference_instanced.vert.hlsl.
        var zMin = float.MaxValue;
        var zMax = float.MinValue;
        foreach (var a in anchors)
        {
            if (a.Pos.Z < zMin) zMin = a.Pos.Z;
            if (a.Pos.Z > zMax) zMax = a.Pos.Z;
        }

        var zRange = MathF.Max(1e-3f, zMax - zMin);

        // Place one leaf/blossom card; returns true if a card was actually emitted (not rejected by
        // RoomForLeaf or a full buffer).
        bool TryPlace(LeafAnchor anchor)
        {
            var template = anchor.Template;
            var windWeight = 0.15f + 0.85f * Math.Clamp((anchor.Pos.Z - zMin) / zRange, 0f, 1f);

            // Card width/height = cardScale · Corner1, from CSpeedTreeRT::Compute (compute decompile L3679):
            // leaf[+0x48/+0x4c] = CTreeEngine[+0x4c](size mid) · leaf[+0x3c/+0x40](=Corner1). cardScale is the
            // non-random size MID (token 2006), NOT the per-instance random draw — runtime-verified by the
            // RoomForLeaf trace (card = mid·Corner1 exactly). CLeafGeometry::Update splits that full
            // width/height around Corner0 (leaf[+0x30/+0x34]) rather than treating it as a symmetric half-size.
            var width = cardScale * template.Corner1.X;
            var height = cardScale * template.Corner1.Y;
            var x0 = -template.Corner0.X * width;
            var x1 = (1f - template.Corner0.X) * width;
            var y0 = -template.Corner0.Y * height;
            var y1 = (1f - template.Corner0.Y) * height;

            // Bud: 60° declination off the branch at a random roll (ComputeBud: fStack_234 = 60.0 constant,
            // random(±180°)), stepped off the twig by the bud's own (small) length so leaves hug the bough.
            var budFrame = anchor.Frame;
            var budSpinDeg = rng.Range(-180f, 180f);
            budFrame = budFrame.MultiplyLocal(BranchFrame.FromAxisAngle(Vector3.UnitX, budSpinDeg * Deg2Rad));
            budFrame = budFrame.MultiplyLocal(BranchFrame.FixedAngleRotate(LeafBudDeclinationDeg));
            var budDir = budFrame.Direction;
            var center = anchor.Pos + budDir * anchor.BudReach;

            // RoomForLeaf (CIdvBranch::RoomForLeaf, 360 0x829753D8): reject if an existing leaf is within
            // max(w,h)·spacing in ALL THREE axes, checked against the engine's ONE global leaf-manager array
            // (CTreeEngine+0x84+0x10). Half-extent = leaf size × the leaf table's spacing factor (token 3007
            // = SIdvLeafInfo+0x20). Skipped only when the placement mode (token 3008 = SIdvLeafInfo+0xc) is 0.
            var placed = placementMode == 0 ? null : globalPlaced;
            if (placed is not null)
            {
                var spacing = MathF.Max(width, height) * spacingFactor;
                if (!HasRoomForLeaf(placed, center, spacing))
                {
                    return false;
                }

                placed.Add(center);
            }

            // The engine uses the TREE record's ICON as the leaf atlas (overriding the .spt's dev-era
            // material); fall back to the .spt material only when no override is supplied.
            var atlasPath = opt.LeafTextureOverride
                            ?? SpeedTreeTexturePath.ToGamePath(template.Material, SpeedTreeTextureKind.Leaf)
                            ?? "spt:leaf";
            if (!leafGroups.TryGetValue(atlasPath, out var buffer))
            {
                buffer = new MeshBuffer();
                leafGroups[atlasPath] = buffer;
            }

            var quads = opt is { LeafBillboard: false, CrossedLeafCards: true } ? 2 : 1;
            if (!buffer.CanAdd(4 * quads))
            {
                return false;
            }

            // CLeafGeometry::SetTextureCoords consumes an 8-float UV block per leaf template. That block
            // comes from post-tree token 10000/10002 (CSpeedTreeRT::ParseTextureCoordInfo), not from the
            // leaf material filename; missing blocks use the SDK's InitTables full-atlas default.
            var uv = anchor.TextureCoords;

            if (opt.LeafBillboard)
            {
                // One card per leaf, encoded as center + signed offset; the GPU re-faces it per frame. The
                // baked positions (default bud frame) only feed bounds + the CPU still path.
                var (bbRight, bbUp) = BuildFrame(budDir);
                AddLeafCard(buffer, center, bbRight, bbUp, budDir, uv, x0, x1, y0, y1, billboard: true,
                    windWeight: windWeight);
                return true;
            }

            if (opt.LeafFaceDirection is { } face)
            {
                // Camera-facing billboard stand-in: one quad whose normal faces the given direction, so a
                // still shows the leaf cluster full-on (what the engine's per-card leaf billboards produce).
                var faceDir = SafeNormalize(face, Vector3.UnitZ);
                var (fRight, fUp) = BuildFrame(faceDir);
                AddLeafCard(buffer, center, fRight, fUp, faceDir, uv, x0, x1, y0, y1);
                return true;
            }

            // Static fallback: crossed pair gives volume from any angle (the engine re-faces leaves to
            // camera in-shader; a static mesh can't, so two perpendicular cards substitute).
            var (right, up) = BuildFrame(budDir);
            AddLeafCard(buffer, center, right, up, budDir, uv, x0, x1, y0, y1);
            if (opt.CrossedLeafCards)
            {
                AddLeafCard(buffer, center, budDir, up, right, uv, x0, x1, y0, y1);
            }

            return true;
        }

        foreach (var anchor in anchors)
        {
            _ = TryPlace(anchor);
        }
    }

    /// <summary>True if no already-placed leaf center lies within <paramref name="spacing" /> of
    /// <paramref name="center" /> on every axis (the engine's axis-aligned cube overlap test).</summary>
    private static bool HasRoomForLeaf(List<Vector3> placed, Vector3 center, float spacing)
    {
        foreach (var p in placed)
        {
            if (MathF.Abs(center.X - p.X) < spacing &&
                MathF.Abs(center.Y - p.Y) < spacing &&
                MathF.Abs(center.Z - p.Z) < spacing)
            {
                return false;
            }
        }

        return true;
    }

    private static int TruncateSpawnCount(float raw) =>
        raw > 0f && float.IsFinite(raw) ? (int)raw : 0;

    // ---- Geometry emission --------------------------------------------------------------

    /// <summary>
    ///     Reproduce <c>CTreeEngine::BuildBranchLods</c>' LOD0 selection: the engine lofts the full skeleton
    ///     then keeps, for the rendered LOD0 mesh, only the highest-"volume" branches until their cumulative
    ///     volume reaches <c>nearFraction · totalVolume</c> (decompile L3915-3961; LOD0 fraction = near when
    ///     <c>numLods ≥ 2</c>, else 1.0 = keep all). Without a <c>.spt</c> LOD section the ctor default near
    ///     = 1.0, so every branch is kept (no decimation), matching the engine.
    /// </summary>
    private static void EmitDecimatedBranches(List<CollectedBranch> branches, SptLodInfo? lod, MeshBuffer bark)
    {
        if (branches.Count == 0)
        {
            return;
        }

        var keepFraction = lod is { NumBranchLods: >= 2 } && lod.BranchNearFraction < 1f
            ? Math.Clamp(lod.BranchNearFraction, 0f, 1f)
            : 1f;

        IReadOnlyList<CollectedBranch> kept;
        if (keepFraction >= 1f)
        {
            kept = branches;
        }
        else
        {
            var total = 0f;
            foreach (var b in branches)
            {
                total += b.Importance;
            }

            // BuildBranchLods (L3864-3961) accumulates branches until cumulative volume reaches the fraction.
            // The engine walks a partitioned flat order (high-volume branches first); we approximate with a
            // strict volume-descending sort, which keeps the trunk + main limbs and drops the small twigs.
            // NOTE: this is the closest visual match, NOT the engine's exact order — the exact flat order over
            // our current branch generation over-keeps (the generated branch volumes don't yet match the
            // engine's), so closing the residual count gap is a branch-GENERATION fidelity task, not a
            // decimation one. Always keeps at least the single most-important branch so a tree never vanishes.
            var ordered = branches.OrderByDescending(b => b.Importance).ToList();
            var target = total * keepFraction;
            var selected = new List<CollectedBranch>(ordered.Count);
            var cumulative = 0f;
            foreach (var b in ordered)
            {
                selected.Add(b);
                cumulative += b.Importance;
                if (cumulative >= target)
                {
                    break;
                }
            }

            kept = selected;
        }

        foreach (var b in kept)
        {
            int[]? prevRing = null;
            foreach (var ring in b.Rings)
            {
                prevRing = EmitRing(bark, ring.Pos, ring.Frame, ring.Radius, b.VertsPerRing, ring.PathT, prevRing);
            }
        }
    }

    private static int[]? EmitRing(
        MeshBuffer bark, Vector3 center, BranchFrame frame, float radius, int radial, float v, int[]? prevRing)
    {
        if (!bark.CanAdd(radial + 1))
        {
            return prevRing;
        }

        var ring = new int[radial + 1];
        for (var k = 0; k <= radial; k++)
        {
            var a = MathF.Tau * (k % radial) / radial;
            var normal = SafeNormalize(frame.Y * MathF.Sin(a) + frame.Z * MathF.Cos(a), frame.Y);
            var vertex = center + normal * radius;
            ring[k] = bark.Add(vertex, normal, new Vector2(k / (float)radial, v * 2f));
        }

        if (prevRing is not null)
        {
            for (var k = 0; k < radial; k++)
            {
                // CCW winding when viewed from OUTSIDE the tube: the opaque PSO is FrontCounterClockwise
                // with CullMode.Back, so the previous (prevRing[k], prevRing[k+1], ring[k+1], ring[k])
                // order wound the outer wall clockwise → back-facing → culled (the "reversed culling" on
                // trunks). Reversing it makes the outer wall front-facing. Normals stay outward (set above),
                // so lighting is unchanged.
                bark.Quad(prevRing[k], ring[k], ring[k + 1], prevRing[k + 1]);
            }
        }

        return ring;
    }

    private static (Vector3 Pos, BranchFrame Frame, float Radius) SampleAlong(List<BranchRing> rings, float frac)
    {
        if (rings.Count == 0)
        {
            return (Vector3.Zero, BranchFrame.Identity, 0f);
        }

        // CIdvBranch::FillBranch receives frac * branchLength, then searches each ring's stored
        // cumulative distance field (+0x40) and returns the lower ring plus a local interpolation factor.
        // Position is interpolated between ring centers, but the binary passes the lower ring's stored
        // matrix to child branches / ComputeBud rather than interpolating orientation.
        var targetDistance = Math.Clamp(frac, 0f, 1f) * rings[^1].Distance;
        if (targetDistance <= rings[0].Distance)
        {
            return (rings[0].Pos, rings[0].Frame, rings[0].Radius);
        }

        for (var i = 1; i < rings.Count; i++)
        {
            if (targetDistance >= rings[i].Distance)
            {
                continue;
            }

            var lower = rings[i - 1];
            var upper = rings[i];
            var span = upper.Distance - lower.Distance;
            var lt = span > 1e-6f ? (targetDistance - lower.Distance) / span : 0f;
            var pos = Vector3.Lerp(lower.Pos, upper.Pos, lt);
            var radius = MathF.Max(0f, lower.Radius + (upper.Radius - lower.Radius) * lt);
            return (pos, lower.Frame, radius);
        }

        return (rings[^1].Pos, rings[^1].Frame, rings[^1].Radius);
    }

    private static void AddLeafCard(
        MeshBuffer buf, Vector3 center, Vector3 right, Vector3 up, Vector3 normal, SptLeafTextureCoords uv,
        float x0, float x1, float y0, float y1, bool billboard = false, float windWeight = 0f)
    {
        var p00 = center + right * x0 + up * y0;
        var p10 = center + right * x1 + up * y0;
        var p11 = center + right * x1 + up * y1;
        var p01 = center + right * x0 + up * y1;

        if (billboard)
        {
            // Encode the card as center (tangent) + signed 2D card-space offset (bitangent) so the GPU
            // leaf-billboard VS rebuilds it camera-facing (SpeedTree's CPU-side leaf billboard, moved to
            // the shader). The baked positions still hold a real 3D quad for bounds + the CPU still path.
            // bitangent.z = per-leaf wind weight (a [0,1] blend factor, NOT a size — ApplyHeightScale
            // leaves it untouched), consumed by the VS's wind sway.
            var v00b = buf.Add(p00, normal, uv[0], center, new Vector3(x0, y0, windWeight));
            var v10b = buf.Add(p10, normal, uv[1], center, new Vector3(x1, y0, windWeight));
            var v11b = buf.Add(p11, normal, uv[2], center, new Vector3(x1, y1, windWeight));
            var v01b = buf.Add(p01, normal, uv[3], center, new Vector3(x0, y1, windWeight));
            buf.Triangle(v00b, v10b, v11b);
            buf.Triangle(v00b, v11b, v01b);
            return;
        }

        var v00 = buf.Add(p00, normal, uv[0]);
        var v10 = buf.Add(p10, normal, uv[1]);
        var v11 = buf.Add(p11, normal, uv[2]);
        var v01 = buf.Add(p01, normal, uv[3]);
        buf.Triangle(v00, v10, v11);
        buf.Triangle(v00, v11, v01);
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static float Eval(IReadOnlyList<SptBezierSpline?> splines, int slot, float param,
        Func<float, float, float> rand) =>
        slot < splines.Count && splines[slot] is { } spline ? spline.Evaluate(param, rand) : 0f;

    private static float ScaledVariance(IReadOnlyList<SptBezierSpline?> splines, int slot, float param,
        Func<float, float, float> rand) =>
        slot < splines.Count && splines[slot] is { } spline ? spline.ScaledVariance(param, rand) : 0f;

    private static float SpawnTemplateParam(float frac, float start, float end) =>
        MathF.Abs(end - start) > 1e-6f ? Math.Clamp((frac - start) / (end - start), 0f, 1f) : 1f;

    private static BranchFrame ApplyGravity(
        BranchFrame frame, IReadOnlyList<SptBezierSpline?> splines, float pathT, float slot1Gain,
        Func<float, float, float> rand)
    {
        var dir = frame.Direction;
        var declDeg = MathF.Acos(Math.Clamp(Vector3.Dot(dir, Vector3.UnitZ), -1f, 1f)) * 57.29578f;
        var weight = 1f - MathF.Abs(90f - declDeg) * 0.011111111f;
        var roll = splines[8] is { } s8 ? -(s8.Evaluate(pathT, rand) - 0.5f) * 2f : 0f;
        var bendDeg = roll * slot1Gain * declDeg * weight;
        var axis = Vector3.Cross(dir, Vector3.UnitZ);
        return axis.LengthSquared() > 1e-10f && MathF.Abs(bendDeg) > 1e-6f
            ? frame.MultiplyWorld(BranchFrame.FromAxisAngle(axis, bendDeg * Deg2Rad))
            : frame;
    }

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

            // Leaf-billboard tangent (card center) is a POSITION and bitangent.xy (card offset) a SIZE —
            // both scale with the tree, or the cards would keep their pre-rescale size. bitangent.z is the
            // per-leaf wind WEIGHT (a [0,1] factor), so it must NOT be scaled — skip every 3rd component.
            if (sub.IsLeafBillboard)
            {
                if (sub.Tangents is { } t)
                {
                    for (var i = 0; i < t.Length; i++) t[i] *= scale;
                }

                if (sub.Bitangents is { } b)
                {
                    for (var i = 0; i < b.Length; i++)
                    {
                        if (i % 3 != 2) b[i] *= scale;
                    }
                }
            }
        }
    }

    private static (Vector3 Right, Vector3 Up) BuildFrame(Vector3 dir)
    {
        dir = SafeNormalize(dir, Vector3.UnitZ);
        var reference = MathF.Abs(dir.Z) > 0.99f ? Vector3.UnitX : Vector3.UnitZ;
        var right = SafeNormalize(Vector3.Cross(reference, dir), Vector3.UnitX);
        var up = Vector3.Cross(dir, right);
        return (right, up);
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

    private readonly record struct BranchRing(Vector3 Pos, BranchFrame Frame, float Distance, float Radius, float PathT);

    /// <summary>A fully-lofted branch held back from emission so the LOD decimation can drop the
    /// lowest-"volume" branches before any vertices are produced (see <see cref="EmitDecimatedBranches" />).
    /// <paramref name="Importance" /> = <c>CIdvBranch::ComputeVolume</c> = Σ segLen·(rᵢ+rᵢ₊₁).</summary>
    private readonly record struct CollectedBranch(
        IReadOnlyList<BranchRing> Rings, int VertsPerRing, float Importance);

    /// <summary>
    ///     Column-major 3x3 branch frame matching the runtime ring matrix. Column X is the branch growth
    ///     vector; columns Y/Z form the cross-section plane.
    /// </summary>
    private readonly record struct BranchFrame(Vector3 X, Vector3 Y, Vector3 Z)
    {
        public static BranchFrame Identity { get; } = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

        public Vector3 Direction => SafeNormalize(X, Vector3.UnitZ);

        public Vector3 Transform(Vector3 local) => X * local.X + Y * local.Y + Z * local.Z;

        public BranchFrame MultiplyLocal(BranchFrame local) =>
            new(Transform(local.X), Transform(local.Y), Transform(local.Z));

        public BranchFrame MultiplyWorld(BranchFrame world) =>
            new(world.Transform(X), world.Transform(Y), world.Transform(Z));

        public static BranchFrame RotateTwoAngles(float angleAdeg, float angleBdeg)
        {
            var a = angleAdeg * Deg2Rad;
            var b = angleBdeg * Deg2Rad;
            var ca = MathF.Cos(a);
            var sa = MathF.Sin(a);
            var cb = MathF.Cos(b);
            var sb = MathF.Sin(b);

            // spt_RotateTwoAngles_734A0: local X becomes the branch vector extracted by NormalizeExtract.
            return new BranchFrame(
                new Vector3(ca * cb, ca * sb, -sa),
                new Vector3(-sb, cb, 0f),
                new Vector3(sa * cb, sa * sb, ca));
        }

        public static BranchFrame FixedAngleRotate(float angleDeg)
        {
            var a = angleDeg * Deg2Rad;
            var c = MathF.Cos(a);
            var s = MathF.Sin(a);
            return new BranchFrame(
                new Vector3(c, 0f, -s),
                Vector3.UnitY,
                new Vector3(s, 0f, c));
        }

        public static BranchFrame FromAxisAngle(Vector3 axis, float angle)
        {
            axis = SafeNormalize(axis, Vector3.UnitX);
            var c = MathF.Cos(angle);
            var s = MathF.Sin(angle);
            var t = 1f - c;
            var x = axis.X;
            var y = axis.Y;
            var z = axis.Z;

            // spt_SetVecFromParent_73618 / spt_GravityRotate_73860 matrix layout.
            return new BranchFrame(
                new Vector3(t * x * x + c, t * x * y + s * z, t * x * z - s * y),
                new Vector3(t * x * y - s * z, t * y * y + c, t * y * z + s * x),
                new Vector3(t * x * z + s * y, t * y * z - s * x, t * z * z + c));
        }
    }

    /// <summary>A pending leaf card: where on a terminal branch it attaches, which template it uses, and
    /// how far the bud steps off the branch.</summary>
    private readonly record struct LeafAnchor(
        Vector3 Pos, BranchFrame Frame, SptLeaf Template, SptLeafTextureCoords TextureCoords, float BudReach);

    /// <summary>
    ///     SpeedTree's <c>CIdvRandom</c>/<c>Random</c> uniform generator: a Park-Miller 16807 LCG with a
    ///     128-float shuffle table (decompile: <c>CIdvRandom::Reseed</c>, <c>Random::Raw</c>,
    ///     <c>Random::Next</c>). Seed 0/1 are normalized to 1 by <c>CIdvRandom::Reseed</c>.
    /// </summary>
    private sealed class SptRandom
    {
        private const int Modulus = 2147483647;
        private const int Multiplier = 16807;
        private const int Quotient = 127773;
        private const int Remainder = 2836;

        // Engine scale constant from Random::Raw: `(float)state * 4.656613e-10`. The literal is a DOUBLE
        // and `state` is cast to float first, so the product is computed in double — reproduced exactly here.
        // Matching the float/double mix bit-for-bit matters: the shuffle index and the uniform multiply ride
        // on this value, so a float-only port drifts in the ~7th digit and flips RoomForLeaf decisions near
        // the spacing boundary (verified divergence vs the runtime trace).
        private const double InvModulus = 4.656613e-10;
        private readonly float[] _shuffle = new float[128]; // engine stores the shuffle table as float
        private int _state;

        public SptRandom(uint seed)
        {
            Reseed(seed);
        }

        public void Reseed(uint seed)
        {
            var s = seed > int.MaxValue ? (int)(seed & 0x7fffffffu) : (int)seed;
            if (s < 2)
            {
                s = 1;
            }

            _state = s;
            for (var i = 0; i < _shuffle.Length; i++)
            {
                _shuffle[i] = (float)Raw();
            }
        }

        // Random::Next (Bays-Durham shuffle): the index uses the DOUBLE raw * 128.0; the value comes from the
        // float table; the replaced entry stores (float)Raw(); the result is the table float promoted to double.
        public double NextDouble()
        {
            var index = (int)(Raw() * 128.0);
            if ((uint)index >= (uint)_shuffle.Length)
            {
                index = _shuffle.Length - 1; // engine relies on raw<1; guard the (float)maxState rounding edge
            }

            double value = _shuffle[index];
            _shuffle[index] = (float)Raw();
            return value;
        }

        public float NextFloat() => (float)NextDouble();

        // CIdvRandom::GetUniform: min + (float)((double)(max-min) * Next()). The subtract is float, promoted to
        // double for the multiply with the double Next(), the product cast back to float, then added to min.
        public float Range(float min, float max) => min + (float)((double)(max - min) * NextDouble());

        public int NextInt(int exclusiveMax) =>
            exclusiveMax <= 0 ? 0 : (int)Range(0f, 1_000_000f) % exclusiveMax;

        // Random::Raw: Park-Miller 16807 via Schrage; identical to the engine's `state*16807 - hi*Modulus`
        // (since 16807*Quotient + Remainder == Modulus and the result fits in int32, the engine's overflowing
        // form and this Schrage form agree bit-for-bit). Returns a DOUBLE (see InvModulus).
        private double Raw()
        {
            var hi = _state / Quotient;
            var lo = _state - hi * Quotient;
            var next = Multiplier * lo - Remainder * hi;
            if (next < 1)
            {
                next += Modulus;
            }

            _state = next;
            return (float)_state * InvModulus;
        }
    }

    /// <summary>Growing vertex/index accumulator with a ushort index guard.</summary>
    private sealed class MeshBuffer
    {
        private const int MaxVertices = 60000;
        private readonly List<float> _positions = [];
        private readonly List<float> _normals = [];
        private readonly List<float> _uvs = [];
        private readonly List<float> _tangents = [];
        private readonly List<float> _bitangents = [];
        private bool _hasTangents;
        private readonly List<ushort> _indices = [];

        public int VertexCount => _positions.Count / 3;

        public bool CanAdd(int verts) => VertexCount + verts <= MaxVertices;

        public int Add(Vector3 position, Vector3 normal, Vector2 uv) =>
            Add(position, normal, uv, Vector3.Zero, Vector3.Zero);

        /// <summary>Adds a vertex carrying the leaf-billboard payload in the tangent/bitangent slots:
        /// <paramref name="tangent" /> = the card center (pivot), <paramref name="bitangent" /> = the
        /// signed 2D card-space corner offset. The GPU leaf-billboard VS rebuilds the quad from these.</summary>
        public int Add(Vector3 position, Vector3 normal, Vector2 uv, Vector3 tangent, Vector3 bitangent)
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
            _tangents.Add(tangent.X);
            _tangents.Add(tangent.Y);
            _tangents.Add(tangent.Z);
            _bitangents.Add(bitangent.X);
            _bitangents.Add(bitangent.Y);
            _bitangents.Add(bitangent.Z);
            if (tangent != Vector3.Zero || bitangent != Vector3.Zero)
            {
                _hasTangents = true;
            }

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
            string name, string? diffuse, string? normalMap, bool doubleSided, bool leaf, SptGeometryOptions opt,
            bool leafBillboard = false)
        {
            return new RenderableSubmesh
            {
                ShapeName = name,
                Positions = [.. _positions],
                Triangles = [.. _indices],
                Normals = [.. _normals],
                UVs = [.. _uvs],
                // Tangent/bitangent carry the leaf-billboard payload (center + signed 2D offset); only
                // emitted when leaves actually populated them, so bark stays tangent-free as before.
                Tangents = _hasTangents ? [.. _tangents] : null,
                Bitangents = _hasTangents ? [.. _bitangents] : null,
                DiffuseTexturePath = diffuse,
                NormalMapTexturePath = normalMap,
                IsDoubleSided = doubleSided,
                HasAlphaTest = leaf,
                IsLeafBillboard = leafBillboard,
                AlphaTestThreshold = leaf ? opt.LeafAlphaThreshold : (byte)128,
            };
        }
    }
}
