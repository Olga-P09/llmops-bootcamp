// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — дефолт, грошей не треба.
// MODEL=gpt-5-mini або claude-haiku + ключ у gateway/.env — реальна модель.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();

var app = builder.Build();

// налаштування беремо з оточення (задаються в docker-compose.yml)
var gateway = Environment.GetEnvironmentVariable("GATEWAY_URL")
    ?? "http://gateway:4000";

var dbConn = Environment.GetEnvironmentVariable("DB_CONN")
    ?? "Host=postgres;Database=llmops;Username=llmops;Password=llmops";

var defaultModel = Environment.GetEnvironmentVariable("MODEL") ?? "mock";

// [W2] training prices in USD per 1k tokens
var prices = new Dictionary<string, (decimal InPer1K, decimal OutPer1K)>
{
    ["mock-mini"]   = (0.00015m, 0.0006m),
    ["mock-strong"] = (0.0025m,  0.01m),
};

// [W3] in-memory exact cache: ключ → готова відповідь моделі.
// живе в пам'яті процесу й обнуляється при рестарті сервісу (TTL поки немає).
var cache = new ConcurrentDictionary<string, string>();
long cacheHits = 0;
long cacheMisses = 0;  // знадобляться для плитки cache-hit % (W5)

// [W4] кожен перехід між моделями у fallback-ланцюгу — з нього живе плитка «Fallback» (W5)
long fallbackEvents = 0;

