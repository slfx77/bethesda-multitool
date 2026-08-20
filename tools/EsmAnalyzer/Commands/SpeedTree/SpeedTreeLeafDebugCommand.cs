using System.CommandLine;
using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.SpeedTree;

namespace EsmAnalyzer.Commands.SpeedTree;

/// <summary>
///     <c>spt leaf-debug</c>: isolate a single leaf-card template's atlas region so UV bugs are obvious.
///     For each leaf template (or one <c>--index</c>) it crops the resolved leaf atlas to that template's
///     token-10002 UV rectangle TWICE — once verbatim and once with V flipped (<c>v → 1−v</c>) — and lays
///     the two crops side by side on a magenta field (so transparent pixels read as magenta and the leaf
///     silhouette is unmistakable). Whichever column shows a clean SINGLE leaf cluster is the orientation
///     the renderer must use; a column showing two half-clusters or a hard rectangular crop edge is the bug.
/// </summary>
internal static class SpeedTreeLeafDebugCommand
{
    public static Command Create()
    {
        var command = new Command("leaf-debug",
            "Crop each leaf template's atlas UV region (raw vs V-flipped) to a side-by-side PNG for UV diagnosis");
        var fileArg = new Argument<string>("file") { Description = "Path to the .spt file" };
        var outOption = new Option<string>("-o", "--output") { Description = "Output PNG path", Required = true };
        var dataOption = new Option<string>("--data")
        {
            Description = "Texture source dir or BSA (resolves textures\\trees\\...).",
            DefaultValueFactory = _ => "Sample/Unpacked_Builds/PC_Final_Unpacked/Data"
        };
        var bsaOption = new Option<string?>("--bsa")
            { Description = "Read <file> from this BSA archive instead of disk" };
        var esmOption = new Option<string?>("--esm")
            { Description = "ESM to source TREE.ICON (the engine's authoritative leaf atlas)." };
        var leafTexOption = new Option<string?>("--leaf-texture")
            { Description = "Override the leaf atlas (bare name → textures\\trees\\leaves\\..., or a full path)." };
        var indexOption = new Option<int?>("--index")
            { Description = "Render only this leaf template index (default: all templates)." };
        var cellOption = new Option<int>("--cell")
            { Description = "Cell size in px per crop", DefaultValueFactory = _ => 192 };
        command.Arguments.Add(fileArg);
        command.Options.Add(outOption);
        command.Options.Add(dataOption);
        command.Options.Add(bsaOption);
        command.Options.Add(esmOption);
        command.Options.Add(leafTexOption);
        command.Options.Add(indexOption);
        command.Options.Add(cellOption);
        command.SetAction(parseResult => Run(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(outOption)!,
            parseResult.GetValue(dataOption)!,
            parseResult.GetValue(bsaOption),
            parseResult.GetValue(esmOption),
            parseResult.GetValue(leafTexOption),
            parseResult.GetValue(indexOption),
            parseResult.GetValue(cellOption)));
        return command;
    }

    private static int Run(string sptPath, string outPng, string dataSource, string? bsa, string? esmPath,
        string? leafTexture, int? index, int cell)
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

        if (model.Leaves.Count == 0)
        {
            Console.Error.WriteLine("This .spt has no leaf templates.");
            return 1;
        }

        // Resolve the atlas exactly like `spt render`: TREE.ICON override (engine-authoritative) wins, else
        // the .spt's first leaf material routed through the leaf-kind path.
        var leafOverride = leafTexture is not null
            ? SpeedTreeTexturePath.IconToLeafPath(leafTexture)
            : ResolveIconFromEsm(esmPath, sptPath);
        var atlasPath = leafOverride
                        ?? SpeedTreeTexturePath.ToGamePath(model.Leaves[0].Material, SpeedTreeTextureKind.Leaf);
        if (atlasPath is null)
        {
            Console.Error.WriteLine("Could not resolve a leaf atlas path.");
            return 1;
        }

        using var resolver = new NifTextureResolver(dataSource);
        var atlas = resolver.GetTexture(atlasPath);
        if (atlas is null)
        {
            Console.Error.WriteLine($"TEX MISS: {atlasPath}");
            return 1;
        }

        Console.WriteLine($"Atlas: {atlasPath} ({atlas.Width}x{atlas.Height})");

        var indices = index is { } i
            ? i >= 0 && i < model.Leaves.Count ? [i] : Array.Empty<int>()
            : Enumerable.Range(0, model.Leaves.Count).ToArray();
        if (indices.Length == 0)
        {
            Console.Error.WriteLine($"Leaf index {index} out of range (0..{model.Leaves.Count - 1}).");
            return 1;
        }

        // Layout: one row per template, two columns [RAW | FLIPPED-V]. Magenta gutters/background.
        const int pad = 8;
        var cols = 2;
        var width = pad + cols * (cell + pad);
        var height = pad + indices.Length * (cell + pad);
        var canvas = NewCanvas(width, height, 255, 0, 255);

