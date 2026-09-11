# Agentic Document Processing Demo — Development Roadmap

> 更新日期：2026-09-11
> 本版依據技術評估調整：LLM 改用內部已部署的遠端 Ollama（OpenAI 相容端點）、Supplier / Part mapping 可切換內部 SQL Execution API、Manual Review 加入 Mail 通知，並修正 compose 網路、volume、步數上限等問題。

---

## 1. Project Goal

建立一套可於部門例會展示的 **Agentic Document Processing Demo**，完整展示：

1. Agent 接收自然語言任務。
2. 透過 MCP Tool 驅動 Browser Automation。
3. 登入模擬網站並下載指定條件的文件。
4. 將下載檔案移入 staging 區。
5. 解析文件內容。
6. 使用內部遠端 LLM（Ollama / gemma4:26b，OpenAI 相容 API）進行文件分類與必要欄位判斷。
7. 呼叫內部 SQL Execution API（或 mock）取得 Supplier / Part mapping 資料。
8. 將結果結構化寫入 PostgreSQL / Excel。
9. 將無法自動處理的項目送入 Manual Review，並透過內部 Mail API 通知負責人。
10. 由 Web UI 顯示完整 Agent Execution Trace。

自建服務皆以 Docker Container 執行，並使用 Docker Compose 統一啟動。LLM、DB Query、Mail 為既有內部服務，直接遠端呼叫，不在 compose 內。

---

# 2. Demo Scenario

使用者在 Web UI 輸入：

> 處理今天 SCM 部門的文件。

Agent 自主完成：

```text
使用者
  ↓
Agent Orchestrator
  ↓
判斷需要取得文件
  ↓
MCP Tool
  ↓
Playwright
  ↓
登入 Mock Portal
  ↓
選單 → 條件 → 查詢 → 下載
  ↓
Staging
  ↓
檔案內容解析
  ↓
LLM Classification（遠端 Ollama）
  ↓
Supplier / Part Mapping（SQL Execution API 或 mock）
  ↓
結構化資料
  ↓
PostgreSQL / Excel
  ↓
Manual Review → Mail 通知
  ↓
Agent Summary
```

預期 Demo Result（與第 14 節 mock 資料一致，共 4 份）：

```text
✓ Portal login successful
✓ Downloaded 4 documents
✓ Classified 4 documents
✓ Mapping completed: 3
⚠ Manual review required: 1
✓ Notification sent: 1
✓ Archived: 3

Completed.
```

---

# 3. High-Level Architecture

```text
┌──────────────────────────────────────────────────┐
│                  Docker Host                     │
│                                                  │
│  ┌──────────────┐                                │
│  │ React Web UI │  (瀏覽器端，經 /api proxy)      │
│  │   frontend   │                                │
│  └──────┬───────┘                                │
│         │ HTTP / SSE  (proxy → agent-api:8080)   │
│         ▼                                        │
│  ┌───────────────────────┐                       │
│  │ Agent Orchestrator    │                       │
│  │ ASP.NET Core Web API  │──────┐                │
│  │       agent-api       │      │ MCP            │
│  └───────┬───────────────┘      │ (Streamable    │
│          │ OpenAI-compatible    │  HTTP)         │
│          │ chat/completions     ▼                │
│          │           ┌──────────────────┐        │
│          │           │ Python MCP Worker│        │
│          │           │    mcp-worker    │        │
│          │           └───┬─────┬─────┬──┘        │
│          │               │     │     │           │
│          │        Playwright Parser  File I/O    │
│          │               │                       │
│          │               ▼                       │
│          │   ┌───────────────────┐               │
│          │   │ Mock Portal       │               │
│          │   │ ASP.NET Core MVC  │               │
│          │   │   mock-portal     │               │
│          │   └───────────────────┘               │
│          │                                       │
│          │   ┌───────────────────┐               │
│          │   │ PostgreSQL 18     │               │
│          │   │       db          │               │
│          │   └───────────────────┘               │
└──────────┼───────────────────────────────────────┘
           │
           ▼  External / Internal Services（既有，不在 compose）
  ┌───────────────────────────────────────────────┐
  │ LLM        https://ds.ecs.com.tw/ollama/v1    │  gemma4:26b
  │ LLM backup https://openrouter.ai/api/v1       │  google/gemma-4-26b-a4b-it
  │ DbQueryApi https://srm.ecs.com.tw:8017        │  SQL Execution API (POST /F83/T1)
  │ MailApi    https://scm.ecs.com.tw:8020        │  POST /api/send
  └───────────────────────────────────────────────┘
```

---

# 4. Services

## 4.1 frontend

