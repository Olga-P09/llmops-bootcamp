// Сервіс — це наш control plane. LiteLLM тільки ходить у модель, а рішення
// (яку модель брати, коли ретраїти, скільки коштує) робимо тут.
// MODEL=mock — дефолт, грошей не треба.
// MODEL=gpt-5-mini або claude-haiku + ключ у gateway/.env — реальна модель.

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

app.MapPost("/chat", async (ChatIn body, IHttpClientFactory httpFactory) =>
{
    var requestId = Guid.NewGuid();
    var startedAt = DateTimeOffset.UtcNow;

    // guardrails (W4): тут перевірити вхід на PII / інʼєкції. поки нічого.
    // TODO(student, W4)

    // routing (W2): поки одна модель


  var model = Route(body.Message, defaultModel); // TODO(student, W2)

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

    // cache (W3): перед викликом глянути в Redis
    // TODO(student, W3)

    // fallback (W4): якщо 429/5xx — піти на іншого провайдера
    // TODO(student, W4)

    var payload = JsonSerializer.Serialize(new
    {
        model,
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

    var http = httpFactory.CreateClient();

    var answer = "";
    string? toolCall = null;

    int promptTokens = 0;
    int completionTokens = 0;
    int status = 0; // 0 = відповіді не було

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

        var rawJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(rawJson);

        var message = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message");

        answer = message
            .GetProperty("content")
            .GetString() ?? "";

        // tools + HITL (W3/W4)
        // TODO(student, W3/W4)
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
        }

        var usage = doc.RootElement.GetProperty("usage");

        promptTokens = usage
            .GetProperty("prompt_tokens")
            .GetInt32();

        completionTokens = usage
            .GetProperty("completion_tokens")
            .GetInt32();
    }
    catch
    {
        // мережа/gateway недоступні або відповідь не розпарсилась
        // TODO(student, W4): graceful degradation
        answer = "Сервіс тимчасово недоступний.";
    }

    var latencyMs =
        (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
          
// cost (W2): usage -> model price -> cost_usd
decimal? costUsd = null;

if (prices.TryGetValue(model, out var price))
{
    costUsd = Math.Round(
        promptTokens / 1000m * price.InPer1K
        + completionTokens / 1000m * price.OutPer1K,
        6
    );
}
    // W1: записуємо prompt_version у unified log
    await LogRequest(
        dbConn,
        requestId,
        model,
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
        latency_ms = latencyMs
    });
});

// W2: routing policy
// fallback chain: mock-strong -> mock-mini -> controlled error
// budget policy: at 80% of daily budget, raise an alert and keep routing unchanged
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
app.MapGet("/prompts", async () =>
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
});


// POST /prompts/{version}/activate
// Атомарно робить вказану версію активною,
// а всі інші версії support-system — неактивними.
app.MapPost("/prompts/{version}/activate", async (string version) =>
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
});


// ======================================================
// Інші endpoints
// ======================================================

app.MapGet(
    "/health",
    () => Results.Ok(new { status = "ok" })
);

app.MapGet(
    "/observability",
    () => Results.Json(
        new { todo = "aggregate from requests table" }
    )
); // W5

app.MapGet("/cost", async () =>
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
});// W2/W5

app.MapGet(
    "/providers",
    () => Results.Json(
        new { todo = "provider health" }
    )
); // W5

app.MapGet(
    "/approvals",
    () => Results.Json(
        new { todo = "pending HITL approvals" }
    )
); // W4


app.Run("http://0.0.0.0:8080");


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