// [W4] graceful degradation: усі моделі ланцюга впали. чесно, без вигаданих фактів
// (ні статусу, ні номера, ні «зроблено») і не стартерний текст.
const string DegradedAnswer =
    "Вибачте, зараз у нас тимчасова проблема з відповідями. Спробуйте, будь ласка, трохи пізніше.";

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): тут перевірити вхід на PII / інʼєкції. поки нічого.
    // TODO(student, W4)

    // routing (W2): скарги / повернення / терміново → mock-strong, решта → mock-mini (див. Route)
    var model = Route(body.Message, defaultModel);

    // W1: беремо активний prompt із registry
    string promptVersion;
    string systemPrompt;

    try
    {
        (promptVersion, systemPrompt) = await GetActivePrompt(dbConn);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to load active prompt: {ex.Message}");

        return Results.Json(
            new
            {
                request_id = requestId,
                content = "Сервіс тимчасово недоступний.",
                tool = (string?)null,
                latency_ms = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
            },
            statusCode: 503
        );
    }

    // cache (W3): перед викликом глянути в in-memory exact cache — раптом вже відповідали.
    if (cache.TryGetValue(CacheKey(model, systemPrompt, body.Message), out var cachedAnswer))
    {
        Interlocked.Increment(ref cacheHits);

        var hitLatencyMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
        Console.WriteLine($"CACHE HIT  model={model} prompt={promptVersion} request={requestId}");

        // модель не викликали: 0 токенів і cost 0 (не null) — так у логах видно, що це кеш
        await LogRequest(dbConn, requestId, model, promptVersion, hitLatencyMs, 0, 0, 0m, 200);

        return Results.Json(new
        {
            request_id = requestId,
            content = cachedAnswer,
            tool = (string?)null,
            latency_ms = hitLatencyMs,
            cached = true
        });
    }

    Interlocked.Increment(ref cacheMisses);
    Console.WriteLine($"CACHE MISS model={model} prompt={promptVersion} request={requestId}");

    // fallback (W4): ідемо по ланцюгу моделей (див. FallbackChain).
    // 429 / 5xx / мережа / нерозбірна відповідь → наступна модель; кожен перехід → fallbackEvents.
    // усі впали → graceful degradation (DegradedAnswer, HTTP 200, не 500).
    var chain = FallbackChain(model, defaultModel);

    var http = httpFactory.CreateClient();

    var answer = "";
    string? toolCall = null;
    var respondedModel = model; // модель, що реально відповіла (при повному збої — остання спробувана)

    int promptTokens = 0;
    int completionTokens = 0;
    int status = 0; // 0 = відповіді не було
    bool modelOk = false; // true лише якщо відповідь моделі повністю розібрана

    for (var attempt = 0; attempt < chain.Length && !modelOk; attempt++)
    {
        if (attempt > 0)
        {
            Interlocked.Increment(ref fallbackEvents);
            Console.WriteLine($"FALLBACK {chain[attempt - 1]} -> {chain[attempt]} (status={status}) request={requestId}");
        }

        respondedModel = chain[attempt];
        answer = "";
        toolCall = null;
        promptTokens = 0;
        completionTokens = 0;
        status = 0;

        var payload = JsonSerializer.Serialize(new
        {
            model = respondedModel,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = systemPrompt
                },
                new
                {
                    role = "user",
                    content = body.Message
                }
            }
        });

        try
        {
            var response = await http.PostAsync(
                $"{gateway}/v1/chat/completions",
                new StringContent(
                    payload,
                    Encoding.UTF8,
                    "application/json"
                )
            );

            status = (int)response.StatusCode;

            // 429 / 5xx (і будь-який не-2xx) — збій провайдера: пробуємо наступну модель
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var rawJson = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(rawJson);

            var message = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message");

            answer = message
                .GetProperty("content")
                .GetString() ?? "";

            // tools (W3): lookup_order виконуємо і підклеюємо результат до відповіді.
            // HITL (W4): create_ticket — незворотна дія, тут НЕ виконується (черга approvals).
            // TODO(student, W4)
            if (
                message.TryGetProperty("tool_calls", out var tools)
                && tools.ValueKind == JsonValueKind.Array
                && tools.GetArrayLength() > 0
            )
            {
                toolCall = tools[0]
                    .GetProperty("function")
                    .GetProperty("name")
                    .GetString();

                if (toolCall == "lookup_order")
                {
                    answer += $" (статус: {RunTool(toolCall)})";
                }
            }

            var usage = doc.RootElement.GetProperty("usage");

            promptTokens = usage
                .GetProperty("prompt_tokens")
                .GetInt32();

            completionTokens = usage
                .GetProperty("completion_tokens")
                .GetInt32();

            modelOk = true;
        }
        catch (Exception ex)
        {
            // мережа/gateway недоступні або відповідь не розпарсилась — теж збій: наступна модель
            Console.Error.WriteLine($"Model {respondedModel} failed: {ex.Message}");
        }
    }

    if (!modelOk)
    {
        // graceful degradation (W4): усі моделі ланцюга впали
        Console.Error.WriteLine($"DEGRADED all models failed ({string.Join(" -> ", chain)}) request={requestId}");
        answer = DegradedAnswer;
        toolCall = null;
    }

    // cache (W3): кладемо лише успішну відповідь без tool_call.
    // помилку не кешуємо — інакше один збій «застрягне» для цього питання до рестарту;
    // tool_call не кешуємо — дію/дані інструмента треба отримувати щоразу заново.
    // ключ — від моделі, що реально відповіла: відповідь резервної моделі не видаємо за відповідь основної.
    if (modelOk && status == 200 && toolCall is null)
    {
        cache[CacheKey(respondedModel, systemPrompt, body.Message)] = answer;
    }

    var latencyMs =
        (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

    // cost (W2): usage -> model price -> cost_usd
    // модель не відповіла (збій) → cost лишається null: невідома вартість ≠ 0.
    // 0 у логах — лише для cache hit, тож збій не виглядає як влучання в кеш.
    decimal? costUsd = null;

    if (modelOk && prices.TryGetValue(respondedModel, out var price))
    {
        costUsd = Math.Round(
            promptTokens / 1000m * price.InPer1K
            + completionTokens / 1000m * price.OutPer1K,
            6
        );
    }

    // W1: записуємо prompt_version у unified log
    // W4: model = та, що реально відповіла (після fallback), а не та, яку обрав routing
    await LogRequest(
        dbConn,
        requestId,
        respondedModel,
        promptVersion,
        latencyMs,
        promptTokens,
        completionTokens,
        costUsd,
        status
    );

    return Results.Json(new
    {
        request_id = requestId,
        content = answer,
        tool = toolCall,
        latency_ms = latencyMs,
        cached = false
    });
});

