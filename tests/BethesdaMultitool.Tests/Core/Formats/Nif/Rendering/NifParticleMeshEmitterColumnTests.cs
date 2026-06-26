using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     End-to-end check that a mesh-emitter particle system (FXDustWhirlWind01 — the dust column emits from a
///     <c>NiPSysMeshEmitter</c> volume mesh, not a point) bakes a cloud that FILLS the column volume rather
///     than clustering at the origin as a single blob. Validates
///     <c>NifParticleSystemExtractor.ResolveMeshEmitterBounds</c>: the baked <c>ParticleCloud</c> AABB should
///     approximate the emitter mesh's own world-space AABB (computed independently here with the same geometry
///     readers). Sample-gated — skips when the FNV meshes BSA isn't present (e.g. CI).
/// </summary>
public sealed class NifParticleMeshEmitterColumnTests
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";
    private const string FxDustPath = @"meshes\effects\ambient\fxdustwhirlwind01.nif";

    [Fact]
    public void Extract_FxDustWhirlwind_ParticleCloudFillsEmitterMeshColumn()
    {
        var bsaPath = SampleFileFixture.FindSamplePath(MeshesBsaRelative);
        Assert.SkipWhen(bsaPath is null, "FNV PC final meshes BSA not available");

        using var archives = MeshArchiveSet.Open(bsaPath!, null, enableFuzzy: false, includeLooseFiles: false);
        Assert.True(archives.TryExtractFile(FxDustPath, out var data, out _), "FXDust NIF not found in BSA");

        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);
        if (nif!.IsBigEndian)
        {
            var converted = NifConverter.Convert(data);
            Assert.True(converted.Success && converted.OutputData != null);
            data = converted.OutputData!;
            nif = NifParser.Parse(data)!;
        }

        // Bake particles through the real worldspace path (collectBillboards). treatRootsAsIdentity:false keeps
        // everything in the NIF's own world frame, so the cloud and the emitter-mesh AABB are directly comparable.
        var model = NifGeometryExtractor.Extract(data, nif, collectBillboards: true);
        Assert.NotNull(model);

        var clouds = model!.Submeshes.Where(s => s.ShapeName == "ParticleCloud").ToList();
        Assert.NotEmpty(clouds);

        var cloud = Aabb.FromSubmeshes(clouds.Select(c => c.Positions));
        var meshes = EmitterMeshWorldAabb(data, nif);
        Assert.True(meshes.Valid, "could not compute emitter mesh AABB");
        Assert.True(meshes.Extent.Length() > 1f, "emitter column should have real size");

        // Particles SPAWN across the emitter column at every age and only then drift (velocity + bomb-vortex +
        // gravity), so the steady-state cloud always CONTAINS the column — the youngest particles still sit on
        // it. Point emission could never cover the whole column box (it would be a drift cone from one point),
        // so "cloud ⊇ column" is the discriminating signal that volume emission is wired up. Tolerance absorbs
        // grow-fade thinning at the spawn edges.
        var tol = meshes.Extent * 0.2f + new Vector3(6f);
        Assert.True(
            cloud.Min.X <= meshes.Min.X + tol.X && cloud.Max.X >= meshes.Max.X - tol.X &&
            cloud.Min.Y <= meshes.Min.Y + tol.Y && cloud.Max.Y >= meshes.Max.Y - tol.Y &&
            cloud.Min.Z <= meshes.Min.Z + tol.Z && cloud.Max.Z >= meshes.Max.Z - tol.Z,
            $"cloud [{cloud.Min}..{cloud.Max}] should contain the emitter column [{meshes.Min}..{meshes.Max}] " +
            $"(tol {tol}); a point emitter would not.");
    }

    /// <summary>World-space AABB of every emitter-volume mesh, computed with the same readers the extractor uses.</summary>
    private static Aabb EmitterMeshWorldAabb(byte[] data, NifInfo nif)
    {
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var worldTransforms = new Dictionary<int, Matrix4x4>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap);
        NifSceneGraphWalker.ComputeWorldTransforms(data, nif, nodeChildren, worldTransforms);

        var box = Aabb.Empty();
        foreach (var meshIndex in NifParticleSystemParser.CollectEmitterMeshShapes(data, nif))
        {
            if (meshIndex < 0 || meshIndex >= nif.Blocks.Count)
            {
                continue;
            }

            var shape = nif.Blocks[meshIndex];
            var dataRef = NifBlockParsers.ParseShapeDataRef(
                data, shape, nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
            if (dataRef < 0 || dataRef >= nif.Blocks.Count)
            {
                continue;
            }

            var world = worldTransforms.TryGetValue(meshIndex, out var w) ? w : Matrix4x4.Identity;
            var dataBlock = nif.Blocks[dataRef];
            var sub = dataBlock.TypeName switch
            {
                "NiTriStripsData" => NifBlockParsers.ExtractTriStripsData(
                    data, dataBlock, nif.IsBigEndian, nif.BsVersion, nif.BinaryVersion, world),
                "NiTriShapeData" => NifBlockParsers.ExtractTriShapeData(
                    data, dataBlock, nif.IsBigEndian, nif.BsVersion, nif.BinaryVersion, world),
                _ => null,
            };

            if (sub?.Positions is { Length: >= 3 } pos)
            {
                box.Add(pos);
            }
        }

        return box;
    }

    private struct Aabb
    {
        public Vector3 Min;
        public Vector3 Max;
        public bool Valid;

        public Vector3 Extent => Valid ? Max - Min : Vector3.Zero;

        public static Aabb Empty() => new()
        {
            Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue), Valid = false,
        };

        public void Add(float[] positions)
        {
            for (var v = 0; v + 2 < positions.Length; v += 3)
            {
                var p = new Vector3(positions[v], positions[v + 1], positions[v + 2]);
                Min = Vector3.Min(Min, p);
                Max = Vector3.Max(Max, p);
                Valid = true;
            }
        }

        public static Aabb FromSubmeshes(IEnumerable<float[]> positionArrays)
        {
            var box = Empty();
            foreach (var p in positionArrays)
            {
                box.Add(p);
            }

            return box;
        }
    }
}
