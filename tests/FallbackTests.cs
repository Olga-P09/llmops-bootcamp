using System.Text.Json;

// регресія для правила fallback: на наступну модель ідемо лише при 429 / 5xx / мережі / таймауті
public class FallbackTests
{
    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void RetryableStatus_TriggersFallback(int status) =>
        Assert.True(Fallback.IsRetryableStatus(status));

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    public void NonRetryable4xx_DoesNotTriggerFallback(int status) =>
        Assert.False(Fallback.IsRetryableStatus(status));

    [Fact]
    public void NetworkFailure_TriggersFallback() =>
        Assert.True(Fallback.IsRetryableError(new HttpRequestException("connection refused")));

    [Fact]
    public void Timeout_TriggersFallback()
    {
        Assert.True(Fallback.IsRetryableError(new TaskCanceledException("HttpClient timeout")));
        Assert.True(Fallback.IsRetryableError(new TimeoutException()));
    }

    [Fact]
    public void UnreadableResponse_DoesNotTriggerFallback()
    {
        Assert.False(Fallback.IsRetryableError(new JsonException("bad json")));
        Assert.False(Fallback.IsRetryableError(new KeyNotFoundException("choices")));
    }
}
