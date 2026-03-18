using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

[Collection(DumpSerialTestGroup.Name)]
public sealed class RuntimeNpcDumpRegressionTests(SampleFileFixture samples)
{
    [Fact]
    [Trait("Category", "Slow")]
    public void FortVulpesInculta_DebugDump_ResolvesFaceGenLikeMemDebug()
    {
        Assert.SkipWhen(samples.DebugDump is null, "Debug memory dump not available");
        var memDebugDump = SampleFileFixture.FindSamplePath(@"Sample\MemoryDump\Fallout_Release_MemDebug.xex.dmp");
        Assert.SkipWhen(memDebugDump is null, "MemDebug memory dump not available");

        var debugNpc = LoadNpc(samples.DebugDump!, "FortVulpesInculta");
        var memDebugNpc = LoadNpc(memDebugDump!, "FortVulpesInculta");

        Assert.NotNull(debugNpc);
        Assert.NotNull(memDebugNpc);

        Assert.Equal(50, debugNpc!.FaceGenGeometrySymmetric!.Length);
        Assert.Equal(30, debugNpc.FaceGenGeometryAsymmetric!.Length);
        Assert.Equal(50, debugNpc.FaceGenTextureSymmetric!.Length);

        Assert.Equal(50, memDebugNpc!.FaceGenGeometrySymmetric!.Length);
        Assert.Equal(30, memDebugNpc.FaceGenGeometryAsymmetric!.Length);
        Assert.Equal(50, memDebugNpc.FaceGenTextureSymmetric!.Length);

        AssertArraysClose(debugNpc.FaceGenGeometrySymmetric, memDebugNpc.FaceGenGeometrySymmetric, 0.001f);
        AssertArraysClose(debugNpc.FaceGenGeometryAsymmetric, memDebugNpc.FaceGenGeometryAsymmetric, 0.001f);
        AssertArraysClose(debugNpc.FaceGenTextureSymmetric, memDebugNpc.FaceGenTextureSymmetric, 0.001f);

        Assert.Equal(memDebugNpc.HairFormId, debugNpc.HairFormId);
        Assert.Equal(memDebugNpc.EyesFormId, debugNpc.EyesFormId);
        Assert.Equal(memDebugNpc.CombatStyleFormId, debugNpc.CombatStyleFormId);
    }

    private static NpcRecord? LoadNpc(string dumpPath, string editorId)
    {
        var fileInfo = new FileInfo(dumpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var minidumpInfo = MinidumpParser.Parse(dumpPath);
        var scanResult = EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileInfo.Length);
        EsmEditorIdExtractor.ExtractRuntimeEditorIds(accessor, fileInfo.Length, minidumpInfo, scanResult);

        var npcEntries = scanResult.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x2A)
            .ToList();

        var entry = npcEntries.FirstOrDefault(entry =>
            string.Equals(entry.EditorId, editorId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);

        var reader = RuntimeStructReader.CreateWithAutoDetect(
            accessor,
            fileInfo.Length,
            minidumpInfo,
            scanResult.RuntimeRefrFormEntries,
            npcEntries);

        return reader.ReadRuntimeNpc(entry!);
    }

    private static void AssertArraysClose(float[] actual, float[] expected, float epsilon)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.InRange(Math.Abs(actual[i] - expected[i]), 0.0f, epsilon);
        }
    }
}