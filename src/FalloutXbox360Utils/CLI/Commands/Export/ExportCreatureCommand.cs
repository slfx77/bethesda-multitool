using System.CommandLine;
using FalloutXbox360Utils.CLI.Rendering.Gltf;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Export;

internal static class ExportCreatureCommand
{
    private static readonly Logger Log = Logger.Instance;

    public static Command Create()
    {
        var command = new Command("creature", "Export creature GLBs from BSA + ESM data");

        var inputArg = new Argument<string>("meshes-bsa")
        {
            Description = "Path to meshes BSA file"
        };
        var extraMeshesBsaOption = new Option<string[]?>("--extra-meshes-bsa")
        {
            Description = "Additional meshes BSA file(s) searched as fallback",
            AllowMultipleArgumentsPerToken = true
        };
        var esmOption = new Option<string>("--esm")
        {
            Description = "Path to ESM file",
            Required = true
        };
        var texturesBsaOption = new Option<string[]?>("--textures-bsa")
        {
            Description = "Path to textures BSA file(s) (auto-detected from meshes BSA directory if omitted)",
            AllowMultipleArgumentsPerToken = true
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for GLBs",
            Required = true
        };
        var creatureOption = new Option<string[]?>("--creature")
        {
            Description = "Export specific creatures by FormID or EditorID (e.g., --creature 0x00104E38 --creature CrDeathclaw)",
            AllowMultipleArgumentsPerToken = true
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show debug output"
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(extraMeshesBsaOption);
        command.Options.Add(esmOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(outputOption);
        command.Options.Add(creatureOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, _) =>
        {
            Log.SetVerbose(parseResult.GetValue(verboseOption));

            var meshesBsaPath = parseResult.GetValue(inputArg)!;
            var extraMeshesBsa = parseResult.GetValue(extraMeshesBsaOption);
            var esmPath = parseResult.GetValue(esmOption)!;
            var texturesBsa = parseResult.GetValue(texturesBsaOption);
            var outputDir = parseResult.GetValue(outputOption)!;
            var creatureFilters = parseResult.GetValue(creatureOption);

            Run(meshesBsaPath, extraMeshesBsa, esmPath, texturesBsa, outputDir, creatureFilters);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void Run(
        string meshesBsaPath,
        string[]? extraMeshesBsa,
        string esmPath,
        string[]? texturesBsaPaths,
        string outputDir,
        string[]? creatureFilters)
    {
        if (!File.Exists(meshesBsaPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Meshes BSA not found: {0}", Markup.Escape(meshesBsaPath));
            return;
        }

        if (!File.Exists(esmPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ESM file not found: {0}", Markup.Escape(esmPath));
            return;
        }

        // Auto-discover texture BSAs from meshes BSA directory if not specified
        if (texturesBsaPaths == null || texturesBsaPaths.Length == 0)
        {
            var bsaDir = Path.GetDirectoryName(meshesBsaPath) ?? ".";
            var discovered = BsaDiscovery.Discover(Path.Combine(bsaDir, Path.GetFileName(esmPath)));
            texturesBsaPaths = discovered.TexturesBsaPaths;
        }

        // Read ESM and scan creature records
        AnsiConsole.MarkupLine("Scanning ESM for creature records...");
        var esmData = File.ReadAllBytes(esmPath);
        var bigEndian = NpcBrowserService.DetectEsmBigEndian(esmData);
        var resolver = NpcAppearanceResolver.Build(esmData, bigEndian);

        var creatures = resolver.GetAllCreatures();
        if (creatures.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No creature records found in ESM.[/]");
            return;
        }

        AnsiConsole.MarkupLine("Found [cyan]{0}[/] creature records", creatures.Count);

        // Filter creatures if specific ones requested
        var targetCreatures = FilterCreatures(creatures, creatureFilters);
        if (targetCreatures.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching creatures found for the given filter(s).[/]");
            return;
        }

        Directory.CreateDirectory(outputDir);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsaPath, extraMeshesBsa);
        using var textureResolver = texturesBsaPaths is { Length: > 0 }
            ? new NifTextureResolver(texturesBsaPaths)
            : new NifTextureResolver();

        var exported = 0;
        var skipped = 0;

        AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .Start(ctx =>
            {
                var task = ctx.AddTask("Exporting creatures", maxValue: targetCreatures.Count);

                foreach (var (formId, creature) in targetCreatures)
                {
                    task.Description = $"Exporting {creature.EditorId ?? $"0x{formId:X8}"}";

                    if (creature.SkeletonPath == null || creature.BodyModelPaths is not { Length: > 0 })
                    {
                        Log.Warn("Creature 0x{0:X8} ({1}) has no skeleton or body model paths, skipping",
                            formId, creature.EditorId ?? "?");
                        skipped++;
                        task.Increment(1);
                        continue;
                    }

                    // Resolve first weapon from creature inventory
                    string? weaponMeshPath = null;
                    if (creature.InventoryItems != null)
                    {
                        foreach (var item in creature.InventoryItems)
                        {
                            weaponMeshPath = resolver.ResolveWeaponMeshPath(item.ItemFormId);
                            if (weaponMeshPath != null)
                            {
                                break;
                            }
                        }
                    }

                    var scene = NifExportSceneBuilder.BuildCreature(
                        creature.SkeletonPath, creature.BodyModelPaths, meshArchives,
                        idleAnimationPath: creature.ResolveIdleAnimationPath(),
                        weaponMeshPath: weaponMeshPath);
                    if (scene == null || scene.MeshParts.Count == 0)
                    {
                        Log.Warn("No exportable geometry for creature 0x{0:X8}", formId);
                        skipped++;
                        task.Increment(1);
                        continue;
                    }

                    var fileName = $"{creature.EditorId ?? $"creature_{formId:X8}"}.glb";
                    var outputPath = Path.Combine(outputDir, fileName);

                    NpcGlbWriter.Write(scene, textureResolver, outputPath);
                    try
                    {
                        GltfValidatorRunner.ValidateOrThrow(outputPath);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("GLB validation warning for {0}: {1}", fileName, ex.Message);
                    }

                    exported++;
                    task.Increment(1);
                }
            });

        AnsiConsole.MarkupLine("[green]Exported {0} creature GLBs[/] ({1} skipped)", exported, skipped);
    }

    private static List<KeyValuePair<uint, CreatureScanEntry>> FilterCreatures(
        IReadOnlyDictionary<uint, CreatureScanEntry> creatures,
        string[]? filters)
    {
        if (filters == null || filters.Length == 0)
        {
            return creatures.ToList();
        }

        var result = new List<KeyValuePair<uint, CreatureScanEntry>>();
        var filterSet = new HashSet<string>(filters, StringComparer.OrdinalIgnoreCase);

        foreach (var (formId, creature) in creatures)
        {
            var formIdHex = $"0x{formId:X8}";
            if (filterSet.Contains(formIdHex) ||
                (creature.EditorId != null && filterSet.Contains(creature.EditorId)))
            {
                result.Add(new KeyValuePair<uint, CreatureScanEntry>(formId, creature));
            }
        }

        return result;
    }
}
