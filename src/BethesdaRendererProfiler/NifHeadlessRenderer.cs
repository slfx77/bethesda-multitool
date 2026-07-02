using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Vortice.Direct3D12;

namespace BethesdaRendererProfiler;

/// <summary>
///     Headless single-NIF render through the REAL viewer D3D12 stack (<see cref="ReferenceRenderer12" />
///     + reference.frag — the exact path the live 3D viewer uses for placed objects), to a PNG. Unlike the
///     CLI <c>render</c> command (which uses the lightweight <c>GpuSpriteRenderer12</c> + skin shaders),
///     this reproduces the viewer's material/alpha/pass behaviour, so viewer-specific bugs (opaque-vs-
///     transparent, effect shaders, baked shadows) are reproducible + verifiable offscreen.
///     <para>Invoked via <c>--render-nif &lt;esm-relative-path&gt; --archive &lt;meshes.bsa&gt;
///     --textures-bsa &lt;a.bsa&gt; [&lt;b.bsa&gt; …] --out &lt;png&gt; [--size N]
///     [--bg &lt;#RRGGBB|magenta|gray|checker&gt;]</c>. <c>--bg</c> composites the (transparent)
///     render over an opaque backdrop so transparency-vs-opacity bugs are visible.</para>
/// </summary>
internal static class NifHeadlessRenderer
{
    public static int Run(string[] args)
    {
        string? nifPath = null;
        string? meshArchive = null;
        var textureArchives = new List<string>();
        string? outPng = null;
        var size = 512;
        string? bgSpec = null;
        float? litHour = null; // when set, bind real AtmosphereState lighting (sun+ambient) at this hour

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--render-nif": nifPath = Next(args, ref i); break;
                case "--archive" or "--bsa": meshArchive = Next(args, ref i); break;
                case "--out" or "-o": outPng = Next(args, ref i); break;
                case "--size": _ = int.TryParse(Next(args, ref i), out size); break;
                case "--bg": bgSpec = Next(args, ref i); break;
                case "--lit": litHour = float.TryParse(Next(args, ref i), out var h) ? h : 13f; break;
                case "--textures-bsa" or "--textures-archive":
                    // Consume following tokens until the next flag.
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        textureArchives.Add(args[++i]);
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(nifPath) || string.IsNullOrWhiteSpace(meshArchive) || string.IsNullOrWhiteSpace(outPng))
        {
            Console.Error.WriteLine("Usage: --render-nif <esm-relative-nif-path> --archive <meshes.bsa> " +
                                    "--textures-bsa <tex.bsa> [<tex2.bsa> ...] --out <png> [--size N] " +
                                    "[--bg <#RRGGBB|magenta|gray|checker>]");
            return 2;
        }
        if (!File.Exists(meshArchive))
        {
            Console.Error.WriteLine($"Meshes archive not found: {meshArchive}");
            return 2;
        }

        size = Math.Clamp(size, 32, 2048);

        // This is a verification tool — always decode the mesh fresh. The persistent on-disk mesh cache
        // bakes the decoded geometry AND its alpha classification (render mode, depth-writing-blend, …),
        // so a warm entry would serve a STALE decode that hides classifier/extractor changes under test.
        EnvironmentVariables.Set(EnvironmentVariables.Viewer.PersistentMeshCache, "0");

        Console.WriteLine($"[nif-render] {nifPath}  via {Path.GetFileName(meshArchive)}  -> {outPng} ({size}px)");

        var gpu = GpuDevice12.Create(false);
        if (gpu is null)
        {
            Console.Error.WriteLine("D3D12 device unavailable.");
            return 3;
        }

        GpuCommandRecorder12? recorder = null;
        GpuRingBuffer12? ring = null;
        GpuDescriptorHeapAllocator12? heap = null;
        GpuRootSignature12? rootSig = null;
        GpuDeletionQueue12? deletion = null;
        MeshArchiveSet? meshArchives = null;
        GpuTextureCache12? textureCache = null;
        ReferenceMeshCache12? meshCache = null;
        ReferenceRenderer12? references = null;
        GpuOffscreenSceneTarget12? target = null;

