namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>The processing applied to each explicitly selected asset.</summary>
public enum AssetExportMode
{
    /// <summary>Write the original decoded payload without conversion.</summary>
    Original,
    /// <summary>Convert DDX texture payloads through the existing DDXConv implementation.</summary>
    DdxToDds
}
