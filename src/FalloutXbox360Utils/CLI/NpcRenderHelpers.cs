using System.IO.MemoryMappedFiles;
using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Geometry transform utilities, head bone correction, and DMP NPC resolution.
///     BSA loading lives in <see cref="NpcMeshHelpers" />, texture/classification utilities in
///     <see cref="NpcTextureHelpers" />.
/// </summary>
internal static class NpcRenderHelpers
{
    private static readonly Logger Log = Logger.Instance;

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

    internal static bool TryGetRootRotationCompensation(
        byte[] nifData,
        NifInfo nifInfo,
        out Matrix4x4 compensation)
    {
        compensation = Matrix4x4.Identity;

        var nifBones = NifGeometryExtractor.ExtractNamedBoneTransforms(nifData, nifInfo);
        if (!nifBones.TryGetValue(NifGeometryExtractor.RootTransformKey, out var nifRoot))
            return false;

        var rootRotation = RemoveTranslation(nifRoot);
        if (IsNearlyIdentity(rootRotation) ||
            !Matrix4x4.Invert(rootRotation, out compensation))
        {
            compensation = Matrix4x4.Identity;
            return false;
        }

        return true;
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
        string? attachmentLabel = null,
        HeadAttachmentRootPolicy rootPolicy = HeadAttachmentRootPolicy.PreserveAuthoredBasis)
    {
        var nifBones = NifGeometryExtractor.ExtractNamedBoneTransforms(nifData, nifInfo);
        var correctionResult = GetHeadAttachmentCorrection(
            nifBones,
            targetHeadTransform,
            bonelessAttachmentTransform,
            rootPolicy);

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
            "  Mode={0} rootPolicy={1} correction T=({2:F2},{3:F2},{4:F2}) rootDet={5:F2}",
            correctionResult.Mode,
            rootPolicy,
            correctionResult.Correction.M41,
            correctionResult.Correction.M42,
            correctionResult.Correction.M43,
            correctionResult.RootDeterminant);
        TransformModel(model, correctionResult.Correction);

