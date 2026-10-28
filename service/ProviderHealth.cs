using System.Collections.Concurrent;

// W5: статус провайдерів для консолі (GET /providers) — з реальних викликів, а не прибитий «ok».
// рахуємо поспіль ідучі retryable-збої (429 / 5xx / мережа / таймаут — див. Fallback) для кожної моделі:
//   0 → ok, 1–2 → degraded, ≥3 → down.
// успішний виклик моделі скидає лічильник лише ЦІЄЇ моделі.
// cache hit сюди не потрапляє: модель не викликали, тож нових даних про її стан немає.
// живе в пам'яті процесу й обнуляється при рестарті (як cache-hit і fallback).
public sealed class ProviderHealth
{
    public const int DownThreshold = 3;

    private sealed record State(int Failures, DateTimeOffset? LastCheckedAt);

    private readonly ConcurrentDictionary<string, State> _states = new();
    private readonly List<string> _order = new();
    private readonly TimeProvider _timeProvider;

    public ProviderHealth(IEnumerable<string> models, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        foreach (var model in models)
        {
            if (_states.TryAdd(model, new State(0, null)))
                _order.Add(model);
        }
    }

    public void RecordFailure(string model) => RecordCheck(model, false);

    public void RecordSuccess(string model) => RecordCheck(model, true);

    // A null outcome records a call without changing the retryable failure streak.
    public void RecordCheck(string model, bool? succeeded = null)
    {
        _states.AddOrUpdate(model,
            _ => new State(succeeded == false ? 1 : 0, _timeProvider.GetUtcNow()),
            (_, state) => new State(
                succeeded switch { true => 0, false => state.Failures + 1, null => state.Failures },
                _timeProvider.GetUtcNow()));
    }

    public static string StatusFor(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 0 => "ok",
        < DownThreshold => "degraded",
        _ => "down"
    };

    public string StatusOf(string model) =>
        StatusFor(_states.TryGetValue(model, out var state) ? state.Failures : 0);

    // порядок — як моделі передали в конструктор, щоб картка в консолі не «стрибала»
    public IReadOnlyList<(string Name, string Status, DateTimeOffset? LastCheckedAt)> Snapshot() =>
        _order.Select(m =>
        {
            var state = _states[m];
            return (m, StatusFor(state.Failures), state.LastCheckedAt);
        }).ToList();
}
