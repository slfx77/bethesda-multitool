namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>TES4's eye loader copies one source image unchanged into all six cube faces.</summary>
internal static class OblivionEyeCubePayload
{
    internal const string RequestPath = @"textures\effects\eyereflection.dds#oblivion-eye-cube-v1";
    internal const string SourcePath = @"textures\effects\eyereflection.dds";

    internal static GpuTexturePayload? Create(GpuTexturePayload? source)
    {
        // Admit only the inspected stock format. In particular, never relabel a 2D descriptor
        // as a cube or reinterpret an already-cubical/modded image as six concatenated faces.
        if (source is not
            {
                ArraySize: 1, Width: 64, Height: 64, MipCount: 1,
                Format: GpuTexturePayloadFormat.BC1
            } || source.MipLevels[0].Bytes.Length != 2048)
            return null;
        var mip = source.MipLevels[0];
        if (mip.Width != source.Width || mip.Height != source.Height) return null;
        var faces = new GpuTextureMipPayload[6];
        for (var face = 0; face < faces.Length; face++)
            faces[face] = new GpuTextureMipPayload(mip.Width, mip.Height, (byte[])mip.Bytes.Clone());
        return new GpuTexturePayload(source.Format, source.Width, source.Height, faces, 6);
    }
}
