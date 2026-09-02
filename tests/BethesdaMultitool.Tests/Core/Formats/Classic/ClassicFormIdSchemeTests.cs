using BethesdaMultitool.Core.Formats.Classic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Pins the synthetic-FormID layout for classic games: (domain &lt;&lt; 24) | 24-bit stable index,
///     round-trippable, with the reserved domains rejected — ids must stay stable across installs
///     (they derive from source identity), so the composition arithmetic itself must never drift.
/// </summary>
public class ClassicFormIdSchemeTests
{
    [Theory]
    [InlineData(0x01, 0x000000u, 0x01000000u)]
    [InlineData(0x10, 0x000136u, 0x10000136u)] // a Fallout PRO index survives verbatim
    [InlineData(0x7F, 0xFFFFFFu, 0x7FFFFFFFu)] // both fields at max
    public void Compose_PacksDomainAndIndex(byte domain, uint index, uint expected)
    {
        Assert.Equal(expected, ClassicFormIdScheme.Compose(domain, index));
    }

    [Theory]
    [InlineData(0x01, 0u)]
    [InlineData(0x42, 12345u)]
    [InlineData(0xFE, 0xFFFFFFu)]
    public void DomainAndIndex_RoundTrip(byte domain, uint index)
    {
        var formId = ClassicFormIdScheme.Compose(domain, index);
        Assert.Equal(domain, ClassicFormIdScheme.DomainOf(formId));
        Assert.Equal(index, ClassicFormIdScheme.IndexOf(formId));
    }

    [Fact]
    public void Compose_RejectsIndexBeyond24Bits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClassicFormIdScheme.Compose(0x01, ClassicFormIdScheme.MaxIndex + 1));
    }

    [Theory]
    [InlineData(0x00)] // collides with genuine low FormIDs in mixed displays
    [InlineData(0xFF)] // TES3 shared-namespace convention
    public void Compose_RejectsReservedDomains(byte domain)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicFormIdScheme.Compose(domain, 1));
    }
}
