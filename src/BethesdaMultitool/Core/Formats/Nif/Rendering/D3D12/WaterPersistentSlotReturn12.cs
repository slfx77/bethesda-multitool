using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Returns one water descriptor when its owning deferred-release action becomes safe.</summary>
/// <param name="heap">Borrowed heap owning the persistent slot.</param>
/// <param name="slot">Bindless index to return.</param>
internal sealed class WaterPersistentSlotReturn12(GpuDescriptorHeapAllocator12 heap, uint slot) : IDisposable
{
    /// <summary>Returns the captured slot to its heap; the retained caller invokes it once after success.</summary>
    public void Dispose() => heap.FreePersistent(slot);
}
