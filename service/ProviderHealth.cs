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

    private readonly ConcurrentDictionary<string, int> _failures = new();
    private readonly List<string> _order = new();

    public ProviderHealth(IEnumerable<string> models)
    {
        foreach (var model in models)
        {
            if (_failures.TryAdd(model, 0))
                _order.Add(model);
        }
    }

    public void RecordFailure(string model)
    {
        _failures.AddOrUpdate(model, 1, (_, n) => n + 1);
    }

    public void RecordSuccess(string model)
    {
        _failures[model] = 0;
    }

    public static string StatusFor(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 0 => "ok",
        < DownThreshold => "degraded",
        _ => "down"
    };

    public string StatusOf(string model) =>
        StatusFor(_failures.TryGetValue(model, out var n) ? n : 0);

    // порядок — як моделі передали в конструктор, щоб картка в консолі не «стрибала»
    public IReadOnlyList<(string Name, string Status)> Snapshot() =>
        _order.Select(m => (m, StatusOf(m))).ToList();
}
