namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

/// <summary>
///     Immutable view of the weather-to-weather transition state owned by the runtime
///     <c>Sky</c> singleton. A null current or outgoing weather preserves the engine pointer
///     state; callers must not substitute the default weather in this contract. The recovered
///     FNV <c>Sky</c> layout does not expose the elapsed clock used by animatable weather IMADs,
///     so <see cref="ModifierElapsedSeconds" /> is explicitly null until that clock is recovered.
///     Weather IDs are the live <c>TESForm::formID</c> values read from the pointed objects—the same
///     raw key domain used by DMP runtime-record enumeration. Supplementary plugin lookup is valid
///     only when its configured load order matches the captured runtime order; consumers fail closed
///     when an ID does not resolve after merge.
/// </summary>
public sealed record WeatherTransitionSnapshot(
    uint SkyVirtualAddress,
    uint? CurrentWeatherPointer,
    uint? CurrentWeatherFormId,
    uint? OutgoingWeatherPointer,
    uint? OutgoingWeatherFormId,
    float CurrentWeatherWeight,
    float? ModifierElapsedSeconds);
