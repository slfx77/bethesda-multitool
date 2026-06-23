namespace BethesdaMultitool.Core.Formats.Nif.Conditions;

internal interface IValueNode
{
    long Eval(IReadOnlyDictionary<string, object> fields);
    void GatherFields(HashSet<string> fields);
}
