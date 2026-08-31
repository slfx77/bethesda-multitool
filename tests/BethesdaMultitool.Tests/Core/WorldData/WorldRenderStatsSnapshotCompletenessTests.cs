using System.Collections;
using System.Reflection;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     Pins that <c>WorldRenderStats.Snapshot()</c> copies <em>every</em> settable property.
///     <para>
///         This exists because of a measured, silent failure. <c>ReferenceCullCacheVeto</c> — the
///         field added specifically so the renderer could report WHICH cull-cache clause failed —
///         was set by <c>ReferenceRenderer12</c>, reset by <c>Reset()</c>, and then dropped by
///         <c>Snapshot()</c>, which copied its three immediate neighbours and not it. Every profiler
///         consumer reads the snapshot, so the value never left the renderer. A source comment had
///         instructed "instrument WHICH clause fails before changing this again"; the instrument was
///         built and silently discarded, and two guessed fixes were spent in its absence.
///     </para>
///     <para>
///         A hand-written list of expected properties would rot the first time somebody adds one, so
///         this walks the type. An unhandled property type throws rather than skipping: a test that
///         quietly ignores what it cannot construct is exactly the "cannot fail" shape the repo's
///         test discipline forbids.
///     </para>
/// </summary>
public sealed class WorldRenderStatsSnapshotCompletenessTests
{
    private static PropertyInfo[] SettableProperties()
    {
        var properties = typeof(WorldRenderStats)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        // Guards the reflection itself: if a refactor made these properties non-settable or renamed
        // the type's shape, an empty walk would pass vacuously.
        Assert.True(properties.Length > 100,
            $"expected WorldRenderStats to expose many settable stats; found {properties.Length}");
        return properties;
    }

    /// <summary>
    ///     Builds a value that is distinct from the type's default, so a property the snapshot fails
    ///     to copy shows up as default-vs-assigned rather than coincidentally matching.
    /// </summary>
    private static object MakeValue(Type type, int seed, int depth = 0)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(int)) return seed;
        if (target == typeof(uint)) return (uint)seed;
        if (target == typeof(long)) return (long)seed;
        if (target == typeof(short)) return (short)seed;
        if (target == typeof(byte)) return (byte)(seed & 0xFF);
        if (target == typeof(float)) return seed + 0.5f;
        if (target == typeof(double)) return seed + 0.25;
        if (target == typeof(bool)) return true;
        if (target == typeof(string)) return $"value-{seed}";
        if (target.IsEnum) return Enum.GetValues(target).GetValue(0)!;

        if (target.IsArray)
        {
            var element = target.GetElementType()!;
            var array = Array.CreateInstance(element, 1);
            array.SetValue(MakeValue(element, seed + 1, depth + 1), 0);
            return array;
        }

        if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var element = target.GetGenericArguments()[0];
            var array = Array.CreateInstance(element, 1);
            array.SetValue(MakeValue(element, seed + 1, depth + 1), 0);
            return array;
        }

        if (depth < 3)
        {
            // Records and other composites: drive the widest constructor with generated arguments.
            var constructor = target.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
            if (constructor is not null && constructor.GetParameters().Length > 0)
            {
                var arguments = constructor.GetParameters()
                    .Select((p, i) => MakeValue(p.ParameterType, seed + i + 1, depth + 1))
                    .ToArray();
                return constructor.Invoke(arguments);
            }
        }

        throw new NotSupportedException(
            $"WorldRenderStatsSnapshotCompletenessTests cannot construct a value for '{target}'. " +
            "Extend MakeValue — do not skip the property, or the completeness check stops covering it.");
    }

    private static bool ValuesMatch(object? expected, object? actual)
    {
        if (expected is string || expected is not IEnumerable expectedSequence)
        {
            return Equals(expected, actual);
        }

        if (actual is not IEnumerable actualSequence)
        {
            return false;
        }

        return expectedSequence.Cast<object?>().SequenceEqual(actualSequence.Cast<object?>());
    }

    [Fact]
    public void Snapshot_copies_every_settable_property()
    {
        var properties = SettableProperties();
        var source = new WorldRenderStats();

        var seed = 1;
        foreach (var property in properties)
        {
            property.SetValue(source, MakeValue(property.PropertyType, seed));
            seed += 7;
        }

        var snapshot = source.Snapshot();

        var dropped = properties
            .Where(p => !ValuesMatch(p.GetValue(source), p.GetValue(snapshot)))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(dropped.Length == 0,
            "Snapshot() silently dropped these properties, so no profiler consumer can ever see " +
            $"them: {string.Join(", ", dropped)}");
    }

    [Fact]
    public void Reset_clears_every_settable_property_to_the_canonical_frame_default()
    {
        var properties = SettableProperties();
        var source = new WorldRenderStats();
        var expected = new WorldRenderStats();

        var seed = 1;
        foreach (var property in properties)
        {
            property.SetValue(source, MakeValue(property.PropertyType, seed));
            seed += 7;
        }

        source.Reset();
        expected.Reset();
        var stale = properties
            .Where(p => !ValuesMatch(p.GetValue(expected), p.GetValue(source)))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(stale.Length == 0,
            "Reset() left prior-frame values in these properties: " + string.Join(", ", stale));
    }

    [Fact]
    public void Snapshot_copies_collections_instead_of_aliasing_them()
    {
        // A snapshot that shares its backing arrays is not a snapshot: the renderer clears and
        // refills these per frame, so an aliased copy would mutate under a consumer mid-read.
        var source = new WorldRenderStats();
        var collections = SettableProperties()
            .Where(p => p.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.PropertyType))
            .ToArray();

        Assert.NotEmpty(collections);

        var seed = 1;
        foreach (var property in collections)
        {
            property.SetValue(source, MakeValue(property.PropertyType, seed));
            seed += 7;
        }

        var snapshot = source.Snapshot();

        var aliased = collections
            .Where(p => ReferenceEquals(p.GetValue(source), p.GetValue(snapshot)))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(aliased.Length == 0,
            $"Snapshot() aliased these collections rather than copying them: {string.Join(", ", aliased)}");
    }
}