**Technology**

- React
- Vite
- TypeScript

**Responsibilities**

- 建立 Task。
- 顯示 Task 狀態。
- 顯示 Agent execution trace。
- 顯示分類結果。
- 顯示 Manual Review。
- 顯示執行摘要。

**Port**

```text
5173
```

**注意**

瀏覽器端無法解析 Docker 內部主機名稱，前端一律呼叫相對路徑 `/api/...`，由 Vite dev server（開發）或 nginx（容器）反向代理到 `http://agent-api:8080`。SSE 路徑需關閉 proxy buffering。

---

## 4.2 agent-api

**Technology**

- ASP.NET Core Web API
- C#
- ModelContextProtocol C# SDK（MCP Client，Streamable HTTP）

**Responsibilities**

- 接收使用者 Task。
- Agent orchestration / Agent loop。
- LLM client（OpenAI 相容格式，可切換 provider）。
- Document classification（呼叫 LLM structured output，不做成 MCP tool）。
- MCP Client / Tool routing。
- Task state management。
- Retry / Timeout。
- SSE event streaming。
- 最終結果與摘要。

**Port**

```text
5000 (host) → 8080 (container)
```

---

## 4.3 mcp-worker

**Technology**

- Python
- MCP SDK（FastMCP，streamable-http transport）
- Playwright（基底映像 `mcr.microsoft.com/playwright/python`）
- openpyxl / pandas
- PDF / CSV / TXT parser

**Responsibilities**

提供 Agent 可呼叫的 MCP Tools。工具數量刻意壓低，提升模型 tool selection 穩定度。

第一版：

```text
download_documents        登入 → 查詢 → 下載 → 自動搬入 staging
list_staging_files
extract_documents         批次解析（避免步數隨文件數線性成長）
search_supplier           內部 DbQueryApi 或 mock
search_part               內部 DbQueryApi 或 mock
archive_record
notify_manual_review      內部 MailApi
write_excel               （Phase 5 後段，可延後）
```

注意：

- Classification 由 agent-api 直接呼叫 LLM structured output，不放在 MCP Worker。
- `move_file` 不對 agent 開放，由 `download_documents` 與 `archive_record` 內部處理。

**Port**

```text
8000（僅 internal network）
```

---

## 4.4 mock-portal

**Technology**

- ASP.NET Core MVC / Razor Pages

**Responsibilities**

模擬 Legacy / Internal Web System。

流程：

```text
Login
 ↓
Sidebar Menu
 ↓
Document Query
 ↓
Input Parameters
 ↓
Search
 ↓
Result List
 ↓
Download
```

**Port**

```text
5100 (host) → 8080 (container)
```

Mock Login：

```text
Username: admin
Password: 123456
```

查詢邏輯不依賴日期：不論輸入哪一天，SCM 部門皆回傳固定 4 份 mock 檔案，確保 demo 可重現。

---

## 4.5 External Services（既有內部服務）

### LLM — 遠端 Ollama

- 端點：`https://ds.ecs.com.tw/ollama/v1/chat/completions`（OpenAI 相容格式，Bearer token）
- 模型：`gemma4:26b`
- Timeout：120s

負責：

- Tool Selection
- Task Planning
- Classification
- Exception Handling
- Result Summarization

備援 provider：OpenRouter `google/gemma-4-26b-a4b-it`，同為 OpenAI 格式，靠設定切換，內部服務異常時可即時切換。

動工前必須驗證（curl 各測一次）：

- [ ] 端點是否支援原生 `tools` / `tool_calls`（經 `/ollama/` 閘道轉發，欄位可能被吃掉）。不支援則退為 prompt 描述工具、模型回傳 JSON 決策、agent-api 自行解析。
- [ ] `response_format` 是否支援 `json_schema`。不支援則用 `json_object` 加 prompt 約束，並做 schema 驗證與一次重試。
- [ ] 單次 tool-call 決策實際延遲（共用資源，白天可能有其他人在用），決定 max_steps 與 UI 進度動畫需求。

### DbQueryApi — SQL Execution API

- 端點：`POST https://srm.ecs.com.tw:8017/F83/T1`
- Body：`{"SQL": "select ..."}`
- 文件：Notion「SQL Execution API」
- 資料來源為 Oracle EBS（例如 `apps.po_vendors`）

驗證指令：

```bash
curl.exe -sk -X POST "https://srm.ecs.com.tw:8017/F83/T1" \
  -H "Content-Type: application/json" \
  -d "{\"SQL\":\"select vendor_name from apps.po_vendors where vendor_id = 2610\"}"
```

設計原則：

