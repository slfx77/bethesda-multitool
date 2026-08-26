using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Particles;

/// <summary>
///     Parses the real FXDustWhirlWind01 particle NIF (FNV PC final meshes BSA) and pins the
///     <see cref="NifParticleSystemParser" /> output: the NiParticleSystem's modifier chain, the mesh
///     emitter, and the emitter-volume meshes that must be suppressed from rendering. Sample-gated — skips
///     when the FNV meshes BSA isn't present (e.g. CI). Layout independently verified against the block-46
///     hex (Data ref 51, World Space, 9 modifiers = blocks 52-60).
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifParticleSystemParserTests
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";

    private const string FxDustPath = @"meshes\effects\ambient\fxdustwhirlwind01.nif";

    private const string HowitzerPath = @"meshes\vehicles\nvnellisartillery\nvnellisartillery.nif";

    public NifParticleSystemParserTests()
    {
        BucketBTestGuard.SkipUnlessEnabled();
    }

    [Fact]
    public void Parse_FxDustWhirlwind_FindsModifierChainEmitterAndSuppressesEmitterMesh()
    {
        var bsaPath = SampleFileFixture.FindSamplePath(MeshesBsaRelative);
        Assert.SkipWhen(bsaPath is null, "FNV PC final meshes BSA not available");

        using var archives = MeshArchiveSet.Open(bsaPath!, null, false);
        Assert.True(archives.TryExtractFile(FxDustPath, out var data, out _), "FXDust NIF not found in BSA");

        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);
        if (nif!.IsBigEndian)
        {
            var converted = NifConverter.Convert(data);
            // Split: a compound assert cannot say which half failed.
            Assert.True(converted.Success, converted.ErrorMessage ?? "NifConverter reported failure.");
            Assert.NotNull(converted.OutputData);
            data = converted.OutputData!;
            nif = NifParser.Parse(data)!;
        }

        // FXDust contains two particle systems: the 19-particle face-surface whirlwind and a
        // separate 200-particle sprite system whose NiPSysData authors the 4x4 atlas. Preserve
        // those independent source contracts instead of assigning one system's atlas to another.
        var systems = nif.Blocks.Select((block, index) => (block, index))
            .Where(x => NifParticleSystemParser.IsParticleSystem(x.block.TypeName))
            .Select(x => NifParticleSystemParser.Parse(data, nif, x.index))
            .OfType<ParticleSystemDefinition>()
            .ToArray();
        Assert.NotEmpty(systems);

        var def = Assert.Single(systems, system => system.Capacity == 19);
        Assert.True(def.WorldSpace);
        Assert.Equal(19, def.Capacity);

        // FXDust's first system has 9 modifiers (AgeDeath, MeshEmitter, Spawn, GrowFade, Color, Rotation,
        // Bomb, Position, BoundUpdate).
        Assert.True(def.Modifiers.Count >= 8, $"expected the full modifier chain, got {def.Modifiers.Count}");

        // The emitter is a mesh emitter with at least one emitter-volume mesh.
        Assert.NotNull(def.Emitter);
        Assert.Equal(ParticleEmitterShape.Mesh, def.Emitter!.Shape);
        Assert.NotEmpty(def.Emitter.EmitterMeshIndices);
        Assert.Equal(ParticleVelocityType.UseNormals, def.Emitter.VelocityType);
        Assert.Equal(ParticleEmitFrom.FaceSurface, def.Emitter.EmitFrom);
        var wispsRate = Assert.IsType<ParticleRateControllerDefinition>(def.Emitter.BirthRateController);
        Assert.NotNull(wispsRate.SequenceTiming);
        Assert.Equal(7.5f, wispsRate.Sample(0f), 4);

        var atlasDef = Assert.Single(systems, system => system.SubtextureOffsets.Count == 16);
        var debrisRate = Assert.IsType<ParticleRateControllerDefinition>(
            Assert.IsType<ParticleEmitterDefinition>(atlasDef.Emitter).BirthRateController);
        Assert.NotNull(debrisRate.SequenceTiming);
        Assert.Equal(60f, debrisRate.Sample(0f), 4);
        var expectedCells = Enumerable.Range(0, 16)
            .Select(i => new Vector4(
                i % 4 * 0.25f, i / 4 * 0.25f, 0.25f, 0.25f))
            .ToArray();
        Assert.Equal(expectedCells, atlasDef.SubtextureOffsets);
        Assert.Equal(16, atlasDef.SubtextureOffsets.Distinct().Count());
        Assert.All(atlasDef.SubtextureOffsets, rect =>
        {
            Assert.Equal(0.25f, rect.Z, 4);
            Assert.Equal(0.25f, rect.W, 4);
        });

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

    /// <summary>
    ///     User report 2026-08-10: FortHowitzer (ACTI 0x00125C2A, this NIF) showed firing smoke at
    ///     rest. Its auto-playing Idle sequence binds each emitter's BirthRate as a NONZERO constant
    ///     pose (PCloud01 2250 / PCloud02 300 / PCloud03 90) and gates the smoke solely through the
    ///     NiPSysEmitterCtlr's second controlled block — EmitterActive, constant FALSE at idle. Only
    ///     the activation-triggered Forward sequence pulses it true for ~0.1s. The rest-state resolve
    ///     must therefore sample every system at rate 0.
    /// </summary>
    [Fact]
    public void Parse_NellisArtillery_IdleEmitterActiveFalseZeroesAllRestRates()
    {
        var bsaPath = SampleFileFixture.FindSamplePath(MeshesBsaRelative);
        Assert.SkipWhen(bsaPath is null, "FNV PC final meshes BSA not available");

        using var archives = MeshArchiveSet.Open(bsaPath!, null, false);
        Assert.True(
            archives.TryExtractFile(HowitzerPath, out var data, out _),
            "NVNellisArtillery NIF not found in BSA");

        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);

        var rates = nif!.Blocks.Select((block, index) => (block, index))
            .Where(x => NifParticleSystemParser.IsParticleSystem(x.block.TypeName))
            .Select(x => NifParticleSystemParser.Parse(data, nif, x.index))
            .OfType<ParticleSystemDefinition>()
            .Select(system => system.Emitter?.BirthRateController)
            .OfType<ParticleRateControllerDefinition>()
            .ToArray();
        Assert.Equal(3, rates.Length); // PCloud01/02/03

        foreach (var rate in rates)
        {
            // The Idle binding is present (not the dormant-triggered verdict) — the bool does the work.
            Assert.False(rate.DormantTriggeredFx);
            Assert.Equal(0f, rate.Sample(0f));
            Assert.Equal(0f, rate.Sample(2.5f));
        }
    }
}