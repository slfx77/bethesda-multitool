using System.CommandLine;
using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.SpeedTree;

namespace EsmAnalyzer.Commands;

/// <summary>
///     <c>spt meshstats</c>: build one <c>.spt</c> (no render) and print the geometry metrics the engine
///     oracles are judged by — bark bounds/tri count, leaf-card count, leaf-center z-distribution as a
///     fraction of total height, and leaf-vs-bark radial extents. Comparisons against OBSE
///     <c>ExportMesh</c> dumps are RATIO-based (height-normalized): the builder rescales to
///     TREE OBND/billboard height, so absolutes are meaningless.
/// </summary>
internal static class SpeedTreeMeshStatsCommand
{
    public static Command Create()
    {
        var command = new Command("meshstats",
            "Build a .spt (no render) and print bark/leaf geometry metrics (z-distribution, extents) for oracle diffs");
        var fileArg = new Argument<string>("file") { Description = "Path to the .spt file (or BSA-internal path with --bsa)" };
        var bsaOption = new Option<string?>("--bsa")
        { Description = "Read <file> from this BSA archive instead of disk" };
        var esmOption = new Option<string?>("--esm")
        { Description = "ESM to source per-tree TREE.SNAM seed + OBND/BNAM height (matches the viewer build)" };
        var csvOption = new Option<string?>("--csv")
        { Description = "Write per-leaf-card centers (x,y,z) to this CSV" };
        var lowestOption = new Option<int>("--lowest")
        { Description = "Print the N lowest leaf-card centers", DefaultValueFactory = _ => 0 };
        command.Arguments.Add(fileArg);
        command.Options.Add(bsaOption);
        command.Options.Add(esmOption);
        command.Options.Add(csvOption);
        command.Options.Add(lowestOption);
        command.SetAction(parseResult => Run(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(bsaOption),
            parseResult.GetValue(esmOption),
            parseResult.GetValue(csvOption),
            parseResult.GetValue(lowestOption)));
        return command;
    }

    private static int Run(string sptPath, string? bsa, string? esmPath, string? csvPath, int lowest)
    {
        var bytes = SpeedTreeSptIo.LoadSptBytes(sptPath, bsa);
        if (bytes is null)
        {
            Console.Error.WriteLine($"File not found: {sptPath}");
            return 1;
        }

        SptModel model;
        try
        {
            model = SptFile.Parse(bytes);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"Not a valid .spt file: {ex.Message}");
            return 1;
        }

        TreeMetadata? treeMeta = null;
        if (!string.IsNullOrWhiteSpace(esmPath))
        {
            var map = SpeedTreeMetadata.BuildTreeMetadataMap(esmPath);
            treeMeta = SpeedTreeMetadata.ResolveTreeMetadata(map, sptPath);
        }

        var opt = SptGeometryOptions.FromEnvironment() with
        {
            TargetHeight = treeMeta?.TargetHeight,
        };
        var seed = treeMeta?.Seed ?? model.General.Token2005;
        var renderable = SptGeometryBuilder.Build(model, seed, opt);

        var ci = CultureInfo.InvariantCulture;
        Console.WriteLine(string.Create(ci,
            $"=== {Path.GetFileName(sptPath)} seed={seed} targetHeight={(treeMeta?.TargetHeight is { } th ? th.ToString("F1", ci) : "(none)")} ==="));

        var barkMin = new Vector3(float.MaxValue);
        var barkMax = new Vector3(float.MinValue);
        var barkTris = 0;
        var centers = new List<Vector3>();
        foreach (var sub in renderable.Submeshes)
        {
            var tris = sub.Triangles.Length / 3;
            Console.WriteLine(string.Create(ci, $"  submesh {sub.ShapeName,-12} verts={sub.Positions.Length / 3,-6} tris={tris}"));
            if (sub.ShapeName == "spt:bark")
            {
                barkTris += tris;
                for (var i = 0; i + 2 < sub.Positions.Length; i += 3)
                {
                    var p = new Vector3(sub.Positions[i], sub.Positions[i + 1], sub.Positions[i + 2]);
                    barkMin = Vector3.Min(barkMin, p);
                    barkMax = Vector3.Max(barkMax, p);
                }
            }
            else if (sub.ShapeName == "spt:leaves")
            {
                // Leaf cards are emitted as sequential quads (4 verts, 2 tris). The card CENTER is the mean
                // of its 4 corners — the value comparable to a dump NIF's 4-coincident-vert card centers.
                for (var card = 0; card * 6 + 5 < sub.Triangles.Length; card++)
                {
                    var unique = new HashSet<int>();
                    for (var k = 0; k < 6; k++)
                    {
                        unique.Add(sub.Triangles[card * 6 + k]);
                    }

                    var c = Vector3.Zero;
                    foreach (var vi in unique)
                    {
                        c += new Vector3(sub.Positions[vi * 3], sub.Positions[vi * 3 + 1], sub.Positions[vi * 3 + 2]);
                    }

                    centers.Add(c / unique.Count);
                }
            }
        }

