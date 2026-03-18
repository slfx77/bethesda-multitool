using Xunit;

namespace FalloutXbox360Utils.Tests.Core;

/// <summary>
///     Forces DMP-heavy tests to run sequentially, preventing 4+ GB memory spikes
///     from multiple 150-250 MB dumps loaded in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DumpSerialTestGroup
{
    public const string Name = "DumpSerial";

    private DumpSerialTestGroup()
    {
    }
}
