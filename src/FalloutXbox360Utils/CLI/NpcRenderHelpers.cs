using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     Shared utility methods for NPC rendering: BSA loading, model cloning,
///     texture resolution, DMP resolution, and geometry transforms.
/// </summary>
internal static class NpcRenderHelpers
{
    private static readonly Logger Log = Logger.Instance;
    internal const uint HeadEquipmentFlags = 0x01 | 0x02 | 0x200 | 0x400 | 0x800 | 0x4000;
    internal const uint HatEquipmentFlags = 0x01 | 0x400 | 0x4000;

    internal enum HeadAttachmentCorrectionMode
    {
        Boned,
        BonelessUseAttachmentTransform
    }

    internal readonly record struct HeadAttachmentCorrectionResult(
        Matrix4x4 Correction,
        HeadAttachmentCorrectionMode Mode,
        float RootDeterminant);

    internal static bool IsHeadEquipment(uint bipedFlags)
    {
        return (bipedFlags & HeadEquipmentFlags) != 0;
    }

    internal static bool HasHatEquipment(IEnumerable<EquippedItem>? equippedItems)
    {
        if (equippedItems == null)
            return false;

        foreach (var item in equippedItems)
        {
            if ((item.BipedFlags & HatEquipmentFlags) != 0)
                return true;
        }

        return false;
    }

    internal static Matrix4x4? BuildBonelessHeadAttachmentTransform(
        IReadOnlyDictionary<string, Matrix4x4>? attachmentBoneTransforms,
        IReadOnlyDictionary<string, Matrix4x4>? poseDeltaCache)
    {
        if (attachmentBoneTransforms == null ||
            !attachmentBoneTransforms.TryGetValue("Bip01 Head", out var targetHeadTransform))
        {
            return null;
        }

        var correction = Matrix4x4.CreateTranslation(targetHeadTransform.Translation);

        if (poseDeltaCache != null &&
            poseDeltaCache.TryGetValue("Bip01 Head", out var headPoseDelta))
        {
            var rotationOnly = RemoveTranslation(headPoseDelta);
            correction = rotationOnly * Matrix4x4.CreateTranslation(targetHeadTransform.Translation);
        }

        return correction;
    }

    internal static Matrix4x4? BuildHeadEquipmentTransformOverride(
        IReadOnlyDictionary<string, Matrix4x4>? attachmentBoneTransforms,
        IReadOnlyDictionary<string, Matrix4x4>? poseDeltaCache)
    {
        return BuildBonelessHeadAttachmentTransform(attachmentBoneTransforms, poseDeltaCache);
    }

    internal static Matrix4x4 BuildRootAdjustedTransform(
        IReadOnlyDictionary<string, Matrix4x4> nifBones,
        Matrix4x4 targetTransform)
    {
        if (!nifBones.TryGetValue(NifGeometryExtractor.RootTransformKey, out var nifRoot))
            return targetTransform;

        var rootRotation = RemoveTranslation(nifRoot);
        if (IsNearlyIdentity(rootRotation) ||
            !Matrix4x4.Invert(rootRotation, out var invRootRotation))
        {
            return targetTransform;
        }

        return invRootRotation * targetTransform;
    }

    /// <summary>
    ///     Transforms all positions (and normals/tangents/bitangents if present) in a submesh by the given matrix.
    /// </summary>
    internal static void TransformSubmesh(RenderableSubmesh sub, Matrix4x4 transform)
    {
        for (var i = 0; i < sub.Positions.Length; i += 3)
        {
            var v = Vector3.Transform(
                new Vector3(sub.Positions[i], sub.Positions[i + 1], sub.Positions[i + 2]),
                transform);
            sub.Positions[i] = v.X;
            sub.Positions[i + 1] = v.Y;
            sub.Positions[i + 2] = v.Z;
        }

        if (sub.Normals != null)
        {
            for (var i = 0; i < sub.Normals.Length; i += 3)
            {
                var n = Vector3.TransformNormal(
                    new Vector3(sub.Normals[i], sub.Normals[i + 1], sub.Normals[i + 2]),
                    transform);
                n = Vector3.Normalize(n);
                sub.Normals[i] = n.X;
                sub.Normals[i + 1] = n.Y;
                sub.Normals[i + 2] = n.Z;
            }
        }

        if (sub.Tangents != null)
        {
            for (var i = 0; i < sub.Tangents.Length; i += 3)
            {
                var t = Vector3.TransformNormal(
                    new Vector3(sub.Tangents[i], sub.Tangents[i + 1], sub.Tangents[i + 2]),
                    transform);
                t = Vector3.Normalize(t);
                sub.Tangents[i] = t.X;
                sub.Tangents[i + 1] = t.Y;
                sub.Tangents[i + 2] = t.Z;
            }
        }

        if (sub.Bitangents != null)
        {
            for (var i = 0; i < sub.Bitangents.Length; i += 3)
            {
                var b = Vector3.TransformNormal(
                    new Vector3(sub.Bitangents[i], sub.Bitangents[i + 1], sub.Bitangents[i + 2]),
                    transform);
                b = Vector3.Normalize(b);
                sub.Bitangents[i] = b.X;
                sub.Bitangents[i + 1] = b.Y;
                sub.Bitangents[i + 2] = b.Z;
            }
        }
    }

