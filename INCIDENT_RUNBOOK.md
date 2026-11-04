# SupportGW incident runbook

## Purpose and scope

This runbook has two parts:

1. **Incident demo: provider outage → fallback → recovery.** A reproducible drill on a clean clone with the mock provider. Start here if you want to see the incident end to end.
2. **Operations reference.** How to detect and classify an incident, and how to manually restore a verified prompt version with the existing SupportGW API.

It covers the bootcamp Compose stack: Angular Operator Console, .NET service, LiteLLM gateway, mock provider, and PostgreSQL. It does not deploy application releases or restore database backups.

**IMPLEMENTED** means the behavior exists in this repository. **PROPOSED / NOT IMPLEMENTED** means an operator policy or a future feature, not an automatic safeguard. The prompt recovery commands change prompt activation only when an operator runs them on purpose against an identified environment.

For this exercise, v1 (`You are an assistant.`) is a deliberately bad prompt, and v2 (`You are a support assistant. Be concise and helpful.`) is the verified good prompt. Earlier local and CI runs gave v1: 6/11, exit 1; v2: 11/11, exit 0. Do not choose a rollback target only because it is older. These results do not certify a different production prompt or model.

## Environment and settings

Prerequisites: Docker with Compose 2.24 or later (`demo-ports.yml` uses `!override`), `curl` (for bash and zsh), and Python 3 (only for the Golden Evals in Part 2).

Run all commands from the repository root of the stack you mean to use. Compose takes the project name from the folder name. So a clean clone in a new folder gets its own containers and its own database volume. You don't need a `-p` flag.

**Do not run the incident demo on your working stack.** It adds failed requests to today's metrics and marks both mock models as `down` until each one answers again. Use a clean clone. Two stacks can't use the same host ports. Either stop the other stack first (`docker compose down` in its folder, without `-v`), or start the clean clone with shifted ports.

| Setting | Default ports (`docker-compose.yml`) | Shifted ports (`+ demo-ports.yml`) |
|---|---|---|
| Start command | `docker compose up --build -d` | `docker compose -f docker-compose.yml -f demo-ports.yml up --build -d` |
| Service API | `http://localhost:8080` | `http://localhost:8180` |
| Gateway | `http://localhost:4000` | `http://localhost:4100` |
| Operator Console | `http://localhost:4200` | `http://localhost:4300` |

The commands below use the default ports. If you started with shifted ports, change only the settings lines, as shown in the comments. Commands like `docker compose logs` and `docker compose exec` work the same with both options, because the project (folder) is the same and they don't use host ports.

**bash or zsh** (Linux, macOS, WSL, Git Bash):

```bash
SERVICE_URL=http://localhost:8080   # shifted ports: http://localhost:8180
GATEWAY_URL=http://localhost:4000   # shifted ports: http://localhost:4100
CONSOLE_URL=http://localhost:4200   # shifted ports: http://localhost:4300
```

**PowerShell** (Windows):

```powershell
$serviceUrl = 'http://localhost:8080'   # shifted ports: 'http://localhost:8180'
$gatewayUrl = 'http://localhost:4000'   # shifted ports: 'http://localhost:4100'
$consoleUrl = 'http://localhost:4200'   # shifted ports: 'http://localhost:4300'
$composeArgs = @()                      # shifted ports: @('-f', 'docker-compose.yml', '-f', 'demo-ports.yml')
```

## Part 1. Incident demo: provider outage → fallback → recovery

**Scenario.** The LLM provider starts returning HTTP 503. The mock provider fails on purpose when the user message contains `__fail_503`, and it fails for both mock models (`mock-mini` and `mock-strong`). This is what SupportGW does (all IMPLEMENTED, see [service/Program.cs](service/Program.cs)):

- The first model returns 503. The service moves to the next model in the chain, and `fallback_events` goes up by 1.
- The backup model also returns 503. The user gets the polite degraded answer (`Вибачте, зараз у нас тимчасова проблема з відповідями…`) with HTTP 200, not a 500.
- The request is logged with a 5xx status, so `error_rate_pct` goes up. Each model's failure streak grows: 1–2 failures → `degraded`, 3 or more → `down`.
- Failed answers are never cached.

