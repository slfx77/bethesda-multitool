namespace BethesdaMultitool.Core.Actors;

/// <summary>Whether a named master was supplied, in the input plugin's MAST order.</summary>
internal sealed record ActorMasterStatus(string Name, bool Loaded);
