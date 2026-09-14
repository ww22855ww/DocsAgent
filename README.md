# Agentic Document Processing Demo

一個可在部門例會展示的 demo：使用者用自然語言下一個任務，agent 自己登入入口
網站、下載文件、解析分類、對應 ERP 主檔、歸檔，處理不了的送人工複核。

重點不是「AI 很厲害」，而是把一條原本會在第一個例外就停住的自動化流程，
換成**遇到例外會自己想辦法、但知道什麼時候不該亂猜**的做法。

---

## 快速開始

需要 Docker Desktop、PowerShell 7、Python 3（只用來跑輔助腳本）。

```powershell
cp .env.example .env      # 填入 LLM API key 與 AD 帳密
docker compose up -d
./scripts/reset-demo.ps1
```

| 位址 | 用途 |
|---|---|
| http://localhost:5173 | Agent 主控台，展示時開這個 |
| http://localhost:5100 | 模擬入口網站，帳號 `admin` 密碼 `123456` |
| http://localhost:5000/api/health/services | 六項依賴的健康檢查 |

講解用的互動導覽：
https://claude.ai/code/artifact/04668997-0199-4a6f-b8ac-d3c15a2c51d9

導覽是獨立的網頁，不需要 Docker 跑著。萬一現場 demo 出狀況，講解可以照常進行。

三層備援，由輕到重：

1. 切 **Scripted** 模式。畫面一模一樣，流程照走，不碰 LLM。
2. 放 `docs/media/agent-run-fallback.gif`，一次完整 Agent 執行的錄影。
3. 只講導覽網頁。概念和架構都在裡面，不需要任何服務。

---

## 五分鐘展示腳本

事前：跑 `./scripts/reset-demo.ps1`，確認狀態列六項全綠，瀏覽器開好主控台。
先跑一次暖機，避免第一次呼叫 LLM 的冷啟動落在正式展示。

### 0 分 · 先講問題（40 秒）

> 採購和品保每天要處理二三十份供應商文件。登入、查詢、下載、開檔、查主檔、
> 建檔，六個動作。動作不難，難在量大又重複。

開導覽網頁的第 1 到第 2 步，講固定流程為什麼在第一個例外就停住。

### 1 分 · 先跑固定流程（60 秒）

主控台切到 **Scripted**，輸入「處理今天 SCM 文件」，按 Execute。

執行中就可以講：這是傳統做法，順序寫死，模型完全沒有參與決策。

結果：**2 份歸檔、2 份人工複核**。指出 `quality_002.xlsx` 的原因是
「文件上沒有供應商代碼」。

> 這一份其實有寫廠商名稱，只是流程裡沒有「用名稱查」這條分支。

### 2 分 · 同一個任務交給 Agent（90 秒）

按 **Reset** 腳本或直接重跑（暫存已被清空則需先 reset），切到 **Agent**，
輸入同一句話。

執行中指著軌跡講三件事：

1. 第 2 步旁邊有模型的判斷理由，它自己決定要先下載。
2. 分類完 `quality_002.xlsx` 後，**第 9 步變成「查詢供應商 勝宏科技」**，
   這條規則沒有人寫過。
3. 最後它用繁體中文寫出摘要。

結果：**3 份歸檔、1 份人工複核**。同一份文件、同一組工具，差別在有沒有判斷。

### 3 分 30 秒 · 換個講法，看路由（40 秒）

輸入「整理各家廠商回覆的碳排與勞權自評表」，這句話裡沒有 ESG 三個字。

- Scripted 會抓錯分頁，去拿 SCM 文件。
- Agent 讀得懂意思，去拿 ESG 問卷。

### 4 分 10 秒 · Agent 知道什麼時候不該猜（40 秒）

```powershell
./scripts/reset-demo.ps1 -Ambiguous
```

這會把品檢報告上的廠商從「百辰光電」換成「勝宏科技」。兩個都是你們主檔裡的
真實廠商，差別是前者只有一筆，後者有惠州和泰國兩筆。

Agent 沒有挑一個，而是轉人工複核，理由寫著「找到 2 個候選」。

