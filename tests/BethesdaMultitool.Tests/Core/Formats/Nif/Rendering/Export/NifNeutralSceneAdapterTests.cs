using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Pins the neutral NIF snapshot against the native writer's own rules.
/// </summary>
/// <remarks>
///     An earlier attempt at this adapter is kept under <c>docs/failed-attempts/</c>. It adapted the writer's
///     input rather than its prepared output, so it never crossed the Z-up to Y-up basis, and it emitted zero
///     normals that the shared validator rejects — yet all fifteen of its cases passed, because none of them
///     asserted either property. The first three cases here exist specifically so that class of defect fails.
/// </remarks>
public sealed class NifNeutralSceneAdapterTests
{
    private static NifTextureResolver Resolver() => new(_ => null);

    /// <summary>A unit triangle on the NIF Z-up axes, with explicit normals so the fallback is not what is measured.</summary>
    private static GlbScene Triangle(
        float[]? normals = null,
        float[]? tangents = null,
        float[]? bitangents = null,
        GlbSkinBinding? skin = null,
        Action<RenderableSubmesh>? mutate = null,
        float[]? positions = null,
        float[]? uvs = null)
    {
        var submesh = new RenderableSubmesh
        {
            ShapeName = "Shape",
            // Z-up source: the third component is height.
            Positions = positions ?? [0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f],
            Triangles = [0, 1, 2],
            Normals = normals ?? [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = uvs ?? [0f, 0f, 1f, 0f, 0f, 1f],
            Tangents = tangents,
            Bitangents = bitangents
        };
        mutate?.Invoke(submesh);
        return Scene(submesh, skin);
    }

    /// <summary>Gives one surface the owning "Shape" node under the synthetic root, as every fixture here does.</summary>
    /// <remarks>The scene holds node 0 (the root) and node 1 (the owner), so the next added node is index 2.</remarks>
    private static GlbScene Scene(RenderableSubmesh submesh, GlbSkinBinding? skin = null)
    {
        var scene = new GlbScene();
        var node = scene.AddNode("Shape", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, "Shape");
        scene.MeshParts.Add(new GlbMeshPart { Name = "Shape", NodeIndex = node, Submesh = submesh, Skin = skin });
        return scene;
    }

    /// <summary>
    ///     The same unit triangle typed as an authored sky, with exactly one RGBA weight per vertex, which is what
    ///     makes the native writer project it and suffix its mesh name.
    /// </summary>
    private static RenderableSubmesh SkyTriangle(NifShaderTextureMetadata? metadata = null) => new()
    {
        ShapeName = "Shape",
        Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f],
        Triangles = [0, 1, 2],
        Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
        UVs = [0f, 0f, 1f, 0f, 0f, 1f],
        VertexColors = [255, 0, 0, 255, 0, 0, 255, 255, 0, 0, 255, 128],
        UseVertexColors = true,
        SkyType = SkyObjectType.Sky,
        ShaderMetadata = metadata
    };

    /// <summary>Resolves every requested path to one two-texel image, so any authored map slot is populated.</summary>
    private static NifTextureResolver TexturedResolver() =>
        new(_ => DecodedTexture.FromBaseLevel([29, 29, 29, 255, 220, 220, 220, 255], 2, 1, false));

    /// <summary>A one-joint palette on the scene root with one full weight per vertex of the unit triangle.</summary>
    private static GlbSkinBinding RootSkin() => new()
    {
        JointNodeIndices = [GlbScene.RootNodeIndex],
        InverseBindMatrices = [Matrix4x4.Identity],
        PerVertexInfluences = [[(0, 1f)], [(0, 1f)], [(0, 1f)]]
    };

    /// <summary>
    ///     Requires a decline and requires that no exception escapes. Every fixture passed here reached
    ///     <see cref="SceneValidation.Validate" /> and threw <see cref="InvalidDataException" /> before its decline
    ///     existed, so this is what fails if the decline is removed.
    /// </summary>
    private static string? DeclineWithoutThrowing(GlbScene scene, NifTextureResolver resolver)
    {
        var adapted = true;
        ModelDocument? document = null;
        string? reason = null;
        var escaped = Record.Exception(() =>
        {
            adapted = NifNeutralSceneAdapter.TryAdapt(scene, resolver, "fixture", out document, out reason,
                TestContext.Current.CancellationToken);
        });

        Assert.Null(escaped);
        Assert.False(adapted);
        Assert.Null(document);
        return reason;
    }

