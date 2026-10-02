using System.Runtime.InteropServices;
using BethesdaMultitool.CLI.Commands.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>Compiler-free coverage for the shipped DXBC container and its build/runtime wiring.</summary>
public sealed class GpuShaderBytecodePack12Tests
{
    private const string SampleKey = "sample.frag.hlsl|main|ps_5_1|";

    [Fact]
    public void EveryAuthoritativePermutationRoundTripsThroughThePack()
    {
        var keys = GpuShaderBytecodePack12.CurrentPermutationKeys();
        var entries = keys
            .Select((key, index) => KeyValuePair.Create(key, FakeDxbc(unchecked((byte)index))))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var fingerprint = GpuShaderBytecodePack12.ComputeCurrentFingerprint();

        using var stream = new MemoryStream();
        GpuShaderBytecodePack12.Write(stream, fingerprint, entries);
        stream.Position = 0;
        var pack = GpuShaderBytecodePack12.Read(stream, fingerprint, keys);

        Assert.Equal(entries.Count, pack.Count);
        foreach (var (key, expectedBytecode) in entries)
        {
            Assert.True(pack.TryGetBytecode(key, out var actualBytecode));
            Assert.True(expectedBytecode.AsSpan().SequenceEqual(actualBytecode.Span));
        }
    }

    /// <summary>Preserves Shared's exact wire bytes and permits each reader to consume the other writer's output.</summary>
    [Fact]
    public void AdapterAndSharedWritersProduceInterchangeableIdenticalPacks()
    {
        const string secondKey = "another.vert.hlsl|main|vs_5_1|";
        var fingerprint = TestFingerprint(0x31);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [SampleKey] = FakeDxbc(0xA1),
            [secondKey] = FakeDxbc(0xB2)
        };
        using var adapterOutput = new MemoryStream();
        using var sharedOutput = new MemoryStream();

        GpuShaderBytecodePack12.Write(adapterOutput, fingerprint, entries);
        ShaderBytecodePack.Write(sharedOutput, fingerprint, entries);

