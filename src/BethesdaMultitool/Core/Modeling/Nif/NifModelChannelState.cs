using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One transform channel's Shared state and static value, as <see cref="NifModelChannelStateMapping" /> derives them
///     (plan section 1.3).
/// </summary>
/// <param name="State">Keyed, Constant or NotDriven.</param>
/// <param name="StaticValue">
///     The static value in Shared's layout (translation x, y, z; rotation X, Y, Z, W; scale s, s, s): the Constant value,
///     a Keyed channel's fallback, or null (NotDriven, or a Keyed channel whose static is the sentinel).
/// </param>
internal readonly record struct NifModelChannelState(SceneAnimationChannelState State, float[]? StaticValue);
