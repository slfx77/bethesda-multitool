namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Explicit engine script tracing scope. Entry/return observations do not establish block execution.</summary>
public sealed record RuntimeScriptTraceOptions(string Mode = "requested",
    IReadOnlyList<RuntimeScriptTraceTarget>? Scripts = null, IReadOnlyList<RuntimeScriptTraceTarget>? Quests = null)
{
    internal string StartMetadata()
    {
        if (Mode is not ("requested" or "selected" or "all" or "off"))
            throw new ArgumentException("Script trace mode must be requested, selected, all or off.");
        var count = (Scripts?.Count ?? 0) + (Quests?.Count ?? 0);
        if (count > 64 || Mode == "selected" && count == 0 || Mode != "selected" && count != 0)
            throw new ArgumentException("Selected script tracing requires one to 64 explicit script or quest targets; other modes accept no targets.");
        var lines = new List<string> { "@bmt-script-scope=" + Mode };
        Add(Scripts, "script");
        Add(Quests, "quest");
        return string.Join('\n', lines);

        void Add(IReadOnlyList<RuntimeScriptTraceTarget>? targets, string kind)
        {
            foreach (var target in targets ?? [])
            {
                if (target is null) throw new ArgumentException("Script trace targets cannot be null.");
                var validated = new RuntimeAction("read-quest-variable", Name: "Trace", Plugin: target.Plugin,
                    FormId: target.FormId).ToFrame(1).Payload;
                lines.Add("@bmt-" + kind + "=" + validated[..validated.LastIndexOf('\t')]);
            }
        }
    }
}

public sealed record RuntimeScriptTraceTarget(string Plugin, string FormId);
