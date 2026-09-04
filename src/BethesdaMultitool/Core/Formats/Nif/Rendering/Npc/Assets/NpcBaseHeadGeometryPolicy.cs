using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     Prepares base-head normals and tangent space before a renderer-specific material is applied.
///     Keeping this operation shared prevents the CPU/native model and glTF adapters from observing
///     different normal bases after the same FaceGen morph.
/// </summary>
internal static class NpcBaseHeadGeometryPolicy
{
    internal static void PrepareForMaterial(
        IEnumerable<RenderableSubmesh> submeshes,
        bool positionsWereMorphed,
        bool deferTangentRebuildToMaterialResolver)
    {
        ArgumentNullException.ThrowIfNull(submeshes);

        foreach (var submesh in submeshes)
        {
            var needsTangentSpace = submesh.NormalMapTexturePath != null ||
                                    submesh.ShaderMetadata?.NormalMapPath != null ||
                                    submesh.Tangents != null ||
                                    submesh.Bitangents != null;
            if (positionsWereMorphed)
            {
                FaceGenMeshMorpher.RecalculateNormals(submesh);
            }
            else if (submesh.Normals != null)
            {
                FaceGenMeshMorpher.WeldSeamNormals(submesh.Positions, submesh.Normals);
            }

            // Either operation can invalidate the authored frame. Oblivion's material resolver
            // rebuilds after selecting its implicit family normal. Other games rebuild here so
            // this shared parity step cannot disable their authored normal/parallax route.
            submesh.Tangents = null;
            submesh.Bitangents = null;
            if (!deferTangentRebuildToMaterialResolver && needsTangentSpace)
            {
                _ = NpcTangentSpaceBuilder.TryRebuild(submesh);
            }
        }
    }
}
