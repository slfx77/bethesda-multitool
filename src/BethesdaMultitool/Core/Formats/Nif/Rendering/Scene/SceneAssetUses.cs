using System.Collections.Immutable;
using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

/// <summary>Edges follow explicit parsed/assembled mesh paths and slots, never filename guesses.</summary>
internal static class SceneAssetUses
{
    internal static AssetUseGraph WithResolvedTextureReads(AssetUseGraph graph, NifTextureResolver resolver,
        IEnumerable<AssetSelectionReceipt> sceneReceipts)
    {
        var observed = sceneReceipts.Select(AssetUseRead.From).Select(r => r.ReceiptId).ToHashSet(StringComparer.Ordinal);
        var bindings = graph.Bindings.ToDictionary(b => b.UseId);
        foreach (var node in graph.Nodes)
        {
            if (node.RequestedPath is not { } path) continue;
            if (bindings.TryGetValue(node.Id, out var explicitBinding) && explicitBinding.Basis == "component-read-scope") continue;
            var reads = resolver.ObservedReads(path).Select(AssetUseRead.From).Distinct().ToImmutableArray();
            // A previous actor's resolver entry is not evidence for this scene. Warm reads are
            // admitted only when ObserveCached replayed their receipts into this scene's scope.
            if (reads.IsEmpty || reads.Any(r => !observed.Contains(r.ReceiptId))) continue;
            bindings[node.Id] = new(node.Id, reads, "component-read-scope");
        }
        return graph with { Bindings = bindings.Values.ToImmutableArray() };
    }

    internal static AssetUseGraph WithGeneratedInputs(AssetUseGraph graph, NifTextureResolver resolver)
    {
        var nodes = graph.Nodes.ToBuilder();
        var bindings = graph.Bindings.ToDictionary(b => b.UseId);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in graph.Nodes.Where(n => n.Relation != "generation-input")
            .Select(n => n.RequestedPath).OfType<string>().ToArray()) Add(path, 0);
        var parentsChanged = false;
        for (var i = 0; i < nodes.Count; i++)
        {
            var consumer = nodes[i];
            if (consumer.Relation != "assembled-mesh-slot" || consumer.RequestedPath is not { } path) continue;
            var generated = nodes.FirstOrDefault(n => n.Relation == "generated-texture" && SamePath(n.RequestedPath, path));
            if (generated is null || consumer.Parents.Contains(generated.Id) || DependsOn(generated.Id, consumer.Id)) continue;
            nodes[i] = consumer with { Parents = consumer.Parents.Add(generated.Id) };
            parentsChanged = true;
        }
        var unchanged = !parentsChanged && nodes.Count == graph.Nodes.Length && bindings.Count == graph.Bindings.Length &&
            graph.Bindings.All(previous => bindings.TryGetValue(previous.UseId, out var current) &&
                previous.Basis == current.Basis && previous.Reads.SequenceEqual(current.Reads));
        return unchanged ? graph : new(nodes.ToImmutable(), bindings.Values.ToImmutableArray());

        static bool SamePath(string? left, string right) => left is not null &&
            string.Equals(NifTexturePathUtility.Normalize(left), NifTexturePathUtility.Normalize(right), StringComparison.OrdinalIgnoreCase);

        bool DependsOn(string from, string target)
        {
            var pending = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            pending.Push(from);
            while (pending.TryPop(out var id))
            {
                if (id == target) return true;
                if (!seen.Add(id)) continue;
                var node = nodes.FirstOrDefault(n => n.Id == id);
                if (node is not null)
                    foreach (var parent in node.Parents) pending.Push(parent);
            }
            return false;
        }

        bool CanFollowGeneratedInput(string dependency, string output)
        {
            var pending = new Stack<(string Path, int Depth)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push((dependency, 0));
            while (pending.TryPop(out var current))
            {
                var path = NifTexturePathUtility.Normalize(current.Path);
                if (string.Equals(path, output, StringComparison.OrdinalIgnoreCase)) return false;
                if (!seen.Add(path)) continue;
                if (resolver.GeneratedInput(current.Path) is not { } generated) continue;
                // A retained path cycle has no generation-version identity. Keep its
                // observed input leaves instead of choosing an order from traversal.
                if (current.Depth >= 16) return false;
                foreach (var input in generated.Inputs) pending.Push((input, current.Depth + 1));
            }
            return true;
        }

