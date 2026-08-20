namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     Classifies a 32-bit pointer value by where it points: null, unmapped, into a module's range, or into heap
///     memory.
/// </summary>
public enum PointerClassification
{
    Null,
    Unmapped,
    ModuleRange,
    Heap
}
