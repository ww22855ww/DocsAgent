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

Run a full task and watch its trace:

```bash
python scripts/run_task.py scripted
python scripts/run_task.py llm "處理今天 SCM 文件"
```

Reset between runs, or the second run finds staging already emptied by the
first:

```bash
find data/staging data/archive data/downloads -type f ! -name '.gitkeep' -delete
docker exec adp-postgres psql -U agent -d agentdemo -q -c "TRUNCATE archives, manual_reviews RESTART IDENTITY;"
```

Exercise any MCP tool through the probe harness:

```bash
python scripts/probe_mcp.py list
python scripts/probe_mcp.py search_supplier '{"supplier_name":"ACME"}'
python scripts/probe_mcp.py download_documents '{"date":"2026-09-11","department":"SCM"}'
```

To exercise the real Oracle backend without touching `.env`, override the mode
for one process. A shell variable will not work, because `env_file` wins over
the shell for values it defines:

```bash
docker exec -e DBQUERY_MODE=real adp-mcp-worker python -c "from tools import mapping; print(mapping.search_supplier(supplier_code='2400401'))"
```

Or drive the endpoint by hand (note `Accept` must list both content types):

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
prompt-based tool parsing. Tool selection runs 1 to 2 seconds; classification
runs 3 to 7 seconds.

**Self-reported confidence is not a signal.** gemma4 returns 1.0 on nearly every
classification, including ones it gets wrong. Manual-review routing must be
decided by rules (missing category, missing supplier, missing document number)
with the confidence threshold as the last check, never the first. See
`ClassificationResult.NeedsManualReview`.

**The classifier distinguishes documents by structure, not subject.** Free-form
prose about inspections and defects was confidently labelled QualityReport until
the prompt was changed to say that a business category requires labelled fields
and its own reference number. If a new document type misclassifies, fix that
distinction in the prompt rather than adding a confidence rule.

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

**The Oracle item master has one row per inventory organization.** A plain
`SELECT` on a part number returns the same part ten times and the lookup reports
`ambiguous` when the answer is really unique. The real part query uses `DISTINCT`
inside a subquery, with the row cap outside it, because Oracle applies `rownum`
before `DISTINCT` and would otherwise truncate the wrong set.

**Real supplier codes are numeric** (`2400401`), not the `V00123` shape the mock
data uses. Both backends return the same field names, so nothing above the
mapping module cares, but do not assume the mock format when reading real rows.

**A task is finished when `finished` is true, not when its state looks
terminal.** The runner sets ManualReview or Completed on its summary step, but
TaskService still has the Excel export to add afterwards. The SSE stream closed
on state alone at first and dropped that last step from the live view, while the
task detail endpoint showed it. Any new end-of-task work inherits this.

**The SSE route is `/api/tasks/stream/{id}`, not `/api/tasks/{id}/stream`.** The
flat shape keeps it under one nginx location with `proxy_buffering off`; a
buffered proxy delivers the whole run in one burst when it ends, which looks
exactly like a broken stream. The server replays the trace from step 1 on every
connect, so the client keys steps by index rather than appending.

**The schema lives in `db/init.sql`, not in EF Core migrations.** That file runs
on first boot through the Postgres entrypoint and every statement is
`IF NOT EXISTS`, so it can be replayed against a live database:

```bash
docker exec -i adp-postgres psql -U agent -d agentdemo -v ON_ERROR_STOP=1 < db/init.sql
```

agent-api maps onto that schema and never creates or migrates it. Add a table by
editing the SQL and replaying it, then add the EF Core mapping.

**Who writes which table follows who produces the data.** mcp-worker writes
`archives` and `manual_reviews` as it goes; agent-api writes `tasks`,
`task_steps`, `documents`, `document_classifications` and `document_mappings`
once the run finishes. A re-save replaces a task's child rows rather than
appending, so re-running cannot double a trace.

**`write_excel` is hidden from the model.** Exporting is reporting, not a
decision, so TaskService calls it once after the runner finishes. That way the
agent cannot skip it, call it early, or call it twice. It reads back what was
committed to the database rather than what the agent reported, so the workbook
and the database cannot disagree.

**The agent must not be asked to carry data it already produced.** archive_record
takes a classification and a mapping; when the model was asked to supply them it
passed an empty object and every archive column came out null. ToolRegistry now
fills those arguments from AgentContext for both runners, so what gets written is
decided by code. Apply the same rule to any new tool: the agent chooses when to
call it, never what gets recorded.

**The trace and the model get different shapes of a tool result.**
`AgentContext.ForTrace` keeps the browser journal, because showing which selector
Playwright addressed is what answers "how does it read the page" for an audience
that has not met it. `ForModel` drops that journal, since the model cannot act on
it and it costs a few hundred tokens a step. Both slim document bodies. Using one
method for both is what silently removed the journal from the UI the first time.

**Steps carry who decided them.** `TaskStep.DecidedBy` is `model` or `script`, and
the console badges every step with it. `Note` marks the one or two steps the
comparison turns on: the agent recovering from a missing code, and the fixed flow
running out of options on the same document. Keep notes rare; they stop working
when everything is highlighted.

**Document text never goes back to the model.** extract_documents returns every
document body; AgentContext caches it and returns only filenames and sizes to the
agent. classify_document takes a filename and reads the text from that cache.
Undoing this would spend thousands of tokens per step.

**Tasks are serialised.** All tasks share one staging directory, so TaskService
runs them one at a time. Two concurrent runs move each other's files and fail
with "not in staging".

**Mock master data is real.** The suppliers and part numbers in `db/init.sql`
are Oracle EBS vendors with 2026 purchase orders at operating unit 152, and items
actually bought from them. `DBQUERY_MODE=mock` and `DBQUERY_MODE=real` therefore
answer the same questions the same way, so switching modes during the demo proves
the adapter rather than changing the story. Keep it that way when editing the
seed data.