        void Add(string path, int depth)
        {
            var normalized = NifTexturePathUtility.Normalize(path);
            if (depth >= 16 || !visited.Add(normalized) || resolver.GeneratedInput(path) is not { } input) return;
            active.Add(normalized);
            var parents = new List<string>();
            foreach (var dependency in input.Inputs)
            {
                var followGenerated = CanFollowGeneratedInput(dependency, normalized);
                if (followGenerated) Add(dependency, depth + 1);
                // Reused paths describe the bytes consumed before injection. They must not
                // resolve back to this output, nor through an assembled consumer of it.
                var produced = !followGenerated || active.Contains(NifTexturePathUtility.Normalize(dependency)) ? null :
                    nodes.FirstOrDefault(n => n.Relation == "generated-texture" && SamePath(n.RequestedPath, dependency));
                string[] matches = produced is not null ? [produced.Id] : nodes.Where(n =>
                    n.Relation is not ("generated-texture" or "assembled-mesh-slot" or "observed-material-resolution") &&
                    SamePath(n.RequestedPath, dependency)).Select(n => n.Id).ToArray();
                if (matches.Length == 0)
                {
                    var id = $"use-{nodes.Count + 1}";
                    nodes.Add(new(id, null, "generated-texture-input", "input", dependency,
                        "generation-input", []));
                    matches = [id];
                }
                parents.AddRange(matches);
                if (!input.ObservedInputs.IsDefault)
                {
                    var reads = input.ObservedInputs.Where(observed => SamePath(observed.RequestedPath, dependency))
                        .SelectMany(observed => observed.Receipts).Select(AssetUseRead.From).Distinct().ToImmutableArray();
                    if (!reads.IsEmpty)
                        foreach (var id in matches)
                            bindings[id] = new(id, (bindings.GetValueOrDefault(id)?.Reads ?? []).Concat(reads).Distinct().ToImmutableArray(), "component-read-scope");
                }
            }
            if (input.UsesFaceGenCoefficients)
                parents.AddRange(nodes.Where(n => n.Component == "facegen-texture-coefficients").Select(n => n.Id));
            if (!nodes.Any(n => n.Relation == "generated-texture" && SamePath(n.RequestedPath, path)))
                nodes.Add(new($"use-{nodes.Count + 1}", null, "generated-texture", input.Recipe,
                    path, "generated-texture", parents.Distinct().ToImmutableArray()));
            active.Remove(normalized);
        }
    }

    internal static AssetUseGraph WithMeshes(AssetUseGraph graph, IEnumerable<RenderableSubmesh> meshes)
    {
        var nodes = graph.Nodes.ToBuilder();
        foreach (var mesh in meshes)
        {
            if (mesh.SourceNifPath is not { Length: > 0 } source) continue;
            var parents = graph.Nodes.Where(n => n.RequestedPath is not null &&
                string.Equals(AssetUseGraph.Normalize(n.RequestedPath), AssetUseGraph.Normalize(source), StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Id).ToImmutableArray();
            if (parents.IsEmpty) continue;
            foreach (var (slot, path) in new[]
            {
                ("DiffuseTexturePath", mesh.DiffuseTexturePath),
                ("NormalMapTexturePath", mesh.NormalMapTexturePath),
                ("SpecularMapTexturePath", mesh.SpecularMapTexturePath),
                ("GradientMapTexturePath", mesh.GradientMapTexturePath),
                ("BgsmGlowMapTexturePath", mesh.BgsmGlowMapTexturePath),
                ("EnvironmentMapTexturePath", mesh.EnvironmentMapTexturePath),
                ("ClassicEnvironmentMapTexturePath", mesh.ClassicEnvironmentMapTexturePath),
                ("ClassicEnvironmentMaskTexturePath", mesh.ClassicEnvironmentMaskTexturePath),
                ("ClassicParallaxHeightMapTexturePath", mesh.ClassicParallaxHeightMapTexturePath),
                ("Lighting30GlowMapTexturePath", mesh.Lighting30GlowMapTexturePath),
                ("MaterialPath", mesh.ShaderMetadata?.MaterialPath)
            })
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var block = mesh.SourceBlockIndex >= 0 ? (int?)mesh.SourceBlockIndex : null;
                // A texture override remains an assembled-slot edge. A separate typed-record node
                // carries its direct owner, avoiding a false assertion that it was stored in the NIF.
                var relation = "assembled-mesh-slot";
                var generatedParents = nodes.Where(n => n.Relation == "generated-texture" && n.RequestedPath is not null &&
                    string.Equals(NifTexturePathUtility.Normalize(n.RequestedPath), NifTexturePathUtility.Normalize(path), StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
                if (nodes.Any(n => n.Relation == relation && n.Field == slot && n.RequestedPath == path && n.NifBlockIndex == block &&
                    n.Parents.Where(id => !generatedParents.Contains(id)).SequenceEqual(parents))) continue;
                nodes.Add(new($"use-{nodes.Count + 1}", null, mesh.ShapeName ?? "mesh", slot, path, relation, parents, block));
            }
        }
        return graph with { Nodes = nodes.ToImmutable() };
    }

    internal static AssetUseGraph WithObservedMaterialReads(AssetUseGraph graph, RenderableSubmesh mesh,
        IEnumerable<AssetSelectionReceipt> receipts)
    {
        if (mesh.SourceNifPath is not { Length: > 0 } source) return graph;
        var parents = graph.Nodes.Where(n => n.RequestedPath is not null &&
            string.Equals(AssetUseGraph.Normalize(n.RequestedPath), AssetUseGraph.Normalize(source), StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Id).ToImmutableArray();
        if (parents.IsEmpty) return graph;
        var nodes = graph.Nodes.ToBuilder();
        var bindings = graph.Bindings.ToBuilder();
        foreach (var receipt in receipts.Distinct())
        {
            var id = $"use-{nodes.Count + 1}";
            nodes.Add(new(id, null, mesh.ShapeName ?? "mesh", "material/texture-read", receipt.RequestedPath,
                "observed-material-resolution", parents, mesh.SourceBlockIndex >= 0 ? mesh.SourceBlockIndex : null));
            bindings.Add(new(id, [AssetUseRead.From(receipt)], "component-read-scope"));
        }
        return new(nodes.ToImmutable(), bindings.ToImmutable());
    }
}
