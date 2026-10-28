// W5: статус провайдера чесний — не «ok» посеред інциденту, і повертається в «ok» лише після успіху
public class ProviderHealthTests
{
    private static ProviderHealth Fresh() => new(new[] { "mock-mini", "mock-strong" });

    [Fact]
    public void FreshStart_AllProvidersOk()
    {
        var health = Fresh();

        Assert.Equal(
            new[] { ("mock-mini", "ok"), ("mock-strong", "ok") },
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
        var health = Fresh();
        for (var i = 0; i < 3; i++)
        {
            health.RecordFailure("mock-mini");
            health.RecordFailure("mock-strong");
        }

        health.RecordSuccess("mock-mini");

        Assert.Equal("ok", health.StatusOf("mock-mini"));
        Assert.Equal("down", health.StatusOf("mock-strong"));
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
