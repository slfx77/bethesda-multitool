using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Semantic;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Semantic;

[Collection("Logger")]
public sealed class SemanticFileLoaderBufferTests
{
    [Fact]
    public async Task LoadFromAnalysisResult_ByteArrayAccessorMatchesMemoryMappedAccessor()
    {
        var npc = EsmTestFileBuilder.BuildRecord(
            "NPC_",
            0x00006000,
            0,
            ("EDID", NullTerm("BufferNpc")),
            ("FULL", NullTerm("Buffer NPC")));
        var weapon = EsmTestFileBuilder.BuildRecord(
            "WEAP",
            0x00006001,
            0,
            ("EDID", NullTerm("BufferWeapon")),
            ("FULL", NullTerm("Buffer Weapon")));
        var fileData = new EsmTestFileBuilder()
            .AddTopLevelGrup("NPC_", npc)
            .AddTopLevelGrup("WEAP", weapon)
            .Build();

        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.esm");
        await File.WriteAllBytesAsync(path, fileData);

        try
        {
            var analysis = await EsmFileAnalyzer.AnalyzeAsync(path);
            using var mmfResult = SemanticFileLoader.LoadFromAnalysisResult(
                path,
                analysis,
                AnalysisFileType.EsmFile,
                (SemanticFileLoadOptions?)null);
            using var bufferResult = SemanticFileLoader.LoadFromAnalysisResult(
                path,
                analysis,
                AnalysisFileType.EsmFile,
                null,
                new ByteArrayMemoryAccessor(fileData),
                fileData.Length);

            Assert.Equal(mmfResult.Records.TotalRecordsParsed, bufferResult.Records.TotalRecordsParsed);

            var mmfNpc = Assert.Single(mmfResult.Records.Npcs);
            var bufferNpc = Assert.Single(bufferResult.Records.Npcs);
            Assert.Equal(mmfNpc.FormId, bufferNpc.FormId);
            Assert.Equal(mmfNpc.EditorId, bufferNpc.EditorId);
            Assert.Equal(mmfNpc.FullName, bufferNpc.FullName);

            var mmfWeapon = Assert.Single(mmfResult.Records.Weapons);
            var bufferWeapon = Assert.Single(bufferResult.Records.Weapons);
            Assert.Equal(mmfWeapon.FormId, bufferWeapon.FormId);
            Assert.Equal(mmfWeapon.EditorId, bufferWeapon.EditorId);
            Assert.Equal(mmfWeapon.FullName, bufferWeapon.FullName);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static byte[] NullTerm(string value)
    {
        var bytes = new byte[value.Length + 1];
        System.Text.Encoding.ASCII.GetBytes(value, bytes);
        return bytes;
    }
}