        for (var row = 0; row < indices.Length; row++)
        {
            var t = indices[row];
            var uv = t < model.LeafTextureCoords.Count ? model.LeafTextureCoords[t] : SptLeafTextureCoords.FullAtlas;
            var rawCorners = new[] { uv[0], uv[1], uv[2], uv[3] };
            var flipCorners = rawCorners.Select(c => new Vector2(c.X, 1f - c.Y)).ToArray();

            var (ru0, rv0, ru1, rv1) = BoundingBox(rawCorners);
            var (fu0, fv0, fu1, fv1) = BoundingBox(flipCorners);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  leaf[{t}] type={model.Leaves[t].Type} mat={ShortMat(model.Leaves[t].Material)} " +
                $"uv=[({uv[0].X:F3},{uv[0].Y:F3}) ({uv[1].X:F3},{uv[1].Y:F3}) ({uv[2].X:F3},{uv[2].Y:F3}) ({uv[3].X:F3},{uv[3].Y:F3})] " +
                $"rawBox=u[{ru0:F2},{ru1:F2}]v[{rv0:F2},{rv1:F2}]"));

            var y = pad + row * (cell + pad);
            BlitCrop(canvas, width, atlas, ru0, rv0, ru1, rv1, pad, y, cell);
            BlitCrop(canvas, width, atlas, fu0, fv0, fu1, fv1, pad + cell + pad, y, cell);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
        PngWriter.SaveRgba(canvas, width, height, outPng);
        Console.WriteLine(
            $"Saved {outPng} ({width}x{height}) — columns: [RAW | FLIPPED-V], one row per leaf template.");
        return 0;
    }

    private static string? ResolveIconFromEsm(string? esmPath, string sptPath)
    {
        if (string.IsNullOrWhiteSpace(esmPath))
        {
            return null;
        }

        var map = SpeedTreeMetadata.BuildTreeMetadataMap(esmPath);
        return SpeedTreeMetadata.ResolveTreeMetadata(map, sptPath)?.LeafTexture;
    }

    private static (float U0, float V0, float U1, float V1) BoundingBox(IReadOnlyList<Vector2> corners)
    {
        float u0 = 1f, v0 = 1f, u1 = 0f, v1 = 0f;
        foreach (var c in corners)
        {
            u0 = MathF.Min(u0, c.X);
            v0 = MathF.Min(v0, c.Y);
            u1 = MathF.Max(u1, c.X);
            v1 = MathF.Max(v1, c.Y);
        }

        return (Math.Clamp(u0, 0f, 1f), Math.Clamp(v0, 0f, 1f), Math.Clamp(u1, 0f, 1f), Math.Clamp(v1, 0f, 1f));
    }

    private static byte[] NewCanvas(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = r;
            px[i + 1] = g;
            px[i + 2] = b;
            px[i + 3] = 255;
        }

        return px;
    }

    /// <summary>
    ///     Nearest-neighbour blit of the atlas sub-rectangle [u0,v0]-[u1,v1] into a <paramref name="cell" />-
    ///     sized box at (<paramref name="dx" />,<paramref name="dy" />), alpha-compositing over the magenta canvas so
    ///     the leaf cutout shows through.
    /// </summary>
    private static void BlitCrop(byte[] canvas, int canvasW, DecodedTexture atlas,
        float u0, float v0, float u1, float v1, int dx, int dy, int cell)
    {
        var sx0 = u0 * atlas.Width;
        var sy0 = v0 * atlas.Height;
        var sw = MathF.Max(1f, (u1 - u0) * atlas.Width);
        var sh = MathF.Max(1f, (v1 - v0) * atlas.Height);

        for (var py = 0; py < cell; py++)
        {
            for (var px = 0; px < cell; px++)
            {
                var sx = (int)(sx0 + (px + 0.5f) / cell * sw);
                var sy = (int)(sy0 + (py + 0.5f) / cell * sh);
                if ((uint)sx >= (uint)atlas.Width || (uint)sy >= (uint)atlas.Height)
                {
                    continue;
                }

                var si = (sy * atlas.Width + sx) * 4;
                var a = atlas.Pixels[si + 3] / 255f;
                var di = ((dy + py) * canvasW + dx + px) * 4;
                canvas[di] = (byte)(atlas.Pixels[si] * a + canvas[di] * (1 - a));
                canvas[di + 1] = (byte)(atlas.Pixels[si + 1] * a + canvas[di + 1] * (1 - a));
                canvas[di + 2] = (byte)(atlas.Pixels[si + 2] * a + canvas[di + 2] * (1 - a));
            }
        }
    }

    private static string ShortMat(string? material)
    {
        if (string.IsNullOrEmpty(material))
        {
            return "(none)";
        }

        var slash = material.Replace('/', '\\').LastIndexOf('\\');
        return slash >= 0 ? material[(slash + 1)..] : material;
    }
}