**Recovery** means that after the outage, a **new** question (never asked before, so not in the cache) gets a normal answer from the model. `error_rate_pct` then goes down. `fallback_events` is a cumulative counter, so it never goes down. It keeps showing the spike. A repeated question is not proof of recovery: the cache answers it without calling any model (`cached: true`, latency close to 0).

### Step 1. Start a clean clone

```bash
git clone https://github.com/Olga-P09/llmops-bootcamp.git supportgw-clean
cd supportgw-clean
git checkout homework/week6   # only while PR #7 is open; after the merge, stay on main
docker compose up --build -d
```

If the default ports are busy, use the shifted-ports start command from the table above instead of the last line. Then run the settings block for your shell from [Environment and settings](#environment-and-settings), in the same terminal.

### Step 2. Wait until the service and the gateway are ready

A cold LiteLLM gateway can fail the first request. We warm it up directly (like the CI workflow does), so the warm-up does not change the service metrics.

bash or zsh:

```bash
for i in $(seq 1 40); do if curl -sf "$SERVICE_URL/health"; then echo; break; fi; sleep 5; done
for i in $(seq 1 40); do
  code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$GATEWAY_URL/v1/chat/completions" \
    -H "Content-Type: application/json" \
    -d '{"model":"mock","messages":[{"role":"user","content":"hi"}]}')
  if [ "$code" = "200" ]; then echo "gateway ready"; break; fi
  sleep 5
done
```

PowerShell:

```powershell
foreach ($i in 1..40) { try { Invoke-RestMethod "$serviceUrl/health" | Out-Null; 'service ready'; break } catch { Start-Sleep 5 } }
$gatewayBody = '{"model":"mock","messages":[{"role":"user","content":"hi"}]}'
foreach ($i in 1..40) { try { Invoke-RestMethod -Method Post -Uri "$gatewayUrl/v1/chat/completions" -ContentType 'application/json' -Body $gatewayBody | Out-Null; 'gateway ready'; break } catch { Start-Sleep 5 } }
```

Expected: `{"status":"ok"}` (or `service ready`), then `gateway ready`.

### Step 3. Define helper commands

`chat` sends one message to `POST /chat`. `state` prints the console metrics and the provider health. `RUN_ID` makes every demo question unique, so you can repeat the demo and the questions will still not be cached.

bash or zsh (the message body goes to `curl` through stdin, so Ukrainian text also arrives correctly in Git Bash on Windows):

```bash
RUN_ID=$(date +%Y%m%d%H%M%S)
chat() {
  printf '{"message": "%s"}' "$1" | curl -s -X POST "$SERVICE_URL/chat" \
    -H "Content-Type: application/json" --data-binary @-; echo
}
state() {
  curl -s "$SERVICE_URL/observability"; echo
  curl -s "$SERVICE_URL/providers"; echo
}
```

PowerShell:

```powershell
$runId = Get-Date -Format 'yyyyMMddHHmmss'
function chat([string]$Message) {
  $json = @{ message = $Message } | ConvertTo-Json -Compress
  Invoke-RestMethod -Method Post -Uri "$serviceUrl/chat" -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) | Format-List content, cached, latency_ms, request_id
}
function state {
  Invoke-RestMethod "$serviceUrl/observability" | Format-List
  (Invoke-RestMethod "$serviceUrl/providers").providers | Format-Table name, status, last_checked_at
}
```

### Step 4. Baseline: a normal question works

bash or zsh:

```bash
chat "Привіт, перевірка перед інцидентом $RUN_ID"
state
```

PowerShell:

```powershell
chat "Привіт, перевірка перед інцидентом $runId"
state
```

Expected: `content` is the greeting `Вітаю! Розкажіть, що сталося — допоможу розібратися.`, and `cached` is `false`. In the metrics, `fallback_events` is 0 and `error_rate_pct` is 0. `mock-mini` is `ok` and has a `last_checked_at` time. `mock-strong` is `ok` with `last_checked_at: null` (not called yet).

### Step 5. Incident: the provider returns 503

Send three failing requests, then check the metrics and the service log.

bash or zsh:

```bash
for i in 1 2 3; do chat "__fail_503 How do I reset my password? $RUN_ID"; done
state
docker compose logs --since 10m service | grep -E "FALLBACK|DEGRADED"
```

PowerShell:

```powershell
foreach ($i in 1..3) { chat "__fail_503 How do I reset my password? $runId" }
state
docker compose @composeArgs logs --since 10m service | Select-String 'FALLBACK|DEGRADED'
```

Expected:

- Each answer is the degraded message `Вибачте, зараз у нас тимчасова проблема з відповідями. Спробуйте, будь ласка, трохи пізніше.`, with `cached: false`. The service does not return an error to the user.
- `fallback_events` went up by 3 (one `mock-mini -> mock-strong` transition per request).
- `error_rate_pct` went up (75.0 on a fresh clone, see the timeline below).
- Both `mock-mini` and `mock-strong` are `down` (3 failures in a row each).
- The log has three `FALLBACK mock-mini -> mock-strong` lines and three `DEGRADED` lines with `chain=(mock-mini -> mock-strong)`. The `status=` value in these lines is the gateway's 5xx code (503 is expected).

Now open the Operator Console (`$CONSOLE_URL` / `$consoleUrl`, Console view, refreshes every 4 seconds). The **Error rate** tile is red (above 5%). The **Fallback** tile shows the spike. Both providers show `down`.

### Step 6. Recovery: new questions get normal answers

The outage ends when messages no longer contain `__fail_503`. Ask two **new** questions. The first one goes to `mock-mini`. The second one contains `скарга` (complaint), so routing sends it to `mock-strong`. This way each model proves its own recovery: a model's `down` status is reset only by its own successful call.

bash or zsh:

```bash
chat "Привіт, перевірка після інциденту $RUN_ID"
chat "У мене скарга, перевірка резервної моделі $RUN_ID"
state
```

PowerShell:

```powershell
chat "Привіт, перевірка після інциденту $runId"
chat "У мене скарга, перевірка резервної моделі $runId"
state
```

Expected:

- First answer: `Вітаю! Розкажіть, що сталося — допоможу розібратися.`, `cached: false`.
- Second answer: `Уточніть, будь ласка, деталі — і я допоможу.`, `cached: false`. No ticket and no approval is created.
- `error_rate_pct` went down compared to Step 5.
- `fallback_events` did not change (cumulative counter).
- Both providers are `ok` again, with newer `last_checked_at` times.

In the console, the Error rate tile shows a lower value, the Fallback tile keeps the spike, and both providers are `ok`.

### Step 7 (optional). Why the recovery question must be new

Repeat the first recovery question:

bash or zsh:

```bash
chat "Привіт, перевірка після інциденту $RUN_ID"
state
```

PowerShell:

```powershell
chat "Привіт, перевірка після інциденту $runId"
state
```

Expected: the same answer with `cached: true` and `latency_ms` close to 0. `cache_hit_pct` is now above 0. The cache answered without calling any model. That's why a repeated question can look "green" in the middle of an outage, and why recovery is checked only with new questions.

### Expected timeline on a fresh clone

The numbers assume that the demo is the only traffic in this database today (a clean clone, steps run once, in order). `error_rate_pct` and `requests` count today's rows in the `requests` table (PostgreSQL `CURRENT_DATE`). `fallback_events` and `cache_hit_pct` are process counters, and a service restart resets them. If the database already has traffic from today, the exact numbers will be different, but the direction must be the same.

| Time | Event | requests | error_rate_pct | fallback_events | cache_hit_pct | mock-mini | mock-strong |
|---|---|---|---|---|---|---|---|
| T0 | Baseline question (Step 4) | 1 | 0.0 | 0 | 0 | ok | ok (never checked) |
| T1 | 3 × `__fail_503` (Step 5) | 4 | 75.0 | 3 | 0 | down | down |
| T2 | 2 new questions (Step 6) | 6 | 50.0 | 3 | 0 | ok | ok |
| T3 | Repeated question, cache hit (Step 7) | 7 | 42.9 | 3 | 14.3 | ok | ok |

To keep evidence for the incident record, save the output of `state` after each step and take a console screenshot at T1 and T2. To find the demo rows in the request log (the same command works in bash, zsh, and PowerShell):

```bash
docker compose exec -T postgres psql -U llmops -d llmops -c "SELECT created_at, model, prompt_version, latency_ms, cost_usd, status FROM requests ORDER BY created_at DESC LIMIT 10;"
```

Expected: the three incident rows have `model = mock-strong` (the last model tried), a 5xx `status`, and empty `cost_usd` (unknown cost, not 0). The recovery rows have `status = 200` and a cost. The cache-hit row has `cost_usd = 0`.

### Step 8. Stop the clean clone

```bash
docker compose down
```

Run it in the clean clone folder. It works for both port options. Without `-v`, the database volume is kept. The volume belongs only to this clone and does not touch your working stack.

## Part 2. Operations reference

### Identify the environment

Run the PowerShell examples in order, in one session, from the repository root, after the settings block from [Environment and settings](#environment-and-settings). You need access to the intended service and database, and operator authorization to change that environment. The stack must already be running. Do not start, restart, rebuild, stop, or reset it as a side step of prompt recovery. Stop if any command fails. A failed check does not give permission to continue with activation.

First inspect the environment without making changes:

```powershell
git branch --show-current
git rev-parse HEAD
git status --short
docker compose ls
docker ps --format '{{.Names}} {{.Ports}}'
```

Confirm that the settings match the stack you mean to work on:

```powershell
docker compose @composeArgs ps
docker compose @composeArgs port service 8080
docker compose @composeArgs port postgres 5432
```

Stop if the project (folder), ports, or database don't match the intended environment.

Record the project, URLs, Git SHA, incident start time in UTC, configured model, and database volume identity in the incident record. Inspect mounts without exposing gateway credentials:

```powershell
$dbContainer = docker compose @composeArgs ps -q postgres
if ($LASTEXITCODE -ne 0 -or -not $dbContainer) { throw 'Cannot identify PostgreSQL container' }
docker inspect --format '{{json .Mounts}}' $dbContainer
docker compose @composeArgs exec -T service printenv MODEL
```

Exercise results assume `MODEL=mock`. Do not switch a real-model environment to mock as an incident shortcut. Do not print environment files, API keys, or full container environments into incident evidence.

## Detection tools: IMPLEMENTED

| Tool | What it provides | Interpretation |
|---|---|---|
| `GET /health` | HTTP 200 with `status: ok` | Service liveness only; no database or provider readiness test. |
| `GET /observability` | `requests`, `p95_ms`, `error_rate_pct`, `cache_hit_pct`, `fallback_events` | Database metrics are for today; cache/fallback metrics belong to the current service process. |
| `GET /providers` | Each model's `name`, `status`, `last_checked_at` | Observed calls, not active background probes. |
| `GET /cost` | `today_usd`, `budget_usd` | Recorded daily spend and a fixed $5 display budget; no budget enforcement. |
| Operator Console | Polls the API every four seconds; shows metrics, prompt activation, provider health, and approvals | p95 above 1,500 ms and error rate above 5% are visual warnings, not automated alerts or rollback criteria. |
| PostgreSQL `requests` | Request ID/time, model, prompt version, latency, token counts, cost, status | One record per logged chat request, not one record per provider attempt. |
| Container logs | Cache decisions, fallback transitions, degraded responses, provider/database errors, approval actions | Use request IDs where available; not all messages contain a correlation ID. |
| Golden Evals | Rule-based answer checks through `/chat` | Unchanged gate: at least 10 of 11 cases; exit 0 passes, exit 1 fails. |

Capture a baseline and inspect recent evidence:

```powershell
$detectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
Invoke-RestMethod "$serviceUrl/health"
$beforeObservability = Invoke-RestMethod "$serviceUrl/observability"
$beforeProviders = Invoke-RestMethod "$serviceUrl/providers"
$beforeCost = Invoke-RestMethod "$serviceUrl/cost"
$beforeObservability | ConvertTo-Json
$beforeProviders | ConvertTo-Json -Depth 4
$beforeCost | ConvertTo-Json
docker compose @composeArgs logs --since 15m --tail 200 service gateway mock-provider postgres
docker compose @composeArgs exec -T postgres psql -v ON_ERROR_STOP=1 -U llmops -d llmops -c "SELECT request_id, created_at, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status FROM requests ORDER BY created_at DESC LIMIT 20;"
if ($LASTEXITCODE -ne 0) { throw 'Request-log inspection failed' }
```

The 15-minute log selection and 20-row query are manual evidence samples, not implemented alerting windows. Database-backed endpoints return 503 on handled database failures; missing data is not evidence of zero errors.

### Observability limitations

- `error_rate_pct` is the percentage of today's logged rows whose stored status differs from the string `200`. That status is the final provider HTTP status, 0 when no response was obtained for the final attempt, or 200 for a cache hit; it is not the client-facing `/chat` status or an answer-quality score.
- Graceful degradation can return HTTP 200 to the client. A malformed provider response can retain stored status 200 even though parsing failed. A successful fallback can also leave status 200 after an earlier attempt failed. These cases are not fully represented by the error percentage.
- Daily totals and percentiles use PostgreSQL `CURRENT_DATE`, so the database timezone determines the boundary. They can hide a recent regression and do not provide a rolling incident window, per-version comparison, or canary comparison. Cache hits are included in request totals and latency statistics. Logged latency is measured before the request-log database write, so it is not full client-observed response time.
- Provider health counts consecutive retryable failures per model: 0 means `ok`, 1-2 means `degraded`, and 3 or more means `down`. Only that model's successful call resets its streak. Non-retryable/format errors refresh its timestamp without changing the streak. Cache hits do neither.
- Provider status starts at `ok` with a null timestamp; the console shows `Never checked`. Old timestamps are stale evidence. An uncalled model may remain `down` after another model recovers. There is no background probing or automatic expiry of health evidence.
- `fallback_events` is cumulative per process and increments on transitions between models. It does not decrease during recovery. Compare snapshots from the same process; restarting resets it, provider health, cache, and cache counters.
- Database log writes are best effort. Initial prompt-load failures return before request logging, and log-write failures are reported only to stderr. Missing records can understate traffic and errors. The schema's `provider` column is not populated by the request logger.
- Cost is estimated from configured mock prices. Failed/unpriced calls can have null cost, which the SQL sum excludes. A small recorded total does not prove actual spend is small.
- Golden Evals exercise only their fixed cases. Cached successes can hide provider availability problems, and a passing score does not prove universal safety or production readiness.

## Rollback decisions: PROPOSED / NOT IMPLEMENTED

These are manual response policies. No alert service, production monitoring window, production threshold, automatic rollback, or canary traffic splitting is implemented. Console warning values are not validated production SLOs. Operators must agree on observation windows, minimum traffic samples, baselines, escalation owners, and production thresholds before using them operationally.

| Signal | Proposed decision criterion | Proposed action |
|---|---|---|
| Prompt quality regression | Evals fall below the existing 10/11 gate, or reproducible functional failures correlate with a prompt change. First distinguish quality failures from connectivity/readiness errors. | Reject the candidate release. For an already active regression, restore a verified good prompt and recheck behavior. Do not lower the gate or edit expectations. |
| Safety incident | A confirmed unsafe response or unauthorized action occurs, even if overall evals pass. | Escalate immediately and pause manual approvals of suspect actions. Restore a prompt only if evidence supports that mitigation; preserve evidence and investigate completed actions separately. There is no implemented traffic stop or action reversal. |
| Provider availability | Fresh retryable failures, fallback transitions, degraded responses, or recently checked `down` models show ongoing impact. | Investigate provider/gateway/network availability. Existing fallback handles eligible mock-model failures. Prompt rollback is appropriate only if the prompt change is implicated; it cannot repair an unavailable provider or database. |
| Latency degradation | Fresh uncached requests show sustained deterioration against an agreed baseline, corroborated by logs and request timing. | Diagnose provider, gateway, database, and prompt changes. Restore a prompt only when evidence links it to the slowdown. Daily p95 alone is insufficient. Production timing windows and thresholds remain unimplemented. |
| Unexpected cost | Recorded spend or usage departs materially from an agreed budget/baseline, with null/unpriced calls considered. | Investigate traffic, routing, cache behavior, and token usage. Restore a prompt if its change caused excess usage. Budget notifications, spend limits, and automatic model switching are not implemented. |

Do not use a service restart to make health indicators look healthy. Do not approve pending tool actions as a recovery test. Prompt recovery does not undo actions already completed.

## Manual prompt recovery: existing API

Perform these steps only after identifying the environment, recording evidence, and deciding that prompt restoration is appropriate. The commands use the variables established above. Do not execute v1 activation as part of real recovery; v1 is the intentionally bad exercise version.

### 1. Record the active prompt before changing anything

```powershell
$promptsBefore = @((Invoke-RestMethod "$serviceUrl/prompts" -ErrorAction Stop) | ForEach-Object { $_ })
$activeBefore = @($promptsBefore | Where-Object { $_.name -eq 'support-system' -and $_.active })
$promptsBefore | ConvertTo-Json -Depth 4
if ($activeBefore.Count -ne 1) { throw 'Expected exactly one active support-system prompt; investigate before changing it' }
$activeBefore | ConvertTo-Json
docker compose @composeArgs exec -T postgres psql -v ON_ERROR_STOP=1 -U llmops -d llmops -c "SELECT name, version, body, active, created_at FROM prompts WHERE name = 'support-system' ORDER BY created_at;"
if ($LASTEXITCODE -ne 0) { throw 'Prompt-body inspection failed; do not activate a version' }
```

Record the active version and relevant prompt contents in approved incident storage, redacting sensitive content if necessary. `GET /prompts` returns name/version/active, not the prompt body. The explicit pipeline above enumerates the JSON array before filtering, including in Windows PowerShell; each filter must inspect one prompt object, not an array of prompts.

### 2. Identify and activate the verified good version

For this repository's mock exercise, choose v2 based on its verified behavior. In another environment, explicitly identify and validate its own known-good version first. Prompt version labels alone are not evidence that stored bodies are unchanged.

```powershell
$knownGoodVersion = 'v2'
$knownGood = @($promptsBefore | Where-Object { $_.name -eq 'support-system' -and $_.version -eq $knownGoodVersion })
if ($knownGood.Count -ne 1) { throw 'Known-good prompt is absent or ambiguous; stop' }
$activationAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
$activation = Invoke-RestMethod -Method Post -Uri "$serviceUrl/prompts/$knownGoodVersion/activate" -ErrorAction Stop
if ($activation.version -ne $knownGoodVersion -or -not $activation.active) { throw 'Unexpected activation response' }
$activation | ConvertTo-Json
```

The API updates activation in one SQL statement: the requested existing `support-system` version becomes active and the others inactive. Unknown versions return 404 without changing activation. Database errors must be investigated rather than bypassed. The service reads the active prompt on each chat request; a rebuild or restart is unnecessary. Requests already in flight may finish using the earlier prompt.

### 3. Verify the registry immediately

```powershell
$promptsAfter = @((Invoke-RestMethod "$serviceUrl/prompts" -ErrorAction Stop) | ForEach-Object { $_ })
$activeAfter = @($promptsAfter | Where-Object { $_.name -eq 'support-system' -and $_.active })
$promptsAfter | ConvertTo-Json -Depth 4
if ($activeAfter.Count -ne 1 -or $activeAfter[0].version -ne $knownGoodVersion) { throw 'Prompt restoration is not confirmed' }
```

### 4. Run the unchanged Golden Evals

```powershell
$env:SERVICE_URL = $serviceUrl
python evals/run.py --dataset evals/golden.jsonl --threshold 10
$evalExitCode = $LASTEXITCODE
Write-Output "Golden Eval exit code: $evalExitCode"
if ($evalExitCode -ne 0) { throw 'Golden Evals failed; record the output and investigate before proceeding' }
```

Record the actual score printed by the runner and the exit code. For mock v2, the verified baseline is 11/11 and exit 0; the gate passes at 10/11. If the exit code is nonzero, do not declare recovery: investigate failed cases, readiness, and logs without changing the dataset, grader, or threshold. Do not repeatedly switch prompt versions without evidence.

Evals send real `/chat` requests: they add request logs/cost observations, affect counters/cache, and can create pending mock ticket approvals. They do not approve those tickets. Use a clean clone (see [Part 1](#part-1-incident-demo-provider-outage--fallback--recovery)) for repeatable failure demos; production validation needs a separately approved test policy.

### 5. Verify a fresh functional response, logs, and metrics

Make a unique, non-tool mock request so a previous cached answer cannot certify provider recovery:

```powershell
$probeId = [Guid]::NewGuid().ToString('N')
$probeBody = @{ message = "hello recovery verification $probeId" } | ConvertTo-Json
$probe = Invoke-RestMethod -Method Post -Uri "$serviceUrl/chat" -ContentType 'application/json' -Body $probeBody -ErrorAction Stop
$probe | ConvertTo-Json
if ($probe.cached -ne $false) { throw 'Probe did not demonstrate an uncached provider call' }
$probeRequestId = [Guid]$probe.request_id
docker compose @composeArgs exec -T postgres psql -v ON_ERROR_STOP=1 -U llmops -d llmops -c "SELECT request_id, created_at, model, prompt_version, latency_ms, prompt_tokens, completion_tokens, cost_usd, status FROM requests WHERE request_id = '$probeRequestId'::uuid;"
if ($LASTEXITCODE -ne 0) { throw 'Probe-log inspection failed; recovery is not confirmed' }
docker compose @composeArgs logs --since 5m --tail 100 service
Invoke-RestMethod "$serviceUrl/health"
Invoke-RestMethod "$serviceUrl/observability" | ConvertTo-Json
Invoke-RestMethod "$serviceUrl/providers" | ConvertTo-Json -Depth 4
Invoke-RestMethod "$serviceUrl/cost" | ConvertTo-Json
Invoke-RestMethod "$serviceUrl/prompts" | ConvertTo-Json -Depth 4
```

Inspect the response for a meaningful greeting rather than the degraded message or `не знаю`. Confirm the request row exists, uses v2 for the exercise, has positive prompt tokens, and records status 200. A missing row is an observability gap, not successful verification. Check the console at `$consoleUrl` as well.

In mock mode this greeting initially routes to `mock-mini`, but fallback can produce a successful answer from `mock-strong`. Inspect the request row's model, fallback logs, and provider timestamps to identify which model actually succeeded; success through the backup does not prove the primary recovered. The probe does not establish that both models are healthy. Only each model's own successful call can demonstrate its recovery. Do not reset both models or treat an old timestamp as fresh evidence. A falling daily error percentage can support recovery, but is not required to change visibly after one request; fallback counts should not be expected to decrease. Capture counter resets if a restart occurred independently.

## Seed changes and existing databases

`db/schema.sql` is mounted as a PostgreSQL initialization script. It runs when PostgreSQL initializes a fresh data directory. Editing or reverting its activation flags does **not** change the active prompt in an existing database, and restarting containers does not rerun initialization against an established volume.

Use the existing activation API for an existing database. Never delete/reset a working database volume to activate a prompt. A Git revert of seed data is evidence of a fresh-environment default change, not proof that runtime activation has been restored.

## Incident record template

```text
Incident ID / title:
Operator / incident owner:
Environment: Compose project, service URL, console URL, database volume:
Git SHA / configured model:
Detection time (UTC) / report source:
User impact / affected functions:
Active prompt before change / body verification:
Known-good target / evidence supporting selection:
Timeline (UTC): detection, decision, activation, verification:
Symptoms and classification: quality, safety, provider, latency, cost, database:
Request IDs / redacted response examples / relevant log excerpts:
Before/after metrics and provider last_checked_at values:
Missing logs, null costs, stale checks, or counter resets:
Decision rationale / approving operator:
API activation response / registry confirmation:
Golden Eval score, threshold (10/11), exit code, workflow URL if relevant:
Fresh probe result / logged prompt version:
Pending or completed actions requiring separate review:
Residual impact / recovery decision / closure time (UTC):
Root cause or current hypothesis:
Follow-up actions / owners / due dates:
```

## Recovery checklist

- [ ] Confirm the exact environment and database before any activation.
- [ ] Record the previous active prompt, impact, timestamps, and evidence.
- [ ] Separate prompt regression from provider/database availability failures.
- [ ] Select a verified known-good prompt; v2 is the exercise target, not v1.
- [ ] Activate through the existing API and verify exactly one intended active version.
- [ ] Record unchanged Golden Eval results: score, threshold 10/11, exit code.
- [ ] Verify meaningful uncached behavior and the corresponding logged prompt version.
- [ ] Inspect provider timestamps and available metrics with their limitations in mind.
- [ ] Review pending/completed actions separately; do not approve them to prove recovery.
- [ ] Record unresolved evidence gaps and the operator's recovery decision.
- [ ] Leave databases and unrelated resources intact; do not reset health by restarting.

**PROPOSED / NOT IMPLEMENTED:** final incident closure should also require an agreed observation period, sample size, owner approval, and follow-up review. No automated closure or production observation window exists today.

## Current limitations and future production improvements

The current implementation has no background health probes, alert delivery, automatic prompt rollback, canary traffic splitting, deployment rollback workflow, or production SLO enforcement. The console polls rather than alerting. The default HTTP client has no application-specific timeout budget or retry loop. Fallback is between the two mock aliases; real-model mode has no configured alternate model in the service fallback chain.

Prompt activation has no application authentication/authorization or dedicated durable audit history. Prompt bodies are not enforced as immutable, and the database has no unique constraint ensuring only one active row per prompt name outside the activation API. If multiple rows are active, chat selects the newest by creation time. With no active row, it uses version `none` and a generic prompt; this is not a known-good recovery mode.

There is no exposed cache bypass/clear API, cache TTL, persistent provider health, or durable approval queue. Cached responses may reappear when an earlier prompt body is restored. Request records omit individual attempt outcomes and an explicit answer-quality/degradation field; early failures and failed log writes can be absent. Provider health does not recognize semantic quality failures. Prompt activation cannot undo completed side effects.

**PROPOSED / NOT IMPLEMENTED production improvements:** authenticated operator access; audited activation with immutable prompt artifacts; single-active-version database enforcement; durable approvals; rolling metrics by prompt/model and per-attempt outcomes; explicit degraded/quality signals; alert routing with agreed thresholds/windows/sample counts; application timeout budgets; controlled fresh-probe support; bounded cache lifetime; cost accounting for unpriced calls; and a separately approved canary/rollback design. These are future work, not claims about the existing stack or changes required by this documentation.

## Implementation references

- [Service endpoints, prompt activation, request logging, and routing](service/Program.cs)
- [Provider health state and timestamps](service/ProviderHealth.cs)
- [Fallback eligibility](service/Fallback.cs)
- [Database schema and fresh initialization defaults](db/schema.sql)
- [Operator Console](ui/src/app/console.component.ts)
- [Mock provider failure markers (`__fail_503` and others)](mock-provider/Program.cs)
- [Golden Eval runner](evals/run.py) and [gate explanation](evals/README.md)
- [Existing failure scenarios](docs/failure-scenarios.md)
- [Compose configuration](docker-compose.yml) and [shifted ports](demo-ports.yml)
