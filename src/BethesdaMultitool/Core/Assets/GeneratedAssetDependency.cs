using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Assets;

/// <summary>Inputs actually used to generate cached pixels; consuming records remain scene-local.</summary>
internal sealed record GeneratedAssetDependency(string Recipe, ImmutableArray<string> Inputs,
    bool UsesFaceGenCoefficients = false, ImmutableArray<GeneratedAssetInput> ObservedInputs = default);

internal sealed record GeneratedAssetInput(string RequestedPath, ImmutableArray<AssetSelectionReceipt> Receipts);
