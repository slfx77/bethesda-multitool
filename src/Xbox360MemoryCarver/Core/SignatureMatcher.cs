namespace Xbox360MemoryCarver.Core;

/// <summary>
///     Multi-pattern search for efficient file signature matching.
///     Uses Aho-Corasick algorithm to find all occurrences of multiple patterns in a single pass.
/// </summary>
public sealed class SignatureMatcher
{
    private readonly List<(string Name, byte[] Pattern)> _patterns;
    private readonly Node _root;

    public SignatureMatcher()
    {
        _root = new Node();
        _patterns = [];
    }

    public int PatternCount => _patterns.Count;

    public int MaxPatternLength => _patterns.Count > 0 ? _patterns.Max(p => p.Pattern.Length) : 0;

    /// <summary>
    ///     Add a pattern to search for.
    /// </summary>
    public void AddPattern(string name, byte[] pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var patternIndex = _patterns.Count;
        _patterns.Add((name, pattern));

        var current = _root;
        foreach (var b in pattern)
        {
            if (!current.Children.TryGetValue(b, out var next))
            {
                next = new Node();
                current.Children[b] = next;
            }

            current = next;
        }

        current.Output.Add(patternIndex);
    }

    /// <summary>
    ///     Build the failure links. Must be called after all patterns are added.
    /// </summary>
    public void Build()
    {
        if (_patterns.Count == 0) return;

        var queue = new Queue<Node>();

        // Initialize failure links for depth-1 nodes
        foreach (var child in _root.Children.Values)
        {
            child.Failure = _root;
            queue.Enqueue(child);
        }

        // BFS to build failure links
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var (b, child) in current.Children)
            {
                queue.Enqueue(child);

                var failure = current.Failure;
                while (failure?.Children.ContainsKey(b) == false) failure = failure.Failure;

                child.Failure = failure?.Children.GetValueOrDefault(b) ?? _root;
                if (child.Failure == child) child.Failure = _root;

                // Merge output from failure link
                child.Failure?.Output.ForEach(o => child.Output.Add(o));
            }
        }
    }

    /// <summary>
    ///     Search for all pattern matches in the data.
    ///     Returns (patternName, patternBytes, position) for each match.
    /// </summary>
    public List<(string Name, byte[] Pattern, long Position)> Search(ReadOnlySpan<byte> data, long baseOffset = 0)
    {
        var current = _root;
        var results = new List<(string, byte[], long)>();

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];

            while (current != _root && !current.Children.ContainsKey(b)) current = current.Failure!;

            current = current.Children.GetValueOrDefault(b) ?? _root;

            foreach (var patternIndex in current.Output)
            {
                var (name, pattern) = _patterns[patternIndex];
                var matchPos = baseOffset + i - pattern.Length + 1;
                results.Add((name, pattern, matchPos));
            }
        }

        return results;
    }

    private sealed class Node
    {
        public Dictionary<byte, Node> Children { get; } = [];
        public Node? Failure { get; set; }
        public List<int> Output { get; } = [];
    }
}
