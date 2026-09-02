using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Pure descriptor/format helpers shared by <see cref="GpuTextureCache12" /> and its
///     collaborators. Stateless — every method is a deterministic function of its arguments, so
///     they are safe to call from the render thread, the uploader thread, or the solid-texture
///     factory without synchronization.
/// </summary>
internal static class GpuTextureFormatHelpers12
{
    internal static ShaderResourceViewDescription MakeSrvDesc(ushort mipCount, Format format)
    {
        return new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView
            {
                MipLevels = mipCount,
                MostDetailedMip = 0
            }
        };
    }

    /// <summary>
    ///     TextureCube SRV over a 6-slice texture array. The shader reads it through the aliased
    ///     <c>TextureCube cubemaps[] : register(t0, space2)</c> bindless range (same heap slots as
    ///     the 2D array — the descriptor's view dimension is what makes the slot a cube).
    /// </summary>
    internal static ShaderResourceViewDescription MakeCubeSrvDesc(ushort mipCount, Format format)
    {
        return new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.TextureCube,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            TextureCube = new TextureCubeShaderResourceView
            {
                MipLevels = mipCount,
                MostDetailedMip = 0
            }
        };
    }

    internal static Format ToDxgiFormat(GpuTexturePayloadFormat format)
    {
        return format switch
        {
            GpuTexturePayloadFormat.Rgba8 => Format.R8G8B8A8_UNorm,
            GpuTexturePayloadFormat.BC1 => Format.BC1_UNorm,
            GpuTexturePayloadFormat.BC2 => Format.BC2_UNorm,
            GpuTexturePayloadFormat.BC3 => Format.BC3_UNorm,
            GpuTexturePayloadFormat.BC4 => Format.BC4_UNorm,
            GpuTexturePayloadFormat.BC5 => Format.BC5_UNorm,
            GpuTexturePayloadFormat.BC7 => Format.BC7_UNorm,
            GpuTexturePayloadFormat.BC4S => Format.BC4_SNorm,
            GpuTexturePayloadFormat.BC5S => Format.BC5_SNorm,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    internal static uint GetSourceRowPitch(GpuTexturePayload payload, GpuTextureMipPayload level)
    {
        if (!payload.IsCompressed)
        {
            return (uint)level.Width * 4u;
        }

        var blocksWide = Math.Max(1, (level.Width + 3) / 4);
        return (uint)(blocksWide * payload.BytesPerBlock);
    }

    internal static uint GetSourceRowCount(GpuTexturePayload payload, GpuTextureMipPayload level)
    {
        if (!payload.IsCompressed)
        {
            return (uint)level.Height;
        }

        return (uint)Math.Max(1, (level.Height + 3) / 4);
    }
}
