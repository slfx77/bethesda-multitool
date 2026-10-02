namespace BethesdaMultitool.Core.Formats.Dialogue.CreationKit;

/// <summary>A Creation Kit dialogue diagram, kept separate from authoritative plugin dialogue.</summary>
/// <param name="Source">The file or ZIP-entry identity.</param>
/// <param name="Version">The authored diagram version.</param>
/// <param name="Nodes">The diagram nodes.</param>
/// <param name="Links">The diagram edges.</param>
/// <param name="OriginalXml">The complete loaded XML, including properties not interpreted by this reader.</param>
public sealed record DialogueViewDocument(string Source, string Version, IReadOnlyList<DialogueViewNode> Nodes,
    IReadOnlyList<DialogueViewLink> Links, string OriginalXml);
