using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Checks original record ownership against complete synthetic plugin master tables.</summary>
public sealed class DialoguePluginIdentityTests
{
    /// <summary>Distinguishes master overrides, locally authored records, and unresolved indices.</summary>
    [Theory]
    [InlineData(0x00001234u, "Base.esm")]
    [InlineData(0x01001234u, "Change.esp")]
    [InlineData(0x02001234u, null)]
    public void ResolvesDeclaredPluginOwnership(uint formId, string? expected)
    {
        WithPlugin(CreatePlugin(), path => Assert.Equal(expected, DialoguePluginIdentity.ResolveOwner(path, formId)));
    }

    /// <summary>Refuses to infer ownership when the complete declared master table is unavailable.</summary>
    [Fact]
    public void TruncatedHeaderDoesNotTreatMissingMastersAsLocallyAuthoredRecords()
    {
        var complete = CreatePlugin();
        WithPlugin(complete[..30], path => Assert.Null(DialoguePluginIdentity.ResolveOwner(path, 0x1234)));
    }

    /// <summary>Constructs one TES4 header with a HEDR and one complete MAST/DATA pair.</summary>
    private static byte[] CreatePlugin()
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("HEDR"u8);
            writer.Write((ushort)12);
            writer.Write(1.34f);
            writer.Write(0u);
            writer.Write(0x1000u);
            writer.Write("MAST"u8);
            writer.Write((ushort)9);
            writer.Write("Base.esm\0"u8);
            writer.Write("DATA"u8);
            writer.Write((ushort)8);
            writer.Write(0L);
        }
        using var plugin = new MemoryStream();
        using (var writer = new BinaryWriter(plugin, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("TES4"u8);
            writer.Write((uint)body.Length);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0L);
            writer.Write(body.ToArray());
        }
        return plugin.ToArray();
    }

    /// <summary>Runs a header check against an isolated loose fixture and removes only that fixture.</summary>
    private static void WithPlugin(byte[] bytes, Action<string> verify)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dialogue-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Change.esp");
            File.WriteAllBytes(path, bytes);
            verify(path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
