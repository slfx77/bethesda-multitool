using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeNpcDumpRegressionTests
{
    private static readonly string SnippetDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "Dmp");

    [Fact]
    public async Task FortVulpesInculta_DebugDump_ResolvesFaceGenLikeMemDebug()
    {
        var debugNpc = await LoadNpcAsync("debug_dump", "FortVulpesInculta");
        var memDebugNpc = await LoadNpcAsync("memdebug_dump", "FortVulpesInculta");

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

    private static async Task<NpcRecord?> LoadNpcAsync(string snippetName, string editorId)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);

        var npcEntries = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x2A)
            .ToList();

        var entry = npcEntries.FirstOrDefault(e =>
            string.Equals(e.EditorId, editorId, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(entry);

        var reader = RuntimeStructReader.CreateWithAutoDetect(
            snippet.Accessor,
            snippet.FileSize,
            snippet.MinidumpInfo,
            snippet.RuntimeRefrFormEntries,
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