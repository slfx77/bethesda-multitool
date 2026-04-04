namespace FalloutXbox360Utils.Core.Formats.Nif.Expressions;

internal sealed class FieldNode(string fieldName) : IValueNode
{
    public long Eval(IReadOnlyDictionary<string, object> fields)
    {
        if (fields.TryGetValue(fieldName, out var val))
        {
            return val switch
            {
                bool b => b ? 1 : 0,
                byte b => b,
                sbyte sb => sb,
                short s => s,
                ushort us => us,
                int i => i,
                uint ui => ui,
                long l => l,
                ulong ul => (long)ul,
                _ => 0
            };
        }

        // Field not found - default to 0 (conservative for "Has X" conditions)
        return 0;
    }

    public void GatherFields(HashSet<string> fields)
    {
        fields.Add(fieldName);
    }
}