// W2: routing policy — обрати модель за текстом запиту (лише в mock-режимі).
// TODO(student, W5): budget alert при 80% денного бюджету (routing не змінюємо). НЕ реалізовано.
static string Route(string message, string defaultModel)
{
    // Routing rules below are only for mock mode.
    // For a real configured model, keep the selected model unchanged.
    if (defaultModel != "mock")
        return defaultModel;

    var text = message.ToLowerInvariant();

    bool escalation =
        text.Contains("поверн") ||
        text.Contains("терміново") ||
        text.Contains("скарг") ||
        text.Contains("refund");

    return escalation ? "mock-strong" : "mock-mini";
}

// W4: fallback-ланцюг — спершу модель від routing, далі резервний аліас із litellm-config.yaml
// (mock-strong ↔ mock-mini); після останнього — graceful degradation.
// реальна модель (MODEL=...): резервної моделі в конфігу не задано, тож лише вона сама.
static string[] FallbackChain(string routed, string defaultModel)
{
    if (defaultModel != "mock")
        return new[] { routed };

    return routed == "mock-strong"
        ? new[] { "mock-strong", "mock-mini" }
        : new[] { "mock-mini", "mock-strong" };
}

// W3: ключ кешу = model + повний текст systemPrompt + повідомлення (зміна будь-чого → інший ключ).
// \u001F — роздільник, якого немає у звичайному тексті: "ab"+"c" і "a"+"bc" дають різні ключі.
static string CacheKey(string model, string systemPrompt, string message) =>
    string.Join('\u001F', model, systemPrompt, message);

// W3: виконати інструмент, який попросила модель. результат — mock, без реальної системи замовлень.
// викликається лише для lookup_order (read-only); create_ticket сюди не потрапляє (W4, HITL).
static string RunTool(string name) => name switch
{
    "lookup_order" => "в дорозі",
    _ => throw new ArgumentException($"Unknown tool: {name}", nameof(name))
};

// ======================================================
// Prompt Registry
// ======================================================

// W1: отримати активний prompt
static async Task<(string Version, string Body)> GetActivePrompt(
    string conn)
{
    await using var db = new NpgsqlConnection(conn);
    await db.OpenAsync();

    await using var cmd = new NpgsqlCommand(
        """
        SELECT version, body
        FROM prompts
        WHERE name = 'support-system'
          AND active = true
        ORDER BY created_at DESC
        LIMIT 1
        """,
        db
    );

    await using var reader = await cmd.ExecuteReaderAsync();

    if (await reader.ReadAsync())
    {
        return (
            reader.GetString(0),
            reader.GetString(1)
        );
    }

    // fail-visible:
    // активного prompt немає.
    // fallback навмисно НЕ містить слово "support",
    // тому mock змінить поведінку.
    return (
        "none",
        "You are an assistant."
    );
}


// GET /prompts
// Повертає всі версії, щоб UI показував registry
app.MapGet("/prompts", () => WithDb("list prompts", async () =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();

    await using var cmd = new NpgsqlCommand(
        """
        SELECT name, version, active
        FROM prompts
        ORDER BY created_at ASC
        """,
        db
    );

    await using var reader = await cmd.ExecuteReaderAsync();

    var prompts = new List<object>();

    while (await reader.ReadAsync())
    {
        prompts.Add(new
        {
            name = reader.GetString(0),
            version = reader.GetString(1),
            active = reader.GetBoolean(2)
        });
    }

    return Results.Json(prompts);
}));


// POST /prompts/{version}/activate
// Атомарно робить вказану версію активною,
// а всі інші версії support-system — неактивними.
app.MapPost("/prompts/{version}/activate", (string version) => WithDb("activate prompt", async () =>
{
    await using var db = new NpgsqlConnection(dbConn);
    await db.OpenAsync();

    await using var cmd = new NpgsqlCommand(
        """
        UPDATE prompts
        SET active = (version = @v)
        WHERE name = 'support-system'
          AND EXISTS (
              SELECT 1
              FROM prompts
              WHERE name = 'support-system'
                AND version = @v
          )
        """,
        db
    );

    cmd.Parameters.AddWithValue("v", version);

    var affected = await cmd.ExecuteNonQueryAsync();

    // Якщо версії не існує — UPDATE не змінить жодного рядка
    if (affected == 0)
    {
        return Results.NotFound(new
        {
            error = $"Prompt version '{version}' not found."
        });
    }

    return Results.Ok(new
    {
        version,
        active = true
    });
}));


