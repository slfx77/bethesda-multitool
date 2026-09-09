using BethesdaMultitool.Core.RuntimeBuffer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeBuffer;

public sealed class OwnershipConfidenceMapTests
{
    /// <summary>
    ///     Every <see cref="ClaimSource" /> must state which evidence tier it belongs to. A C# switch
    ///     expression over an enum cannot be proven exhaustive at compile time, so this is the
    ///     enforcement: add a claim source without mapping it and this test throws rather than the
    ///     new strategy silently inheriting whatever tier happened to be first.
    /// </summary>
    [Fact]
    public void EveryClaimSource_MapsToATier()
    {
        foreach (var source in Enum.GetValues<ClaimSource>())
        {
            var tier = OwnershipConfidenceMap.For(source);
            Assert.True(Enum.IsDefined(tier), $"{source} mapped to an undefined tier: {tier}");
        }
    }

    /// <summary>
    ///     The tiers are declared strongest-first so <c>&lt;=</c> reads as "at least this
    ///     trustworthy". Pin the ordering, because a later ruling that promotes claims into emitted
    ///     records will express its threshold as a comparison against these values.
    /// </summary>
    [Fact]
    public void Tiers_AreOrderedStrongestFirst()
    {
        Assert.True(OwnershipConfidence.FieldNamed < OwnershipConfidence.ExactTextMatch);
        Assert.True(OwnershipConfidence.ExactTextMatch < OwnershipConfidence.OwnerNamed);
        Assert.True(OwnershipConfidence.OwnerNamed < OwnershipConfidence.Positional);
    }

    /// <summary>
    ///     The relaxed reverse lookup skips the BSStringT length validation its strict sibling
    ///     performs, so it must rank strictly lower. Until 2026-09-04 both reported
    ///     <see cref="ClaimSource.SecondPassReverse" /> and the distinction was unobservable.
    /// </summary>
    [Fact]
    public void RelaxedReverseLookup_RanksBelowItsStrictSibling()
    {
        Assert.True(
            OwnershipConfidenceMap.For(ClaimSource.SecondPassReverse)
            < OwnershipConfidenceMap.For(ClaimSource.SecondPassReverseRelaxed));
    }

    /// <summary>
    ///     The cFormEditorID fallback infers an owner from position alone. It shared
    ///     <see cref="ClaimSource.SecondPassVtable" /> with the field-named RTTI hit until
    ///     2026-09-04, which made the strongest and weakest vtable evidence one number.
    /// </summary>
    [Fact]
    public void PositionalFallback_IsTheWeakestTier_AndNotTheFieldNamedVtableHit()
    {
        Assert.Equal(
            OwnershipConfidence.Positional,
            OwnershipConfidenceMap.For(ClaimSource.SecondPassCFormEditorIdPosition));
        Assert.Equal(
            OwnershipConfidence.FieldNamed,
            OwnershipConfidenceMap.For(ClaimSource.SecondPassVtable));
    }
}