- **LLM 絕對不產生 SQL。** `search_supplier`、`search_part` 內部使用固定 SQL template，參數由程式白名單驗證與轉義後填入。
- 只允許 SELECT。
- `DBQUERY_MODE=mock|real`：例會展示用 mock（可重現），Q&A 時可切 real。
- mock 資料存在 PostgreSQL，與 real 回傳相同欄位結構，上層不需改碼。

### MailApi

- 端點：`POST https://scm.ecs.com.tw:8020/api/send`
- 健康檢查：`GET /health`
- Swagger：`https://scm.ecs.com.tw:8020/docs`
- 必填：`username`（AD 帳號，不含 domain）、`password`、`to`
- `from_email`：AD 帳號與信箱前綴格式不同時必填
- 附件 `attachments[]`：`filename` 必填，`url` 與 `content_base64` 擇一

設計原則：

- 帳密只存 `.env`，不進 prompt、不回傳給 LLM。
- `notify_manual_review` 工具收件人固定由設定決定，agent 只能決定主旨與內文摘要。
- 內文夾帶原始文件（`content_base64`）與分類結果，讓收件人可直接處理。

---

## 4.6 postgres

**Image**

```text
postgres:18
```

**Responsibilities**

儲存：

- Task
- Task Step
- Tool Execution
- Document Metadata
- Classification Result
- Mapping Result
- Archive Result
- Manual Review
- Mock Supplier / Part 資料（DBQUERY_MODE=mock 時使用）

**Port**

```text
5432
```

注意：postgres 18 官方映像資料目錄改為 `/var/lib/postgresql`（非舊版 `/var/lib/postgresql/data`），volume 掛載路徑需對應，否則資料不會持久化。

---

# 5. Repository Structure

```text
agentic-document-demo/

├─ docker-compose.yml
├─ .env                    （不進 git）
├─ .env.example
├─ .gitignore
├─ README.md
├─ roadmap.md
│
├─ certs/
│  └─ ecs-root-ca.crt      公司 Root CA，掛入容器信任庫
│
├─ scripts/
│  └─ reset-demo.ps1       清空 data/、truncate 資料表、還原 mock 檔案
│
├─ frontend/
│  ├─ Dockerfile
│  ├─ nginx.conf           /api → agent-api:8080（含 SSE 設定）
│  ├─ vite.config.ts       dev proxy
│  ├─ package.json
│  └─ src/
│
├─ agent-api/
│  ├─ Dockerfile
│  ├─ AgentApi.csproj
│  ├─ Controllers/
│  ├─ Services/
│  │  ├─ Agent/
│  │  ├─ Llm/               OpenAI 相容 client，provider 切換
│  │  ├─ Classification/
│  │  ├─ Mcp/
│  │  └─ Tasks/
│  └─ Models/
│
├─ mcp-worker/
│  ├─ Dockerfile
│  ├─ requirements.txt
│  ├─ server.py
│  ├─ tools/
│  │  ├─ browser.py
│  │  ├─ filesystem.py
│  │  ├─ documents.py
│  │  ├─ mapping.py         search_supplier / search_part（mock|real adapter）
│  │  ├─ notify.py          MailApi
│  │  └─ archive.py
│  └─ parsers/
│
├─ mock-portal/
│  ├─ Dockerfile
│  ├─ MockPortal.csproj
│  ├─ Controllers/
│  ├─ Views/
│  └─ MockData/
│
├─ data/                    bind mount，方便 demo 時直接開檔
│  ├─ downloads/
│  ├─ staging/
│  ├─ archive/
│  └─ output/
│
└─ db/
   └─ init.sql              schema + mock supplier/part 資料
```

---

# 6. Docker Compose Design

預計 services（無 ollama 容器）：

```yaml
services:

  frontend:
    build: ./frontend
    ports: ["5173:80"]

  agent-api:
    build: ./agent-api
    ports: ["5000:8080"]
    env_file: .env
    volumes:
      - ./certs:/usr/local/share/ca-certificates:ro

  mcp-worker:
    build: ./mcp-worker
    env_file: .env
    volumes:
      - ./data/downloads:/app/downloads
      - ./data/staging:/app/staging
      - ./data/archive:/app/archive
      - ./data/output:/app/output
      - ./certs:/usr/local/share/ca-certificates:ro

  mock-portal:
    build: ./mock-portal
    ports: ["5100:8080"]

  postgres:
    image: postgres:18
    volumes:
      - postgres-data:/var/lib/postgresql

volumes:
  postgres-data:
```

Internal Docker Network：

```text
agent-network
```

服務互連：

