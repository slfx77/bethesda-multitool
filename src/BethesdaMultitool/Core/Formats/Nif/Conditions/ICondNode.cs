namespace BethesdaMultitool.Core.Formats.Nif.Conditions;

internal interface ICondNode
{
    bool Eval(IReadOnlyDictionary<string, object> fields);
    void GatherFields(HashSet<string> fields);
}
