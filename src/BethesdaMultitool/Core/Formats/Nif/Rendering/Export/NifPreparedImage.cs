namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Owns one encoded image produced by the existing Bethesda texture preparation policy.</summary>
/// <param name="Name">The same derived image label used by the legacy exporter.</param>
/// <param name="Png">The owned PNG bytes; consumers retain or snapshot them without modifying them.</param>
internal sealed record NifPreparedImage(string Name, byte[] Png);