        try
        {
            recorder = new GpuCommandRecorder12(gpu);
            ring = new GpuRingBuffer12(gpu, GpuCommandRecorder12.FramesInFlight, bytesPerFrame: 64u * 1024 * 1024);
            heap = new GpuDescriptorHeapAllocator12(
                gpu, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                capacity: 131072, framesInFlight: GpuCommandRecorder12.FramesInFlight, persistentCapacity: 16384);
            rootSig = GpuRootSignature12.Create(gpu);
            deletion = new GpuDeletionQueue12(framesToHold: GpuCommandRecorder12.FramesInFlight);

            meshArchives = MeshArchiveSet.Open(
                meshArchive, null, enableFuzzy: false, includeLooseFiles: false);
            var texArr = textureArchives.ToArray();
            var textureResolver = new NifTextureResolver(texArr);
            var gpuTextureResolver = new NifGpuTextureResolver(texArr);
            textureCache = new GpuTextureCache12(gpu, recorder, heap, gpuTextureResolver, deletion);
            meshCache = new ReferenceMeshCache12(
                gpu, meshArchives, textureResolver, textureCache, deletion,
                capacity: 2048, decodedCacheByteBudget: 256L * 1024 * 1024,
                autoSizeMeshCapacity: false, speedTreeHeights: null, speedTreeLeafTextures: null);
            references = new ReferenceRenderer12(gpu, recorder, ring, rootSig, heap, meshCache)
            {
                ShowInitiallyDisabled = true,
                ShowMarkers = true,
                StreamingThrottled = false, // drain decodes/uploads as fast as possible
            };

            // Synthetic 1-cell scene: one REFR at the world origin in grid cell (0,0). A null spatial
            // index makes ReferenceRenderer12 iterate the cell dict directly (cylinder.ContainsCell).
            var placement = new PlacedReference
            {
                ModelPath = nifPath,
                FormId = 1,
                BaseFormId = 1,
                RecordType = "REFR",
                X = 0,
                Y = 0,
                Z = 0,
                Scale = 1f,
            };
            var cell = new CellRecord { FormId = 1, GridX = 0, GridY = 0, PlacedObjects = [placement] };
            var cells = new Dictionary<(int gx, int gy), CellRecord> { [(0, 0)] = cell };
            references.LoadData(new WorldRenderCache(), cells, spatialIndex: null);

            target = new GpuOffscreenSceneTarget12(gpu, size, size);
            var meshId = RenderableReference.ComputeMeshId(nifPath);

            // Frame the mesh's real AABB center (the placement is at world origin, scale 1, so the
            // mesh-local AABB == its world AABB). Without this the camera centres on the NIF origin, which
            // for off-origin geometry (e.g. the saloon sign sits ~700u up the facade) pushes it to a
            // corner. Falls back to origin + LocalBoundsRadius framing if the geometry can't be extracted.
            var frameCenter = Vector3.Zero;
            var frameRadius = 0f;
            if (TryExtractModelBounds(meshArchives, textureResolver, nifPath, out var bMin, out var bMax))
            {
                frameCenter = (bMin + bMax) * 0.5f;
                frameRadius = (bMax - bMin).Length() * 0.5f;
                Console.WriteLine($"[nif-render] AABB center=({frameCenter.X:F0},{frameCenter.Y:F0},{frameCenter.Z:F0}) radius={frameRadius:F0}");
            }

            // Render until the mesh + textures finish streaming (or a safety cap), then save. The first
            // frames use a default frame size; once the mesh resolves we frame to its real bounds.
            const int maxIterations = 200;
            byte[]? finalBgra = null;
            var drew = false;
            // Per-iteration render stats are noisy; opt in with NIF_RENDER_VERBOSE=1 when diagnosing a
            // "nothing rendered" case (shows cull/missing/texPending/drawn counts per frame).
            var verbose = Environment.GetEnvironmentVariable("NIF_RENDER_VERBOSE") is "1";
            for (var it = 0; it < maxIterations; it++)
            {
                var localRadius = references.TryGetMeshLocalRadius(meshId, out var r) && r > 1f ? r : 300f;
                // Prefer the extracted AABB center+radius; fall back to origin + the resolved local radius.
                var focus = frameRadius > 1f ? frameCenter : Vector3.Zero;
                var halfHeight = (frameRadius > 1f ? frameRadius : localRadius) * 1.1f;
                // 3/4 view (NW azimuth, ~30° elevation) — reads form better than flat top-down.
                var viewProj = OrthoViewProjBuilder.BuildViewProj(focus, azimuthDeg: 315f, elevationDeg: 30f,
                    orthoHalfHeight: halfHeight, aspect: 1f);
                var (camRight, camUp) = OrthoViewProjBuilder.CameraBasis(315f, 30f);
                // Cull cylinder centred at the REFR origin (world 0,0,0 — cell (0,0)) with a radius that
                // reaches the framed geometry, so ContainsCell(0,0) + the per-REFR sphere both pass.
                var cullRadius = MathF.Max((focus.Length() + halfHeight) * 1.5f, 4096f);
                var cylinder = OrthoViewProjBuilder.BuildCoverCylinder(Vector3.Zero, cullRadius);

                recorder.BeginFrame();
                var cmd = recorder.CommandList;
                deletion.Tick();
                ring.ResetFrame();
                heap.BeginFrame(recorder.FrameIndex);
                cmd.SetDescriptorHeaps(1, new[] { heap.Heap });
                cmd.SetGraphicsRootSignature(rootSig.RootSignature);
                if (litHour is { } lh)
                {
                    BindLitAtmosphere(cmd, recorder.FrameIndex, ring, lh, focus);
                }
                else
                {
                    BindFlatAtmosphere(cmd, recorder.FrameIndex, ring);
                }
                target.Bind(cmd);
                references.SetLeafBillboardBasis(camRight, camUp);
                references.SetWind(Vector2.UnitX, 0f, 0f);
                references.Render(viewProj, cylinder);
                target.RecordReadback(cmd);
                recorder.EndFrame();

                var fenceValue = recorder.LastSubmittedFenceValue;
                var complete = StreamingComplete(references.LastStats);
                WaitForFence(gpu.FrameFence, fenceValue);

                var s = references.LastStats;
                if (verbose)
                {
                    Console.WriteLine(
                        $"[nif-render] it={it} focus=({focus.X:F0},{focus.Y:F0},{focus.Z:F0}) halfH={halfHeight:F0} " +
                        $"cells={s.ReferenceCellsVisited} cand={s.ReferenceCandidates} culled={s.ReferenceCulled} " +
                        $"missing={s.ReferenceMeshMissing} texPending={s.ReferenceTexturePending} drawn={s.ReferenceDrawn} " +
                        $"submeshDraws={s.ReferenceSubmeshDraws} batches={s.ReferenceBatches} inst={s.ReferenceInstances} " +
                        $"instDraws={s.ReferenceInstancedDraws} blended={s.ReferenceBlendedDraws} " +
                        $"uploads={s.ReferenceGpuUploads} qDec={s.ReferenceQueuedDecodes} aDec={s.ReferenceActiveDecodes} " +
                        $"texPendRes={s.ReferenceTexturePendingResolves} texPendUp={s.ReferenceTexturePendingUploads}");
                }

                finalBgra = target.ReadbackToBytes(); // keep latest in case we hit the cap

                // Settle only once the reference has actually DRAWN (its submeshes passed the
                // TexturesReady gate) AND streaming has quiesced. StreamingComplete alone can go true a
                // frame or two before a resolved texture's async copy-queue upload flips TexturesReady —
                // that intermediate state isn't in PendingResolveCount/PendingUploadCount — so waiting on
                // it alone saves a blank frame with the mesh withheld. Requiring ReferenceDrawn > 0 waits
                // for the actual draw. (IsReady flips on success OR failure, so a missing texture still
                // resolves to a fallback and draws rather than hanging.)
                if (complete && it > 0 && s.ReferenceDrawn > 0)
                {
                    drew = true;
                    Console.WriteLine(
                        $"[nif-render] settled at iter {it} (drawn={s.ReferenceDrawn}, " +
                        $"submeshDraws={s.ReferenceSubmeshDraws}, local radius {localRadius:F0})");
                    break;
                }
                Thread.Sleep(40); // let background decode/texture-resolve advance before re-rendering
            }

            if (finalBgra is null)
            {
                Console.Error.WriteLine("Render produced no pixels.");
                return 4;
            }

            if (!drew)
            {
                // The reference never issued a draw within the cap — the saved frame is likely blank.
                // Surface it loudly (with the last stats) instead of silently writing an empty PNG; re-run
                // with NIF_RENDER_VERBOSE=1 to see per-frame cull/missing/texPending counts.
                var s = references.LastStats;
                Console.Error.WriteLine(
                    $"[nif-render] WARNING: reference never drew within {maxIterations} iterations — output " +
                    $"may be blank. last: cells={s.ReferenceCellsVisited} culled={s.ReferenceCulled} " +
                    $"missing={s.ReferenceMeshMissing} texPending={s.ReferenceTexturePending} drawn={s.ReferenceDrawn}");
            }

            var rgba = BgraToRgba(finalBgra);
            // --bg composites the (premultiplied-alpha) render over an opaque backdrop. The render
            // target clears to (0,0,0,0) and reference.frag's blend leaves premultiplied output (see
            // ReferencePipelineFactory12: straight SrcAlpha color blend + One/One alpha over a zero
            // dest), so the over-operator is out = src_rgb + bg*(1-a). This makes transparency vs.
            // opacity bugs obvious: a faithfully-transparent sign background lets the backdrop show
            // through; an erroneously-opaque one paints a solid block. "checker" spots alpha holes.
            if (!string.IsNullOrWhiteSpace(bgSpec))
            {
                CompositeOverBackground(rgba, size, size, bgSpec);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
            PngWriter.SaveRgba(rgba, size, size, outPng);
            Console.WriteLine($"[nif-render] saved {outPng}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nif-render] failed: {ex}");
            return 5;
        }
        finally
        {
            try { recorder?.WaitForGpuIdle(); } catch { /* best effort */ }
            target?.Dispose();
            references?.Dispose();
            meshCache?.Dispose();
            textureCache?.Dispose();
            meshArchives?.Dispose();
            deletion?.Dispose();
            heap?.Dispose();
            ring?.Dispose();
            rootSig?.Dispose();
            recorder?.Dispose();
            gpu.Dispose();
        }
    }

