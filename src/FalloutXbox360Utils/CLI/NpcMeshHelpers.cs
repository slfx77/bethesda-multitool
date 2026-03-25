using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     BSA mesh/EGM/EGT/TRI loading and model cloning utilities.
/// </summary>
internal static partial class NpcRenderHelpers
{
    /// <summary>
    ///     Loads a NIF from BSA, converts BE->LE if needed, and extracts renderable geometry.
    /// </summary>
    internal static NifRenderableModel? LoadNifFromBsa(
        string bsaPath,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, System.Numerics.Matrix4x4>? externalBoneTransforms = null,
        string? filterShapeName = null,
        Dictionary<string, System.Numerics.Matrix4x4>? externalPoseDeltas = null,
        bool useDualQuaternionSkinning = false)
    {
        var result = LoadNifRawFromBsa(bsaPath, meshArchives);
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
        NpcMeshArchiveSet meshArchives,
        bool skipConversion = false)
    {
        if (!meshArchives.TryExtractFile(bsaPath, out var nifData, out _))
        {
            Log.Warn("NIF not found in BSA: {0}", bsaPath);
            return null;
        }

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
                Log.Warn("NIF BE->LE conversion failed: {0}", bsaPath);
                return null;
            }

            nifData = converted.OutputData;
            nif = NifParser.Parse(nifData);
            if (nif == null)
            {
                Log.Warn("NIF re-parse failed after BE->LE conversion: {0}", bsaPath);
                return null;
            }
        }

        return (nifData, nif);
    }

    internal static EgmParser? LoadEgmFromBsa(string bsaPath, NpcMeshArchiveSet meshArchives)
    {
        if (!meshArchives.TryExtractFile(bsaPath, out var data, out _))
        {
            Log.Warn("EGM not found in BSA: {0}", bsaPath);
            return null;
        }

        if (data.Length == 0)
        {
            Log.Warn("EGM extracted but empty (0 bytes): {0}", bsaPath);
            return null;
        }

        return EgmParser.Parse(data);
    }

    internal static EgtParser? LoadEgtFromBsa(string bsaPath, NpcMeshArchiveSet meshArchives)
    {
        if (!meshArchives.TryExtractFile(bsaPath, out var data, out _))
        {
            Log.Warn("EGT not found in BSA: {0}", bsaPath);
            return null;
        }

        if (data.Length == 0)
        {
            Log.Warn("EGT extracted but empty (0 bytes): {0}", bsaPath);
            return null;
        }

        return EgtParser.Parse(data);
    }

    internal static TriParser? LoadTriFromBsa(string bsaPath, NpcMeshArchiveSet meshArchives)
    {
        if (!meshArchives.TryExtractFile(bsaPath, out var data, out _))
        {
            Log.Warn("TRI not found in BSA: {0}", bsaPath);
            return null;
        }

        if (data.Length == 0)
        {
            Log.Warn("TRI extracted but empty (0 bytes): {0}", bsaPath);
            return null;
        }

        return TriParser.Parse(data);
    }

    /// <summary>
    ///     Loads an EGM from BSA (with cache), applies FaceGen morphs to the model.
    ///     Consolidates the repeated cache-check -> load -> apply pattern.
    /// </summary>
    internal static void LoadAndApplyEgm(
        string egmPath,
        NifRenderableModel model,
        float[]? symCoeffs, float[]? asymCoeffs,
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, EgmParser?> egmCache)
    {
        if (!egmCache.TryGetValue(egmPath, out var egm))
        {
            egm = LoadEgmFromBsa(egmPath, meshArchives);
            egmCache[egmPath] = egm;
        }

        if (egm != null)
        {
            Log.Debug("EGM '{0}': {1} sym + {2} asym morphs, {3} vertices",
                egmPath, egm.SymmetricMorphs.Length, egm.AsymmetricMorphs.Length, egm.VertexCount);
            FaceGenMeshMorpher.Apply(model, egm, symCoeffs, asymCoeffs);
        }
    }

    internal static EgmParser? LoadAndCacheEgm(
        string egmPath,
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, EgmParser?> egmCache)
    {
        if (!egmCache.TryGetValue(egmPath, out var egm))
        {
            egm = LoadEgmFromBsa(egmPath, meshArchives);
            egmCache[egmPath] = egm;
        }

        return egm;
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
        string? renderVariantLabel,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache)
    {
        if (!egtCache.TryGetValue(egtPath, out var egt))
        {
            egt = LoadEgtFromBsa(egtPath, meshArchives);
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

        var morphedKey = BuildNpcBodyEgtTextureKey(npcFormId, partLabel, renderVariantLabel);
        textureResolver.InjectTexture(morphedKey, morphed);
        Log.Debug("Body EGT morph applied: NPC 0x{0:X8} {1} -> {2}", npcFormId, partLabel, egtPath);
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
}