```text
browser
  ↓  http://localhost:5173/api/...
frontend (nginx proxy)
  ↓  http://agent-api:8080
agent-api
  ↓  https://ds.ecs.com.tw/ollama/v1   （LLM，外部）
  ↓  http://mcp-worker:8000/mcp        （MCP Streamable HTTP）
  ↓  postgres:5432
mcp-worker
  ↓  http://mock-portal:8080
  ↓  https://srm.ecs.com.tw:8017       （DbQueryApi，外部）
  ↓  https://scm.ecs.com.tw:8020       （MailApi，外部）
  ↓  postgres:5432                     （mock mapping 資料）
```

環境變數（`.env.example`，實際值不進 git）：

```env
# Agent
AGENT_MODE=llm                # llm | scripted
AGENT_MAX_STEPS=30
AGENT_TOOL_TIMEOUT_SEC=60
AGENT_TASK_TIMEOUT_MIN=10

# LLM
LLM_PROVIDER=ollama           # ollama | openrouter
LLM_OLLAMA_API_URL=https://ds.ecs.com.tw/ollama/v1/chat/completions
LLM_OLLAMA_API_KEY=
LLM_OLLAMA_MODEL=gemma4:26b
LLM_OLLAMA_TIMEOUT_SEC=120
LLM_OPENROUTER_API_URL=https://openrouter.ai/api/v1/chat/completions
LLM_OPENROUTER_API_KEY=
LLM_OPENROUTER_MODEL=google/gemma-4-26b-a4b-it
LLM_OPENROUTER_TIMEOUT_SEC=300

# DB Query API
DBQUERY_MODE=mock             # mock | real
DBQUERY_BASE_URL=https://srm.ecs.com.tw:8017
DBQUERY_PATH=/F83/T1
DBQUERY_TIMEOUT_SEC=60

# Mail API
MAIL_API_BASE_URL=https://scm.ecs.com.tw:8020
MAIL_USERNAME=
MAIL_PASSWORD=
MAIL_FROM_EMAIL=
MAIL_TO=
MAIL_TIMEOUT_SEC=60

# Mock Portal
PORTAL_URL=http://mock-portal:8080
PORTAL_USERNAME=admin
PORTAL_PASSWORD=123456

# Postgres
POSTGRES_DB=agentdemo
POSTGRES_USER=agent
POSTGRES_PASSWORD=
```

---

# 7. Docker Volumes

檔案類目錄一律用 **bind mount** 到 `./data`，例會時可直接在檔案總管開 `result.xlsx` 與 archive 檔案。命名 volume 在 Windows Docker Desktop 上藏在 WSL 內，不便展示。

```text
./data/downloads  → mcp-worker:/app/downloads
./data/staging    → mcp-worker:/app/staging
./data/archive    → mcp-worker:/app/archive
./data/output     → mcp-worker:/app/output
```

命名 volume 只保留：

```yaml
volumes:
  postgres-data:
```

---

# 8. Agent Tool Design

第一版不要提供過度底層 Browser Tool：

不建議：

```text
browser_click
browser_type
browser_wait
browser_scroll
```

建議：

```text
download_documents
```

Agent 呼叫：

```json
{
  "department": "SCM",
  "date": "2026-09-11",
  "document_type": "ALL"
}
```

`date` 由 agent-api 在 system prompt 注入今日日期，不讓模型自行推算。

MCP Worker 內部自行使用 Playwright：

```text
Login
 ↓
Navigate
 ↓
Select Menu
 ↓
Fill Parameters
 ↓
Search
 ↓
Download (expect_download → save_as)
 ↓
Move to staging
```

Agent 不需要知道 selector。

---

# 9. Initial MCP Tools

## Browser

```text
download_documents(
    date,
    department,
    document_type
)
```

Return：

```json
{
  "status": "success",
  "downloaded_count": 4,
  "files": [
    "supplier_001.xlsx",
    "quality_002.xlsx",
    "debit_003.csv",
    "unknown_004.txt"
  ]
}
```

重複呼叫具冪等性：staging 已有同名檔案則略過下載並回傳現有清單。

---

## File System

```text
list_staging_files()
```

---

## Document Parser

```text
extract_documents(
    filenames        # 省略則解析 staging 全部
)
```

Return：

```json
{
  "documents": [
    { "filename": "supplier_001.xlsx", "content": "...", "metadata": {} },
    { "filename": "quality_002.xlsx",  "content": "...", "metadata": {} }
  ]
}
```

---

## Business Mapping

```text
search_supplier(
    supplier_code,      # 可空
    supplier_name       # 可空，支援模糊查詢
)
```

```text
search_part(
    part_no
)
```

實作：

