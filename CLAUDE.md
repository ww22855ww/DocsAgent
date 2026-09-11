# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A demo of agentic document processing, built to be shown at a department
meeting. A user types one natural-language task ("處理今天 SCM 文件") and the
agent logs into a mock portal, downloads documents, parses and classifies them,
maps suppliers and parts against internal data, archives the results, and sends
anything it cannot handle to manual review.

`roadmap.md` is the source of truth for scope, design decisions, and phase
order. Read it before making architectural changes. It is written in Traditional
Chinese; the user communicates in Traditional Chinese, so respond in kind.

**Phase discipline:** work proceeds one phase at a time. The user has asked to
review and approve each phase before the next one starts. Do not begin the next
phase without explicit approval, even when the current phase clearly succeeded.

## Commands

Everything runs through Docker Compose from the repo root. There is no local
build; the Dockerfiles are the build.

```bash
docker compose build                 # build all four custom images
docker compose up -d                 # start all five services
docker compose up -d --build <svc>   # rebuild and restart one service
docker compose ps                    # state + health of each service
docker compose logs <svc> --tail 50
docker compose down                  # stop; add -v to also drop the DB volume
docker compose config --quiet        # validate compose + .env interpolation
```

`.env` is required and gitignored. Create it from `.env.example`. Compose reads
it both for `${VAR}` interpolation in `docker-compose.yml` and as `env_file` for
`agent-api` and `mcp-worker`.

### Verification probes

There is no test suite. Phases are verified with probes against the running
stack. These are the checks that matter:

```bash
curl -s http://localhost:5000/health        # agent-api liveness
curl -s http://localhost:5000/api/ready     # postgres + mcp-worker reachability
curl -s http://localhost:5173/api/health    # the browser path, through nginx
curl -s http://localhost:5100/health        # mock-portal
curl -s http://localhost:8000/health        # mcp-worker
docker exec adp-postgres psql -U agent -d agentdemo -c "select * from mock_suppliers"
```

Exercise an MCP tool directly (note `Accept` must list both content types):

```bash
curl -s -X POST http://localhost:8000/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"ping","arguments":{}}}'
```

## Architecture

Five containers on the `agent-network` bridge, plus three pre-existing internal
services reached over the public internet. The internal services are **not** in
compose and must not be added to it.

```
browser → frontend (nginx :5173) → agent-api (:5000) → mcp-worker (:8000) → mock-portal (:5100)
                                        ↓                     ↓
                                   postgres (:5432) ←─────────┘
                                        ↓
              ds.ecs.com.tw (LLM) / srm.ecs.com.tw (SQL) / scm.ecs.com.tw (Mail)
```

### The division of responsibility

This is the core design constraint and the reason the project exists. From
`roadmap.md` section 20:

> Agent decides WHAT to do. Deterministic tools decide HOW it is executed.

The LLM does decision, classification, reasoning, and exception handling.
Code does browser automation, file I/O, parsing, SQL, DB writes, mail,
validation, and archiving. When adding a capability, put the judgement in the
agent loop and the mechanics in an MCP tool. Do not push mechanics into prompts.

**The LLM never generates SQL.** `search_supplier` and `search_part` use fixed
SQL templates with parameters validated against a character whitelist. The SQL
Execution API at `srm.ecs.com.tw` runs arbitrary SQL against Oracle EBS, so this
boundary is load-bearing, not stylistic.

### Service roles

- **agent-api** (ASP.NET Core, .NET 10) owns the agent loop, the LLM client, and
  classification. It is an MCP *client*. Classification lives here rather than
  in an MCP tool so that structured output stays under program control.
- **mcp-worker** (Python, FastMCP) exposes the tools the agent may call. Keep
  the tool count low; tool-selection reliability degrades as the list grows.
  Tools are coarse-grained on purpose: `download_documents` performs the whole
  login-query-download-stage sequence, and the agent never sees a selector.
- **mock-portal** (ASP.NET Core MVC) is a stand-in for a legacy internal web
  system, built to be driven by Playwright.
- **frontend** (React + Vite, served by nginx) shows the execution trace.
- **postgres** holds task state and the mock supplier/part tables used when
  `DBQUERY_MODE=mock`.

### Mode switches

Three environment switches exist to keep the live demo recoverable. Preserve
them when adding features.

