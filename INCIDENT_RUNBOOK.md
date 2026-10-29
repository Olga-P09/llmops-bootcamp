# SupportGW incident runbook

## Purpose and scope

Use this runbook to investigate an incident and manually restore a verified prompt version using the existing SupportGW API. It covers the bootcamp Compose stack: Angular Operator Console, .NET service, gateway, mock provider, and PostgreSQL. It does not deploy application releases or restore database backups.

**IMPLEMENTED** means behavior present in this repository. **PROPOSED / NOT IMPLEMENTED** means an operator policy or future capability, not an automatic safeguard. The recovery commands below change prompt activation only when an operator deliberately executes them against an identified environment.

For this exercise, v1 (`You are an assistant.`) is deliberately bad and v2 (`You are a support assistant. Be concise and helpful.`) is the verified known-good prompt. Earlier local and CI verification produced v1: 6/11, exit 1; v2: 11/11, exit 0. Do not choose a rollback target solely because it is older, and do not assume these results certify a different production prompt or model.

## Environment identification and prerequisites

Run the PowerShell examples in order, in one session, from the repository root. Prerequisites are Docker with Compose 2.24 or later (the shifted-port file uses `!override`), Python 3 available as `python`, access to the intended service and database, and operator authorization to change that environment. The stack must already be running. Do not start, restart, rebuild, stop, or reset it as an incidental part of prompt recovery. Stop if any command fails; a failed inspection is not permission to continue with activation.

First inspect the environment without making changes:

```powershell
git branch --show-current
git rev-parse HEAD
git status --short
docker compose ls
docker ps --format '{{.Names}} {{.Ports}}'
```

The following settings identify the existing local project with shifted ports. Confirm them against the inventory above before continuing:

```powershell
$serviceUrl = 'http://localhost:8180'
$consoleUrl = 'http://localhost:4300'
$composeArgs = @('-p', 'llmops-bootcamp', '-f', 'docker-compose.yml', '-f', 'demo-ports.yml')
docker compose @composeArgs ps
docker compose @composeArgs port service 8080
docker compose @composeArgs port postgres 5432
```

For a stack using only the default Compose file, explicitly replace these settings with service URL `http://localhost:8080`, console URL `http://localhost:4200`, and `$composeArgs = @('-p', 'llmops-bootcamp', '-f', 'docker-compose.yml')`. If the project name differs, replace `llmops-bootcamp` with the verified name. Stop if ports, project, or database identity do not match the intended environment. An isolated exercise must use its own confirmed project and database, not these working-stack defaults.

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

Evals send real `/chat` requests: they add request logs/cost observations, affect counters/cache, and can create pending mock ticket approvals. They do not approve those tickets. Use the isolated exercise environment for repeatable destructive-failure demonstrations; production validation needs a separately approved test policy.

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
- [Golden Eval runner](evals/run.py) and [gate explanation](evals/README.md)
- [Existing failure scenarios](docs/failure-scenarios.md)
- [Compose configuration](docker-compose.yml) and [shifted ports](demo-ports.yml)