```text
DBQUERY_MODE=mock  → 查 PostgreSQL mock 表
DBQUERY_MODE=real  → POST DbQueryApi，固定 SQL template：
                     select vendor_id, vendor_name, segment1
                     from apps.po_vendors
                     where segment1 = :code or upper(vendor_name) like :name
```

參數在程式端白名單驗證（僅允許字母、數字、`-`、`_`、空白），轉義後填入。LLM 永不接觸 SQL。

Return 可能為 0 筆、1 筆、多筆，交由 agent 判斷後續（見 Phase 7）。

---

## Notification

```text
notify_manual_review(
    filename,
    reason,
    summary
)
```

內部呼叫 MailApi `POST /api/send`，收件人由 `MAIL_TO` 決定，附件以 `content_base64` 夾帶 staging 中的原始檔案。

---

## Archive

```text
archive_record(
    filename,
    classification,
    mapping
)
```

寫入 PostgreSQL 並將檔案由 staging 搬到 archive。

---

## Excel（可延後）

```text
write_excel(task_id)
```

由 mcp-worker 以 openpyxl 產生 `output/result.xlsx`。統一由 worker 負責，agent-api 不寫 Excel。

---

# 10. Classification Design

由 agent-api 直接呼叫 LLM，使用 `response_format` json_schema（若端點不支援則退為 json_object 加驗證重試）。

分類：

```text
SupplierInvoice
DebitNote
QualityReport
ShippingDocument
Unknown
```

LLM 回傳固定 JSON：

```json
{
  "category": "SupplierInvoice",
  "confidence": 0.96,
  "supplier_code": "V00123",
  "supplier_name": null,
  "part_no": "ABC-9981",
  "document_no": "INV-001"
}
```

Manual Review 判定採**規則優先、confidence 輔助**（LLM 自報 confidence 未經校準，不能單獨作為依據）：

```text
任一成立 → manual review：
  - category == Unknown
  - 該類別必要欄位缺失且無法透過 search_supplier(name) 補齊
  - search_supplier / search_part 回傳多筆無法唯一決定
  - confidence < 0.80
```

---

# 11. Agent Loop

```text
Task
 ↓
LLM decides next action
 ↓
Tool Call
 ↓
Tool Result
 ↓
LLM observes
 ↓
Next Action
 ↓
...
 ↓
Complete / Manual Review / Failed
```

需要防止：

- Infinite loop
- Repeated tool calls（相同 tool + 相同參數連續出現視為卡住）
- Invalid arguments（schema 驗證失敗回傳錯誤訊息給模型，最多重試 2 次）
- Tool timeout
- Hallucinated tool（不在 registry 的工具名稱直接回傳錯誤）
- Excessive LLM calls

初期限制：

```text
max_steps     = 30    （批次 extract 後 4 份文件約 12 到 15 步）
tool_timeout  = 60s
task_timeout  = 10 min
```

### AGENT_MODE=scripted

提供固定順序執行同一組 MCP tools 的 deterministic 模式，UI 顯示完全相同。用途：

- 開發階段先驗證整條 pipeline。
- 例會現場 LLM 服務異常時的保命開關。

### LLM Provider Fallback

內部 Ollama 連線失敗或超時時，agent-api 可依設定自動切換 OpenRouter 重試一次，並在 trace 中標示。

---

# 12. Task States

```text
CREATED

RUNNING

WAITING_TOOL

WAITING_LLM

MANUAL_REVIEW

COMPLETED

FAILED
```

---

# 13. Execution Trace

每次執行紀錄：

```text
Step 01
Agent:
需要先取得 SCM 今天的文件。

Tool:
download_documents

Arguments:
date=2026-09-11
department=SCM

Result:
4 files downloaded.
```

Frontend 顯示：

```text
✓ Login Portal

✓ Search Documents

✓ Download 4 files

✓ Extracted 4 documents

→ Classifying supplier_001.xlsx

✓ Classified: SupplierInvoice

→ Searching Supplier V00123

✓ Supplier mapped: V00123

✓ Archived

...

⚠ unknown_004.txt → Manual Review

✓ Notification sent
```

---

# 14. Mock Data Design

Mock Portal 預先放：

```text
supplier_001.xlsx
quality_002.xlsx
debit_003.csv
unknown_004.txt
```

## supplier_001.xlsx

```text
SupplierCode: V00123
PartNo: ABC-9981
Qty: 500
InvoiceNo: INV-001
```

## quality_002.xlsx

```text
Vendor: ACME
Lot: LOT-99123
Inspection Result: FAIL
```

刻意不含 SupplierCode，只有名稱，觸發 Phase 7 的「以名稱查詢」情境。