**The portal has two screens, so the agent has a routing decision.** SCM
documents and ESG questionnaires have separate query forms and separate download
routes, reached by `download_documents` and `download_esg_surveys`. Scripted mode
routes by keyword match on the prompt, which is brittle on purpose: a task worded
as "整理各家廠商回覆的碳排與勞權自評表" sends scripted to the wrong screen while
the agent reads the intent and picks ESG. That contrast is the point.

**The two modes are expected to disagree, and the demo turns on how.**
quality_002.xlsx names 勝宏科技 with no supplier code. Scripted always sends it to
manual review. What the agent does depends on whether the name resolves, which
`scripts/scenario.py` switches by enabling the Thailand entity of that real
two-company vendor group:

| | scripted | agent |
|---|---|---|
| unique (default) | manual review, no code | archived as 40891 |
| ambiguous | manual review, no code | manual review, both candidates named |

Both agent outcomes are correct. It recovers when the data supports a decision
and declines when it does not, and the prompt forbids picking a row out of an
ambiguous result. In `DBQUERY_MODE=real` the name is always ambiguous, because
both companies really exist. Reset to `unique` after demonstrating the ambiguous
case.

**The model cannot reproduce Traditional Chinese proper nouns, anywhere.**
Asked to write the closing summary it turned 華碩電腦股份有限公司 into
鈺玮电讯股份有限公司 and 聯強國際 into 鼎瑞国际 — clean UTF-8, wrong characters.
The prompt therefore tells it to use file names and counts only, and
`LlmAgentRunner.Vet` discards any summary containing a company-name marker in
favour of a generated one. Wrong vendor names in front of a procurement audience
discredit everything else on the screen. Do not add a feature that has the model
restate a name.

**Never make the model retype an identifier it has already seen.** Asked to
repeat a Chinese vendor name into a tool argument, gemma4 turned 勝宏科技 into
涵孚科技 and the lookup missed. `search_supplier` and `search_part` therefore
accept a `filename`, which ToolRegistry swaps for the values the classifier
extracted. This is the same rule as `archive_record`: the agent chooses which
document and which tool, never the data.

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

## Running the demo

`./scripts/reset-demo.ps1` is the one command before any rehearsal or the
meeting: it clears `data/`, empties the task and result tables, regenerates the
mock documents and sets the supplier scenario back to resolvable. `-Ambiguous`
leaves the two-vendor case on; `-Full` also reseeds the master tables, which is
only needed after editing `db/init.sql`.

`GET /api/health/services` probes all six dependencies in parallel and the
console shows it as a strip at the top. `DBQUERY_MODE` and `MAIL_ENABLED` in
that strip are read from mcp-worker's own `/health`, not from agent-api's copy:
both services load the same `.env`, so restarting only one leaves the other
reporting a stale switch. Getting that wrong once claimed mail was off while the
worker was actually sending. When the worker does not answer, the strip says the
state is unknown rather than defaulting to off. **Changing `.env` needs both
services restarted:** `docker compose up -d agent-api mcp-worker`. Reachability only: the LLM check is an
unauthenticated call whose 401 still proves DNS, TLS and the route, so checking
costs no tokens and sends no mail. The SQL API is marked optional because the
default demo runs on mock master data.

`README.md` holds the five-minute script, the likely questions, and the three
fallbacks in order: switch to Scripted, play
`docs/media/agent-run-fallback.gif`, or present the walkthrough alone.

## Rehearsal tooling

`POST /api/routing` and `scripts/probe_routing.py` compare how each mode routes a
task, without running one. Nothing in the console calls the endpoint; it exists so
the presenter can try twenty phrasings while choosing demo sentences, and have a
real number ready if asked how reliable routing is.

```bash
python scripts/probe_routing.py -n 5
```

Both sides go through the real implementations: the system prompt comes from
`LlmAgentRunner.BuildSystemPrompt` and the keyword rule from
`ScriptedAgentRunner.MatchedKeywords`. Keep it that way. A probe holding its own
copy of either keeps reporting a comparison that stopped being true, which is
worse than having no probe.

The built-in matrix is graded on purpose, and the last group matters most: a test
that only shows the keyword rule missing invites "then add the keyword", so it
also shows what adding one costs. `永續` is in the list to catch 永續調查表, and
that is exactly why a task mentioning the 永續發展部 gets routed to the wrong
screen. Measured at 5 runs each: keyword 3/8, agent 8/8, about 1.5 s per decision.

**Do not put those figures in the main demo flow.** The room does not yet know
what an agent is; routing statistics answer a question nobody asked and make the
system sound shaky. Keep them for Q&A.

## Presentation aid

`docs/architecture-walkthrough.html` is a twelve-step interactive walkthrough
used to explain the demo at the department meeting, published as an Artifact at
https://claude.ai/code/artifact/04668997-0199-4a6f-b8ac-d3c15a2c51d9

It is self-contained and needs nothing running, so it still works if the stack
does not. Republish by passing that URL as `url` from any conversation, or the
same file path from the one that published it.

Two conventions hold it together. Colour encodes one idea everywhere: violet is
a decision the model makes, teal is work deterministic code carries out, amber
is a system we do not own. And architecture edges carry explicit waypoints
rather than auto-routing, because auto-routing put labels on top of nodes and
ran two edges down the same channel; the node columns are chosen so every edge
has an empty channel to run down.

Keep its figures true to the code. The numbers it quotes (3 archived / 1 review
in agent mode, 2 / 2 scripted), the vendor codes, and the transcription bug it
cites are all real results from this repo.

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
