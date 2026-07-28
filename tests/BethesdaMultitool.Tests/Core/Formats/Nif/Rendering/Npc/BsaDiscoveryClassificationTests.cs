using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     Pins archive discovery to classify BSAs by their <see cref="BsaFileFlags" /> content bits, not
///     by filename. A mod that packs everything into one <c>&lt;Mod&gt; - Main.bsa</c> (matching neither
///     <c>*Meshes*.bsa</c> nor <c>*Texture*.bsa</c>) must still be found for both meshes and textures —
///     the regression that left ESP worldspaces rendering empty.
/// </summary>
public class BsaDiscoveryClassificationTests
{
    private static string WriteBsa(string dir, string fileName, params string[] virtualPaths)
    {
        using var writer = BsaWriter.CreateWithAutoFlags(virtualPaths);
        foreach (var path in virtualPaths)
        {
            writer.AddFile(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        }

        var fullPath = Path.Combine(dir, fileName);
        writer.Write(fullPath);
        return fullPath;
    }

    [Fact]
    public void Discover_MainBsaWithMeshesAndTextures_ClassifiedAsBoth()
    {
        var dir = Directory.CreateTempSubdirectory("bsadisc_main_").FullName;
        try
        {
            var main = WriteBsa(dir, "MyMod - Main.bsa", "meshes\\a\\foo.nif", "textures\\a\\foo.dds");
            File.WriteAllBytes(Path.Combine(dir, "MyMod.esp"), [0x54, 0x45, 0x53, 0x34]);

            var result = BsaDiscovery.Discover(Path.Combine(dir, "MyMod.esp"));

            Assert.Contains(main, result.MeshesBsaPaths);
            Assert.Contains(main, result.TexturesBsaPaths);
            Assert.True(result.HasMeshes);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Discover_TexturesOnlyFolder_NoLongerSkipped()
    {
        // Old behavior early-returned Empty when no *Meshes*.bsa existed, dropping the textures too.
        var dir = Directory.CreateTempSubdirectory("bsadisc_tex_").FullName;
        try
        {
            var tex = WriteBsa(dir, "MyMod - Textures.bsa", "textures\\a\\foo.dds");
            File.WriteAllBytes(Path.Combine(dir, "MyMod.esp"), [0x54, 0x45, 0x53, 0x34]);

            var result = BsaDiscovery.Discover(Path.Combine(dir, "MyMod.esp"));

            Assert.Contains(tex, result.TexturesBsaPaths);
            Assert.Empty(result.MeshesBsaPaths);
            Assert.False(result.HasMeshes);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DiscoverInDirectory_OddlyNamedTexturesArchive_Found()
    {
        // The NifConverter tab used a "*Texture*.bsa" filename glob; an archive whose name carries no
        // "Texture" (e.g. a mod's single "<Mod> - Stuff.bsa") was invisible to it even when it holds
        // the textures. Content classification must find it.
        var dir = Directory.CreateTempSubdirectory("bsadisc_odd_").FullName;
        try
        {
            var odd = WriteBsa(dir, "Game - Stuff.bsa", "textures\\a\\foo.dds");

            var result = BsaDiscovery.DiscoverInDirectory(dir);

            Assert.Contains(odd, result.TexturesBsaPaths);
            Assert.Empty(result.MeshesBsaPaths);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NifBrowserService_CreateFromBsa_PicksContentClassifiedTextureArchives()
    {
        // End-to-end over the tab's auto-detection path: opening a meshes BSA must surface texture
        // archives by content (including a combined main BSA and an oddly-named textures BSA), not by
        // the old "*Texture*" filename glob.
        var dir = Directory.CreateTempSubdirectory("bsadisc_tab_").FullName;
        try
        {
            var meshes = WriteBsa(dir, "Game - Meshes.bsa", "meshes\\a\\foo.nif");
            var odd = WriteBsa(dir, "Game - Stuff.bsa", "textures\\a\\foo.dds");
            var main = WriteBsa(dir, "MyMod - Main.bsa", "meshes\\b\\bar.nif", "textures\\b\\bar.dds");

            using var service = NifBrowserService.CreateFromBsa(meshes);

            Assert.Contains(odd, service.TexturePaths);
            Assert.Contains(main, service.TexturePaths);
            Assert.DoesNotContain(meshes, service.TexturePaths);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Discover_SeparateMeshesAndTexturesArchives_ClassifiedSeparately()
    {
        var dir = Directory.CreateTempSubdirectory("bsadisc_split_").FullName;
        try
        {
            var meshes = WriteBsa(dir, "Game - Meshes.bsa", "meshes\\a\\foo.nif");
            var textures = WriteBsa(dir, "Game - Textures.bsa", "textures\\a\\foo.dds");
            File.WriteAllBytes(Path.Combine(dir, "Game.esm"), [0x54, 0x45, 0x53, 0x34]);

            var result = BsaDiscovery.Discover(Path.Combine(dir, "Game.esm"));

            Assert.Contains(meshes, result.MeshesBsaPaths);
            Assert.DoesNotContain(textures, result.MeshesBsaPaths);
            Assert.Contains(textures, result.TexturesBsaPaths);
            Assert.DoesNotContain(meshes, result.TexturesBsaPaths);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}