using Xunit;

namespace FalloutXbox360Utils.Tests.Core;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LoggerSerialTestGroup
{
    public const string Name = "LoggerSerial";

    private LoggerSerialTestGroup()
    {
    }
}