    internal static void TransformModel(NifRenderableModel model, Matrix4x4 transform)
    {
        foreach (var sub in model.Submeshes)
            TransformSubmesh(sub, transform);

        model.MinX = float.MaxValue;
        model.MinY = float.MaxValue;
        model.MinZ = float.MaxValue;
        model.MaxX = float.MinValue;
        model.MaxY = float.MinValue;
        model.MaxZ = float.MinValue;

        foreach (var sub in model.Submeshes)
            model.ExpandBounds(sub.Positions);
    }

    /// <summary>
    ///     Applies a correction transform to unskinned head attachment geometry (hair, eyes,
    ///     eyebrows). Boned NIFs (with Bip01 Head): undo the NIF's own bone transform and
    ///     apply the target's. Boneless attachments preserve their authored local basis and
    ///     use an explicit boneless attachment transform instead of inheriting the head
    ///     bone's full basis.
    /// </summary>
    internal static void ApplyHeadBoneCorrection(
        NifRenderableModel model,
        byte[] nifData, NifInfo nifInfo,
        Matrix4x4 targetHeadTransform,
        Matrix4x4? bonelessAttachmentTransform = null,
        string? attachmentLabel = null)
    {
        var nifBones = NifGeometryExtractor.ExtractNamedBoneTransforms(nifData, nifInfo);
        var correctionResult = GetHeadAttachmentCorrection(
            nifBones,
            targetHeadTransform,
            bonelessAttachmentTransform);

        Log.Debug("HeadBoneCorrection[{0}]: {1} NIF bones: [{2}]",
            attachmentLabel ?? "(unnamed)",
            nifBones.Count,
            nifBones.Count > 0 ? string.Join(", ", nifBones.Keys) : "(none)");
        Log.Debug(
            "  Target: T=({0:F2},{1:F2},{2:F2}) R=[{3:F4},{4:F4},{5:F4}] [{6:F4},{7:F4},{8:F4}] [{9:F4},{10:F4},{11:F4}]",
            targetHeadTransform.M41, targetHeadTransform.M42, targetHeadTransform.M43,
            targetHeadTransform.M11, targetHeadTransform.M12, targetHeadTransform.M13,
            targetHeadTransform.M21, targetHeadTransform.M22, targetHeadTransform.M23,
            targetHeadTransform.M31, targetHeadTransform.M32, targetHeadTransform.M33);
        Log.Debug(
            "  Mode={0} correction T=({1:F2},{2:F2},{3:F2}) rootDet={4:F2}",
            correctionResult.Mode,
            correctionResult.Correction.M41,
            correctionResult.Correction.M42,
            correctionResult.Correction.M43,
            correctionResult.RootDeterminant);
        TransformModel(model, correctionResult.Correction);

        if (model.Submeshes.Count > 0)
        {
            Log.Debug("  Post-correction bounds: ({0:F1},{1:F1},{2:F1})→({3:F1},{4:F1},{5:F1})",
                model.MinX, model.MinY, model.MinZ, model.MaxX, model.MaxY, model.MaxZ);
        }
    }