    /// <summary>Prepares one surface exactly as the adapter does, to pin the fixture's precondition.</summary>
    private static NifPreparedMaterial Prepare(RenderableSubmesh submesh, NifTextureResolver resolver) =>
        NifMaterialPreparation.Prepare(submesh, resolver, [], StarfieldGlbVertexLerpProjection.Resolve(submesh));

    private static ModelDocument Adapt(GlbScene scene)
    {
        using var resolver = Resolver();
        Assert.True(NifNeutralSceneAdapter.TryAdapt(scene, resolver, "fixture", out var document,
            out var reason, TestContext.Current.CancellationToken));
        Assert.Null(reason);
        return document;
    }

    private static string? Decline(GlbScene scene)
    {
        using var resolver = Resolver();
        Assert.False(NifNeutralSceneAdapter.TryAdapt(scene, resolver, "fixture", out var document,
            out var reason, TestContext.Current.CancellationToken));
        Assert.Null(document);
        return reason;
    }

    [Fact]
    public void Positions_CrossTheZUpToYUpBasis()
    {
        // The source authored (0,0,1), which is "up" in the NIF basis. glTF is Y-up, so the adapter must
        // report (0,1,0). An adapter that forgets the basis reports (0,0,1) and every export is rotated.
        var primitive = Adapt(Triangle()).Meshes[0].Primitives[0];
        var up = primitive.Vertices[2].Position;

        Assert.Equal(0f, up.X, 5);
        Assert.Equal(1f, up.Y, 5);
        Assert.Equal(0f, up.Z, 5);
    }

    [Fact]
    public void Normals_CrossTheBasisAndAreNeverZero()
    {
        var primitive = Adapt(Triangle()).Meshes[0].Primitives[0];

        foreach (var vertex in primitive.Vertices)
        {
            Assert.True(vertex.Normal.LengthSquared() > 0.9f,
                "A zero normal is what the shared validator rejects and what the earlier attempt emitted.");
            Assert.Equal(1f, vertex.Normal.Y, 5);
        }
    }

