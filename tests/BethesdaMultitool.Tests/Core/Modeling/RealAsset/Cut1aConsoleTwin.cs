namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The PC twin of one big-endian cut-1a cover file (<c>cut1a-console-twins.json</c>): the PC file at the same
///     Data-relative path, pinned by SHA-256 and size, and the console the manifest source names.
/// </summary>
/// <param name="ConsoleSha256">The console file's manifest SHA-256 (the key).</param>
/// <param name="Entry">The console file's manifest entry.</param>
/// <param name="Platform">The console platform the probe derives from the manifest source (<c>x360</c> or <c>ps3</c>).</param>
/// <param name="PcPath">The Data-relative path of the PC twin.</param>
/// <param name="PcSha256">The pinned lowercase SHA-256 of the PC twin's bytes.</param>
/// <param name="PcSize">The pinned byte length of the PC twin.</param>
/// <param name="PcLayer">The layer of the Data folder that held the twin when it was pinned (a loose file or an archive name).</param>
/// <param name="PcStep">
///     The Data folder that held the twin when it was pinned: <c>steamFinalBuild</c> (the Steam Final build's Data
///     folder) or <c>steamInstall</c> (the installed Steam Fallout New Vegas Data folder), the step names
///     <c>Cut1aConsoleTwins</c> resolves through.
/// </param>
internal sealed record Cut1aConsoleTwin(string ConsoleSha256, string Entry, string Platform, string PcPath,
    string PcSha256, long PcSize, string PcLayer, string PcStep);
