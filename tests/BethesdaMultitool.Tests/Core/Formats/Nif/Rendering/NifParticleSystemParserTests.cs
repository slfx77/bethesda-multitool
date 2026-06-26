using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Parses the real FXDustWhirlWind01 particle NIF (FNV PC final meshes BSA) and pins the
///     <see cref="NifParticleSystemParser" /> output: the NiParticleSystem's modifier chain, the mesh
///     emitter, and the emitter-volume meshes that must be suppressed from rendering. Sample-gated — skips
///     when the FNV meshes BSA isn't present (e.g. CI). Layout independently verified against the block-46
///     hex (Data ref 51, World Space, 9 modifiers = blocks 52-60).
/// </summary>
public sealed class NifParticleSystemParserTests
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";
    private const string FxDustPath = @"meshes\effects\ambient\fxdustwhirlwind01.nif";

    [Fact]
    public void Parse_FxDustWhirlwind_FindsModifierChainEmitterAndSuppressesEmitterMesh()
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

        // Find a NiParticleSystem block and parse it.
        var psIndex = -1;
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (NifParticleSystemParser.IsParticleSystem(nif.Blocks[i].TypeName))
            {
                psIndex = i;
                break;
            }
        }

        Assert.True(psIndex >= 0, "no NiParticleSystem block found");

        var def = NifParticleSystemParser.Parse(data, nif, psIndex);
        Assert.NotNull(def);
        Assert.True(def!.WorldSpace);
        Assert.True(def.Capacity > 0, "NiPSysData capacity should be non-zero");

        // FXDust's first system has 9 modifiers (AgeDeath, MeshEmitter, Spawn, GrowFade, Color, Rotation,
        // Bomb, Position, BoundUpdate).
        Assert.True(def.Modifiers.Count >= 8, $"expected the full modifier chain, got {def.Modifiers.Count}");

        // The emitter is a mesh emitter with at least one emitter-volume mesh.
        Assert.NotNull(def.Emitter);
        Assert.Equal(ParticleEmitterShape.Mesh, def.Emitter!.Shape);
        Assert.NotEmpty(def.Emitter.EmitterMeshIndices);

        // The vortex (Bomb) + grow/fade modifiers are recognised with their params.
        Assert.Contains(def.Modifiers, m => m.Kind == ParticleModifierKind.Bomb);
        var growFade = Assert.IsType<GrowFadeModifierDefinition>(
            Assert.Single(def.Modifiers, m => m.Kind == ParticleModifierKind.GrowFade));
        Assert.True(growFade.GrowTime >= 0f && growFade.FadeTime >= 0f);

        // The emitter-volume meshes are collected for render suppression (so they don't draw as blobs).
        var suppressed = NifParticleSystemParser.CollectEmitterMeshShapes(data, nif);
        Assert.NotEmpty(suppressed);
        foreach (var meshIndex in def.Emitter.EmitterMeshIndices)
        {
            Assert.Contains(meshIndex, suppressed);
        }
    }
}
