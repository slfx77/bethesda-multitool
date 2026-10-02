namespace BethesdaMultitool.Core.Actors;

/// <summary>Caller-supplied identity context; file-local and merged FormIDs must never be conflated.</summary>
internal sealed record ActorSourceIdentity
{
    internal string? PrimaryFileName { get; init; }
    internal IReadOnlyList<ActorMasterStatus> Masters { get; init; } = [];
    internal bool IsPartialCapture { get; init; }
    internal Func<uint, string?>? ResolveDefiningPlugin { get; init; }
    internal Func<uint, string?>? ResolveWinningPlugin { get; init; }
    internal Func<uint, string?>? ResolveMissingReason { get; init; }

    /// <summary>Resolves file-local ownership unless a merged load-order callback was supplied.</summary>
    internal string? Owner(uint formId)
    {
        if (ResolveDefiningPlugin is not null) return ResolveDefiningPlugin(formId);
        if (IsPartialCapture || formId < 0x800) return null;
        var slot = formId >> 24;
        return slot < Masters.Count ? Masters[(int)slot].Name
            : slot == Masters.Count ? PrimaryFileName : null;
    }

    /// <summary>Distinguishes a missing master from a dangling reference or incomplete memory capture.</summary>
    internal string MissingReason(uint formId)
    {
        if (IsPartialCapture) return "NotInCapture";
        if (ResolveMissingReason?.Invoke(formId) is { } reason) return reason;
        if (formId < 0x800) return "TemplateMissing";
        var slot = formId >> 24;
        return slot < Masters.Count && !Masters[(int)slot].Loaded ? "MasterNotLoaded" : "TemplateMissing";
    }

    /// <summary>The winning source of a supplied record; distinct from the plugin owning its FormID.</summary>
    internal string? SourcePlugin(uint formId) => ResolveWinningPlugin?.Invoke(formId) ?? Owner(formId);
}
