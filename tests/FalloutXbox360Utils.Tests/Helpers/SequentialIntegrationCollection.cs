using Xunit;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     xUnit collection that forces sequential execution of heavyweight integration tests
///     that load large DMP/BSA/ESM fixtures. Running these in parallel causes flaky
///     failures due to file I/O, memory, and CPU contention — not real bugs.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SequentialIntegrationCollection
{
    public const string Name = "SequentialIntegration";
}
