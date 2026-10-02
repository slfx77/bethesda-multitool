using BethesdaMultitool.Core.Modeling.Nif;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Everything one slice-14 fixture read produced before the animation stage
///     (<see cref="NifModelAnimationPropertyTestSupport.ReadFixture" />): the read state, the node graph placed over its
///     meshes, the cut-1a material, texture and geometry results, the property targets built from them, and the platform
///     the read resolved.
/// </summary>
/// <param name="State">The read state.</param>
/// <param name="Graph">The node graph with its mesh placements.</param>
/// <param name="Materials">The material reader's result.</param>
/// <param name="Textures">The texture source's result.</param>
/// <param name="Geometry">The geometry reader's result.</param>
/// <param name="Targets">The property targets slice 14 binds to.</param>
/// <param name="Platform">The platform selection of a read with no option.</param>
internal sealed record NifModelPropertyFixture(
    NifModelReadState State,
    NifModelNodeGraph Graph,
    NifModelMaterialResult Materials,
    NifModelTextureResult Textures,
    NifModelGeometryResult Geometry,
    NifModelPropertyTargets Targets,
    NifPackedPlatformSelection Platform);
