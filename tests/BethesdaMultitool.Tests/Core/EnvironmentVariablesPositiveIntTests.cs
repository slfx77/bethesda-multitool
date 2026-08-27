using BethesdaMultitool.Core;
using Xunit;

namespace BethesdaMultitool.Tests.Core;

/// <summary>
///     Pins the difference between the two integer knob readers, which is the whole reason the
///     second one exists.
///     <para>
///         <c>GetClampedInt</c> clamps ANY parseable value into range, so <c>FOO=0</c> becomes the
///         minimum. For a worker count or a poll interval the minimum is the most aggressive setting
///         available, so a user typing 0 to mean "leave it alone" would get the opposite of what
///         they asked for. <c>GetPositiveIntOrDefault</c> treats 0 and below as "unset".
///     </para>
///     <para>
///         Each test uses a variable name unique to itself, so nothing here needs the
///         process-environment collection — see that type's remarks.
///     </para>
/// </summary>
public sealed class EnvironmentVariablesPositiveIntTests
{
    private static string UniqueName() => $"BETHESDA_TEST_POSINT_{Guid.NewGuid():N}";

    private static void WithVariable(string? value, Action<string> body)
    {
        var name = UniqueName();
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            body(name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-999")]
    public void Zero_and_negative_fall_through_to_the_default_rather_than_clamping_up(string raw)
    {
        // The exact divergence from GetClampedInt, asserted against BOTH so the contrast is the
        // subject of the test rather than a comment about it.
        WithVariable(raw, name =>
        {
            Assert.Equal(2000, EnvironmentVariables.GetPositiveIntOrDefault(name, 2000, 1, 9999));
            Assert.Equal(1, EnvironmentVariables.GetClampedInt(name, 2000, 1, 9999));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("3.5")]
    public void Unset_and_unparseable_values_use_the_default(string? raw)
    {
        WithVariable(raw, name =>
            Assert.Equal(2000, EnvironmentVariables.GetPositiveIntOrDefault(name, 2000, 1, 9999)));
    }

    [Fact]
    public void A_positive_value_wins_and_is_clamped_into_range()
    {
        WithVariable("500", name =>
            Assert.Equal(500, EnvironmentVariables.GetPositiveIntOrDefault(name, 2000, 1, 9999)));
        WithVariable("99999", name =>
            Assert.Equal(9999, EnvironmentVariables.GetPositiveIntOrDefault(name, 2000, 1, 9999)));
        WithVariable("1", name =>
            Assert.Equal(5, EnvironmentVariables.GetPositiveIntOrDefault(name, 2000, 5, 9999)));
    }

    [Fact]
    public void Parsing_is_invariant_culture_regardless_of_the_thread_culture()
    {
        // Environment variables are machine configuration, not localized input. A German thread
        // culture must not change how "1234" is read, and must not make "1.234" parse as 1234.
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            WithVariable("1234", name =>
                Assert.Equal(1234, EnvironmentVariables.GetPositiveIntOrDefault(name, 7, 1, 99999)));
            WithVariable("1.234", name =>
                Assert.Equal(7, EnvironmentVariables.GetPositiveIntOrDefault(name, 7, 1, 99999)));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
