// Original to this repository: AweMultitool (slfx77) ships no schema dumper; this walks the
// Gr2TypeTree the ported reader produces.

using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Granny;

/// <summary>
///     Renders a Granny 2 file's self-describing schema as indented text: every member of the root
///     type and, recursively, of every referenced type, with the counts the root object carries. It
///     is how a new build's layout is READ rather than assumed — the member names and types come
///     from the file, so the text is the file's own description of itself.
/// </summary>
internal static class Gr2SchemaDumper
{
    private const int MaximumDepth = 12;

    /// <summary>The root object's structure: member names, types, element counts, array lengths and strings.</summary>
    public static string DumpRootObject(Gr2TypeTree tree, int maximumArrayElements = 2)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var text = new StringBuilder();
        DumpObject(text, tree.RootObject, 0, maximumArrayElements, []);
        return text.ToString();
    }

    private static void DumpObject(StringBuilder text, Gr2Object value, int depth, int maximumArrayElements,
        HashSet<Gr2Address> visited)
    {
        if (depth > MaximumDepth || !visited.Add(value.Address))
        {
            Line(text, depth, "...");
            return;
        }

        foreach (var member in value.Members)
        {
            var definition = member.Definition;
            var label = string.Create(CultureInfo.InvariantCulture, $"{definition.Name} : {definition.Type}");
            if (definition.ArrayWidth > 0)
            {
                label += string.Create(CultureInfo.InvariantCulture, $"[{definition.ArrayWidth}]");
            }

            switch (definition.Type)
            {
                case Gr2MemberType.String:
                    Line(text, depth, $"{label} = \"{member.ReadString()}\"");
                    break;
                case Gr2MemberType.Int32:
                    Line(text, depth, $"{label} = {member.ReadInt32().ToString(CultureInfo.InvariantCulture)}");
                    break;
                case Gr2MemberType.UInt32:
                    Line(text, depth, $"{label} = {member.ReadUInt32().ToString(CultureInfo.InvariantCulture)}");
                    break;
                case Gr2MemberType.Real32:
                    Line(text, depth,
                        $"{label} = {member.ReadSingle().ToString("G6", CultureInfo.InvariantCulture)}{(definition.ElementCount > 1 ? " ..." : string.Empty)}");
                    break;
                case Gr2MemberType.Inline:
                case Gr2MemberType.Reference:
                case Gr2MemberType.VariantReference:
                {
                    var child = member.ReadObject();
                    Line(text, depth, child is null ? $"{label} = null" : label);
                    if (child is not null)
                    {
                        DumpObject(text, child, depth + 1, maximumArrayElements, visited);
                    }

                    break;
                }
                case Gr2MemberType.ReferenceToArray:
                case Gr2MemberType.ArrayOfReferences:
                case Gr2MemberType.ReferenceToVariantArray:
                {
                    var list = member.ReadObjects();
                    Line(text, depth,
                        $"{label} count={list.Count.ToString(CultureInfo.InvariantCulture)} elementSize={(list.ElementType?.Size ?? 0).ToString(CultureInfo.InvariantCulture)}");
                    if (list.Count > 0 && list.ElementType is not null)
                    {
                        Line(text, depth + 1,
                            "element type: " + string.Join(", ", list.ElementType.Members.Select(DescribeMember)));
                    }

                    for (var index = 0; index < Math.Min(list.Count, maximumArrayElements); index++)
                    {
                        var element = list[index];
                        Line(text, depth + 1, $"[{index.ToString(CultureInfo.InvariantCulture)}]");
                        if (element is not null)
                        {
                            DumpObject(text, element, depth + 2, maximumArrayElements, visited);
                        }
                    }

                    break;
                }
                default:
                    Line(text, depth, label);
                    break;
            }
        }

        visited.Remove(value.Address);
    }

    private static string DescribeMember(Gr2TypeMember member)
    {
        return member.ArrayWidth > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"{member.Name}:{member.Type}[{member.ArrayWidth}]@{member.ObjectOffset}")
            : string.Create(CultureInfo.InvariantCulture, $"{member.Name}:{member.Type}@{member.ObjectOffset}");
    }

    private static void Line(StringBuilder text, int depth, string content)
    {
        text.Append(' ', depth * 2).AppendLine(content);
    }
}
