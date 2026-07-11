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
    /// <summary>The engine's degrees↔radians constant: <c>CRotTransform::{RotateYZ,RotateAxis,
    /// RotateAxisFromIdentity}</c> convert degrees to radians by DIVIDING by this exact float
    /// (<c>divss xmm, [57.29578]</c> in the Oblivion Remastered x86-64 build; the 360 decompile shows
    /// <c>angle / 57.29578</c> too), and convert radians to degrees by multiplying. The builder must DIVIDE
    /// (not multiply by π/180): <c>1/57.29578f = 0.017453292</c> differs from <c>π/180 = 0.017453294</c> at
    /// the last ULP, and float division rounds differently from reciprocal-multiply — that ULP, propagated
    /// through every per-ring sin/cos, is the accumulated position drift. .NET's MathF.Sin/Cos call the same
    /// ucrtbase sinf/cosf the engine imports, so matching the angle bit-for-bit makes the rotation bit-exact.</summary>
    private const float DegPerRad = 57.29578f;

    /// <summary>
    ///     The FNV/Oblivion game instantiates a <c>.spt</c> at 10× its stored size: the SpeedTree lib parses
    ///     <c>CTreeEngine.ceMid = Float2006</c> (token 2006), then the host scales ×10 before
    ///     <c>CTreeEngine::Compute</c> (verified: WastelandShrub size draw <c>GetUniform(400,600)</c> for
    ///     <c>Float2006=50</c>; Oblivion YewForest 300→3000). The builder MUST loft at this same scale,
    ///     because the engine's generation does float arithmetic whose rounding is scale-sensitive at integer
    ///     boundaries: the child/leaf counts are <c>(int)(Float6012/treeSize · length)</c> truncations
    ///     (<c>CIdvBranch::Compute</c> L2373), and the <c>budReach</c>/ring-radius floors are a literal
    ///     <c>0.01</c> in ×10 space. At ÷10 scale <c>100/42.5 = 2.352551</c> vs the engine's
    ///     <c>1000/425 = 2.352550</c> — a count landing near 8.0 floors to 7 in the builder and 8 in the
    ///     engine, spawning one fewer child and forking the entire downstream RNG draw sequence. Generating
    ///     at ×10 makes every such operation bit-identical to the engine; the final mesh is rescaled to OBND
    ///     by <see cref="ApplyHeightScale" />, so the absolute ×10 never reaches the output.
    /// </summary>
    private const float EngineWorldScale = 10f;

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

    public static NifRenderableModel Build(SptModel model, uint seed, SptGeometryOptions? options = null)
    {
        var opt = options ?? SptGeometryOptions.FromEnvironment();
        var rng = new SptRandom(seed);

        // Optional: dump the RNG draw sequence (env SPT_RNG_TRACE) in the runtime tracer's format so the
        // engine's GetUniform/Reseed stream can be diffed against this builder's 1:1 (residual leaf-count).
        using var rngTrace = SptRngTrace.FromEnvironment(seed);
        rng.Trace = rngTrace;
        rng.SetPhase("size");

        var treeSize = ComputeTreeSize(model.General, opt, rng);
        // CTreeEngine::Compute reseeds after the randomized tree-size draw and before recursive branch
        // generation, so branch/leaf layout depends on SNAM but not on the size-variance sample.
        rng.SetPhase("init");
        rng.Reseed(seed);
        Func<float, float, float> rand = rng.Range;

        var barkPath = SpeedTreeTexturePath.ToGamePath(model.General.BarkTexturePath, SpeedTreeTextureKind.Bark);
        var barkNormalPath = DeriveNormalMap(barkPath);

        var bark = new MeshBuffer();
        var leafGroups = new Dictionary<string, MeshBuffer>(StringComparer.OrdinalIgnoreCase);
        var collectedBranches = new List<CollectedBranch>();

        // Leaf placement is INLINE during the loft — the engine places each bud inside CIdvBranch::Compute
        // (ComputeBud → MakeLeaf → RoomForLeaf), NOT in a deferred pass. RoomForLeaf and its TWO on-accept
        // GetUniform draws advance the shared RNG between terminal branches, so deferring them desynced every
        // later branch (the residual leaf-count gap). The placer holds the ONE global RoomForLeaf list plus
        // the accepted leaves. Spacing factor + placement mode come from the .spt leaf table (tokens
        // 3007 / 3008, SIdvLeafInfo::Parse → +0x20 / +0x0C; ctor defaults 0.5 / mode 2).
        //
        // The per-map spacing SIZE is Corner1 (token 4005) × the tree-size MID: CTreeEngine::Compute
        // overwrites the parsed Corner2 default at map[+0x48/+0x4C] with sizeMid·Corner1 before generation
        // (FNV 360 L3679; Oblivion FUN_007a45f0 — identical formula, both binaries). Shipped .spts author
        // Corner2 ≡ Corner1·Float2006, so on Oblivion (host size = authored size) the overwrite is a no-op,
        // while FNV's ×10 host size scales it — the "era difference" is only the host's tree size. Because
        // spacing shares the loft's own sizeMid, it stays consistent with our geometry units by construction.
        var leafSpacing = model.LeafTable?.Float3007 ?? DefaultLeafSpacingFactor;
        var placementMode = model.LeafTable?.UInt3008 ?? 2u;
        var treeSizeMid = (model.General.Float2006 > 1e-3f ? model.General.Float2006 : opt.TrunkHeight) * EngineWorldScale;
        // BSTreeModel::InitFromBase sets SetNumLeafRockingGroups(2) when the tree has < 4 leaf
        // maps, else 1 — the rock group is part of each leaf's LeafBase/phase slot (wind rock/rustle).
        var placer = new LeafPlacer(leafSpacing, treeSizeMid, placementMode, model.Leaves.Count < 4 ? 2 : 1);

        // Canopy-depth dimming (CIdvBranch::MakeLeaf / BuildCrossSection): interior leaves and bark near the
        // trunk base are darkened toward (1 − dimmingScalar). The enable flag + leaf scalar are .spt tokens
        // 3009/3010 (SIdvLeafInfo +0/+4, ctor defaults true/1.0); in-game BOTH scalars are overridden from
        // TREE CNAM via TESObjectTREE::Get{Leaf,Branch}Dimming → CSpeedTreeRT::Set{Leaf,Branch}DimmingScalar.
        // Branch dimming has NO .spt token (SIdvLeafInfo::Parse never writes +8) — without a CNAM value it
        // stays 0 (neutral).
        var dimming = new TreeDimming(
            (model.LeafTable?.Byte3009 ?? 1) != 0,
            Math.Clamp(opt.LeafDimming ?? model.LeafTable?.Float3010 ?? 1f, 0f, 1f),
            Math.Clamp(opt.BranchDimming ?? 0f, 0f, 1f));

        var levels = Math.Min(model.Branches.Count, Math.Max(1, opt.MaxLevels));
        if (model.Branches.Count > 0)
        {
            // Root trunk: identity 3x3 frame; parentRadius starts large so the trunk's own radius is not clamped.
            // The trunk's vertex-color helps are 0 (CIdvBranch::LeafVertexColorHelp base case; CTreeEngine's
            // root Compute passes help = 0).
            GenerateBranch(model, 0, levels, 0f, Vector3.Zero, BranchFrame.Identity,
                parentRadius: float.MaxValue, treeSize, opt, rng, rand, collectedBranches, placer, seed,
                dimming, leafHelp: 0f, barkHelp: 0f, windRaw: 1f);
        }

        // The engine never renders the raw skeleton: CTreeEngine::Compute lofts every branch, then
        // BuildBranchLods decimates them into the rendered LOD mesh. Reproduce LOD0 here — keep the
        // highest-"volume" branches until their cumulative volume reaches the .spt's near fraction.
        EmitDecimatedBranches(collectedBranches, model.Lod, bark, dimming);

        // Emit the accepted leaf cards' geometry (no RNG — every draw happened inline during the loft).
        EmitPlacedLeaves(placer.Leaves, treeSizeMid, model, opt, leafGroups);

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
        // ×10 to loft in the engine's native scale (see EngineWorldScale) — the size GetUniform's range then
        // matches the engine's exactly (e.g. (400,600) not (40,60)), as does every count/floor downstream.
        var mid = general.Float2006 * EngineWorldScale;
        var spread = MathF.Abs(general.Float2007) * EngineWorldScale;
        var size = mid > 1e-3f ? rng.Range(mid - spread, mid + spread) : opt.TrunkHeight * EngineWorldScale;
        return MathF.Max(1f, size);
    }

    // ---- Branch loft (port of CIdvBranch::Compute) --------------------------------------

    /// <summary>
    ///     The engine's RAW per-vertex wind weight along a branch (design doc B.3, windLevel = 1):
    ///     below the wind level everything is rigid (raw 1); AT the wind level the branch ramps
    ///     <c>raw = 1 − t</c> (<c>CIdvBranch::ComputeVertexWeight</c>, a·b ≈ t); deeper levels ramp from
    ///     the raw INHERITED at their spawn point toward full articulation at the tip
    ///     (<c>ComputeHighLevelVertexWeight</c>: <c>raw = inherited·(1 − t)</c>). The shader consumes
    ///     <c>1 − raw</c> (the store convention of <c>AddVertexWind</c>/<c>InitLods</c>) as the wind-matrix
    ///     lerp weight.
    /// </summary>
    private static float WindRawAt(int level, float windRawBase, float t) => level switch
    {
        0 => 1f,
        1 => 1f - t,
        _ => windRawBase * (1f - t),
    };

    private static void GenerateBranch(
        SptModel model, int level, int levels, float parentT, Vector3 basePos, BranchFrame parentFrame,
        float parentRadius, float treeSize, SptGeometryOptions opt, SptRandom rng,
        Func<float, float, float> rand, List<CollectedBranch> collected, LeafPlacer placer,
        uint branchSeed, TreeDimming dimming, float leafHelp, float barkHelp, float windRaw)
    {
        if (level >= levels || level >= model.Branches.Count)
        {
            return;
        }

        var branch = model.Branches[level];
        var s = branch.Splines;
        rng.SetPhase($"branch L{level}");

        var numRings = Math.Clamp((int)branch.UInt6009 + 1, 2, 48);
        var vertsPerRing = Math.Clamp((int)branch.UInt6008 + 1, 3, 24);

        // ---- Ring-0 setup: CIdvBranch::Compute's base-ring draws, in the engine's EXACT order (360 decompile
        // L2021-2168). The branch struct holds the 9 splines at piVar2[0x14..0x1c] = s[0..8]. Every
        // Evaluate / ScaledVariance draws GetUniform UNCONDITIONALLY (forceDraw) — even at zero variance it
        // returns 0 but STILL advances the shared RNG — so the stream that later sets every branch length and
        // leaf position stays in lock-step (verified: trunk aligns 46-47 RNG states vs the live engine trace).
        // Order: s4(length, the LEAD draw before the spin) · spin(-180,180) · s7(declination) · s1(bend gain) ·
        // s5(radius) · s2 · s6@0(base taper) · s0×2(noise) · s6@0 · s3@0 · s8@0(roll) = 9 grav + spin + 2 noise.
        //
        // slot 4 = length, slot 5 = radius (both ×treeSize). The engine has NO child-length clamp — limbs are
        // MEANT to be far longer than the (stubby) trunk — so only the radius is clamped against the sampled
        // parent ring radius at the attachment.
        var length = MathF.Max(0.01f, Eval(s, 4, parentT, rand, forceDraw: true) * treeSize); // LEAD (pre-spin)
        var spinDeg = rng.Range(-180f, 180f);
        var declMean = Eval(s, 7, parentT, rand, forceDraw: true);
        var bendGain = Eval(s, 1, parentT, rand, forceDraw: true); // constant per branch; per-ring bend gain
        var radius = MathF.Max(0.005f, Eval(s, 5, parentT, rand, forceDraw: true) * treeSize);
        _ = Eval(s, 2, parentT, rand, forceDraw: true);                       // s2 (flex factor; drawn for parity)

        // Child-radius clamp (CIdvBranch::Compute 360 L2067-2072 = PC FUN_00b11050, both agree): the engine
        // tests the BASE radius — scale·slot6(0), the FIRST s6@0 draw — against 0.85·parent, and on trigger
        // COLLAPSES the radius SCALE to the clamped value, so every ring becomes 0.85·parent·slot6(t).
        // (Round-1 clamped the bare scale, over-clamping whenever slot6(0) < 1.) The ring-0 radius then uses
        // the SECOND s6@0 draw (L2154), not the clamp draw.
        var clampTaper = MathF.Max(0f, Eval(s, 6, 0f, rand, forceDraw: true)); // s6@0 draw 1 (clamp eval)
        if (parentRadius > 0f && radius * clampTaper > parentRadius * 0.85f)
        {
            radius = parentRadius * 0.85f;
        }

        var noise0B = ScaledVariance(s, 0, 0f, rand);
        var noise0A = ScaledVariance(s, 0, 0f, rand);
        var baseTaper = MathF.Max(0f, Eval(s, 6, 0f, rand, forceDraw: true)); // s6@0 draw 2 → ring-0 radius
        _ = Eval(s, 3, 0f, rand, forceDraw: true);                            // s3@0 (flex factor; parity)
        var roll0 = s.Count > 8 && s[8] is not null
            ? -(Eval(s, 8, 0f, rand, forceDraw: true) - 0.5f) * 2f
            : 0f;                                                             // s8@0 (roll)

        // Initial frame: spin about the parent growth axis (RotateAxis, L2144) and the declination
        // RotateTwoAngles(s7 + noise0A, noise0B) (L2147). The ORDER of these two matters and differs by level.
        // The branch growth axis starts HORIZONTAL (local +X); for the trunk it is mapped to world-up only by
        // slot-7's −90° mean (verified: willow-oak trunk slot7 header=(−90,−90,5)). The spin is a roll about
        // the growth axis — but if it is applied BEFORE the −90° declination (as a naive "engine-exact for all
        // levels" port did), it rolls the still-horizontal +X vector, so a spin near ±90°/180° tips the entire
        // trunk sideways or upside-down (the U-turn / inverted / under-terrain trees the viewer showed). So the
        // trunk DECLINES FIRST (establishing the near-vertical +Z growth, keeping slot-7's ±5° lean as natural
        // character) and only THEN rolls about that vertical axis (harmless to the up direction). Child branches
        // keep spin-first: RotateAxis about the parent growth axis sets the child's radial azimuth, then slot-7
        // declines it off the parent. RNG draw order is identical for both — only the matrix multiply order differs.
        var frame = parentFrame;
        var spinRoll = BranchFrame.FromAxisAngle(Vector3.UnitX, spinDeg / DegPerRad);
        var decline = BranchFrame.RotateTwoAngles(declMean + noise0A, noise0B);
        frame = level == 0
            ? frame.MultiplyLocal(decline).MultiplyLocal(spinRoll)
            : frame.MultiplyLocal(spinRoll).MultiplyLocal(decline);
        frame = ApplyGravity(frame, roll0, bendGain); // ring-0 gravity bend (L2171)

        var pos = basePos;
        var rings = new List<BranchRing>(numRings)
        {
            new(pos, frame, 0f, MathF.Max(0.01f, radius * baseTaper), 0f),
        };
        var previousDistance = 0f;
        var previousFrame = frame;

        // Rings 1..R-1: the engine draws s6(taper) · s3(flex) · s8(roll) · s0×2(noise) per ring (L2241-2289),
        // then bends by gravity and rotates by the noise pair. 3 grav + 2 noise — matching the setup's per-ring
        // tail, so the total is 3·R+6 grav, 2·R noise, 1 spin (exactly the live trace).
        for (var r = 1; r < numRings; r++)
        {
            var t = r / (float)(numRings - 1);
            // The engine's ring param is pow(r/(R−1), exponent) — CIdvBranch::Compute computes it once
            // (360 L2235-2244 powf; PC FUN_00b11050 confirms) and uses it for BOTH the segment length and
            // every per-ring Evaluate/ScaledVariance. But the exponent (SIdvBranchInfo+0x24) is a DEAD
            // parameter in the .spt era: the ctor defaults it to 1.0 and NO branch token writes it
            // (SIdvBranchInfo::Parse maps 6008..6017 exhaustively — 6013/6014 are the bark U/V texture
            // tilings, NOT an exponent, which is what the old t^Float6014 warp misread). pow(t,1) = t,
            // so linear it is — consistent with the live-trace slot-0 amplitudes at t=ring/(R−1).
            var pathT = t;
            var pathDistance = pathT * length;

            var taper = MathF.Max(0f, Eval(s, 6, pathT, rand, forceDraw: true)); // s6
            _ = Eval(s, 3, pathT, rand, forceDraw: true);                        // s3 (flex factor; parity)
            var rollContribution = s.Count > 8 && s[8] is not null
                ? -(Eval(s, 8, pathT, rand, forceDraw: true) - 0.5f) * 2f
                : 0f;                                                            // s8 roll
            var noiseA = ScaledVariance(s, 0, pathT, rand);
            var noiseB = ScaledVariance(s, 0, pathT, rand);

            var delta = pathDistance - previousDistance;
            pos += previousFrame.Direction * delta;
            frame = ApplyGravity(previousFrame, rollContribution, bendGain);
            frame = frame.MultiplyLocal(BranchFrame.RotateTwoAngles(noiseA, noiseB));

            previousDistance = pathDistance;

            // Ring radius = base radius × slot-6 taper. No fraction floor — the engine lets the taper reach 0
            // at the tip; only a tiny absolute epsilon guards against degenerate rings.
            var ringRadius = MathF.Max(0.01f, radius * taper);
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

        // Bark UV tilings (CIdvBranch::Compute 360 L2051-2062 → the p0/p1 of BuildCrossSection's UV
        // triple; the p2 U-twist term is DEAD — SIdvBranchInfo ctor zeroes [8] and no token writes it):
        //   U tile = 6013 raw when the absolute-U flag (6015) is set, else 6013 × radiusScale × 2π
        //            (repeats per unit of circumference);
        //   V tile = 6014 raw when the absolute-V flag (6016) is set, else 6014 × (length / treeSize)
        //            (repeats scale with the branch's relative length → uniform bark density).
        // The callee then emits u = aroundFrac × Utile, v = (pathT + barkHelp) × Vtile — the same
        // cumulative-spawn-fraction "help" the vertex-color dimming composes rides in as the V base.
        var barkUTile = branch.Bool6015 ? branch.Float6013 : branch.Float6013 * radius * MathF.Tau;
        var barkVTile = branch.Bool6016
            ? branch.Float6014
            : branch.Float6014 * (treeSize > 0f ? length / treeSize : 1f);

        // Frond gate (CIdvBranch::Compute head, all three binaries: 360 L1996, Oblivion FUN_007925b0
        // L613, FNV PC FUN_00b11050): when the .spt's 13000 section enables fronds, branches at
        // level >= frondLevel still generate fully — same draws, and their placed leaves persist —
        // but the engine destroys them instead of linking them as children (360 L2447-2462), so they
        // loft no bark tube and never enter BuildBranchLods' volume ranking. Bethesda's BSTreeModel
        // never consumes frond geometry, so the gated levels are simply invisible in-game.
        if (model.Frond is not { Enabled: true } frond || level < frond.Level)
        {
            collected.Add(new CollectedBranch(rings, vertsPerRing, importance, barkHelp, level == 0,
                barkUTile, barkVTile));
        }

        // A branch at level L spawns children at L+1. The LAST .spt branch record is the leaf-stub
        // TEMPLATE — never lofted as a tube; when a branch's child level would be that terminal record,
        // its "children" are leaves (buds), not branches (CIdvBranch::Compute L1308 terminal check uses
        // the CHILD level, L1397 ComputeBud). So leaves attach to the second-deepest level (e.g. the
        // whiteoak's twig level, Float6012=600), NOT the deepest, and record 3 is purely the leaf source.
        var terminalRecord = model.Branches.Count - 1;
        if (level + 1 < terminalRecord && level + 1 < levels)
        {
            SpawnChildren(model, level, levels, branch, rings, length, treeSize, opt, rng, rand, collected,
                placer, branchSeed, dimming, leafHelp, barkHelp, windRaw);
        }
        else
        {
            SpawnLeaves(branch, rings, length, treeSize, model, rng, placer, dimming, leafHelp, parentT,
                level, windRaw);
        }
    }

    private static void SpawnChildren(
        SptModel model, int level, int levels, SptBranch branch, List<BranchRing> rings,
        float length, float treeSize, SptGeometryOptions opt, SptRandom rng,
        Func<float, float, float> rand, List<CollectedBranch> collected, LeafPlacer placer,
        uint branchSeed, TreeDimming dimming, float leafHelp, float barkHelp, float windRaw)
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
            rng.SetPhase($"child L{level + 1} c{c}");
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

            // Vertex-color help composition (CIdvBranch::Compute L2435-2438 + this+4 store at L2016): the
            // child's help = lerp(parentHelp, 1, spawnT). The LEAF walk (LeafVertexColorHelp) recomposes
            // this chain WITHOUT squaring; the BARK help passed down to Compute is additionally SQUARED
            // when the spawning branch's level < 2 (trunk + first-level limbs).
            var childLeafHelp = spawnT * (1f - leafHelp) + leafHelp;
            var childBarkHelp = spawnT * (1f - barkHelp) + barkHelp;
            if (level < 2)
            {
                childBarkHelp *= childBarkHelp;
            }

            // The child inherits this branch's raw wind weight at its attachment point (B.3: the lerped
            // per-ring +0x44 at the spawn row — our raw is the same analytic ramp, evaluated directly).
            var childWindRaw = WindRawAt(level, windRaw, frac);
            GenerateBranch(model, level + 1, levels, spawnT, cpos, cframe, parentAttachRadius, treeSize, opt, rng, rand,
                collected, placer, childSeed, dimming, childLeafHelp, childBarkHelp, childWindRaw);
        }
    }

    // ---- Leaves (port of ComputeBud / MakeLeaf) -----------------------------------------

    private static void SpawnLeaves(
        SptBranch branch, List<BranchRing> rings, float length, float treeSize,
        SptModel model, SptRandom rng, LeafPlacer placer, TreeDimming dimming, float leafHelp,
        float branchSpawnT, int level, float windRaw)
    {
        if (model.Leaves.Count == 0 || rings.Count == 0)
        {
            return;
        }

        rng.SetPhase("leafspawn");

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

        // Per candidate, in CIdvBranch::Compute / ComputeBud / MakeLeaf draw order (verified vs the live trace):
        //   frac · budReach(Eval, forceDraw) · budSpin(-180,180) · [IsBlossom] · template(doubled pick) · RoomForLeaf
        //   · [on accept: 2 GetUniform draws]. Every draw advances the ONE shared RNG, so the next terminal
        //   branch stays aligned — deferring budSpin / RoomForLeaf / the on-accept draws was the residual gap.
        var (branchOrigin, _, _) = SampleAlong(rings, 0f);
        for (var i = 0; i < count; i++)
        {
            var frac = Math.Clamp(rng.Range(start, end), 0f, 1f);
            var spawnT = SpawnTemplateParam(frac, start, end);
            var (pos, frame, _) = SampleAlong(rings, frac);

            // budReach = the terminal stub's length spline (slot 4 = struct +0x60, ComputeBud L2793), ALWAYS
            // drawn; a SMALL twig step off the bough, not the leaf-card size.
            var budReach = MathF.Max(0.01f,
                Eval(budSplines, 4, spawnT, rng.Range, forceDraw: true) * treeSize / budReachDivisor);

            // Bud frame: random roll about the twig (ComputeBud GetUniform(-180,180) L2806) then a fixed 60°
            // declination (fStack_234 = 60.0). The card is stepped off the twig by budReach so leaves hug it.
            var budSpinDeg = rng.Range(-180f, 180f);
            var budFrame = frame.MultiplyLocal(BranchFrame.FromAxisAngle(Vector3.UnitX, budSpinDeg / DegPerRad));
            budFrame = budFrame.MultiplyLocal(BranchFrame.FixedAngleRotate(LeafBudDeclinationDeg));

            // IsBlossom's percent is ANCHOR-dependent (token 3001 = SIdvLeafInfo+0x2C, ctor default 1):
            // anchor != 0 gates on the bud's own raw percent along its branch, but anchor == 0 runs the
            // node walk whose loop bound is dead (breaks on iteration 1) yet whose SUBSTITUTION is live —
            // percent = the leaf-bearing BRANCH's spawn fraction on ITS parent (node[+4], Oblivion
            // FUN_0078f600). Verified draw-for-draw vs the TreeDogwoodSU trace (3001=0): the engine skips
            // the gate draw for whole low-spawned branches (6382 gate draws vs our old 7502), and the
            // blossom rate among gated candidates is exactly weight=0.23 (1426/6382).
            var anchor = model.LeafTable?.UInt3001 ?? 1;
            var gatePercent = anchor == 0 ? branchSpawnT : frac;
            var useBlossom = blossomTemplates is { Count: > 0 } && ShouldUseBlossom(gatePercent, model, rng);
            var pool = useBlossom ? blossomTemplates! : leafTemplates;

            // The engine picks from the DOUBLED entry list: the list partition pushes entries 2i and 2i+1
            // for every map i, and MakeLeaf draws trunc(GetUniform(0, K)) % (2·maps) over it — K = 1e6 for
            // the leaf pool but 1e5 for the blossom pool (360 literals 0x412E8480…/0x40F86A00…). Same
            // single draw either way, so the RNG stream matches; the low bit picks the H-mirror variant:
            // EVEN = authored UVs + pivot.x flipped (CLeafGeometry::Update), ODD = U-mirrored UVs +
            // authored pivot (CLeafGeometry::SetTextureCoords' second entry).
            var doubledIndex = (int)rng.Range(0f, useBlossom ? 100_000f : 1_000_000f) % (pool.Count * 2);
            var templateIndex = pool[doubledIndex / 2];
            var oddVariant = doubledIndex % 2 == 1;
            var template = model.Leaves[templateIndex];

            // RoomForLeaf spaces by the picked map's OWN entry: the pool lists hold doubled GLOBAL map
            // indices (Oblivion pool builder FUN_00791870 pushes 2i / 2i+1 partitioned by the map's
            // blossom flag), and MakeLeaf passes picked/2 straight into RoomForLeaf (FUN_007919d0 →
            // FUN_0078fd30, which reads map[+0x48/+0x4C] = sizeMid·Corner1). The earlier "pool-local
            // alias" reading of FNV L3233 was a misdecompile — iStack_120 held the pool VALUE, not the
            // pool slot; the placed leaf also stores the doubled GLOBAL index at leaf[+0x40] (the LOD
            // pair-merge pass re-reads it >>1 against the global table).
            var texCoords = templateIndex < model.LeafTextureCoords.Count
                ? model.LeafTextureCoords[templateIndex]
                : SptLeafTextureCoords.FullAtlas;

            // Canopy-depth dimmer (CIdvBranch::MakeLeaf L3241-3247 + ×255 quantization at L3264): the
            // leaf's tip-proximity e = lerp(help(branch), 1, spawnT) is CUBED, then the color scalar =
            // e³ + (1−e³)·(1−leafDimming) — leaves at branch tips of the outer canopy stay 1.0, interior
            // leaves darken toward (1 − leafDimming). Baked into the card's vertex color; the STLEAF
            // shaders multiply the whole lit result by it (the frc of the packed rock-index attribute).
            var dimmerByte = (byte)255;
            if (dimming.Enabled && dimming.LeafScalar > 0f)
            {
                var e = spawnT * (1f - leafHelp) + leafHelp;
                e = e * e * e;
                var dimmer = e + (1f - e) * (1f - dimming.LeafScalar);
                dimmerByte = (byte)Math.Clamp((int)(dimmer * 255f), 0, 255);
            }

            // The leaf's wind-matrix lerp weight = the spawning branch's weight at its attachment t
            // (B.3: ComputeBud → MakeLeaf pass the interpolated row weight down to CBillboardLeaf+0x44;
            // the shader consumes 1 − raw).
            var leafWindWeight = Math.Clamp(1f - WindRawAt(level, windRaw, frac), 0f, 1f);
            placer.TryPlace(rng, pos, branchOrigin, budFrame, template, texCoords, budReach,
                doubledIndex, oddVariant, dimmerByte, leafWindWeight);
        }

        // CIdvBranch::Compute clears the mode-1 RoomForLeaf list once a leaf-spawning branch finishes
        // (L2476-2483) — the rejection scope is per branch, not per tree.
        placer.EndBranchScope();
    }

    private static bool ShouldUseBlossom(float branchParam, SptModel model, SptRandom rng)
    {
        // Gate = percent > threshold(token 3000, SIdvLeafInfo+0x24) AND uniform(0,1) <= weight
        // (token 3002, +0x28); ctor defaults 0.75 / 0.8. The draw is UNCONDITIONAL once past the
        // threshold (Oblivion FUN_0078f600) — short-circuiting weight >= 1 would desync the RNG stream.
        var threshold = model.LeafSize > 0f ? model.LeafSize : DefaultBlossomThreshold;
        if (branchParam <= threshold)
        {
            return false;
        }

        var probability = model.LeafTable?.Float3002 ?? DefaultBlossomProbability;
        return rng.Range(0f, 1f) <= Math.Clamp(probability, 0f, 1f);
    }

    /// <summary>
    ///     Emit geometry for the leaves the <see cref="LeafPlacer" /> accepted during the loft (RoomForLeaf
    ///     rejection + the on-accept RNG draws already happened inline, in CIdvBranch::MakeLeaf order). Card
    ///     size = cardScale·Corner1 split around Corner0; the per-leaf wind-matrix weight + matrix slot
    ///     (design doc B.3) ride in the billboard card's packed bitangent.z.
    /// </summary>
    private static void EmitPlacedLeaves(
        List<PlacedLeaf> leaves, float cardScale, SptModel model, SptGeometryOptions opt,
        Dictionary<string, MeshBuffer> leafGroups)
    {
        if (leaves.Count == 0)
        {
            return;
        }

        // BSTreeModel::CreateLeafGeometry pairwise-strcmps the .spt leaf-map material names (0x8249C0F0
        // L634-656): any mismatch selects the card-local (u_j, v_{3−j}) texcoord path in AddLeafCard.
        var cardVerticalSwap = false;
        for (var i = 1; i < model.Leaves.Count && !cardVerticalSwap; i++)
        {
            cardVerticalSwap = !string.Equals(model.Leaves[i].Material, model.Leaves[0].Material,
                StringComparison.Ordinal);
        }

        foreach (var leaf in leaves)
        {
            var template = leaf.Template;
            // Wind-matrix lerp weight (rides in the packed attribute's FRACTION; the rock/rustle
            // phase slot is the integer part): the engine's branch-level ramp evaluated at the leaf's
            // spawn point (ComputeVertexWeight/ComputeHighLevelVertexWeight — design doc B.3),
            // replacing the old canopy-height stand-in.
            var windWeight = leaf.WindWeight;

            // Card width/height = cardScale·Corner1 (CSpeedTreeRT::Compute L3679, the size MID not the random
            // draw), further scaled by the leaf's own size scalar: MakeLeaf stores token-4001 base + the
            // on-accept jitter draw (±token 4002) per leaf, and CLeafGeometry::Update multiplies the corner
            // extents by it (the `s` in ±px·w·s). Shipped maps author 4001=(1,1,1), so this is 1 ± jitter.
            // CLeafGeometry::Update splits the size around Corner0 rather than as a symmetric half-size.
            // The doubled H-mirror variants: EVEN entries keep authored UVs but flip pivot.x (Update's
            // even-parity `1 − pivot.x`); ODD entries keep the authored pivot but U-mirror the UV pairs
            // (SetTextureCoords' second entry). Neither variant is "authored UVs + authored pivot".
            var width = cardScale * template.Corner1.X * MathF.Max(0.05f, template.Position.X + leaf.SizeJitter);
            var height = cardScale * template.Corner1.Y * MathF.Max(0.05f, template.Position.Y + leaf.SizeJitter);
            var pivotX = leaf.OddVariant ? template.Corner0.X : 1f - template.Corner0.X;
            var x0 = -pivotX * width;
            var x1 = (1f - pivotX) * width;
            var y0 = -template.Corner0.Y * height;
            var y1 = (1f - template.Corner0.Y) * height;

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
                continue;
            }

            // Token-10002 leaf UVs need v→1−v against the SHIPPED composite atlases: the compiled DDS
            // is stored V-flipped relative to the .spt's UV table (verified against
            // treedogwoodleavessu.dds — flower in the top half, leaves in the bottom, while the .spt
            // leaf quads span V∈[0,0.5]). The engine's own mechanism is SetTextureCoords' global
            // vSign=−1 (equivalent to 1−v under wrap sampling). ODD doubled-entries additionally get
            // the U-mirrored pair set (u of pairs 0↔1 and 2↔3 swapped, v's in place).
            var uv = FlipTexCoordV(leaf.TextureCoords);
            if (leaf.OddVariant)
            {
                uv = MirrorTexCoordU(uv);
            }
            var center = leaf.Center;
            var budDir = leaf.BudDir;

            // MakeLeaf's lighting normal: lerp(outward-from-branch-origin, bud direction, texture
            // variance [token 4002]), normalized — the per-leaf normal the STLEAF shaders consume as v1.
            // The origin is the leaf's own branch base (the IsBlossom-style ancestor walk is dead code in
            // both binaries). Replaces the invented up-dominant "canopy normal".
            var variance = Math.Clamp(template.Size, 0f, 1f);
            var outward = SafeNormalize(center - leaf.BranchOrigin, budDir);
            var normal = SafeNormalize(Vector3.Lerp(outward, budDir, variance), Vector3.UnitZ);

            if (opt.LeafBillboard)
            {
                // One card per leaf, encoded as center + signed offset; the GPU re-faces it to the camera per
                // frame.
                var (bbRight, bbUp) = BuildFrame(budDir);
                AddLeafCard(buffer, center, bbRight, bbUp, normal, uv, x0, x1, y0, y1, leaf.Dimmer,
                    cardVerticalSwap, billboard: true, windWeight: windWeight, slotBase: leaf.SlotBase,
                    windMatrixIndex: leaf.WindMatrixIndex);
                continue;
            }

            if (opt.LeafFaceDirection is { } face)
            {
                var faceDir = SafeNormalize(face, Vector3.UnitZ);
                var (fRight, fUp) = BuildFrame(faceDir);
                AddLeafCard(buffer, center, fRight, fUp, normal, uv, x0, x1, y0, y1, leaf.Dimmer, cardVerticalSwap);
                continue;
            }

            // Static fallback: crossed pair gives volume from any angle (the engine re-faces leaves in-shader).
            var (right, up) = BuildFrame(budDir);
            AddLeafCard(buffer, center, right, up, normal, uv, x0, x1, y0, y1, leaf.Dimmer, cardVerticalSwap);
            if (opt.CrossedLeafCards)
            {
                AddLeafCard(buffer, center, budDir, up, right, uv, x0, x1, y0, y1, leaf.Dimmer, cardVerticalSwap);
            }
        }
    }

    /// <summary>
    ///     Inline leaf placer: reproduces CIdvBranch::MakeLeaf's RoomForLeaf rejection + its two on-accept RNG
    ///     draws DURING the loft, so each terminal branch's leaf draws advance the ONE shared RNG before the
    ///     next branch generates (a deferred pass desynced every later branch — the residual leaf-count gap).
    ///     REJECTION SCOPE is mode-dependent: placement mode 1 tests the list the engine CLEARS after every
    ///     leaf-spawning branch (CIdvBranch::Compute L2476-2483 / Oblivion FUN_007925b0 tail erase), so
    ///     big-card leaves only reject against SAME-BRANCH siblings; mode 2 tests the caller's tree-global
    ///     list that is never cleared during the loft. Oracle (TreeDogwoodSU OBSE dump + trace, re-read
    ///     against the decompiled accept stream): the engine's LOD0 = 126 leaf-pool + 1077 blossom-pool
    ///     cards from 6076/1426 picks — the dump NIF's two leaf shapes are LOD0/LOD1 lists (the LOD1 285
    ///     comes from the pair-merge LOD builder FUN_007a3940, which we intentionally do not model).
    ///     Geometry is emitted afterward by <see cref="EmitPlacedLeaves" />.
    /// </summary>
    private sealed class LeafPlacer
    {
        private readonly List<Vector3>? _placedCenters; // RoomForLeaf rejection list; null when mode 0
        private readonly bool _perBranchScope;          // mode 1: engine clears the list per spawning branch
        private readonly float _spacingFactor;
        private readonly float _treeSizeMid;
        private readonly int _numRockGroups;

        public LeafPlacer(float spacingFactor, float treeSizeMid, uint placementMode, int numRockGroups)
        {
            _spacingFactor = spacingFactor;
            _treeSizeMid = treeSizeMid;
            _numRockGroups = Math.Max(1, numRockGroups);
            _placedCenters = placementMode != 0 ? [] : null;
            _perBranchScope = placementMode == 1;
        }

        /// <summary>End-of-branch hook (CIdvBranch::Compute L2476-2483): mode 1 clears its rejection list
        /// after every leaf-spawning branch, so the next branch's leaves start with an empty scope.</summary>
        public void EndBranchScope()
        {
            if (_perBranchScope)
            {
                _placedCenters?.Clear();
            }
        }

        public List<PlacedLeaf> Leaves { get; } = [];

        public void TryPlace(SptRandom rng, Vector3 pos, Vector3 branchOrigin, BranchFrame budFrame,
            SptLeaf template, SptLeafTextureCoords texCoords, float budReach,
            int doubledIndex, bool oddVariant, byte dimmer, float windWeight)
        {
            var budDir = budFrame.Direction;
            var center = pos + budDir * budReach;

            var accepted = true;
            if (_placedCenters is not null)
            {
                // RoomForLeaf: reject if any placed leaf is within max(sx,sy)·factor on ALL THREE axes
                // (strict inequalities; Oblivion FUN_0078fd30 = FNV 360 L3348-3385). The size pair is the
                // picked map's +0x48/+0x4C, which CTreeEngine::Compute set to Corner1(token 4005)·sizeMid
                // — the SAME sizeMid our loft scales geometry by, so spacing and positions share units.
                // (The .spt-parsed Corner2 default ≡ Corner1·Float2006 only matches when the host size is
                // the authored size; our loft is Float2006·EngineWorldScale, so Corner1·_treeSizeMid.)
                var spacing = MathF.Max(template.Corner1.X, template.Corner1.Y) * _treeSizeMid * _spacingFactor;
                accepted = HasRoomForLeaf(_placedCenters, center, spacing);
                rng.Trace?.Leaf(_placedCenters.Count, spacing, accepted);
            }

            if (!accepted)
            {
                return;
            }

            // On accept the engine's MakeLeaf draws two more uniforms (Oblivion FUN_007919d0 L1518+1552,
            // same shape in the FNV 360 text): the rock-angle pick GetUniform(0,1e4), then the per-leaf
            // SIZE JITTER GetUniform(−4002, +4002) whose value is ADDED to the token-4001 size base and
            // stored on the leaf — CLeafGeometry::Update multiplies the card extents by that scalar.
            // (The GetUniform state advance is bounds-independent, so the stream stays aligned either
            // way — but the VALUE sizes the card, previously discarded with invented ±0.1 bounds.)
            // The 1e4 draw IS the leaf's rocking-group pick (MakeLeaf: ftol(uniform(0,1e4)) %
            // numRockGroups) — the group joins the doubled map index in the LeafBase phase slot
            // the STLEAF wind math reads (design doc A.5: cardSlot = (texIdx·nRock + rock)·4 + corner).
            var rockGroup = (int)rng.Range(0f, 10000f) % _numRockGroups;
            var slotBase = (doubledIndex * _numRockGroups + rockGroup) % 12;
            var sizeJitter = rng.Range(-template.Size, template.Size);

            _placedCenters?.Add(center);
            // Wind-matrix slot: the same rocking-group draw is the leaf's wind group (CBillboardLeaf+0x48);
            // CLeafGeometry::InitLods maps it to a matrix as group % numMatrices(=4).
            Leaves.Add(new PlacedLeaf(pos, branchOrigin, center, budDir, template, texCoords, oddVariant, dimmer,
                sizeJitter, slotBase, windWeight, rockGroup % 4));
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
    private static void EmitDecimatedBranches(
        List<CollectedBranch> branches, SptLodInfo? lod, MeshBuffer bark, TreeDimming dimming)
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
            // CTreeEngine::BuildBranchLods (360 0x82989890, decompiled): rank ALL linked branches (frond-
            // gated ones were never linked), then
            //   1. split out a GUARANTEED set: volume > max·(1 − guaranteeFraction)   [token 9014, ctor 0.05]
            //   2. per remaining branch draw u = GetUniform(0, demotionRandomness)    [token 9013, ctor 0]
            //      and zero its sort key when (1−u)·v + u·max < 0 (only reachable for negative 9013);
            //      the draw comes from a PRIVATE Reseed(-1) = time-seeded RNG — never the tree stream, and
            //      nondeterministic in-engine, so a fixed-seed stand-in is as engine-faithful as any run
            //   3. sort both sets by key, front-insert the guaranteed set (one-by-one, so its order
            //      reverses), then keep branches in order until cumulative REAL volume reaches
            //      fraction·total (the crossing branch is kept).
            // The sort comparator direction is inferred DESCENDING (LOD0 visually keeps trunk + main limbs;
            // ascending would keep twigs first).
            var total = 0f;
            var max = 0f;
            foreach (var b in branches)
            {
                total += b.Importance;
                max = MathF.Max(max, b.Importance);
            }

            var demotion = lod?.BranchDemotionRandomness ?? 0f;
            var guaranteeThreshold = max * (1f - (lod?.BranchGuaranteeFraction ?? 0.05f));
            var demotionRng = new SptRandom(uint.MaxValue);
            var guaranteed = new List<CollectedBranch>();
            var rest = new List<(CollectedBranch Branch, float Key)>();
            foreach (var b in branches)
            {
                if (b.Importance > guaranteeThreshold)
                {
                    guaranteed.Add(b);
                    continue;
                }

                var u = demotionRng.Range(0f, demotion);
                var key = (1f - u) * b.Importance + u * max < 0f ? 0f : b.Importance;
                rest.Add((b, key));
            }

            // Ascending = the engine's descending guaranteed sort reversed by its per-item insert-at-begin.
            var ordered = guaranteed.OrderBy(b => b.Importance)
                .Concat(rest.OrderByDescending(r => r.Key).Select(r => r.Branch));

            var target = total * keepFraction;
            var selected = new List<CollectedBranch>(branches.Count);
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
                // Bark vertex dimming (CIdvBranch::BuildCrossSection L2582-2590): d = lerp(help, 1, t),
                // squared on the trunk (the flag'd square; children carry the level<2-squared help
                // instead), then color = d·branchDim + (1 − branchDim). Neutral (255) when no CNAM
                // BranchDimming value reached us.
                var dimmerByte = (byte)255;
                if (dimming.BranchScalar > 0f)
                {
                    var d = ring.PathT * (1f - b.BarkHelp) + b.BarkHelp;
                    if (b.IsTrunk)
                    {
                        d *= d;
                    }

                    var dim = d * dimming.BranchScalar + (1f - dimming.BranchScalar);
                    dimmerByte = (byte)Math.Clamp((int)(dim * 255f), 0, 255);
                }

                // Bark UVs (CIdvBranch::BuildCrossSection vertex loop, 360 L2601-2605):
                //   u = aroundFrac × Utile (the p2·path twist term is dead — ctor-zeroed, token-less)
                //   v = (pathT + barkHelp) × Vtile (help = the cumulative spawn fraction, so V roughly
                //       continues up parent→child chains).
                prevRing = EmitRing(bark, ring.Pos, ring.Frame, ring.Radius, b.VertsPerRing,
                    (ring.PathT + b.BarkHelp) * b.BarkVTile, b.BarkUTile, prevRing, dimmerByte);
            }
        }
    }

    private static int[]? EmitRing(
        MeshBuffer bark, Vector3 center, BranchFrame frame, float radius, int radial, float v, float uTile,
        int[]? prevRing, byte dimmer)
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
            ring[k] = bark.Add(vertex, normal, new Vector2(k / (float)radial * uTile, v), Vector3.Zero, Vector3.Zero,
                dimmer);
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
        float x0, float x1, float y0, float y1, byte dimmer, bool cardVerticalSwap,
        bool billboard = false, float windWeight = 0f, int slotBase = 0, int windMatrixIndex = 0)
    {
        var p00 = center + right * x0 + up * y0;
        var p10 = center + right * x1 + up * y0;
        var p11 = center + right * x1 + up * y1;
        var p01 = center + right * x0 + up * y1;

        // UV pair → corner pairing (BSTreeModel::CreateLeafGeometry): vertex j takes corner slot (j+2)&3
        // from CLeafGeometry::Update's table — the spatial walk LB, RB, RT, LT — and texcoord pair j
        // VERBATIM, except when the .spt's leaf-map material names are NOT all identical (the pairwise
        // strcmp at 0x8249C0F0 L634-656): then vertex j gets (u_j, v_{3−j}) — a card-local vertical swap
        // (L869-878). For the axis-aligned rectangles shipped .spts author, the swapped form equals full
        // pair reversal, which is why the dogwood-class (multi-map) trees pinned the "vertical mirror";
        // single-map trees take the verbatim path.
        Vector2 uvLB, uvRB, uvRT, uvLT;
        if (cardVerticalSwap)
        {
            uvLB = new Vector2(uv[0].X, uv[3].Y);
            uvRB = new Vector2(uv[1].X, uv[2].Y);
            uvRT = new Vector2(uv[2].X, uv[1].Y);
            uvLT = new Vector2(uv[3].X, uv[0].Y);
        }
        else
        {
            uvLB = uv[0];
            uvRB = uv[1];
            uvRT = uv[2];
            uvLT = uv[3];
        }

        if (billboard)
        {
            // Encode the card as center (tangent) + signed 2D card-space offset (bitangent) so the GPU
            // leaf-billboard VS rebuilds it camera-facing (SpeedTree's CPU-side leaf billboard, moved to
            // the shader). The baked positions still hold a real 3D quad for bounds + the CPU still path.
            // bitangent.z = per-leaf wind weight (a [0,1] blend factor, NOT a size — ApplyHeightScale
            // leaves it untouched), consumed by the VS's wind sway.
            // bitangent.z packs the engine's STLEAF v3.z, widened for the wind-matrix slot: integer =
            // the LeafBase phase slot (slotBase·4 + the engine's (j+2)&3 corner slot for our
            // LB,RB,RT,LT emission order, 0..47) + 48·windMatrixIndex (0..3 → packed 0..191, exact in
            // fp32), fraction = the wind-matrix lerp weight. The VS splits: idx = packed/48,
            // slot = packed%48, weight = frac.
            var wf = Math.Clamp(windWeight, 0f, 0.995f);
            var mtx = windMatrixIndex * 48;
            var v00b = buf.Add(p00, normal, uvLB, center, new Vector3(x0, y0, mtx + slotBase * 4 + 2 + wf), dimmer);
            var v10b = buf.Add(p10, normal, uvRB, center, new Vector3(x1, y0, mtx + slotBase * 4 + 3 + wf), dimmer);
            var v11b = buf.Add(p11, normal, uvRT, center, new Vector3(x1, y1, mtx + slotBase * 4 + 0 + wf), dimmer);
            var v01b = buf.Add(p01, normal, uvLT, center, new Vector3(x0, y1, mtx + slotBase * 4 + 1 + wf), dimmer);
            buf.Triangle(v00b, v10b, v11b);
            buf.Triangle(v00b, v11b, v01b);
            return;
        }

        var v00 = buf.Add(p00, normal, uvLB, Vector3.Zero, Vector3.Zero, dimmer);
        var v10 = buf.Add(p10, normal, uvRB, Vector3.Zero, Vector3.Zero, dimmer);
        var v11 = buf.Add(p11, normal, uvRT, Vector3.Zero, Vector3.Zero, dimmer);
        var v01 = buf.Add(p01, normal, uvLT, Vector3.Zero, Vector3.Zero, dimmer);
        buf.Triangle(v00, v10, v11);
        buf.Triangle(v00, v11, v01);
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static float Eval(IReadOnlyList<SptBezierSpline?> splines, int slot, float param,
        Func<float, float, float> rand, bool forceDraw = false) =>
        slot < splines.Count && splines[slot] is { } spline ? spline.Evaluate(param, rand, forceDraw) : 0f;

    private static float ScaledVariance(IReadOnlyList<SptBezierSpline?> splines, int slot, float param,
        Func<float, float, float> rand) =>
        slot < splines.Count && splines[slot] is { } spline ? spline.ScaledVariance(param, rand) : 0f;

    private static float SpawnTemplateParam(float frac, float start, float end) =>
        MathF.Abs(end - start) > 1e-6f ? Math.Clamp((frac - start) / (end - start), 0f, 1f) : 1f;

    /// <summary>
    ///     Soft spherical canopy lighting normal for a leaf card at <paramref name="center" />: outward from
    ///     the trunk axis (local x=y=0) blended with a strong up bias. This shades the canopy like a rounded
    ///     volume — bright on top/sun-side, gently darker beneath — instead of letting each billboard take its
    ///     random bud-growth direction as the lighting normal (which, through the shader's <c>saturate(N·L)</c>,
    ///     sent every leaf whose bud faced away from the sun to near-black). SpeedTree likewise lights leaf
    ///     cards from a smoothed normal rather than the twig direction.
    /// </summary>
    private static Vector3 CanopyNormal(Vector3 center)
    {
        // Outward-from-trunk-axis as a UNIT vector FIRST. center is in world units — hundreds at the ×10 loft
        // scale — so blending the raw vector with the unit up dwarfs the up term and the normal collapses to
        // horizontal, leaving every shadow-side card facing away from the sun (saturate(N·L)=0 → dim ambient →
        // the BLACK leaf cards). Normalizing makes the blend an actual up-dominant direction (up 1.0, outward
        // 0.4): the canopy is lit primarily from above with a gentle outward lean for volume, so no card faces
        // fully away from the light.
        var outward = SafeNormalize(new Vector3(center.X, center.Y, 0f), Vector3.Zero);
        return SafeNormalize(outward * 0.4f + Vector3.UnitZ, Vector3.UnitZ);
    }

    /// <summary>Flip the V (texture-Y) of all four leaf-card UV corners: the .spt stores leaf UVs with a
    /// bottom-left origin, our rasterizer samples top-left. See the call site in <see cref="EmitPlacedLeaves" />.</summary>
    private static SptLeafTextureCoords FlipTexCoordV(SptLeafTextureCoords uv) => new(
        new Vector2(uv.Corner0.X, 1f - uv.Corner0.Y),
        new Vector2(uv.Corner1.X, 1f - uv.Corner1.Y),
        new Vector2(uv.Corner2.X, 1f - uv.Corner2.Y),
        new Vector2(uv.Corner3.X, 1f - uv.Corner3.Y));

    /// <summary>The ODD doubled-entry UV set from <c>CLeafGeometry::SetTextureCoords</c>: the u components
    /// of pairs 0↔1 and 2↔3 swap while every v stays in place — the horizontal mirror of the leaf map.</summary>
    private static SptLeafTextureCoords MirrorTexCoordU(SptLeafTextureCoords uv) => new(
        new Vector2(uv.Corner1.X, uv.Corner0.Y),
        new Vector2(uv.Corner0.X, uv.Corner1.Y),
        new Vector2(uv.Corner3.X, uv.Corner2.Y),
        new Vector2(uv.Corner2.X, uv.Corner3.Y));

    // Gravity/bend with PRECOMPUTED slot values: the per-ring slot evals (slot 1 bend gain, slot 8 roll) are
    // drawn in the ring loop so they advance the shared RNG in the engine's exact order (see GenerateBranch's
    // per-ring loft block); this only applies them. rollContribution = -(slot8Eval - 0.5)·2 (0 when no slot 8).
    //
    // CIdvBranch::Compute per-ring bend (360 L2261-2274, symbolized; Oblivion FUN_007925b0 L842-874 agrees):
    // the engine's reference vector is world-DOWN — Oblivion .data 0x00B2B724 = (0, 0, −1), growth basis
    // 0x00B2B718 = (1, 0, 0) — so the angle is measured FROM DOWN (180° − declination) and the axis is
    // cross(dir, DOWN):
    //   RotateAxisFromIdentity(roll(s8) · bendGain(s1) · AngleBetween(dir, DOWN) · weight,
    //                          normalize(cross(dir, DOWN)))
    //   weight = 1 − |90° − angle| / 90°  (symmetric — same value from either pole).
    // With the typically-negative s8 roll the engine curls limbs UPWARD; measuring from UP (the pre-derivation
    // port) flipped the bend, arcing them down and out (vine maple bark dove to −0.28·height below origin and
    // spread 0.72·height radially where the engine stays above ground at 0.43·height — the sprawl defect).
    // The engine has NO trunk restoring/damping term: with the bend sign correct, trace-exact noise draws
    // hold trunks upright on their own (japanesemaple/sugarmaple/willowoak render-verified), so the old
    // trunk-only correctives are deleted.
    private static BranchFrame ApplyGravity(BranchFrame frame, float rollContribution, float bendGain)
    {
        var dir = frame.Direction;
        var angleDeg = MathF.Acos(Math.Clamp(Vector3.Dot(dir, -Vector3.UnitZ), -1f, 1f)) * DegPerRad;
        var weight = 1f - MathF.Abs(90f - angleDeg) * 0.011111111f;
        var bendDeg = rollContribution * bendGain * angleDeg * weight;
        var axis = Vector3.Cross(dir, -Vector3.UnitZ);
        return axis.LengthSquared() > 1e-10f && MathF.Abs(bendDeg) > 1e-6f
            ? frame.MultiplyWorld(BranchFrame.FromAxisAngle(axis, bendDeg / DegPerRad))
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
        IReadOnlyList<BranchRing> Rings, int VertsPerRing, float Importance, float BarkHelp, bool IsTrunk,
        float BarkUTile, float BarkVTile);

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
            var a = angleAdeg / DegPerRad;
            var b = angleBdeg / DegPerRad;
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
            var a = angleDeg / DegPerRad;
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

    /// <summary>An ACCEPTED leaf card (RoomForLeaf passed during the loft): the bud attach point (Pos, for
    /// canopy-height wind), the owning branch's base origin (for the MakeLeaf lighting normal), the card
    /// center + facing direction, which template / atlas UVs it uses, and the on-accept size jitter
    /// (MakeLeaf's GetUniform(−4002, +4002), added to the token-4001 size base at emission).
    /// Geometry is built from these afterward by <see cref="EmitPlacedLeaves" />.</summary>
    private readonly record struct PlacedLeaf(
        Vector3 Pos, Vector3 BranchOrigin, Vector3 Center, Vector3 BudDir, SptLeaf Template,
        SptLeafTextureCoords TextureCoords, bool OddVariant, byte Dimmer, float SizeJitter, int SlotBase,
        float WindWeight, int WindMatrixIndex);

    /// <summary>Per-tree canopy-depth dimming parameters (SIdvLeafInfo +0/+4/+8): enabled = .spt token 3009,
    /// leaf scalar = TREE CNAM LeafDimming (fallback token 3010), branch scalar = TREE CNAM BranchDimming
    /// (no .spt source — 0/neutral without a record).</summary>
    private readonly record struct TreeDimming(bool Enabled, float LeafScalar, float BranchScalar);

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

        /// <summary>Optional diagnostic sink: when set, every <see cref="Range" /> draw and
        ///     <see cref="Reseed" /> is logged in the engine trace's format for 1:1 sequence diffing.
        ///     Null in production (no overhead). See <see cref="SptRngTrace" />.</summary>
        public SptRngTrace? Trace { get; set; }

        /// <summary>Set the trace's current phase label (no-op when not tracing).</summary>
        public void SetPhase(string phase)
        {
            if (Trace is not null)
            {
                Trace.Phase = phase;
            }
        }

        public SptRandom(uint seed)
        {
            Reseed(seed);
        }

        public void Reseed(uint seed)
        {
            var oldState = _state;
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

            Trace?.Reseed(seed, oldState);
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

        // CIdvRandom::GetUniform: min + (float)((double)(max-min) * Next()). The subtract is float, promoted to
        // double for the multiply with the double Next(), the product cast back to float, then added to min.
        public float Range(float min, float max)
        {
            if (Trace is null)
            {
                return min + (float)((double)(max - min) * NextDouble());
            }

            var stateBefore = _state;
            var result = min + (float)((double)(max - min) * NextDouble());
            Trace.Uniform(stateBefore, min, max, result);
            return result;
        }

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
        private readonly List<byte> _colors = [];
        private bool _hasTangents;
        private bool _hasDimming;
        private readonly List<ushort> _indices = [];

        public int VertexCount => _positions.Count / 3;

        public bool CanAdd(int verts) => VertexCount + verts <= MaxVertices;

        public int Add(Vector3 position, Vector3 normal, Vector2 uv) =>
            Add(position, normal, uv, Vector3.Zero, Vector3.Zero, 255);

        /// <summary>Adds a vertex carrying the leaf-billboard payload in the tangent/bitangent slots:
        /// <paramref name="tangent" /> = the card center (pivot), <paramref name="bitangent" /> = the
        /// signed 2D card-space corner offset. The GPU leaf-billboard VS rebuilds the quad from these.
        /// <paramref name="dimmer" /> = the canopy-depth dimming scalar (engine ×255 quantization), baked
        /// as the vertex color the pixel shader multiplies into the lit result.</summary>
        public int Add(Vector3 position, Vector3 normal, Vector2 uv, Vector3 tangent, Vector3 bitangent,
            byte dimmer)
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
            _colors.Add(dimmer);
            _colors.Add(dimmer);
            _colors.Add(dimmer);
            _colors.Add(255);
            if (tangent != Vector3.Zero || bitangent != Vector3.Zero)
            {
                _hasTangents = true;
            }

            if (dimmer != 255)
            {
                _hasDimming = true;
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
                // Canopy-depth dimming rides the vertex color (the engine bakes the CBillboardLeaf /
                // BuildCrossSection dimmer into the leaf/bark vertex streams); only emitted when some
                // vertex actually dims, so undimmed trees carry no color arrays.
                VertexColors = _hasDimming ? [.. _colors] : null,
                UseVertexColors = _hasDimming,
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
