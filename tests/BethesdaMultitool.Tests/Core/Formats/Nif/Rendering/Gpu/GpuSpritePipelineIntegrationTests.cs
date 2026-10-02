using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Exercises sprite pipeline reuse and independent submission ownership on an actual native device.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class GpuSpritePipelineIntegrationTests
{
    private const string RedTexture = @"textures\pipeline-tests\red.dds";
    private const string BlueTexture = @"textures\pipeline-tests\blue.dds";
    private const string GreenTexture = @"textures\pipeline-tests\green.dds";

    /// <summary>Completes the later submission first without releasing an earlier result that still needs readback.</summary>
    [Fact]
    public void OverlappingSubmissionsRetainIndependentResourcesUntilTheirOwnCompletion()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.PreferHardwareThenWarp);
        Assert.NotNull(gpu);
        using var renderer = new GpuSpriteRenderer12(gpu);
        using var textures = CreateTextures();
        var red = Submit(renderer, CreateModel(CreateQuad(RedTexture)), textures);
        var blue = Submit(renderer, CreateModel(CreateQuad(BlueTexture)), textures);

        Assert.NotSame(red.Resources, blue.Resources);
        Assert.True(red.Resources.HasPending);
        Assert.True(blue.Resources.HasPending);
        Assert.True(blue.FenceValue > red.FenceValue);

        var blueSprite = renderer.CompleteRender(blue);
        AssertCenterColor(blueSprite, 0, 0, 255);
        Assert.False(blue.Resources.HasPending);
        Assert.Equal(IntPtr.Zero, blue.ReadbackBuffer.NativePointer);
        Assert.True(red.Resources.HasPending);
        Assert.NotEqual(IntPtr.Zero, red.ReadbackBuffer.NativePointer);

        var redSprite = renderer.CompleteRender(red);
        AssertCenterColor(redSprite, 255, 0, 0);
        Assert.False(red.Resources.HasPending);
        Assert.Equal(IntPtr.Zero, red.ReadbackBuffer.NativePointer);
        Assert.Throws<InvalidOperationException>(() => renderer.CompleteRender(blue));
    }

    /// <summary>Drains and retires submitted sprites whose caller abandons readback, with repeatable renderer disposal.</summary>
    [Fact]
    public void DisposalRetiresEveryUncompletedSubmissionAndRejectsFurtherWork()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.PreferHardwareThenWarp);
        Assert.NotNull(gpu);
        using var renderer = new GpuSpriteRenderer12(gpu);
        using var textures = CreateTextures();
        var model = CreateModel(CreateQuad(RedTexture));
        var first = Submit(renderer, model, textures);
        var second = Submit(renderer, model, textures);
        Assert.True(first.Resources.HasPending);
        Assert.True(second.Resources.HasPending);

        renderer.Dispose();

        Assert.False(first.Resources.HasPending);
        Assert.False(second.Resources.HasPending);
        Assert.Equal(IntPtr.Zero, first.ReadbackBuffer.NativePointer);
        Assert.Equal(IntPtr.Zero, second.ReadbackBuffer.NativePointer);
        renderer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => renderer.CompleteRender(first));
        Assert.Throws<ObjectDisposedException>(() => Submit(renderer, model, textures));
    }

    /// <summary>Preserves byte-identical blend output through slot reuse and rejected foreign-thread mutations.</summary>
    [Fact]
    public void EquivalentBlendInputsReuseTheRendererAfterForeignThreadCallsAreRejected()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.PreferHardwareThenWarp);
        Assert.NotNull(gpu);
        using var renderer = new GpuSpriteRenderer12(gpu);
        using var textures = CreateTextures();
        var knownModel = CreateModel(CreateQuad(BlueTexture), CreateQuad(GreenTexture, 1f, true, 6));
        var unknownModel = CreateModel(CreateQuad(BlueTexture), CreateQuad(GreenTexture, 1f, true, 255));
        var known = Submit(renderer, knownModel, textures);
        var ownerThread = Environment.CurrentManagedThreadId;

        // Keep native ownership on this thread. The asynchronous yield prevents an inline task
        // wait from executing the rejection checks on the renderer's creating thread.
