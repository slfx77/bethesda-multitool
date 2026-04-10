using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Rendering;

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

        var device = GpuDevice.Create();
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

        var renderer = new GpuSpriteRenderer(device);
        AnsiConsole.MarkupLine(
            "GPU rendering: [green]{0}[/] ({1})",
            device.Backend,
            device.Device.DeviceName);
        return new SpriteRenderBackendSelection(device, renderer, false);
    }
}