namespace BethesdaMultitool.Core.Actors;

/// <summary>An authored actor field with an invariant key and optional record reference.</summary>
internal sealed record ActorStatistic(string Key, string Value, uint? Reference = null);
