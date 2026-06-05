using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Rendering;

/// <summary>
///     Picks the CLI sprite render backend. D3D12 is the only supported GPU path; falls
///     back to the CPU software renderer when D3D12 is unavailable (or when <c>--cpu</c>
///     is forced). <c>FALLOUT_VIEWER_D3D12_DEBUG=1</c> enables the validation layer.
/// </summary>
internal static class SpriteRenderBackendSelector
{
    internal static SpriteRenderBackendSelection Create(
        bool forceCpu,
        bool forceGpu,
        string? forcedCpuMessage = null,
        string? ignoredGpuMessage = null,
        string? fallbackCpuMessage = "GPU not available -- using [yellow]CPU software renderer[/]",
        string forceGpuUnavailableMessage = "[red]Error:[/] --gpu specified but no GPU backend available")
    {
        if (!string.IsNullOrWhiteSpace(forcedCpuMessage))
        {
            if (forceGpu && !string.IsNullOrWhiteSpace(ignoredGpuMessage))
            {
                AnsiConsole.MarkupLine(ignoredGpuMessage);
            }

            AnsiConsole.MarkupLine(forcedCpuMessage);
            return new SpriteRenderBackendSelection(null, null, false);
        }

        if (forceCpu)
        {
            AnsiConsole.MarkupLine("Using [yellow]CPU software renderer[/] (--cpu)");
            return new SpriteRenderBackendSelection(null, null, false);
        }

        var enableDebugLayer = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_D3D12_DEBUG") == "1";
        var device = GpuDevice12.Create(enableDebugLayer);
        if (device == null)
        {
            if (forceGpu)
            {
                AnsiConsole.MarkupLine(forceGpuUnavailableMessage);
                return new SpriteRenderBackendSelection(null, null, true);
            }

            if (!string.IsNullOrWhiteSpace(fallbackCpuMessage))
            {
                AnsiConsole.MarkupLine(fallbackCpuMessage);
            }

            return new SpriteRenderBackendSelection(null, null, false);
        }

        var renderer = new GpuSpriteRenderer12(device);
        AnsiConsole.MarkupLine(
            "GPU rendering: [green]{0}[/] ({1})",
            GpuDevice12.Backend,
            device.DeviceName);
        return new SpriteRenderBackendSelection(device, renderer, false);
    }
}