    internal static HeadAttachmentCorrectionResult GetHeadAttachmentCorrection(
        IReadOnlyDictionary<string, Matrix4x4> nifBones,
        Matrix4x4 targetHeadTransform,
        Matrix4x4? bonelessAttachmentTransform = null)
    {
        // Boned NIFs (hair, eyebrows, teeth): undo the NIF's own Bip01 Head and apply target's.
        if (nifBones.TryGetValue("Bip01 Head", out var nifBip01Head) &&
            Matrix4x4.Invert(nifBip01Head, out var invNifBip01Head))
        {
            return new HeadAttachmentCorrectionResult(
                invNifBip01Head * targetHeadTransform,
                HeadAttachmentCorrectionMode.Boned,
                0f);
        }

        // Boneless attachments preserve their authored local root basis. Most head attachments
        // (hair, brows, facial hair, head-slot gear) should use the explicit boneless
        // attachment transform instead of the full head basis. Eyes are the special case:
        // they intentionally omit the override and keep the target head transform so their
        // socket basis remains intact.
        nifBones.TryGetValue("__root__", out var nifRoot);
        var det3x3 = GetDeterminant3x3(RemoveTranslation(nifRoot));
        var effectiveBonelessTransform = bonelessAttachmentTransform ?? targetHeadTransform;

        return new HeadAttachmentCorrectionResult(
            effectiveBonelessTransform,
            HeadAttachmentCorrectionMode.BonelessUseAttachmentTransform,
            det3x3);
    }

    private static Matrix4x4 RemoveTranslation(Matrix4x4 transform)
    {
        transform.M41 = 0f;
        transform.M42 = 0f;
        transform.M43 = 0f;
        transform.M44 = 1f;
        return transform;
    }

    private static bool IsNearlyIdentity(Matrix4x4 transform)
    {
        return MathF.Abs(transform.M11 - 1f) < 0.0001f &&
               MathF.Abs(transform.M22 - 1f) < 0.0001f &&
               MathF.Abs(transform.M33 - 1f) < 0.0001f &&
               MathF.Abs(transform.M44 - 1f) < 0.0001f &&
               MathF.Abs(transform.M12) < 0.0001f &&
               MathF.Abs(transform.M13) < 0.0001f &&
               MathF.Abs(transform.M14) < 0.0001f &&
               MathF.Abs(transform.M21) < 0.0001f &&
               MathF.Abs(transform.M23) < 0.0001f &&
               MathF.Abs(transform.M24) < 0.0001f &&
               MathF.Abs(transform.M31) < 0.0001f &&
               MathF.Abs(transform.M32) < 0.0001f &&
               MathF.Abs(transform.M34) < 0.0001f &&
               MathF.Abs(transform.M41) < 0.0001f &&
               MathF.Abs(transform.M42) < 0.0001f &&
               MathF.Abs(transform.M43) < 0.0001f;
    }

    private static float GetDeterminant3x3(Matrix4x4 transform)
    {
        return transform.M11 * (transform.M22 * transform.M33 - transform.M23 * transform.M32)
             - transform.M12 * (transform.M21 * transform.M33 - transform.M23 * transform.M31)
             + transform.M13 * (transform.M21 * transform.M32 - transform.M22 * transform.M31);
    }

    /// <summary>
    ///     Loads a NIF from BSA, converts BE→LE if needed, and extracts renderable geometry.
    /// </summary>
    internal static NifRenderableModel? LoadNifFromBsa(
        string bsaPath,
        BsaArchive archive,
        BsaExtractor extractor,
        NifTextureResolver textureResolver,
        Dictionary<string, Matrix4x4>? externalBoneTransforms = null,
        string? filterShapeName = null,
        Dictionary<string, Matrix4x4>? externalPoseDeltas = null,
        bool useDualQuaternionSkinning = false)
    {
        var result = LoadNifRawFromBsa(bsaPath, archive, extractor);
        if (result == null)
            return null;

        return NifGeometryExtractor.Extract(result.Value.Data, result.Value.Info, textureResolver,
            externalBoneTransforms: externalBoneTransforms, filterShapeName: filterShapeName,
            externalPoseDeltas: externalPoseDeltas,
            useDualQuaternionSkinning: useDualQuaternionSkinning);
    }

    /// <summary>
    ///     Loads and converts a NIF from BSA, returning the raw byte data, parsed NifInfo,
    ///     and any packed geometry data extracted before conversion (for Xbox 360 NIFs).
    /// </summary>
    internal static (byte[] Data, NifInfo Info)? LoadNifRawFromBsa(
        string bsaPath,
        BsaArchive archive,
        BsaExtractor extractor,
        bool skipConversion = false)
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

        // Convert Xbox 360 big-endian NIFs (skip for animation KF files whose
        // data is parsed with endian-aware readers — BulkSwap32 fallback corrupts
        // NiBSplineData int16 arrays and NiControllerSequence mixed-size fields)
        if (nif.IsBigEndian && !skipConversion)
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

