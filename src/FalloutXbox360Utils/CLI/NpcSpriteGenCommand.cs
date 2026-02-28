using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for rendering NPC head sprites from BSA + ESM data.
///     Phase 2: Base head mesh + EGM morph application.
/// </summary>
public static class NpcSpriteGenCommand
{
    private static readonly Logger Log = Logger.Instance;
    public static Command Create()
    {
        var command = new Command("npc", "Render NPC head sprites from BSA + ESM data");

        var inputArg = new Argument<string>("meshes-bsa") { Description = "Path to meshes BSA file" };
        var esmOption = new Option<string>("--esm") { Description = "Path to ESM file", Required = true };
        var texturesBsaOption = new Option<string>("--textures-bsa") { Description = "Path to textures BSA file", Required = true };
        var outputOption = new Option<string>("-o", "--output") { Description = "Output directory for sprites", Required = true };
        var npcOption = new Option<string[]?>("--npc") { Description = "Render specific NPCs by FormID (e.g., --npc 0x00104C0C --npc 0x00133FDD)", AllowMultipleArgumentsPerToken = true };
        var sizeOption = new Option<int>("--size") { Description = "Sprite size in pixels (longest edge)", DefaultValueFactory = _ => 512 };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Show debug output (bone transforms, EGM details, bounds)" };
        var dmpOption = new Option<string?>("--dmp") { Description = "Path to Xbox 360 memory dump (.dmp) — uses DMP-sourced FaceGen coefficients" };

        command.Arguments.Add(inputArg);
        command.Options.Add(esmOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(outputOption);
        command.Options.Add(npcOption);
        command.Options.Add(sizeOption);
        command.Options.Add(verboseOption);
        command.Options.Add(dmpOption);

        command.SetAction((parseResult, _) =>
        {
            Log.SetVerbose(parseResult.GetValue(verboseOption));

            var settings = new NpcSpriteSettings
            {
                MeshesBsaPath = parseResult.GetValue(inputArg)!,
                EsmPath = parseResult.GetValue(esmOption)!,
                TexturesBsaPath = parseResult.GetValue(texturesBsaOption)!,
                OutputDir = parseResult.GetValue(outputOption)!,
                NpcFormIds = ParseFormIds(parseResult.GetValue(npcOption)),
                SpriteSize = parseResult.GetValue(sizeOption),
                DmpPath = parseResult.GetValue(dmpOption)
            };

            Run(settings);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void Run(NpcSpriteSettings s)
    {
        // Validate inputs
        if (!File.Exists(s.MeshesBsaPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Meshes BSA not found: {0}", s.MeshesBsaPath);
            return;
        }

        if (!File.Exists(s.EsmPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ESM file not found: {0}", s.EsmPath);
            return;
        }

        if (!File.Exists(s.TexturesBsaPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Textures BSA not found: {0}", s.TexturesBsaPath);
            return;
        }

        if (s.DmpPath != null && !File.Exists(s.DmpPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] DMP file not found: {0}", s.DmpPath);
            return;
        }

        Directory.CreateDirectory(s.OutputDir);

        // Load ESM
        AnsiConsole.MarkupLine("Loading ESM: [cyan]{0}[/]", Path.GetFileName(s.EsmPath));
        var esm = EsmFileLoader.Load(s.EsmPath, printStatus: false);
        if (esm == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to load ESM file");
            return;
        }

        // Build NPC appearance resolver
        AnsiConsole.MarkupLine("Scanning NPC_ and RACE records...");
        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        AnsiConsole.MarkupLine("Found [green]{0}[/] NPCs, [green]{1}[/] races", resolver.NpcCount, resolver.RaceCount);

        // Parse meshes BSA
        AnsiConsole.MarkupLine("Parsing meshes BSA: [cyan]{0}[/]", Path.GetFileName(s.MeshesBsaPath));
        var meshesArchive = BsaParser.Parse(s.MeshesBsaPath);

        // Create texture resolver
        AnsiConsole.MarkupLine("Loading textures BSA: [cyan]{0}[/]", Path.GetFileName(s.TexturesBsaPath));
        using var textureResolver = new NifTextureResolver(s.TexturesBsaPath);

        // Determine plugin name for FaceGen paths
        var pluginName = Path.GetFileName(s.EsmPath);

        // Resolve appearances — either from DMP or ESM
        List<NpcAppearance> appearances;
        if (s.DmpPath != null)
        {
            appearances = ResolveFromDmp(s.DmpPath, resolver, pluginName, s.NpcFormIds?.FirstOrDefault());
            if (appearances.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No NPCs resolved from DMP[/]");
                return;
            }
        }
        else if (s.NpcFormIds is { Count: > 0 })
        {
            appearances = [];
            foreach (var formId in s.NpcFormIds)
            {
                var appearance = resolver.ResolveHeadOnly(formId, pluginName);
                if (appearance == null)
                {
                    AnsiConsole.MarkupLine("[yellow]Warning:[/] NPC 0x{0:X8} not found in ESM", formId);
                    continue;
                }

                appearances.Add(appearance);
            }

            if (appearances.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] None of the specified NPCs found in ESM");
                return;
            }
        }
        else
        {
            appearances = resolver.ResolveAllHeadOnly(pluginName, filterNamed: true);
            AnsiConsole.MarkupLine("Resolved [green]{0}[/] named NPCs", appearances.Count);
        }

        // Cache head meshes, EGM morphs, and EGT morphs per race/gender to avoid re-extracting
        var headMeshCache = new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache = new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache = new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        using var meshExtractor = new BsaExtractor(s.MeshesBsaPath);
        var rendered = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var npc in appearances)
        {
            try
            {
                var result = RenderNpcHead(npc, meshesArchive, meshExtractor, textureResolver,
                    headMeshCache, egmCache, egtCache, s.SpriteSize);

                if (result == null)
                {
                    skipped++;
                    if (s.NpcFormIds != null)
                    {
                        AnsiConsole.MarkupLine("[yellow]Skipped:[/] 0x{0:X8} {1} — no head mesh found",
                            npc.NpcFormId, npc.FullName ?? npc.EditorId ?? "unknown");
                    }

                    continue;
                }

                // Save sprite
                var name = npc.EditorId ?? $"{npc.NpcFormId:X8}";
                var fileName = $"{name}.png";
                var outputPath = Path.Combine(s.OutputDir, fileName);
                PngWriter.SaveRgba(result.Pixels, result.Width, result.Height, outputPath);
                rendered++;

                if (s.NpcFormIds != null || appearances.Count <= 20)
                {
                    AnsiConsole.MarkupLine("[green]OK:[/] 0x{0:X8} {1} → {2} ({3}x{4})",
                        npc.NpcFormId, npc.FullName ?? "?", fileName, result.Width, result.Height);
                }
            }
            catch (Exception ex)
            {
                failed++;
                AnsiConsole.MarkupLine("[red]FAIL:[/] 0x{0:X8} {1}: {2}",
                    npc.NpcFormId, npc.EditorId ?? "?", Markup.Escape(ex.Message));
            }
        }

        AnsiConsole.MarkupLine("\nRendered: [green]{0}[/]  Skipped: [yellow]{1}[/]  Failed: [red]{2}[/]",
            rendered, skipped, failed);
    }

    /// <summary>
    ///     Loads NPC records from a DMP file and resolves their appearance using ESM asset data.
    /// </summary>
    private static List<NpcAppearance> ResolveFromDmp(
        string dmpPath, NpcAppearanceResolver resolver, string pluginName, uint? filterFormId)
    {
        if (!File.Exists(dmpPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] DMP file not found: {0}", dmpPath);
            return [];
        }

        AnsiConsole.MarkupLine("Loading DMP: [cyan]{0}[/]", Path.GetFileName(dmpPath));
        var fileInfo = new FileInfo(dmpPath);

        using var mmf = MemoryMappedFile.CreateFromFile(dmpPath, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        // Parse minidump header
        var minidumpInfo = MinidumpParser.Parse(dmpPath);
        if (!minidumpInfo.IsValid)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid minidump format");
            return [];
        }

        // Scan for runtime Editor IDs (walks pAllForms hash table)
        var scanResult = EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileInfo.Length);
        EsmEditorIdExtractor.ExtractRuntimeEditorIds(accessor, fileInfo.Length, minidumpInfo, scanResult, false);

        // Filter NPC_ entries (FormType 0x2A)
        var npcEntries = scanResult.RuntimeEditorIds
            .Where(e => e.FormType == 0x2A)
            .ToList();

        if (npcEntries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No NPC_ entries found in DMP runtime hash table[/]");
            return [];
        }

        AnsiConsole.MarkupLine("Found [green]{0}[/] NPC_ entries in DMP", npcEntries.Count);

        // Read NPC structs from DMP memory
        var structReader = new RuntimeStructReader(accessor, fileInfo.Length, minidumpInfo);
        var appearances = new List<NpcAppearance>();

        foreach (var entry in npcEntries)
        {
            if (filterFormId.HasValue && entry.FormId != filterFormId.Value)
                continue;

            var npcRecord = structReader.ReadRuntimeNpc(entry);
            if (npcRecord == null)
            {
                Log.Debug("Failed to read NPC struct for 0x{0:X8} ({1})", entry.FormId, entry.EditorId);
                continue;
            }

            var appearance = resolver.ResolveFromDmpRecord(npcRecord, pluginName);
            if (appearance == null)
            {
                Log.Debug("Failed to resolve appearance for 0x{0:X8} ({1})", entry.FormId, entry.EditorId);
                continue;
            }

            // Coefficient comparison diagnostic (verbose)
            CompareWithEsmCoefficients(resolver, appearance, npcRecord, pluginName);

            appearances.Add(appearance);
        }

        AnsiConsole.MarkupLine("Resolved [green]{0}[/] NPC appearances from DMP", appearances.Count);
        return appearances;
    }

    /// <summary>
    ///     Compares DMP-sourced coefficients against ESM-sourced coefficients for validation.
    ///     Only emits output when verbose logging is enabled.
    /// </summary>
    private static void CompareWithEsmCoefficients(
        NpcAppearanceResolver resolver, NpcAppearance dmpAppearance,
        Core.Formats.Esm.Models.NpcRecord npcRecord, string pluginName)
    {
        var esmAppearance = resolver.ResolveHeadOnly(npcRecord.FormId, pluginName);
        if (esmAppearance == null)
        {
            Log.Debug("NPC 0x{0:X8} ({1}): not found in ESM — no coefficient comparison",
                npcRecord.FormId, npcRecord.EditorId ?? "?");
            return;
        }

        var fggsMatch = CountMatches(dmpAppearance.FaceGenSymmetricCoeffs, esmAppearance.FaceGenSymmetricCoeffs);
        var fggaMatch = CountMatches(dmpAppearance.FaceGenAsymmetricCoeffs, esmAppearance.FaceGenAsymmetricCoeffs);
        var fgtsMatch = CountMatches(dmpAppearance.FaceGenTextureCoeffs, esmAppearance.FaceGenTextureCoeffs);

        Log.Debug("NPC 0x{0:X8} ({1}): FGGS match={2}/{3}, FGGA match={4}/{5}, FGTS match={6}/{7}",
            npcRecord.FormId, npcRecord.EditorId ?? "?",
            fggsMatch.matched, fggsMatch.total,
            fggaMatch.matched, fggaMatch.total,
            fgtsMatch.matched, fgtsMatch.total);
    }

    private static (int matched, int total) CountMatches(float[]? a, float[]? b)
    {
        if (a == null && b == null) return (0, 0);
        if (a == null || b == null) return (0, Math.Max(a?.Length ?? 0, b?.Length ?? 0));

        var total = Math.Min(a.Length, b.Length);
        var matched = 0;
        for (var i = 0; i < total; i++)
        {
            if (Math.Abs(a[i] - b[i]) < 0.001f)
                matched++;
        }

        return (matched, total);
    }

    private static SpriteResult? RenderNpcHead(
        NpcAppearance npc,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, NifRenderableModel?> headMeshCache,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        int spriteSize)
    {
        // Try to load the base head mesh from the race (primary path)
        NifRenderableModel? model = null;
        var headTexturePath = npc.HeadDiffuseOverride;
        var usedBaseRaceMesh = false;
        Dictionary<string, System.Numerics.Matrix4x4>? headBoneTransforms = null;

        if (npc.BaseHeadNifPath != null)
        {
            // Cache key: race head mesh path (shared by all NPCs of same race/gender)
            var cacheKey = npc.BaseHeadNifPath;

            if (!headMeshCache.TryGetValue(cacheKey, out var cached))
            {
                cached = LoadNifFromBsa(npc.BaseHeadNifPath, meshesArchive, meshExtractor, textureResolver);
                headMeshCache[cacheKey] = cached;
            }

            if (cached != null)
            {
                // Deep-clone positions to avoid mutating shared cache when morphs are applied
                model = DeepCloneModel(cached);
                usedBaseRaceMesh = true;

                // Extract bone transforms from head NIF for positioning attachments (hair, eyes)
                var headRaw = LoadNifRawFromBsa(npc.BaseHeadNifPath, meshesArchive, meshExtractor);
                if (headRaw != null)
                {
                    headBoneTransforms = NifGeometryExtractor.ExtractNamedBoneTransforms(
                        headRaw.Value.Data, headRaw.Value.Info);

                    if (headBoneTransforms.Count == 0)
                    {
                        Log.Warn("Head NIF has 0 named bone transforms: {0}", npc.BaseHeadNifPath);
                    }
                }
                else
                {
                    Log.Warn("Failed to load raw head NIF for bone extraction: {0}", npc.BaseHeadNifPath);
                }
            }
        }

        // Fallback: try per-NPC FaceGen mesh (already pre-morphed, skip EGM)
        if (model == null && npc.FaceGenNifPath != null)
        {
            model = LoadNifFromBsa(npc.FaceGenNifPath, meshesArchive, meshExtractor, textureResolver);
        }

        if (model == null || !model.HasGeometry)
            return null;

        // Apply EGM morphs only when using the base race mesh (FaceGen fallback is pre-morphed)
        if (usedBaseRaceMesh && npc.BaseHeadNifPath != null &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var egmPath = Path.ChangeExtension(npc.BaseHeadNifPath, ".egm");

            if (!egmCache.TryGetValue(egmPath, out var egm))
            {
                egm = LoadEgmFromBsa(egmPath, meshesArchive, meshExtractor);
                egmCache[egmPath] = egm;
            }

            if (egm != null)
            {
                FaceGenMeshMorpher.Apply(model, egm,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs);
            }
        }

        // Apply head texture override from RACE INDX 0 ICON
        string? fullTexPath = null;
        if (headTexturePath != null)
        {
            fullTexPath = "textures\\" + headTexturePath;
            foreach (var submesh in model.Submeshes)
            {
                submesh.DiffuseTexturePath = fullTexPath;
            }
        }

        // Apply EGT texture morphs (only when using base race mesh and have FGTS coefficients)
        if (usedBaseRaceMesh && npc.BaseHeadNifPath != null &&
            npc.FaceGenTextureCoeffs != null && fullTexPath != null)
        {
            var egtPath = Path.ChangeExtension(npc.BaseHeadNifPath, ".egt");

            if (!egtCache.TryGetValue(egtPath, out var egt))
            {
                egt = LoadEgtFromBsa(egtPath, meshesArchive, meshExtractor);
                egtCache[egtPath] = egt;
            }

            if (egt != null)
            {
                // Load the base texture, apply EGT morphs, inject per-NPC result
                var baseTexture = textureResolver.GetTexture(fullTexPath);
                if (baseTexture != null)
                {
                    var morphed = FaceGenTextureMorpher.Apply(baseTexture, egt, npc.FaceGenTextureCoeffs);
                    if (morphed != null)
                    {
                        // Inject under a unique per-NPC key so it doesn't pollute the shared cache
                        var npcTexKey = $"facegen_egt\\{npc.NpcFormId:X8}.dds";
                        textureResolver.InjectTexture(npcTexKey, morphed);
                        foreach (var submesh in model.Submeshes)
                        {
                            submesh.DiffuseTexturePath = npcTexKey;
                        }
                    }
                    else
                    {
                        Log.Warn("EGT texture morph returned null for NPC 0x{0:X8} (base texture: {1})",
                            npc.NpcFormId, fullTexPath);
                    }
                }
                else
                {
                    Log.Warn("Base head texture not found for EGT morph: {0}", fullTexPath);
                }
            }
        }

        Log.Debug("Head bounds: ({0:F2}, {1:F2}, {2:F2}) → ({3:F2}, {4:F2}, {5:F2})",
            model.MinX, model.MinY, model.MinZ, model.MaxX, model.MaxY, model.MaxZ);

        // Load and attach hair mesh
        if (npc.HairNifPath != null)
        {
            // Hair NIFs contain "NoHat" (full hair) and "Hat" (trimmed for headgear) variants.
            // Engine AttachHairToHead picks one based on equipment; we always use "NoHat".
            var hairModel = LoadNifFromBsa(npc.HairNifPath, meshesArchive, meshExtractor, textureResolver,
                filterShapeName: "NoHat");
            if (hairModel != null && hairModel.HasGeometry)
            {
                // Hair NIFs are NOT skinned — vertices are in Bip01 Head local space.
                // Engine pipeline (AttachHairToHead, VA 0x8248BC58):
                //   1. Apply -π/2 Y-axis rotation to hair local transform (NiMatrix3::MakeYRotation)
                //   2. Attach hair as child of head NiNode → inherits Bip01 Head world transform
                // In our pipeline, head mesh is already in world space after skinning, so we only
                // need to translate hair to the Bip01 Head bone position. The engine's -π/2 rotation
                // + bone rotation compose to identity relative to our coordinate frame.
                if (headBoneTransforms != null &&
                    headBoneTransforms.TryGetValue("Bip01 Head", out var headBoneMatrix))
                {
                    var offset = headBoneMatrix.Translation;
                    Log.Debug("Offsetting hair by Bip01 Head: ({0:F4}, {1:F4}, {2:F4})",
                        offset.X, offset.Y, offset.Z);
                    foreach (var sub in hairModel.Submeshes)
                    {
                        for (var i = 0; i < sub.Positions.Length; i += 3)
                        {
                            sub.Positions[i] += offset.X;
                            sub.Positions[i + 1] += offset.Y;
                            sub.Positions[i + 2] += offset.Z;
                        }
                    }
                }
                else
                {
                    Log.Warn("'Bip01 Head' bone not found — hair will render at origin. " +
                             "Available bones: {0}",
                        headBoneTransforms != null
                            ? string.Join(", ", headBoneTransforms.Keys)
                            : "(headBoneTransforms is null)");
                }

                // Apply same EGM morphs to hair (engine uses same FGGS/FGGA coefficients)
                if (usedBaseRaceMesh &&
                    (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
                {
                    // Hair EGMs use "{name}nohat.egm" / "{name}hat.egm" naming, not "{name}.egm"
                    var hairBaseName = Path.GetFileNameWithoutExtension(npc.HairNifPath);
                    var hairDir = Path.GetDirectoryName(npc.HairNifPath) ?? "";
                    var hairEgmPath = Path.Combine(hairDir, hairBaseName + "nohat.egm");

                    if (!egmCache.TryGetValue(hairEgmPath, out var hairEgm))
                    {
                        hairEgm = LoadEgmFromBsa(hairEgmPath, meshesArchive, meshExtractor);
                        egmCache[hairEgmPath] = hairEgm;
                    }

                    if (hairEgm != null)
                    {
                        Log.Debug("Hair EGM '{0}': {1} sym + {2} asym morphs, {3} vertices",
                            hairEgmPath, hairEgm.SymmetricMorphs.Length,
                            hairEgm.AsymmetricMorphs.Length, hairEgm.VertexCount);
                        FaceGenMeshMorpher.Apply(hairModel, hairEgm,
                            npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs);
                    }
                }

                // Merge hair submeshes into head model, injecting alpha property where needed
                foreach (var sub in hairModel.Submeshes)
                {
                    // Engine injects NiAlphaProperty on hair if slot 0 is empty
                    if (!sub.HasAlphaBlend && !sub.HasAlphaTest)
                    {
                        sub.HasAlphaBlend = true;
                        sub.HasAlphaTest = true;
                        sub.AlphaTestThreshold = 128;
                    }

                    // Engine renders hair AFTER head (scene graph child order).
                    // RenderOrder=1 ensures hair triangles sort after all head triangles.
                    sub.RenderOrder = 1;
                    model.Submeshes.Add(sub);
                    model.ExpandBounds(sub.Positions);
                }
            }
            else
            {
                Log.Warn("Hair NIF failed to load or has no geometry: {0}", npc.HairNifPath);
            }
        }

        // Load and attach eye meshes (left and right independently)
        // Eye NIFs are in Bip01 Head local space — use same bone offset as hair.
        // Head NIF's truncated skeleton doesn't include eye-specific bones.
        AttachEyeMesh(npc.LeftEyeNifPath, "Bip01 Head", npc, model, headBoneTransforms,
            usedBaseRaceMesh, meshesArchive, meshExtractor, textureResolver, egmCache);
        AttachEyeMesh(npc.RightEyeNifPath, "Bip01 Head", npc, model, headBoneTransforms,
            usedBaseRaceMesh, meshesArchive, meshExtractor, textureResolver, egmCache);

        // Load and attach head part meshes (eyebrows, beards, teeth, etc. from PNAM → HDPT)
        if (npc.HeadPartNifPaths != null)
        {
            foreach (var partPath in npc.HeadPartNifPaths)
            {
                var partModel = LoadNifFromBsa(partPath, meshesArchive, meshExtractor, textureResolver);
                if (partModel == null || !partModel.HasGeometry)
                {
                    Log.Warn("Head part NIF failed to load: {0}", partPath);
                    continue;
                }

                // Head parts are NOT skinned — vertices are in Bip01 Head local space.
                // Engine attaches as child of head NiNode; we translate by bone position.
                if (headBoneTransforms != null &&
                    headBoneTransforms.TryGetValue("Bip01 Head", out var headBone))
                {
                    var offset = headBone.Translation;
                    foreach (var sub in partModel.Submeshes)
                    {
                        for (var i = 0; i < sub.Positions.Length; i += 3)
                        {
                            sub.Positions[i] += offset.X;
                            sub.Positions[i + 1] += offset.Y;
                            sub.Positions[i + 2] += offset.Z;
                        }
                    }
                }

                // Apply EGM morphs (same FGGS/FGGA coefficients as head)
                if (usedBaseRaceMesh &&
                    (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
                {
                    var egmPath = Path.ChangeExtension(partPath, ".egm");
                    if (!egmCache.TryGetValue(egmPath, out var egm))
                    {
                        egm = LoadEgmFromBsa(egmPath, meshesArchive, meshExtractor);
                        egmCache[egmPath] = egm;
                    }

                    if (egm != null)
                    {
                        FaceGenMeshMorpher.Apply(partModel, egm,
                            npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs);
                    }
                }

                // Merge into head model. RenderOrder=3 (after head=0, hair=1, eyes=2).
                foreach (var sub in partModel.Submeshes)
                {
                    sub.RenderOrder = 3;
                    model.Submeshes.Add(sub);
                    model.ExpandBounds(sub.Positions);
                }
            }
        }

        // Render front view (azimuth 90° = camera at +Y looking toward face)
        return NifSpriteRenderer.Render(model, textureResolver,
            pixelsPerUnit: 1.0f, minSize: 32, maxSize: spriteSize,
            azimuthDeg: 90f, elevationDeg: 0f,
            fixedSize: spriteSize);
    }

    private static EgmParser? LoadEgmFromBsa(string bsaPath, BsaArchive archive, BsaExtractor extractor)
    {
        var fileRecord = archive.FindFile(bsaPath);
        if (fileRecord == null)
        {
            Log.Warn("EGM not found in BSA: {0}", bsaPath);
            return null;
        }

        var data = extractor.ExtractFile(fileRecord);
        if (data.Length == 0)
        {
            Log.Warn("EGM extracted but empty (0 bytes): {0}", bsaPath);
            return null;
        }

        return EgmParser.Parse(data);
    }

    private static EgtParser? LoadEgtFromBsa(string bsaPath, BsaArchive archive, BsaExtractor extractor)
    {
        var fileRecord = archive.FindFile(bsaPath);
        if (fileRecord == null)
        {
            Log.Warn("EGT not found in BSA: {0}", bsaPath);
            return null;
        }

        var data = extractor.ExtractFile(fileRecord);
        if (data.Length == 0)
        {
            Log.Warn("EGT extracted but empty (0 bytes): {0}", bsaPath);
            return null;
        }

        return EgtParser.Parse(data);
    }

    /// <summary>
    ///     Loads an eye NIF, positions it via bone transform, applies EGM morphs,
    ///     overrides eye texture, and merges into the head model.
    /// </summary>
    private static void AttachEyeMesh(
        string? eyeNifPath,
        string boneName,
        NpcAppearance npc,
        NifRenderableModel model,
        Dictionary<string, System.Numerics.Matrix4x4>? headBoneTransforms,
        bool usedBaseRaceMesh,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache)
    {
        if (eyeNifPath == null)
            return;

        var eyeModel = LoadNifFromBsa(eyeNifPath, meshesArchive, meshExtractor, textureResolver);
        if (eyeModel == null || !eyeModel.HasGeometry)
        {
            Log.Warn("Eye NIF failed to load or has no geometry: {0}", eyeNifPath);
            return;
        }

        Log.Debug("Eye '{0}' bounds (local): ({1:F2}, {2:F2}, {3:F2}) → ({4:F2}, {5:F2}, {6:F2})",
            eyeNifPath, eyeModel.MinX, eyeModel.MinY, eyeModel.MinZ,
            eyeModel.MaxX, eyeModel.MaxY, eyeModel.MaxZ);

        // Eye NIFs are NOT skinned — transform from eye-NIF-local space to world space.
        // The engine attaches eye NiNode as child of head NiNode, so:
        //   world_pos = head_bone_world_matrix × eye_NIF_pos
        // We must apply the FULL bone matrix (rotation + translation), not just translation,
        // because the eye vertex offsets from bone center are directional (forward/up/left-right)
        // and the bone rotation maps these directions to world axes.
        if (headBoneTransforms != null &&
            headBoneTransforms.TryGetValue(boneName, out var eyeBoneMatrix))
        {
            var t = eyeBoneMatrix.Translation;
            Log.Debug("Transforming eye by {0} matrix: T=({1:F4}, {2:F4}, {3:F4}), " +
                      "R0=({4:F4}, {5:F4}, {6:F4}), R1=({7:F4}, {8:F4}, {9:F4}), R2=({10:F4}, {11:F4}, {12:F4})",
                boneName, t.X, t.Y, t.Z,
                eyeBoneMatrix.M11, eyeBoneMatrix.M12, eyeBoneMatrix.M13,
                eyeBoneMatrix.M21, eyeBoneMatrix.M22, eyeBoneMatrix.M23,
                eyeBoneMatrix.M31, eyeBoneMatrix.M32, eyeBoneMatrix.M33);
            foreach (var sub in eyeModel.Submeshes)
            {
                for (var i = 0; i < sub.Positions.Length; i += 3)
                {
                    var v = System.Numerics.Vector3.Transform(
                        new System.Numerics.Vector3(sub.Positions[i], sub.Positions[i + 1], sub.Positions[i + 2]),
                        eyeBoneMatrix);
                    sub.Positions[i] = v.X;
                    sub.Positions[i + 1] = v.Y;
                    sub.Positions[i + 2] = v.Z;
                }
            }
        }
        else
        {
            Log.Warn("'{0}' bone not found — eye will render at origin. Available bones: {1}",
                boneName,
                headBoneTransforms != null
                    ? string.Join(", ", headBoneTransforms.Keys)
                    : "(headBoneTransforms is null)");
        }

        // Apply EGM morphs to eye (same FGGS/FGGA coefficients as head)
        if (usedBaseRaceMesh &&
            (npc.FaceGenSymmetricCoeffs != null || npc.FaceGenAsymmetricCoeffs != null))
        {
            var eyeEgmPath = Path.ChangeExtension(eyeNifPath, ".egm");

            if (!egmCache.TryGetValue(eyeEgmPath, out var eyeEgm))
            {
                eyeEgm = LoadEgmFromBsa(eyeEgmPath, meshesArchive, meshExtractor);
                egmCache[eyeEgmPath] = eyeEgm;
            }

            if (eyeEgm != null)
            {
                Log.Debug("Eye EGM '{0}': {1} sym + {2} asym morphs, {3} vertices",
                    eyeEgmPath, eyeEgm.SymmetricMorphs.Length,
                    eyeEgm.AsymmetricMorphs.Length, eyeEgm.VertexCount);
                FaceGenMeshMorpher.Apply(eyeModel, eyeEgm,
                    npc.FaceGenSymmetricCoeffs, npc.FaceGenAsymmetricCoeffs);
            }
        }

        // Override eye texture from EYES record (if NPC has one)
        if (npc.EyeTexturePath != null)
        {
            foreach (var sub in eyeModel.Submeshes)
            {
                sub.DiffuseTexturePath = npc.EyeTexturePath;
            }
        }

        // Merge eye submeshes into head model.
        // Engine renders eyes AFTER hair (scene graph order). RenderOrder=2.
        foreach (var sub in eyeModel.Submeshes)
        {
            sub.RenderOrder = 2;
            model.Submeshes.Add(sub);
            model.ExpandBounds(sub.Positions);
        }
    }

    /// <summary>
    ///     Loads a NIF from BSA, converts BE→LE if needed, and extracts renderable geometry.
    /// </summary>
    private static NifRenderableModel? LoadNifFromBsa(
        string bsaPath,
        BsaArchive archive,
        BsaExtractor extractor,
        NifTextureResolver textureResolver,
        Dictionary<string, System.Numerics.Matrix4x4>? externalBoneTransforms = null,
        string? filterShapeName = null)
    {
        var result = LoadNifRawFromBsa(bsaPath, archive, extractor);
        if (result == null)
            return null;

        return NifGeometryExtractor.Extract(result.Value.Data, result.Value.Info, textureResolver,
            externalBoneTransforms: externalBoneTransforms, filterShapeName: filterShapeName);
    }

    /// <summary>
    ///     Loads and converts a NIF from BSA, returning the raw byte data and parsed NifInfo
    ///     for use with ExtractNamedBoneTransforms() or other NIF inspection.
    /// </summary>
    private static (byte[] Data, NifInfo Info)? LoadNifRawFromBsa(
        string bsaPath,
        BsaArchive archive,
        BsaExtractor extractor)
    {
        var fileRecord = archive.FindFile(bsaPath);
        if (fileRecord == null)
        {
            Log.Warn("NIF not found in BSA: {0}", bsaPath);
            return null;
        }

        var nifData = extractor.ExtractFile(fileRecord);
        if (nifData.Length == 0)
        {
            Log.Warn("NIF extracted but empty (0 bytes): {0}", bsaPath);
            return null;
        }

        var nif = NifParser.Parse(nifData);
        if (nif == null)
        {
            Log.Warn("NIF parse failed ({0} bytes): {1}", nifData.Length, bsaPath);
            return null;
        }

        // Convert Xbox 360 big-endian NIFs
        if (nif.IsBigEndian)
        {
            var converted = NifConverter.Convert(nifData);
            if (!converted.Success || converted.OutputData == null)
            {
                Log.Warn("NIF BE→LE conversion failed: {0}", bsaPath);
                return null;
            }

            nifData = converted.OutputData;
            nif = NifParser.Parse(nifData);
            if (nif == null)
            {
                Log.Warn("NIF re-parse failed after BE→LE conversion: {0}", bsaPath);
                return null;
            }
        }

        return (nifData, nif);
    }

    private static NifRenderableModel DeepCloneModel(NifRenderableModel source)
    {
        var clone = new NifRenderableModel
        {
            MinX = source.MinX,
            MinY = source.MinY,
            MinZ = source.MinZ,
            MaxX = source.MaxX,
            MaxY = source.MaxY,
            MaxZ = source.MaxZ
        };

        foreach (var sub in source.Submeshes)
        {
            clone.Submeshes.Add(new RenderableSubmesh
            {
                Positions = (float[])sub.Positions.Clone(),
                Triangles = sub.Triangles,
                Normals = sub.Normals != null ? (float[])sub.Normals.Clone() : null,
                UVs = sub.UVs,
                VertexColors = sub.VertexColors,
                Tangents = sub.Tangents,
                Bitangents = sub.Bitangents,
                DiffuseTexturePath = sub.DiffuseTexturePath,
                NormalMapTexturePath = sub.NormalMapTexturePath,
                IsEmissive = sub.IsEmissive,
                UseVertexColors = sub.UseVertexColors,
                IsDoubleSided = sub.IsDoubleSided,
                HasAlphaBlend = sub.HasAlphaBlend,
                HasAlphaTest = sub.HasAlphaTest,
                AlphaTestThreshold = sub.AlphaTestThreshold
            });
        }

        return clone;
    }

    private static uint? ParseFormId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];

        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var id) ? id : null;
    }

    private static HashSet<uint>? ParseFormIds(string[]? values)
    {
        if (values == null || values.Length == 0)
            return null;

        var set = new HashSet<uint>();
        foreach (var v in values)
        {
            var id = ParseFormId(v);
            if (id.HasValue) set.Add(id.Value);
        }

        return set.Count > 0 ? set : null;
    }

    private sealed class NpcSpriteSettings
    {
        public required string MeshesBsaPath { get; init; }
        public required string EsmPath { get; init; }
        public required string TexturesBsaPath { get; init; }
        public required string OutputDir { get; init; }
        public HashSet<uint>? NpcFormIds { get; init; }
        public int SpriteSize { get; init; } = 512;
        public string? DmpPath { get; init; }
    }
}
