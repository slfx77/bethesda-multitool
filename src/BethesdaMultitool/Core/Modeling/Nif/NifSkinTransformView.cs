using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One decoded NiTransform (nif.xml: Rotation Matrix33, Translation Vector3, Scale float): the overall Skin
///     Transform of an NiSkinData block, or one bone's Skin Transform in its BoneData. Values are exactly as stored; a
///     non-finite value is kept and reported by <see cref="IsFinite" />, never replaced.
/// </summary>
internal sealed class NifSkinTransformView
{
    private readonly float[] _rotation;

    private NifSkinTransformView(Vector3 translation, float[] rotation, float scale)
    {
        Translation = translation;
        _rotation = rotation;
        Scale = scale;
    }

    /// <summary>The stored Translation.</summary>
    public Vector3 Translation { get; }

    /// <summary>The nine stored Matrix33 floats in file order (row-major R for column vectors).</summary>
    public IReadOnlyList<float> RowMajorRotation => _rotation;

    /// <summary>The stored uniform Scale.</summary>
    public float Scale { get; }

    /// <summary>True when every stored value is finite.</summary>
    public bool IsFinite =>
        float.IsFinite(Translation.X) && float.IsFinite(Translation.Y) && float.IsFinite(Translation.Z) &&
        float.IsFinite(Scale) && Array.TrueForAll(_rotation, float.IsFinite);

    /// <summary>True when the stored transform is exactly the identity (zero translation, identity rotation, scale 1).</summary>
    public bool IsIdentity
    {
        get
        {
            if (Translation != Vector3.Zero || Scale != 1f)
            {
                return false;
            }

            for (var i = 0; i < 9; i++)
            {
                if (_rotation[i] != (i % 4 == 0 ? 1f : 0f))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Reads a NiTransform struct field.</summary>
    /// <param name="block">The block the struct belongs to (for error messages).</param>
    /// <param name="owner">The struct holding the field.</param>
    /// <param name="field">The NiTransform field name.</param>
    /// <param name="path">The field path used in error messages (for example <c>Bone List[3].Skin Transform</c>).</param>
    /// <exception cref="InvalidDataException">The field or one of its components did not decode with the expected shape.</exception>
    public static NifSkinTransformView Read(NifDecodedBlock block, NifStructValue owner, string field, string path)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.TryGet(field, out var value) || value is not NifStructValue transform)
        {
            throw Shape(block, path);
        }

        if (!transform.TryGet("Rotation", out var rotationValue) || rotationValue is not NifStructValue rotation ||
            !transform.TryGet("Translation", out var translationValue) ||
            translationValue is not NifStructValue translation ||
            !transform.TryGet("Scale", out var scaleValue) || scaleValue is not NifFloatValue scale)
        {
            throw Shape(block, path);
        }

        var stored = new float[9];
        for (var i = 0; i < stored.Length; i++)
        {
            stored[i] = Component(block, rotation, NifModelNodeReader.Matrix33Fields[i], path + ".Rotation");
        }

        var vector = new Vector3(
            Component(block, translation, "x", path + ".Translation"),
            Component(block, translation, "y", path + ".Translation"),
            Component(block, translation, "z", path + ".Translation"));
        return new NifSkinTransformView(vector, stored, scale.Value);
    }

    /// <summary>
    ///     The document's row-vector matrix for this transform, S R^T T (<see cref="NifModelTransform.ComposeMatrix" />),
    ///     with no orthonormalization: an NiSkinData bone's Skin Transform is its inverse bind as authored.
    /// </summary>
    public Matrix4x4 ToMatrix()
    {
        return NifModelTransform.ComposeMatrix(Translation, _rotation, Scale);
    }

    /// <summary>
    ///     The stored values plus the TRS rule's measurements (orthonormality error and determinant, in double), recorded
    ///     only: the matrix is never replaced by a decomposition.
    /// </summary>
    public JsonObject ToJson()
    {
        var node = new JsonObject
        {
            ["translation"] = new JsonArray(NifModelNativeValues.Float(Translation.X),
                NifModelNativeValues.Float(Translation.Y), NifModelNativeValues.Float(Translation.Z)),
            ["rotationRowMajor"] = new JsonArray(_rotation.Select(v => (JsonNode?)NifModelNativeValues.Float(v))
                .ToArray()),
            ["scale"] = NifModelNativeValues.Float(Scale),
            ["identity"] = IsIdentity
        };
        if (IsFinite)
        {
            Span<double> stored = stackalloc double[9];
            for (var i = 0; i < 9; i++)
            {
                stored[i] = _rotation[i];
            }

            node["orthonormalityError"] = NifModelNativeValues.Double(NifModelTransform.OrthonormalityError(stored));
            node["determinant"] = NifModelNativeValues.Double(NifModelTransform.Determinant(stored));
        }
        else
        {
            node["finite"] = false;
        }

        return node;
    }

    /// <summary>A short invariant description for diagnostics.</summary>
    public string Describe()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"translation ({Translation.X:R}, {Translation.Y:R}, {Translation.Z:R}), scale {Scale:R}, rotation " +
            $"[{string.Join(", ", _rotation.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))}]");
    }

    private static float Component(NifDecodedBlock block, NifStructValue owner, string component, string path)
    {
        return owner.TryGet(component, out var value) && value is NifFloatValue single
            ? single.Value
            : throw new InvalidDataException(
                $"NIF block {block.Index} ({block.Type}) has no float '{path}.{component}'.");
    }

    private static InvalidDataException Shape(NifDecodedBlock block, string path)
    {
        return new InvalidDataException(
            $"NIF block {block.Index} ({block.Type}) did not decode the NiTransform '{path}' (Rotation, Translation, " +
            "Scale).");
    }
}
