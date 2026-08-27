using Xunit;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     xUnit collection for tests that mutate <em>shared</em> process-wide environment variables —
///     ones production code reads by a fixed name (<c>BETHESDA_TEST_DATA_ROOT</c>,
///     <c>FALLOUT_VIEWER_TONEMAP</c>, …).
///     <para>
///         Environment variables are per-process, not per-test. Saving the old value and restoring
///         it in a <c>finally</c> protects the <em>next</em> test but does nothing for a test
///         running <em>concurrently</em>: xUnit runs collections in parallel, so an unguarded
///         mutator silently changes what every other reader observes for the duration of its run.
///         The failure mode is quiet rather than loud — a redirected asset root makes real-asset
///         tests find nothing and skip, so a sweep reports success having covered less than it
///         claims.
///     </para>
///     <para>
///         Joining this collection serializes all such tests against each other. It does
///         <em>not</em> serialize them against unrelated readers elsewhere in the suite, so prefer
///         a test-owned unique variable name where the code under test allows one — see
///         <c>ConcurrencyPolicyTests</c>, which appends a <see cref="Guid" /> to its variable and
///         therefore needs no collection at all. Use this group only when the variable name is
///         fixed by production.
///     </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentGroup
{
    public const string Name = "ProcessEnvironment";

    private ProcessEnvironmentGroup()
    {
    }
}
