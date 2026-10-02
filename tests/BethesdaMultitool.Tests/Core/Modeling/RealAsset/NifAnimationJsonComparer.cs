using System.Text.Json;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Compares a probe expectation (parsed JSON) with the JSON rendered from a view, member by member and element by
///     element, and records every difference with its path. Numbers are compared by their JSON text, which is exact for the
///     integers the expectations carry (every float is its bits); strings ordinally; a member on one side only is a
///     difference, so nothing is skipped silently.
/// </summary>
internal static class NifAnimationJsonComparer
{
    /// <summary>Compares two nodes recursively, appending each difference to <paramref name="diffs" />.</summary>
    public static void Compare(JsonNode? expected, JsonNode? actual, string path, List<string> diffs)
    {
        if (expected is null || actual is null)
        {
            if (expected is not null || actual is not null)
            {
                diffs.Add($"{path}: expected {Show(expected)}, got {Show(actual)}");
            }

            return;
        }

        switch (expected)
        {
            case JsonObject expectedObject:
                if (actual is not JsonObject actualObject)
                {
                    diffs.Add($"{path}: expected an object, got {Show(actual)}");
                    return;
                }

                CompareMembers(expectedObject, actualObject, path, diffs,
                    expectedObject.Select(static member => member.Key).ToList());
                return;
            case JsonArray expectedArray:
                if (actual is not JsonArray actualArray)
                {
                    diffs.Add($"{path}: expected an array, got {Show(actual)}");
                    return;
                }

                if (expectedArray.Count != actualArray.Count)
                {
                    diffs.Add($"{path}: expected {expectedArray.Count} elements, got {actualArray.Count}");
                }

                for (var index = 0; index < Math.Min(expectedArray.Count, actualArray.Count); index++)
                {
                    Compare(expectedArray[index], actualArray[index], $"{path}[{index}]", diffs);
                }

                return;
        }

        var expectedKind = expected.GetValueKind();
        var actualKind = actual.GetValueKind();
        if (expectedKind != actualKind)
        {
            diffs.Add($"{path}: expected {Show(expected)} ({expectedKind}), got {Show(actual)} ({actualKind})");
            return;
        }

        var equal = expectedKind switch
        {
            JsonValueKind.String => string.Equals(expected.GetValue<string>(), actual.GetValue<string>(),
                StringComparison.Ordinal),
            JsonValueKind.Number => string.Equals(expected.ToJsonString(), actual.ToJsonString(),
                StringComparison.Ordinal),
            _ => true
        };
        if (!equal)
        {
            diffs.Add($"{path}: expected {Show(expected)}, got {Show(actual)}");
        }
    }

    /// <summary>
    ///     Compares the named members of two objects: each must be present on both sides and equal, and the view must carry
    ///     no member outside the list (a member the view renders is always compared).
    /// </summary>
    public static void CompareMembers(
        JsonObject expected,
        JsonObject actual,
        string path,
        List<string> diffs,
        IReadOnlyCollection<string> members)
    {
        foreach (var name in members)
        {
            if (!expected.ContainsKey(name))
            {
                diffs.Add($"{path}.{name}: the view renders it but the expectation has no such member");
            }
            else if (!actual.ContainsKey(name))
            {
                diffs.Add($"{path}.{name}: missing from the view (expected {Show(expected[name])})");
            }
            else
            {
                Compare(expected[name], actual[name], $"{path}.{name}", diffs);
            }
        }

        foreach (var member in actual)
        {
            if (!members.Contains(member.Key))
            {
                diffs.Add($"{path}.{member.Key}: rendered by the view but never compared");
            }
        }
    }

    private static string Show(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        var text = node.ToJsonString();
        return text.Length <= 120 ? text : text[..120] + "...";
    }
}
