using System.Globalization;
using BethesdaMultitool.CLI.Commands.Analysis;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.CLI.Show;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Show;

/// <summary>
///     Retail renders through <c>show</c>.
///     <para>
///         Every MESG in the retail 2022 <c>FalloutNV.esm</c> must render. Before the markup fix, each of the
///         246 messages with at least one button threw "Unbalanced markup stack" (measured on that master's
///         message_report.txt), because the renderer numbered buttons as <c>[1]</c>, which Spectre reads as a
///         colour-number tag. The buttoned-message floor is what lets that test fail: a parse that lost the
///         ITXT subrecords would render every message cleanly and prove nothing.
///     </para>
///     <para>
///         Every SCPT whose source or decompiled text is over the 2,000-character panel cap must come out of
///         <c>show --full</c> complete and verbatim (the 2022 master's script report counts 366 over the cap in
///         SCTX and 223 in the decompilation; BooneSCRIPT and VEFR01NCRGood2QuestSCRIPT among them), and the
///         July 2010 Xbox 360 prototype's SCPT header lines must report the SCHR flags and the bytecode order
///         read from the payload, not from the big-endian container.
///     </para>
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class ShowRetailRenderTests
{
    private const int MinimumButtonedMessages = 200;
    private const int WideConsole = 400;

    /// <summary>The panel cap <c>show</c> applies without <c>--full</c>.</summary>
    private const int PanelCap = 2000;

    /// <summary>
    ///     Floor on scripts with a body over the cap; the 2022 master has about 366. A parse that dropped
    ///     SCTX would leave too few and fail here instead of passing on an empty set.
    /// </summary>
    private const int MinimumLongScripts = 100;

    /// <summary>July 2010 X360 SCPT VDialogueRexScript: SCHR 01 00 01 00 (quest, compiled), 42-byte SCDA.</summary>
    private const uint JulyRexScriptFormId = 0x0011EB4D;

    [Fact]
    public async Task AllRetailMessages_RenderWithoutMarkupErrors()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.NewVegasBuilds.Steam2022("FalloutNV.esm");
        Assert.SkipWhen(esm is null, RealAssetPaths.SkipMessage("2022 Steam FalloutNV.esm"));

        // Cache-owned result: never dispose it.
        var result = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        var records = result.Records;
        var resolver = result.Resolver;

        var failures = new List<string>();
        var buttoned = 0;
        foreach (var message in records.Messages)
        {
            if (message.Buttons.Count > 0)
            {
                buttoned++;
            }

            var label = $"0x{message.FormId:X8} {message.EditorId}";
            try
            {
                var rendered = false;
                var output = CliHelpers.CaptureSpectreOutput(console =>
                {
                    console.Profile.Width = WideConsole;
                    rendered = ShowCommand.TryRender(
                        records, resolver, message.FormId, null, new ShowRenderContext(console));
                });

                if (!rendered || !output.Contains("MESG", StringComparison.Ordinal) ||
                    !output.Contains($"0x{message.FormId:X8}", StringComparison.Ordinal))
                {
                    failures.Add($"{label}: not rendered by the MESG renderer");
                }
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{label}: {ex.Message}");
            }
        }

        Assert.True(buttoned >= MinimumButtonedMessages,
            $"Only {buttoned} buttoned MESG records were parsed; expected at least {MinimumButtonedMessages}.");
        Assert.True(failures.Count == 0,
            $"{failures.Count} of {records.Messages.Count} MESG records failed to render:\n" +
            string.Join("\n", failures.Take(20)));

        // The record the crash was reported against, looked up the way a user types it.
        var turret = Assert.Single(records.Messages,
            m => string.Equals(m.EditorId, "vHDTurretMessageNCR01", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0x00134506u, turret.FormId);
        var turretOutput = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = WideConsole;
            Assert.True(ShowCommand.TryRender(
                records, resolver, null, "vHDTurretMessageNCR01", new ShowRenderContext(console)));
        });

        Assert.Contains("[1] Leave It Alone", turretOutput);
        Assert.Contains("[2] Repair Turret", turretOutput);
    }

    [Fact]
    public async Task RetailScripts_Full_ContainCompleteSourceAndDecompilation()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.NewVegasBuilds.Steam2022("FalloutNV.esm");
        Assert.SkipWhen(esm is null, RealAssetPaths.SkipMessage("2022 Steam FalloutNV.esm"));

        // Cache-owned result: never dispose it.
        var result = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        var records = result.Records;
        var resolver = result.Resolver;

        var failures = new List<string>();
        var longSources = 0;
        var longDecompilations = 0;
        foreach (var script in records.Scripts)
        {
            var sourceIsLong = script.SourceText is { Length: > PanelCap };
            var decompiledIsLong = script.DecompiledText is { Length: > PanelCap };
            if (!sourceIsLong && !decompiledIsLong)
            {
                continue;
            }

            longSources += sourceIsLong ? 1 : 0;
            longDecompilations += decompiledIsLong ? 1 : 0;
            var label = $"0x{script.FormId:X8} {script.EditorId}";
            string output;
            try
            {
                output = Render(records, resolver, script.FormId, null, true);
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{label}: {ex.Message}");
                continue;
            }

            if (!string.IsNullOrEmpty(script.SourceText) &&
                !output.Contains(script.SourceText, StringComparison.Ordinal))
            {
                failures.Add($"{label}: source text ({script.SourceText.Length:N0} chars) not printed verbatim");
            }

            if (!string.IsNullOrEmpty(script.DecompiledText) &&
                !output.Contains(script.DecompiledText, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{label}: decompiled text ({script.DecompiledText.Length:N0} chars) not printed verbatim");
            }

            if (output.Contains("(truncated", StringComparison.Ordinal))
            {
                failures.Add($"{label}: --full output still carries a truncation marker");
            }
        }

        Assert.True(longSources >= MinimumLongScripts,
            $"Only {longSources} scripts have SCTX over {PanelCap:N0} characters " +
            $"({longDecompilations} decompilations); expected at least {MinimumLongScripts}.");
        Assert.True(failures.Count == 0,
            $"{failures.Count} long scripts did not print completely under --full:\n" +
            string.Join("\n", failures.Take(20)));

        // The default panel for BooneSCRIPT (SCTX ~2,923 characters): cut, with a marker that names the
        // script's own total and --full, under the authored-plugin label.
        var boone = Assert.Single(records.Scripts,
            s => string.Equals(s.EditorId, "BooneSCRIPT", StringComparison.OrdinalIgnoreCase));
        Assert.True(boone.SourceText is { Length: > PanelCap }, "BooneSCRIPT's SCTX is no longer over the cap.");
        var booneDefault = Render(records, resolver, null, "BooneSCRIPT", false);
        var total = boone.SourceText!.Length.ToString("N0", CultureInfo.InvariantCulture);
        Assert.Contains($"... (truncated: 2,000 of {total} characters shown; rerun with --full)", booneDefault,
            StringComparison.Ordinal);
        Assert.Contains("Source (SCTX):", booneDefault, StringComparison.Ordinal);
        Assert.Contains("Compiled flag: True", booneDefault, StringComparison.Ordinal);
        Assert.Contains("Bytecode:   Little-Endian (ScriptName anchor)", booneDefault, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The July 2010 prototype is a big-endian container whose serialized SCDA is little-endian. Its
    ///     VDialogueRexScript used to show as an uncompiled Effect script decoding to UnknownFunc_0x1D00; the
    ///     header lines now read the SCHR type/flags bytes unswapped and name the payload's own order.
    /// </summary>
    [Fact]
    public async Task X360July_RexScript_ShowsCompiledFlagAndPayloadByteOrder()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.NewVegasBuilds.X360July2010();
        Assert.SkipWhen(esm is null, RealAssetPaths.SkipMessage("July 2010 X360 FalloutNV.esm"));

        // Cache-owned result: never dispose it.
        var result = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);

        var output = Render(result.Records, result.Resolver, JulyRexScriptFormId, null, true);

        Assert.Contains("SCPT VDialogueRexScript", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Type:       Quest", output, StringComparison.Ordinal);
        Assert.Contains("Compiled:   42 bytes", output, StringComparison.Ordinal);
        Assert.Contains("Compiled flag: True", output, StringComparison.Ordinal);
        Assert.Contains("Bytecode:   Little-Endian (ScriptName anchor)", output, StringComparison.Ordinal);
        Assert.Contains("VNPCFollowers (0x000B16D0)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("UnknownFunc", output, StringComparison.Ordinal);
    }

    private static string Render(RecordCollection records, FormIdResolver resolver, uint? formId, string? editorId,
        bool fullText)
    {
        var rendered = false;
        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = WideConsole;
            rendered = ShowCommand.TryRender(records, resolver, formId, editorId,
                new ShowRenderContext(console, fullText));
        });

        Assert.True(rendered, $"No show renderer claimed {(formId is { } id ? $"0x{id:X8}" : editorId)}.");
        return output;
    }
}
