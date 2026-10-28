// W5: розрахунки для плиток консолі, які живуть у коді, а не в БД.
public static class Observability
{
    // частка cache hit серед усіх перевірок кешу, %, 1 знак після коми.
    // ще не було жодної перевірки (свіжий старт) → 0: лічильники справді нульові.
    public static decimal CacheHitPct(long hits, long misses)
    {
        var total = hits + misses;

        if (total <= 0)
            return 0m;

        return Math.Round(100m * hits / total, 1, MidpointRounding.AwayFromZero);
    }
}
