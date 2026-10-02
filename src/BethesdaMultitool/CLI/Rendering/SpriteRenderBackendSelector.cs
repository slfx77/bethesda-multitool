using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Rendering;

/// <summary>
///     Picks the CLI sprite render backend. D3D12 is the only supported GPU path; falls
///     back to the CPU software renderer when D3D12 is unavailable (or when <c>--cpu</c>
///     is forced). <c>FALLOUT_VIEWER_D3D12_DEBUG=1</c> enables the validation layer.
/// </summary>
internal static class SpriteRenderBackendSelector
{
    /// <summary>Selects the existing CPU or GPU backend and retains construction failures through native retirement.</summary>
    /// <param name="forceCpu">Selects CPU processing without creating a device.</param>
    /// <param name="forceGpu">Reports an unavailable GPU as an abort selection.</param>
    /// <param name="forcedCpuMessage">Optional workflow-specific reason to use the CPU.</param>
    /// <param name="ignoredGpuMessage">Optional explanation for a workflow that cannot honor force-GPU.</param>
    /// <param name="fallbackCpuMessage">Message when automatic selection finds no device.</param>
    /// <param name="forceGpuUnavailableMessage">Message when an explicitly requested GPU is unavailable.</param>
    /// <returns>The caller-owned backend selection with its existing abort semantics.</returns>
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

        var enableDebugLayer = EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.D3D12Debug);
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

        GpuSpriteRenderer12? renderer = null;
        try
        {
            renderer = new GpuSpriteRenderer12(device);
            AnsiConsole.MarkupLine(
                "GPU rendering: [green]{0}[/] ({1})",
                GpuDevice12.Backend,
                device.DeviceName);
            return new SpriteRenderBackendSelection(device, renderer, false);
        }
        catch
        {
            if (renderer is not null)
            {
                renderer.Dispose();
            }
            else if (!device.TryForceDeviceRemoval("sprite-backend-construction"))
            {
                // An unpublished constructor may have failed while retiring native work.
                // Do not explicitly release a device whose terminal retirement is unproved.
                throw;
            }
            device.Dispose();
            throw;
        }
    }
}