    internal static EgmParser? LoadEgmFromBsa(string bsaPath, BsaArchive archive, BsaExtractor extractor)
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

    internal static EgtParser? LoadEgtFromBsa(string bsaPath, BsaArchive archive, BsaExtractor extractor)
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
    ///     Loads an EGM from BSA (with cache), applies FaceGen morphs to the model.
    ///     Consolidates the repeated cache-check → load → apply pattern.
    /// </summary>
    internal static void LoadAndApplyEgm(
        string egmPath,
        NifRenderableModel model,
        float[]? symCoeffs, float[]? asymCoeffs,
        BsaArchive archive, BsaExtractor extractor,
        Dictionary<string, EgmParser?> egmCache)
    {
        if (!egmCache.TryGetValue(egmPath, out var egm))
        {
            egm = LoadEgmFromBsa(egmPath, archive, extractor);
            egmCache[egmPath] = egm;
        }

        if (egm != null)
        {
            Log.Debug("EGM '{0}': {1} sym + {2} asym morphs, {3} vertices",
                egmPath, egm.SymmetricMorphs.Length, egm.AsymmetricMorphs.Length, egm.VertexCount);
            FaceGenMeshMorpher.Apply(model, egm, symCoeffs, asymCoeffs);
        }
    }

    /// <summary>
    ///     Loads a body EGT from BSA (with cache), morphs the base texture using FaceGen texture
    ///     coefficients, and injects the result into the texture resolver under a unique per-NPC key.
    ///     Returns the injected key, or null if any step fails.
    /// </summary>
    internal static string? ApplyBodyEgtMorph(
        string egtPath,
        string baseTexturePath,
        float[] textureCoeffs,
        uint npcFormId,
        string partLabel,
        BsaArchive meshesArchive,
        BsaExtractor meshExtractor,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache)
    {
        if (!egtCache.TryGetValue(egtPath, out var egt))
        {
            egt = LoadEgtFromBsa(egtPath, meshesArchive, meshExtractor);
            egtCache[egtPath] = egt;
        }

        if (egt == null)
            return null;

        var baseTexture = textureResolver.GetTexture(baseTexturePath);
        if (baseTexture == null)
        {
            Log.Warn("Base body texture not found for EGT morph: {0}", baseTexturePath);
            return null;
        }

        var morphed = FaceGenTextureMorpher.Apply(baseTexture, egt, textureCoeffs);
        if (morphed == null)
        {
            Log.Warn("Body EGT morph returned null for NPC 0x{0:X8} (part: {1})", npcFormId, partLabel);
            return null;
        }

        var morphedKey = $"body_egt\\{npcFormId:X8}_{partLabel}.dds";
        textureResolver.InjectTexture(morphedKey, morphed);
        Log.Debug("Body EGT morph applied: NPC 0x{0:X8} {1} → {2}", npcFormId, partLabel, egtPath);
        return morphedKey;
    }

