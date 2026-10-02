namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>The production admission data for the normalized NIF GLB writer.</summary>
/// <remarks>
///     A family is admitted only after its corpus parity stratum passes, and an admission is a row added here, never a
///     code path. The table is empty: no family is admitted, so every production export stays on the native writer.
///     Tests admit a family through the admission-predicate overload of <c>NifGlbExport.Plan</c> instead of
///     changing this table.
/// </remarks>
internal static class NifGlbNormalizedAdmission
{
    /// <summary>The reason recorded for a scene with a skinned mesh part.</summary>
    public const string SkinnedScopeReason = "A skinned mesh part is outside the normalized static export scope.";

    /// <summary>Admitted (family, converted-from-big-endian) rows. Empty until an owner ruling admits one.</summary>
    private static readonly HashSet<(NifExportFamily Family, bool ConvertedFromBigEndian)> Admitted = [];

    /// <summary>Whether a family, in the given conversion state, may take the normalized route.</summary>
    /// <param name="family">The source's stream family.</param>
    /// <param name="convertedFromBigEndian">Whether the source was converted from an Xbox 360 big-endian stream.</param>
    /// <returns>True only for a row on the admission table.</returns>
    public static bool IsAdmitted(NifExportFamily family, bool convertedFromBigEndian)
    {
        return Admitted.Contains((family, convertedFromBigEndian));
    }

    /// <summary>Whether a scene is outside the static scope the normalized route currently covers.</summary>
    /// <param name="scene">The assembled scene.</param>
    /// <returns><see cref="SkinnedScopeReason" /> when any mesh part is skinned; otherwise null.</returns>
    /// <exception cref="ArgumentNullException">The scene is null.</exception>
    public static string? ScopeReason(GlbScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return scene.MeshParts.Any(part => part.Skin is not null) ? SkinnedScopeReason : null;
    }
}
