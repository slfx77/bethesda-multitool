using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Shared root signature for the D3D12 live renderers. Lays out the binding contract
///     that all renderer shaders compile against.
///     <para>
///         Slot layout (see <see cref="Slots" />):
///     </para>
///     <list type="bullet">
///         <item>0: root CBV at <c>b0</c> — per-frame uniforms (viewProj). VS + PS.</item>
///         <item>1: root CBV at <c>b1</c> — per-draw uniforms (world matrix + flags). VS + PS.</item>
///         <item>2: root CBV at <c>b2</c> — per-mode/debug uniforms. VS + PS.</item>
///         <item>3: legacy descriptor table for <c>t0..t7, space0</c> — water and transition SRVs. VS + PS.</item>
///         <item>4: unbounded descriptor table for <c>t0.., space1</c> — bindless textures. VS + PS.</item>
///         <item>5: root SRV at <c>t8, space0</c> — reference instance structured buffer. VS.</item>
///     </list>
///     <para>
///         Root CBVs at slots 0/1 are faster than descriptor tables for the
///         frequently-updated uniforms — no indirection.
///     </para>
///     <para>
///         v3 Pass 4 Step 4a — SRV table is now UNBOUNDED (`numDescriptors = uint.MaxValue`)
///         and the root signature carries <c>CbvSrvUavHeapDirectlyIndexed</c>. Textures live
///         at stable slot indices in a shader-visible persistent SRV heap; shaders sample
///         via <c>Texture2D textures[] : register(t0, space1)</c> + <c>NonUniformResourceIndex</c>
///         lookup keyed by a per-instance/per-cell <c>TexIndices</c> uint passed through
///         the per-draw or per-instance constant data. Per-batch
///         <c>CopyDescriptorsSimple + SetGraphicsRootDescriptorTable</c> goes away;
///         the table is bound once per frame at the heap start.
///     </para>
///     <para>
///         <c>AllowInputAssemblerInputLayout</c> is on — D3D12's spec requires it when
///         shaders bind an input layout via IA.
///     </para>
/// </summary>
internal sealed class GpuRootSignature12 : IDisposable
{
    /// <summary>Root-parameter slot indices, in the order they are declared in the root signature.</summary>
    public static class Slots
    {
        public const int PerFrameCbv = 0;
        public const int PerDrawCbv = 1;
        public const int PerModeCbv = 2;
        public const int SrvTable = 3;
        public const int BindlessSrvTable = 4;
        public const int ReferenceInstanceSrv = 5;

        /// <summary>Root CBV at <c>b3</c> — the shared scene atmosphere (sun direction + sky / ambient
        /// / fog colors), uploaded once per frame and read by the lighting / sky / water shaders.</summary>
        public const int AtmosphereCbv = 6;
    }

    /// <summary>SRV slots <c>t0..t(N-1)</c> reserved in the legacy table at slot
    /// <see cref="Slots.SrvTable" /> (space 0). Water still uses this path for its per-frame
    /// structured-buffer SRV. 4a's bindless texture array lives in
    /// <see cref="Slots.BindlessSrvTable" /> at space 1 — orthogonal to this range, so the two
    /// coexist during the transition.</summary>
    public const uint SrvTableSize = 8;

    private bool _disposed;

    private GpuRootSignature12(ID3D12RootSignature root)
    {
        RootSignature = root;
    }

    /// <summary>The underlying D3D12 root signature. Bind via
    /// <c>commandList.SetGraphicsRootSignature(rs.RootSignature)</c> at frame start.</summary>
    public ID3D12RootSignature RootSignature { get; }

