// W5: cache-hit % — без ділення на нуль на свіжому старті й з передбачуваним округленням
public class ObservabilityTests
{
    [Fact]
    public void NoCacheLookups_ZeroPercent() =>
        Assert.Equal(0m, Observability.CacheHitPct(0, 0));

    [Theory]
    [InlineData(1, 1, 50.0)]
    [InlineData(1, 2, 33.3)]
    [InlineData(2, 1, 66.7)]
    [InlineData(0, 5, 0.0)]
    [InlineData(5, 0, 100.0)]
    public void CacheHitPct_RoundedToOneDecimal(long hits, long misses, double expected) =>
        Assert.Equal((decimal)expected, Observability.CacheHitPct(hits, misses));
}