        Assert.True(adapterOutput.TryGetBuffer(out var adapterBytes));
        Assert.True(sharedOutput.TryGetBuffer(out var sharedBytes));
        Assert.True(adapterBytes.AsSpan().SequenceEqual(sharedBytes.AsSpan()));
        adapterOutput.Position = 0;
        sharedOutput.Position = 0;
        var sharedRead = ShaderBytecodePack.Read(adapterOutput, fingerprint, entries.Keys);
        var adapterRead = GpuShaderBytecodePack12.Read(sharedOutput, fingerprint, entries.Keys);
        foreach (var (key, expected) in entries)
        {
            Assert.True(sharedRead.TryGetBytecode(key, out var fromAdapter));
            Assert.True(adapterRead.TryGetBytecode(key, out var fromShared));
            Assert.True(expected.AsSpan().SequenceEqual(fromAdapter.Span));
            Assert.True(expected.AsSpan().SequenceEqual(fromShared.Span));
        }
    }

    /// <summary>Reuses one owned bytecode buffer per entry after the input stream has been mutated and closed.</summary>
    [Fact]
    public void RepeatedAdapterLookupsBorrowTheSameOwnedMemory()
    {
        var fingerprint = TestFingerprint(0x32);
        using var stream = WriteSamplePack(fingerprint);
        var pack = GpuShaderBytecodePack12.Read(stream, fingerprint, [SampleKey]);

        Assert.True(pack.TryGetBytecode(SampleKey, out var first));
        Assert.True(pack.TryGetBytecode(SampleKey, out var second));
        Assert.True(first.Equals(second));
        Assert.True(MemoryMarshal.TryGetArray(first, out var firstBacking));
        Assert.True(MemoryMarshal.TryGetArray(second, out var secondBacking));
        Assert.Same(firstBacking.Array, secondBacking.Array);
        Assert.Equal(firstBacking.Offset, secondBacking.Offset);
        Assert.Equal(firstBacking.Count, secondBacking.Count);
        Assert.NotSame(stream.GetBuffer(), firstBacking.Array);

        Array.Fill(stream.GetBuffer(), (byte)0);
        stream.Dispose();

        Assert.True(pack.TryGetBytecode(SampleKey, out var retained));
        Assert.True(first.Equals(retained));
        Assert.True(FakeDxbc(0x77).AsSpan().SequenceEqual(retained.Span));
        Assert.False(pack.TryGetBytecode("missing.frag.hlsl|main|ps_5_1|", out var missing));
        Assert.True(missing.IsEmpty);
    }

    [Fact]
    public void FingerprintIsOrderIndependentAndCoversSourcesInventoryAndCompilerContract()
    {
        var sources = new[]
        {
            KeyValuePair.Create("z.hlsli", "include-z"),
            KeyValuePair.Create("a.hlsl", "entry-a")
        };
        var keys = new[] { "z|main|ps_5_1|", "a|main|vs_5_1|" };
        var baseline = GpuShaderBytecodePack12.ComputeFingerprint(sources, keys, "compiler-v1");
        var reordered = GpuShaderBytecodePack12.ComputeFingerprint(
            sources.Reverse(), keys.Reverse(), "compiler-v1");

        Assert.True(baseline.SequenceEqual(reordered));
        Assert.False(baseline.SequenceEqual(GpuShaderBytecodePack12.ComputeFingerprint(
            [.. sources.Take(1), KeyValuePair.Create("a.hlsl", "changed")],
            keys,
            "compiler-v1")));
        Assert.False(baseline.SequenceEqual(GpuShaderBytecodePack12.ComputeFingerprint(
            sources,
            [.. keys.Take(1), "changed|main|vs_5_1|"],
            "compiler-v1")));
        Assert.False(baseline.SequenceEqual(GpuShaderBytecodePack12.ComputeFingerprint(
            sources,
            keys,
            "compiler-v2")));
    }

    [Fact]
    public void ReadRejectsAStaleFingerprint()
    {
        var writtenFingerprint = TestFingerprint(0x11);
        var expectedFingerprint = TestFingerprint(0x22);
        using var stream = WriteSamplePack(writtenFingerprint);

        var exception = Assert.Throws<InvalidDataException>(() =>
            GpuShaderBytecodePack12.Read(stream, expectedFingerprint, [SampleKey]));

        Assert.Contains("fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsDuplicateExpectedKeysInsteadOfSilentlyTreatingThemAsASet()
    {
        var fingerprint = TestFingerprint(0x12);
        using var stream = WriteSamplePack(fingerprint);

        var exception = Assert.Throws<ArgumentException>(() =>
            GpuShaderBytecodePack12.Read(stream, fingerprint, [SampleKey, SampleKey]));

        Assert.Contains("more than once", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadRejectsCorruptEntryBytesBeforeReturningDxbc()
    {
        var fingerprint = TestFingerprint(0x33);
        using var valid = WriteSamplePack(fingerprint);
        var bytes = valid.ToArray();
        bytes[^1] ^= 0xFF; // Last byte is part of the entry's SHA-256 digest.
        using var corrupt = new MemoryStream(bytes);

        var exception = Assert.Throws<InvalidDataException>(() =>
            GpuShaderBytecodePack12.Read(corrupt, fingerprint, [SampleKey]));

        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AtomicWriterReplacesAnExistingPackWithoutLeavingATemporaryFile()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"GpuShaderBytecodePack12Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, GpuShaderBytecodePack12.DefaultFileName);
        var fingerprint = TestFingerprint(0x44);
        try
        {
            await File.WriteAllBytesAsync(path, [0x00, 0x01], TestContext.Current.CancellationToken);
            await GpuShaderBytecodePack12.WriteAtomicallyAsync(
                path,
                fingerprint,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [SampleKey] = FakeDxbc(0x55)
                },
                TestContext.Current.CancellationToken);

            await using var stream = File.OpenRead(path);
            var pack = GpuShaderBytecodePack12.Read(stream, fingerprint, [SampleKey]);
            Assert.True(pack.TryGetBytecode(SampleKey, out var bytecode));
            Assert.True(FakeDxbc(0x55).AsSpan().SequenceEqual(bytecode.Span));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BuildCommandReusesAnExactCurrentPackWithoutCompilingItAgain()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"GpuShaderBytecodePack12Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, GpuShaderBytecodePack12.DefaultFileName);
        var keys = GpuShaderBytecodePack12.CurrentPermutationKeys();
        var entries = keys
            .Select((key, index) => KeyValuePair.Create(key, FakeDxbc(unchecked((byte)index))))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        try
        {
            await GpuShaderBytecodePack12.WriteAtomicallyAsync(
                path,
                GpuShaderBytecodePack12.ComputeCurrentFingerprint(),
                entries,
                TestContext.Current.CancellationToken);
            var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            var deliberatelyOldTimestamp = DateTime.UtcNow.AddHours(-1);
            File.SetLastWriteTimeUtc(path, deliberatelyOldTimestamp);

            var exitCode = await BuildShaderBytecodePackCommand.BuildAsync(path, CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.True(File.GetLastWriteTimeUtc(path) > deliberatelyOldTimestamp);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BuildCommandHonorsCancellationBeforeReusingACurrentPack()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"GpuShaderBytecodePack12Tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, GpuShaderBytecodePack12.DefaultFileName);
        var keys = GpuShaderBytecodePack12.CurrentPermutationKeys();
        var entries = keys
            .Select((key, index) => KeyValuePair.Create(key, FakeDxbc(unchecked((byte)index))))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        try
        {
            await GpuShaderBytecodePack12.WriteAtomicallyAsync(
                path,
                GpuShaderBytecodePack12.ComputeCurrentFingerprint(),
                entries,
                TestContext.Current.CancellationToken);
            var before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            using var cancellationSource = new CancellationTokenSource();
            await cancellationSource.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BuildShaderBytecodePackCommand.BuildAsync(path, cancellationSource.Token));

            Assert.Equal(before, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void InteractiveWindowsBuildAndSingleFilePublishShipThePhysicalSidecar()
    {
        var project = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "BethesdaMultitool.csproj");
        Assert.Contains(
            $"<ShaderBytecodePackFileName>{GpuShaderBytecodePack12.DefaultFileName}</ShaderBytecodePackFileName>",
            project,
            StringComparison.Ordinal);
        Assert.Contains("AfterTargets=\"Build\"", project, StringComparison.Ordinal);
        var enableStart = project.IndexOf(
            "<GenerateShaderBytecodePackOnBuild",
            StringComparison.Ordinal);
        Assert.True(enableStart >= 0);
        var enableEnd = project.IndexOf(
            "</GenerateShaderBytecodePackOnBuild>",
            enableStart,
            StringComparison.Ordinal);
        Assert.True(enableEnd > enableStart);
        var enableProperty = project[enableStart..enableEnd];
        Assert.Contains("$([MSBuild]::IsOSPlatform('Windows'))", enableProperty, StringComparison.Ordinal);
        Assert.DoesNotContain("$(Configuration)", enableProperty, StringComparison.Ordinal);
        Assert.Contains(BuildShaderBytecodePackCommand.CommandName, project, StringComparison.Ordinal);
        Assert.Contains("Name=\"GetShaderBytecodePackOutput\"", project, StringComparison.Ordinal);
        Assert.Contains(
            "DependsOnTargets=\"GenerateShaderBytecodePack\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "Returns=\"@(_ShaderBytecodePackOutput)\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "AfterTargets=\"ComputeResolvedFilesToPublishList\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains("<ExcludeFromSingleFile>true</ExcludeFromSingleFile>", project,
            StringComparison.Ordinal);

        var compiler = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuShaderCompiler12.cs");
        var packLookup = compiler.IndexOf("shippedPack.TryGetBytecode", StringComparison.Ordinal);
        Assert.True(packLookup >= 0);
        var sourceFallback = compiler.IndexOf(
            "ReadOnlyMemory<byte> bytecode = CompileSource(",
            packLookup,
            StringComparison.Ordinal);
        Assert.True(sourceFallback > packLookup);
    }

    [Fact]
    public void ProfilerQueriesTheProducerTargetAndPublishesTheReturnedPhysicalSidecar()
    {
        var project = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "BethesdaRendererProfiler.csproj");

        Assert.Contains(
            $"<ShaderBytecodePackFileName>{GpuShaderBytecodePack12.DefaultFileName}</ShaderBytecodePackFileName>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ShaderBytecodePackProducer>true</ShaderBytecodePackProducer>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "'$(BuildTestsOnly)' != 'true'",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "'$(GenerateShaderBytecodePackOnBuild)' == '' or '$(GenerateShaderBytecodePackOnBuild)' == 'true'",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "'$(GenerateShaderBytecodePackOnBuild)' != 'false'",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "Condition=\"'$(_ShaderBytecodePackPropagationEnabled)' == 'true'\"",
            project,
            StringComparison.Ordinal);
        var propagationDefaults = project[..project.IndexOf(
            "<!-- Fast test builds",
            StringComparison.Ordinal)];
        Assert.DoesNotContain("$(Configuration)", propagationDefaults, StringComparison.Ordinal);

        var copyTargetStart = project.IndexOf(
            "<Target Name=\"CopyShaderBytecodePackFromBethesdaMultitool\"",
            StringComparison.Ordinal);
        Assert.True(copyTargetStart >= 0);
        var copyTargetEnd = project.IndexOf("</Target>", copyTargetStart, StringComparison.Ordinal);
        Assert.True(copyTargetEnd > copyTargetStart);
        var copyTarget = project[copyTargetStart..copyTargetEnd];
        var msbuildTaskStart = copyTarget.IndexOf("<MSBuild ", StringComparison.Ordinal);
        Assert.True(msbuildTaskStart >= 0);
        var msbuildTaskEnd = copyTarget.IndexOf('>', msbuildTaskStart);
        Assert.True(msbuildTaskEnd > msbuildTaskStart);
        var msbuildTask = copyTarget[msbuildTaskStart..msbuildTaskEnd];

        Assert.Contains(
            "Projects=\"@(_ShaderBytecodePackProducerProject)\"",
            msbuildTask,
            StringComparison.Ordinal);
        Assert.Contains(
            "Targets=\"GetShaderBytecodePackOutput\"",
            msbuildTask,
            StringComparison.Ordinal);
        Assert.Contains("Include=\"@(_MSBuildProjectReferenceExistent)\"", copyTarget,
            StringComparison.Ordinal);
        foreach (var metadata in new[] { "SetConfiguration", "SetPlatform", "SetTargetFramework" })
        {
            Assert.Contains($"%(_ShaderBytecodePackProducerProject.{metadata})", msbuildTask,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "RemoveProperties=\"%(_ShaderBytecodePackProducerProject.GlobalPropertiesToRemove)" +
            "$(_GlobalPropertiesToRemoveFromProjectReferences)\"",
            msbuildTask, StringComparison.Ordinal);
        // A solution may map the x64 profiler to an AnyCPU producer. The query must use the
        // resolved reference metadata, or it selects another output and repeats the main build.
        Assert.DoesNotContain("Platform=$(Platform)", msbuildTask, StringComparison.Ordinal);
        Assert.Contains(
            "TaskParameter=\"TargetOutputs\"",
            copyTarget,
            StringComparison.Ordinal);
        Assert.Contains("DestinationFolder=\"$(TargetDir)\"", copyTarget, StringComparison.Ordinal);
        Assert.DoesNotContain("Targets=\"Build\"", msbuildTask, StringComparison.Ordinal);
        Assert.DoesNotContain("BethesdaMultitool\\bin", copyTarget, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "Name=\"IncludeShaderBytecodePackInProfilerPublish\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "DependsOnTargets=\"CopyShaderBytecodePackFromBethesdaMultitool\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ResolvedFileToPublish Include=\"@(_ProfilerShaderBytecodePack)\">",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "'%(Filename)%(Extension)' != '$(ShaderBytecodePackFileName)'",
            project,
            StringComparison.Ordinal);
        Assert.Contains("<ExcludeFromSingleFile>true</ExcludeFromSingleFile>", project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Map2DProfilerPropagatesThePackForItsOptionalWorldView3DHost()
    {
        var project = SourceContract.ReadSource(
            "src", "BethesdaMap2DProfiler", "BethesdaMap2DProfiler.csproj");

        Assert.Contains(
            $"<ShaderBytecodePackFileName>{GpuShaderBytecodePack12.DefaultFileName}</ShaderBytecodePackFileName>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ShaderBytecodePackProducer>true</ShaderBytecodePackProducer>",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "Condition=\"'$(_ShaderBytecodePackPropagationEnabled)' == 'true'\"",
            project,
            StringComparison.Ordinal);

        var copyTargetStart = project.IndexOf(
            "<Target Name=\"CopyShaderBytecodePackFromBethesdaMultitool\"",
            StringComparison.Ordinal);
        Assert.True(copyTargetStart >= 0);
        var copyTargetEnd = project.IndexOf("</Target>", copyTargetStart, StringComparison.Ordinal);
        Assert.True(copyTargetEnd > copyTargetStart);
        var copyTarget = project[copyTargetStart..copyTargetEnd];
        Assert.Contains("Targets=\"GetShaderBytecodePackOutput\"", copyTarget, StringComparison.Ordinal);
        Assert.Contains("DestinationFolder=\"$(TargetDir)\"", copyTarget, StringComparison.Ordinal);
        Assert.DoesNotContain("BethesdaMultitool\\bin", copyTarget, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "DependsOnTargets=\"CopyShaderBytecodePackFromBethesdaMultitool\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<ResolvedFileToPublish Include=\"@(_Map2DShaderBytecodePack)\">",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "'%(Filename)%(Extension)' != '$(ShaderBytecodePackFileName)'",
            project,
            StringComparison.Ordinal);
        Assert.Contains("<ExcludeFromSingleFile>true</ExcludeFromSingleFile>", project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InteractiveWindowsBuildSeedsSharedPackButAlwaysValidatesTheFinalTargetDirCopy()
    {
        var project = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "BethesdaMultitool.csproj");
        Assert.Contains(
            "$(BaseIntermediateOutputPath)shader-bytecode-cache\\$(ShaderBytecodePackFileName)",
            project,
            StringComparison.Ordinal);

        var targetStart = project.IndexOf(
            "<Target Name=\"GenerateShaderBytecodePack\"",
            StringComparison.Ordinal);
        Assert.True(targetStart >= 0);
        var targetEnd = project.IndexOf("</Target>", targetStart, StringComparison.Ordinal);
        Assert.True(targetEnd > targetStart);
        var target = project[targetStart..targetEnd];

        var seed = target.IndexOf(
            "SourceFiles=\"$(ShaderBytecodePackCachePath)\"",
            StringComparison.Ordinal);
        var validate = target.IndexOf(
            "build-shader-bytecode-pack &quot;$(TargetDir)$(ShaderBytecodePackFileName)&quot;",
            StringComparison.Ordinal);
        var refreshCache = target.IndexOf(
            "DestinationFiles=\"$(ShaderBytecodePackCachePath)\"",
            StringComparison.Ordinal);

        Assert.True(seed >= 0);
        Assert.True(validate > seed);
        Assert.True(refreshCache > validate);
        Assert.Contains(
            "!Exists('$(TargetDir)$(ShaderBytecodePackFileName)')",
            target,
            StringComparison.Ordinal);
        Assert.Contains("SkipUnchangedFiles=\"true\"", target, StringComparison.Ordinal);
        Assert.DoesNotContain("Inputs=", target, StringComparison.Ordinal);
        Assert.DoesNotContain("Outputs=", target, StringComparison.Ordinal);

        // The producer contract must keep returning the validated physical TargetDir copy. The
        // shared cache is only a seed and must never become a publish/runtime path.
        var outputTargetStart = project.IndexOf(
            "<Target Name=\"GetShaderBytecodePackOutput\"",
            StringComparison.Ordinal);
        Assert.True(outputTargetStart > targetEnd);
        var outputTargetEnd = project.IndexOf("</Target>", outputTargetStart, StringComparison.Ordinal);
        Assert.True(outputTargetEnd > outputTargetStart);
        var outputTarget = project[outputTargetStart..outputTargetEnd];
        Assert.Contains(
            "<_ShaderBytecodePackOutput Include=\"$(TargetDir)$(ShaderBytecodePackFileName)\">",
            outputTarget,
            StringComparison.Ordinal);
        Assert.DoesNotContain("$(ShaderBytecodePackCachePath)", outputTarget, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthoritativeInventoryIncludesTheProductionOblivionInstancedBlendAbi()
    {
        Assert.Contains(
            ShaderPermutations.All,
            permutation => permutation.File == "reference_grass_oblivion.vert.hlsl" &&
                           permutation.Macros.Any(macro =>
                               macro.Name == "GRASS_INSTANCED" && macro.Definition == "1"));
    }

    private static MemoryStream WriteSamplePack(byte[] fingerprint)
    {
        var stream = new MemoryStream();
        GpuShaderBytecodePack12.Write(
            stream,
            fingerprint,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [SampleKey] = FakeDxbc(0x77)
            });
        stream.Position = 0;
        return stream;
    }

    private static byte[] FakeDxbc(byte marker)
    {
        return [(byte)'D', (byte)'X', (byte)'B', (byte)'C', marker, 0x10, 0x20, 0x30];
    }

    private static byte[] TestFingerprint(byte value)
    {
        var fingerprint = new byte[32];
        Array.Fill(fingerprint, value);
        return fingerprint;
    }
}