    public static GpuRootSignature12 Create(GpuDevice12 gpu)
    {
        // Slot 0: root CBV b0 (per-frame). Visible to VS + PS — both stages read viewProj.
        var perFrame = new RootParameter1(
            RootParameterType.ConstantBufferView,
            new RootDescriptor1(shaderRegister: 0, registerSpace: 0),
            ShaderVisibility.All);

        // Slot 1: root CBV b1 (per-draw). Visible to VS + PS — PS reads alpha-test flags.
        var perDraw = new RootParameter1(
            RootParameterType.ConstantBufferView,
            new RootDescriptor1(shaderRegister: 1, registerSpace: 0),
            ShaderVisibility.All);

        // Slot 2: root CBV b2 (per-mode / debug toggle). Used by TerrainRenderer for the
        // VCLR-only mode flag; CellGrid + Reference + Water leave it unbound (legal).
        var perMode = new RootParameter1(
            RootParameterType.ConstantBufferView,
            new RootDescriptor1(shaderRegister: 2, registerSpace: 0),
            ShaderVisibility.All);

        // Slot 3: legacy SRV table t0..t(SrvTableSize-1) in space 0. Water still uses the
        // CopyDescriptorsSimple path here. DescriptorsVolatile so per-frame
        // CopyDescriptorsSimple writes are visible to the GPU mid-frame.
        var srvRange = new DescriptorRange1
        {
            RangeType = DescriptorRangeType.ShaderResourceView,
            NumDescriptors = SrvTableSize,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            Flags = DescriptorRangeFlags.DescriptorsVolatile,
            OffsetInDescriptorsFromTableStart = 0,
        };
        var srvTable = new RootParameter1(
            new RootDescriptorTable1(srvRange),
            ShaderVisibility.All);

        // Slot 4: UNBOUNDED bindless SRV table t0.. in space 1. Bound ONCE per frame to
        // the persistent region of the shared shader-visible heap (heap slot 0). Shaders
        // declare `Texture2D textures[] : register(t0, space1)` and sample via
        // `textures[NonUniformResourceIndex(idx)]` keyed on the per-instance/per-cell
        // TexIndices. Space 1 keeps the array orthogonal to slot 3's space-0 bindings.
        var bindlessTextures = new DescriptorRange1
        {
            RangeType = DescriptorRangeType.ShaderResourceView,
            NumDescriptors = uint.MaxValue,
            BaseShaderRegister = 0,
            RegisterSpace = 1,
            Flags = DescriptorRangeFlags.DescriptorsVolatile,
            OffsetInDescriptorsFromTableStart = 0,
        };
        // Second unbounded range ALIASING the same heap slots as space 1, declared in HLSL as
        // `TextureCube cubemaps[] : register(t0, space2)`. A descriptor heap is typeless — the SRV
        // written into a slot decides the view dimension, so a slot holding a TextureCube SRV
        // (environment maps) is indexed through this alias with the SAME bindless index while every
        // other slot keeps sampling through the 2D array. Overlapping ranges are legal under
        // DescriptorsVolatile; shaders must only index a slot through the matching declaration.
        var bindlessCubemaps = new DescriptorRange1
        {
            RangeType = DescriptorRangeType.ShaderResourceView,
            NumDescriptors = uint.MaxValue,
            BaseShaderRegister = 0,
            RegisterSpace = 2,
            Flags = DescriptorRangeFlags.DescriptorsVolatile,
            OffsetInDescriptorsFromTableStart = 0,
        };
        var bindlessTable = new RootParameter1(
            new RootDescriptorTable1(bindlessTextures, bindlessCubemaps),
            ShaderVisibility.All);

        // Slot 5: reference instance structured buffer root SRV. It lives at t8, space 0 so
        // it does not overlap slot 3's legacy t0..t7 table while terrain/water transition.
        var referenceInstanceSrv = new RootParameter1(
            RootParameterType.ShaderResourceView,
            new RootDescriptor1(shaderRegister: 8, registerSpace: 0),
            ShaderVisibility.Vertex);

        // Slot 6: root CBV b3 (scene atmosphere — sun dir + sky/ambient/fog colors). Uploaded once
        // per frame by the host and bound for the whole scene; the lighting/sky/water shaders read it.
        // Additive: shaders that don't declare the b3 cbuffer are unaffected, and binding a CBV no
        // shader reads is legal — so it is a no-op for shaders that do not read the atmosphere buffer.
        var atmosphere = new RootParameter1(
            RootParameterType.ConstantBufferView,
            new RootDescriptor1(shaderRegister: 3, registerSpace: 0),
            ShaderVisibility.All);

        var staticSamplers = new[]
        {
            new StaticSamplerDescription(
                0,
                Filter.Anisotropic,
                TextureAddressMode.Wrap,
                TextureAddressMode.Wrap,
                TextureAddressMode.Wrap,
                0f,
                16,
                ComparisonFunction.Never,
                StaticBorderColor.OpaqueBlack,
                0f,
                float.MaxValue,
                ShaderVisibility.Pixel,
                0),
            new StaticSamplerDescription(
                1,
                Filter.Anisotropic,
                TextureAddressMode.Wrap,
                TextureAddressMode.Wrap,
                TextureAddressMode.Wrap,
                0f,
                16,
                ComparisonFunction.Never,
                StaticBorderColor.OpaqueBlack,
                0f,
                float.MaxValue,
                ShaderVisibility.Pixel,
                0),
            // s2: linear CLAMP — the FO4/FO76 grayscale-to-palette lookup. Palette rows are selected
            // by v = GradientMapV × vertexColor.R, and GradientMapV is commonly exactly 1.0 (bottom
            // row): the wrap samplers above would wrap v=1.0 back to row 0, which in shipped palettes
            // is a rainbow hue strip (ShippingCrates01Grad_d.dds) — the engine clamps
            // (fo76utils getPixelBC).
            new StaticSamplerDescription(
                2,
                Filter.MinMagMipLinear,
                TextureAddressMode.Clamp,
                TextureAddressMode.Clamp,
                TextureAddressMode.Clamp,
                0f,
                1,
                ComparisonFunction.Never,
                StaticBorderColor.OpaqueBlack,
                0f,
                float.MaxValue,
                ShaderVisibility.Pixel,
                0),
        };

        // Vortice convenience: CreateRootSignature(RootSignatureDescription1) does the
        // D3D12SerializeVersionedRootSignature + CreateRootSignature pair internally — no
        // manual blob marshaling.
        // CbvSrvUavHeapDirectlyIndexed enables the bindless access pattern: shaders
        // declare `Texture2D textures[] : register(t0, space1)` and sample via
        // `textures[NonUniformResourceIndex(idx)]`, with the heap bound once per frame at
        // the table head. Without this flag the runtime rejects unbounded resource arrays.
        var desc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout |
            RootSignatureFlags.ConstantBufferViewShaderResourceViewUnorderedAccessViewHeapDirectlyIndexed,
            new[] { perFrame, perDraw, perMode, srvTable, bindlessTable, referenceInstanceSrv, atmosphere },
            staticSamplers);
        var rs = gpu.Device.CreateRootSignature(desc);
        return new GpuRootSignature12(rs);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RootSignature.Dispose();
    }
}