        var totalMinZ = renderable.MinZ;
        var totalMaxZ = renderable.MaxZ;
        var height = totalMaxZ - totalMinZ;
        Console.WriteLine(string.Create(ci,
            $"  model bounds  x[{renderable.MinX:F1},{renderable.MaxX:F1}] y[{renderable.MinY:F1},{renderable.MaxY:F1}] z[{totalMinZ:F1},{totalMaxZ:F1}] height={height:F1}"));
        Console.WriteLine(string.Create(ci,
            $"  bark          tris={barkTris} x[{barkMin.X:F1},{barkMax.X:F1}] y[{barkMin.Y:F1},{barkMax.Y:F1}] z[{barkMin.Z:F1},{barkMax.Z:F1}]"));

        if (centers.Count > 0 && height > 0f)
        {
            var zs = centers.Select(c => c.Z).OrderBy(z => z).ToArray();
            var xAbs = centers.Max(c => MathF.Max(MathF.Abs(c.X), MathF.Abs(c.Y)));
            var barkAbs = MathF.Max(
                MathF.Max(MathF.Abs(barkMin.X), MathF.Abs(barkMax.X)),
                MathF.Max(MathF.Abs(barkMin.Y), MathF.Abs(barkMax.Y)));
            Console.WriteLine(string.Create(ci,
                $"  leaf cards    count={centers.Count} centerZ[{zs[0]:F1},{zs[^1]:F1}] " +
                $"p5={Pct(zs, 0.05f):F1} p50={Pct(zs, 0.50f):F1} p95={Pct(zs, 0.95f):F1}"));
            Console.WriteLine(string.Create(ci,
                $"  leaf z floor  = {(zs[0] - totalMinZ) / height:F3} of height; radial |xy| max leaf={xAbs:F1} bark={barkAbs:F1} ratio={(barkAbs > 0 ? xAbs / barkAbs : 0):F2}"));

            var bands = new int[10];
            foreach (var z in zs)
            {
                var f = Math.Clamp((z - totalMinZ) / height, 0f, 0.9999f);
                bands[(int)(f * 10)]++;
            }

            Console.WriteLine("  z-band histogram (bottom→top, fraction of height):");
            for (var b = 0; b < 10; b++)
            {
                Console.WriteLine(string.Create(ci,
                    $"    [{b * 10,2}-{b * 10 + 10,3}%] {bands[b],5}  {new string('#', Math.Min(60, bands[b] * 60 / Math.Max(1, centers.Count)))}"));
            }

            if (lowest > 0)
            {
                Console.WriteLine($"  lowest {Math.Min(lowest, centers.Count)} leaf centers:");
                foreach (var c in centers.OrderBy(c => c.Z).Take(lowest))
                {
                    Console.WriteLine(string.Create(ci, $"    ({c.X:F1}, {c.Y:F1}, {c.Z:F1})  zFrac={(c.Z - totalMinZ) / height:F3}"));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(csvPath) && centers.Count > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);
            using var w = new StreamWriter(csvPath);
            w.WriteLine("x,y,z");
            foreach (var c in centers)
            {
                w.WriteLine(string.Create(ci, $"{c.X},{c.Y},{c.Z}"));
            }

            Console.WriteLine($"  wrote {centers.Count} centers to {csvPath}");
        }

        return 0;
    }

    private static float Pct(float[] sorted, float p) =>
        sorted[Math.Clamp((int)(p * sorted.Length), 0, sorted.Length - 1)];
}