> 這比上一段更重要。上一段證明它會想辦法，這一段證明它知道自己的極限。
> 而且這個歧義是真的，兩家公司都在 EBS 裡，今年都有採購單。

### 4 分 50 秒 · 收尾（30 秒）

打開導覽網頁最後一步，講那條線：

> 模型決定做什麼，程式決定怎麼做。SQL 是寫死的樣板，瀏覽器操作是寫死的腳本，
> 模型碰不到也繞不過。這是能不能放進正式環境的關鍵。

---

## 現場可能被問到的問題

**資料是真的嗎？**
供應商和料號查的是即時的 Oracle EBS，不是模擬資料。只有文件本身是模擬的。
`勝宏科技` 兩家實體的歧義是查出來的，不是安排的。

**會不會亂寫 SQL？**
模型永遠不產生 SQL。`search_supplier` 和 `search_part` 用固定樣板，參數先檢查
有沒有引號、分號、註解符號。查詢 API 會執行任何送進去的 SQL，所以這道關卡是
承重的。

**這是查真的 ERP 嗎？**
是。預設就是 `DBQUERY_MODE=real`，每次查詢當場打公司的 SQL 查詢 API，
約 50 毫秒。本機還留了一份相同供應商的副本，只有在 ERP 連不上時才切過去用。

**模型跑不動怎麼辦？**
切 Scripted，畫面一模一樣，流程照走。LLM 端點失效時 agent-api 也會自動切到
OpenRouter 備援。

**接真實入口網站要改多少？**
只改 `mcp-worker/tools/browser.py` 裡的 Playwright 流程。Agent、MCP 介面、
分類、對應、歸檔都不動。

---

## 常用指令

```powershell
./scripts/reset-demo.ps1              # 展示前重置
./scripts/reset-demo.ps1 -Ambiguous   # 重置並切到多筆匹配情境
./scripts/reset-demo.ps1 -Full        # 連主檔一起重新載入

python scripts/scenario.py            # 看文件上印哪家廠商，以及 ERP 命中幾筆
python scripts/scenario.py ambiguous  # 換成勝宏科技（命中 2 筆）
python scripts/scenario.py unique     # 換回百辰光電（命中 1 筆）

python scripts/run_task.py llm "處理今天 SCM 文件"   # 不開瀏覽器跑一次
python scripts/probe_mcp.py list                      # 列出所有 MCP 工具

docker compose logs agent-api --tail 50
docker compose ps
```

---

## 架構

五個自建容器，三個公司既有的內部服務。

```
瀏覽器 → frontend(nginx) → agent-api ─┬→ LLM（gemma4:26b）
                                      ├→ mcp-worker ─┬→ mock-portal（Playwright）
                                      │              ├→ SQL 查詢 API（Oracle EBS）
                                      │              └→ Mail API
                                      └→ postgres
```

- **agent-api**（.NET）Agent 迴圈、LLM 呼叫、文件分類、任務狀態。
- **mcp-worker**（Python）所有工具的實作。帳號密碼只存在這個行程裡。
- **mock-portal**（.NET MVC）模擬的企業入口網站，兩個分頁：SCM 文件、ESG 問卷。
- **frontend**（React）主控台，執行軌跡即時串流。
- **postgres** 任務、軌跡、分類與對應歷程、歸檔結果。

設計細節、踩過的坑、以及為什麼某些地方要那樣寫，都記在 `CLAUDE.md`。
分階段的規劃與驗收標準在 `roadmap.md`。

---

## 環境變數

完整清單看 `.env.example`。展示時最常動的三個：

| 變數 | 值 | 用途 |
|---|---|---|
| `AGENT_MODE` | `scripted` \| `llm` | 主控台可以直接切，這個是預設值 |
| `DBQUERY_MODE` | `mock` \| `real` | 主檔查 PostgreSQL 還是 Oracle EBS |
| `MAIL_ENABLED` | `false` \| `true` | 人工複核要不要真的寄信 |

`.env` 含 API key 與 AD 密碼，已在 `.gitignore` 裡，不要提交。