| Variable | Values | Purpose |
|---|---|---|
| `AGENT_MODE` | `scripted` \| `llm` | `scripted` runs the same MCP tools in fixed order with identical UI output. It is both the development path and the fallback if the LLM misbehaves on stage. |
| `DBQUERY_MODE` | `mock` \| `real` | `mock` reads Postgres for reproducibility; `real` hits Oracle EBS for the Q&A portion of the demo. Both adapters must return the same field shape. |
| `MAIL_ENABLED` | `false` \| `true` | `false` makes `notify_manual_review` log instead of sending. Keep it false unless deliberately testing mail. |

Build `scripted` before `llm` for any new flow. Debugging a deterministic
pipeline is far cheaper than debugging one wrapped in an agent loop.

## Non-obvious constraints

These were established by testing against the real services. Changing them will
break things in ways that are slow to diagnose.

**The LLM always emits reasoning tokens.** `gemma4:26b` behind
`ds.ecs.com.tw/ollama/v1` ignores both `think: false` and `reasoning_effort`.
Every response burns roughly 110 tokens on a `reasoning` field before producing
`content`. A `max_tokens` set too low returns `finish_reason: "length"` with an
empty `content` string and no error. Hence `LLM_MAX_TOKENS_TOOL=1000` and
`LLM_MAX_TOKENS_CLASSIFY=1500`. Do not lower these.

**Native tool calling and `json_schema` both work** on that endpoint despite the
gateway in the path, so use the OpenAI `tools` array and
`response_format: {type: "json_schema"}` directly. There is no need for
prompt-based tool parsing. Typical decision latency is 3 to 4 seconds.

**No company CA import is needed.** TLS to all three internal services succeeds
from both the .NET and Python containers using the stock trust store. Never
disable certificate verification to work around a connection problem.

**The MCP endpoint is mounted deliberately.** `streamable_http_app()` already
serves at `settings.streamable_http_path`. The worker sets that path to `/mcp`
and mounts the app at `/`, which yields exactly `/mcp`. Mounting the app at
`/mcp` instead produces `/mcp/mcp`, and letting Starlette redirect produces a
307 on POST that stricter MCP clients will not follow.

**Container healthchecks must use `127.0.0.1`, not `localhost`.** `localhost`
resolves to `::1` first inside these containers while the servers bind IPv4
only, so the check fails with connection refused while the service is fine.

**Postgres 18 moved its data directory** to `/var/lib/postgresql`. The volume
mount uses that path; the older `/var/lib/postgresql/data` silently loses data.

**The browser cannot resolve Docker service names.** Frontend code always calls
relative `/api/...` paths. nginx proxies them to `agent-api:8080`, so every
browser-facing agent-api route needs an `/api`-prefixed form. `/health` exists
twice for this reason: bare for the container healthcheck, `/api/health` for the
proxy. The SSE location in `nginx.conf` has `proxy_buffering off`; without it
trace events arrive in batches instead of streaming.

**`.env` must not contain inline comments.** Put comments on their own lines;
trailing `# ...` can end up inside the value.

**`data/` is bind-mounted, not a named volume.** On Windows a named volume hides
inside WSL, which makes it awkward to open `result.xlsx` during the demo. Only
`postgres-data` is a named volume.

## Demo data

The mock set is four files, deliberately shaped to exercise different paths:
a clean supplier invoice, a quality report carrying only a vendor *name* (which
forces the agent to look the code up by name), a debit note CSV, and an
unstructured text file that should land in manual review.

`db/init.sql` seeds a second `ACME` supplier (`V00556`) with `enabled = false`
so the default run resolves `ACME` to exactly one row. Enabling it switches on
the ambiguous-match scenario. Keep the default run reproducible; the numbers in
`roadmap.md` section 2 assume it.

The mock portal's query must ignore the date parameter and always return the
same four files, so the demo does not break on a different day.

## Secrets

`.env` holds the LLM API keys, the AD credentials for the Mail API, and the
Postgres password. It is gitignored and must stay that way. Do not copy values
from it into `roadmap.md`, `README.md`, source files, prompts, or commit
messages. Mail credentials are read only by `mcp-worker` and must never enter
the agent-api LLM context.

`MAIL_PASSWORD` is currently blank and `MAIL_ENABLED=false`; the user needs to
supply the AD password before mail can be tested for real.

## Importing other agent configs

Codex and Gemini CLI configs exist at the user level on this machine. To bring
over MCP servers, slash commands, subagents, skills, or instructions, reply
`/import` to scan and list what is importable, then `/import --yes=<digest>`
using the digest the scan prints. If `/import` is unavailable on this surface,
run `claude import` from a terminal.
