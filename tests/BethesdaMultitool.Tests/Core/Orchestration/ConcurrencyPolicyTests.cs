using BethesdaMultitool.Core.Orchestration;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Orchestration;

public sealed class ConcurrencyPolicyTests
{
    private static string UniqueVariable()
    {
        return "FALLOUT_TEST_CONCURRENCY_" + Guid.NewGuid().ToString("N");
    }

    [Fact]
    public void Presets_resolve_from_processor_count()
    {
        var cores = Environment.ProcessorCount;
        Assert.Equal(1, ConcurrencyPolicy.Serial.Resolve());
        Assert.Equal(7, ConcurrencyPolicy.Fixed(7).Resolve());
        Assert.Equal(Math.Max(1, cores), ConcurrencyPolicy.FullCores.Resolve());
        Assert.Equal(Math.Max(1, cores - 1), ConcurrencyPolicy.CoresMinusOne.Resolve());
        Assert.Equal(Math.Max(1, cores * 2), ConcurrencyPolicy.DoubleCores.Resolve());
        Assert.Equal(Math.Clamp(cores / 2, 2, 8), ConcurrencyPolicy.HalfCoresClamped(2, 8).Resolve());
    }

    [Fact]
    public void Default_policy_resolves_to_at_least_one()
    {
        Assert.Equal(1, default(ConcurrencyPolicy).Resolve());
    }

    [Fact]
    public void Invalid_preset_arguments_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(static () => ConcurrencyPolicy.Fixed(0));
        Assert.Throws<ArgumentOutOfRangeException>(static () => ConcurrencyPolicy.HalfCoresClamped(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(static () => ConcurrencyPolicy.HalfCoresClamped(4, 2));
    }

    [Fact]
    public void Positive_environment_value_overrides_preset_and_is_clamped()
    {
        var variable = UniqueVariable();
        try
        {
            Environment.SetEnvironmentVariable(variable, "3");
            Assert.Equal(3, ConcurrencyPolicy.Fixed(16).WithEnvironmentOverride(variable).Resolve());

            Environment.SetEnvironmentVariable(variable, "999");
            Assert.Equal(8, ConcurrencyPolicy.Fixed(16).WithEnvironmentOverride(variable, max: 8).Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("garbage")]
    [InlineData("")]
    public void Non_positive_or_unparseable_environment_values_fall_through_to_the_preset(string raw)
    {
        var variable = UniqueVariable();
        try
        {
            Environment.SetEnvironmentVariable(variable, raw);
            Assert.Equal(16, ConcurrencyPolicy.Fixed(16).WithEnvironmentOverride(variable).Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Explicit_override_wins_over_environment_and_preset()
    {
        var variable = UniqueVariable();
        try
        {
            Environment.SetEnvironmentVariable(variable, "3");
            var policy = ConcurrencyPolicy.Fixed(16)
                .WithEnvironmentOverride(variable)
                .WithExplicitOverride(5);
            Assert.Equal(5, policy.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Null_or_invalid_explicit_override_is_ignored()
    {
        Assert.Equal(16, ConcurrencyPolicy.Fixed(16).WithExplicitOverride(null).Resolve());
        Assert.Equal(16, ConcurrencyPolicy.Fixed(16).WithExplicitOverride(0).Resolve());
    }

    [Fact]
    public void Cores_scaled_divides_and_clamps()
    {
        var cores = Environment.ProcessorCount;

        Assert.Equal(Math.Clamp(cores, 1, 64), ConcurrencyPolicy.CoresScaled(1, 1, 64).Resolve());
        Assert.Equal(Math.Clamp(cores / 4, 4, 8), ConcurrencyPolicy.CoresScaled(4, 4, 8).Resolve());
        // The clamp genuinely binds in both directions, not just the divide.
        Assert.Equal(3, ConcurrencyPolicy.CoresScaled(1024, 3, 9).Resolve());
        Assert.Equal(2, ConcurrencyPolicy.CoresScaled(1, 1, 2).Resolve());
    }

    [Fact]
    public void Half_cores_is_exactly_cores_scaled_by_two()
    {
        // HalfCoresClamped is now a named alias, so the two must not be able to drift apart.
        Assert.Equal(
            ConcurrencyPolicy.CoresScaled(2, 2, 8).Resolve(),
            ConcurrencyPolicy.HalfCoresClamped(2, 8).Resolve());
    }

    [Fact]
    public void Cores_scaled_rejects_a_nonsense_divisor_or_range()
    {
        // A divisor of zero would be a divide-by-zero at Resolve time — far from the call site that
        // introduced it, and only on whatever machine happened to run that path.
        Assert.Throws<ArgumentOutOfRangeException>(static () => ConcurrencyPolicy.CoresScaled(0, 1, 8));
        Assert.Throws<ArgumentOutOfRangeException>(static () => ConcurrencyPolicy.CoresScaled(-1, 1, 8));
        Assert.Throws<ArgumentOutOfRangeException>(static () => ConcurrencyPolicy.CoresScaled(2, 0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(static () => ConcurrencyPolicy.CoresScaled(2, 8, 4));
    }
}