    [Fact]
    public void AbsentNormals_FallBackToUnitYRatherThanZero()
    {
        var scene = new GlbScene();
        var node = scene.AddNode("Shape", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, "Shape");
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "Shape",
            NodeIndex = node,
            Submesh = new RenderableSubmesh
            {
                ShapeName = "Shape",
                Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f],
                Triangles = [0, 1, 2],
                Normals = null,
                UVs = [0f, 0f, 1f, 0f, 0f, 1f]
            }
        });

        foreach (var vertex in Adapt(scene).Meshes[0].Primitives[0].Vertices)
        {
            Assert.Equal(Vector3.UnitY, vertex.Normal);
        }
    }

    [Fact]
    public void TheDocumentSatisfiesTheSharedValidator()
    {
        // TryAdapt calls SceneValidation.Validate itself, so reaching here already proves it. Asserting it
        // again independently keeps the guarantee explicit if that call is ever moved.
        var document = Adapt(Triangle());
        SceneValidation.Validate(document, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Tangents_CarryUnitDirectionsAndExactHandedness()
    {
        var document = Adapt(Triangle(
            tangents: [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f],
            bitangents: [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f]));
        var tangents = document.Meshes[0].Primitives[0].Tangents;

        Assert.NotNull(tangents);
        Assert.Equal(3, tangents.Values.Count);
        foreach (var tangent in tangents.Values)
        {
            var direction = new Vector3(tangent.X, tangent.Y, tangent.Z);
            Assert.Equal(1f, direction.Length(), 4);
            Assert.True(tangent.W is 1f or -1f, "glTF handedness must be exactly +1 or -1, never 0.");
        }
    }

    /// <summary>
    ///     Near-zero authored tangents on a surface without a normal map take the native builder's cutoff and fallback,
    ///     so every normalized tangent equals the one <see cref="GlbWriter" /> writes; the adapter's former own
    ///     conversion produced (1, 0, 0) there (measured divergence on the Xbox 360 stratum of the M2.1 gate).
    /// </summary>
    [Fact]
    public void NearZeroAuthoredTangents_WithoutANormalMap_EqualTheNativeWriters()
    {
        // A +X source normal makes the native fallback (cross of +Y with the normal) point down -Z in the source,
        // which is -Y after the basis change: distinct from the (1, 0, 0) the former conversion wrote.
        var scene = Triangle(
            normals: [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f],
            tangents: [0f, 0f, 1e-5f, 1e-5f, 0f, 0f, 0f, 0f, 0f],
            bitangents: [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f]);
        var submesh = scene.MeshParts[0].Submesh;
        Assert.True(NifNeutralSceneAdapter.HasAuthoredTangents(submesh));
        var built = NpcGlbTangentBuilder.BuildTangents(submesh);
        var expected = Enumerable.Range(0, 3).Select(index => GlbWriter.ReadTangent(submesh, built, index)).ToArray();
        // The basis rotation leaves float noise of order 1e-8 in the third component, so the fixture is pinned
        // within a tolerance; the comparison with the adapter below is exact.
        Assert.All(expected, tangent =>
        {
            Assert.Equal(0f, tangent.X, 1e-6f);
            Assert.Equal(-1f, tangent.Y, 1e-6f);
            Assert.Equal(0f, tangent.Z, 1e-6f);
            Assert.Equal(1f, tangent.W);
        });

        var tangents = Adapt(scene).Meshes[0].Primitives[0].Tangents;

        Assert.NotNull(tangents);
        Assert.Equal(expected, tangents.Values);
    }

    /// <summary>
    ///     A complete authored tangent array with an infinite value takes the native builder, whose normalization
    ///     yields NaN that <see cref="GlbWriter.ReadTangent" /> replaces with +X; the adapter writes exactly the same
    ///     instead of declining, because the native writer tolerates it.
    /// </summary>
    [Fact]
    public void InfiniteCompleteAuthoredTangent_MatchesTheNativeWriter()
    {
        var scene = Triangle(tangents: [float.PositiveInfinity, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f]);
        var submesh = scene.MeshParts[0].Submesh;
        var built = NpcGlbTangentBuilder.BuildTangents(submesh);
        var expected = Enumerable.Range(0, 3).Select(index => GlbWriter.ReadTangent(submesh, built, index)).ToArray();
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), expected[0]);

        var tangents = Adapt(scene).Meshes[0].Primitives[0].Tangents;

        Assert.NotNull(tangents);
        Assert.Equal(expected, tangents.Values);
    }

    /// <summary>
    ///     An authored tangent array whose length disagrees with the positions declines to the native writer, which
    ///     generates tangents from texture coordinates for it; the adapter never reads such an array.
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(6)]
    public void IncompleteAuthoredTangents_DeclineToTheNativeWriter(int length)
    {
        var authored = new float[length];
        for (var index = 0; index < length; index += 3) authored[index] = 1f;
        using var resolver = Resolver();

        Assert.Equal("A tangent array that disagrees with the position count retains the native writer.",
            DeclineWithoutThrowing(Triangle(tangents: authored), resolver));
    }

    /// <summary>The tangent guard rejects a non-unit, non-finite or zero-handedness tangent on a built primitive.</summary>
    [Theory]
    [InlineData(float.NaN, 0f, 0f, 1f)]
    [InlineData(2f, 0f, 0f, 1f)]
    [InlineData(1f, 0f, 0f, 0f)]
    public void UnportableTangent_IsRejectedByTheGuard(float x, float y, float z, float w)
    {
        var vertices = new[]
        {
            new SceneVertex(Vector3.Zero, Vector3.UnitY, Vector4.One, Vector2.Zero),
            new SceneVertex(Vector3.UnitX, Vector3.UnitY, Vector4.One, Vector2.Zero),
            new SceneVertex(Vector3.UnitZ, Vector3.UnitY, Vector4.One, Vector2.Zero)
        };
        var good = new Vector4(1f, 0f, 0f, 1f);
        var primitive = new ScenePrimitive("Shape", vertices, [0, 1, 2], 0, SceneColorEncoding.FloatingPoint,
            tangents: new SceneTangents([new Vector4(x, y, z, w), good, good]));

        Assert.Equal("A tangent that is not a finite unit direction with unit handedness retains the native writer.",
            NifNeutralSceneAdapter.PrimitiveReason(primitive, TestContext.Current.CancellationToken));
        Assert.Null(NifNeutralSceneAdapter.PrimitiveReason(
            new ScenePrimitive("Shape", vertices, [0, 1, 2], 0, SceneColorEncoding.FloatingPoint,
                tangents: new SceneTangents([good, good, good])),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AbsentTangents_AreOmittedRatherThanInvented()
    {
        Assert.Null(Adapt(Triangle()).Meshes[0].Primitives[0].Tangents);
    }

    [Fact]
    public void SkinInfluences_AreCarriedBeyondTheNativeFourInfluenceCap()
    {
        // SceneSkin.JointNodeIndices are node indices, not free-standing bone ids, so the fixture has to
        // put five real joints in the hierarchy. The shared validator enforces this, which is how the
        // first version of this test was caught inventing indices the scene did not contain.
        var scene = new GlbScene();
        var shape = scene.AddNode("Shape", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, "Shape");
        var joints = new int[5];
        for (var index = 0; index < joints.Length; index++)
        {
            joints[index] = scene.AddNode($"Bone{index}", GlbScene.RootNodeIndex, Matrix4x4.Identity,
                Matrix4x4.Identity, GlbNodeKind.Skeleton, $"Bone{index}");
        }

        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "Shape",
            NodeIndex = shape,
            Submesh = new RenderableSubmesh
            {
                ShapeName = "Shape",
                Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f],
                Triangles = [0, 1, 2],
                Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
                UVs = [0f, 0f, 1f, 0f, 0f, 1f]
            },
            Skin = new GlbSkinBinding
            {
                JointNodeIndices = joints,
                InverseBindMatrices =
                [
                    Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
                    Matrix4x4.Identity
                ],
                PerVertexInfluences =
                [
                    [(0, 0.2f), (1, 0.2f), (2, 0.2f), (3, 0.2f), (4, 0.2f)],
                    [(0, 1f)],
                    [(1, 1f)]
                ]
            }
        });

        var document = Adapt(scene);
        var influences = document.Meshes[0].Primitives[0].SkinInfluences;

        Assert.NotNull(influences);
        // Five influences on one vertex, where the native writer's packed joint vertex caps at four.
        Assert.Equal(5, influences.InfluencesPerVertex);
        Assert.Equal(15, influences.JointIndices.Count);
        Assert.Single(document.Skins);
        Assert.Equal(5, document.Skins[0].JointNodeIndices.Count);
    }

    [Fact]
    public void InverseBindMatrices_AlsoCrossTheBasis()
    {
        var translation = Matrix4x4.CreateTranslation(0f, 0f, 1f);
        var skin = new GlbSkinBinding
        {
            JointNodeIndices = [0],
            InverseBindMatrices = [translation],
            PerVertexInfluences = [[(0, 1f)], [(0, 1f)], [(0, 1f)]]
        };

        var bind = Adapt(Triangle(skin: skin)).Skins[0].InverseBindMatrices[0];

        // The source translated one unit along Z, which is "up" in the NIF basis. After crossing to
        // Y-up that unit must appear in M42, and M43 must be zero. Asserting it the other way round
        // would pass against an adapter that never crossed the basis at all.
        Assert.Equal(1f, bind.M42, 5);
        Assert.Equal(0f, bind.M43, 5);
    }

    [Fact]
    public void NodeHierarchy_PreservesIndicesAndTheSyntheticRoot()
    {
        var document = Adapt(Triangle());

        Assert.Equal("SceneRoot", document.Nodes[0].Name);
        Assert.Contains(1, document.Nodes[0].Children);
        Assert.Equal("Shape", document.Nodes[1].Name);
        Assert.NotNull(document.Nodes[1].MeshIndex);
    }

    [Theory]
    [InlineData("EmptyGeometry")]
    [InlineData("BadPositionStride")]
    [InlineData("NormalCountMismatch")]
    [InlineData("IndexOutOfRange")]
    [InlineData("TangentCountMismatch")]
    public void MalformedGeometry_Declines(string shape)
    {
        var scene = new GlbScene();
        var node = scene.AddNode("Shape", GlbScene.RootNodeIndex, Matrix4x4.Identity, Matrix4x4.Identity,
            GlbNodeKind.Attachment, "Shape");
        scene.MeshParts.Add(new GlbMeshPart { Name = "Shape", NodeIndex = node, Submesh = Malformed(shape) });

        Assert.False(string.IsNullOrWhiteSpace(Decline(scene)));
    }

    private static RenderableSubmesh Malformed(string shape) => shape switch
    {
        "EmptyGeometry" => new RenderableSubmesh { Positions = [], Triangles = [] },
        "BadPositionStride" => new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f], Triangles = [0, 1, 2]
        },
        "NormalCountMismatch" => new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f], Triangles = [0, 1, 2], Normals = [0f, 0f, 1f]
        },
        "IndexOutOfRange" => new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f], Triangles = [0, 1, 9]
        },
        "TangentCountMismatch" => new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f], Triangles = [0, 1, 2], Tangents = [1f, 0f, 0f]
        },
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
    };

    [Fact]
    public void UnrepresentedSeparateSpecularMap_Declines()
    {
        var reason = Decline(Triangle(mutate: submesh => submesh.SpecularMapTexturePath = "s.dds"));
        Assert.Equal("Maps beyond diffuse retain the native material writer.", reason);
    }

    [Fact]
    public void DecalBehavior_Declines()
    {
        Assert.Equal("Decal behavior retains the native material writer.",
            Decline(Triangle(mutate: submesh => submesh.IsDecal = true)));
    }

    [Fact]
    public void ExternalEmittance_Declines()
    {
        Assert.Equal("Emissive and external-emittance state retains the native material writer.",
            Decline(Triangle(mutate: submesh => submesh.UsesExternalEmittance = true)));
    }

    [Fact]
    public void AuthoredOblivionInputs_Decline()
    {
        Assert.Equal("Authored Oblivion shader inputs retain the native material writer.",
            Decline(Triangle(mutate: submesh => submesh.HasAuthoredOblivionOrdinaryInputs = true)));
    }

    [Fact]
    public void EffectTint_Declines()
    {
        Assert.Equal("Effect tint retains the native material writer.",
            Decline(Triangle(mutate: submesh => submesh.EffectTint = (0.5f, 0.5f, 0.5f))));
    }

    [Fact]
    public void SkinInfluenceOutsideTheJointPalette_Declines()
    {
        var skin = new GlbSkinBinding
        {
            JointNodeIndices = [0],
            InverseBindMatrices = [Matrix4x4.Identity],
            PerVertexInfluences = [[(7, 1f)], [(0, 1f)], [(0, 1f)]]
        };

        Assert.Equal("A skin influence outside the joint palette retains the native writer.",
            Decline(Triangle(skin: skin)));
    }

    /// <summary>An authored sky forced unlit but carrying a packed occlusion map declines instead of failing shared validation.</summary>
    [Fact]
    public void LightingMapsOnAPreparedUnlitSurface_Decline()
    {
        // An authored sky is forced unlit, yet its height slot still packs an occlusion map. The shared
        // validator rejects any lighting map on an unlit material, so this fixture used to throw there.
        var sky = SkyTriangle(new NifShaderTextureMetadata { TextureSlots = [null, null, null, "height.dds"] });
        using var resolver = TexturedResolver();
        var prepared = Prepare(sky, resolver);
        Assert.True(prepared.Unlit);
        Assert.False(prepared.HasEmission);
        Assert.NotNull(prepared.OcclusionImage);

        Assert.Equal("Lighting maps on a prepared unlit surface retain the native material writer.",
            DeclineWithoutThrowing(Scene(sky), resolver));
    }

    /// <summary>A NaN base-color factor declines instead of failing shared validation.</summary>
    [Fact]
    public void BaseColorOutsideTheUnitRange_Declines()
    {
        // Preparation clamps material alpha into [0,1], but Math.Clamp passes NaN through, so a NaN authored
        // alpha reaches the base-color factor that the shared validator requires to be in the unit range.
        var scene = Triangle(mutate: submesh => submesh.MaterialAlpha = float.NaN);
        using var resolver = Resolver();
        Assert.True(float.IsNaN(Prepare(scene.MeshParts[0].Submesh, resolver).BaseColor.W));

        Assert.Equal("A base color or alpha cutoff outside the unit range retains the native material writer.",
            DeclineWithoutThrowing(scene, resolver));
    }

    /// <summary>A NaN roughness factor declines instead of failing shared validation.</summary>
    [Fact]
    public void MaterialFactorOutsideItsPortableRange_Declines()
    {
        // A NaN authored glossiness passes through the roughness conversion and its clamp as NaN.
        var submesh = new RenderableSubmesh
        {
            ShapeName = "Shape",
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            MaterialGlossiness = float.NaN
        };
        using var resolver = Resolver();
        var prepared = Prepare(submesh, resolver);
        Assert.False(prepared.Unlit);
        Assert.True(float.IsNaN(prepared.RoughnessFactor));

        Assert.Equal("A material factor outside its portable range retains the native material writer.",
            DeclineWithoutThrowing(Scene(submesh), resolver));
    }

    /// <summary>Each projected vertex channel the shared validator rejects declines with its own reason.</summary>
    [Theory]
    [InlineData("position", "A non-finite vertex position retains the native writer.")]
    [InlineData("normal", "A vertex normal that is not a finite unit vector retains the native writer.")]
    [InlineData("uv", "A non-finite texture coordinate retains the native writer.")]
    public void UnportableProjectedGeometry_Declines(string channel, string expected)
    {
        // An infinite normal is not rejected as non-finite: normalizing it yields NaN, which the basis conversion
        // turns into a zero vector. The validator then rejects its length instead. Authored tangents always take
        // the native builder, so no NIF tangent reaches the tangent guard; it is proved directly below.
        var scene = channel switch
        {
            "position" => Triangle(positions: [float.NaN, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f]),
            "normal" => Triangle(normals: [float.PositiveInfinity, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 1f]),
            "uv" => Triangle(uvs: [float.NaN, 0f, 1f, 0f, 0f, 1f]),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
        };
        using var resolver = Resolver();

        Assert.Equal(expected, DeclineWithoutThrowing(scene, resolver));
    }

    /// <summary>The vertex-color guard accepts the unit range and rejects values outside it, including NaN.</summary>
    [Theory]
    [InlineData(1f, 1f, null)]
    [InlineData(0f, 0f, null)]
    [InlineData(1.5f, 1f, "A projected vertex color outside the unit range retains the native writer.")]
    [InlineData(-0.25f, 1f, "A projected vertex color outside the unit range retains the native writer.")]
    [InlineData(float.NaN, 1f, "A projected vertex color outside the unit range retains the native writer.")]
    [InlineData(1f, 1.0001f, "A projected vertex color outside the unit range retains the native writer.")]
    public void ProjectedVertexColor_MustStayInTheUnitRange(float red, float alpha, string? expected)
    {
        // Every current projection divides a byte by 255 or clamps, so no source reaches this through TryAdapt
        // today. The primitive check is exercised directly so that the guard is proved, not assumed.
        var primitive = new ScenePrimitive("Shape",
            [new SceneVertex(Vector3.Zero, Vector3.UnitY, new Vector4(red, 0f, 0f, alpha), Vector2.Zero)],
            [0, 0, 0]);

        Assert.Equal(expected,
            NifNeutralSceneAdapter.PrimitiveReason(primitive, TestContext.Current.CancellationToken));
    }

    /// <summary>Each joint-palette state the shared skin validator rejects declines with its own reason.</summary>
    [Theory]
    [InlineData("empty", "An empty joint palette retains the native writer.")]
    [InlineData("outsideTable", "A joint outside the node table retains the native writer.")]
    [InlineData("repeated", "A joint palette that repeats a node retains the native writer.")]
    [InlineData("nonFiniteBind", "A non-finite inverse bind matrix retains the native writer.")]
    [InlineData("detached", "A joint outside the scene root's hierarchy retains the native writer.")]
    public void UnportableJointPalette_Declines(string shape, string expected)
    {
        (int, float)[][] fullWeights = [[(0, 1f)], [(0, 1f)], [(0, 1f)]];
        var skin = shape switch
        {
            "empty" => new GlbSkinBinding
            {
                JointNodeIndices = [], InverseBindMatrices = [], PerVertexInfluences = [[], [], []]
            },
            "outsideTable" => new GlbSkinBinding
            {
                JointNodeIndices = [9], InverseBindMatrices = [Matrix4x4.Identity], PerVertexInfluences = fullWeights
            },
            "repeated" => new GlbSkinBinding
            {
                JointNodeIndices = [0, 0],
                InverseBindMatrices = [Matrix4x4.Identity, Matrix4x4.Identity],
                PerVertexInfluences = [[(0, 1f)], [(1, 1f)], [(0, 1f)]]
            },
            "nonFiniteBind" => new GlbSkinBinding
            {
                JointNodeIndices = [0],
                InverseBindMatrices = [Matrix4x4.CreateTranslation(float.NaN, 0f, 0f)],
                PerVertexInfluences = fullWeights
            },
            // Node 2 is added below with no parent, so it is its own topological root.
            "detached" => new GlbSkinBinding
            {
                JointNodeIndices = [2], InverseBindMatrices = [Matrix4x4.Identity], PerVertexInfluences = fullWeights
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
        var scene = Triangle(skin: skin);
        if (shape == "detached")
        {
            Assert.Equal(2, scene.AddNode("Detached", null, Matrix4x4.Identity, Matrix4x4.Identity,
                GlbNodeKind.Skeleton, "Detached"));
        }

        using var resolver = Resolver();

        Assert.Equal(expected, DeclineWithoutThrowing(scene, resolver));
    }

    /// <summary>Per-vertex skin weights the shared skin validator rejects decline instead of failing it.</summary>
    [Theory]
    [InlineData("negative", "A negative, non-finite or repeated skin weight retains the native writer.")]
    [InlineData("nonFinite", "A negative, non-finite or repeated skin weight retains the native writer.")]
    [InlineData("repeatedJoint", "A negative, non-finite or repeated skin weight retains the native writer.")]
    [InlineData("underweight", "Skin weights that do not sum to one retain the native writer.")]
    [InlineData("unweighted", "Skin weights that do not sum to one retain the native writer.")]
    public void UnportableSkinWeights_Decline(string shape, string expected)
    {
        // The repeated-joint row sums to exactly one, so only the repeat can decline it. The unweighted row
        // carries no influence at all, which reached the validator as a skin instance with no skin attributes.
        (int, float)[][] influences = shape switch
        {
            "negative" => [[(0, -1f)], [(0, 1f)], [(0, 1f)]],
            "nonFinite" => [[(0, float.NaN)], [(0, 1f)], [(0, 1f)]],
            "repeatedJoint" => [[(0, 0.5f), (0, 0.5f)], [(0, 1f)], [(0, 1f)]],
            "underweight" => [[(0, 0.5f)], [(0, 1f)], [(0, 1f)]],
            "unweighted" => [[], [], []],
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
        var skin = new GlbSkinBinding
        {
            JointNodeIndices = [GlbScene.RootNodeIndex],
            InverseBindMatrices = [Matrix4x4.Identity],
            PerVertexInfluences = influences
        };
        using var resolver = Resolver();

        Assert.Equal(expected, DeclineWithoutThrowing(Triangle(skin: skin), resolver));
    }

    /// <summary>A parent cycle or a parented scene root declines instead of failing shared hierarchy validation.</summary>
    [Theory]
    [InlineData("cycle")]
    [InlineData("parentedRoot")]
    public void CyclicHierarchyOrParentedRoot_Declines(string shape)
    {
        var scene = Triangle();
        if (shape == "cycle")
        {
            // Nodes 2 and 3 parent each other; neither is reachable from the root.
            Assert.Equal(2, scene.AddNode("A", 3, Matrix4x4.Identity, Matrix4x4.Identity, GlbNodeKind.Attachment, "A"));
            Assert.Equal(3, scene.AddNode("B", 2, Matrix4x4.Identity, Matrix4x4.Identity, GlbNodeKind.Attachment, "B"));
        }
        else
        {
            // The scene root gains a parent that is itself a root, which is acyclic but no longer a scene root.
            var holder = scene.AddNode("Holder", null, Matrix4x4.Identity, Matrix4x4.Identity,
                GlbNodeKind.Attachment, "Holder");
            scene.Nodes[GlbScene.RootNodeIndex] = new GlbNode
            {
                Name = "SceneRoot",
                ParentIndex = holder,
                LocalTransform = Matrix4x4.Identity,
                WorldTransform = Matrix4x4.Identity,
                Kind = GlbNodeKind.Root
            };
        }

        using var resolver = Resolver();

        Assert.Equal("A cyclic node hierarchy or a parented scene root retains the native writer.",
            DeclineWithoutThrowing(scene, resolver));
    }

    /// <summary>A node transform that converts to a non-finite matrix declines instead of failing shared validation.</summary>
    [Fact]
    public void NonFiniteNodeTransform_Declines()
    {
        var scene = Triangle();
        scene.AddNode("Broken", GlbScene.RootNodeIndex, Matrix4x4.CreateTranslation(float.NaN, 0f, 0f),
            Matrix4x4.Identity, GlbNodeKind.Attachment, "Broken");
        using var resolver = Resolver();

        Assert.Equal("A node transform that is not a finite affine matrix retains the native writer.",
            DeclineWithoutThrowing(scene, resolver));
    }

    /// <summary>An authored-sky part's primitive carries the native mesh name, suffix included; its node-named mesh does not.</summary>
    [Fact]
    public void AuthoredSkySuffix_NamesThePrimitiveAsTheNativeWriterNamesItsMesh()
    {
        var scene = Scene(SkyTriangle());
        using var resolver = Resolver();
        // Adapt first: the native writer normalizes winding on the source it is handed.
        var document = Adapt(scene);
        var native = SharpGLTF.Schema2.ModelRoot.ParseGLB(GlbWriter.WriteToBytes(scene, resolver));

        var mesh = Assert.Single(document.Meshes);
        var primitive = Assert.Single(mesh.Primitives);
        Assert.Equal("Shape" + AuthoredSkyGlbPreviewProjection.NameSuffix, primitive.Name);
        Assert.Equal(Assert.Single(native.LogicalMeshes).Name, primitive.Name);
        // The owning node holds this one part, so the mesh takes the part's suffixed name, as the native mesh does.
        Assert.Equal(Assert.Single(native.LogicalMeshes).Name, mesh.Name);
        // Control: an ordinary surface keeps its bare part name, so the suffix is not applied unconditionally.
        Assert.Equal("Shape", Adapt(Triangle()).Meshes[0].Primitives[0].Name);
    }

    /// <summary>A skinned authored-sky part's one-primitive mesh carries the suffix; its skin and placement node do not.</summary>
    [Fact]
    public void AuthoredSkySuffix_NamesASkinnedPartsMeshButNotItsSkinOrPlacement()
    {
        var document = Adapt(Scene(SkyTriangle(), RootSkin()));

        var mesh = Assert.Single(document.Meshes);
        Assert.Equal("Shape" + AuthoredSkyGlbPreviewProjection.NameSuffix, mesh.Name);
        Assert.Equal(mesh.Name, Assert.Single(mesh.Primitives).Name);
        Assert.Equal("Shape", Assert.Single(document.Skins).Name);
        Assert.Equal("Shape", document.Nodes[^1].Name);
    }

    [Fact]
    public void Cancellation_IsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var resolver = Resolver();

        Assert.Throws<OperationCanceledException>(() =>
            NifNeutralSceneAdapter.TryAdapt(Triangle(), resolver, "fixture", out _, out _, cancellation.Token));
    }

    [Fact]
    public void SourceFormatAndSceneIdentity_AreRecorded()
    {
        var document = Adapt(Triangle());

        Assert.Equal("nif", document.SourceFormat);
        Assert.Equal("fixture", document.Name);
        Assert.Single(document.Scenes);
    }
}
