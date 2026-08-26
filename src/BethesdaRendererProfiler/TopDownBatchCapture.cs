using System.Diagnostics;
using System.Globalization;
using System.Text;
using BethesdaMultitool;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaRendererProfiler;

/// <summary>
///     Renders every top-down-capturable scene in a loaded source — each exterior worldspace, the
///     unlinked-exterior set, and each interior cell — to one PNG apiece, framed to the subject's
///     own extent.
///     <para>
///         Everything runs against a SINGLE load of the source. Opening a memory dump costs seconds
///         to minutes, and a corpus has hundreds of subjects across dozens of dumps, so a
///         process-per-subject harness would spend almost all of its wall clock re-parsing. The
///         renderer's caches also stay warm across subjects, which matters most for the shared
///         meshes an interior set has in common.
///     </para>
/// </summary>
internal sealed class TopDownBatchCapture(
    WorldView3DControl worldView,
    RendererProfilerOptions options)
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Pause between convergence attempts. The render itself is synchronous on the UI thread;
    ///     this yields long enough for decode/upload workers to make progress before the next pass
    ///     re-reads their state.
    /// </summary>
    private const int AttemptDelayMilliseconds = 200;

    /// <summary>
    ///     Renders every subject and returns the run summary. Never throws for a single subject's
    ///     failure — one bad scene must not cost the other subjects in the dump, let alone the rest
    ///     of a corpus run.
    /// </summary>
    internal async Task<TopDownBatchResult> RunAsync(CancellationToken ct = default)
    {
        var outputDir = options.CaptureTopDownBatchDirectory!;
        Directory.CreateDirectory(outputDir);

        // List-only never renders, so it must not require a render-capable provider — it exists
        // precisely so a driver script can inventory a dump with no donors and no GPU. Every
        // other mode still demands one, and the list-only branch returns before any render, so
        // `provider` is only dereferenced when it passed this gate.
        var provider = worldView as ITopDownSceneRenderer;
        if (!options.CaptureBatchListOnly && provider?.CanRenderTopDown != true)
        {
            Console.WriteLine("[Batch] UNAVAILABLE: top-down provider not ready (no D3D12 / no Meshes BSA).");
            return new TopDownBatchResult(0, 0, 0, 0, "provider-unavailable");
        }

        // Collapse the live view so its render loop idles and does not share the command recorder
        // with the offscreen passes — the same thing the single-shot capture does.
        worldView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        // Lift the control's default final-dimension cap, which is sized for the 2D map's
        // continuously re-rendering overlay. A one-shot capture at a fixed world-units-per-pixel
        // scale legitimately needs more, and the cap silently rescaled every large subject.
        worldView.TopDownMaxFinalDimension = Math.Max(options.CaptureBatchMaxPixels, 64);
        await Task.Delay(800, ct);

        IReadOnlyList<TopDownCaptureSubject> subjects = worldView.Profiler_EnumerateTopDownSubjects();
        if (!string.IsNullOrEmpty(options.CaptureBatchFilter))
        {
            // Comma-separated substrings, OR-combined, so a verification run can name several
            // subjects without paying for the dump's whole sweep per subject.
            var needles = options.CaptureBatchFilter.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            subjects = subjects
                .Where(s => needles.Any(n => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            Console.WriteLine($"[Batch] filter '{options.CaptureBatchFilter}' -> {subjects.Count} subject(s).");
        }

        if (!string.IsNullOrEmpty(options.CaptureBatchFilterFile))
        {
            // Exact EditorIDs, one per line: substring matching over-selects ("Vault3" swallows
            // Vault3a/Vault3c), and a scripted partial re-render can carry hundreds of names —
            // past what a command line holds.
            var names = (await File.ReadAllLinesAsync(options.CaptureBatchFilterFile, ct))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            subjects = subjects.Where(s => names.Contains(s.Name)).ToArray();
            Console.WriteLine($"[Batch] filter-file {names.Count} name(s) -> {subjects.Count} subject(s).");
        }

        if (options.CaptureBatchListOnly)
        {
            // Headless inventory: everything a driver script needs to diff this dump's CURRENT
            // ref attribution against an existing corpus manifest — no GPU, no asset donors.
            foreach (var s in subjects)
            {
                var gatedOut = s.NonPersistentCount < MinNonPersistentPlacements &&
                               (s.Kind == TopDownSubjectKind.Interior || s.TerrainCellCount == 0);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[BatchList] {s.Kind}|{s.Name}|0x{s.FormId:X8}|placements={s.PlacementCount}|" +
                    $"nonPersistent={s.NonPersistentCount}|render={!gatedOut}"));
            }

            Console.WriteLine($"[Batch] list-only: {subjects.Count} subject(s).");
            return new TopDownBatchResult(0, 0, 0, 0, null);
        }

        if (subjects.Count == 0)
        {
            Console.WriteLine("[Batch] no capturable worldspaces or interiors in this source.");
            return new TopDownBatchResult(0, 0, 0, 0, "no-subjects");
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[Batch] {subjects.Count} subject(s): " +
            $"{subjects.Count(s => s.Kind != TopDownSubjectKind.Interior)} exterior, " +
            $"{subjects.Count(s => s.Kind == TopDownSubjectKind.Interior)} interior. -> {outputDir}"));

        var manifest = new StringBuilder();
        manifest.AppendLine(ManifestHeader);

        // Cells whose refs resolved no drawable asset. Recorded rather than silently dropped: on a
        // memory dump this is evidence that the cell WAS captured but its assets are not recoverable
        // from the donor builds, which is a different fact from the cell not being captured at all.
        var empties = new List<string>();

        // Subjects skipped because the capture never retained them: only persistent refs (which
        // every cell owns regardless of residency), no terrain. Without this gate most of the
        // corpus is doors floating in a void. Recorded so "not rendered" stays auditable.
        var noScenery = new List<string>();

        // subject -> mesh paths that stayed unresolved at quiescence. The manifest's ref_drawn <
        // ref_instances says SOMETHING is missing; this says what, which is the actionable half —
        // it's how "the Ultra Luxe tables don't render" becomes a named path to chase through the
        // donor builds.
        var missingMeshes = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Angles are trimetric-only: under the straight-down projection every yaw produces the same
        // world-axis-aligned image, so rendering four of them would write four identical files.
        var angles = options.CaptureBatchAngles == 4 &&
                     options.CaptureBatchProjection == TopDownProjection.Trimetric
            ? FourCompassAngles
            : PrimaryAngleOnly;

        int written = 0, skipped = 0, unsettled = 0, failed = 0;
        foreach (var subject in subjects)
        {
            // Residency gate. Terrain exempts exteriors: a terrain-only worldspace renders
            // legitimate captured ground with zero placements.
            if (subject.NonPersistentCount < MinNonPersistentPlacements &&
                (subject.Kind == TopDownSubjectKind.Interior || subject.TerrainCellCount == 0))
            {
                noScenery.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{subject.Name} (non-persistent={subject.NonPersistentCount}, placements={subject.PlacementCount})"));
                Console.WriteLine(
                    $"[Batch] skip (not captured): {subject.Name} ({subject.NonPersistentCount} non-persistent / {subject.PlacementCount} placements)");
                continue;
            }

            // Whether any angle of THIS subject has produced content. An "empty" verdict on a later
            // angle then cannot be true — the same geometry is in frame, only the camera moved — so
            // it is treated as the streaming flake it is rather than as a fact about the subject.
            var subjectDrew = false;
            foreach (var (yaw, angleName) in angles)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(outputDir, BuildFileName(subject, angleName));

                if (options.CaptureBatchResume && File.Exists(path))
                {
                    skipped++;
                    Console.WriteLine($"[Batch] skip (exists): {Path.GetFileName(path)}");
                    continue;
                }

                try
                {
                    var outcome = await CaptureSubjectAsync(provider!, subject, path, yaw, angleName, ct);
                    if (outcome is { Empty: true } && subjectDrew)
                    {
                        // A sibling angle already drew this subject, so "nothing drew" is a
                        // transient streaming/cull flake, not a verdict. One full re-capture gets a
                        // fresh settle budget and has always recovered in practice.
                        Console.WriteLine($"[Batch] retry: {subject.Name} [{angleName}] drew nothing but a sibling angle drew content");
                        outcome = await CaptureSubjectAsync(provider!, subject, path, yaw, angleName, ct);
                    }

                    if (outcome is null)
                    {
                        failed++;
                        Console.WriteLine($"[Batch] FAILED: {subject.Name} [{angleName}] (render returned null)");
                        continue;
                    }

                    if (outcome.Empty)
                    {
                        if (subjectDrew)
                        {
                            failed++;
                            Console.WriteLine($"[Batch] FAILED: {subject.Name} [{angleName}] (drew nothing twice despite sibling angles drawing)");
                            continue;
                        }

                        // Emptiness is a property of the subject's content, not of the viewpoint —
                        // the remaining angles would render the same nothing, so stop here and
                        // record the subject once.
                        empties.Add(subject.Name);
                        Console.WriteLine($"[Batch] empty (nothing drew): {subject.Name}");
                        break;
                    }

                    subjectDrew = true;
                    written++;
                    if (!outcome.Settled) unsettled++;
                    manifest.AppendLine(outcome.ManifestRow);
                    Console.WriteLine(outcome.ConsoleLine);

                    // The renderer's missing list describes its LAST pass, which for a settled
                    // outcome is quiescent — nulls there are terminally unresolved, not pending.
                    if (outcome.Settled &&
                        worldView.Profiler_LastMissingMeshPaths is { Count: > 0 } missing)
                    {
                        var set = missingMeshes.TryGetValue(subject.Name, out var existing)
                            ? existing
                            : missingMeshes[subject.Name] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var meshPath in missing)
                        {
                            set.Add(meshPath);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Error("Batch capture '{0}' [{1}] failed: {2}", subject.Name, angleName, ex);
                    Console.WriteLine($"[Batch] FAILED: {subject.Name} [{angleName}]: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // The manifest is what makes a corpus reviewable: a blank PNG is indistinguishable from a
        // missing-assets PNG by eye, but coverage + ref_drawn + settled separate them.
        //
        // A FILTERED run into a directory that already holds reports MERGES them: this run's
        // subjects replace their old rows/entries, everything else is preserved. Without this a
        // scripted partial re-render (e.g. patching subjects an attribution fix changed) would
        // clobber a whole corpus dump's manifest with a handful of rows.
        var partialRun = !string.IsNullOrEmpty(options.CaptureBatchFilter) ||
                         !string.IsNullOrEmpty(options.CaptureBatchFilterFile);
        var renderedNames = subjects.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var manifestPath = Path.Combine(outputDir, $"{Prefix()}manifest.csv");
        if (partialRun && File.Exists(manifestPath))
        {
            var merged = new StringBuilder();
            var lines = await File.ReadAllLinesAsync(manifestPath, ct);
            merged.AppendLine(lines.Length > 0 ? lines[0] : ManifestHeader);
            foreach (var line in lines.Skip(1))
            {
                if (!renderedNames.Contains(CsvField(line, 2))) merged.AppendLine(line);
            }

            // This run's rows, minus the duplicate header.
            var newBody = manifest.ToString();
            merged.Append(newBody.AsSpan(newBody.IndexOf('\n') + 1));
            await File.WriteAllTextAsync(manifestPath, merged.ToString(), ct);
        }
        else
        {
            await File.WriteAllTextAsync(manifestPath, manifest.ToString(), ct);
        }

        await WriteOrMergeListAsync(
            Path.Combine(outputDir, $"{Prefix()}empty.txt"), empties, partialRun, renderedNames, ct);
        await WriteOrMergeListAsync(
            Path.Combine(outputDir, $"{Prefix()}no_scenery.txt"), noScenery, partialRun, renderedNames, ct);

        if (missingMeshes.Count > 0 || partialRun)
        {
            var missingPath = Path.Combine(outputDir, $"{Prefix()}missing_meshes.txt");
            var report = new StringBuilder();
            if (partialRun && File.Exists(missingPath))
            {
                // Keep other subjects' blocks: a block is a non-indented subject line followed by
                // its indented paths.
                var keep = true;
                foreach (var line in await File.ReadAllLinesAsync(missingPath, ct))
                {
                    if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                    {
                        keep = !renderedNames.Contains(line.Trim());
                    }

                    if (keep) report.AppendLine(line);
                }
            }

            foreach (var (subjectName, paths) in missingMeshes)
            {
                report.AppendLine(subjectName);
                foreach (var meshPath in paths)
                {
                    report.Append("  ").AppendLine(meshPath);
                }
            }

            if (report.Length > 0)
            {
                await File.WriteAllTextAsync(missingPath, report.ToString(), ct);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[Batch] {missingMeshes.Sum(kv => kv.Value.Count)} unresolved mesh path(s) across " +
                    $"{missingMeshes.Count} subject(s) -> {missingPath}"));
            }
        }

        var summary = string.Create(CultureInfo.InvariantCulture,
            $"[Batch] done: {written} written ({unsettled} unsettled), {empties.Count} empty, " +
            $"{skipped} skipped, {failed} failed, {noScenery.Count} no-scenery -> {manifestPath}");
        Log.Info(summary);
        Console.WriteLine(summary);
        return new TopDownBatchResult(written, skipped, unsettled, failed, null);
    }

    /// <summary>
    ///     The four-corner angle set: camera positions at the NE/SE/SW/NW compass corners, each
    ///     looking back at the subject. Yaw here is the camera's azimuth, so 30° puts the camera
    ///     north-east of the subject. Offset from the exact 45° corners by the trimetric asymmetry
    ///     (see <see cref="TrimetricViewProjBuilder.YawDegrees" />), and 90° apart so together the
    ///     four views see every facade.
    /// </summary>
    private static readonly (float Yaw, string Name)[] FourCompassAngles =
    [
        (TrimetricViewProjBuilder.YawDegrees, "ne"),
        (TrimetricViewProjBuilder.YawDegrees + 90f, "se"),
        (TrimetricViewProjBuilder.YawDegrees + 180f, "sw"),
        (TrimetricViewProjBuilder.YawDegrees + 270f, "nw")
    ];

    /// <summary>Single-angle set: the primary NE view, with no filename suffix.</summary>
    private static readonly (float Yaw, string Name)[] PrimaryAngleOnly =
    [
        (TrimetricViewProjBuilder.YawDegrees, "")
    ];

    /// <summary>
    ///     Fraction of the image's long edge the drawn content should span after auto-fit. Short of
    ///     1.0 so nothing sits flush against the border.
    /// </summary>
    private const float TargetFillFraction = 0.92f;

    /// <summary>
    ///     A subject renders iff it holds at least one NON-PERSISTENT ref (terrain-bearing
    ///     exteriors are exempt). USER RULING: capture residency is the test, not mesh taxonomy —
    ///     persistent refs (doors, activators) exist for every cell in the file whether or not it
    ///     was ever resident, while non-persistent refs exist only because the cell was loaded at
    ///     capture time.
    /// </summary>
    private const int MinNonPersistentPlacements = 1;

    /// <summary>
    ///     Maximum auto-fit iterations per subject. Each costs a full settle, so this is a budget,
    ///     not a convergence target; a well-framed subject exits for free the moment
    ///     <see cref="TryAutoFit" /> declines to move the frame. The budget must cover the worst
    ///     realistic case — clipped on two edges — which spends passes on compounded
    ///     <see cref="ClippedGrowthStep" /> growth before the shrink-to-target pass can even run.
    /// </summary>
    private const int MaxRefitPasses = 4;

    /// <summary>
    ///     Minimum box growth applied when content is clipped at the image border. A clipped frame's
    ///     measured fill saturates just below 1.0 however much is actually outside, so the
    ///     proportional correction alone converges far too slowly to recover within the pass budget.
    /// </summary>
    private const float ClippedGrowthStep = 1.35f;

    /// <summary>
    ///     Renders one subject, auto-fits the framing to what was actually drawn, and saves the PNG.
    ///     <para>
    ///         The auto-fit pass is not optional polish. The initial framing comes from placed-object
    ///         ORIGINS, but what gets drawn is their MESHES, which extend around those origins by an
    ///         amount nothing in the record data reports — the Ultra Luxe casino's amphitheatre
    ///         reaches far enough past its origin to hang out of frame while the opposite half of the
    ///         picture is empty. Measuring the rendered pixels is the only way to know the true
    ///         extent, so the first pass doubles as the measurement.
    ///     </para>
    /// </summary>
    private async Task<SubjectOutcome?> CaptureSubjectAsync(
        ITopDownSceneRenderer provider,
        TopDownCaptureSubject subject,
        string path,
        float yawDegrees,
        string angleName,
        CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        var first = await RenderUntilSettledAsync(provider, subject, yawDegrees, deadline, ct);
        if (first is null) return null;

        var render = first.Value.Render;
        var attempts = first.Value.Attempts;

        if (options.CaptureBatchProjection == TopDownProjection.Trimetric)
        {
            // Iterate rather than single-shot. One refit cannot fix a CLIPPED frame reliably: when
            // content runs off the edge the measurement only sees the part that survived, so it
            // cannot know how much is missing and one correction may still fall short. Bounded so a
            // pathological subject cannot spin.
            //
            // Each pass CONTINUES from its own refit even when that pass's render did not rank
            // better; only the OUTPUT keeps the best-ranked render. The two must be decoupled: a
            // frame clipped on two edges needs several growth steps compounded before any single
            // render ranks better, and breaking on the first not-better pass (as this loop
            // originally did) froze such subjects at their bloated initial frame — Benny's floor
            // shipped at 7% coverage, cut on two edges, identically on all four angles.
            var work = render;
            var bestSubject = subject;
            for (var pass = 0; pass < MaxRefitPasses; pass++)
            {
                if (TryAutoFit(subject, work, yawDegrees) is not { } refitted) break;

                var next = await RenderUntilSettledAsync(provider, refitted, yawDegrees, deadline, ct);
                attempts += next?.Attempts ?? 0;
                if (next is not { } fit) break;

                work = fit.Render;
                subject = refitted;
                var kept = IsBetterFraming(work, render);
                if (kept)
                {
                    render = work;
                    bestSubject = refitted;
                }

                var wb = OpaqueBounds(work.Bgra, work.Width, work.Height);
                Log.Info("[Batch] {0} [{1}] fit pass {2}: {3}x{4} clippedEdges={5} kept={6}",
                    subject.Name, angleName, pass + 1, work.Width, work.Height,
                    wb?.ClippedEdges ?? -1, kept);
            }

            // The manifest's world box and the empty-confirm re-render must describe the frame that
            // was actually written, not wherever the exploration ended.
            subject = bestSubject;
        }

        var coverage = Coverage(render.Bgra);

        // Emptiness is judged ONLY on the settled render, and on drawn PIXELS rather than on
        // ReferenceDrawn: a terrain-only exterior legitimately has content with zero references,
        // and an early pass is empty simply because nothing has streamed in yet.
        //
        // A single settled-but-blank pass is NOT trusted: quiescence can report true before the
        // first decode request has been issued (observed once on a warm disk cache — the same
        // subject drew 724 refs on the next run), so an empty verdict takes a second, independent
        // settle to confirm. Costs a fraction of a second per genuinely-empty subject.
        if (coverage <= 0d)
        {
            var confirm = await RenderUntilSettledAsync(provider, subject, yawDegrees, deadline, ct);
            attempts += confirm?.Attempts ?? 0;
            if (confirm is not { } second || Coverage(second.Render.Bgra) <= 0d)
            {
                // drawn/instances separate the two ways a frame can be blank: 0 instances means
                // scene selection or culling rejected everything, drawn>0 with no pixels means
                // geometry was submitted but rasterized nothing (framing/projection).
                Log.Info("[Batch] {0} [{1}] empty verdict: {2}x{3} drawn={4}/{5} settled={6}",
                    subject.Name, angleName, render.Width, render.Height,
                    render.ReferenceDrawn, render.ReferenceInstances, render.IsComplete);
                return SubjectOutcome.EmptySubject;
            }

            render = second.Render;
            coverage = Coverage(render.Bgra);
        }

        // Save-time trim: the auto-fit frames with deliberate padding and a target-fill tolerance,
        // which reads as large dead margins in the file. Crop to the content, keep the scale.
        var (outBgra, outW, outH) = CropToContent(render.Bgra, render.Width, render.Height, TrimMarginPixels);
        coverage = Coverage(outBgra);
        var rgba = BgraToRgba(outBgra);
        PngWriter.SaveRgba(rgba, outW, outH, path);

        var seconds = deadline.Elapsed.TotalSeconds;
        var name = Path.GetFileName(path);
        var row = string.Create(CultureInfo.InvariantCulture,
            $"{Csv(name)},{subject.Kind},{Csv(subject.Name)},0x{subject.FormId:X8}," +
            $"{subject.CellCount},{subject.PlacementCount},{outW},{outH}," +
            $"{subject.MinX:F0},{subject.MaxX:F0},{subject.MinY:F0},{subject.MaxY:F0}," +
            $"{render.IsComplete},{coverage:F4},{render.ReferenceDrawn},{render.ReferenceInstances}," +
            $"{attempts},{seconds:F1},{(angleName.Length == 0 ? "ne" : angleName)}");

        var line = string.Create(CultureInfo.InvariantCulture,
            $"[Batch] {name} {outW}x{outH} coverage={coverage:P1} " +
            $"drawn={render.ReferenceDrawn}/{render.ReferenceInstances} " +
            $"settled={render.IsComplete} attempts={attempts} {seconds:F1}s");

        return new SubjectOutcome(render.IsComplete, row, line);
    }

    /// <summary>
    ///     Re-frames a subject so the pixels actually drawn end up centred and filling
    ///     <see cref="TargetFillFraction" /> of the image. Returns null when the render was empty or
    ///     already well framed, so a well-fitted subject costs no second pass.
    /// </summary>
    private static TopDownCaptureSubject? TryAutoFit(
        TopDownCaptureSubject subject, TopDownRender render, float yawDegrees)
    {
        if (OpaqueBounds(render.Bgra, render.Width, render.Height) is not { } b) return null;
        var (minPx, minPy, maxPx, maxPy, clippedEdges) = b;
        var clipped = clippedEdges > 0;

        var contentW = maxPx - minPx + 1;
        var contentH = maxPy - minPy + 1;
        if (contentW <= 1 || contentH <= 1) return null;

        var fill = MathF.Max((float)contentW / render.Width, (float)contentH / render.Height);
        var centreOffX = (minPx + maxPx + 1) * 0.5f - render.Width * 0.5f;
        var centreOffY = (minPy + maxPy + 1) * 0.5f - render.Height * 0.5f;
        var offCentre = MathF.Max(
            MathF.Abs(centreOffX) / render.Width, MathF.Abs(centreOffY) / render.Height);

        // Already framed well enough — skip the extra render. A CLIPPED frame is never "well
        // enough", however close its fill looks: content running off the edge reads as a fill near
        // 1.0, which is exactly what a perfectly-framed subject also looks like.
        if (!clipped && MathF.Abs(fill - TargetFillFraction) < 0.06f && offCentre < 0.03f) return null;

        // The measured pixel offset is in THIS angle's image plane, so the world shift must use the
        // same yaw's basis — the NE basis applied to an SW render would shift the box the wrong way.
        var (_, right, up) = TrimetricViewProjBuilder.Basis(yawDegrees);
        var (frameW, frameH) = TrimetricViewProjBuilder.MeasureFrame(
            subject.MinX, subject.MaxX, subject.MinY, subject.MaxY, subject.MinZ, subject.MaxZ,
            yawDegrees);

        // Pixel offset → world shift. Image +Y runs DOWN while the camera's up axis runs up, hence
        // the negation on the vertical term.
        var shift = right * (centreOffX * (frameW / render.Width))
                    - up * (centreOffY * (frameH / render.Height));

        // Grow the box when content overflows the target fill, shrink it when content is lost in
        // empty space. Clamped so one bad measurement cannot send a subject to a degenerate or
        // absurd scale.
        //
        // A clipped frame gets a MINIMUM growth step: its measured fill saturates just under 1.0 no
        // matter how much content is outside, so the proportional term alone would nudge the box by
        // ~9% per pass and could never recover a badly-overflowing subject within the pass budget.
        var scale = Math.Clamp(fill / TargetFillFraction, 0.25f, 4f);
        if (clipped) scale = MathF.Max(scale, ClippedGrowthStep);

        var cx = (subject.MinX + subject.MaxX) * 0.5f + shift.X;
        var cy = (subject.MinY + subject.MaxY) * 0.5f + shift.Y;
        var cz = (subject.MinZ + subject.MaxZ) * 0.5f + shift.Z;
        var hx = MathF.Max((subject.MaxX - subject.MinX) * 0.5f * scale, 1f);
        var hy = MathF.Max((subject.MaxY - subject.MinY) * 0.5f * scale, 1f);
        var hz = MathF.Max((subject.MaxZ - subject.MinZ) * 0.5f * scale, 1f);

        return subject with
        {
            MinX = cx - hx, MaxX = cx + hx,
            MinY = cy - hy, MaxY = cy + hy,
            MinZ = cz - hz, MaxZ = cz + hz
        };
    }

    /// <summary>
    ///     Fraction of drawn pixels the auto-fit box may discard from each edge.
    ///     <para>
    ///         A strict min/max box is at the mercy of a single outlier. Cells routinely contain one
    ///         stray reference sitting far outside the room — a lone door panel below Benny's floor
    ///         pushed the box down until the actual suite occupied the top 30% of a 2048² image.
    ///         Trimming a thin tail off each edge frames what is actually there.
    ///     </para>
    /// </summary>
    private const float OutlierTrimFraction = 0.005f;

    /// <summary>
    ///     Minimum drawn pixels in an edge row/column for it to count as clipped, as a fraction of
    ///     the perpendicular image dimension. 2% of a 2000-px edge is 40 px — well above any orphan
    ///     blob, well below a wall face running off the frame.
    /// </summary>
    private const float EdgeClipFraction = 0.02f;

    /// <summary>Absolute floor for the edge-clip test, for very small renders.</summary>
    private const float EdgeClipMinPixels = 24f;

    /// <summary>
    ///     Whether <paramref name="candidate" /> frames the subject better than <paramref name="current" />.
    ///     <para>
    ///         Ranked on CLIPPING first, not on coverage. Coverage is the wrong yardstick and was
    ///         actively harmful here: zooming out to bring clipped content back into frame
    ///         necessarily LOWERS coverage (the extra pixels are empty), so a coverage-based guard
    ///         rejected precisely the corrections that fixed the clipping it was meant to catch.
    ///         Fewer clipped EDGES wins outright — a refit that recovers one of two cut edges is
    ///         real progress even though both frames are "clipped". Among equally-clipped framings,
    ///         the one closer to the target fill wins.
    ///     </para>
    /// </summary>
    private static bool IsBetterFraming(TopDownRender candidate, TopDownRender current)
    {
        var cb = OpaqueBounds(candidate.Bgra, candidate.Width, candidate.Height);
        if (cb is null) return false; // drew nothing — never accept
        var rb = OpaqueBounds(current.Bgra, current.Width, current.Height);
        if (rb is null) return true;

        var (cMinX, cMinY, cMaxX, cMaxY, cClippedEdges) = cb.Value;
        var (rMinX, rMinY, rMaxX, rMaxY, rClippedEdges) = rb.Value;
        if (rClippedEdges != cClippedEdges) return cClippedEdges < rClippedEdges;

        var cFill = MathF.Max(
            (float)(cMaxX - cMinX + 1) / candidate.Width, (float)(cMaxY - cMinY + 1) / candidate.Height);
        var rFill = MathF.Max(
            (float)(rMaxX - rMinX + 1) / current.Width, (float)(rMaxY - rMinY + 1) / current.Height);
        return MathF.Abs(cFill - TargetFillFraction) < MathF.Abs(rFill - TargetFillFraction);
    }

    /// <summary>
    ///     Bounding box of drawn pixels, discarding sparse outlying fringes
    ///     (<see cref="OutlierTrimFraction" /> of the drawn pixels from each edge). Null when nothing
    ///     was drawn.
    /// </summary>
    private static (int MinX, int MinY, int MaxX, int MaxY, int ClippedEdges)? OpaqueBounds(
        byte[] bgra, int width, int height)
    {
        var columns = new int[width];
        var rows = new int[height];
        long total = 0;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (bgra[row + x * 4 + 3] == 0) continue;
                columns[x]++;
                rows[y]++;
                total++;
            }
        }

        if (total == 0) return null;

        var trim = (long)(total * OutlierTrimFraction);
        var (minX, maxX) = TrimmedRange(columns, trim);
        var (minY, maxY) = TrimmedRange(rows, trim);

        // Clipping is judged by DENSITY ON THE EDGE itself — how many drawn pixels sit in the very
        // first/last row and column — because both simpler definitions failed in opposite ways:
        //   - "any pixel touches the border" (raw bbox): a single orphan blob at the border made
        //     every zoom-in read as newly-clipped, so the refit that framed the room correctly was
        //     rejected as a regression and three of four angles stuck at their bloated frame.
        //   - "trimmed bbox touches the border": the 0.5% trim budget is thousands of pixels on a
        //     large render, enough to swallow a genuinely clipped sliver whole (Hoover NW ran 775
        //     pixels of wall off the left edge and still measured as unclipped).
        // Density separates the two directly: an orphan contributes tens of pixels to its edge
        // line, geometry running off the frame contributes hundreds of contiguous ones.
        var clipEdgeX = MathF.Max(EdgeClipMinPixels, height * EdgeClipFraction);
        var clipEdgeY = MathF.Max(EdgeClipMinPixels, width * EdgeClipFraction);
        var clippedEdges = (columns[0] >= clipEdgeX ? 1 : 0) +
                           (columns[width - 1] >= clipEdgeX ? 1 : 0) +
                           (rows[0] >= clipEdgeY ? 1 : 0) +
                           (rows[height - 1] >= clipEdgeY ? 1 : 0);

        return (minX, minY, maxX, maxY, clippedEdges);
    }

    /// <summary>Transparent border kept around the content by the save-time trim, in pixels.</summary>
    private const int TrimMarginPixels = 16;

    /// <summary>Manifest column header — shared by fresh writes and partial-run merges.</summary>
    private const string ManifestHeader =
        "file,kind,name,formid,cells,placements,width_px,height_px," +
        "world_min_x,world_max_x,world_min_y,world_max_y," +
        "settled,coverage,ref_drawn,ref_instances,attempts,seconds,angle";

    /// <summary>Extracts the <paramref name="index" />th field of a CSV row, quote-aware.</summary>
    private static string CsvField(string line, int index)
    {
        var field = 0;
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i <= line.Length; i++)
        {
            if (i < line.Length && line[i] == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (i < line.Length && (line[i] != ',' || inQuotes)) continue;

            if (field == index)
            {
                var raw = line[start..i].Trim();
                return raw.Length >= 2 && raw.StartsWith('"') && raw.EndsWith('"')
                    ? raw[1..^1].Replace("\"\"", "\"")
                    : raw;
            }

            field++;
            start = i + 1;
        }

        return "";
    }

    /// <summary>
    ///     Writes a per-subject report list, merging with an existing file on a partial
    ///     (filtered) run: entries for subjects PROCESSED this run — whether they rendered or
    ///     were gated — are replaced by this run's verdicts, everything else survives. Entries
    ///     are "Name" or "Name (details)"; the name is the merge key.
    /// </summary>
    private static async Task WriteOrMergeListAsync(
        string path,
        List<string> entries,
        bool partialRun,
        HashSet<string> processedNames,
        CancellationToken ct)
    {
        if (partialRun && File.Exists(path))
        {
            static string SubjectOf(string line)
            {
                var paren = line.IndexOf(" (", StringComparison.Ordinal);
                return (paren >= 0 ? line[..paren] : line).Trim();
            }

            var kept = (await File.ReadAllLinesAsync(path, ct))
                .Where(l => l.Trim().Length > 0 && !processedNames.Contains(SubjectOf(l)))
                .ToList();
            kept.AddRange(entries);
            if (kept.Count > 0)
            {
                await File.WriteAllLinesAsync(path, kept, ct);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if (entries.Count > 0)
        {
            await File.WriteAllLinesAsync(path, entries, ct);
        }
    }

    /// <summary>
    ///     Crops a BGRA buffer to the EXACT bounding box of its non-transparent pixels plus
    ///     <see cref="TrimMarginPixels" />. Scale is untouched — only dead margin is removed, so the
    ///     64px-figure reference stays valid. Exact bounds rather than the outlier-trimmed ones the
    ///     refit measures: a stray orphan pixel should stay visible near the edge, not be sliced
    ///     through mid-blob.
    /// </summary>
    private static (byte[] Bgra, int Width, int Height) CropToContent(
        byte[] bgra, int width, int height, int margin)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (bgra[row + x * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                maxY = y;
            }
        }

        if (maxX < 0) return (bgra, width, height); // nothing drawn — empties never reach the save

        minX = Math.Max(0, minX - margin);
        minY = Math.Max(0, minY - margin);
        maxX = Math.Min(width - 1, maxX + margin);
        maxY = Math.Min(height - 1, maxY + margin);
        var outW = maxX - minX + 1;
        var outH = maxY - minY + 1;
        if (outW == width && outH == height) return (bgra, width, height);

        var cropped = new byte[outW * outH * 4];
        for (var y = 0; y < outH; y++)
        {
            Buffer.BlockCopy(bgra, ((minY + y) * width + minX) * 4, cropped, y * outW * 4, outW * 4);
        }

        return (cropped, outW, outH);
    }

    /// <summary>
    ///     First and last index of <paramref name="counts" /> after discarding <paramref name="trim" />
    ///     accumulated weight from each end.
    /// </summary>
    private static (int Min, int Max) TrimmedRange(int[] counts, long trim)
    {
        int min = 0, max = counts.Length - 1;
        long acc = 0;
        while (min < max)
        {
            acc += counts[min];
            if (acc > trim) break;
            min++;
        }

        acc = 0;
        while (max > min)
        {
            acc += counts[max];
            if (acc > trim) break;
            max--;
        }

        return (min, max);
    }

    /// <summary>
    ///     Renders at the resolved size, halving the resolution up to twice when the renderer
    ///     returns nothing. A null from the provider is indistinguishable at this level between
    ///     "scene selection failed" (no size will help) and "offscreen target allocation failed"
    ///     (E_OUTOFMEMORY — a smaller target succeeds); retrying smaller converts the second from a
    ///     lost subject into a lower-resolution image, and costs two wasted attempts for the first.
    /// </summary>
    private async Task<(TopDownRender Render, int Attempts)?> RenderUntilSettledAsync(
        ITopDownSceneRenderer provider,
        TopDownCaptureSubject subject,
        float yawDegrees,
        Stopwatch deadline,
        CancellationToken ct)
    {
        var attempts = 0;
        for (var shrink = 0; shrink < 3; shrink++)
        {
            var result = await RenderUntilSettledAtScaleAsync(
                provider, subject, yawDegrees, shrink, deadline, ct);
            attempts += result?.Attempts ?? 2;
            if (result is { } ok)
            {
                return (ok.Render, attempts);
            }
        }

        return null;
    }

    private async Task<(TopDownRender Render, int Attempts)?> RenderUntilSettledAtScaleAsync(
        ITopDownSceneRenderer provider,
        TopDownCaptureSubject subject,
        float yawDegrees,
        int shrinkShift,
        Stopwatch deadline,
        CancellationToken ct)
    {
        var (pxW, pxH) = ResolvePixelSize(subject, yawDegrees);
        pxW = Math.Max(pxW >> shrinkShift, 16);
        pxH = Math.Max(pxH >> shrinkShift, 16);

        // Worldspace vs interior are mutually exclusive on the renderer: an interior ignores the
        // worldspace argument, and the unlinked-exterior set is addressed by a null worldspace.
        uint? worldspaceFormId = subject.Kind == TopDownSubjectKind.Worldspace ? subject.FormId : null;
        uint? interiorFormId = subject.Kind == TopDownSubjectKind.Interior ? subject.FormId : null;

        var budget = TimeSpan.FromSeconds(options.CaptureBatchTimeoutSeconds);
        TopDownRender? render = null;
        var attempts = 0;

        // IsComplete alone is not trusted: quiescence can report true in the window between one
        // stream stage finishing and the next being requested, yielding a "settled" frame with only
        // part of the scene drawn (observed as the same cell rendering 652 refs on one run and 724
        // on the next, both claiming complete). Two consecutive complete passes that agree on the
        // drawn count close that window — a mid-stream pass cannot repeat its count, because the
        // next pass draws what streamed in meanwhile.
        var lastCompleteDrawn = -1;

        while (deadline.Elapsed < budget)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;
            var pass = await provider.RenderTopDownAsync(
                subject.MinX, subject.MaxX, subject.MinY, subject.MaxY,
                pxW, pxH,
                showDisabled: true,
                // Driven by the subject's own water flag. Outdoors that is always true; indoors it
                // is the CELL's water bit, so only cells that actually declared water (flooded
                // vaults, sewers) get a plane. Rendering one unconditionally laid an opaque sheet
                // over every interior floor plan the ceiling clip had just exposed.
                showWater: subject.HasWater,
                worldspaceFormId,
                [],
                // Full-bright by default: these are inventory documents, and directional shading
                // fights legibility — interiors have no sun and half of every exterior mesh faces
                // away from a fixed noon light. --capture-batch-lit restores the lit path.
                enableLighting: !options.CaptureBatchFullBright,
                gameHour: 12f,
                interiorFormId,
                // Self-contained image: nothing composites underneath these PNGs, so terrain has to
                // be drawn in colour rather than depth-only.
                includeTerrainColor: true,
                options.CaptureBatchProjection,
                contentWorldZ: (subject.MinZ, subject.MaxZ),
                trimetricYawDegrees: yawDegrees,
                ct);

            if (pass is null)
            {
                // Null means the provider could not select the scene at all (unknown FormID) or the
                // record failed. Retrying a selection failure is pointless, so give it one grace
                // pass for a transient device hiccup and then give up on this subject.
                if (attempts >= 2) return null;
                await Task.Delay(AttemptDelayMilliseconds, ct);
                continue;
            }

            render = pass;
            if (pass.IsComplete && pass.ReferenceDrawn == lastCompleteDrawn) break;
            lastCompleteDrawn = pass.IsComplete ? pass.ReferenceDrawn : -1;
            await Task.Delay(AttemptDelayMilliseconds, ct);
        }

        return render is null ? null : (render, attempts);
    }

    /// <summary>Pixel dimensions for a subject at the configured fixed scale.</summary>
    private (int Width, int Height) ResolvePixelSize(TopDownCaptureSubject subject, float yawDegrees)
    {
        // Under a tilted camera the image is NOT the world rectangle: that rectangle projects to a
        // rotated, foreshortened parallelogram whose bounding box has a different shape — and a
        // different one per yaw, so each angle sizes its own image. Ask the projection what it will
        // actually frame.
        var (w, h) = options.CaptureBatchProjection == TopDownProjection.Trimetric
            ? TrimetricViewProjBuilder.MeasureFrame(
                subject.MinX, subject.MaxX, subject.MinY, subject.MaxY, subject.MinZ, subject.MaxZ,
                yawDegrees)
            : (subject.Width, subject.Height);
        if (!float.IsFinite(w) || !float.IsFinite(h) || w <= 0 || h <= 0)
        {
            return (512, 512);
        }

        // Fixed world-units-per-pixel: the image SIZE follows the content, rather than the content
        // being squeezed to fit a fixed image. Fitting to a fixed box made every subject render at a
        // different, unstated scale and letterboxed long thin ones (sewer corridors came out as
        // ~1024×40 strips with the geometry unreadable and clipped).
        var unitsPerPixel = TrimetricViewProjBuilder.WorldUnitsPerPixelAtUnitScale /
                            MathF.Max(options.CaptureBatchScale, 0.01f);
        var pxW = (int)MathF.Ceiling(w / unitsPerPixel);
        var pxH = (int)MathF.Ceiling(h / unitsPerPixel);

        // Cap: a whole worldspace at the reference scale is tens of thousands of pixels. Scale both
        // axes by the same factor so the aspect — and therefore the geometry — is never distorted.
        var cap = Math.Max(options.CaptureBatchMaxPixels, 64);
        var longest = Math.Max(pxW, pxH);
        if (longest > cap)
        {
            var shrink = (float)cap / longest;
            pxW = (int)MathF.Round(pxW * shrink);
            pxH = (int)MathF.Round(pxH * shrink);
        }

        return (Math.Max(pxW, 16), Math.Max(pxH, 16));
    }

    private string Prefix() =>
        string.IsNullOrEmpty(options.CaptureNamePrefix) ? "" : options.CaptureNamePrefix + "_";

    /// <summary>
    ///     Output filename for one subject at one angle. The single-angle set uses an empty angle
    ///     name, keeping the un-suffixed filenames earlier corpus runs produced so
    ///     <c>--capture-batch-resume</c> still recognises them.
    /// </summary>
    private string BuildFileName(TopDownCaptureSubject subject, string angleName) =>
        angleName.Length == 0
            ? $"{Prefix()}{SanitizeFileNameComponent(subject.Name)}.png"
            : $"{Prefix()}{SanitizeFileNameComponent(subject.Name)}_{angleName}.png";

    /// <summary>
    ///     Makes an EditorID safe as a filename component. EditorIDs are authored strings and a few
    ///     carry characters Windows rejects in paths, which would otherwise abort the subject with
    ///     an IO exception rather than produce a usable file.
    /// </summary>
    internal static string SanitizeFileNameComponent(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unnamed";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c);
        }

        return sb.ToString();
    }

    private static string Csv(string value) =>
        value.Contains(',', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    private static double Coverage(byte[] bgra)
    {
        if (bgra.Length < 4) return 0;
        var total = bgra.Length / 4;
        long opaque = 0;
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] > 0) opaque++;
        }

        return (double)opaque / total;
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

    /// <summary>
    ///     Result of capturing one subject. <paramref name="Empty" /> means the settled render drew
    ///     nothing at all — no PNG and no manifest row, but the subject is still accounted for.
    /// </summary>
    private sealed record SubjectOutcome(
        bool Settled, string ManifestRow, string ConsoleLine, bool Empty = false)
    {
        internal static SubjectOutcome EmptySubject { get; } = new(true, "", "", true);
    }
}

/// <summary>Totals from one <see cref="TopDownBatchCapture" /> run.</summary>
/// <param name="Written">PNGs written this run.</param>
/// <param name="Skipped">Subjects skipped because their PNG already existed (resume mode).</param>
/// <param name="Unsettled">Of <paramref name="Written" />, how many hit the timeout before converging.</param>
/// <param name="Failed">Subjects that produced no image.</param>
/// <param name="AbortReason">Non-null when the run could not start at all.</param>
internal readonly record struct TopDownBatchResult(
    int Written,
    int Skipped,
    int Unsettled,
    int Failed,
    string? AbortReason);
