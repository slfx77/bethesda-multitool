namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal interface IValueNode
{
    long Eval(IReadOnlyDictionary<string, object> fields);
    void GatherFields(HashSet<string> fields);
}
