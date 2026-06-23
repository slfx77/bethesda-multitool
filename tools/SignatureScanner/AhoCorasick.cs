namespace SignatureScanner;

/// <summary>
/// Aho-Corasick algorithm implementation for efficient multi-pattern string matching.
/// Allows scanning large data (100MB+) for multiple patterns in a single pass.
/// </summary>
internal sealed class AhoCorasick
{
    private readonly TrieNode _root = new();
    private bool _isBuilt;

    /// <summary>
    /// Add a pattern to the matcher.
    /// </summary>
    public void AddPattern(string name, byte[] pattern)
    {
        if (_isBuilt)
            throw new InvalidOperationException("Cannot add patterns after Build() has been called");

        var node = _root;
        foreach (var b in pattern)
        {
            node = node.GetOrAddChild(b);
        }
        node.Output = (name, pattern);
    }

    /// <summary>
    /// Build the failure links. Must be called after all patterns are added.
    /// </summary>
    public void Build()
    {
        if (_isBuilt) return;

        // BFS to build failure links
        var queue = new Queue<TrieNode>();

        // Initialize depth-1 nodes
        foreach (var child in _root.Children.Values)
        {
            child.Failure = _root;
            queue.Enqueue(child);
        }

        // Build failure links for deeper nodes
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var (b, child) in current.Children)
            {
                queue.Enqueue(child);

                // Find failure link
                var failure = current.Failure;
                while (failure != null && !failure.Children.ContainsKey(b))
                {
                    failure = failure.Failure;
                }

                child.Failure = failure?.Children.GetValueOrDefault(b) ?? _root;

                // If child doesn't have output but failure does, link to it
                if (child.Output == null && child.Failure.Output != null)
                {
                    child.OutputLink = child.Failure;
                }
                else if (child.Failure.OutputLink != null)
                {
                    child.OutputLink = child.Failure.OutputLink;
                }
            }
        }

        _isBuilt = true;
    }

    /// <summary>
    /// Search for all patterns in the given data.
    /// </summary>
    /// <returns>List of (patternName, pattern, offset) tuples for each match</returns>
    public List<(string Name, byte[] Pattern, int Offset)> Search(ReadOnlySpan<byte> data)
    {
        if (!_isBuilt)
            throw new InvalidOperationException("Build() must be called before Search()");

        var results = new List<(string, byte[], int)>();
        var node = _root;

        for (int i = 0; i < data.Length; i++)
        {
            var b = data[i];

            // Follow failure links until we find a match or reach root
            while (node != _root && !node.Children.ContainsKey(b))
            {
                node = node.Failure!;
            }

            node = node.Children.GetValueOrDefault(b) ?? _root;

            // Check for outputs at this node and via output links
            var outputNode = node;
            while (outputNode != null)
            {
                if (outputNode.Output is { } output)
                {
                    var matchOffset = i - output.Pattern.Length + 1;
                    results.Add((output.Name, output.Pattern, matchOffset));
                }
                outputNode = outputNode.OutputLink;
            }
        }

        return results;
    }

    private class TrieNode
    {
        public Dictionary<byte, TrieNode> Children { get; } = [];
        public TrieNode? Failure { get; set; }
        public TrieNode? OutputLink { get; set; }
        public (string Name, byte[] Pattern)? Output { get; set; }

        public TrieNode GetOrAddChild(byte b)
        {
            if (!Children.TryGetValue(b, out var child))
            {
                child = new TrieNode();
                Children[b] = child;
            }
            return child;
        }
    }
}
