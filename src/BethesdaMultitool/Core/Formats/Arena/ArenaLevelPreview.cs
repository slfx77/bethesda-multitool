using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     The Arena half of the asset browser's 3D LEVEL pane: recognises a <c>.MIF</c> by its
///     <c>MHDR</c> tag, assembles its starting level through <see cref="ArenaSceneAssembler" />
///     and hands the viewer the level's art as generated textures — the same route
///     <c>classic level export</c> takes to a GLB, so the two cannot disagree on geometry.
///     <para>
///         ⚠ An <c>.RMD</c> has no magic (its first word is a length, or a floor voxel), so the
///         wilderness chunks are deliberately NOT claimed here; they open in the 2D pane and
///         export through the CLI.
///     </para>
/// </summary>
internal static class ArenaLevelPreview
{
    /// <summary>True when the bytes open with a .MIF header.</summary>
    public static bool IsMif(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4 && data[..4].SequenceEqual("MHDR"u8);
    }

    /// <summary>
    ///     Assembles the map's starting level and resolves its textures, or returns an empty list
    ///     when the install cannot be found beside <paramref name="meshDirectory" />.
    /// </summary>
    /// <param name="bytes">The .MIF payload.</param>
    /// <param name="name">The entry or file name, for labels.</param>
    /// <param name="meshDirectory">The directory the browser hands over — the opened source's parent.</param>
    /// <param name="generated">Receives the RGBA textures the scene must register, keyed by viewer path.</param>
    /// <param name="textureFor">Material (archive, record) → registered texture; null when untextured.</param>
    public static IReadOnlyList<XnGineMeshInstance> Assemble(
        byte[] bytes, string name, string meshDirectory,
        Dictionary<string, DecodedTexture> generated, out Func<int, int, XnGineViewerTexture?>? textureFor)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(meshDirectory);
        ArgumentNullException.ThrowIfNull(generated);

        textureFor = null;
        var map = ArenaMifFile.Parse(bytes, name);
        if (map.Levels.Count == 0)
        {
            return [];
        }

        var levelIndex = Math.Clamp(map.StartingLevelIndex, 0, map.Levels.Count - 1);
        var level = map.Levels[levelIndex];
        var kind = ArenaLevelLibrary.KindOf(level);
        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        var label = map.Levels.Count == 1 ? stem : $"{stem}_L{levelIndex:D2}";

        var root = ArenaLevelLibrary.FindDataRootNear(meshDirectory);
        if (root is null)
        {
            // No install, no .INF: the geometry still reads, untextured, through an empty index.
            var bare = ArenaInfVoxelTextures.FromInf(ArenaInfFile.ParseText(string.Empty, "NONE.INF"));
            return ArenaSceneAssembler.Assemble(ArenaLevelPlanes.FromMifLevel(level), bare, kind, label).Instances;
        }

        using var library = ArenaLevelLibrary.Open(root);
        var textures = library.ResolveTextures(level.InfoFile, kind, null, out _)
                       ?? ArenaInfVoxelTextures.FromInf(ArenaInfFile.ParseText(string.Empty, "NONE.INF"));
        var assembly = ArenaSceneAssembler.Assemble(ArenaLevelPlanes.FromMifLevel(level), textures, kind, label);
        if (assembly.Instances.Count == 0)
        {
            return [];
        }

        // Resolved EAGERLY: the library (and its archive) is disposed when this returns, and the
        // adapter asks for textures afterwards.
        var lookup = new Dictionary<(int Archive, int Record), XnGineViewerTexture>();
        foreach (var slotIndex in assembly.UsedSlots)
        {
            var slot = textures.Slots[slotIndex];
            var path = ArenaLevelLibrary.ViewerTexturePathFor(slot);
            if (!generated.TryGetValue(path, out var texture))
            {
                texture = library.ResolveDecoded(slot.FileName, slot.SetIndex);
                if (texture is null)
                {
                    continue;
                }

                generated[path] = texture;
            }

            lookup[(ArenaSceneAssembler.TextureArchive, slotIndex)] = new XnGineViewerTexture(path, texture.Width, texture.Height);
        }

        textureFor = (archive, record) => lookup.GetValueOrDefault((archive, record));
        return assembly.Instances;
    }
}