        if (model.Submeshes.Count > 0)
        {
            Log.Debug("  Post-correction bounds: ({0:F1},{1:F1},{2:F1})->({3:F1},{4:F1},{5:F1})",
                model.MinX, model.MinY, model.MinZ, model.MaxX, model.MaxY, model.MaxZ);
        }
    }

    internal static HeadAttachmentCorrectionResult GetHeadAttachmentCorrection(
        IReadOnlyDictionary<string, Matrix4x4> nifBones,
        Matrix4x4 targetHeadTransform,
        Matrix4x4? bonelessAttachmentTransform = null,
        HeadAttachmentRootPolicy rootPolicy = HeadAttachmentRootPolicy.PreserveAuthoredBasis)
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

        // Boneless attachments preserve their authored local root basis. Hair/head parts always
        // use the explicit boneless attachment transform. For rigid head equipment, only root-only
        // NIFs should inherit the full head basis; named local nodes (e.g. berets/shades) still
        // preserve their authored basis and only compensate rotated NIF roots.
        nifBones.TryGetValue("__root__", out var nifRoot);
        var det3x3 = GetDeterminant3x3(RemoveTranslation(nifRoot));
        var effectiveBonelessTransform = bonelessAttachmentTransform ?? targetHeadTransform;
        if (rootPolicy == HeadAttachmentRootPolicy.CompensateRotatedRoot &&
            !HasNamedLocalAttachmentNodes(nifBones))
        {
            effectiveBonelessTransform = targetHeadTransform;
        }

        if (rootPolicy == HeadAttachmentRootPolicy.CompensateRotatedRoot)
        {
            // Compensate for NIF root rotation (e.g., hats with rotated root nodes).
            // Mirrors the eye attachment root compensation pattern in TryGetRootRotationCompensation.
            var rootRotation = RemoveTranslation(nifRoot);
            if (!IsNearlyIdentity(rootRotation) &&
                Matrix4x4.Invert(rootRotation, out var invRootRotation))
            {
                effectiveBonelessTransform = invRootRotation * effectiveBonelessTransform;
            }
        }

        return new HeadAttachmentCorrectionResult(
            effectiveBonelessTransform,
            HeadAttachmentCorrectionMode.BonelessUseAttachmentTransform,
            det3x3);
    }

    private static bool HasNamedLocalAttachmentNodes(IReadOnlyDictionary<string, Matrix4x4> nifBones)
    {
        foreach (var boneName in nifBones.Keys)
        {
            if (string.Equals(boneName, "Bip01 Head", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(boneName, "__root__", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        return false;
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
    ///     Loads NPC records from a DMP file and resolves their appearance using ESM asset data.
    /// </summary>
    internal static List<NpcAppearance> ResolveFromDmp(
        string dmpPath,
        NpcAppearanceResolver resolver,
        string pluginName,
        string[]? filters)
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

        var structReader = scanResult.RuntimeRefrFormEntries.Count > 0 || npcEntries.Count > 0
            ? RuntimeStructReader.CreateWithAutoDetect(
                accessor,
                fileInfo.Length,
                minidumpInfo,
                scanResult.RuntimeRefrFormEntries,
                npcEntries)
            : new RuntimeStructReader(accessor, fileInfo.Length, minidumpInfo);
        var npcEntriesByFormId = npcEntries.ToDictionary(entry => entry.FormId);
        var npcEntriesByEditorId = npcEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.EditorId))
            .GroupBy(entry => entry.EditorId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var actorInfos = LoadRuntimeActorInfos(structReader, scanResult.RuntimeRefrFormEntries);
        var actorInfosByFormId = actorInfos.ToDictionary(info => info.Entry.FormId);
        var actorInfosByBaseNpcFormId = actorInfos
            .GroupBy(info => info.Refr.BaseFormId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var targets = BuildDmpTargets(
            filters,
            npcEntries,
            npcEntriesByFormId,
            npcEntriesByEditorId,
            actorInfosByFormId,
            actorInfosByBaseNpcFormId);
        var appearances = new List<NpcAppearance>();
        var seenTargetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            var targetKey = target.RuntimeWeaponSelection is
                { HasRuntimeTarget: true, ActorRefFormId: { } actorRefFormId }
                ? $"ACHR:{actorRefFormId:X8}"
                : $"NPC:{target.NpcEntry.FormId:X8}";
            if (!seenTargetKeys.Add(targetKey))
            {
                continue;
            }

            var npcRecord = structReader.ReadRuntimeNpc(target.NpcEntry);
            if (npcRecord == null)
            {
                Log.Debug("Failed to read NPC struct for 0x{0:X8} ({1})", target.NpcEntry.FormId,
                    target.NpcEntry.EditorId);
                continue;
            }

            var appearance = resolver.ResolveFromDmpRecord(
                npcRecord,
                pluginName,
                target.RuntimeWeaponSelection);
            if (appearance == null)
            {
                Log.Debug("Failed to resolve appearance for 0x{0:X8} ({1})", target.NpcEntry.FormId,
                    target.NpcEntry.EditorId);
                continue;
            }

            CompareWithEsmCoefficients(resolver, appearance, npcRecord, pluginName);
            appearances.Add(appearance);
        }

        AnsiConsole.MarkupLine("Resolved [green]{0}[/] NPC appearances from DMP", appearances.Count);
        return appearances;
    }

    private static List<DmpActorRuntimeInfo> LoadRuntimeActorInfos(
        RuntimeStructReader structReader,
        IReadOnlyList<RuntimeEditorIdEntry> runtimeRefrEntries)
    {
        var actorInfos = new List<DmpActorRuntimeInfo>();
        foreach (var entry in runtimeRefrEntries)
        {
            if (entry.FormType != 0x3B)
            {
                continue;
            }

            var refr = structReader.ReadRuntimeRefr(entry);
            if (refr == null)
            {
                continue;
            }

            var weaponState = structReader.ReadRuntimeActorWeaponState(entry);
            actorInfos.Add(new DmpActorRuntimeInfo(entry, refr, weaponState));
        }

        return actorInfos;
    }

    private static List<DmpNpcTarget> BuildDmpTargets(
        string[]? filters,
        IReadOnlyList<RuntimeEditorIdEntry> npcEntries,
        Dictionary<uint, RuntimeEditorIdEntry> npcEntriesByFormId,
        Dictionary<string, RuntimeEditorIdEntry> npcEntriesByEditorId,
        IReadOnlyDictionary<uint, DmpActorRuntimeInfo> actorInfosByFormId,
        IReadOnlyDictionary<uint, List<DmpActorRuntimeInfo>> actorInfosByBaseNpcFormId)
    {
        var targets = new List<DmpNpcTarget>();

        if (filters is not { Length: > 0 })
        {
            foreach (var npcEntry in npcEntries)
            {
                targets.Add(new DmpNpcTarget(
                    npcEntry,
                    TryBuildRuntimeSelection(npcEntry.FormId, actorInfosByBaseNpcFormId)));
            }

            return targets;
        }

        foreach (var filter in filters)
        {
            var formId = NpcTextureHelpers.ParseFormId(filter);
            if (formId.HasValue)
            {
                if (actorInfosByFormId.TryGetValue(formId.Value, out var actorInfo) &&
                    npcEntriesByFormId.TryGetValue(actorInfo.Refr.BaseFormId, out var actorBaseNpc))
                {
                    targets.Add(new DmpNpcTarget(
                        actorBaseNpc,
                        new NpcWeaponResolver.RuntimeWeaponSelection(
                            true,
                            actorInfo.Entry.FormId,
                            actorInfo.WeaponState?.WeaponFormId)));
                    continue;
                }

                if (npcEntriesByFormId.TryGetValue(formId.Value, out var npcEntry))
                {
                    targets.Add(new DmpNpcTarget(
                        npcEntry,
                        TryBuildRuntimeSelection(formId.Value, actorInfosByBaseNpcFormId)));
                }

                continue;
            }

            if (npcEntriesByEditorId.TryGetValue(filter.Trim(), out var editorIdNpc))
            {
                targets.Add(new DmpNpcTarget(
                    editorIdNpc,
                    TryBuildRuntimeSelection(editorIdNpc.FormId, actorInfosByBaseNpcFormId)));
            }
        }

        return targets;
    }

    private static NpcWeaponResolver.RuntimeWeaponSelection? TryBuildRuntimeSelection(
        uint npcFormId,
        IReadOnlyDictionary<uint, List<DmpActorRuntimeInfo>> actorInfosByBaseNpcFormId)
    {
        if (!actorInfosByBaseNpcFormId.TryGetValue(npcFormId, out var actorInfos) ||
            actorInfos.Count != 1)
        {
            return null;
        }

        var actorInfo = actorInfos[0];
        return new NpcWeaponResolver.RuntimeWeaponSelection(
            true,
            actorInfo.Entry.FormId,
            actorInfo.WeaponState?.WeaponFormId);
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
            Log.Debug("NPC 0x{0:X8} ({1}): not found in ESM -- no coefficient comparison",
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

    internal enum HeadAttachmentCorrectionMode
    {
        Boned,
        BonelessUseAttachmentTransform
    }

    internal enum HeadAttachmentRootPolicy
    {
        PreserveAuthoredBasis,
        CompensateRotatedRoot
    }

    internal readonly record struct HeadAttachmentCorrectionResult(
        Matrix4x4 Correction,
        HeadAttachmentCorrectionMode Mode,
        float RootDeterminant);

    private readonly record struct DmpNpcTarget(
        RuntimeEditorIdEntry NpcEntry,
        NpcWeaponResolver.RuntimeWeaponSelection? RuntimeWeaponSelection);

    private readonly record struct DmpActorRuntimeInfo(
        RuntimeEditorIdEntry Entry,
        ExtractedRefrRecord Refr,
        RuntimeActorWeaponReader.RuntimeActorWeaponState? WeaponState);
}
