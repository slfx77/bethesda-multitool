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
        string? bgClearSpec = null; // --bg-clear: clear the target OPAQUE before rendering (see below)
        string? leafTextureOverride = null; // --leaf-texture: SPT leaf atlas (the TREE ICON stand-in)
        float? leafDimming = null;   // --leaf-dimming: TREE CNAM LeafDimmingValue stand-in (0..1)
        float? branchDimming = null; // --branch-dimming: TREE CNAM BranchDimmingValue stand-in (0..1)
        float? litHour = null; // when set, bind real AtmosphereState lighting (sun+ambient) at this hour
        var azimuthDeg = 315f; // camera azimuth; override with --yaw to view a specific face
        var animTime = 0f; // --anim-time: pins the animation clock (UV scroll / skinned pose) for deterministic captures
        // --anim-hold + --out2: after settle, keep rendering N extra iterations with the SAME camera
        // and the clock ADVANCING from --anim-time at 30 Hz, then save the last frame to --out2.
        // Reproduces the live viewer's parked-camera state (streaming quiesces → batch reuse/freeze
        // engages) so idle-playback regressions show up headless: for an animated NIF, out2 must
        // DIFFER from out; equal pixels = playback froze when the scene settled.
        var animHoldIterations = 0;
        string? outPng2 = null;
        // --gui-shape: drive the reference renderer with the LIVE VIEWER's call shape — a
        // CullCameraPose (tolerant cull → widened batches + per-instance refilter) and
        // deferBlended:true + RenderBlendedDeferred() — instead of the harness's exact-cull inline
        // form. The paths diverge in the renderer, so an idle-playback bug can hide in one and not
        // the other.
        var guiShape = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--render-nif": nifPath = Next(args, ref i); break;
                case "--leaf-texture": leafTextureOverride = Next(args, ref i); break;
                case "--leaf-dimming":
                    leafDimming = float.TryParse(Next(args, ref i), out var ld) ? ld : null;
                    break;
                case "--branch-dimming":
                    branchDimming = float.TryParse(Next(args, ref i), out var bd) ? bd : null;
                    break;
                case "--archive" or "--bsa": meshArchive = Next(args, ref i); break;
                case "--out" or "-o": outPng = Next(args, ref i); break;
                case "--size": _ = int.TryParse(Next(args, ref i), out size); break;
                case "--bg": bgSpec = Next(args, ref i); break;
                case "--bg-clear": bgClearSpec = Next(args, ref i); break;
                case "--lit": litHour = float.TryParse(Next(args, ref i), out var h) ? h : 13f; break;
                case "--yaw": azimuthDeg = float.TryParse(Next(args, ref i), out var az) ? az : 315f; break;
                case "--anim-time": animTime = float.TryParse(Next(args, ref i), out var at) ? at : 0f; break;
                case "--anim-hold": _ = int.TryParse(Next(args, ref i), out animHoldIterations); break;
                case "--out2": outPng2 = Next(args, ref i); break;
                case "--gui-shape": guiShape = true; break;
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
                                    "[--bg <#RRGGBB|magenta|gray|checker>] [--bg-clear <#RRGGBB|gray|...>]");
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
        WaterRenderer12? water = null;
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
            // --leaf-texture: stand-in for the TREE record's ICON leaf atlas (the engine's leaf-texture
            // source; ESM-less renders otherwise miss the dev-era .spt material and leaves render white).
            // Keyed by both path shapes the decoder may look up.
            Dictionary<string, string>? leafTextures = null;
            if (!string.IsNullOrWhiteSpace(leafTextureOverride))
            {
                leafTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [nifPath!] = leafTextureOverride!,
                    ["trees\\" + Path.GetFileName(nifPath!)] = leafTextureOverride!,
                };
            }

            // --leaf-dimming/--branch-dimming: TREE CNAM canopy-depth dimming stand-ins for ESM-less
            // renders (the engine applies these per tree via CSpeedTreeRT::Set{Leaf,Branch}DimmingScalar).
            Dictionary<string, SpeedTreeDimming>? dimming = null;
            if (leafDimming is not null || branchDimming is not null)
            {
                var pair = new SpeedTreeDimming(leafDimming ?? 0f, branchDimming ?? 0f);
                dimming = new Dictionary<string, SpeedTreeDimming>(StringComparer.OrdinalIgnoreCase)
                {
                    [nifPath!] = pair,
                    ["trees\\" + Path.GetFileName(nifPath!)] = pair,
                };
            }

            meshCache = new ReferenceMeshCache12(
                gpu, meshArchives, textureResolver, textureCache, deletion,
                capacity: 2048, decodedCacheByteBudget: 256L * 1024 * 1024,
                autoSizeMeshCapacity: false, speedTreeLeafTextures: leafTextures,
                speedTreeDimming: dimming);
            references = new ReferenceRenderer12(gpu, recorder, ring, rootSig, heap, meshCache)
            {
                ShowInitiallyDisabled = true,
                ShowMarkers = true,
                StreamingThrottled = false, // drain decodes/uploads as fast as possible
            };

            // Water-shader submeshes are DIVERTED out of the reference draw path into authored
            // NifWaterGeometry (ReferenceMeshCache12 skips them as drawables), so a pure-water NIF
            // (e.g. FO4 Water\Water1024.nif) draws nothing through ReferenceRenderer12 alone —
            // 0/0/0 stats and a blank frame that reads as "dropped". Mirror the live viewer
            // (WorldView3DControl.Frame) by rendering the accumulated planes each frame.
            water = new WaterRenderer12(gpu, recorder, ring, rootSig, heap, deletion);

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
                // 3/4 view (default NW azimuth, ~30° elevation) — reads form better than flat top-down.
                var viewProj = OrthoViewProjBuilder.BuildViewProj(focus, azimuthDeg: azimuthDeg, elevationDeg: 30f,
                    orthoHalfHeight: halfHeight, aspect: 1f);
                var (camRight, camUp) = OrthoViewProjBuilder.CameraBasis(azimuthDeg, 30f);
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
                // --bg-clear: clear the target to an OPAQUE color BEFORE rendering, instead of the
                // post-composite --bg. Required to judge multiplicative (ZERO/SRC_COLOR) decals —
                // e.g. baked soft-shadow planes — whose output is fb·srcColor: over the default
                // transparent black clear they are unconditionally black; over an opaque clear they
                // darken it like they darken terrain in the live viewer.
                if (!string.IsNullOrWhiteSpace(bgClearSpec))
                {
                    var (cr, cg, cb) = ParseColor(bgClearSpec);
                    target.Bind(cmd, new Vortice.Mathematics.Color4(cr / 255f, cg / 255f, cb / 255f, 1f));
                }
                else
                {
                    target.Bind(cmd);
                }

                references.SetLeafBillboardBasis(camRight, camUp);
                // --anim-time pins the renderer's animation clock (UV scroll offsets, skinned pose)
                // to a fixed value: two renders at the same time are byte-identical, different times
                // show the motion — the settle loop re-renders the SAME pose each iteration.
                references.SetWind(Vector2.UnitX, 0f, animTime);
                int waterDraws;
                if (guiShape)
                {
                    // Live-viewer call shape: tolerant cull pose + deferred blended pass.
                    var pose = new BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12
                        .ReferenceRenderer12.CullCameraPose(
                            Vector3.Normalize(new Vector3(-1f, -1f, -0.5f)), 0.9f, 1f);
                    references.Render(viewProj, cylinder, deferBlended: true,
                        cullViewProj: viewProj, renderOrigin: default, cullCameraPose: pose);
                    water.SetNifWaterPlanes(references.NifWaterPlanes);
                    waterDraws = water.Render(viewProj, cylinder);
                    references.RenderBlendedDeferred();
                }
                else
                {
                    references.Render(viewProj, cylinder);
                    water.SetNifWaterPlanes(references.NifWaterPlanes);
                    waterDraws = water.Render(viewProj, cylinder);
                }

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
                // Water-only NIFs never increment ReferenceDrawn (their sole submesh is diverted to
                // a water plane), so also settle once the plane has drawn and nothing else is
                // pending. texPending==0 guards the mixed case (water + textured submeshes): the
                // textured part must still reach TexturesReady before we accept the frame.
                var waterSettled = waterDraws > 0 && s.ReferenceTexturePending == 0 && s.ReferenceMeshMissing == 0;
                if (complete && it > 0 && (s.ReferenceDrawn > 0 || waterSettled))
                {
                    drew = true;
                    Console.WriteLine(
                        $"[nif-render] settled at iter {it} (drawn={s.ReferenceDrawn}, " +
                        $"submeshDraws={s.ReferenceSubmeshDraws}, waterPlanes={waterDraws}, " +
                        $"local radius {localRadius:F0})");
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
                    $"missing={s.ReferenceMeshMissing} texPending={s.ReferenceTexturePending} " +
                    $"drawn={s.ReferenceDrawn} waterPlanes={references.NifWaterPlanes.Count}");
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

            // Parked-camera hold phase: identical camera every frame, clock advancing at 30 Hz —
            // the live viewer's idle state. Batch reuse engages once the quiet-build streak is met,
            // so a frozen-playback regression reproduces here without the GUI.
            if (animHoldIterations > 0 && !string.IsNullOrWhiteSpace(outPng2))
            {
                var localRadius = references.TryGetMeshLocalRadius(meshId, out var hr) && hr > 1f ? hr : 300f;
                var focus = frameRadius > 1f ? frameCenter : Vector3.Zero;
                var halfHeight = (frameRadius > 1f ? frameRadius : localRadius) * 1.1f;
                var viewProj = OrthoViewProjBuilder.BuildViewProj(focus, azimuthDeg: azimuthDeg,
                    elevationDeg: 30f, orthoHalfHeight: halfHeight, aspect: 1f);
                var (camRight, camUp) = OrthoViewProjBuilder.CameraBasis(azimuthDeg, 30f);
                var cullRadius = MathF.Max((focus.Length() + halfHeight) * 1.5f, 4096f);
                var cylinder = OrthoViewProjBuilder.BuildCoverCylinder(Vector3.Zero, cullRadius);

                var holdStartContentVersion = references.BatchContentVersion;
                byte[]? holdBgra = null;
                for (var k = 1; k <= animHoldIterations; k++)
                {
                    recorder.BeginFrame();
                    var cmd = recorder.CommandList;
                    deletion.Tick();
                    ring.ResetFrame();
                    heap.BeginFrame(recorder.FrameIndex);
                    cmd.SetDescriptorHeaps(1, new[] { heap.Heap });
                    cmd.SetGraphicsRootSignature(rootSig.RootSignature);
                    if (litHour is { } holdHour)
                    {
                        BindLitAtmosphere(cmd, recorder.FrameIndex, ring, holdHour, focus);
                    }
                    else
                    {
                        BindFlatAtmosphere(cmd, recorder.FrameIndex, ring);
                    }

                    target.Bind(cmd);
                    references.SetLeafBillboardBasis(camRight, camUp);
                    references.SetWind(Vector2.UnitX, 0f, animTime + (k / 30f));
                    if (guiShape)
                    {
                        var pose = new BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12
                            .ReferenceRenderer12.CullCameraPose(
                                Vector3.Normalize(new Vector3(-1f, -1f, -0.5f)), 0.9f, 1f);
                        references.Render(viewProj, cylinder, deferBlended: true,
                            cullViewProj: viewProj, renderOrigin: default, cullCameraPose: pose);
                        water.SetNifWaterPlanes(references.NifWaterPlanes);
                        water.Render(viewProj, cylinder);
                        references.RenderBlendedDeferred();
                    }
                    else
                    {
                        references.Render(viewProj, cylinder);
                        water.SetNifWaterPlanes(references.NifWaterPlanes);
                        water.Render(viewProj, cylinder);
                    }

                    target.RecordReadback(cmd);
                    recorder.EndFrame();
                    WaitForFence(gpu.FrameFence, recorder.LastSubmittedFenceValue);
                    holdBgra = target.ReadbackToBytes();
                }

                if (holdBgra is not null)
                {
                    var holdRgba = BgraToRgba(holdBgra);
                    if (!string.IsNullOrWhiteSpace(bgSpec))
                    {
                        CompositeOverBackground(holdRgba, size, size, bgSpec);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng2))!);
                    PngWriter.SaveRgba(holdRgba, size, size, outPng2);
                    // rebuilds ≈ animHoldIterations ⇒ batch reuse NEVER engaged and the hold did not
                    // exercise the frozen-scene path; a handful ⇒ the scene froze as intended.
                    Console.WriteLine(
                        $"[nif-render] saved {outPng2} (hold {animHoldIterations} iters, clock " +
                        $"{animTime:F2}->{animTime + (animHoldIterations / 30f):F2}s, " +
                        $"rebuilds during hold={references.BatchContentVersion - holdStartContentVersion})");
                }
            }

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
            water?.Dispose();
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

    /// <summary>Binds a neutral atmosphere CB (b3) — lighting/fog/sky disabled and EmissiveMult=1, so
    /// reference.frag uses its legacy flat shade (0.4 + 0.6·lambert). Faithful for verifying
    /// material/alpha, not time-of-day.</summary>
    private static void BindFlatAtmosphere(ID3D12GraphicsCommandList cmd, int frameIndex, GpuRingBuffer12 ring)
    {
        const int atmosphereBytes = 10 * 16 + 4 * 64 + 4 * 16;
        var cb = new float[atmosphereBytes / sizeof(float)];
        // CameraOrigin.w carries EmissiveMult; the material verifier has no active IMGS, so bind the
        // explicit neutral value while every atmosphere enable flag remains zero.
        cb[9 * 4 + 3] = 1f;
        var bytes = new byte[atmosphereBytes];
        Buffer.BlockCopy(cb, 0, bytes, 0, atmosphereBytes);
        var alloc = ring.Allocate(frameIndex, atmosphereBytes, GpuRingBuffer12.CbAlignment);
        Marshal.Copy(bytes, 0, alloc.CpuPtr, atmosphereBytes);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, alloc.GpuAddress);
        BindEmptyPointLights(cmd, frameIndex, ring);
    }

    /// <summary>Binds the REAL <see cref="AtmosphereState" /> lighting (sun + ambient) at
    /// <paramref name="gameHour" /> — the exact constants the live viewer uploads — so the worldspace
    /// shading path (full-strength ambient + sun·N·L, the engine SLS sum) is reproduced offscreen. Sky +
    /// fog are disabled to isolate the surface lighting. Mirrors the complete append-only
    /// WorldView3DControl.AtmosphereConstants layout. uAmbientColor.w stays 0 → the shader's
    /// 1.0 fallback (engine value).</summary>
    private static void BindLitAtmosphere(
        ID3D12GraphicsCommandList cmd, int frameIndex, GpuRingBuffer12 ring, float gameHour, Vector3 focus)
    {
        var a = AtmosphereState.Resolve(gameHour, weather: null, climate: null, lightingEnabled: true);
        // Zero the complete append-only b3 layout (ten atmosphere vectors, four shadow matrices,
        // four shadow vectors). In particular Params.w remains zero: this verifier has no world
        // placement cache, so it binds an empty local-light list.
        const int atmosphereBytes = 10 * 16 + 4 * 64 + 4 * 16;
        var cb = new float[atmosphereBytes / sizeof(float)];
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
        Put(8, a.FogFarColor.X, a.FogFarColor.Y, a.FogFarColor.Z, a.FogMaxOpacity);
        // CameraOrigin (camera-relative render origin) = 0: this is an ABSOLUTE ortho render, so nothing is
        // shifted. The reference VS no longer reads this slot anyway (it folds the origin CPU-side); leaving
        // the old eye value here was a latent shift that only didn't bite because the VS now ignores it.
        Put(9, 0f, 0f, 0f, 1f); // absolute origin + neutral EmissiveMult (no active IMGS)

        var alloc = ring.Allocate(frameIndex, atmosphereBytes, GpuRingBuffer12.CbAlignment);
        var bytes = new byte[atmosphereBytes];
        Buffer.BlockCopy(cb, 0, bytes, 0, atmosphereBytes);
        Marshal.Copy(bytes, 0, alloc.CpuPtr, atmosphereBytes);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, alloc.GpuAddress);
        BindEmptyPointLights(cmd, frameIndex, ring);
    }

    private static void BindEmptyPointLights(
        ID3D12GraphicsCommandList cmd, int frameIndex, GpuRingBuffer12 ring)
    {
        const int pointLightBytes = 4 * 16;
        var alloc = ring.Allocate(frameIndex, pointLightBytes, alignment: 16);
        Marshal.Copy(new byte[pointLightBytes], 0, alloc.CpuPtr, pointLightBytes);
        cmd.SetGraphicsRootShaderResourceView(
            (uint)GpuRootSignature12.Slots.PointLightsSrv,
            alloc.GpuAddress);
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
            if (nif is null) return false;
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
        StreamingQuiescence.IsQuiesced(r, terrain: null, strict: false);

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
