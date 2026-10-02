namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     Which step of the identification chain (cut-1c plan section 6.2) answered the game question. The chain tries the
///     steps in this order and stops at the first that answers; a step whose answer the content refutes does not answer
///     and is recorded as a diagnostic.
/// </summary>
internal enum XnGineGameIdentityStep
{
    /// <summary>No step answered: the game is Unknown and the units are the guard row.</summary>
    None,

    /// <summary>Step 1: the <c>bmt.game</c> app option (the CLI's <c>--game</c>), a user assertion.</summary>
    GameOption,

    /// <summary>Step 2: the container the entry came from (a numbered XnGine BSA, a named XnGine BSA with LZSS entries, or a ROB).</summary>
    Container,

    /// <summary>Step 3: the <c>bmt.classic-game</c> app option the shell set from the install walk-up.</summary>
    Install,

    /// <summary>Step 4: the record's own bytes (which plane-header size walks, and header +20).</summary>
    Content
}