    internal static NifRenderableModel DeepCloneModel(NifRenderableModel source)
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
                ShapeName = sub.ShapeName,
                Positions = (float[])sub.Positions.Clone(),
                Triangles = (ushort[])sub.Triangles.Clone(),
                Normals = sub.Normals != null ? (float[])sub.Normals.Clone() : null,
                UVs = sub.UVs != null ? (float[])sub.UVs.Clone() : null,
                VertexColors = sub.VertexColors != null ? (byte[])sub.VertexColors.Clone() : null,
                Tangents = sub.Tangents != null ? (float[])sub.Tangents.Clone() : null,
                Bitangents = sub.Bitangents != null ? (float[])sub.Bitangents.Clone() : null,
                ShaderMetadata = sub.ShaderMetadata,
                DiffuseTexturePath = sub.DiffuseTexturePath,
                NormalMapTexturePath = sub.NormalMapTexturePath,
                IsEmissive = sub.IsEmissive,
                UseVertexColors = sub.UseVertexColors,
                IsDoubleSided = sub.IsDoubleSided,
                HasAlphaBlend = sub.HasAlphaBlend,
                HasAlphaTest = sub.HasAlphaTest,
                AlphaTestThreshold = sub.AlphaTestThreshold,
                AlphaTestFunction = sub.AlphaTestFunction,
                SrcBlendMode = sub.SrcBlendMode,
                DstBlendMode = sub.DstBlendMode,
                MaterialAlpha = sub.MaterialAlpha,
                IsEyeEnvmap = sub.IsEyeEnvmap,
                EnvMapScale = sub.EnvMapScale,
                RenderOrder = sub.RenderOrder,
                TintColor = sub.TintColor
            });
        }

        return clone;
    }

    /// <summary>
    ///     Unpacks HCLR hair color (0x00BBGGRR) into a float RGB tint tuple.
    ///     Returns null if no hair color is set.
    /// </summary>
    internal static (float R, float G, float B)? UnpackHairColor(uint? hclr)
    {
        if (hclr == null)
            return null;

        var v = hclr.Value;
        var r = (v & 0xFF) / 255f;
        var g = ((v >> 8) & 0xFF) / 255f;
        var b = ((v >> 16) & 0xFF) / 255f;
        return (r, g, b);
    }

    /// <summary>
    ///     Determines whether an equipment submesh is a body skin submesh that needs tinting.
    /// </summary>
    internal static bool IsEquipmentSkinSubmesh(string? texturePath)
    {
        if (string.IsNullOrEmpty(texturePath))
            return false;

        if (texturePath.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
            texturePath.Contains("eyes", StringComparison.OrdinalIgnoreCase) ||
            texturePath.Contains("headhuman", StringComparison.OrdinalIgnoreCase) ||
            texturePath.Contains("underwear", StringComparison.OrdinalIgnoreCase))
            return false;

        return texturePath.Contains("characters\\_male", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("characters\\male", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("characters\\_female", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("characters\\female", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Determines whether the RACE body texture override should replace a submesh's texture.
    /// </summary>
    internal static bool ShouldApplyBodyTextureOverride(string? existingPath, string overridePath)
    {
        if (string.IsNullOrEmpty(existingPath))
            return true;

        if (existingPath.Contains("underwear", StringComparison.OrdinalIgnoreCase))
            return false;

        if (existingPath.Contains("characters", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    internal static uint? ParseFormId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];

        return uint.TryParse(s, NumberStyles.HexNumber, null, out var id) ? id : null;
    }

    /// <summary>
    ///     Resolves texture BSA paths. If an explicit path is given, uses that.
    ///     Otherwise, auto-discovers all *Texture* BSA files in the meshes BSA directory.
    /// </summary>
    internal static string[] ResolveTexturesBsaPaths(string meshesBsaPath, string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            if (!File.Exists(explicitPath))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Textures BSA not found: {0}", explicitPath);
                return [];
            }

            return [explicitPath];
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(meshesBsaPath));
        if (dir == null || !Directory.Exists(dir))
            return [];

        var found = Directory.GetFiles(dir, "*Texture*.bsa")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (found.Length == 0)
            AnsiConsole.MarkupLine("[yellow]Warning:[/] No texture BSA files found in {0}", dir);
        else
            AnsiConsole.MarkupLine("Auto-detected [green]{0}[/] texture BSA(s) in [cyan]{1}[/]", found.Length, dir);

        return found;
    }

    /// <summary>
    ///     Loads NPC records from a DMP file and resolves their appearance using ESM asset data.
    /// </summary>
    internal static List<NpcAppearance> ResolveFromDmp(
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

        var minidumpInfo = MinidumpParser.Parse(dmpPath);
        if (!minidumpInfo.IsValid)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid minidump format");
            return [];
        }

        var scanResult = EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileInfo.Length);
        EsmEditorIdExtractor.ExtractRuntimeEditorIds(accessor, fileInfo.Length, minidumpInfo, scanResult);

        var npcEntries = scanResult.RuntimeEditorIds
            .Where(e => e.FormType == 0x2A)
            .ToList();

        if (npcEntries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No NPC_ entries found in DMP runtime hash table[/]");
            return [];
        }

        AnsiConsole.MarkupLine("Found [green]{0}[/] NPC_ entries in DMP", npcEntries.Count);

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

            CompareWithEsmCoefficients(resolver, appearance, npcRecord, pluginName);
            appearances.Add(appearance);
        }

        AnsiConsole.MarkupLine("Resolved [green]{0}[/] NPC appearances from DMP", appearances.Count);
        return appearances;
    }

    /// <summary>
    ///     Compares DMP-sourced coefficients against ESM-sourced coefficients for validation.
    /// </summary>
    private static void CompareWithEsmCoefficients(
        NpcAppearanceResolver resolver, NpcAppearance dmpAppearance,
        NpcRecord npcRecord, string pluginName)
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
}
