namespace BethesdaMultitool.Core.Actors;

/// <summary>Explains an engine-dependent value deliberately not inferred from static records.</summary>
internal sealed record ActorCalculatedStatistic(string Key, string Status, string Reason);
