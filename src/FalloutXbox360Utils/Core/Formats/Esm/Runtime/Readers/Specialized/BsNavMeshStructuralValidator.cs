using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

internal enum BsNavMeshValidationMode
{
    /// <summary>
    ///     Trusts upstream calibration. Accepts candidates whose <c>pParentCell</c> is null or
    ///     unmapped, since the caller has already restricted the candidate set to bytes the
    ///     enumerator anchored or drift-confirmed as NAVM. Detached navmeshes (paged-out cell,
    ///     InfoMap-only entries) still surface.
    /// </summary>
    Permissive,

    /// <summary>
    ///     Used for speculative candidates from an uncalibrated build. Requires
    ///     <c>pParentCell</c> to dereference to a VA already classified as a runtime
    ///     <c>TESObjectCELL</c> by the cell enumerator. This is the strongest cross-reference
    ///     available without trusting the candidate's own FormType byte.
    /// </summary>
    Strict
}

/// <summary>
///     Shape-only predicate for "does this VA look like a BSNavMesh?". Reads the 280-byte
///     BSNavMesh window at the candidate VA and applies a fixed sequence of structural
///     checks (vtable in module range, three BSSimpleArrays with sane <c>iSize</c>/
///     <c>iReservedSize</c>, optional parent-cell cross-reference). Does NOT project the
///     struct into a <c>NavMeshRecord</c> — that's the caller's responsibility once the
///     candidate passes validation.
///
///     Used by Phase 2d to gate Path 4's direct pAllForms NAVM walk. When the
///     <see cref="RuntimeCellEnumerator" />'s NAVM-byte calibration falls back to canonical
///     (no anchor + no drift remap), it emits a speculative candidate list at raw bytes
///     <c>[NavmFormType-2..NavmFormType+2]</c>; this validator filters out the false positives
///     (DIAL / INFO / PROJ / etc. at neighbouring bytes) before
///     <see cref="RuntimeNavMeshDiscovery.DiscoverForNavMeshVa" /> projects the survivors.
/// </summary>
internal sealed class BsNavMeshStructuralValidator
{
    private const int BsNavMeshSize = 280;
    private const int BsNavMeshParentCellOffset = 52;
    private const int BsNavMeshVerticesOffset = 56;
    private const int BsNavMeshTrianglesOffset = 72;
    private const int BsNavMeshDoorPortalsOffset = 104;

    private const int BsSimpleArrayCountOffset = 8;
    private const int BsSimpleArrayReservedSizeOffset = 12;
    private const int BsSimpleArraySize = 16;

    // Mirrors RuntimeNavMeshDiscovery.MaxArrayCount so the structural cutoff is consistent
    // between the validator and the eventual projection.
    private const uint MaxArrayCount = 1_000_000;

    private readonly RuntimeMemoryContext _context;
    private readonly IReadOnlySet<uint> _knownCellVas;
    private readonly BsNavMeshValidationMode _mode;

    public BsNavMeshStructuralValidator(
        RuntimeMemoryContext context,
        IReadOnlySet<uint> knownCellVas,
        BsNavMeshValidationMode mode)
    {
        _context = context;
        _knownCellVas = knownCellVas;
        _mode = mode;
    }

    public bool LooksLikeBsNavMesh(uint navMeshVa)
    {
        if (!_context.IsValidPointer(navMeshVa))
        {
            return false;
        }

        var fileOffset = _context.VaToFileOffset(navMeshVa);
        if (fileOffset is not long offset)
        {
            return false;
        }

        var navmBytes = _context.ReadBytes(offset, BsNavMeshSize);
        if (navmBytes is null || navmBytes.Length < BsNavMeshSize)
        {
            return false;
        }

        // 1. vfptr at +0 must point into the module image (BSNavMesh has a vtable). Non-vtable
        //    structs (most TESForm leaves) fail this cheaply.
        var vfptr = BinaryUtils.ReadUInt32BE(navmBytes, 0);
        if (!Xbox360MemoryUtils.IsModulePointer(vfptr))
        {
            return false;
        }

        // 2-4. Three BSSimpleArray headers must each have a plausible (iSize, iReservedSize)
        //      shape. Pass-through pBuffer here — paged-out buffers are common and handled
        //      gracefully by RuntimeNavMeshDiscovery.ReadArrayPayload.
        if (!TryReadBsSimpleArrayShape(navmBytes, BsNavMeshVerticesOffset, out var verticesSize))
        {
            return false;
        }

        if (!TryReadBsSimpleArrayShape(navmBytes, BsNavMeshTrianglesOffset, out var trianglesSize))
        {
            return false;
        }

        if (!TryReadBsSimpleArrayShape(navmBytes, BsNavMeshDoorPortalsOffset, out var doorPortalsSize))
        {
            return false;
        }

        // 5. At least one array must be non-empty. All-zero stubs would round-trip as
        //    0-vertex NAVMs and pollute the output — there's no value in surfacing them and
        //    they're a common shape for unrelated structs whose +56/+72/+104 fields happen
        //    to all be zero.
        if (verticesSize == 0 && trianglesSize == 0 && doorPortalsSize == 0)
        {
            return false;
        }

        // 6. pParentCell cross-reference (Strict only). Real BSNavMeshes set pParentCell to
        //    the loaded TESObjectCELL; if the candidate isn't a BSNavMesh, the dword at +52
        //    is unrelated and almost certainly not in the cell enumerator's hits.
        if (_mode == BsNavMeshValidationMode.Strict)
        {
            var parentCellVa = BinaryUtils.ReadUInt32BE(navmBytes, BsNavMeshParentCellOffset);
            if (!_knownCellVas.Contains(parentCellVa))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadBsSimpleArrayShape(byte[] navmBytes, int headerOffset, out uint count)
    {
        count = 0;
        if (headerOffset + BsSimpleArraySize > navmBytes.Length)
        {
            return false;
        }

        var iSize = BinaryUtils.ReadUInt32BE(navmBytes, headerOffset + BsSimpleArrayCountOffset);
        var iReservedSize = BinaryUtils.ReadUInt32BE(navmBytes, headerOffset + BsSimpleArrayReservedSizeOffset);

        if (iSize > MaxArrayCount || iReservedSize > MaxArrayCount)
        {
            return false;
        }

        if (iReservedSize < iSize)
        {
            return false;
        }

        count = iSize;
        return true;
    }
}
