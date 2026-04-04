namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal interface ICondNode
{
    bool Eval(IReadOnlyDictionary<string, object> fields);
    void GatherFields(HashSet<string> fields);
}
