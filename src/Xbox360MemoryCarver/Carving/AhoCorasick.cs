using System.Buffers;

namespace Xbox360MemoryCarver.Carving;

/// <summary>
/// Aho-Corasick multi-pattern search algorithm for efficient signature matching.
/// Finds all occurrences of multiple patterns in a single pass through the data.
/// </summary>
public sealed class AhoCorasick
{
    private readonly Node _root;
    private readonly List<(string Name, byte[] Pattern)> _patterns;

    private sealed class Node
    {
        public Dictionary<byte, Node> Children { get; } = [];
        public Node? Failure { get; set; }
        public List<int> Output { get; } = [];
    }

    public AhoCorasick()
    {
        _root = new Node();
        _patterns = [];
    }

    /// <summary>
    /// Add a pattern to search for.
    /// </summary>
    public void AddPattern(string name, byte[] pattern)
    {
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
    /// Build the failure links. Must be called after all patterns are added.
    /// </summary>
    public void Build()
    {
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
                while (failure != null && !failure.Children.ContainsKey(b))
                {
                    failure = failure.Failure;
                }

                child.Failure = failure?.Children.GetValueOrDefault(b) ?? _root;
                if (child.Failure == child)
                    child.Failure = _root;

                // Merge output from failure link
                child.Output.AddRange(child.Failure.Output);
            }
        }
    }

    /// <summary>
    /// Search for all pattern matches in the data.
    /// Returns (patternName, patternBytes, position) for each match.
    /// </summary>
    public IEnumerable<(string Name, byte[] Pattern, long Position)> Search(ReadOnlySpan<byte> data, long baseOffset = 0)
    {
        var current = _root;
        var results = new List<(string, byte[], long)>();

        for (int i = 0; i < data.Length; i++)
        {
            var b = data[i];

            while (current != _root && !current.Children.ContainsKey(b))
            {
                current = current.Failure!;
            }

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

    /// <summary>
    /// Search using a memory-mapped view for large files.
    /// </summary>
    public IEnumerable<(string Name, byte[] Pattern, long Position)> Search(
        System.IO.MemoryMappedFiles.MemoryMappedViewAccessor accessor,
        long offset,
        long length)
    {
        const int chunkSize = 64 * 1024 * 1024; // 64MB chunks
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        var results = new List<(string, byte[], long)>();

        try
        {
            long position = offset;
            var current = _root;
            int maxPatternLength = _patterns.Max(p => p.Pattern.Length);

            while (position < offset + length)
            {
                int toRead = (int)Math.Min(chunkSize, offset + length - position);
                accessor.ReadArray(position, buffer, 0, toRead);

                for (int i = 0; i < toRead; i++)
                {
                    var b = buffer[i];

                    while (current != _root && !current.Children.ContainsKey(b))
                    {
                        current = current.Failure!;
                    }

                    current = current.Children.GetValueOrDefault(b) ?? _root;

                    foreach (var patternIndex in current.Output)
                    {
                        var (name, pattern) = _patterns[patternIndex];
                        var matchPos = position + i - pattern.Length + 1;
                        results.Add((name, pattern, matchPos));
                    }
                }

                // Overlap for patterns that span chunks
                position += toRead - maxPatternLength;
                if (position < offset) position = offset + toRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return results;
    }

    public int PatternCount => _patterns.Count;
}
