using System.Globalization;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>The per-file tally of a console-against-PC geometry comparison (<see cref="NifPackedGeometryComparison" />).</summary>
internal sealed class NifPackedGeometryTally
{
    /// <summary>Packed positions.</summary>
    public NifPackedChannelCensus Positions { get; } = new();

    /// <summary>Packed normals.</summary>
    public NifPackedChannelCensus Normals { get; } = new();

    /// <summary>Packed UV set 0.</summary>
    public NifPackedChannelCensus Uvs { get; } = new();

    /// <summary>Packed tangent xyz.</summary>
    public NifPackedChannelCensus Tangents { get; } = new();

    /// <summary>Packed colors compared (within one byte under the platform's order).</summary>
    public int Colors { get; set; }

    /// <summary>Packed influence slots compared against the PC partition.</summary>
    public int Influences { get; set; }

    /// <summary>X360 packed vertices whose engine lanes were compared bit for bit against the engine rule over the PC twin.</summary>
    public int EngineLanes { get; set; }

    /// <summary>
    ///     X360 packed vertices on which the joint-0 control ran and failed as required: the derived weight is nonzero on a
    ///     slot-3 bone other than joint 0, and the same weight moved to joint 0 is reported by the comparison.
    /// </summary>
    public int EngineLaneJointControls { get; set; }

    /// <summary>
    ///     X360 packed vertices where the derived weight is nonzero on a slot-3 bone other than joint 0 but its placement
    ///     cannot be observed: both placements merge it into a lane and round it away there.
    /// </summary>
    public int EngineLaneJointUnobservable { get; set; }

    /// <summary>Components of inline console shapes compared bit for bit (positions, normals, UVs, tangents, colors, weights).</summary>
    public int InlineComponents { get; set; }

    /// <summary>
    ///     Over the skinned packed shapes: PC NiTriShapeData triangles that no PC partition carries (a recorded fact;
    ///     giantant.nif shape 5 has two). The console file stores no such triangle, so they are outside the comparison.
    /// </summary>
    public int DataBlockTrianglesNotInPartitions { get; set; }

    /// <summary>Over the skinned packed shapes: PC partition triangles the PC NiTriShapeData does not store (a recorded fact).</summary>
    public int PartitionTrianglesNotInDataBlock { get; set; }

    /// <inheritdoc />
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"positions {Positions}, normals {Normals}, uvs {Uvs}, tangents {Tangents}, colors {Colors}, influence " +
            $"slots {Influences} (X360 engine-lane vertices {EngineLanes}, joint-0 controls failed as required " +
            $"{EngineLaneJointControls}, unobservable {EngineLaneJointUnobservable}), inline components " +
            $"{InlineComponents}, PC data-block triangles outside the PC " +
            $"partitions {DataBlockTrianglesNotInPartitions}, PC partition triangles outside the data block " +
            $"{PartitionTrianglesNotInDataBlock}");
    }
}