    /// <summary>Binds a zeroed atmosphere CB (b3) — lighting/fog/sky disabled, so reference.frag uses its
    /// legacy flat shade (0.4 + 0.6·lambert). Faithful for verifying material/alpha, not time-of-day.</summary>
    private static void BindFlatAtmosphere(ID3D12GraphicsCommandList cmd, int frameIndex, GpuRingBuffer12 ring)
    {
        const int atmosphereBytes = 9 * 16; // 9 float4, matches the b3 cbuffer layout
        var alloc = ring.Allocate(frameIndex, atmosphereBytes, GpuRingBuffer12.CbAlignment);
        // Zero the (mapped upload) CB so lighting/fog/sky flags read 0 → reference.frag's legacy shade.
        Marshal.Copy(new byte[atmosphereBytes], 0, alloc.CpuPtr, atmosphereBytes);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, alloc.GpuAddress);
    }

    /// <summary>Binds the REAL <see cref="AtmosphereState" /> lighting (sun + ambient) at
    /// <paramref name="gameHour" /> — the exact constants the live viewer uploads — so the worldspace
    /// shading path (ambient×0.3 + sun·N·L) is reproduced offscreen. Sky + fog are disabled to isolate
    /// the surface lighting. Mirrors WorldView3DControl.AtmosphereConstants (9 × float4).</summary>
    private static void BindLitAtmosphere(
        ID3D12GraphicsCommandList cmd, int frameIndex, GpuRingBuffer12 ring, float gameHour, Vector3 focus)
    {
        var a = AtmosphereState.Resolve(gameHour, weather: null, climate: null, lightingEnabled: true);
        // 9 float4 = 144 bytes, matching the b3 cbuffer layout in reference.frag.hlsl.
        var cb = new float[9 * 4];
        void Put(int slot, float x, float y, float z, float w)
        {
            cb[slot * 4 + 0] = x; cb[slot * 4 + 1] = y; cb[slot * 4 + 2] = z; cb[slot * 4 + 3] = w;
        }
        Put(0, a.SunWorldDirection.X, a.SunWorldDirection.Y, a.SunWorldDirection.Z, a.SunIntensity);
        Put(1, a.SunColor.X, a.SunColor.Y, a.SunColor.Z, 1f);   // w = lightingEnabled
        Put(2, a.AmbientColor.X, a.AmbientColor.Y, a.AmbientColor.Z, 0f);
        Put(3, a.SkyTopColor.X, a.SkyTopColor.Y, a.SkyTopColor.Z, 0f);   // w = skyEnabled OFF
        Put(4, a.SkyHorizonColor.X, a.SkyHorizonColor.Y, a.SkyHorizonColor.Z, 0f);
        Put(5, a.FogColor.X, a.FogColor.Y, a.FogColor.Z, 0f);   // w = fogEnabled OFF
        // Oblique eye matching the 315°/30° ortho view — NOT directly above. A straight-up eye makes V
        // point along +Z, and a below-horizon/degenerate sunDir gives H = normalize(sunDir+V) ≈
        // normalize(0) = NaN in the specular path, which 4×MSAA resolves to a blank frame.
        var eye = new Vector3(focus.X - 5793f, focus.Y - 5793f, focus.Z + 4096f); // NW + up
        Put(6, gameHour, a.FogNear, a.FogFar, 0f);
        Put(7, eye.X, eye.Y, eye.Z, a.FogPower);  // camera pos for spec/fog
        // CameraOrigin (camera-relative render origin) = 0: this is an ABSOLUTE ortho render, so nothing is
        // shifted. The reference VS no longer reads this slot anyway (it folds the origin CPU-side); leaving
        // the old eye value here was a latent shift that only didn't bite because the VS now ignores it.
        Put(8, 0f, 0f, 0f, 0f);

        const int atmosphereBytes = 9 * 16;
        var alloc = ring.Allocate(frameIndex, atmosphereBytes, GpuRingBuffer12.CbAlignment);
        var bytes = new byte[atmosphereBytes];
        Buffer.BlockCopy(cb, 0, bytes, 0, atmosphereBytes);
        Marshal.Copy(bytes, 0, alloc.CpuPtr, atmosphereBytes);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, alloc.GpuAddress);
    }

    /// <summary>Extracts the NIF's local-space AABB (for framing) by parsing the mesh from the archive.
    /// The path is tried as-is and with a <c>meshes\</c> prefix (ESM-relative vs archive-internal).</summary>
    private static bool TryExtractModelBounds(
        MeshArchiveSet meshArchives, NifTextureResolver textureResolver, string nifPath,
        out Vector3 min, out Vector3 max)
    {
        min = default;
        max = default;

        // SpeedTree trees are .spt recipes, not NIFs — NifParser.Parse would throw and the camera would fall
        // back to origin framing (the tree renders off-frame / invisibly). Build the same geometry the live
        // decode does (LeafBillboard=true) and take ITS AABB so --render-nif frames trees correctly.
        if (nifPath.EndsWith(".spt", StringComparison.OrdinalIgnoreCase))
        {
            if (!meshArchives.TryExtractFile(nifPath, out var sptBytes, out _)
                && !meshArchives.TryExtractFile("trees\\" + Path.GetFileName(nifPath), out sptBytes, out _))
            {
                return false;
            }

            try
            {
                var spt = SptFile.TryParse(sptBytes);
                if (spt is null)
                {
                    return false;
                }

                var seed = spt.General.Token2005 != 0 ? spt.General.Token2005 : 1u;
                var sptModel = SptGeometryBuilder.Build(spt, seed,
                    SptGeometryOptions.FromEnvironment() with { LeafBillboard = true });
                if (sptModel is not { HasGeometry: true } || sptModel.MaxX < sptModel.MinX)
                {
                    return false;
                }

                min = new Vector3(sptModel.MinX, sptModel.MinY, sptModel.MinZ);
                max = new Vector3(sptModel.MaxX, sptModel.MaxY, sptModel.MaxZ);
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (!meshArchives.TryExtractFile(nifPath, out var bytes, out _)
            && !meshArchives.TryExtractFile("meshes\\" + nifPath, out bytes, out _))
        {
            return false;
        }
        try
        {
            var nif = NifParser.Parse(bytes);
            // Match the reference/decode path (collectBillboards + treatRootsAsIdentity) so the AABB includes
            // baked particle clouds — otherwise a pure-particle NIF (FXDust) frames to nothing.
            var model = NifGeometryExtractor.Extract(bytes, nif, textureResolver,
                treatRootsAsIdentity: true, collectBillboards: true);
            if (model is not { HasGeometry: true } || model.MaxX < model.MinX) return false;
            min = new Vector3(model.MinX, model.MinY, model.MinZ);
            max = new Vector3(model.MaxX, model.MaxY, model.MaxZ);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool StreamingComplete(WorldRenderStats r) =>
        r.ReferenceGpuUploads == 0
        && r.ReferenceQueuedDecodes == 0
        && r.ReferenceActiveDecodes == 0
        && r.ReferenceTexturePendingResolves == 0
        && r.ReferenceTexturePendingUploads == 0;

    private static void WaitForFence(ID3D12Fence fence, ulong value)
    {
        if (fence.CompletedValue >= value) return;
        using var ev = new AutoResetEvent(false);
        D3D12FenceWaiter.WaitForFence(fence, value, ev);
    }

    /// <summary>Alpha-composites <paramref name="rgba" /> (premultiplied) over a solid backdrop or a
    /// checkerboard in place, forcing the result opaque. <paramref name="spec" /> is a hex color
    /// (<c>#RRGGBB</c>/<c>RRGGBB</c>), a named color (magenta/gray/white/black/green), or
    /// <c>checker</c> (alternating grays — best for spotting transparency holes).</summary>
    private static void CompositeOverBackground(byte[] rgba, int width, int height, string spec)
    {
        var checker = spec.Equals("checker", StringComparison.OrdinalIgnoreCase);
        var (sr, sg, sb) = checker ? (default, default, default) : ParseColor(spec);
        const int cell = 16; // checker square size in pixels
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                byte br, bg, bb;
                if (checker)
                {
                    var dark = ((x / cell) + (y / cell)) % 2 == 0;
                    br = bg = bb = dark ? (byte)80 : (byte)160;
                }
                else
                {
                    br = sr; bg = sg; bb = sb;
                }

                var a = rgba[i + 3] / 255f;
                var inv = 1f - a;
                // Premultiplied over: src already carries src_rgb*a, so just add bg*(1-a).
                rgba[i] = (byte)Math.Clamp(rgba[i] + br * inv, 0f, 255f);
                rgba[i + 1] = (byte)Math.Clamp(rgba[i + 1] + bg * inv, 0f, 255f);
                rgba[i + 2] = (byte)Math.Clamp(rgba[i + 2] + bb * inv, 0f, 255f);
                rgba[i + 3] = 255;
            }
        }
    }

    private static (byte r, byte g, byte b) ParseColor(string spec)
    {
        switch (spec.ToLowerInvariant())
        {
            case "magenta": return (255, 0, 255);
            case "gray" or "grey": return (128, 128, 128);
            case "white": return (255, 255, 255);
            case "black": return (0, 0, 0);
            case "green": return (0, 200, 0);
            case "cyan": return (0, 255, 255);
        }
        var hex = spec.StartsWith('#') ? spec[1..] : spec;
        if (hex.Length == 6
            && byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return (r, g, b);
        }
        return (255, 0, 255); // unparseable → magenta (loud, so it's obvious the spec was wrong)
    }

    private static byte[] BgraToRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
        return rgba;
    }

    private static string? Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : null;
}
