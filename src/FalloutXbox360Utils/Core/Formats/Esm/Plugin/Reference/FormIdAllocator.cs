namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     Allocates fresh local FormIDs for new records emitted by the plugin. Each allocated
///     FormID has the high byte set to <c>0x01</c> (placeholder plugin index — the engine
///     remaps this to the actual load-order index when the player enables the plugin) and the
///     low 24 bits taken from a monotonically-incrementing counter.
///     The starting local ID defaults to <c>0x800</c>, matching the convention used by GECK
///     and other Bethesda authoring tools — local IDs below 0x800 are reserved for the engine.
/// </summary>
public sealed class FormIdAllocator
{
    /// <summary>Plugin-index byte placed in the high byte of every allocated FormID.</summary>
    public const byte PluginIndex = 0x01;

    /// <summary>Default first local ID (0x800) — matches GECK convention.</summary>
    public const uint DefaultBaseLocalId = 0x800;

    public FormIdAllocator(uint baseLocalId = DefaultBaseLocalId)
    {
        if (baseLocalId > 0x00FFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(baseLocalId),
                "Local IDs are 24-bit; base must be ≤ 0x00FFFFFF.");
        }

        BaseLocalId = baseLocalId;
        NextLocalId = baseLocalId;
    }

    /// <summary>The first local ID this allocator started from.</summary>
    public uint BaseLocalId { get; }

    /// <summary>The local ID that will be returned by the next call to <see cref="Allocate" />.</summary>
    public uint NextLocalId { get; private set; }

    /// <summary>
    ///     The local ID one past the highest already-allocated ID. Suitable as the value of
    ///     TES4 HEDR <c>NextObjectId</c> — that field tells GECK where to start allocating
    ///     when the user adds new records via the editor. Returns <see cref="BaseLocalId" />
    ///     when nothing has been allocated yet.
    /// </summary>
    public uint NextObjectId => NextLocalId;

    /// <summary>True if <see cref="Allocate" /> has been called at least once.</summary>
    public bool HasAllocations => NextLocalId > BaseLocalId;

    /// <summary>
    ///     Allocate a fresh FormID with plugin index <see cref="PluginIndex" /> in the high byte.
    /// </summary>
    public uint Allocate()
    {
        if (NextLocalId > 0x00FFFFFF)
        {
            throw new InvalidOperationException("FormID allocator exhausted (24-bit local ID space).");
        }

        var formId = ((uint)PluginIndex << 24) | NextLocalId;
        NextLocalId++;
        return formId;
    }
}
