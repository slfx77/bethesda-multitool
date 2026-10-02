namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     One diagnostic the identification chain records (cut-1c plan section 6.2): a stable code the reader turns into a
///     document diagnostic, and the message that says what happened, in the chain's own words.
/// </summary>
/// <param name="Code">The stable code (<c>bmt.xngine.game-ambiguous</c> and its siblings on <see cref="XnGineGameIdentity" />).</param>
/// <param name="Message">What the chain observed and, where the user can act, which option resolves it.</param>
internal readonly record struct XnGineIdentityDiagnostic(string Code, string Message);
