using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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
    ///     Reserved cache-key suffix selecting slot 1 (normal) instead of slot 0 (diffuse) from a
    ///     Starfield material. <c>|</c> cannot occur in a Windows asset filename, so this cannot
    ///     collide with retail content.
    /// </summary>
    internal const string StarfieldNormalMapSuffix = "|sfmat-normal";

    /// <summary>Reserved role suffix selecting CE2 layer-0 texture-set slot 2 (opacity).</summary>
    internal const string StarfieldOpacityMapSuffix = "|sfmat-opacity";

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
        bool normalMap = false)
    {
        return ResolveStarfieldSlot(materialPath, sources, normalMap).TexturePath;
    }

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

    /// <summary>
    ///     Resolves the base layer's effective colour policy from Starfield's compiled material
    ///     database. A default value means the path/database/object chain could not be resolved;
    ///     callers must fail closed rather than treating the mesh's decoded colour bytes as albedo.
    /// </summary>
    internal static StarfieldMaterialColorPolicy ResolveStarfieldBaseColorPolicy(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        if (!IsStarfieldMaterialPath(materialPath))
        {
            return default;
        }

        return GetMaterialDatabase(sources)?.ResolveBaseColorPolicy(materialPath) ?? default;
    }

    /// <summary>
    ///     Resolves the effective root CE2Material two-sided flag. Null is an unresolved or malformed
    ///     material chain; false is a resolved one-sided material. This never interprets the type-4
    ///     layer-material ParamBool, whose separate meaning is vertex-colour tint.
    /// </summary>
    internal static bool? ResolveStarfieldRootTwoSided(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        if (!IsStarfieldMaterialPath(materialPath))
        {
            return null;
        }

        return GetMaterialDatabase(sources)?.ResolveRootTwoSided(materialPath);
    }

    /// <summary>
    ///     Resolves the effective root-material AlphaSettings component. Material opacity/cutout is
    ///     deliberately independent from base-colour tint and its Lerp weight.
    /// </summary>
    internal static StarfieldMaterialAlphaPolicy ResolveStarfieldAlphaPolicy(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        if (!IsStarfieldMaterialPath(materialPath))
        {
            return default;
        }

        return GetMaterialDatabase(sources)?.ResolveAlphaPolicy(materialPath) ?? default;
    }

    /// <summary>
    ///     Resolves typed CE2 EffectSettings for the bounded Mesh Viewer glass-alpha lane. Texture
    ///     slots are normalized here so export can load them directly.
    /// </summary>
    internal static StarfieldMaterialEffectPolicy ResolveStarfieldEffectPolicy(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        if (!IsStarfieldMaterialPath(materialPath))
        {
            return default;
        }

        var policy = GetMaterialDatabase(sources)?.ResolveEffectPolicy(materialPath) ?? default;
        return policy with { OpacitySlot = NormalizeStarfieldSlot(policy.OpacitySlot) };
    }

    /// <summary>
    ///     Resolves a looping CE2 base-layer UV-offset curve when it is exactly representable as a
    ///     constant-rate native viewer scroll. Unsupported/nonlinear controller graphs fail closed.
    /// </summary>
    internal static StarfieldMaterialUvAnimationPolicy ResolveStarfieldBaseLayerUvAnimation(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        if (!IsStarfieldMaterialPath(materialPath))
        {
            return default;
        }

        return GetMaterialDatabase(sources)?.ResolveBaseLayerUvAnimation(materialPath) ?? default;
    }

    /// <summary>
    ///     Resolves the effective root CE2 ShaderRoute. Null is unresolved/malformed and must not
    ///     authorize a specialized renderer; Deferred is a valid resolved ordinary material.
    /// </summary>
    internal static StarfieldMaterialShaderRoute? ResolveStarfieldShaderRoute(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        if (!IsStarfieldMaterialPath(materialPath))
        {
            return null;
        }

        return GetMaterialDatabase(sources)?.ResolveShaderRoute(materialPath);
    }

    /// <summary>
    ///     Resolves the strict static-layer CE2 ORM policy used only by standards-based export.
    ///     Texture paths are normalized here so callers can load them directly without inventing a
    ///     role-qualified key that could accidentally become part of the live renderer contract.
    /// </summary>
    internal static StarfieldMaterialOrmPolicy ResolveStarfieldOrmPolicy(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        if (!IsStarfieldMaterialPath(materialPath))
        {
            return default;
        }

        var database = GetMaterialDatabase(sources);
        if (database is null)
        {
            return default;
        }

        var policy = database.ResolveOrmPolicy(materialPath);
        return policy with
        {
            RoughnessSlot = NormalizeStarfieldSlot(policy.RoughnessSlot),
            MetalnessSlot = NormalizeStarfieldSlot(policy.MetalnessSlot),
            AmbientOcclusionSlot = NormalizeStarfieldSlot(policy.AmbientOcclusionSlot)
        };
    }

    internal static StarfieldMaterialSlot ResolveStarfieldOpacitySlot(
        string materialPath,
        IReadOnlyList<INifTextureSource> sources)
    {
        var policy = ResolveStarfieldAlphaPolicy(materialPath, sources);
        return policy.TryResolveStaticCutout(out _) ? policy.OpacitySlot : default;
    }

    private static StarfieldMaterialSlot NormalizeStarfieldSlot(StarfieldMaterialSlot slot)
    {
        return slot.TexturePath is { Length: > 0 } path
            ? new StarfieldMaterialSlot(NifTexturePathUtility.Normalize(path), null)
            : slot;
    }

    /// <summary>
    ///     Cheap persistent-cache dependency identity for every ordered source that contains
    ///     Starfield's compiled material database. The loader may skip a present-but-invalid CDB and
    ///     parse a later candidate, so covering the whole candidate set makes either result coherent
    ///     without extracting and hashing any 105 MB payload on the caller thread. Changes to an
    ///     unused lower-priority candidate may conservatively invalidate decoded meshes.
    /// </summary>
    internal static string? ResolveStarfieldMaterialDatabaseCacheIdentity(
        IReadOnlyList<INifTextureSource> sources)
    {
        StringBuilder? builder = null;
        var candidateIndex = 0;
        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            var source = sources[sourceIndex];
            if (!source.Exists(StarfieldMaterialDatabasePath))
            {
                continue;
            }

            builder ??= new StringBuilder(512);
            Append("candidateIndex", candidateIndex++);
            Append("sourceIndex", sourceIndex);
            Append("assetPath", StarfieldMaterialDatabasePath);
            if (!source.TryGetAssetMetadata(StarfieldMaterialDatabasePath, out var metadata))
            {
                // Exists established that this is a candidate. Preserve its ordered presence even
                // when a transient stat failure prevents richer metadata; null is reserved for a
                // source set with no CDB candidate at all.
                Append("metadata", "unavailable");
                continue;
            }

            Append("sourcePath", metadata.SourcePath);
            Append("sourceLength", metadata.SourceLength);
            Append("sourceWriteUtcTicks", metadata.SourceLastWriteUtcTicks);
            Append("entryOffset", FormatNullable(metadata.EntryOffset));
            Append("entryRawSize", FormatNullable(metadata.EntryRawSize));
            Append("entrySize", FormatNullable(metadata.EntrySize));
            Append("entryNameHash", FormatNullable(metadata.EntryNameHash));
            Append("entryDirectoryHash", FormatNullable(metadata.EntryDirectoryHash));
            Append("entryIndex", FormatNullable(metadata.EntryIndex));
        }

        return builder?.ToString();

        void Append(string name, object value)
        {
            var target = builder ?? throw new InvalidOperationException("CDB identity builder was not initialized.");
            target.Append(name);
            target.Append('=');
            target.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            target.Append('\n');
        }
    }

    /// <summary>True when <paramref name="path" /> is a Starfield material reference.</summary>
    internal static bool IsStarfieldMaterialPath(string path)
    {
        return path.Trim().EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Builds the canonical role-qualified request used by the GPU, resolver, and persistent
    ///     texture caches for a Starfield material's normal slot.
    /// </summary>
    internal static string BuildStarfieldNormalMapRequest(string materialPath)
    {
        var normalized = NifTexturePathUtility.Normalize(materialPath);
        if (!IsStarfieldMaterialPath(normalized))
        {
            throw new ArgumentException("The path must name a Starfield .mat material.", nameof(materialPath));
        }

        return string.Concat(normalized, StarfieldNormalMapSuffix);
    }

    /// <summary>Builds the role-qualified request for a supported CE2 opacity slot.</summary>
    internal static string BuildStarfieldOpacityMapRequest(string materialPath)
    {
        var normalized = NifTexturePathUtility.Normalize(materialPath);
        if (!IsStarfieldMaterialPath(normalized))
        {
            throw new ArgumentException("The path must name a Starfield .mat material.", nameof(materialPath));
        }

        return string.Concat(normalized, StarfieldOpacityMapSuffix);
    }

    /// <summary>Splits a role-qualified normal request back into its real material path.</summary>
    internal static bool TrySplitStarfieldNormalMapRequest(string requestPath, out string materialPath)
    {
        materialPath = requestPath;
        if (!requestPath.EndsWith(StarfieldNormalMapSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = requestPath[..^StarfieldNormalMapSuffix.Length];
        if (!IsStarfieldMaterialPath(candidate))
        {
            return false;
        }

        materialPath = candidate;
        return true;
    }

    /// <summary>Splits a role-qualified opacity request back into its real material path.</summary>
    internal static bool TrySplitStarfieldOpacityMapRequest(string requestPath, out string materialPath)
    {
        materialPath = requestPath;
        if (!requestPath.EndsWith(StarfieldOpacityMapSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = requestPath[..^StarfieldOpacityMapSuffix.Length];
        if (!IsStarfieldMaterialPath(candidate))
        {
            return false;
        }

        materialPath = candidate;
        return true;
    }

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

    private static string FormatNullable<T>(T? value) where T : struct, IFormattable
    {
        return value.HasValue ? value.Value.ToString(null, CultureInfo.InvariantCulture) : string.Empty;
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
