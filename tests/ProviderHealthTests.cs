// W5: статус провайдера чесний — не «ok» посеред інциденту, і повертається в «ok» лише після успіху
public class ProviderHealthTests
{
    private static ProviderHealth Fresh() => new(new[] { "mock-mini", "mock-strong" });

    [Fact]
    public void FreshStart_AllProvidersOk()
    {
        var health = Fresh();

        Assert.Equal(
            new[] { ("mock-mini", "ok", (DateTimeOffset?)null), ("mock-strong", "ok", (DateTimeOffset?)null) },
            health.Snapshot());
    }

    [Fact]
    public void OneFailure_Degraded()
    {
        var health = Fresh();
        health.RecordFailure("mock-mini");

        Assert.Equal("degraded", health.StatusOf("mock-mini"));
    }

    [Fact]
    public void TwoFailures_StillDegraded()
    {
        var health = Fresh();
        health.RecordFailure("mock-mini");
        health.RecordFailure("mock-mini");

        Assert.Equal("degraded", health.StatusOf("mock-mini"));
    }

    [Fact]
    public void ThreeConsecutiveFailures_Down()
    {
        var health = Fresh();
        for (var i = 0; i < 3; i++)
            health.RecordFailure("mock-mini");

        Assert.Equal("down", health.StatusOf("mock-mini"));
    }

    [Fact]
    public void Success_ResetsOnlyThatModel()
    {
        var clock = new TestClock();
        var health = new ProviderHealth(new[] { "mock-mini", "mock-strong" }, clock);
        for (var i = 0; i < 3; i++)
        {
            health.RecordFailure("mock-mini");
            health.RecordFailure("mock-strong");
        }

        var failedAt = clock.Now;
        clock.Now = clock.Now.AddMinutes(1);
        health.RecordSuccess("mock-mini");

        Assert.Equal("ok", health.StatusOf("mock-mini"));
        Assert.Equal("down", health.StatusOf("mock-strong"));
        Assert.Equal(clock.Now, health.Snapshot()[0].LastCheckedAt);
        Assert.Equal(failedAt, health.Snapshot()[1].LastCheckedAt);

        clock.Now = clock.Now.AddMinutes(1);
        health.RecordSuccess("mock-strong");
        Assert.Equal("ok", health.StatusOf("mock-strong"));
        Assert.Equal(clock.Now, health.Snapshot()[1].LastCheckedAt);
        Assert.Equal(clock.Now.AddMinutes(-1), health.Snapshot()[0].LastCheckedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void EveryCall_UpdatesOnlyThatModelsTimestamp(bool? succeeded)
    {
        var clock = new TestClock();
        var health = new ProviderHealth(new[] { "mock-mini", "mock-strong" }, clock);

        health.RecordCheck("mock-mini", succeeded);
        Assert.Equal(clock.Now, health.Snapshot()[0].LastCheckedAt);
        Assert.Null(health.Snapshot()[1].LastCheckedAt);

        clock.Now = clock.Now.AddMinutes(1);
        health.RecordCheck("mock-mini", succeeded);
        Assert.Equal(clock.Now, health.Snapshot()[0].LastCheckedAt);
        Assert.Null(health.Snapshot()[1].LastCheckedAt);

        // Reading health (or serving a cache hit) supplies no new provider evidence.
        var snapshot = health.Snapshot();
        clock.Now = clock.Now.AddMinutes(1);
        Assert.Equal(snapshot, health.Snapshot());
    }

    [Theory]
    [InlineData(0, "ok")]
    [InlineData(1, "degraded")]
    [InlineData(3, "down")]
    public void NonRetryableCall_RefreshesTimestampWithoutChangingStatus(int failures, string status)
    {
        var clock = new TestClock();
        var health = new ProviderHealth(new[] { "mock-mini" }, clock);
        for (var i = 0; i < failures; i++)
            health.RecordFailure("mock-mini");

        clock.Now = clock.Now.AddMinutes(1);
        health.RecordCheck("mock-mini");

        Assert.Equal(status, health.StatusOf("mock-mini"));
        Assert.Equal(clock.Now, health.Snapshot()[0].LastCheckedAt);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Success_BreaksTheStreak()
    {
        var health = Fresh();
        health.RecordFailure("mock-mini");
        health.RecordFailure("mock-mini");
        health.RecordSuccess("mock-mini");
        health.RecordFailure("mock-mini");

        // після успіху рахуємо заново: 1 збій поспіль, а не 3
        Assert.Equal("degraded", health.StatusOf("mock-mini"));
    }

    [Fact]
    public void FailureOfOneModel_DoesNotAffectOther()
    {
        var health = Fresh();
        health.RecordFailure("mock-strong");

        Assert.Equal("ok", health.StatusOf("mock-mini"));
        Assert.Equal("degraded", health.StatusOf("mock-strong"));
    }

    [Fact]
    public void ConcurrentFailures_AllCounted()
    {
        var health = Fresh();
        Parallel.For(0, 100, _ => health.RecordFailure("mock-mini"));

        Assert.Equal("down", health.StatusOf("mock-mini"));
    }

    [Theory]
    [InlineData(0, "ok")]
    [InlineData(1, "degraded")]
    [InlineData(2, "degraded")]
    [InlineData(3, "down")]
    [InlineData(10, "down")]
    public void StatusFor_Thresholds(int failures, string expected) =>
        Assert.Equal(expected, ProviderHealth.StatusFor(failures));
}
