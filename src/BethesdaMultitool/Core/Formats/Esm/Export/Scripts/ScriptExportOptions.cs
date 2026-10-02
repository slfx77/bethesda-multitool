namespace BethesdaMultitool.Core.Formats.Esm.Export.Scripts;

/// <summary>When a script export writes a <c>.decompiled</c> companion file rendered from SCDA.</summary>
public enum ScriptDecompiledPolicy
{
    /// <summary>
    ///     Only for a script that has no author-written source text (plugin SCTX or text recovered from a
    ///     memory dump) but does have decompiled bytecode. The default: every compiled script gets readable
    ///     text, and none gets a reconstruction beside its real source.
    /// </summary>
    Missing,

    /// <summary>For every script with decompiled bytecode, beside any source file.</summary>
    All,

    /// <summary>Never. A script without source text is then listed in the manifest as skipped.</summary>
    None
}

/// <summary>Options for <see cref="ScriptExportWriter" />.</summary>
public sealed record ScriptExportOptions
{
    /// <summary>
    ///     The file extension, with or without its dot (normalized by
    ///     <see cref="ScriptExportFileNamer.NormalizeExtension" />). The per-game default is
    ///     <see cref="ScriptExportFileNamer.DefaultExtension" />.
    /// </summary>
    public required string Extension { get; init; }

    /// <summary>When to write a <c>.decompiled</c> companion; <see cref="ScriptDecompiledPolicy.Missing" /> by default.</summary>
    public ScriptDecompiledPolicy Decompiled { get; init; } = ScriptDecompiledPolicy.Missing;

    /// <summary>
    ///     Whether existing files may be replaced. When false, the export fails before writing anything if
    ///     any file it would write already exists.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>
    ///     The producing tool's version, recorded as the manifest's <c>toolVersion</c>. Null uses the
    ///     BethesdaMultitool assembly's informational version (the value the CLI's JSON documents record).
    /// </summary>
    public string? ToolVersion { get; init; }

    /// <summary>The lower-case token for a policy, as the CLI option and the manifest spell it.</summary>
    public static string FormatDecompiledPolicy(ScriptDecompiledPolicy policy)
    {
        return policy switch
        {
            ScriptDecompiledPolicy.Missing => "missing",
            ScriptDecompiledPolicy.All => "all",
            ScriptDecompiledPolicy.None => "none",
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown decompiled-file policy.")
        };
    }

    /// <summary>Parses <c>missing</c>, <c>all</c> or <c>none</c> (any case, surrounding spaces ignored).</summary>
    /// <returns>False for anything else, including null.</returns>
    public static bool TryParseDecompiledPolicy(string? text, out ScriptDecompiledPolicy policy)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "missing":
                policy = ScriptDecompiledPolicy.Missing;
                return true;
            case "all":
                policy = ScriptDecompiledPolicy.All;
                return true;
            case "none":
                policy = ScriptDecompiledPolicy.None;
                return true;
            default:
                policy = ScriptDecompiledPolicy.Missing;
                return false;
        }
    }
}