// ======================================================
// Інші endpoints
// ======================================================

// ці ендпоінти читає готова консоль. поверни потрібну форму — картки оживуть.

// ліфнес, не для консолі
app.MapGet(
    "/health",
    () => Results.Ok(new { status = "ok" })
);

// W5: { p95_ms, requests, cache_hit_pct, error_rate_pct, fallback_events }
app.MapGet(
    "/observability",
    () => Results.Json(
        new { todo = "aggregate from requests table" }
    )
);

// W2/W5: { today_usd, budget_usd }
// якщо БД недоступна — 503, а НЕ today_usd = 0: невідома витрата ≠ нульова.
app.MapGet("/cost", () => WithDb("read today's cost", async () =>
{
    await using var conn = new NpgsqlConnection(dbConn);
    await conn.OpenAsync();

    await using var cmd = new NpgsqlCommand(
        """
        SELECT COALESCE(SUM(cost_usd), 0)
        FROM requests
        WHERE created_at::date = CURRENT_DATE
        """,
        conn
    );

    var result = await cmd.ExecuteScalarAsync();
    var today = Convert.ToDecimal(result);

    return Results.Json(new
    {
        today_usd = Math.Round(today, 4),
        budget_usd = 5.0
    });
}));

// W5: { providers: [ { name, status } ] }
app.MapGet(
    "/providers",
    () => Results.Json(
        new { todo = "provider health" }
    )
);

// W4: { pending: [ { id, action } ] }
app.MapGet(
    "/approvals",
    () => Results.Json(
        new { todo = "pending HITL approvals" }
    )
);


app.Run("http://0.0.0.0:8080");


// ======================================================
// DB errors -> 503
// ======================================================

// postgres недоступний (не підключились / таймаут / помилка БД) → 503 з JSON,
// а не голий 500. як і в /chat: лог у stderr, клієнту — зрозуміла відповідь.
static async Task<IResult> WithDb(string operation, Func<Task<IResult>> action)
{
    try
    {
        return await action();
    }
    catch (Exception ex) when (ex is NpgsqlException
        or System.Net.Sockets.SocketException
        or TimeoutException)
    {
        Console.Error.WriteLine($"Database unavailable ({operation}): {ex.Message}");

        return Results.Json(
            new { error = "Database unavailable. Please try again later." },
            statusCode: 503
        );
    }
}


// ======================================================
// Unified Log
// ======================================================

// пише один рядок у requests.
// якщо лог впав — запит користувача все одно віддаємо.
static async Task LogRequest(
    string conn,
    Guid id,
    string model,
    string promptVersion,
    int latency,
    int promptTokens,
    int completionTokens,
    decimal? cost,
    int status)
{
    try
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO requests
            (
                request_id,
                model,
                prompt_version,
                latency_ms,
                prompt_tokens,
                completion_tokens,
                cost_usd,
                status
            )
            VALUES
            (
                @id,
                @model,
                @promptVersion,
                @lat,
                @pt,
                @ct,
                @cost,
                @status
            )
            """,
            db
        );

        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue(
            "promptVersion",
            promptVersion
        );
        cmd.Parameters.AddWithValue("lat", latency);
        cmd.Parameters.AddWithValue("pt", promptTokens);
        cmd.Parameters.AddWithValue("ct", completionTokens);
        cmd.Parameters.AddWithValue(
            "cost",
            (object?)cost ?? DBNull.Value
        );
        cmd.Parameters.AddWithValue(
            "status",
            status.ToString()
        );

        await cmd.ExecuteNonQueryAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"Failed to write request log: {ex.Message}"
        );
    }
}


record ChatIn(string Message);