## debit_003.csv

```csv
supplier,reason,amount
V00321,late delivery,25000
```

## unknown_004.txt

```text
Vendor ABC Corp.

We have identified abnormal damage
during incoming inspection.

Reference: LOT-88881
```

最後一份刻意設計成無固定格式，預期進 Manual Review 並觸發 Mail 通知。

## Mock Supplier 表（PostgreSQL）

```text
V00123  Foxlink Precision
V00321  Delta Components
V00555  ACME Electronics      ← quality_002 以名稱 ACME 查得唯一一筆
V00556  ACME Logistics        ← 若要展示「多筆 → manual review」可啟用
```

---

# 15. Development Phases

---

## Phase 0 — Project Bootstrap

### Goal

Docker Compose 可以成功啟動所有自建服務，且容器內可連通三個內部服務。

### Tasks

- [ ] 建立 Git Repository，`.gitignore` 排除 `.env`、`data/`、`certs/*.key`。
- [ ] 建立目錄結構。
- [ ] 建立 docker-compose.yml。
- [ ] 建立 `.env.example` 與本機 `.env`（含 LLM key、Mail 帳密、DB 密碼）。
- [ ] 匯出公司 Root CA 到 `certs/`，agent-api 與 mcp-worker Dockerfile 加入信任庫（`update-ca-certificates`）。
- [ ] 建立 frontend Dockerfile + nginx.conf（/api proxy，SSE 關 buffering）。
- [ ] 建立 agent-api Dockerfile。
- [ ] 建立 mcp-worker Dockerfile（Playwright 基底映像）。
- [ ] 建立 mock-portal Dockerfile。
- [ ] 加入 PostgreSQL 18（volume 掛 `/var/lib/postgresql`）。
- [ ] 建立 internal network。
- [ ] 建立 `data/` bind mount 目錄。
- [ ] 驗證遠端 LLM：`tools` 支援、`response_format` 支援、延遲。
- [ ] 驗證 DbQueryApi：以 `select vendor_name from apps.po_vendors where vendor_id = 2610` 測通。
- [ ] 驗證 MailApi：`GET /health` 與一封測試信。

### Acceptance

```bash
docker compose up -d
```

後：

```text
frontend      running
agent-api     running
mcp-worker    running
mock-portal   running
postgres      running
```

且從 agent-api 容器內 curl 三個內部 HTTPS 服務皆不出現憑證錯誤。

---

# Phase 1 — Mock Portal

### Goal

建立可被 Playwright 操作的模擬企業網站。

### Tasks

- [ ] Login Page。
- [ ] Cookie / Session。
- [ ] Sidebar。
- [ ] Document Query。
- [ ] Date Parameter（不影響結果）。
- [ ] Department Dropdown。
- [ ] Document Type。
- [ ] Query Button。
- [ ] Result List。
- [ ] Download Button。
- [ ] Mock Files。

### Acceptance

人工使用 Browser：

```text
Login
→ Query
→ Download
```

可以成功取得文件。

---

# Phase 2 — MCP Worker

### Goal

Agent 可以透過 MCP 操作網站、檔案與內部服務。

### Tasks

- [ ] 建立 MCP Server（FastMCP，streamable-http，port 8000）。
- [ ] Playwright setup（headless，`accept_downloads`）。
- [ ] download_documents（含搬入 staging、冪等）。
- [ ] list_staging_files。
- [ ] extract_documents（xlsx / csv / txt；pdf 可後補）。
- [ ] search_supplier / search_part：mock adapter（PostgreSQL）。
- [ ] search_supplier / search_part：real adapter（DbQueryApi，固定 SQL template，參數白名單）。
- [ ] notify_manual_review（MailApi，base64 附件）。
- [ ] archive_record。

### Acceptance

使用 MCP Client（或 MCP Inspector）逐一測試每個 tool。`download_documents` 可：

```text
登入
→ 查詢
→ 下載
→ 搬入 staging
→ 回傳 files
```

`search_supplier` 在 mock 與 real 兩種模式回傳相同欄位結構。

---

# Phase 3 — LLM Integration（Remote）

### Goal

.NET API 可以呼叫內部遠端 LLM，並可切換備援 provider。

### Tasks

- [ ] OpenAI 相容 chat/completions client（HttpClient 或 Microsoft.Extensions.AI OpenAI provider，自訂 BaseUrl）。
- [ ] Provider 設定：ollama / openrouter，Timeout 分開設定。
- [ ] Fallback：主 provider 失敗自動切備援一次。
- [ ] JSON structured output（json_schema 或 json_object + 驗證重試）。
- [ ] Classification Prompt。
- [ ] Tool definition（OpenAI `tools` 格式）。
- [ ] Tool selection test；若原生 tool calling 不可用，實作 prompt-based JSON 決策解析。
- [ ] 量測並記錄單次呼叫延遲。

