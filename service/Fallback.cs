// W4: які збої моделі варто пробувати на наступній моделі ланцюга.
// окремий public static class — щоб правило покривали юніт-тести (GETTING_STARTED §10).
public static class Fallback
{
    // 429 (rate limit) і 5xx (провайдер лежить) — тимчасові збої провайдера: інша модель може відповісти.
    // 400 / 401 / 403 / 404 та інші 4xx — проблема самого запиту чи конфігурації: fallback не допоможе.
    public static bool IsRetryableStatus(int status) => status == 429 || status >= 500;

    // мережа недоступна (HttpRequestException) або таймаут (TaskCanceledException / TimeoutException).
    // нерозбірна відповідь (JsonException тощо) — збій формату, не провайдера: не fallback.
    public static bool IsRetryableError(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or TimeoutException;
}
