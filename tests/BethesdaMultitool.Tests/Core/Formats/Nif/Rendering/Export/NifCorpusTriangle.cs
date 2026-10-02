using System.Numerics;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>One encoded draw occurrence with an oriented local-content identity and its bind-world corners.</summary>
/// <param name="Identity">Material and cyclically canonical local vertex attributes.</param>
/// <param name="World">Three world positions in the same cyclic order, preserving winding.</param>
internal sealed record NifCorpusTriangle(string Identity, Vector3[] World);