### Acceptance

輸入文件內容：

```text
SupplierCode V00123
Invoice INV-001
```

模型輸出：

```json
{
  "category": "SupplierInvoice",
  "confidence": 0.95
}
```

且給定工具清單與任務描述，模型能正確選出 `download_documents` 並帶出合法參數。

---

# Phase 4 — Agent Orchestrator

### Goal

完成真正 Agentic Flow。

### Tasks

- [ ] Task entity。
- [ ] 今日日期注入 system prompt。
- [ ] AGENT_MODE=scripted：固定順序跑通全部 tools（先做）。
- [ ] Agent loop（後做）。
- [ ] Tool registry。
- [ ] MCP Client（ModelContextProtocol C# SDK，Streamable HTTP）。
- [ ] Tool execution。
- [ ] Tool result feedback。
- [ ] Agent stop condition。
- [ ] max_steps / 重複呼叫偵測。
- [ ] Timeout。
- [ ] Retry。
- [ ] Execution log。

### Acceptance

輸入：

```text
處理今天 SCM 文件
```

scripted 與 llm 兩種模式皆可完成：

```text
download
→ extract
→ classify
→ mapping
→ archive / manual review + notify
```

---

# Phase 5 — DB & Archive

### Goal

建立正式結構化資料。

### Tables

```text
tasks

task_steps

documents

document_classifications

document_mappings

archives

manual_reviews

mock_suppliers

mock_parts
```

### Tasks

- [ ] EF Core。
- [ ] Migration（或 init.sql）。
- [ ] Archive Record。
- [ ] Excel export（mcp-worker `write_excel`，時間不足可延後）。

### Acceptance

Task 完成後 PostgreSQL 有完整紀錄；若已做 Excel，`data/output/result.xlsx` 可直接開啟。

---

# Phase 6 — React Agent Console

### Goal

建立可於例會展示的 UI。

### Main Screen

```text
┌────────────────────────────────────────┐
│ Agentic Document Processor             │
├────────────────────────────────────────┤
│ Task                                   │
│                                        │
│ [處理今天 SCM 文件]                    │
│                          [Execute]     │
├────────────────────────────────────────┤
│ Execution                              │
│                                        │
│ ✓ Portal Login                         │
│ ✓ Downloaded 4 documents               │
│ ✓ Classified 3                         │
│ → Processing unknown_004.txt           │
│                                        │
├────────────────────────────────────────┤
│ Result                                 │
│                                        │
│ SupplierInvoice     1                   │
│ DebitNote           1                   │
│ QualityReport       1                   │
│ Manual Review       1  (mail sent)      │
└────────────────────────────────────────┘
```

### Tasks

- [ ] Task form。
- [ ] Execute。
- [ ] Task status。
- [ ] SSE（經 proxy）。
- [ ] Timeline。
- [ ] Tool call display。
- [ ] Classification result。
- [ ] Manual review。
- [ ] Summary。
- [ ] LLM 等待中的進度動畫（遠端模型延遲可能數十秒）。

---

# Phase 7 — Agentic Exception Handling

### Goal

讓 Demo 不只是固定 Workflow。第一版只做一個情境，做穩為優先。

### Scenario A（必做）：Supplier code missing

`quality_002.xlsx` 只有 Vendor 名稱 `ACME`：

```text
classify → supplier_code = null, supplier_name = "ACME"
 ↓
search_supplier(name="ACME")
 ↓
1 result (V00555)
 ↓
continue mapping → archive
```

### Scenario B（選做）：多筆結果

啟用 mock 表中的第二筆 ACME：

```text
search_supplier(name="ACME")
 ↓
2 results
 ↓
manual review → notify_manual_review
```

### Acceptance

展示：

```text
scripted 模式遇到缺 code → 直接 manual review

llm 模式 → 自主改以名稱查詢並完成 mapping
```

---

# Phase 8 — Demo Polish

### Goal

準備部門例會展示。

### Tasks

- [ ] `scripts/reset-demo.ps1`：清空 `data/*`、truncate 資料表、還原 mock 檔案。
- [ ] 固定 Mock Data。
- [ ] Health Check（含三個內部服務可達性，UI 顯示綠燈）。
- [ ] Loading indicator。
- [ ] Execution animation。
- [ ] Agent Trace 優化。
- [ ] Error message。
- [ ] 預錄完整流程 GIF 作為備援。
- [ ] Demo README。
- [ ] Architecture Diagram。
- [ ] 5-minute Demo Script（含切換 AGENT_MODE、DBQUERY_MODE 的 Q&A 橋段）。

