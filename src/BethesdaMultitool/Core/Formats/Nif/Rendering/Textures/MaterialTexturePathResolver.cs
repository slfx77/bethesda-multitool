using System.Runtime.CompilerServices;
using BethesdaMultitool.Core.Formats.Nif.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Resolves a Fallout 4 / Fallout 76 material file (<c>.bgsm</c> lighting / <c>.bgem</c> effect)
///     to its diffuse texture path. FO4/FO76 shapes don't carry an inline texture set — the shader's
///     Name points at a material under <c>materials\</c> whose texture-path table drives rendering.
///     Shared by both the CPU (<see cref="NifTextureResolver" />) and GPU
///     (<c>NifGpuTextureResolver</c>) texture resolvers so the worldspace viewer and the standalone
///     render/export pipelines resolve materials identically.
/// </summary>
internal static class MaterialTexturePathResolver
{
    /// <summary>Data-relative path of Starfield's compiled material database.</summary>
    private const string StarfieldMaterialDatabasePath = @"materials\materialsbeta.cdb";

    /// <summary>
    ///     One parsed material database per source set. Keyed weakly on the source list so a viewer
    ///     session that swaps games drops the old database with its sources; parsing the retail 105 MB
    ///     file takes ~0.6 s and yields half a million objects, so it must happen once, not per shape.
    /// </summary>
    private static readonly ConditionalWeakTable<object, Lazy<StarfieldMaterialDatabase?>> MaterialDatabases = [];

    /// <summary>
    ///     Resolves a Starfield <c>.mat</c> reference to a texture path through the compiled material
    ///     database. Starfield ships no per-file materials — a mesh's shader Name and a landscape
    ///     texture's <c>LTEX.BNAM</c> both name a <c>.mat</c> that exists only as a record inside
    ///     <c>materialsbeta.cdb</c> — so this is the only route to a diffuse for either.
    ///     Returns null when the database is absent or the material declares no such slot.
    /// </summary>
    internal static string? ResolveStarfieldTexturePath(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources,
        bool normalMap = false) =>
        ResolveStarfieldSlot(materialPath, sources, normalMap).TexturePath;

    /// <summary>
    ///     Resolves a Starfield <c>.mat</c> slot to EITHER a texture path or a flat colour. Callers that
    ///     can only consume a path lose the colour case and render those surfaces untextured — measured
    ///     at 26% of the shapes drawn in three retail worldspaces — so prefer this over
    ///     <see cref="ResolveStarfieldTexturePath" /> wherever a solid colour can be honoured.
    /// </summary>
    internal static StarfieldMaterialSlot ResolveStarfieldSlot(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources,
        bool normalMap = false)
    {
        var database = GetMaterialDatabase(sources);
        if (database is null)
        {
            return default;
        }

        var slot = normalMap
            ? database.ResolveNormalSlot(materialPath)
            : database.ResolveDiffuseSlot(materialPath);

        return slot.TexturePath is { Length: > 0 } path
            ? new StarfieldMaterialSlot(NifTexturePathUtility.Normalize(path), null)
            : slot;
    }

    /// <summary>True when <paramref name="path" /> is a Starfield material reference.</summary>
    internal static bool IsStarfieldMaterialPath(string path) =>
        path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     True when <paramref name="materialPath" /> exists in the database but resolves NO diffuse
    ///     in any form — neither a texture nor a flat replacement colour. That population is engine
    ///     content deliberately authored without an albedo: normal-only detail decals (bolts/trims),
    ///     glow-only strips, occlusion boxes, and <c>BlankNoRender</c> placeholders (measured 14k
    ///     placed shape instances in Akila City alone). The engine draws them through channels this
    ///     renderer does not implement yet — or never draws them — so the correct near-term treatment
    ///     is to SKIP the shape; binding the white fallback paints every building with bright white
    ///     overlay geometry. A material MISSING from the database returns false: that is broken
    ///     content and should stay loudly visible.
    /// </summary>
    internal static bool IsStarfieldNoDrawMaterial(
        string materialPath, IReadOnlyList<INifTextureSource> sources)
    {
        if (!IsStarfieldMaterialPath(materialPath))
        {
            return false;
        }

        var database = GetMaterialDatabase(sources);
        return database is not null &&
               database.Contains(materialPath) &&
               !database.ResolveDiffuseSlot(materialPath).IsResolved;
    }

    private static StarfieldMaterialDatabase? GetMaterialDatabase(IReadOnlyList<INifTextureSource> sources)
    {
        var lazy = MaterialDatabases.GetValue(
            sources,
            _ => new Lazy<StarfieldMaterialDatabase?>(
                () => LoadMaterialDatabase(sources), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private static StarfieldMaterialDatabase? LoadMaterialDatabase(IReadOnlyList<INifTextureSource> sources)
    {
        foreach (var source in sources)
        {
            if (source.TryLoadRaw(StarfieldMaterialDatabasePath) is { Length: > 0 } raw &&
                StarfieldMaterialDatabase.Parse(raw) is { } database)
            {
                return database;
            }
        }

        return null;
    }

    /// <summary>
    ///     Loads <paramref name="materialPath" /> from the first source that has it, parses the
    ///     BGSM/BGEM, and returns its diffuse texture path normalized for archive lookup. Returns
    ///     <c>null</c> when the material is absent, unparseable, or carries no diffuse slot.
    /// </summary>
    internal static string? ResolveDiffuseTexturePath(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        var diffuse = ResolveMaterial(materialPath, sources)?.Diffuse;
        return string.IsNullOrEmpty(diffuse) ? null : NifTexturePathUtility.Normalize(diffuse);
    }

    /// <summary>
    ///     Loads <paramref name="materialPath" /> from the first source that has it and parses it.
    ///     Returns <c>null</c> when the material is absent or unparseable. The path is normalized
    ///     first: FO4 shaders commonly bake absolute developer build paths into the material Name
    ///     (e.g. <c>C:\Projects\Fallout4\Build\PC\Data\materials\…\SDMart1Letters.BGSM</c>) — without
    ///     peeling to the Data-relative form the archive lookup misses and the material's render
    ///     state (alpha cutout, two-sided, specular) silently never applies.
    /// </summary>
    internal static BgsmMaterial? ResolveMaterial(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        var normalized = NifTexturePathUtility.Normalize(materialPath);
        foreach (var source in sources)
        {
            var raw = source.TryLoadRaw(normalized);
            if (raw is not null)
            {
                return BgsmMaterial.Parse(raw);
            }
        }

        return null;
    }
}
