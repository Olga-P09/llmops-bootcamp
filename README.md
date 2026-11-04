# SupportGW: LLMOps Bootcamp capstone

SupportGW is a support chat gateway with an LLMOps control layer: Angular Operator Console, .NET service, LiteLLM gateway, mock provider, and PostgreSQL. Everything runs on the mock provider, so you don't need a real API key.

What the service does (built week by week):

| Area | What is implemented |
|---|---|
| Prompt registry | Versioned prompts in PostgreSQL, `GET /prompts`, `POST /prompts/{version}/activate` |
| Routing and cost | Complaints, refunds, and urgent requests go to `mock-strong`, everything else goes to `mock-mini`; tokens and cost are logged for each request; `GET /cost` |
| Cache | Exact in-memory cache; only successful answers without tool calls are cached |
| Tools and HITL | `lookup_order` runs right away; `create_ticket` waits in the approval queue until an operator approves it |
| Reliability | Fallback to the other model on 429, 5xx, and network errors; a polite degraded answer when all models fail |
| Safety | Email addresses are masked before routing, cache, model call, and approvals |
| Observability | `GET /observability` (requests, p95, error rate, cache hit, fallback events) and `GET /providers` (status and last check time per model), shown in the console |
| Quality gate | Golden Dataset (11 cases) and the required `eval` CI check |

## Quick start (clean clone)

```sh
git clone https://github.com/Olga-P09/llmops-bootcamp.git
cd llmops-bootcamp
docker compose up --build
```

Open the console at `http://localhost:4200`; the service API is at `http://localhost:8080`. If these ports are busy, start with shifted ports: `docker compose -f docker-compose.yml -f demo-ports.yml up --build -d`. Then the console is at `http://localhost:4300` and the API is at `http://localhost:8180`.

## Incident runbook

**➡ [INCIDENT_RUNBOOK.md](INCIDENT_RUNBOOK.md)** has a reproducible incident demo (provider outage → fallback → recovery, with exact commands and expected metrics) and the manual prompt rollback procedure.

## Eval quality gate (CI)

Every pull request runs the [`eval-gate`](.github/workflows/eval-gate.yml) workflow. Its job, `eval`, starts the stack, waits until the service and the gateway are ready, and runs the Golden Evals ([evals/golden.jsonl](evals/golden.jsonl), 11 cases) against `/chat`.

- **Threshold: 10 of 11.** Our HW5 rule is `ceil(0.85 · N)` for N > 10, so `ceil(9.35) = 10`. This leaves room for one flaky case. A broken prompt fails 5 cases (6/11), so it always fails the gate. The full calculation is in [evals/README.md](evals/README.md). The threshold is set in two places that must change together: the workflow and the local command in [GETTING_STARTED.md §6](GETTING_STARTED.md).
- **It blocks the merge.** A GitHub ruleset on `main` requires a pull request and the `eval` status check. The workflow has no `continue-on-error`, no `|| true`, and no `if: always()` on the Evals step. Note: this is a ruleset, not classic branch protection, so `gh api repos/Olga-P09/llmops-bootcamp/branches/main/protection` returns nothing. To see the rule, run `gh api repos/Olga-P09/llmops-bootcamp/rules/branches/main`. It lists `required_status_checks` with the context `eval`.
- **RED and GREEN runs in PR #7:** the prompt broken on purpose (v1 active) gave a [red run](https://github.com/Olga-P09/llmops-bootcamp/actions/runs/40940908553) (6/11). Restoring v2 gave a [green run](https://github.com/Olga-P09/llmops-bootcamp/actions/runs/40942253519) (11/11).

Run the same check locally (with the stack running):

```sh
SERVICE_URL=http://localhost:8080 python evals/run.py --dataset evals/golden.jsonl --threshold 10
```

Exit code 0 means the gate passes; 1 means it fails.

## 30-day rollout

How I would bring the course practices into my own team, one step per week:

| Week | Step | Acceptance criteria |
|---|---|---|
| 1 | Route all LLM calls through one gateway and move every production prompt into a versioned registry, with one log row per request (prompt version, model, tokens, cost, latency, status). | 100% of LLM calls have a log row with `prompt_version`; prompts change only through the registry; daily cost per feature is visible. |
| 2 | Build a Golden Dataset of 20–30 real, anonymized team cases (including negative and safety cases) and make the `eval` check required on `main`. | Threshold is `ceil(0.85 · N)`; a test PR with a broken prompt is red and can't be merged, and the fix is green. |
| 3 | Add reliability and safety controls: fallback to a second model, a degraded answer instead of errors, PII masking before the model call, and human approval for irreversible actions. | In a failure drill, users get no 5xx errors; 0 irreversible actions run without approval; no raw emails in logs or cache. |
| 4 | Launch the operator console and the incident runbook, and run one incident drill with the on-call team. | Console shows p95, error rate, cache hit, fallback events, cost, and provider health; an on-call engineer runs the drill from the runbook without help and records a timeline. |

## Why it matters (business view)

These are the KPIs I would track after the rollout:

- **Answer quality:** Golden Eval pass rate on every release (never below the gate).
- **Availability:** error rate and the share of degraded answers; with fallback, a single provider outage should not reach users as errors.
- **Cost:** cost per request and per day, and cache hit rate (every cache hit is a model call we don't pay for).
- **Safety:** irreversible actions without approval (target 0) and PII leaks into logs (target 0).
- **Incident response:** time from detection to recovery, using the runbook steps.

---

## Starter notes

Те, що студент клонує на першому уроці й дороблює до capstone.

**➡ Повний гайд для студента (вимоги, запуск, кожна папка, troubleshooting): [GETTING_STARTED.md](GETTING_STARTED.md).**
**➡ Домашні завдання — окремий файл на кожен тиждень: [homework/](homework/README.md).**

## Що дається готовим, що добудовує студент

| Компонент | Дається готовим | Студент добудовує |
|---|---|---|
| UI (Angular) | готові в'юхи: Chat + Console/Observability | не змінює |
| App service (.NET) | skeleton | control plane: routing, fallback, cost, policies |
| Observability API (.NET) | skeleton | агрегати з Postgres (traces, p95, cost, error-taxonomy) у Console-в'юху |
| Gateway (LiteLLM) | базовий конфіг | моделі/провайдери, порядок fallback |
| Mock provider | готовий | використовує для тестів і failure-сценаріїв |
| Postgres | базова схема | розширює під logs / cost / prompt records |
| Eval runner (Python) | skeleton | dataset, graders, пороги |
| CI (GitHub Actions) | шаблон `.github/workflows/eval-gate.yml` | eval gate |
| Redis / дашборди | опційний шаблон | advanced extension |

## Запуск (мінімальний стек)

```sh
docker compose up --build
```

Піднімаються: UI (`:4200`), сервіс (`:8080`), LiteLLM (`:4000`), Postgres, mock provider.
Для mock реальні ключі не потрібні. Redis і дашборди — опційні: `docker compose --profile advanced up`.
Для оцінки якості (model-based evals): `cp gateway/.env.example gateway/.env` і заповнити ключі.
