namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>Half-open SCDA byte range and its reconstruction lines, never stored SCTX line numbers.</summary>
public sealed record ScriptInstructionSpan(int Offset, int Length, ushort? Opcode, string Kind, string Status,
    int? ParentOffset, int? ReconstructionLineStart, int? ReconstructionLineEnd, int? ReferencePrefixOffset);

public sealed record ScriptInstructionMap(string Reconstruction, IReadOnlyList<ScriptInstructionSpan> Instructions);

/// <summary>Optional instrumentation of the canonical decoder; it does not parse bytecode.</summary>
internal sealed class ScriptInstructionRecorder(int length)
{
    private readonly int _length = length;
    private readonly List<Scope> _items = [];
    private Scope? _current;
    internal int? PendingReferenceOffset { get; set; }

    internal void ConsumeReference()
    {
        if (_current is not null) _current.ReferencePrefixOffset = PendingReferenceOffset;
        PendingReferenceOffset = null;
    }

    internal Scope Begin(int offset, string kind)
    {
        var scope = new Scope(this, _current, Math.Clamp(offset, 0, _length), kind);
        _items.Add(scope);
        _current = scope;
        return scope;
    }

    internal IReadOnlyList<ScriptInstructionSpan> Snapshot() => _items.Select(item => item.Snapshot()).ToArray();

    internal sealed class Scope(ScriptInstructionRecorder recorder, Scope? parent, int offset, string kind) : IDisposable
    {
        private readonly Scope? _parent = parent;
        private readonly int _offset = offset;
        private int _end = offset;
        private int? _lineStart, _lineEnd;
        internal ushort? Opcode { get; set; }
        internal string Status { get; set; } = "Decoded";
        internal int? ReferencePrefixOffset { get; set; }
        internal void EndAt(int end) => _end = Math.Max(_end, Math.Clamp(end, _offset, recorder._length));
        internal void Lines(int start, int end)
        {
            if (end >= start) { _lineStart = start; _lineEnd = end; }
        }
        internal ScriptInstructionSpan Snapshot()
        {
            var lines = this;
            while (lines._lineStart is null && lines._parent is not null) lines = lines._parent;
            return new(_offset, _end - _offset, Opcode, kind, Status, _parent?._offset,
                lines._lineStart, lines._lineEnd, ReferencePrefixOffset);
        }
        public void Dispose()
        {
            if (Status != "Decoded" && _parent?.Status == "Decoded") _parent.Status = "Partial";
            recorder._current = _parent;
        }
    }
}