---

# 16. MVP Scope

第一版只需要完成：

```text
User
 ↓
React
 ↓
Agent API
 ↓
Remote LLM (gemma4:26b)
 ↓
MCP
 ↓
Playwright
 ↓
Mock Portal
 ↓
Download
 ↓
Classification
 ↓
Mapping (mock)
 ↓
PostgreSQL
 ↓
Manual Review + Mail
```

MVP 不做：

```text
Authentication / RBAC
Distributed Queue
Kubernetes
Multiple Workers
HA
Enterprise Observability
Real Internal Portal
PDF 解析（除非 mock 資料需要）
```

可選（時間允許再做）：

```text
Excel export
DBQUERY_MODE=real 現場切換
Phase 7 Scenario B
```

避免 Demo 前過度工程化。

---

# 17. Recommended Development Order

```text
1. docker-compose + CA cert + .env + 三個內部服務連通驗證

2. mock-portal

3. Playwright standalone（下載到 staging）

4. MCP wrapper（全部 tools，先 mock mapping）

5. LLM client + classification structured output

6. Agent API scripted mode（跑通整條 pipeline）

7. Agent loop（llm mode）

8. PostgreSQL 完整 schema

9. React UI

10. Exception handling (Scenario A)

11. real mapping adapter / Excel（選做）

12. Demo polish + GIF 備援
```

非常重要：

**先讓 deterministic flow 跑通，再加入 Agent。**

即 `AGENT_MODE=scripted` 先成功：

```text
Playwright
 ↓
Download
 ↓
Parser
 ↓
Classification
 ↓
Mapping
 ↓
DB / Mail
```

之後才切 `AGENT_MODE=llm`，讓 Agent 決定何時呼叫這些能力。這樣 Debug 難度會低非常多，也天然得到現場備援模式。

---

# 18. Definition of Done

使用者只輸入：

```text
處理今天 SCM 文件
```

系統必須自主完成：

- [ ] 登入 Portal。
- [ ] 選擇正確功能。
- [ ] 輸入參數。
- [ ] 查詢。
- [ ] 下載。
- [ ] 搬移至 staging。
- [ ] 解析檔案。
- [ ] LLM 分類。
- [ ] 資料 mapping（含缺 code 時以名稱補查）。
- [ ] 寫入 DB。
- [ ] Unknown → Manual Review → Mail 通知。
- [ ] Web 顯示完整 execution trace。
- [ ] Agent 產生最終 summary。
- [ ] （選做）產生 Excel。

最終 Demo Summary：

```text
Task Completed

Downloaded:              4
Automatically Processed: 3
Manual Review:           1
Notifications Sent:      1
Archived:                3

Duration:                XX seconds
```

---

# 19. Future Extensions

## Computer Use

```text
Playwright
 ↓
Vision / Computer Use Agent
```

## Real Internal Portal

```text
Mock Portal
 ↓
Internal Portal
```

Agent / MCP 架構不改，只換 `browser.py` 內部流程。

## Real ERP

`DBQUERY_MODE=real` 已直連 Oracle EBS 的 SQL Execution API。後續可擴充更多固定 SQL template（PO、Receipt、Invoice 對帳），仍維持 LLM 不寫 SQL 的原則。

## Workflow Platform Integration

```text
Normal Node
 ↓
Normal Node
 ↓
Agent Node
 ↓
Normal Node
```

固定流程由 Workflow 控制，不確定性高的步驟交由 Agent 決策。

---

# 20. Core Design Principle

本專案避免：

```text
所有事情都交給 LLM
```

採用：

```text
LLM
負責：
Decision
Classification
Reasoning
Exception Handling

Code
負責：
Browser Automation
File I/O
Parsing
SQL（固定 template，LLM 永不產生）
DB
Mail
Validation
Archive
```

也就是：

> Agent decides WHAT to do.
>
> Deterministic tools decide HOW it is executed.

這是整個 Demo 從實驗性 AI Application，走向可實際落地 Enterprise Agent 的核心。

---

# 21. Secrets Handling

- 所有 API key、AD 帳密、DB 密碼只存 `.env`，`.gitignore` 排除。
- roadmap.md、README.md、程式碼、prompt 中不得出現實際憑證。
- 已在對話或聊天中貼出過的 key 視為已曝光，建議在正式 demo 前輪替。
- Mail 帳密只由 mcp-worker 讀取，不經 agent-api、不進 LLM context。