#pragma warning disable xUnit1031 // Only rejected calls run remotely; every native operation remains on the owner thread.
        Task.Run(async () =>
        {
            await Task.Yield();
            Assert.NotEqual(ownerThread, Environment.CurrentManagedThreadId);
            Assert.Throws<InvalidOperationException>(() => Submit(renderer, unknownModel, textures));
            Assert.Throws<InvalidOperationException>(() => renderer.CompleteRender(known));
            Assert.Throws<InvalidOperationException>(renderer.Dispose);
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        Assert.True(known.Resources.HasPending);
        Assert.NotEqual(IntPtr.Zero, known.ReadbackBuffer.NativePointer);
        var unknown = Submit(renderer, unknownModel, textures);
        var knownSprite = renderer.CompleteRender(known);
        Assert.True(unknown.Resources.HasPending);
        var unknownSprite = renderer.CompleteRender(unknown);
        Assert.Equal(knownSprite.Width, unknownSprite.Width);
        Assert.Equal(knownSprite.Height, unknownSprite.Height);
        Assert.True(knownSprite.Pixels.AsSpan().SequenceEqual(unknownSprite.Pixels));
        var center = (knownSprite.Height / 2 * knownSprite.Width + knownSprite.Width / 2) * 4;
        Assert.InRange((int)knownSprite.Pixels[center], 0, 16);
        Assert.InRange((int)knownSprite.Pixels[center + 1], 96, 160);
        Assert.InRange((int)knownSprite.Pixels[center + 2], 96, 160);
        Assert.Equal((byte)255, knownSprite.Pixels[center + 3]);
        Assert.False(known.Resources.HasPending);
        Assert.False(unknown.Resources.HasPending);
    }

    /// <summary>Creates deterministic one-pixel diffuse inputs without external fixture dependencies.</summary>
    /// <returns>A caller-owned resolver containing opaque red/blue and half-alpha green.</returns>
    private static NifTextureResolver CreateTextures()
    {
        var textures = new NifTextureResolver();
        textures.InjectTexture(RedTexture, TestTextures.FromTexels(1, 1, (255, 0, 0, 255)));
        textures.InjectTexture(BlueTexture, TestTextures.FromTexels(1, 1, (0, 0, 255, 255)));
        textures.InjectTexture(GreenTexture, TestTextures.FromTexels(1, 1, (0, 255, 0, 128)));
        return textures;
    }

    /// <summary>Records a small front-facing sprite and requires an actual submitted result.</summary>
    /// <param name="renderer">Renderer owned by the calling thread.</param>
    /// <param name="model">Synthetic geometry and material inputs.</param>
    /// <param name="textures">Resolver kept alive through the synchronous recording phase.</param>
    /// <returns>The submission whose readback resources remain owned by the renderer.</returns>
    private static GpuSpriteRenderer12.PendingRender Submit(GpuSpriteRenderer12 renderer,
        NifRenderableModel model, NifTextureResolver textures)
    {
        var pending = renderer.SubmitRender(model, textures, 1f, 32, 64, 90f, 0f, 64);
        Assert.NotNull(pending);
        return pending;
    }

    /// <summary>Builds bounds from the same geometry that will be uploaded by the production sprite path.</summary>
    /// <param name="submeshes">Quads in their authored draw order.</param>
    /// <returns>A bounded model ready for submission.</returns>
    private static NifRenderableModel CreateModel(params RenderableSubmesh[] submeshes)
    {
        var model = new NifRenderableModel();
        foreach (var submesh in submeshes)
        {
            model.Submeshes.Add(submesh);
            model.ExpandBounds(submesh.Positions);
        }
        return model;
    }

    /// <summary>Creates an unlit quad using the established sprite fixture orientation.</summary>
    /// <param name="texture">Injected diffuse texture identity.</param>
    /// <param name="y">Depth toward the chosen front-facing camera.</param>
    /// <param name="blended">Whether the NIF alpha state enables blending.</param>
    /// <param name="sourceBlend">Raw NIF source factor; unknown values retain the existing SourceAlpha fallback.</param>
    /// <returns>A double-sided quad whose texture supplies its color and alpha.</returns>
    private static RenderableSubmesh CreateQuad(string texture, float y = 0f,
        bool blended = false, byte sourceBlend = 6) => new()
    {
        Positions = [-1f, y, -1f, 1f, y, -1f, 1f, y, 1f, -1f, y, 1f],
        Triangles = [0, 1, 2, 0, 2, 3],
        UVs = [0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f],
        DiffuseTexturePath = texture,
        IsEmissive = true,
        IsDoubleSided = true,
        HasAlphaBlend = blended,
        SrcBlendMode = sourceBlend,
        DstBlendMode = 7,
        MaterialAlpha = 1f
    };

    /// <summary>Checks the actual center texel so empty or transparent captures cannot satisfy lifetime tests.</summary>
    /// <param name="sprite">Completed tightly packed RGBA sprite.</param>
    /// <param name="red">Expected unlit red channel.</param>
    /// <param name="green">Expected unlit green channel.</param>
    /// <param name="blue">Expected unlit blue channel.</param>
    private static void AssertCenterColor(SpriteResult sprite, byte red, byte green, byte blue)
    {
        Assert.True(sprite.Width > 0 && sprite.Height > 0);
        Assert.Equal(sprite.Width * sprite.Height * 4, sprite.Pixels.Length);
        var offset = (sprite.Height / 2 * sprite.Width + sprite.Width / 2) * 4;
        Assert.Equal(red, sprite.Pixels[offset]);
        Assert.Equal(green, sprite.Pixels[offset + 1]);
        Assert.Equal(blue, sprite.Pixels[offset + 2]);
        Assert.Equal((byte)255, sprite.Pixels[offset + 3]);
    }
}
